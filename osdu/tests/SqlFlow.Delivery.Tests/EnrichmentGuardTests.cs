using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What keeps a delivery flow without <c>enrich:</c> steps exactly as it was (osdu/docs/reference/flow/enrich.md): its render
/// context, its document's hash and its definition. A moved context would render every record of every flow again, and a moved
/// document hash would orphan a renamed flow's ledger, so these run in the tiers on every pull request, beside the on-demand
/// suite of the steps themselves (<see cref="EnrichmentSuite"/>).
/// </summary>
public sealed class EnrichmentGuardTests
{
    [Fact]
    public void A_render_context_without_steps_is_written_as_it_always_was()
    {
        var context = TestSchema.Context();

        Assert.Equal(
            $$"""{"cache":"dev","cacheVersion":"refs-1","mapping":"Thing@1.0.0","parameters":{"dataPartition":"dev"},"schema":"{{TestSchema.Build().Version}}"}""",
            context.Canonical());
    }

    [Fact]
    public void A_render_context_with_steps_names_them_in_order_and_reads_back_as_written()
    {
        var context = TestSchema.Context() with
        {
            Steps =
            [
                new RenderStep("lasfile", "WellLogFile@1.0.0", "fp-1", TestSchema.Build().Version, new Dictionary<string, string>(StringComparer.Ordinal)),
                new RenderStep("quality", "Quality@2.0.0", null, TestSchema.Build().Version, new Dictionary<string, string>(StringComparer.Ordinal) { ["grade"] = "A" }),
            ],
        };

        var canonical = context.Canonical();
        Assert.Contains("\"enrich\":[{\"mapping\":\"WellLogFile@1.0.0\",\"mappingFingerprint\":\"fp-1\",\"name\":\"lasfile\"", canonical, StringComparison.Ordinal);
        Assert.NotEqual(TestSchema.Context().Hash(), context.Hash());
        Assert.NotEqual(TestSchema.Context().RulesHash(), context.RulesHash());
        var read = RenderContext.Parse(canonical);
        Assert.Equal(canonical, read.Canonical());
        Assert.Equal(["lasfile", "quality"], read.Steps.Select(s => s.Name));
    }

    [Fact]
    public void A_flow_without_enrich_has_no_steps_and_hashes_as_it_did_before_steps_existed()
    {
        // The value the code before enrich: computed, pinned with the same document read the same way.
        var source = new DeliveryDocumentLoader().ParseSource(File.ReadAllText(Samples.Flow).ReplaceLineEndings("\n"), Samples.Flow);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal) { ["WellLog@1.4.0"] = "mapping-hash" };

        Assert.All(source.Interfaces, flow => Assert.Null(flow.Enrich));
        Assert.All(source.Interfaces, flow => Assert.Empty(flow.EnrichWorkflows));
        Assert.Equal(DeliveryDocumentHashTests.SampleFlowHash, DeliveryDocumentHash.Of(source, mappings));
    }

    [Fact]
    public void A_flow_running_workflows_over_its_mappings_enrichments_hashes_apart_from_the_same_flow_without()
    {
        const string workflows = """
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
        var text = File.ReadAllText(Samples.Flow).ReplaceLineEndings("\n");
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal) { ["WellLog@1.4.0"] = "mapping-hash" };

        var running = new DeliveryDocumentLoader().ParseSource(text.TrimEnd('\n') + "\n" + workflows.ReplaceLineEndings("\n") + "\n", Samples.Flow);

        Assert.NotEqual(DeliveryDocumentHashTests.SampleFlowHash, DeliveryDocumentHash.Of(running, mappings));
    }
}
