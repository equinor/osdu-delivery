using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Protocols;

/// <summary>One payload file of a record, in delivery order: its position, where it is, how long it is and when it was last written.</summary>
public sealed record PayloadFile(int Index, string Path, long Size, DateTimeOffset? Modified = null);

/// <summary>Opens a record's payload files for streaming. Each open returns a fresh stream, so retries re-open the blob.</summary>
public interface IPayloadSource
{
    Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default);

    Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default);
}

/// <summary>What one delivery attempt must do for one record.</summary>
public sealed record DeliveryWork
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoSteps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public required DeliveryKey Key { get; init; }

    public required string TargetId { get; init; }

    public required JsonObject Document { get; init; }

    /// <summary>
    /// What the mapping's assertions found of <see cref="Document"/> when it was rendered (osdu/docs/reference/flow/mapping-assertions.md),
    /// or null when the mapping states none or the document was queued before mappings could assert. The check before
    /// sending adds them to the record's verdict and holds the record for a failure whose action is hold.
    /// </summary>
    public Validation.AssertionFindings? Assertions { get; init; }

    public required bool DeliverMetadata { get; init; }

    public required bool DeliverPayload { get; init; }

    public IPayloadSource? Payload { get; init; }

    /// <summary>The last known OSDU version, when updating; null when creating.</summary>
    public long? ExistingVersion { get; init; }

    /// <summary>
    /// True when OSDU no longer held the record when it was last looked for (a verify, or an inventory that found it missing):
    /// a route that writes a storage record brings a soft-deleted record back under its id before it writes, so what the
    /// write keeps of the record (its preserved keys) is read from it, and a purged one is written again under the same id.
    /// </summary>
    public bool RestoreFirst { get; init; }

    public string? SourceKey { get; init; }

    public string? Label { get; init; }

    /// <summary>
    /// The steps an earlier try of this same pending work already completed, by step name, with what the target
    /// returned for each (osdu/docs/reference/concepts/protocols.md, Steps and returned values). A protocol skips a
    /// completed step and reuses its values: a file uploaded and registered by the previous try is referenced, not uploaded
    /// again.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> CompletedSteps { get; init; } = NoSteps;

    /// <summary>
    /// Called by the protocol after every step that changed the target, with what the target returned and what the step
    /// created in OSDU (osdu/docs/reference/concepts/protocols.md, When a delivery does not complete), and before a call
    /// whose id the service chooses, with its intent. The worker persists it on the record, the artifacts in the same
    /// transaction, before the next step starts, so a crash never repeats a completed step and never loses an id a step
    /// minted.
    /// </summary>
    public Func<StepReport, CancellationToken, Task>? StepCompleted { get; init; }

    /// <summary>
    /// The unit of work this delivery belongs to (osdu/docs/reference/concepts/protocols.md, When a delivery does not
    /// complete): a later try of the same pending work carries the same unit. Null outside the worker (a restore, a test),
    /// where nothing records what the delivery creates.
    /// </summary>
    public DeliveryUnit? Unit { get; init; }

    /// <summary>The identifiers the target returned for this record in earlier deliveries (dataset ids, a workflow run).</summary>
    public IReadOnlyDictionary<string, string> TargetState { get; init; } = NoValues;

    /// <summary>
    /// The parts of the record's payload a route that sends parts reads (osdu/docs/reference/concepts/protocols.md, Payload
    /// parts), each with its files and content hash; empty for a route that sends one payload set (<see cref="Payload"/>)
    /// or none.
    /// </summary>
    public IReadOnlyList<WorkPayloadPart> Parts { get; init; } = [];

    /// <summary>The parts this delivery sends whatever their hashes say (a redelivery named them, or the record never delivered its payload).</summary>
    public IReadOnlySet<string> ForcedParts { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The datasets the record's steps of <c>enrich:</c> register (osdu/docs/reference/flow/enrich-files.md),
    /// each with its files to stream; empty for a record whose steps register none. The route that wraps the flow's own sends
    /// each whose hash moved before the record, and the record names them already.
    /// </summary>
    public IReadOnlyList<WorkStepPart> StepParts { get; init; } = [];

    /// <summary>
    /// True when the plan found the record's document the one the last delivery wrote, its step datasets alone having moved
    /// (osdu/docs/reference/flow/enrich-files.md, What a change sends): the datasets go, and the record, which names them already, is
    /// not written again. False writes it.
    /// </summary>
    public bool StepDocumentUnchanged { get; init; }

    /// <summary>True when every step dataset goes whatever the hash its last delivery kept: the record was asked to be sent again, or never was.</summary>
    public bool StepPartsForced { get; init; }

    /// <summary>
    /// The datasets of the steps set aside on their holds (osdu/docs/reference/flow/enrich-holds.md), which an earlier delivery
    /// registered and the document still links: the route keeps them listed as the record's, neither sent nor superseded.
    /// </summary>
    public IReadOnlyList<string> StepKeptDatasets { get; init; } = [];

    /// <summary>
    /// What each step set aside on its holds composed into this document, by target state key (<c>enrich.step.&lt;name&gt;</c>):
    /// the worker keeps it with the record once the delivery succeeds, for a later render the step holds in.
    /// </summary>
    public IReadOnlyDictionary<string, string> StepKeep { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether this delivery sends <paramref name="part"/>: the payload is due, and the part is forced or its content
    /// hash differs from the one the record last delivered it with.
    /// </summary>
    public bool Sends(WorkPayloadPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return DeliverPayload
            && (ForcedParts.Contains(part.Role)
                || !TargetState.TryGetValue(Model.PayloadParts.StateKey(part.Payload), out var delivered)
                || !string.Equals(delivered, part.Hash, StringComparison.Ordinal));
    }

    /// <summary>Whether this delivery runs a part that has no files of its own (the workflow run) because it is forced.</summary>
    public bool Forces(string role) => DeliverPayload && ForcedParts.Contains(role);

    /// <summary>The values of a completed step, or null when the step has not run for this pending work.</summary>
    public IReadOnlyDictionary<string, string>? Completed(string step)
        => CompletedSteps.TryGetValue(step, out var values) ? values : null;

    /// <summary>Reports a completed step to the worker (a no-op when nobody listens).</summary>
    public Task ReportStepAsync(string step, IReadOnlyDictionary<string, string> returned, CancellationToken ct)
        => ReportStepAsync(step, returned, [], ct);

    /// <summary>
    /// Reports a completed step to the worker with what it created in OSDU, or, for a step marked before its call goes, with
    /// the intent of what the call will create (a no-op when nobody listens). Every artifact is checked first, so a route that
    /// reports one the ledger cannot keep fails here, before the call it describes.
    /// </summary>
    /// <exception cref="DeliveryException">An artifact is not one the ledger can keep.</exception>
    public Task ReportStepAsync(string step, IReadOnlyDictionary<string, string> returned, IReadOnlyList<TargetArtifact> artifacts, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentNullException.ThrowIfNull(returned);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (string.Equals(step, DeliveryUnit.StepName, StringComparison.Ordinal))
        {
            throw new DeliveryException($"A route reported a step named '{DeliveryUnit.StepName}', the name the worker keeps a delivery's unit of work under.");
        }

        foreach (var artifact in artifacts)
        {
            artifact.Validate();
        }

        return StepCompleted is null ? Task.CompletedTask : StepCompleted(new StepReport(step, returned, artifacts), ct);
    }
}

/// <summary>One part of a record's payload as a route that sends parts reads it.</summary>
/// <param name="Role">The part (files, bulk).</param>
/// <param name="Payload">The payload set it is read from.</param>
/// <param name="Source">Its files; null for an optional part the record carries none for.</param>
/// <param name="Hash">Its content hash, which the record's target state keeps once the part is delivered.</param>
public sealed record WorkPayloadPart(string Role, string Payload, IPayloadSource? Source, string Hash);

/// <summary>One dataset a step of the flow's <c>enrich:</c> registers for the record, and where its files stream from.</summary>
public sealed record WorkStepPart(Rendering.EnrichPart Part, IPayloadSource Source);

/// <summary>One step of a delivery attempt, with its timing, the status the target answered, and what it returned.</summary>
public sealed record DeliveryStep(
    string Name,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    int? Status,
    IReadOnlyDictionary<string, string> Returned,
    bool Resumed = false,
    string? Error = null);

/// <summary>Builds the step list of one attempt as the protocol goes.</summary>
public sealed class DeliverySteps
{
    private readonly TimeProvider _time;
    private readonly List<DeliveryStep> _steps = [];

    public DeliverySteps(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<DeliveryStep> Steps => _steps;

    /// <summary>All values returned so far, later steps overriding earlier ones on the same name.</summary>
    public IReadOnlyDictionary<string, string> Returned
    {
        get
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var step in _steps)
            {
                foreach (var (name, value) in step.Returned)
                {
                    merged[name] = value;
                }
            }

            return merged;
        }
    }

    public DateTime Now => _time.GetUtcNow().UtcDateTime;

    public void Add(string name, DateTime started, int? status, IReadOnlyDictionary<string, string>? returned = null, string? error = null)
        => _steps.Add(new DeliveryStep(name, started, Now, status, returned ?? new Dictionary<string, string>(StringComparer.Ordinal), false, error));

    /// <summary>A step an earlier try completed: recorded as resumed, with the values it returned then.</summary>
    public void Resumed(string name, IReadOnlyDictionary<string, string> returned)
    {
        var now = Now;
        _steps.Add(new DeliveryStep(name, now, now, null, returned, Resumed: true));
    }
}

