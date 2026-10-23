using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// An assertion flow (<c>flowType: assertion</c>, osdu/docs/reference/flow/assertion.md): qualified tests of what an OSDU
/// partition holds, run after the flows that populate it. Each test reads one OSDU kind (through the search index,
/// storage, or the bulk data a DDMS keeps for its records) and holds what it reads to one or more assertions with a
/// severity each; a run records every test's outcome in the flow's ledger, so the state of a partition after a delivery
/// is a report anyone can open, and every earlier report stays comparable with it. A test reads; nothing is ever written
/// to OSDU.
/// </summary>
public sealed record AssertionFlowDefinition
{
    public const string FlowTypeName = "assertion";

    /// <summary>The most tests one flow declares.</summary>
    public const int MaxTests = 500;

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its ledger is kept under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Parameters the tests may use as <c>{name}</c> tokens in their queries, ids and expected text.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the tests read, and where its services are served.</summary>
    public required AssertionSource Source { get; init; }

    /// <summary>
    /// The partitions the flow tests (<c>partitions</c>, osdu/docs/reference/flow/assertion.md, Which partition a run tests),
    /// in document order; empty for a flow that names none, which tests the partition its <c>source.headers</c> name or every
    /// registered one.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a run tests. Null for a flow whose
    /// partition is its header's, and for one that works in partitions before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it tests.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>True for a flow that names neither its partitions nor a <c>data-partition-id</c> header: it tests every registered partition.</summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>True when the flow tests partitions, named or registered, rather than the one its header names.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>What a test takes when it says nothing itself.</summary>
    public AssertionDefaults Defaults { get; init; } = new();

    /// <summary>Which outcomes fail the platform run: an error-severity failure (the default), a warning too, or none.</summary>
    public FailRunOn FailRunOn { get; init; } = FailRunOn.Error;

    /// <summary>The tests, in the order the document declares them; names are unique within the flow.</summary>
    public required IReadOnlyList<AssertionTest> Tests { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>How many tests run at once, and how many requests one test has in flight (<c>parallelism.concurrency</c>).</summary>
    public RunParallelism Parallelism { get; init; } = new();

    /// <summary>
    /// The ledger identity the flow's results are kept under: derived from the name and the bound partition for a flow that
    /// works in partitions, so each partition keeps a report of its own, and from the name alone for a flow whose partition
    /// is its header's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow works in partitions and is bound to none.</exception>
    [JsonIgnore]
    public Guid LedgerId => Partitioned
        ? Identity.FlowId.Of(Name, BoundPartition())
        : Identity.FlowId.Of(Name);

    /// <summary>The ledger's name as pages show it: <c>name@partition</c> for a flow that works in partitions, the name otherwise.</summary>
    [JsonIgnore]
    public string LedgerName => Partitioned ? $"{Name}@{BoundPartition()}" : Name;

    /// <summary>
    /// Whether settling the partition a run tests (<paramref name="requested"/>, or none) needs the registry: always for a
    /// flow that follows it, and for one that names several partitions when none is named.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (DeclaresPartitions && string.IsNullOrWhiteSpace(requested) && Partitions.Count > 1);

    /// <summary>The partitions the flow tests: those it names, every registered one for a flow that follows the registry, none for a header flow.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>: its requests carry the partition as <c>data-partition-id</c>,
    /// its <c>{partition}</c> token is the partition, and its results are kept in the partition's ledger.
    /// </summary>
    /// <exception cref="DeliveryException">The flow's partition is its header's, or the flow does not test this partition.</exception>
    public AssertionFlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Assertion flow '{Name}' names no partitions; it tests the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Assertion flow '{Name}' does not test partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Assertion flow '{Name}' cannot test '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with { Partition = declared, Source = Source with { Headers = headers } };
    }

    /// <summary>
    /// The flow as a run tests it (osdu/docs/reference/flow/assertion.md, Which partition a run tests): the partition the
    /// run names, which the flow has to serve; when it names none, the registry's default when the flow serves it, else the
    /// flow's only partition. A flow whose partition is its header's is tested as it is. A run tests one partition:
    /// <see cref="PartitionNames.Every"/> is refused, since a report describes one partition.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow tests.</exception>
    public AssertionFlowDefinition ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? this
                : throw new DeliveryException(
                    $"Assertion flow '{Name}' names no partitions; it tests the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow tests every registered partition.");
        }

        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"Assertion flow '{Name}' tests one partition per run, since a report describes one partition; name the partition to test instead of '{PartitionNames.Every}'.");
        }

        if (wanted is not null)
        {
            return ForPartition(FollowsRegistry ? registry.Find(wanted) ?? throw new DeliveryException(registry.NotRegistered(wanted)) : wanted);
        }

        var served = Served(registry);
        if (registry.Default is { } fallback && served.Contains(fallback, StringComparer.OrdinalIgnoreCase))
        {
            return ForPartition(fallback);
        }

        if (DeclaresPartitions && Partitions.Count == 1)
        {
            return ForPartition(Partitions[0]);
        }

        throw new DeliveryException(FollowsRegistry && registry.All.Count == 0
            ? $"No partition is registered with the catalog, so assertion flow '{Name}' has none to test. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"Assertion flow '{Name}' tests {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run tests.");
    }

    /// <summary>The test named <paramref name="name"/> (case-insensitively), or null.</summary>
    public AssertionTest? Test(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Tests.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The tests a run runs: those <paramref name="names"/> names and those carrying one of <paramref name="tags"/>, in
    /// document order; every test when both are empty.
    /// </summary>
    /// <exception cref="DeliveryException">A name is not a test of the flow, or a tag is carried by none of its tests.</exception>
    public IReadOnlyList<AssertionTest> Select(IReadOnlyCollection<string> names, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(tags);
        if (names.Count == 0 && tags.Count == 0)
        {
            return Tests;
        }

        var unknown = names.Where(n => Test(n) is null).ToList();
        if (unknown.Count > 0)
        {
            throw new DeliveryException(
                $"Assertion flow '{Name}' has no test named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; its tests are {Shown(Tests.Select(t => t.Name))}.");
        }

        var unused = tags.Where(tag => !Tests.Any(t => t.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))).ToList();
        if (unused.Count > 0)
        {
            var carried = Tests.SelectMany(t => t.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            throw new DeliveryException(
                $"No test of assertion flow '{Name}' carries the tag {string.Join(", ", unused.Select(n => $"'{n}'"))}; "
                + (carried.Count == 0 ? "its tests carry no tags." : $"its tests carry {Shown(carried)}."));
        }

        return Tests
            .Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase) || t.Tags.Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The secret references the source declares, keyed by document path; references only, never values.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (Source.Auth.SecretRef is { } secret)
        {
            yield return new("source.auth.secretRef", secret);
        }

        if (Source.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("source.auth.secondarySecretRef", secondary);
        }

        if (IsReference(Source.Endpoint))
        {
            yield return new("source.endpoint", Source.Endpoint);
        }

        foreach (var (name, value) in Source.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"source.headers.{name}", value);
        }

        if (Source.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"source.auth.token.body.{name}", value);
            }
        }
    }

    private string BoundPartition() => Partition
        ?? throw new InvalidOperationException(
            DeclaresPartitions
                ? $"Assertion flow '{Name}' tests each of {PartitionNames.Listed(Partitions)}; bind it to the partition a run tests before asking for its ledger."
                : $"Assertion flow '{Name}' tests every registered partition; bind it to the partition a run tests before asking for its ledger.");

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);

    private static string Shown(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 20 ? string.Join(", ", list) : string.Join(", ", list.Take(20)) + $" and {list.Count - 20} more";
    }
}

