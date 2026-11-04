using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// A test as a run evaluates it: its <c>{parameter}</c> and <c>{partition}</c> tokens substituted with the run's values in
/// its query, its ids and every text it compares with (never in a regular expression, whose braces are its own).
/// </summary>
public static class AssertionBinding
{
    public static AssertionTest Bind(AssertionTest test, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(values);
        string Sub(string text) => FlowParameters.Substitute(text, values);
        ValueCondition Condition(ValueCondition c) => c.Operator is ValueOperator.Matches or ValueOperator.NotMatches
            ? c
            : c with { Operands = c.Operands.Select(o => o.WithText(Sub)).ToList() };
        var assertions = test.Assertions.Select(a => a switch
        {
            ValueAssertion v => v with { Condition = Condition(v.Condition), Where = v.Where.Select(w => w with { Condition = Condition(w.Condition) }).ToList() },
            RecordSetAssertion r => r with { Rows = r.Rows.Select(row => (IReadOnlyList<ExpectedValue?>)row.Select(c => c?.WithText(Sub)).ToList()).ToList() },
            KnownValuesAssertion k => k with
            {
                Rows = k.Rows.Select(row => row with
                {
                    Locate = row.Locate.Select(l => l with { Value = l.Value.WithText(Sub) }).ToList(),
                    Checks = row.Checks.Select(c => c with { Condition = Condition(c.Condition) }).ToList(),
                }).ToList(),
            },
            AggregateAssertion g when g.Except.Count > 0 => g with { Except = g.Except.Select(e => e.WithText(Sub)).ToList() },
            GroupAssertion g => g with { Groups = g.Groups.Select(x => x with { Key = Sub(x.Key) }).ToList(), Absent = g.Absent.Select(Sub).ToList() },
            _ => a,
        }).ToList();
        return test with
        {
            Query = test.Query is null ? null : Sub(test.Query),
            Ids = test.Ids.Select(Sub).ToList(),
            Assertions = assertions,
        };
    }
}

/// <summary>
/// Evaluates one test (osdu/docs/reference/flow/assertion.md, Tests): counts what its query matches (or reads the
/// records its ids name), reads the records its assertions need once, in pages, feeding every assertion as it goes and
/// every bulk assertion each record's bulk data, and completes each assertion. A test that does not fit its template is
/// not evaluated; a test whose reads fail is errored, with whatever the search alone could still answer answered.
/// </summary>
public static class TestEvaluator
{
    /// <summary>How many ids a note names when it lists them.</summary>
    private const int NamedIds = 5;

    public static async Task<TestResult> EvaluateAsync(TestScope scope, TemplateFit fit, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fit);
        ArgumentNullException.ThrowIfNull(time);
        var test = scope.Test;
        var started = time.GetUtcNow().UtcDateTime;
        var clock = Stopwatch.StartNew();
        if (fit.Problems.Count > 0)
        {
            return new TestResult
            {
                Test = test.Name,
                Description = test.Description,
                Kind = test.Kind,
                Tags = test.Tags,
                Outcome = TestOutcomes.Errored,
                Query = test.Query,
                Ids = test.Ids.Count,
                Template = fit.Version,
                DefinitionHash = test.DefinitionHash,
                DurationMs = clock.ElapsedMilliseconds,
                Error = fit.Problems.Count == 1 ? fit.Problems[0] : string.Create(CultureInfo.InvariantCulture, $"The test does not fit the template of its kind in {fit.Problems.Count} places."),
                Problems = fit.Problems,
                Assertions = test.Assertions.Select((a, i) => AssertionOutcome.Skipped(i, a, "not evaluated: the test does not fit the template of its kind")).ToList(),
                StartedUtc = started,
                CompletedUtc = time.GetUtcNow().UtcDateTime,
            };
        }

