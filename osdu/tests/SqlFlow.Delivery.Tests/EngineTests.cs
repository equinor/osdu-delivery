using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Execution;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

[Trait("Category", "Integration")]
public class ProtocolTests
{
    private static (OsduHttpClient Client, FakeHttpHandler Handler, HttpRuntime Runtime) Client(FakeHttpHandler? handler = null)
    {
        handler ??= new FakeHttpHandler();
        var runtime = new HttpRuntime(new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 1, MaxDelayMs = 1 } }, new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, "http://localhost/facade-api", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string> { ["data-partition-id"] = "dev" });
        return (client, handler, runtime);
    }

    /// <summary>A log describing every curve the test chunks carry, as the wellbore DDMS requires of a log's bulk data.</summary>
    private static readonly string LogDocument =
        "{\"id\":\"dev:work-product-component--WellLog:abc\",\"kind\":\"k\",\"data\":{\"Name\":\"n\",\"Curves\":["
        + string.Join(",", Enumerable.Range(0, 10).Select(i => "CURVE_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Prepend("MD").Select(c => "{\"CurveID\":\"" + c + "\"}"))
        + "]}}";

    private static DeliveryWork Work(bool metadata, bool payload, int chunks, long? existing = null, IPayloadSource? source = null, string? document = null) => new()
    {
        Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("test", ["abc"]),
        TargetId = "dev:work-product-component--WellLog:abc",
        Document = TestSchema.Doc(document ?? LogDocument),
        DeliverMetadata = metadata,
        DeliverPayload = payload,
        Payload = source ?? new MemoryPayload(chunks),
        ExistingVersion = existing,
    };

    [Fact]
    public async Task WellLog_single_chunk_posts_record_then_streams_data()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, """{"recordCount":1,"recordIds":["dev:work-product-component--WellLog:abc"],"recordIdVersions":["dev:work-product-component--WellLog:abc:1699999"]}""")
            .On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(true, true, 1));

            // The metadata write made version 1699999 and the bulk write made 1700001, which is what OSDU serves: a live
            // wellbore DDMS answered a verify straight after a delivery recorded at the first with "drifted".
            Assert.Equal(1700001, outcome.TargetVersion);
            Assert.Equal("1700001", outcome.Returned["version"]);
            Assert.Equal(1, outcome.ChunksSent);
            Assert.Equal(3, handler.Calls.Count);
            Assert.Equal(HttpMethod.Get, handler.Calls[2].Method);
            Assert.StartsWith("[{", handler.Calls[0].Body, StringComparison.Ordinal);
            Assert.Equal("dev", handler.Calls[0].Headers["data-partition-id"]);
            Assert.Equal("application/x-parquet", handler.Calls[1].ContentType);
            Assert.StartsWith("PAR1", handler.Calls[1].Body, StringComparison.Ordinal);
            Assert.EndsWith("/welllogs/dev:work-product-component--WellLog:abc/data", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Several_chunks_always_open_a_session_because_the_bulk_endpoint_replaces_the_whole_bulk()
    {
        // POST /welllogs/{id}/data carries "the entire bulk which will replace as latest version any previous
        // bulk", so posting three chunks to it would leave the record holding the third and report three delivered.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-9"}""")
            .On(HttpMethod.Post, "/sessions/sess-9/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-9", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions { SessionThresholdChunks = 1 }, Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 3));

            Assert.Equal(3, outcome.ChunksSent);
            Assert.Equal("sess-9", outcome.Returned["sessionId"]);
            Assert.Equal(1700001, outcome.TargetVersion);
            // Nothing is posted to the bulk endpoint; the only request there is the read of the committed log's description.
            Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/welllogs/dev:work-product-component--WellLog:abc/data", StringComparison.Ordinal));
            Assert.Equal(3, handler.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/sessions/sess-9/data", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task A_threshold_of_zero_opens_a_session_even_for_one_chunk()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-0"}""")
            .On(HttpMethod.Post, "/sessions/sess-0/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-0", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions { SessionThresholdChunks = 0 }, Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 1));

            Assert.Equal(1, outcome.ChunksSent);
            Assert.Equal("sess-0", outcome.Returned["sessionId"]);
        }
    }

    [Fact]
    public async Task A_commit_resent_after_a_lost_response_settles_on_the_session_state_rather_than_failing()
    {
        // The commit is a PATCH and the retry stack resends it, so a commit that worked and whose response was lost
        // meets a session that is no longer open. The session says which happened.
        var committed = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-c"}""")
            .On(HttpMethod.Post, "/sessions/sess-c/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-c", HttpStatusCode.Conflict, """{"detail":"session is not open"}""")
            .On(HttpMethod.Get, "/sessions/sess-c", HttpStatusCode.OK, """{"id":"sess-c","state":"committed"}""")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        var (client, _, runtime) = Client(committed);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 2));

            Assert.Equal(2, outcome.ChunksSent);
            Assert.Single(committed.Calls, c => c.Method == HttpMethod.Get && c.Uri.AbsolutePath.EndsWith("/sessions/sess-c", StringComparison.Ordinal));
            Assert.Equal(1700001, outcome.TargetVersion);
        }

        // A session that is not committed is a real failure, and the payload is reported as not landed.
        var abandoned = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-a"}""")
            .On(HttpMethod.Post, "/sessions/sess-a/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-a", HttpStatusCode.Conflict, """{"detail":"session expired"}""")
            .On(HttpMethod.Get, "/sessions/sess-a", HttpStatusCode.OK, """{"id":"sess-a","state":"abandoned"}""");
        var (client2, _, runtime2) = Client(abandoned);
        using (runtime2)
        {
            var protocol = new OsduDdmsProtocol(client2, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => protocol.DeliverAsync(Work(false, true, 2)));

            Assert.Contains("abandoned", ex.Message, StringComparison.Ordinal);
            Assert.Contains("did not land", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WellLog_multi_chunk_uses_a_session_and_abandons_on_failure()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-1","mode":"overwrite"}""")
            .On(HttpMethod.Post, "/sessions/sess-1/data", hit => hit == 1 ? FakeHttpHandler.Json(HttpStatusCode.UnprocessableEntity, "bad chunk") : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"))
            .On(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var ex = await Assert.ThrowsAsync<OsduStatusException>(() => protocol.DeliverAsync(Work(false, true, 3, existing: 5)));
            Assert.Equal(422, ex.StatusCode);
            var create = handler.Calls[0];
            Assert.Contains("\"fromVersion\":5", create.Body, StringComparison.Ordinal);
            Assert.Contains("\"mode\":\"overwrite\"", create.Body, StringComparison.Ordinal);
            var abandon = handler.Calls.Last();
            Assert.Equal(HttpMethod.Patch, abandon.Method);
            Assert.Contains("abandon", abandon.Body, StringComparison.Ordinal);
        }

        var ok = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-2"}""")
            .On(HttpMethod.Post, "/sessions/sess-2/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-2", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        var (client2, _, runtime2) = Client(ok);
        using (runtime2)
        {
            var protocol = new OsduDdmsProtocol(client2, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 3));
            Assert.Equal(3, outcome.ChunksSent);
            var sent = ok.Calls.Where(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/data", StringComparison.Ordinal)).Select(c => c.Body).ToList();
            Assert.Equal(3, sent.Count);
            Assert.All(sent, body => Assert.StartsWith("PAR1", body, StringComparison.Ordinal));
            Assert.Equal(3, sent.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("commit", ok.Calls.Last(c => c.Method == HttpMethod.Patch).Body, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Get, ok.Calls.Last().Method);
        }
    }

    /// <summary>A wellbore DDMS that takes one session, and describes the log it committed when given a description.</summary>
    private static FakeHttpHandler SessionHandler(string sessionId, string? describe)
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, $$"""{"id":"{{sessionId}}"}""")
            .On(HttpMethod.Post, $"/sessions/{sessionId}/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, $"/sessions/{sessionId}", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        return describe is null ? handler : handler.On(HttpMethod.Get, "/data", HttpStatusCode.OK, describe);
    }

    [Fact]
    public async Task Chunks_that_restart_their_row_numbers_hold_the_record_before_anything_is_sent()
    {
        // Seen live on an M26 service: chunks of five and four rows that both numbered their rows from zero committed a
        // log of five rows, because a session aggregates its chunks by row label, and the commit reported nothing wrong.
        var handler = new FakeHttpHandler();
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var held = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(true, true, 2, source: new MemoryPayload(2, rowsPerChunk: 5, labels: ChunkLabels.Restarting))));

            Assert.Contains(
                "payload chunks 0 (chunk_0.parquet: no row index, so a reader numbers its rows 0 to 4) and 1 (chunk_1.parquet: no row index, so a reader numbers its rows 0 to 4)",
                held.Message, StringComparison.Ordinal);
            Assert.Contains("silently lose rows", held.Message, StringComparison.Ordinal);

            // Held before the metadata write, so the log is never left with a payload that lost rows.
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task Chunks_whose_stored_index_overlaps_hold_the_record()
    {
        var handler = new FakeHttpHandler();
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var held = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(false, true, 2, source: new MemoryPayload(2, rowsPerChunk: 3, labels: ChunkLabels.OverlappingColumn))));

            Assert.Contains("(chunk_0.parquet: a stored index from 0 to 2) and 1 (chunk_1.parquet: a stored index from 2 to 4)", held.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task Chunks_that_split_a_logs_curves_share_one_index_and_go_into_one_session()
    {
        // A wellbore with more curves than the column ceiling splits its curves across chunks, each with the same rows.
        var handler = SessionHandler("sess-s", """{"numberOfRows":4,"columns":["CURVE_0","CURVE_1","MD"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 2, source: new MemoryPayload(2, rowsPerChunk: 4, labels: ChunkLabels.CurvesSplit)));

            Assert.Equal(2, outcome.ChunksSent);
            Assert.Equal("4", outcome.Returned["rows"]);
        }
    }

    [Fact]
    public async Task A_committed_log_holding_fewer_rows_than_its_chunks_carried_holds_the_record_naming_both_counts()
    {
        // The footers cannot show every collision (labels repeated inside one chunk, a multi-level index), so what the
        // log holds after the commit is read back.
        var handler = SessionHandler("sess-f", """{"numberOfRows":5,"columns":["CURVE_1","MD"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var held = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(false, true, 3, source: new MemoryPayload(3, rowsPerChunk: 3))));

            Assert.Contains(
                "session sess-f for dev:work-product-component--WellLog:abc was committed, but the log holds 5 rows where its chunks carried 9",
                held.Message, StringComparison.Ordinal);
            Assert.Contains("release the record to send them again", held.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_committed_log_missing_a_curve_its_chunks_carried_holds_the_record()
    {
        var handler = SessionHandler("sess-m", """{"numberOfRows":4,"columns":["CURVE_0","MD"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var held = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(false, true, 2, source: new MemoryPayload(2, rowsPerChunk: 4, labels: ChunkLabels.CurvesSplit))));

            Assert.Contains("the log lacks the curves CURVE_1 that its chunks carried", held.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_whole_committed_log_reports_its_rows_and_a_target_that_cannot_describe_it_leaves_the_delivery_unchecked()
    {
        var described = SessionHandler("sess-w", """{"numberOfRows":9,"columns":["CURVE_1","MD"]}""");
        var (client, _, runtime) = Client(described);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 3, source: new MemoryPayload(3, rowsPerChunk: 3)));

            Assert.Equal("9", outcome.Returned["rows"]);
            Assert.Contains(described.Calls, c => c.Method == HttpMethod.Get && c.Uri.Query.Contains("describe=true", StringComparison.Ordinal));
        }

        // A facade without the describe query answers 404: the delivery stands, unchecked, rather than failing a log that landed.
        var silent = SessionHandler("sess-u", describe: null);
        var (client2, _, runtime2) = Client(silent);
        using (runtime2)
        {
            var protocol = new OsduDdmsProtocol(client2, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 3, source: new MemoryPayload(3, rowsPerChunk: 3)));

            Assert.Equal(3, outcome.ChunksSent);
            Assert.False(outcome.Returned.ContainsKey("rows"));
        }
    }

    [Fact]
    public async Task WellLog_holds_a_chunk_above_the_wellbore_ddms_bulk_ceilings_before_writing_metadata()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:1"]}""")
            .On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            // 40 rows by 4 columns is 160 values, so a ceiling of 100 values holds it.
            var values = new OsduDdmsProtocol(client, new ProtocolOptions { MaxChunkValues = 100 }, Samples.Logger<OsduDdmsProtocol>());
            var tooManyValues = await Assert.ThrowsAsync<RecordHeldException>(
                () => values.DeliverAsync(Work(true, true, 1, source: new MemoryPayload(1, columns: 4, rowsPerChunk: 40))));
            Assert.Contains("160 values (40 rows by 4 columns)", tooManyValues.Message, StringComparison.Ordinal);
            Assert.Contains("maxChunkValues", tooManyValues.Message, StringComparison.Ordinal);

            var columns = new OsduDdmsProtocol(client, new ProtocolOptions { MaxChunkColumns = 3 }, Samples.Logger<OsduDdmsProtocol>());
            var tooManyColumns = await Assert.ThrowsAsync<RecordHeldException>(
                () => columns.DeliverAsync(Work(true, true, 1, source: new MemoryPayload(1, columns: 4, rowsPerChunk: 2))));
            Assert.Contains("has 4 columns", tooManyColumns.Message, StringComparison.Ordinal);
            Assert.Contains("maxChunkColumns", tooManyColumns.Message, StringComparison.Ordinal);

            // The record is held before the metadata write, so a held payload never leaves a record without one.
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task WellLog_checks_the_shape_only_for_parquet_payloads_and_only_when_a_ceiling_is_in_force()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":1700001}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            // A payload the target takes as JSON is not measurable from a parquet footer, so it is not measured.
            var json = new ProtocolOptions { PayloadContentType = "application/json", MaxChunkValues = 1, MaxChunkColumns = 1 };
            var outcome = await new OsduDdmsProtocol(client, json, Samples.Logger<OsduDdmsProtocol>()).DeliverAsync(Work(false, true, 1));
            Assert.Equal(1, outcome.ChunksSent);

            // Both ceilings off is the explicit opt out for a target that has raised them.
            var off = new ProtocolOptions { MaxChunkValues = 0, MaxChunkColumns = 0 };
            var second = await new OsduDdmsProtocol(client, off, Samples.Logger<OsduDdmsProtocol>())
                .DeliverAsync(Work(false, true, 1, source: new MemoryPayload(1, columns: 8, rowsPerChunk: 8)));
            Assert.Equal(1, second.ChunksSent);
        }
    }

    [Fact]
    public async Task A_bulk_write_whose_record_cannot_be_read_back_fails_naming_the_record()
    {
        // The bulk landed but the version OSDU serves is unknown. Recording the metadata write's version would be a
        // ledger that disagrees with OSDU, so the try fails; the retry resumes past the metadata write and sends the
        // whole bulk again, which replaces rather than adds.
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => protocol.DeliverAsync(Work(false, true, 1)));

            Assert.Contains("dev:work-product-component--WellLog:abc", ex.Message, StringComparison.Ordinal);
            Assert.Contains("could not be read back", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WellLog_holds_a_log_whose_reference_curve_is_not_one_of_its_curves_before_writing_it()
    {
        // A live wellbore DDMS refused exactly this with HTTP 400; the OpenAPI description does not state the rule.
        var handler = new FakeHttpHandler();
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(true, true, 1,
                document: """{"id":"dev:work-product-component--WellLog:abc","kind":"k","data":{"ReferenceCurveID":"MD","Curves":[{"CurveID":"GR"},{"CurveID":"RHOB"}]}}""")));

            Assert.Contains("data.ReferenceCurveID is 'MD'", held.Message, StringComparison.Ordinal);
            Assert.Contains("describes only GR, RHOB", held.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }

        // A reference curve that is one of the log's curves, or none named at all, is not the protocol's concern.
        Assert.Null(WellboreDdmsRules.RecordProblem(WellboreDdmsRules.WellLog, TestSchema.Doc("""{"data":{"ReferenceCurveID":"MD","Curves":[{"CurveID":"MD"},{"CurveID":"GR"}]}}"""), withBulk: false));
        Assert.Null(WellboreDdmsRules.RecordProblem(WellboreDdmsRules.WellLog, TestSchema.Doc("""{"data":{"Curves":[{"CurveID":"GR"}]}}"""), withBulk: false));
        Assert.Contains("describes no curve", WellboreDdmsRules.RecordProblem(WellboreDdmsRules.WellLog, TestSchema.Doc("""{"data":{"ReferenceCurveID":"MD"}}"""), withBulk: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WellLog_holds_a_chunk_that_is_declared_parquet_but_is_not()
    {
        var (client, handler, runtime) = Client();
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var ex = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(true, true, 1, source: new NotParquetPayload())));
            Assert.Contains("declared as parquet but its footer could not be read", ex.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public void Bulk_limits_carry_the_documented_wellbore_ddms_numbers()
    {
        Assert.Equal(10_000_000, WellboreDdmsBulkLimits.MaxChunkValues);
        Assert.Equal(3_000, WellboreDdmsBulkLimits.MaxChunkColumns);
        Assert.Equal(500, WellboreDdmsBulkLimits.MaxChunkColumnsThroughM25);
        Assert.Equal(WellboreDdmsBulkLimits.MaxChunkValues, new ProtocolOptions().MaxChunkValues);
        Assert.Equal(WellboreDdmsBulkLimits.MaxChunkColumns, new ProtocolOptions().MaxChunkColumns);

        // A shape no file can hold saturates instead of overflowing into a value that would pass the check.
        Assert.Equal(long.MaxValue, WellboreDdmsBulkLimits.Values(long.MaxValue, 2));
        Assert.Equal(0, WellboreDdmsBulkLimits.Values(10, 0));
        Assert.Null(WellboreDdmsBulkLimits.Exceeded(0, "chunk_00000.parquet", 1000, 10, 10_000, 3_000));
        Assert.Null(WellboreDdmsBulkLimits.Exceeded(0, "chunk_00000.parquet", long.MaxValue, 4_000, 0, 0));
    }

    [Fact]
    public async Task Record_protocol_puts_arrays_preserves_keys_and_verifies_versions()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/records/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"x","version":7,"data":{"Datasets":["ds1"],"Name":"old"}}""")
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:8"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions { PreserveDataKeys = ["Datasets"] });
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 7));
            Assert.Equal(8, outcome.TargetVersion);
            var put = handler.Calls.Single(c => c.Method == HttpMethod.Put);
            Assert.Contains("\"Datasets\":[\"ds1\"]", put.Body, StringComparison.Ordinal);
            Assert.Contains("\"Name\":\"n\"", put.Body, StringComparison.Ordinal);

            var verify = await protocol.VerifyAsync("dev:work-product-component--WellLog:abc", 8);
            Assert.Equal(VerifyOutcome.Drifted, verify.Outcome);
            Assert.Equal(7, verify.ObservedVersion);
            var match = await protocol.VerifyAsync("dev:work-product-component--WellLog:abc", 7);
            Assert.Equal(VerifyOutcome.Match, match.Outcome);
        }

        var missing = new FakeHttpHandler().On(HttpMethod.Get, "/records/gone", HttpStatusCode.NotFound, null);
        var (client2, _, runtime2) = Client(missing);
        using (runtime2)
        {
            var protocol = new OsduRecordProtocol(client2, new ProtocolOptions());
            Assert.Equal(VerifyOutcome.Missing, (await protocol.VerifyAsync("gone", 1)).Outcome);
        }
    }

    [Fact]
    public async Task Record_protocol_lists_a_records_versions_and_reads_one_of_them()
    {
        const string id = "dev:work-product-component--WellLog:abc";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/records/versions/" + id, HttpStatusCode.OK, """{"recordId":"dev:work-product-component--WellLog:abc","versions":[7,9,8]}""")
            .On(HttpMethod.Get, "/records/versions/gone", HttpStatusCode.NotFound, null)
            .On(HttpMethod.Get, "/records/" + id + "/7", HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:abc","version":7,"data":{"Name":"old"}}""")
            .On(HttpMethod.Get, "/records/" + id + "/6", HttpStatusCode.NotFound, null);
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());

            // Newest first, whatever order the service lists them in; a record the service does not hold has none.
            Assert.Equal(new long[] { 9, 8, 7 }, await protocol.VersionsAsync(id));
            Assert.Empty((await protocol.VersionsAsync("gone"))!);

            // A version reads the record as it was then; a version the service never gave is nothing, not a failure.
            Assert.Equal("old", (await protocol.ReadVersionAsync(id, 7))!["data"]!["Name"]!.GetValue<string>());
            Assert.Null(await protocol.ReadVersionAsync(id, 6));
            Assert.EndsWith("/api/storage/v2/records/versions/" + id, handler.Calls.First(c => c.Uri.AbsolutePath.Contains("versions", StringComparison.Ordinal)).Uri.AbsolutePath, StringComparison.Ordinal);

            // The list sits beside the storage record path only: a flow that reads its records elsewhere has no history to ask for.
            var elsewhere = new OsduRecordProtocol(client, new ProtocolOptions { VerifyPath = "/api/custom/{id}" });
            Assert.Null(await elsewhere.VersionsAsync(id));
        }

        // A protocol whose target keeps no version list says so, and refuses a read at a version rather than guessing.
        var (client2, _, runtime2) = Client(new FakeHttpHandler());
        using (runtime2)
        {
            IDeliveryProtocol ddms = new OsduDdmsProtocol(client2, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            Assert.Null(await ddms.VersionsAsync(id));
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => ddms.ReadVersionAsync(id, 7));
            Assert.Contains("keeps no version history", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_record_write_asks_storage_to_skip_duplicates_only_when_the_flow_opts_in()
    {
        // Opted in, skipdupes is sent. A record the service names under skippedRecordIds has no version in the answer, so
        // the route reads the version storage holds, which another writer may have moved since the ledger knew it. By
        // default it is not sent: the spec does not say what the service compares, and an envelope-only change must never
        // be skipped while the ledger records it as delivered.
        const string skippedAnswer = """{"recordIdVersions":[],"skippedRecordIds":["dev:work-product-component--WellLog:abc"]}""";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, skippedAnswer)
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":6}],"invalidRecords":[],"retryRecords":[]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions { SkipDuplicates = true });
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 4));

            Assert.True(outcome.MetadataDelivered);
            Assert.Equal(6, outcome.TargetVersion);
            Assert.Equal("true", outcome.Returned["skipped"]);
            Assert.Equal("unchanged at the target", outcome.Detail);
            Assert.Contains("skipdupes=true", Assert.Single(handler.Calls, c => c.Method == HttpMethod.Put).Uri.Query, StringComparison.Ordinal);
            Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post);
        }

        // A read that fails keeps the version the ledger knew, and the outcome says so.
        var unread = new FakeHttpHandler()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, skippedAnswer)
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.InternalServerError, """{"code":500,"reason":"Server error"}""");
        var (client3, _, runtime3) = Client(unread);
        using (runtime3)
        {
            var outcome = await new OsduRecordProtocol(client3, new ProtocolOptions { SkipDuplicates = true }).DeliverAsync(Work(true, false, 0, existing: 4));
            Assert.Equal(4, outcome.TargetVersion);
            Assert.Contains("unchanged at the target; the version OSDU holds could not be read (", outcome.Detail, StringComparison.Ordinal);
            Assert.EndsWith("so the version the ledger knew stands", outcome.Detail, StringComparison.Ordinal);
        }

        var plain = new FakeHttpHandler()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:9"]}""");
        var (client2, _, runtime2) = Client(plain);
        using (runtime2)
        {
            var protocol = new OsduRecordProtocol(client2, new ProtocolOptions());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 4));

            Assert.Equal(9, outcome.TargetVersion);
            Assert.DoesNotContain("skipdupes", plain.Calls.Single().Uri.Query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Verifying_many_records_is_one_batched_read_that_separates_a_match_from_drift_and_absence()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/query/records",
            HttpStatusCode.OK,
            """{"records":[{"id":"dev:x:match","version":3},{"id":"dev:x:drifted","version":9}],"invalidRecords":["dev:x:listed"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            IDeliveryProtocol protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            Assert.Equal(OsduRecordProtocol.MaxVerifyBatch, protocol.MaxVerifyBatch);

            var results = await protocol.VerifyBatchAsync(
            [
                new VerifyRequest("dev:x:match", 3),
                new VerifyRequest("dev:x:drifted", 3),
                new VerifyRequest("dev:x:listed", 3),
                new VerifyRequest("dev:x:absent", 3),
                new VerifyRequest("dev:x:adopted", null),
            ]);

            Assert.Equal(VerifyOutcome.Match, results[0].Outcome);
            Assert.Equal(3, results[0].ObservedVersion);
            Assert.Equal(VerifyOutcome.Drifted, results[1].Outcome);
            Assert.Equal(9, results[1].ObservedVersion);
            Assert.Contains("observed version 9", results[1].Detail, StringComparison.Ordinal);
            // Storage names a record it does not hold under invalidRecords: that is an absence like any other.
            Assert.Equal(VerifyOutcome.Missing, results[2].Outcome);
            Assert.Contains("invalidRecords", results[2].Detail, StringComparison.Ordinal);
            Assert.Equal(VerifyOutcome.Missing, results[3].Outcome);
            Assert.Equal(VerifyOutcome.Missing, results[4].Outcome);

            // Five records, one request: this is what keeps a drift pass over a large estate off one call per record.
            var call = Assert.Single(handler.Calls);
            var body = JsonNode.Parse(call.Body!)!.AsObject();
            Assert.Equal(5, body["records"]!.AsArray().Count);
            Assert.Equal("id", body["attributes"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_401_is_retried_once_under_a_freshly_resolved_token()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Put,
            "/records",
            hit => hit == 0
                ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized"}""")
                : FakeHttpHandler.Json(HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:2"]}"""));
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0));

            Assert.Equal(2, outcome.TargetVersion);
            Assert.Equal(2, handler.Calls.Count);
        }

        // A 401 that survives the fresh token is a real authorisation failure and is not tried a third time.
        var refusing = new FakeHttpHandler().On(HttpMethod.Put, "/records", HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized"}""");
        var (client2, _, runtime2) = Client(refusing);
        using (runtime2)
        {
            var protocol = new OsduRecordProtocol(client2, new ProtocolOptions());
            var ex = await Assert.ThrowsAsync<OsduStatusException>(() => protocol.DeliverAsync(Work(true, false, 0)));

            Assert.Equal(401, ex.StatusCode);
            Assert.Equal(2, refusing.Calls.Count);
        }
    }

    /// <summary>A chunk the manifest calls parquet that is not one.</summary>
    private sealed class NotParquetPayload : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "mem://chunk_0.parquet", 7)]);

        public Task<Stream> OpenAsync(PayloadFile chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("chunk-0")));
    }

    /// <summary>How a test chunk labels its rows, as a dataframe writer would.</summary>
    private enum ChunkLabels
    {
        /// <summary>A pandas RangeIndex that continues from the previous chunk: what a correct prepare writes.</summary>
        Continuing,

        /// <summary>No pandas metadata, so every chunk numbers its rows from zero.</summary>
        Restarting,

        /// <summary>A stored index column whose labels overlap the previous chunk's.</summary>
        OverlappingColumn,

        /// <summary>The same RangeIndex in every chunk, with one curve each: a log whose curves were split.</summary>
        CurvesSplit,
    }

    /// <summary>
    /// Real parquet chunks, because the protocol reads each chunk's footer to check it against the wellbore DDMS
    /// bulk ceilings, and a session's chunks against each other, before sending them. Each chunk carries values from
    /// its own chunk index so the requests stay distinct.
    /// </summary>
    private sealed class MemoryPayload(int chunks, int columns = 2, int rowsPerChunk = 1, ChunkLabels labels = ChunkLabels.Continuing) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>(
                Enumerable.Range(0, chunks).Select(i => new PayloadFile(i, $"mem://chunk_{i}.parquet", Bytes(i).Length)).ToList());

        public Task<Stream> OpenAsync(PayloadFile chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(Bytes(chunk.Index), writable: false));

        private byte[] Bytes(int index)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var names = new List<(string Name, Type ClrType)> { ("MD", typeof(double)) };
            if (labels == ChunkLabels.CurvesSplit)
            {
                names.Add(("CURVE_" + index.ToString(culture), typeof(double)));
            }
            else
            {
                for (var c = 1; c < columns; c++)
                {
                    names.Add(("CURVE_" + c.ToString(culture), typeof(double)));
                }
            }

            var stored = labels == ChunkLabels.OverlappingColumn;
            var rows = new List<IReadOnlyDictionary<string, object?>>(rowsPerChunk);
            for (var r = 0; r < rowsPerChunk; r++)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (name, _) in names)
                {
                    row[name] = labels == ChunkLabels.CurvesSplit ? (double)r : (double)((index * rowsPerChunk) + r);
                }

                if (stored)
                {
                    // Each chunk starts one label before the previous chunk ended.
                    row["__index_level_0__"] = (long)((index * Math.Max(rowsPerChunk - 1, 0)) + r);
                }

                rows.Add(row);
            }

            if (stored)
            {
                names.Add(("__index_level_0__", typeof(long)));
            }

            var metadata = labels switch
            {
                ChunkLabels.Continuing => PandasMetadata.Range(names, index * rowsPerChunk, (index + 1) * rowsPerChunk),
                ChunkLabels.CurvesSplit => PandasMetadata.Range(names, 0, rowsPerChunk),
                ChunkLabels.OverlappingColumn => PandasMetadata.Stored(names, "__index_level_0__"),
                _ => null,
            };

            using var buffer = new MemoryStream();
            SqlFlow.Delivery.Storage.ParquetFiles.WriteAsync(buffer, names, rows, metadata).GetAwaiter().GetResult();
            return buffer.ToArray();
        }
    }
}

