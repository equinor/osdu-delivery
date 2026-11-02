using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What a sync of the ledger with the ingestion tables found and did, record by record. <see cref="Checked"/> records
/// had a key to read their row by; each of them is in agreement, or counted under what differed. The ledger wrote
/// <see cref="ArrivalsRecorded"/> arrivals, asked for <see cref="PlansRequested"/> records to be planned by the flow's
/// next run, and put <see cref="NotFoundRecorded"/> rows not found on their records' history.
/// </summary>
public sealed record SourceSyncSummary(
    int Checked,
    int InAgreement,
    int ArrivalsRecorded,
    int ChangedUnseen,
    int DeletedUnseen,
    int PlansRequested,
    int Restored,
    int NotFound,
    int NotFoundRecorded,
    int FoundAgain,
    int WithoutKey,
    int NotInLedger,
    IReadOnlyList<string> NotFoundSample)
{
    public override string ToString()
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{Checked} checked, {InAgreement} in agreement"),
        };
        void Add(int count, string what)
        {
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {what}"));
            }
        }

        Add(ArrivalsRecorded, "arrival(s) recorded");
        Add(ChangedUnseen, "changed since the ledger's version");
        Add(DeletedUnseen, "marked deleted unseen");
        Add(PlansRequested, "asked to be planned by the next run");
        Add(Restored, "held as deleted, but no longer deleted in the source (release to deliver)");
        Add(NotFound, "not found in the ingestion table");
        Add(NotFoundRecorded, "not-found row(s) put on their record's history");
        Add(FoundAgain, "found again after being reported not found");
        Add(WithoutKey, "without a stored key to read the row by");
        Add(NotInLedger, "named but not in the ledger");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Consolidates the ledger with the ingestion tables, one page of records at a time. Each record's row is read by the key
/// tuple the ledger stored, in the scope the record was planned under (its last submission's parameters, as the Source
/// tab reads it), and compared with what the ledger holds:
/// <list type="bullet">
/// <item>the row's arrival (<c>InsertedDate_DW</c>) is written where the ledger lacks it or holds another moment;</item>
/// <item>a row whose fingerprint moved past both versions the ledger holds, or that the table marked deleted without the
/// ledger knowing, is asked to be planned: the flow's next run reads it by its key and decides it as it decides every
/// change;</item>
/// <item>a row the table no longer holds, or holds out of the record's scope, is put on the record's history once, and the
/// record keeps its status;</item>
/// <item>a record held because its row was deleted, whose row is no longer deleted, is counted for an operator to release.</item>
/// </list>
/// Nothing is rendered and nothing reaches OSDU. Rows the ledger holds no record of are not a sync's to find: a row's
/// delivery key comes from its mapping, so a plan finds them.
/// </summary>
public sealed class SourceSync
{
    /// <summary>Records read, compared and written per page: one key-scoped read of the source per scope on the page.</summary>
    public const int PageSize = 1000;

    /// <summary>
    /// The most records a sync names by key, which ride in its run's payload (<see cref="DeliveryRunPayload.MaxRecordKeys"/>):
    /// a selection, or every record a filter matches. More than that is a sync of the whole interface, which pages the
    /// ledger instead and names no record.
    /// </summary>
    public const int MaxNamedRecords = DeliveryRunPayload.MaxRecordKeys;

    /// <summary>The source keys of rows not found that a summary names; the history of each record names every one.</summary>
    public const int MaxNotFoundSample = 20;

    /// <summary>How many records pass between two progress lines, so a sync of millions logs a bounded trace.</summary>
    private const int ProgressEvery = 50_000;

    private readonly EngineContext _context;
    private readonly FlowDefinition _flow;
    private readonly ILedger _ledger;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly Guid? _runId;
    private readonly ILogger _log;
    private readonly Dictionary<string, IIngestionSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, string>> _scopes = [];

    public SourceSync(EngineContext context, FlowDefinition flow, ILedger ledger, IReadOnlyDictionary<string, string> values, Guid? runId, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _flow = flow;
        _ledger = ledger;
        _values = values;
        _runId = runId;
        _log = log;
    }

