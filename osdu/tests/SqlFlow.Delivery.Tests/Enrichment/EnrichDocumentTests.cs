using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The <c>enrich:</c> block of a mapping as its loader reads it (osdu/docs/reference/flow/enrich.md, The keys): each
/// enrichment's rows, mapping, key, hold and bulk data, in the order the document lists them moved after what each names under
/// <c>after:</c>, and what could never render refused when the mapping loads, naming the key. A delivery flow binds the rows and
/// files and names the workflows; an <c>enrich:</c> block in a flow is refused, saying where it goes now.
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

    private const string Workflow = """
          lasfile:
            stages:
              - workflow: csv_ingestion
                context:
                  id: "{dataset:enrich.lasfile}"
                  dataPartitionId: "{partition}"
                  data_service_to_use: file
            results:
              ids: "dev:master-data--Well:from-las"
              minimum: 1
        """;

    private static FlowValidationException Refused(string enrich)
        => Assert.Throws<FlowValidationException>(() => Owner(enrich: enrich));

    [Fact]
    public void A_mapping_without_enrich_declares_no_enrichment()
        => Assert.Empty(Owner().Enrich);

    [Fact]
    public void An_enrichment_is_read_with_its_rows_mapping_key_hold_and_description()
    {
        var mapping = Owner(enrich: """
            lasfile:
              description: The log's LAS file.
              rows: many
              mapping: LasDataset@1.0.0
              key: [file_name]
              onHold: step
            grid:
              rows: one
              bulk: true
            summary:
              mapping: Summary@1.0.0
            """);

        var (lasfile, grid, summary) = (mapping.Enrich[0], mapping.Enrich[1], mapping.Enrich[2]);
        Assert.Equal(
            ("lasfile", "The log's LAS file.", (EnrichRows?)EnrichRows.Many, "LasDataset@1.0.0", EnrichHold.Step, false),
            (lasfile.Name, lasfile.Description, lasfile.Rows, lasfile.Mapping, lasfile.OnHold, lasfile.Bulk));
        Assert.Equal(["file_name"], lasfile.Key);
        Assert.Equal(((EnrichRows?)EnrichRows.One, true, (string?)null), (grid.Rows, grid.Bulk, grid.Mapping));
        Assert.Equal(((EnrichRows?)null, EnrichHold.Record), (summary.Rows, summary.OnHold));
    }

    [Fact]
    public void Enrichments_render_in_document_order_each_moved_after_those_it_names()
    {
        var mapping = Owner(enrich: """
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

        Assert.Equal(["a", "b", "c", "d"], mapping.Enrich.Select(e => e.Name));
        Assert.Equal(["a"], mapping.Enrich[1].After);
    }

    [Theory]
    [InlineData("a:\n  mapping: A@1.0.0\n  after: [z]\n", "enrich.a.after names 'z', which is no enrichment of the mapping")]
    [InlineData("a:\n  mapping: A@1.0.0\n  after: [a]\n", "enrich.a.after names the enrichment itself")]
    [InlineData("a:\n  mapping: A@1.0.0\nb:\n  mapping: B@1.0.0\n  after: [a, a]\n", "enrich.b.after names 'a' more than once")]
    [InlineData("a:\n  mapping: A@1.0.0\n  after: [b]\nb:\n  mapping: B@1.0.0\n  after: [a]\n", "the enrichments a, b wait for each other through after:")]
    [InlineData("a:\n  mapping: A\n", "enrich.a.mapping 'A' must be pinned as 'Name@version'")]
    [InlineData("a:\n  description: no mapping\n", "enrich.a names no mapping and no bulk data")]
    [InlineData("a:\n  mapping: A@1.0.0\n  rows: all\n", "enrich.a.rows is 'all'")]
    [InlineData("a:\n  mapping: A@1.0.0\n  onHold: never\n", "enrich.a.onHold is 'never'")]
    [InlineData("a:\n  mapping: A@1.0.0\n  key: [file_name]\n", "enrich.a.key tells the datasets of an enrichment's rows apart, and it takes no rows")]
    [InlineData("a:\n  mapping: A@1.0.0\n  rows: one\n  key: [file_name]\n", "and it takes one row per record (rows: one)")]
    [InlineData("a:\n  mapping: A@1.0.0\n  rows: many\n  key: [file_name, FILE_NAME]\n", "enrich.a.key names a column more than once")]
    [InlineData("a:\n  bulk: true\n", "enrich.a.bulk names the record's bulk data by the enrichment's row, and it reads no rows")]
    [InlineData("a:\n  bulk: true\n  rows: many\n", "the enrichment takes many rows (rows: many); one supplying bulk data takes one row per record")]
    [InlineData("record:\n  mapping: A@1.0.0\n", "enrich names an enrichment 'record'")]
    [InlineData("9lives:\n  mapping: A@1.0.0\n", "enrich names an enrichment '9lives'")]
    [InlineData("a:\n  mapping: A@1.0.0\nA:\n  mapping: B@1.0.0\n", "enrich declares 'A' more than once")]
    [InlineData("{}\n", "enrich lists no enrichment")]
    public void An_enrichment_that_could_never_render_is_refused_naming_its_key(string enrich, string reason)
        => Assert.Contains(reason, Refused(enrich).Message, StringComparison.Ordinal);

    [Fact]
    public void An_empty_enrichment_is_refused_saying_what_it_names()
        => Assert.Contains("enrich.a is empty; name the enrichment mapping it renders", Refused("a:\n").Message, StringComparison.Ordinal);

    [Fact]
    public void More_enrichments_than_a_mapping_declares_are_refused()
    {
        var enrich = string.Concat(Enumerable.Range(0, EnrichStep.MaxSteps + 1).Select(i => $"s{i}:\n  mapping: S{i}@1.0.0\n"));

        Assert.Contains("a mapping declares at most 16", Refused(enrich).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_of_an_enrichment_is_refused_as_every_key_of_the_document_is()
        => Assert.Contains("mappings", Refused("a:\n  mapping: A@1.0.0\n  mappings: B@1.0.0\n").Message, StringComparison.Ordinal);

    [Fact]
    public void An_enrichment_mapping_declares_no_enrichments_of_its_own()
    {
        var document = FragmentDocument("data:\n  Symbol: { $from: grade }").Replace("enrichment: true\n", "enrichment: true\nenrich:\n  a:\n    mapping: A@1.0.0\n", StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "extra.yaml"));

        Assert.Contains("an enrichment mapping (enrichment: true) declares no enrichments of its own", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_of_a_DSPDM_row_declares_no_enrichment()
    {
        var document = OwnerDocument(enrich: "a:\n  mapping: A@1.0.0\n").Replace($"kind: {TestSchema.Kind}", "kind: test:dspdm:WELL:1.0.0", StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("enrich: adds to OSDU records, and test:dspdm:WELL:1.0.0 is a row of a DSPDM business object", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_declaring_enrich_is_refused_saying_the_mapping_declares_it_and_the_flow_binds_its_rows()
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(Head + "enrich:\n  lasfile:\n    mapping: A@1.0.0\n", "flows/demo.yaml"));

        Assert.Contains("enrich declares what is added to each record, which the mapping says now", refused.Message, StringComparison.Ordinal);
        Assert.Contains("bind its rows here under source.datasets.<name>", refused.Message, StringComparison.Ordinal);
        Assert.Contains("its workflow under workflows.<name>", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_without_workflows_runs_none_and_one_names_each_by_its_enrichment()
    {
        Assert.Empty(new DeliveryDocumentLoader().ParseFlow(Head, "flows/demo.yaml").EnrichWorkflows);

        var flow = new DeliveryDocumentLoader().ParseFlow(Head + "workflows:\n" + Workflow, "flows/demo.yaml");

        var (name, workflow) = Assert.Single(flow.EnrichWorkflows);
        Assert.Equal(("lasfile", "csv_ingestion"), (name, Assert.Single(workflow.Stages).Workflow));
        Assert.Null(flow.Enrich);
    }

    [Theory]
    [InlineData("  9lives:\n    stages: []\n", "workflows names '9lives', and each workflow there is named by the enrichment of the flow's mapping it runs over")]
    [InlineData("  a:\n    stages: []\n", "workflows.a.stages lists no stage")]
    public void A_workflow_is_named_by_an_enrichment_and_declared_as_the_routes_is(string workflows, string reason)
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(Head + "workflows:\n" + workflows, "flows/demo.yaml"));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_dataset_names_the_files_of_its_rows_with_their_defaults()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Head.Replace("""
                  join: { source_project: source_project, log_id: log_id }
            """.ReplaceLineEndings("\n"), """
                  join: { source_project: source_project, log_id: log_id }
                  files:
                    root: ../data/las
                    locationColumn: las_folder
                    hashColumn: las_hash
                    contentType: text/plain
            """.ReplaceLineEndings("\n"), StringComparison.Ordinal), "flows/demo.yaml");

        var files = flow.Source.Datasets["curves"].Files!;
        Assert.Equal(("../data/las", "las_folder", "*", "las_hash", "text/plain"), (files.Root, files.LocationColumn, files.Pattern, files.HashColumn, files.ContentType));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("locationColumn")]
    public void A_child_datasets_files_name_their_root_and_their_folder_column(string missing)
    {
        var files = string.Concat(new[] { "root: ../data/las", "locationColumn: las_folder", "hashColumn: las_hash" }
            .Where(line => !line.StartsWith(missing + ":", StringComparison.Ordinal))
            .Select(line => "        " + line + "\n"));
        var yaml = Head.Replace("      join: { source_project: source_project, log_id: log_id }\n", "      join: { source_project: source_project, log_id: log_id }\n      files:\n" + files, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "flows/demo.yaml"));

        Assert.Contains($"source.datasets.curves.files.{missing}", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_datasets_files_pattern_is_a_file_name_glob_without_a_path()
    {
        var yaml = Head.Replace(
            "      join: { source_project: source_project, log_id: log_id }\n",
            "      join: { source_project: source_project, log_id: log_id }\n      files:\n        root: ../data/las\n        locationColumn: las_folder\n        pattern: ../*.las\n",
            StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "flows/demo.yaml"));

        Assert.Contains("source.datasets.curves.files.pattern '../*.las' is a file name glob under each row's folder, without a path", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interface_names_workflows_of_its_own_and_the_source_level_refuses_them_and_enrich()
    {
        const string head = """
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
                datasets:
                  lasfile:
                    object: OsduData.silver.WellLogFile
                    join: { log_id: log_id }
                mapping: WellLog@1.4.0
                workflows:

            """;
        var source = head.ReplaceLineEndings("\n") + Indent(Workflow, 6) + "\n";

        var flow = Assert.Single(new DeliveryDocumentLoader().ParseSource(source, "flows/welldb.yaml").Interfaces);
        Assert.Equal("lasfile", Assert.Single(flow.EnrichWorkflows).Key);
        Assert.Contains("lasfile", flow.Source.Datasets.Keys);

        var workflows = source.Replace("interfaces:\n", "workflows:\n" + Workflow.ReplaceLineEndings("\n") + "\ninterfaces:\n", StringComparison.Ordinal);
        var refusedWorkflows = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(workflows, "flows/welldb.yaml"));
        Assert.Contains("workflows (the workflows run over an interface's enrichments are interfaces.<name>.workflows", refusedWorkflows.Message, StringComparison.Ordinal);

        var enrich = source.Replace("interfaces:\n", "enrich:\n  a:\n    mapping: A@1.0.0\ninterfaces:\n", StringComparison.Ordinal);
        var refusedEnrich = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(enrich, "flows/welldb.yaml"));
        Assert.Contains("enrich (what is added to an interface's records is declared in the enrich: block of its mapping", refusedEnrich.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interface_declaring_enrich_is_refused_naming_the_interfaces_key()
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
                    mapping: WellLogFile@1.0.0
            """;

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(source, "flows/welldb.yaml"));

        Assert.Contains("interfaces.logs.enrich declares what is added to each record", refused.Message, StringComparison.Ordinal);
        Assert.Contains("interfaces.logs.datasets.<name>", refused.Message, StringComparison.Ordinal);
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        var lines = text.ReplaceLineEndings("\n").Split('\n').Where(line => line.Trim().Length > 0).ToList();
        var least = lines.Min(l => l.Length - l.TrimStart(' ').Length);
        return string.Join("\n", lines.Select(line => pad + line[least..]));
    }
}
