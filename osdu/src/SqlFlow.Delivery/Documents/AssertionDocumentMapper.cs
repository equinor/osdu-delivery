using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a parsed assertion document onto <see cref="AssertionFlowDefinition"/> and validates what YAML cannot
/// (osdu/docs/reference/flow/assertion.md, Assertions): every assertion names exactly one subject, carries only the
/// keys that subject takes, and every operand is of the type its operator compares. What needs the flow's templates
/// (whether a path is a property of the kind, whether a value fits the property's type) is checked when a run or a plan
/// reads them.
/// </summary>
internal static partial class AssertionMapper
{
    /// <summary>The most assertions one test holds.</summary>
    public const int MaxAssertions = 100;

    /// <summary>The most tags one test carries.</summary>
    public const int MaxTags = 20;

    /// <summary>The most ids a test reads by id.</summary>
    public const int MaxIds = 1000;

    /// <summary>The most values an <c>in</c> or <c>notIn</c> lists.</summary>
    public const int MaxListed = ConditionReader.MaxListed;

    /// <summary>The most rows a record set expects.</summary>
    public const int MaxRows = 1000;

    /// <summary>The most columns a record set compares, and the most fields one unique assertion combines.</summary>
    public const int MaxColumns = 20;

    /// <summary>The most conditions one <c>where</c> holds.</summary>
    public const int MaxConditions = 10;

    /// <summary>The longest label an assertion is shown under.</summary>
    public const int MaxLabel = 200;

    /// <summary>How long a <c>matches</c> expression may take on one value before it is refused as a failure to evaluate.</summary>
    public static readonly TimeSpan PatternTimeout = ConditionReader.PatternTimeout;

    /// <summary>The properties an OSDU record has at its root, the only places a field path may start.</summary>
    public static readonly IReadOnlySet<string> RecordRoots = ConditionReader.RecordRoots;

    private static readonly JsonSerializerOptions HashJson = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AssertionFlowDefinition Map(AssertionYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var headers = new Dictionary<string, string>(src.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        var partitions = MapPartitions(y.Partitions, source);
        if (partitions.Count > 0)
        {
            if (headers.ContainsKey(FlowMapper.PartitionHeader))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers names '{FlowMapper.PartitionHeader}', and the flow names its partitions: every run sets the header to the partition it tests. Remove the header.");
            }
        }
        else if (headers.TryGetValue(FlowMapper.PartitionHeader, out var partition))
        {
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers.{FlowMapper.PartitionHeader} is empty. Name the partition the flow tests, or take the header out so the flow tests every partition registered with the catalog.");
            }

            _ = CacheScope.Normalize(partition, $"{source}: source.headers");
        }

