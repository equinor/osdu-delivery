using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Flows that work in partitions as the control plane serves them (osdu/docs/reference/concepts/partitions.md): the
/// dispatcher gives a run every partition's configuration and a node task its own partition's, a flow is read one partition
/// at a time and says so when a request settles none, a record says the partition its ledger delivers to and the task queued
/// for it acts there, the cache listing names the partitions a cache flow builds, and the partition registry is kept
/// through the API and settles the partition of a flow that leaves it open.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryPartitionsApiTests
{
    /// <summary>The dispatcher the configured one decorates, keeping what it was asked to queue.</summary>
    private static Dictionary<string, string> Properties(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private sealed class RecordingDispatcher : IRunDispatcher
    {
        public List<RunEnqueueRequest> Runs { get; } = [];

        public List<ComputeTaskEnqueueRequest> Tasks { get; } = [];

        public Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
        {
            Runs.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
        {
            Tasks.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests queue single runs and node tasks only.");

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests cancel nothing.");

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests cancel nothing.");

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests cancel nothing.");
    }

    [Fact]
    public async Task The_dispatcher_gives_a_run_every_partition_s_configuration_and_a_node_task_its_own_partition_s()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var repoId = Guid.NewGuid();
        var store = new DeliveryConfigStore(() => SampleEstate.Context(cs));
        var now = DateTime.UtcNow;

        // A property is set for a repository the catalog holds.
        await using var catalog = CatalogDatabase.Create(cs);
        catalog.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-dispatch-" + repoId.ToString("N")[..10], FirstSeenUtc = now, LastSyncUtc = now });
        await catalog.SaveChangesAsync();

        try
        {
            await store.SetAsync(repoId, null, "OSDU_LEGAL_TAG", "estate-legal", null, "tests", now, catalog);
            await store.SetAsync(repoId, null, "OSDU_URL", "https://osdu.example.com", null, "tests", now, catalog);
            await store.SetAsync(repoId, "test", "OSDU_LEGAL_TAG", "estate-test-legal", null, "tests", now, catalog);

            var inner = new RecordingDispatcher();
            var dispatcher = new ConfiguredRunDispatcher(inner, store);

            // The values set for no partition reach every run of every kind through the control plane's own reference source.
            var everyRun = await new CentralConfigurationReferences(store).ReferencesAsync(repoId, CancellationToken.None);
            Assert.Equal(("estate-legal", "https://osdu.example.com"), (everyRun["OSDU_LEGAL_TAG"], everyRun["OSDU_URL"]));

            // A delivery run and a cache run carry each partition's own besides; the partition a run binds to resolves with its
            // own over the values every run carries.
            await dispatcher.EnqueueAsync(null!, new RunEnqueueRequest(repoId, "welllogs", "delivery"));
            await dispatcher.EnqueueAsync(null!, new RunEnqueueRequest(repoId, "lookups", "cache"));
            foreach (var run in inner.Runs)
            {
                var payload = DeliveryRunPayload.Parse(run.Parameters!.Payload);
                Assert.Empty(payload.References);
                Assert.Equal(["test"], payload.PartitionReferences.Keys);
                Assert.Equal(Properties(("OSDU_LEGAL_TAG", "estate-test-legal")), payload.ReferencesFor("test"));
                Assert.Empty(payload.ReferencesFor("dev"));
            }

            // A run whose payload already names properties keeps them, and a run of SQLFlow's own kinds is given nothing.
            var composed = new DeliveryRunPayload { References = new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_URL"] = "https://composed.example.com" } }.ToJson();
            await dispatcher.EnqueueAsync(null!, new RunEnqueueRequest(repoId, "welllogs", "delivery", Parameters: new RunParameters { Payload = composed }));
            Assert.Equal(composed, inner.Runs[^1].Parameters!.Payload);
            await dispatcher.EnqueueAsync(null!, new RunEnqueueRequest(repoId, "landing", "pre"));
            Assert.Null(inner.Runs[^1].Parameters);

            // A node task names its repository and partition, and carries that partition's values alone.
            var task = new ComputeTaskPayload
            {
                Operation = CheckValuesOperation.OperationName,
                SourceRef = "welllogs",
                Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConfiguredRunDispatcher.RepoArgument] = repoId.ToString("D"),
                    [DeliveryOperation.PartitionArgument] = "test",
                },
            };
            await dispatcher.EnqueueComputeTaskAsync(null!, new ComputeTaskEnqueueRequest(task.Operation, "welllogs", "delivery", task.ToJson()));
            var queued = ComputeTaskPayload.FromJson(inner.Tasks.Single().ArgumentsJson, [task.Operation]);
            Assert.Equal(Properties(("OSDU_LEGAL_TAG", "estate-test-legal"), ("OSDU_URL", "https://osdu.example.com")), queued.References);
            Assert.Equal("test", queued.Argument(DeliveryOperation.PartitionArgument));
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryConfigProperties.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await catalog.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task A_flow_naming_its_partitions_is_read_one_partition_at_a_time_and_its_records_act_where_they_were_delivered()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-partitions-" + suffix;
        var cacheFlowName = "api-partitions-cache-" + suffix;
        var (dev, test) = ("d" + suffix, "t" + suffix);
        var repoId = FlowIdentity.FromName("repo/cp-partitions-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var devLedger = FlowId.Of(flowName);
        var testLedger = FlowId.Of(flowName, test);
        var (devKey, testKey) = (new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()));
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            partitions:
              - name: {{dev}}
                keepLedger: true
              - {{test}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.WellLog, key: [log_id] }
              lastModified: update_date
              work: ../.work/partitions
            render:
              mapping: WellLog@1.4.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-partitions-" + suffix, RemoteUrl = "https://example/cp-partitions.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId,
                RepoId = repoId,
                Name = flowName,
                Kind = "delivery",
                RelativePath = "flows/" + flowName + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var osdu = SampleEstate.Context(cs))
        {
            // What a sync of a cache flow naming both partitions records: its type once per partition.
            foreach (var scope in new[] { dev, test })
            {
                osdu.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
                {
                    Id = FlowIdentity.FromName($"delivery-cache/{repoId:N}/{cacheFlowName}/CurveDictionary@{scope}"),
                    RepoId = repoId,
                    FlowName = cacheFlowName,
                    Scope = scope,
                    DeclaresPartitions = true,
                    Origin = "table",
                    Connection = "${env:OSDU_DATA_DB}",
                    SourceObject = "OsduData.arc.CurveDictionary",
                    KeyField = "mnemonic",
                    RelativePath = "cache/" + cacheFlowName + ".yaml",
                    Name = "CurveDictionary",
                    EntityType = "lookup--CurveDictionary",
                    FieldsJson = "[]",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
            }

            osdu.DeliveryConfigProperties.Add(new DeliveryConfigProperty
            {
                RepoId = repoId, Partition = test, Name = "OSDU_LEGAL_TAG", Value = "estate-test-legal", UpdatedUtc = now, UpdatedBy = "tests",
            });
            await osdu.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(devLedger, dev, flowName);
        await ledger.RegisterAsync(testLedger, test, flowName, $"{flowName}@{test}");
        foreach (var (flowId, key) in new[] { (devLedger, devKey), (testLedger, testKey) })
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                new RecordState
                {
                    DeliveryKey = key,
                    FlowId = flowId,
                    SourceKey = "L-1001",
                    Label = "L-1001",
                    MappingName = "WellLog",
                    Status = RecordStatus.Pending,
                    PendingDocumentRef = "1:0:10",
                    PendingMetadata = true,
                },
            ]);
        }

        var operations = new RecordedOperations();
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client, ["read", "operate"]);

            // The flow was synced before its interfaces were described; the control plane describes it in both partitions.
            await WaitForInterfacesAsync(cs, repoId, 2);

            // The listing of a flow's interfaces says which partitions it delivers to: every interface in every partition, in the
            // order the flow names them, each with its own ledger; or those of the one partition a request names.
            var everywhere = (await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces")).EnumerateArray().ToList();
            Assert.Equal(
                [(devLedger, flowName, dev), (testLedger, flowName + "@" + test, test)],
                everywhere.Select(i => (i.GetProperty("flowId").GetGuid(), i.GetProperty("ledger").GetString(), i.GetProperty("partition").GetString())));
            var inTest = Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces?partition={test}")).EnumerateArray().ToList());
            Assert.Equal((testLedger, flowName + "@" + test, test), (inTest.GetProperty("flowId").GetGuid(), inTest.GetProperty("ledger").GetString(), inTest.GetProperty("partition").GetString()));
            Assert.Equal(1, inTest.GetProperty("stats").GetProperty("pending").GetInt64());

            // Records are read in one partition, and a request that names none, or one the flow does not name, is told so.
            using (var nameless = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records"))
            {
                var body = await nameless.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
                Assert.Contains("Partition required", body, StringComparison.Ordinal);
                Assert.Contains("Name it with ?partition=.", body, StringComparison.Ordinal);
            }

            using (var unknown = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/stats?partition=prod"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
                Assert.Contains("No such partition", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // The partition that keeps the ledger reads the one the flow kept before it named its partitions; the flow as a
            // whole adds every partition's ledger up and says which partitions it covers.
            var inDev = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/stats?partition={dev}");
            Assert.Equal((devLedger, dev, 1L), (inDev.GetProperty("flowId").GetGuid(), inDev.GetProperty("partition").GetString(), inDev.GetProperty("pending").GetInt64()));
            var whole = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/stats");
            Assert.Equal((Guid.Empty, 2L, 1), (whole.GetProperty("flowId").GetGuid(), whole.GetProperty("pending").GetInt64(), whole.GetProperty("interfaces").GetInt32()));
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("partition").ValueKind);
            Assert.Equal([dev, test], whole.GetProperty("partitions").EnumerateArray().Select(p => p.GetString()));

            var records = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records?partition={test}");
            Assert.Equal(testKey.Value, Assert.Single(records.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());

            // A record says where it was delivered, and a read of it acts there, run by the control plane with that partition's
            // configuration exactly as a node would be given it.
            var record = await JsonAsync(client, token, $"/api/v1/delivery/records/{testLedger:D}/{testKey.Value:D}");
            Assert.Equal((pipelineId, test), (record.GetProperty("pipelineId").GetGuid(), record.GetProperty("partition").GetString()));
            using (var read = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{testLedger:D}/{testKey.Value:D}/read"))
            {
                Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
                var ran = operations.Last();
                Assert.Equal(ReadRecordOperation.OperationName, ran.Operation);
                Assert.Equal(test, ran.Argument(DeliveryOperation.PartitionArgument));
                Assert.Equal("estate-test-legal", ran.References!["OSDU_LEGAL_TAG"]);
                await using var db = CatalogDatabase.Create(cs);
                Assert.False(await db.ComputeTasks.AnyAsync(t => t.SourceRef == flowName));
            }

            var devRecord = await JsonAsync(client, token, $"/api/v1/delivery/records/{devLedger:D}/{devKey.Value:D}");
            Assert.Equal(dev, devRecord.GetProperty("partition").GetString());

            // The cache listing names the partitions the cache flow builds, under each partition's cache.
            var caches = (await JsonAsync(client, token, $"/api/v1/delivery/caches?repoId={repoId:D}")).EnumerateArray().ToList();
            foreach (var scope in new[] { dev, test })
            {
                var cache = Assert.Single(caches, c => c.GetProperty("scope").GetString() == scope);
                var flow = Assert.Single(cache.GetProperty("flows").EnumerateArray().ToList(), f => f.GetProperty("name").GetString() == cacheFlowName);
                Assert.Equal(new[] { dev, test }.Order(StringComparer.Ordinal), flow.GetProperty("partitions").EnumerateArray().Select(p => p.GetString()));
            }

            // The partitions the catalog knows are what the title bar's switcher offers: each with the cache flows that fill it
            // and the delivery flows that name it; neither holds a cache version yet.
            var known = (await JsonAsync(client, token, "/api/v1/delivery/partitions")).EnumerateArray().ToList();
            foreach (var scope in new[] { dev, test })
            {
                var partition = Assert.Single(known, p => p.GetProperty("name").GetString() == scope);
                Assert.Equal([cacheFlowName], partition.GetProperty("cacheFlows").EnumerateArray().Select(f => f.GetString()));
                Assert.Equal([flowName], partition.GetProperty("deliveryFlows").EnumerateArray().Select(f => f.GetString()));
                Assert.Equal(JsonValueKind.Null, partition.GetProperty("currentVersion").ValueKind);
                Assert.Equal(0, partition.GetProperty("pendingChanges").GetInt64());
            }

            Assert.Equal(known.Select(p => p.GetProperty("name").GetString()).Order(StringComparer.OrdinalIgnoreCase), known.Select(p => p.GetProperty("name").GetString()));

            // The configuration is set and read per partition; a partition written as a reference names none.
            var admin = await TokenAsync(client, ["admin"]);
            using (var set = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/OSDU_URL?repoId={repoId:D}&partition={test}", new DeliveryConfigSetRequest("https://test.osdu.example.com", null)))
            {
                Assert.True(set.StatusCode == HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
                Assert.Equal(test, JsonDocument.Parse(await set.Content.ReadAsStringAsync()).RootElement.GetProperty("partition").GetString());
            }

            var effective = await JsonAsync(client, token, $"/api/v1/delivery/config/effective/{repoId:D}?partition={test}");
            Assert.Equal(("https://test.osdu.example.com", "estate-test-legal"), (effective.GetProperty("OSDU_URL").GetString(), effective.GetProperty("OSDU_LEGAL_TAG").GetString()));
            var none = await JsonAsync(client, token, $"/api/v1/delivery/config/effective/{repoId:D}");
            Assert.False(none.TryGetProperty("OSDU_URL", out _));
            using (var reference = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/config/effective/{repoId:D}?partition={Uri.EscapeDataString("${env:PART}")}"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, reference.StatusCode);
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryRecords.Where(r => r.FlowId == devLedger || r.FlowId == testLedger).ExecuteDeleteAsync();
                await osdu.DeliveryLedgers.Where(l => l.FlowId == devLedger || l.FlowId == testLedger).ExecuteDeleteAsync();
                await osdu.DeliveryLedgerPartitions.Where(p => p.Name == dev || p.Name == test).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryConfigProperties.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    [Fact]
    public async Task The_registry_is_kept_through_the_api_and_settles_the_partition_of_a_flow_that_names_none()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-registry-" + suffix;
        var (dev, test, prod, unregistered) = ("d" + suffix, "t" + suffix, "p" + suffix, "u" + suffix);
        var repoId = FlowIdentity.FromName("repo/cp-registry-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var devLedger = FlowId.Of(flowName);
        var testLedger = FlowId.Of(flowName, test);
        var (devKey, testKey) = (new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()));
        var now = DateTime.UtcNow;

        // A flow that names neither partitions nor a header serves every registered partition; the dev partition keeps the
        // ledger it kept before.
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            keepLedger: {{dev}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.WellLog, key: [log_id] }
              lastModified: update_date
              work: ../.work/registry
            render:
              mapping: WellLog@1.4.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
            """;

        // The suites run one test at a time, so the registry is this test's while it runs.
        var registry = new DeliveryPartitionRegistry(() => SampleEstate.Context(cs));
        await using (var osdu = SampleEstate.Context(cs))
        {
            await osdu.DeliveryPartitions.ExecuteDeleteAsync();
            osdu.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = FlowIdentity.FromName($"delivery-cache/{repoId:N}/lookups/CurveDictionary@{unregistered}"),
                RepoId = repoId,
                FlowName = "lookups-" + suffix,
                Scope = unregistered,
                DeclaresPartitions = true,
                Origin = "table",
                Connection = "${env:OSDU_DATA_DB}",
                SourceObject = "OsduData.arc.CurveDictionary",
                KeyField = "mnemonic",
                RelativePath = "cache/lookups.yaml",
                Name = "CurveDictionary",
                EntityType = "lookup--CurveDictionary",
                FieldsJson = "[]",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await osdu.SaveChangesAsync();
        }

        await registry.AddAsync(dev, "Development", makeDefault: false, "tests", now);
        await registry.AddAsync(test, null, makeDefault: false, "tests", now);

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-registry-" + suffix, RemoteUrl = "https://example/cp-registry.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId,
                RepoId = repoId,
                Name = flowName,
                Kind = "delivery",
                RelativePath = "flows/" + flowName + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(devLedger, dev, flowName);
        await ledger.RegisterAsync(testLedger, test, flowName, $"{flowName}@{test}");
        foreach (var (flowId, key) in new[] { (devLedger, devKey), (testLedger, testKey) })
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                new RecordState
                {
                    DeliveryKey = key,
                    FlowId = flowId,
                    SourceKey = "L-2001",
                    Label = "L-2001",
                    MappingName = "WellLog",
                    Status = RecordStatus.Pending,
                    PendingDocumentRef = "1:0:10",
                    PendingMetadata = true,
                },
            ]);
        }

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client, ["read", "operate"]);
            var admin = await TokenAsync(client, ["admin"]);

            // Described in every registered partition, each with its own ledger.
            await WaitForInterfacesAsync(cs, repoId, 2);
            var everywhere = (await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces")).EnumerateArray().ToList();
            Assert.Equal(
                [(devLedger, dev), (testLedger, test)],
                everywhere.Select(i => (i.GetProperty("flowId").GetGuid(), i.GetProperty("partition").GetString())));

            // A request that names no partition is read in the default; one naming a partition the registry does not hold is refused.
            var inDefault = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records");
            Assert.Equal(devKey.Value, Assert.Single(inDefault.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            using (var outside = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?partition={prod}"))
            {
                var body = await outside.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
                Assert.Contains("No such partition", body, StringComparison.Ordinal);
                Assert.Contains($"'{prod}' is not registered", body, StringComparison.Ordinal);
            }

            // The registry is admin work to keep.
            using (var refused = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/partitions", new DeliveryPartitionAddRequest(prod, null, null)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            using (var added = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/partitions", new DeliveryPartitionAddRequest(prod, "Production", null)))
            {
                Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
                var row = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal((prod, "Production", false, true), (row.GetProperty("name").GetString(), row.GetProperty("description").GetString(), row.GetProperty("isDefault").GetBoolean(), row.GetProperty("registered").GetBoolean()));
            }

            using (var twice = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/partitions", new DeliveryPartitionAddRequest(prod.ToUpperInvariant(), null, null)))
            {
                Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
            }

            using (var reference = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/partitions", new DeliveryPartitionAddRequest("${env:PART}", null, null)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, reference.StatusCode);
            }

            // The listing marks the default, and lists a partition something is kept under while the registry does not hold it.
            var known = (await JsonAsync(client, token, "/api/v1/delivery/partitions")).EnumerateArray().ToList();
            Assert.Equal([dev, prod, test, unregistered], known.Select(p => p.GetProperty("name").GetString()).Where(n => n!.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            var devRow = Assert.Single(known, p => p.GetProperty("name").GetString() == dev);
            Assert.Equal((true, true, "Development", "tests"), (devRow.GetProperty("isDefault").GetBoolean(), devRow.GetProperty("registered").GetBoolean(), devRow.GetProperty("description").GetString(), devRow.GetProperty("createdBy").GetString()));
            Assert.Equal([flowName], devRow.GetProperty("deliveryFlows").EnumerateArray().Select(f => f.GetString()));
            var unregisteredRow = Assert.Single(known, p => p.GetProperty("name").GetString() == unregistered);
            Assert.Equal((false, false), (unregisteredRow.GetProperty("registered").GetBoolean(), unregisteredRow.GetProperty("isDefault").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, unregisteredRow.GetProperty("createdUtc").ValueKind);

            using (var described = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/partitions/{test}", new DeliveryPartitionDescribeRequest("Test platform")))
            {
                Assert.True(described.StatusCode == HttpStatusCode.OK, await described.Content.ReadAsStringAsync());
                Assert.Equal("Test platform", JsonDocument.Parse(await described.Content.ReadAsStringAsync()).RootElement.GetProperty("description").GetString());
            }

            using (var missing = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/partitions/{unregistered}", new DeliveryPartitionDescribeRequest("x")))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            // Another default moves what a request that names no partition reads.
            using (var moved = await SendAsync(client, admin, HttpMethod.Post, $"/api/v1/delivery/partitions/{test}/default"))
            {
                Assert.True(moved.StatusCode == HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
                Assert.True(JsonDocument.Parse(await moved.Content.ReadAsStringAsync()).RootElement.GetProperty("isDefault").GetBoolean());
            }

            var inTest = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records");
            Assert.Equal(testKey.Value, Assert.Single(inTest.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());

            // The default is not removed while others are registered; another partition is, and keeps what it held.
            using (var keepDefault = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/partitions/{test}"))
            {
                Assert.Equal(HttpStatusCode.Conflict, keepDefault.StatusCode);
            }

            using (var removed = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/partitions/{dev}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }

            using (var gone = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/partitions/{dev}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            }

            // A partition taken out of the registry is no longer served: nothing is read from its target or acted on in it. What
            // its ledger kept is still read, where the ledger's directory says it is.
            using (var notServed = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/target?partition={dev}"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, notServed.StatusCode);
            }

            var kept = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records?partition={dev}");
            Assert.Equal(devKey.Value, Assert.Single(kept.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            using (var nowhere = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?partition={unregistered}"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nowhere.StatusCode);
            }

            // A record of a partition taken out of the registry is still read where its ledger says it was delivered.
            var record = await JsonAsync(client, token, $"/api/v1/delivery/records/{devLedger:D}/{devKey.Value:D}");
            Assert.Equal((pipelineId, dev), (record.GetProperty("pipelineId").GetGuid(), record.GetProperty("partition").GetString()));
            var devStill = Assert.Single((await JsonAsync(client, token, "/api/v1/delivery/partitions")).EnumerateArray().ToList(), p => p.GetProperty("name").GetString() == dev);
            Assert.False(devStill.GetProperty("registered").GetBoolean());
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryRecords.Where(r => r.FlowId == devLedger || r.FlowId == testLedger).ExecuteDeleteAsync();
                await osdu.DeliveryLedgers.Where(l => l.FlowId == devLedger || l.FlowId == testLedger).ExecuteDeleteAsync();
                await osdu.DeliveryLedgerPartitions.Where(p => p.Name == dev || p.Name == test).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryPartitions.ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    /// <summary>
    /// A partition registered makes every repository sync again to describe its registry-driven flows in it, and that sync
    /// recomputes, it never moves: a repository whose automatic sync is off is synced at the commit it holds, so registering
    /// a partition never brings a merge into an environment that takes merges only when a trigger reports them.
    /// </summary>
    [Fact]
    public async Task Registering_a_partition_resyncs_a_repository_that_syncs_only_on_request_at_the_commit_it_holds()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partition = "r" + suffix;
        var sourceName = "cp-held-" + suffix;
        var sourceId = FlowIdentity.FromName($"reposource/{sourceName}");
        var held = new string('a', 40);
        var now = DateTime.UtcNow;

        await using (var db = CatalogDatabase.Create(cs))
        {
            await RepoSourceStore.UpsertAsync(db, sourceName, "https://example/" + sourceName + ".git", "main", enabled: true, syncIntervalSeconds: 300, now);
            Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, sourceId, now, now.AddMinutes(5), now));
            await RepoSourceStore.RecordSuccessAsync(db, sourceId, held, now);
            Assert.Equal(AutoSyncChange.Applied, (await RepoSourceStore.SetAutoSyncAsync(db, sourceId, false, "tests", now)).Outcome);
        }

        try
        {
            // The managed sync is off, so the request the registration makes stays on the row to be read.
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("ControlPlane:ManagedSync:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var admin = await TokenAsync(client, ["admin"]);

            using (var added = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/partitions", new DeliveryPartitionAddRequest(partition, null, null)))
            {
                Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
            }

            await using var check = CatalogDatabase.Create(cs);
            var source = await check.RepoSources.AsNoTracking().SingleAsync(s => s.Id == sourceId);
            Assert.False(source.AutoSync);
            Assert.NotNull(source.NextSyncUtc);
            Assert.True(source.ForceLineageOnNextSync);
            Assert.Null(source.MoveRequestedUtc);
            Assert.Equal(held, source.LastSyncedSha);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
            }

            await using var db = CatalogDatabase.Create(cs);
            await db.ActivityEvents.Where(e => e.SubjectKey == sourceId.ToString()).ExecuteDeleteAsync();
            await db.RepoSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
        }
    }

    private static async Task WaitForInterfacesAsync(string cs, Guid repoId, int expected)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await using var osdu = SampleEstate.Context(cs);
            if (await osdu.DeliveryInterfaces.CountAsync(i => i.RepoId == repoId) >= expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The control plane did not describe the {expected} interface(s) of repository {repoId:D} within 30 seconds of starting.");
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes.ToArray()));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
