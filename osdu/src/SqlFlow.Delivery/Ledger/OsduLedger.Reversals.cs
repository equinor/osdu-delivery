using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Reversals (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission): what one run or
/// one submission put into OSDU, listed record by record with what OSDU held before, and settled with each record. The
/// listing and the settlement read and write a slice at a time; the counts are read from the items.
/// </summary>
public sealed partial class OsduLedger
{
    /// <summary>The most records a page of a reversal's items or a preview's sample names.</summary>
    public const int MaxReversalPage = 1_000;

    /// <summary>The most reversals one listing names.</summary>
    public const int MaxReversals = 200;

    /// <summary>
    /// The most of a run's own attempts in one ledger <see cref="SourceDeliveredAsync"/> reads for a delivered one, once the
    /// submissions the run planned delivered nothing. A run's attempts are read through the run's index, which does not hold
    /// the outcome, so each one read is a lookup: the bound keeps the answer quick for a run that held a million records and
    /// delivered none. A run that wrote more attempts than this in the ledger, none of the first of them delivered, and
    /// delivered only records of other submissions is answered as having delivered nothing; those submissions are reversed
    /// on their own.
    /// </summary>
    public const int SourceDeliveredProbe = 50_000;

    public async Task<ReversalState> OpenReversalAsync(Guid flowId, string flowName, ReversalSource source, string actor, Guid? runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            await using var db = Open();
            var reversal = await db.DeliveryReversals
                .FirstOrDefaultAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.SourceKind == source.Kind && r.SourceId == source.Id, ct)
                .ConfigureAwait(false);
            if (reversal is null)
            {
                var submissions = await SourceSubmissionsAsync(db, partition, flowId, flowName, source, ct).ConfigureAwait(false);
                reversal = new DeliveryReversal
                {
                    PartitionId = partition,
                    FlowId = flowId,
                    FlowName = flowName,
                    SourceKind = source.Kind,
                    SourceId = source.Id,
                    SubmissionsJson = JsonSerializer.Serialize(submissions),
                    RequestedBy = actor,
                    RequestedUtc = nowUtc,
                };
                db.DeliveryReversals.Add(reversal);
            }