/// <summary>How a deliver run decides whether it re-plans a drop, and what a record-scoped run sends again.</summary>
[Trait("Category", "Integration")]
public class DeliverRunScopeTests
{
    [Fact]
    public void A_run_scoped_to_records_replans_past_the_whole_run_gates()
    {
        Assert.True(DeliveryExecutor.ForcesReplan(new DeliveryRunPayload { RecordKeys = [Guid.NewGuid()] }, reRunningSubmission: false));
        Assert.True(DeliveryExecutor.ForcesReplan(new DeliveryRunPayload { Force = true }, reRunningSubmission: false));
        Assert.True(DeliveryExecutor.ForcesReplan(DeliveryRunPayload.None, reRunningSubmission: true));
        Assert.False(DeliveryExecutor.ForcesReplan(DeliveryRunPayload.None, reRunningSubmission: false));
        Assert.True(DeliveryExecutor.ForcesReplan(new DeliveryRunPayload { Rerender = true }, reRunningSubmission: false));
    }

    [Fact]
    public void Bringing_records_up_to_date_is_a_deliver_run_or_a_plan_of_one_and_never_a_forced_send()
    {
        // A deliver run brings named records, or every delivered one, up to date; a plan says what that would send.
        var named = DeliveryRunPayload.Parse("""{"rerender":true,"recordKeys":["11111111-1111-1111-1111-111111111111"]}""");
        named.Validate(DeliveryOperations.Deliver);
        named.Validate(DeliveryOperations.Plan);
        Assert.True(named.Rerender);
        Assert.False(named.IsEmpty);
        Assert.Equal("""{"recordKeys":["11111111-1111-1111-1111-111111111111"],"rerender":true}""", named.ToJson());
        Assert.Equal(named.Rerender, DeliveryRunPayload.Parse(named.ToJson()).Rerender);
        DeliveryRunPayload.Parse("""{"rerender":true}""").Validate(DeliveryOperations.Deliver);
        Assert.False(DeliveryRunPayload.Parse("""{"rerender":false}""").Rerender);

        // It decides by the hashes, so it never goes with a forced send, nor with a submission's run.
        Assert.Contains(
            "rerender sends what renders differently and redeliver sends whatever the hashes say",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { Rerender = true, Redeliver = RedeliverScopes.All }.Validate(DeliveryOperations.Deliver)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "a run on a submission delivers what that submission planned",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { Rerender = true, SubmissionId = Guid.NewGuid() }.Validate(DeliveryOperations.Deliver)).Message,
            StringComparison.Ordinal);

