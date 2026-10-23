using System.Text.Json.Serialization;
using SqlFlow.Core.Connections;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A removal flow (<c>flowType: removal</c>, osdu/docs/reference/flow/removal.md): reads the keys a source holds now, and
/// optionally a source listing keys the source removed, compares them with the ledger of each delivery flow it names, and
/// takes out of OSDU, through that delivery flow's own removal path and into its ledger, the records whose keys the source no
/// longer holds. It keeps no ledger of its own: everything it does is written to the delivery flow's ledger, under that
/// flow's ledger identity, whichever pipeline delivers the records (osdu/docs/wiki/decisions/0022-removal-flows.md).
/// </summary>
public sealed record RemovalFlowDefinition
{
    public const string FlowTypeName = "removal";

    /// <summary>The most removals one flow declares.</summary>
    public const int MaxEntries = 50;

    /// <summary>Path of the file the flow was loaded from, for error messages and the locations it anchors. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its runs are kept under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Parameters a removal's values and locations use as <c>{name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>
    /// The partitions the flow removes in (<c>partitions</c>), in document order; empty for a flow that names none, which removes
    /// in whichever partition a run names, among those its delivery flows serve.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>The partition this definition is bound to (<see cref="ForPartition"/>): the one a run removes in. Null before it is bound.</summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it removes in.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>True for a flow that names no partitions: it removes in every partition registered with the catalog.</summary>
    [JsonIgnore]
    public bool FollowsRegistry => Partitions.Count == 0;

    /// <summary>When a run stops before removing anything, for an operator to approve (<c>guard</c>).</summary>
    public RemovalGuard Guard { get; init; } = new();

    /// <summary>The removals, in the order the document declares them; names are unique within the flow ignoring case.</summary>
    public required IReadOnlyList<RemovalEntry> Entries { get; init; }

    /// <summary>
    /// Whether settling the partition a run removes in (<paramref name="requested"/>, or none) needs the registry: always for a
    /// flow that names no partitions, and for one that names several when none is named.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (string.IsNullOrWhiteSpace(requested) && Partitions.Count > 1);

    /// <summary>The partitions the flow removes in: those it names, or every registered one.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : registry.Names;
    }

    /// <summary>This definition bound to <paramref name="partition"/>, which it has to remove in.</summary>
    /// <exception cref="DeliveryException">The flow names its partitions and this is not one of them.</exception>
    public RemovalFlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Removal flow '{Name}' does not remove in partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Removal flow '{Name}' cannot remove in '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        return this with { Partition = declared };
    }

    /// <summary>
    /// The flow as a run reads it: the partition the run names, which the flow has to serve; when it names none, the registry's
    /// default when the flow serves it, else the flow's only partition. A run removes in one partition, since the ledgers it
    /// writes to are a partition's: <see cref="PartitionNames.Every"/> is refused.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow removes in.</exception>
    public RemovalFlowDefinition ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"Removal flow '{Name}' removes in one partition per run, since the ledgers it writes to are a partition's; name the partition instead of '{PartitionNames.Every}'.");
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
            ? $"No partition is registered with the catalog, so removal flow '{Name}' has none to remove in. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"Removal flow '{Name}' removes in {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run removes in.");
    }

    /// <summary>The removal named <paramref name="name"/> (case-insensitively), or null.</summary>
    public RemovalEntry? Entry(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Entries.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The removals a run carries out: those <paramref name="names"/> names, in document order; every one when it names none.</summary>
    /// <exception cref="DeliveryException">A name is not a removal of the flow.</exception>
    public IReadOnlyList<RemovalEntry> Select(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return Entries;
        }

        var unknown = names.Where(n => Entry(n) is null).ToList();
        if (unknown.Count > 0)
        {
            throw new DeliveryException(
                $"Removal flow '{Name}' has no removal named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; its removals are {string.Join(", ", Entries.Select(e => e.Name))}.");
        }

        return Entries.Where(e => names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The secret references the key sources declare, keyed by document path; references only, never values.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            foreach (var (side, keys) in Entries[i].Sources())
            {
                var path = $"removals[{i}].{side}";
                if (keys.Table is { } table)
                {
                    yield return new($"{path}.connection", table.Connection);
                }

                if (keys.Reader is { Location: { } location } && location.Contains("${", StringComparison.Ordinal))
                {
                    yield return new($"{path}.location", location);
                }
            }
        }
    }
}

