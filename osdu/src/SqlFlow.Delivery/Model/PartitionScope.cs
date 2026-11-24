using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// How a kind of flow says what it does in a partition, in the words its refusals use: its kind (<c>Download</c>), the verb
/// for what it does there in both forms (<c>read</c> and <c>reads</c>, or <c>build in</c> and <c>builds in</c>), and why a run
/// takes one partition and never every registered one.
/// </summary>
public sealed record PartitionWords(string Kind, string Verb, string Verbs, string OnePartitionPerRun);

/// <summary>
/// The partitions a flow works in (osdu/docs/reference/concepts/partitions.md): those it names under <c>partitions</c>; every
/// registered one for a flow that names neither them nor a <c>data-partition-id</c> header; or, for a flow whose header names
/// it, that one partition. It settles the one partition a run takes, checks a partition a flow is bound to, and names the
/// ledger each partition keeps. One rule for the download, inventory, dimension and assertion flows, each saying it in its
/// own <see cref="PartitionWords"/>.
/// </summary>
/// <param name="Declared">The partitions the flow names, in document order; empty when it names none.</param>
/// <param name="FollowsRegistry">True for a flow that names neither its partitions nor a header: it works in every registered partition.</param>
/// <param name="Bound">The partition the flow is bound to, or null before it is bound and for a flow whose partition is its header's.</param>
/// <param name="Words">How the flow's kind says what it does in a partition.</param>
/// <param name="Flow">The flow's name, as its refusals and ledger name it.</param>
public sealed record PartitionScope(IReadOnlyList<string> Declared, bool FollowsRegistry, string? Bound, PartitionWords Words, string Flow)
{
    /// <summary>True when the flow names its partitions.</summary>
    public bool DeclaresPartitions => Declared.Count > 0;

    /// <summary>True when the flow works in partitions, named or registered, rather than the one its header names.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>
    /// Whether settling the partition a run takes (<paramref name="requested"/>, or none) needs the registry: always for a flow
    /// that follows it, and for one that names several partitions when none is named.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (DeclaresPartitions && string.IsNullOrWhiteSpace(requested) && Declared.Count > 1);

    /// <summary>The partitions the flow serves: those it names, every registered one for a flow that follows the registry, none for a header flow.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Declared : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// The partition binding the flow to <paramref name="partition"/> binds it to: the one the flow names, as it names it, or for
    /// a flow that follows the registry the partition itself, which has to be a data-partition-id.
    /// </summary>
    /// <exception cref="DeliveryException">The flow's partition is its header's, or the flow does not work in this partition.</exception>
    public string Bind(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"{Words.Kind} flow '{Flow}' names no partitions; it {Words.Verbs} the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        return DeclaresPartitions
            ? Declared.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"{Words.Kind} flow '{Flow}' does not {Words.Verb} partition '{wanted}'; it names {PartitionNames.Listed(Declared)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"{Words.Kind} flow '{Flow}' cannot {Words.Verb} '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
    }

    /// <summary>
    /// The partition a run takes: the one it names, which the flow has to serve; when it names none, the registry's default when
    /// the flow serves it, else the flow's only partition. Null for a flow whose partition is its header's, which a run takes as
    /// it is. A run takes one partition: <see cref="PartitionNames.Every"/> is refused.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow serves.</exception>
    public string? Settle(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? null
                : throw new DeliveryException(
                    $"{Words.Kind} flow '{Flow}' names no partitions; it {Words.Verbs} the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow {Words.Verbs} every registered partition.");
        }

        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"{Words.Kind} flow '{Flow}' {Words.Verbs} one partition per run, since {Words.OnePartitionPerRun}; name the partition to {Words.Verb} instead of '{PartitionNames.Every}'.");
        }

        if (wanted is not null)
        {
            return FollowsRegistry ? registry.Find(wanted) ?? throw new DeliveryException(registry.NotRegistered(wanted)) : wanted;
        }

        var served = Served(registry);
        if (registry.Default is { } fallback && served.Contains(fallback, StringComparer.OrdinalIgnoreCase))
        {
            return fallback;
        }

        if (DeclaresPartitions && Declared.Count == 1)
        {
            return Declared[0];
        }

        throw new DeliveryException(FollowsRegistry && registry.All.Count == 0
            ? $"No partition is registered with the catalog, so {Words.Kind.ToLowerInvariant()} flow '{Flow}' has none to {Words.Verb}. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"{Words.Kind} flow '{Flow}' {Words.Verbs} {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run {Words.Verbs}.");
    }

    /// <summary>The partition the flow is bound to.</summary>
    /// <exception cref="InvalidOperationException">The flow works in partitions and is bound to none.</exception>
    public string BoundPartition => Bound
        ?? throw new InvalidOperationException(
            DeclaresPartitions
                ? $"{Words.Kind} flow '{Flow}' {Words.Verbs} each of {PartitionNames.Listed(Declared)}; bind it to the partition a run {Words.Verbs} before asking for its ledger."
                : $"{Words.Kind} flow '{Flow}' {Words.Verbs} every registered partition; bind it to the partition a run {Words.Verbs} before asking for its ledger.");

    /// <summary>
    /// The ledger identity the flow keeps its rows under: derived from the name and the bound partition for a flow that works in
    /// partitions, so each partition keeps a ledger of its own, and from the name alone for a flow whose partition is its header's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow works in partitions and is bound to none.</exception>
    public Guid LedgerId => Partitioned ? Identity.FlowId.Of(Flow, BoundPartition) : Identity.FlowId.Of(Flow);

    /// <summary>The ledger's name as pages show it: <c>name@partition</c> for a flow that works in partitions, the name otherwise.</summary>
    /// <exception cref="InvalidOperationException">The flow works in partitions and is bound to none.</exception>
    public string LedgerName => Partitioned ? $"{Flow}@{BoundPartition}" : Flow;
}