public sealed record DeliveryOutcome
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    public required bool MetadataDelivered { get; init; }

    public required bool PayloadDelivered { get; init; }

    /// <summary>The OSDU version after the write, when the response reported one.</summary>
    public long? TargetVersion { get; init; }

    public int ChunksSent { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// Every value the target returned for the record: the record id and version, dataset ids and file sources,
    /// a bulk session id, a workflow run id. Merged into the record's target state and written on the attempt.
    /// </summary>
    public IReadOnlyDictionary<string, string> Returned { get; init; } = NoValues;

    /// <summary>The steps the attempt took, in order, with what each returned.</summary>
    public IReadOnlyList<DeliveryStep> Steps { get; init; } = [];

    /// <summary>Set when the work failed; the worker classifies it (held, retried, failed) exactly as a thrown one.</summary>
    public Exception? Failure { get; init; }

    /// <summary>
    /// The OSDU ids an earlier delivery of the record minted that this delivery made obsolete (the datasets of files a payload
    /// change replaced): they stay live in OSDU, since earlier versions of the record name them, and the ledger marks them
    /// superseded so the inventory tells them from an orphan.
    /// </summary>
    public IReadOnlyList<string> Superseded { get; init; } = [];

    public bool Succeeded => Failure is null;

    public static DeliveryOutcome Failed(Exception failure, IReadOnlyList<DeliveryStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, Failure = failure, Steps = steps ?? [] };
    }
}