        // The operations that send nothing, or read rows rather than delivered records, refuse it by name.
        foreach (var operation in new[] { DeliveryOperations.Intake, DeliveryOperations.Drain, DeliveryOperations.Verify, DeliveryOperations.Sync, DeliveryOperations.Replan })
        {
            Assert.Contains(
                $"payload {DeliveryRunPayload.RerenderProperty} does not apply to the {operation} operation",
                Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { Rerender = true }.Validate(operation)).Message,
                StringComparison.Ordinal);
        }

        Assert.Contains("payload rerender must be true or false", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"rerender":"yes"}""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_scoped_run_redelivers_the_part_it_names_on_its_route_and_everything_by_default()
    {
        var key = Guid.NewGuid();
        var loader = new DeliveryDocumentLoader();
        var logs = loader.LoadFlow(Samples.Flow);
        var wellbores = loader.LoadFlow(Samples.WellboreFlowFile);
        var files = logs with { Target = logs.Target with { Protocol = DeliveryProtocol.File } };
        RedeliverScope Of(FlowDefinition flow, string? part) => DeliveryExecutor.RedeliverScopeOf(new DeliveryRunPayload { RecordKeys = [key], Redeliver = part }, flow).Scope;

        // The ddms route sends the record and its bulk data.
        Assert.Equal(RedeliverScope.All, Of(logs, null));
        Assert.Equal(RedeliverScope.Payload, Of(logs, RedeliverScopes.Bulk));
        Assert.Equal(RedeliverScope.Payload, Of(logs, RedeliverScopes.Payload));
        Assert.Equal(RedeliverScope.Metadata, Of(logs, "Record"));
        Assert.Equal(RedeliverScope.Metadata, Of(logs, "Metadata"));
        Assert.Equal(
            "'welldb-welllog-03-header-delivery' is delivered by the ddms route, which sends the record and its bulk data, so a redelivery of 'files' has nothing to send; name one of all, record, bulk, metadata, payload.",
            Assert.Throws<DeliveryException>(() => Of(logs, RedeliverScopes.Files)).Message);

        // The file route sends the record and its files.
        Assert.Equal(RedeliverScope.Payload, Of(files, RedeliverScopes.Files));
        Assert.Contains("a redelivery of 'bulk' has nothing to send", Assert.Throws<DeliveryException>(() => Of(files, RedeliverScopes.Bulk)).Message, StringComparison.Ordinal);

        // The storage route sends the record alone.
        Assert.Equal(RedeliverScope.Metadata, Of(wellbores, RedeliverScopes.Record));
        Assert.Equal(RedeliverScope.All, Of(wellbores, RedeliverScopes.All));
        Assert.Contains(
            "'wells-wellbore-03-header-delivery' is delivered by the storage route, which sends the record alone, so a redelivery of 'payload' has nothing to send; name one of all, record, metadata.",
            Assert.Throws<DeliveryException>(() => Of(wellbores, RedeliverScopes.Payload)).Message,
            StringComparison.Ordinal);

        Assert.Contains("redeliver 'everything' is not one of all, record, files, bulk, workflow, metadata, payload", Assert.Throws<DeliveryException>(() => Of(logs, "everything")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undo_run_takes_everything_unfinished_deliveries_left_and_force_alone()
    {
        // The sweep of the whole ledger, and with force the undos that have used their tries too.
        DeliveryRunPayload.None.Validate(DeliveryOperations.Undo);
        new DeliveryRunPayload { Force = true }.Validate(DeliveryOperations.Undo);

        string Refused(DeliveryRunPayload payload) => Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Undo)).Message;
        Assert.Contains("not a submission's", Refused(new DeliveryRunPayload { SubmissionId = Guid.NewGuid() }), StringComparison.Ordinal);
        Assert.Contains("every unfinished delivery of the ledger left", Refused(new DeliveryRunPayload { RecordKeys = [Guid.NewGuid()] }), StringComparison.Ordinal);
        Assert.Contains("an undo sends nothing the flow renders", Refused(new DeliveryRunPayload { Redeliver = RedeliverScopes.All }), StringComparison.Ordinal);
        Assert.Contains("an undo renders nothing", Refused(new DeliveryRunPayload { Rerender = true }), StringComparison.Ordinal);
        Assert.Contains("only an intake member plans slices", Refused(new DeliveryRunPayload { Slices = [0] }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_a_run_carries_is_checked_against_the_operation_it_travels_with()
    {
        // The kind validates its own arguments at every trust boundary, so a member's payload can never ask a drain to
        // plan slices, or a replan to name the records it reads.
        var slices = new DeliveryRunPayload { SubmissionId = Guid.NewGuid(), Slices = [0, 1] };
        slices.Validate(DeliveryOperations.Intake);
        Assert.Contains(
            "only an intake member plans slices",
            Assert.Throws<SqlFlowException>(() => slices.Validate(DeliveryOperations.Drain)).Message,
            StringComparison.Ordinal);

        var payload = DeliveryRunPayload.Parse("""{"force":true,"recordKeys":["11111111-1111-1111-1111-111111111111"],"redeliver":"payload"}""");
        payload.Validate(DeliveryOperations.Deliver);
        Assert.True(payload.Force);
        Assert.Equal(RedeliverScopes.Payload, payload.Redeliver);
        Assert.Contains(
            "a replan reads every row",
            Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Replan)).Message,
            StringComparison.Ordinal);
        Assert.Contains("is not one of", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"nothing":1}""")).Message, StringComparison.Ordinal);

        // Without keys a redelivery sends again every record the flow has delivered; a run on a submission delivers what
        // that submission planned, so it takes none.
        DeliveryRunPayload.Parse("""{"redeliver":"bulk"}""").Validate(DeliveryOperations.Deliver);
        Assert.Contains(
            "a run on a submission delivers what that submission planned",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { SubmissionId = Guid.NewGuid(), Redeliver = RedeliverScopes.All }.Validate(DeliveryOperations.Deliver)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "a plan sends nothing",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { Redeliver = RedeliverScopes.All }.Validate(DeliveryOperations.Plan)).Message,
            StringComparison.Ordinal);

        // A sync reads the rows of the ledger's records, all of them or the ones it names, and sends nothing.
        DeliveryRunPayload.Parse("""{"recordKeys":["11111111-1111-1111-1111-111111111111"],"interface":"wells"}""").Validate(DeliveryOperations.Sync);
        DeliveryRunPayload.None.Validate(DeliveryOperations.Sync);
        Assert.Contains(
            "a sync sends nothing",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { RecordKeys = [Guid.NewGuid()], Redeliver = RedeliverScopes.Payload }.Validate(DeliveryOperations.Sync)).Message,
            StringComparison.Ordinal);
        Assert.Contains("a sync passes no gate", Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Sync)).Message, StringComparison.Ordinal);
        Assert.Contains(
            "a sync reads the rows of the ledger's records",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { SubmissionId = Guid.NewGuid() }.Validate(DeliveryOperations.Sync)).Message,
            StringComparison.Ordinal);
        Assert.False(DeliveryExecutor.ReachesTarget(DeliveryOperations.Sync));
        Assert.True(DeliveryExecutor.ReachesTarget(DeliveryOperations.Verify));
    }
}

/// <summary>What a deliver run reports: its own work, and the submission it worked on alongside.</summary>
[Trait("Category", "Integration")]
public class DeliverOutcomeTests
{
    private const string Source = "OsduData.silver.WellLog";

    private static SubmissionState Submission(long planned, long delivered) => new()
    {
        SubmissionId = Guid.NewGuid(),
        FlowId = Guid.NewGuid(),
        FlowName = "welldb-welllog-03-header-delivery",
        MappingReference = "WellLog@1.4.0",
        RenderContext = "{}",
        SourceObject = Source,
        SourceConnection = "${env:OSDU_DATA_DB}",
        Status = SubmissionStatus.Completed,
        Planned = planned,
        Delivered = delivered,
        SkippedUnchanged = 1,
    };

    [Fact]
    public void A_run_reports_what_it_did_itself_not_the_submission_it_worked_on()
    {
        // Live, a run that found its submission completed reported "1 delivered", and one that re-sent two records
        // of a three-record submission reported three.
        var submission = Submission(planned: 3, delivered: 3);

        var idle = DeliverOutcome.From(
            new RunResult(new IntakeResult(submission, null, IntakeCounts.Empty, AlreadyProcessed: true), WorkerSummary.Empty, submission),
            DeliveryOperations.Deliver, Source, "incremental since 2026-09-01T06:00:00Z", RequestedPasses.None);
        Assert.Equal(0, idle.Planned);
        Assert.Equal(0, idle.Delivered);
        Assert.True(idle.NothingToDo);
        Assert.Equal(3, idle.Submission.Delivered);
        Assert.Equal(Source, idle.Source);

        var two = DeliverOutcome.From(
            new RunResult(new IntakeResult(submission, null, new IntakeCounts(3, 2, 1, 0, 0, 0, 1), AlreadyProcessed: false), new WorkerSummary(2, 2, 0, 1, 0, 1), submission),
            DeliveryOperations.Deliver, Source, "full", RequestedPasses.None);
        Assert.Equal(2, two.Planned);
        Assert.Equal(2, two.Delivered);
        Assert.Equal(1, two.SkippedUnchanged);
        Assert.Equal(1, two.Held);
        Assert.Equal(3, two.Submission.Planned);
    }

    [Fact]
    public void A_fan_out_root_reports_the_submission_its_members_worked_on()
    {
        var submission = Submission(planned: 5000, delivered: 4990);
        var root = DeliverOutcome.From(
            new RunResult(new IntakeResult(submission, null, new IntakeCounts(5000, 1250, 0, 0, 0, 0, 3), AlreadyProcessed: false), new WorkerSummary(40, 40, 0, 0, 0, 1), submission, IntakeMembers: 3, DrainMembers: 4),
            DeliveryOperations.Deliver, Source, "full", RequestedPasses.None);

        Assert.Equal(5000, root.Planned);
        Assert.Equal(4990, root.Delivered);
    }
}

/// <summary>The run boundary: a run ends as a recorded failure whatever stopped it.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryRunBoundaryTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();

    private sealed class UnbuildableProtocolFactory : IProtocolFactory
    {
        public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
            => throw new NotSupportedException("the protocol could not be built");
    }

    [Fact]
    public async Task A_failure_of_an_unexpected_kind_ends_the_run_as_a_recorded_failure()
    {
        // Live, an exception outside the kinds the executor listed crashed a CLI run, which then wrote no run.
        var root = Samples.NewTempDirectory();
        var tables = await SampleEstate.BuildAsync(root, new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc));
        var engine = Samples.Engine(_db.Ledger(), protocols: new UnbuildableProtocolFactory(), sources: tables);
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        var flow = Samples.InFolder(Samples.LocalFlow(root), root);

        var result = await new DeliveryExecutor(provider).ExecuteAsync(
            new DeliveryFlowDocument { Source = SourceDefinition.Of(flow) },
            flow.SourcePath!,
            new DocumentExecutionOptions { Parameters = new RunParameters { Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal) } },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("NotSupportedException: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("the protocol could not be built", result.Error, StringComparison.Ordinal);
        Assert.NotNull(result.RunDirectory);
        Assert.True(File.Exists(Path.Combine(result.RunDirectory!, "run.json")));
    }

    public void Dispose() => _db.Dispose();
}
