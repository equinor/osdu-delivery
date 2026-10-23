using System.Text.Json.Serialization;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A dimension flow (<c>flowType: dimension</c>, osdu/docs/reference/flow/dimension.md): the distinct values attributes of
/// OSDU records hold, each kept exactly as the search index holds it (an original) and grouped under a cleaned value (a
/// member), so an application can offer the clean values as a filter and search OSDU for every record holding any original
/// behind the ones picked. A build reads every distinct value through the search's aggregation, paged by value ranges, and
/// keeps them in the flow's ledger; nothing is ever written to OSDU.
/// </summary>
public sealed record DimensionFlowDefinition
{
    public const string FlowTypeName = "dimension";

    /// <summary>The most dimensions one flow declares.</summary>
    public const int MaxDimensions = 100;

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its ledger is kept under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Parameters the dimensions' queries may use as <c>{name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the dimensions are read from.</summary>
    public required DimensionSource Source { get; init; }

    /// <summary>
    /// The partitions the flow builds dimensions in (<c>partitions</c>, osdu/docs/reference/flow/dimension.md,
    /// Partitions), in document order; empty for a flow that names none, which builds in the partition its
    /// <c>source.headers</c> name or in every registered one.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a run builds in. Null for a flow whose
    /// partition is its header's, and for one that works in partitions before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it builds in.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>True for a flow that names neither its partitions nor a <c>data-partition-id</c> header: it builds in every registered partition.</summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>True when the flow builds in partitions, named or registered, rather than the one its header names.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>The dimensions, in the order the document declares them; names are unique within the flow.</summary>
    public required IReadOnlyList<DimensionSpec> Dimensions { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>How many dimensions build at once, and how many requests each asks of the search at once (<c>parallelism.concurrency</c>).</summary>
    public RunParallelism Parallelism { get; init; } = new();

    /// <summary>
    /// How a build loads the flow's dimensions after their first full load (osdu/docs/reference/flow/dimension.md, Full
    /// and incremental loads): null for a flow that loads every dimension in full on every build.
    /// </summary>
    public DimensionIncremental? Incremental { get; init; }

    /// <summary>
    /// The module's database as the pipelines reading the flow's tables and views name it
    /// (osdu/docs/reference/flow/dimension-views.md), so SQLFlow's lineage orders them after the flow; null when the flow
    /// names none, which a flow declaring views must.
    /// </summary>
    public DimensionTarget? Target { get; init; }

    /// <summary>The views over the flow's dimension tables, in the order the document declares them; names are unique ignoring case.</summary>
    public IReadOnlyList<DimensionViewSpec> Views { get; init; } = [];

    /// <summary>True when a dimension cleans its values through a dictionary document, which the run reads at its commit.</summary>
    public bool ReadsDictionaries => Dimensions.Any(d => d.Clean.Any(s => s.Kind == CleanStepKind.Map));

    /// <summary>
    /// The ledger identity the flow's dimensions are kept under: derived from the name and the bound partition for a flow that
    /// works in partitions, so each partition keeps dimensions of its own, and from the name alone for a flow whose partition
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
    /// Whether settling the partition a run builds in (<paramref name="requested"/>, or none) needs the registry: always for a
    /// flow that follows it, and for one that names several partitions when none is named.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (DeclaresPartitions && string.IsNullOrWhiteSpace(requested) && Partitions.Count > 1);

    /// <summary>The partitions the flow builds in: those it names, every registered one for a flow that follows the registry, none for a header flow.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>: its requests carry the partition as <c>data-partition-id</c>, its
    /// <c>{partition}</c> token is the partition, and its dimensions are kept in the partition's ledger.
    /// </summary>
    /// <exception cref="DeliveryException">The flow's partition is its header's, or the flow does not build in this partition.</exception>
    public DimensionFlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Dimension flow '{Name}' names no partitions; it builds in the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Dimension flow '{Name}' does not build in partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Dimension flow '{Name}' cannot build in '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with { Partition = declared, Source = Source with { Headers = headers } };
    }

