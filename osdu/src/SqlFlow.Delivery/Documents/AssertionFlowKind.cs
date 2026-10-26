using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// An assertion flow as the platform sees it: the OSDU endpoint is the source reference, the report its runs keep in the
/// module database the target, and the source's secret references are what the hygiene check inspects. It needs the
/// repository tree: its tests read OSDU and the module database, and the template their fields are checked against comes
/// from the template folders on the flow's path. Its lineage is the OSDU types its tests read
/// (<see cref="AssertionLineage"/>), so it runs after the flows that write them.
/// </summary>
public sealed record AssertionFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as an assertion flow's target: the report its runs keep in the module database.</summary>
    public const string ReportTarget = "report";

    public required AssertionFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => AssertionFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => ReportTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    /// <summary>
    /// A test that reads a field is checked against the template its kind has in the template folders on the flow's path
    /// (osdu/docs/reference/concepts/templates.md, What a document reads), so the node needs the repository's tree at the
    /// run's commit: a snapshot of the flow's one file holds no template.
    /// </summary>
    public override bool RequiresRepoTree => true;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OrderLineage.BindEveryPartition(AssertionLineage.Describe(Flow), context);
    }
}

/// <summary>The <c>flowType: assertion</c> document kind, registered in every host next to its executor.</summary>
public sealed class AssertionFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public AssertionFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => AssertionFlowDefinition.FlowTypeName;

    public string Description => "run qualified tests of what an OSDU partition holds (counts, values, references, schema conformance, bulk data) and keep every run's report";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Test, "Check", "Run the assertions (all of them, or those the payload names or tags) against what OSDU holds, and record the report.", WritesTarget: false),
        new(DeliveryOperations.Plan, "Plan", "Fit every assertion to the template of its kind and count what each would read, evaluating nothing and recording nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new AssertionFlowDocument { Flow = _loader.ParseAssertion(yaml, source) };
    }

    /// <summary>
    /// What an assertion flow's run may be given: its parameters, and the partition it tests when it works in partitions (<see cref="KindRunInputs"/>).
    /// </summary>
    public FlowRunInputs DescribeRun(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return DescribeAssertion(_loader.ParseAssertion(yaml, source));
    }

    private static FlowRunInputs DescribeAssertion(AssertionFlowDefinition flow)
        => KindRunInputs.Of(flow.Parameters, flow.Partitioned, "tests");

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseSeveralValues(parameters, AssertionFlowDefinition.FlowTypeName, scopeTakesSeveral: false);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, AssertionFlowDefinition.FlowTypeName, "a test reads what OSDU holds now; name the tests to run in the payload.");

        // A run names the tests it runs, by name or by tag, beside the central configuration.
        DeliveryRunPayload.Parse(parameters).RefuseOtherThan(
            AssertionFlowDefinition.FlowTypeName, [DeliveryRunPayload.TestsProperty, DeliveryRunPayload.TagsProperty]);
    }

    /// <summary>The operation an assertion flow runs: test (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Test) switch
        {
            DeliveryOperations.Test => DeliveryOperations.Test,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"An assertion flow runs the {DeliveryOperations.Test} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }
}
