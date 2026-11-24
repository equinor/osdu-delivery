using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Search;
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

    public static AssertionFlowDefinition Map(AssertionYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var headers = FlowMapper.MapHeaders(src.Headers, "source.headers", source);
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
        var parameters = FlowParameterDeclarations.Map(y.Parameters, source);
        if (parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', which an assertion flow keeps for the partition a run tests: {{partition}} in a test is always that partition. Rename the parameter.");
        }

        var defaults = MapDefaults(y.Defaults, source);
        var dimensions = MapDimensions(y.Dimensions, "dimensions", source);
        var queries = MapQueries(y.Queries, parameters, source);
        var flow = new AssertionFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            Set = MapSet(y.Set, source) ?? Optional(y.Batch),
            Title = TitleOf(y.Title, "title", source),
            Dimensions = dimensions,
            TelemetryOperation = FlowMapper.MapTelemetry(y.Telemetry, AssertionFlowDefinition.FlowTypeName, source),
            Parameters = parameters,
            Queries = queries,
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
            Tests = MapTests(y.Tests, parameters, queries, partitions, followsRegistry, defaults, dimensions, source),
            Reliability = FlowMapper.MapReliability(y.Reliability, "an assertion flow", takesRecordsAtOnce: true, source),
            Parallelism = FlowMapper.MapRunParallelism(y.Parallelism, "the tests evaluated at once, and the requests one test has in flight", source),
        };

        return flow;
    }

    /// <summary>
    /// The set the flow names (<c>set</c>, osdu/docs/reference/flow/assertion-sets.md), or null when it names none: a set is
    /// named as a test is, since a link, a route and the command line name it.
    /// </summary>
    private static string? MapSet(string? declared, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var set = declared.Trim();
        return SelectableNames.IsName(set)
            ? set
            : throw new FlowValidationException(
                $"{source}: set '{declared}' is not a set name: {SelectableNames.Rule}. Name the set the flow's tests are grouped under, such as set: welllog-proof-values, or take the key out to group them under the flow's batch.");
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

    /// <summary>
    /// The flow's queries (osdu/docs/reference/flow/staged-proof-values.md): each named as a test is, the database it runs
    /// against as a reference, and one read-only SELECT whose tokens are the flow's parameters or {partition}.
    /// </summary>
    private static IReadOnlyDictionary<string, StagedQuery> MapQueries(
        Dictionary<string, AssertionQueryYaml?>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, string source)
    {
        var queries = new Dictionary<string, StagedQuery>(StringComparer.Ordinal);
        if (declared is null)
        {
            return queries;
        }

        if (declared.Count > StagedQuery.MaxQueries)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{source}: queries names {declared.Count} queries; a flow names at most {StagedQuery.MaxQueries}. Split its tests over several flows."));
        }

        foreach (var (written, y) in declared)
        {
            var name = written?.Trim() ?? string.Empty;
            var at = $"queries.{name}";
            if (!SelectableNames.IsName(name))
            {
                throw new FlowValidationException($"{source}: queries names '{written}', which is not a query name: {SelectableNames.Rule}.");
            }

            if (queries.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: queries names '{name}' twice, ignoring case; a test names each query by its name, so each needs its own.");
            }

            if (y is null)
            {
                throw new FlowValidationException($"{source}: {at} is empty; give it connection, the database it runs against, and sql, its one SELECT.");
            }

            var query = ConditionReader.Query(y.Connection, y.Sql, y.TimeoutSeconds, name, at, source);
            Engine.Assertions.StagedSql.Check(query.Sql, parameters, $"{source}: {at}.sql");
            queries[name] = query;
        }

        return queries;
    }

    // Columns is true while known values read from a query are read: their values name the query's columns.
    private sealed record TestContext(
        string Where, string Source, string Kind, AssertionSeverity Severity, bool ByIds, AssertionRead Read, bool Sorted, bool Bulk, string? AsOf,
        IReadOnlyDictionary<string, FlowParameter> Parameters, IReadOnlyDictionary<string, StagedQuery> Queries, bool Columns = false);

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

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Shown(string value) => value.Length > 80 ? value[..80] + "..." : value;
}
