using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// An inventory flow as the platform sees it: the OSDU endpoint is the source reference, the inventories its builds keep in the
/// module database the target, and the source's secret references are what the hygiene check inspects. It needs no repository
/// tree. Its lineage is the kinds it reads (<see cref="InventoryLineage"/>), so it runs after the flows that write them.
/// </summary>
public sealed record InventoryFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as an inventory flow's target: the inventories its builds keep in the module database.</summary>
    public const string InventoriesTarget = "inventories";

    public required InventoryFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => InventoryFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => InventoriesTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => false;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OrderLineage.BindEveryPartition(InventoryLineage.Describe(Flow), context);
    }
}

/// <summary>The <c>flowType: inventory</c> document kind, registered in every host next to its executor.</summary>
public sealed class InventoryFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public InventoryFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => InventoryFlowDefinition.FlowTypeName;

    public string Description => "keep every id and version OSDU kinds hold in a partition, and report where the ledgers disagree with it: orphans, missing records, undos left to do, and ids a ledger forgot; an operator selects its ids by what OSDU holds or finds its duplicate records by a key, and a flow that allows it removes the orphan, stale and forgotten ids an operator picks or selects";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Build, "Build", "Read every id each inventory's kind holds (all of them, or those the payload names), keep what changed, and compare every id with the ledgers of the partition.", WritesTarget: true),
        new(DeliveryOperations.Reconcile, "Reconcile", "Compare each inventory, as its last build left it, with the ledgers as they stand now, reading from storage only the ids a ledger expects.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Count the records each inventory would read, and say what would stop it, reading no id and keeping nothing.", WritesTarget: false),
        new(DeliveryOperations.Remove, "Remove", "Remove from OSDU the ids of one finding an inventory found (orphan, stale or forgotten), each checked again first, for a flow whose document allows it; the inventory's page asks for it.", WritesTarget: true),
        new(DeliveryOperations.Match, "Match", "Find which ids of an inventory hold what an operator's conditions ask in OSDU, through the search, and keep them as the selection the inventory's page opened; nothing in OSDU changes.", WritesTarget: false),
        new(DeliveryOperations.Duplicates, "Duplicates", "Find which ids of an inventory are copies of one another by the key the inventory's page opened a check with, read each group's copies from storage and pick the most complete to keep; nothing in OSDU changes.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new InventoryFlowDocument { Flow = _loader.ParseInventory(yaml, source) };
    }

    /// <summary>
    /// What an inventory flow's run may be given: its parameters, and the partition it reads when it works in partitions (<see cref="KindRunInputs"/>).
    /// </summary>
    public FlowRunInputs DescribeRun(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return DescribeInventory(_loader.ParseInventory(yaml, source));
    }

    private static FlowRunInputs DescribeInventory(InventoryFlowDefinition flow)
        => KindRunInputs.Of(flow.Parameters, flow.Partitioned, "reads");

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseSeveralValues(parameters, InventoryFlowDefinition.FlowTypeName, scopeTakesSeveral: false);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, InventoryFlowDefinition.FlowTypeName, "a build reads every id the kind holds; name the inventories to build in the payload.");
        // A run names the inventories it works on, a remove run what it removes and the partition it acts in, a match run the
        // selection it fills, and a duplicates run the check it fills, beside the central configuration; which operation takes
        // which is settled below.
        var payload = DeliveryRunPayload.Parse(parameters);
        payload.RefuseOtherThan(
            InventoryFlowDefinition.FlowTypeName,
            [DeliveryRunPayload.InventoriesProperty, DeliveryRunPayload.RemovalProperty, DeliveryRunPayload.ConfirmProperty, DeliveryRunPayload.SelectionProperty,
                DeliveryRunPayload.DuplicateCheckProperty]);

        var operation = Operation(parameters);
        if (operation == DeliveryOperations.Remove)
        {
            if (payload.Removal is null || payload.Inventories.Count != 1 || string.IsNullOrWhiteSpace(payload.Confirm))
            {
                throw new SqlFlowException(
                    "A remove run names the one inventory it removes from (inventories), what it removes (removal: finding, scope, expected, and ids or a selection when it takes those), and the partition it acts in (confirm); nothing is removed without all three.");
            }
        }
        else if (payload.Removal is not null || payload.Confirm is not null)
        {
            throw new SqlFlowException(
                $"payload {(payload.Removal is not null ? DeliveryRunPayload.RemovalProperty : DeliveryRunPayload.ConfirmProperty)} does not apply to the {operation} operation: only a remove run names what it removes and the partition it acts in.");
        }

        if (operation == DeliveryOperations.Match)
        {
            if (payload.SelectionId is null || payload.Inventories.Count != 1)
            {
                throw new SqlFlowException(
                    "A match run names the one inventory it matches (inventories) and the selection it fills (selection), which the inventory's page opens with the conditions to match; ask for a match from there.");
            }
        }
        else if (payload.SelectionId is not null)
        {
            throw new SqlFlowException(
                $"payload {DeliveryRunPayload.SelectionProperty} does not apply to the {operation} operation: only a match run names the selection it fills.");
        }

        if (operation == DeliveryOperations.Duplicates)
        {
            if (payload.DuplicateCheckId is null || payload.Inventories.Count != 1)
            {
                throw new SqlFlowException(
                    "A duplicates run names the one inventory it checks (inventories) and the check it fills (duplicateCheck), which the inventory's page opens with the key to compare the records by; ask for a check from there.");
            }
        }
        else if (payload.DuplicateCheckId is not null)
        {
            throw new SqlFlowException(
                $"payload {DeliveryRunPayload.DuplicateCheckProperty} does not apply to the {operation} operation: only a duplicates run names the check it fills.");
        }
    }

    /// <summary>The operation an inventory flow runs: build (its default), reconcile, plan, remove, match or duplicates.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Build) switch
        {
            DeliveryOperations.Build => DeliveryOperations.Build,
            DeliveryOperations.Reconcile => DeliveryOperations.Reconcile,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            DeliveryOperations.Remove => DeliveryOperations.Remove,
            DeliveryOperations.Match => DeliveryOperations.Match,
            DeliveryOperations.Duplicates => DeliveryOperations.Duplicates,
            var other => throw new SqlFlowException(
                $"An inventory flow runs the {DeliveryOperations.Build}, {DeliveryOperations.Reconcile}, {DeliveryOperations.Plan}, {DeliveryOperations.Remove}, {DeliveryOperations.Match} and {DeliveryOperations.Duplicates} operations; '{other}' is not one of them."),
        };
    }
}

