using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The tests of an assertion flow (osdu/docs/reference/flow/assertion.md, Tests): each test's records, partitions,
// tags and bulk read, the query a value it compares with is read from, the date its records are taken as of, and
// the hash a test's definition is known by.
internal static partial class AssertionMapper
{
    private static readonly JsonSerializerOptions HashJson = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static IReadOnlyList<AssertionTest> MapTests(
        List<AssertionTestYaml>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyDictionary<string, StagedQuery> queries,
        IReadOnlyList<string> partitions, bool followsRegistry, AssertionDefaults defaults, IReadOnlyList<AssertionDimension> dimensions, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: tests must list at least one test; an assertion flow without tests has nothing to run.");
        }

        if (declared.Count > AssertionFlowDefinition.MaxTests)
        {
            throw new FlowValidationException($"{source}: tests lists {declared.Count} tests; one flow holds at most {AssertionFlowDefinition.MaxTests}. Split them over several flows.");
        }

        var tests = new List<AssertionTest>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var test = MapTest(declared[i], i, parameters, queries, partitions, followsRegistry, defaults, dimensions, source);
            if (tests.Any(t => string.Equals(t.Name, test.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: tests[{i}] is named '{test.Name}', as an earlier test is; a run and a report name each test by its name, so each needs its own.");
            }

            tests.Add(test);
        }

        return tests;
    }