/// <summary>
/// The names a removal of a removal flow takes (<c>removals[].name</c>, or the delivery flow's name with <c>/interface</c> after
/// it): what its runs, approvals and deletions are kept under, and what a run's payload names it by in <c>entries</c>.
/// </summary>
public static class RemovalNames
{
    /// <summary>The longest name, as the ledger keeps it.</summary>
    public const int MaxLength = Data.DeliveryModel.MaxRemovalNameLength;

    /// <summary>The rule, as messages state it.</summary>
    public const string Rule = "at most 300 characters, with no control character and no space at either end";

    /// <summary>Whether <paramref name="name"/> keeps to <see cref="Rule"/>.</summary>
    public static bool IsName(string? name)
        => name is { Length: > 0 and <= MaxLength } && !char.IsWhiteSpace(name[0]) && !char.IsWhiteSpace(name[^1]) && !name.Any(char.IsControl);
}

/// <summary>
/// When a removal run stops before it removes anything (<c>guard</c>, osdu/docs/reference/flow/removal.md, The guard): a
/// source that suddenly holds far fewer keys is more often a broken read than a real mass removal, so a run that would take
/// out more than the guard allows removes nothing until an operator approves the count it was shown.
/// </summary>
public sealed record RemovalGuard
{
    /// <summary>The share of the records a removal compares that it may take out without an approval, unless the flow says otherwise (SQLFlow's matchKeys default).</summary>
    public const int DefaultMaxPercent = 20;

    /// <summary>The share, 0 to 100, of the keys a removal compares (the ledger's records and the ingestion table's rows in its scope) it may take out.</summary>
    public int MaxPercent { get; init; } = DefaultMaxPercent;

    /// <summary>How many keys one removal may take out without an approval, whatever their share; null for no such bound.</summary>
    public long? MaxRecords { get; init; }
}

/// <summary>
/// One removal of a removal flow (<c>removals[]</c>): the delivery flow (and interface) whose ledger it compares and writes to,
/// the values of that flow's parameters the comparison is scoped by, and the sources of the keys: the keys the source holds
/// now (<see cref="Keys"/>), the keys it lists as removed (<see cref="Removed"/>), or both.
/// </summary>
public sealed record RemovalEntry
{
    /// <summary>The removal's name, unique in its flow ignoring case: the delivery flow's name (and interface) unless the document names it.</summary>
    public required string Name { get; init; }

    /// <summary>The delivery flow whose records it removes, by its name.</summary>
    public required string Flow { get; init; }

    /// <summary>The interface of that flow, for a source with interfaces; null for a flow in the single form.</summary>
    public string? Interface { get; init; }

    /// <summary>
    /// The values of the delivery flow's parameters the removal compares under (<c>values</c>), each a literal or a
    /// <c>{name}</c> token of this flow's parameters: they bind the delivery flow's record scope, so only the records planned
    /// under those values are compared.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether a record other records in OSDU still name stays there until they are gone (<c>keepReferenced</c>, the default).</summary>
    public bool KeepReferenced { get; init; } = true;

    /// <summary>The source's current, complete key set; null for a removal that reads only a source of removed keys.</summary>
    public RemovalKeySource? Keys { get; init; }

    /// <summary>A source of keys the source removed, each with the moment it was removed; null for none.</summary>
    public RemovalKeySource? Removed { get; init; }

    /// <summary>The document path of the removal, for messages.</summary>
    public required string Path { get; init; }

    /// <summary>The key sources the removal declares, by the key they are written under.</summary>
    public IEnumerable<(string Side, RemovalKeySource Source)> Sources()
    {
        if (Keys is { } keys)
        {
            yield return ("keys", keys);
        }

        if (Removed is { } removed)
        {
            yield return ("removed", removed);
        }
    }
}

