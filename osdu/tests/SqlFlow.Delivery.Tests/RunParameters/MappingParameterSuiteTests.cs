using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping's parameters (osdu/docs/reference/flow/mapping.md, parameters), which its flow gives under
/// <c>render.parameters</c>, follow the rule a run's values follow (<see cref="FlowParameters.ExplainMapping"/>): the value
/// the flow gives, else the mapping's default, else none, a required one without either and a value the mapping does not
/// declare each named. A mapping parameter takes any text, a value written as a list included, since nothing a run gives
/// reaches it but what the flow writes.
/// </summary>
[Trait(RunParameterSuite.Trait, RunParameterSuite.Name)]
public sealed class MappingParameterSuiteTests
{
    private static MappingDefinition Mapping()
        => new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument().ReplaceLineEndings("\n").Replace(
                "parameters:\n  dataPartition: { required: true }\n",
                "parameters:\n  dataPartition: { required: true }\n  unit: { default: m }\n  note: { description: Free text. }\n",
                StringComparison.Ordinal),
            "thing.yaml");

    public static TheoryData<string, string[], string[]> Cases() => new()
    {
        // given (name=value), then each parameter as name:origin in the mapping's order, then the names nothing declares.
        { "nothing", [], ["dataPartition:Missing", "unit:Default", "note:Unset"] },
        { "the required one", ["dataPartition=dev"], ["dataPartition:Given", "unit:Default", "note:Unset"] },
        { "every one", ["dataPartition=dev", "unit=ft", "note=n"], ["dataPartition:Given", "unit:Given", "note:Given"] },
        { "a list's text", ["dataPartition=[\"a\",\"b\"]"], ["dataPartition:Given", "unit:Default", "note:Unset"] },
        { "an undeclared one", ["dataPartition=dev", "zone=z"], ["dataPartition:Given", "unit:Default", "note:Unset", "zone:NotDeclared"] },
        { "only an undeclared one", ["zone=z"], ["dataPartition:Missing", "unit:Default", "note:Unset", "zone:NotDeclared"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_mappings_parameters_take_what_the_flow_gives_else_their_default_by_the_rule_a_run_follows(string caseName, string[] given, string[] expected)
    {
        Assert.False(string.IsNullOrEmpty(caseName));
        var supplied = given.Select(g => g.Split('=', 2)).ToDictionary(g => g[0], g => g[1], StringComparer.Ordinal);
        var explained = FlowParameters.ExplainMapping(Mapping(), supplied);

        Assert.Equal(expected, explained.Select(v => $"{v.Name}:{v.Origin}"));
        Assert.All(explained.Where(v => v.Origin is RunValueOrigin.Given or RunValueOrigin.Default or RunValueOrigin.Unset), v => Assert.Null(v.Problem));
        Assert.All(explained.Where(v => v.Origin is RunValueOrigin.Missing or RunValueOrigin.NotDeclared), v => Assert.NotNull(v.Problem));
        if (explained.SingleOrDefault(v => v.Name == "unit") is { Origin: RunValueOrigin.Default } unit)
        {
            Assert.Equal("m", unit.Value);
        }
    }
}
