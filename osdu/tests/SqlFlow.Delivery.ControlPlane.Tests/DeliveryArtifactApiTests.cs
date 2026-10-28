using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What deliveries created in OSDU, as the API serves it (osdu/docs/reference/concepts/api.md, Flows and records): a
/// record's artifacts, newest first, with where each stands and who settled it; the undo attempts and the tries that
/// waited for an undo, carried on the record's attempts as the ledger wrote them; and a flow's open undos, counted for
/// the source and for each interface, with the records holding them listed one ledger at a time, those whose undo has
/// used its tries first. Every artifact is written through the ledger as a worker and an undo write it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryArtifactApiTests
{
    private const string Failure = "HTTP 503 Service Unavailable from POST /api/storage/v2/records/delete";

    [Fact]
    public async Task A_record_s_artifacts_and_undo_attempts_are_served_and_the_flow_lists_its_open_undos()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-artifacts-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-artifacts-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var flowId = FlowId.Of(flowName);
        await SeedPipelineAsync(cs, repoId, pipelineId, flowName, SingleForm(flowName));

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var aborted = Key(suffix, "aborted");
        var due = Key(suffix, "due");
        var delivered = Key(suffix, "delivered");
        await ledger.UpsertPendingAsync(flowId, [Pending(flowId, aborted, "WB-ABORTED"), Pending(flowId, due, "WB-DUE"), Pending(flowId, delivered, "WB-DELIVERED")]);

        try
        {
            // A delivery that registered a dataset, wrote the record and opened a session, and stopped there.
            var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-30));
            await StepAsync(ledger, flowId, aborted, unit,
                TargetArtifact.Created("dataset:landing/a", ArtifactRoles.Dataset, "dev:dataset--File.Generic:A1", locator: "landing/a"),
                TargetArtifact.RecordWritten("dev:master-data--Wellbore:WB-ABORTED", 7, null),
                TargetArtifact.Created("session", ArtifactRoles.Session, "session-a", locator: "dev:master-data--Wellbore:WB-ABORTED"));
            var written = (await ledger.RecordArtifactsAsync(flowId, aborted, 10)).ToDictionary(a => a.Role);

            // Its undo took back the session and the dataset; OSDU refused the record's removal every time the sweep tried it.
            for (var attempt = 1; attempt <= ArtifactLimits.MaxUndoAttempts; attempt++)
            {
                var settlements = new List<ArtifactSettlement>
                {
                    new(written[ArtifactRoles.Record].ArtifactId, ArtifactStatus.Failed, Failure,
                        attempt < ArtifactLimits.MaxUndoAttempts ? DateTime.UtcNow.AddMinutes(attempt) : null),
                };
                if (attempt == 1)
                {
                    settlements.Add(new(written[ArtifactRoles.Session].ArtifactId, ArtifactStatus.Removed, "abandoned", null));
                    settlements.Add(new(written[ArtifactRoles.Dataset].ArtifactId, ArtifactStatus.Removed, null, null));
                }

                await ledger.SettleArtifactsAsync(flowId, [new RecordUndo(aborted, UndoAttempt(aborted, unit, attempt == 1), settlements, null, "sweep-test")]);
            }

            // Newer work for the record waited for the undo until it had used its tries, and was held.
            await CompleteAsync(ledger, flowId, new RecordCompletion
            {
                DeliveryKey = aborted,
                Status = RecordStatus.Held,
                Error = "an earlier delivery of this record left 1 item(s) in OSDU that 10 undo tries could not take back",
                Attempt = Attempt(aborted, AttemptOutcome.Held, AttemptPhases.UndoWait) with { Error = "an earlier delivery of this record left 1 item(s) in OSDU that 10 undo tries could not take back" },
            });

            // A delivery that held: what it made is due, and newer work waits for the undo's next try.
            var held = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-20));
            await StepAsync(ledger, flowId, due, held, TargetArtifact.Created("objects", ArtifactRoles.Objects, "eml:///dataspace('dev')/obj-1"));
            await CompleteAsync(ledger, flowId, new RecordCompletion
            {
                DeliveryKey = due, Status = RecordStatus.Held, UnitId = held.Id, Error = "HTTP 422", Attempt = Attempt(due, AttemptOutcome.Held, "metadata") with { Error = "HTTP 422" },
            });
            await CompleteAsync(ledger, flowId, new RecordCompletion
            {
                DeliveryKey = due,
                Status = RecordStatus.Pending,
                NothingSent = true,
                NextAttemptUtc = DateTime.UtcNow.AddMinutes(2),
                Attempt = Attempt(due, AttemptOutcome.Skipped, AttemptPhases.UndoWait) with
                {
                    ResultJson = """{"detail":"an earlier delivery of this record left 1 item(s) in OSDU that its undo could not take back yet, so the newer work waits for the undo's next try"}""",
                },
            });

            // A delivery that committed: the dataset it minted is live, and nothing of it is open.
            var committed = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-10));
            await StepAsync(ledger, flowId, delivered, committed, TargetArtifact.Created("dataset:landing/c", ArtifactRoles.Dataset, "dev:dataset--File.Generic:C1", locator: "landing/c"));
            await CompleteAsync(ledger, flowId, new RecordCompletion
            {
                DeliveryKey = delivered, Status = RecordStatus.Delivered, Promote = true, UnitId = committed.Id,
                TargetId = "dev:master-data--Wellbore:WB-DELIVERED", TargetVersion = 3, Attempt = Attempt(delivered, AttemptOutcome.Delivered, "metadata+payload"),
            });

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The aborted record's artifacts, newest first: the record's removal used every try, the rest settled by the undo.
            var artifacts = (await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{aborted.Value:D}/artifacts")).EnumerateArray().ToList();
            Assert.Equal([ArtifactRoles.Session, ArtifactRoles.Record, ArtifactRoles.Dataset], artifacts.Select(a => a.GetProperty("role").GetString()));
            var record = artifacts[1];
            Assert.Equal(("failed", true, true), (record.GetProperty("state").GetString(), record.GetProperty("open").GetBoolean(), record.GetProperty("exhausted").GetBoolean()));
            Assert.Equal((ArtifactLimits.MaxUndoAttempts, JsonValueKind.Null), (record.GetProperty("undoAttempts").GetInt32(), record.GetProperty("nextUndoUtc").ValueKind));
            Assert.Equal(("record", "dev:master-data--Wellbore:WB-ABORTED", 7L), (record.GetProperty("slot").GetString(), record.GetProperty("targetId").GetString(), record.GetProperty("version").GetInt64()));
            Assert.Equal(Failure, record.GetProperty("note").GetString());
            Assert.Equal(unit.Id, record.GetProperty("unitId").GetGuid());
            var dataset = artifacts[2];
            Assert.Equal(("removed", false, false), (dataset.GetProperty("state").GetString(), dataset.GetProperty("open").GetBoolean(), dataset.GetProperty("exhausted").GetBoolean()));
            Assert.Equal(("landing/a", "sweep-test"), (dataset.GetProperty("locator").GetString(), dataset.GetProperty("settledBy").GetString()));
            Assert.NotEqual(JsonValueKind.Null, dataset.GetProperty("settledUtc").ValueKind);
            Assert.Equal(("removed", "abandoned"), (artifacts[0].GetProperty("state").GetString(), artifacts[0].GetProperty("note").GetString()));

            // At most as many as asked for; the committed record's minted dataset is live and closed; a key never held is not found.
            Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{aborted.Value:D}/artifacts?max=1")).EnumerateArray());
            var minted = Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{delivered.Value:D}/artifacts")).EnumerateArray().ToList());
            Assert.Equal(("live", false, "dev:dataset--File.Generic:C1"), (minted.GetProperty("state").GetString(), minted.GetProperty("open").GetBoolean(), minted.GetProperty("targetId").GetString()));
            await StatusAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{Guid.NewGuid():D}/artifacts", HttpStatusCode.NotFound);

            // The record's attempts carry every undo as the ledger wrote it, and the newer work held behind it.
            var attempts = (await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{aborted.Value:D}/attempts")).EnumerateArray().ToList();
            var undos = attempts.Where(a => a.GetProperty("outcome").GetString() == "undone").ToList();
            Assert.Equal(ArtifactLimits.MaxUndoAttempts, undos.Count);
            Assert.All(undos, a => Assert.Equal(AttemptPhases.Undo, a.GetProperty("phase").GetString()));
            var first = undos.Single(a => a.GetProperty("result").GetProperty("undo").GetProperty("artifacts").GetArrayLength() == 3);
            Assert.Equal(("held", "2 removed, 1 failed"), (first.GetProperty("result").GetProperty("undo").GetProperty("reason").GetString(), first.GetProperty("result").GetProperty("undo").GetProperty("summary").GetString()));
            var wait = Assert.Single(attempts, a => a.GetProperty("phase").GetString() == AttemptPhases.UndoWait);
            Assert.Equal("held", wait.GetProperty("outcome").GetString());
            Assert.Contains("10 undo tries", wait.GetProperty("error").GetString(), StringComparison.Ordinal);
            var skipped = Assert.Single(
                (await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{due.Value:D}/attempts")).EnumerateArray().ToList(),
                a => a.GetProperty("phase").GetString() == AttemptPhases.UndoWait);
            Assert.Equal("skipped", skipped.GetProperty("outcome").GetString());
            Assert.Contains("waits for the undo's next try", skipped.GetProperty("result").GetProperty("detail").GetString(), StringComparison.Ordinal);

            // The flow's open undos: one due, one failed that used its tries; the record holding it listed first.
            var open = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos");
            Assert.Equal((0L, 0L, 1L, 1L, 1L, 2L), Counts(open.GetProperty("counts")));
            var only = Assert.Single(open.GetProperty("interfaces").EnumerateArray().ToList());
            Assert.Equal((JsonValueKind.Null, flowId), (only.GetProperty("interface").ValueKind, only.GetProperty("flowId").GetGuid()));
            Assert.Equal(Counts(open.GetProperty("counts")), Counts(only.GetProperty("counts")));
            Assert.Equal(flowId, open.GetProperty("flowId").GetGuid());
            var page = open.GetProperty("records");
            Assert.Equal(2L, page.GetProperty("total").GetInt64());
            var rows = page.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal([aborted.Value, due.Value], rows.Select(r => r.GetProperty("deliveryKey").GetGuid()));
            Assert.Equal(("WB-ABORTED", "held", 1L, 1L, 0L), (rows[0].GetProperty("sourceKey").GetString(), rows[0].GetProperty("status").GetString(), rows[0].GetProperty("failed").GetInt64(), rows[0].GetProperty("failedExhausted").GetInt64(), rows[0].GetProperty("due").GetInt64()));
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("nextUndoUtc").ValueKind);
            Assert.Equal(("WB-DUE", 1L, 0L), (rows[1].GetProperty("sourceKey").GetString(), rows[1].GetProperty("due").GetInt64(), rows[1].GetProperty("failed").GetInt64()));

            // Paged: the second page of one holds the second record, with the count of all.
            var second = (await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos?page=2&pageSize=1")).GetProperty("records");
            Assert.Equal((2L, 2, 1), (second.GetProperty("total").GetInt64(), second.GetProperty("page").GetInt32(), second.GetProperty("pageSize").GetInt32()));
            Assert.Equal(due.Value, Assert.Single(second.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            var past = (await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos?page=9&pageSize=1")).GetProperty("records");
            Assert.Equal((2L, 0), (past.GetProperty("total").GetInt64(), past.GetProperty("items").GetArrayLength()));

            // An interface the flow does not declare, and a pipeline the catalog does not hold, are not found.
            await StatusAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos?interface=nope", HttpStatusCode.NotFound);
            await StatusAsync(client, token, $"/api/v1/delivery/flows/{Guid.NewGuid():D}/undos", HttpStatusCode.NotFound);
        }
        finally
        {
            await CleanUpAsync(cs, repoId, flowId);
        }
    }

    [Fact]
    public async Task A_record_s_enrichments_are_served_in_render_order_with_the_records_they_added_and_every_write_of_them()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-enrichments-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-enrichments-" + suffix);
        var flowId = FlowId.Of(flowName);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var log = Key(suffix, "log");
        var plain = Key(suffix, "plain");
        const string logId = "dev:work-product-component--WellLog:L-1001";
        const string linked = logId + "-enrich.lasfile";
        const string superseded = "dev:dataset--File.Generic:L-1001-enrich.lasfile.0c4f";

        // The log rendered with two enrichments: a quality grade composed into it, and its LAS file registered as a dataset.
        var context = new RenderContext
        {
            MappingReference = "WellLog@1.4.0",
            CacheVersion = string.Empty,
            SchemaSnapshotVersion = "1.4.0",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
            Steps =
            [
                new RenderStep("quality", "WellLogQuality@1.0.0", "fp-quality", "1.4.0", new Dictionary<string, string>(StringComparer.Ordinal)),
                new RenderStep("lasfile", null, null, "1.4.0", new Dictionary<string, string>(StringComparer.Ordinal))
                {
                    Records = """{"kind":"osdu:wks:dataset--File.Generic:1.0.0","mapping":"WellLogLasDataset@1.0.0","mappingFingerprint":"fp-las","through":"dataset"}""",
                },
            ],
        }.Canonical();
        await ledger.UpsertPendingAsync(flowId, [Pending(flowId, log, "L-1001") with { PendingRenderContext = context }, Pending(flowId, plain, "WB-PLAIN")]);

        try
        {
            // A try created a dataset the log no longer links, then held; the next resumed that registration and replaced the
            // version of the dataset the log links now, and delivered. Each write is a step of its attempt, as the worker keeps it.
            const string held = "the record's own write was refused";
            await CompleteAsync(ledger, flowId, new RecordCompletion
            {
                DeliveryKey = log,
                Status = RecordStatus.Held,
                Error = held,
                Attempt = Attempt(log, AttemptOutcome.Held, "metadata") with
                {
                    Error = held,
                    ResultJson = Registrations(
                        Registration(EnrichPartsProtocol.IntentStep(0), superseded, "step", "lasfile"),
                        Registration(EnrichPartsProtocol.RegisterStep(0), superseded, "version", "1")),
                },
            });
            var delivered = Attempt(log, AttemptOutcome.Delivered, "metadata") with
            {
                RunId = Guid.NewGuid(),
                ResultJson = Registrations(
                    Registration(EnrichPartsProtocol.RegisterStep(0), superseded, "version", "1", resumed: true),
                    Registration(EnrichPartsProtocol.IntentStep(1), linked, "priorVersion", "3"),
                    Registration(EnrichPartsProtocol.RegisterStep(1), linked, "version", "4")),
            };
            await CompleteAsync(ledger, flowId, new RecordCompletion
            {
                DeliveryKey = log,
                Status = RecordStatus.Delivered,
                Promote = true,
                TargetId = logId,
                TargetVersion = 3,
                TargetStateJson = new JsonObject
                {
                    [EnrichParts.RegisteredValue] = EnrichParts.RegisteredText([linked]),
                    [EnrichParts.StateKey(linked)] = "doc-hash",
                    [EnrichParts.FilesStateKey(linked)] = "files-hash",
                    [EnrichTrace.SourceKey(linked)] = "welllog_files.csv#12",
                    [EnrichTrace.VersionKey(linked)] = "4",
                    [EnrichTrace.RowsKey("lasfile")] = """{"files":["welllog_files.csv"],"filesCounted":1,"row":"welllog_files.csv#12","rows":1}""",
                }.ToJsonString(),
                Attempt = delivered,
            });

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // In the order they render: the grade adds paths, the LAS file adds records through the Dataset service.
            var enrichments = (await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{log.Value:D}/enrichments")).EnumerateArray().ToList();
            Assert.Equal(["quality", "lasfile"], enrichments.Select(e => e.GetProperty("name").GetString()));
            var quality = enrichments[0];
            Assert.Equal(["paths"], quality.GetProperty("adds").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(("WellLogQuality@1.0.0", "fp-quality", 0), (quality.GetProperty("mapping").GetString(), quality.GetProperty("mappingFingerprint").GetString(), quality.GetProperty("records").GetArrayLength()));
            var las = enrichments[1];
            Assert.Equal(["records"], las.GetProperty("adds").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(
                ("WellLogLasDataset@1.0.0", "osdu:wks:dataset--File.Generic:1.0.0", "dataset", false),
                (las.GetProperty("mapping").GetString(), las.GetProperty("recordKind").GetString(), las.GetProperty("writtenThrough").GetString(), las.GetProperty("setAside").GetBoolean()));
            var rows = las.GetProperty("rows");
            Assert.Equal((1, "welllog_files.csv#12", "welllog_files.csv"), (rows.GetProperty("count").GetInt32(), rows.GetProperty("row").GetString(), rows.GetProperty("files")[0].GetString()));

            // The record the log links first, with the version the ledger saw land and where it came from; the one it no longer links after.
            var records = las.GetProperty("records").EnumerateArray().ToList();
            Assert.Equal([linked, superseded], records.Select(r => r.GetProperty("id").GetString()));
            Assert.Equal(
                (true, 4L, "doc-hash", "files-hash", "welllog_files.csv#12"),
                (records[0].GetProperty("linked").GetBoolean(), records[0].GetProperty("version").GetInt64(), records[0].GetProperty("hash").GetString(), records[0].GetProperty("filesHash").GetString(), records[0].GetProperty("source").GetString()));
            var replaced = Assert.Single(records[0].GetProperty("writes").EnumerateArray().ToList());
            Assert.Equal(
                ("delivered", 4L, 3L),
                (replaced.GetProperty("outcome").GetString(), replaced.GetProperty("version").GetInt64(), replaced.GetProperty("priorVersion").GetInt64()));
            Assert.Equal(delivered.RunId, replaced.GetProperty("runId").GetGuid());

            // The dataset it no longer links: written once, by the try that held, which created it; the try resuming that write is not counted again.
            Assert.Equal((false, 1L), (records[1].GetProperty("linked").GetBoolean(), records[1].GetProperty("version").GetInt64()));
            var createdWrite = Assert.Single(records[1].GetProperty("writes").EnumerateArray().ToList());
            Assert.Equal(
                ("held", 1L, JsonValueKind.Null),
                (createdWrite.GetProperty("outcome").GetString(), createdWrite.GetProperty("version").GetInt64(), createdWrite.GetProperty("priorVersion").ValueKind));
            Assert.NotEqual(replaced.GetProperty("attemptId").GetInt64(), createdWrite.GetProperty("attemptId").GetInt64());

            // A record rendered without enrichments has none; a key the ledger does not hold is not found.
            Assert.Empty((await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{plain.Value:D}/enrichments")).EnumerateArray());
            await StatusAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{Guid.NewGuid():D}/enrichments", HttpStatusCode.NotFound);
        }
        finally
        {
            await CleanUpAsync(cs, repoId, flowId);
        }
    }

    [Fact]
    public async Task A_source_s_open_undos_are_counted_per_interface_and_listed_one_ledger_at_a_time()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-undos-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-undos-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var wellbores = FlowId.Of(flowName + "/wellbores");
        var welllogs = FlowId.Of(flowName + "/welllogs");
        await SeedPipelineAsync(cs, repoId, pipelineId, flowName, TwoInterfaces(flowName));

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(wellbores, TestLedgers.Partition, flowName, flowName + "/wellbores");
        await ledger.RegisterAsync(welllogs, TestLedgers.Partition, flowName, flowName + "/welllogs");
        var log = Key(suffix, "log");
        await ledger.UpsertPendingAsync(welllogs, [Pending(welllogs, log, "L-1001")]);

        try
        {
            // A well log's delivery opened a bulk session and is still under way; another registered a dataset and held.
            var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-5));
            await StepAsync(ledger, welllogs, log, unit,
                TargetArtifact.Intent("dataset:landing/l", ArtifactRoles.Dataset, "landing/l"),
                TargetArtifact.Created("session", ArtifactRoles.Session, "session-l", locator: "dev:work-product-component--WellLog:L-1001"));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // Named no interface: the source counted whole and per interface, and no records, which are one ledger's.
            var whole = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos");
            Assert.Equal((1L, 1L, 0L, 0L, 0L, 0L), Counts(whole.GetProperty("counts")));
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("records").ValueKind);
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("interface").ValueKind);
            var perInterface = whole.GetProperty("interfaces").EnumerateArray().ToDictionary(i => i.GetProperty("interface").GetString()!, i => Counts(i.GetProperty("counts")));
            Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), perInterface["wellbores"]);
            Assert.Equal((1L, 1L, 0L, 0L, 0L, 0L), perInterface["welllogs"]);

            // Named one, its records are listed: an open delivery is listed though nothing of it is due yet.
            var logs = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos?interface=welllogs");
            Assert.Equal(("welllogs", welllogs), (logs.GetProperty("interface").GetString(), logs.GetProperty("flowId").GetGuid()));
            var row = Assert.Single(logs.GetProperty("records").GetProperty("items").EnumerateArray().ToList());
            Assert.Equal((log.Value, welllogs, 1L, 1L, "pending"), (row.GetProperty("deliveryKey").GetGuid(), row.GetProperty("flowId").GetGuid(), row.GetProperty("intent").GetInt64(), row.GetProperty("pending").GetInt64(), row.GetProperty("status").GetString()));
            var bores = (await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/undos?interface=wellbores")).GetProperty("records");
            Assert.Equal((0L, 0), (bores.GetProperty("total").GetInt64(), bores.GetProperty("items").GetArrayLength()));
        }
        finally
        {
            await CleanUpAsync(cs, repoId, wellbores, welllogs);
        }
    }

    private static (long Intent, long Pending, long Due, long Failed, long Exhausted, long ToUndo) Counts(JsonElement counts) => (
        counts.GetProperty("intent").GetInt64(), counts.GetProperty("pending").GetInt64(), counts.GetProperty("due").GetInt64(),
        counts.GetProperty("failed").GetInt64(), counts.GetProperty("failedExhausted").GetInt64(), counts.GetProperty("toUndo").GetInt64());

    private static DeliveryKey Key(string suffix, string name) => DeliveryKey.Derive("artifacts-" + suffix, [name]);

    private static RecordState Pending(Guid flowId, DeliveryKey key, string sourceKey) => new()
    {
        DeliveryKey = key,
        FlowId = flowId,
        SourceKey = sourceKey,
        Label = sourceKey,
        MappingName = "Wellbore",
        Status = RecordStatus.Pending,
        TargetId = "dev:master-data--Wellbore:" + sourceKey,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "1:0:10",
        PendingMetadata = true,
    };

    /// <summary>An attempt's result listing <paramref name="steps"/>, as the worker writes it.</summary>
    private static string Registrations(params JsonObject[] steps) => new JsonObject { ["steps"] = new JsonArray(steps) }.ToJsonString();

    /// <summary>
    /// A step of an enrichment's registration of <paramref name="recordId"/>, returning <paramref name="name"/> as
    /// <paramref name="value"/> beside the id, as the worker keeps it; <paramref name="resumed"/> for one taken over from an earlier try.
    /// </summary>
    private static JsonObject Registration(string step, string recordId, string name, string value, bool resumed = false)
    {
        var node = new JsonObject
        {
            ["name"] = step,
            ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["ms"] = 5,
            ["status"] = 201,
            ["returned"] = new JsonObject { ["recordId"] = recordId, [name] = value },
        };
        if (resumed)
        {
            node["resumed"] = true;
        }

        return node;
    }

    /// <summary>A step of <paramref name="unit"/> reporting <paramref name="artifacts"/>, applied as a worker's checkpoint applies it.</summary>
    private static async Task StepAsync(OsduLedger ledger, Guid flowId, DeliveryKey key, DeliveryUnit unit, params TargetArtifact[] artifacts)
    {
        var steps = new JsonObject
        {
            [DeliveryUnit.StepName] = new JsonObject { ["id"] = unit.Id.ToString("D"), ["startedUtc"] = unit.StartedUtc.ToString("O", CultureInfo.InvariantCulture) },
        }.ToJsonString();
        var token = "artifact-api-tests/" + Guid.NewGuid().ToString("N");
        await ledger.AppendAsync(flowId, token, new LeaseAppend([new RecordStep(key, null, "1:0:10", steps, DateTime.UtcNow) { Unit = unit, Artifacts = artifacts }], []));
        await ledger.CheckpointLeaseAsync(token, DateTime.UtcNow);
    }

    /// <summary>A try settled as <paramref name="completion"/> says, applied as a worker's checkpoint applies it.</summary>
    private static async Task CompleteAsync(OsduLedger ledger, Guid flowId, RecordCompletion completion)
    {
        var token = "artifact-api-tests/" + Guid.NewGuid().ToString("N");
        await ledger.AppendAsync(flowId, token, new LeaseAppend([], [completion]));
        await ledger.CheckpointLeaseAsync(token, completion.Attempt.CompletedUtc);
    }

    private static AttemptRecord Attempt(DeliveryKey key, AttemptOutcome outcome, string phase) => new()
    {
        DeliveryKey = key,
        Worker = "artifact-api-tests",
        StartedUtc = DateTime.UtcNow,
        CompletedUtc = DateTime.UtcNow,
        Outcome = outcome,
        Phase = phase,
    };

    /// <summary>An undo's attempt as the undo writes it: its reason, its unit and each artifact with what became of it.</summary>
    private static AttemptRecord UndoAttempt(DeliveryKey key, DeliveryUnit unit, bool whole) => Attempt(key, AttemptOutcome.Undone, AttemptPhases.Undo) with
    {
        Error = $"1 of {(whole ? 3 : 1)} artifact(s) could not be undone yet: dev:master-data--Wellbore:WB-ABORTED: {Failure}",
        ResultJson = new JsonObject
        {
            ["correlationId"] = Guid.NewGuid().ToString("D"),
            ["undo"] = new JsonObject
            {
                ["reason"] = "held",
                ["keptRecord"] = false,
                ["units"] = new JsonArray(JsonValue.Create(unit.Id.ToString("D"))),
                ["summary"] = whole ? "2 removed, 1 failed" : "1 failed",
                ["artifacts"] = whole
                    ? new JsonArray(
                        new JsonObject { ["artifactId"] = 1, ["slot"] = "dataset:landing/a", ["role"] = "dataset", ["outcome"] = "removed", ["targetId"] = "dev:dataset--File.Generic:A1" },
                        new JsonObject { ["artifactId"] = 2, ["slot"] = "record", ["role"] = "record", ["outcome"] = "failed", ["targetId"] = "dev:master-data--Wellbore:WB-ABORTED", ["note"] = Failure },
                        new JsonObject { ["artifactId"] = 3, ["slot"] = "session", ["role"] = "session", ["outcome"] = "removed", ["targetId"] = "session-a", ["note"] = "abandoned" })
                    : new JsonArray(
                        new JsonObject { ["artifactId"] = 2, ["slot"] = "record", ["role"] = "record", ["outcome"] = "failed", ["targetId"] = "dev:master-data--Wellbore:WB-ABORTED", ["note"] = Failure }),
            },
        }.ToJsonString(),
    };

    private static string SingleForm(string flowName) => $$"""
        flowType: delivery
        name: {{flowName}}
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.arc.Wellbore, key: [facility_name] }
          work: ../.work/artifacts
        render:
          mapping: Wellbore@1.0.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: storage
          headers: { data-partition-id: dev }
        """;

    private static string TwoInterfaces(string flowName) => $$"""
        flowType: delivery
        name: {{flowName}}
        source:
          connection: ${env:OSDU_DATA_DB}
          work: ../.work/undos
        render:
          parameters:
            dataPartition: dev
        target:
          endpoint: ${env:OSDU_URL}
          headers: { data-partition-id: dev }
          protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
        interfaces:
          wellbores:
            record: { object: OsduData.arc.Wellbore, key: [facility_name] }
            mapping: Wellbore@1.0.0
          welllogs:
            record: { object: OsduData.arc.WellLog, key: [source_project, log_id] }
            bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
            mapping: WellLog@1.4.0
        """;

    private static async Task SeedPipelineAsync(string cs, Guid repoId, Guid pipelineId, string flowName, string yaml)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId, Name = "cp-" + flowName, RemoteUrl = "https://example/" + flowName + ".git",
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

    private static async Task CleanUpAsync(string cs, Guid repoId, params Guid[] flowIds)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            await osdu.DeliveryArtifacts.Where(a => flowIds.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryActivityRecords.Where(a => flowIds.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => flowIds.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => flowIds.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryRecordEvents.Where(e => flowIds.Contains(e.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryLeases.Where(l => flowIds.Contains(l.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => flowIds.Contains(i.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => flowIds.Contains(r.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => flowIds.Contains(l.FlowId)).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static ControlPlaneAppFactory Factory(string cs) => new ControlPlaneAppFactory()
        .WithCatalog(cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await GetAsync(client, token, path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task StatusAsync(HttpClient client, string token, string path, HttpStatusCode expected)
    {
        using var response = await GetAsync(client, token, path);
        Assert.True(response.StatusCode == expected, $"GET {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

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
}