/// <summary>
/// What an inventory flow contributes to SQLFlow's lineage (osdu/docs/reference/concepts/lineage.md, What each kind reads and
/// writes): it reads the kind (wildcards allowed) of each inventory on its platform and partition, and writes nothing another
/// flow reads. It is so ordered after the delivery flows writing the kinds it reads, and a schedule firing both builds the
/// inventory once the records have landed.
/// </summary>
public static class InventoryLineage
{
    /// <summary>
    /// Everything the flow contributes, the kinds once each in declaration order. A flow that names its partitions reads in every
    /// one of them; one that leaves them to the registry is described once, under the partition that stands for every registered one.
    /// </summary>
    public static RegisteredFlowLineage Describe(InventoryFlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"inventory flow '{flow.Name}'";
        var warnings = new List<string>();
        var partitions = flow.DeclaresPartitions && flow.Partition is null
            ? flow.Partitions
            : flow.FollowsRegistry && flow.Partition is null
                ? [PartitionNames.Every]
                : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings) is { } one ? [one] : [];

        var datasets = new List<DeclaredDataset>();
        foreach (var partition in partitions)
        {
            foreach (var inventory in flow.Inventories)
            {
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, inventory.Kind, who, $"inventory '{inventory.Name}'", warnings) is { } read
                    && !datasets.Contains(read))
                {
                    datasets.Add(read);
                }
            }
        }

        return new RegisteredFlowLineage { Datasets = datasets, Warnings = warnings };
    }
}
