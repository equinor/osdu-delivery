using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Intake;

/// <summary>
/// What one intake pass produced: the counts of what it planned and the batches it wrote. <c>Stale</c> counts the
/// records the source carried in a version older than the ledger holds, delivered or queued, which are never sent.
/// </summary>
public sealed record IntakeCounts(long Records, long Planned, long Skipped, long Held, long Blocked, long Untracked, int Batches, long Stale = 0, long AwaitingApproval = 0)
{
    public static IntakeCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public IntakeCounts Add(IntakeCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new IntakeCounts(
            Records + other.Records, Planned + other.Planned, Skipped + other.Skipped, Held + other.Held, Blocked + other.Blocked,
            Untracked + other.Untracked, Batches + other.Batches, Stale + other.Stale, AwaitingApproval + other.AwaitingApproval);
    }

    /// <summary>Whether the intake changed nothing: it planned, held and blocked no record, and none waits on an approval.</summary>
    public bool Idle => Planned == 0 && Held == 0 && Blocked == 0 && AwaitingApproval == 0;

    /// <summary>The counts that are not zero, as the audit trail shows an intake run: "12 planned, 3 unchanged".</summary>
    public string Headline => CountLine.Of(
        "nothing to plan",
        (Planned, "planned"), (Skipped, "unchanged"), (AwaitingApproval, "awaiting approval"), (Stale, "stale"), (Held, "held"), (Blocked, "blocked"),
        (Untracked, "untracked"));

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Records} record(s): {Planned} to deliver in {Batches} batch(es), {Skipped} unchanged, {AwaitingApproval} awaiting approval, {Stale} stale, {Held} held, {Blocked} blocked, {Untracked} untracked");
}

public sealed record IntakeResult(SubmissionState Submission, PlanHeader? Header, IntakeCounts Counts, bool AlreadyProcessed)
{
    public bool NothingToDo => AlreadyProcessed || Header is null || Header.SkippedWholeRun || Counts.Planned == 0;
}

/// <summary>
/// What an intake works on: which records to read, the submission it belongs to when a run was given one (a re-run, or a
/// fan-out member's share), and the key slices a member plans of it.
/// </summary>
public sealed record IntakeRequest(SourceSelection Selection, Guid? SubmissionId = null, IReadOnlyList<int>? Slices = null);

/// <summary>
/// A submission ready to plan: registered, its read opened, and the window it covers recorded. <see cref="Done"/> is set
/// instead when there is nothing to plan (the submission was already completed, or the tier-0 gate skipped the run).
/// </summary>
public sealed record PreparedIntake(SubmissionState Submission, PlanHeader Header, string WorkRoot, IntakeResult? Done, SourceWindowDescription? Window = null);

/// <summary>
/// Turns one read of the ingestion tables into ledger state (osdu/docs/reference/concepts/submissions.md): registers
/// the submission, plans its records, writes the rendered documents to work batches at the flow's work location, and
/// stages the pending work per record in the ledger, batch by batch. Re-running an intake for a completed submission
/// does nothing unless forced; re-running one that was interrupted picks up where it stopped. An intake can be
/// restricted to a share of the key slices its coordinating run cut, which is how a fan-out spreads it across nodes.
/// </summary>
public sealed class SubmissionIntake
{
    /// <summary>The held records of one read the trace names with their reason; the submission counts the rest.</summary>
    private const int HoldsLogged = 20;

    /// <summary>Batch numbers per fan-out slice namespace.</summary>
    public const int BatchesPerPartition = 1_000_000;

    /// <summary>The highest slice index a fan-out intake can namespace within a 32-bit batch number.</summary>
    public const int MaxFanOutPartition = (int.MaxValue / BatchesPerPartition) - 1;

    private readonly ILedger _ledger;
    private readonly Planner _planner;
    private readonly FileStoreRegistry _stores;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<SubmissionIntake> _logger;

    /// <summary>The platform run the intake happens in, stamped on the pending work it writes.</summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// The trace of the run the intake plans for, or null outside a run. What the intake plans for a record the trace
    /// describes is on it, as a plan run shows it; so is how far the planning has got.
    /// </summary>
    public RunTrace? Trace { get; init; }

    /// <summary>
    /// The flow's target, asked whether OSDU already holds a record at an OSDU id made from a key's values before a record
    /// first claims it (<see cref="HoldOccupiedAsync"/>). Null leaves every such id unconfirmed, so its record is held.
    /// </summary>
    public Func<CancellationToken, Task<IDeliveryProtocol>>? Target { get; init; }

    /// <summary>
    /// The holds of the run the intake plans for (<see cref="SubmissionHolds"/>): the submission it registers is held before
    /// the ledger has it, and one it reopens before it is planned again, so no later run takes over a submission this run
    /// works on. Null holds nothing, which only an intake outside a runtime does.
    /// </summary>
    public SubmissionHolds? Holds { get; init; }

    public SubmissionIntake(ILedger ledger, Planner planner, FileStoreRegistry stores, TimeProvider time, IDeliveryListener listener, ILogger<SubmissionIntake> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _planner = planner;
        _stores = stores;
        _time = time;
        _listener = listener;
        _logger = logger;
    }

