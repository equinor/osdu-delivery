using SqlFlow.Delivery.Order;

namespace SqlFlow.Delivery.Ordering;

/// <summary>What checking one partition's order against a repository's waves found.</summary>
public sealed record PartitionOrderCheck(OrderPlan Plan, OrderCheckResult Result);

/// <summary>
/// For one repository, what checking its waves against the OSDU order of its own deliveries found
/// (osdu/docs/reference/concepts/osdu-order.md, Checking a repository's waves).
/// </summary>
public sealed record RepositoryOrderReport(Guid RepoId, IReadOnlyList<PartitionOrderCheck> Checks, IReadOnlyList<string> Problems, bool WavesComputed)
{
    /// <summary>Whether the repository's waves keep every reference the order keeps between its own flows.</summary>
    public bool Holds => Checks.All(c => c.Result.Holds);

    /// <summary>
    /// What the report says a repository's owner should act on, one line each, without repeats: every breach of the
    /// order, and every delivery the order could not read. A flow that serves every partition is checked once per
    /// partition it reaches, and says the same thing in each, so a finding repeated across partitions is said once.
    /// </summary>
    public IReadOnlyList<string> Warnings()
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in Checks)
        {
            foreach (var breach in check.Result.Breaches.Where(b => seen.Add(b.Why)))
            {
                lines.Add($"The OSDU order of {RepositoryOrder.Where(check.Plan.Partition)} is not kept: {breach.Why}");
            }
        }

        lines.AddRange(Problems.Where(seen.Add));
        return lines;
    }
}

/// <summary>
/// The OSDU order the catalog holds, and the check of one repository's waves against it. Lineage orders a repository's
/// flows by the order of its own deliveries, and a schedule runs flows of one repository in those waves, so the order a
/// repository's waves are checked against is the order of its own deliveries; the repository sync checks it after it
/// reconciles the repository's interfaces. The order of a partition as a whole, every repository's deliveries together,
/// is what the order pages show and what tells which schedules run before which (<see cref="ScheduleOrders"/>).
/// </summary>
public static class RepositoryOrder
{
    /// <summary>
    /// The order of every partition <paramref name="read"/>'s deliveries reach, with the deliveries of each repository
    /// labelled by the name <paramref name="repositoryNames"/> gives it. Two deliveries pinning one kind under different
    /// template versions read the first one's relationships, as lineage does.
    /// </summary>
    public static IReadOnlyList<OrderPlan> Plan(CatalogDeliveries read, IReadOnlyDictionary<Guid, string> repositoryNames)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(repositoryNames);
        var nodes = new Dictionary<string, SchemaNode>(StringComparer.OrdinalIgnoreCase);
        var deliveries = new List<OrderDelivery>(read.Deliveries.Count);
        foreach (var held in read.Deliveries)
        {
            if (held.Schema is { } schema)
            {
                nodes.TryAdd(schema.Kind, schema);
            }

            deliveries.Add(repositoryNames.TryGetValue(held.RepoId, out var name) ? held.Delivery with { RepositoryName = name } : held.Delivery);
        }

        return OsduOrder.PlanPartitions(deliveries, OsduSchemaGraph.Of(nodes.Values));
    }

    /// <summary>
    /// Checks the waves of repository <paramref name="repoId"/> against <paramref name="plans"/>, each partition its
    /// deliveries reach. <paramref name="waves"/> holds the wave lineage gave each of its active flows, by flow name; a flow
    /// lineage gave no wave is left out of it. When lineage gave none of them a wave (it failed, and said so), nothing is
    /// checked.
    /// </summary>
    public static RepositoryOrderReport Check(
        Guid repoId, IReadOnlyList<OrderPlan> plans, IReadOnlyDictionary<string, int> waves, IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(waves);
        ArgumentNullException.ThrowIfNull(problems);
        if (waves.Count == 0)
        {
            return new RepositoryOrderReport(repoId, [], problems, WavesComputed: false);
        }

        // The cycle members lineage placed in its fallback wave are not stored with the waves; two flows of one cycle share
        // that wave, so a reference between them is reported as not earlier, which is what it is.
        var repository = repoId.ToString("D");
        var ordered = new RepositoryWaves(repository, waves, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var checks = plans
            .Where(p => p.Deliveries.Any(d => string.Equals(d.Repository, repository, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new PartitionOrderCheck(p, OrderCheck.Check(p, ordered)))
            .ToList();
        return new RepositoryOrderReport(repoId, checks, problems, WavesComputed: true);
    }

    /// <summary>How messages name a partition: by name, or as every registered partition.</summary>
    public static string Where(string partition)
        => string.Equals(partition, OsduOrder.EveryPartition, StringComparison.Ordinal) ? "every registered partition" : $"partition '{partition}'";
}