    /// <summary>
    /// The flow as a run builds it (osdu/docs/reference/flow/dimension.md, Partitions): the partition the run names, which
    /// the flow has to serve; when it names none, the registry's default when the flow serves it, else the flow's only
    /// partition. A flow whose partition is its header's is built as it is. A run builds in one partition:
    /// <see cref="PartitionNames.Every"/> is refused, since a run's result and its builds describe one partition.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow builds in.</exception>
    public DimensionFlowDefinition ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? this
                : throw new DeliveryException(
                    $"Dimension flow '{Name}' names no partitions; it builds in the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow builds in every registered partition.");
        }

        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"Dimension flow '{Name}' builds in one partition per run, since a run's builds describe one partition; name the partition to build in instead of '{PartitionNames.Every}'.");
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
            ? $"No partition is registered with the catalog, so dimension flow '{Name}' has none to build in. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"Dimension flow '{Name}' builds in {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run builds in.");
    }

    /// <summary>The dimension named <paramref name="name"/> (case-insensitively), or null.</summary>
    public DimensionSpec? Dimension(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Dimensions.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The dimensions a run builds: those <paramref name="names"/> names, in document order; every dimension when it names none.</summary>
    /// <exception cref="DeliveryException">A name is not a dimension of the flow.</exception>
    public IReadOnlyList<DimensionSpec> Select(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return Dimensions;
        }

        var unknown = names.Where(n => Dimension(n) is null).ToList();
        if (unknown.Count > 0)
        {
            var known = Dimensions.Select(d => d.Name).ToList();
            throw new DeliveryException(
                $"Dimension flow '{Name}' has no dimension named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; its dimensions are "
                + (known.Count <= 20 ? string.Join(", ", known) : string.Join(", ", known.Take(20)) + $" and {known.Count - 20} more") + ".");
        }

        return Dimensions.Where(d => names.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
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

        if (Target is { } target && IsReference(target.Connection))
        {
            yield return new("target.connection", target.Connection);
        }
    }

    private string BoundPartition() => Partition
        ?? throw new InvalidOperationException(
            DeclaresPartitions
                ? $"Dimension flow '{Name}' builds in each of {PartitionNames.Listed(Partitions)}; bind it to the partition a run builds in before asking for its ledger."
                : $"Dimension flow '{Name}' builds in every registered partition; bind it to the partition a run builds in before asking for its ledger.");

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

/// <summary>Where a flow's dimensions are read: the platform's endpoint and credentials, its search paths, and how many groups its aggregation returns.</summary>
/// <summary>
/// The <c>incremental</c> block of a dimension flow: each build reads again only the keys of the records that changed since
/// the last build read the dimension (by <see cref="DateColumns"/>), the keys those records held before (kept by their
/// unique key, <see cref="KeyColumns"/>), and the keys whose labels and attributes were read through a record that changed;
/// and reads a dimension in full when it has to.
/// </summary>
public sealed record DimensionIncremental
{
    /// <summary>How far behind now a window ends, unless the flow says otherwise: the retrieval flow's own default.</summary>
    public const int DefaultLagMinutes = 5;

    /// <summary>The most properties a record's unique key, or its change time, is read from.</summary>
    public const int MaxColumns = 3;

    /// <summary>
    /// When a record last changed unless the flow says otherwise: OSDU's <c>modifyTime</c>, which it sets from a record's
    /// second version on, and for a record never modified its <c>createTime</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultDateColumns { get; } = ["modifyTime", "createTime"];

    /// <summary>
    /// The properties whose values together identify one record (<c>id</c>): a full load keeps the keys each record holds
    /// by them, and an incremental load reads again the keys a changed record held before as well as those it holds now.
    /// </summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>
    /// When a record last changed: the first of these properties it holds, each a date the index holds once a record. A
    /// window reads the records whose change time falls in it, each in the one window its change time falls in.
    /// </summary>
    public IReadOnlyList<string> DateColumns { get; init; } = DefaultDateColumns;

    /// <summary>The unique key as a full load keeps it with its records: the key columns, in order, comma-separated.</summary>
    public string RecordKey => string.Join(",", KeyColumns);

    /// <summary>The longest lag a flow names: a day.</summary>
    public const int MaxLagMinutes = 24 * 60;

    /// <summary>The longest a flow waits between full loads: a year.</summary>
    public const int MaxFullLoadAfterHours = 366 * 24;

    /// <summary>
    /// How far behind now a window ends, so records the indexer has not caught up with are read by the next build; a window
    /// also begins this far before the last one ended, so a record indexed up to twice as late is still read.
    /// </summary>
    public int LagMinutes { get; init; } = DefaultLagMinutes;

    /// <summary>
    /// A build loads a dimension in full when its last full load completed more than this many hours ago, so keys no record
    /// holds any more are removed and counts that went down are settled; null loads in full only when one is asked for or
    /// needed.
    /// </summary>
    public int? FullLoadAfterHours { get; init; }

    /// <summary>Every build loads in full, as without the block, keeping the block's settings for when this is taken out.</summary>
    public bool FullLoad { get; init; }
}

public sealed record DimensionSource
{
    public const string DefaultQueryPath = "/api/search/v2/query";
    public const string DefaultSearchPath = "/api/search/v2/query_with_cursor";

    /// <summary>
    /// The search service's own default for how many groups an aggregation returns (<c>aggregationSize</c> in
    /// <c>SearchConfigurationProperties</c>), which a platform's operators can change with <c>AGGREGATION_SIZE</c>.
    /// </summary>
    public const int DefaultAggregationSize = 1000;

    /// <summary>The platform base URL; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The offset search an aggregation and a count ask (openapi search v2, POST /query).</summary>
    public string QueryPath { get; init; } = DefaultQueryPath;

    /// <summary>The cursor search a scan pages through (openapi search v2, POST /query_with_cursor).</summary>
    public string SearchPath { get; init; } = DefaultSearchPath;

    /// <summary>
    /// How a read of every record a search matches pages through them (<c>source.paging</c>): through the search cursor, the
    /// default, or in createTime order through the plain search, which holds nothing on the service between pages.
    /// </summary>
    public SearchPaging Paging { get; init; } = SearchPaging.Cursor;

    /// <summary>
    /// How many groups the platform's aggregation returns. A slice answered with fewer is taken as complete, so this must not
    /// be more than the platform's setting; less only asks more often.
    /// </summary>
    public int AggregationSize { get; init; } = DefaultAggregationSize;
}

/// <summary>
/// One dimension: the attribute of a kind's records whose distinct values it holds, and how each value is cleaned into the
/// member it belongs to.
/// </summary>
public sealed record DimensionSpec
{
    /// <summary>The most distinct values a dimension keeps unless it says otherwise.</summary>
    public const long DefaultMaxValues = 1_000_000;

    /// <summary>The most distinct values any dimension may keep: a build holds its values in memory while it groups them.</summary>
    public const long MaxValuesCeiling = 5_000_000;

    /// <summary>The longest clean value a member is kept under.</summary>
    public const int MaxCleanLength = 256;

    /// <summary>The longest original a dimension keeps; a longer one is counted and left out.</summary>
    public const int MaxOriginalLength = 1024;

    /// <summary>The most clean steps one dimension applies.</summary>
    public const int MaxCleanSteps = 20;

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The kind whose records are read, <c>authority:source:entityType:version</c>, wildcards allowed per segment.</summary>
    public required string Kind { get; init; }

    /// <summary>A Lucene query narrowing the records read, with <c>{parameter}</c> and <c>{partition}</c> tokens; null reads every record of the kind.</summary>
    public string? Query { get; init; }

    /// <summary>The attribute, as a path from the record's root: <c>data.Curves.Mnemonic</c>, <c>legal.legaltags</c>, <c>tags.Source</c>.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Where each key's human-friendly value is read, for a key that names an OSDU record (<c>data.WellboreID</c> holds
    /// <c>dev:master-data--Wellbore:...:</c>): a path of the record the key names (<c>data.FacilityName</c>), or several,
    /// each but the last reading a reference to the record the next is read from (<c>data.GeoContexts.GeoPoliticalEntityID</c>,
    /// then <c>data.GeoPoliticalEntityName</c>). Empty reads no label: a key is its own value, cleaned by <see cref="Clean"/>.
    /// </summary>
    public IReadOnlyList<string> Label { get; init; } = [];

    /// <summary>The most records a label reads through, one per step.</summary>
    public const int MaxLabelSteps = 3;

    /// <summary>
    /// The value of a key whose label is not read (it names no record, the search does not hold the record, or the record
    /// holds nothing where the label is read), cleaned like a label: <c>Not specified</c>, so a drop-down lists such keys
    /// under one value a person picks, as an application does. Null values such a key by the code its id ends with. It is
    /// also the value of each attribute a key holds none of, so every cascading drop-down lists such keys too.
    /// </summary>
    public string? Unlabelled { get; init; }

    /// <summary>
    /// Further facts of each key read the way its label is, each under a name: a path of the record the key names
    /// (<c>SpudDate: data.SpudDate</c>), or several read through its references (<c>Country:
    /// [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]</c>). A key keeps each attribute's value, so a
    /// dimension's values and keys can be looked up and a search picked by them (<c>Wellbore where Country is United States</c>).
    /// </summary>
    public IReadOnlyList<DimensionAttributeSpec> Attributes { get; init; } = [];

    /// <summary>The most attributes a dimension reads.</summary>
    public const int MaxAttributes = 20;

    /// <summary>The longest attribute value kept; a longer one is cut, with a note.</summary>
    public const int MaxAttributeValueLength = 256;

    /// <summary>The longest value of an element's field kept; a longer one is cut, with a note. No index holds it, so it is longer than an attribute's.</summary>
    public const int MaxElementValueLength = 4000;

    /// <summary>The attribute <paramref name="name"/> names, ignoring case; null when the dimension reads none of that name.</summary>
    public DimensionAttributeSpec? Attribute(string name)
        => Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The objects of a nested array of each record, a row each with its fields; null when the dimension reads none.</summary>
    public DimensionElementsSpec? Elements { get; init; }

    /// <summary>
    /// The column of the dimension's table that holds each key, exactly as the index holds it: named after the property
    /// the path ends with (<c>data.WellboreID</c> is <c>WellboreID</c>), with <c>Key</c> at its end where the value's
    /// column has that name (<c>SourceKey</c> beside <c>Source</c>), unless the document names it (<c>columns.key</c>).
    /// </summary>
    public string KeyColumn { get; init; } = DimensionColumnNames.KeyRole;

    /// <summary>
    /// The column of the dimension's table that holds each key's value: named after the property the label ends with
    /// (<c>data.FacilityName</c> is <c>FacilityName</c>), or after the dimension when it reads no label, unless the
    /// document names it (<c>columns.value</c>).
    /// </summary>
    public string ValueColumn { get; init; } = DimensionColumnNames.ValueRole;

    /// <summary>The steps each value is cleaned by, in order (a key's label when it has one, else the key); none keeps it as it is.</summary>
    public IReadOnlyList<CleanStep> Clean { get; init; } = [];

    /// <summary>
    /// Count each member's records exactly, with one count of the search per member whose originals could share a record: where
    /// the attribute repeats in a record, the index's own counts are of values held, not of records.
    /// </summary>
    public bool CountRecords { get; init; }

    /// <summary>The most distinct values the dimension keeps; a build that finds more fails before it writes anything.</summary>
    public long MaxValues { get; init; } = DefaultMaxValues;

    /// <summary>The partitions of the flow the dimension is built in, when not all of them; empty builds in every partition the flow does.</summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// A hash of what the document says of the dimension, so a build says whether the dimension changed since the build
    /// before it. Two documents stating the same dimension the same way hash the same, whatever their layout.
    /// </summary>
    public string DefinitionHash { get; init; } = string.Empty;

    /// <summary>Whether the dimension is built in <paramref name="partition"/>: every partition when it names none.</summary>
    public bool BuildsIn(string? partition)
        => Partitions.Count == 0 || partition is null || Partitions.Contains(partition, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One attribute of a dimension's keys: its name, and where its values come from. Either it is read from the record a key
/// names, as the label is (<see cref="Steps"/>: each path but the last reading the reference to the next record), one value
/// a key; or it is collected from the dimension's own records (<see cref="Collect"/>: every value the records holding a key
/// hold at that path, so a wellbore keyed by its logs' WellboreID collects the Source of each of its logs). Picking a
/// collected value in a search finds the records holding it, not every record of the keys that collected it.
/// </summary>
/// <param name="Name">A letter, then letters, digits and underscores, at most 64; unique in its dimension ignoring case.</param>
/// <param name="Steps">The paths read from the record a key names, one to <see cref="DimensionSpec.MaxLabelSteps"/>; empty for a collected attribute.</param>
/// <param name="Collect">The path of the dimension's own records whose values the attribute collects; null for one read from a key's record.</param>
/// <param name="Keep">How each value is kept: as a value shows it, or as a key another dimension's table is joined on.</param>
public sealed record DimensionAttributeSpec(string Name, IReadOnlyList<string> Steps, string? Collect = null, DimensionValueKeep Keep = DimensionValueKeep.Value)
{
    /// <summary>Whether the attribute's values are collected from the dimension's own records rather than read from a key's record.</summary>
    public bool IsCollected => Collect is not null;

    /// <summary>The longest attribute name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Names an attribute cannot take: the columns a cached dimension's rows and a dimension's table hold already, and the
    /// words a key's own facts are known by. The columns of a dimension's key and value take names of their own
    /// (<see cref="DimensionColumnNames"/>), which an attribute of the dimension cannot share either.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(["value", "keys", "key", "key_id", "records", "filter", "label", "id", "partition"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is an attribute name: a letter, then letters, digits and underscores.</summary>
    public static bool IsName(string? name)
        => name is { Length: > 0 and <= MaxNameLength } && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>How a value read for an attribute or an element's field is kept in the dimension's table.</summary>
public enum DimensionValueKeep
{
    /// <summary>As a value shows it: a record reference by the code its id ends with, its escapes decoded; a longer text cut.</summary>
    Value,

    /// <summary>Exactly as the record holds it, so it is the text the key column of a dimension keyed by the same path holds.</summary>
    Key,

    /// <summary>
    /// A record reference as the id of the record it names (its version, or the trailing colon of the latest, taken off), so
    /// it is the text the key column of a dimension keyed by <c>id</c> holds; any other text as the record holds it.
    /// </summary>
    Id,
}

/// <summary>
/// The objects of a nested array of each record (<c>data.Curves</c>), a row each in the dimension's table beside the key
/// of the record that holds them, with a column per field read from the object: so a dimension keyed by a well log's id
/// lists every curve of the log with its id, mnemonic, unit and depths, each curve's fields kept together.
/// </summary>
/// <remarks>
/// The path is read as a label's is (<see cref="DimensionPath"/>): every array on the way is stepped into, so a path through
/// two arrays (<c>data.Curves.Columns</c>) makes a row of every object of the inner one, and any segment can filter the
/// objects it holds (<c>data.GeoContexts[GeoTypeID$=:Field:]</c>). What the path reaches at its end is the element: an
/// object, whose fields are read inside it, or a plain value, which the field <c>@</c> reads.
/// </remarks>
/// <param name="Path">The array, as a path from the record's root, filters allowed.</param>
/// <param name="Fields">The fields read from each element, in their order, each a column of the table.</param>
public sealed record DimensionElementsSpec(string Path, IReadOnlyList<DimensionElementField> Fields)
{
    /// <summary>
    /// The column of the table that tells the rows of one key apart: each object's place among the key's, from 1, in the order
    /// of the records' ids and then of the objects in each record.
    /// </summary>
    public const string Column = "element";

    /// <summary>The most objects one build reads: a build holds them in memory while it writes them.</summary>
    public const long MaxElements = 5_000_000;

    /// <summary>The field path that reads the element itself, for an array of plain values.</summary>
    public const string Self = "@";

    /// <summary>The path, parsed; the document mapper refuses one that does not parse.</summary>
    public DimensionPath Parsed => DimensionPath.Parse(Path).Path ?? throw new InvalidOperationException($"'{Path}' is not an elements path.");

    /// <summary>
    /// What a search is asked to return to read every field: for each field, the property names of the path down to the object
    /// it is read from, then the field's own path, and the property each filter on the way compares.
    /// </summary>
    public IReadOnlyList<string> ReturnedFields()
    {
        var path = Parsed;
        var names = path.Segments.Select(s => s.Name).ToList();
        var fields = new List<string>(path.ReturnedFields.Skip(1));
        foreach (var field in Fields)
        {
            var at = string.Join('.', names.Take(names.Count - field.Up));
            if (field.Path == Self)
            {
                fields.Add(at);
                continue;
            }

            fields.AddRange(DimensionPath.Parse(field.Path).Path!.ReturnedFields.Select(f => at.Length == 0 ? f : at + "." + f));
        }

        return fields.Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>How a field reached several times inside one element is kept.</summary>
public enum DimensionElementMany
{
    /// <summary>The first value that is not empty.</summary>
    First,

    /// <summary>Every value that is not empty, each once, in order, joined by <see cref="DimensionElementField.Separator"/>.</summary>
    Join,
}

/// <summary>One field of a dimension's elements: the column it is kept in, its path, and how it is read and kept.</summary>
/// <param name="Name">The column, named as an attribute is.</param>
/// <param name="Path">The path inside the element (<c>CurveUnit</c>, <c>Quantity.Code</c>, filters allowed), or <c>@</c> for the element itself.</param>
/// <param name="Keep">How each value is kept: as a value shows it, or as a key another dimension's table is joined on.</param>
/// <param name="Up">How many objects up the element's path the field is read from: 0 the element, 1 the object holding its array.</param>
/// <param name="Many">How a field the path reaches several times is kept.</param>
public sealed record DimensionElementField(
    string Name, string Path, DimensionValueKeep Keep = DimensionValueKeep.Value, int Up = 0, DimensionElementMany Many = DimensionElementMany.First)
{
    /// <summary>What joins several values of one field.</summary>
    public const string Separator = "; ";
}

/// <summary>What a clean step does.</summary>
public enum CleanStepKind
{
    /// <summary>Removes white space at both ends.</summary>
    Trim,

    /// <summary>Makes every run of white space one space.</summary>
    CollapseSpaces,

    /// <summary>Upper case, by the invariant culture's rules, so it never depends on the machine.</summary>
    Upper,

    /// <summary>Lower case, by the invariant culture's rules.</summary>
    Lower,

    /// <summary>Unicode normalization form C: composed characters.</summary>
    Nfc,

    /// <summary>Unicode normalization form KC: compatibility characters folded to their plain forms, then composed.</summary>
    Nfkc,

    /// <summary>Letters and digits kept in order, every run of anything else one hyphen, lower-cased: the cache's separator fold.</summary>
    FoldSeparators,

    /// <summary>A regular expression replaced, run without backtracking.</summary>
    Replace,

    /// <summary>A dictionary document looked up: the value it gives replaces the one looked up.</summary>
    Map,
}

/// <summary>What a <c>map</c> step does with a value the dictionary does not list.</summary>
public enum MapOtherwise
{
    /// <summary>The value goes on as it is.</summary>
    Keep,

    /// <summary>The value is left out of every member, with the reason kept on its original.</summary>
    LeaveOut,

    /// <summary>The value becomes <see cref="CleanStep.OtherwiseText"/>.</summary>
    Text,
}

/// <summary>One step a dimension cleans its originals by.</summary>
public sealed record CleanStep
{
    public required CleanStepKind Kind { get; init; }

    /// <summary>For <see cref="CleanStepKind.Replace"/>: the regular expression.</summary>
    public string? Pattern { get; init; }

    /// <summary>For <see cref="CleanStepKind.Replace"/>: what each match is replaced with, <c>$1</c> naming a group.</summary>
    public string? With { get; init; }

    /// <summary>For <see cref="CleanStepKind.Map"/>: the dictionary document looked up.</summary>
    public string? Dictionary { get; init; }

    /// <summary>
    /// For <see cref="CleanStepKind.Map"/>: the dictionary's field that gives the clean value; null takes the one field an entry
    /// holds beside its key.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>For <see cref="CleanStepKind.Map"/>: what a value the dictionary does not list comes to.</summary>
    public MapOtherwise Otherwise { get; init; } = MapOtherwise.Keep;

    /// <summary>For <see cref="MapOtherwise.Text"/>: the text an unlisted value becomes.</summary>
    public string? OtherwiseText { get; init; }

    /// <summary>The step as a person reads it, the way the document writes it.</summary>
    public string Describe()
    {
        switch (Kind)
        {
            case CleanStepKind.Replace:
                return $"replace /{Pattern}/ with '{With}'";
            case CleanStepKind.Map:
                var field = Field is null ? string.Empty : $" ({Field})";
                var otherwise = Otherwise switch
                {
                    MapOtherwise.LeaveOut => "leave it out",
                    MapOtherwise.Text => $"'{OtherwiseText}'",
                    _ => "keep",
                };
                return $"map through dictionary {Dictionary}{field}, otherwise {otherwise}";
            default:
                return Name(Kind);
        }
    }

    /// <summary>How a step without settings is written in a document.</summary>
    public static string Name(CleanStepKind kind) => kind switch
    {
        CleanStepKind.Trim => "trim",
        CleanStepKind.CollapseSpaces => "collapseSpaces",
        CleanStepKind.Upper => "upper",
        CleanStepKind.Lower => "lower",
        CleanStepKind.Nfc => "nfc",
        CleanStepKind.Nfkc => "nfkc",
        CleanStepKind.FoldSeparators => "foldSeparators",
        CleanStepKind.Replace => "replace",
        _ => "map",
    };
}
