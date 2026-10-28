using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a step set aside on its hold (osdu/docs/reference/flow/enrich-holds.md) is in the document, the render context and the
/// plan: <c>onHold</c> read with its default and refused otherwise, named in the context only when it sets the step aside; and
/// the plan keeping what each such step composed, linking a set-aside step's datasets at the hash its last delivery kept, and
/// naming a step whose files cannot be found so the record renders again with it held.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepHoldsPlanTests
{
    private const string Head = """
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.Thing
            key: [name]
          work: work
        render:
          mapping: Thing@1.0.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/api
          headers: { data-partition-id: dev }
          protocol: storage
        enrich:
          grade:
            dataset:
              object: OsduData.silver.ThingGrade
              join: { name: name }
            mapping: Grade@1.0.0

        """;

    private static FlowDefinition Parsed(string extra) => new DeliveryDocumentLoader().ParseFlow(Head + extra, Path.Combine(Path.GetTempPath(), "enrich-holds", "flows", "demo.yaml"));

    [Theory]
    [InlineData("", EnrichHold.Record)]
    [InlineData("    onHold: record\n", EnrichHold.Record)]
    [InlineData("    onHold: step\n", EnrichHold.Step)]
    public void OnHold_is_read_with_its_default(string line, EnrichHold expected)
    {
        Assert.Equal(expected, Assert.Single(Parsed(line).Enrich!).OnHold);
    }

    [Fact]
    public void Another_onHold_is_refused_naming_both_words()
    {
        var refused = Assert.Throws<FlowValidationException>(() => Parsed("    onHold: skip\n"));

        Assert.Contains("enrich.grade.onHold is 'skip'; write record", refused.Message, StringComparison.Ordinal);
        Assert.Contains("or step for one set aside", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_render_context_names_a_step_set_aside_and_writes_any_other_as_it_was()
    {
        var plain = new RenderStep("grade", "Grade@1.0.0", "fp", "v1", new Dictionary<string, string>(StringComparer.Ordinal));
        var aside = plain with { SetAsideOnHold = true };
        var context = TestSchema.Context() with { Steps = [plain] };

        Assert.DoesNotContain("onHold", context.Canonical(), StringComparison.Ordinal);
        var named = (context with { Steps = [aside] }).Canonical();
        Assert.Contains("\"onHold\":\"step\"", named, StringComparison.Ordinal);
        Assert.True(Assert.Single(RenderContext.Parse(named).Steps).SetAsideOnHold);
        Assert.NotEqual(context.RulesHash(), (context with { Steps = [aside] }).RulesHash());
    }

    private static readonly MappingDefinition Grade = Fragment("data:\n  Symbol: { $from: grade }", name: "Grade");

    private static (RenderResult Render, string? Hold, StepAside? Aside) Plan(FlowDefinition flow, IReadOnlyList<ResolvedStep> steps, SourceRecord record, ChainInputs inputs, RecordState? state = null)
    {
        var render = new RecordChain(Renderer(Owner()), steps).Render(record, inputs);
        return EnrichParts.Resolve(flow, new Dictionary<string, string>(StringComparer.Ordinal), record, render, steps, state, hasPayload: false);
    }

    private static ResolvedStep AsideStep(FlowDefinition flow)
    {
        var step = Step("grade", Grade);
        return step with { Step = flow.Enrich![0] };
    }

    [Fact]
    public void The_plan_keeps_what_a_step_set_aside_on_its_hold_composed_and_nothing_of_one_that_holds_the_record()
    {
        var flow = Parsed("    onHold: step\n");
        var (render, hold, aside) = Plan(flow, [AsideStep(flow)], Record("a", 1, ("grade", [Row(("grade", "A"))])), ChainInputs.None);

        Assert.Null(hold);
        Assert.Null(aside);
        var kept = KeptStep.Parse(render.StepWork!.Keep["grade"])!;
        Assert.Equal("A", Assert.Single(kept.Fragments)["data"]!["Symbol"]!.GetValue<string>());
        Assert.Equal(Hashing.ContentHash.Of(render.Canonical), render.MetadataHash);
        Assert.NotNull(EnrichParts.Encode(render.StepWork));

        var plain = Parsed(string.Empty);
        var (none, _, _) = Plan(plain, [Step("grade", Grade) with { Step = plain.Enrich![0] }], Record("a", 1, ("grade", [Row(("grade", "A"))])), ChainInputs.None);
        Assert.Null(none.StepWork);
    }

    [Fact]
    public void A_step_set_aside_is_not_kept_again_and_its_datasets_go_on_being_linked_at_their_kept_hash()
    {
        var flow = Parsed("    onHold: step\n");
        var dataset = WorkflowValues.DerivedDatasetId("dev:work-product-component--Thing:x", "dataset--File.Generic", "enrich.grade");
        var keptState = new JsonObject
        {
            [KeptStep.StateKey("grade")] = new KeptStep([new JsonObject { ["data"] = new JsonObject { ["Symbol"] = "K" } }], [dataset]).ToText(),
            [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText([dataset]),
            [EnrichParts.StateKey(dataset)] = "kept-hash",
        };
        var state = new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", ["a"]),
            FlowId = Guid.NewGuid(),
            SourceKey = "a",
            MappingName = "Thing",
            TargetVersion = 2,
            TargetStateJson = keptState.ToJsonString(),
        };
        var record = Record("a", 1, ("grade", [Row(("grade", "A")), Row(("grade", "B"))]));

        var (render, hold, _) = Plan(flow, [AsideStep(flow)], record, new ChainInputs { Kept = KeptSteps.From(state) }, state);

        Assert.Null(hold);
        Assert.Equal(["grade"], render.StepsSetAside);
        Assert.False(render.StepWork!.Keep.ContainsKey("grade"));
        Assert.Equal([dataset], render.StepWork.KeptDatasets);
        Assert.Equal(EnrichParts.Fold(render.Canonical, [(dataset, "kept-hash")]), render.MetadataHash);

        // Delivered as it was, the record hashes as it did: nothing is sent while the step holds.
        var landed = state with { MetadataHash = render.MetadataHash };
        var (again, _, _) = Plan(flow, [AsideStep(flow)], record, new ChainInputs { Kept = KeptSteps.From(landed) }, landed);
        Assert.True(again.StepWork!.DocumentUnchanged);
        Assert.Equal(render.MetadataHash, again.MetadataHash);
    }

    [Fact]
    public void A_step_set_aside_whose_files_cannot_be_found_is_named_for_the_plan_to_render_without_it()
    {
        var yaml = Head.Replace("    mapping: Grade@1.0.0\n", "    mapping: Grade@1.0.0\n    onHold: step\n    files:\n      root: ../data/las\n      locationColumn: las_folder\n      hashColumn: las_hash\n", StringComparison.Ordinal);
        var flow = new DeliveryDocumentLoader().ParseFlow(yaml, Path.Combine(Path.GetTempPath(), "enrich-holds", "flows", "demo.yaml"));
        var files = flow.Enrich![0].Files!;
        var step = Step("grade", Grade) with { Step = flow.Enrich[0] };
        step = step with { Files = new ResolvedFiles(files, "dataset--File.Generic", null, null) };
        var record = Record("a", 1, ("grade", [Row(("grade", "A"), ("las_folder", " "), ("las_hash", "h"))]));

        var (_, hold, aside) = Plan(flow, [step], record, ChainInputs.None);

        Assert.Null(hold);
        Assert.Equal("grade", aside!.Step);
        Assert.Contains("takes its folder from column 'las_folder'", aside.Reason, StringComparison.Ordinal);

        // Held for it, the step is set aside and the plan has nothing more to find.
        var (render, again, second) = Plan(flow, [step], record, ChainInputs.None.Hold(aside.Step, aside.Reason));
        Assert.Null(again);
        Assert.Null(second);
        Assert.Equal(["grade"], render.StepsSetAside);
    }

    [Fact]
    public async Task The_route_keeps_a_set_aside_steps_datasets_listed_neither_sent_nor_superseded()
    {
        var owner = "dev:work-product-component--WellLog:holds-1";
        var kept = WorkflowValues.DerivedDatasetId(owner, "dataset--File.Generic", "enrich.grade");
        var document = FakeOsduPlatform.Record(owner, "osdu:wks:work-product-component--WellLog:1.4.0", new JsonObject { ["Datasets"] = new JsonArray(kept + ":") });
        var platform = new FakeOsduPlatform();
        using var http = new Http.HttpRuntime(new FlowReliability { Retry = new FlowRetry { Attempts = 1 } }, new Core.Secrets.SecretResolver([new Core.Secrets.EnvSecretProvider()]), TimeProvider.System, platform, allowLoopback: true);
        var client = new OsduHttpClient(http, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        var route = new EnrichPartsProtocol(new OsduRecordProtocol(client, new ProtocolOptions()), client, new ProtocolOptions());
        var state = new Dictionary<string, string>(StringComparer.Ordinal) { [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText([kept]), [EnrichParts.StateKey(kept)] = "h" };

        var outcome = await route.DeliverAsync(new DeliveryWork
        {
            Key = DeliveryKey.Derive("holds", [owner]),
            TargetId = owner,
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = false,
            StepKeptDatasets = [kept],
            TargetState = state,
        });

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal([kept], EnrichParts.Datasets(outcome.Returned));
        Assert.Empty(outcome.Superseded);
        Assert.DoesNotContain(platform.Calls, c => c.Uri.AbsolutePath.Contains("registerDataset", StringComparison.Ordinal));
    }
}
