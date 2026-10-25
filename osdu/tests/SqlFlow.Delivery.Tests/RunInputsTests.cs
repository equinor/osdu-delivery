using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a run of each of the module's flow kinds may be given (osdu/docs/reference/flow/parameters.md): the parameters a
/// document declares, with their choices and a delivery flow's lookups, checked as the document loads; the one rule every
/// run resolves its values by, choices enforced; and what each kind describes to a run form and a schedule's preview
/// (<see cref="KindRunInputs"/>): which operations resolve the parameters, the partition a run acts in, and a delivery
/// flow's first records of its scope.
/// </summary>
public sealed class RunInputsTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private const string Delivery = """
        flowType: delivery
        name: logs
        parameters:
          logSource: { required: true, description: The log source a run reads. }
          project: { default: PROJECT_A, choices: [PROJECT_A, PROJECT_B] }
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
            scope: { log_source: logSource }
            maxRecords: 100
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
          work: work/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/facade-api
          headers: { data-partition-id: dev }
          protocol: ddms
          protocolOptions: { payload: curves, recordMethod: POST }
        """;

    private const string Interfaces = """
        flowType: delivery
        name: estate
        parameters:
          logSource: { required: true }
          wellbore: { lookup: { column: facility_name, interface: wellbores } }
        source:
          connection: ${env:OSDU_DATA_DB}
          work: ../.work/{logSource}
        render:
          parameters:
            dataPartition: dev
        target:
          endpoint: ${env:OSDU_URL}
          headers: { data-partition-id: dev }
          protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
        interfaces:
          wellbores:
            record: { object: OsduData.silver.Wellbore, key: [facility_name] }
            mapping: Wellbore@1.0.0
          welllogs:
            record: { object: OsduData.silver.WellLog, key: [source_project, log_id], scope: { log_source: logSource }, maxRecords: 20 }
            bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
            mapping: WellLog@1.4.0
        """;

    private static string WithProject(string project)
        => Delivery.ReplaceLineEndings("\n").Replace("  project: { default: PROJECT_A, choices: [PROJECT_A, PROJECT_B] }", "  project: " + project, StringComparison.Ordinal);

    private static readonly JsonSerializerOptions DefinitionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Refused(string yaml, string source = "flows/logs.yaml")
        => Assert.Throws<FlowValidationException>(() => Loader.ParseSource(yaml, source)).Message;

    [Fact]
    public void A_parameter_lists_its_choices_and_a_run_given_another_value_is_refused_by_name()
    {
        var flow = Loader.ParseFlow(Delivery, "flows/logs.yaml");
        Assert.Equal(["PROJECT_A", "PROJECT_B"], flow.Parameters["project"].Choices);
        Assert.Null(flow.Parameters["logSource"].Choices);

        var refused = Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "A", ["project"] = "PROJECT_C" }));
        Assert.Equal("flows/logs.yaml: parameter 'project' is given 'PROJECT_C', which is not one of its choices: PROJECT_A, PROJECT_B.", refused.Message);

        Assert.Equal("PROJECT_B", FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "A", ["project"] = "PROJECT_B" })["project"]);
        Assert.Equal("PROJECT_A", FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "A" })["project"]);

        // The scope parameter still takes several values, as its set.
        Assert.Equal(RunValueList.Of(["A", "B"]), FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = RunValueList.Of(["B", "A", "B"]) })["logSource"]);
    }

    [Theory]
    [InlineData("{ choices: [] }", "flows/logs.yaml: parameters.project.choices lists no values; list the values the parameter may take, or leave choices out.")]
    [InlineData("{ choices: [A, A] }", "flows/logs.yaml: parameters.project.choices lists 'A' more than once.")]
    [InlineData("{ choices: [\" A\"] }", "flows/logs.yaml: parameters.project.choices holds ' A'; every choice is a value a run gives, written without surrounding space.")]
    [InlineData("{ choices: [\"\"] }", "flows/logs.yaml: parameters.project.choices holds ''; every choice is a value a run gives, written without surrounding space.")]
    [InlineData("{ default: C, choices: [A, B] }", "flows/logs.yaml: parameters.project.default is 'C', which is not one of its choices: A, B.")]
    [InlineData("{ choices: [A], lookup: { column: source_project } }", "flows/logs.yaml: parameters.project lists choices and names a lookup; a parameter offers its own list or the values a column holds, so keep one of them.")]
    [InlineData("{ lookup: { } }", "flows/logs.yaml: parameters.project.lookup.column is missing: name the column of the record table whose values the parameter offers.")]
    public void A_parameters_choices_and_lookup_are_checked_as_the_document_loads(string project, string message)
        => Assert.Equal(message, Refused(WithProject(project)));

    [Fact]
    public void A_parameter_lists_at_most_five_hundred_choices()
    {
        var many = "{ choices: [" + string.Join(", ", Enumerable.Range(0, FlowParameterDeclarations.MaxChoices + 1).Select(i => "v" + i)) + "] }";
        Assert.Equal(
            "flows/logs.yaml: parameters.project.choices lists 501 values; a parameter lists at most 500. Leave choices out, so a run takes any value.",
            Refused(WithProject(many)));
        Assert.Equal(500, Loader.ParseFlow(WithProject("{ choices: [" + string.Join(", ", Enumerable.Range(0, 500).Select(i => "v" + i)) + "] }"), "f.yaml").Parameters["project"].Choices!.Count);
    }

    [Fact]
    public void A_parameter_written_with_no_body_is_optional_with_no_default_in_every_kind()
    {
        var flow = Loader.ParseFlow(WithProject("~"), "flows/logs.yaml");
        Assert.Equal(new FlowParameter(), flow.Parameters["project"]);

        var retrieval = Loader.ParseRetrieval(Retrieval.Replace("parameters:\n  region: { choices: [north, south] }", "parameters:\n  region:", StringComparison.Ordinal), "r.yaml");
        Assert.Equal(new FlowParameter(), retrieval.Parameters["region"]);
    }

    [Fact]
    public void A_lookup_names_a_column_of_a_record_table_the_source_reads()
    {
        var source = Loader.ParseSource(Interfaces, "flows/estate.yaml");
        Assert.Equal(new FlowParameterLookup("facility_name", "wellbores"), source.Parameters["wellbore"].Lookup);
        Assert.Equal("wellbores", KindRunInputs.LookupInterface(source, "wellbore")!.Interface);
        Assert.Equal("welllogs", KindRunInputs.LookupInterface(source, "logSource")!.Interface);
        Assert.Null(KindRunInputs.LookupInterface(source, "nothing"));

        var text = Interfaces.ReplaceLineEndings("\n");
        Assert.Equal(
            "flows/estate.yaml: parameters.wellbore.lookup.interface names interface 'curves', which the flow does not declare; it declares wellbores, welllogs.",
            Refused(text.Replace("interface: wellbores }", "interface: curves }", StringComparison.Ordinal), "flows/estate.yaml"));
        Assert.Equal(
            "flows/estate.yaml: parameters.wellbore.lookup.column names a column '[facility]'; a column name is 1 to 128 characters, without brackets, control characters or surrounding space.",
            Refused(text.Replace("column: facility_name", "column: '[facility]'", StringComparison.Ordinal), "flows/estate.yaml"));
        Assert.Equal(
            "flows/estate.yaml: parameters.logSource.lookup: interfaces.welllogs.record.scope binds parameter 'logSource' to column 'log_source', whose values a run form offers for it; leave lookup out.",
            Refused(text.Replace("logSource: { required: true }", "logSource: { required: true, lookup: { column: log_source } }", StringComparison.Ordinal), "flows/estate.yaml"));
        Assert.Equal(
            "flows/logs.yaml: parameters.project.lookup.interface names interface 'logs', and the flow declares no interfaces: its one record table is where the column is read, so leave interface out.",
            Refused(WithProject("{ lookup: { column: source_project, interface: logs } }")));
    }

    [Fact]
    public void Only_a_delivery_flows_parameter_names_a_lookup()
    {
        var refused = Assert.Throws<FlowValidationException>(() => Loader.ParseRetrieval(
            Retrieval.Replace("region: { choices: [north, south] }", "region: { lookup: { column: region } }", StringComparison.Ordinal), "r.yaml"));
        Assert.Contains("lookup", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivery_flow_describes_its_parameters_its_reading_operations_and_its_first_records()
    {
        var inputs = new DeliveryFlowKind(Loader).DescribeRun(Delivery, "flows/logs.yaml");

        Assert.Equal(["logSource", "project"], inputs.Parameters.Select(p => p.Name));
        var logSource = inputs.Parameter("logSource")!;
        Assert.True(logSource.Required);
        Assert.True(logSource.TakesSeveral);
        Assert.Equal("The log source a run reads.", logSource.Description);
        Assert.Equal("the values of column log_source of OsduData.silver.WellLog, which source.record.scope binds it to", logSource.Lookup);
        var project = inputs.Parameter("project")!;
        Assert.Equal(("PROJECT_A", false, null), (project.Default, project.TakesSeveral, project.Lookup));
        Assert.Equal(["PROJECT_A", "PROJECT_B"], project.Choices);
        Assert.Equal(FlowParameters.ScopeSeveralValuesRule, inputs.SeveralValuesRule);

        // Only the operations that read the ingestion tables resolve the parameters; the others take what they are given.
        Assert.Equal(["deliver", "plan", "intake", "replan"], inputs.ParameterOperations);
        Assert.True(inputs.TakesParameters(DeliveryOperations.Replan));
        Assert.False(inputs.TakesParameters(DeliveryOperations.Verify));

        // A flow whose partition is its header's takes none from a run.
        Assert.Empty(inputs.KindValues);

        var setting = Assert.Single(inputs.Settings);
        Assert.Equal(
            (DeliveryRunPayload.MaxRecordsProperty, RunSettingInput.WholeNumber, "source.record.maxRecords", "100", 0L, 1000L),
            (setting.Key, setting.Input, setting.DocumentKey, setting.DocumentValue, setting.Minimum, setting.Maximum));
        Assert.Equal(inputs.ParameterOperations, setting.Operations);
        Assert.Equal("the run reads the scope whole", setting.WhenUnset);
    }

    [Fact]
    public void A_source_with_interfaces_describes_each_interfaces_first_records_and_a_partitioned_flow_its_partition()
    {
        var inputs = new DeliveryFlowKind(Loader).DescribeRun(Interfaces, "flows/estate.yaml");
        var setting = Assert.Single(inputs.Settings);
        Assert.Equal(("record.maxRecords of each interface", "wellbores none, welllogs 20"), (setting.DocumentKey, setting.DocumentValue));
        Assert.Equal("the values of column facility_name of OsduData.silver.Wellbore", inputs.Parameter("wellbore")!.Lookup);
        Assert.Equal("the values of column log_source of OsduData.silver.WellLog, which interfaces.welllogs.record.scope binds it to", inputs.Parameter("logSource")!.Lookup);

        var same = new DeliveryFlowKind(Loader).DescribeRun(Interfaces.Replace(", maxRecords: 20", string.Empty, StringComparison.Ordinal), "flows/estate.yaml");
        Assert.Equal(("record.maxRecords of every interface", null), (same.Settings[0].DocumentKey, same.Settings[0].DocumentValue));

        // A flow that names its partitions mints its ids in the one a run targets, so it sets no dataPartition of its own.
        var partitioned = new DeliveryFlowKind(Loader).DescribeRun(
            Delivery.ReplaceLineEndings("\n")
                .Replace("  headers: { data-partition-id: dev }\n", string.Empty, StringComparison.Ordinal)
                .Replace("  parameters: { dataPartition: dev }\n", string.Empty, StringComparison.Ordinal)
                .Replace("name: logs\n", "name: logs\npartitions: [dev, test]\n", StringComparison.Ordinal),
            "flows/logs.yaml");
        var partition = Assert.Single(partitioned.KindValues);
        Assert.Equal(PartitionNames.RunValue, partition.Name);
        Assert.StartsWith("The partition the run delivers in:", partition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_describes_its_parameters_and_the_partition_a_run_acts_in_when_it_works_in_partitions()
    {
        var retrieval = new RetrievalFlowKind(Loader).DescribeRun(Retrieval, "r.yaml");
        Assert.Equal(["north", "south"], Assert.Single(retrieval.Parameters).Choices);
        Assert.Empty(retrieval.KindValues);
        Assert.Empty(retrieval.ParameterOperations);

        var cache = new CacheFlowKind(Loader).DescribeRun(Cache, "c.yaml");
        Assert.Empty(cache.KindValues);
        var cachePartitioned = new CacheFlowKind(Loader).DescribeRun(Cache.Replace("  headers:\n    data-partition-id: dev\n", string.Empty, StringComparison.Ordinal), "c.yaml");
        Assert.EndsWith("Name * to fill every partition the flow serves, one after another.", Assert.Single(cachePartitioned.KindValues).Description, StringComparison.Ordinal);

        Assert.StartsWith("The partition the run tests:", Assert.Single(new AssertionFlowKind(Loader).DescribeRun(Assertion, "a.yaml").KindValues).Description, StringComparison.Ordinal);
        Assert.StartsWith("The partition the run builds in:", Assert.Single(new DimensionFlowKind(Loader).DescribeRun(Dimension, "d.yaml").KindValues).Description, StringComparison.Ordinal);
        Assert.StartsWith("The partition the run reads:", Assert.Single(new InventoryFlowKind(Loader).DescribeRun(Inventory, "i.yaml").KindValues).Description, StringComparison.Ordinal);
        var removal = new RemovalFlowKind(Loader).DescribeRun(Removal, "rm.yaml");
        Assert.StartsWith("The partition the run removes in:", Assert.Single(removal.KindValues).Description, StringComparison.Ordinal);
        Assert.Equal(("country", "US"), (Assert.Single(removal.Parameters).Name, removal.Parameters[0].Default));
        Assert.Empty(removal.Settings);
    }

    [Fact]
    public void A_document_that_does_not_read_is_refused_naming_the_document()
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryFlowKind(Loader).DescribeRun(WithProject("{ choices: [] }"), "flows/logs.yaml"));
        Assert.StartsWith("flows/logs.yaml: parameters.project.choices", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameters_values_are_looked_up_in_the_column_its_scope_binds_or_its_lookup_names()
    {
        var source = Loader.ParseSource(Interfaces, "flows/estate.yaml");
        Assert.Equal("log_source", ScopeValuesOperation.LookedUpIn(source.Interface("welllogs"), "logSource"));
        Assert.Equal("facility_name", ScopeValuesOperation.LookedUpIn(source.Interface("wellbores"), "wellbore"));
        Assert.Equal(
            "Flow 'estate/welllogs' looks up no values for parameter 'wellbore': its record table's scope does not bind it, and it names no lookup here.",
            Assert.Throws<SqlFlowException>(() => ScopeValuesOperation.LookedUpIn(source.Interface("welllogs"), "wellbore")).Message);
        Assert.Equal(
            "Flow 'estate/wellbores' declares no parameter 'nothing'.",
            Assert.Throws<SqlFlowException>(() => ScopeValuesOperation.LookedUpIn(source.Interface("wellbores"), "nothing")).Message);
    }

    [Fact]
    public void A_parameter_without_choices_or_a_lookup_is_stored_as_it_was_before_either_existed()
    {
        Assert.Equal("""{"required":true,"description":"The log source a run reads."}""", JsonSerializer.Serialize(Loader.ParseFlow(Delivery, "f.yaml").Parameters["logSource"], DefinitionJson));
    }

    [Fact]
    public void A_search_of_a_columns_values_takes_its_text_as_written_whatever_wildcards_it_holds()
    {
        Assert.Equal(@"%50\%\_A\[B\\%", IngestionSql.ContainsPattern(@"50%_a[b\"));
        Assert.Equal("%COMP%", IngestionSql.ContainsPattern("comp"));
    }

    private const string Retrieval = """
        flowType: retrieval
        name: wellbores-out
        parameters:
          region: { choices: [north, south] }
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          kind: "osdu:wks:master-data--Wellbore:1.0.0"
        target:
          location: ./out
        """;

    private const string Cache = """
        flowType: cache
        name: welldb-reference-cache
        source:
          endpoint: ${env:OSDU_URL}
          headers:
            data-partition-id: dev
        types:
          - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
            fields: [data.Code]
        """;

    private const string Assertion = """
        flowType: assertion
        name: t
        partitions: [dev]
        source: { endpoint: http://localhost }
        tests:
          - name: one
            kind: "osdu:wks:master-data--Wellbore:1.3.0"
            assert:
              - count: { atLeast: 1 }
        """;

    private const string Dimension = """
        flowType: dimension
        name: welldb-welllog-05-dimensions
        source:
          endpoint: http://localhost
        dimensions:
          - name: LogSource
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Source
        """;

    private const string Inventory = """
        flowType: inventory
        name: welllog-inventory
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: bearer
            secretRef: ${env:OSDU_TOKEN}
        inventories:
          - name: WellLogs
            kind: "*:*:work-product-component--WellLog:*"
        """;

    private const string Removal = """
        flowType: removal
        name: welldb-wellbore-05-removal
        partitions: [dev, test]
        parameters:
          country: { default: US }
        removals:
          - flow: welldb-wellbore-03-delivery
            keys:
              type: csv
              location: ../data/wellbore
              options: { delimiter: ",", header: true }
        """;
}
