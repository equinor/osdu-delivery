using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The <c>files:</c> of a step of <c>enrich:</c> as the loader reads it (osdu/docs/reference/flow/enrich-files.md):
/// where each row's files are and the column holding their hash, the dataset kind they become, the columns telling
/// a row's dataset apart, the fragment rendered into each dataset record and the path the record links them from, each with
/// its default; and what could never register, refused when the document loads with the key it names.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepFilesDocumentTests
{
    private const string Head = """
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
          work: work
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/api
          headers: { data-partition-id: dev }
          protocol: ddms
          protocolOptions:
            ddmsRoot: /api/os-wellbore-ddms

        """;

    private const string Dataset = "    dataset:\n      object: OsduData.silver.WellLogFile\n      join: { source_project: source_project, log_id: log_id }\n";

    private static FlowDefinition Parsed(string enrich, string head = Head) => new DeliveryDocumentLoader().ParseFlow(head + "enrich:\n" + enrich, "flows/demo.yaml");

    private static FlowValidationException Refused(string enrich, string head = Head)
        => Assert.Throws<FlowValidationException>(() => Parsed(enrich, head));

    private static string Step(string files, string rest = "", string dataset = Dataset) => "  lasfile:\n" + dataset + rest + "    files:\n" + files;

    private const string Minimal = "      root: ../data/las\n      locationColumn: las_folder\n      hashColumn: las_hash\n";

    [Fact]
    public void A_step_registering_files_alone_takes_every_default()
    {
        var step = Assert.Single(Parsed(Step(Minimal)).Enrich!);

        Assert.Null(step.Mapping);
        var files = Assert.IsType<EnrichFiles>(step.Files);
        Assert.Equal(("../data/las", "las_folder", "*", "las_hash"), (files.Payload.Root, files.Payload.LocationColumn, files.Payload.Pattern, files.Payload.HashColumn));
        Assert.Equal((EnrichFiles.DefaultKind, EnrichFiles.DefaultLink, (string?)null, (string?)null), (files.Kind, files.Link, files.Mapping, files.ContentType));
        Assert.Empty(files.Key);
    }

    [Fact]
    public void A_step_reads_every_key_of_its_files_and_a_fragment_beside_them()
    {
        var step = Assert.Single(Parsed(Step(
            Minimal.Replace("      hashColumn", "      pattern: \"*.las\"\n      hashColumn", StringComparison.Ordinal)
            + "      kind: osdu:wks:dataset--FileCollection.Generic:1.0.0\n      key: [file_set]\n      mapping: LasDataset@1.0.0\n"
            + "      link: data.Artefacts.Datasets\n      contentType: text/plain\n",
            rest: "    rows: many\n    mapping: WellLogFile@1.0.0\n")).Enrich!);

        Assert.Equal("WellLogFile@1.0.0", step.Mapping);
        var files = step.Files!;
        Assert.Equal(
            ("*.las", "osdu:wks:dataset--FileCollection.Generic:1.0.0", "LasDataset@1.0.0", "data.Artefacts.Datasets", "text/plain"),
            (files.Payload.Pattern, files.Kind, files.Mapping, files.Link, files.ContentType));
        Assert.Equal(["file_set"], files.Key);
    }

    [Fact]
    public void A_step_declaring_neither_a_mapping_nor_files_adds_nothing_and_is_refused()
    {
        var refused = Refused("  lasfile:\n" + Dataset);
        Assert.Contains("enrich.lasfile declares neither a mapping nor files nor bulk data", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_without_a_dataset_have_no_row_to_name_them_and_are_refused()
    {
        var refused = Refused(Step(Minimal, dataset: string.Empty));
        Assert.Contains("enrich.lasfile.files registers the files each of the step's rows names, and the step declares no dataset", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("locationColumn")]
    [InlineData("hashColumn")]
    public void Files_name_their_root_their_folder_column_and_their_hash_column(string missing)
    {
        var files = string.Concat(Minimal.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.TrimStart().StartsWith(missing + ":", StringComparison.Ordinal)).Select(line => line + "\n"));
        var refused = Refused(Step(files));
        Assert.Contains($"enrich.lasfile.files.{missing}", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("osdu:wks:master-data--Wellbore:1.0.0", "is not a dataset kind")]
    [InlineData("not-a-kind", "must be 'authority:source:entityType:major.minor.patch'")]
    [InlineData("osdu:wks:dataset--ConnectedSource.Generic:1.0.0", "External Data Services proxy dataset")]
    public void Files_are_registered_as_a_dataset_kind_that_holds_files_of_its_own(string kind, string reason)
    {
        var refused = Refused(Step(Minimal + $"      kind: {kind}\n"));
        Assert.Contains($"enrich.lasfile.files.kind '{kind}'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_taking_many_rows_names_the_columns_that_tell_their_datasets_apart()
    {
        var refused = Refused(Step(Minimal, rest: "    rows: many\n"));
        Assert.Contains("enrich.lasfile.files.key names the columns telling each row's dataset", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_taking_one_row_has_one_dataset_its_name_names_and_takes_no_key()
    {
        var refused = Refused(Step(Minimal + "      key: [file_name]\n"));
        Assert.Contains("remove enrich.lasfile.files.key, or declare rows: many", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_naming_a_column_twice_is_refused()
    {
        var refused = Refused(Step(Minimal + "      key: [file_name, FILE_NAME]\n", rest: "    rows: many\n"));
        Assert.Contains("enrich.lasfile.files.key names a column more than once", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Datasets")]
    [InlineData("data.")]
    [InlineData("data..Datasets")]
    [InlineData("tags.Datasets")]
    public void The_link_is_a_path_of_the_records_data(string link)
    {
        var refused = Refused(Step(Minimal + $"      link: \"{link}\"\n"));
        Assert.Contains("enrich.lasfile.files.link is the path of the record's data the datasets are listed at", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dataset_fragment_is_pinned()
    {
        var refused = Refused(Step(Minimal + "      mapping: LasDataset\n"));
        Assert.Contains("enrich.lasfile.files.mapping 'LasDataset' must be pinned as 'Name@version'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_whose_endpoint_is_the_ddms_itself_reaches_no_dataset_service_and_is_refused()
    {
        var head = Head.Replace("  protocolOptions:\n    ddmsRoot: /api/os-wellbore-ddms\n", string.Empty, StringComparison.Ordinal)
            .Replace("https://example.org/api", "https://example.org/api/os-wellbore-ddms", StringComparison.Ordinal);
        var refused = Refused(Step(Minimal), head);
        Assert.Contains("registers files through the Dataset service", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interface_declares_step_files_of_its_own()
    {
        var yaml = """
            flowType: delivery
            name: demo
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
                      join: { source_project: source_project, log_id: log_id }
                    files:
                      root: ../data/las
                      locationColumn: las_folder
                      hashColumn: las_hash
            """;

        var flow = Assert.Single(new DeliveryDocumentLoader().ParseSource(yaml, "flows/demo.yaml").Interfaces);
        Assert.Equal("las_hash", Assert.Single(flow.Enrich!).Files!.Payload.HashColumn);
    }
}
