using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

// The records the ledger asked to be planned again (by a release, a redelivery, a removal or a cache rollout, and the
// records deleted from the ledger whose rows wait to be read again): the pages of them a deliver run reads by key, and
// how a pass settles the requests it read and could not answer by planning them.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// How many of the records the ledger asked to be planned again one pass of a deliver run reads by key
    /// (<see cref="DeliveryExecutor.RequestedPerPass"/>); a run makes as many passes as there are such records. Tests lower it.
    /// </summary>
    internal int RequestedPerPass { get; set; } = DeliveryExecutor.RequestedPerPass;

    /// <summary>
    /// The next records the ledger asked to be planned again, after <paramref name="after"/> in the order they were asked
    /// for, at most <paramref name="max"/>: what a run turns into a
    /// key-scoped selection so a release, a redelivery, a removal and a cache rollout reach the records they marked, and the
    /// records deleted from the ledger whose rows wait to be read again. The page names the last record it read, which the
    /// next page starts after, so a run walking every page meets each record once, even one a pass could not plan (its row
    /// gone from the table, or out of the run's scope): that one keeps its request for the run that can. A record with no
    /// source key cannot be read by key and is counted rather than read; the first few are named.
    /// </summary>
    public async Task<PlanRequestedPage> PlanRequestedKeysAsync(int max, PlanRequestedRecord? after, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var ledger = RequireLedger();
        var keys = new List<KeyTuple>();
        var seen = new HashSet<KeyTuple>();
        var requests = new List<PlanRequestedRecord>();
        var purgedRequests = new List<PlanRequestedRecord>();
        var keylessRequests = new List<PlanRequestedRecord>();
        var keyless = 0;
        var read = 0;
        var last = after;
        while (read < max)
        {
            var page = await ledger.ListPlanRequestedAsync(Flow.Id, last, Math.Min(max - read, 500), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var record in page)
            {
                if (record.SourceKeyJson is { } json)
                {
                    // A record deleted from the ledger and made a record again before a run read its line asks for the
                    // same row twice; the pass reads it once.
                    var key = KeyTuple.FromJson(json);
                    if (seen.Add(key))
                    {
                        keys.Add(key);
                    }

                    (record.Purged ? purgedRequests : requests).Add(record);
                }
                else
                {
                    keyless++;
                    keylessRequests.Add(record);
                    if (_keylessNamed++ < KeylessNamed)
                    {
                        _log.LogInformation(
                            "Record {Key} waits to be planned again but the ledger holds no source key for it (it predates the ingestion source); this run reads the whole scope to plan it.",
                            record.DeliveryKey);
                    }
                }
            }

            read += page.Count;
            last = page[^1];
        }

        return new PlanRequestedPage(keys, read == 0 ? null : last, keyless) { Requests = requests, PurgedRequests = purgedRequests, KeylessRequests = keylessRequests };
    }

    /// <summary>
    /// Settles the requests of records that carry no source key (<paramref name="asked"/>, each as its record carried it)
    /// after a read of the whole scope: one it met is planned, answered, and keyed by that plan; one still standing, with no
    /// key yet, belongs to a row the read did not meet, which is gone from the ingestion table. Its request is answered with
    /// that on the record's history, and a released record is blocked again, so no run reads the whole scope for it again.
    /// Returns how many such rows were reported gone.
    /// </summary>
    internal async Task<int> SettleKeylessAsync(IReadOnlyList<PlanRequestedRecord> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0)
        {
            return 0;
        }

        var ledger = RequireLedger();
        var records = await ledger.GetRecordsAsync(Flow.Id, asked.Select(a => a.DeliveryKey).Distinct().ToList(), ct).ConfigureAwait(false);
        var findings = asked
            .Where(a => records.TryGetValue(a.DeliveryKey, out var record) && record.PlanRequestedUtc == a.RequestedUtc && record.SourceKeyJson is null)
            .GroupBy(a => a.DeliveryKey)
            .Select(g => records[g.Key])
            .Select(record => new SourceSyncFinding
            {
                DeliveryKey = record.DeliveryKey,
                NotFound = "a read of the whole scope did not meet the record's row, and the record carries no source key to read it by: the row was deleted from the ingestion table, or its key changed",
                Gone = true,
                Origin = record.PendingSourceUpdatedUtc is not null || record.PendingSourceFileName is not null ? record.PendingOrigin : record.Origin,
            })
            .ToList();
        if (findings.Count == 0)
        {
            return 0;
        }

        var applied = await ledger.ApplySourceSyncAsync(Flow.Id, findings, RunId, ct).ConfigureAwait(false);
        return applied.NotFound;
    }

    /// <summary>
    /// Settles the requests a pass read by key and left as they were (<paramref name="asked"/>, each as its record carried
    /// it): the pass could not plan those records. Each is read again by a sync (<see cref="SourceSync"/>) in the scope it was
    /// planned under: a row gone from the table answers its request, and a released record is blocked again, so the request
    /// is not read by every run that follows; a row in the scope of another run keeps its request for that run. Returns what
    /// the sync found, or null when every request was answered.
    /// </summary>
    internal async Task<SourceSyncSummary?> SettleUnansweredAsync(IReadOnlyList<PlanRequestedRecord> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0)
        {
            return null;
        }

        var ledger = RequireLedger();
        var records = await ledger.GetRecordsAsync(Flow.Id, asked.Select(a => a.DeliveryKey).Distinct().ToList(), ct).ConfigureAwait(false);
        var unanswered = asked
            .Where(a => records.TryGetValue(a.DeliveryKey, out var record) && record.PlanRequestedUtc == a.RequestedUtc)
            .Select(a => a.DeliveryKey)
            .Distinct()
            .ToList();
        if (unanswered.Count == 0)
        {
            return null;
        }

        var sync = new SourceSync(_context, Flow, ledger, Parameters, RunId, _context.Loggers.CreateLogger<SourceSync>());
        return await sync.RunAsync(unanswered, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles the requests of records deleted from the ledger that a pass read by key (<paramref name="asked"/>, as the
    /// listing gave them). A row the pass planned made a record again, which answers its request, whatever became of the
    /// record then (delivered, held, failed: its own history says); so does a row the ingestion table no longer holds, which
    /// no run can deliver. A row outside this run's scope keeps its request for the run of its own scope, and so does a row
    /// the pass read and planned nothing of, which the log names. Returns what became of them, or null when the pass read none.
    /// </summary>
    internal async Task<PurgedRequestsSettled?> SettlePurgedRequestsAsync(IReadOnlyList<PlanRequestedRecord> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0)
        {
            return null;
        }

        var ledger = RequireLedger();
        var records = await ledger.GetRecordsAsync(Flow.Id, asked.Select(a => a.DeliveryKey).Distinct().ToList(), ct).ConfigureAwait(false);
        var planned = asked.Where(a => records.ContainsKey(a.DeliveryKey)).ToList();
        var unplanned = asked
            .Where(a => !records.ContainsKey(a.DeliveryKey) && a.SourceKeyJson is not null)
            .Select(a => (Request: a, Key: KeyTuple.FromJson(a.SourceKeyJson!)))
            .ToList();
        var gone = new List<PlanRequestedRecord>();
        var elsewhere = 0;
        var unexplained = 0;
        if (unplanned.Count > 0)
        {
            // Read once more by key in this run's scope, which says why the pass planned nothing of them: not in the table at
            // all, or in it outside this scope. Nothing is rendered or written by the read.
            var source = _context.Sources.Open(Flow, Parameters, _context.Loggers);
            var header = await source.OpenAsync(SourceSelection.ForKeys([.. unplanned.Select(u => u.Key).Distinct()]), null, ct).ConfigureAwait(false);
            var missing = header.MissingKeys.ToHashSet();
            var outside = header.OutOfScopeKeys.ToHashSet();
            foreach (var (request, key) in unplanned)
            {
                if (missing.Contains(key))
                {
                    gone.Add(request);
                    if (gone.Count <= PurgedNamed)
                    {
                        _log.LogInformation(
                            "Record {Key} was deleted from the ledger and its row is gone from {Object}, so it cannot be delivered again; its request is answered.",
                            request.DeliveryKey, Flow.Source.Record.Object);
                    }
                }
                else if (outside.Contains(key))
                {
                    elsewhere++;
                }
                else
                {
                    unexplained++;
                    if (unexplained <= PurgedNamed)
                    {
                        _log.LogWarning(
                            "Record {Key} was deleted from the ledger and its row is in {Object} in this run's scope, but the pass planned nothing of it; its request stays for the next run.",
                            request.DeliveryKey, Flow.Source.Record.Object);
                    }
                }
            }
        }

        await ledger.AnswerPurgedRequestsAsync(Flow.Id, [.. planned, .. gone], ct).ConfigureAwait(false);
        return new PurgedRequestsSettled(planned.Count, gone.Count, elsewhere, unexplained);
    }
}

