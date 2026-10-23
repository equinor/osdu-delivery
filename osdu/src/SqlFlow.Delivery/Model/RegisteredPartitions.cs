using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Model;

/// <summary>One OSDU partition registered with the catalog (<c>osdu.Partition</c>): its name, what it is for, and whether it is the default.</summary>
/// <param name="Name">The data-partition-id, as runs name it and caches and ledgers are keyed by it.</param>
/// <param name="Description">What the partition is for, in the words of whoever registered it.</param>
/// <param name="IsDefault">True for the one partition a run that names none runs in.</param>
public sealed record RegisteredPartition(string Name, string? Description, bool IsDefault);

/// <summary>
/// The OSDU partitions registered with the catalog (osdu/docs/reference/concepts/partitions.md, The partition registry):
/// the partitions every run, cache and ledger of the module is keyed by, and the one a run that names none runs in. A flow
/// that names no partitions and no <c>data-partition-id</c> header serves every one of them, so the same documents deploy
/// to every environment; a flow that names its partitions serves those of them that are registered.
/// </summary>
public sealed class RegisteredPartitions
{
    /// <summary>No partition registered.</summary>
    public static readonly RegisteredPartitions None = new([]);

    public RegisteredPartitions(IEnumerable<RegisteredPartition> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        All = partitions.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (All.Count(p => p.IsDefault) > 1)
        {
            throw new DeliveryException(
                $"The partition registry marks {PartitionNames.Listed(All.Where(p => p.IsDefault).Select(p => p.Name))} as the default; one partition is the default.");
        }
    }

    /// <summary>Every registered partition, ordered by name.</summary>
    public IReadOnlyList<RegisteredPartition> All { get; }

    /// <summary>The registered names, ordered.</summary>
    public IReadOnlyList<string> Names => All.Select(p => p.Name).ToList();

    /// <summary>The partition a run that names none runs in, or null when none is the default.</summary>
    public string? Default => All.FirstOrDefault(p => p.IsDefault)?.Name;

    /// <summary>The registered name <paramref name="name"/> stands for, compared regardless of case, or null when it is not registered.</summary>
    public string? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wanted = name.Trim();
        return All.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    /// <summary>The message that refuses a partition the registry does not hold, saying how to register it.</summary>
    public string NotRegistered(string name)
        => $"Partition '{name}' is not registered with the catalog, so nothing runs in it. Name it in the control plane's configuration (Osdu:Partitions:{name}), or register it with 'sqlflow partition add {name}'"
            + (All.Count == 0 ? "." : $"; the registered partitions are {PartitionNames.Listed(Names)}.");
}

/// <summary>A partition the registry was asked about is not registered.</summary>
public sealed class PartitionNotRegisteredException : DeliveryException
{
    public PartitionNotRegisteredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads the partition registry. The module's database holds it, so every host that runs a delivery or cache flow can read
/// it; one started without that database answers with a registry that says so when read.
/// </summary>
public interface IPartitionRegistry
{
    /// <summary>The partitions registered now.</summary>
    Task<RegisteredPartitions> ReadAsync(CancellationToken ct = default);
}

/// <summary>A registry for a host without the module's database: reading it explains why nothing can be registered here.</summary>
public sealed class UnavailablePartitionRegistry : IPartitionRegistry
{
    public static readonly UnavailablePartitionRegistry Instance = new();

    private UnavailablePartitionRegistry()
    {
    }

    public Task<RegisteredPartitions> ReadAsync(CancellationToken ct = default)
        => throw new DeliveryException(
            $"The partition registry lives in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}");
}
