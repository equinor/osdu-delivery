using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Data;

// The database's own: the version of the osdu schema it holds, and the central configuration the control plane supplies
// to the runs it queues.

/// <summary>
/// The single row that says which version of the osdu schema a database holds: the module version and the last migration
/// applied, when and by whom, and the oldest SQLFlow catalog migration this schema works with. Written by the module
/// database's migrate step in the same connection as the migrations, and read by every host at startup.
/// </summary>
public sealed class OsduSchemaVersion
{
    /// <summary>Always 1: the table holds exactly one row.</summary>
    public int Id { get; set; } = 1;

    public string ModuleVersion { get; set; } = string.Empty;

    public string LastMigration { get; set; } = string.Empty;

    public DateTime AppliedUtc { get; set; }

    /// <summary>The host and actor that ran the migrate.</summary>
    public string AppliedBy { get; set; } = string.Empty;

    /// <summary>The oldest SQLFlow catalog migration the schema needs beside it.</summary>
    public string MinimumCatalogMigration { get; set; } = string.Empty;
}

/// <summary>
/// What a central configuration property may be called and hold, and how many of them one run carries. The bounds are
/// here rather than at each caller so the store, the API, the CLI and the run payload all refuse the same things.
/// </summary>
public static partial class DeliveryConfigNames
{
    /// <summary>The longest accepted property name, which is an environment variable name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest accepted value. A value is an identifier, a URL or a reference, never a document.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The longest accepted description of what a property is for: the width of its column.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The most properties one run carries, which bounds what a queued run writes into its payload.</summary>
    public const int MaxPerRun = 64;

    /// <summary>Whether <paramref name="name"/> can name a property: an environment variable name, which is what a flow spells it as.</summary>
    public static bool IsName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    /// <summary>
    /// Whether <paramref name="value"/> can be held as a property: non-empty, within the bound and free of the control
    /// characters that would make a value unprintable in a log line or a run payload.
    /// </summary>
    public static bool IsValue(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// One property of the central configuration: a value the control plane supplies to the runs it queues, so a flow that
/// names <c>${env:NAME}</c> resolves it from here rather than from whatever the node that picks the run up happens to
/// hold. A property is set once for the whole control plane (<see cref="RepoId"/> null) and may be set again for one
/// repository, which is an estate: the repository's value wins for the flows that repository holds. Either may also be set
/// for one OSDU partition (<see cref="Partition"/>), and a run of a flow bound to that partition resolves with it first.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is a non-secret value (an entitlements group, a legal tag, a base URL) or a
/// <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, which travels unresolved and is resolved on the node.
/// A literal secret here is a defect: this row, and the run payload it is carried in, are ordinary catalog content.
/// </para>
/// <para>
/// For a run bound to partition P the value of a name is taken from the first scope that sets it: the repository's for P,
/// the control plane's for P, the repository's, the control plane's (osdu/docs/reference/concepts/environment-variables.md,
/// Where a reference resolves from). A value set for a partition always wins over one that is not, so an estate-wide
/// endpoint never sends a run of one partition to another's platform.
/// </para>
/// </remarks>
public sealed class DeliveryConfigProperty
{
    public Guid Id { get; set; }

    /// <summary>The repository whose flows this value applies to, or null for the control plane's own value.</summary>
    public Guid? RepoId { get; set; }

    /// <summary>
    /// The OSDU partition whose runs this value applies to, by its data-partition-id, or null for a value that applies
    /// whatever the partition.
    /// </summary>
    public string? Partition { get; set; }

    /// <summary>The reference name a flow spells as <c>${env:NAME}</c>, held as written.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The value, or a reference the node resolves.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>What this property is for, shown wherever it is listed.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True while the control plane's configuration (<c>Osdu:Partitions:&lt;partition&gt;:Properties</c>) sets this value: the
    /// control plane wrote it from there, and only the configuration changes or removes it. A value the configuration stops
    /// setting stays with this cleared.
    /// </summary>
    public bool IsConfigured { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who set it last.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}
