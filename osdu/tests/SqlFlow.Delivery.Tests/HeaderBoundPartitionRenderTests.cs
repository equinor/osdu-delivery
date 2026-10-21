using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A delivery flow bound to its partition by its <c>data-partition-id</c> header, rendered as a run renders it: the mapping's
/// <c>dataPartition</c>, and so every id the run mints, is the partition the header names, resolved the way the header is,
/// and never the partition <c>OSDU_DATA_PARTITION</c> happens to hold where the run resolves its references.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class HeaderBoundPartitionRenderTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>The environment a run resolves its references in: what the central configuration and the node hold.</summary>
    private sealed class RunEnvironment(params (string Name, string Value)[] variables) : ISecretProvider
    {
        private readonly Dictionary<string, string> _held = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        public string Scheme => "env";

        public string Resolve(string locator)
            => _held.TryGetValue(locator, out var value) ? value : throw new SqlFlowException($"Environment variable '{locator}' is not set.");
    }

    [Fact]
    public async Task A_flow_bound_by_its_header_mints_every_id_in_the_partition_its_requests_carry()
    {
        var templates = new MemoryTemplates(TestSchema.Build());
        var directory = Samples.NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "Thing@1.0.0.yaml"), TestSchema.MappingDocument());

        // The estate keeps an estate-wide OSDU_DATA_PARTITION for its other flows; this flow's header names its own.
        var secrets = new SecretResolver([new RunEnvironment(("OSDU_DATA_PARTITION", "elsewhere"), ("TARGET_PARTITION", "dev"))]);
        var resolver = new RenderResolver(new MappingCatalog(directory, new DeliveryDocumentLoader()), _db.Caches(), templates, secrets);
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = "https://osdu.example.test",
            Protocol = DeliveryProtocol.Storage,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "${env:TARGET_PARTITION}" },
        }) with
        {
            Render = new FlowRender { Mapping = "Thing@1.0.0" },
        };

        var resolved = await resolver.ResolveAsync(flow);
        Assert.Equal("dev", resolved.Context.Parameters[DeliveryDestination.DataPartitionParameter]);

        var rendered = resolved.Renderer.Render(new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "Wellbore A-1", ["depth"] = "1" }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
        });
        Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
        Assert.StartsWith("dev:", rendered.TargetId, StringComparison.Ordinal);

        // A flow that names its partition literally is minted in it the same way, and a value the flow supplies still wins.
        var literal = await resolver.ResolveAsync(flow with
        {
            Target = flow.Target with { Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "test" } },
        });
        Assert.Equal("test", literal.Context.Parameters[DeliveryDestination.DataPartitionParameter]);
        var pinned = await resolver.ResolveAsync(flow with
        {
            Render = flow.Render with { Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [DeliveryDestination.DataPartitionParameter] = "${env:OSDU_DATA_PARTITION}" } },
        });
        Assert.Equal("elsewhere", pinned.Context.Parameters[DeliveryDestination.DataPartitionParameter]);
    }
}
