using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A delivery flow bound to the enrichments its mapping declares (<see cref="EnrichBinding"/>, osdu/docs/reference/flow/enrich.md):
/// the mapping says what each enrichment is (paths of the record's own kind, or records of another kind), the flow binds its rows
/// (<c>source.datasets.&lt;name&gt;</c>), the files they name and the workflow it runs; and what the two cannot do together is
/// refused before anything is read or sent, naming the flow and the mapping's key.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class EnrichBindingTests
{
    private const string DocumentKind = "osdu:wks:work-product-component--Document:1.0.0";

    private const string Files = """
              files:
                root: ../data/las
                locationColumn: las_folder
                pattern: "*.las"
                hashColumn: las_hash
        """;

    private const string Workflow = """
        workflows:
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

    private static readonly MappingDefinition LasDataset = Fragment("data:\n  Name: { $from: file_name }", name: "LasDataset", kind: DatasetKind);
    private static readonly MappingDefinition Note = Fragment("data:\n  Name: { $from: file_name }", name: "Note", kind: DocumentKind);
    private static readonly MappingDefinition Grade = Fragment("data:\n  Symbol: { $from: grade }", name: "Grade");

    /// <summary>
    /// The test flow over the storage route, binding the child datasets <paramref name="datasets"/> (comma separated), the
    /// first with <paramref name="files"/> under it (none when empty), and the top-level blocks <paramref name="rest"/>.
    /// </summary>
    private static FlowDefinition Flow(string files = Files, string rest = "", string datasets = "lasfile")
    {
        var names = datasets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bound = string.Concat(names.Select((name, i) =>
            $"    {name}:\n      object: OsduData.silver.Thing{name}\n      join: {{ name: name }}\n"
            + (i == 0 && files.Length > 0 ? files.ReplaceLineEndings("\n").TrimEnd('\n') + "\n" : string.Empty)));
        var yaml = """
            flowType: delivery
            name: demo
            source:
              connection: ${env:OSDU_DATA_DB}
              record:
                object: OsduData.silver.Thing
                key: [name]
              datasets:

            """ + bound + """
              work: work
            render:
              mapping: Thing@1.0.0
              parameters: { dataPartition: dev }
            target:
              endpoint: https://example.org/api
              headers: { data-partition-id: dev }
              protocol: storage

            """ + rest;
        return new DeliveryDocumentLoader().ParseFlow(yaml, "flows/demo.yaml");
    }

    private static FlowDefinition Bind(FlowDefinition flow, string enrich, string data = "")
        => Bound(flow, Owner(data: data, enrich: enrich), LasDataset, Note, Grade);

    private static string Refused(FlowDefinition flow, string enrich, string data = "")
        => Assert.Throws<FlowValidationException>(() => Bind(flow, enrich, data)).Message;

    [Fact]
    public void A_mapping_without_enrichments_leaves_the_flow_as_it_is()
    {
        var flow = Flow(files: string.Empty);

        Assert.Same(flow, Bound(flow, Owner()));
        Assert.Null(Bound(flow, Owner()).Enrich);
    }

    [Fact]
    public void Each_enrichment_is_what_its_mappings_kind_says_with_the_rows_files_and_workflow_the_flow_binds()
    {
        var flow = Bind(Flow(rest: Workflow, datasets: "lasfile, grade, notes"), """
            lasfile:
              description: The log's LAS file.
              rows: one
              mapping: LasDataset@1.0.0
            grade:
              rows: one
              mapping: Grade@1.0.0
              after: [lasfile]
              onHold: step
            notes:
              rows: many
              key: [file_name]
              mapping: Note@1.0.0
            summary:
              mapping: Note@1.0.0
            """);

        var steps = flow.Enrich!;
        Assert.Equal(["lasfile", "grade", "notes", "summary"], steps.Select(s => s.Name));

        var lasfile = steps[0];
        Assert.Equal(("The log's LAS file.", true, EnrichRows.One, (string?)null), (lasfile.Description, lasfile.ReadsRows, lasfile.Rows, lasfile.Mapping));
        Assert.Equal((DatasetKind, "LasDataset@1.0.0", true, "las_hash"), (lasfile.Records!.Kind, lasfile.Records.Mapping, lasfile.Records.RegistersFiles, lasfile.Records.Files!.HashColumn));
        Assert.Equal("csv_ingestion", Assert.Single(lasfile.Workflow!.Stages).Workflow);

        var grade = steps[1];
        Assert.Equal(("Grade@1.0.0", (EnrichRecords?)null, EnrichHold.Step), (grade.Mapping, grade.Records, grade.OnHold));
        Assert.Equal(["lasfile"], grade.After);

        var notes = steps[2];
        Assert.Equal((DocumentKind, false, EnrichRows.Many), (notes.Records!.Kind, notes.Records.RegistersFiles, notes.Rows));
        Assert.Equal(["file_name"], notes.Records.Key);

        var summary = steps[3];
        Assert.Equal((false, DocumentKind), (summary.ReadsRows, summary.Records!.Kind));
        Assert.Equal([DatasetKind, DocumentKind], EnrichRecords.KindsOf(flow));
    }

    [Fact]
    public void Binding_a_bound_flow_again_changes_nothing()
    {
        var once = Bind(Flow(), "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n");

        Assert.Same(once, Bound(once, Owner()));
    }

    [Fact]
    public void An_enrichment_reading_rows_the_flow_does_not_bind_is_refused_naming_the_key_to_declare()
    {
        var message = Refused(Flow(files: string.Empty, datasets: "other"), "lasfile:\n  rows: one\n  mapping: Grade@1.0.0\n");

        Assert.Contains("enrich.lasfile of the mapping Thing@1.0.0 reads the rows of the child dataset lasfile, which the flow does not bind; declare source.datasets.lasfile", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_are_bound_by_the_enrichments_exact_name()
    {
        var message = Refused(Flow(files: string.Empty, datasets: "LasFile"), "lasfile:\n  rows: one\n  mapping: Grade@1.0.0\n");

        Assert.Contains("which the flow does not bind", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_a_child_dataset_names_that_no_enrichment_reads_are_refused()
    {
        Assert.Contains(
            "source.datasets.lasfile.files names the files of each row of lasfile, and no enrichment of the mapping Thing@1.0.0 reads those rows",
            Assert.Throws<FlowValidationException>(() => Bound(Flow(), Owner())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Files_beside_an_enrichment_adding_paths_are_refused_saying_what_files_become()
    {
        var message = Refused(Flow(), "lasfile:\n  rows: one\n  mapping: Grade@1.0.0\n");

        Assert.Contains("neither registers them (with a mapping of a dataset kind) nor names them as the record's bulk data (bulk: true)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_beside_records_of_a_kind_the_Dataset_service_registers_none_with_are_refused()
    {
        var message = Refused(Flow(), "lasfile:\n  rows: one\n  mapping: Note@1.0.0\n");

        Assert.Contains($"source.datasets.lasfile.files names files for each row, and Note@1.0.0 fills {DocumentKind}, which the Dataset service registers no files with", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dataset_kind_whose_rows_name_no_files_is_written_through_storage_as_it_renders()
    {
        var flow = Bind(Flow(files: string.Empty), "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n");

        var records = Assert.Single(flow.Enrich!).Records!;
        Assert.Equal((DatasetKind, false, (FlowPayload?)null), (records.Kind, records.RegistersFiles, records.Files));
    }

    [Fact]
    public void Registered_files_name_the_column_holding_their_hash()
    {
        var files = Files.ReplaceLineEndings("\n").Replace("\n        hashColumn: las_hash", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("hashColumn", files, StringComparison.Ordinal);

        var message = Refused(Flow(files: files), "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n");

        Assert.Contains("source.datasets.lasfile.files.hashColumn names the column holding the content hash of each row's files", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_from_many_rows_name_the_key_telling_them_apart()
    {
        var message = Refused(Flow(files: string.Empty), "lasfile:\n  rows: many\n  mapping: Note@1.0.0\n");

        Assert.Contains("key names the columns telling each row's record from the enrichment's other rows'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_beside_an_enrichment_adding_paths_is_refused()
    {
        var message = Refused(Flow(files: string.Empty), "lasfile:\n  rows: many\n  key: [file_name]\n  mapping: Grade@1.0.0\n");

        Assert.Contains("key tells apart the records an enrichment adds from many rows, and this one adds none; remove key", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enrichment_naming_a_mapping_that_keys_records_of_its_own_is_refused()
    {
        var keyed = TestSchema.Mapping();
        var owner = Owner(enrich: $"lasfile:\n  rows: one\n  mapping: {keyed.Reference}\n");

        var message = Assert.Throws<FlowValidationException>(() => Bound(Flow(files: string.Empty), owner, keyed)).Message;

        Assert.Contains($"names {keyed.Reference}, a mapping that keys records of its own; an enrichment renders an enrichment mapping (enrichment: true)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mapping_repeating_an_enrichments_rows_is_refused_since_they_are_the_enrichments()
    {
        var message = Refused(
            Flow(files: string.Empty),
            "lasfile:\n  rows: many\n  key: [file_name]\n  mapping: Note@1.0.0\n",
            data: "Curves:\n  $forEach: lasfile\n  $item:\n    CurveID: { $from: file_name }");

        Assert.Contains("the mapping repeats the rows of lasfile with $forEach, and they are the enrichment's own rows", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_an_enrichment_adding_paths_is_refused_since_it_adds_no_record()
    {
        var message = Refused(Flow(files: string.Empty), "lasfile:\n  rows: one\n  mapping: Grade@1.0.0\n", data: "Aliases: [{ $link: lasfile }]");

        Assert.Contains("links the records of enrich.lasfile with $link, and that enrichment adds none: its mapping fills the record's own kind", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_naming_no_enrichment_of_the_mapping_is_refused()
    {
        var none = Assert.Throws<FlowValidationException>(() => Bound(Flow(files: string.Empty, rest: Workflow), Owner())).Message;
        Assert.Contains("workflows.lasfile runs a workflow over the records of an enrichment 'lasfile', and the mapping Thing@1.0.0 declares no enrichment", none, StringComparison.Ordinal);

        var other = Refused(Flow(files: string.Empty, rest: Workflow, datasets: "grade"), "grade:\n  rows: one\n  mapping: Grade@1.0.0\n");
        Assert.Contains("workflows.lasfile runs a workflow over the records of an enrichment 'lasfile', which the mapping Thing@1.0.0 does not declare; it declares grade", other, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeliveryProtocol.Dspdm, "the dspdm route writes rows of DSPDM business objects")]
    [InlineData(DeliveryProtocol.Etp, "the etp route writes Energistics objects")]
    public void A_route_writing_no_OSDU_record_refuses_enrichments(DeliveryProtocol protocol, string reason)
    {
        var flow = Flow(files: string.Empty);
        flow = flow with { Target = flow.Target with { Protocol = protocol } };

        var message = Refused(flow, "lasfile:\n  rows: one\n  mapping: Grade@1.0.0\n");

        Assert.Contains("the mapping Thing@1.0.0 declares enrich:, which adds to the OSDU records the flow renders", message, StringComparison.Ordinal);
        Assert.Contains(reason, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ddms_flow_whose_endpoint_is_the_DDMS_itself_reaches_no_storage_or_Dataset_service_for_the_records_it_adds()
    {
        var flow = Flow(files: string.Empty);
        flow = flow with { Target = flow.Target with { Protocol = DeliveryProtocol.Ddms, ProtocolOptions = new ProtocolOptions() } };

        var message = Refused(flow, "lasfile:\n  rows: one\n  mapping: Note@1.0.0\n");

        Assert.Contains("adds records through the platform's Dataset and storage services, which a flow reaches under its endpoint, and this flow's endpoint is the DDMS itself", message, StringComparison.Ordinal);
        Assert.NotNull(Bind(flow with { Target = flow.Target with { ProtocolOptions = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" } } }, "lasfile:\n  rows: one\n  mapping: Note@1.0.0\n").Enrich);
        Assert.NotNull(Bind(flow, "lasfile:\n  rows: one\n  mapping: Grade@1.0.0\n").Enrich);
    }

    [Fact]
    public void An_enrichment_naming_the_records_bulk_data_reads_it_from_its_row_on_the_ddms_route()
    {
        var flow = Flow();
        flow = flow with { Target = flow.Target with { Protocol = DeliveryProtocol.Ddms, ProtocolOptions = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" } } };

        var bound = Bind(flow, "lasfile:\n  rows: one\n  bulk: true\n");

        var step = Assert.Single(bound.Enrich!);
        Assert.Equal(("lasfile", "las_folder", (EnrichRecords?)null), (step.Bulk!.Step, step.Bulk.LocationColumn, step.Records));
        Assert.Equal(PayloadParts.Bulk, Assert.Single(bound.Source.Payloads).Key);
        Assert.Equal("lasfile", bound.Source.Payloads[PayloadParts.Bulk].Step);
    }

    [Fact]
    public void Bulk_data_is_refused_beside_files_its_rows_register_off_the_ddms_route_and_twice()
    {
        var ddms = Flow(datasets: "lasfile, grid");
        ddms = ddms with { Target = ddms.Target with { Protocol = DeliveryProtocol.Ddms, ProtocolOptions = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" } } };

        Assert.Contains(
            "registers the files its rows name with the datasets it adds and names them as the record's bulk data (bulk: true)",
            Refused(ddms, "lasfile:\n  rows: one\n  bulk: true\n  mapping: LasDataset@1.0.0\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "names the record's DDMS bulk data, which the ddms route sends as the record's payload, and this flow's route is storage",
            Refused(Flow(), "lasfile:\n  rows: one\n  bulk: true\n"),
            StringComparison.Ordinal);

        var twice = Flow(files: string.Empty, datasets: "lasfile, grid") with { };
        twice = twice with
        {
            Source = twice.Source with
            {
                Datasets = twice.Source.Datasets.ToDictionary(d => d.Key, d => d.Value with { Files = LasFiles() }, StringComparer.Ordinal),
            },
            Target = twice.Target with { Protocol = DeliveryProtocol.Ddms, ProtocolOptions = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" } },
        };
        Assert.Contains(
            "the enrichments lasfile, grid of the mapping Thing@1.0.0 each name the record's bulk data, and a record has one set of it",
            Refused(twice, "lasfile:\n  rows: one\n  bulk: true\ngrid:\n  rows: one\n  bulk: true\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_for_target_operations_opens_without_its_mapping_and_its_worker_refuses_to_send_without_the_binding()
    {
        // Verify, delete and release write nothing an enrichment adds, so a mapping that does not read leaves them working; the
        // worker writes what the enrichments add to each record, and says why it cannot.
        var context = Samples.Engine(null);
        using var runtime = FlowRuntime.ForTarget(context, Samples.Targeting(new FlowTarget { Endpoint = "http://localhost", Protocol = DeliveryProtocol.Storage }));

        Assert.Null(runtime.Flow.Enrich);
        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => runtime.WorkerAsync());
        Assert.Contains(
            "the pending work is not sent, since it is written with what the mapping Targeting@1.0.0 adds to each record through its enrichments, and the flow could not be bound to them: Mapping 'Targeting@1.0.0' was not found",
            refused.Message,
            StringComparison.Ordinal);
    }
}