    /// <summary>
    /// Registers and plans one read. With <paramref name="request"/> naming no slices the whole read is planned here and
    /// the submission is closed with its counts; with slices only that share is planned, and the coordinating run
    /// finalises the submission.
    /// </summary>
    public async Task<IntakeResult> IntakeAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        IntakeRequest request,
        bool force,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);

        var prepared = await PrepareAsync(flow, resolved, parameters, request, force, ct).ConfigureAwait(false);
        try
        {
            if (prepared.Done is { } done)
            {
                return done;
            }

            var counts = await PlanSlicesAsync(flow, prepared, request.Slices, ct).ConfigureAwait(false);
            if (request.Slices is { Count: > 0 } slices)
            {
                _logger.LogInformation("Intake of slices {Slices}: {Counts}.", KeySlices.Describe(slices), counts);
                return new IntakeResult(prepared.Submission, prepared.Header, counts, AlreadyProcessed: false);
            }

            var submission = await FinalizePlanningAsync(flow, prepared.Submission.SubmissionId, parameters, counts, ct).ConfigureAwait(false);
            return new IntakeResult(submission, prepared.Header, counts, AlreadyProcessed: false);
        }
        finally
        {
            // The intake is all this call does with the submission: whatever drains it later holds it for itself.
            if (Holds is not null)
            {
                await Holds.ReleaseAsync(prepared.Submission.SubmissionId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Opens the read and registers (or reopens) the submission it belongs to. A run that names a submission reopens the
    /// window that submission recorded, so a member, a re-run and a drain all read the rows the coordinating run read. A run
    /// the platform executes again after an interruption reopens the submission its interrupted attempt left unsettled for
    /// the same read (<see cref="ResumableAsync"/>) rather than registering another. The submission is held for the run
    /// (<see cref="Holds"/>) before it is registered or planned again; the caller lets go of it when it is done with it.
    /// Returns early with <see cref="PreparedIntake.Done"/> when there is nothing to plan.
    /// </summary>
    public async Task<PreparedIntake> PrepareAsync(
        FlowDefinition flow, ResolvedMapping resolved, IReadOnlyDictionary<string, string> parameters, IntakeRequest request, bool force, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);
        var now = _time.GetUtcNow().UtcDateTime;
        var workRoot = FlowParameters.WorkLocation(flow, parameters);

        var existing = request.SubmissionId is { } named
            ? await _ledger.GetSubmissionAsync(named, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Submission {named:D} is not in the ledger.")
            : await ResumableAsync(flow, request, ct).ConfigureAwait(false);
        if (existing is not null && existing.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {existing.SubmissionId:D} belongs to flow '{existing.FlowName}', not '{flow.Label}'.");
        }

        // Held before anything is planned into it, and a new one before the ledger has it, so no moment passes in which a
        // later run could take it for a submission nobody works on.
        var submissionId = existing?.SubmissionId ?? Guid.CreateVersion7();
        var held = Holds is not null && await Holds.HoldAsync(submissionId, ct).ConfigureAwait(false);
        try
        {
            return await PrepareHeldAsync(flow, resolved, parameters, request, force, existing, submissionId, now, workRoot, ct).ConfigureAwait(false);
        }
        catch when (held)
        {
            await Holds!.ReleaseAsync(submissionId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The submission of this run's attempt that an interrupted attempt of the same platform run left unsettled for the same
    /// read, or null. The platform executes a run again when the node executing it stopped (its lease lapsed), and the
    /// attempt that stopped may have registered a submission and planned, or sent, part of it; the new attempt resumes that
    /// submission, as the platform hands it back the fan-out members it already has, so its window, its key slices and its
    /// members stay one. A plan of the whole scope resumes the run's unsettled plan of the whole scope; a key-scoped plan
    /// resumes only one made for exactly the same records. A member names its submission and never resumes one.
    /// </summary>
    private async Task<SubmissionState?> ResumableAsync(FlowDefinition flow, IntakeRequest request, CancellationToken ct)
    {
        if (RunId is not { } runId || request.Slices is not null)
        {
            return null;
        }

        foreach (var candidate in await _ledger.ListUnsettledSubmissionsOfRunAsync(flow.Id, runId, ct).ConfigureAwait(false))
        {
            var recorded = SourceWindowDescription.Parse(candidate.SourceWindowJson);
            if (!SameRead(request.Selection, candidate, recorded))
            {
                continue;
            }

            _logger.LogInformation(
                "Run {RunId} is executing again after an interruption: it resumes submission {SubmissionId} ({Status}), which its interrupted attempt left unsettled, rather than registering another.",
                runId, candidate.SubmissionId, StatusText.Of(candidate.Status));
            return candidate;
        }

        return null;
    }

    /// <summary>Whether a submission recorded as <paramref name="recorded"/> was made for the read <paramref name="selection"/> asks for.</summary>
    private static bool SameRead(SourceSelection selection, SubmissionState candidate, SourceWindowDescription? recorded)
    {
        var keys = recorded?.Selection == SubmissionKinds.Keys || (recorded is null && candidate.Kind == SubmissionKinds.Keys);
        if (selection.Kind != SourceSelectionKind.Keys)
        {
            return !keys;
        }

        if (!keys || recorded is null)
        {
            return false;
        }

        var asked = selection.Keys.Select(k => JsonSerializer.Serialize(k.Values)).ToHashSet(StringComparer.Ordinal);
        var covered = recorded.Keys.Select(k => JsonSerializer.Serialize(k)).ToHashSet(StringComparer.Ordinal);
        return asked.SetEquals(covered);
    }

    /// <summary><see cref="PrepareAsync"/> once the submission is held.</summary>
    private async Task<PreparedIntake> PrepareHeldAsync(
        FlowDefinition flow, ResolvedMapping resolved, IReadOnlyDictionary<string, string> parameters, IntakeRequest request, bool force,
        SubmissionState? existing, Guid submissionId, DateTime now, string workRoot, CancellationToken ct)
    {
        var described = existing is null ? null : SourceWindowDescription.Parse(existing.SourceWindowJson);
        var selection = described is null ? request.Selection : described.ToSelection();
        var header = await _planner.OpenAsync(
            flow, resolved, parameters, selection, gate: !force && existing is null, stored: described?.Window(), ct: ct).ConfigureAwait(false);

        SubmissionState submission;
        var created = false;
        if (existing is not null)
        {
            submission = existing;
        }
        else
        {
            (submission, created) = await _ledger.RegisterSubmissionAsync(new SubmissionState
            {
                SubmissionId = submissionId,
                FlowId = flow.Id,
                FlowName = flow.Label,
                MappingReference = resolved.Mapping.Reference,
                RenderContext = resolved.Context.Canonical(),
                ParametersJson = JsonSerializer.Serialize(parameters),
                Kind = SourceWindowDescription.Name(selection.Kind),
                SourceConnection = flow.Source.Connection,
                SourceObject = flow.Source.Record.Object,
                WindowFromUtc = header.Source.Window.LowerUtc,
                WindowToUtc = header.Source.Window.UpperUtc,
                SourceWindowJson = SourceWindowDescription.Of(header.Source, []).ToJson(),
                RecordCount = header.Source.EstimatedCandidates,
                WorkLocation = workRoot,
                Slices = 1,
                RunId = RunId,
                ReceivedUtc = now,
            }, ct).ConfigureAwait(false);
        }

        if (!created && submission.Status == SubmissionStatus.Completed && !force && request.Slices is null)
        {
            _logger.LogInformation("Submission {SubmissionId} was already completed at {CompletedUtc}; nothing to do (force re-plans it).", submission.SubmissionId, submission.CompletedUtc);
            return new PreparedIntake(submission, header, workRoot, new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: true), described);
        }

        if (!created && request.Slices is null)
        {
            _logger.LogInformation("Submission {SubmissionId} exists with status {Status}; re-planning against the current ledger.", submission.SubmissionId, submission.Status);
        }

        if (header.SkippedWholeRun)
        {
            // The scope did not move, so the plan is complete as it stands. The watermark still advances, which is what
            // keeps the next run's window from re-reading the same quiet stretch.
            submission = submission with { Status = SubmissionStatus.Completed, StartedUtc = now, CompletedUtc = now, Error = null };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
            await WriteWatermarkAsync(flow, submission, parameters, ct).ConfigureAwait(false);
            await EmitAsync(flow, submission, "submission.completed", header.SkipReason, ct).ConfigureAwait(false);
            return new PreparedIntake(submission, header, workRoot, new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: false), described);
        }

        if (request.Slices is null)
        {
            submission = submission with { Status = SubmissionStatus.Received, StartedUtc = submission.StartedUtc ?? now, Error = null, WorkLocation = workRoot };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        }

        return new PreparedIntake(submission, header, workRoot, null, described);
    }

    /// <summary>
    /// Records the key slices a coordinating run cut its read into, so its members plan exactly the ranges it dealt out
    /// and no record falls between two of them.
    /// </summary>
    public async Task<PreparedIntake> RecordSlicesAsync(PreparedIntake prepared, IReadOnlyList<KeyRange> ranges, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(ranges);
        var description = SourceWindowDescription.Of(prepared.Header.Source, ranges);
        var submission = prepared.Submission with { Slices = ranges.Count, SourceWindowJson = description.ToJson() };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        return prepared with { Submission = submission, Window = description, Header = prepared.Header with { Slices = ranges.Count } };
    }

    /// <summary>Plans the whole read (<paramref name="slices"/> null) or the named key slices of it into work batches.</summary>
    public async Task<IntakeCounts> PlanSlicesAsync(FlowDefinition flow, PreparedIntake prepared, IReadOnlyList<int>? slices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(prepared);
        if (slices is null or { Count: 0 })
        {
            return await StageAsync(flow, prepared, null, 0, ct).ConfigureAwait(false);
        }

        var description = prepared.Window
            ?? throw new DeliveryException(
                $"Submission {prepared.Submission.SubmissionId:D} records no key slices, so slice {slices[0].ToString(CultureInfo.InvariantCulture)} cannot be planned; its coordinating run cuts the slices before it hands them out.");

        var counts = IntakeCounts.Empty;
        foreach (var slice in slices.Distinct().Order())
        {
            var range = description.Range(slice)
                ?? throw new DeliveryException($"Submission {prepared.Submission.SubmissionId:D} was cut into {description.Slices.Count} slice(s); slice {slice.ToString(CultureInfo.InvariantCulture)} is not one of them.");
            counts = counts.Add(await StageAsync(flow, prepared, range, BatchBase(slice), ct).ConfigureAwait(false));
        }

        return counts;
    }

    /// <summary>
    /// Closes the planning phase of a submission with the totals of every intake pass (one, or one per fan-out member)
    /// and records the scope's watermark when the plan covered the whole scope. The submission is planned when there is
    /// work, completed when there is none.
    /// </summary>
    public async Task<SubmissionState> FinalizePlanningAsync(
        FlowDefinition flow, Guid submissionId, IReadOnlyDictionary<string, string> parameters, IntakeCounts counts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(counts);
        var now = _time.GetUtcNow().UtcDateTime;
        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");

        // A submission planned again (a re-run, or a run resuming what its interrupted attempt left) can still hold records
        // an earlier pass queued, which this pass found already queued: it is complete only once none is left to send.
        var complete = counts.Planned == 0 && !await _ledger.HasPendingAsync(flow.Id, submissionId, now, ct).ConfigureAwait(false);
        submission = submission with
        {
            Status = complete ? SubmissionStatus.Completed : SubmissionStatus.Planned,
            StartedUtc = submission.StartedUtc ?? now,
            CompletedUtc = complete ? now : null,
            RecordCount = counts.Records,
            Planned = counts.Planned,
            SkippedUnchanged = counts.Skipped,
            AwaitingApproval = counts.AwaitingApproval,
            SkippedStale = counts.Stale,
            UnchangedAtPush = 0,
            Blocked = counts.Blocked,
            Held = counts.Held,
            Untracked = counts.Untracked,
            BatchCount = counts.Batches,
            Delivered = 0,
            Failed = 0,
            Error = null,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        await WriteWatermarkAsync(flow, submission, parameters, ct).ConfigureAwait(false);
        _logger.LogInformation("Submission {SubmissionId}: {Counts}.", submission.SubmissionId, counts);
        await EmitAsync(flow, submission, complete ? "submission.completed" : "submission.planned", Summarize(submission), ct).ConfigureAwait(false);
        return submission;
    }

    /// <summary>
    /// Moves the scope's watermark to the window this plan covered. Only a plan of the whole scope may: a key-scoped
    /// submission reads a few records and says nothing about the rows it never looked at.
    /// </summary>
    private async Task WriteWatermarkAsync(FlowDefinition flow, SubmissionState submission, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!flow.Change.UseSourceVersions || !submission.CoversScope || submission.WindowToUtc is not { } through)
        {
            return;
        }

        await _ledger.SetWatermarkAsync(
            new SourceWatermark(
                flow.Id,
                Planner.ScopeKey(parameters),
                through,
                submission.SubmissionId,
                _time.GetUtcNow().UtcDateTime,
                Delivery.Snapshots.RenderContext.Parse(submission.RenderContext).RulesHash()),
            ct).ConfigureAwait(false);
    }

    /// <summary>Streams the plan of one read (or one key range of it) into work batches and the ledger.</summary>
    private async Task<IntakeCounts> StageAsync(FlowDefinition flow, PreparedIntake prepared, KeyRange? range, int batchBase, CancellationToken ct)
    {
        var header = prepared.Header;
        var submission = prepared.Submission;
        var workRoot = prepared.WorkRoot;
        var summary = new PlanSummary();
        var batchRecords = Math.Max(1, flow.Reliability.BatchRecords);
        var nextBatch = batchBase;
        var batches = 0;
        long staged = 0;

        WorkBatchWriter? writer = null;
        var pending = new List<RecordState>(batchRecords);
        // The records of the batch about to claim an OSDU id made from their key for the first time, and of those the ones an
        // operator asked to adopt the record OSDU holds there.
        var unclaimed = new HashSet<DeliveryKey>();
        var adopting = new HashSet<DeliveryKey>();
        var skipped = new List<SkippedRecord>(Planner.RenderBatch);
        var context = header.Mapping.Context.Canonical();
        long refused = 0;
        long conflicted = 0;
        var held = new List<RecordState>();
        var untrackedLogged = 0;
        var holdsLogged = 0;
        var lastProgress = _time.GetUtcNow();
        _logger.LogInformation(
            "Planning submission {SubmissionId}: {What}, {Workers} render batch(es) of {Records} at a time, {BatchRecords} record(s) to a work batch.",
            submission.SubmissionId,
            range is null
                ? string.Create(CultureInfo.InvariantCulture, $"{header.Source.EstimatedCandidates} candidate record(s) of {flow.Source.Record.Object}")
                : string.Create(CultureInfo.InvariantCulture, $"key slice {range.Value.Slice} of {flow.Source.Record.Object}"),
            flow.Reliability.EffectiveRenderParallelism, Planner.RenderBatch, batchRecords);

        try
        {
            await foreach (var entry in _planner.EntriesAsync(header, range, flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
            {
                if (entry.Key is null)
                {
                    if (untrackedLogged++ < 20)
                    {
                        _logger.LogWarning("Record {SourceKey} has no derivable delivery key and cannot be tracked: {Reason}", entry.SourceKey, entry.Reason);
                    }

                    continue;
                }

                switch (entry.Action)
                {
                    case PlannedAction.Skip:
                        skipped.Add(Skipped(entry, context));
                        if (skipped.Count >= Planner.RenderBatch)
                        {
                            await _ledger.MarkSkippedAsync(flow.Id, skipped, submission.SubmissionId, ct).ConfigureAwait(false);
                            skipped.Clear();
                        }

                        break;

                    case PlannedAction.Blocked:
                        // Left untouched: its state and history belong to the submission that held it. A blocked record
                        // asked to be planned again is planned, so none carries a request here; one made since the plan
                        // read the record stays for the next run.
                        break;

                    case PlannedAction.Hold:
                        if (Describes(entry.Key.Value) || holdsLogged++ < HoldsLogged)
                        {
                            _logger.LogWarning("{Record} is held: {Reason}", RunTrace.Record(entry.SourceKey, entry.Label, entry.Key.Value), Http.HeaderRedaction.RedactMessage(entry.Reason));
                        }

                        // What the held render read, the ids it found missing among them, becomes the record's set, so a cache
                        // version that brings a missing one in plans the record again.
                        var heldSet = entry.Render is { CacheUsages.Count: > 0 } heldRender && header.Mapping.Context.CacheScope is { } heldScope
                            ? await _ledger.EnsureCacheSetAsync(heldScope, heldRender.CacheUsages, ct).ConfigureAwait(false)
                            : (long?)null;
                        held.Add(HeldState(flow, submission, entry, header.Mapping) with { CacheSetId = heldSet });
                        if (held.Count >= Planner.RenderBatch)
                        {
                            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
                        }

                        break;

                    default:
                        if (!entry.IsDelivery)
                        {
                            break;
                        }

                        if (Describes(entry.Key.Value))
                        {
                            _logger.LogInformation("{Entry}", PlanFormatting.Describe(entry));
                        }

                        writer ??= await WorkBatchWriter.OpenAsync(_stores, workRoot, submission.SubmissionId, nextBatch, ct).ConfigureAwait(false);
                        var reference = await writer.WriteAsync(new WorkItem(entry.Key.Value.Value, entry.TargetId!, entry.Render!.Canonical, entry.Render.Assertions?.ToText()), ct).ConfigureAwait(false);
                        // The values this document was built from become one set id on the record: no row per
                        // dependency, and the id is resolved once per distinct combination for the whole run.
                        var cacheSet = entry.Render!.CacheUsages.Count == 0
                            ? (long?)null
                            : await _ledger.EnsureCacheSetAsync(
                                header.Mapping.Context.CacheScope
                                    ?? throw new DeliveryException($"Mapping {header.Mapping.Mapping.Reference} read cached values under a render context that names no cache partition; the render resolver names the partition whenever a mapping reads the cache."),
                                entry.Render.CacheUsages,
                                ct).ConfigureAwait(false);
                        pending.Add(PendingState(flow, submission, header.Mapping, entry, reference, nextBatch) with { CacheSetId = cacheSet });
                        if (header.Mapping.Mapping.Dataset.IdFrom == MappingIdSource.Key && entry.Existing?.ClaimedTargetId is null)
                        {
                            unclaimed.Add(entry.Key.Value);
                            if (entry.Existing?.AdoptRequestedUtc is not null)
                            {
                                adopting.Add(entry.Key.Value);
                            }
                        }

                        if (pending.Count >= batchRecords)
                        {
                            var (closed, occupied) = await CloseBatchAsync(flow, submission, writer, pending, unclaimed, adopting, skipped, ct).ConfigureAwait(false);
                            staged += closed.Staged;
                            refused += closed.Refused.Count;
                            conflicted += closed.Conflicts.Count + occupied;
                            writer = null;
                            batches++;
                            nextBatch++;
                        }

                        break;
                }

                // Paced by the run's trace, which slows as the run goes on; outside a run, every progress interval.
                var nowUtc = _time.GetUtcNow();
                if (Trace is { } trace ? trace.ProgressDue("intake", nowUtc) : nowUtc - lastProgress >= RunTrace.ProgressInterval)
                {
                    lastProgress = nowUtc;
                    _logger.LogInformation(RunTrace.Bounded, "Intake progress: {Summary}; {Batches} batch(es) written.", summary, batches);
                }
            }

            if (writer is not null)
            {
                var (closed, occupied) = await CloseBatchAsync(flow, submission, writer, pending, unclaimed, adopting, skipped, ct).ConfigureAwait(false);
                staged += closed.Staged;
                refused += closed.Refused.Count;
                conflicted += closed.Conflicts.Count + occupied;
                writer = null;
                batches++;
            }
        }
        catch
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        if (skipped.Count > 0)
        {
            await _ledger.MarkSkippedAsync(flow.Id, skipped, submission.SubmissionId, ct).ConfigureAwait(false);
        }

        if (held.Count > 0)
        {
            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
        }

        return new IntakeCounts(summary.Records, staged, summary.Skips, summary.Holds + conflicted, summary.Blocked, summary.Untracked, batches, summary.Stale + refused, summary.AwaitingApproval);
    }

    /// <summary>
    /// Whether the run's trace describes the record the intake plans. Only a record the intake sends or holds asks, so the
    /// records a run describes are the ones it does something with, not the unchanged ones it reads past.
    /// </summary>
    private bool Describes(DeliveryKey key) => Trace?.Describes(key, _logger) == true;

    /// <summary>What the ledger is told about one skipped plan entry.</summary>
    private SkippedRecord Skipped(PlanEntry entry, string renderContext) => new()
    {
        DeliveryKey = entry.Key!.Value,
        Kind = entry.SkipTier switch
        {
            SkipTier.Stale => SkipKind.Stale,
            SkipTier.ContentHash => SkipKind.Rendered,
            SkipTier.Approval => SkipKind.AwaitingApproval,
            SkipTier.SourceRemoved => SkipKind.SourceRemoved,
            _ => SkipKind.Unchanged,
        },
        Basis = PlanBasis.Of(entry.Existing),
        Reason = entry.Reason,
        SourceKeyJson = entry.SourceKeyJson,
        SourceFingerprint = entry.SourceFingerprint,
        SourceModifiedUtc = entry.SourceModifiedUtc,
        Origin = new RecordOrigin(entry.Origin.FileName, entry.Origin.RowNumber, entry.Origin.UpdatedUtc),
        SourceInsertedUtc = entry.Origin.InsertedUtc,
        PayloadModifiedUtc = entry.PayloadModifiedUtc,
        RenderContext = entry.SkipTier == SkipTier.ContentHash ? renderContext : null,
        ChildLinkKey = entry.SkipTier == SkipTier.ContentHash ? entry.Render?.ChildLinkKey : null,
        RunId = RunId,
    };

    /// <summary>
    /// Commits the batch file, stages its records in the ledger and registers the batch. A record about to claim an OSDU id
    /// made from its key for the first time is held first when OSDU already holds a record there (<see cref="HoldOccupiedAsync"/>).
    /// Records the ledger refuses because a newer version landed or was queued since they were planned go to
    /// <paramref name="stale"/>, to be recorded like any other stale skip. Records whose OSDU id another record has claimed
    /// are held here, each naming the record that owns the id, so the conflict is on the record's page and in its history
    /// rather than only in a log. Returns the staging and how many records were held for an id OSDU already holds.
    /// </summary>
    private async Task<(PendingStaging Staging, int Occupied)> CloseBatchAsync(
        FlowDefinition flow, SubmissionState submission, WorkBatchWriter writer, List<RecordState> pending, HashSet<DeliveryKey> unclaimed, HashSet<DeliveryKey> adopting,
        List<SkippedRecord> stale, CancellationToken ct)
    {
        await writer.DisposeAsync().ConfigureAwait(false);
        var (occupied, adoptions) = await HoldOccupiedAsync(flow, submission, writer.Batch, pending, unclaimed, adopting, ct).ConfigureAwait(false);
        var staging = await _ledger.UpsertPendingAsync(flow.Id, pending, ct).ConfigureAwait(false);
        await RecordAdoptionsAsync(flow, submission, adoptions, staging, ct).ConfigureAwait(false);
        if (staging.Conflicts.Count > 0)
        {
            var byKey = pending.GroupBy(p => p.DeliveryKey).ToDictionary(g => g.Key, g => g.Last());
            var held = staging.Conflicts
                .Select(conflict => ConflictState(byKey[conflict.DeliveryKey], conflict))
                .ToList();
            _logger.LogWarning(
                "Batch {Batch}: {Conflicts} record(s) were held because another record has claimed their OSDU ids; the first: {Detail}",
                writer.Batch, staging.Conflicts.Count, staging.Conflicts[0].Describe());
            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
        }

        if (staging.Refused.Count > 0)
        {
            var refused = staging.Refused.ToHashSet();
            foreach (var record in pending.Where(p => refused.Contains(p.DeliveryKey)))
            {
                var carried = record.PendingSourceModifiedUtc is { } modified
                    ? string.Create(CultureInfo.InvariantCulture, $" (this run read the row as last modified {modified:yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'})")
                    : string.Empty;
                stale.Add(new SkippedRecord
                {
                    DeliveryKey = record.DeliveryKey,
                    Kind = SkipKind.Stale,
                    Reason = $"a newer version of the record was delivered or queued while this run was planning{carried}; OSDU keeps the newer version",
                    SourceKeyJson = record.SourceKeyJson,
                    SourceFingerprint = record.PendingSourceFingerprint,
                    SourceModifiedUtc = record.PendingSourceModifiedUtc,
                    Origin = record.PendingOrigin,
                    PayloadModifiedUtc = record.PendingPayloadModifiedUtc,
                    RunId = RunId,
                });
            }

            _logger.LogWarning(
                "Batch {Batch}: {Refused} record(s) were not staged because a newer version was delivered or queued while this run was planning; they are recorded as stale.",
                writer.Batch, staging.Refused.Count);
        }

        await _ledger.AddWorkBatchAsync(new WorkBatchState
        {
            SubmissionId = submission.SubmissionId,
            FlowId = flow.Id,
            Index = writer.Batch,
            Location = writer.Path,
            RecordCount = staging.Staged,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
        }, ct).ConfigureAwait(false);
        pending.Clear();
        return (staging, occupied);
    }

    /// <summary>
    /// Holds the records of <paramref name="unclaimed"/> whose OSDU id, made from their key, OSDU already holds a record at,
    /// and takes them out of <paramref name="pending"/> before staging. Such a record never queued a document, so nothing it
    /// did wrote that record: it is another system's, or one this ledger has no record of, and writing to the id would make
    /// a new version of a record the ledger does not own. The hold keeps the id and the version OSDU answered, so an operator
    /// who knows the records are this ledger's can ask them to adopt it (<see cref="ILedger.RequestAdoptionAsync"/>). A record
    /// of <paramref name="adopting"/>, asked so, is not held: it stays in <paramref name="pending"/> carrying the version OSDU
    /// holds now, and staging claims the id for it, so the batch writes it there as an update of that record. The flow's own
    /// read back asks (<see cref="IDeliveryProtocol.VerifyBatchAsync"/>), one batched read for the batch. An id a record of the
    /// ledger has claimed is not asked: staging refuses it, naming that record. An answer that is not an absence holds the
    /// record too, adoption asked or not, since an id made from a key is claimed only when it is known to be free or known to
    /// hold a record to adopt; a release, or the next run, asks again. A target that keeps records under keys of its own (a
    /// DSPDM row's primary key) stores nothing at the id, and is not asked. Returns how many records were held, and the
    /// records adopted.
    /// </summary>
    private async Task<(int Held, IReadOnlyList<RecordAdoption> Adopted)> HoldOccupiedAsync(
        FlowDefinition flow, SubmissionState submission, int batch, List<RecordState> pending, HashSet<DeliveryKey> unclaimed, HashSet<DeliveryKey> adopting,
        CancellationToken ct)
    {
        if (unclaimed.Count == 0)
        {
            adopting.Clear();
            return (0, []);
        }

        var asking = pending.Where(p => unclaimed.Contains(p.DeliveryKey) && p.TargetId is not null).ToList();
        var asked = adopting.ToHashSet();
        unclaimed.Clear();
        adopting.Clear();
        var claimed = await _ledger.ClaimedTargetIdsAsync(asking.Select(p => p.TargetId!).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
        asking.RemoveAll(p => claimed.Contains(p.TargetId!));
        if (asking.Count == 0)
        {
            return (0, []);
        }

        var occupancies = await OccupiedAsync(asking.Select(p => p.TargetId!).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
        var held = new List<RecordState>();
        var adopted = new Dictionary<DeliveryKey, RecordAdoption>();
        foreach (var record in asking.Where(p => occupancies.ContainsKey(p.TargetId!)))
        {
            var occupancy = occupancies[record.TargetId!];
            if (occupancy.Held && asked.Contains(record.DeliveryKey))
            {
                adopted[record.DeliveryKey] = new RecordAdoption(record.DeliveryKey, record.TargetId!, occupancy.Version);
            }
            else
            {
                held.Add(OccupiedState(record, occupancy));
            }
        }

        if (adopted.Count > 0)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (adopted.TryGetValue(pending[i].DeliveryKey, out var adoption))
                {
                    pending[i] = pending[i] with { TargetVersion = adoption.Version };
                }
            }

            _logger.LogInformation(
                "Batch {Batch}: {Adopted} record(s) adopt the records OSDU holds at their OSDU ids, as an operator asked; the first: {TargetId}",
                batch, adopted.Count, adopted.Values.First().TargetId);
        }

        if (held.Count > 0)
        {
            var keys = held.Select(h => h.DeliveryKey).ToHashSet();
            pending.RemoveAll(p => keys.Contains(p.DeliveryKey));
            _logger.LogWarning(
                "Batch {Batch}: {Held} record(s) were held because their OSDU ids, made from their keys, are not known to be free; the first: {Detail}",
                batch, held.Count, held[0].LastError);
            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
        }

        return (held.Count, [.. adopted.Values]);
    }

    /// <summary>
    /// Records on each adopted record's history that this run took over the record OSDU held at its id, and tells the
    /// listeners: only for a record staging wrote, since one it refused (another record claimed the id since, or a newer
    /// version was queued) claimed nothing.
    /// </summary>
    private async Task RecordAdoptionsAsync(FlowDefinition flow, SubmissionState submission, IReadOnlyList<RecordAdoption> adoptions, PendingStaging staging, CancellationToken ct)
    {
        if (adoptions.Count == 0)
        {
            return;
        }

        var refused = staging.Refused.Concat(staging.Conflicts.Select(c => c.DeliveryKey)).ToHashSet();
        var staged = adoptions.Where(a => !refused.Contains(a.Key)).ToList();
        if (staged.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _ledger.RecordAdoptionsAsync(flow.Id, staged, submission.SubmissionId, RunId, now, ct).ConfigureAwait(false);
        foreach (var adoption in staged)
        {
            await _listener.OnEventAsync(new DeliveryEvent
            {
                AtUtc = now,
                FlowId = flow.Id,
                FlowName = flow.Label,
                Interface = flow.Interface,
                Kind = "record.adopted",
                SubmissionId = submission.SubmissionId,
                DeliveryKey = adoption.Key,
                TargetId = adoption.TargetId,
                TargetVersion = adoption.Version,
                Worker = "intake",
                Detail = "adopted the record OSDU holds at the id its key makes, as an operator asked",
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Why an id made from a key could not be claimed: OSDU holds a record there (<paramref name="Held"/>, at
    /// <paramref name="Version"/> when its answer named one), which an operator may adopt; or the target could not say, or
    /// there was no target to ask, which a release or the next run asks again.
    /// </summary>
    private sealed record Occupancy(string Reason, bool Held, long? Version);

    /// <summary>
    /// Why each of <paramref name="ids"/> that is not known to be free cannot be claimed: OSDU holds a record there, the target
    /// could not say, or there is no target to ask. An id the target answers is absent is free, and not in the result.
    /// </summary>
    private async Task<Dictionary<string, Occupancy>> OccupiedAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var reasons = new Dictionary<string, Occupancy>(StringComparer.Ordinal);
        if (Target is null)
        {
            foreach (var id in ids)
            {
                reasons[id] = new Occupancy(
                    $"the OSDU id {id} is made from the key, and no target was given to ask whether OSDU already holds a record there; "
                    + "an id made from a key is claimed only when it is known to be free",
                    Held: false,
                    Version: null);
            }

            return reasons;
        }

        var protocol = await Target(ct).ConfigureAwait(false);
        if (protocol.VerifiesWithTargetState)
        {
            return reasons;
        }

        foreach (var chunk in ids.Chunk(Math.Max(1, protocol.MaxVerifyBatch)))
        {
            IReadOnlyList<VerifyResult> results;
            try
            {
                results = await protocol.VerifyBatchAsync(chunk.Select(id => new VerifyRequest(id, null)).ToList(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                var why = Http.HeaderRedaction.RedactMessage(ex.Message);
                foreach (var id in chunk)
                {
                    reasons[id] = new Occupancy(
                        $"asking OSDU whether it already holds a record at {id}, the OSDU id made from the key, failed ({why}); "
                        + "an id made from a key is claimed only when it is known to be free, so a release or the next run asks again",
                        Held: false,
                        Version: null);
                }

                continue;
            }

            if (results.Count != chunk.Length)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The target answered {results.Count} result(s) for the {chunk.Length} OSDU id(s) it was asked about, starting with {chunk[0]}; a read back answers once for each id."));
            }

            for (var i = 0; i < chunk.Length; i++)
            {
                var result = results[i];
                switch (result.Outcome)
                {
                    case VerifyOutcome.Missing:
                        break;
                    case VerifyOutcome.Match or VerifyOutcome.Drifted:
                        var version = result.ObservedVersion is { } observed ? string.Create(CultureInfo.InvariantCulture, $" at version {observed}") : string.Empty;
                        reasons[chunk[i]] = new Occupancy(
                            $"{OccupiedHolds.Prefix}{chunk[i]}{version}, the OSDU id made from the key, and no record of the ledger claimed it: "
                            + "another system wrote it, or a ledger this database does not hold, so nothing is sent rather than writing a new version of a "
                            + "record no ledger owns. Adopt the records held so when they are this flow's (the next run takes each over and writes its row there), "
                            + "remove or rename that record, or give the mapping a key or dataset.idFrom that yields another id",
                            Held: true,
                            Version: result.ObservedVersion);
                        break;
                    default:
                        reasons[chunk[i]] = new Occupancy(
                            $"OSDU could not say whether it holds a record at {chunk[i]}, the OSDU id made from the key ({Http.HeaderRedaction.RedactMessage(result.Detail ?? "no detail")}); "
                            + "an id made from a key is claimed only when it is known to be free, so a release or the next run asks again",
                            Held: false,
                            Version: null);
                        break;
                }
            }
        }

        return reasons;
    }

    /// <summary>
    /// A record held before staging because its OSDU id is not known to be free, as a hold: the origin and version the work
    /// was built from, no OSDU id (the one it names is not the ledger's), and the reason. An id OSDU holds a record at is kept
    /// apart, with the version OSDU answered, as the one an operator's adoption claims.
    /// </summary>
    private static RecordState OccupiedState(RecordState record, Occupancy occupancy) => record with
    {
        OccupiedTargetId = occupancy.Held ? record.TargetId : null,
        OccupiedTargetVersion = occupancy.Held ? occupancy.Version : null,
        TargetId = null,
        PendingDocumentRef = null,
        WorkBatch = null,
        PendingMetadataHash = null,
        PendingPayloadHash = null,
        PendingPayloadLocation = null,
        PendingMetadata = false,
        PendingPayload = false,
        CacheSetId = null,
        LastError = occupancy.Reason,
    };

    /// <summary>
    /// A record staging refused because another record claimed its OSDU id, as a hold: the origin and version the work was
    /// built from, no OSDU id (the one it names is not this record's), and the conflict as its reason.
    /// </summary>
    private static RecordState ConflictState(RecordState refused, TargetIdConflict conflict) => refused with
    {
        TargetId = null,
        PendingDocumentRef = null,
        WorkBatch = null,
        PendingMetadataHash = null,
        PendingPayloadHash = null,
        PendingPayloadLocation = null,
        PendingMetadata = false,
        PendingPayload = false,
        CacheSetId = null,
        LastError = conflict.Describe(),
    };

    private async Task FlushHeldAsync(FlowDefinition flow, SubmissionState submission, List<RecordState> held, CancellationToken ct)
    {
        // A record released, redelivered or delivered since the plan read it is not held: the ledger asks for it to be
        // planned again instead, and no event says it was held.
        var marked = (await _ledger.MarkHeldAsync(flow.Id, held, ct).ConfigureAwait(false)).ToHashSet();
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var record in held.Where(r => marked.Contains(r.DeliveryKey)))
        {
            await _listener.OnEventAsync(new DeliveryEvent
            {
                AtUtc = now,
                FlowId = flow.Id,
                FlowName = flow.Label,
                Interface = flow.Interface,
                Kind = "record.held",
                SubmissionId = submission.SubmissionId,
                DeliveryKey = record.DeliveryKey,
                SourceKey = record.SourceKey,
                Label = record.Label,
                TargetId = record.TargetId,
                Worker = "intake",
                Phase = "render",
                Detail = record.LastError,
            }, ct).ConfigureAwait(false);
        }

        held.Clear();
    }

    private RecordState HeldState(FlowDefinition flow, SubmissionState submission, PlanEntry entry, ResolvedMapping resolvedMapping) => new()
    {
        DeliveryKey = entry.Key!.Value,
        FlowId = flow.Id,
        SourceKey = entry.SourceKey,
        SourceKeyJson = entry.SourceKeyJson,
        Label = entry.Label,
        Identities = entry.Identities,
        MappingName = resolvedMapping.Mapping.Name,
        TargetId = entry.TargetId,
        LastSubmissionId = submission.SubmissionId,
        RunId = RunId,
        PendingSourceFingerprint = entry.SourceFingerprint,
        PendingSourceModifiedUtc = entry.SourceModifiedUtc,
        PendingSourceFileName = entry.Origin.FileName,
        PendingSourceRowNumber = entry.Origin.RowNumber,
        PendingSourceUpdatedUtc = entry.Origin.UpdatedUtc,
        PendingSourceDeletedUtc = entry.SourceDeletedUtc,
        SourceInsertedUtc = entry.Origin.InsertedUtc,
        LastError = Http.HeaderRedaction.RedactMessage(entry.Reason),
        // The parent the held render read, or found missing: the ledger plans the record again when it lands.
        ParentId = entry.Render?.Parent?.Id,
        PendingParentVersion = entry.Render?.Parent?.Version,
        // The children the held render looked for: what a child landing finds the record by once its hold is lifted.
        ChildLinkKey = entry.Render?.ChildLinkKey,
        Basis = PlanBasis.Of(entry.Existing),
    };

    private RecordState PendingState(FlowDefinition flow, SubmissionState submission, ResolvedMapping resolved, PlanEntry entry, DocumentRef reference, int batch) => new()
    {
        DeliveryKey = entry.Key!.Value,
        FlowId = flow.Id,
        SourceKey = entry.SourceKey,
        SourceKeyJson = entry.SourceKeyJson,
        Label = entry.Label,
        Identities = entry.Identities,
        MappingName = resolved.Mapping.Name,
        TargetId = entry.TargetId,
        LastSubmissionId = submission.SubmissionId,
        RunId = RunId,
        PendingDocumentRef = reference.ToString(),
        WorkBatch = batch,
        PendingRenderContext = resolved.Context.Canonical(),
        PendingSourceFingerprint = entry.SourceFingerprint,
        PendingSourceModifiedUtc = entry.SourceModifiedUtc,
        PendingSourceFileName = entry.Origin.FileName,
        PendingSourceRowNumber = entry.Origin.RowNumber,
        PendingSourceUpdatedUtc = entry.Origin.UpdatedUtc,
        SourceInsertedUtc = entry.Origin.InsertedUtc,
        PendingMetadataHash = entry.Render!.MetadataHash,
        PendingPayloadHash = entry.PayloadHash,
        PendingPayloadModifiedUtc = entry.DeliverPayload ? entry.PayloadModifiedUtc : null,
        PendingPayloadLocation = entry.PayloadLocation,
        PendingMetadata = entry.DeliverMetadata,
        PendingPayload = entry.DeliverPayload,
        PendingReferences = entry.References,
        // The parent the document's sections were taken from, and its version: what the record's history traces them to.
        ParentId = entry.Render!.Parent?.Id,
        PendingParentVersion = entry.Render.Parent?.Version,
        // The children the document links, or was rendered without: a child landing plans the record again.
        ChildLinkKey = entry.Render.ChildLinkKey,
        Basis = PlanBasis.Of(entry.Existing),
    };

    /// <summary>Closes the submission after the drains finished, with honest counts scoped to the records it touched.</summary>
    public Task<SubmissionState> CompleteAsync(Guid submissionId, Guid flowId, CancellationToken ct = default)
        => CloseAsync(submissionId, flowId, stopped: null, ct);

    /// <summary>
    /// Closes a submission whose run stopped before its records were all sent (a failure guard tripped, the run was
    /// cancelled, or the run ended and a later run took the submission over): its totals counted as
    /// <see cref="CompleteAsync"/> counts them, and failed with <paramref name="reason"/> while records of it are still
    /// pending, or when its planning never finished, since a plan cut short is no completed one whatever it had sent. A
    /// closed submission's pending records are sent by the flow's next run, so what the stop left is not stranded behind a
    /// submission nobody works on any more.
    /// </summary>
    public Task<SubmissionState> StopAsync(Guid submissionId, Guid flowId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return CloseAsync(submissionId, flowId, reason, ct);
    }

    /// <summary>
    /// Counts a submission's totals from the ledger and closes it: running while records of it are still pending, else
    /// failed when any of its records failed, else completed; with <paramref name="stopped"/>, failed with that reason
    /// while records are still pending or its planning never finished. The listeners hear of a submission that closes.
    /// </summary>
    private async Task<SubmissionState> CloseAsync(Guid submissionId, Guid flowId, string? stopped, CancellationToken ct)
    {
        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");
        // Deliveries are counted from the attempts the submission wrote, not from where record pointers now stand: a
        // record can move on to a newer submission while its delivery for this one is still in flight.
        var delivered = await _ledger.CountAttemptsAsync(submissionId, AttemptOutcome.Delivered, null, ct).ConfigureAwait(false);
        var unchangedAtPush = await _ledger.CountAttemptsAsync(submissionId, AttemptOutcome.Skipped, AttemptPhases.Unchanged, ct).ConfigureAwait(false);
        var held = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Held, ct).ConfigureAwait(false);
        var failed = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Failed, ct).ConfigureAwait(false);
        var waiting = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Waiting, ct).ConfigureAwait(false);
        var stillPending = await _ledger.HasPendingAsync(flowId, submissionId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        var wasClosed = submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed;
        var stop = stopped is not null && (stillPending || submission.Status == SubmissionStatus.Received);
        submission = submission with
        {
            Delivered = delivered,
            UnchangedAtPush = unchangedAtPush,
            Held = held,
            Failed = failed,
            Waiting = waiting,
            Status = stop ? SubmissionStatus.Failed : stillPending ? SubmissionStatus.Running : (failed > 0 ? SubmissionStatus.Failed : SubmissionStatus.Completed),
            CompletedUtc = stop || !stillPending ? now : null,
            Error = stop ? stopped : submission.Error,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        if (stop)
        {
            await EmitAsync(null, submission, "submission.completed", $"{stopped}; {Summarize(submission)}", ct).ConfigureAwait(false);
        }
        else if (!stillPending && !wasClosed)
        {
            await EmitAsync(null, submission, "submission.completed", Summarize(submission), ct).ConfigureAwait(false);
        }

        return submission;
    }

    public static string Summarize(SubmissionState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return string.Create(CultureInfo.InvariantCulture, $"{s.Status.ToString().ToLowerInvariant()}: {s.Planned} planned in {s.BatchCount} batch(es), {s.Delivered} delivered, {s.SkippedUnchanged + s.UnchangedAtPush} unchanged, {s.AwaitingApproval} awaiting approval, {s.SkippedStale} stale, {s.Blocked} blocked, {s.Held} held, {s.Failed} failed, {s.Waiting} waiting");
    }

    /// <summary>The batch numbers a share writes: a namespace per first slice, so fan-out members never collide.</summary>
    public static int BatchBase(int firstSlice)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSlice);
        if (firstSlice > MaxFanOutPartition)
        {
            throw new DeliveryException($"A fan-out intake covers at most {MaxFanOutPartition + 1} slices; slice {firstSlice} is out of range.");
        }

        return firstSlice * BatchesPerPartition;
    }

    private ValueTask EmitAsync(FlowDefinition? flow, SubmissionState submission, string kind, string? detail, CancellationToken ct)
        => _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _time.GetUtcNow().UtcDateTime,
            FlowId = submission.FlowId,
            FlowName = flow?.Label ?? submission.FlowName,
            Interface = flow?.Interface,
            Kind = kind,
            SubmissionId = submission.SubmissionId,
            Worker = "intake",
            Detail = detail,
        }, ct);
}