/// <summary>
/// The rule every name a run selects by keeps to (an assertion flow's tests and tags, a dimension flow's dimensions), so a
/// name travels in a URL, a payload and a report unchanged.
/// </summary>
public static partial class SelectableNames
{
    /// <summary>The longest name.</summary>
    public const int MaxLength = 100;

    /// <summary>The rule, as messages state it.</summary>
    public const string Rule = "a letter or digit followed by letters, digits, '.', '_' and '-', at most 100 characters";

    /// <summary>Whether <paramref name="name"/> keeps to <see cref="Rule"/>.</summary>
    public static bool IsName(string? name) => name is not null && Pattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Where a flow's tests read the platform: its endpoint and credentials, and the path of each service.</summary>
public sealed record AssertionSource
{
    public const string DefaultQueryPath = "/api/search/v2/query";
    public const string DefaultSearchPath = "/api/search/v2/query_with_cursor";
    public const string DefaultRecordQueryPath = "/api/storage/v2/query/records";
    public const string DefaultLegalPath = "/api/legal/v1";

    /// <summary>The Wellbore DDMS's root under the platform, the ingress route it is deployed under (osdu/specs/wellbore-ddms).</summary>
    public const string DefaultDdmsRoot = "/api/os-wellbore-ddms";

    /// <summary>The platform base URL; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The offset search a count and a grouping ask (openapi search v2, POST /query).</summary>
    public string QueryPath { get; init; } = DefaultQueryPath;

    /// <summary>The cursor search records are paged through (openapi search v2, POST /query_with_cursor).</summary>
    public string SearchPath { get; init; } = DefaultSearchPath;

    /// <summary>
    /// How a read of every record a search matches pages through them (<c>source.paging</c>): through the search cursor, the
    /// default, or in createTime order through the plain search, which holds nothing on the service between pages.
    /// </summary>
    public SearchPaging Paging { get; init; } = SearchPaging.Cursor;

    /// <summary>The storage read of records by id (openapi storage v2, POST /query/records).</summary>
    public string RecordQueryPath { get; init; } = DefaultRecordQueryPath;

    /// <summary>The legal service's root (openapi legal v1), under which <c>/legaltags:validate</c> is asked.</summary>
    public string LegalPath { get; init; } = DefaultLegalPath;

    /// <summary>The Wellbore DDMS's root, under which <c>/ddms/v3/{collection}/{id}/data</c> reads a record's bulk data.</summary>
    public string DdmsRoot { get; init; } = DefaultDdmsRoot;
}

/// <summary>What a test takes when it says nothing itself.</summary>
public sealed record AssertionDefaults
{
    /// <summary>The most records a test reads unless it says otherwise.</summary>
    public const int DefaultMaxRecords = 10_000;

    /// <summary>The ceiling on what any test may read: a test reads records into bounded memory, never a partition whole.</summary>
    public const int MaxRecordsCeiling = 1_000_000;

    /// <summary>The failing examples an assertion names unless the flow says otherwise.</summary>
    public const int DefaultExamples = 20;

    /// <summary>The most failing examples an assertion names.</summary>
    public const int MaxExamples = 500;

    /// <summary>
    /// How long, in seconds, the search index is given to list a change before a test of the changed type is judged, unless
    /// the flow says otherwise: OSDU's indexer takes the change from a queue, which commonly lists it within a minute and,
    /// under a large delivery, within a few.
    /// </summary>
    public const int DefaultIndexSettleSeconds = 300;

    /// <summary>The longest settle window a flow or a test may give the index.</summary>
    public const int MaxIndexSettleSeconds = 3600;

    public int MaxRecords { get; init; } = DefaultMaxRecords;

    public int Examples { get; init; } = DefaultExamples;

    public AssertionRead Read { get; init; } = AssertionRead.Storage;

    /// <summary>
    /// The settle window a test takes unless it says otherwise (docs: osdu/docs/reference/flow/assertion.md, Records
    /// the index may not list yet); 0 judges every test whatever changed.
    /// </summary>
    public int IndexSettleSeconds { get; init; } = DefaultIndexSettleSeconds;
}

/// <summary>Where a test reads the records its assertions look at.</summary>
public enum AssertionRead
{
    /// <summary>The records as storage holds them, read by the ids the search finds: every property, exactly as stored.</summary>
    Storage,

    /// <summary>The search hits themselves: only what the index holds of each record, but no storage read.</summary>
    Index,
}

/// <summary>Which outcomes fail the platform run of an assertion flow.</summary>
public enum FailRunOn
{
    /// <summary>A test that failed an error-severity assertion, or could not be evaluated.</summary>
    Error,

    /// <summary>As <see cref="Error"/>, and a test that failed a warning-severity assertion.</summary>
    Warning,

    /// <summary>Never: the run reports its tests and succeeds whenever it could run them.</summary>
    Never,
}

/// <summary>How much an assertion's failure matters. The order is the order of weight: an error outweighs a warning.</summary>
public enum AssertionSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>How severities weigh against each other.</summary>
public static class Severities
{
    /// <summary>The milder of two severities: what a miss within a warning band weighs, an assertion of info staying info.</summary>
    public static AssertionSeverity Milder(AssertionSeverity a, AssertionSeverity b) => a <= b ? a : b;
}

/// <summary>
/// One test of an assertion flow: what it reads from OSDU (one kind, found by a query or named by id, and optionally the
/// bulk data of each record) and the assertions it holds that to.
/// </summary>
public sealed record AssertionTest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Labels a run selects tests by, and the board groups them by.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The partitions of the flow the test runs in, when not all of them; empty runs in every partition the flow tests.</summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>The severity of the test's assertions that do not state one.</summary>
    public AssertionSeverity Severity { get; init; } = AssertionSeverity.Error;

    /// <summary>
    /// The one type the test reads, in one version (<c>authority:source:entityType:major.minor.patch</c>), as the mapping
    /// that delivers it names it in its <c>template.kind</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The template version the test's paths are checked against; null takes the one <see cref="TemplateMapping"/> fills,
    /// or else the one the template folders on the flow's path hold of the kind.
    /// </summary>
    public string? Template { get; init; }

    /// <summary>
    /// The mapping (<c>Name@version</c>) whose template the test's paths are checked against (<c>template: WellLog@1.5.0</c>):
    /// the kind and version its <c>template</c> pins, read from the synced mapping when the test is checked, so the test
    /// follows the mapping when it is pinned to another capture. Null when the test names a version, or none.
    /// </summary>
    public string? TemplateMapping { get; init; }