        var followsRegistry = partitions.Count == 0 && !headers.ContainsKey(FlowMapper.PartitionHeader);
        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new FlowParameter { Required = kv.Value.Required, Default = kv.Value.Default, Description = kv.Value.Description },
            StringComparer.Ordinal);
        if (parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', which an assertion flow keeps for the partition a run tests: {{partition}} in a test is always that partition. Rename the parameter.");
        }

        var defaults = MapDefaults(y.Defaults, source);
        var flow = new AssertionFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            Parameters = parameters,
            Source = new AssertionSource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
                QueryPath = ServicePath(src.QueryPath, AssertionSource.DefaultQueryPath, "source.queryPath", source),
                SearchPath = ServicePath(src.SearchPath, AssertionSource.DefaultSearchPath, "source.searchPath", source),
                Paging = FlowMapper.ParseEnum(src.Paging, SearchPaging.Cursor, "source.paging", source),
                RecordQueryPath = ServicePath(src.RecordQueryPath, AssertionSource.DefaultRecordQueryPath, "source.recordQueryPath", source),
                LegalPath = ServicePath(src.LegalPath, AssertionSource.DefaultLegalPath, "source.legalPath", source),
                DdmsRoot = ServicePath(src.DdmsRoot, AssertionSource.DefaultDdmsRoot, "source.ddmsRoot", source),
            },
            Partitions = partitions,
            FollowsRegistry = followsRegistry,
            Defaults = defaults,
            FailRunOn = FlowMapper.ParseEnum(y.FailRunOn, FailRunOn.Error, "failRunOn", source),
            Tests = MapTests(y.Tests, parameters, partitions, followsRegistry, defaults, source),
            Reliability = FlowMapper.MapReliability(y.Reliability, "an assertion flow", takesRecordsAtOnce: true, source),
            Parallelism = FlowMapper.MapRunParallelism(y.Parallelism, "the tests evaluated at once, and the requests one test has in flight", source),
        };

        return flow;
    }

    private static IReadOnlyList<string> MapPartitions(IReadOnlyList<string>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        var names = declared.Select((n, i) => PartitionNames.Check(n, string.Create(CultureInfo.InvariantCulture, $"{source}: partitions[{i}]"))).ToList();
        PartitionNames.CheckList(names, $"{source}: partitions");
        return names;
    }

    private static string ServicePath(string? declared, string fallback, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return fallback;
        }

        var path = declared.Trim();
        if (!path.StartsWith('/') || path.Any(char.IsWhiteSpace) || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: {key} '{path}' must be a path under the endpoint, starting with '/', with no whitespace, query or fragment.");
        }

        return path.TrimEnd('/').Length == 0 ? path : path.TrimEnd('/');
    }

    private static AssertionDefaults MapDefaults(AssertionDefaultsYaml? d, string source)
    {
        if (d is null)
        {
            return new AssertionDefaults();
        }

        var defaults = new AssertionDefaults
        {
            MaxRecords = d.MaxRecords ?? AssertionDefaults.DefaultMaxRecords,
            Examples = d.Examples ?? AssertionDefaults.DefaultExamples,
            Read = FlowMapper.ParseEnum(d.Read, AssertionRead.Storage, "defaults.read", source),
            IndexSettleSeconds = d.IndexSettleSeconds ?? AssertionDefaults.DefaultIndexSettleSeconds,
        };
        CheckMaxRecords(defaults.MaxRecords, "defaults.maxRecords", source);
        CheckIndexSettle(defaults.IndexSettleSeconds, "defaults.indexSettleSeconds", source);
        if (defaults.Examples is < 1 or > AssertionDefaults.MaxExamples)
        {
            throw new FlowValidationException($"{source}: defaults.examples must be between 1 and {AssertionDefaults.MaxExamples}.");
        }

        return defaults;
    }

    private static void CheckIndexSettle(int value, string key, string source)
    {
        if (value is < 0 or > AssertionDefaults.MaxIndexSettleSeconds)
        {
            throw new FlowValidationException(
                $"{source}: {key} must be between 0 and {AssertionDefaults.MaxIndexSettleSeconds} seconds: how long the search index is given to list a change before a test of the changed type is judged, 0 judging it whatever changed.");
        }
    }

    private static void CheckMaxRecords(int value, string key, string source)
    {
        if (value is < 1 or > AssertionDefaults.MaxRecordsCeiling)
        {
            throw new FlowValidationException(
                $"{source}: {key} must be between 1 and {AssertionDefaults.MaxRecordsCeiling.ToString("N0", CultureInfo.InvariantCulture)}; a test reads its records into bounded memory, never a partition whole.");
        }
    }

    private static IReadOnlyList<AssertionTest> MapTests(
        List<AssertionTestYaml>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyList<string> partitions, bool followsRegistry,
        AssertionDefaults defaults, string source)
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
            var test = MapTest(declared[i], i, parameters, partitions, followsRegistry, defaults, source);
            if (tests.Any(t => string.Equals(t.Name, test.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: tests[{i}] is named '{test.Name}', as an earlier test is; a run and a report name each test by its name, so each needs its own.");
            }

            tests.Add(test);
        }

        return tests;
    }

    private static AssertionTest MapTest(
        AssertionTestYaml? t, int index, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyList<string> partitions, bool followsRegistry,
        AssertionDefaults defaults, string source)
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
        var context = new TestContext(where, source, kind, severity, ids.Count > 0, read, sort.Count > 0, bulk is not null, Optional(t.AsOf));
        var assertions = MapAssertions(t.Assert, context);
        var test = new AssertionTest
        {
            Name = name,
            Description = Optional(t.Description),
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

    private static IReadOnlyList<string> MapIds(List<string>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count == 0 || declared.Count > MaxIds)
        {
            throw new FlowValidationException($"{source}: {where}: ids lists between 1 and {MaxIds} record ids.");
        }

        var ids = new List<string>(declared.Count);
        foreach (var raw in declared)
        {
            var id = raw?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Length > 1024 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new FlowValidationException($"{source}: {where}: ids holds '{Shown(id)}', which is not a record id (1 to 1024 characters, no whitespace).");
            }

            if (ids.Contains(id, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {where}: ids names '{Shown(id)}' more than once.");
            }

            ids.Add(id);
        }

        return ids;
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

    private static IReadOnlyList<AssertionSort> MapSort(List<AssertionSortYaml>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count is 0 or > 5)
        {
            throw new FlowValidationException($"{source}: {where}: sort lists between 1 and 5 fields.");
        }

        var sort = new List<AssertionSort>(declared.Count);
        foreach (var s in declared)
        {
            var field = ConditionReader.RecordPath(FlowMapper.Require(s?.Field, where + ".sort[].field", source), where + ": sort", source);
            var order = s!.Order?.Trim().ToLowerInvariant() ?? "asc";
            if (order is not ("asc" or "desc"))
            {
                throw new FlowValidationException($"{source}: {where}: sort order '{s.Order}' is not asc or desc.");
            }

            sort.Add(new AssertionSort(field, order == "desc"));
        }

        return sort;
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
    /// The search's spatial filter as written (openapi search v2, <c>SpatialFilter</c>): a geo field, and exactly one of the
    /// filter types, each point with its latitude and longitude in range.
    /// </summary>
    private static JsonObject? MapSpatial(object? declared, string where, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var at = where + ": spatial";
        if (ToJson(declared, at, source) is not JsonObject spatial)
        {
            throw new FlowValidationException($"{source}: {at} must be a mapping: a field and one filter (byBoundingBox, byDistance, byGeoPolygon, byIntersection, byWithinPolygon).");
        }

        string[] filters = ["byBoundingBox", "byDistance", "byGeoPolygon", "byIntersection", "byWithinPolygon"];
        foreach (var (key, _) in spatial)
        {
            if (key != "field" && !filters.Contains(key, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at} has '{key}', which is not field or one of {string.Join(", ", filters)}.");
            }
        }

        if (spatial["field"] is not JsonValue fieldValue || !fieldValue.TryGetValue<string>(out var field) || string.IsNullOrWhiteSpace(field))
        {
            throw new FlowValidationException($"{source}: {at}.field is required: the geo field the filter applies to, such as data.SpatialLocation.Wgs84Coordinates.");
        }

        _ = ConditionReader.RecordPath(field, at + ".field", source);
        var named = filters.Where(f => spatial.ContainsKey(f)).ToList();
        if (named.Count != 1)
        {
            throw new FlowValidationException($"{source}: {at} names {(named.Count == 0 ? "no filter" : string.Join(" and ", named))}; the search takes exactly one of {string.Join(", ", filters)}.");
        }

        var filter = spatial[named[0]] as JsonObject
            ?? throw new FlowValidationException($"{source}: {at}.{named[0]} must be a mapping.");
        switch (named[0])
        {
            case "byBoundingBox":
                Point(filter["topLeft"], $"{at}.byBoundingBox.topLeft", source);
                Point(filter["bottomRight"], $"{at}.byBoundingBox.bottomRight", source);
                Only(filter, ["topLeft", "bottomRight"], $"{at}.byBoundingBox", source);
                break;
            case "byDistance":
                Point(filter["point"], $"{at}.byDistance.point", source);
                if (!IsNumber(filter["distance"], out var distance) || distance < 0)
                {
                    throw new FlowValidationException($"{source}: {at}.byDistance.distance must be a distance in metres, zero or more.");
                }

                Only(filter, ["point", "distance"], $"{at}.byDistance", source);
                break;
            case "byIntersection":
                if (filter["polygons"] is not JsonArray polygons || polygons.Count == 0)
                {
                    throw new FlowValidationException($"{source}: {at}.byIntersection.polygons must list at least one polygon.");
                }

                for (var i = 0; i < polygons.Count; i++)
                {
                    var polygonAt = string.Create(CultureInfo.InvariantCulture, $"{at}.byIntersection.polygons[{i}]");
                    var polygon = polygons[i] as JsonObject ?? throw new FlowValidationException($"{source}: {polygonAt} must be a mapping with points.");
                    Points(polygon["points"], polygonAt + ".points", source);
                    Only(polygon, ["points"], polygonAt, source);
                }

                Only(filter, ["polygons"], $"{at}.byIntersection", source);
                break;
            default:
                Points(filter["points"], $"{at}.{named[0]}.points", source);
                Only(filter, ["points"], $"{at}.{named[0]}", source);
                break;
        }

        return spatial;
    }

    /// <summary>
    /// A finite number the document wrote, whatever YAML read it as: <c>72</c> arrives as a whole number and <c>72.5</c> as a
    /// fraction, and each is read from the JSON it writes, so a whole-number latitude or distance counts as surely as a decimal.
    /// </summary>
    private static bool IsNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue number
            && number.GetValueKind() == JsonValueKind.Number
            && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static void Only(JsonObject node, string[] keys, string at, string source)
    {
        foreach (var (key, _) in node)
        {
            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at} has '{key}', which is not one of {string.Join(", ", keys)}.");
            }
        }
    }

    private static void Points(JsonNode? node, string at, string source)
    {
        if (node is not JsonArray points || points.Count == 0)
        {
            throw new FlowValidationException($"{source}: {at} must list at least one point.");
        }

        for (var i = 0; i < points.Count; i++)
        {
            Point(points[i], string.Create(CultureInfo.InvariantCulture, $"{at}[{i}]"), source);
        }
    }

    private static void Point(JsonNode? node, string at, string source)
    {
        if (node is not JsonObject point
            || !IsNumber(point["latitude"], out var latitude)
            || !IsNumber(point["longitude"], out var longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new FlowValidationException($"{source}: {at} must be a point: latitude between -90 and 90, longitude between -180 and 180.");
        }

        Only(point, ["latitude", "longitude"], at, source);
    }

    private sealed record TestContext(
        string Where, string Source, string Kind, AssertionSeverity Severity, bool ByIds, AssertionRead Read, bool Sorted, bool Bulk, string? AsOf);

    private static IReadOnlyList<TestAssertion> MapAssertions(List<AssertionItemYaml>? declared, TestContext test)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {test.Where}: assert must list at least one assertion; a test without one asserts nothing.");
        }

        if (declared.Count > MaxAssertions)
        {
            throw new FlowValidationException($"{test.Source}: {test.Where}: assert lists {declared.Count} assertions; a test holds at most {MaxAssertions}. Split it into several tests.");
        }

        var assertions = new List<TestAssertion>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{test.Where}: assert[{i}]");
            var item = declared[i] ?? throw new FlowValidationException($"{test.Source}: {at} is empty.");
            assertions.Add(MapAssertion(item, at, test));
        }

        return assertions;
    }

    /// <summary>The keys an assertion item sets, by the names the document writes them with.</summary>
    private static List<string> KeysOf(AssertionConditionYaml y)
    {
        var keys = new List<string>();
        void Add(bool present, string key)
        {
            if (present)
            {
                keys.Add(key);
            }
        }

        Add(y.Field is not null, "field");
        Add(y.Column is not null, "column");
        foreach (var (key, _) in OperatorsOf(y))
        {
            keys.Add(key);
        }

        Add(y.IgnoreCase is not null, "ignoreCase");
        Add(y.Tolerance is not null, "tolerance");
        if (y is AssertionItemYaml item)
        {
            Add(item.Count is not null, "count");
            Add(item.Aggregate is not null, "aggregate");
            Add(item.Unique is not null, "unique");
            Add(item.GroupBy is not null, "groupBy");
            Add(item.Groups is not null, "groups");
            Add(item.Absent is not null, "absent");
            Add(item.Mode is not null, "mode");
            Add(item.GroupCount is not null, "groupCount");
            Add(item.RecordSet is not null, "recordSet");
            Add(item.KnownValues is not null, "knownValues");
            Add(item.Except is not null, "except");
            Add(item.WarnWithin is not null, "warnWithin");
            Add(item.Conforms is not null, "conforms");
            Add(item.Mapping is not null, "mapping");
            Add(item.Indexed is not null, "indexed");
            Add(item.Legal is not null, "legal");
            Add(item.Delivered is not null, "delivered");
            Add(item.Interface is not null, "interface");
            Add(item.Exact is not null, "exact");
            Add(item.RowCount is not null, "rowCount");
            Add(item.Columns is not null, "columns");
            Add(item.Monotonic is not null, "monotonic");
            Add(item.For is not null, "for");
            Add(item.Values is not null, "values");
            Add(item.Optional is not null, "optional");
            Add(item.Disabled is not null, "disabled");
            Add(item.AsOf is not null, "asOf");
            Add(item.ValidUntil is not null, "validUntil");
            Add(item.ValidFor is not null, "validFor");
            Add(item.Where is not null, "where");
        }

        return keys;
    }

    /// <summary>The operators a condition sets, each with its operand as the document writes it.</summary>
    private static List<(string Key, object Operand)> OperatorsOf(AssertionConditionYaml y)
    {
        var operators = new List<(string, object)>();
        void Add(object? operand, string key)
        {
            if (operand is not null)
            {
                operators.Add((key, operand));
            }
        }

        Add(y.EqualTo, "equals");
        Add(y.NotEquals, "notEquals");
        Add(y.In, "in");
        Add(y.NotIn, "notIn");
        Add(y.AtLeast, "atLeast");
        Add(y.AtMost, "atMost");
        Add(y.GreaterThan, "greaterThan");
        Add(y.LessThan, "lessThan");
        Add(y.Between, "between");
        Add(y.Matches, "matches");
        Add(y.NotMatches, "notMatches");
        Add(y.StartsWith, "startsWith");
        Add(y.EndsWith, "endsWith");
        Add(y.Contains, "contains");
        Add(y.NotContains, "notContains");
        Add(y.Exists, "exists");
        Add(y.Empty, "empty");
        Add(y.Type, "type");
        Add(y.Length, "length");
        Add(y.Resolves, "resolves");
        return operators;
    }

    private static readonly string[] Common = ["name", "description", "severity", "disabled", "asOf", "validUntil", "validFor"];

    private static TestAssertion MapAssertion(AssertionItemYaml y, string at, TestContext test)
    {
        var subjects = new List<string>();
        foreach (var (present, key) in new (bool, string)[]
                 {
                     (y.Count is not null, "count"), (y.Aggregate is not null, "aggregate"), (y.Monotonic is not null, "monotonic"),
                     (y.Unique is not null, "unique"), (y.GroupBy is not null, "groupBy"), (y.RecordSet is not null, "recordSet"),
                     (y.Conforms is not null, "conforms"), (y.Indexed is not null, "indexed"), (y.Legal is not null, "legal"),
                     (y.Delivered is not null, "delivered"), (y.RowCount is not null, "rowCount"), (y.Columns is not null, "columns"),
                     (y.Mapping is not null, "mapping"), (y.KnownValues is not null, "knownValues"),
                 })
        {
            if (present)
            {
                subjects.Add(key);
            }
        }

        if (subjects.Count == 0)
        {
            if (y.Field is not null && y.Column is not null)
            {
                throw new FlowValidationException($"{test.Source}: {at} names both a field and a column; an assertion looks at a field of each record or a column of its bulk data, not both.");
            }

            if (y.Field is not null)
            {
                subjects.Add("field");
            }
            else if (y.Column is not null)
            {
                subjects.Add("column");
            }
        }

        if (subjects.Count != 1)
        {
            throw new FlowValidationException(subjects.Count == 0
                ? $"{test.Source}: {at} names no subject; an assertion is one of count, field, column, aggregate, unique, groupBy, recordSet, knownValues, conforms, mapping, indexed, legal, delivered, rowCount, columns or monotonic."
                : $"{test.Source}: {at} names {string.Join(" and ", subjects)}; an assertion has one subject. Write one assertion for each.");
        }

        var subject = subjects[0];
        var severity = FlowMapper.ParseEnum(y.Severity, test.Severity, at + ".severity", test.Source);
        var description = Optional(y.Description);
        TestAssertion assertion = subject switch
        {
            "count" => MapCount(y, at, test),
            "field" or "column" => MapValue(y, subject == "column", at, test),
            "aggregate" => MapAggregate(y, at, test),
            "unique" => MapUnique(y, at, test),
            "groupBy" => MapGroup(y, at, test),
            "recordSet" => MapRecordSet(y, at, test),
            "knownValues" => MapKnownValues(y, at, test),
            "conforms" => MapConforms(y, at, test),
            "mapping" => MapMapping(y, at, test),
            "indexed" => MapIndexed(y, at, test),
            "legal" => MapLegal(y, at, test),
            "delivered" => MapDelivered(y, at, test),
            "rowCount" => MapRowCount(y, at, test),
            "columns" => MapColumns(y, at, test),
            _ => MapMonotonic(y, at, test),
        };

        var name = Optional(y.Name);
        if (name is not null && name.Length > MaxLabel)
        {
            throw new FlowValidationException($"{test.Source}: {at}.name is longer than {MaxLabel} characters.");
        }

        return assertion with
        {
            Label = name ?? Clip(assertion.Label, MaxLabel),
            Description = description,
            Severity = severity,
            Disabled = DisabledOf(y.Disabled, at, test.Source),
            AsOf = DateOf(y.AsOf, at + ".asOf", test.Source),
            ValidUntil = ValidUntilOf(y.AsOf ?? test.AsOf, y.ValidUntil, at, test.Source),
            ValidFor = ValidForOf(y.AsOf ?? test.AsOf, y.ValidUntil, y.ValidFor, at, test.Source),
        };
    }

    /// <summary>Refuses every key of <paramref name="y"/> that an assertion of <paramref name="subject"/> does not take.</summary>
    private static void OnlyKeys(AssertionConditionYaml y, string subject, IEnumerable<string> allowed, string at, TestContext test)
    {
        var accepted = new HashSet<string>(allowed.Concat(Common), StringComparer.Ordinal);
        var stray = KeysOf(y).Where(k => !accepted.Contains(k)).ToList();
        if (stray.Count > 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} is {Article(subject)} {subject} assertion, which does not take {string.Join(", ", stray)}.");
        }
    }

    private static string Article(string subject) => subject.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(subject[0]), StringComparison.Ordinal) ? "an" : "a";

    private static CountAssertion MapCount(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "count", ["count", "warnWithin"], at, test);
        var comparison = ConditionReader.CountComparison(y.Count!, at + ".count", test.Source);
        var band = ConditionReader.Band(y.WarnWithin, at, test.Source);
        if (band is not null)
        {
            ConditionReader.CheckBand(band, null, comparison, at, test.Source);
        }

        return new CountAssertion(comparison) { WarnWithin = band, Label = $"count {comparison}" };
    }

    private static RowCountAssertion MapRowCount(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "rowCount", ["rowCount"], at, test);
        RequireBulk("rowCount", at, test);
        var comparison = ConditionReader.CountComparison(y.RowCount!, at + ".rowCount", test.Source);
        return new RowCountAssertion(comparison) { Label = $"rowCount {comparison}" };
    }

    private static ConformsAssertion MapConforms(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "conforms", ["conforms"], at, test);
        RequireTrue(y.Conforms, "conforms", at, test);
        if (test.Read == AssertionRead.Index)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} asserts that every record meets its template, which needs each record as storage holds it; the test reads the index (read: index), which holds only what it indexed. Take read: index out.");
        }

        return new ConformsAssertion { Label = "conforms to its template" };
    }

    /// <summary>
    /// A <c>mapping</c> assertion: the mapping, as <c>Name@version</c>, whose record-stage assertions every record read is held
    /// to. It reads each record whole, as storage holds it; what the index holds of a record leaves out what an assertion
    /// may read.
    /// </summary>
    private static MappingRulesAssertion MapMapping(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "mapping", ["mapping"], at, test);
        var reference = MappingReference(y.Mapping!, at + ".mapping", "the mapping whose assertions the records are held to", "mapping: WellLog@1.4.0", test.Source);

        if (test.Read == AssertionRead.Index)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} holds every record to what mapping {reference} asserts, which needs each record as storage holds it; the test reads the index (read: index), which holds only what it indexed. Take read: index out.");
        }

        return new MappingRulesAssertion(reference) { Label = $"meets what mapping {reference} asserts" };
    }

    /// <summary>A mapping as a document names it, <c>Name@version</c>: a name and a version, no path, no whitespace.</summary>
    private static string MappingReference(string declared, string at, string what, string example, string source)
    {
        var reference = declared.Trim();
        var split = reference.IndexOf('@', StringComparison.Ordinal);
        if (split <= 0 || split == reference.Length - 1 || reference.IndexOf('@', split + 1) >= 0
            || reference.Contains('/', StringComparison.Ordinal) || reference.Contains('\\', StringComparison.Ordinal) || reference.Contains("..", StringComparison.Ordinal)
            || reference.Any(char.IsWhiteSpace))
        {
            throw new FlowValidationException($"{source}: {at} '{Shown(reference)}' names {what} as Name@version, such as {example}.");
        }

        return reference;
    }

    private static IndexedAssertion MapIndexed(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "indexed", ["indexed"], at, test);
        RequireTrue(y.Indexed, "indexed", at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException($"{test.Source}: {at} asks the search index how it indexed the records, and the test reads its records by id; give it a query instead.");
        }

        return new IndexedAssertion { Label = "indexed cleanly" };
    }

    private static void RequireTrue(bool? flag, string key, string at, TestContext test)
    {
        if (flag != true)
        {
            throw new FlowValidationException($"{test.Source}: {at}.{key} is written 'true'; to stop asserting it, remove the assertion.");
        }
    }

    private static LegalAssertion MapLegal(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "legal", ["legal"], at, test);
        if (!string.Equals(y.Legal?.Trim(), "valid", StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{test.Source}: {at}.legal is written 'valid': every legal tag the records carry has to be valid.");
        }

        return new LegalAssertion { Label = "legal tags valid" };
    }

    private static DeliveredAssertion MapDelivered(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "delivered", ["delivered", "interface", "exact"], at, test);
        var flow = y.Delivered!.Trim();
        if (flow.Length == 0 || flow.Length > FlowDefinition.MaxLedgerNameLength || flow.Any(char.IsControl))
        {
            throw new FlowValidationException($"{test.Source}: {at}.delivered names the delivery flow whose ledger is read: 1 to {FlowDefinition.MaxLedgerNameLength} characters.");
        }

        var named = Optional(y.Interface);
        if (named is not null && !SourceDefinition.IsInterfaceName(named))
        {
            throw new FlowValidationException($"{test.Source}: {at}.interface '{named}' is not an interface name: a letter followed by letters, digits, '_' and '-'.");
        }

        return new DeliveredAssertion
        {
            Flow = flow,
            Interface = named,
            Exact = y.Exact ?? false,
            Label = $"delivered by {flow}{(named is null ? string.Empty : "/" + named)}",
        };
    }

    private static ColumnsAssertion MapColumns(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "columns", ["columns"], at, test);
        RequireBulk("columns", at, test);
        var c = y.Columns!;
        IReadOnlyList<string> List(List<string>? declared, string key)
        {
            var list = new List<string>();
            foreach (var raw in declared ?? [])
            {
                var column = CheckColumn(raw, $"{at}.columns.{key}", test.Source);
                if (!list.Contains(column, StringComparer.Ordinal))
                {
                    list.Add(column);
                }
            }

            return list;
        }

        var assertion = new ColumnsAssertion
        {
            Includes = List(c.Includes, "includes"),
            Excludes = List(c.Excludes, "excludes"),
            Exactly = List(c.Exactly, "equals"),
            Label = "columns",
        };
        if (assertion.Includes.Count + assertion.Excludes.Count + assertion.Exactly.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.columns names no column: list them under includes, excludes or equals.");
        }

        var both = assertion.Includes.Concat(assertion.Exactly).Intersect(assertion.Excludes, StringComparer.Ordinal).ToList();
        if (both.Count > 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.columns both expects and excludes {string.Join(", ", both)}.");
        }

        return assertion with { Label = "columns " + assertion.Expected };
    }

    private static MonotonicAssertion MapMonotonic(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "monotonic", ["monotonic", "column"], at, test);
        RequireBulk("monotonic", at, test);
        var column = CheckColumn(y.Column ?? throw new FlowValidationException($"{test.Source}: {at} asserts an order, and names no column to read it in."), at + ".column", test.Source);
        var direction = FlowMapper.ParseEnum<MonotonicDirection>(y.Monotonic!.Trim(), at + ".monotonic", test.Source);
        return new MonotonicAssertion(column, direction) { Label = $"{column} {AssertionText.Of(direction)}" };
    }

    private static UniqueAssertion MapUnique(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "unique", ["unique"], at, test);
        var fields = new List<string>();
        foreach (var raw in y.Unique!)
        {
            var path = ConditionReader.RecordPath(raw, at + ".unique", test.Source);
            if (fields.Contains(path, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{test.Source}: {at}.unique names '{path}' more than once.");
            }

            fields.Add(path);
        }

        if (fields.Count is 0 or > MaxColumns)
        {
            throw new FlowValidationException($"{test.Source}: {at}.unique lists between 1 and {MaxColumns} fields whose values, together, no two records may share.");
        }

        return new UniqueAssertion(fields) { Label = "unique " + string.Join(" + ", fields) };
    }

    private static GroupAssertion MapGroup(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "groupBy", ["groupBy", "groups", "absent", "mode", "groupCount"], at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException($"{test.Source}: {at} groups what the search index holds, and the test reads its records by id; give it a query instead.");
        }

        var field = ConditionReader.RecordPath(y.GroupBy!, at + ".groupBy", test.Source);
        var groups = new List<ExpectedGroup>();
        foreach (var (key, value) in y.Groups ?? [])
        {
            var group = key?.Trim() ?? string.Empty;
            if (group.Length == 0)
            {
                throw new FlowValidationException($"{test.Source}: {at}.groups names a group with an empty key.");
            }

            groups.Add(new ExpectedGroup(group, ConditionReader.CountComparison(value, $"{at}.groups.{group}", test.Source)));
        }

        var absent = new List<string>();
        foreach (var raw in y.Absent ?? [])
        {
            var group = raw?.Trim() ?? string.Empty;
            if (group.Length == 0 || absent.Contains(group, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{test.Source}: {at}.absent lists an empty group, or one group twice.");
            }

            absent.Add(group);
        }

        var clash = groups.Select(g => g.Key).Intersect(absent, StringComparer.Ordinal).ToList();
        if (clash.Count > 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} both expects and excludes the group {string.Join(", ", clash)}.");
        }

        var mode = y.Mode?.Trim().ToLowerInvariant();
        if (mode is not (null or "includes" or "exact"))
        {
            throw new FlowValidationException($"{test.Source}: {at}.mode '{y.Mode}' is not includes or exact.");
        }

        if (mode is not null && groups.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.mode says how the groups listed under groups compare with the ones found, and groups lists none.");
        }

        var groupCount = y.GroupCount is null ? null : ConditionReader.CountComparison(y.GroupCount, at + ".groupCount", test.Source);
        if (groups.Count == 0 && absent.Count == 0 && groupCount is null)
        {
            throw new FlowValidationException($"{test.Source}: {at} groups by {field} and expects nothing of the groups: name them under groups or absent, or bound their number with groupCount.");
        }

        return new GroupAssertion
        {
            Field = field,
            Groups = groups,
            Exact = mode == "exact",
            Absent = absent,
            GroupCount = groupCount,
            Label = $"groups of {field}",
        };
    }

    private static RecordSetAssertion MapRecordSet(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "recordSet", ["recordSet"], at, test);
        var set = y.RecordSet!;
        var columns = new List<string>();
        foreach (var raw in set.Columns ?? [])
        {
            columns.Add(ConditionReader.RecordPath(raw, at + ".recordSet.columns", test.Source));
        }

        if (columns.Count is 0 or > MaxColumns)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.columns lists between 1 and {MaxColumns} fields each record is projected onto.");
        }

        var mode = FlowMapper.ParseEnum(set.Mode, RecordSetMode.Exact, at + ".recordSet.mode", test.Source);
        if (mode == RecordSetMode.Ordered && !test.Sorted)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} compares the records in order, and the test names no sort; the search returns records in no particular order without one. Give the test a sort.");
        }

        if (set.Rows is null || set.Rows.Count > MaxRows)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.rows lists at most {MaxRows} rows, each a list of {columns.Count} value(s).");
        }

        var rows = new List<IReadOnlyList<ExpectedValue?>>(set.Rows.Count);
        for (var i = 0; i < set.Rows.Count; i++)
        {
            var rowAt = string.Create(CultureInfo.InvariantCulture, $"{at}.recordSet.rows[{i}]");
            var row = set.Rows[i] ?? throw new FlowValidationException($"{test.Source}: {rowAt} is empty.");
            if (row.Count != columns.Count)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} holds {row.Count} value(s) for {columns.Count} column(s).");
            }

            rows.Add(row.Select(cell => cell is null ? null : ConditionReader.Scalar(cell, rowAt, test.Source)).ToList());
        }

        if (rows.Count == 0 && mode is RecordSetMode.Includes or RecordSetMode.Excludes)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.rows lists no row, so '{AssertionText.Of(mode)}' has nothing to compare; list the rows, or use mode exact to assert that no record matches.");
        }

        return new RecordSetAssertion
        {
            Columns = columns,
            Mode = mode,
            Rows = rows,
            Label = $"record set of {string.Join(", ", columns)}",
        };
    }

    /// <summary>
    /// Known values (osdu/docs/reference/flow/assertion.md, Known values): rows of proof values, each a full path and the
    /// value (or condition) the record a row finds holds there, and the full paths and values the row finds its record by.
    /// What the assertion lists under <c>locateBy</c> and <c>check</c> every row takes, beside its own; with no rows, those
    /// are the one row. What a path is in the kind's schema (a property, a reference, a list of objects) is checked against the
    /// templates when a run reads them; here every path is read, every row is located by something, every value suits its
    /// comparison, and no two rows are located alike.
    /// </summary>
    private static KnownValuesAssertion MapKnownValues(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "knownValues", ["knownValues"], at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} finds its records by the values its rows give, and the test names its records by id; give the test a query (or none) instead of ids.");
        }

        var known = y.KnownValues!;
        var block = at + ".knownValues";
        var tolerance = ConditionReader.Tolerance(known.Tolerance, block, test.Source);
        var band = ConditionReader.Band(known.WarnWithin, block, test.Source);
        var ignoreCase = known.IgnoreCase ?? false;
        var sharedLocate = ReadLocate(known.LocateBy, ignoreCase, block + ".locateBy", test);
        var sharedChecks = ReadChecks(known.Check, tolerance, band, ignoreCase, block + ".check", test);
        var declared = known.Rows is { Count: > 0 } listed ? listed : [new AssertionKnownRowYaml()];
        if (declared.Count > MaxRows)
        {
            throw new FlowValidationException($"{test.Source}: {block}.rows lists {declared.Count} rows; known values hold at most {MaxRows}. Split them over several assertions.");
        }

        var rows = new List<KnownRow>(declared.Count);
        var located = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++)
        {
            var rowAt = known.Rows is { Count: > 0 } ? string.Create(CultureInfo.InvariantCulture, $"{block}.rows[{i}]") : block;
            var row = declared[i] ?? throw new FlowValidationException($"{test.Source}: {rowAt} is empty.");
            var locate = Joined(sharedLocate, ReadLocate(row.LocateBy, ignoreCase, rowAt + ".locateBy", test), l => l.Column.Path, "is located by", rowAt, test);
            var checks = Joined(sharedChecks, ReadChecks(row.Check, tolerance, band, ignoreCase, rowAt + ".check", test), c => c.Column.Label, "checks", rowAt, test);
            if (locate.Count is 0 or > MaxLocatingPaths)
            {
                throw new FlowValidationException(locate.Count == 0
                    ? $"{test.Source}: {rowAt} is located by nothing; give it locateBy, the full paths and values its record holds, such as data.Name: COMPOSITE."
                    : $"{test.Source}: {rowAt} is located by {locate.Count} paths; a row is located by at most {MaxLocatingPaths}.");
            }

            if (checks.Count > MaxProofValues)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} checks {checks.Count} proof values; a row checks at most {MaxProofValues}. Split it over two rows of their own assertions.");
            }

            var clash = locate.Select(l => l.Column.Path).Intersect(checks.Where(c => c.Column.Aggregate is null).Select(c => c.Column.Path), StringComparer.Ordinal).FirstOrDefault();
            if (clash is not null)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} is located by {clash} and checks it as well; a value a row is found by is the record's already.");
            }

            var identity = string.Join("\u001f", locate.OrderBy(l => l.Column.Path, StringComparer.Ordinal)
                .Select(l => l.Column.Path + "=" + (l.Column.IgnoreCase ? l.Value.Text.ToUpperInvariant() : l.Value.Text)));
            if (located.TryGetValue(identity, out var earlier))
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"{test.Source}: {rowAt} is located as rows[{earlier}] is ({string.Join(" / ", locate.Select(l => l.Value.Text))}); a record is held to one row of proof values, so locate each once."));
            }

            located[identity] = i;
            rows.Add(new KnownRow(locate, checks, DisabledOf(row.Disabled, rowAt, test.Source)));
        }

        var notFound = known.NotFound is null ? (AssertionSeverity?)null : FlowMapper.ParseEnum<AssertionSeverity>(known.NotFound.Trim(), block + ".notFound", test.Source);
        return new KnownValuesAssertion
        {
            Rows = rows,
            NotFound = notFound,
            Label = string.Create(CultureInfo.InvariantCulture, $"known values of {rows.Count} row(s)"),
        };
    }

    /// <summary>The most full paths a row of known values is located by.</summary>
    public const int MaxLocatingPaths = 8;

    /// <summary>The most proof values one row of known values checks.</summary>
    public const int MaxProofValues = 60;

    /// <summary>The comparisons a proof value holds the values a record holds to.</summary>
    private static readonly string[] CheckComparisons =
    [
        "equals", "notEquals", "in", "notIn", "atLeast", "atMost", "greaterThan", "lessThan", "between", "matches", "notMatches",
        "startsWith", "endsWith", "contains", "notContains", "exists", "empty", "type", "length",
    ];

    /// <summary>What the assertion lists for every row, followed by what the row lists itself; a path both list is refused.</summary>
    private static List<T> Joined<T>(IReadOnlyList<T> shared, IReadOnlyList<T> own, Func<T, string> path, string verb, string at, TestContext test)
    {
        var twice = own.Select(path).Intersect(shared.Select(path), StringComparer.Ordinal).FirstOrDefault();
        if (twice is not null)
        {
            throw new FlowValidationException($"{test.Source}: {at} {verb} {twice}, which the assertion lists for every row already; give it in one place.");
        }

        return [.. shared, .. own];
    }

    /// <summary>An ISO 8601 date or date and time as the document writes it (<c>2026-10-13</c>), or null when it gives none.</summary>
    private static string? DateOf(string? declared, string at, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var text = declared.Trim();
        return Engine.Assertions.RecordValues.TryInstant(text, out _)
            ? text
            : throw new FlowValidationException($"{source}: {at} '{Shown(text)}' is not an ISO 8601 date, such as 2026-10-13 or 2026-10-13T12:00:00Z.");
    }

    /// <summary>The last day what is expected holds, refused when it comes before the day it was stated.</summary>
    private static string? ValidUntilOf(string? asOf, string? validUntil, string at, string source)
    {
        var until = DateOf(validUntil, at + ".validUntil", source);
        if (until is not null && DateOf(asOf, at + ".asOf", source) is { } stated
            && Engine.Assertions.RecordValues.TryInstant(stated, out var from) && AssertionActivity.Until(until) is { } last && last < from)
        {
            throw new FlowValidationException($"{source}: {at}.validUntil {until} comes before asOf {stated}; values hold from when they are stated.");
        }

        return until;
    }

    /// <summary>
    /// How long after asOf what is expected holds (<c>90d</c>, <c>12w</c>, <c>6m</c>, <c>1y</c>), refused without an asOf to count
    /// from, or beside a validUntil that says it already.
    /// </summary>
    private static string? ValidForOf(string? asOf, string? validUntil, string? validFor, string at, string source)
    {
        if (validFor is null)
        {
            return null;
        }

        var period = validFor.Trim();
        if (!AssertionActivity.TryPeriod(period, out _, out _))
        {
            throw new FlowValidationException(
                $"{source}: {at}.validFor '{Shown(period)}' is not a period: a whole number of days, weeks, months or years, such as 90d, 12w, 6m or 1y.");
        }

        if (validUntil is not null)
        {
            throw new FlowValidationException($"{source}: {at} gives validUntil and validFor; values hold until a day, or for a period after asOf, so give one.");
        }

        if (asOf is null)
        {
            throw new FlowValidationException($"{source}: {at}.validFor counts from the day the values were stated, and no asOf says which; give asOf.");
        }

        return period;
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

    /// <summary>Why something is disabled, as the document writes it: null when it is not, and refused when it says nothing.</summary>
    private static string? DisabledOf(object? declared, string at, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var why = (declared as string)?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > MaxLabel)
        {
            throw new FlowValidationException(
                $"{source}: {at}.disabled says why it is disabled, in at most {MaxLabel} characters, such as disabled: \"OSDU holds no LogSource for these logs yet\".");
        }

        return why;
    }

    /// <summary>The full paths and values a row is located by: each value a scalar, or a mapping of equals with its ignoreCase and tolerance.</summary>
    private static IReadOnlyList<KnownLocate> ReadLocate(Dictionary<string, object?>? declared, bool ignoreCase, string at, TestContext test)
    {
        var locate = new List<KnownLocate>();
        foreach (var (written, raw) in declared ?? [])
        {
            var pathAt = $"{at}.{written}";
            var column = ReadKnownPath(written, pathAt, test) with { IgnoreCase = ignoreCase };
            ExpectedValue value;
            if (raw is IDictionary map)
            {
                object? equals = null;
                var given = false;
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    switch (entry.Key as string)
                    {
                        case "equals":
                            equals = entry.Value;
                            given = true;
                            break;
                        case "ignoreCase":
                            column = column with { IgnoreCase = Flag(entry.Value, pathAt + ".ignoreCase", test) };
                            break;
                        case "tolerance":
                            column = column with { Tolerance = ConditionReader.Tolerance(entry.Value, pathAt, test.Source) };
                            break;
                        default:
                            throw new FlowValidationException(
                                $"{test.Source}: {pathAt} has '{entry.Key}'; a row is located by values its record equals: a value, or a mapping of equals, ignoreCase and tolerance.");
                    }
                }

                if (!given)
                {
                    throw new FlowValidationException($"{test.Source}: {pathAt} gives no value; write it, or a mapping with equals.");
                }

                value = ConditionReader.Scalar(equals, pathAt, test.Source);
            }
            else
            {
                value = ConditionReader.Scalar(raw, pathAt, test.Source);
            }

            if (value.Kind == ExpectedValueKind.Null)
            {
                throw new FlowValidationException($"{test.Source}: {pathAt} gives no value; a row is found by the values its record holds, so each is given.");
            }

            locate.Add(new KnownLocate(column, value));
        }

        return locate;
    }

    /// <summary>
    /// The proof values a row holds its record to: each a full path and a value it equals, ~ for no value at all, or a
    /// condition written as a mapping (one comparison, with tolerance, warnWithin, ignoreCase, values, an aggregate and its
    /// except, and disabled), as a field assertion's condition is written. The assertion's warning band serves every proof
    /// value it can widen that gives none itself.
    /// </summary>
    private static IReadOnlyList<KnownCheck> ReadChecks(
        Dictionary<string, object?>? declared, double? tolerance, WarningBand? band, bool ignoreCase, string at, TestContext test)
    {
        var checks = new List<KnownCheck>();
        foreach (var (written, raw) in declared ?? [])
        {
            var pathAt = $"{at}.{written}";
            var column = ReadKnownPath(written, pathAt, test);
            if (raw is not IDictionary map)
            {
                var condition = raw is null
                    ? new ValueCondition { Operator = ValueOperator.Exists, Flag = false }
                    : Proof([("equals", raw)], null, null, tolerance, ignoreCase, pathAt, test);
                checks.Add(new KnownCheck(column with { WarnWithin = Shared(band, condition) }, condition));
                continue;
            }

            var operators = new List<(string Key, object Operand)>();
            object? columnTolerance = null;
            object? columnBand = null;
            bool? columnIgnoreCase = null;
            string? values = null;
            string? aggregate = null;
            List<object>? except = null;
            object? disabled = null;
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var key = entry.Key as string ?? string.Empty;
                switch (key)
                {
                    case "tolerance":
                        columnTolerance = entry.Value;
                        break;
                    case "warnWithin":
                        columnBand = entry.Value ?? throw new FlowValidationException($"{test.Source}: {pathAt}.warnWithin has no value.");
                        break;
                    case "ignoreCase":
                        columnIgnoreCase = Flag(entry.Value, pathAt + ".ignoreCase", test);
                        break;
                    case "values":
                        values = entry.Value as string ?? throw new FlowValidationException($"{test.Source}: {pathAt}.values is all or any.");
                        break;
                    case "aggregate":
                        aggregate = entry.Value as string ?? throw new FlowValidationException($"{test.Source}: {pathAt}.aggregate names an aggregate, such as count.");
                        break;
                    case "except":
                        except = (entry.Value as IList)?.Cast<object>().ToList()
                            ?? throw new FlowValidationException($"{test.Source}: {pathAt}.except lists the values the aggregate leaves out.");
                        break;
                    case "disabled":
                        disabled = entry.Value ?? string.Empty;
                        break;
                    default:
                        if (!CheckComparisons.Contains(key, StringComparer.Ordinal))
                        {
                            throw new FlowValidationException(
                                $"{test.Source}: {pathAt} has '{entry.Key}'; a proof value is a value, or a condition: one of {string.Join(", ", CheckComparisons)}, with tolerance, warnWithin, ignoreCase, values, aggregate, except and disabled.");
                        }

                        if (entry.Value is not null)
                        {
                            operators.Add((key, entry.Value));
                        }

                        break;
                }
            }

            var function = aggregate is null ? (AggregateFunction?)null : FlowMapper.ParseEnum<AggregateFunction>(aggregate.Trim(), pathAt + ".aggregate", test.Source);
            if (function == AggregateFunction.Missing)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt}.aggregate missing counts records without a value, and a proof value reads the values of one record; hold its count to 0 instead.");
            }

            if (function is not null && operators.Any(o => !ConditionReader.ComparisonKeys.Contains(o.Key, StringComparer.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt} compares {AssertionText.Of(function.Value)}({column.Path}), a number, so it takes one of {string.Join(", ", ConditionReader.ComparisonKeys)}.");
            }

            var condition2 = Proof(operators, columnIgnoreCase, columnTolerance, tolerance, ignoreCase, pathAt, test);
            if (function is { } taken)
            {
                var dated = taken is AggregateFunction.Min or AggregateFunction.Max;
                if (condition2.Operands.Any(o => o.Kind != ExpectedValueKind.Number && !(dated && o.Kind == ExpectedValueKind.Text && Engine.Assertions.RecordValues.TryInstant(o.Text, out _))))
                {
                    throw new FlowValidationException(
                        $"{test.Source}: {pathAt} compares {AssertionText.Of(taken)}({column.Path}), which is a number{(dated ? " or a date" : string.Empty)}; give a number{(dated ? " or an ISO 8601 date" : string.Empty)}.");
                }
            }

            if (function is not null && values is not null)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt} aggregates its values and says which of them are held to the condition; an aggregate is taken of them all. Write two proof values.");
            }

            if (except is not null && function is null)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {pathAt}.except leaves values out of an aggregate, and the proof value aggregates nothing; give it an aggregate, such as count.");
            }

            var anyValue = (values?.Trim().ToLowerInvariant()) switch
            {
                null or "all" => false,
                "any" => true,
                _ => throw new FlowValidationException($"{test.Source}: {pathAt}.values '{values}' is all or any."),
            };
            var own = ConditionReader.Band(columnBand, pathAt, test.Source);
            if (own is not null)
            {
                ConditionReader.CheckBand(own, condition2.Tolerance, [AssertionText.Of(condition2.Operator)], condition2.Operands, pathAt, test.Source);
            }

            checks.Add(new KnownCheck(
                column with
                {
                    AnyValue = anyValue,
                    Aggregate = function,
                    Except = except is null ? [] : MapExcept(except, pathAt, test),
                    WarnWithin = own ?? Shared(band, condition2),
                },
                condition2,
                DisabledOf(disabled, pathAt, test.Source)));
        }

        return checks;
    }

    /// <summary>
    /// The condition a proof value states: its comparison with the settings it writes itself, and the assertion's tolerance and
    /// ignoreCase where it writes none and they fit (a tolerance where a number is compared, ignoreCase where text is), so the
    /// assertion's settings serve its numbers and its text alike.
    /// </summary>
    private static ValueCondition Proof(
        IReadOnlyList<(string Key, object Operand)> operators, bool? ignoreCase, object? tolerance, double? sharedTolerance, bool sharedIgnoreCase, string at, TestContext test)
    {
        var condition = ConditionReader.Read(operators, ignoreCase, tolerance, ResolvesRefused, at, test.Source);
        var takesTolerance = tolerance is null && sharedTolerance is not null
            && condition.Operator is ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn or ValueOperator.Between
                or ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan
            && condition.Operands.Any(o => o.Kind == ExpectedValueKind.Number);
        var takesIgnoreCase = ignoreCase is null && sharedIgnoreCase
            && condition.Operator is ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn or ValueOperator.Matches
                or ValueOperator.NotMatches or ValueOperator.StartsWith or ValueOperator.EndsWith or ValueOperator.Contains or ValueOperator.NotContains
            && condition.Operands.Any(o => o.Kind == ExpectedValueKind.Text);
        return takesTolerance || takesIgnoreCase
            ? ConditionReader.Read(operators, takesIgnoreCase ? true : ignoreCase, takesTolerance ? sharedTolerance : tolerance, ResolvesRefused, at, test.Source)
            : condition;
    }

    /// <summary>The assertion's warning band for a proof value giving none: where it can widen the proof value's comparison, else none.</summary>
    private static WarningBand? Shared(WarningBand? band, ValueCondition condition)
        => band is not null && ConditionReader.Widens(band, condition) ? band : null;

    /// <summary>Why <c>resolves</c> is not a proof value's comparison.</summary>
    private const string ResolvesRefused = "does not apply to a proof value; read the record a reference names with a list of paths instead, such as '[data.WellboreID, data.FacilityName]'.";

    private static bool Flag(object? declared, string at, TestContext test) => declared switch
    {
        bool flag => flag,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => throw new FlowValidationException($"{test.Source}: {at} is true or false."),
    };

    /// <summary>
    /// A full path of known values. A path in the OSDU record, from its root (<c>data.Name</c>, <c>tags.SourceProject</c>), as
    /// the assertion's field, a mapping's assertions and a dimension read a record; or a list of paths, read step by step as
    /// a dimension's label and attributes are (osdu/docs/reference/flow/dimension-attributes.md, Labels and attributes): each path but
    /// the last reads the reference the next records are found by, and the last reads the value
    /// (<c>[data.WellboreID, data.FacilityName]</c>, the name of the wellbore a log belongs to). An entry
    /// <c>group--Entity[reference]</c> steps the other way, to the records of that type whose reference names the record
    /// reached so far (<c>[work-product-component--WellLog[data.WellboreID], data.Name]</c>, the names of a wellbore's
    /// logs). A path names properties: the item of a list a row reads is the one its locating values find, never one by
    /// its position.
    /// </summary>
    private static KnownColumn ReadKnownPath(string written, string at, TestContext test)
    {
        var text = written.Trim();
        if (!text.StartsWith('['))
        {
            if (ChildrenPrefix().IsMatch(text))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {at} '{Shown(text)}' steps to the records naming one outside a list of paths; write the step and the path read in them as a list, such as '[work-product-component--WellLog[data.WellboreID], data.Name]'.");
            }

            var path = PathOf(text, written, at, test);
            return new KnownColumn { Path = path, Steps = [new KnownStep(KnownStepVia.Record, path)] };
        }

        var entries = StepEntries(text, written, at, test);
        if (entries.Count > KnownColumn.MaxSteps)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{test.Source}: {at} lists {entries.Count} paths; a list reads through at most {KnownColumn.MaxSteps}: the record's own and three more to the records it names or that name it."));
        }

        var steps = new List<KnownStep>();
        var afterChildren = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (ChildrenStep().Match(entry) is { Success: true } children)
            {
                if (i == entries.Count - 1)
                {
                    throw new FlowValidationException(
                        $"{test.Source}: {at} ends at the {children.Groups["type"].Value} records naming the record, and reads nothing of them; end the list with the path read in them, such as '[{entry}, data.Name]'.");
                }

                if (i > 0 && !afterChildren)
                {
                    // The record the reference before names, whose children the step reaches.
                    steps.Add(new KnownStep(KnownStepVia.Parent, null));
                }

                steps.Add(new KnownStep(KnownStepVia.Children, null, children.Groups["type"].Value, PathOf(children.Groups["reference"].Value, written, at, test)));
                afterChildren = true;
                continue;
            }

            var path = PathOf(entry, written, at, test);
            if (afterChildren)
            {
                // The path read in the records the step before reached.
                steps[^1] = steps[^1] with { Field = path };
                afterChildren = false;
                continue;
            }

            steps.Add(new KnownStep(i == 0 ? KnownStepVia.Record : KnownStepVia.Parent, path));
        }

        // A list of one path is that path, so it is written, compared and reported as the path itself.
        return new KnownColumn { Path = entries.Count == 1 ? steps[0].Field! : "[" + string.Join(", ", entries) + "]", Steps = steps };
    }

    /// <summary>
    /// The entries of a list of paths as a key writes it, <c>[data.WellboreID, data.FacilityName]</c>: separated by commas
    /// outside brackets, each optionally quoted as the same list in a dimension is; one entry is the path itself.
    /// </summary>
    private static List<string> StepEntries(string text, string written, string at, TestContext test)
    {
        if (!text.EndsWith(']'))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' opens a list of paths it does not close; write it as '[data.WellboreID, data.FacilityName]'.");
        }

        var entries = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        char? quote = null;
        foreach (var c in text[1..^1])
        {
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"' when current.ToString().Trim().Length == 0:
                    quote = c;
                    continue;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    entries.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }

            if (depth < 0)
            {
                break;
            }

            current.Append(c);
        }

        entries.Add(current.ToString().Trim());
        if (depth != 0 || quote is not null)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' has a bracket or a quote that does not pair up; write the list as '[data.WellboreID, data.FacilityName]'.");
        }

        if (entries.Any(e => e.Length == 0))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' has an empty entry; write a path between each two commas.");
        }

        return entries;
    }

    /// <summary>A path a step reads: a record path from a root property, with no item by its position, its [*] steps implied.</summary>
    private static string PathOf(string part, string written, string at, TestContext test)
    {
        var path = ConditionReader.RecordPath(part, at, test.Source);
        if (PositionStep().IsMatch(path))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' names an item of a list by its position; a row reads the item its locating values find, so name the property ({PositionStep().Replace(path, string.Empty)}).");
        }

        return path.Replace("[*]", string.Empty, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\[\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex PositionStep();

    /// <summary>An entry stepping to the records naming one: <c>group--Entity[reference]</c>.</summary>
    [GeneratedRegex(@"^(?<type>[A-Za-z0-9_.-]+--[A-Za-z0-9_.]+)\[(?<reference>[^\]]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ChildrenStep();

    /// <summary>The start of such an entry, as a path that is not a list may begin with one by mistake.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+--[A-Za-z0-9_.]+\[", RegexOptions.CultureInvariant)]
    private static partial Regex ChildrenPrefix();

    /// <summary>The values an aggregate leaves out: 1 to <see cref="MaxListed"/> scalars, none of them null, which is no value already.</summary>
    private static IReadOnlyList<ExpectedValue> MapExcept(List<object>? declared, string at, TestContext test)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count is 0 or > MaxListed)
        {
            throw new FlowValidationException($"{test.Source}: {at}.except lists between 1 and {MaxListed} values the aggregate leaves out.");
        }

        var values = new List<ExpectedValue>(declared.Count);
        foreach (var raw in declared)
        {
            var value = ConditionReader.Scalar(raw, at + ".except", test.Source);
            if (value.Kind == ExpectedValueKind.Null)
            {
                throw new FlowValidationException($"{test.Source}: {at}.except lists null; an aggregate already leaves out what has no value.");
            }

            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static AggregateAssertion MapAggregate(AssertionItemYaml y, string at, TestContext test)
    {
        var isColumn = y.Column is not null;
        OnlyKeys(y, "aggregate", ConditionReader.ComparisonKeys.Append("aggregate").Append(isColumn ? "column" : "field").Append("tolerance").Append("except").Append("warnWithin"), at, test);
        var function = FlowMapper.ParseEnum<AggregateFunction>(y.Aggregate!.Trim(), at + ".aggregate", test.Source);
        ValueTarget target;
        if (isColumn)
        {
            RequireBulk("column aggregate", at, test);
            target = new ValueTarget(CheckColumn(y.Column!, at + ".column", test.Source), IsColumn: true);
        }
        else
        {
            var field = y.Field ?? throw new FlowValidationException($"{test.Source}: {at} aggregates, and names no field (or column) to aggregate.");
            target = new ValueTarget(ConditionReader.RecordPath(field, at + ".field", test.Source), IsColumn: false);
        }

        // Min and max compare dates as well as numbers; every other aggregate is a number.
        var numbersOnly = function is not (AggregateFunction.Min or AggregateFunction.Max);
        var terms = OperatorsOf(y).Select(o => ConditionReader.Term(o.Key, o.Operand, $"{at}.{o.Key}", test.Source, numbersOnly)).ToList();
        if (terms.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} aggregates {target} and compares it with nothing: give it one of {string.Join(", ", ConditionReader.ComparisonKeys)}.");
        }

        var tolerance = ConditionReader.Tolerance(y.Tolerance, at, test.Source);
        if (tolerance is not null && terms.All(t => t.Value.Kind != ExpectedValueKind.Number))
        {
            throw new FlowValidationException($"{test.Source}: {at}.tolerance applies to comparisons of numbers, and the aggregate is compared with a date.");
        }

        var band = ConditionReader.Band(y.WarnWithin, at, test.Source);
        if (band is not null)
        {
            ConditionReader.CheckBand(band, tolerance, new Comparison(terms), at, test.Source);
        }

        var assertion = new AggregateAssertion
        {
            Function = function,
            Target = target,
            Comparison = new Comparison(terms),
            Tolerance = tolerance,
            WarnWithin = band,
            Except = MapExcept(y.Except, at, test),
            Label = string.Empty,
        };
        return assertion with { Label = $"{AssertionText.Of(function)}({target}) {assertion.Expected}" };
    }

    private static ValueAssertion MapValue(AssertionItemYaml y, bool isColumn, string at, TestContext test)
    {
        var subject = isColumn ? "column" : "field";
        var takes = OperatorsOf(y).Select(o => o.Key).ToList();
        takes.AddRange([subject, "ignoreCase", "tolerance", "for", "optional", "where"]);
        if (!isColumn)
        {
            takes.Add("values");
        }

        OnlyKeys(y, subject, takes, at, test);
        ValueTarget target;
        if (isColumn)
        {
            RequireBulk("column", at, test);
            target = new ValueTarget(CheckColumn(y.Column!, at + ".column", test.Source), IsColumn: true);
        }
        else
        {
            target = new ValueTarget(ConditionReader.RecordPath(y.Field!, at + ".field", test.Source), IsColumn: false);
        }

        var condition = MapCondition(y, isColumn, at, test);
        var quantifier = MapQuantifier(y.For, at, test.Source);
        var anyValue = (y.Values?.Trim().ToLowerInvariant()) switch
        {
            null or "all" => false,
            "any" => true,
            _ => throw new FlowValidationException($"{test.Source}: {at}.values '{y.Values}' is not all or any."),
        };
        if (condition.Operator == ValueOperator.Resolves && (y.For is not null || y.Values is not null))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} asserts that references resolve, which holds for every reference the records carry; for and values do not apply.");
        }

        var where = new List<ValueFilter>();
        foreach (var (filter, i) in (y.Where ?? []).Select((c, i) => (c, i)))
        {
            var whereAt = string.Create(CultureInfo.InvariantCulture, $"{at}.where[{i}]");
            if (filter is null)
            {
                throw new FlowValidationException($"{test.Source}: {whereAt} is empty.");
            }

            where.Add(MapFilter(filter, isColumn, whereAt, test));
        }

        if (y.Where is { Count: 0 or > MaxConditions })
        {
            throw new FlowValidationException($"{test.Source}: {at}.where lists between 1 and {MaxConditions} conditions.");
        }

        var assertion = new ValueAssertion
        {
            Target = target,
            Condition = condition,
            For = quantifier,
            AnyValue = anyValue,
            Optional = y.Optional ?? false,
            Where = where,
            Label = string.Empty,
        };
        var label = $"{target} {condition}"
            + (quantifier.Mode == QuantifierMode.All ? string.Empty : $" for {quantifier}")
            + (where.Count == 0 ? string.Empty : " where " + string.Join(" and ", where));
        return assertion with { Label = label };
    }

    private static ValueFilter MapFilter(AssertionConditionYaml y, bool isColumn, string at, TestContext test)
    {
        var subject = isColumn ? "column" : "field";
        var keys = KeysOf(y);
        var allowed = OperatorsOf(y).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        allowed.UnionWith([subject, "ignoreCase", "tolerance"]);
        var stray = keys.Where(k => !allowed.Contains(k)).ToList();
        if (stray.Count > 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} selects the {(isColumn ? "rows" : "records")} the assertion looks at by a {subject}, and does not take {string.Join(", ", stray)}.");
        }

        var target = isColumn
            ? new ValueTarget(CheckColumn(y.Column ?? throw new FlowValidationException($"{test.Source}: {at} names no column; a condition on a column assertion selects rows by a column."), at + ".column", test.Source), IsColumn: true)
            : new ValueTarget(ConditionReader.RecordPath(y.Field ?? throw new FlowValidationException($"{test.Source}: {at} names no field; a condition on a field assertion selects records by a field."), at + ".field", test.Source), IsColumn: false);
        var condition = MapCondition(y, isColumn, at, test);
        if (condition.Operator == ValueOperator.Resolves)
        {
            throw new FlowValidationException($"{test.Source}: {at} selects by whether a reference resolves, which only an assertion can ask.");
        }

        return new ValueFilter(target, condition);
    }

    private static ValueCondition MapCondition(AssertionConditionYaml y, bool isColumn, string at, TestContext test)
        => ConditionReader.Read(
            OperatorsOf(y), y.IgnoreCase, y.Tolerance, isColumn ? "asks whether a reference resolves, and a column of bulk data holds no references." : null, at, test.Source);

    private static Quantifier MapQuantifier(string? declared, string at, string source)
    {
        var text = declared?.Trim().ToLowerInvariant();
        switch (text)
        {
            case null or "all":
                return Quantifier.All;
            case "any":
                return Quantifier.Any;
            case "none":
                return Quantifier.None;
        }

        if (text.EndsWith('%')
            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            && percent is > 0 and <= 100)
        {
            return percent == 100 ? Quantifier.All : Quantifier.AtLeast(percent);
        }

        throw new FlowValidationException($"{source}: {at}.for '{declared}' is all, any, none, or a share such as 95%.");
    }

    private static void RequireBulk(string subject, string at, TestContext test)
    {
        if (!test.Bulk)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} is {Article(subject)} {subject} assertion, which reads each record's bulk data; give the test a bulk block (bulk: {{ columns: [...] }}).");
        }
    }

    /// <summary>A column (curve) of a record's bulk data as the DDMS names it; a comma would split the read's curve list.</summary>
    private static string CheckColumn(string? declared, string at, string source)
    {
        var column = declared?.Trim() ?? string.Empty;
        if (column.Length is 0 or > 256 || column.Contains(',', StringComparison.Ordinal) || column.Any(char.IsControl))
        {
            throw new FlowValidationException($"{source}: {at} '{Shown(column)}' is not a column name: 1 to 256 characters, without commas or control characters.");
        }

        return column;
    }

    /// <summary>A YAML value (scalars, mappings, sequences) as JSON, for what the document passes to the platform as written.</summary>
    private static JsonNode? ToJson(object? value, string at, string source) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int or long or short or byte or sbyte or uint or ushort => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => JsonValue.Create(u),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        float f when float.IsFinite(f) => JsonValue.Create(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        decimal m => JsonValue.Create(m),
        IDictionary map => ToJsonObject(map, at, source),
        IList list => new JsonArray(list.Cast<object?>().Select(item => ToJson(item, at, source)).ToArray()),
        _ => throw new FlowValidationException($"{source}: {at} holds a value that is not text, a number, true/false, a mapping or a list."),
    };

    private static JsonObject ToJsonObject(IDictionary map, string at, string source)
    {
        var node = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            var key = entry.Key as string ?? throw new FlowValidationException($"{source}: {at} has a key that is not text.");
            node[key] = ToJson(entry.Value, $"{at}.{key}", source);
        }

        return node;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string Shown(string value) => value.Length > 80 ? value[..80] + "..." : value;
}
