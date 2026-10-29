using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The workflow route end to end against a fake Workflow service, one test for every workflow the workflows and EDS
/// briefs cover (osdu/specs/workflows/INTEGRATION.md section 3; osdu/specs/eds-dms/INTEGRATION.md section 5.5): the
/// anchor written, the inputs registered, each stage triggered with a context its workflow's contract accepts, the run
/// polled, its outputs read, what it created found and read back, and every request kept to the pinned contracts.
/// </summary>
public sealed class WorkflowRouteTests
{
    private const string Partition = "dev";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    static WorkflowRouteTests()
    {
        Environment.SetEnvironmentVariable("OSDU_DELIVERY_TEST_SD_TOKEN", "sd-token-value");
        Environment.SetEnvironmentVariable("OSDU_DELIVERY_TEST_SD_KEY", "sd-key-value");
        Environment.SetEnvironmentVariable("OSDU_DELIVERY_TEST_STORAGE_KEY", "storage-key-value");
        Environment.SetEnvironmentVariable("OSDU_DELIVERY_TEST_CLIENT", "client-value");
        Environment.SetEnvironmentVariable("OSDU_DELIVERY_TEST_AF_USER", "airflow-user");
        Environment.SetEnvironmentVariable("OSDU_DELIVERY_TEST_AF_PASSWORD", "airflow-password");
    }

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform)
        {
            Platform = platform;
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Partition });
        }

        public FakeOsduPlatform Platform { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        /// <summary>The flow's options; the staging area is Azure's, which a flow on a non-Azure host names the blob type for.</summary>
        public static ProtocolOptions Options => new()
        {
            WorkflowPollSeconds = 1,
            DatasetIndexWaitSeconds = 0,
            UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
        };

        public OsduWorkflowProtocol Protocol(WorkflowRoute route, IWorkflowXCom? xcom = null)
            => new(Client, Options, route, Samples.Logger<OsduWorkflowProtocol>(), Secrets, xcom);

        public AirflowXCom Airflow(AirflowApiVersion version) => new(Runtime, new AirflowAccess
        {
            Endpoint = FakeOsduPlatform.AirflowEndpoint,
            ApiVersion = version,
            Auth = new TargetAuth { Type = TargetAuthType.Basic, SecretRef = "${env:OSDU_DELIVERY_TEST_AF_PASSWORD}", SecondarySecretRef = "${env:OSDU_DELIVERY_TEST_AF_USER}" },
        }, Secrets);

        public void Dispose() => Runtime.Dispose();
    }

    private static DeliveryWork Work(JsonObject document, IReadOnlyList<WorkPayloadPart>? parts = null, long? existing = null, IReadOnlyDictionary<string, string>? state = null, IReadOnlySet<string>? forced = null, bool metadata = true)
        => new()
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("workflow-route", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = true,
            Parts = parts ?? [],
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ForcedParts = forced ?? new HashSet<string>(StringComparer.Ordinal) { PayloadParts.Files, PayloadParts.Workflow },
        };

    private static WorkflowStage Stage(string workflow, string context, IReadOnlyDictionary<string, WorkflowOutput>? outputs = null)
        => new() { Workflow = workflow, Context = (JsonObject)JsonNode.Parse(context)!, Outputs = outputs ?? new Dictionary<string, WorkflowOutput>() };

    /// <summary>The trigger's context, which must hold to the workflow's contract as it was sent.</summary>
    private static JsonObject AssertContract(FakeOsduPlatform.Run run, string contract)
    {
        var problems = WorkflowContextCheck.CheckContext(WorkflowCatalog.All.Single(c => c.Name == contract), run.Context);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        return run.Context;
    }

    private static IReadOnlySet<string> AssertConform(FakeOsduPlatform platform, params ApiContract[] extra)
        => OsduContracts.AssertConform(
            platform.Calls,
            FakeOsduPlatform.ToSignedLocation,
            OsduContracts.ContextValueTyping,
            [OsduContracts.Workflow, OsduContracts.Dataset, OsduContracts.Storage, OsduContracts.Search, OsduContracts.Legal, .. extra]);

    private static JsonObject Descriptor(string id, string targetKind) => FakeOsduPlatform.Record(id, "osdu:wks:dataset--File.Generic:1.0.0", new JsonObject
    {
        ["Name"] = "wells.csv",
        ["ExtensionProperties"] = new JsonObject { ["FileContentsDetails"] = new JsonObject { ["TargetKind"] = targetKind, ["FileType"] = "csv" } },
    });

    [Fact]
    public async Task The_csv_parser_reads_a_descriptor_registered_with_its_file_and_its_rows_are_found_by_the_anchor_tag()
    {
        var platform = new FakeOsduPlatform();
        const string anchor = "dev:dataset--File.Generic:csv-wells-1";
        var tag = WorkflowValues.Tag(anchor);
        platform.Register("csv_ingestion", new FakeOsduPlatform.Script
        {
            Pending = "running",
            Effect = (p, run) =>
            {
                // The parser copies the descriptor's tags onto every row it stores (csv-parser TagsHandler).
                var descriptor = p.Records[run.Context["id"]!.GetValue<string>()];
                for (var i = 0; i < 3; i++)
                {
                    var row = FakeOsduPlatform.Record($"dev:master-data--Well:wks-row{i}", "osdu:wks:master-data--Well:1.0.0");
                    row["tags"] = descriptor["tags"]!.DeepClone();
                    p.Put(row);
                }
            },
        });
        platform.Search = (kind, query) => kind == "osdu:wks:master-data--Well:1.0.0" && query == $"tags.osduDeliveryAnchor:\"{tag}\""
            ? platform.Records.Values
                .Where(r => r["kind"]!.GetValue<string>() == kind && r["tags"]?["osduDeliveryAnchor"]?.GetValue<string>() == tag)
                .Select(r => r["id"]!.GetValue<string>())
                .ToList()
            : [];

        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Dataset,
            AnchorTagKey = "osduDeliveryAnchor",
            Stages = [Stage("csv_ingestion", """{ "id": "{record:id}", "dataPartitionId": "{partition}", "data_service_to_use": "file" }""")],
            Results = new WorkflowResults
            {
                Strategy = WorkflowResultStrategy.Search,
                Kind = "{record:data.ExtensionProperties.FileContentsDetails.TargetKind}",
                Query = "tags.osduDeliveryAnchor:\"{anchorTag}\"",
                Minimum = 3,
            },
        };

        using var rig = new Rig(platform);
        var files = new WorkPayloadPart(PayloadParts.Files, PayloadParts.Files, new MemoryFiles(("wells.csv", "uwi,name\n1,A\n2,B\n3,C\n")), "h1");
        var outcome = await rig.Protocol(route).DeliverAsync(Work(Descriptor(anchor, "osdu:wks:master-data--Well:1.0.0"), [files]));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.True(outcome.MetadataDelivered);
        Assert.True(outcome.PayloadDelivered);
        Assert.Equal("3", outcome.Returned[OsduWorkflowProtocol.RecordsValue]);
        Assert.Equal("h1", outcome.Returned[PayloadParts.StateKey(PayloadParts.Files)]);
        Assert.Equal(platform.Records[anchor]["version"]!.GetValue<long>(), outcome.TargetVersion);

        // The descriptor is registered with its id, its file and the anchor tag; the parser is told its id.
        var registered = platform.Records[anchor];
        Assert.Equal(tag, registered["tags"]!["osduDeliveryAnchor"]!.GetValue<string>());
        Assert.StartsWith("/staging/blob/", registered["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("wells.csv", registered["data"]!["DatasetProperties"]!["FileSourceInfo"]!["Name"]!.GetValue<string>());
        var run = Assert.Single(platform.Runs);
        var context = AssertContract(run, WorkflowCatalog.CsvParser);
        Assert.Equal(anchor, context["id"]!.GetValue<string>());
        Assert.Equal(Partition, context["Payload"]!["data-partition-id"]!.GetValue<string>());
        Assert.Equal(2, run.Polls);

        // The steps record the run and never the location the file went to, which is a credential.
        Assert.Contains(outcome.Steps, s => s.Name == "stage-1" && s.Returned["runId"] == run.RunId);
        Assert.DoesNotContain(outcome.Steps.SelectMany(s => s.Returned.Values), v => v.Contains("sig=", StringComparison.Ordinal));

        Assert.Contains("core/workflow POST /v1/workflow/{workflow_name}/workflowRun", AssertConform(platform));
    }

    [Fact]
    public async Task Energistics_translates_by_one_run_and_ingests_by_reference_in_the_next_reading_the_manifest_id_the_service_reports()
    {
        await EnergisticsAsync(xcom: null, airflowVersion: null);
    }

    [Theory]
    [InlineData(AirflowApiVersion.V1)]
    [InlineData(AirflowApiVersion.V2)]
    public async Task Energistics_reads_the_manifest_id_from_airflow_when_the_flow_says_where_it_is(AirflowApiVersion version)
    {
        await EnergisticsAsync(xcom: version, airflowVersion: version == AirflowApiVersion.V1 ? "v1" : "v2");
    }

    private static async Task EnergisticsAsync(AirflowApiVersion? xcom, string? airflowVersion)
    {
        var platform = new FakeOsduPlatform { AirflowVersion = airflowVersion ?? "v1" };
        const string anchor = "dev:dataset--File.Generic:epc-1";
        const string manifest = "dev:dataset--File.Generic:energistics-manifest-9";
        var tag = WorkflowValues.Tag(anchor);
        platform.Register("Energyml_Converter", new FakeOsduPlatform.Script
        {
            Effect = (p, run) => p.Put(FakeOsduPlatform.Record(manifest, "osdu:wks:dataset--File.Generic:1.0.0")),

            // The operator variant's final status task holds the manifest id under saved_record_ids (workflows brief 3.6).
            XCom = _ => new Dictionary<(string, string), JsonNode?>
            {
                [("update_status_finished_task", "saved_record_ids")] = new JsonObject { ["energyml_manifest_creation"] = new JsonArray(manifest + ":") },
            },
        });
        platform.Register("Osdu_ingest_by_reference", new FakeOsduPlatform.Script
        {
            Effect = (p, run) =>
            {
                Assert.Equal(manifest, run.Context["manifest"]!.GetValue<string>());
                var wpc = FakeOsduPlatform.Record("dev:work-product-component--WellLog:resqml-1", "osdu:wks:work-product-component--WellLog:1.4.0", new JsonObject { ["Tags"] = new JsonArray(tag) });
                p.Put(wpc);
            },
        });
        platform.Search = (kind, query) => query == $"data.Tags:\"{tag}\"" ? ["dev:work-product-component--WellLog:resqml-1"] : [];

        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Dataset,
            Inputs = [new WorkflowInput("h5", "osdu:wks:dataset--File.Generic:1.0.0", Optional: true)],
            Stages =
            [
                Stage(
                    "Energyml_Converter",
                    """{ "dataset_xml": ["{record:id}"], "dataset_h5": "{input:h5}", "data_partition_id": "{partition}", "tags_every_entity_keys": "{anchorTag}" }""",
                    new Dictionary<string, WorkflowOutput>
                    {
                        ["manifestId"] = new() { XComTask = "update_status_finished_task", XComKey = "saved_record_ids", Match = "dataset--File.Generic" },
                    }),
                Stage("Osdu_ingest_by_reference", """{ "manifest": "{stage:1.manifestId}" }"""),
            ],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Search, Kind = "*:*:*--*:*.*.*", Query = "data.Tags:\"{anchorTag}\"", Minimum = 1 },
        };

        using var rig = new Rig(platform);
        var epc = new WorkPayloadPart(PayloadParts.Files, PayloadParts.Files, new MemoryFiles(("model.epc", "PK-epc")), "e1");
        var h5 = new WorkPayloadPart(PayloadParts.Files, "h5", new MemoryFiles(("model.h5", "HDF-1"), ("model2.h5", "HDF-2")), "x1");
        var protocol = rig.Protocol(route, xcom is { } v ? rig.Airflow(v) : null);
        var outcome = await protocol.DeliverAsync(Work(FakeOsduPlatform.Record(anchor, "osdu:wks:dataset--File.Generic:1.0.0"), [epc, h5]));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("1", outcome.Returned[OsduWorkflowProtocol.RecordsValue]);
        Assert.Equal(2, platform.Runs.Count);
        var translate = AssertContract(platform.Runs[0], WorkflowCatalog.EnergymlConverter);
        Assert.Equal([anchor], translate["dataset_xml"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(
            ["dev:dataset--File.Generic:epc-1-h5-0", "dev:dataset--File.Generic:epc-1-h5-1"],
            translate["dataset_h5"]!.AsArray().Select(n => n!.GetValue<string>()));
        AssertContract(platform.Runs[1], WorkflowCatalog.OsduIngestByReference);
        Assert.Equal("dev:dataset--File.Generic:epc-1-h5-0,dev:dataset--File.Generic:epc-1-h5-1", outcome.Returned[OsduWorkflowProtocol.InputValue("h5")]);
        Assert.True(platform.Records.ContainsKey("dev:dataset--File.Generic:epc-1-h5-1"));

        var used = AssertConform(platform, OsduContracts.AirflowV1, OsduContracts.AirflowV2, OsduContracts.AirflowAuth);
        switch (xcom)
        {
            case null:
                Assert.Contains("core/workflow GET /v1/workflow/{workflow_name}/workflowRun/{runId}/latestInfo", used);
                break;
            case AirflowApiVersion.V1:
                Assert.Contains("workflows/airflow-v1 GET /dags/{dag_id}/dagRuns/{dag_run_id}/taskInstances/{task_id}/xcomEntries/{xcom_key}", used);
                break;
            default:
                Assert.Contains("workflows/airflow-v2 GET /api/v2/dags/{dag_id}/dagRuns/{dag_run_id}/taskInstances/{task_id}/xcomEntries/{xcom_key}", used);
                Assert.Contains("workflows/airflow-auth POST /auth/token", used);
                break;
        }
    }

    [Fact]
    public async Task Enyparser_writes_its_manifest_where_it_is_told_and_the_records_it_lists_are_read_back()
    {
        var platform = new FakeOsduPlatform();
        const string anchor = "dev:dataset--File.Generic:resqml-pack-1";
        const string manifest = "dev:dataset--File.Generic:resqml-pack-1-manifest";
        platform.Register("Enyparser_Translation", new FakeOsduPlatform.Script
        {
            Effect = (p, run) =>
            {
                Assert.Equal(manifest, run.Context["manifest_dataset_id"]!.GetValue<string>());
                p.Put(FakeOsduPlatform.Record(manifest, "osdu:wks:dataset--File.Generic:1.0.0"));
                p.Content[manifest] = Encoding.UTF8.GetBytes("""
                    {"kind":"osdu:wks:Manifest:1.0.0",
                     "MasterData":[{"id":"dev:master-data--Wellbore:wb-9"}],
                     "Data":{"WorkProductComponents":[{"id":"dev:work-product-component--WellLog:wl-9"},{"id":"surrogate-key:wpc-1"}]}}
                    """);
            },
        });
        platform.Register("Osdu_ingest_by_reference", new FakeOsduPlatform.Script
        {
            Effect = (p, run) =>
            {
                p.Put(FakeOsduPlatform.Record("dev:master-data--Wellbore:wb-9", "osdu:wks:master-data--Wellbore:1.3.0"));
                p.Put(FakeOsduPlatform.Record("dev:work-product-component--WellLog:wl-9", "osdu:wks:work-product-component--WellLog:1.4.0"));
            },
        });

        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Dataset,
            Stages =
            [
                Stage(
                    "Enyparser_Translation",
                    """{ "source_dataset_ids": ["{record:id}"], "manifest_dataset_id": "{dataset:manifest}", "work_product_name": "{record:data.Name}", "enyparserConfig": { "acl": "{record:acl}", "legal": "{record:legal}" } }""",
                    new Dictionary<string, WorkflowOutput> { ["manifestId"] = new() { Value = "{dataset:manifest}" } }),
                Stage("Osdu_ingest_by_reference", """{ "manifest": "{stage:1.manifestId}", "acl": "{record:acl}", "legal": "{record:legal}" }"""),
            ],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Manifest, Template = "{stage:1.manifestId}", Minimum = 2 },
        };

        using var rig = new Rig(platform);
        var pack = new WorkPayloadPart(PayloadParts.Files, PayloadParts.Files, new MemoryFiles(("pack.epc", "PK")), "p1");
        var outcome = await rig.Protocol(route).DeliverAsync(Work(FakeOsduPlatform.Record(anchor, "osdu:wks:dataset--File.Generic:1.0.0"), [pack]));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("2", outcome.Returned[OsduWorkflowProtocol.RecordsValue]);
        Assert.Equal("dev:master-data--Wellbore:wb-9,dev:work-product-component--WellLog:wl-9", outcome.Returned[OsduWorkflowProtocol.RecordIdsValue]);
        var translate = AssertContract(platform.Runs[0], WorkflowCatalog.EnyparserTranslation);
        Assert.Equal(anchor, translate["work_product_name"]!.GetValue<string>());
        Assert.Equal("dev-public", translate["enyparserConfig"]!["legal"]!["legaltags"]![0]!.GetValue<string>());
        AssertContract(platform.Runs[1], WorkflowCatalog.OsduIngestByReference);

        // The manifest is read from the location its retrieval instructions give, without the flow's credentials.
        var download = Assert.Single(platform.Calls, c => c.Uri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal));
        Assert.False(download.Headers.ContainsKey("data-partition-id"));
        AssertConform(platform);
    }

    [Theory]
    [InlineData(null, "/api/search/v2/query_with_cursor")]
    [InlineData("/api/search/v2/query", "/api/search/v2/query_with_cursor")]
    [InlineData("/api/search/v2/query_with_cursor", "/api/search/v2/query_with_cursor")]
    public async Task A_search_for_what_the_runs_wrote_pages_by_cursor_on_the_search_path_the_flow_names(string? declared, string asked)
    {
        var platform = new FakeOsduPlatform { Search = (_, _) => ["dev:master-data--Well:w-1"] };
        using var rig = new Rig(platform);
        var route = new WorkflowRoute { Anchor = WorkflowAnchor.Storage, Stages = [Stage("csv_ingestion", "{}")] };
        var protocol = new OsduWorkflowProtocol(rig.Client, Rig.Options with { SearchQueryPath = declared }, route, Samples.Logger<OsduWorkflowProtocol>(), Secrets);

        Assert.Equal(["dev:master-data--Well:w-1"], await protocol.SearchAsync("osdu:wks:master-data--Well:1.*.*", "tags.osduDeliveryAnchor:\"x\"", CancellationToken.None));
        Assert.Equal(asked, Assert.Single(platform.Calls).Uri.AbsolutePath);
        Assert.Equal(["core/search POST /query_with_cursor"], AssertConform(platform));
    }

    [Fact]
    public async Task A_search_for_what_the_runs_wrote_that_matches_more_than_a_route_reads_fails_rather_than_name_some_of_them()
    {
        // A results search or a removal that named the first hundred thousand would leave the rest unread, or in place.
        var ids = Enumerable.Range(0, OsduWorkflowProtocol.MaxSearchResults + 1).Select(i => $"dev:master-data--Well:w-{i}").ToList();
        var platform = new FakeOsduPlatform { Search = (_, _) => ids };
        using var rig = new Rig(platform);
        var route = new WorkflowRoute { Anchor = WorkflowAnchor.Storage, Stages = [Stage("csv_ingestion", "{}")] };
        var protocol = new OsduWorkflowProtocol(rig.Client, Rig.Options, route, Samples.Logger<OsduWorkflowProtocol>(), Secrets);

        var error = await Assert.ThrowsAsync<DeliveryException>(() => protocol.SearchAsync("osdu:wks:master-data--Well:1.*.*", "tags.osduDeliveryAnchor:\"x\"", CancellationToken.None));

        Assert.Contains("matches 100001 records, more than the 100000 a route names", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string, string> Conversions => new()
    {
        {
            "Segy_to_vds_conversion_sdms", WorkflowCatalog.SegyToVds, "Bluware.OpenVDS",
            """{ "file_record_id": "{record:data.Datasets[0]|id}", "work_product_id": "{record:data.Parameters[Title=work_product_id].DataObjectParameter|first|id}", "id_token": "{secret:token}", "segyimport_arguments": ["--header-field", "offset=37:4"] }"""
        },
        {
            "segy-to-zgy-conversion", WorkflowCatalog.SegyToZgy, "Slb.OpenZGY",
            """{ "data_partition_id": "{partition}", "filecollection_segy_id": "{record:data.Datasets[0]|ref}", "work_product_id": "{record:data.Parameters[Title=work_product_id].DataObjectParameter|first}", "sd_svc_api_key": "{secret:sdKey}", "storage_svc_api_key": "{secret:storageKey}", "id_token": "{secret:token}" }"""
        },
        {
            "segy_to_mdio_conversion", WorkflowCatalog.SegyToMdio, "TGS.MDIO",
            """{ "work_product_id": "{record:data.Parameters[Title=work_product_id].DataObjectParameter|first|id}", "filecollection_segy_id": "{record:data.Datasets[0]|id}", "mdio_sd_path": "sd://dev/mdio/{record:data.Name}", "lossless": true, "chunksize": [64, 64, 64], "client_id": "{secret:client}", "client_secret": "{secret:client}", "refresh_token": "{secret:client}", "refresh_url": "{secret:client}" }"""
        },
    };

    [Theory]
    [MemberData(nameof(Conversions))]
    public async Task A_seg_y_conversion_starts_from_a_stored_record_and_its_artefact_is_found_on_it(string workflow, string contract, string artefactKind, string context)
    {
        var platform = new FakeOsduPlatform();
        const string anchor = "dev:work-product-component--SeismicTraceData:st-1";
        var artefact = $"dev:dataset--FileCollection.{artefactKind}:out-1";
        platform.Register(workflow, new FakeOsduPlatform.Script
        {
            Effect = (p, run) =>
            {
                // The converter writes the artefact and a new version of the trace data naming it (workflows brief 3.9 to 3.11).
                p.Put(FakeOsduPlatform.Record(artefact, $"osdu:wks:dataset--FileCollection.{artefactKind}:1.0.0"));
                var trace = (JsonObject)p.Records[anchor].DeepClone();
                trace["data"]!["Artefacts"] = new JsonArray(new JsonObject
                {
                    ["RoleID"] = "dev:reference-data--ArtefactRole:ConvertedContent:",
                    ["ResourceKind"] = $"osdu:wks:dataset--FileCollection.{artefactKind}:1.0.0",
                    ["ResourceID"] = artefact + ":",
                });
                p.Put(trace);
            },
        });

        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            Secrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["token"] = "${env:OSDU_DELIVERY_TEST_SD_TOKEN}",
                ["sdKey"] = "${env:OSDU_DELIVERY_TEST_SD_KEY}",
                ["storageKey"] = "${env:OSDU_DELIVERY_TEST_STORAGE_KEY}",
                ["client"] = "${env:OSDU_DELIVERY_TEST_CLIENT}",
            },
            Stages = [Stage(workflow, context)],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Artefact, ArtefactRole = "ConvertedContent", ArtefactKind = artefactKind, Minimum = 1 },
        };

        var document = FakeOsduPlatform.Record(anchor, "osdu:wks:work-product-component--SeismicTraceData:1.3.0", new JsonObject
        {
            ["Name"] = "st-1",
            ["Datasets"] = new JsonArray("dev:dataset--FileCollection.SEGY:segy-1:"),
            ["Parameters"] = new JsonArray(new JsonObject { ["Title"] = "work_product_id", ["DataObjectParameter"] = "dev:work-product--WorkProduct:wp-1:" }),
        });

        using var rig = new Rig(platform);
        var outcome = await rig.Protocol(route).DeliverAsync(Work(document));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(artefact, outcome.Returned[OsduWorkflowProtocol.RecordIdsValue]);

        // The ledger holds the version the converter wrote, not the one the route wrote.
        Assert.Equal(platform.Records[anchor]["version"]!.GetValue<long>(), outcome.TargetVersion);
        var sent = AssertContract(Assert.Single(platform.Runs), contract);

        // Credentials reach the request, and only the request: the step keeps the context with them redacted.
        var secrets = new[] { "sd-token-value", "sd-key-value", "storage-key-value", "client-value" };
        Assert.Contains(secrets, s => sent.ToJsonString().Contains(s, StringComparison.Ordinal));
        var kept = string.Join("\n", outcome.Steps.SelectMany(s => s.Returned.Values));
        Assert.DoesNotContain(secrets, s => kept.Contains(s, StringComparison.Ordinal));
        Assert.Contains(WorkflowTemplate.Redacted, kept, StringComparison.Ordinal);
        AssertConform(platform);
    }

    [Fact]
    public async Task Energistics_delivery_exports_records_and_its_datasets_are_read_from_the_task_that_wrote_them()
    {
        var platform = new FakeOsduPlatform { AirflowVersion = "v2" };
        const string anchor = "dev:work-product--WorkProduct:export-1";
        platform.Register("Energyml_Delivery", new FakeOsduPlatform.Script
        {
            Effect = (p, run) =>
            {
                p.Put(FakeOsduPlatform.Record("dev:dataset--File.Generic:epc-out", "osdu:wks:dataset--File.Generic:1.0.0"));
                p.Put(FakeOsduPlatform.Record("dev:dataset--File.Generic:h5-out", "osdu:wks:dataset--File.Generic:1.0.0"));
            },
            XCom = _ => new Dictionary<(string, string), JsonNode?>
            {
                [("epc_h5_delivery", "return_value")] = new JsonObject { ["epc"] = "dev:dataset--File.Generic:epc-out", ["h5"] = "dev:dataset--File.Generic:h5-out" },
            },
        });

        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            RunWhen = WorkflowRunWhen.Created,
            Stages = [Stage("Energyml_Delivery", """{ "ids": "{record:data.Components}", "name": "{record:data.Name}.epc" }""")],
            Results = new WorkflowResults
            {
                Strategy = WorkflowResultStrategy.XCom,
                XCom = new WorkflowOutput { XComTask = "epc_h5_delivery", XComKey = "return_value", Match = "dataset--File.Generic" },
                Minimum = 2,
            },
        };

        var document = FakeOsduPlatform.Record(anchor, "osdu:wks:work-product--WorkProduct:1.2.0", new JsonObject
        {
            ["Name"] = "field-a",
            ["Components"] = new JsonArray("dev:work-product-component--WellLog:a:", "dev:work-product-component--WellLog:b:"),
        });

        using var rig = new Rig(platform);
        var protocol = rig.Protocol(route, rig.Airflow(AirflowApiVersion.V2));
        var runId = Guid.CreateVersion7();
        DeliveryOutcome outcome;
        using (OsduCorrelation.ForRun("wl", FlowTelemetry.Delivery, runId))
        {
            outcome = await protocol.DeliverAsync(Work(document));
        }

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("dev:dataset--File.Generic:epc-out,dev:dataset--File.Generic:h5-out", outcome.Returned[OsduWorkflowProtocol.RecordIdsValue]);
        var sent = AssertContract(Assert.Single(platform.Runs), WorkflowCatalog.EnergymlDelivery);
        Assert.Equal("field-a.epc", sent["name"]!.GetValue<string>());

        // The XCom entries are read from Airflow behind the Workflow service under the run's correlation id, as the service is asked.
        var xcom = platform.Calls.Where(c => c.Uri.AbsolutePath.Contains("/xcomEntries/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(xcom);
        Assert.All(xcom, c => Assert.Equal($"osdu-delivery-wl-dlv-{runId:D}", c.Headers[OsduCorrelation.HeaderName]));

        // Created: a later change to the record is written, and the export is not run again.
        var later = await protocol.DeliverAsync(Work(document, existing: outcome.TargetVersion, forced: new HashSet<string>()));
        Assert.True(later.Succeeded, later.Failure?.Message);
        Assert.Single(platform.Runs);
        AssertConform(platform, OsduContracts.AirflowV2, OsduContracts.AirflowAuth);
    }

    [Fact]
    public async Task External_data_services_run_a_job_only_when_an_operator_asks_and_the_scheduler_and_naturalization_take_their_contexts()
    {
        var platform = new FakeOsduPlatform();
        const string job = "dev:master-data--ConnectedSourceDataJob:job-1";
        platform.Register("eds_ingest");
        platform.Register("eds_scheduler");
        platform.Register("eds_naturalization");

        using var rig = new Rig(platform);
        var ingest = rig.Protocol(new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            RunWhen = WorkflowRunWhen.Requested,
            Stages = [Stage("eds_ingest", """{ "connectedSourceDataJobId": "{record:id}" }""")],
        });
        var document = FakeOsduPlatform.Record(job, "osdu:wks:master-data--ConnectedSourceDataJob:2.0.0");

        // Writing the job does not start it (EDS brief section 2.2: operator-started).
        var written = await ingest.DeliverAsync(Work(document));
        Assert.True(written.Succeeded, written.Failure?.Message);
        Assert.Empty(platform.Runs);
        Assert.Contains("runs when a redelivery names it", written.Detail, StringComparison.Ordinal);

        // A redelivery of the workflow starts it, with the job's id and no Payload, which the EDS DAG does not read.
        var requested = await ingest.DeliverAsync(Work(document, existing: written.TargetVersion, forced: new HashSet<string> { PayloadParts.Workflow }, metadata: false));
        Assert.True(requested.Succeeded, requested.Failure?.Message);
        var run = Assert.Single(platform.Runs);
        Assert.Equal(job, AssertContract(run, WorkflowCatalog.EdsIngest)["connectedSourceDataJobId"]!.GetValue<string>());
        Assert.Null(run.Context["Payload"]);

        var scheduler = rig.Protocol(new WorkflowRoute { Anchor = WorkflowAnchor.Storage, Stages = [Stage("eds_scheduler", "{}")] });
        Assert.True((await scheduler.DeliverAsync(Work(FakeOsduPlatform.Record("dev:master-data--ConnectedSourceRegistryEntry:csre-1", "osdu:wks:master-data--ConnectedSourceRegistryEntry:1.0.0")))).Succeeded);
        Assert.Empty(AssertContract(platform.Runs[1], WorkflowCatalog.EdsScheduler));

        var naturalize = rig.Protocol(new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            Stages = [Stage("eds_naturalization", """{ "items": [{ "id": "{record:id}" }] }""")],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Anchor },
        });
        var wpc = FakeOsduPlatform.Record("dev:work-product-component--Document:doc-1", "osdu:wks:work-product-component--Document:1.0.0");
        var naturalized = await naturalize.DeliverAsync(Work(wpc));
        Assert.True(naturalized.Succeeded, naturalized.Failure?.Message);
        Assert.Equal("dev:work-product-component--Document:doc-1", AssertContract(platform.Runs[2], WorkflowCatalog.EdsNaturalization)["items"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("1", naturalized.Returned[OsduWorkflowProtocol.RecordsValue]);
        AssertConform(platform);
    }

    [Fact]
    public async Task Osdu_ingest_takes_an_inline_manifest_the_context_builds_from_the_record()
    {
        var platform = new FakeOsduPlatform();
        const string anchor = "dev:master-data--Wellbore:wb-inline";
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script
        {
            Effect = (p, run) =>
            {
                foreach (var node in run.Context["manifest"]!["MasterData"]!.AsArray())
                {
                    p.Put(FakeOsduPlatform.Record(node!["id"]!.GetValue<string>(), node["kind"]!.GetValue<string>()));
                }
            },
        });

        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            Stages = [Stage("Osdu_ingest", """{ "manifest": { "kind": "osdu:wks:Manifest:1.0.0", "MasterData": [{ "id": "{record:id}-copy", "kind": "{record:kind}", "acl": "{record:acl}", "legal": "{record:legal}", "data": { "FacilityName": "{record:data.Name}" } }] } }""")],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Ids, Template = "{record:id}-copy", Minimum = 1 },
        };

        using var rig = new Rig(platform);
        var outcome = await rig.Protocol(route).DeliverAsync(Work(FakeOsduPlatform.Record(anchor, "osdu:wks:master-data--Wellbore:1.3.0")));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(anchor + "-copy", outcome.Returned[OsduWorkflowProtocol.RecordIdsValue]);
        AssertContract(Assert.Single(platform.Runs), WorkflowCatalog.OsduIngest);
        AssertConform(platform);
    }

    [Fact]
    public void The_workflow_services_test_dag_is_no_delivery_target()
    {
        var contract = WorkflowCatalog.Find("manifest_ingestion", null)!;
        Assert.False(contract.Deliverable);
        var problems = WorkflowContextCheck.CheckTemplate(contract, [], contract.AddsPayload, "stages[0].context");
        Assert.Contains("test DAG", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_fails_fails_the_record_and_the_next_try_starts_a_new_one()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("eds_scheduler", new FakeOsduPlatform.Script { Terminal = "failed" });
        using var rig = new Rig(platform);
        var route = new WorkflowRoute { Anchor = WorkflowAnchor.Storage, Stages = [Stage("eds_scheduler", "{}")] };
        var steps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var document = FakeOsduPlatform.Record("dev:master-data--ConnectedSourceRegistryEntry:csre-2", "osdu:wks:master-data--ConnectedSourceRegistryEntry:1.0.0");
        DeliveryWork Tracked() => Work(document) with
        {
            CompletedSteps = steps,
            StepCompleted = (report, _) =>
            {
                steps[report.Step] = report.Returned;
                return Task.CompletedTask;
            },
        };

        var first = await rig.Protocol(route).DeliverAsync(Tracked());
        Assert.False(first.Succeeded);
        Assert.Contains("failed (stage 1)", first.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal("failed", steps["stage-1"]["state"]);

        platform.Workflows["eds_scheduler"] = new FakeOsduPlatform.Script();
        var second = await rig.Protocol(route).DeliverAsync(Tracked());
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.Equal(2, platform.Runs.Count);
        Assert.NotEqual(platform.Runs[0].RunId, platform.Runs[1].RunId);

        // The anchor an earlier try wrote is not written again.
        Assert.Single(platform.Calls, c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == "/api/storage/v2/records");
    }

    [Fact]
    public async Task A_trigger_whose_answer_was_lost_is_sent_again_under_its_run_id_and_a_finished_run_is_not_run_again()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("eds_scheduler");
        using var rig = new Rig(platform);
        var document = FakeOsduPlatform.Record("dev:master-data--ConnectedSourceRegistryEntry:csre-3", "osdu:wks:master-data--ConnectedSourceRegistryEntry:1.0.0");
        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            Stages = [Stage("eds_scheduler", "{}")],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Ids, Template = "dev:master-data--Well:late", Minimum = 1 },
        };

        // The record was written and the service took the run, and the try stopped before its answer was recorded.
        platform.Put(document);
        const string runId = "11111111-2222-3333-4444-555555555555";
        platform.Runs.Add(new FakeOsduPlatform.Run("eds_scheduler", runId, []));
        var steps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["anchor"] = new Dictionary<string, string> { ["recordId"] = document["id"]!.GetValue<string>(), ["version"] = "5" },
            ["stage-1"] = new Dictionary<string, string> { ["runId"] = runId, ["state"] = "triggering", ["workflow"] = "eds_scheduler" },
        };
        DeliveryWork Tracked() => Work(document) with
        {
            CompletedSteps = steps,
            StepCompleted = (report, _) =>
            {
                steps[report.Step] = report.Returned;
                return Task.CompletedTask;
            },
        };

        // The result is not there yet: the try fails after the run finished.
        var first = await rig.Protocol(route).DeliverAsync(Tracked());
        Assert.False(first.Succeeded);
        Assert.Contains("0 of the 1 record(s) the route requires were found", first.Failure!.Message, StringComparison.Ordinal);
        Assert.Single(platform.Runs);
        Assert.Equal("finished", steps["stage-1"]["state"]);
        Assert.Contains(platform.Calls, c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/workflowRun", StringComparison.Ordinal));

        // The next try finds it, and runs nothing again.
        platform.Put(FakeOsduPlatform.Record("dev:master-data--Well:late", "osdu:wks:master-data--Well:1.0.0"));
        var triggers = platform.Triggers("eds_scheduler").Count();
        var second = await rig.Protocol(route).DeliverAsync(Tracked());
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.Equal(triggers, platform.Triggers("eds_scheduler").Count());
        Assert.Single(platform.Runs);
    }

    [Fact]
    public async Task A_context_its_workflow_would_refuse_holds_the_record_before_the_trigger()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("csv_ingestion");
        using var rig = new Rig(platform);
        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Storage,
            Stages = [Stage("csv_ingestion", """{ "id": "{record:data.Name}", "dataPartitionId": "{partition}", "data_service_to_use": "{record:data.Service}" }""")],
        };
        var document = FakeOsduPlatform.Record("dev:master-data--Well:w-1", "osdu:wks:master-data--Well:1.0.0", new JsonObject { ["Name"] = "", ["Service"] = "blob" });
        var outcome = await rig.Protocol(route).DeliverAsync(Work(document));
        var held = Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Contains("'id' must not be empty", held.Message, StringComparison.Ordinal);
        Assert.Contains("'data_service_to_use' is 'blob'", held.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Runs);
    }

    [Fact]
    public async Task A_removal_takes_what_the_runs_created_and_a_search_is_repeated_to_name_them_all()
    {
        var platform = new FakeOsduPlatform();
        const string anchor = "dev:dataset--File.Generic:csv-remove";
        foreach (var id in new[] { "dev:master-data--Well:r1", "dev:master-data--Well:r2", "dev:dataset--File.Generic:csv-remove-h5-0" })
        {
            platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:master-data--Well:1.0.0"));
        }

        platform.Put(FakeOsduPlatform.Record(anchor, "osdu:wks:dataset--File.Generic:1.0.0"));
        platform.Search = (_, _) => ["dev:master-data--Well:r1", "dev:master-data--Well:r2"];
        using var rig = new Rig(platform);
        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Dataset,
            Inputs = [new WorkflowInput("h5", "osdu:wks:dataset--File.Generic:1.0.0", false)],
            Stages = [Stage("csv_ingestion", """{ "id": "{record:id}", "dataPartitionId": "{partition}" }""")],
            Results = new WorkflowResults { Strategy = WorkflowResultStrategy.Search, Kind = "osdu:wks:master-data--Well:1.0.0", Minimum = 1, MaxRecorded = 1 },
        };
        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OsduWorkflowProtocol.RecordsValue] = "2",
            [OsduWorkflowProtocol.RecordIdsValue] = "dev:master-data--Well:r1",
            [OsduWorkflowProtocol.SearchKindValue] = "osdu:wks:master-data--Well:1.0.0",
            [OsduWorkflowProtocol.InputValue("h5")] = "dev:dataset--File.Generic:csv-remove-h5-0",
        };

        var protocol = rig.Protocol(route);
        var removed = await protocol.DeleteAsync(anchor, RemovalScope.Record, state);
        Assert.True(removed.Deleted);
        Assert.Contains("2 record(s) the route created for it removed at the same scope", removed.Detail, StringComparison.Ordinal);
        Assert.Contains(anchor, platform.Removed);
        Assert.Contains("dev:master-data--Well:r2", platform.Removed);

        // The reversible removal leaves the inputs, so OSDU can restore the record whole; everything takes them.
        Assert.DoesNotContain("dev:dataset--File.Generic:csv-remove-h5-0", platform.Removed);
        var purged = await protocol.DeleteAsync(anchor, RemovalScope.Everything, state);
        Assert.Contains("dev:dataset--File.Generic:csv-remove-h5-0", platform.Purged);
        Assert.Contains(anchor, platform.Purged);
        Assert.True(purged.Deleted);

        // The dataset service's reversible removal is the one a dataset anchor goes through.
        Assert.Contains(platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/softDelete", StringComparison.Ordinal));
        AssertConform(platform);
    }

    [Fact]
    public async Task The_probe_asks_for_every_workflow_the_route_runs()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Energyml_Converter");
        using var rig = new Rig(platform);
        var route = new WorkflowRoute
        {
            Anchor = WorkflowAnchor.Dataset,
            Stages = [Stage("Energyml_Converter", "{}"), Stage("Osdu_ingest_by_reference", "{}")],
        };
        var missing = await rig.Protocol(route).ProbeAsync();
        Assert.False(missing.Reachable);
        Assert.Contains("no workflow named Osdu_ingest_by_reference", missing.Detail, StringComparison.Ordinal);

        platform.Register("Osdu_ingest_by_reference");
        var found = await rig.Protocol(route).ProbeAsync();
        Assert.True(found.Reachable, found.Detail);
        Assert.Contains("knows Energyml_Converter, Osdu_ingest_by_reference", found.Detail, StringComparison.Ordinal);
        AssertConform(platform);
    }

    [Fact]
    public void Every_workflow_the_briefs_name_has_a_contract()
    {
        // Workflow names per deployment (osdu/specs/workflows/INTEGRATION.md section 1.4, EDS brief section 4.3).
        string[] named =
        [
            "Osdu_ingest", "Osdu_ingest_by_reference", "manifest_ingestion", "csv_ingestion", "csv-parser", "csv-parser-pipeline",
            "Energyml_Converter", "Energyml_Delivery", "Enyparser_Translation",
            "Segy_to_vds_conversion_sdms", "segy-to-vds-conversion", "openvds_import",
            "Segy_to_zgy_conversion", "segy-to-zgy-conversion", "sgy-to-zgy", "sgy-to-zgy-pipeline",
            "segy_to_mdio_conversion", "eds_ingest", "Eds_ingest", "eds_scheduler", "eds_naturalization",
        ];
        foreach (var name in named)
        {
            Assert.True(WorkflowCatalog.Find(name, null) is not null, $"{name} has no contract");
        }

        Assert.Equal(13, WorkflowCatalog.All.Count);
        Assert.Equal(WorkflowCatalog.All.Count, WorkflowCatalog.All.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(named.Length, WorkflowCatalog.All.Sum(c => c.Workflows.Count));
        Assert.All(WorkflowCatalog.All, c => Assert.StartsWith("osdu/specs/", c.Source, StringComparison.Ordinal));
        Assert.Equal(
            ["energymlConverter", "enyparserTranslation"],
            WorkflowCatalog.All.Where(c => c.TranslatesOnly).Select(c => c.Name));
        Assert.Equal(1440, WorkflowCatalog.Find("sgy-to-zgy", null)!.TimeoutMinutes);
        Assert.Equal(180, WorkflowCatalog.Find("Osdu_ingest", null)!.TimeoutMinutes);
    }
}
