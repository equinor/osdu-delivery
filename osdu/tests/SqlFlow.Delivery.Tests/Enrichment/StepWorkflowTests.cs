using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The workflow a flow runs over an enrichment of its mapping (<c>workflows.&lt;name&gt;</c>,
/// osdu/docs/reference/flow/enrich-workflow.md): declared as <c>target.workflow</c> is, with no anchor, anchor tag or inputs,
/// its placeholders checked against what it has; run through the workflow route itself once the record and the enrichment's
/// dataset landed, reading the dataset as <c>{dataset:enrich.&lt;name&gt;}</c>, its steps, values and artifacts kept under the
/// enrichment; run again only when the enrichment's files move; a failed run failing the record; its runs undone, and its
/// records removed with the record.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepWorkflowTests
{
    private const string OwnerId = "dev:work-product-component--WellLog:wf-1";
    private const string OwnerKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Produced = "dev:master-data--Well:from-las";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private const string Head = """
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
          datasets:
            lasfile:
              object: OsduData.silver.WellLogFile
              join: { source_project: source_project, log_id: log_id }
              files:
                root: ../data/las
                locationColumn: las_folder
                hashColumn: las_hash
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

    private static readonly MappingDefinition LasDataset = EnrichmentSuite.Fragment("data:\n  Name: { $from: file_name }", name: "LasDataset", kind: EnrichmentSuite.DatasetKind);

    private static readonly MappingDefinition Grade = EnrichmentSuite.Fragment("data:\n  Symbol: { $from: grade }", name: "Grade");

    /// <summary>The test flow with <paramref name="workflow"/>, bound to a mapping whose enrichment <c>lasfile</c> registers the LAS files as datasets.</summary>
    private static FlowDefinition Parsed(string workflow, string head = Head, string enrichment = "LasDataset@1.0.0")
        => EnrichmentSuite.Bound(
            new DeliveryDocumentLoader().ParseFlow(head + workflow, "flows/demo.yaml"),
            EnrichmentSuite.Owner(enrich: $"lasfile:\n  rows: one\n  mapping: {enrichment}\n"),
            LasDataset,
            Grade);

    [Fact]
    public void An_enrichments_workflow_is_read_as_the_routes_is_and_anchored_on_the_record()
    {
        var step = Assert.Single(Parsed(Workflow).Enrich!);

        var workflow = step.Workflow!;
        Assert.Equal((WorkflowAnchor.Storage, WorkflowRunWhen.Changed, "csv_ingestion", 1), (workflow.Anchor, workflow.RunWhen, Assert.Single(workflow.Stages).Workflow, workflow.Results.Minimum));
        Assert.Empty(workflow.Inputs);
    }

    [Theory]
    [InlineData("    anchor: storage\n", "names an anchor or an anchor tag")]
    [InlineData("    anchorTag: osduAnchor\n", "names an anchor or an anchor tag")]
    [InlineData("    inputs:\n      logs:\n        root: r\n        locationColumn: c\n", "workflows.lasfile.inputs registers files")]
    public void An_enrichments_workflow_names_no_anchor_and_no_inputs(string extra, string reason)
    {
        var refused = Assert.Throws<FlowValidationException>(() => Parsed(Workflow.Replace("    stages:\n", extra + "    stages:\n", StringComparison.Ordinal)));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"{input:logs}\"", "names the input 'logs', and the route registers none")]
    [InlineData("\"{secret:key}\"", "names the secret 'key', which workflows.lasfile.secrets does not declare")]
    public void An_enrichments_workflow_names_only_what_it_has(string value, string reason)
    {
        var refused = Assert.Throws<FlowValidationException>(() => Parsed(Workflow.Replace("\"{dataset:enrich.lasfile}\"", value, StringComparison.Ordinal)));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_runs_over_an_enrichment_adding_paths_alone()
    {
        var head = Head.Replace("""
                  files:
                    root: ../data/las
                    locationColumn: las_folder
                    hashColumn: las_hash
            """.ReplaceLineEndings("\n"), string.Empty, StringComparison.Ordinal);
        var flow = Parsed(Workflow.Replace("{dataset:enrich.lasfile}", "{record:id}", StringComparison.Ordinal), head, "Grade@1.0.0");

        var step = Assert.Single(flow.Enrich!);
        Assert.Null(step.Records);
        Assert.Equal("Grade@1.0.0", step.Mapping);
        Assert.NotNull(step.Workflow);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(WorkflowRunWhen runWhen = WorkflowRunWhen.Changed, string terminal = "finished")
        {
            Platform = new FakeOsduPlatform();
            Platform.Register("csv_ingestion", new FakeOsduPlatform.Script
            {
                Terminal = terminal,
                Effect = (p, _) => p.Put(FakeOsduPlatform.Record(Produced, "osdu:wks:master-data--Well:1.0.0")),
            });
            Faults = new FileFamilyFaults(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Faults, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var options = new ProtocolOptions
            {
                WorkflowPollSeconds = 1,
                DatasetIndexWaitSeconds = 0,
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            };
            var route = Parsed(Workflow).Enrich![0].Workflow! with { RunWhen = runWhen };
            var workflow = new StepWorkflow("lasfile", runWhen, true, new OsduWorkflowProtocol(Client, options, route, Samples.Logger<OsduWorkflowProtocol>(), Secrets));
            Protocol = new EnrichPartsProtocol(new OsduRecordProtocol(Client, options), Client, options, workflows: [workflow]);
        }

        public FakeOsduPlatform Platform { get; }

        public FileFamilyFaults Faults { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public EnrichPartsProtocol Protocol { get; }

        public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work) => (await Protocol.DeliverBatchAsync([work]))[0];

        public void Dispose()
        {
            Runtime.Dispose();
            Faults.Dispose();
        }
    }

    private static string DatasetId => WorkflowValues.DerivedDatasetId(OwnerId, "dataset--File.Generic", "enrich.lasfile");

    private static JsonObject Owner(string name = "log") => FakeOsduPlatform.Record(OwnerId, OwnerKind, new JsonObject
    {
        ["Name"] = name,
        ["Datasets"] = new JsonArray(DatasetId + ":"),
    });

    private static DeliveryWork Work(JsonObject document, string hash, DeliveryOutcome? earlier = null, bool unchanged = false)
        => new()
        {
            Key = DeliveryKey.Derive("step-workflow", [OwnerId]),
            TargetId = OwnerId,
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = false,
            StepParts = [new WorkStepPart(new EnrichPart("lasfile", DatasetId, EnrichmentSuite.DatasetKind, "mem://las/*", hash, "{}", null), new MemoryFiles(("a.csv", "uwi\n1\n" + hash)))],
            StepDocumentUnchanged = unchanged,
            StepPartsForced = earlier is null,
            ExistingVersion = earlier?.TargetVersion,
            TargetState = earlier?.Returned ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    [Fact]
    public async Task The_workflow_runs_over_the_record_once_its_dataset_landed_and_keeps_its_values_under_the_step()
    {
        using var rig = new Rig();
        var ledger = new FileFamilyLedger(() => rig.Platform.Calls.Count);

        var outcome = await rig.DeliverAsync(ledger.Track(Work(Owner(), "h1")));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var run = Assert.Single(rig.Platform.Runs);
        Assert.Equal(DatasetId, run.Context["id"]!.GetValue<string>());
        Assert.Contains(Produced, rig.Platform.Records.Keys);
        Assert.Equal("1", outcome.Returned["enrich.lasfile." + OsduWorkflowProtocol.RecordsValue]);
        Assert.False(outcome.Returned.ContainsKey(OsduWorkflowProtocol.RecordsValue));
        Assert.Contains(outcome.Steps, s => s.Name == "enrich.lasfile.stage-1");
        Assert.Contains(ledger.Reports, r => r.Step.StartsWith("enrich.lasfile.", StringComparison.Ordinal));
        Assert.All(ledger.Rows.Where(a => a.Role == ArtifactRoles.Run), a => Assert.StartsWith("enrich.lasfile.", a.Slot, StringComparison.Ordinal));
        Assert.Equal(rig.Platform.Records[OwnerId]["version"]!.GetValue<long>(), outcome.TargetVersion);
    }

    [Fact]
    public async Task The_workflow_runs_again_when_the_steps_files_move_and_not_for_a_change_of_the_record_alone()
    {
        using var rig = new Rig();
        var first = await rig.DeliverAsync(Work(Owner(), "h1"));

        var renamed = await rig.DeliverAsync(Work(Owner("renamed"), "h1", first));
        Assert.True(renamed.Succeeded, renamed.Failure?.Message);
        Assert.Single(rig.Platform.Runs);

        var moved = await rig.DeliverAsync(Work(Owner("renamed"), "h2", renamed, unchanged: true));
        Assert.True(moved.Succeeded, moved.Failure?.Message);
        Assert.Equal(2, rig.Platform.Runs.Count);
    }

    [Fact]
    public async Task A_workflow_that_runs_when_the_record_is_created_runs_once()
    {
        using var rig = new Rig(WorkflowRunWhen.Created);
        var first = await rig.DeliverAsync(Work(Owner(), "h1"));

        var moved = await rig.DeliverAsync(Work(Owner(), "h2", first, unchanged: true));

        Assert.True(moved.Succeeded, moved.Failure?.Message);
        Assert.Single(rig.Platform.Runs);
    }

    [Fact]
    public async Task A_run_that_fails_fails_the_record_naming_the_step()
    {
        using var rig = new Rig(terminal: "failed");

        var outcome = await rig.DeliverAsync(Work(Owner(), "h1"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("enrich.lasfile: its workflow did not finish", outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfinished_delivery_hands_the_steps_runs_to_the_workflow_route_under_their_own_names()
    {
        using var rig = new Rig(terminal: "failed");
        var ledger = new FileFamilyLedger(() => rig.Platform.Calls.Count);
        var work = ledger.Track(Work(Owner(), "h1"));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);

        var runs = undo.Items.Where(i => i.Artifact.Role == ArtifactRoles.Run).ToList();
        Assert.NotEmpty(runs);
        Assert.All(runs, run =>
        {
            var result = Assert.Single(results, r => r.Item.ArtifactId == run.ArtifactId);
            Assert.Same(run, result.Item);
            Assert.Equal(ArtifactStatus.Kept, result.Outcome);
        });
        Assert.Equal(undo.Items.Count, results.Count);
    }

    [Fact]
    public async Task Removing_the_record_takes_what_its_steps_workflow_created()
    {
        using var rig = new Rig();
        var first = await rig.DeliverAsync(Work(Owner(), "h1"));

        var removed = await rig.Protocol.DeleteAsync(OwnerId, RemovalScope.Record, first.Returned);

        Assert.True(removed.Deleted);
        Assert.Contains(Produced, rig.Platform.Removed);
        Assert.Contains("enrich.lasfile: 1 record(s) the route created for it removed at the same scope", removed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_whose_steps_run_workflows_gets_its_route_wrapped_with_one_workflow_route_a_step()
    {
        var flow = Parsed(Workflow);
        using var http = new HttpRuntime(new FlowReliability(), Secrets, TimeProvider.System, new FakeOsduPlatform(), allowLoopback: true);

        var route = await ProtocolFactory.CreateAsync(flow, http, Secrets, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var wrapped = Assert.IsType<EnrichPartsProtocol>(route);
        var workflow = Assert.Single(wrapped.Workflows);
        Assert.Equal(("lasfile", WorkflowRunWhen.Changed, true, "enrich.lasfile."), (workflow.Step, workflow.RunWhen, workflow.RegistersFiles, workflow.Prefix));
        Assert.IsType<OsduRecordProtocol>(wrapped.Inner);
    }

    [Fact]
    public async Task The_probe_asks_the_workflow_service_for_the_steps_workflows()
    {
        using var rig = new Rig();

        var probe = await rig.Protocol.ProbeAsync();

        Assert.True(probe.Reachable, probe.Detail);
        Assert.Contains(rig.Platform.Calls, c => c.Uri.AbsolutePath.Contains("/workflow", StringComparison.Ordinal));
    }
}
