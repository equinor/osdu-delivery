using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A scope parameter given several values (osdu/docs/reference/flow/delivery.md, Parameters): only a parameter
/// <c>source.record.scope</c> binds takes them, resolved to their set; the work root names the set, and a payload root, a
/// query token and the partition each take one value; the read selects the rows holding any of them; the recorded values
/// read back as a list; and a removal's <c>values</c> take a YAML sequence.
/// </summary>
public sealed class ScopeValueListTests
{
    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
          region: { default: north }
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
            scope: { log_source: logSource }
          datasets:
            curves:
              object: OsduData.silver.WellLogCurve
              join: { source_project: source_project, log_id: log_id }
              orderBy: [curve_ordinal]
          payloads:
            curves:
              root: curves/{logSource}
              locationColumn: curve_folder
              pattern: "chunk_*.parquet"
              hashColumn: payload_hash
          lastModified: update_date
          work: work/{region}/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/facade-api
          headers: { data-partition-id: dev }
          protocol: ddms
          protocolOptions: { payload: curves, recordMethod: POST }
        """;

    private static FlowDefinition Parsed() => new DeliveryDocumentLoader().ParseFlow(Flow, "flows/demo.yaml");

    private static Dictionary<string, string> Values(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private static string List(params string[] items) => RunValueList.Of(items);

    // ---- Resolving ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_scope_parameter_given_several_values_resolves_to_their_set()
    {
        var values = FlowParameters.Resolve(Parsed(), Values(("logSource", List("WIRELINE", "COMPOSITE", "WIRELINE"))));

        Assert.Equal(["COMPOSITE", "WIRELINE"], RunValueList.Items(values["logSource"]));
        Assert.Equal("north", values["region"]);

        // The same values named in another order are the same scope, and so keep the same watermark.
        var reordered = FlowParameters.Resolve(Parsed(), Values(("logSource", List("COMPOSITE", "WIRELINE"))));
        Assert.Equal(Planner.ScopeKey(values), Planner.ScopeKey(reordered));
        Assert.Equal("""logSource=["COMPOSITE","WIRELINE"];region=north""", Planner.ScopeKey(values));
    }

    [Fact]
    public void A_list_of_one_value_is_that_value()
    {
        var values = FlowParameters.Resolve(Parsed(), Values(("logSource", List("COMPOSITE", "COMPOSITE"))));
        Assert.Equal("COMPOSITE", values["logSource"]);
    }

    [Fact]
    public void A_parameter_the_scope_does_not_bind_takes_one_value()
    {
        var ex = Assert.Throws<FlowValidationException>(
            () => FlowParameters.Resolve(Parsed(), Values(("logSource", "COMPOSITE"), ("region", List("north", "south")))));
        Assert.Equal(
            "flows/demo.yaml: parameter 'region' is given 2 values, and takes one: only a parameter source.record.scope binds (logSource) takes several.",
            ex.Message);
    }

    [Fact]
    public void A_flow_kind_without_a_record_scope_takes_one_value_per_parameter()
    {
        var parameters = new Dictionary<string, FlowParameter>(StringComparer.Ordinal) { ["logSource"] = new() };
        var ex = Assert.Throws<FlowValidationException>(
            () => FlowParameters.Resolve(parameters, "flows/assertion.yaml", Values(("logSource", List("COMPOSITE", "WIRELINE")))));
        Assert.Equal("flows/assertion.yaml: parameter 'logSource' is given 2 values, and takes one.", ex.Message);
    }

    // ---- Locations and tokens ----------------------------------------------------------------------------------------

    [Fact]
    public void The_work_root_names_the_set_its_values_joined()
    {
        var flow = Parsed();
        var values = FlowParameters.Resolve(flow, Values(("logSource", List("WIRELINE", "COMPOSITE"))));

        var work = FlowParameters.WorkLocation(flow, values);

        Assert.EndsWith(Path.Combine("work", "north", "COMPOSITE+WIRELINE"), work, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_of_the_set_that_would_climb_out_of_the_work_root_is_refused()
    {
        var flow = Parsed();
        var values = FlowParameters.Resolve(flow, Values(("logSource", List("COMPOSITE", "../etc"))));
        Assert.Contains("must not contain", Assert.Throws<FlowValidationException>(() => FlowParameters.WorkLocation(flow, values)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_root_takes_one_value()
    {
        var flow = Parsed();
        var values = FlowParameters.Resolve(flow, Values(("logSource", List("COMPOSITE", "WIRELINE"))));

        var ex = Assert.Throws<FlowValidationException>(
            () => FlowParameters.ResolvePath(flow, flow.Source.Payloads["curves"].Root, values, "source.payloads.curves.root"));

        Assert.Equal(
            "flows/demo.yaml: parameter 'logSource' is given 2 values, and source.payloads.curves.root is a location that takes one: run one value at a time.",
            ex.Message);
    }

    [Fact]
    public void A_token_in_a_query_takes_one_value()
    {
        var ex = Assert.Throws<FlowValidationException>(
            () => FlowParameters.Substitute("data.Name:\"{logSource}\"", Values(("logSource", List("COMPOSITE", "WIRELINE")))));
        Assert.Contains("Parameter 'logSource' is given 2 values, and '{logSource}' fills 'data.Name:\"{logSource}\"', which takes one", ex.Message, StringComparison.Ordinal);
        Assert.Equal("data.Name:\"COMPOSITE\"", FlowParameters.Substitute("data.Name:\"{logSource}\"", Values(("logSource", "COMPOSITE"))));
    }

    // ---- The read ----------------------------------------------------------------------------------------------------

    private static IngestionLayout Layout(int values) => new()
    {
        Record = SourceObjectName.Parse("OsduData.silver.WellLog"),
        Key = [new SourceKeyColumn("log_id", "nvarchar(100)")],
        Updated = "UpdatedDate_DW",
        Scope = [new IngestionScope("log_source", "logSource", values), new IngestionScope("region_id", "region")],
    };

    [Fact]
    public void One_value_compares_the_column_with_it_and_several_select_the_rows_holding_any()
    {
        Assert.Contains("WHERE r.[log_source] = @scope0 AND r.[region_id] = @scope1", IngestionSql.KeyPage(Layout(1), after: false), StringComparison.Ordinal);
        Assert.Contains(
            "WHERE r.[log_source] IN (@scope0_0, @scope0_1, @scope0_2) AND r.[region_id] = @scope1",
            IngestionSql.KeyPage(Layout(3), after: false),
            StringComparison.Ordinal);
        Assert.Contains("r.[log_source] IN (@scope0_0, @scope0_1)", IngestionSql.CandidateCount(Layout(2), SourceSelectionKind.Incremental, hasLower: true), StringComparison.Ordinal);
    }

    // ---- What a run accepts ------------------------------------------------------------------------------------------

    [Fact]
    public void A_delivery_run_takes_several_values_for_a_parameter_and_one_partition()
    {
        var several = new RunParameters { Values = Values(("logSource", List("COMPOSITE", "WIRELINE"))) };
        DeliveryOperations.RefuseSeveralValues(several, FlowDefinition.FlowTypeName, scopeTakesSeveral: true);

        var partitions = new RunParameters { Values = Values(("partition", List("dev", "test"))) };
        var ex = Assert.Throws<SqlFlowException>(() => DeliveryOperations.RefuseSeveralValues(partitions, FlowDefinition.FlowTypeName, scopeTakesSeveral: true));
        Assert.Equal("partition is given 2 values; a run acts in one partition.", ex.Message);
    }

    [Fact]
    public void A_kind_whose_parameters_fill_queries_takes_one_value_per_parameter()
    {
        var several = new RunParameters { Values = Values(("logSource", List("COMPOSITE", "WIRELINE"))) };
        var ex = Assert.Throws<SqlFlowException>(() => DeliveryOperations.RefuseSeveralValues(several, AssertionFlowDefinition.FlowTypeName, scopeTakesSeveral: false));
        Assert.Equal("Parameter 'logSource' is given 2 values; a parameter of 'assertion' flows takes one, since it fills a query or a location.", ex.Message);
    }

    [Fact]
    public void The_values_a_submission_recorded_read_back_a_list_as_a_list()
    {
        var recorded = RunParameters.ValuesToJson(Values(("logSource", List("COMPOSITE", "WIRELINE")), ("region", "north")));
        Assert.Equal("""{"logSource":["COMPOSITE","WIRELINE"],"region":"north"}""", recorded);

        var read = DeliveryExecutor.ParseValues(recorded);
        Assert.Equal(["COMPOSITE", "WIRELINE"], RunValueList.Items(read["logSource"]));
        Assert.Equal("north", DeliveryExecutor.ParseValues("""{"region":"north"}""")["region"]);
        Assert.Contains("recorded parameters are not valid", Assert.Throws<DeliveryException>(() => DeliveryExecutor.ParseValues("[1]")).Message, StringComparison.Ordinal);
    }

    // ---- A removal's values ------------------------------------------------------------------------------------------

    private static RemovalFlowDefinition Removal(string values) => new DeliveryDocumentLoader().ParseRemoval($$"""
        flowType: removal
        name: welldb-welllog-04-removal
        partitions: [dev]
        parameters:
          source: { default: COMPOSITE }
        removals:
          - flow: welldb-welllog-03-header-delivery
            values: {{values}}
            keys:
              type: csv
              location: ../data/keys
              options: { delimiter: ",", header: true }
        """, "flows/removal.yaml");

    [Fact]
    public void A_removal_compares_under_a_list_of_values_written_as_a_sequence_each_as_written()
    {
        var entry = Assert.Single(Removal("{ logSource: [COMPOSITE, \"{source}\", 007] }").Entries);
        Assert.Equal(["COMPOSITE", "{source}", "007"], RunValueList.Items(entry.Values["logSource"]));

        var single = Assert.Single(Removal("{ logSource: 007 }").Entries);
        Assert.Equal("007", single.Values["logSource"]);
    }

    [Fact]
    public void A_removal_value_that_is_neither_a_value_nor_a_list_of_them_is_refused()
    {
        var nested = Assert.Throws<FlowValidationException>(() => Removal("{ logSource: [COMPOSITE, [WIRELINE]] }"));
        Assert.Contains("A list of values holds values only", nested.Message, StringComparison.Ordinal);
        var mapping = Assert.Throws<FlowValidationException>(() => Removal("{ logSource: { name: COMPOSITE } }"));
        Assert.Contains("A value is one scalar, or a list of them", mapping.Message, StringComparison.Ordinal);
        var undeclared = Assert.Throws<FlowValidationException>(() => Removal("{ logSource: [COMPOSITE, \"{nope}\"] }"));
        Assert.Contains("uses '{nope}', which is not declared under parameters", undeclared.Message, StringComparison.Ordinal);
    }
}
