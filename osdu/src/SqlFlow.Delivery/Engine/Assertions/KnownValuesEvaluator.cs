using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// Known values (osdu/docs/reference/flow/assertion.md, Known values): each row finds its record (or the item of the list it
/// is an item of) by the full paths and values it is located by, and is held to its proof values. It reads records of its
/// own rather than every record the test's query matches: a search per batch of rows located alike, the test's query
/// narrowed by the rows' values, where a path through other records narrows by the records its values find there; then the
/// records the paths reach from the records found, the ones they name and the ones naming them. Every row is judged against
/// every record read, so a record two batches find is read once. A row with curve proof values has the bulk data of the
/// record it finds measured before it is judged (osdu/docs/reference/flow/curve-proof-values.md), each record once.
/// </summary>
internal sealed class KnownValuesEvaluator(int index, KnownValuesAssertion assertion, int examples) : Evaluator(index, assertion, examples)
{
    /// <summary>
    /// The rows one search is narrowed by: every distinct value is one clause of the query, and fifty rows located by eight
    /// paths stay far inside the 1024 clauses the search service allows in one query.
    /// </summary>
    public const int RowsPerSearch = 50;

    /// <summary>
    /// The most records one search through other records may find for its ids to narrow the next search; past it the rows are
    /// read without that narrowing, and judged all the same.
    /// </summary>
    public const int MaxFoundPerSearch = 1000;

    /// <summary>More rows located alike than this are found by reading every record the test's query matches once, rather than by a search per batch.</summary>
    public const int WholeReadRows = 1000;

    /// <summary>The most proof values a reason names, and the paths an outcome names as differing most.</summary>
    private const int Named = 8;

    /// <summary>The records a reason names when a row is found more than once.</summary>
    private const int NamedMatches = 5;