/// <summary>
/// Where a removal reads keys from (<c>keys</c> or <c>removed</c>): a SQLFlow source reader (<see cref="Reader"/>: files of any
/// format SQLFlow reads, with their schema found as they are read) or a table or view of a source database
/// (<see cref="Table"/>), and the columns the record key is read from.
/// </summary>
public sealed record RemovalKeySource
{
    /// <summary>The files or dataset a SQLFlow source reader reads; null for a table source.</summary>
    public RemovalReaderSource? Reader { get; init; }

    /// <summary>The table or view of a source database; null for a reader source.</summary>
    public RemovalTableSource? Table { get; init; }

    /// <summary>
    /// The columns the record key is read from, in SQLFlow's transform column notation (<c>name</c>, <c>as</c>, <c>expr</c>):
    /// empty reads each of the delivery flow's record key columns from a column of the same name.
    /// </summary>
    public IReadOnlyList<RemovalKeyColumn> Columns { get; init; } = [];

    /// <summary>
    /// The column holding the moment a key was removed (<c>removedAt</c>), for a source of removed keys: required for a table
    /// source; a reader source without one takes each file's date (<c>FileDate_DW</c>).
    /// </summary>
    public RemovalKeyColumn? RemovedAt { get; init; }

    /// <summary>The document path of the source, for messages.</summary>
    public required string Path { get; init; }
}

/// <summary>Files or a dataset a SQLFlow source reader reads (<c>type</c>, <c>location</c>, <c>options</c>), exactly as a SQLFlow file flow's source.</summary>
public sealed record RemovalReaderSource
{
    /// <summary>The reader, as SQLFlow names it (<c>csv</c>, <c>xlsx</c>, <c>json</c>, <c>xml</c>, <c>parquet</c>, <c>duckdb</c>, ...).</summary>
    public required string Type { get; init; }

    /// <summary>The folder, file, glob or URI, as written: relative to the flow file unless rooted, a URI or a reference.</summary>
    public string? Location { get; init; }

    /// <summary>The reader's options, keys matched ignoring case.</summary>
    public IReadOnlyDictionary<string, string?> Options { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A table or view of a source database (<c>connection</c>, <c>provider</c>, <c>object</c>, <c>filter</c>), read through SQLFlow's source providers.</summary>
public sealed record RemovalTableSource
{
    /// <summary>The connection, as a <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference (or a SQL Server connection string holding no secret).</summary>
    public required string Connection { get; init; }

    /// <summary>The source system SQLFlow reads it with.</summary>
    public DataSourceKind Provider { get; init; } = DataSourceKind.MSSQL;

    /// <summary>The table or view (<c>object</c>), a three-part <c>[database].[schema].[object]</c> name.</summary>
    public required string ObjectName { get; init; }

    /// <summary>A predicate narrowing the rows read, carrying its own leading <c>AND</c>; null reads every row.</summary>
    public string? Filter { get; init; }
}

/// <summary>
/// One column a key is read from, in SQLFlow's transform column notation: the source column (<see cref="Name"/>), the record
/// key column it fills (<see cref="As"/>, the same name when left out) and an expression over it (<see cref="Expr"/>, with
/// <c>@ColName</c> standing for the source column) evaluated before the value is converted to the key column's type.
/// </summary>
public sealed record RemovalKeyColumn
{
    public required string Name { get; init; }

    public string? As { get; init; }

    public string? Expr { get; init; }

    /// <summary>The record key column the value fills.</summary>
    [JsonIgnore]
    public string Output => As ?? Name;
}

/// <summary>
/// An operator's approval of the removed keys a removal's guard held (the run payload's <c>approve</c>, with the removal in
/// <c>entries</c> and the partition in <c>confirm</c>): how many keys wait for approval, as the operator was shown them. The run
/// makes them pending when the ledger holds exactly that many, and takes them out; what it finds removed besides meets the
/// guard on its own.
/// </summary>
public sealed record RemovalApproval
{
    /// <summary>How many removed keys the operator was shown waiting for approval.</summary>
    public required long Expected { get; init; }
}
