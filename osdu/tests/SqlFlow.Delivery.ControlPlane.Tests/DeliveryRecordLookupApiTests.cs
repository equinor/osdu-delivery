using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Records page's lookup, as the API serves it: an operator holding a source key, an OSDU id, a delivery key or the
/// name of the file a record came from finds the record without knowing which flow delivered it, across every flow, and
/// narrows to a custody state. With nothing to look for, the same route lists what the delivery system last took in or
/// sent, newest first. Both are indexed reads, so a page past the candidate bound is empty rather than a scan.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryRecordLookupApiTests
{
    private const string FlowName = "wells-wellbore-03-header-delivery";

    [Fact]
    public async Task A_record_is_found_across_flows_by_what_an_operator_holds_and_narrowed_by_state()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var otherFlow = FlowId.Of(FlowName + "-lookup-other");
        var marker = "LK" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var delivered = new DeliveryKey(Guid.NewGuid());
        var held = new DeliveryKey(Guid.NewGuid());
        var elsewhere = new DeliveryKey(Guid.NewGuid());
        var when = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, otherFlow);

        try
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                Record(flowId, delivered, marker + "-A", $"dev:master-data--Wellbore:{marker}-A", marker + "_wellbores.csv", 3),
                Record(flowId, held, marker + "-B", $"dev:master-data--Wellbore:{marker}-B", marker + "_wellbores.csv", 4),
            ]);
            await ledger.UpsertPendingAsync(otherFlow,
            [
                Record(otherFlow, elsewhere, marker + "-C", $"dev:master-data--Well:{marker}-C", marker + "_wells.csv", 1),
            ]);

            var lease = "lookup-test/" + Guid.NewGuid().ToString("N");
            await ledger.AppendAsync(flowId, lease, new LeaseAppend([],
            [
                new RecordCompletion
                {
                    DeliveryKey = delivered,
                    Status = RecordStatus.Delivered,
                    Promote = true,
                    TargetId = $"dev:master-data--Wellbore:{marker}-A",
                    TargetVersion = 1,
                    Attempt = new AttemptRecord
                    {
                        DeliveryKey = delivered, Worker = "lookup-test", StartedUtc = when, CompletedUtc = when.AddSeconds(1),
                        Outcome = AttemptOutcome.Delivered, Phase = "metadata",
                    },
                },
                new RecordCompletion
                {
                    DeliveryKey = held,
                    Status = RecordStatus.Held,
                    Error = "held by the test",
                    Attempt = new AttemptRecord
                    {
                        DeliveryKey = held, Worker = "lookup-test", StartedUtc = when, CompletedUtc = when.AddSeconds(1),
                        Outcome = AttemptOutcome.Held, Phase = "metadata", Error = "held by the test",
                    },
                },
            ]));
            await ledger.CheckpointLeaseAsync(lease, when.AddSeconds(2));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // A source key prefix finds the records of every flow, newest first, each naming its flow's ledger.
            var byKey = await LookupAsync(client, token, $"search={marker}-");
            Assert.Equal(3, byKey.GetProperty("total").GetInt64());
            Assert.False(byKey.GetProperty("totalCapped").GetBoolean());
            var hits = byKey.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(3, hits.Count);
            Assert.Contains(hits, h => h.GetProperty("deliveryKey").GetGuid() == elsewhere.Value && h.GetProperty("flowId").GetGuid() == otherFlow);
            var hitA = Assert.Single(hits, h => h.GetProperty("deliveryKey").GetGuid() == delivered.Value);
            Assert.Equal(flowId, hitA.GetProperty("flowId").GetGuid());
            Assert.Equal("delivered", hitA.GetProperty("status").GetString());
            Assert.Equal($"dev:master-data--Wellbore:{marker}-A", hitA.GetProperty("targetId").GetString());

            // Each hit names the ingestion file and row its newest version came from: the delivered version's for a
            // record that landed, and the one a held record was left at, which never landed.
            Assert.Equal(marker + "_wellbores.csv", hitA.GetProperty("sourceFileName").GetString());
            Assert.Equal(3, hitA.GetProperty("sourceRowNumber").GetInt64());
            var hitHeld = Assert.Single(hits, h => h.GetProperty("deliveryKey").GetGuid() == held.Value);
            Assert.Equal(marker + "_wellbores.csv", hitHeld.GetProperty("sourceFileName").GetString());
            Assert.Equal(4, hitHeld.GetProperty("sourceRowNumber").GetInt64());

            // The OSDU id, the ingestion file name and the delivery key itself find a record too. A file finds every
            // record built from it, landed or not: an operator asking "what came out of this file" means all of them,
            // and the held one is exactly what they are looking for.
            Assert.Equal(1, (await LookupAsync(client, token, $"search=dev:master-data--Well:{marker}")).GetProperty("total").GetInt64());
            var byFile = await LookupAsync(client, token, $"search={marker}_wellbores");
            Assert.Equal(2, byFile.GetProperty("total").GetInt64());
            Assert.Contains(byFile.GetProperty("items").EnumerateArray(), h => h.GetProperty("deliveryKey").GetGuid() == delivered.Value);
            Assert.Contains(byFile.GetProperty("items").EnumerateArray(), h => h.GetProperty("deliveryKey").GetGuid() == held.Value);
            var byDeliveryKey = await LookupAsync(client, token, $"search={held.Value:D}");
            Assert.Equal(held.Value, Assert.Single(byDeliveryKey.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());

            // A state narrows the same lookup, and one the ledger does not know is refused.
            var heldOnly = await LookupAsync(client, token, $"search={marker}-&status=held");
            Assert.Equal(held.Value, Assert.Single(heldOnly.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            Assert.Equal(0, (await LookupAsync(client, token, $"search={marker}-&status=failed")).GetProperty("total").GetInt64());
            using var badStatus = await GetAsync(client, token, $"/api/v1/delivery/records?search={marker}&status=nonsense");
            Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

            // Paging walks the same hits.
            var pageTwo = await LookupAsync(client, token, $"search={marker}-&page=2&pageSize=2");
            Assert.Single(pageTwo.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(3, pageTwo.GetProperty("total").GetInt64());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var keys = new[] { delivered.Value, held.Value, elsewhere.Value };
            await osdu.DeliveryRecordIdentities.Where(i => keys.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => keys.Contains(a.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_lookup_narrows_as_a_flows_records_tab_does_and_matches_a_term_anywhere()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName + "-lookup-anywhere");
        var marker = "LA" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var plain = new DeliveryKey(Guid.NewGuid());
        var held = new DeliveryKey(Guid.NewGuid());
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId);

        try
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                Record(flowId, plain, $"NO 16/2-10 {marker}", $"dev:master-data--Wellbore:{marker}-A", marker + "_wellbores.csv", 1),
                Record(flowId, held, $"NO 25/8-4 {marker}", $"dev:master-data--Wellbore:{marker}-B", marker + "_wellbores.csv", 2) with { HeldSteps = "grade" },
            ]);

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var scope = $"flowId={flowId:D}";

            // No value of either record starts with a term from inside the well name; matching anywhere finds it, and the
            // hit names the value that holds it.
            Assert.Equal(0, (await LookupAsync(client, token, $"search=6/2-10&{scope}")).GetProperty("total").GetInt64());
            var anywhere = await LookupAsync(client, token, $"search=6/2-10&mode=contains&{scope}");
            var hit = Assert.Single(anywhere.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(plain.Value, hit.GetProperty("deliveryKey").GetGuid());
            Assert.Equal($"NO 16/2-10 {marker}", Assert.Single(hit.GetProperty("matched").EnumerateArray().ToList()).GetProperty("value").GetString());

            // The Records tab's switches narrow the lookup and the listing of what came in last alike.
            var stepHeld = await LookupAsync(client, token, $"search={marker}&stepHeld=true&{scope}");
            Assert.Equal(held.Value, Assert.Single(stepHeld.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            Assert.Equal(1, (await LookupAsync(client, token, $"stepHeld=true&{scope}")).GetProperty("total").GetInt64());
            Assert.Equal(1, (await LookupAsync(client, token, $"search=_wellbores&mode=contains&stepHeld=true&{scope}")).GetProperty("total").GetInt64());
            Assert.Equal(0, (await LookupAsync(client, token, $"drifted=true&{scope}")).GetProperty("total").GetInt64());
            Assert.Equal(2, (await LookupAsync(client, token, $"drifted=false&stepHeld=false&{scope}")).GetProperty("total").GetInt64());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var keys = new[] { plain.Value, held.Value };
            await osdu.DeliveryRecordIdentities.Where(i => keys.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task A_record_and_its_parent_each_link_to_the_other_on_their_pages()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        // A canonical log and a custom one of the same minted id, the custom one reading the canonical one as its parent
        // (osdu/docs/reference/flow/mapping-parent.md), each delivered by a flow of its own in one partition.
        var canonicalFlow = FlowId.Of(FlowName + "-parent-canonical");
        var customFlow = FlowId.Of(FlowName + "-parent-custom");
        var minted = Guid.NewGuid().ToString("N");
        var parentId = $"dev:work-product-component--WellLog:{minted}";
        var childId = $"dev:work-product-component--CustomLog:{minted}";
        var canonical = new DeliveryKey(Guid.NewGuid());
        var custom = new DeliveryKey(Guid.NewGuid());
        var plain = new DeliveryKey(Guid.NewGuid());
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(canonicalFlow, customFlow);

        try
        {
            await ledger.UpsertPendingAsync(canonicalFlow, [Record(canonicalFlow, canonical, minted + "-A", parentId, "logs.csv", 1)]);
            await ledger.UpsertPendingAsync(customFlow,
            [
                Record(customFlow, custom, minted + "-B", childId, "logs.csv", 1) with { ParentId = parentId, PendingParentVersion = 1761037100000007 },
                Record(customFlow, plain, minted + "-C", $"dev:work-product-component--CustomLog:{minted}-plain", "logs.csv", 2),
            ]);

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The custom record names its parent and the version its queued document was rendered from, and links to the
            // record of the ledger that holds the parent's id.
            var child = await DetailAsync(client, token, customFlow, custom);
            Assert.Equal(parentId, child.GetProperty("record").GetProperty("parentId").GetString());
            Assert.Equal(1761037100000007, child.GetProperty("record").GetProperty("pendingParentVersion").GetInt64());
            var parent = child.GetProperty("parent");
            Assert.Equal((canonicalFlow, canonical.Value, parentId), (parent.GetProperty("flowId").GetGuid(), parent.GetProperty("deliveryKey").GetGuid(), parent.GetProperty("targetId").GetString()));
            Assert.Empty(child.GetProperty("readBy").EnumerateArray());

            // The canonical record lists the records reading it as their parent, and only those.
            var read = await DetailAsync(client, token, canonicalFlow, canonical);
            var reader = Assert.Single(read.GetProperty("readBy").EnumerateArray().ToList());
            Assert.Equal((customFlow, custom.Value, childId), (reader.GetProperty("flowId").GetGuid(), reader.GetProperty("deliveryKey").GetGuid(), reader.GetProperty("targetId").GetString()));
            Assert.Equal(JsonValueKind.Null, read.GetProperty("parent").ValueKind);

            // A record of a linked template whose parent no flow of the partition delivers names it, and links nowhere.
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryRecords.Where(r => r.DeliveryKey == canonical.Value).ExecuteDeleteAsync();
            }

            child = await DetailAsync(client, token, customFlow, custom);
            Assert.Equal(parentId, child.GetProperty("record").GetProperty("parentId").GetString());
            Assert.Equal(JsonValueKind.Null, child.GetProperty("parent").ValueKind);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var keys = new[] { canonical.Value, custom.Value, plain.Value };
            await osdu.DeliveryRecordIdentities.Where(i => keys.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
        }
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, string token, Guid flowId, DeliveryKey key)
    {
        using var response = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{key.Value:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task With_nothing_to_look_for_the_records_the_system_last_took_in_are_listed_newest_first()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var marker = "RC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var keys = new[] { new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()) };
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId);

        try
        {
            // Staged now, so these are the records the ledger last touched: the listing has to show them first.
            await ledger.UpsertPendingAsync(flowId, keys
                .Select((key, i) => Record(flowId, key, $"{marker}-{i}", $"dev:master-data--Wellbore:{marker}-{i}", marker + "_wellbores.csv", i))
                .ToList());

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var latest = await LookupAsync(client, token, "pageSize=200");
            var items = latest.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            foreach (var key in keys)
            {
                Assert.Contains(items, h => h.GetProperty("deliveryKey").GetGuid() == key.Value);
            }

            // Newest first, and nothing was typed, so no row claims a value matched it.
            var times = items.Select(h => h.GetProperty("updatedUtc").GetDateTime()).ToList();
            Assert.Equal(times.OrderByDescending(t => t).ToList(), times);
            Assert.All(items, h => Assert.False(h.TryGetProperty("matched", out var matched) && matched.ValueKind != JsonValueKind.Null));

            // A blank term is nothing typed, not a term that matches nothing.
            var blank = await LookupAsync(client, token, "search=%20&pageSize=200");
            Assert.Equal(items.Count, blank.GetProperty("items").EnumerateArray().Count());

            // The same listing narrowed to one custody state, and a page past the recency bound is empty rather than a scan.
            var pending = await LookupAsync(client, token, "status=pending&pageSize=200");
            Assert.All(pending.GetProperty("items").EnumerateArray(), h => Assert.Equal("pending", h.GetProperty("status").GetString()));
            Assert.Contains(pending.GetProperty("items").EnumerateArray(), h => h.GetProperty("deliveryKey").GetGuid() == keys[0].Value);
            var past = await LookupAsync(client, token, $"page={(RecordListing.LookupCandidateLimit / 50) + 1}&pageSize=50");
            Assert.Empty(past.GetProperty("items").EnumerateArray());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var ids = keys.Select(k => k.Value).ToArray();
            await osdu.DeliveryRecordIdentities.Where(i => ids.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => ids.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_listing_and_the_lookup_narrow_to_one_flow_the_page_offers()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "FL" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var single = $"{marker}-wells";
        var source = $"{marker}-logs";
        var singleFlow = FlowId.Of(single);
        var headerFlow = FlowId.Of($"{source}/header");
        var unsynced = FlowId.Of($"{marker}-gone");
        var repoId = Guid.NewGuid();
        var singlePipeline = Guid.NewGuid();
        var sourcePipeline = Guid.NewGuid();
        var keys = new[] { new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()) };
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(singleFlow, headerFlow);

        try
        {
            await ledger.UpsertPendingAsync(singleFlow, [Record(singleFlow, keys[0], $"{marker}-W1", $"dev:master-data--Well:{marker}-W1", marker + "_wells.csv", 1)]);
            await ledger.UpsertPendingAsync(headerFlow,
            [
                Record(headerFlow, keys[1], $"{marker}-H1", $"dev:work-product-component--WellLog:{marker}-H1", marker + "_logs.csv", 1),
                Record(headerFlow, keys[2], $"{marker}-H2", $"dev:work-product-component--WellLog:{marker}-H2", marker + "_logs.csv", 2),
            ]);

            // What the repository sync records: a flow in the single form, a source of two interfaces, and a ledger
            // whose pipeline the catalog no longer holds.
            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryInterfaces.AddRange(
                    Interface(repoId, single, "", singleFlow),
                    Interface(repoId, source, "header", headerFlow),
                    Interface(repoId, source, "curves", FlowId.Of($"{source}/curves")),
                    Interface(repoId, $"{marker}-gone", "", unsynced));
                await osdu.SaveChangesAsync();
            }

            await using (var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs)))
            {
                catalog.Pipelines.AddRange(Pipeline(singlePipeline, repoId, single), Pipeline(sourcePipeline, repoId, source));
                await catalog.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The choices: one per ledger identity that holds records, named by pipeline and interface as a hit is. The
            // curves interface has delivered nothing, so narrowing to it could only show an empty page, and a ledger no
            // synced pipeline holds is no choice either.
            using var flowsResponse = await GetAsync(client, token, "/api/v1/delivery/records/flows");
            Assert.Equal(HttpStatusCode.OK, flowsResponse.StatusCode);
            using var flowsJson = JsonDocument.Parse(await flowsResponse.Content.ReadAsStringAsync());
            var ours = flowsJson.RootElement.EnumerateArray().Where(f => f.GetProperty("flowName").GetString()!.StartsWith(marker, StringComparison.Ordinal)).ToList();
            Assert.Equal(
                [(source, "header", headerFlow), (single, (string?)null, singleFlow)],
                ours.Select(f => (f.GetProperty("flowName").GetString()!, f.GetProperty("interface").GetString(), f.GetProperty("flowId").GetGuid())).ToList());
            Assert.Equal([sourcePipeline, singlePipeline], ours.Select(f => f.GetProperty("pipelineId").GetGuid()).ToList());
            Assert.DoesNotContain(flowsJson.RootElement.EnumerateArray(), f => f.GetProperty("flowId").GetGuid() == FlowId.Of($"{source}/curves"));
            Assert.DoesNotContain(flowsJson.RootElement.EnumerateArray(), f => f.GetProperty("flowId").GetGuid() == unsynced);

            // The recency listing of one flow is that flow's records alone.
            var header = await LookupAsync(client, token, $"flowId={headerFlow:D}&pageSize=200");
            Assert.Equal(2, header.GetProperty("total").GetInt64());
            Assert.All(header.GetProperty("items").EnumerateArray(), h => Assert.Equal(headerFlow, h.GetProperty("flowId").GetGuid()));
            Assert.All(header.GetProperty("items").EnumerateArray(), h => Assert.Equal("header", h.GetProperty("interface").GetString()));

            // A term matching both flows' records finds the chosen flow's only, and a status narrows it further.
            Assert.Equal(3, (await LookupAsync(client, token, $"search={marker}-")).GetProperty("total").GetInt64());
            var wells = await LookupAsync(client, token, $"search={marker}-&flowId={singleFlow:D}");
            Assert.Equal(keys[0].Value, Assert.Single(wells.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            Assert.Equal(0, (await LookupAsync(client, token, $"search={marker}-&flowId={singleFlow:D}&status=held")).GetProperty("total").GetInt64());
            Assert.Equal(0, (await LookupAsync(client, token, $"search={marker}-W&flowId={headerFlow:D}")).GetProperty("total").GetInt64());

            using var badFlow = await GetAsync(client, token, "/api/v1/delivery/records?flowId=not-a-flow");
            Assert.Equal(HttpStatusCode.BadRequest, badFlow.StatusCode);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var ids = keys.Select(k => k.Value).ToArray();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => ids.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => ids.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
            await using var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs));
            await catalog.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    private static DeliveryInterface Interface(Guid repoId, string flowName, string name, Guid ledgerFlowId) => new()
    {
        Id = Guid.NewGuid(),
        RepoId = repoId,
        FlowName = flowName,
        Interface = name,
        LedgerFlowId = ledgerFlowId,
        LedgerName = name.Length == 0 ? flowName : $"{flowName}/{name}",
        Route = "storage",
        MappingReference = SampleEstate.WellboreMapping,
        RelativePath = $"flows/{flowName}.yaml",
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
        Active = true,
    };

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = FlowDefinition.FlowTypeName,
        RelativePath = $"flows/{name}.yaml",
        Active = true,
    };

    /// <summary>
    /// The partition picked in the title bar is a filter on every read across flows
    /// (osdu/docs/reference/concepts/partitions.md, In the GUI and the API): every call carries it in
    /// <c>X-Osdu-Partition</c>, a partition the request names wins over it, and without either every partition is read,
    /// each hit naming its own. The Records page's lookup, the audit trail, the search box's records and the Partitions
    /// page all follow it.
    /// </summary>
    [Fact]
    public async Task The_workbench_partition_filters_every_read_across_flows()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "WP" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (first, second) = ("pa" + marker.ToLowerInvariant(), "pb" + marker.ToLowerInvariant());
        var (inFirst, inSecond) = (FlowId.Of($"{marker}-first"), FlowId.Of($"{marker}-second"));
        var (a, b) = (new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()));
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(first, inFirst);
        await ledger.RegisterAsync(second, inSecond);
        try
        {
            await ledger.UpsertPendingAsync(inFirst, [Record(inFirst, a, $"{marker}-A", $"{first}:master-data--Well:{marker}-A", marker + "_a.csv", 1)]);
            await ledger.UpsertPendingAsync(inSecond, [Record(inSecond, b, $"{marker}-B", $"{second}:master-data--Well:{marker}-B", marker + "_b.csv", 1)]);
            foreach (var flow in new[] { inFirst, inSecond })
            {
                await ledger.StartActivityAsync(new ActivityRecord { FlowId = flow, FlowName = marker, Kind = "release", Actor = "user:" + marker, StartedUtc = DateTime.UtcNow });
            }

            // A scheduled run that found nothing to do.
            var quiet = await ledger.StartActivityAsync(new ActivityRecord { FlowId = inSecond, FlowName = marker, Kind = "deliver", Actor = "schedule:" + marker, StartedUtc = DateTime.UtcNow });
            await ledger.CompleteActivityAsync(quiet.ActivityId, "completed", "nothing to deliver", null, DateTime.UtcNow, idle: true);

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // Without a partition, every partition's records, each naming its own.
            var every = await ReadAsync(client, token, $"/api/v1/delivery/records?search={marker}-", null);
            Assert.Equal(2, every.GetProperty("total").GetInt64());
            Assert.Equal(
                [first, second],
                every.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("partition").GetString()).Order(StringComparer.Ordinal));

            // The workbench's partition narrows them, and one the request names wins over it.
            var inWorkbench = await ReadAsync(client, token, $"/api/v1/delivery/records?search={marker}-", second);
            Assert.Equal(b.Value, Assert.Single(inWorkbench.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            var named = await ReadAsync(client, token, $"/api/v1/delivery/records?search={marker}-&partition={first}", second);
            Assert.Equal(a.Value, Assert.Single(named.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());

            // A flow of another partition than the workbench's reads empty.
            var elsewhere = await ReadAsync(client, token, $"/api/v1/delivery/records?flowId={inFirst}", second);
            Assert.Equal(0, elsewhere.GetProperty("total").GetInt64());

            // The audit trail across flows, and the search box's records, follow the same partition.
            var trail = await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=user:{marker}", first);
            var entry = Assert.Single(trail.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal((inFirst, first), (entry.GetProperty("flowId").GetGuid(), entry.GetProperty("partition").GetString()));
            Assert.Equal(2, (await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=user:{marker}", null)).GetProperty("total").GetInt64());

            // The idle run is on the trail and says so; idle=false leaves it out, and idle=true lists only such runs.
            var scheduled = Assert.Single((await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=schedule:{marker}", null)).GetProperty("items").EnumerateArray().ToList());
            Assert.True(scheduled.GetProperty("idle").GetBoolean());
            Assert.False(entry.GetProperty("idle").GetBoolean());
            Assert.Equal(0, (await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=schedule:{marker}&idle=false", null)).GetProperty("total").GetInt64());
            Assert.Equal(2, (await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=user:{marker}&idle=false", null)).GetProperty("total").GetInt64());
            Assert.Equal(1, (await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=schedule:{marker}&idle=true", second)).GetProperty("total").GetInt64());
            Assert.Equal(0, (await ReadAsync(client, token, $"/api/v1/delivery/activities?actor=schedule:{marker}&idle=true", first)).GetProperty("total").GetInt64());
            var searched = await ReadAsync(client, token, $"/api/v1/search/categories/records?q={marker}-", second);
            Assert.Equal(1, searched.GetProperty("total").GetInt64());

            // A partition only a ledger is kept under is listed, unregistered, with the ledgers it keeps.
            using var listing = await GetAsync(client, token, "/api/v1/delivery/partitions");
            using var partitions = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
            var listed = Assert.Single(partitions.RootElement.EnumerateArray().ToList(), p => p.GetProperty("name").GetString() == first);
            Assert.Equal((false, 1), (listed.GetProperty("registered").GetBoolean(), listed.GetProperty("ledgers").GetInt32()));

            // A workbench partition that is no data-partition-id is ignored rather than trusted.
            Assert.Equal(2, (await ReadAsync(client, token, $"/api/v1/delivery/records?search={marker}-", "not a partition")).GetProperty("total").GetInt64());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var flows = new[] { inFirst, inSecond };
            await osdu.DeliveryRecordIdentities.Where(i => flows.Contains(i.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => flows.Contains(r.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(r => flows.Contains(r.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => flows.Contains(l.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryLedgerPartitions.Where(p => p.Name == first || p.Name == second).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The audit trail narrowed to one flow, as its page's flow filter reads it: the choices are the ledgers of the workbench
    /// partition with activity on the trail, delivery flows, interfaces of a source and dimensions alike, named by the
    /// ledger's directory; a choice's ledger identity narrows the trail to that flow, every other filter applies within it,
    /// and a flow kept in another partition reads empty. Naming a flow by its pipeline and its ledger at once is refused.
    /// </summary>
    [Fact]
    public async Task The_audit_trail_narrows_to_a_flow_it_offers()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "AF" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (home, away) = ("ph" + marker.ToLowerInvariant(), "pq" + marker.ToLowerInvariant());
        var (wells, curves, dimension, quiet, elsewhere) = (
            FlowId.Of($"{marker}-wells"), FlowId.Of($"{marker}-logs/curves"), FlowId.Of($"{marker}-dimension"), FlowId.Of($"{marker}-quiet"), FlowId.Of($"{marker}-elsewhere"));
        var flows = new[] { wells, curves, dimension, quiet, elsewhere };
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(wells, home, $"{marker}-wells");
        await ledger.RegisterLedgerAsync(new LedgerEntry { FlowId = curves, Partition = home, Kind = LedgerKinds.Delivery, FlowName = $"{marker}-logs", Interface = "curves", LedgerName = $"{marker}-logs/curves" });
        await ledger.RegisterLedgerAsync(new LedgerEntry { FlowId = dimension, Partition = home, Kind = LedgerKinds.Dimension, FlowName = $"{marker}-dimension", LedgerName = $"{marker}-dimension" });
        await ledger.RegisterAsync(quiet, home, $"{marker}-quiet");
        await ledger.RegisterAsync(elsewhere, away, $"{marker}-elsewhere");
        try
        {
            foreach (var (flow, name, kind, actor) in new[]
            {
                (wells, $"{marker}-wells", "deliver", "schedule:" + marker),
                (wells, $"{marker}-wells", "release", "user:" + marker),
                (curves, $"{marker}-logs/curves", "deliver", "schedule:" + marker),
                (dimension, $"{marker}-dimension", DimensionRemoval.ActivityKind, "user:" + marker),
                (elsewhere, $"{marker}-elsewhere", "deliver", "schedule:" + marker),
            })
            {
                await ledger.StartActivityAsync(new ActivityRecord { FlowId = flow, FlowName = name, Kind = kind, Actor = actor, StartedUtc = DateTime.UtcNow });
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The choices of the workbench partition: each ledger with activity, by flow, then interface. The quiet flow has
            // none, and the flow of the other partition is that partition's choice.
            var offered = await ReadArrayAsync(client, token, "/api/v1/delivery/activities/flows", home);
            Assert.Equal(
                [(dimension, $"{marker}-dimension", LedgerKinds.Dimension, (string?)null), (curves, $"{marker}-logs", LedgerKinds.Delivery, "curves"), (wells, $"{marker}-wells", LedgerKinds.Delivery, null)],
                offered.Select(f => (f.GetProperty("flowId").GetGuid(), f.GetProperty("flowName").GetString()!, f.GetProperty("kind").GetString()!, f.GetProperty("interface").GetString())).ToList());
            Assert.All(offered, f => Assert.Equal(home, f.GetProperty("partition").GetString()));
            var everyPartition = await ReadArrayAsync(client, token, "/api/v1/delivery/activities/flows", null);
            Assert.Contains(everyPartition, f => f.GetProperty("flowId").GetGuid() == elsewhere && f.GetProperty("partition").GetString() == away);
            Assert.DoesNotContain(everyPartition, f => f.GetProperty("flowId").GetGuid() == quiet);

            // One flow's trail is its entries alone, and the other filters narrow it further.
            var trail = await ReadAsync(client, token, $"/api/v1/delivery/activities?flowId={wells:D}", home);
            Assert.Equal(2, trail.GetProperty("total").GetInt64());
            Assert.All(trail.GetProperty("items").EnumerateArray(), a => Assert.Equal(wells, a.GetProperty("flowId").GetGuid()));
            var released = await ReadAsync(client, token, $"/api/v1/delivery/activities?flowId={wells:D}&actor=user:{marker}", home);
            Assert.Equal("release", Assert.Single(released.GetProperty("items").EnumerateArray().ToList()).GetProperty("kind").GetString());
            Assert.Equal(0, (await ReadAsync(client, token, $"/api/v1/delivery/activities?flowId={wells:D}&kind=delete", home)).GetProperty("total").GetInt64());
            Assert.Equal(1, (await ReadAsync(client, token, $"/api/v1/delivery/activities?flowId={dimension:D}", home)).GetProperty("total").GetInt64());

            // A flow kept in another partition than the workbench's reads empty; without a workbench partition it reads whole.
            Assert.Equal(0, (await ReadAsync(client, token, $"/api/v1/delivery/activities?flowId={elsewhere:D}", home)).GetProperty("total").GetInt64());
            Assert.Equal(1, (await ReadAsync(client, token, $"/api/v1/delivery/activities?flowId={elsewhere:D}", null)).GetProperty("total").GetInt64());

            using var both = await GetAsync(client, token, $"/api/v1/delivery/activities?flowId={wells:D}&pipelineId={Guid.NewGuid():D}");
            Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
            using var badFlow = await GetAsync(client, token, "/api/v1/delivery/activities?flowId=not-a-flow");
            Assert.Equal(HttpStatusCode.BadRequest, badFlow.StatusCode);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryActivities.Where(a => flows.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => flows.Contains(l.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryLedgerPartitions.Where(p => p.Name == home || p.Name == away).ExecuteDeleteAsync();
        }
    }

    /// <summary>A read of a JSON array at <paramref name="path"/> in the workbench partition <paramref name="partition"/>, or in none.</summary>
    private static async Task<List<JsonElement>> ReadArrayAsync(HttpClient client, string token, string path, string? partition)
    {
        var array = await ReadAsync(client, token, path, partition);
        return array.EnumerateArray().ToList();
    }

    /// <summary>A read of <paramref name="path"/> in the workbench partition <paramref name="partition"/>, or in none.</summary>
    private static async Task<JsonElement> ReadAsync(HttpClient client, string token, string path, string? partition)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (partition is not null)
        {
            request.Headers.TryAddWithoutValidation(WorkbenchPartition.Header, partition);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static RecordState Record(Guid flowId, DeliveryKey key, string sourceKey, string targetId, string file, long row) => new()
    {
        DeliveryKey = key,
        FlowId = flowId,
        SourceKey = sourceKey,
        Label = sourceKey,
        MappingName = SampleEstate.WellboreMapping,
        Status = RecordStatus.Pending,
        TargetId = targetId,
        SourceKeyJson = $$"""{"facility_name":"{{sourceKey}}"}""",
        SourceFileName = file,
        SourceRowNumber = row,
        PendingSourceFileName = file,
        PendingSourceRowNumber = row,
        PendingDocumentRef = "1:0:10",
        PendingMetadata = true,
    };

    private static async Task<JsonElement> LookupAsync(HttpClient client, string token, string query)
    {
        using var response = await GetAsync(client, token, "/api/v1/delivery/records?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
