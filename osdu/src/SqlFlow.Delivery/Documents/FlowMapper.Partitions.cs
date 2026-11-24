using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The partitions a delivery flow delivers to and the ledgers its flows keep
// (osdu/docs/reference/concepts/partitions.md, A ledger per partition; osdu/docs/reference/flow/interfaces.md, Ledger
// identity): the partitions the document names, the one that keeps the ledger the flow kept before it named them, the
// ledger a renamed flow or interface keeps, and the checks that no two interfaces share a ledger and that every
// ledger name fits the width the ledger keeps it in.
internal static partial class FlowMapper
{
    /// <summary>
    /// The top-level <c>keepLedger</c>: for a flow that leaves its partitions to the registry, the partition that keeps the
    /// ledger it kept before it served partitions (osdu/docs/reference/concepts/partitions.md, A ledger per partition). A
    /// flow that names its partitions marks it on the partition's entry instead. Null when the document names none.
    /// </summary>
    private static string? MapKeepLedger(string? declared, IReadOnlyList<DeclaredPartition> partitions, string source)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        if (partitions.Count > 0)
        {
            throw new FlowValidationException(
                $"{source}: keepLedger at the top of a flow names the partition that keeps its ledger when the flow leaves its partitions to the registry; this flow names them, so mark that partition with keepLedger: true on its entry under partitions.");
        }

        if (bool.TryParse(declared.Trim(), out _))
        {
            throw new FlowValidationException(
                $"{source}: keepLedger at the top of a flow names the partition that keeps the ledger the flow kept before it served partitions, such as keepLedger: dev; '{declared.Trim()}' is no partition.");
        }