public sealed record VerifyResult(Ledger.VerifyOutcome Outcome, long? ObservedVersion, string? Detail);

/// <summary>
/// One record to verify: the id the target holds it under, the version the ledger recorded for it, and, when its delivery
/// recorded what a verify compares beyond the version (the hash of the content the flow owns), the record's target state.
/// </summary>
public sealed record VerifyRequest(string TargetId, long? ExpectedVersion, IReadOnlyDictionary<string, string>? TargetState = null);

/// <summary>
/// A named delivery protocol implemented in code and parameterised by the flow (osdu/docs/reference/concepts/protocols.md,
/// Why a route is code the flow parameterises, not steps the flow writes). The core is protocol independent: identity,
/// rendering, change detection, the ledger and idempotency; only this varies. A protocol that can write several records in one request declares
/// <see cref="MaxBatch"/> above one and the worker hands it batches; every protocol reports each step it took and what
/// the target returned.
/// </summary>
public interface IDeliveryProtocol
{
    DeliveryProtocol Kind { get; }

    /// <summary>Records per write request the protocol can batch. One delivers each record in its own request.</summary>
    int MaxBatch => 1;

    Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default);

    /// <summary>
    /// Delivers several records, in one request when <see cref="MaxBatch"/> allows it. Outcomes align with the
    /// works; an item that failed carries its failure instead of throwing, so one bad record never sinks the batch.
    /// </summary>
    async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new List<DeliveryOutcome>(works.Count);
        foreach (var work in works)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(await DeliverAsync(work, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                outcomes.Add(DeliveryOutcome.Failed(ex));
            }
        }

        return outcomes;
    }

    /// <summary>Reads the record back and compares the observed version with the expected one.</summary>
    Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default);

    /// <summary>Records one batched read of the target takes: the id to look up and the version the ledger holds.</summary>
    int MaxVerifyBatch => 1;

    /// <summary>
    /// Whether a verify needs each record's target state, which then goes with every <see cref="VerifyRequest"/>: a protocol
    /// whose target keeps a record under a key the target gave (a DSPDM row's primary key) finds the record by it.
    /// </summary>
    bool VerifiesWithTargetState => false;

    /// <summary>
    /// Verifies several records, in one request when <see cref="MaxVerifyBatch"/> allows it. Results align with
    /// <paramref name="requests"/>. The default reads them one at a time; a protocol whose service takes a list of
    /// ids overrides this, which is what keeps a drift pass over a large estate to a handful of requests rather
    /// than one per record.
    /// </summary>
    async Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<VerifyResult>(requests.Count);
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await VerifyAsync(request.TargetId, request.ExpectedVersion, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// The legal tags among <paramref name="tags"/> the legal service would refuse, each with its reason, or null
    /// when this target does not ask the legal service: a protocol without a legal check, a flow that turned the
    /// check off, or a well log flow whose endpoint is the DDMS and names no legal path. Null means "not checked"
    /// and is never to be read as "valid".
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    /// <summary>
    /// Removes the record from OSDU to the extent <paramref name="scope"/> asks for. A record that is already gone
    /// is not an error. <paramref name="targetState"/> carries the identifiers of what else the record owns (its
    /// dataset records), for protocols that remove those too.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default);

    /// <summary>
    /// Removes several records. Outcomes align with <paramref name="removals"/>; one that failed carries its failure
    /// instead of throwing, so one bad record never sinks the set. The default removes them one at a time; a protocol
    /// whose service takes a list overrides this for the scopes that service can batch.
    /// </summary>
    Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
        => DeleteOneByOneAsync(removals, scope, ct);

    /// <summary>
    /// Removes the records one at a time through <see cref="DeleteAsync"/>, each reporting its own outcome. This is
    /// the fallback behind every batched removal: a protocol whose service takes a list uses it for the scopes that
    /// service cannot batch, and for a chunk the service refused over something other than the records in it.
    /// </summary>
    async Task<IReadOnlyList<RemovalResult>> DeleteOneByOneAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        var results = new List<RemovalResult>(removals.Count);
        foreach (var removal in removals)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await DeleteAsync(removal.TargetId, scope, removal.TargetState, ct).ConfigureAwait(false);
                results.Add(new RemovalResult(removal, outcome, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                results.Add(new RemovalResult(removal, null, ex));
            }
        }

        return results;
    }

    /// <summary>Reads the record back as the target holds it, or null when the target has no such record.</summary>
    Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default);

    /// <summary>
    /// Reads the record back as the target holds it, given what its deliveries recorded (<paramref name="targetState"/>), or
    /// null when the target has no such record. A protocol that finds a record by its id alone reads it as
    /// <see cref="ReadAsync(string, CancellationToken)"/> does.
    /// </summary>
    Task<JsonObject?> ReadAsync(string targetId, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
        => ReadAsync(targetId, ct);

    /// <summary>
    /// The versions the target keeps of one record, newest first, or null when the target keeps no version list for it
    /// (a DDMS, a historian, a store that overwrites in place). The storage service answers it for every record it holds,
    /// so a protocol whose records live there lists them; the others say null, which the page reads as "no history".
    /// </summary>
    Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<long>?>(null);

    /// <summary>
    /// The record as the target held it at one of its versions, or null when the target has no such version. A protocol
    /// whose target keeps no version list (see <see cref="VersionsAsync"/>) refuses, since there is nothing to read at.
    /// </summary>
    Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => throw new DeliveryException($"The target keeps no version history for {targetId}, so there is no version {version.ToString(System.Globalization.CultureInfo.InvariantCulture)} to read.");

    /// <summary>A reachability and credential check against the service's info endpoint, under the flow's auth.</summary>
    Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether a delivery on this route can create anything in OSDU before its last call
    /// (osdu/docs/reference/concepts/protocols.md, When a delivery does not complete): files, datasets, a record before its
    /// bulk data, rows, a session. Such a route reports each artifact with the step that created
    /// it and undoes an aborted unit (<see cref="UndoAsync"/>); the worker reads and undoes artifacts only for it. A route
    /// that makes one atomic write has nothing to undo.
    /// </summary>
    bool Undoes => false;

    /// <summary>
    /// Undoes what aborted units created (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished deliveries and
    /// their undo): removes what they created, reversibly where the route can, writes back the version a write replaced
    /// where the route can, and answers every other artifact kept, with why. Results answer every item of every work, in
    /// any order; an item the route cannot reach is
    /// answered <see cref="ArtifactStatus.Failed"/> rather than thrown, so one record's failure never hides another's undo.
    /// An undo is idempotent: an artifact OSDU no longer holds is <see cref="ArtifactStatus.Gone"/>. The default keeps every
    /// artifact, for a route that creates none.
    /// </summary>
    Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var note = $"the {DeliveryProtocols.Name(Kind)} route makes one write, so it has nothing to undo";
        return Task.FromResult<IReadOnlyList<UndoResult>>(works.SelectMany(w => w.Items).Select(i => UndoResult.Kept(i, note)).ToList());
    }

    /// <summary>
    /// Writes each record back as OSDU held it at an earlier version, as the record's next version (a reversal,
    /// osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission).
    /// <see cref="VersionRestore.Stored"/> is the version as <see cref="ReadVersionAsync"/> read it:
    /// its system properties are left out, and only the data keys another system writes on its own are carried from the
    /// latest version. Results align with <paramref name="restores"/>; one that failed carries its failure instead
    /// of throwing. The default refuses every one: a route whose records carry what a version of the record does not bring
    /// back (files, rows, objects kept outside it) cannot restore one, and <c>ReversalRoute</c> says so before anything is
    /// asked of it.
    /// </summary>
    Task<IReadOnlyList<RestoreResult>> RestoreBatchAsync(IReadOnlyList<VersionRestore> restores, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restores);
        var refused = new DeliveryException($"The {DeliveryProtocols.Name(Kind)} route cannot write an earlier version of a record back.");
        return Task.FromResult<IReadOnlyList<RestoreResult>>(restores.Select(r => new RestoreResult(r, null, null, refused)).ToList());
    }
}