            // Opened, or resumed: the latest run says it works on it now, and how far it is decides where it starts.
            reversal.Status = reversal.CapturedUtc is null ? ReversalStatuses.Capturing : ReversalStatuses.Reversing;
            reversal.StartedUtc = nowUtc;
            reversal.CompletedUtc = null;
            reversal.LastRunId = runId;
            reversal.Error = null;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < 3 && IsDuplicate(ex))
            {
                // Opened by another request meanwhile: resume that one.
                continue;
            }

            return await StateOfAsync(reversal, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The submissions a source covers, checked to be the ledger's: a submission must be one of its own, and a run must have
    /// coordinated a submission of it or delivered (or tried to deliver) a record of it.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SourceSubmissionsAsync(OsduDbContext db, short partition, Guid flowId, string flowName, ReversalSource source, CancellationToken ct)
    {
        if (!source.IsRun)
        {
            var submission = await db.DeliverySubmissions.AsNoTracking()
                .Where(s => s.SubmissionId == source.Id)
                .Select(s => new { s.PartitionId, s.FlowId, s.FlowName })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (submission is null)
            {
                throw new DeliveryException($"Submission {source.Id:D} is not in the ledger, so there is nothing of it to reverse.");
            }

            if (submission.PartitionId != partition || submission.FlowId != flowId)
            {
                throw new DeliveryException($"Submission {source.Id:D} belongs to flow '{submission.FlowName}', not '{flowName}'; reverse it in its own flow.");
            }

            return [source.Id];
        }

        var coordinated = await db.DeliverySubmissions.AsNoTracking()
            .Where(s => s.RunId == source.Id && s.PartitionId == partition && s.FlowId == flowId)
            .OrderBy(s => s.ReceivedUtc)
            .ThenBy(s => s.SubmissionId)
            .Select(s => s.SubmissionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (coordinated.Count == 0 && !await SqlServerLedgerBulk.RunTouchedAsync(db, partition, flowId, source.Id, ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Run {source.Id:D} neither planned a submission of flow '{flowName}' nor delivered a record of it, so there is nothing of it to reverse there.");
        }

        return coordinated;
    }

    public async Task<ReversalCapture> CaptureReversalAsync(long reversalId, Func<ReversalCapture, Task>? progress, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        await using var db = Open();
        var reversal = await db.DeliveryReversals.FirstOrDefaultAsync(r => r.ReversalId == reversalId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Reversal {reversalId} is not in the ledger."));
        var partition = reversal.PartitionId;
        var flowId = reversal.FlowId;
        if (reversal.CapturedUtc is not null)
        {
            return new ReversalCapture(0, await CountItemsAsync(partition, reversalId, ct).ConfigureAwait(false));
        }

        var submissions = ParseSubmissions(reversal.SubmissionsJson);
        Guid? runId = reversal.SourceKind == ReversalSource.RunKind ? reversal.SourceId : null;
        long added = 0;
        long read = 0;

        // Each submission's delivered attempts, a phase at a time in the order of the submission's index.
        foreach (var submission in submissions)
        {
            var phases = await RetryDeadlockAsync(() => SqlServerLedgerBulk.SubmissionPhasesAsync(db, submission, ct), ct).ConfigureAwait(false);
            foreach (var phase in phases)
            {
                long after = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var from = after;
                    var page = await RetryDeadlockAsync(
                        () => SqlServerLedgerBulk.SubmissionDeliveredPageAsync(db, partition, submission, phase, from, WriteSlice, ct), ct).ConfigureAwait(false);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    var keys = page.Select(p => p.DeliveryKey).Distinct().ToList();
                    added += await RetryDeadlockAsync(
                        () => SqlServerLedgerBulk.CaptureSliceAsync(db, partition, flowId, reversalId, keys, reversal.SubmissionsJson, runId, Now, ct), ct).ConfigureAwait(false);
                    read += page.Count;
                    if (progress is not null)
                    {
                        await progress(new ReversalCapture(added, read)).ConfigureAwait(false);
                    }

                    if (page.Count < WriteSlice)
                    {
                        break;
                    }

                    after = page[^1].AttemptId;
                }
            }
        }

        // A run's own deliveries of records of other submissions (released records it sent after its own).
        if (runId is { } run)
        {
            var after = Guid.Empty;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var from = after;
                var keys = await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.RunDeliveredPageAsync(db, partition, flowId, run, from, WriteSlice, ct), ct).ConfigureAwait(false);
                if (keys.Count == 0)
                {
                    break;
                }

                added += await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.CaptureSliceAsync(db, partition, flowId, reversalId, keys, reversal.SubmissionsJson, runId, Now, ct), ct).ConfigureAwait(false);
                read += keys.Count;
                if (progress is not null)
                {
                    await progress(new ReversalCapture(added, read)).ConfigureAwait(false);
                }

                if (keys.Count < WriteSlice)
                {
                    break;
                }

                after = keys[^1];
            }
        }

        reversal.CapturedUtc = Now;
        reversal.Status = ReversalStatuses.Reversing;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ReversalCapture(added, await CountItemsAsync(partition, reversalId, ct).ConfigureAwait(false));
    }

    private Task<long> CountItemsAsync(short partition, long reversalId, CancellationToken ct)
        => ReadAsync(db => db.DeliveryReversalItems.LongCountAsync(i => i.PartitionId == partition && i.ReversalId == reversalId, ct), ct);

    public async Task<IReadOnlyList<ReversalItemState>> ListReversalWorkAsync(long reversalId, DeliveryKey? after, int max, CancellationToken ct = default)
    {
        if (await ReversalPartitionAsync(reversalId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var from = after?.Value ?? Guid.Empty;
        var take = Math.Clamp(max, 1, MaxReversalPage);
        var rows = await ReadAsync(
            db => db.DeliveryReversalItems
                .Where(i => i.PartitionId == partition && i.ReversalId == reversalId && i.DeliveryKey.CompareTo(from) > 0
                    && (i.State == ReversalItemStates.Pending || i.State == ReversalItemStates.Sending || i.State == ReversalItemStates.Failed
                        || (i.State == ReversalItemStates.Skipped && (i.Outcome == ReversalOutcomes.Busy || i.Outcome == ReversalOutcomes.Restricted))))
                .OrderBy(i => i.DeliveryKey)
                .Take(take)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task MarkReversalSendingAsync(long reversalId, IReadOnlyList<DeliveryKey> keys, Guid? runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || await ReversalPartitionAsync(reversalId, ct).ConfigureAwait(false) is not { } partition)
        {
            return;
        }

        await using var db = Open();
        foreach (var slice in keys.Select(k => k.Value).Distinct().Chunk(WriteSlice))
        {
            await RetryDeadlockAsync(
                () => db.DeliveryReversalItems
                    .Where(i => i.PartitionId == partition && i.ReversalId == reversalId && slice.Contains(i.DeliveryKey))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(i => i.State, ReversalItemStates.Sending).SetProperty(i => i.RunId, runId).SetProperty(i => i.UpdatedUtc, nowUtc),
                        ct),
                ct).ConfigureAwait(false);
        }
    }

    public async Task<ReversalSettled> SettleReversalAsync(
        long reversalId, IReadOnlyList<ReversalSettlement> settlements, string actor, long? activityId, Guid? runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settlements);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (settlements.Count == 0)
        {
            return ReversalSettled.None;
        }

        var reversal = await ReadAsync(db => db.DeliveryReversals.AsNoTracking().FirstOrDefaultAsync(r => r.ReversalId == reversalId, ct), ct).ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Reversal {reversalId} is not in the ledger."));
        var source = ReversalSource.Of(reversal.SourceKind, reversal.SourceId);
        var rows = settlements
            .GroupBy(s => s.DeliveryKey)
            .Select(g => g.Last())
            .Select(s => Row(s, source, reversalId, actor))
            .ToList();

        await using var db = Open();
        var total = ReversalSettled.None;
        foreach (var slice in rows.Chunk(WriteSlice))
        {
            ct.ThrowIfCancellationRequested();
            var settled = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.SettleReversalSliceAsync(db, reversal.PartitionId, reversal.FlowId, reversalId, slice, actor, activityId, runId, nowUtc, ct), ct).ConfigureAwait(false);
            total = total.Add(settled);

            // After the slice commits: the records linking one the reversal took out of OSDU as their child render again and
            // leave the link out (osdu/docs/reference/flow/mapping-child.md, When a child is taken out). A removal settled
            // failed left its record as it was, so only the records the slice marked deleted are asked about.
            var removed = slice.Where(r => string.Equals(r.Change, "remove", StringComparison.Ordinal)).Select(r => r.DeliveryKey).ToList();
            if (settled.Changed > 0 && removed.Count > 0)
            {
                var deleted = StatusText.Of(RecordStatus.Deleted);
                var gone = await ReadAsync(
                    async read => (IReadOnlyList<string>)await read.DeliveryRecords.AsNoTracking()
                        .Where(r => r.PartitionId == reversal.PartitionId && r.FlowId == reversal.FlowId && removed.Contains(r.DeliveryKey)
                            && r.Status == deleted && r.TargetId != null)
                        .Select(r => r.TargetId!)
                        .ToListAsync(ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                await PlanParentsOfAsync(reversal.PartitionId, gone, nowUtc, ct).ConfigureAwait(false);
            }
        }

        return total;
    }

    /// <summary>One settlement as the settle statement reads it: the note the record keeps, and the attempt's result.</summary>
    private static SqlServerLedgerBulk.SettleRow Row(ReversalSettlement s, ReversalSource source, long reversalId, string actor)
    {
        var detail = s.Detail is null ? null : HeaderRedaction.RedactMessage(s.Detail);
        if (detail is { Length: > 2000 })
        {
            detail = detail[..2000];
        }

        var change = !s.ChangesRecord ? "none" : s.Outcome == ReversalOutcomes.Restored ? "restore" : "remove";
        var note = change switch
        {
            "restore" => string.Create(CultureInfo.InvariantCulture, $"reverted by {actor} (reversal {reversalId} of {source}): version {s.RestoredVersion}, the one OSDU held before, written back as version {s.NewVersion}; release the record or change the source to plan it again"),
            "remove" => string.Create(CultureInfo.InvariantCulture, $"removed from OSDU (reversible) by {actor} (reversal {reversalId} of {source}), which created it; release the record or change the source to plan it again"),
            _ => null,
        };
        if (note is { Length: > 2000 })
        {
            note = note[..2000];
        }

        var result = new System.Text.Json.Nodes.JsonObject();
        if (s.CorrelationId is not null)
        {
            result["correlationId"] = s.CorrelationId;
        }

        result["steps"] = new System.Text.Json.Nodes.JsonArray();
        result["reversal"] = new System.Text.Json.Nodes.JsonObject
        {
            ["reversalId"] = reversalId,
            ["source"] = source.Kind,
            ["sourceId"] = source.Id.ToString("D"),
            ["outcome"] = s.Outcome,
        };
        if (s.RestoredVersion is { } restored)
        {
            result["reversal"]!["restoredVersion"] = restored;
        }

        if (detail is not null)
        {
            result["detail"] = detail;
        }

        return new SqlServerLedgerBulk.SettleRow(
            s.DeliveryKey.Value, s.State, s.Outcome, detail, s.RestoredVersion, s.NewVersion, s.TargetStateJson, result.ToJsonString(), note, change);
    }

    public async Task CloseReversalAsync(long reversalId, string status, string? failure, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!ReversalStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{status}' is not a reversal state.", nameof(status));
        }

        var redacted = failure is null ? null : HeaderRedaction.RedactMessage(failure);
        if (redacted is { Length: > 2000 })
        {
            redacted = redacted[..2000];
        }

        await using var db = Open();
        await db.DeliveryReversals
            .Where(r => r.ReversalId == reversalId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, status).SetProperty(r => r.CompletedUtc, nowUtc).SetProperty(r => r.Error, redacted),
                ct)
            .ConfigureAwait(false);
    }

    public async Task<ReversalState?> GetReversalAsync(long reversalId, CancellationToken ct = default)
    {
        var row = await ReadAsync(db => db.DeliveryReversals.FirstOrDefaultAsync(r => r.ReversalId == reversalId, ct), ct).ConfigureAwait(false);
        return row is null ? null : await StateOfAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<ReversalState?> FindReversalAsync(Guid flowId, ReversalSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var row = await ReadAsync(
            db => db.DeliveryReversals.FirstOrDefaultAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.SourceKind == source.Kind && r.SourceId == source.Id, ct),
            ct).ConfigureAwait(false);
        return row is null ? null : await StateOfAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReversalState>> ListReversalsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(max, 1, MaxReversals);
        var rows = await ReadAsync(
            db => db.DeliveryReversals
                .Where(r => r.PartitionId == partition && r.FlowId == flowId)
                .OrderByDescending(r => r.RequestedUtc)
                .ThenByDescending(r => r.ReversalId)
                .Take(take)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var states = new List<ReversalState>(rows.Count);
        foreach (var row in rows)
        {
            states.Add(await StateOfAsync(row, ct).ConfigureAwait(false));
        }

        return states;
    }

    public async Task<ReversalCounts> CountReversalAsync(long reversalId, CancellationToken ct = default)
    {
        if (await ReversalPartitionAsync(reversalId, ct).ConfigureAwait(false) is not { } partition)
        {
            return ReversalCounts.Empty;
        }

        var groups = await ReadAsync(
            db => db.DeliveryReversalItems
                .Where(i => i.PartitionId == partition && i.ReversalId == reversalId)
                .GroupBy(i => new { i.State, i.Outcome })
                .Select(g => new { g.Key.State, g.Key.Outcome, Count = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var states = new Dictionary<string, long>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            states[group.State] = states.GetValueOrDefault(group.State) + group.Count;
            if (group.Outcome is { } outcome)
            {
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + group.Count;
            }
        }

        return new ReversalCounts(groups.Sum(g => g.Count), states, outcomes);
    }

    public async Task<IReadOnlyList<ReversalItemState>> ListReversalItemsAsync(long reversalId, string? outcome, DeliveryKey? after, int max, CancellationToken ct = default)
    {
        if (outcome is not null && !ReversalOutcomes.All.Contains(outcome, StringComparer.Ordinal) && !ReversalItemStates.All.Contains(outcome, StringComparer.Ordinal))
        {
            throw new DeliveryException($"'{outcome}' is not what came of a record of a reversal: one of {string.Join(", ", ReversalOutcomes.All)}, or {ReversalItemStates.Pending}.");
        }

        if (await ReversalPartitionAsync(reversalId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var from = after?.Value ?? Guid.Empty;
        var take = Math.Clamp(max, 1, MaxReversalPage);
        var rows = await ReadAsync(
            db =>
            {
                var query = db.DeliveryReversalItems.Where(i => i.PartitionId == partition && i.ReversalId == reversalId && i.DeliveryKey.CompareTo(from) > 0);

                // An outcome is read through the outcome index; the records not settled yet have none, and are read by state.
                query = outcome switch
                {
                    null => query,
                    ReversalItemStates.Pending => query.Where(i => i.State == ReversalItemStates.Pending || i.State == ReversalItemStates.Sending),
                    _ => query.Where(i => i.Outcome == outcome),
                };
                return query.OrderBy(i => i.DeliveryKey).Take(take).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<bool> SourceDeliveredAsync(Guid flowId, ReversalSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return false;
        }

        return await ReadAsync(db => SqlServerLedgerBulk.SourceDeliveredAsync(db, partition, flowId, source, SourceDeliveredProbe, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, PriorVersion>> PriorVersionsAsync(Guid flowId, IReadOnlyDictionary<DeliveryKey, long> current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Count == 0)
        {
            return new Dictionary<DeliveryKey, PriorVersion>();
        }

        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            throw new DeliveryException($"Flow {flowId:D} has no ledger yet, so it holds no version of any record.");
        }

        var priors = new Dictionary<DeliveryKey, PriorVersion>(current.Count);
        foreach (var slice in current.Chunk(MaxReversalPage))
        {
            ct.ThrowIfCancellationRequested();
            var read = await ReadAsync(db => SqlServerLedgerBulk.PriorVersionsAsync(db, partition, flowId, slice, ct), ct).ConfigureAwait(false);
            foreach (var (key, prior) in read)
            {
                priors[key] = prior;
            }
        }

        return priors;
    }

    public async Task<ReversalSourceRead> ReadReversalSourceAsync(Guid flowId, string flowName, ReversalSource source, int sample, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(source);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            throw new DeliveryException($"Flow '{flowName}' has no ledger yet, so nothing of it was delivered to reverse.");
        }

        var take = Math.Clamp(sample, 0, MaxReversalPage);
        return await ReadAsync(
            async db =>
            {
                var submissions = await SourceSubmissionsAsync(db, partition, flowId, flowName, source, ct).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(submissions);
                Guid? runId = source.IsRun ? source.Id : null;
                var records = await SqlServerLedgerBulk.SourceRecordsAsync(db, partition, flowId, json, runId, ct).ConfigureAwait(false);
                IReadOnlyList<ReversalItemState> items = [];
                if (take > 0 && records > 0)
                {
                    var keys = await SqlServerLedgerBulk.SourceSampleAsync(db, partition, flowId, json, runId, take, ct).ConfigureAwait(false);
                    var resolved = await SqlServerLedgerBulk.ResolveAsync(db, partition, flowId, keys, json, runId, ct).ConfigureAwait(false);
                    items = resolved.Select(r => new ReversalItemState
                    {
                        DeliveryKey = new DeliveryKey(r.DeliveryKey),
                        TargetId = r.TargetId,
                        FirstAttemptId = r.FirstAttemptId,
                        FirstVersion = r.FirstVersion,
                        RunVersion = r.RunVersion,
                        Prior = r.Prior,
                        PriorVersion = r.PriorVersion,
                        PriorAttemptId = r.PriorAttemptId,
                        State = ReversalItemStates.Pending,
                    }).ToList();
                }

                return new ReversalSourceRead(submissions, records, items);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>The partition a reversal's rows are kept under, or null for a reversal the ledger does not hold.</summary>
    private Task<short?> ReversalPartitionAsync(long reversalId, CancellationToken ct)
        => ReadAsync(db => db.DeliveryReversals.AsNoTracking().Where(r => r.ReversalId == reversalId).Select(r => (short?)r.PartitionId).FirstOrDefaultAsync(ct), ct);

    private async Task<ReversalState> StateOfAsync(DeliveryReversal row, CancellationToken ct) => new()
    {
        ReversalId = row.ReversalId,
        FlowId = row.FlowId,
        Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false),
        FlowName = row.FlowName,
        Source = ReversalSource.Of(row.SourceKind, row.SourceId),
        Submissions = ParseSubmissions(row.SubmissionsJson),
        Status = row.Status,
        RequestedBy = row.RequestedBy,
        RequestedUtc = DateTime.SpecifyKind(row.RequestedUtc, DateTimeKind.Utc),
        CapturedUtc = Utc(row.CapturedUtc),
        StartedUtc = Utc(row.StartedUtc),
        CompletedUtc = Utc(row.CompletedUtc),
        LastRunId = row.LastRunId,
        Error = row.Error,
    };

    private static ReversalItemState ToState(DeliveryReversalItem row) => new()
    {
        DeliveryKey = new DeliveryKey(row.DeliveryKey),
        TargetId = row.TargetId,
        FirstAttemptId = row.FirstAttemptId,
        FirstVersion = row.FirstVersion,
        RunVersion = row.RunVersion,
        Prior = row.Prior,
        PriorVersion = row.PriorVersion,
        PriorAttemptId = row.PriorAttemptId,
        State = row.State,
        Outcome = row.Outcome,
        Detail = row.Detail,
        RestoredVersion = row.RestoredVersion,
        NewVersion = row.NewVersion,
        RunId = row.RunId,
        UpdatedUtc = DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc),
    };

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>The submissions a reversal keeps as JSON; a value that is not a list of ids is a defect in the row, named.</summary>
    private static IReadOnlyList<Guid> ParseSubmissions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A reversal's submissions are not a list of ids: {ex.Message}", ex);
        }
    }
}
