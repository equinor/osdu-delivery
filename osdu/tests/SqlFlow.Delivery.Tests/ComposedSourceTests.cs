using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A source whose well logs carry LAS files and curve bulk data, delivered through the fileAndDdms route by the executor
/// (osdu/docs/reference/flow/routes.md, fileAndDdms and manifestAndDdms; osdu/docs/reference/concepts/protocols.md,
/// Payload parts): the planner keeps both parts in the ledger's payload hash and location, the worker sends only the
/// part that moved, a redelivery of the files alone sends them and rewrites the record with its bulk link, and every
/// request keeps to the pinned contracts.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ComposedSourceTests : IDisposable
{
    private const string WellLogTable = "OsduData.silver.WellLog";
    private const string Ddms = FakeOsduPlatform.DdmsRoot + "/ddms/v3/welllogs";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero));
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open by a finished run is the temporary folder's to clean up.
        }
    }

    private string SourceYaml()
    {
        var root = _root.Replace('\\', '/');
        var mappings = Samples.FixtureMappings.Replace('\\', '/');
        return ($$"""
            flowType: delivery
            name: logs
            parameters:
              logSource: { required: true }
            source:
              connection: ${env:OSDU_DATA_DB}
              lastModified: update_date
              work: '{{root}}/work/{logSource}'
            render:
              mappings: '{{mappings}}'
              parameters:
                dataPartition: dev
                aclOwner: data.welldb.owners@dev.dataservices.energy
                aclViewer: data.welldb.viewers@dev.dataservices.energy
                legalTag: dev-osdu-default-legal
            target:
              endpoint: {{FakeOsduPlatform.Endpoint}}
              headers:
                data-partition-id: {{Samples.SampleCacheScope}}
              ddms:
                wellbore:
                  root: {{FakeOsduPlatform.DdmsRoot}}
            reliability:
              retry: { attempts: 1 }
            parallelism:
              recordsAtOnce: 1
              renderThreads: 1
              interfacesAtOnce: 1
            interfaces:
              welllogs:
                record: { object: {{WellLogTable}}, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
                datasets:
                  curves: { object: OsduData.silver.WellLogCurve, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
                files: { root: '{{root}}/las', locationColumn: las_folder, pattern: "*.las", hashColumn: las_hash }
                bulk: { root: '{{root}}/curves', locationColumn: curve_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
                mapping: WellLog@1.4.0
            """).ReplaceLineEndings("\n");
    }

    private SourceDefinition Load()
    {
        var path = Path.Combine(_root, "flows", "logs.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SourceYaml());
        return new DeliveryDocumentLoader().LoadSource(path);
    }

    /// <summary>The sample logs, each with its curve chunk and a LAS file beside it, and the row naming both.</summary>
    private async Task<MemoryIngestionTables> EstateAsync()
    {
        var tables = new MemoryIngestionTables(_clock);
        var logs = SampleEstate.Logs();
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            await SampleWellLogs.WriteChunkAsync(SampleEstate.PayloadFolder(_root, log), log);
            // A log id holds a slash, so the LAS file is named for its folder.
            var las = Path.Combine([_root, "las", .. log.Folder.Split('/')]);
            Directory.CreateDirectory(las);
            await File.WriteAllTextAsync(Path.Combine(las, Path.GetFileName(las) + ".las"), $"~VERSION INFORMATION\n VERS. 2.0 :\n~WELL INFORMATION\n WELL. {log.WellboreUwi} :\n");
            tables.Add(SampleEstate.Record(log, Now, i + 1)
                .With("las_folder", log.Folder)
                .With("las_hash", "las-" + log.LogId));
        }

        return tables;
    }

    private static async Task<DocumentExecutionResult> RunAsync(EngineContext engine, SourceDefinition source, DeliveryRunPayload? payload = null)
    {
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        return await new DeliveryExecutor(provider).ExecuteAsync(
            new DeliveryFlowDocument { Source = source },
            source.SourcePath!,
            new DocumentExecutionOptions
            {
                RunId = Guid.CreateVersion7(),
                Actor = "test",
                Parameters = new RunParameters
                {
                    Operation = DeliveryOperations.Deliver,
                    Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal),
                    Payload = payload?.ToJson(),
                },
            },
            CancellationToken.None);
    }

    /// <summary>The interface's outcome: a run of the source reports it among its interfaces, a run of the one interface alone.</summary>
    private static DeliverOutcome Delivered(DocumentExecutionResult result)
    {
        Assert.True(result.Success, result.Error);
        if (result.Result is DeliverOutcome alone)
        {
            return alone;
        }

        var run = Assert.Single(Assert.IsType<SourceRunOutcome>(result.Result).Interfaces);
        Assert.Equal(("fileAndDdms", InterfaceStates.Completed), (run.Route, run.State));
        return Assert.IsType<DeliverOutcome>(run.Result);
    }

    /// <summary>The reachability checks every run makes of the services its route reaches, before it plans anything.</summary>
    private static readonly string[] Probes = ["GET /api/file/v2/info", "GET " + FakeOsduPlatform.DdmsRoot + "/about"];

    /// <summary>
    /// What a run sent to the file service and the DDMS since <paramref name="from"/>, past its reachability checks and its
    /// legal tag check, with a landing-zone location named by its kind.
    /// </summary>
    private static List<string> Sent(FakeOsduPlatform platform, int from)
        => Calls(platform, from)
            .Where(c => !Probes.Contains(c, StringComparer.Ordinal) && !c.StartsWith("POST /api/legal/", StringComparison.Ordinal))
            .ToList();

    private static IEnumerable<string> Calls(FakeOsduPlatform platform, int from)
        => platform.Calls.Skip(from)
            .Select(c => Regex.Replace(c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath), "/staging/landing/l-[0-9]+$", "/staging/landing/l-N"));

    private static string TargetId(int log) => "dev:work-product-component--WellLog:" + SampleEstate.Key(log).Value.ToString("N");

    private static string? BulkLink(JsonObject record)
        => record["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>();

    [Fact]
    public async Task Well_logs_with_files_and_bulk_data_send_each_part_when_it_moves_or_a_redelivery_names_it()
    {
        var tables = await EstateAsync();
        var platform = new FakeOsduPlatform();
        using var protocols = new FakeOsduProtocols(platform);
        var engine = Samples.Engine(_db.Ledger(_clock), _clock, protocols: protocols, sources: new MemoryEstate().Add(WellLogTable, tables));
        var source = Load();
        var ledger = engine.Ledger!;
        var flowId = FlowId.Of("logs/welllogs");

        // Every log: its file uploaded and registered, its record written through the well log collection pointing at
        // the dataset, then its curves.
        Assert.Equal(5, Delivered(await RunAsync(engine, source)).Delivered);
        Assert.Equal(Probes, Calls(platform, 0).Take(2));
        var first = Sent(platform, 0);
        Assert.Equal(5, first.Count(c => c == "POST /api/file/v2/files/metadata"));
        Assert.Equal(5, first.Count(c => c == "POST " + Ddms));
        Assert.Equal(5, first.Count(c => c.StartsWith("POST " + Ddms + "/", StringComparison.Ordinal) && c.EndsWith("/data", StringComparison.Ordinal)));
        Assert.Equal(
            [
                "GET /api/file/v2/files/uploadURL",
                "PUT /staging/landing/l-N",
                "POST /api/file/v2/files/metadata",
                "POST " + Ddms,
                "POST " + Ddms + "/" + TargetId(0) + "/data",
                "GET " + Ddms + "/" + TargetId(0),
            ],
            first.Take(6));

        // The ledger keeps one payload hash over both parts, and each part's hash and the dataset in the target state.
        var logs = SampleEstate.Logs();
        var delivered = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        Assert.Equal(RecordStatus.Delivered, delivered.Status);
        Assert.Matches("^[0-9a-f]{64}$", delivered.PayloadHash);
        var state = JsonMerge.ToValues(delivered.TargetStateJson);
        Assert.Equal("las-" + logs[0].LogId, state[PayloadParts.StateKey(PayloadParts.Files)]);
        Assert.Equal(logs[0].GridHash(), state[PayloadParts.StateKey(PayloadParts.Bulk)]);
        var dataset = state[Engine.Protocols.FileUploads.DatasetIdsValue];
        Assert.StartsWith("dev:dataset--File.Generic:minted-", dataset, StringComparison.Ordinal);
        Assert.Contains(dataset + ":", platform.Records[TargetId(0)]["data"]!["Datasets"]!.AsArray().Select(d => d!.GetValue<string>()));

        // The same rows again: nothing is sent.
        var mark = platform.Calls.Count;
        Assert.Equal(0, Delivered(await RunAsync(engine, source)).Delivered);
        Assert.Empty(Sent(platform, mark));

        // One log's curves are rewritten: only its bulk data goes, and the files' hash and dataset stay as they were.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var moved = logs[1].WithValues(logs[1].Columns()[1], value => value is null ? null : value + 10);
        await SampleEstate.RewritePayloadAsync(_root, tables.Records[1], moved, Now);
        mark = platform.Calls.Count;
        Assert.Equal(1, Delivered(await RunAsync(engine, source)).Delivered);
        Assert.Equal(["POST " + Ddms + "/" + TargetId(1) + "/data", "GET " + Ddms + "/" + TargetId(1)], Sent(platform, mark));
        var rewritten = JsonMerge.ToValues((await ledger.GetRecordAsync(flowId, SampleEstate.Key(1)))!.TargetStateJson);
        Assert.Equal(moved.GridHash(), rewritten[PayloadParts.StateKey(PayloadParts.Bulk)]);
        Assert.Equal("las-" + logs[1].LogId, rewritten[PayloadParts.StateKey(PayloadParts.Files)]);

        // A redelivery of the third log's files: the ledger names the part, and the run uploads and registers the file
        // again and rewrites the record to point at it with the bulk link the DDMS holds. The curves are not sent.
        var before = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(2)))!;
        var link = BulkLink(platform.Records[TargetId(2)]);
        Assert.NotNull(link);
        mark = platform.Calls.Count;
        var redelivery = new DeliveryRunPayload { Interface = "welllogs", RecordKeys = [SampleEstate.Key(2).Value], Redeliver = RedeliverScopes.Files };
        Assert.Equal(1, Delivered(await RunAsync(engine, source, redelivery)).Delivered);
        Assert.Equal(
            [
                "GET /api/file/v2/files/uploadURL",
                "PUT /staging/landing/l-N",
                "POST /api/file/v2/files/metadata",
                "GET " + Ddms + "/" + TargetId(2),
                "POST " + Ddms,
            ],
            Sent(platform, mark));
        var after = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(2)))!;
        Assert.Equal(RecordStatus.Delivered, after.Status);
        Assert.Equal(before.PayloadHash, after.PayloadHash);
        var redelivered = JsonMerge.ToValues(after.TargetStateJson)[Engine.Protocols.FileUploads.DatasetIdsValue];
        Assert.NotEqual(JsonMerge.ToValues(before.TargetStateJson)[Engine.Protocols.FileUploads.DatasetIdsValue], redelivered);
        var record = platform.Records[TargetId(2)];
        Assert.Equal([redelivered + ":"], record["data"]!["Datasets"]!.AsArray().Select(d => d!.GetValue<string>()).Where(d => d.Contains("dataset--File.Generic", StringComparison.Ordinal)));
        Assert.Equal(link, BulkLink(record));
        Assert.Equal(0, platform.RefusedLinks);

        // The redelivery is on the record's history, and the next run has nothing left to send.
        Assert.Contains(await ledger.ListAttemptsAsync(flowId, SampleEstate.Key(2), 10), a => a.Outcome == AttemptOutcome.Delivered && a.CompletedUtc >= before.UpdatedUtc);
        mark = platform.Calls.Count;
        Assert.Equal(0, Delivered(await RunAsync(engine, source)).Delivered);
        Assert.Empty(Sent(platform, mark));

        // A redelivery of the curves that names no record sends every delivered log's bulk data again, and no file.
        mark = platform.Calls.Count;
        await RunAsync(engine, source, new DeliveryRunPayload { Interface = "welllogs", Redeliver = RedeliverScopes.Bulk });
        var resent = Sent(platform, mark);
        Assert.Equal(5, resent.Count(c => c.StartsWith("POST " + Ddms + "/", StringComparison.Ordinal) && c.EndsWith("/data", StringComparison.Ordinal)));
        Assert.DoesNotContain(resent, c => c.Contains("/api/file/", StringComparison.Ordinal));
        Assert.Empty(await ledger.ListPlanRequestedAsync(flowId, null, 10));
        mark = platform.Calls.Count;
        Assert.Equal(0, Delivered(await RunAsync(engine, source)).Delivered);
        Assert.Empty(Sent(platform, mark));

        OsduContracts.AssertConform(platform.Calls, FakeOsduPlatform.ToSignedLocation, OsduContracts.File, OsduContracts.WellboreDdms, OsduContracts.Legal);
    }
}
