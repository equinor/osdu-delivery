using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// A staged delivered assertion (osdu/docs/reference/flow/staged-accounting.md): every row a delivery flow's record table holds
/// in the scope read, as its delivery reads it, is accounted for. A row is in OSDU when the test's query finds the record its
/// ledger holds it as (or, for a row the ledger holds no record of, the id its mapping makes of its key); otherwise the ledger
/// explains it (held, failed, waiting for another record, queued, removed or reverted, each with what the ledger recorded),
/// or nothing does (never planned, delivered and not found, a blank key). A row the record table marks deleted is not
/// expected in OSDU. With exact, a record the test's query finds that no staged row accounts for fails it too.
/// </summary>
internal sealed class StagedAccountingEvaluator(int index, DeliveredAssertion assertion, int examples, AssertionTest test, string at)
    : Evaluator(index, assertion, examples)
{
    /// <summary>How many staged keys are read, and looked up in the ledger, at a time.</summary>
    private const int Batch = 1000;

    /// <summary>The most reasons the outcome names among the rows the ledger holds back.</summary>
    private const int NamedReasons = 5;

    private readonly HashSet<string> _found = new(StringComparer.Ordinal);

    public override RecordNeed Need => RecordNeed.Ids;

    public override void Observe(string id, JsonNode record) => _found.Add(id);

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return NotRead(subject);
        }

        if (subject.Sampled)
        {
            return AssertionOutcome.Errored(Index, Assertion,
                "The test read a sample of the records its query matches, and whether every staged row is among them needs all of them: raise maxRecords, or narrow the query.");
        }

        if (scope.Ledger is not { } ledger)
        {
            return AssertionOutcome.Errored(Index, Assertion, DeliveryServices.NoLedgerMessage);
        }

        if (scope.Deliveries is not { } deliveries)
        {
            return AssertionOutcome.Errored(Index, Assertion,
                "This host opens no delivery flow's staged rows, so whether each is in OSDU cannot be told here; run the flow where its delivery flows run.");
        }

        var staged = await deliveries.OpenAsync(assertion, at, ct).ConfigureAwait(false);
        var tally = new Tally(staged, scope.Partition, Examples);
        var batch = new List<IngestionKey>(Batch);
        await foreach (var key in staged.Source.ReadKeysAsync(Batch, SourceKeyRead.Read, ct).ConfigureAwait(false))
        {
            batch.Add(key);
            if (batch.Count == Batch)
            {
                await tally.AddAsync(ledger, batch, _found, ct).ConfigureAwait(false);
                batch.Clear();
            }

            if (tally.Rows + batch.Count > test.MaxRecords)
            {
                return AssertionOutcome.Errored(Index, Assertion, string.Create(CultureInfo.InvariantCulture,
                    $"{staged.Flow.Label} stages more than {test.MaxRecords} rows in the scope read, more than this test reads; raise maxRecords (at most {AssertionDefaults.MaxRecordsCeiling}), or read a narrower scope with values."));
            }
        }

        await tally.AddAsync(ledger, batch, _found, ct).ConfigureAwait(false);
        return Judge(tally);
    }

    private AssertionOutcome Judge(Tally tally)
    {
        var extra = assertion.Exact
            ? _found.Where(id => !tally.Accounted.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList()
            : [];
        var explainedSeverity = assertion.Explained ?? Assertion.Severity;
        AssertionSeverity? heaviest = tally.Unexplained > 0 || extra.Count > 0 ? Assertion.Severity : null;
        if (tally.Explained > 0 && (heaviest is null || explainedSeverity > heaviest))
        {
            heaviest = explainedSeverity;
        }

        var failures = tally.UnexplainedExamples
            .Concat(extra.Take(Examples).Select(id => new AssertionExample(id, null, $"is found by the test's query, and no row {tally.Label} stages accounts for it")))
            .Concat(tally.ExplainedExamples)
            .Take(Examples)
            .ToList();
        var expected = tally.Rows - tally.Marked;
        var reasons = tally.Reasons
            .OrderByDescending(r => r.Value.Count).ThenBy(r => r.Value.Text, StringComparer.Ordinal)
            .Take(NamedReasons)
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Value.Text} ({r.Value.Count})"))
            .ToList();
        var actual = string.Create(CultureInfo.InvariantCulture,
                $"{tally.Rows} row(s) staged: {tally.Present} in OSDU, {tally.Explained} held back by the ledger, {tally.Unexplained} not in OSDU with nothing to explain it ({tally.NeverPlanned} never planned, {tally.NotFound} delivered and not found, {tally.Blank} with a blank key)")
            + (tally.Marked > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {tally.Marked} marked deleted, and not expected in OSDU") : string.Empty)
            + (assertion.Exact ? string.Create(CultureInfo.InvariantCulture, $"; {_found.Count} found, {extra.Count} of them accounted for by no staged row") : string.Empty)
            + (reasons.Count > 0 ? "; held back most for " + string.Join(", ", reasons) : string.Empty);
        var message = string.Join("; ", new[]
        {
            tally.Unexplained > 0 ? string.Create(CultureInfo.InvariantCulture, $"{tally.Unexplained} staged row(s) are not in OSDU, and the ledger of {tally.Label} does not say why") : null,
            extra.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{extra.Count} record(s) the test's query finds are accounted for by no staged row") : null,
            tally.Explained > 0 ? string.Create(CultureInfo.InvariantCulture, $"{tally.Explained} staged row(s) the ledger holds back, saying why")
                + (explainedSeverity != Assertion.Severity ? $" ({AssertionText.Of(explainedSeverity)})" : string.Empty) : null,
        }.Where(m => m is not null)) + ".";
        var outcome = Result(
            heaviest is null, actual, message, expected, tally.Unexplained + tally.Explained + extra.Count,
            expected == 0 ? null : Math.Round(tally.Present * 100.0 / expected, 3), failures);
        return heaviest is { } severity ? outcome with { Severity = AssertionText.Of(severity) } : outcome;
    }

    /// <summary>What the staged rows came to, counted as they are read, with the first examples of each and the reasons the ledger gave.</summary>
    private sealed class Tally(StagedDelivery staged, string partition, int examples)
    {
        public string Label => staged.Flow.Label;

        public long Rows { get; private set; }

        public long Present { get; private set; }

        public long Explained { get; private set; }

        public long NeverPlanned { get; private set; }

        public long NotFound { get; private set; }

        public long Blank { get; private set; }

        public long Marked { get; private set; }

        public long Unexplained => NeverPlanned + NotFound + Blank;

        /// <summary>The OSDU ids the staged rows account for, which an exact assertion holds the records found to.</summary>
        public HashSet<string> Accounted { get; } = new(StringComparer.Ordinal);

        public List<AssertionExample> UnexplainedExamples { get; } = [];

        public List<AssertionExample> ExplainedExamples { get; } = [];

        /// <summary>The reasons the ledger holds rows back for, by status and problem: the first message of each, and how many rows share it.</summary>
        public Dictionary<(RecordStatus, long?), (string Text, long Count)> Reasons { get; } = [];

        public async Task AddAsync(ILedger ledger, IReadOnlyList<IngestionKey> keys, HashSet<string> found, CancellationToken ct)
        {
            if (keys.Count == 0)
            {
                return;
            }

            var system = staged.Mapping.Dataset.System;
            var live = new List<(IngestionKey Row, DeliveryKey Key)>(keys.Count);
            foreach (var row in keys)
            {
                Rows++;
                if (row.DeletedUtc is not null)
                {
                    Marked++;
                }
                else if (row.Key.Values.Any(string.IsNullOrWhiteSpace))
                {
                    Blank++;
                    Note(UnexplainedExamples, new AssertionExample(
                        SourceKey.Display(system, row.Key.Values), null, $"is staged with a blank key column, so {Label} makes no record of it"));
                }
                else
                {
                    live.Add((row, DeliveryKey.Derive(system, row.Key.Values)));
                }
            }

            if (live.Count == 0)
            {
                return;
            }

            var records = await ledger.GetRecordsAsync(staged.Flow.Id, live.Select(l => l.Key), ct).ConfigureAwait(false);
            foreach (var (row, key) in live)
            {
                records.TryGetValue(key, out var record);
                var id = Normalized(record?.TargetId ?? record?.ClaimedTargetId ?? staged.Mapping.RecordIdOf(partition, key, row.Key.Values));
                var shown = record?.Label ?? record?.SourceKey ?? SourceKey.Display(system, row.Key.Values);
                if (id is not null && found.Contains(id))
                {
                    Present++;
                    Accounted.Add(id);
                    continue;
                }

                if (id is not null)
                {
                    Accounted.Add(id);
                }

                if (record is null)
                {
                    NeverPlanned++;
                    Note(UnexplainedExamples, new AssertionExample(id ?? shown, TestResults.Quote(shown),
                        $"is staged, and the ledger of {Label} holds no record of it: no run has planned it"));
                    continue;
                }

                if (record.Status == RecordStatus.Delivered)
                {
                    NotFound++;
                    Note(UnexplainedExamples, new AssertionExample(id ?? shown, TestResults.Quote(shown),
                        string.Create(CultureInfo.InvariantCulture, $"is delivered by {Label} as {id ?? "no id"}")
                        + (record.TargetVersion is { } version ? string.Create(CultureInfo.InvariantCulture, $" (version {version})") : string.Empty)
                        + (record.LastDeliveredUtc is { } when ? string.Create(CultureInfo.InvariantCulture, $" on {when:yyyy-MM-dd HH:mm}Z") : string.Empty)
                        + ", and the test's query does not find it" + Where(record)));
                    continue;
                }

                Explained++;
                var why = Why(record);
                var reason = (record.Status, record.ProblemHash);
                Reasons[reason] = Reasons.TryGetValue(reason, out var known) ? (known.Text, known.Count + 1) : (why.Length > 120 ? why[..120] + "..." : why, 1);
                Note(ExplainedExamples, new AssertionExample(id ?? shown, TestResults.Quote(shown), why + Where(record)));
            }
        }

        private void Note(List<AssertionExample> list, AssertionExample example)
        {
            if (list.Count < examples)
            {
                list.Add(example);
            }
        }

        /// <summary>What the ledger says of a record it holds back: its state, with the reason it recorded.</summary>
        private string Why(RecordState record) => record.Status switch
        {
            RecordStatus.Held => $"is held by {Label}: {record.LastError ?? "the ledger records no reason"}",
            RecordStatus.Failed => $"failed in {Label}: {record.LastError ?? "the ledger records no error"}",
            RecordStatus.Waiting => $"waits in {Label} for {record.WaitingFor ?? "another record"}, which another record of the ledger holds and has not delivered",
            RecordStatus.Pending or RecordStatus.Delivering => $"is queued in {Label} and not sent yet",
            RecordStatus.Deleted => $"was removed from OSDU, and {Label} sends it again when a run reads its row",
            RecordStatus.Reverted => $"was reverted, and {Label} does not send it again while its row is unchanged, unless it is released",
            _ => $"is {record.Status.ToString().ToLowerInvariant()} in {Label}",
        };

        /// <summary>Where the row the ledger last read of a record sits: the file and row it was landed from, when the ledger knows them.</summary>
        private static string Where(RecordState record)
        {
            var file = record.PendingSourceFileName ?? record.SourceFileName;
            var line = record.PendingSourceFileName is not null ? record.PendingSourceRowNumber : record.SourceRowNumber;
            return file is null
                ? string.Empty
                : line is { } n ? string.Create(CultureInfo.InvariantCulture, $" (row {n} of {file})") : $" (from {file})";
        }

        private static string? Normalized(string? id) => id is null ? null : TargetId.WithoutVersion(id).TrimEnd(':');
    }
}