    /// <summary>A Lucene query narrowing the kind, with <c>{parameter}</c> and <c>{partition}</c> tokens; null matches the whole kind.</summary>
    public string? Query { get; init; }

    /// <summary>The records the test reads by id instead of by query, with tokens; empty for a test that searches.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary>A spatial filter the search applies (openapi search v2, <c>spatialFilter</c>), as the document writes it.</summary>
    public JsonObject? Spatial { get; init; }

    /// <summary>The order the search returns records in, which an ordered record set compares against.</summary>
    public IReadOnlyList<AssertionSort> Sort { get; init; } = [];

    public AssertionRead Read { get; init; } = AssertionRead.Storage;

    /// <summary>The most records the test reads.</summary>
    public int MaxRecords { get; init; } = AssertionDefaults.DefaultMaxRecords;

    /// <summary>
    /// What a test does when more records match than <see cref="MaxRecords"/>: false (the default) says so and evaluates
    /// nothing, since an assertion over part of the records proves nothing of the rest; true evaluates the first
    /// <see cref="MaxRecords"/> and marks the result as a sample.
    /// </summary>
    public bool Sample { get; init; }

    /// <summary>
    /// How long, in seconds, the search index is given to list a change a delivery flow of this module made to records of the
    /// test's entity type before the test is judged: a run within that time of such a change skips the test, saying why,
    /// rather than judging what the index lists so far (docs: osdu/docs/reference/flow/assertion.md, Records the index
    /// may not list yet). 0 judges it whatever changed. It says when the test is judged, not what it judges, so it
    /// stays out of <see cref="DefinitionHash"/>.
    /// </summary>
    [JsonIgnore]
    public int IndexSettleSeconds { get; init; } = AssertionDefaults.DefaultIndexSettleSeconds;

    /// <summary>The bulk data the test reads for each record, or null for a test that reads the records alone.</summary>
    public AssertionBulk? Bulk { get; init; }

    /// <summary>
    /// Why the test is disabled (<c>disabled: &lt;why&gt;</c>), or null when it runs: a disabled test stays part of the flow
    /// and of every report, recorded as skipped with this reason, and reads nothing.
    /// </summary>
    public string? Disabled { get; init; }

    /// <summary>
    /// When the values the test expects were stated (<c>asOf</c>, an ISO 8601 date or date-time), which every result quotes
    /// beside what was expected; null when the test does not say.
    /// </summary>
    public string? AsOf { get; init; }

    /// <summary>
    /// The last day (or instant) the values the test expects hold (<c>validUntil</c>), past which the test is flagged as
    /// needing new proof values rather than judged, since what it expects is no longer known to be so; null when they hold
    /// for good, or when <see cref="ValidFor"/> says how long.
    /// </summary>
    public string? ValidUntil { get; init; }

    /// <summary>How long after <see cref="AsOf"/> the values the test expects hold (<c>validFor: 90d</c>), in place of <see cref="ValidUntil"/>.</summary>
    public string? ValidFor { get; init; }

    /// <summary>
    /// The date property a record holds on or before <see cref="AsOf"/> to be among the test's records (<c>asOfField</c>:
    /// <c>createTime</c>, <c>modifyTime</c>, or a date of its data), so values counted on that day are compared with what OSDU
    /// held of them then; null compares every record the test finds.
    /// </summary>
    public string? AsOfField { get; init; }

    public required IReadOnlyList<TestAssertion> Assertions { get; init; }

    /// <summary>
    /// A hash of what the document says of the test, so a result can say whether the test has changed since it was
    /// recorded. Two documents stating the same test the same way hash the same, whatever their layout.
    /// </summary>
    public string DefinitionHash { get; init; } = string.Empty;

    /// <summary>True when the test reads records named by id rather than found by a search.</summary>
    public bool ByIds => Ids.Count > 0;

