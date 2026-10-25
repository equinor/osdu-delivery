using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The one rule for a run's values (osdu/docs/reference/flow/parameters.md, How a run takes its values), for every one of
/// the module's flow kinds: what a run of the kind resolves from a document and the values it is given (the kind's own
/// resolution, as its executor calls it) is exactly what a run form and a schedule's preview explain for the same document
/// and values (the kind's description of the document, explained by SQLFlow's rule): the same refusal, word for word, or
/// the same values. Every value given, every default, every required parameter left out, every name nothing declares,
/// every value outside a parameter's choices and every list given where one value is taken.
/// </summary>
[Trait(RunParameterSuite.Trait, RunParameterSuite.Name)]
public sealed class RunParameterResolutionSuiteTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    /// <summary>The parameters every kind's document declares here, after a delivery flow's own <c>logSource</c>.</summary>
    private static readonly string[] Declared =
    [
        "region: { default: north, choices: [north, south] }",
        "label:",
        "site: { required: true }",
        "stage: { required: true, default: raw }",
        "country: { choices: [NO, UK] }",
    ];

    /// <summary>
    /// The values a run is given, by case name: each a list of name and value pairs, a value of several written as a
    /// <see cref="RunValueList"/>. A delivery flow is also given its scope parameter where a case gives <c>site</c>.
    /// </summary>
    private static readonly (string Name, (string Name, string Value)[] Values)[] Cases =
    [
        ("nothing", []),
        ("the required one", [("site", "s1")]),
        ("every one", [("site", "s1"), ("region", "south"), ("label", "x"), ("stage", "clean"), ("country", "UK")]),
        ("an empty value", [("site", ""), ("label", "")]),
        ("a value outside the choices", [("site", "s1"), ("region", "west")]),
        ("a value outside the choices in another case", [("site", "s1"), ("region", "North")]),
        ("a choice of a parameter without a default", [("site", "s1"), ("country", "SE")]),
        ("several values where one is taken", [("site", "s1"), ("region", RunValueList.Of(["north", "south"]))]),
        ("several values of the required one", [("site", RunValueList.Of(["s1", "s2"]))]),
        ("a list of one value", [("site", RunValueList.Of(["s1"]))]),
        ("a name nothing declares", [("site", "s1"), ("colour", "red")]),
        ("two names nothing declares", [("site", "s1"), ("zeta", "z"), ("alpha", "a")]),
        ("a name differing in case", [("site", "s1"), ("Region", "south")]),
        ("a required value left out but a default given", [("stage", "clean")]),
    ];

    public static TheoryData<string, string> KindsAndCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in FlowKindDocument.All)
        {
            foreach (var (name, _) in Cases)
            {
                data.Add(kind.FlowType, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndCases))]
    public void What_a_run_resolves_is_what_its_form_and_a_schedules_preview_explain(string flowType, string caseName)
    {
        var kind = FlowKindDocument.Of(flowType);
        var yaml = kind.Document(Declared);
        var given = Given(kind, caseName);

        var run = Run(kind, yaml, given);
        var inputs = kind.Kind(Loader).DescribeRun(yaml, "flows/suite.yaml");
        var explained = RunValueResolution.Explain(inputs, given);
        var problem = explained.FirstOrDefault(v => v.Problem is not null)?.Problem;

        if (run.Refusal is { } refusal)
        {
            Assert.Equal(refusal, $"flows/suite.yaml: {problem}");
            return;
        }

        Assert.Null(problem);
        var taken = explained
            .Where(v => v.Value is not null && v.Origin is RunValueOrigin.Given or RunValueOrigin.Default)
            .ToDictionary(v => v.Name, v => Set(v.Value!), StringComparer.Ordinal);
        Assert.Equal(taken, run.Values!.ToDictionary(v => v.Key, v => Set(v.Value), StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(KindsAndCases))]
    public void A_runs_refusal_names_the_parameter_and_what_it_takes(string flowType, string caseName)
    {
        var kind = FlowKindDocument.Of(flowType);
        var run = Run(kind, kind.Document(Declared), Given(kind, caseName));
        var several = kind.IsDelivery
            ? ", and takes one: only a parameter source.record.scope binds (logSource) takes several."
            : ", and takes one.";
        var expected = caseName switch
        {
            "nothing" => kind.IsDelivery
                ? "flows/suite.yaml: parameter 'logSource' is required. Supply it with --set logSource=value or the run's values."
                : "flows/suite.yaml: parameter 'site' is required. Supply it with --set site=value or the run's values.",
            "a value outside the choices" => "flows/suite.yaml: parameter 'region' is given 'west', which is not one of its choices: north, south.",
            "a value outside the choices in another case" => "flows/suite.yaml: parameter 'region' is given 'North', which is not one of its choices: north, south.",
            "a choice of a parameter without a default" => "flows/suite.yaml: parameter 'country' is given 'SE', which is not one of its choices: NO, UK.",
            "several values where one is taken" => "flows/suite.yaml: parameter 'region' is given 2 values" + several,
            "several values of the required one" => "flows/suite.yaml: parameter 'site' is given 2 values" + several,
            "a name nothing declares" => "flows/suite.yaml: parameter 'colour' is not declared under parameters.",
            "two names nothing declares" => "flows/suite.yaml: parameter 'alpha' is not declared under parameters.",
            "a name differing in case" => "flows/suite.yaml: parameter 'Region' is not declared under parameters.",
            "a required value left out but a default given" => "flows/suite.yaml: parameter 'site' is required. Supply it with --set site=value or the run's values.",
            _ => null,
        };

        Assert.Equal(expected, run.Refusal);
    }

    [Theory]
    [MemberData(nameof(KindsAndCases))]
    public void A_run_takes_each_given_value_else_its_default_and_an_empty_value_as_given(string flowType, string caseName)
    {
        var kind = FlowKindDocument.Of(flowType);
        var run = Run(kind, kind.Document(Declared), Given(kind, caseName));
        if (run.Values is not { } values)
        {
            return;
        }

        switch (caseName)
        {
            case "the required one":
                Assert.Equal(("s1", "north", "raw"), (values["site"], values["region"], values["stage"]));
                Assert.False(values.ContainsKey("label"));
                Assert.False(values.ContainsKey("country"));
                break;
            case "every one":
                Assert.Equal(("s1", "south", "x", "clean", "UK"), (values["site"], values["region"], values["label"], values["stage"], values["country"]));
                break;
            case "an empty value":
                // A run given an empty value takes it as given: the run form gives no value for a field left empty.
                Assert.Equal((string.Empty, string.Empty), (values["site"], values["label"]));
                break;
            case "a list of one value":
                Assert.Equal("s1", values["site"]);
                break;
            default:
                Assert.Fail($"Case '{caseName}' was expected to be refused, and the run took {string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"))}.");
                break;
        }
    }

    [Fact]
    public void A_delivery_flows_scope_parameter_takes_several_values_as_their_set_where_its_form_lists_them_as_given()
    {
        var kind = FlowKindDocument.Of(Model.FlowDefinition.FlowTypeName);
        var yaml = kind.Document(Declared);
        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = RunValueList.Of(["WIRELINE", "COMPOSITE", "WIRELINE"]), ["site"] = "s1" };

        Assert.Equal(RunValueList.Of(["COMPOSITE", "WIRELINE"]), kind.Resolve(Loader, yaml, given)["logSource"]);
        var explained = RunValueResolution.Explain(kind.Kind(Loader).DescribeRun(yaml, "flows/suite.yaml"), given);
        Assert.Equal(given["logSource"], explained.Single(v => v.Name == "logSource").Value);
        Assert.All(explained, v => Assert.Null(v.Problem));
    }

    private static Dictionary<string, string> Given(FlowKindDocument kind, string caseName)
    {
        var values = Cases.Single(c => c.Name == caseName).Values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        if (kind.IsDelivery && caseName != "nothing")
        {
            values["logSource"] = "COMPOSITE";
        }

        return values;
    }

    private static (IReadOnlyDictionary<string, string>? Values, string? Refusal) Run(FlowKindDocument kind, string yaml, IReadOnlyDictionary<string, string> given)
    {
        try
        {
            return (kind.Resolve(Loader, yaml, given), null);
        }
        catch (FlowValidationException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>A value as the set a run takes it as: each item once, in ordinal order.</summary>
    private static List<string> Set(string value)
        => [.. RunValueList.Items(value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
