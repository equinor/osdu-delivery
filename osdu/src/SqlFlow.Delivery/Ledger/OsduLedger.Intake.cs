using System.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// What the intake writes of the records a plan read (osdu/docs/reference/concepts/change-detection.md, What is decided
// for each record read): the records with work to send staged pending, and the ones it found unchanged skipped or held
// at their render, each of those decided of the record as it stands and written with it as one unit.
public sealed partial class OsduLedger
{
    public async Task<PendingStaging> UpsertPendingAsync(Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        RequireFlow(flowId, records.Select(r => (r.FlowId, r.DeliveryKey)));
        if (records.Count == 0)
        {
            return PendingStaging.Empty;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        await using var db = Open();
        var staged = await SqlServerLedgerBulk.UpsertPendingAsync(db, partition, flowId, records, WriteSlice, now, ct).ConfigureAwait(false);
        await WriteIdentitiesAsync(db, partition, flowId, Staged(records, staged), ct).ConfigureAwait(false);
        return staged;
    }

    /// <summary>
    /// The records a staging actually wrote: every record it was given except the ones it refused as older and the ones
    /// whose OSDU id another flow holds. Only those have a row to be findable by.
    /// </summary>
    private static IReadOnlyList<RecordState> Staged(IReadOnlyList<RecordState> records, PendingStaging result)
    {
        if (result.Refused.Count == 0 && result.Conflicts.Count == 0)
        {
            return records;
        }

        var skipped = result.Refused.Select(k => k.Value).Concat(result.Conflicts.Select(c => c.DeliveryKey.Value)).ToHashSet();
        return records.Where(r => !skipped.Contains(r.DeliveryKey.Value)).ToList();
    }

    /// <summary>Refuses records of another flow than the one a flow-scoped write was called for, naming the first.</summary>
    private static void RequireFlow(Guid flowId, IEnumerable<(Guid FlowId, DeliveryKey Key)> records)
    {
        foreach (var (recordFlow, key) in records)
        {
            if (recordFlow != flowId)
            {
                throw new ArgumentException(
                    $"Record {key} belongs to flow {recordFlow:D}, and this write is for flow {flowId:D}: a flow writes only its own records.",
                    nameof(records));
            }
        }
    }

    public async Task MarkSkippedAsync(Guid flowId, IReadOnlyList<SkippedRecord> records, Guid submissionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var waiting = StatusText.Of(RecordStatus.Waiting);
        var delivered = StatusText.Of(RecordStatus.Delivered);
        string[] parked = [StatusText.Of(RecordStatus.Held), StatusText.Of(RecordStatus.Failed), StatusText.Of(RecordStatus.Reverted)];
        await using (var db = Open())
        {
            // An unchanged record, or one a cache change waiting for approval holds back, carries nothing to write but the
            // submission pointer: one statement per chunk, however many a quiet run skips. A record with queued work stays with
            // the submission that queued it, or that submission's drain would never find it. The statement sets what it sets
            // whenever it runs, so one the database rolled back to end a deadlock is run again.
            foreach (var chunk in SqlServerLedgerBulk.InKeyOrderOf(records.Where(r => r.Kind is SkipKind.Unchanged or SkipKind.AwaitingApproval or SkipKind.SourceRemoved).Select(r => r.DeliveryKey.Value)).Chunk(ChunkSize))
            {
                await RetryDeadlockAsync(
                    () => db.DeliveryRecords
                        .Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey)
                            && !(r.PendingDocumentRef != null && (r.Status == pending || r.Status == delivering || r.Status == waiting)))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.LastSubmissionId, submissionId)
                            .SetProperty(r => r.UpdatedUtc, now), ct),
                    ct).ConfigureAwait(false);
            }

            // A plan that saw a record has answered the request to plan it again that it read, whatever it decided, except one
            // that waits for a cache change to be approved: the plan that runs once the change is decided answers that.
            await AnswerRequestsAsync(db, partition, flowId, records.Where(r => r.Kind != SkipKind.AwaitingApproval).Select(r => (r.DeliveryKey.Value, r.Basis)), ct)
                .ConfigureAwait(false);
        }

        foreach (var chunk in records.Where(r => r.Kind is SkipKind.Rendered or SkipKind.Stale).OrderBy(r => new SqlGuid(r.DeliveryKey.Value)).Chunk(ChunkSize))
        {
            // Each chunk is decided of its records as they stand, held from the read to the write (RecordUnitAsync).
            await RecordUnitAsync(partition, flowId, chunk.Select(r => r.DeliveryKey.Value), async (db, entities) =>
            {
                var recorded = await StaleRecordedAsync(db, partition, flowId, chunk, ct).ConfigureAwait(false);
                var unfinished = await UnfinishedAsync(db, partition, flowId, entities.Values.Where(e => parked.Contains(e.Status, StringComparer.Ordinal)).Select(e => e.DeliveryKey), ct)
                    .ConfigureAwait(false);
                foreach (var skip in chunk)
                {
                    entities.TryGetValue(skip.DeliveryKey.Value, out var entity);
                    if (Moved(entity, skip.Basis))
                    {
                        // Released, redelivered, restored, delivered or deleted from the ledger since the plan read it: what the
                        // plan decided is not the record's any more. The next run plans it again from its row.
                        if (entity is not null)
                        {
                            entity.PlanRequestedUtc ??= now;
                        }

                        continue;
                    }

                    if (entity is null)
                    {
                        throw new DeliveryException($"Record {skip.DeliveryKey} is not in the ledger, but a {skip.Kind.ToString().ToLowerInvariant()} skip is only ever decided against a record the ledger holds.");
                    }

                    entity.SourceKeyJson = Truncate(skip.SourceKeyJson, 2000) ?? entity.SourceKeyJson;

                    // A render that sent nothing still looked for the record's children, or no longer does under a mapping that
                    // stopped linking them: what it found decides which landings plan the record again.
                    if (skip.Kind == SkipKind.Rendered)
                    {
                        entity.ChildLinkKey = skip.ChildLinkKey;
                        entity.HeldSteps = skip.HeldSteps;
                    }

                    // A record the ledger parked (held, failed or reverted) that OSDU holds a version of, and that the plan
                    // found rendering exactly that version, or (released, so no longer blocked) carrying a row older than it:
                    // OSDU holds what the source has to give, so the record is delivered again, with nothing sent. Left
                    // parked, it would stay held with nothing to release, since the plan has answered the request to plan it.
                    // Not while what an unfinished delivery left in OSDU is still to be undone: until then OSDU holds more than
                    // the ledger says, so a released record is blocked again, saying why.
                    var was = entity.Status;
                    var candidate = entity.TargetVersion is not null && parked.Contains(was, StringComparer.Ordinal)
                        && (skip.Kind == SkipKind.Rendered || (skip.Kind == SkipKind.Stale && !entity.Blocked));
                    var settles = candidate && !unfinished.Contains(entity.DeliveryKey);
                    if (candidate && !settles)
                    {
                        // Blocked at the row just read, so no plan renders it again until the row moves or the undo is done and
                        // someone releases it; the attempt says why it was not delivered again.
                        entity.Blocked = true;
                        entity.LastError = $"the {was} record renders what OSDU holds, but what an earlier delivery of it left in OSDU is not undone yet; "
                            + "release it once the undo is done (the record's artifacts say what is left)";
                        entity.ProblemHash = ProblemSignature.Of(entity.LastError);
                        entity.PendingSourceFingerprint = skip.SourceFingerprint ?? entity.PendingSourceFingerprint;
                        entity.PendingSourceModifiedUtc = skip.SourceModifiedUtc ?? entity.PendingSourceModifiedUtc;
                        entity.PendingSourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength) ?? entity.PendingSourceFileName;
                        entity.PendingSourceRowNumber = skip.Origin.RowNumber ?? entity.PendingSourceRowNumber;
                        entity.PendingSourceUpdatedUtc = skip.Origin.UpdatedUtc ?? entity.PendingSourceUpdatedUtc;
                        entity.LastSubmissionId = submissionId;
                        entity.UpdatedUtc = now;
                        db.DeliveryAttempts.Add(new DeliveryAttempt
                        {
                            PartitionId = partition,
                            FlowId = flowId,
                            DeliveryKey = entity.DeliveryKey,
                            SubmissionId = submissionId,
                            RunId = skip.RunId,
                            Worker = "intake",
                            StartedUtc = now,
                            CompletedUtc = now,
                            Outcome = StatusText.Of(AttemptOutcome.Held),
                            Phase = AttemptPhases.UndoWait,
                            Error = entity.LastError,
                            SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                            SourceRowNumber = skip.Origin.RowNumber,
                            SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                        });
                        continue;
                    }

                    if (skip.Kind == SkipKind.Stale)
                    {
                        // The record is left as it is, unless it settles: the attempt is the whole of what happened, and says
                        // which version and origin the source carried and which version stands. A row the ledger already
                        // recorded as stale is the same skip read again (the overlap of the next run's window, or a run's pass
                        // over the records asked for followed by its own read), so it is recorded once.
                        if (!settles && skip.Origin.UpdatedUtc is { } read && !recorded.Add((entity.DeliveryKey, read)))
                        {
                            continue;
                        }

                        db.DeliveryAttempts.Add(new DeliveryAttempt
                        {
                            PartitionId = partition,
                            FlowId = flowId,
                            DeliveryKey = entity.DeliveryKey,
                            SubmissionId = submissionId,
                            RunId = skip.RunId,
                            Worker = "intake",
                            StartedUtc = now,
                            CompletedUtc = now,
                            Outcome = StatusText.Of(AttemptOutcome.Skipped),
                            Phase = AttemptPhases.Stale,
                            ResultJson = settles
                                ? AttemptResult.Settled(Truncate(Http.HeaderRedaction.RedactMessage(Settled(skip.Reason, was, stale: true)), 2000)!)
                                : AttemptResult.WithDetail(null, Truncate(Http.HeaderRedaction.RedactMessage(skip.Reason), 2000)),
                            SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                            SourceRowNumber = skip.Origin.RowNumber,
                            SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                        });
                        if (settles)
                        {
                            entity.LastSubmissionId = submissionId;
                            SettleDelivered(entity, now);
                        }

                        continue;
                    }

                    entity.SourceInsertedUtc = skip.SourceInsertedUtc ?? entity.SourceInsertedUtc;

                    // The ingestion table changed the row since the version the ledger stands at, delivered or queued, and it
                    // renders the document OSDU already has: a change of the record although nothing is sent. The attempt
                    // keeps it with the row's new origin, and the origin moves on with the fingerprint below, so a later plan
                    // that renders the same row again (a cache or mapping rollout) finds the change recorded and writes nothing.
                    // A parked record's pending version is the one it was parked at, not one that lands, so only the version
                    // OSDU holds counts for it; and settling it is a change of the record whether its row moved or not.
                    var changed = skip.Origin.UpdatedUtc is { } stamp
                        && !(entity.SourceUpdatedUtc >= stamp)
                        && (settles || !(entity.PendingSourceUpdatedUtc >= stamp));

                    // A render something moved for (a request, the mapping, the cache, a child or lookup row) is on the record's
                    // history too, saying what moved, so whether a change reached the record is read from the record itself.
                    if (changed || settles || skip.Why is not null)
                    {
                        db.DeliveryAttempts.Add(new DeliveryAttempt
                        {
                            PartitionId = partition,
                            FlowId = flowId,
                            DeliveryKey = entity.DeliveryKey,
                            SubmissionId = submissionId,
                            RunId = skip.RunId,
                            Worker = "intake",
                            StartedUtc = now,
                            CompletedUtc = now,
                            Outcome = StatusText.Of(AttemptOutcome.Skipped),
                            Phase = AttemptPhases.Identical,
                            ResultJson = AttemptResult.WithReason(
                                settles
                                    ? AttemptResult.Settled(Truncate(Http.HeaderRedaction.RedactMessage(Settled(skip.Reason, was, stale: false)), 2000)!)
                                    : AttemptResult.WithDetail(null, Truncate(Http.HeaderRedaction.RedactMessage(skip.Reason), 2000)),
                                skip.Why),
                            Triggers = skip.Why?.Kinds is { } moved ? (int)moved : null,
                            SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                            SourceRowNumber = skip.Origin.RowNumber,
                            SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                        });
                    }

                    if (entity.PendingDocumentRef is not null && (entity.Status == pending || entity.Status == delivering || entity.Status == waiting))
                    {
                        // Identical to the queued work: the version the drop carried belongs to that work and lands with it.
                        entity.PendingSourceFingerprint = skip.SourceFingerprint ?? entity.PendingSourceFingerprint;
                        entity.PendingSourceModifiedUtc = Latest(entity.PendingSourceModifiedUtc, skip.SourceModifiedUtc);
                        if (entity.PendingPayload)
                        {
                            entity.PendingPayloadModifiedUtc = Latest(entity.PendingPayloadModifiedUtc, skip.PayloadModifiedUtc);
                        }

                        if (changed)
                        {
                            entity.PendingSourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength);
                            entity.PendingSourceRowNumber = skip.Origin.RowNumber;
                            entity.PendingSourceUpdatedUtc = skip.Origin.UpdatedUtc;
                        }

                        entity.UpdatedUtc = now;
                        continue;
                    }

                    entity.LastSubmissionId = submissionId;
                    entity.UpdatedUtc = now;
                    if (settles)
                    {
                        SettleDelivered(entity, now);
                    }

                    if (entity.Status == delivered)
                    {
                        // OSDU holds a document identical to this render, so it is as true of the version and context just
                        // rendered as of the ones it was built from: the next plan decides the record without rendering.
                        entity.SourceFingerprint = skip.SourceFingerprint ?? entity.SourceFingerprint;
                        entity.SourceModifiedUtc = Latest(entity.SourceModifiedUtc, skip.SourceModifiedUtc);
                        entity.PayloadModifiedUtc = Latest(entity.PayloadModifiedUtc, skip.PayloadModifiedUtc);
                        if (skip.RenderContext is not null)
                        {
                            entity.RenderContext = skip.RenderContext;
                            entity.RenderCommit = Truncate(skip.RenderCommit, DeliveryModel.MaxCommitLength);
                        }

                        // A record delivered before the ledger kept its document's paths keeps them from the render found
                        // identical to what OSDU holds, so its next delivery can name what it changes.
                        entity.RenderPaths ??= skip.RenderPaths is { } kept ? Convert.FromBase64String(kept) : null;
                        if (changed)
                        {
                            entity.SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength);
                            entity.SourceRowNumber = skip.Origin.RowNumber;
                            entity.SourceUpdatedUtc = skip.Origin.UpdatedUtc;
                        }
                    }
                }

                return chunk.Length;
            }, ct).ConfigureAwait(false);
        }
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Whether a record changed since the plan read it (<see cref="PlanBasis"/>): another write moved its update time, or it
    /// was added to or deleted from the ledger meanwhile. A write that is not a plan's has no basis and never moved.
    /// </summary>
    private static bool Moved(DeliveryRecord? entity, PlanBasis? basis)
        => basis is { } read && (entity is null
            ? read.UpdatedUtc is not null
            : read.UpdatedUtc is not { } at || entity.UpdatedUtc != at);

    /// <summary>
    /// Takes the requests to plan records again that their plans answered: for a plan's decision, only the request it read,
    /// so one made while the run planned (an operator's release or redelivery, a cache rollout) stays for the next run; for
    /// a write that is not a plan's, whatever request the record carries. One statement per distinct request moment and
    /// chunk: a release stamps every record it reaches with one moment, so a pass over them is a handful of statements.
    /// Each takes only the request it names, whenever it runs, so one the database rolled back to end a deadlock is run again.
    /// </summary>
    private static async Task AnswerRequestsAsync(
        OsduDbContext db, short partition, Guid flowId, IEnumerable<(Guid DeliveryKey, PlanBasis? Basis)> answered, CancellationToken ct)
    {
        var list = answered.ToList();
        foreach (var chunk in SqlServerLedgerBulk.InKeyOrderOf(list.Where(a => a.Basis is null).Select(a => a.DeliveryKey)).Chunk(ChunkSize))
        {
            await RetryDeadlockAsync(
                () => db.DeliveryRecords
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && r.PlanRequestedUtc != null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, (DateTime?)null), ct),
                ct).ConfigureAwait(false);
        }

        foreach (var request in list.Where(a => a.Basis?.PlanRequestedUtc is not null).GroupBy(a => a.Basis!.Value.PlanRequestedUtc!.Value))
        {
            var seen = request.Key;
            foreach (var chunk in SqlServerLedgerBulk.InKeyOrderOf(request.Select(a => a.DeliveryKey)).Chunk(ChunkSize))
            {
                await RetryDeadlockAsync(
                    () => db.DeliveryRecords
                        .Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && r.PlanRequestedUtc == seen)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, (DateTime?)null), ct),
                    ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Which of <paramref name="keys"/> have something an unfinished delivery left in OSDU still to undo: an artifact in an
    /// open state, read through the index that holds open artifacts.
    /// </summary>
    private static async Task<HashSet<Guid>> UnfinishedAsync(OsduDbContext db, short partition, Guid flowId, IEnumerable<Guid> keys, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        foreach (var chunk in keys.Distinct().Chunk(LookupChunk))
        {
            var rows = await db.DeliveryArtifacts.AsNoTracking()
                .Where(a => a.PartitionId == partition && a.FlowId == flowId && chunk.Contains(a.DeliveryKey) && OpenStates.Contains(a.State))
                .Select(a => a.DeliveryKey)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            found.UnionWith(rows);
        }

        return found;
    }

    /// <summary>
    /// The rows of <paramref name="chunk"/>'s stale skips the ledger already recorded as stale, by record and the moment the
    /// ingestion table stamped the row: a seek of each record's attempts, made only for a chunk with a stale skip.
    /// </summary>
    private static async Task<HashSet<(Guid DeliveryKey, DateTime Stamped)>> StaleRecordedAsync(
        OsduDbContext db, short partition, Guid flowId, IEnumerable<SkippedRecord> chunk, CancellationToken ct)
    {
        var stale = chunk.Where(s => s.Kind == SkipKind.Stale && s.Origin.UpdatedUtc is not null).Select(s => s.DeliveryKey.Value).Distinct().ToArray();
        if (stale.Length == 0)
        {
            return [];
        }

        var skipped = StatusText.Of(AttemptOutcome.Skipped);
        var rows = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.PartitionId == partition && a.FlowId == flowId && stale.Contains(a.DeliveryKey)
                && a.Outcome == skipped && a.Phase == AttemptPhases.Stale && a.SourceUpdatedUtc != null)
            .Select(a => new { a.DeliveryKey, a.SourceUpdatedUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(a => (a.DeliveryKey, a.SourceUpdatedUtc!.Value)).ToHashSet();
    }

    /// <summary>
    /// A parked record a plan found OSDU already holding the source's version of, delivered again: unblocked, its problem
    /// and error gone, its tries counted afresh, and whatever it was parked with (a kept document, the steps of a try, the
    /// version it was left at) dropped, as a delivery that sent nothing leaves a record. What OSDU holds is unchanged.
    /// </summary>
    private static void SettleDelivered(DeliveryRecord entity, DateTime now)
    {
        entity.Status = StatusText.Of(RecordStatus.Delivered);
        entity.Blocked = false;
        entity.ProblemHash = null;
        entity.LastError = null;
        entity.PlanRequestedUtc = null;
        entity.AttemptCount = 0;
        entity.NextAttemptUtc = null;
        entity.LeaseOwner = null;
        entity.PendingDocumentRef = null;
        entity.WorkBatch = null;
        entity.PendingStepJson = null;
        entity.PendingReferences = null;
        entity.WaitingFor = null;
        entity.PendingMetadata = false;
        entity.PendingPayload = false;
        entity.PendingPayloadLocation = null;
        entity.PendingPayloadModifiedUtc = null;
        entity.PendingSourceFingerprint = null;
        entity.PendingSourceModifiedUtc = null;
        entity.PendingSourceFileName = null;
        entity.PendingSourceRowNumber = null;
        entity.PendingSourceUpdatedUtc = null;
        entity.UpdatedUtc = now;
    }

    /// <summary>What an attempt that settles a parked record says: the plan's reason, and what became of the record.</summary>
    private static string Settled(string reason, string was, bool stale)
        => stale
            ? $"{reason}; nothing was sent and the {was} record is delivered again, at the version OSDU holds"
            : $"{reason}; OSDU already holds the document this row renders, so nothing was sent and the {was} record is delivered again";

    public async Task<IReadOnlyList<DeliveryKey>> MarkHeldAsync(Guid flowId, IEnumerable<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records as IReadOnlyList<RecordState> ?? records.ToList();
        RequireFlow(flowId, list.Select(r => (r.FlowId, r.DeliveryKey)));
        if (list.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        var held = new List<DeliveryKey>(list.Count);
        foreach (var chunk in list.OrderBy(r => new SqlGuid(r.DeliveryKey.Value)).Chunk(ChunkSize))
        {
            // Each chunk is decided of its records as they stand, held from the read to the write (RecordUnitAsync).
            held.AddRange(await RecordUnitAsync(partition, flowId, chunk.Select(r => r.DeliveryKey.Value), async (db, existing) =>
            {
                var marked = new List<DeliveryKey>(chunk.Length);
                // A held record claims nothing, and it is not given an id another record holds, of another flow or of this one:
                // nothing this flow does to it (a removal, a read back) may reach that record's OSDU record. A record that claimed
                // an id keeps it; one that claimed none names the id its latest render gives, since no earlier one was written.
                var claims = await ClaimedAsync(
                    db, chunk.Where(r => existing.GetValueOrDefault(r.DeliveryKey.Value)?.ClaimedTargetId is null).Select(r => r.TargetId), ct).ConfigureAwait(false);
                foreach (var record in chunk)
                {
                    existing.TryGetValue(record.DeliveryKey.Value, out var entity);
                    if (Moved(entity, record.Basis))
                    {
                        // Released, redelivered, restored or delivered since the plan read it: the hold is not the record's any
                        // more. The next run plans it again from its row, and holds it then if the row still calls for it.
                        if (entity is not null)
                        {
                            entity.PlanRequestedUtc ??= now;
                        }

                        continue;
                    }

                    if (entity is null)
                    {
                        entity = new DeliveryRecord
                        {
                            PartitionId = partition,
                            DeliveryKey = record.DeliveryKey.Value,
                            FlowId = flowId,
                            SourceKey = Truncate(record.SourceKey, 400)!,
                            MappingName = record.MappingName,
                            CreatedUtc = now,
                        };
                        db.DeliveryRecords.Add(entity);
                        existing[entity.DeliveryKey] = entity;
                    }

                    entity.Label = Truncate(record.Label, 400) ?? entity.Label;
                    if (entity.ClaimedTargetId is null && record.TargetId is { } targetId)
                    {
                        entity.TargetId = claims.Contains(targetId) ? null : targetId;
                    }

                    // The id OSDU already holds a record at, for a record held because of it, is what an adoption claims; any
                    // other hold, and a record that claimed an id, has none. The intake asks OSDU only of ids no record claimed.
                    entity.OccupiedTargetId = entity.ClaimedTargetId is null ? record.OccupiedTargetId : null;
                    entity.OccupiedTargetVersion = entity.OccupiedTargetId is null ? null : record.OccupiedTargetVersion;
                    // A request to plan the record again made after the plan read it, which did not move the record (a sync that
                    // found its row changed), is kept: the record is held unblocked, so the next run plans it under that request.
                    var asked = record.Basis is { } basis && entity.PlanRequestedUtc is { } requested && requested != basis.PlanRequestedUtc
                        ? requested
                        : (DateTime?)null;
                    entity.Status = StatusText.Of(RecordStatus.Held);
                    entity.LastError = Truncate(record.LastError, 2000);
                    entity.ProblemHash = asked is null ? ProblemSignature.Of(entity.LastError) : null;
                    entity.LastSubmissionId = record.LastSubmissionId;
                    entity.LeaseOwner = null;
                    entity.PendingDocumentRef = null;
                    entity.WorkBatch = null;
                    entity.PendingStepJson = null;
                    entity.PendingReferences = null;
                    entity.WaitingFor = null;
                    entity.SourceKeyJson = Truncate(record.SourceKeyJson, 2000) ?? entity.SourceKeyJson;
                    entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                    entity.PendingSourceModifiedUtc = record.PendingSourceModifiedUtc;
                    entity.PendingSourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength);
                    entity.PendingSourceRowNumber = record.PendingSourceRowNumber;
                    entity.PendingSourceUpdatedUtc = record.PendingSourceUpdatedUtc;
                    entity.SourceInsertedUtc = record.SourceInsertedUtc ?? entity.SourceInsertedUtc;
                    entity.PlanRequestedUtc = asked;
                    entity.PendingMetadata = false;
                    entity.PendingPayload = false;
                    entity.Blocked = asked is null;
                    entity.UpdatedUtc = now;

                    // What the held render read, the ids it found missing among them, so a cache version that brings one in
                    // releases the record. What the document OSDU holds was built from stays the record's cache set.
                    entity.HeldCacheSetId = record.CacheSetId;

                    // The rules the hold was rendered under, so a plan under other rules (a mapping, template or parameter that
                    // changed) renders the record again rather than leaving the error these rules gave.
                    entity.HeldRulesHash = record.HeldRulesHash;

                    // The parent the held render read, or found missing, so the parent landing plans the record again; and the
                    // children it looked for, so a child landing finds it once its hold is lifted.
                    entity.ParentId = record.ParentId;
                    entity.PendingParentVersion = record.PendingParentVersion;
                    entity.ChildLinkKey = record.ChildLinkKey;
                    entity.HeldSteps = record.HeldSteps;
                    marked.Add(record.DeliveryKey);
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = record.DeliveryKey.Value,
                        SubmissionId = record.LastSubmissionId,
                        RunId = record.RunId,
                        Worker = "intake",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Held),
                        Phase = record.PendingSourceDeletedUtc is null ? "render" : AttemptPhases.SourceDeleted,
                        Error = Truncate(record.LastError, 2000),
                        SourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = record.PendingSourceRowNumber,
                        SourceUpdatedUtc = record.PendingSourceUpdatedUtc,
                        ParentVersion = record.PendingParentVersion,
                        SourceDeletedUtc = record.PendingSourceDeletedUtc,
                    });
                }

                return marked;
            }, ct).ConfigureAwait(false));
        }

        return held;
    }

    /// <summary>How many times a unit of records is decided when another writer reached one of them first.</summary>
    private const int UnitAttempts = 5;

    /// <summary>
    /// Decides and writes <paramref name="keys"/>, records of one ledger, as one unit: in one transaction it takes the
    /// records that exist in the order of the table's key, each held against every other writer
    /// (<see cref="SqlServerLedgerBulk.LockRecordsAsync"/>), hands them to <paramref name="write"/> tracked, and saves
    /// what it changed and added before it lets go of them. So what <paramref name="write"/> decides of a record (whether it
    /// moved since a plan read it, what it is left as) is decided of the row as it stands and written before another writer
    /// reaches it, and two writers that share records meet at the first one, where one waits holding nothing the other
    /// wants. A write that takes its records one statement at a time in an order of its own does neither: a landing that
    /// releases or plans again two of the same records then holds one while it waits for the other.
    /// </summary>
    /// <remarks>
    /// A unit that still lost a race is rolled back whole and decided again from the rows the winner left: the table's key
    /// refused a record another writer inserted after the unit looked for it, which the unit now finds, or the database
    /// ended a deadlock with a statement that reached the unit's records in another order. <paramref name="write"/>
    /// therefore changes nothing but the context and the records it is given, and returns what it decided.
    /// </remarks>
    private async Task<T> RecordUnitAsync<T>(
        short partition, Guid flowId, IEnumerable<Guid> keys, Func<OsduDbContext, Dictionary<Guid, DeliveryRecord>, Task<T>> write, CancellationToken ct)
    {
        var unit = keys.Distinct().ToList();
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = Open();
                var strategy = db.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    // A strategy that runs the unit again starts it from nothing, as a unit that lost a race does.
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                    var records = await SqlServerLedgerBulk.LockRecordsAsync(db, partition, flowId, unit, ct).ConfigureAwait(false);
                    var result = await write(db, records).ConfigureAwait(false);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return result;
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < UnitAttempts && SqlServerLedgerBulk.IsContention(ex))
            {
                // Waiting a moment, longer each time and never the same for two writers, keeps the two from meeting in
                // the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
