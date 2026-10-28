using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The <c>enrich:</c> block of a delivery flow as its loader reads it (osdu/docs/reference/flow/enrich.md, The enrich block):
/// each step's rows become a child dataset under its name, the steps are ordered by the document and their <c>after:</c>, and
/// what could never render is refused when the document loads, naming the key. A flow without the block loads as it always did.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class EnrichDocumentTests
{
    private const string Head = """
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
          datasets:
            curves:
              object: OsduData.silver.WellLogCurve
              join: { source_project: source_project, log_id: log_id }
          work: work
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/api
          headers: { data-partition-id: dev }
          protocol: storage

        """;

    private const string LasFile = """
          lasfile:
            description: The log's LAS file name.
            dataset:
              object: OsduData.silver.WellLogFile
              join: { source_project: source_project, log_id: log_id }
              orderBy: [file_name]
            mapping: WellLogFile@1.0.0
        """;

    private static FlowDefinition Parsed(string enrich, string head = Head) => new DeliveryDocumentLoader().ParseFlow(head + "enrich:\n" + enrich, "flows/demo.yaml");

    private static FlowValidationException Refused(string enrich, string head = Head)
        => Assert.Throws<FlowValidationException>(() => Parsed(enrich, head));

    [Fact]
    public void A_flow_without_enrich_has_no_steps_and_its_datasets_as_declared()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Head, "flows/demo.yaml");

        Assert.Null(flow.Enrich);
        Assert.Equal(["curves"], flow.Source.Datasets.Keys);
    }

    [Fact]
    public void A_steps_rows_are_a_child_dataset_under_its_name_beside_the_flows_own()
    {
        var flow = Parsed(LasFile);

        var step = Assert.Single(flow.Enrich!);
        Assert.Equal(("lasfile", "WellLogFile@1.0.0", true, EnrichRows.One, "The log's LAS file name."), (step.Name, step.Mapping, step.ReadsRows, step.Rows, step.Description));
        Assert.Equal(["curves", "lasfile"], flow.Source.Datasets.Keys.Order(StringComparer.Ordinal));
        var dataset = flow.Source.Datasets["lasfile"];
        Assert.Equal(("OsduData.silver.WellLogFile", "file_name"), (dataset.Object, Assert.Single(dataset.OrderBy)));
    }

    [Fact]
    public void A_step_without_a_dataset_reads_no_rows_and_rows_many_is_read()
    {
        var flow = Parsed("""
              summary:
                mapping: Summary@1.0.0
              aliases:
                dataset:
                  object: OsduData.silver.WellLogAlias
                  join: { source_project: source_project, log_id: log_id }
                rows: many
                mapping: Aliases@1.0.0
            """);

        Assert.False(flow.Enrich![0].ReadsRows);
        Assert.Equal(EnrichRows.Many, flow.Enrich[1].Rows);
        Assert.DoesNotContain("summary", flow.Source.Datasets.Keys);
    }

    [Fact]
    public void Steps_render_in_document_order_each_moved_after_the_steps_it_names()
    {
        var flow = Parsed("""
              c:
                mapping: C@1.0.0
                after: [b]
              a:
                mapping: A@1.0.0
              b:
                mapping: B@1.0.0
                after: [A]
              d:
                mapping: D@1.0.0
            """);

        var steps = flow.Enrich!;
        Assert.Equal(["a", "b", "c", "d"], steps.Select(s => s.Name));
        Assert.Equal(["a"], steps[1].After);
    }

    [Theory]
    [InlineData("  a:\n    mapping: A@1.0.0\n    after: [z]\n", "enrich.a.after names 'z', which is no step of enrich")]
    [InlineData("  a:\n    mapping: A@1.0.0\n    after: [a]\n", "enrich.a.after names the step itself")]
    [InlineData("  a:\n    mapping: A@1.0.0\n  b:\n    mapping: B@1.0.0\n    after: [a, a]\n", "enrich.b.after names 'a' more than once")]
    [InlineData("  a:\n    mapping: A@1.0.0\n    after: [b]\n  b:\n    mapping: B@1.0.0\n    after: [a]\n", "the steps a, b of enrich wait for each other through after:")]
    [InlineData("  a:\n    mapping: A\n", "enrich.a.mapping 'A' must be pinned as 'Name@version'")]
    [InlineData("  a:\n    description: no mapping\n", "enrich.a.mapping")]
    [InlineData("  a:\n    mapping: A@1.0.0\n    rows: many\n", "enrich.a.rows says how many rows of the step's dataset one record takes, and the step declares no dataset")]
    [InlineData("  a:\n    mapping: A@1.0.0\n    rows: all\n    dataset: { object: x.y.z, join: { source_project: source_project, log_id: log_id } }\n", "enrich.a.rows is 'all'")]
    [InlineData("  curves:\n    mapping: A@1.0.0\n", "enrich.curves is named as source.datasets.curves is")]
    [InlineData("  record:\n    mapping: A@1.0.0\n", "enrich names a step 'record'")]
    [InlineData("  9lives:\n    mapping: A@1.0.0\n", "enrich names a step '9lives'")]
    [InlineData("  a:\n    mapping: A@1.0.0\n  A:\n    mapping: B@1.0.0\n", "enrich declares 'A' more than once")]
    [InlineData("  {}\n", "enrich lists no step")]
    public void A_step_that_could_never_render_is_refused_naming_its_key(string enrich, string reason)
    {
        var refused = Refused(enrich);

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_steps_dataset_is_checked_as_a_child_dataset_and_named_by_the_key_the_document_writes()
    {
        var refused = Refused("""
              lasfile:
                dataset:
                  object: OsduData.silver.WellLogFile
                  join: { log_id: log_id }
                mapping: WellLogFile@1.0.0
            """);

        Assert.Contains("enrich.lasfile.dataset.join does not cover key column 'source_project'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_steps_than_a_flow_declares_are_refused()
    {
        var steps = string.Concat(Enumerable.Range(0, EnrichStep.MaxSteps + 1).Select(i => $"  s{i}:\n    mapping: S{i}@1.0.0\n"));

        Assert.Contains("a flow declares at most 16", Refused(steps).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_of_a_step_is_refused_as_every_key_of_the_document_is()
    {
        var refused = Refused("  a:\n    mapping: A@1.0.0\n    mappings: B@1.0.0\n");

        Assert.Contains("mappings", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dspdm", "the dspdm route writes rows of DSPDM business objects")]
    [InlineData("etp", "the etp route writes Energistics objects")]
    public void A_route_writing_no_OSDU_record_refuses_steps(string protocol, string reason)
    {
        var head = Head.Replace("protocol: storage", $"protocol: {protocol}", StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => Parsed("  a:\n    mapping: A@1.0.0\n", head));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interface_declares_steps_of_its_own_and_the_source_level_refuses_them()
    {
        const string source = """
            flowType: delivery
            name: welldb
            source:
              connection: ${env:OSDU_DATA_DB}
              work: work
            render:
              parameters: { dataPartition: dev }
            target:
              endpoint: https://example.org/api
              headers: { data-partition-id: dev }
            interfaces:
              logs:
                record:
                  object: OsduData.silver.WellLog
                  key: [log_id]
                mapping: WellLog@1.4.0
                enrich:
                  lasfile:
                    dataset:
                      object: OsduData.silver.WellLogFile
                      join: { log_id: log_id }
                    mapping: WellLogFile@1.0.0
            """;

        var flow = Assert.Single(new DeliveryDocumentLoader().ParseSource(source, "flows/welldb.yaml").Interfaces);
        Assert.Equal("lasfile", Assert.Single(flow.Enrich!).Name);
        Assert.Contains("lasfile", flow.Source.Datasets.Keys);

        var misplaced = source.Replace("interfaces:\n", "enrich:\n  a:\n    mapping: A@1.0.0\ninterfaces:\n", StringComparison.Ordinal);
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(misplaced, "flows/welldb.yaml"));
        Assert.Contains("enrich (the steps that extend an interface's records are interfaces.<name>.enrich", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interfaces_dataset_message_names_the_interfaces_key()
    {
        const string source = """
            flowType: delivery
            name: welldb
            source:
              connection: ${env:OSDU_DATA_DB}
              work: work
            render:
              parameters: { dataPartition: dev }
            target:
              endpoint: https://example.org/api
              headers: { data-partition-id: dev }
            interfaces:
              logs:
                record:
                  object: OsduData.silver.WellLog
                  key: [source_project, log_id]
                mapping: WellLog@1.4.0
                enrich:
                  lasfile:
                    dataset:
                      object: OsduData.silver.WellLogFile
                      join: { log_id: log_id }
                    mapping: WellLogFile@1.0.0
            """;

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(source, "flows/welldb.yaml"));

        Assert.Contains("interfaces.logs.enrich.lasfile.dataset.join does not cover key column 'source_project'", refused.Message, StringComparison.Ordinal);
    }
}