    /// <summary>Syncs the records <paramref name="keys"/> names, or every record of the flow when it is null.</summary>
    public async Task<SourceSyncSummary> RunAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct)
    {
        var tally = new Tally();
        if (keys is not null)
        {
            foreach (var chunk in keys.Distinct().Chunk(PageSize))
            {
                var records = await _ledger.GetRecordsAsync(_flow.Id, chunk, ct).ConfigureAwait(false);
                tally.NotInLedger += chunk.Count(k => !records.ContainsKey(k));
                await SyncPageAsync([.. chunk.Where(records.ContainsKey).Select(k => records[k])], tally, ct).ConfigureAwait(false);
            }

            return tally.Summary();
        }

        DeliveryKey? after = null;
        var logged = 0;
        while (true)
        {
            var page = await _ledger.ListRecordsAsync(_flow.Id, after, PageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            await SyncPageAsync(page, tally, ct).ConfigureAwait(false);
            after = page[^1].DeliveryKey;
            if (tally.Checked + tally.WithoutKey - logged >= ProgressEvery)
            {
                logged = tally.Checked + tally.WithoutKey;
                _log.LogInformation("Sync of {Flow}: {Records} records read so far; {Summary}.", _flow.Label, logged, tally.Summary());
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        return tally.Summary();
    }

    private async Task SyncPageAsync(IReadOnlyList<RecordState> records, Tally tally, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        var readable = new List<(RecordState Record, KeyTuple Key, IReadOnlyDictionary<string, string> Values)>(records.Count);
        foreach (var record in records)
        {
            if (record.SourceKeyJson is not { } json)
            {
                tally.WithoutKey++;
                continue;
            }

            readable.Add((record, KeyTuple.FromJson(json), await ScopeAsync(record.LastSubmissionId, ct).ConfigureAwait(false)));
        }

        if (readable.Count == 0)
        {
            return;
        }

        var latest = await _ledger.LatestAttemptsAsync(_flow.Id, [.. readable.Select(r => r.Record.DeliveryKey)], ct).ConfigureAwait(false);
        var findings = new List<SourceSyncFinding>();
        foreach (var scope in readable.GroupBy(r => Signature(r.Values), StringComparer.Ordinal))
        {
            var values = scope.First().Values;
            var source = SourceFor(scope.Key, values);
            var header = await source.OpenAsync(SourceSelection.ForKeys([.. scope.Select(r => r.Key).Distinct()]), null, ct).ConfigureAwait(false);
            var rows = new Dictionary<KeyTuple, SourceRecord>();
            await foreach (var row in source.ReadAsync(header, null, ct).ConfigureAwait(false))
            {
                if (row.SourceKeyJson is { } found)
                {
                    rows[KeyTuple.FromJson(found)] = row;
                }
            }

            var outOfScope = header.OutOfScopeKeys.ToHashSet();
            foreach (var (record, key, _) in scope)
            {
                tally.Checked++;
                if (Decide(record, rows.GetValueOrDefault(key), outOfScope.Contains(key), latest.GetValueOrDefault(record.DeliveryKey), tally) is { } finding)
                {
                    findings.Add(finding);
                }
            }
        }

        if (findings.Count > 0)
        {
            tally.Add(await _ledger.ApplySourceSyncAsync(_flow.Id, findings, _runId, ct).ConfigureAwait(false));
        }
    }

    /// <summary>A held or failed record nothing blocks, with no document to send: released, and waiting for a plan of its row.</summary>
    private static bool Unblocked(RecordState record)
        => !record.Blocked && record.Status is RecordStatus.Held or RecordStatus.Failed && record.PendingDocumentRef is null;

    /// <summary>What the ledger has to take in of one record's row, or null when the two agree.</summary>
    private static SourceSyncFinding? Decide(RecordState record, SourceRecord? row, bool outOfScope, AttemptRecord? latest, Tally tally)
    {
        var reportedNotFound = latest is { Outcome: AttemptOutcome.Skipped, Phase: AttemptPhases.SourceMissing };

        // A record out of OSDU whose row is gone, or left out by the flow's filter (a removal flow marks the rows it takes
        // out), agrees with its source when nothing asks for it to be planned: there is nothing to report.
        if (row is null && record.Status == RecordStatus.Deleted && record.PlanRequestedUtc is null)
        {
            tally.InAgreement++;
            return null;
        }

        if (row is null)
        {
            tally.NotFound++;
            tally.Sample(record.SourceKey);
            if (reportedNotFound)
            {
                // Its history already says so. A row that is gone still answers a request to plan the record made since,
                // and blocks a held or failed record a release left unblocked, without saying so twice.
                var unanswerable = !outOfScope && (record.PlanRequestedUtc is not null || Unblocked(record));
                return unanswerable ? new SourceSyncFinding { DeliveryKey = record.DeliveryKey, Gone = true } : null;
            }

            return new SourceSyncFinding
            {
                DeliveryKey = record.DeliveryKey,
                NotFound = outOfScope
                    ? "a sync found the row in the ingestion table, but outside the scope the record was planned under"
                    : "a sync did not find the row in the ingestion table: it was deleted from the table, or its key changed",
                Gone = !outOfScope,
                Origin = record.PendingSourceUpdatedUtc is not null || record.PendingSourceFileName is not null ? record.PendingOrigin : record.Origin,
            };
        }

        if (reportedNotFound)
        {
            tally.FoundAgain++;
        }

        var inserted = row.Origin.InsertedUtc is { } arrival && record.SourceInsertedUtc != arrival ? arrival : (DateTime?)null;
        var heldAsDeleted = latest is { Outcome: AttemptOutcome.Held, Phase: AttemptPhases.SourceDeleted };
        var changed = !Current(record, row, latest);
        var deletedUnseen = !changed && row.DeletedUtc is not null && !heldAsDeleted && record.Status != RecordStatus.Deleted;
        if (changed)
        {
            tally.ChangedUnseen++;
        }
        else if (deletedUnseen)
        {
            tally.DeletedUnseen++;
        }

        if (row.DeletedUtc is null && heldAsDeleted && record.Blocked)
        {
            tally.Restored++;
        }
        else if (inserted is null && !changed && !deletedUnseen && !reportedNotFound)
        {
            tally.InAgreement++;
        }

        return inserted is null && !changed && !deletedUnseen
            ? null
            : new SourceSyncFinding { DeliveryKey = record.DeliveryKey, InsertedUtc = inserted, RequestPlan = changed || deletedUnseen };
    }

    /// <summary>
    /// Whether the ledger stands at the row's version: its fingerprint is the one the delivered or the queued version was
    /// built from, or the ledger already recorded this very row as older than the version it holds. A ledger that holds
    /// no fingerprint for the record is compared by the moment the table stamped the row.
    /// </summary>
    private static bool Current(RecordState record, SourceRecord row, AttemptRecord? latest)
    {
        if (latest is { Outcome: AttemptOutcome.Skipped, Phase: AttemptPhases.Stale } && latest.SourceUpdatedUtc == row.Origin.UpdatedUtc)
        {
            return true;
        }

        if (row.Version.Fingerprint is { } fingerprint && (record.SourceFingerprint is not null || record.PendingSourceFingerprint is not null))
        {
            return string.Equals(fingerprint, record.SourceFingerprint, StringComparison.Ordinal)
                || string.Equals(fingerprint, record.PendingSourceFingerprint, StringComparison.Ordinal);
        }

        var known = record.SourceUpdatedUtc is { } delivered && (record.PendingSourceUpdatedUtc is not { } pending || delivered >= pending)
            ? record.SourceUpdatedUtc
            : record.PendingSourceUpdatedUtc;
        return row.Origin.UpdatedUtc is not { } stamped || (known is { } held && stamped <= held);
    }

    /// <summary>
    /// The parameter values a record's row is read in: those of the submission that last planned it, as the Source tab
    /// reads it, or the run's own for a record no submission names or one whose submission recorded none.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ScopeAsync(Guid? submissionId, CancellationToken ct)
    {
        if (submissionId is not { } id)
        {
            return _values;
        }

        if (!_scopes.TryGetValue(id, out var values))
        {
            var submission = await _ledger.GetSubmissionAsync(id, ct).ConfigureAwait(false);
            var recorded = DeliveryExecutor.ParseValues(submission?.ParametersJson);
            values = recorded.Count == 0 ? _values : recorded;
            _scopes[id] = values;
        }

        return values;
    }

    private IIngestionSource SourceFor(string signature, IReadOnlyDictionary<string, string> values)
    {
        if (!_sources.TryGetValue(signature, out var source))
        {
            source = _context.Sources.Open(_flow, values, _context.Loggers);
            _sources[signature] = source;
        }

        return source;
    }

    private static string Signature(IReadOnlyDictionary<string, string> values)
        => string.Join('\u001f', values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + '\u001e' + v.Value));

    /// <summary>The running counts of a sync.</summary>
    private sealed class Tally
    {
        private readonly List<string> _sample = [];

        public int Checked { get; set; }

        public int InAgreement { get; set; }

        public int Arrivals { get; set; }

        public int ChangedUnseen { get; set; }

        public int DeletedUnseen { get; set; }

        public int PlansRequested { get; set; }

        public int Restored { get; set; }

        public int NotFound { get; set; }

        public int NotFoundRecorded { get; set; }

        public int FoundAgain { get; set; }

        public int WithoutKey { get; set; }

        public int NotInLedger { get; set; }

        public void Sample(string sourceKey)
        {
            if (_sample.Count < MaxNotFoundSample)
            {
                _sample.Add(sourceKey);
            }
        }

        public void Add(SourceSyncApplied applied)
        {
            Arrivals += applied.Arrivals;
            PlansRequested += applied.PlansRequested;
            NotFoundRecorded += applied.NotFound;
        }

        public SourceSyncSummary Summary() => new(
            Checked, InAgreement, Arrivals, ChangedUnseen, DeletedUnseen, PlansRequested, Restored, NotFound, NotFoundRecorded,
            FoundAgain, WithoutKey, NotInLedger, [.. _sample]);
    }
}
