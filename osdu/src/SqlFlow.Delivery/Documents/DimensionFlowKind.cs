using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A dimension flow as the platform sees it: the OSDU endpoint is the source reference, the dimensions its builds keep in the
/// module database the target, and the source's secret references are what the hygiene check inspects. It always needs the
/// repository tree: a build reads how the index stores each field from the template folders on the flow's path, and a
/// cleaning step's dictionary from its file. Its lineage is the kinds its dimensions read and the dimensions it writes
/// (<see cref="DimensionLineage"/>).
/// </summary>
public sealed record DimensionFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as a dimension flow's target: the dimensions its builds keep in the module database.</summary>
    public const string DimensionsTarget = "dimensions";

    public required DimensionFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => DimensionFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => DimensionsTarget;

    /// <summary>The connection a flow naming <c>target.connection</c> writes its dimension tables and views on, so its
    /// pipeline names that server as its target (the connections page counts it, and a run's statements are attributed
    /// there); none for a flow that names no target.</summary>
    public override string? TargetConnectionReference => Flow.Target?.Connection;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    /// <summary>
    /// A build and a plan read the template of every kind a dimension's records are of from the template folders on the flow's
    /// path (osdu/docs/reference/concepts/templates.md, What a document reads), and a cleaning step's dictionary from its file,
    /// so the node needs the repository's tree at the run's commit: a snapshot of the flow's one file holds neither.
    /// </summary>
    public override bool RequiresRepoTree => true;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OrderLineage.BindEveryPartition(DimensionLineage.Describe(Flow, context), context);
    }

    /// <summary>The dictionary each map step cleans through, found and read as a build finds and reads it (<see cref="CompanionFiles.Dictionaries(DimensionFlowDefinition, string, DeliveryDocumentLoader)"/>).</summary>
    public override IReadOnlyList<string> CheckOffline(string documentPath)
        => CompanionFiles.Dictionaries(Flow, documentPath, new DeliveryDocumentLoader());
}

/// <summary>The <c>flowType: dimension</c> document kind, registered in every host next to its executor.</summary>
public sealed class DimensionFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public DimensionFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => DimensionFlowDefinition.FlowTypeName;

    public string Description => "read every distinct key of attributes of OSDU records, label and clean each into the value a person picks, and keep both with the search filter each stands for";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Build, "Build", "Load each dimension (all of them, or those the payload names) from the search index: in full, every distinct key read, labelled and cleaned into values; or, for a flow with an incremental block, the keys of the records that changed since the last build, read again. Keep what changed.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Check every dimension against the templates of the kinds it reads, count the records each would read, and say how a build would load it, reading no value and keeping nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new DimensionFlowDocument { Flow = _loader.ParseDimension(yaml, source) };
    }

    /// <summary>
    /// What a dimension flow's run may be given: its parameters, and the partition it builds in when it works in partitions (<see cref="KindRunInputs"/>).
    /// </summary>
    public FlowRunInputs DescribeRun(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return DescribeDimension(_loader.ParseDimension(yaml, source));
    }

    private static FlowRunInputs DescribeDimension(DimensionFlowDefinition flow)
        => KindRunInputs.Of(flow.Parameters, flow.Partitioned, "builds in");

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseSeveralValues(parameters, DimensionFlowDefinition.FlowTypeName, scopeTakesSeveral: false);

        // A full load, and the window an incremental load reads, are SQLFlow's own: a build takes them as SQLFlow's flows do.
        // Whether the flow loads incrementally, which a window needs, is the document's to say, and the run checks it.
        DeliveryOperations.RefuseBuiltInOverrides(
            parameters with { FullLoad = false, BackfillFrom = null, BackfillTo = null },
            DimensionFlowDefinition.FlowTypeName,
            "a build loads what the flow's incremental block says, in full with fullLoad or over a backfill window; name the dimensions to build in the payload.");

        // A run names the dimensions it builds or plans, beside the central configuration.
        DeliveryRunPayload.Parse(parameters).RefuseOtherThan(DimensionFlowDefinition.FlowTypeName, [DeliveryRunPayload.DimensionsProperty]);
    }

    /// <summary>The operation a dimension flow runs: build (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Build) switch
        {
            DeliveryOperations.Build => DeliveryOperations.Build,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A dimension flow runs the {DeliveryOperations.Build} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }
}