    private readonly Dictionary<string, JsonNode> _records = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, JsonNode?> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, string Reference), Dictionary<string, List<JsonNode>>> _children = [];
    private readonly Dictionary<(string Path, string Value), long> _found = [];
    private readonly Dictionary<(string Id, string Path), IReadOnlyList<JsonNode>> _read = [];
    private readonly Dictionary<string, CurveReading> _curves = new(StringComparer.Ordinal);
    private long _notStored;
    private long _throughOthers;
    private int _searches;

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (scope.Template is not { } template)
        {
            return AssertionOutcome.Errored(Index, Assertion, "Known values read the fields of the records, and the test has no template of its kind to read them by.");
        }

        var enabled = assertion.Rows.Select((row, i) => (Row: row, Index: i)).Where(r => r.Row.Disabled is null).ToList();
        if (enabled.Count == 0)
        {
            return AssertionOutcome.Skipped(Index, Assertion, string.Create(CultureInfo.InvariantCulture,
                $"every one of its {assertion.Rows.Count} row(s) is disabled: {assertion.Rows[0].Disabled}"));
        }

        var shapes = KnownValuesShape.Of(assertion, template, scope.Referenced, Assertion.Label, new List<string>());
        var rows = enabled.Select(r => (r.Row, Shape: shapes[r.Index])).ToList();
        var locatable = rows.Where(r => r.Row.Unlocated is null).ToList();
        await FindAsync(scope, locatable, ct).ConfigureAwait(false);
        await ReachAsync(scope, locatable.Select(r => r.Shape).ToList(), ct).ConfigureAwait(false);
        var all = _order.Select(id => new Match(id, _records[id], null, 0)).ToList();
        var located = rows.Select(r => r.Row.Unlocated is { } why ? new Located([], why, null) : Locate(r.Row, r.Shape, all)).ToList();
        await MeasureCurvesAsync(scope, rows, located, ct).ConfigureAwait(false);
        return Judge(rows, located);
    }

    // --- Measuring the curves ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the bulk data of each record a row with curve proof values finds once (osdu/docs/reference/flow/known-values.md,
    /// Curve proof values), for every curve the rows finding it name, the records at once as the flow's concurrency allows. A
    /// row finding no record, or more than one, measures nothing: it fails on that alone.
    /// </summary>
    private async Task MeasureCurvesAsync(
        TestScope scope, IReadOnlyList<(KnownRow Row, KnownValuesShape Shape)> rows, IReadOnlyList<Located> located, CancellationToken ct)
    {
        var wanted = new Dictionary<string, (JsonNode Record, List<CurveCheck> Checks)>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var checks = rows[i].Row.CurveChecks.Where(c => c.Disabled is null).ToList();
            if (checks.Count == 0 || located[i].Found.Count != 1)
            {
                continue;
            }

            var match = located[i].Found[0];
            if (wanted.TryGetValue(match.Id, out var already))
            {
                already.Checks.AddRange(checks);
            }
            else
            {
                wanted[match.Id] = (match.Record, checks);
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var gate = new Lock();
        await Parallel.ForEachAsync(
            wanted,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, scope.Concurrency), CancellationToken = ct },
            async (entry, token) =>
            {
                var reading = await KnownCurves.MeasureAsync(scope, entry.Key, entry.Value.Record, entry.Value.Checks, assertion.CurveIndex, token).ConfigureAwait(false);
                lock (gate)
                {
                    _curves[entry.Key] = reading;
                }
            }).ConfigureAwait(false);
    }

    /// <summary>Whether the rows read the curve their records name as the one their bulk data is indexed by, which a search then returns.</summary>
    private bool ReadsReferenceCurve(IReadOnlyList<(KnownRow Row, KnownValuesShape Shape)> rows)
        => assertion.CurveIndex is null && rows.Any(r => r.Row.CurveChecks.Any(c => c.Disabled is null && CurveProofValues.ReadsIndex(c.Measure)));

    // --- Finding the records ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the records the rows' values can find, a batch of rows located alike at a time. A batch whose values narrow
    /// nothing reads every record the test's query matches, which then serves every batch.
    /// </summary>
    private async Task FindAsync(TestScope scope, IReadOnlyList<(KnownRow Row, KnownValuesShape Shape)> rows, CancellationToken ct)
    {
        var alike = rows.GroupBy(r => string.Join("\u001f", r.Shape.LocateBy.Where(c => !c.ReadsItem).Select(c => c.Column.Path + (c.Column.IgnoreCase ? "/i" : string.Empty))));
        foreach (var group in alike)
        {
            var members = group.ToList();
            if (members.Count > WholeReadRows)
            {
                // So many rows located alike (the rows a query answered, usually) are found sooner by reading every record the
                // test's query matches once, a page at a time, than by a search for each batch of them.
                await ReadRecordsAsync(scope, rows, scope.Query.Query, ct).ConfigureAwait(false);
                return;
            }

            var paths = members[0].Shape.LocateBy.Where(c => !c.ReadsItem).ToList();
            for (var start = 0; start < members.Count; start += RowsPerSearch)
            {
                var batch = members.Skip(start).Take(RowsPerSearch).ToList();
                var clauses = new List<OsduQuery>();
                var findsNothing = false;
                foreach (var shape in paths)
                {
                    var values = batch.Select(r => r.Row.Locate.First(l => l.Column.Path == shape.Column.Path).Value)
                        .DistinctBy(v => v.Text, StringComparer.Ordinal)
                        .ToList();
                    var (clause, nothing) = shape.LeavesRecord
                        ? await ThroughOthersAsync(scope, shape, values, ct).ConfigureAwait(false)
                        : (Narrowing(shape.Steps[0].Indexed, values, shape.Column.IgnoreCase), false);
                    if (nothing)
                    {
                        // No value of the batch reaches a record, so no record of the test can be found by it.
                        findsNothing = true;
                        break;
                    }

                    if (clause is not null)
                    {
                        clauses.Add(clause);
                    }
                }

                if (findsNothing)
                {
                    continue;
                }

                await ReadRecordsAsync(scope, rows, clauses.Count == 0 ? scope.Query.Query : OsduSearch.Within(scope.Query.Query, OsduQuery.All([.. clauses]).Text), ct)
                    .ConfigureAwait(false);
                if (clauses.Count == 0)
                {
                    // Nothing narrowed the search, so it read every record the test's query matches: every batch's records.
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The clause a path's values narrow a search by, or null when they cannot: compared whole where the index holds the value
    /// whole, by its words (a superset, judged exactly once read) where the path ignores case and the index holds text, and
    /// not at all where the index cannot compare it or a value cannot be carried in a query.
    /// </summary>
    private static OsduQuery? Narrowing(OsduField? field, IReadOnlyList<ExpectedValue> values, bool ignoreCase)
    {
        if (field is null || values.Count == 0)
        {
            return null;
        }

        try
        {
            if (ignoreCase)
            {
                return field.Index == OsduFieldIndex.Text ? OsduQuery.Any(values.Select(v => OsduQuery.Words(field, v.Text)).ToArray()) : null;
            }

            return values.Any(v => OsduQuery.EqualProblem(field, v.Text) is not null) ? null : OsduQuery.AnyOf(field, values.Select(v => v.Text).ToList());
        }
        catch (OsduQueryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The clause the ids of records narrow a search by, through the reference <paramref name="field"/> holds: the reference
    /// starting with each id and its closing ':', so a reference written with a version is found as well as one written
    /// without; whole references where the index cannot compare a beginning; null when it cannot compare the reference.
    /// </summary>
    private static OsduQuery? ReferenceNarrowing(OsduField? field, IReadOnlyList<string> ids)
    {
        if (field is null || ids.Count == 0)
        {
            return null;
        }

        try
        {
            return OsduQuery.Any(ids.Select(id => OsduQuery.Prefix(field, id + ":")).ToArray());
        }
        catch (OsduQueryException)
        {
            try
            {
                return OsduQuery.AnyOf(field, ids.Select(id => id + ":").ToList());
            }
            catch (OsduQueryException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The clause on the test's records a path through other records narrows a search by: the records at the path's end that
    /// hold one of the values, then step by step back to the test's records, the records naming those (searched by their
    /// references to them) or the records those name (by the references they hold). Each value's records at the end are
    /// counted, which says why a row is not found. Null when a step cannot be searched or finds too many to narrow by; "nothing"
    /// when no value reaches a record at all.
    /// </summary>
    private async Task<(OsduQuery? Clause, bool Nothing)> ThroughOthersAsync(TestScope scope, KnownColumnShape path, IReadOnlyList<ExpectedValue> values, CancellationToken ct)
    {
        var steps = path.Steps;
        var last = path.Last;
        if (last.EntityType is not { } lastType || Narrowing(last.Indexed, values, path.Column.IgnoreCase) is not { } clause)
        {
            return (null, false);
        }

        var returned = new List<string> { "id", GroupEvaluator.SearchField(last.Step.Field!) };
        if (last.Step.Via == KnownStepVia.Children)
        {
            returned.Add(GroupEvaluator.SearchField(last.Step.Reference!));
        }

        var hits = await SearchAsync(scope, lastType, clause, returned, ct).ConfigureAwait(false);
        if (hits is null)
        {
            return (null, false);
        }

        var condition = Comparing(path.Column);
        var counted = new Dictionary<string, long>(StringComparer.Ordinal);
        var records = new List<JsonNode>();
        foreach (var hit in hits)
        {
            var held = RecordValues.Select(hit, last.Step.Field!);
            var holds = false;
            foreach (var value in values.Where(v => held.Any(h => ValueComparer.Equal(h, v, condition, out _))))
            {
                counted[value.Text] = counted.GetValueOrDefault(value.Text) + 1;
                holds = true;
            }

            if (holds)
            {
                records.Add(hit);
            }
        }

        foreach (var value in values)
        {
            _found[(path.Column.Path, value.Text)] = counted.GetValueOrDefault(value.Text);
        }

        for (var j = steps.Count - 1; j >= 0; j--)
        {
            if (records.Count == 0)
            {
                return (null, true);
            }

            var step = steps[j];
            switch (step.Step.Via)
            {
                case KnownStepVia.Parent:
                    {
                        // The records of the step before whose reference names one of these.
                        var ids = records.Select(IdOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                        var before = steps[j - 1];
                        if (ReferenceNarrowing(before.Indexed, ids) is not { } byReference)
                        {
                            return (null, false);
                        }

                        if (before.Step.Via == KnownStepVia.Record)
                        {
                            return (byReference, false);
                        }

                        var wanted = new List<string> { "id" };
                        if (before.Step.Via == KnownStepVia.Children)
                        {
                            wanted.Add(GroupEvaluator.SearchField(before.Step.Reference!));
                        }

                        if (before.EntityType is not { } beforeType
                            || await SearchAsync(scope, beforeType, byReference, wanted, ct).ConfigureAwait(false) is not { } naming)
                        {
                            return (null, false);
                        }

                        // The records of the step before, which the next pass steps back from.
                        records = naming;
                        break;
                    }

                case KnownStepVia.Children:
                    {
                        // The records these name by the reference the step reaches them by.
                        var parents = records.SelectMany(r => RecordValues.Select(r, step.Step.Reference!)).Select(ReferenceOf).OfType<string>()
                            .Distinct(StringComparer.Ordinal).ToList();
                        if (j == 0)
                        {
                            if (parents.Count == 0)
                            {
                                return (null, true);
                            }

                            var id = SearchFields.RecordProperty("id").Field;
                            return id is null ? (null, false) : (OsduQuery.AnyOf(id, parents), false);
                        }

                        records = parents.Select(p => (JsonNode)new JsonObject { ["id"] = p }).ToList();
                        break;
                    }

                default:
                    // A path starting in the record's own values is narrowed by the step that leaves it.
                    return (null, false);
            }
        }

        return (null, false);
    }

    /// <summary>Every record of an entity type a clause finds, with the fields asked; null when more than <see cref="MaxFoundPerSearch"/> are found.</summary>
    private async Task<List<JsonNode>?> SearchAsync(TestScope scope, string entityType, OsduQuery clause, IReadOnlyList<string> fields, CancellationToken ct)
    {
        _searches++;
        var hits = new List<JsonNode>();
        var query = new OsduSearchQuery { Kind = $"*:*:{entityType}:*", Query = clause.Text, ReturnedFields = fields.Distinct(StringComparer.Ordinal).ToList() };
        await foreach (var page in scope.Search.PagesAsync(query, OsduSearch.MaxPage, ct).ConfigureAwait(false))
        {
            if (page.TotalCount is > MaxFoundPerSearch)
            {
                return null;
            }

            foreach (var hit in page.Hits)
            {
                if (JsonNode.Parse(hit.GetRawText()) is { } node)
                {
                    hits.Add(node);
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Reads the records a search of the test's kind finds that no earlier search found, as the test reads (storage, or the
    /// index's hits), at most the test's maxRecords in all.
    /// </summary>
    /// <exception cref="DeliveryException">The rows' values find more records than the test reads.</exception>
    private async Task ReadRecordsAsync(TestScope scope, IReadOnlyList<(KnownRow Row, KnownValuesShape Shape)> rows, string? query, CancellationToken ct)
    {
        var test = scope.Test;
        var fromStorage = test.Read == AssertionRead.Storage;
        IReadOnlyList<string> fields = fromStorage
            ? ["id", "kind"]
            : rows.SelectMany(r => r.Shape.LocateBy.Concat(r.Shape.Check))
                .Select(c => c.Steps[0].Step.Via == KnownStepVia.Record ? GroupEvaluator.SearchField(c.Steps[0].Step.Field!) : "id")
                .Append("id").Append("kind")
                .Concat(ReadsReferenceCurve(rows) ? [CurveProofValues.ReferenceCurvePath] : [])
                .Distinct(StringComparer.Ordinal).ToList();
        _searches++;
        var search = scope.Query with { Query = query, Sort = [], ReturnedFields = fields };
        await foreach (var page in scope.Search.PagesAsync(search, Math.Min(OsduSearch.MaxPage, test.MaxRecords), ct).ConfigureAwait(false))
        {
            var fresh = page.Hits
                .Select(hit => (Id: OsduSearch.IdOf(hit), Hit: hit))
                .Where(h => h.Id is not null && !_records.ContainsKey(h.Id))
                .DistinctBy(h => h.Id, StringComparer.Ordinal)
                .ToList();
            if (_records.Count + fresh.Count > test.MaxRecords)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"The rows' values find more than the {test.MaxRecords} records this test reads; narrow the test's query, locate the rows by a value that tells the records apart, or raise maxRecords (at most {AssertionDefaults.MaxRecordsCeiling})."));
            }

            if (fromStorage)
            {
                var ids = fresh.Select(h => h.Id!).ToList();
                var stored = await TestEvaluator.ReadStoredAsync(scope, ids, ct).ConfigureAwait(false);
                foreach (var id in ids)
                {
                    if (stored.TryGetValue(id, out var record))
                    {
                        Keep(id, record);
                    }
                    else
                    {
                        _notStored++;
                    }
                }
            }
            else
            {
                foreach (var (id, hit) in fresh)
                {
                    if (JsonNode.Parse(hit.GetRawText()) is { } record)
                    {
                        Keep(id!, record);
                    }
                }
            }
        }
    }

    private void Keep(string id, JsonNode record)
    {
        _records[id] = record;
        _order.Add(id);
    }

    // --- Reaching other records ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads what the paths reach from the records found, step by step: the records the references they hold name (from
    /// storage, by id), and the records naming them (found by a search of their type for references to them, then read from
    /// storage). Each record is read once, whichever path reaches it.
    /// </summary>
    /// <exception cref="DeliveryException">The paths reach more records than one assertion reads through others.</exception>
    private async Task ReachAsync(TestScope scope, IReadOnlyList<KnownValuesShape> shapes, CancellationToken ct)
    {
        var paths = shapes
            .SelectMany(s => s.LocateBy.Concat(s.Check).Select(c => (Shape: c, s.Items)))
            .Where(p => p.Shape.LeavesRecord)
            .DistinctBy(p => (p.Items, p.Shape.Column.Path))
            .ToList();
        foreach (var (path, items) in paths)
        {
            IReadOnlyList<JsonNode> frontier = _order.SelectMany(id => Start(_records[id], path, items)).ToList();
            var first = path.Steps[0].Step.Via == KnownStepVia.Record ? 1 : 0;
            for (var i = first; i < path.Steps.Count && frontier.Count > 0; i++)
            {
                var step = path.Steps[i];
                if (step.Step.Via == KnownStepVia.Parent)
                {
                    await ReadReferencedAsync(scope, frontier.Select(ReferenceOf).OfType<string>().ToList(), ct).ConfigureAwait(false);
                    frontier = Then(Parents(frontier), step.Step.Field);
                }
                else
                {
                    await ReadChildrenAsync(scope, step, frontier.Select(IdOf).OfType<string>().ToList(), ct).ConfigureAwait(false);
                    frontier = Then(Children(frontier, step), step.Step.Field);
                }
            }
        }
    }

    /// <summary>Where a path starts in a record: the values its first step reads there (in every item of the list a row is an item of, for a path inside it), or the record itself for a path that starts by reaching the records naming it.</summary>
    private static IReadOnlyList<JsonNode> Start(JsonNode record, KnownColumnShape path, string? items)
    {
        if (path.Steps[0].Step.Via != KnownStepVia.Record)
        {
            return [record];
        }

        return path.ReadsItem && items is not null
            ? Items(record, items).SelectMany(item => RecordValues.Select(item.Node, path.InItem!)).ToList()
            : RecordValues.Select(record, path.Steps[0].Step.Field!);
    }

    /// <summary>The values <paramref name="field"/> reads in each record, or the records themselves for a step that reads nothing.</summary>
    private static IReadOnlyList<JsonNode> Then(IReadOnlyList<JsonNode> records, string? field)
        => field is null ? records : records.SelectMany(r => RecordValues.Select(r, field)).ToList();

    /// <summary>The records the references among <paramref name="values"/> name, as read.</summary>
    private List<JsonNode> Parents(IEnumerable<JsonNode> values)
        => values.Select(ReferenceOf).OfType<string>().Select(r => _references.GetValueOrDefault(r)).OfType<JsonNode>().ToList();

    /// <summary>The records of a step's type naming <paramref name="records"/> by its reference, as read.</summary>
    private List<JsonNode> Children(IEnumerable<JsonNode> records, KnownStepShape step)
    {
        if (!_children.TryGetValue((step.Step.EntityType!, step.Step.Reference!), out var byParent))
        {
            return [];
        }

        return records.Select(IdOf).OfType<string>().SelectMany(id => byParent.GetValueOrDefault(id) ?? []).ToList();
    }

    /// <summary>Reads, a hundred at a time, the records <paramref name="ids"/> name that are not read yet; one storage does not return reads as no record.</summary>
    private async Task ReadReferencedAsync(TestScope scope, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var wanted = ids.Where(id => !_references.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        Count(wanted.Count);
        var gate = new Lock();
        await Parallel.ForEachAsync(
            wanted.Chunk(StorageRecords.Batch),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, scope.Concurrency), CancellationToken = ct },
            async (chunk, token) =>
            {
                var read = await scope.Storage.ReadAsync(chunk, null, token).ConfigureAwait(false);
                lock (gate)
                {
                    foreach (var record in read.Records)
                    {
                        if (OsduSearch.IdOf(record) is { } id && JsonNode.Parse(record.GetRawText()) is { } node)
                        {
                            _references[TargetId.WithoutVersion(id).TrimEnd(':')] = node;
                        }
                    }

                    foreach (var missing in chunk)
                    {
                        _references.TryAdd(missing, null);
                    }
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the records of a step's type naming <paramref name="parents"/> by its reference: searched for the reference
    /// starting with each id, a batch of ids at a time, read from storage, and kept by the id each names exactly.
    /// </summary>
    private async Task ReadChildrenAsync(TestScope scope, KnownStepShape step, IReadOnlyList<string> parents, CancellationToken ct)
    {
        var key = (step.Step.EntityType!, step.Step.Reference!);
        if (!_children.TryGetValue(key, out var byParent))
        {
            byParent = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
            _children[key] = byParent;
        }

        var wanted = parents.Where(p => !byParent.ContainsKey(p)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var batch in wanted.Chunk(RowsPerSearch))
        {
            foreach (var parent in batch)
            {
                byParent[parent] = [];
            }

            if (ReferenceNarrowing(step.ReferenceIndexed, batch) is not { } clause)
            {
                throw new DeliveryException(
                    $"The {step.Step.EntityType} records naming a record by {step.Step.Reference} cannot be searched for: the search index does not hold {step.Step.Reference} as a value it compares.");
            }

            _searches++;
            var ids = new List<string>();
            var query = new OsduSearchQuery { Kind = $"*:*:{step.Step.EntityType}:*", Query = clause.Text, ReturnedFields = ["id"] };
            await foreach (var page in scope.Search.PagesAsync(query, OsduSearch.MaxPage, ct).ConfigureAwait(false))
            {
                ids.AddRange(page.Hits.Select(OsduSearch.IdOf).OfType<string>());
            }

            Count(ids.Count);
            var stored = await TestEvaluator.ReadStoredAsync(scope, ids.Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
            foreach (var child in stored.Values)
            {
                foreach (var named in RecordValues.Select(child, step.Step.Reference!).Select(ReferenceOf).OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    if (byParent.TryGetValue(named, out var list) && batch.Contains(named, StringComparer.Ordinal))
                    {
                        list.Add(child);
                    }
                }
            }
        }
    }

    /// <summary>Counts records read through others, refusing past the most one assertion reads.</summary>
    private void Count(long more)
    {
        _throughOthers += more;
        if (_throughOthers > ResolvesEvaluator.MaxReferences)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The paths reach more than {ResolvesEvaluator.MaxReferences} records beyond the ones found; narrow the test's query, or locate the rows by values that find fewer records."));
        }
    }

    /// <summary>The id a value refers to, without its version or closing ':', or null when it is not a record reference.</summary>
    private static string? ReferenceOf(JsonNode value)
        => RecordValues.TryText(value, out var text) && TargetId.IsRecordReference(text) ? TargetId.WithoutVersion(text).TrimEnd(':') : null;

    /// <summary>A record's id without its version or closing ':', or null when it holds none.</summary>
    private static string? IdOf(JsonNode record)
        => record["id"] is JsonValue id && id.TryGetValue<string>(out var text) && text.Length > 0 ? TargetId.WithoutVersion(text).TrimEnd(':') : null;

    /// <summary>The items of the list <paramref name="items"/> names in a record, each with its position.</summary>
    private static IReadOnlyList<(int Position, JsonNode Node)> Items(JsonNode record, string items)
    {
        var found = new List<(int, JsonNode)>();
        foreach (var node in RecordValues.Select(record, items))
        {
            if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject item)
                    {
                        found.Add((i, item));
                    }
                }
            }
            else if (node is JsonObject single)
            {
                found.Add((0, single));
            }
        }

        return found;
    }

    // --- Judging the rows ------------------------------------------------------------------------------------------------

    private enum Verdict
    {
        Holds,

        /// <summary>One record holds the row's values, and every proof value it misses, it misses within its warning band.</summary>
        WithinBand,
        Differs,
        NotFound,
        FoundMoreThanOnce,
    }

    /// <summary>A record (or an item of it) a row's values find.</summary>
    private readonly record struct Match(string Id, JsonNode Record, JsonNode? Item, int Position);

    /// <summary>
    /// The values a full path reads from a match: through every step, from its item or its record. What a path reads of a
    /// record is kept, since every row is judged against every record read; what it reads of an item is read again.
    /// </summary>
    private IReadOnlyList<JsonNode> Values(Match match, KnownColumnShape path)
    {
        if (path.ReadsItem)
        {
            return Read(match, path);
        }

        var key = (match.Id, path.Column.Path);
        if (!_read.TryGetValue(key, out var values))
        {
            values = Read(match, path);
            _read[key] = values;
        }

        return values;
    }

    private IReadOnlyList<JsonNode> Read(Match match, KnownColumnShape path)
    {
        var first = path.Steps[0].Step;
        IReadOnlyList<JsonNode> current = first.Via != KnownStepVia.Record
            ? [match.Record]
            : path.ReadsItem
                ? match.Item is { } item ? RecordValues.Select(item, path.InItem!) : []
                : RecordValues.Select(match.Record, first.Field!);
        for (var i = first.Via == KnownStepVia.Record ? 1 : 0; i < path.Steps.Count && current.Count > 0; i++)
        {
            var step = path.Steps[i];
            current = step.Step.Via == KnownStepVia.Parent
                ? Then(Parents(current), step.Step.Field)
                : Then(Children(current, step), step.Step.Field);
        }

        return current;
    }

    /// <summary>How a value a row is located by compares: equality, with the path's ignoreCase and tolerance.</summary>
    private static ValueCondition Comparing(KnownColumn column)
        => new() { Operator = ValueOperator.EqualTo, Tolerance = column.Tolerance, IgnoreCase = column.IgnoreCase };

    private AssertionOutcome Judge(IReadOnlyList<(KnownRow Row, KnownValuesShape Shape)> rows, IReadOnlyList<Located> located)
    {
        var counts = new Dictionary<Verdict, long> { [Verdict.Holds] = 0, [Verdict.WithinBand] = 0, [Verdict.Differs] = 0, [Verdict.NotFound] = 0, [Verdict.FoundMoreThanOnce] = 0 };
        var differing = new Dictionary<string, long>(StringComparer.Ordinal);
        var failures = new List<AssertionExample>();
        long proofValues = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var (row, shape) = rows[i];
            proofValues += row.Checks.Count(c => c.Disabled is null) + row.CurveChecks.Count(c => c.Disabled is null);
            var (verdict, id, reason) = JudgeRow(row, shape, located[i], differing);
            counts[verdict]++;
            if (verdict != Verdict.Holds && failures.Count < Examples)
            {
                failures.Add(new AssertionExample(id, TestResults.Quote(row.Label), reason));
            }
        }

        var judged = rows.Count;
        var holding = counts[Verdict.Holds];
        var wrong = counts[Verdict.Differs] + counts[Verdict.FoundMoreThanOnce];
        var notFound = counts[Verdict.NotFound];
        var notFoundSeverity = assertion.NotFound ?? Assertion.Severity;
        AssertionSeverity? heaviest = wrong > 0 ? Assertion.Severity : null;
        if (notFound > 0 && (heaviest is null || notFoundSeverity > heaviest))
        {
            heaviest = notFoundSeverity;
        }

        // A row differing only within its warning band weighs a warning, or the assertion's own severity when that is milder.
        var withinBand = counts[Verdict.WithinBand];
        var bandSeverity = Severities.Milder(AssertionSeverity.Warning, Assertion.Severity);
        if (withinBand > 0 && (heaviest is null || bandSeverity > heaviest))
        {
            heaviest = bandSeverity;
        }

        var banded = rows.Any(r => r.Row.Checks.Any(c => c.Column.WarnWithin is not null) || r.Row.CurveChecks.Any(c => c.WarnWithin is not null));

        var disabledRows = assertion.Rows.Count - judged;
        var disabledValues = rows.Sum(r => r.Row.Checks.Count(c => c.Disabled is not null) + r.Row.CurveChecks.Count(c => c.Disabled is not null));
        var mostDiffering = differing.OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.Ordinal).Take(Named)
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Key} ({d.Value})")).ToList();
        var actual = string.Create(CultureInfo.InvariantCulture,
                $"{judged} row(s) with {proofValues} proof value(s): {holding} hold their values, {(banded ? $"{withinBand} within the warning band, " : string.Empty)}{counts[Verdict.Differs]} differ, {notFound} not found, {counts[Verdict.FoundMoreThanOnce]} found more than once; {_records.Count} record(s) read in {_searches} search(es)")
            + (_throughOthers > 0 ? string.Create(CultureInfo.InvariantCulture, $", {_throughOthers} reached through others") : string.Empty)
            + (_curves.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"; the curves of {_curves.Count} record(s) measured") : string.Empty)
            + (_notStored > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_notStored} record(s) the index lists are not in storage, and were left out") : string.Empty)
            + (disabledRows > 0 || disabledValues > 0 ? string.Create(CultureInfo.InvariantCulture, $"; disabled: {disabledRows} row(s), {disabledValues} proof value(s)") : string.Empty)
            + (mostDiffering.Count > 0 ? "; differing most in " + string.Join(", ", mostDiffering) : string.Empty);
        var message = string.Join("; ", new[]
        {
            counts[Verdict.Differs] > 0 ? string.Create(CultureInfo.InvariantCulture, $"{counts[Verdict.Differs]} row(s) differ from the record they find") : null,
            counts[Verdict.FoundMoreThanOnce] > 0 ? string.Create(CultureInfo.InvariantCulture, $"{counts[Verdict.FoundMoreThanOnce]} row(s) find more than one record") : null,
            notFound > 0 ? string.Create(CultureInfo.InvariantCulture, $"{notFound} row(s) find no record") + (notFoundSeverity != Assertion.Severity ? $" ({AssertionText.Of(notFoundSeverity)})" : string.Empty) : null,
            withinBand > 0 ? string.Create(CultureInfo.InvariantCulture, $"{withinBand} row(s) differ only within the warning band") + (bandSeverity != Assertion.Severity ? $" ({AssertionText.Of(bandSeverity)})" : string.Empty) : null,
        }.Where(m => m is not null)) + ".";
        var outcome = Result(heaviest is null, actual, message, judged, judged - holding, Math.Round(holding * 100.0 / judged, 3), failures);
        return heaviest is { } severity ? outcome with { Severity = AssertionText.Of(severity) } : outcome;
    }

    /// <summary>The records (or items) a row's values find, or why they find none and the record that came closest, when one did.</summary>
    private readonly record struct Located(IReadOnlyList<Match> Found, string? NotFound, string? NotFoundId);

    private (Verdict Verdict, string? Id, string Reason) JudgeRow(KnownRow row, KnownValuesShape shape, Located located, Dictionary<string, long> differing)
    {
        if (located.NotFound is { } notFound)
        {
            return (Verdict.NotFound, located.NotFoundId, notFound);
        }

        var found = located.Found;
        if (found.Count > 1)
        {
            var named = found.Take(NamedMatches).Select(m =>
            {
                var differences = Differences(row, shape, m);
                return Shown(m, shape) + (differences.Count == 0 ? " holds its values" : " differs in " + string.Join(", ", differences.Select(d => d.Path)));
            });
            return (Verdict.FoundMoreThanOnce, found[0].Id, string.Create(CultureInfo.InvariantCulture,
                $"found {found.Count} times: {string.Join("; ", named)}{(found.Count > NamedMatches ? "; ..." : string.Empty)}"));
        }

        var match = found[0];
        var differ = Differences(row, shape, match);
        if (differ.Count == 0)
        {
            return (Verdict.Holds, match.Id, string.Empty);
        }

        foreach (var (path, _, _) in differ)
        {
            differing[path] = differing.GetValueOrDefault(path) + 1;
        }

        var banded = differ.All(d => d.WithinBand);
        return (banded ? Verdict.WithinBand : Verdict.Differs, match.Id, string.Create(CultureInfo.InvariantCulture,
            $"differs{(banded ? " within the warning band" : string.Empty)} in {differ.Count} proof value(s){(shape.Items is null ? string.Empty : $" of item {match.Position} of {shape.Items}")}: {string.Join("; ", differ.Take(Named).Select(d => d.Why))}{(differ.Count > Named ? $"; and {differ.Count - Named} more" : string.Empty)}"));
    }

    /// <summary>
    /// The records a row's values find among <paramref name="all"/> read, narrowed path by path, then the items of a list a
    /// row located inside one finds; or why it finds none, at the first path no record holds its value at.
    /// </summary>
    private Located Locate(KnownRow row, KnownValuesShape shape, IReadOnlyList<Match> all)
    {
        // The records, narrowed path by path: the first path no record holds its value at is the one the row is not found by.
        IReadOnlyList<Match> found = all;
        var by = new List<string>();
        for (var k = 0; k < row.Locate.Count; k++)
        {
            var path = shape.LocateBy[k];
            if (path.ReadsItem)
            {
                continue;
            }

            var expected = row.Locate[k].Value;
            var condition = Comparing(path.Column);
            var next = found.Where(m => Values(m, path).Any(v => ValueComparer.Equal(v, expected, condition, out _))).ToList();
            if (next.Count == 0)
            {
                return new Located([], NotFoundBy(path, expected, found.Count, by), null);
            }

            found = next;
            by.Add($"{path.Column.Path} {expected}");
        }

        if (shape.Items is { } items)
        {
            found = found.SelectMany(m => Items(m.Record, items).Select(item => m with { Item = item.Node, Position = item.Position })).ToList();
            for (var k = 0; k < row.Locate.Count; k++)
            {
                var path = shape.LocateBy[k];
                if (!path.ReadsItem)
                {
                    continue;
                }

                var expected = row.Locate[k].Value;
                var condition = Comparing(path.Column);
                var next = found.Where(m => Values(m, path).Any(v => ValueComparer.Equal(v, expected, condition, out _))).ToList();
                if (next.Count == 0)
                {
                    var records = found.Select(m => m.Id).Distinct(StringComparer.Ordinal).ToList();
                    return new Located([], string.Create(CultureInfo.InvariantCulture,
                        $"not found: {Counted(records.Count, "record")} with {string.Join(" and ", by)} hold no item of {items} with {path.Column.Path} {expected}"),
                        records.Count == 1 ? records[0] : null);
                }

                found = next;
                by.Add($"{path.Column.Path} {expected}");
            }
        }

        return new Located(found, null, null);
    }

    /// <summary>
    /// The proof values of a row a match does not hold, each with what it holds instead and whether it misses only within
    /// its warning band; disabled ones are not checked.
    /// </summary>
    private List<(string Path, string Why, bool WithinBand)> Differences(KnownRow row, KnownValuesShape shape, Match match)
    {
        var differences = new List<(string, string, bool)>();
        for (var c = 0; c < row.Checks.Count; c++)
        {
            var check = row.Checks[c];
            if (check.Disabled is not null)
            {
                continue;
            }

            var path = shape.Check[c];
            var values = Values(match, path);
            if (Why(check, values, NoValueWhy(match, path)) is { } why)
            {
                differences.Add((check.Column.Label, why.Why, why.WithinBand));
            }
        }

        // The curve proof values, where the record's bulk data was measured: it is for a row finding one record, and a row
        // finding several names their record proof values alone, failing on being found more than once.
        if (row.CurveChecks.Count > 0 && _curves.TryGetValue(match.Id, out var reading))
        {
            differences.AddRange(KnownCurves.Differences(row.CurveChecks, reading));
        }

        return differences;
    }

    /// <summary>
    /// Why the values a proof value's path reads do not meet its condition, and whether they miss it only within its warning
    /// band; null when they meet it.
    /// </summary>
    private static (string Why, bool WithinBand)? Why(KnownCheck check, IReadOnlyList<JsonNode> values, string noValue)
    {
        var condition = check.Condition;
        var column = check.Column;
        var band = column.WarnWithin;
        if (column.Aggregate is { } function)
        {
            var kept = column.Except.Count == 0 ? values : values.Where(v => !column.Except.Any(e => ValueComparer.Equal(v, e, null, out _))).ToList();
            var aggregator = new Aggregator(function == AggregateFunction.Distinct);
            foreach (var value in kept)
            {
                aggregator.Add(value);
            }

            var (measured, none) = aggregator.Measure(function, 0, new Comparison(condition.Operands.Select(o => new ComparisonTerm(ComparisonOperator.EqualTo, o)).ToList()));
            if (measured is not { } taken)
            {
                return ($"{column.Label} has no value: {none}; expected it {condition}", false);
            }

            JsonNode result = taken.Number is { } number ? JsonValue.Create(number) : JsonValue.Create(taken.ToString());
            if (ValueComparer.Holds(condition, result, out _))
            {
                return null;
            }

            var differs = $"{column.Label} is {taken}, expected it {condition}";
            return band is not null && ValueComparer.Within(condition, result, band)
                ? (differs + "; " + Evaluator.WithinBand(band, ValueComparer.Miss(condition, result)), true)
                : (differs, false);
        }

        switch (condition.Operator)
        {
            case ValueOperator.Exists:
                return (values.Count > 0) == condition.Flag ? null : (condition.Flag ? $"{column.Path} has no value{noValue}" : $"{column.Path} is {Shown(values)}, expected no value", false);
            case ValueOperator.Empty when values.Count == 0:
                return condition.Flag ? null : ($"{column.Path} has no value{noValue}, expected one", false);
        }

        if (values.Count == 0)
        {
            return ($"{column.Path} has no value{noValue}, expected it {condition}", false);
        }

        var missing = values.Where(v => !ValueComparer.Holds(condition, v, out _)).ToList();
        if (column.AnyValue ? missing.Count < values.Count : missing.Count == 0)
        {
            return null;
        }

        var why = $"{column.Path} is {Shown(values)}, expected {(values.Count > 1 ? (column.AnyValue ? "one of them " : "each ") : "it ")}{condition}";

        // Within the band when every value that misses misses within it, or, for one of them, when one does.
        var withinBand = band is not null && (column.AnyValue
            ? values.Any(v => ValueComparer.Within(condition, v, band))
            : missing.All(v => ValueComparer.Within(condition, v, band)));
        return withinBand
            ? (why + "; " + Evaluator.WithinBand(band!, values.Count == 1 ? ValueComparer.Miss(condition, values[0]) : null), true)
            : (why, false);
    }

    /// <summary>Why a path through other records reads no value: the record holds no reference there, or one storage does not return.</summary>
    private string NoValueWhy(Match match, KnownColumnShape path)
    {
        if (!path.LeavesRecord || path.Steps[0].Step.Via != KnownStepVia.Record || path.Steps[1].Step.Via != KnownStepVia.Parent)
        {
            return string.Empty;
        }

        var field = path.Steps[0].Step.Field!;
        var held = path.ReadsItem
            ? match.Item is { } item ? RecordValues.Select(item, path.InItem!) : []
            : RecordValues.Select(match.Record, field);
        var references = held.Select(ReferenceOf).OfType<string>().ToList();
        return references.Count == 0
            ? $" ({field} holds no reference)"
            : references.All(r => _references.GetValueOrDefault(r) is null) ? $" ({field} names {references[0]}, which storage does not return)" : string.Empty;
    }

    /// <summary>
    /// Why a row is not found by a path, as precisely as what was read says: the records a path through others finds at its
    /// end (none, or some no record of the test reaches), or the records the row's earlier paths found.
    /// </summary>
    private string NotFoundBy(KnownColumnShape path, ExpectedValue expected, int among, IReadOnlyList<string> by)
    {
        if (path.LeavesRecord && path.Last.EntityType is { } entityType && _found.TryGetValue((path.Column.Path, expected.Text), out var reached))
        {
            var field = path.Last.Step.Field;
            return reached == 0
                ? $"not found: no {entityType} record holds {field} {expected}"
                : string.Create(CultureInfo.InvariantCulture,
                    $"not found: {Counted(reached, entityType + " record")} hold{(reached == 1 ? "s" : string.Empty)} {field} {expected}, and no record of the test{(by.Count == 0 ? string.Empty : " with " + string.Join(" and ", by))} reaches {(reached == 1 ? "it" : "one of them")} through {path.Column.Path}");
        }

        return by.Count == 0
            ? $"not found: no record of the test has {path.Column.Path} {expected}"
            : string.Create(CultureInfo.InvariantCulture, $"not found: of the {Counted(among, "record")} found with {string.Join(" and ", by)}, none has {path.Column.Path} {expected}");
    }

    /// <summary>The values a path read, as a reason quotes them beside what was expected: text in quotes, as an expected value is.</summary>
    private static string Shown(IReadOnlyList<JsonNode> values)
    {
        static string One(JsonNode value) => RecordValues.TryText(value, out var text) ? "\"" + text + "\"" : RecordValues.Text(value);
        return TestResults.Quote(values.Count == 1
            ? One(values[0])
            : "[" + string.Join(", ", values.Take(20).Select(One)) + (values.Count > 20 ? string.Create(CultureInfo.InvariantCulture, $", ... {values.Count - 20} more") : string.Empty) + "]");
    }

    private static string Shown(Match match, KnownValuesShape shape)
        => shape.Items is null || match.Item is null ? match.Id : string.Create(CultureInfo.InvariantCulture, $"{match.Id} item {match.Position} of {shape.Items}");

    private static string Counted(long count, string noun)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
}
