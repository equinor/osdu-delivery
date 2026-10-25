using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A document's <c>parameters</c> in every one of the module's flow kinds (osdu/docs/reference/flow/parameters.md): each
/// rule of a parameter's <c>required</c>, <c>default</c>, <c>description</c> and <c>choices</c> holds the same in every kind,
/// each refusal naming the key it is about, and only a delivery flow's parameter names a <c>lookup</c>.
/// </summary>
[Trait(RunParameterSuite.Trait, RunParameterSuite.Name)]
public sealed class RunParameterDeclarationSuiteTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    public static TheoryData<string> Kinds() => FlowKindDocument.FlowTypes();

    /// <summary>Each refused declaration of parameter <c>region</c>, and the message every kind refuses it with.</summary>
    public static TheoryData<string, string, string> Refusals()
    {
        var cases = new (string Entry, string Message)[]
        {
            ("region: { choices: [] }", "flows/suite.yaml: parameters.region.choices lists no values; list the values the parameter may take, or leave choices out."),
            ("region: { choices: [north, north] }", "flows/suite.yaml: parameters.region.choices lists 'north' more than once."),
            ("region: { choices: [north, North] }", string.Empty),
            ("region: { choices: [\"north \"] }", "flows/suite.yaml: parameters.region.choices holds 'north '; every choice is a value a run gives, written without surrounding space."),
            ("region: { choices: [\"\"] }", "flows/suite.yaml: parameters.region.choices holds ''; every choice is a value a run gives, written without surrounding space."),
            ("region: { choices: [\" \"] }", "flows/suite.yaml: parameters.region.choices holds ' '; every choice is a value a run gives, written without surrounding space."),
            ("region: { default: west, choices: [north, south] }", "flows/suite.yaml: parameters.region.default is 'west', which is not one of its choices: north, south."),
            ("region: { default: North, choices: [north, south] }", "flows/suite.yaml: parameters.region.default is 'North', which is not one of its choices: north, south."),
            ("region: { choices: " + Many(501) + " }", "flows/suite.yaml: parameters.region.choices lists 501 values; a parameter lists at most 500. Leave choices out, so a run takes any value."),
        };
        var data = new TheoryData<string, string, string>();
        foreach (var kind in FlowKindDocument.All)
        {
            foreach (var (entry, message) in cases)
            {
                data.Add(kind.FlowType, entry, message);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_parameters_choices_are_held_to_the_same_rules_in_every_kind(string flowType, string entry, string message)
    {
        var kind = FlowKindDocument.Of(flowType);
        var yaml = kind.Document(entry);
        if (message.Length == 0)
        {
            // Choices are compared ordinally: values that differ only in case are two choices.
            Assert.Equal(["north", "North"], kind.Parameters(Loader, yaml)["region"].Choices);
            return;
        }

        Assert.Equal(message, Assert.Throws<FlowValidationException>(() => kind.Parameters(Loader, yaml)).Message);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_reads_a_parameters_declaration_the_same_way(string flowType)
    {
        var kind = FlowKindDocument.Of(flowType);
        var parameters = kind.Parameters(Loader, kind.Document(
            "region: { required: true, default: north, description: The region a run reads., choices: [north, south] }",
            "label:",
            "site: { required: true }",
            "sources: { choices: [\"1\", \"2\", \"3\"] }",
            "everything: { choices: " + Many(500) + " }"));

        Assert.Equal(
            new FlowParameter { Required = true, Default = "north", Description = "The region a run reads.", Choices = parameters["region"].Choices },
            parameters["region"]);
        Assert.Equal(["north", "south"], parameters["region"].Choices);
        Assert.Equal(new FlowParameter(), parameters["label"]);
        Assert.Equal(new FlowParameter { Required = true }, parameters["site"]);
        Assert.Equal(["1", "2", "3"], parameters["sources"].Choices);
        Assert.Equal(500, parameters["everything"].Choices!.Count);
        Assert.All(parameters.Values, p => Assert.Null(p.Lookup));

        // The document's order is the order a run form lists them in.
        var names = parameters.Keys.Where(n => n != "logSource").ToList();
        Assert.Equal(["region", "label", "site", "sources", "everything"], names);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Only_a_delivery_flows_parameter_names_a_lookup(string flowType)
    {
        var kind = FlowKindDocument.Of(flowType);
        var yaml = kind.Document("site: { lookup: { column: facility_name } }");
        if (kind.IsDelivery)
        {
            Assert.Equal(new FlowParameterLookup("facility_name", null), kind.Parameters(Loader, yaml)["site"].Lookup);
            return;
        }

        var refused = Assert.ThrowsAny<SqlFlowException>(() => kind.Parameters(Loader, yaml));
        Assert.Contains("lookup", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("site: { lookup: { column: facility_name }, choices: [a] }", "flows/suite.yaml: parameters.site lists choices and names a lookup; a parameter offers its own list or the values a column holds, so keep one of them.")]
    [InlineData("site: { lookup: { } }", "flows/suite.yaml: parameters.site.lookup.column is missing: name the column of the record table whose values the parameter offers.")]
    [InlineData("site: { lookup: { column: \" \" } }", "flows/suite.yaml: parameters.site.lookup.column is missing: name the column of the record table whose values the parameter offers.")]
    [InlineData("site: { lookup: { column: \"facility name \" } }", "flows/suite.yaml: parameters.site.lookup.column names a column 'facility name '; a column name is 1 to 128 characters, without brackets, control characters or surrounding space.")]
    [InlineData("site: { lookup: { column: \"a]b\" } }", "flows/suite.yaml: parameters.site.lookup.column names a column 'a]b'; a column name is 1 to 128 characters, without brackets, control characters or surrounding space.")]
    [InlineData("site: { lookup: { column: facility_name, interface: logs } }", "flows/suite.yaml: parameters.site.lookup.interface names interface 'logs', and the flow declares no interfaces: its one record table is where the column is read, so leave interface out.")]
    [InlineData("other: { lookup: { column: log_source } }", "")]
    public void A_delivery_flows_lookup_names_a_column_it_can_read(string entry, string message)
    {
        var kind = FlowKindDocument.Of(FlowDefinition.FlowTypeName);
        var yaml = kind.Document(entry);
        if (message.Length == 0)
        {
            // A parameter the scope does not bind may look its values up in the column the scope binds another to.
            Assert.Equal("log_source", kind.Parameters(Loader, yaml)["other"].Lookup!.Column);
            return;
        }

        Assert.Equal(message, Assert.Throws<FlowValidationException>(() => kind.Parameters(Loader, yaml)).Message);
    }

    [Fact]
    public void A_delivery_flows_scope_parameter_is_looked_up_in_the_column_its_scope_binds_and_names_no_lookup()
    {
        var kind = FlowKindDocument.Of(FlowDefinition.FlowTypeName);
        var yaml = kind.Document().Replace("logSource: { required: true }", "logSource: { required: true, lookup: { column: log_source } }", StringComparison.Ordinal);
        Assert.Equal(
            "flows/suite.yaml: parameters.logSource.lookup: source.record.scope binds parameter 'logSource' to column 'log_source', whose values a run form offers for it; leave lookup out.",
            Assert.Throws<FlowValidationException>(() => kind.Parameters(Loader, yaml)).Message);
    }

    private static string Many(int count) => "[" + string.Join(", ", Enumerable.Range(0, count).Select(i => "v" + i)) + "]";
}