    /// <summary>Whether the test runs in <paramref name="partition"/>: every partition when it names none.</summary>
    public bool RunsIn(string? partition)
        => Partitions.Count == 0 || partition is null || Partitions.Contains(partition, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One field the search sorts by.</summary>
public sealed record AssertionSort(string Field, bool Descending);

/// <summary>What a test reads of each record's bulk data, from the Wellbore DDMS collection its kind is served under.</summary>
public sealed record AssertionBulk
{
    /// <summary>The most rows read of one record's bulk data unless the test says otherwise.</summary>
    public const long DefaultMaxRows = 1_000_000;

    /// <summary>The ceiling on the rows read of one record's bulk data.</summary>
    public const long MaxRowsCeiling = 100_000_000;

    /// <summary>The collection the kind is served under (<c>welllogs</c>, <c>wellboretrajectories</c>).</summary>
    public required string Collection { get; init; }

    /// <summary>The columns (curves) read; empty reads the columns the assertions name, or every column when none does.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    public long MaxRows { get; init; } = DefaultMaxRows;
}

/// <summary>
/// One assertion of a test. Each names what it looks at and what it expects, with the severity its failure carries and a
/// label reports show it under.
/// </summary>
[JsonDerivedType(typeof(CountAssertion), "count")]
[JsonDerivedType(typeof(ValueAssertion), "value")]
[JsonDerivedType(typeof(AggregateAssertion), "aggregate")]
[JsonDerivedType(typeof(UniqueAssertion), "unique")]
[JsonDerivedType(typeof(GroupAssertion), "groupBy")]
[JsonDerivedType(typeof(RecordSetAssertion), "recordSet")]
[JsonDerivedType(typeof(KnownValuesAssertion), "knownValues")]
[JsonDerivedType(typeof(ConformsAssertion), "conforms")]
[JsonDerivedType(typeof(IndexedAssertion), "indexed")]
[JsonDerivedType(typeof(LegalAssertion), "legal")]
[JsonDerivedType(typeof(DeliveredAssertion), "delivered")]
[JsonDerivedType(typeof(RowCountAssertion), "rowCount")]
[JsonDerivedType(typeof(ColumnsAssertion), "columns")]
[JsonDerivedType(typeof(MonotonicAssertion), "monotonic")]
[JsonDerivedType(typeof(MappingRulesAssertion), "mapping")]
public abstract record TestAssertion
{
    /// <summary>What reports call the assertion: the document's <c>name</c>, or a label read off what it asserts.</summary>
    public required string Label { get; init; }

    public string? Description { get; init; }

    public AssertionSeverity Severity { get; init; } = AssertionSeverity.Error;

    /// <summary>Why the assertion is disabled (<c>disabled: &lt;why&gt;</c>), or null when it is judged: a disabled one is reported as skipped, with this reason.</summary>
    public string? Disabled { get; init; }

    /// <summary>When what the assertion expects was stated, overriding its test's <see cref="AssertionTest.AsOf"/>; null takes the test's.</summary>
    public string? AsOf { get; init; }

    /// <summary>The last day (or instant) what the assertion expects holds, overriding its test's <see cref="AssertionTest.ValidUntil"/>; null takes the test's.</summary>
    public string? ValidUntil { get; init; }

    /// <summary>How long after its <c>asOf</c> (or its test's) what the assertion expects holds, in place of <see cref="ValidUntil"/>.</summary>
    public string? ValidFor { get; init; }

    /// <summary>The assertion's type as documents and reports name it: count, field, aggregate, unique, groupBy, ...</summary>
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>What the assertion expects, as reports quote it.</summary>
    [JsonIgnore]
    public abstract string Expected { get; }
}

/// <summary>How many records the test matches: the index's exact count, or the records its ids name that storage holds.</summary>
public sealed record CountAssertion(Comparison Comparison) : TestAssertion
{
    /// <summary>How far the count may miss <see cref="Comparison"/> and only warn (<c>warnWithin</c>); null fails it outright.</summary>
    public WarningBand? WarnWithin { get; init; }

    public override string Type => "count";

    public override string Expected => Comparison + WarningBand.Note(WarnWithin);
}

/// <summary>
/// A value every record (a field of the record) or every row of its bulk data (a column) holds: a condition on each value
/// the path yields, held for the share of records or rows <see cref="For"/> asks, optionally only of those
/// <see cref="Where"/> selects.
/// </summary>
public sealed record ValueAssertion : TestAssertion
{
    public required ValueTarget Target { get; init; }

    public required ValueCondition Condition { get; init; }

    /// <summary>The share of records (or rows) the condition has to hold for.</summary>
    public Quantifier For { get; init; } = Quantifier.All;

    /// <summary>
    /// For a path yielding several values in one record: true holds the record when one of them meets the condition, false
    /// (the default) only when every one does.
    /// </summary>
    public bool AnyValue { get; init; }

    /// <summary>True passes over a record (or row) that yields no value at all, instead of failing it.</summary>
    public bool Optional { get; init; }

    /// <summary>The conditions a record (or row) has to meet to be looked at: every one of them.</summary>
    public IReadOnlyList<ValueFilter> Where { get; init; } = [];

    public override string Type => Target.IsColumn ? "column" : "field";

    public override string Expected => Condition.ToString() + (For.Mode == QuantifierMode.All ? string.Empty : $" for {For}");
}

/// <summary>An aggregate of the values a field yields over every record, or a column over each record's rows, compared.</summary>
public sealed record AggregateAssertion : TestAssertion
{
    public required AggregateFunction Function { get; init; }

    public required ValueTarget Target { get; init; }

    public required Comparison Comparison { get; init; }

    /// <summary>How far the aggregate may be from a number it is compared with and still be equal to it, or within a range.</summary>
    public double? Tolerance { get; init; }

    /// <summary>
    /// The values the aggregate leaves out (<c>except</c>): a value equal to one of them is not counted, added up or ordered,
    /// such as a log's index curve <c>MD</c> among its curves, or a null sentinel among a column's values.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ExpectedValue> Except { get; init; } = [];

    /// <summary>
    /// <see cref="Except"/> as a test's definition hash reads it: absent when the aggregate leaves nothing out, so a test
    /// written before <c>except</c> existed hashes as it did, and its last result is not taken for one of another test.
    /// </summary>
    [JsonPropertyName("except")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExpectedValue>? ExceptListed => Except.Count == 0 ? null : Except;

    /// <summary>How far the aggregate may miss <see cref="Comparison"/> and only warn (<c>warnWithin</c>); null fails it outright.</summary>
    public WarningBand? WarnWithin { get; init; }

    public override string Type => "aggregate";

    public override string Expected => Comparison + (Tolerance is { } tolerance ? " (tolerance " + tolerance.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty)
        + WarningBand.Note(WarnWithin)
        + (Except.Count == 0 ? string.Empty : ", leaving out " + string.Join(", ", Except));
}

/// <summary>No two records carry the same values of the fields named, taken together.</summary>
public sealed record UniqueAssertion(IReadOnlyList<string> Fields) : TestAssertion
{
    public override string Type => "unique";

    public override string Expected => "no two records share " + string.Join(" + ", Fields);
}

/// <summary>
/// The records the test matches, grouped by the distinct values of one field (the search's <c>aggregateBy</c>): the groups
/// expected with their counts, the groups that must not be there, and how many groups there are.
/// </summary>
public sealed record GroupAssertion : TestAssertion
{
    public required string Field { get; init; }

    /// <summary>The groups expected, each with what its count has to be.</summary>
    public IReadOnlyList<ExpectedGroup> Groups { get; init; } = [];

    /// <summary>True when the groups expected are every group there is (<c>mode: exact</c>); false allows others.</summary>
    public bool Exact { get; init; }

    /// <summary>The groups that must not appear at all.</summary>
    public IReadOnlyList<string> Absent { get; init; } = [];

    /// <summary>What the number of groups has to be, or null.</summary>
    public Comparison? GroupCount { get; init; }

    public override string Type => "groupBy";

    public override string Expected
    {
        get
        {
            var parts = new List<string>();
            if (Groups.Count > 0)
            {
                parts.Add((Exact ? "exactly " : "includes ") + string.Join(", ", Groups.Select(g => $"{g.Key} {g.Count}")));
            }

            if (Absent.Count > 0)
            {
                parts.Add("none of " + string.Join(", ", Absent));
            }

            if (GroupCount is { } count)
            {
                parts.Add($"group count {count}");
            }

            return string.Join("; ", parts);
        }
    }
}

/// <summary>One group a grouping expects, with what its count has to be.</summary>
public sealed record ExpectedGroup(string Key, Comparison Count);

/// <summary>
/// The records the test reads, projected onto columns (fields), compared as a set of rows with the rows expected: the same
/// rows in any order, in order, including them, or excluding them (DeltaForge's RESULT SET,
/// osdu/docs/reference/flow/assertion.md, Assertions).
/// </summary>
public sealed record RecordSetAssertion : TestAssertion
{
    public required IReadOnlyList<string> Columns { get; init; }

    public required RecordSetMode Mode { get; init; }

    public required IReadOnlyList<IReadOnlyList<ExpectedValue?>> Rows { get; init; }

    public override string Type => "recordSet";

    public override string Expected => Mode switch
    {
        RecordSetMode.Exact => $"exactly {Rows.Count} row(s), in any order",
        RecordSetMode.Ordered => $"exactly {Rows.Count} row(s), in order",
        RecordSetMode.Includes => $"includes {Rows.Count} row(s)",
        _ => $"excludes {Rows.Count} row(s)",
    };
}

/// <summary>
/// Known values (osdu/docs/reference/flow/assertion.md, Known values): proof values a source states about records it does
/// not name by id. Each row finds its record by the full paths and values it is located by (<see cref="KnownRow.Locate"/>)
/// and holds that record to its proof values (<see cref="KnownRow.Checks"/>), each a full path and the value, or the
/// condition, the record has to hold there. Every path is a path in the OSDU record from its root, as the assertion's
/// field reads it; a list of paths leaves the record, read step by step as a dimension's label is: up to the record a
/// reference names (<c>[data.WellboreID, data.FacilityName]</c>, the name of the wellbore a log belongs to) and down to the
/// records that name it (<c>[work-product-component--WellLog[data.WellboreID], data.Name]</c>, the names of a wellbore's
/// logs). A row located
/// inside a list of objects (by <c>data.Curves.CurveID</c>) is one item of that list, and its proof values under the list are
/// that item's. Every row comes to one of four verdicts: it holds its values, it differs (and in which), its record is not
/// found (and by which path), or it finds more than one.
/// </summary>
public sealed record KnownValuesAssertion : TestAssertion
{
    /// <summary>The rows, each located and checked by its own full paths.</summary>
    public required IReadOnlyList<KnownRow> Rows { get; init; }

    /// <summary>The severity of a row whose record is not found; null takes the assertion's own, so such a row fails it.</summary>
    public AssertionSeverity? NotFound { get; init; }

    public override string Type => "knownValues";

    public override string Expected => string.Create(CultureInfo.InvariantCulture,
        $"each of {Rows.Count(r => r.Disabled is null)} row(s) finds one record and holds its {Rows.Where(r => r.Disabled is null).Sum(r => r.Checks.Count(c => c.Disabled is null))} proof value(s)")
        + (NotFound is { } severity && severity != Severity ? $"; a row not found counts as {AssertionText.Of(severity)}" : string.Empty);
}

/// <summary>
/// One row of known values: the full paths and values its record is located by, the proof values it holds the record to,
/// and why the row is disabled, when it is: a disabled row stays in the flow, reported, and is neither located nor checked.
/// </summary>
public sealed record KnownRow(IReadOnlyList<KnownLocate> Locate, IReadOnlyList<KnownCheck> Checks, string? Disabled = null)
{
    /// <summary>The row as reports name it: the values it is located by.</summary>
    public string Label => string.Join(" / ", Locate.Select(l => l.Value.Text));
}

/// <summary>A full path and the value the record a row is located by holds there.</summary>
public sealed record KnownLocate(KnownColumn Column, ExpectedValue Value);

/// <summary>
/// A proof value: a full path and the condition the values the record holds there meet (equal to a value unless it says
/// otherwise), and why it is disabled, when it is: a disabled proof value is reported and not checked.
/// </summary>
public sealed record KnownCheck(KnownColumn Column, ValueCondition Condition, string? Disabled = null);

/// <summary>
/// A full path of known values: its steps (<see cref="Steps"/>) from the record (or the item of the list a row is an item
/// of), through references to the records they name and to the records naming it, to the values it reads; how its values
/// compare; and, for a proof value, which of the values it reads are held to the condition: each of them, any of them, or
/// an aggregate of them.
/// </summary>
public sealed record KnownColumn
{
    /// <summary>The most paths a list of paths reads through: the record's own and three more to records it names or that name it.</summary>
    public const int MaxSteps = 4;

    /// <summary>The path as the document writes it: <c>data.Name</c>, <c>[data.WellboreID, data.FacilityName]</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The steps of the path: the first reads the record (or the records naming it), each later one leaves it.</summary>
    public required IReadOnlyList<KnownStep> Steps { get; init; }

    /// <summary>For a locating path: how far a number may be from the value given and still be equal to it.</summary>
    public double? Tolerance { get; init; }

    /// <summary>For a locating path: true compares text ignoring case.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>For a proof value reading several values: true holds it when one meets the condition, false when every one does.</summary>
    public bool AnyValue { get; init; }

    /// <summary>For a proof value: the aggregate of the values it reads that the condition is held to, or null to hold each value.</summary>
    public AggregateFunction? Aggregate { get; init; }

    /// <summary>The values an aggregating proof value leaves out, such as a log's index curve MD among its curves.</summary>
    public IReadOnlyList<ExpectedValue> Except { get; init; } = [];

    /// <summary>For a proof value comparing a number: how far the value may miss it and only warn; null makes any miss a difference.</summary>
    public WarningBand? WarnWithin { get; init; }

    /// <summary>What a report calls it: the path, and the aggregate taken of it.</summary>
    public string Label => Aggregate is { } function ? $"{AssertionText.Of(function)}({Path})" : Path;

    public override string ToString() => Label;
}

/// <summary>How one step of a known value's path reaches the record it reads.</summary>
public enum KnownStepVia
{
    /// <summary>The record (or item) the row is held to.</summary>
    Record,

    /// <summary>The record the reference the step before it read names: the record's parent, such as a log's wellbore.</summary>
    Parent,

    /// <summary>The records of <see cref="KnownStep.EntityType"/> whose <see cref="KnownStep.Reference"/> names the record the step before it reached: its children.</summary>
    Children,
}

/// <summary>
/// One step of a known value's path: how it reaches its records, the entity type of the records it reaches by their
/// references to the one before (<see cref="KnownStepVia.Children"/>), which of their properties holds that reference, and the
/// path it reads in the records it reaches; a step reading nothing stands for the records themselves, which the step after
/// it reaches the children of.
/// </summary>
public sealed record KnownStep(KnownStepVia Via, string? Field, string? EntityType = null, string? Reference = null);

/// <summary>
/// Whether a test, or an assertion of it, is judged in a run (osdu/docs/reference/flow/assertion.md, Disabling a test, and
/// dating its proof values): one that is disabled is skipped, saying why; one whose proof values are past the day they hold
/// until is not judged against values that may no longer be so, and is flagged instead, a warning saying they need
/// restating. What an assertion states of its dates takes over from its test's.
/// </summary>
public static partial class AssertionActivity
{
    /// <summary>Why <paramref name="test"/> is switched off, or null when it runs: it is disabled, or every assertion of it is.</summary>
    public static string? Disabled(AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (test.Disabled is { } why)
        {
            return $"The test is disabled: {why}";
        }

        return test.Assertions.Count > 0 && test.Assertions.All(a => a.Disabled is not null)
            ? string.Create(CultureInfo.InvariantCulture, $"Every one of its {test.Assertions.Count} assertion(s) is disabled ({test.Assertions[0].Disabled}).")
            : null;
    }

    /// <summary>Why <paramref name="assertion"/> is switched off, or null when it is judged.</summary>
    public static string? Disabled(TestAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        return assertion.Disabled is { } why ? $"disabled: {why}" : null;
    }

    /// <summary>
    /// Whether every assertion <paramref name="test"/> judges needs new proof values at <paramref name="now"/>, so the test
    /// asks OSDU nothing and is flagged whole.
    /// </summary>
    public static bool Stale(AssertionTest test, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(test);
        var judged = test.Assertions.Where(a => a.Disabled is null).ToList();
        return judged.Count > 0 && judged.All(a => Stale(a, test, now) is not null);
    }

    /// <summary>
    /// Why what <paramref name="assertion"/> expects needs restating at <paramref name="now"/>: the day it was stated and the
    /// day it holds until, which the run is past; null while it holds, or when it states no such day.
    /// </summary>
    public static string? Stale(TestAssertion assertion, AssertionTest test, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(test);
        var (asOf, validUntil, validFor) = Validity(assertion, test);
        return Last(asOf, validUntil, validFor) is { } last && now > last
            ? $"needs new proof values: {Window(asOf, validUntil, validFor)}, and the run is past that; restate them and move asOf"
            : null;
    }

    /// <summary>
    /// The day an assertion's values were stated, and the day (or the period after it) they hold until: its own where it
    /// states them, its test's where it does not.
    /// </summary>
    public static (string? AsOf, string? ValidUntil, string? ValidFor) Validity(TestAssertion assertion, AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(test);
        var own = assertion.ValidUntil is not null || assertion.ValidFor is not null;
        return (assertion.AsOf ?? test.AsOf, own ? assertion.ValidUntil : test.ValidUntil, own ? assertion.ValidFor : test.ValidFor);
    }

    /// <summary>The day what an assertion expects was stated, as a result quotes it after what was expected; empty when it was not given.</summary>
    public static string Stated(string? asOf) => asOf is null ? string.Empty : $" (stated as of {asOf})";

    /// <summary>
    /// The last instant values hold: <paramref name="validUntil"/>, or <paramref name="validFor"/> after
    /// <paramref name="asOf"/>. A date holds to the end of that day in UTC, a date and time to that instant. Null when they
    /// hold for good.
    /// </summary>
    public static DateTimeOffset? Last(string? asOf, string? validUntil, string? validFor)
    {
        if (validUntil is not null)
        {
            return Until(validUntil);
        }

        if (validFor is null || asOf is null || !TryPeriod(validFor, out var count, out var unit)
            || !Engine.Assertions.RecordValues.TryInstant(asOf, out var from))
        {
            return null;
        }

        var end = unit switch
        {
            'd' => from.AddDays(count),
            'w' => from.AddDays(7.0 * count),
            'm' => from.AddMonths(count),
            _ => from.AddYears(count),
        };
        return IsDate(asOf) ? end.AddDays(1).AddTicks(-1) : end;
    }

    /// <summary>The last instant <paramref name="validUntil"/> covers, or null when it is not an ISO 8601 date.</summary>
    public static DateTimeOffset? Until(string validUntil)
    {
        ArgumentNullException.ThrowIfNull(validUntil);
        if (!Engine.Assertions.RecordValues.TryInstant(validUntil, out var instant))
        {
            return null;
        }

        return IsDate(validUntil) ? instant.AddDays(1).AddTicks(-1) : instant;
    }

    /// <summary>How a reason says when values were stated and how long they hold: <c>stated as of 2026-10-13 and valid until 2027-01-11 (90d after asOf)</c>.</summary>
    public static string Window(string? asOf, string? validUntil, string? validFor)
    {
        var until = validUntil
            ?? (Last(asOf, null, validFor) is { } last
                ? (IsDate(asOf!) ? last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : last.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)) + $" ({validFor} after asOf)"
                : "a day it does not name");
        return asOf is null ? $"valid until {until}" : $"stated as of {asOf} and valid until {until}";
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a period values hold for: a whole number of days, weeks, months or years, written
    /// <c>90d</c>, <c>12w</c>, <c>6m</c>, <c>1y</c> (or in words, or as ISO 8601's <c>P90D</c>).
    /// </summary>
    public static bool TryPeriod(string text, out int count, out char unit)
    {
        ArgumentNullException.ThrowIfNull(text);
        count = 0;
        unit = 'd';
        var match = Period().Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1)
        {
            return false;
        }

        unit = char.ToLowerInvariant(match.Groups["unit"].Value[0]);
        return true;
    }

    /// <summary>The first instant past <paramref name="asOf"/>: the start of the next day for a date, the next millisecond for an instant.</summary>
    public static DateTimeOffset? After(string asOf)
    {
        ArgumentNullException.ThrowIfNull(asOf);
        return Engine.Assertions.RecordValues.TryInstant(asOf, out var instant)
            ? IsDate(asOf) ? instant.AddDays(1) : instant.AddMilliseconds(1)
            : null;
    }

    private static bool IsDate(string text) => text.Trim().Length == 10;

    [GeneratedRegex(@"^(?:P(?<count>\d{1,4})(?<unit>[DWMY])|(?<count>\d{1,4})\s*(?:(?<unit>d)(?:ays?)?|(?<unit>w)(?:eeks?)?|(?<unit>m)(?:onths?)?|(?<unit>y)(?:ears?)?))$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Period();
}

// <summary>How a record set compares with the rows expected.</summary>
public enum RecordSetMode
{
    Exact,
    Ordered,
    Includes,
    Excludes,
}

/// <summary>Every record meets the schema of its kind: required properties, types, formats, patterns, enumerations.</summary>
public sealed record ConformsAssertion : TestAssertion
{
    public override string Type => "conforms";

    public override string Expected => "every record meets the template of its kind";
}

/// <summary>
/// Every record read meets what a mapping asserts of the values a record carries (osdu/docs/reference/flow/mapping-assertions.md):
/// the record-stage assertions of the mapping named, judged on what OSDU holds by the code a render judges a document with.
/// The mapping is read, when a run binds the test, from the mappings the repository sync keeps.
/// </summary>
/// <param name="Mapping">The mapping, as <c>Name@version</c>.</param>
public sealed record MappingRulesAssertion(string Mapping) : TestAssertion
{
    /// <summary>The mapping a run read for the test; null until it is bound, or when it could not be read (<see cref="Unread"/>).</summary>
    [JsonIgnore]
    public MappingDefinition? Definition { get; init; }

    /// <summary>Why the mapping could not be read when the run bound the test; null when it was, or before it is bound.</summary>
    [JsonIgnore]
    public string? Unread { get; init; }

    public override string Type => "mapping";

    public override string Expected => $"every record meets what mapping {Mapping} asserts of the values a record carries";
}

/// <summary>The search indexed every record the test matches cleanly: no record carries an index status above 200 (<c>index.statusCode:[201 TO *]</c>).</summary>
public sealed record IndexedAssertion : TestAssertion
{
    public override string Type => "indexed";

    public override string Expected => "every record indexed with status 200";
}

/// <summary>Every legal tag the records carry is valid now (openapi legal v1, POST /legaltags:validate).</summary>
public sealed record LegalAssertion : TestAssertion
{
    public override string Type => "legal";

    public override string Expected => "every legal tag the records carry is valid";
}

/// <summary>
/// Every record a delivery flow's ledger holds as delivered in the partition is among the records the test matches; with
/// <see cref="Exact"/>, the test matches no record the ledger does not hold either.
/// </summary>
public sealed record DeliveredAssertion : TestAssertion
{
    /// <summary>The delivery flow whose ledger is read.</summary>
    public required string Flow { get; init; }

    /// <summary>The interface of that flow, for a source with interfaces; null for a flow in the single form.</summary>
    public string? Interface { get; init; }

    public bool Exact { get; init; }

    public override string Type => "delivered";

    public override string Expected => Exact
        ? $"exactly the records {Flow}{(Interface is null ? string.Empty : "/" + Interface)} delivered"
        : $"every record {Flow}{(Interface is null ? string.Empty : "/" + Interface)} delivered";
}

/// <summary>How many rows each record's bulk data holds.</summary>
public sealed record RowCountAssertion(Comparison Comparison) : TestAssertion
{
    public override string Type => "rowCount";

    public override string Expected => Comparison.ToString();
}

/// <summary>The columns (curves) each record's bulk data holds.</summary>
public sealed record ColumnsAssertion : TestAssertion
{
    public IReadOnlyList<string> Includes { get; init; } = [];

    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>When not empty, the columns have to be exactly these, in any order.</summary>
    public IReadOnlyList<string> Exactly { get; init; } = [];

    public override string Type => "columns";

    public override string Expected
    {
        get
        {
            var parts = new List<string>();
            if (Exactly.Count > 0)
            {
                parts.Add("exactly " + string.Join(", ", Exactly));
            }

            if (Includes.Count > 0)
            {
                parts.Add("includes " + string.Join(", ", Includes));
            }

            if (Excludes.Count > 0)
            {
                parts.Add("excludes " + string.Join(", ", Excludes));
            }

            return string.Join("; ", parts);
        }
    }
}

/// <summary>A column's values rise (or fall) row after row in each record's bulk data, as an index curve does.</summary>
public sealed record MonotonicAssertion(string Column, MonotonicDirection Direction) : TestAssertion
{
    public override string Type => "monotonic";

    public override string Expected => $"{Column} {AssertionText.Of(Direction)}";
}

public enum MonotonicDirection
{
    Increasing,
    Decreasing,
    StrictlyIncreasing,
    StrictlyDecreasing,
}

/// <summary>An aggregate over the values of a field or a column.</summary>
public enum AggregateFunction
{
    Min,
    Max,
    Sum,
    Avg,

    /// <summary>The values there are.</summary>
    Count,

    /// <summary>The distinct values there are.</summary>
    Distinct,

    /// <summary>The records (or rows) that yield no value.</summary>
    Missing,
}

/// <summary>What a value assertion or an aggregate reads: a path into each record, or a column of each record's bulk data.</summary>
public sealed record ValueTarget(string Path, bool IsColumn)
{
    public override string ToString() => Path;
}

/// <summary>A condition a record (or row) has to meet to be looked at by an assertion.</summary>
public sealed record ValueFilter(ValueTarget Target, ValueCondition Condition)
{
    public override string ToString() => $"{Target} {Condition}";
}

/// <summary>What a condition compares a value with.</summary>
public enum ValueOperator
{
    EqualTo,
    NotEqualTo,
    In,
    NotIn,
    AtLeast,
    AtMost,
    GreaterThan,
    LessThan,
    Between,
    Matches,
    NotMatches,
    StartsWith,
    EndsWith,
    Contains,
    NotContains,
    Exists,
    Empty,
    Type,
    Length,
    Resolves,
}

/// <summary>The JSON type a <c>type</c> condition asks a value to be, which documents name by JSON Schema's words.</summary>
public enum JsonValueType
{
    /// <summary>string</summary>
    Text,

    /// <summary>number: any number, whole or not.</summary>
    Number,

    /// <summary>integer: a number without a fractional part.</summary>
    WholeNumber,

    /// <summary>boolean</summary>
    Boolean,

    /// <summary>object</summary>
    Mapping,

    /// <summary>array</summary>
    Array,

    /// <summary>null</summary>
    Null,
}

/// <summary>
/// One condition on a value: an operator and what it compares with. Text compares ordinally unless
/// <see cref="IgnoreCase"/>; numbers compare as numbers, within <see cref="Tolerance"/> for equality and ranges; dates written
/// in ISO 8601 compare as instants.
/// </summary>
public sealed record ValueCondition
{
    public required ValueOperator Operator { get; init; }

    /// <summary>The values compared with: one for most operators, two for <see cref="ValueOperator.Between"/>, a list for In and NotIn.</summary>
    public IReadOnlyList<ExpectedValue> Operands { get; init; } = [];

    /// <summary>The expression of Matches and NotMatches, compiled with a timeout.</summary>
    [JsonIgnore]
    public Regex? Pattern { get; init; }

    /// <summary>What a string's or an array's length has to be (<see cref="ValueOperator.Length"/>).</summary>
    public Comparison? Length { get; init; }

    /// <summary>For Exists and Empty: whether the value has to be there (or empty), or not.</summary>
    public bool Flag { get; init; } = true;

    /// <summary>For <see cref="ValueOperator.Type"/>: the JSON type the value has to be.</summary>
    public JsonValueType? JsonType { get; init; }

    /// <summary>For <see cref="ValueOperator.Resolves"/>: the entity type the reference has to point at, or null for any.</summary>
    public string? EntityType { get; init; }

    public bool IgnoreCase { get; init; }

    /// <summary>How far a number may be from the value compared with and still be equal to it, or within a range.</summary>
    public double? Tolerance { get; init; }

    public override string ToString()
    {
        var text = Operator switch
        {
            ValueOperator.Between => $"between {Operands[0]} and {Operands[1]}",
            ValueOperator.In => "in [" + string.Join(", ", Operands) + "]",
            ValueOperator.NotIn => "not in [" + string.Join(", ", Operands) + "]",
            ValueOperator.Exists => Flag ? "exists" : "does not exist",
            ValueOperator.Empty => Flag ? "is empty" : "is not empty",
            ValueOperator.Type => "is of type " + AssertionText.Of(JsonType ?? JsonValueType.Text),
            ValueOperator.Length => $"has length {Length}",
            ValueOperator.Resolves => EntityType is null ? "resolves to a record" : $"resolves to a {EntityType}",
            _ => AssertionText.Of(Operator) + " " + (Operands.Count > 0 ? Operands[0].ToString() : string.Empty),
        };
        if (Tolerance is { } tolerance)
        {
            text += " (tolerance " + tolerance.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return IgnoreCase ? text + " ignoring case" : text;
    }
}

/// <summary>A value an assertion expects, as the document writes it: text, a number, a boolean, or null.</summary>
public sealed record ExpectedValue
{
    private ExpectedValue(ExpectedValueKind kind, string text, double? number, bool? boolean)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Boolean = boolean;
    }

    public ExpectedValueKind Kind { get; }

    /// <summary>The value as text: the text itself, a number in invariant form, true or false, or null.</summary>
    public string Text { get; }

    public double? Number { get; }

    public bool? Boolean { get; }

    public static ExpectedValue Null { get; } = new(ExpectedValueKind.Null, "null", null, null);

    public static ExpectedValue OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(ExpectedValueKind.Text, text, null, null);
    }

    public static ExpectedValue OfNumber(double number)
        => OfNumber(number, number.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// A number as the document wrote it: <paramref name="text"/> keeps it exactly (an integer beyond what a double holds,
    /// a decimal a double only approximates), and equality compares by it; <paramref name="number"/> orders it.
    /// </summary>
    public static ExpectedValue OfNumber(double number, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "An expected number is finite.");
        }

        return new(ExpectedValueKind.Number, text, number, null);
    }

    public static ExpectedValue OfBoolean(bool value) => new(ExpectedValueKind.Boolean, value ? "true" : "false", null, value);

    /// <summary>This value with <paramref name="substitute"/> applied to its text, when it is text.</summary>
    public ExpectedValue WithText(Func<string, string> substitute)
    {
        ArgumentNullException.ThrowIfNull(substitute);
        return Kind == ExpectedValueKind.Text ? OfText(substitute(Text)) : this;
    }

    public override string ToString() => Kind == ExpectedValueKind.Text ? "\"" + Text + "\"" : Text;
}

public enum ExpectedValueKind
{
    Text,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// How far a number may miss what an assertion compares it with and still only warn (<c>warnWithin</c>,
/// osdu/docs/reference/flow/assertion.md, A warning band): a distance (<c>5</c>), or a percentage of each number it is
/// compared with (<c>10%</c>). A number that misses by no more is a warning, so its test warns rather than fails; one
/// further off fails as ever. The band widens each number the comparison names its own way: an equality both ways, a lower
/// bound downwards, an upper bound upwards.
/// </summary>
/// <param name="Amount">The distance, or the percentage, greater than zero.</param>
/// <param name="Percent">True when <see cref="Amount"/> is a percentage of the number compared with.</param>
public sealed record WarningBand(double Amount, bool Percent)
{
    /// <summary>How far a number may be from <paramref name="expected"/> and be within the band.</summary>
    public double Around(double expected) => Percent ? Math.Abs(expected) * Amount / 100 : Amount;

    /// <summary>The band as a document writes it: <c>10%</c>, or <c>5</c>.</summary>
    public override string ToString() => Amount.ToString("R", CultureInfo.InvariantCulture) + (Percent ? "%" : string.Empty);

    /// <summary>What an assertion's expectation adds for its band, or nothing when it has none.</summary>
    public static string Note(WarningBand? band) => band is null ? string.Empty : $" (warning within {band})";
}

/// <summary>
/// What a number (a count, an aggregate, a length) has to be: one or more terms, every one of which has to hold, such as
/// <c>atLeast: 5</c> together with <c>atMost: 10</c>.
/// </summary>
public sealed record Comparison(IReadOnlyList<ComparisonTerm> Terms)
{
    public override string ToString() => string.Join(" and ", Terms.Select(t => t.ToString()));
}

/// <summary>One term of a comparison.</summary>
public sealed record ComparisonTerm(ComparisonOperator Operator, ExpectedValue Value, ExpectedValue? Upper = null)
{
    public override string ToString() => Operator == ComparisonOperator.Between
        ? $"between {Value} and {Upper}"
        : AssertionText.Of(Operator) + " " + Value;
}

public enum ComparisonOperator
{
    EqualTo,
    NotEqualTo,
    AtLeast,
    AtMost,
    GreaterThan,
    LessThan,
    Between,
}

/// <summary>The share of records (or rows) an assertion has to hold for: all of them, at least one, none, or a percentage.</summary>
public sealed record Quantifier(QuantifierMode Mode, double Share)
{
    public static Quantifier All { get; } = new(QuantifierMode.All, 100);

    public static Quantifier Any { get; } = new(QuantifierMode.Any, 0);

    public static Quantifier None { get; } = new(QuantifierMode.None, 0);

    public static Quantifier AtLeast(double percent) => new(QuantifierMode.Share, percent);

    public override string ToString() => Mode switch
    {
        QuantifierMode.All => "all",
        QuantifierMode.Any => "any",
        QuantifierMode.None => "none",
        _ => Share.ToString("0.###", CultureInfo.InvariantCulture) + "%",
    };
}

public enum QuantifierMode
{
    All,
    Any,
    None,
    Share,
}

/// <summary>The words documents and reports use for the operators, directions and types.</summary>
public static class AssertionText
{
    public static string Of(ValueOperator op) => op switch
    {
        ValueOperator.EqualTo => "equals",
        ValueOperator.NotEqualTo => "notEquals",
        ValueOperator.In => "in",
        ValueOperator.NotIn => "notIn",
        ValueOperator.AtLeast => "atLeast",
        ValueOperator.AtMost => "atMost",
        ValueOperator.GreaterThan => "greaterThan",
        ValueOperator.LessThan => "lessThan",
        ValueOperator.Between => "between",
        ValueOperator.Matches => "matches",
        ValueOperator.NotMatches => "notMatches",
        ValueOperator.StartsWith => "startsWith",
        ValueOperator.EndsWith => "endsWith",
        ValueOperator.Contains => "contains",
        ValueOperator.NotContains => "notContains",
        ValueOperator.Exists => "exists",
        ValueOperator.Empty => "empty",
        ValueOperator.Type => "type",
        ValueOperator.Length => "length",
        _ => "resolves",
    };

    public static string Of(ComparisonOperator op) => op switch
    {
        ComparisonOperator.EqualTo => "equals",
        ComparisonOperator.NotEqualTo => "notEquals",
        ComparisonOperator.AtLeast => "atLeast",
        ComparisonOperator.AtMost => "atMost",
        ComparisonOperator.GreaterThan => "greaterThan",
        ComparisonOperator.LessThan => "lessThan",
        _ => "between",
    };

    public static string Of(MonotonicDirection direction) => direction switch
    {
        MonotonicDirection.Increasing => "increasing",
        MonotonicDirection.Decreasing => "decreasing",
        MonotonicDirection.StrictlyIncreasing => "strictlyIncreasing",
        _ => "strictlyDecreasing",
    };

    public static string Of(JsonValueType type) => type switch
    {
        JsonValueType.Text => "string",
        JsonValueType.Number => "number",
        JsonValueType.WholeNumber => "integer",
        JsonValueType.Boolean => "boolean",
        JsonValueType.Mapping => "object",
        JsonValueType.Array => "array",
        _ => "null",
    };

    public static string Of(AggregateFunction function) => function switch
    {
        AggregateFunction.Min => "min",
        AggregateFunction.Max => "max",
        AggregateFunction.Sum => "sum",
        AggregateFunction.Avg => "avg",
        AggregateFunction.Count => "count",
        AggregateFunction.Distinct => "distinct",
        _ => "missing",
    };

    public static string Of(AssertionSeverity severity) => severity switch
    {
        AssertionSeverity.Error => "error",
        AssertionSeverity.Warning => "warning",
        _ => "info",
    };

    public static string Of(RecordSetMode mode) => mode switch
    {
        RecordSetMode.Exact => "exact",
        RecordSetMode.Ordered => "ordered",
        RecordSetMode.Includes => "includes",
        _ => "excludes",
    };
}