        var evaluators = Evaluator.For(scope);
        var bulkEvaluators = evaluators.OfType<BulkEvaluator>().ToList();
        var recordEvaluators = evaluators.Where(e => e is not BulkEvaluator && e.Need > RecordNeed.None).ToList();
        var need = evaluators.Count == 0 ? RecordNeed.None : evaluators.Max(e => e.Need);
        var notes = new List<string>(scope.Answers);
        long? matched = null;
        long read = 0;
        var sampled = false;
        string? notRead = null;
        IReadOnlyList<string> missing = [];
        string? error = null;
        try
        {
            var ids = new List<string>();
            if (test.ByIds)
            {
                var (records, absent) = await ReadByIdAsync(scope, notes, ct).ConfigureAwait(false);
                matched = records.Count;
                missing = absent;
                foreach (var (id, record) in records)
                {
                    Observe(recordEvaluators, id, record);
                    ids.Add(id);
                }

                read = records.Count;
            }
            else
            {
                matched = await scope.Search.CountAsync(scope.Query with { ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
                if (need > RecordNeed.None)
                {
                    if (matched > test.MaxRecords && !test.Sample)
                    {
                        notRead = string.Create(CultureInfo.InvariantCulture,
                            $"The query matches {matched} records, more than the {test.MaxRecords} this test reads, so its assertions over the records were not evaluated: narrow the query, raise maxRecords (at most {AssertionDefaults.MaxRecordsCeiling}), or set sample: true to evaluate the first {test.MaxRecords}.");
                    }
                    else
                    {
                        sampled = matched > test.MaxRecords;
                        read = await ReadSearchAsync(scope, need, recordEvaluators, ids, notes, ct).ConfigureAwait(false);
                    }
                }
            }

            if (bulkEvaluators.Count > 0 && notRead is null && ids.Count > 0)
            {
                await Parallel.ForEachAsync(
                    ids,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, scope.Concurrency), CancellationToken = ct },
                    async (id, token) => await BulkReader.EvaluateAsync(scope, bulkEvaluators, id, token).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            error = SecretHygiene.RedactedMessage(ex);
            scope.Logger.LogWarning("test '{Test}': reading OSDU failed: {Error}", test.Name, error);
        }

        var subject = new TestSubject(matched, read, sampled, notRead, missing);
        var outcomes = new List<AssertionOutcome>(evaluators.Count);
        foreach (var evaluator in evaluators)
        {
            if (error is not null && (matched is null || evaluator.Need > RecordNeed.None))
            {
                outcomes.Add(AssertionOutcome.Errored(evaluator.Index, evaluator.Assertion, "Reading OSDU failed: " + error));
                continue;
            }

            try
            {
                outcomes.Add(await evaluator.CompleteAsync(scope, subject, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (Expected(ex, ct))
            {
                outcomes.Add(AssertionOutcome.Errored(evaluator.Index, evaluator.Assertion, SecretHygiene.RedactedMessage(ex)));
            }
        }

        // What an assertion expects is quoted with the day it was stated, and one not judged (disabled, or past the day its
        // values hold until) is reported where the document has it, skipped with why.
        for (var i = 0; i < outcomes.Count; i++)
        {
            var stated = test.Assertions[outcomes[i].Index].AsOf ?? test.AsOf;
            if (stated is not null)
            {
                outcomes[i] = outcomes[i] with { Expected = outcomes[i].Expected + AssertionActivity.Stated(stated) };
            }
        }

        foreach (var (assertion, position) in test.Assertions.Select((a, i) => (a, i)))
        {
            if (AssertionActivity.Disabled(assertion) is { } off)
            {
                outcomes.Add(AssertionOutcome.Skipped(position, assertion, off));
            }
            else if (AssertionActivity.Stale(assertion, test, scope.Now) is { } why)
            {
                outcomes.Add(AssertionOutcome.NeedsNewProofValues(position, assertion, why, assertion.AsOf ?? test.AsOf));
            }
            else if (scope.Unanswered.TryGetValue(position, out var unanswered))
            {
                outcomes.Add(AssertionOutcome.Errored(position, assertion, unanswered));
            }
        }

        outcomes.Sort((a, b) => a.Index.CompareTo(b.Index));
        var (outcome, severity) = TestResults.Outcome(outcomes);
        if (sampled)
        {
            notes.Insert(0, string.Create(CultureInfo.InvariantCulture, $"The query matches {matched} records; the first {test.MaxRecords} were evaluated as a sample."));
        }

        return new TestResult
        {
            Test = test.Name,
            Description = test.Description,
            Kind = test.Kind,
            Tags = test.Tags,
            Outcome = error is null ? outcome : TestOutcomes.Errored,
            Severity = severity,
            Matched = matched,
            Evaluated = read,
            Sampled = sampled,
            Query = scope.Query.Query,
            Ids = test.Ids.Count,
            Template = fit.Version,
            DefinitionHash = test.DefinitionHash,
            DurationMs = clock.ElapsedMilliseconds,
            Error = error ?? notRead,
            Notes = notes,
            Assertions = outcomes,
            StartedUtc = started,
            CompletedUtc = time.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>Whether a failure is one a test reports as errored, rather than a defect or the run being stopped.</summary>
    internal static bool Expected(Exception ex, CancellationToken ct)
        => ex is SqlFlowException or HttpRequestException or JsonException or IOException or TimeoutException or UnauthorizedAccessException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static void Observe(IReadOnlyList<Evaluator> evaluators, string id, JsonNode record)
    {
        foreach (var evaluator in evaluators)
        {
            evaluator.Observe(id, record);
        }
    }

    /// <summary>
    /// The records a test names by id, read from storage a hundred at a time, in the order named; the ids storage did not
    /// return, and the records of another kind than the test's, which are left out and noted.
    /// </summary>
    private static async Task<(IReadOnlyList<(string Id, JsonNode Record)> Records, IReadOnlyList<string> Missing)> ReadByIdAsync(
        TestScope scope, List<string> notes, CancellationToken ct)
    {
        var test = scope.Test;
        var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var gate = new Lock();
        await Parallel.ForEachAsync(
            test.Ids.Chunk(StorageRecords.Batch),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, scope.Concurrency), CancellationToken = ct },
            async (chunk, token) =>
            {
                var result = await scope.Storage.ReadAsync(chunk, null, token).ConfigureAwait(false);
                lock (gate)
                {
                    foreach (var record in result.Records)
                    {
                        if (OsduSearch.IdOf(record) is { } id && JsonNode.Parse(record.GetRawText()) is { } node)
                        {
                            found[id] = node;
                        }
                    }
                }
            }).ConfigureAwait(false);

        var records = new List<(string, JsonNode)>(found.Count);
        var otherKind = new List<string>();
        foreach (var id in test.Ids)
        {
            if (!found.TryGetValue(id, out var record))
            {
                continue;
            }

            if (record["kind"] is JsonValue kind && kind.TryGetValue<string>(out var text) && !string.Equals(text, test.Kind, StringComparison.OrdinalIgnoreCase))
            {
                otherKind.Add($"{id} ({text})");
                continue;
            }

            records.Add((id, record));
        }

        var missing = test.Ids.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{missing.Count} of the {test.Ids.Count} ids are not in storage: {Named(missing)}."));
        }

        if (otherKind.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{otherKind.Count} record(s) are of another kind than {test.Kind}, and were left out: {Named(otherKind)}."));
        }

        return (records, missing);
    }

    /// <summary>
    /// Reads the records the query matches, a page at a time and at most the test's maxRecords, feeding every record
    /// evaluator each record in the order the search returns them: the hits themselves when the test reads the index or
    /// needs only ids, else the records storage holds for the page's ids. Returns how many were read.
    /// </summary>
    private static async Task<long> ReadSearchAsync(
        TestScope scope, RecordNeed need, IReadOnlyList<Evaluator> evaluators, List<string> ids, List<string> notes, CancellationToken ct)
    {
        var test = scope.Test;
        var fromStorage = need == RecordNeed.Fields && test.Read == AssertionRead.Storage;
        IReadOnlyList<string> fields = need == RecordNeed.Fields && !fromStorage
            ? evaluators.SelectMany(e => e.Paths).Select(GroupEvaluator.SearchField).Append("id").Append("kind").Distinct(StringComparer.Ordinal).ToList()
            : ["id", "kind"];
        long read = 0;
        long notStored = 0;
        var notStoredIds = new List<string>();
        await foreach (var page in scope.Search.PagesAsync(scope.Query with { ReturnedFields = fields }, Math.Min(OsduSearch.MaxPage, test.MaxRecords), ct)
                           .ConfigureAwait(false))
        {
            var hits = page.Hits.Take((int)Math.Min(page.Hits.Count, test.MaxRecords - read)).ToList();
            var pageIds = hits.Select(OsduSearch.IdOf).Where(id => id is not null).Cast<string>().ToList();
            IReadOnlyList<(string Id, JsonNode Record)> records;
            if (fromStorage)
            {
                var stored = await ReadStoredAsync(scope, pageIds, ct).ConfigureAwait(false);
                records = pageIds.Where(stored.ContainsKey).Select(id => (id, stored[id])).ToList();
                var gone = pageIds.Where(id => !stored.ContainsKey(id)).ToList();
                notStored += gone.Count;
                notStoredIds.AddRange(gone.Take(Math.Max(0, NamedIds - notStoredIds.Count)));
            }
            else
            {
                records = hits.Where(h => OsduSearch.IdOf(h) is not null).Select(h => (OsduSearch.IdOf(h)!, JsonNode.Parse(h.GetRawText())!)).ToList();
            }

            foreach (var (id, record) in records)
            {
                Observe(evaluators, id, record);
                ids.Add(id);
            }

            read += hits.Count;
            if (read >= test.MaxRecords)
            {
                break;
            }
        }

        if (notStored > 0)
        {
            // The index found these, and storage did not hand them over: deleted since they were indexed (the index lags a
            // write by some seconds), or not readable by this identity.
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{notStored} record(s) the index lists are not in storage (deleted since they were indexed, or not readable by this identity), and were left out: {Named(notStoredIds)}{(notStored > notStoredIds.Count ? ", ..." : string.Empty)}."));
        }

        return read - notStored;
    }

    /// <summary>The records storage holds for <paramref name="ids"/>, read a hundred at a time and as many at once as the test allows, by id.</summary>
    internal static async Task<IReadOnlyDictionary<string, JsonNode>> ReadStoredAsync(TestScope scope, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var gate = new Lock();
        await Parallel.ForEachAsync(
            ids.Chunk(StorageRecords.Batch),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, scope.Concurrency), CancellationToken = ct },
            async (chunk, token) =>
            {
                var result = await scope.Storage.ReadAsync(chunk, null, token).ConfigureAwait(false);
                lock (gate)
                {
                    foreach (var record in result.Records)
                    {
                        if (OsduSearch.IdOf(record) is { } id && JsonNode.Parse(record.GetRawText()) is { } node)
                        {
                            found[id] = node;
                        }
                    }
                }
            }).ConfigureAwait(false);
        return found;
    }


    private static string Named(IReadOnlyList<string> ids) => string.Join(", ", ids.Take(NamedIds)) + (ids.Count > NamedIds ? ", ..." : string.Empty);
}
