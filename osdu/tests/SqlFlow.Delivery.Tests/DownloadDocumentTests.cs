using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The download flow document (<c>flowType: download</c>, osdu/docs/reference/flow/download.md): what it declares, every rule
/// the loader holds it to, each refusal naming the place in the document it is about, how a run binds it to a partition and
/// selects its downloads, the payload and operations it takes, and the lineage it contributes.
/// </summary>
public class DownloadDocumentTests
{
    private const string Head = """
        flowType: download
        name: welldb-download
        batch: welldb
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: bearer
            secretRef: ${env:OSDU_TOKEN}

        """;

    private const string Lake = """
        target:
          location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records

        """;

    private const string Wellbores = """
        downloads:
          - name: Wellbores
            kind: "*:*:master-data--Wellbore:*"
        """;

    private static DownloadFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDownload(yaml, "flows/download.yaml");

    private static FlowValidationException Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml));

    private static string At(string location) => $"target:\n  location: '{location}'\n";

    [Fact]
    public void A_document_declares_its_downloads_and_target_with_every_service_path_defaulted()
    {
        var flow = Parse(Head + """
            parameters:
              country: { default: US }
              root: { default: /data/osdu }
            target:
              location: "{root}/{partition}"
            downloads:
              - name: Wellbores
                description: Every wellbore.
                kind: "*:*:master-data--Wellbore:*"
              - name: CountryWells
                kind: "osdu:wks:master-data--Well:1.*.*"
                query: 'data.Country:"{country}" AND data.Partition:"{partition}"'
            """);

        Assert.Equal(("welldb-download", "welldb", FlowTelemetry.Download), (flow.Name, flow.Batch, flow.TelemetryOperation));
        Assert.Equal(["dev", "test"], flow.Partitions);
        Assert.Equal("{root}/{partition}", flow.Target.Location);
        Assert.Equal(InventoryRead.Search, flow.Source.Read);
        Assert.Equal(
            (InventorySource.DefaultQueryPath, InventorySource.DefaultSearchPath, InventorySource.DefaultRecordQueryPath, InventorySource.DefaultHeadersPath, InventorySource.DefaultSchemaPath),
            (flow.Source.QueryPath, flow.Source.SearchPath, flow.Source.RecordQueryPath, flow.Source.HeadersPath, flow.Source.SchemaPath));
        Assert.Equal(("Wellbores", "Every wellbore.", (string?)null), (flow.Downloads[0].Name, flow.Downloads[0].Description, flow.Downloads[0].Query));
        Assert.Equal("data.Country:\"{country}\" AND data.Partition:\"{partition}\"", flow.Downloads[1].Query);

        // A download is listed as an inventory lists its kind: the latest version of each record.
        var listed = flow.Downloads[1].AsInventory();
        Assert.Equal(("CountryWells", "osdu:wks:master-data--Well:1.*.*", InventoryVersions.Latest), (listed.Name, listed.Kind, listed.Versions));
    }

    [Fact]
    public void Its_downloads_are_named_once_each_as_folders_every_file_system_makes()
    {
        Assert.Contains(
            "downloads must list at least one download",
            Refused(Head + Lake + "downloads: []\n").Message, StringComparison.Ordinal);
        Assert.Contains(
            "downloads[1] is named 'wellbores', as an earlier download is; each download writes to the folder of its name",
            Refused(Head + Lake + Wellbores + "\n  - { name: wellbores, kind: \"osdu:wks:master-data--Wellbore:1.0.0\" }\n").Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "downloads[0].name 'Logs/2026' is not a download name",
            Refused(Head + Lake + "downloads:\n  - { name: Logs/2026, kind: \"osdu:wks:master-data--Well:1.0.0\" }\n").Message,
            StringComparison.Ordinal);

        foreach (var name in new[] { "CON", "nul", "Com1", "LPT9", "aux.logs", "Logs." })
        {
            var refused = Refused(Head + Lake + $"downloads:\n  - {{ name: '{name}', kind: \"osdu:wks:master-data--Well:1.0.0\" }}\n");
            Assert.Contains($"downloads[0].name '{name}' names the folder the download's files go to, and Windows makes no folder of that name", refused.Message, StringComparison.Ordinal);
        }

        foreach (var name in new[] { "Console", "COM10", "Nullable", "Logs.v2" })
        {
            Assert.Equal(name, Parse(Head + Lake + $"downloads:\n  - {{ name: '{name}', kind: \"osdu:wks:master-data--Well:1.0.0\" }}\n").Downloads[0].Name);
        }
    }

    [Fact]
    public void A_downloads_kind_and_query_are_checked_where_they_are_written()
    {
        Assert.Contains(
            "downloads[0] 'Wells': kind 'master-data--Well' is not authority:source:entityType:version",
            Refused(Head + Lake + "downloads:\n  - { name: Wells, kind: master-data--Well }\n").Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "downloads[0] 'Wells': query uses '{country}', which is not declared under parameters.",
            Refused(Head + Lake + "downloads:\n  - { name: Wells, kind: \"osdu:wks:master-data--Well:1.0.0\", query: 'data.Country:\"{country}\"' }\n").Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "query narrows a search, and the flow reads storage (source.read: storage)",
            Refused(Head.Replace("  auth:", "  read: storage\n  auth:", StringComparison.Ordinal) + Lake
                + "downloads:\n  - { name: Wells, kind: \"osdu:wks:master-data--Well:1.0.0\", query: 'data.Name:A' }\n").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_target_is_one_folder_every_run_writes_to_written_out_and_absolute()
    {
        Assert.Contains("'target' is required.", Refused(Head + Wellbores).Message, StringComparison.Ordinal);
        Assert.Contains("'target.location' is required.", Refused(Head + "target: {}\n" + Wellbores).Message, StringComparison.Ordinal);
        Assert.Contains(
            "target.location '${env:LAKE}/osdu' holds a ${...} reference, and a location is not resolved from one",
            Refused(Head + At("${env:LAKE}/osdu") + Wellbores).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.location uses '{date}', and a download writes every run to the same folder",
            Refused(Head + At("/data/osdu/{date}") + Wellbores).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.location uses '{run}', and a download writes every run to the same folder",
            Refused(Head + At("/data/osdu/{run}") + Wellbores).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.location uses '{root}', which is neither {partition} nor declared under parameters.",
            Refused(Head + At("{root}/osdu") + Wellbores).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.location 'out/records' is a relative path, and a run reads its flow from a checkout of its own",
            Refused(Head + At("out/records") + Wellbores).Message,
            StringComparison.Ordinal);

        // A location with no credential: a SAS token in it is refused by the hygiene check, which names what to do instead.
        Assert.Contains(
            "target.location carries a credential in its user info or query string (a SAS token)",
            Refused(Head + At("https://lake.blob.core.windows.net/osdu?sv=2024-01-01&sig=abc123") + Wellbores).Message,
            StringComparison.Ordinal);

        foreach (var location in new[] { "/data/osdu/records", "abfss://lake@welldbstorage.dfs.core.windows.net/osdu", "https://lake.blob.core.windows.net/osdu", "/data/{partition}" })
        {
            Assert.Equal(location, Parse(Head + At(location) + Wellbores).Target.Location);
        }
    }

    [Fact]
    public void A_download_flow_takes_no_key_a_removal_or_an_inventory_reads()
    {
        foreach (var key in new[] { "deletePath: /x/{id}", "bulkDeletePath: /x", "purgePath: /x/{id}", "versionsPath: /x" })
        {
            var refused = Refused(Head.Replace("  auth:", $"  {key}\n  auth:", StringComparison.Ordinal) + Lake + Wellbores);
            Assert.Contains("not found on type 'SqlFlow.Delivery.Documents.DownloadSourceYaml'", refused.Message, StringComparison.Ordinal);
        }

        Assert.Contains(
            "not found on type 'SqlFlow.Delivery.Documents.DownloadFlowYaml'",
            Refused(Head + Lake + Wellbores + "\nremoval: { findings: [orphan] }\n").Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "not found on type 'SqlFlow.Delivery.Documents.DownloadYaml'",
            Refused(Head + Lake + "downloads:\n  - { name: Wells, kind: \"osdu:wks:master-data--Well:1.0.0\", versions: all }\n").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Partitions_are_named_by_the_flow_or_its_header_never_both()
    {
        Assert.Contains(
            "source.headers names 'data-partition-id', and the flow names its partitions",
            Refused(Head.Replace("  auth:", "  headers: { data-partition-id: dev }\n  auth:", StringComparison.Ordinal) + Lake + Wellbores).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "parameters declares 'partition', which a download flow keeps for the partition a run reads",
            Refused(Head + "parameters:\n  partition: { default: dev }\n" + Lake + Wellbores).Message,
            StringComparison.Ordinal);

        var header = Parse(Head.Replace("partitions: [dev, test]\n", string.Empty, StringComparison.Ordinal).Replace("  auth:", "  headers: { data-partition-id: dev }\n  auth:", StringComparison.Ordinal) + Lake + Wellbores);
        Assert.False(header.Partitioned);
        Assert.Equal("welldb-download", header.LedgerName);

        var registry = Parse(Head.Replace("partitions: [dev, test]\n", string.Empty, StringComparison.Ordinal) + Lake + Wellbores);
        Assert.True(registry.FollowsRegistry);
    }

    [Fact]
    public void A_run_binds_the_flow_to_one_partition_and_selects_its_downloads()
    {
        var flow = Parse(Head + Lake + Wellbores + "\n  - { name: Wells, kind: \"*:*:master-data--Well:*\" }\n");
        var registry = new RegisteredPartitions([new RegisteredPartition("dev", null, true), new RegisteredPartition("test", null, false)]);

        var bound = flow.ForRun("test", registry);
        Assert.Equal(("test", "welldb-download@test"), (bound.Partition, bound.LedgerName));
        Assert.Equal("test", bound.Source.Headers[CacheScope.PartitionHeader]);
        Assert.Equal("dev", flow.ForRun(null, registry).Partition);
        Assert.Contains("reads one partition per run", Assert.Throws<DeliveryException>(() => flow.ForRun(PartitionNames.Every, registry)).Message, StringComparison.Ordinal);
        Assert.Contains("does not read partition 'prod'", Assert.Throws<DeliveryException>(() => flow.ForRun("prod", registry)).Message, StringComparison.Ordinal);

        Assert.Equal(["Wellbores", "Wells"], flow.Select([]).Select(d => d.Name));
        Assert.Equal(["Wells"], flow.Select(["wells"]).Select(d => d.Name));
        Assert.Contains(
            "has no download named 'Logs'; its downloads are Wellbores, Wells.",
            Assert.Throws<DeliveryException>(() => flow.Select(["Logs"])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_is_a_download_or_a_plan_and_names_only_its_downloads_and_whether_it_writes_every_record_again()
    {
        var kind = new DownloadFlowKind(new DeliveryDocumentLoader());

        Assert.Equal(DeliveryOperations.Download, DownloadFlowKind.Operation(new RunParameters()));
        Assert.Equal(DeliveryOperations.Plan, DownloadFlowKind.Operation(new RunParameters { Operation = DeliveryOperations.Plan }));
        Assert.Equal(
            "A download flow runs the download and plan operations; 'build' is not one of them.",
            Assert.Throws<SqlFlowException>(() => DownloadFlowKind.Operation(new RunParameters { Operation = DeliveryOperations.Build })).Message);
        Assert.Equal([DeliveryOperations.Download, DeliveryOperations.Plan], kind.Operations.Select(o => o.Name));
        Assert.Equal([true, false], kind.Operations.Select(o => o.WritesTarget));

        kind.ValidateParameters(new RunParameters { Payload = """{ "downloads": ["Wellbores"], "force": true }""" });
        Assert.Equal(["Wellbores"], DeliveryRunPayload.Parse("""{ "downloads": ["Wellbores"] }""").Downloads);
        Assert.Contains(
            "fullLoad",
            Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { FullLoad = true })).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Lineage_reads_each_downloads_kind_in_each_partition_and_writes_a_folder_of_files_per_download()
    {
        var flow = Parse(Head + Lake + Wellbores + "\n  - { name: Wells, kind: \"osdu:wks:master-data--Well:1.0.0\" }\n  - { name: Wellbores2, kind: \"*:*:master-data--Wellbore:*\" }\n");

        var lineage = DownloadLineage.Describe(flow);

        Assert.Equal(
            ["osdu-type/dev/*:*:master-data--Wellbore:*", "osdu-type/dev/osdu:wks:master-data--Well:1.0.0", "osdu-type/test/*:*:master-data--Wellbore:*", "osdu-type/test/osdu:wks:master-data--Well:1.0.0"],
            lineage.Datasets.Select(d => $"{d.System}/{d.Namespace}/{d.Name}"));
        Assert.All(lineage.Datasets, d => Assert.Equal(LineageRelation.Reads, d.Relation));
        Assert.Equal(
            [
                "abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records/Wellbores|*.json",
                "abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records/Wells|*.json",
                "abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records/Wellbores2|*.json",
            ],
            lineage.Files.Select(f => $"{f.Location}|{f.FilePattern}"));
        Assert.All(lineage.Files, f => Assert.Equal(LineageRelation.Writes, f.Relation));
        Assert.Empty(lineage.Warnings);

        var bound = DownloadLineage.Describe(flow.ForPartition("test"));
        Assert.All(bound.Datasets, d => Assert.Equal("test", d.Namespace));
    }

    [Fact]
    public void Its_secrets_are_references_the_hygiene_check_inspects_and_it_needs_the_repository_tree()
    {
        var flow = Parse(Head.Replace("partitions: [dev, test]\n", string.Empty, StringComparison.Ordinal).Replace("  auth:", "  headers: { data-partition-id: '${env:OSDU_PARTITION}' }\n  auth:", StringComparison.Ordinal) + Lake + Wellbores);
        var document = new DownloadFlowDocument { Flow = flow };

        Assert.Equal(
            [
                new KeyValuePair<string, string>("source.auth.secretRef", "${env:OSDU_TOKEN}"),
                new KeyValuePair<string, string>("source.endpoint", "${env:OSDU_URL}"),
                new KeyValuePair<string, string>("source.headers.data-partition-id", "${env:OSDU_PARTITION}"),
            ],
            document.CredentialReferences);
        Assert.Equal((DownloadFlowDefinition.FlowTypeName, "${env:OSDU_URL}", "abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records"), (document.Kind, document.SourceReference, document.TargetReference));
        Assert.True(document.RequiresRepoTree);
    }

    [Fact]
    public void The_sample_download_flow_reads_as_its_page_describes_it()
    {
        var flow = new DeliveryDocumentLoader().LoadDownload(Path.Combine(Samples.Source, "flows", "welldb-osdu-07-download.yaml"));

        Assert.Equal(("welldb-osdu-07-download", "welldb"), (flow.Name, flow.Batch));
        Assert.Equal(["dev"], flow.Partitions);
        Assert.Equal(["Wells", "Wellbores"], flow.Downloads.Select(d => d.Name));
        Assert.Equal("abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records", flow.Target.Location);
    }
}