/// <summary>One record a reversal writes back: its OSDU id, the version to write back as OSDU held it, and what OSDU holds now.</summary>
/// <param name="Key">The record's delivery key.</param>
/// <param name="TargetId">The OSDU id the record claimed.</param>
/// <param name="Version">The version written back.</param>
/// <param name="Stored">That version as the target held it, system properties included.</param>
/// <param name="Latest">The version OSDU holds now, which the write replaces.</param>
/// <param name="TargetState">What the record's deliveries recorded of it (the hashes of the content it owns, ids beside it).</param>
public sealed record VersionRestore(DeliveryKey Key, string TargetId, long Version, JsonObject Stored, long? Latest, IReadOnlyDictionary<string, string>? TargetState);

/// <summary>What writing one record back came to: the version OSDU gave it and what it returned, or the failure that stopped it.</summary>
public sealed record RestoreResult(VersionRestore Restore, long? NewVersion, IReadOnlyDictionary<string, string>? Returned, Exception? Failure)
{
    public bool Succeeded => Failure is null && NewVersion is not null;
}

/// <summary>What a probe found: whether the service answered, with which status, and the path it was asked on.</summary>
public sealed record ProbeOutcome(bool Reachable, int Status, string Detail, string Path);

public sealed record DeleteOutcome(bool Deleted, bool AlreadyGone, string Detail);

