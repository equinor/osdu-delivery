using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Retrieval;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The retrieval kind: its document, and the runner against a fake search and storage service, a temp lake and the ledger on SQL Server.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RetrievalTests : IDisposable
{
    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.*.*";
    private const string Well = "osdu:wks:master-data--Well:1.*.*";

    private const string Yaml = """
        flowType: retrieval
        name: wellbores-out
        description: wellbores from OSDU
        parameters:
          root: { required: true }
          region: { required: false, default: north }
        source:
          endpoint: http://localhost/osdu
          headers: { data-partition-id: dev }
          kinds: ["osdu:wks:master-data--Wellbore:1.*.*", "osdu:wks:master-data--Well:1.*.*"]
          query: 'data.Region:"{region}"'
          pageSize: 2
          incremental: { field: modifyTime, since: "2026-01-01T00:00:00Z", lagMinutes: 5 }
        target:
          location: "{root}/out/{region}"
          rollRecords: 2
          compression: gzip
        reliability: { retry: { attempts: 2, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 1 }
        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler, RetrievalDefinition flow, TestClock clock)
    {
        var runtime = new HttpRuntime(flow.Reliability, new SecretResolver([new EnvSecretProvider()]), clock, handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers);
        return (client, runtime);
    }

    private static string Hits(string cursor, params string[] ids)
    {
        var results = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["kind"] = Wellbore, ["data"] = new JsonObject { ["n"] = id.Length } }).ToArray());
        var body = new JsonObject { ["results"] = results, ["totalCount"] = 3 };
        if (cursor.Length > 0)
        {
            body["cursor"] = cursor;
        }

        return body.ToJsonString();
    }

    private static string[] ReadLines(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    [Fact]
    public void Retrieval_documents_parse_and_validate()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Equal("retrieval", loader.Probe(Yaml));
        var flow = loader.ParseRetrieval(Yaml, "f");
        Assert.Equal("wellbores-out", flow.Name);
        Assert.Equal([Wellbore, Well], flow.Source.Kinds);
        Assert.Equal(2, flow.Source.PageSize);
        Assert.True(flow.Target.Gzip);
        Assert.Equal(2, flow.Target.RollRecords);
        Assert.Equal("modifyTime", flow.Source.Incremental!.Field);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), flow.Source.Incremental.Since);
        Assert.Equal(5, flow.Source.Incremental.LagMinutes);
        Assert.Equal(1, flow.Parallelism.RecordsAtOnce);
        Assert.Empty(flow.CredentialReferences());

        var kind = new RetrievalFlowKind(loader);
        var document = kind.Parse(Yaml, "f");
        Assert.Equal("retrieval", document.Kind);
        Assert.Equal("http://localhost/osdu", document.SourceReference);
        Assert.Equal("{root}/out/{region}", document.TargetReference);
        Assert.False(document.RequiresRepoTree);

        // The operations a retrieval run may be asked for, and which of them writes: the platform reads them from the
        // kind, so a schedule and a trigger offer exactly these.
        Assert.Equal([DeliveryOperations.Retrieve, DeliveryOperations.Plan], kind.Operations.Select(o => o.Name));
        Assert.True(kind.Operations[0].WritesTarget);
        Assert.False(kind.Operations[1].WritesTarget);

        Assert.Contains("pageSize", Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace("pageSize: 2", "pageSize: 5000", StringComparison.Ordinal), "f")).Message, StringComparison.Ordinal);
        Assert.Contains("authority:source", Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace(Well, "not a kind", StringComparison.Ordinal), "f")).Message, StringComparison.Ordinal);
        Assert.Contains("compression", Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace("compression: gzip", "compression: zip", StringComparison.Ordinal), "f")).Message, StringComparison.Ordinal);
        Assert.Contains("declared under parameters", Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace("{root}/out/{region}", "{root}/out/{nope}", StringComparison.Ordinal), "f")).Message, StringComparison.Ordinal);
        Assert.Contains("since", Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace("2026-01-01T00:00:00Z", "yesterday", StringComparison.Ordinal), "f")).Message, StringComparison.Ordinal);
        Assert.Contains("flowType: delivery", Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Yaml, "f")).Message, StringComparison.Ordinal);
        Assert.Contains("rollRecordz", Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace("rollRecords: 2", "rollRecordz: 2", StringComparison.Ordinal), "f")).Message, StringComparison.Ordinal);
        Assert.Contains(
            "f: parallelism.concurrency is now parallelism.recordsAtOnce, as on a delivery flow: the kinds retrieved at once. Rename it.",
            Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml.Replace("parallelism: { recordsAtOnce: 1 }", "parallelism: { concurrency: 1 }", StringComparison.Ordinal), "f")).Message,
            StringComparison.Ordinal);

        // A retrieval payload carries only force, which restarts an incremental flow at its declared start: there is no
        // submission, record or slice for it to name.
        var forced = new RunParameters { Operation = DeliveryOperations.Retrieve, Payload = """{"force":true}""" };
        kind.ValidateParameters(forced);
        Assert.True(RetrievalFlowKind.Forced(forced));
        Assert.False(RetrievalFlowKind.Forced(RunParameters.None));
        var scoped = new RunParameters { Operation = DeliveryOperations.Retrieve, Payload = $$"""{"submissionId":"{{Guid.NewGuid():D}}"}""" };
        Assert.Contains(
            "payload submissionId does not apply to a retrieval flow: only a delivery flow's runs name a submission; a retrieval flow's payload names only force.",
            Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(scoped)).Message, StringComparison.Ordinal);
        Assert.Equal(DeliveryOperations.Retrieve, RetrievalExecutor.Operation(RunParameters.None));
        Assert.Equal(DeliveryOperations.Plan, RetrievalExecutor.Operation(new RunParameters { Operation = DeliveryOperations.Plan }));
        Assert.Throws<SqlFlowException>(() => RetrievalExecutor.Operation(new RunParameters { Operation = DeliveryOperations.Verify }));
    }

    [Fact]
    public async Task A_run_pages_every_kind_into_rolling_files_with_a_manifest_and_a_ledger_row_and_the_next_run_continues_from_its_watermark()
    {
        var root = Samples.NewTempDirectory();
        var flow = new DeliveryDocumentLoader().ParseRetrieval(Yaml, "f");
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query_with_cursor", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit switch
            {
                0 => Hits("c1", "dev:wb:1", "dev:wb:2"),
                1 => Hits(string.Empty, "dev:wb:3"),
                _ => """{"results":[],"totalCount":0}""",
            }));
        var (client, runtime) = Client(handler, flow, _clock);
        using (runtime)
        {
            var ledger = _db.Ledger(_clock);
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["root"] = root };
            await _db.Ledger(_clock).RegisterAsync(flow);
            var runner = new RetrievalRunner(flow, FlowParameters.Resolve(flow.Parameters, "f", values), client, Samples.Stores(), ledger, _clock, Samples.Logger<RetrievalRunner>());
            var runId = Guid.NewGuid();

            var result = await runner.RunAsync(runId, "tester", force: false, CancellationToken.None);
            Assert.False(result.NothingToDo);
            Assert.Equal(3, result.Records);
            Assert.Equal(2, result.Files);
            Assert.Equal(2, result.Kinds.Count);
            Assert.Equal(3, result.Kinds[0].Records);
            Assert.Equal(0, result.Kinds[1].Records);
            Assert.Empty(result.Kinds[1].Files);
            Assert.StartsWith(Path.Combine(root, "out", "north"), result.Location, StringComparison.Ordinal);
            Assert.Contains(runId.ToString("N")[..8], result.Location, StringComparison.Ordinal);
            Assert.Equal("modifyTime", result.Window!.Field);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Window.From);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(-5), result.Window.To);

            var first = Path.Combine(result.Location, RetrievalRunner.Slug(Wellbore), "part-00001.jsonl.gz");
            var second = Path.Combine(result.Location, RetrievalRunner.Slug(Wellbore), "part-00002.jsonl.gz");
            Assert.Equal([first, second], result.Kinds[0].Files.Select(f => f.Path));
            Assert.Equal([2L, 1L], result.Kinds[0].Files.Select(f => f.Records));
            var lines = ReadLines(first);
            Assert.Equal(2, lines.Length);
            Assert.Equal("dev:wb:1", JsonNode.Parse(lines[0])!["id"]!.GetValue<string>());
            Assert.Single(ReadLines(second));

            var manifest = JsonNode.Parse(File.ReadAllText(result.ManifestLocation!))!.AsObject();
            Assert.Equal("wellbores-out", manifest["flow"]!.GetValue<string>());
            Assert.Equal(runId.ToString("D"), manifest["runId"]!.GetValue<string>());
            Assert.Equal(3, manifest["records"]!.GetValue<long>());
            Assert.Equal(2, manifest["files"]!.GetValue<int>());
            Assert.Equal("gzip", manifest["compression"]!.GetValue<string>());
            Assert.Equal("2026-01-01T00:00:00.0000000Z", manifest["window"]!["from"]!.GetValue<string>());
            Assert.Equal(2, manifest["kinds"]![0]!["files"]!.AsArray().Count);
            Assert.Equal(0, manifest["kinds"]![1]!["records"]!.GetValue<long>());

            var rows = await ledger.ListRetrievalsAsync(flow.Id, 10);
            var row = Assert.Single(rows);
            Assert.Equal(RetrievalStatus.Done, row.Status);
            Assert.Equal(3, row.Records);
            Assert.Equal(2, row.Files);
            Assert.Equal(runId, row.RunId);
            Assert.Equal("tester", row.Actor);
            Assert.Equal(result.Window.To, row.WindowTo);
            Assert.Equal(result.ManifestLocation, row.ManifestLocation);
            Assert.Equal(Wellbore + "," + Well, row.Kinds);
            Assert.NotNull(row.CompletedUtc);

            // The requests: the first page counts and carries the window, the second continues the cursor.
            var page1 = JsonNode.Parse(handler.Calls[0].Body!)!.AsObject();
            Assert.Equal(Wellbore, page1["kind"]!.GetValue<string>());
            Assert.Equal(2, page1["limit"]!.GetValue<int>());
            Assert.True(page1["trackTotalCount"]!.GetValue<bool>());
            Assert.Equal(
                "(data.Region:\"north\") AND (modifyTime:[2026-01-01T00:00:00.000Z TO 2026-09-07T11:55:00.000Z} OR (createTime:[2026-01-01T00:00:00.000Z TO 2026-09-07T11:55:00.000Z} AND NOT _exists_:modifyTime))",
                page1["query"]!.GetValue<string>());
            var page2 = JsonNode.Parse(handler.Calls[1].Body!)!.AsObject();
            Assert.Equal("c1", page2["cursor"]!.GetValue<string>());
            Assert.Null(page2["trackTotalCount"]);
            Assert.Equal(Well, JsonNode.Parse(handler.Calls[2].Body!)!["kind"]!.GetValue<string>());
            Assert.Equal(3, handler.Calls.Count);

            // The next run continues from the first run's upper bound.
            _clock.Advance(TimeSpan.FromMinutes(10));
            var next = await runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);
            Assert.Equal(0, next.Records);
            Assert.Equal(0, next.Files);
            Assert.Contains(
                "(modifyTime:[2026-09-07T11:55:00.000Z TO 2026-09-07T12:05:00.000Z} OR (createTime:[2026-09-07T11:55:00.000Z TO 2026-09-07T12:05:00.000Z} AND NOT _exists_:modifyTime))",
                JsonNode.Parse(handler.Calls[3].Body!)!["query"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(5, handler.Calls.Count);

            // A forced run goes back to the declared start; a run with nothing new in the window does nothing at all.
            var forced = await runner.RunAsync(Guid.NewGuid(), "tester", force: true, CancellationToken.None);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), forced.Window!.From);
            Assert.Contains("[2026-01-01T00:00:00.000Z TO", JsonNode.Parse(handler.Calls[5].Body!)!["query"]!.GetValue<string>(), StringComparison.Ordinal);
            var idle = await runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);
            Assert.True(idle.NothingToDo);
            Assert.Equal(7, handler.Calls.Count);
            Assert.Equal(4, (await ledger.ListRetrievalsAsync(flow.Id, 10)).Count);
            Assert.All(await ledger.ListRetrievalsAsync(flow.Id, 10), r => Assert.Equal(RetrievalStatus.Done, r.Status));
        }
    }

    [Fact]
    public async Task Full_records_are_read_back_from_storage_the_ones_it_cannot_return_are_listed_and_a_plan_counts()
    {
        var root = Samples.NewTempDirectory();
        // The raw literal takes the line endings the file was checked out with, so they are made "\n" before a line is cut.
        var yaml = Yaml.ReplaceLineEndings("\n")
            .Replace("kinds: [\"osdu:wks:master-data--Wellbore:1.*.*\", \"osdu:wks:master-data--Well:1.*.*\"]", "kind: " + Wellbore, StringComparison.Ordinal)
            .Replace("pageSize: 2", "pageSize: 3\n  fetchRecords: true\n  fetchParallelism: 2", StringComparison.Ordinal)
            .Replace("  incremental: { field: modifyTime, since: \"2026-01-01T00:00:00Z\", lagMinutes: 5 }\n", string.Empty, StringComparison.Ordinal)
            .Replace("compression: gzip", "compression: none", StringComparison.Ordinal);
        var flow = new DeliveryDocumentLoader().ParseRetrieval(yaml, "f");
        Assert.True(flow.Source.FetchRecords);
        Assert.Null(flow.Source.Incremental);

        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query_with_cursor", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? Hits("z", "a", "b", "c") : """{"results":[]}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0
                ? """{"records":[{"id":"a","data":{"full":1}},{"id":"c","data":{"full":3}}],"retryRecords":["b"]}"""
                : """{"records":[],"retryRecords":[]}"""))
            .On(HttpMethod.Post, "/query", HttpStatusCode.OK, """{"results":[],"totalCount":3}""");
        var (client, runtime) = Client(handler, flow, _clock);
        using (runtime)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["root"] = root };
            await _db.Ledger(_clock).RegisterAsync(flow);
            var runner = new RetrievalRunner(flow, FlowParameters.Resolve(flow.Parameters, "f", values), client, Samples.Stores(), _db.Ledger(_clock), _clock, Samples.Logger<RetrievalRunner>());

            var (window, estimates) = await runner.EstimateAsync(force: false, CancellationToken.None);
            Assert.Null(window);
            Assert.Equal(3, Assert.Single(estimates).TotalCount);
            var count = JsonNode.Parse(handler.Calls[0].Body!)!.AsObject();
            Assert.True(count["trackTotalCount"]!.GetValue<bool>());
            Assert.Equal(1, count["limit"]!.GetValue<int>());
            Assert.Equal("data.Region:\"north\"", count["query"]!.GetValue<string>());

            var result = await runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);
            Assert.Equal(2, result.Records);
            Assert.Null(result.Window);
            var kind = Assert.Single(result.Kinds);
            Assert.Equal(1, kind.Missing);
            var file = Assert.Single(kind.Files);
            Assert.EndsWith("part-00001.jsonl", file.Path, StringComparison.Ordinal);
            var lines = File.ReadAllLines(file.Path);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.Contains("\"full\"", line, StringComparison.Ordinal));
            var manifest = JsonNode.Parse(File.ReadAllText(result.ManifestLocation!))!.AsObject();
            Assert.Equal(["b"], manifest["kinds"]![0]!["missingIds"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Null(manifest["window"]);

            var fetches = handler.Calls.Where(c => c.Uri.AbsolutePath.EndsWith("/query/records", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, fetches.Count);
            Assert.Equal(["a", "b", "c"], JsonNode.Parse(fetches[0].Body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(["b"], JsonNode.Parse(fetches[1].Body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task A_failing_page_closes_the_ledger_row_as_failed_and_leaves_the_watermark_alone()
    {
        var root = Samples.NewTempDirectory();
        var flow = new DeliveryDocumentLoader().ParseRetrieval(Yaml, "f");
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query_with_cursor", hit => hit == 0 ? FakeHttpHandler.Json(HttpStatusCode.OK, Hits("c1", "dev:wb:1", "dev:wb:2")) : FakeHttpHandler.Json(HttpStatusCode.Forbidden, "{\"message\":\"no\"}"))
            .On(HttpMethod.Delete, "/query_with_cursor/c1", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler, flow, _clock);
        using (runtime)
        {
            var ledger = _db.Ledger(_clock);
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["root"] = root };
            await _db.Ledger(_clock).RegisterAsync(flow);
            var runner = new RetrievalRunner(flow, FlowParameters.Resolve(flow.Parameters, "f", values), client, Samples.Stores(), ledger, _clock, Samples.Logger<RetrievalRunner>());
            var ex = await Assert.ThrowsAsync<OsduStatusException>(() => runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None));
            Assert.Equal(403, ex.StatusCode);
            var row = Assert.Single(await ledger.ListRetrievalsAsync(flow.Id, 10));
            Assert.Equal(RetrievalStatus.Failed, row.Status);
            Assert.Contains("403", row.Error, StringComparison.Ordinal);
            Assert.Null(await ledger.LastRetrievalAsync(flow.Id, RetrievalStatus.Done));
            Assert.Contains(handler.Calls, c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.EndsWith("/query_with_cursor/c1", StringComparison.Ordinal));
        }
    }

    public void Dispose() => _db.Dispose();
}
