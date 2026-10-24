using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A storage read of an inventory whose kind carries a wildcard inside a segment (<c>1.*.*</c>), which the document accepts as
/// the search service does: the schema service's kinds are matched segment by segment with <c>*</c> standing for any text
/// within the segment, so the build lists every kind the search would read, and a plan names the same kinds.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryWildcardKindTests : IDisposable
{
    private const string Partition = "dev";
    private const string Estate = "delivery-sp@example.com";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _search = new();
    private readonly StoragePlatform _platform;

    public InventoryWildcardKindTests()
    {
        _platform = new StoragePlatform(_search);
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private static string Flow(string kind) => $$"""
        flowType: inventory
        name: welllog-inventory
        partitions: [dev]
        source:
          endpoint: http://localhost
          read: storage
        inventories:
          - name: WellLogs
            kind: "{{kind}}"
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        """;

    private void Add(string key, string kind)
    {
        var record = _search.Add($"{Partition}:work-product-component--WellLog:{key}", kind, new JsonObject { ["Name"] = key });
        record["version"] = 3;
        record["createUser"] = Estate;
        record["createTime"] = "2026-09-01T08:00:00.000Z";
        record["modifyUser"] = Estate;
        record["modifyTime"] = "2026-09-02T08:00:00.000Z";
    }

    private async Task<InventoryRunner> RunnerAsync(string kind)
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(FlowId.Of("welllog-delivery"));
        var flow = new DeliveryDocumentLoader().ParseInventory(Flow(kind), "flows/inventory.yaml").ForPartition(Partition);
        return new InventoryRunner(Samples.Engine(ledger, _clock), flow, new Dictionary<string, string>(), Samples.Logger<InventoryRunner>(), _platform, allowLoopback: true);
    }

    [Theory]
    [InlineData("osdu:wks:work-product-component--WellLog:1.*.*", new[] { "osdu:wks:work-product-component--WellLog:1.0.0", "osdu:wks:work-product-component--WellLog:1.4.0" })]
    [InlineData("osdu:wks:work-product-component--WellLog:*.0.0", new[] { "osdu:wks:work-product-component--WellLog:1.0.0", "osdu:wks:work-product-component--WellLog:2.0.0" })]
    [InlineData("osdu:wks:work-product-component--Well*:1.4.0", new[] { "osdu:wks:work-product-component--WellLog:1.4.0" })]
    [InlineData("OSDU:wks:*:1.4.*", new[] { "osdu:wks:work-product-component--WellLog:1.4.0" })]
    public async Task A_wildcard_inside_a_segment_lists_every_kind_it_matches_as_the_search_service_matches_it(string kind, string[] expected)
    {
        Add("a", "osdu:wks:work-product-component--WellLog:1.0.0");
        Add("b", "osdu:wks:work-product-component--WellLog:1.4.0");
        Add("c", "osdu:wks:work-product-component--WellLog:2.0.0");
        Add("d", "osdu:wks:work-product-component--WellLogAcquisition:1.0.0");
        var runner = await RunnerAsync(kind);

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal(InventoryRunStatus.Completed, summary.Status);
        Assert.Equal(expected, _platform.KindsListed.Order(StringComparer.Ordinal));
        Assert.Equal(expected.Length, summary.Listed);
    }

    [Fact]
    public async Task A_plan_names_the_kinds_a_wildcard_inside_a_segment_expands_to()
    {
        Add("a", "osdu:wks:work-product-component--WellLog:1.0.0");
        Add("b", "osdu:wks:work-product-component--WellLog:1.4.0");
        Add("c", "osdu:wks:work-product-component--WellLog:2.0.0");
        var runner = await RunnerAsync("osdu:wks:work-product-component--WellLog:1.*.*");

        var inventory = Assert.Single((await runner.PlanAsync([], CancellationToken.None)).Inventories);

        Assert.Equal(["osdu:wks:work-product-component--WellLog:1.0.0", "osdu:wks:work-product-component--WellLog:1.4.0"], inventory.Kinds);
        Assert.Equal(0, _platform.Listings);
    }
}