    private static AssertionTest MapTest(
        AssertionTestYaml? t, int index, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyDictionary<string, StagedQuery> queries,
        IReadOnlyList<string> partitions, bool followsRegistry, AssertionDefaults defaults, IReadOnlyList<AssertionDimension> flowDimensions, string source)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"tests[{index}]");
        if (t is null)
        {
            throw new FlowValidationException($"{source}: {at} is empty.");
        }

        var name = FlowMapper.Require(t.Name, at + ".name", source);
        if (!SelectableNames.IsName(name))
        {
            throw new FlowValidationException($"{source}: {at}.name '{name}' is not a test name: {SelectableNames.Rule}.");
        }

        var where = $"{at} '{name}'";
        var kind = FlowMapper.Require(t.Kind, at + ".kind", source);
        if (!OsduKind.IsExact(kind))
        {
            throw new FlowValidationException(kind.Contains('*', StringComparison.Ordinal)
                ? $"{source}: {where}: kind '{kind}' has wildcards; a test reads one type in one version, the one a mapping delivers "
                    + "(its template.kind), so its records, the template its fields are checked against and its lineage are that type's. "
                    + "Name the kind whole, such as osdu:wks:work-product-component--WellLog:1.4.0."
                : $"{source}: {where}: kind '{kind}' is not authority:source:entityType:major.minor.patch.");
        }

        var ids = MapIds(t.Ids, where, source);
        var query = Optional(t.Query);
        var spatial = MapSpatial(t.Spatial, where, source);
        var sort = MapSort(t.Sort, where, source);
        if (ids.Count > 0 && (query is not null || spatial is not null || sort.Count > 0))
        {
            throw new FlowValidationException(
                $"{source}: {where} reads records by ids, and names {(query is not null ? "a query" : spatial is not null ? "a spatial filter" : "a sort")} as well: a test reads the records its ids name, or the records a search finds, not both.");
        }

        foreach (var (key, text) in ids.Select(id => ("ids", id)).Prepend(("query", query ?? string.Empty)))
        {
            foreach (var token in FlowMapper.Tokens(text))
            {
                if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
                {
                    throw new FlowValidationException(
                        $"{source}: {where}: {key} uses '{{{token}}}', which is neither {{{PartitionNames.RunValue}}} nor declared under parameters.");
                }
            }
        }

        // A template names a template version (2f8a99cb38d32480), or a mapping by Name@version: the template that mapping pins.
        var template = Optional(t.Template);
        string? templateMapping = null;
        if (template is not null && template.Contains('@', StringComparison.Ordinal))
        {
            templateMapping = MappingReference(template, $"{where}: template", "the mapping whose template the test is checked against", "template: WellLog@1.5.0", source);
            template = null;
        }
        else if (template is not null && (template.Length > 64 || template.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new FlowValidationException(
                $"{source}: {where}: template '{template}' is not a template version (at most 64 characters, no whitespace), nor a mapping as Name@version, such as template: WellLog@1.5.0.");
        }

        var read = FlowMapper.ParseEnum(t.Read, defaults.Read, at + ".read", source);
        var maxRecords = t.MaxRecords ?? defaults.MaxRecords;
        CheckMaxRecords(maxRecords, at + ".maxRecords", source);
        var indexSettle = t.IndexSettleSeconds ?? defaults.IndexSettleSeconds;
        CheckIndexSettle(indexSettle, at + ".indexSettleSeconds", source);
        if (ids.Count > maxRecords)
        {
            throw new FlowValidationException($"{source}: {where} names {ids.Count} ids and reads at most {maxRecords} records; raise maxRecords or name fewer ids.");
        }

        var severity = FlowMapper.ParseEnum(t.Severity, AssertionSeverity.Error, at + ".severity", source);
        var bulk = MapBulk(t.Bulk, kind, where, source);
        var context = new TestContext(where, source, kind, severity, ids.Count > 0, read, sort.Count > 0, bulk is not null, Optional(t.AsOf), parameters, queries);
        var assertions = MapAssertions(t.Assert, context)
            .Select((a, i) => AssertionOperands.Map(a, v => QueryOf(v, string.Create(CultureInfo.InvariantCulture, $"{where}: assert[{i}]"), context)))
            .ToList();
        var test = new AssertionTest
        {
            Name = name,
            Description = Optional(t.Description),
            Title = TitleOf(t.Title, where + ": title", source),
            Dimensions = MergeDimensions(flowDimensions, MapDimensions(t.Dimensions, where + ": dimensions", source)),
            Tags = MapTags(t.Tags, where, source),
            Partitions = MapTestPartitions(t.Partitions, partitions, followsRegistry, where, source),
            Severity = severity,
            Kind = kind,
            Template = template,
            TemplateMapping = templateMapping,
            Query = query,
            Ids = ids,
            Spatial = spatial,
            Sort = sort,
            Read = read,
            MaxRecords = maxRecords,
            Sample = t.Sample ?? false,
            IndexSettleSeconds = indexSettle,
            Bulk = bulk is null ? null : bulk with { Columns = BulkColumns(bulk, assertions, where, source) },
            Disabled = DisabledOf(t.Disabled, where, source),
            AsOf = DateOf(t.AsOf, where + ": asOf", source),
            ValidUntil = ValidUntilOf(t.AsOf, t.ValidUntil, where, source),
            ValidFor = ValidForOf(t.AsOf, t.ValidUntil, t.ValidFor, where, source),
            AsOfField = AsOfFieldOf(t.AsOf, t.AsOfField, ids.Count > 0, where, source),
            Assertions = assertions,
        };
        return test with { DefinitionHash = Hash(test) };
    }

    /// <summary>
    /// What a test's bulk read reads: the columns the document lists, or when it lists none, the columns its assertions name
    /// (empty, which reads every column, when none does). The columns the assertions name are always read
    /// (osdu/docs/reference/flow/assertion.md, Bulk data), so a list that leaves one out is refused here rather than leaving
    /// an assertion to read a column the read never asked for.
    /// </summary>
    private static IReadOnlyList<string> BulkColumns(AssertionBulk bulk, IReadOnlyList<TestAssertion> assertions, string where, string source)
    {
        var named = new List<(string Column, int Assertion)>();
        for (var i = 0; i < assertions.Count; i++)
        {
            var columns = assertions[i] switch
            {
                ValueAssertion { Target.IsColumn: true } value => value.Where.Select(w => w.Target.Path).Prepend(value.Target.Path),
                AggregateAssertion { Target.IsColumn: true } aggregate => [aggregate.Target.Path],
                MonotonicAssertion monotonic => [monotonic.Column],
                KnownValuesAssertion known => known.Rows.SelectMany(r => r.CurveChecks).Select(c => c.Curve)
                    .Concat(known.CurveIndex is null ? [] : [known.CurveIndex]),
                _ => [],
            };
            foreach (var column in columns)
            {
                if (!named.Exists(n => string.Equals(n.Column, column, StringComparison.Ordinal)))
                {
                    named.Add((column, i));
                }
            }
        }

        if (bulk.Columns.Count == 0)
        {
            return named.Select(n => n.Column).ToList();
        }

        var unread = named.Where(n => !bulk.Columns.Contains(n.Column, StringComparer.Ordinal)).ToList();
        if (unread.Count > 0)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{source}: {where}: bulk.columns lists the only columns the test reads, and leaves out {string.Join(", ", unread.Select(u => $"'{u.Column}' (assert[{u.Assertion}])"))}; add {(unread.Count == 1 ? "it" : "them")} to bulk.columns, or leave bulk.columns out to read exactly the columns the assertions name."));
        }

        return bulk.Columns;
    }

    private static string Hash(AssertionTest test)
    {
        var node = JsonSerializer.SerializeToNode(test with { DefinitionHash = string.Empty }, HashJson);
        return Hashing.ContentHash.Of(CanonicalJson.ToBytes(node))[..16];
    }

    private static IReadOnlyList<string> MapTags(List<string>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count > MaxTags)
        {
            throw new FlowValidationException($"{source}: {where}: tags lists {declared.Count} tags; a test carries at most {MaxTags}.");
        }

        var tags = new List<string>(declared.Count);
        foreach (var raw in declared)
        {
            var tag = raw?.Trim() ?? string.Empty;
            if (!SelectableNames.IsName(tag))
            {
                throw new FlowValidationException($"{source}: {where}: tag '{Shown(tag)}' is not a tag: {SelectableNames.Rule}.");
            }

            if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"{source}: {where}: tags names '{tag}' more than once.");
            }

            tags.Add(tag);
        }

        return tags;
    }

    private static IReadOnlyList<string> MapTestPartitions(List<string>? declared, IReadOnlyList<string> partitions, bool followsRegistry, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (partitions.Count == 0 && !followsRegistry)
        {
            throw new FlowValidationException(
                $"{source}: {where}: partitions narrows the partitions of its flow, and the flow's one partition is its header's; name the partitions under 'partitions' at the top of the document, or take the header out so the flow tests every registered partition.");
        }

        var names = new List<string>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: partitions[{i}]");
            var name = PartitionNames.Check(declared[i], at);
            names.Add(followsRegistry
                ? name
                : partitions.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FlowValidationException($"{at} '{name}' is not a partition of the flow; it names {PartitionNames.Listed(partitions)}."));
        }

        PartitionNames.CheckList(names, $"{source}: {where}: partitions");
        return names;
    }

    private static AssertionBulk? MapBulk(AssertionBulkYaml? b, string kind, string where, string source)
    {
        if (b is null)
        {
            return null;
        }

        var entityType = OsduKind.EntityType(kind);
        var collection = entityType is null
            ? null
            : DdmsCatalog.WellboreDdmsCollections.FirstOrDefault(c => c.Bulk && string.Equals(c.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        if (collection is null)
        {
            throw new FlowValidationException(
                $"{source}: {where} reads bulk data, and its kind '{kind}' is not one the Wellbore DDMS keeps bulk data for; it serves {string.Join(", ", DdmsCatalog.WellboreDdmsCollections.Where(c => c.Bulk).Select(c => c.EntityType))}.");
        }

        var columns = new List<string>();
        foreach (var raw in b.Columns ?? [])
        {
            var column = CheckColumn(raw, where + ": bulk.columns", source);
            if (columns.Contains(column, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {where}: bulk.columns names '{column}' more than once.");
            }

            columns.Add(column);
        }

        var maxRows = b.MaxRows ?? AssertionBulk.DefaultMaxRows;
        if (maxRows is < 1 or > AssertionBulk.MaxRowsCeiling)
        {
            throw new FlowValidationException($"{source}: {where}: bulk.maxRows must be between 1 and {AssertionBulk.MaxRowsCeiling.ToString("N0", CultureInfo.InvariantCulture)}.");
        }

        return new AssertionBulk { Collection = collection.Segment, Columns = columns, MaxRows = maxRows };
    }

    /// <summary>
    /// A value an assertion compares with, its query read in: a query named by the flow's <c>queries</c> as the flow declares it
    /// (refused when it declares none of that name), and one written in place checked as one is (one read-only SELECT, its
    /// tokens the flow's parameters or {partition}). Any other value as it is.
    /// </summary>
    private static ExpectedValue QueryOf(ExpectedValue value, string at, TestContext test)
    {
        if (value.Kind != ExpectedValueKind.Query || value.Query is not { } query)
        {
            return value;
        }

        if (query.Name is { } name)
        {
            return test.Queries.TryGetValue(name, out var declared)
                ? ExpectedValue.OfQuery(declared)
                : throw new FlowValidationException(test.Queries.Count == 0
                    ? $"{test.Source}: {at} names the query '{name}', and the flow declares no queries; declare it under queries, with its connection and sql."
                    : $"{test.Source}: {at} names the query '{name}', which the flow does not declare under queries; it declares {string.Join(", ", test.Queries.Keys.Order(StringComparer.Ordinal))}.");
        }

        Engine.Assertions.StagedSql.Check(query.Sql, test.Parameters, $"{test.Source}: {at}: the query's sql");
        return value;
    }

    /// <summary>
    /// The date property a record holds on or before asOf to be among the test's records: createTime, modifyTime, or a path
    /// into its data (whose type the template check holds to a date); refused without an asOf, and for a test of ids.
    /// </summary>
    private static string? AsOfFieldOf(string? asOf, string? declared, bool byIds, string where, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var path = ConditionReader.RecordPath(declared, where + ".asOfField", source);
        if (path is not ("createTime" or "modifyTime") && !path.StartsWith("data.", StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: {where}.asOfField '{Shown(path)}' is not a date every record carries: createTime (when OSDU created the record), modifyTime, or a date property of its data.");
        }

        if (asOf is null)
        {
            throw new FlowValidationException($"{source}: {where}.asOfField compares records with the day the values were stated, and no asOf says which; give asOf.");
        }

        if (byIds)
        {
            throw new FlowValidationException($"{source}: {where}.asOfField narrows the records a search finds, and the test names its records by id.");
        }

        return path;
    }
}