/// <summary>
/// A page of the records the ledger asked to be planned again: the key tuples of those that can be read by key, the last
/// record the page read (null when there was none), which the next page starts after, and how many it read that carry no
/// source key.
/// </summary>
public sealed record PlanRequestedPage(IReadOnlyList<KeyTuple> Keys, PlanRequestedRecord? Last, int Keyless)
{
    /// <summary>The requests the page read by key, as each record carried it: what a pass that planned them answered.</summary>
    public IReadOnlyList<PlanRequestedRecord> Requests { get; init; } = [];

    /// <summary>
    /// The requests of records deleted from the ledger the page read by key, as their lines carried them: a pass plans each
    /// as a new record, and <see cref="FlowRuntime.SettlePurgedRequestsAsync"/> answers them.
    /// </summary>
    public IReadOnlyList<PlanRequestedRecord> PurgedRequests { get; init; } = [];

    /// <summary>The requests of records that carry no source key, which no pass reads: a read of the whole scope plans them.</summary>
    public IReadOnlyList<PlanRequestedRecord> KeylessRequests { get; init; } = [];
}

/// <summary>
/// What a pass did with the records deleted from the ledger whose rows it read (<see cref="FlowRuntime.SettlePurgedRequestsAsync"/>):
/// planned as records again, gone from the ingestion table (both answered), outside the run's scope, and read but not planned
/// (both left for a later run).
/// </summary>
public sealed record PurgedRequestsSettled(int Planned, int Gone, int Elsewhere, int Unplanned)
{
    public override string ToString()
    {
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{Planned} planned as records again") };
        if (Gone > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Gone} whose row is gone from the ingestion table, not read again"));
        }

        if (Elsewhere > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Elsewhere} outside this run's scope, left for the run of their own"));
        }

        if (Unplanned > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Unplanned} read but not planned, left for the next run"));
        }

        return string.Join(", ", parts);
    }
}