/// <summary>
/// How much of a record a removal takes away in OSDU (openapi storage v2). The three are genuinely different
/// operations against different endpoints, not degrees of one: only <see cref="Record"/> can be undone, and only
/// <see cref="History"/> leaves the record live.
/// </summary>
public enum RemovalScope
{
    /// <summary>
    /// <c>POST /records/{id}:delete</c>: the record stops resolving in OSDU. Nothing is destroyed and OSDU can
    /// revert it. This is the only reversible scope.
    /// </summary>
    Record,

    /// <summary>
    /// <c>DELETE /records/{id}/versions</c>: every earlier version is destroyed permanently and the latest version
    /// stays live and retrievable. The record itself is untouched, so the ledger keeps it delivered.
    /// </summary>
    History,

    /// <summary>
    /// <c>DELETE /records/{id}</c>: the record and every one of its versions are destroyed permanently. Cannot be
    /// undone.
    /// </summary>
    Everything,
}

/// <summary>What bounds one removal, wherever it is driven from.</summary>
public static class RemovalLimits
{
    /// <summary>
    /// The most records one removal can select by filter. It bounds the task payload, the work a single node takes
    /// on, and the blast radius of a mis-aimed filter; a larger removal is several removals.
    /// </summary>
    public const int MaxSelection = 25_000;

    /// <summary>Records resolved, removed and written back per round trip.</summary>
    public const int Chunk = 500;

    /// <summary>Per-record results carried back in a removal's task result before it is summarised instead.</summary>
    public const int MaxReported = 200;
}

/// <summary>One record a removal is to act on: its OSDU id and what earlier deliveries registered alongside it.</summary>
public sealed record RecordRemoval(DeliveryKey Key, string TargetId, IReadOnlyDictionary<string, string>? TargetState);

/// <summary>What a batched removal did to one record: its outcome, or the failure that stopped it.</summary>
public sealed record RemovalResult(RecordRemoval Removal, DeleteOutcome? Outcome, Exception? Failure)
{
    public bool Succeeded => Failure is null && Outcome is not null;
}