        return PartitionNames.Check(declared, $"{source}: keepLedger");
    }

    /// <summary><paramref name="flow"/> keeping its own ledger in <paramref name="partition"/>, which only a flow that follows the registry names this way.</summary>
    private static FlowDefinition WithLedgerPartition(FlowDefinition flow, string? partition, string source)
    {
        if (partition is null)
        {
            return flow;
        }

        return flow.FollowsRegistry
            ? flow with { LedgerPartition = partition }
            : throw new FlowValidationException(
                $"{source}: keepLedger names the partition that keeps the flow's ledger once it serves partitions, and this flow's target.headers name its one partition, so it keeps one ledger. Take the data-partition-id header out to leave its partitions to the registry, or remove keepLedger.");
    }

    /// <summary>
    /// The partitions a delivery flow may deliver to (osdu/docs/reference/flow/delivery.md, Partitions): each a name, or a
    /// map naming it with its settings. Empty when the document names none. At most one partition keeps the ledger the
    /// flow kept before it named its partitions.
    /// </summary>
    private static IReadOnlyList<DeclaredPartition> MapPartitions(List<object>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        var partitions = new List<DeclaredPartition>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var where = string.Create(CultureInfo.InvariantCulture, $"{source}: partitions[{i}]");
            partitions.Add(declared[i] switch
            {
                IDictionary<object, object?> entry => MapPartition(entry, where),
                var scalar => new DeclaredPartition(PartitionNames.Check(PartitionScalar(scalar, where), where)),
            });
        }

        PartitionNames.CheckList(partitions.Select(p => p.Name).ToList(), $"{source}: partitions");
        var keeping = partitions.Where(p => p.KeepsLedger).Select(p => p.Name).ToList();
        if (keeping.Count > 1)
        {
            throw new FlowValidationException(
                $"{source}: partitions marks {string.Join(", ", keeping)} with keepLedger. The ledger the flow kept before it named its partitions holds the records delivered to one partition; mark that one.");
        }

        return partitions;
    }

    /// <summary>One partition written as a map: its <c>name</c>, and <c>keepLedger</c> when it keeps the flow's own ledger.</summary>
    private static DeclaredPartition MapPartition(IDictionary<object, object?> entry, string where)
    {
        string? name = null;
        var keepLedger = false;
        foreach (var (key, value) in entry)
        {
            switch (key?.ToString())
            {
                case "name":
                    name = PartitionScalar(value, $"{where}.name");
                    break;
                case "keepLedger":
                    keepLedger = value switch
                    {
                        bool flag => flag,
                        string text when bool.TryParse(text, out var parsed) => parsed,
                        _ => throw new FlowValidationException($"{where}.keepLedger must be true or false."),
                    };
                    break;
                default:
                    throw new FlowValidationException($"{where} has no '{key}' setting; a partition takes 'name' and 'keepLedger'.");
            }
        }

        return new DeclaredPartition(PartitionNames.Check(name, $"{where}.name"), keepLedger);
    }

    /// <summary>
    /// A partition name written as a YAML scalar. The loader keeps an unquoted scalar's type, so a name YAML reads as a
    /// number or a boolean is refused rather than turned back into text it may not have been written as.
    /// </summary>
    private static string PartitionScalar(object? value, string where) => value switch
    {
        null => throw new FlowValidationException($"{where} is empty; name the partition by its data-partition-id."),
        string text => text,
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        bool or double or float or decimal => throw new FlowValidationException(
            $"{where} '{Convert.ToString(value, CultureInfo.InvariantCulture)}' is read as a {(value is bool ? "boolean" : "number")}; write the partition name in quotes."),
        _ => throw new FlowValidationException($"{where} is neither a partition name nor a 'name'/'keepLedger' map."),
    };

    /// <summary>
    /// Every ledger name a flow that names its partitions records under fits the width the ledger keeps it in:
    /// <c>name@partition</c> for each partition that keeps a ledger of its own.
    /// </summary>
    private static void CheckPartitionLedgers(IReadOnlyList<FlowDefinition> flows, string source)
    {
        foreach (var flow in flows)
        {
            foreach (var partition in flow.Partitions.Where(p => !p.KeepsLedger))
            {
                CheckLedgerName($"{flow.OwnLedgerName}@{partition.Name}", $"the ledger name of '{flow.Label}' in partition '{partition.Name}'", source);
            }
        }
    }

    /// <summary>
    /// The ledger <paramref name="key"/> names (<c>ledger</c> of a flow in the single form, <c>interfaces.&lt;name&gt;.ledger</c>
    /// of an interface): an existing ledger, kept instead of the one the name would give, as a renamed flow keeps the
    /// ledger of its old name (osdu/docs/reference/flow/interfaces.md, Ledger identity). Null when the key is not written.
    /// </summary>
    private static string? MapLedger(string? ledger, string key, string keeper, string source)
    {
        if (ledger is null)
        {
            return null;
        }

        var name = ledger.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            throw new FlowValidationException($"{source}: {key} must name the flow whose ledger {keeper} keeps.");
        }

        return name;
    }

    /// <summary>Two interfaces of one document never share a ledger, and every ledger name fits where the ledger records it.</summary>
    private static void CheckLedgers(IReadOnlyList<FlowDefinition> flows, string source)
    {
        foreach (var flow in flows)
        {
            CheckLedgerName(flow.Label, $"the interface's name '{flow.Label}'", source);
            if (flow.AdoptedLedger is { } adopted)
            {
                CheckLedgerName(adopted, $"interfaces.{flow.Interface}.ledger", source);
            }
        }

        // Compared by the ledger each interface keeps before any partition: a flow that names its partitions keeps one ledger
        // per interface and partition, derived from that name, so interfaces apart there are apart in every partition.
        foreach (var group in flows.GroupBy(f => Identity.FlowId.Of(f.OwnLedgerName)))
        {
            var sharing = group.ToList();
            if (sharing.Count > 1)
            {
                throw new FlowValidationException(
                    $"{source}: the interfaces {string.Join(", ", sharing.Select(f => f.Interface))} would keep the same ledger ('{sharing[0].OwnLedgerName}'); each interface keeps a ledger of its own.");
            }
        }
    }

    /// <summary>A name the ledger records a flow under fits the width the ledger keeps it in.</summary>
    private static void CheckLedgerName(string name, string what, string source)
    {
        if (name.Length > FlowDefinition.MaxLedgerNameLength)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {what} is {name.Length} characters; the ledger records a flow under at most {FlowDefinition.MaxLedgerNameLength}."));
        }
    }
}
