using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a run form and a schedule's preview are told about a run of each of the module's flow kinds, for every operation of
/// the kind (<see cref="RunExplanation"/>, osdu/docs/reference/flow/parameters.md): an operation that resolves the flow's
/// parameters names each refusal its run would meet, one that does not takes what it is given and requires nothing, the
/// partition a flow that works in partitions acts in is the kind's own run value and refused for a flow that names none,
/// and a payload is checked as the kind checks a trigger's.
/// </summary>
[Trait(RunParameterSuite.Trait, RunParameterSuite.Name)]
public sealed class RunExplanationSuiteTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private static readonly YamlDocumentLoader Documents = YamlDocumentLoader.CreateDefault(FlowKindDocument.All.Select(k => k.Kind(Loader)).ToList());

    private const string Required = "site: { required: true }";

    public static TheoryData<string, string> KindsAndOperations()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in FlowKindDocument.All)
        {
            foreach (var operation in kind.Kind(Loader).Operations)
            {
                data.Add(kind.FlowType, operation.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndOperations))]
    public void An_operation_that_resolves_the_parameters_names_what_a_run_lacks_and_any_other_requires_nothing(string flowType, string operation)
    {
        var kind = FlowKindDocument.Of(flowType);
        var yaml = kind.Document(Required);
        var inputs = kind.Kind(Loader).DescribeRun(yaml, "flows/suite.yaml");
        var explained = RunExplanation.Explain(Documents, flowType, yaml, "flows/suite.yaml", operation, new Dictionary<string, string>(StringComparer.Ordinal), payload: null);

        var parameterProblems = explained.Problems.Where(p => p.StartsWith("parameter '", StringComparison.Ordinal)).ToList();
        if (inputs.TakesParameters(operation))
        {
            var first = kind.IsDelivery ? "logSource" : "site";
            Assert.Contains($"parameter '{first}' is required. Supply it with --set {first}=value or the run's values.", parameterProblems);
            Assert.Contains(explained.Values, v => v.Name == "site" && v.Origin == RunValueOrigin.Missing);
        }
        else
        {
            Assert.Empty(parameterProblems);
            Assert.Empty(explained.Values);
        }
    }

    [Theory]
    [MemberData(nameof(KindsAndOperations))]
    public void An_operation_that_takes_no_parameters_lists_the_values_it_is_given_and_refuses_none_of_them(string flowType, string operation)
    {
        var kind = FlowKindDocument.Of(flowType);
        var yaml = kind.Document(Required);
        var inputs = kind.Kind(Loader).DescribeRun(yaml, "flows/suite.yaml");
        if (inputs.TakesParameters(operation))
        {
            return;
        }

        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["colour"] = "red", ["site"] = RunValueList.Of(["a", "b"]) };
        var explained = RunExplanation.Explain(Documents, flowType, yaml, "flows/suite.yaml", operation, given, payload: null);
        Assert.Equal([("colour", RunValueOrigin.Given), ("site", RunValueOrigin.Given)], explained.Values.Select(v => (v.Name, v.Origin)));
        Assert.DoesNotContain(explained.Problems, p => p.StartsWith("parameter '", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(KindsAndOperations))]
    public void The_partition_is_a_run_value_of_a_flow_that_works_in_partitions_and_refused_for_one_that_names_none(string flowType, string operation)
    {
        var kind = FlowKindDocument.Of(flowType);
        var yaml = kind.Document(Required);
        var inputs = kind.Kind(Loader).DescribeRun(yaml, "flows/suite.yaml");
        if (!inputs.TakesParameters(operation))
        {
            return;
        }

        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["site"] = "s1", ["logSource"] = "COMPOSITE", [PartitionNames.RunValue] = "dev" };
        if (!kind.IsDelivery)
        {
            given.Remove("logSource");
        }

        var explained = RunExplanation.Explain(Documents, flowType, yaml, "flows/suite.yaml", operation, given, payload: null);
        var partition = explained.Values.Single(v => v.Name == PartitionNames.RunValue);
        if (inputs.KindValues.Count > 0)
        {
            Assert.Equal((RunValueOrigin.Kind, null), (partition.Origin, partition.Problem));
        }
        else
        {
            Assert.Equal((RunValueOrigin.NotDeclared, "parameter 'partition' is not declared under parameters."), (partition.Origin, partition.Problem));
        }
    }

    [Theory]
    [MemberData(nameof(KindsAndOperations))]
    public void A_payload_property_the_kind_does_not_take_is_refused_naming_the_kind_that_does(string flowType, string operation)
    {
        var kind = FlowKindDocument.Of(flowType);
        var payload = flowType == AssertionFlowDefinition.FlowTypeName ? """{"dimensions":["Wellbore"]}""" : """{"tests":["one"]}""";
        var explained = RunExplanation.Explain(
            Documents, flowType, kind.Document(Required), "flows/suite.yaml", operation, new Dictionary<string, string>(StringComparer.Ordinal), payload);
        Assert.Contains(explained.Problems, p => p.Contains(flowType == AssertionFlowDefinition.FlowTypeName ? "dimensions" : "tests", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delivery_runs_first_records_are_checked_on_the_operations_that_read_the_scope_and_refused_on_the_rest()
    {
        var kind = FlowKindDocument.Of(FlowDefinition.FlowTypeName);
        var yaml = kind.Document();
        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "COMPOSITE" };
        foreach (var operation in DeliveryFlowKind.DeliveryOperationList.Select(o => o.Name))
        {
            var problems = RunExplanation.Explain(Documents, FlowDefinition.FlowTypeName, yaml, "flows/suite.yaml", operation, given, """{"maxRecords":0}""").Problems;
            if (KindRunInputs.ReadingOperations.Contains(operation))
            {
                Assert.DoesNotContain(problems, p => p.Contains("maxRecords", StringComparison.Ordinal));
                Assert.Contains(
                    "payload maxRecords is 1001; a run reads the first 1 to 1,000 records of its scope, or 0 reads the scope whole.",
                    RunExplanation.Explain(Documents, FlowDefinition.FlowTypeName, yaml, "flows/suite.yaml", operation, given, """{"maxRecords":1001}""").Problems);
            }
            else
            {
                Assert.Contains(problems, p => p.StartsWith($"payload maxRecords does not apply to the {operation} operation", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void A_document_that_does_not_read_is_named_as_the_problem_and_offers_no_inputs()
    {
        var kind = FlowKindDocument.Of(RetrievalDefinition.FlowTypeName);
        var explained = RunExplanation.Explain(
            Documents, RetrievalDefinition.FlowTypeName, kind.Document("region: { choices: [] }"), "flows/suite.yaml", null,
            new Dictionary<string, string>(StringComparer.Ordinal), payload: null);

        Assert.Same(FlowRunInputs.None, explained.Inputs);
        Assert.Equal(
            "flows/suite.yaml: parameters.region.choices lists no values; list the values the parameter may take, or leave choices out.",
            explained.Problems[0]);
    }
}
