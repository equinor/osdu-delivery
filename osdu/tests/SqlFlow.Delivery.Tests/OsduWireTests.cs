using System.Net;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What goes on the wire to OSDU regardless of which call it is, ported from the OSDU C# client's
/// <c>JsonContentTypeHandlerTests</c>. Storage answers a request without a <c>Content-Type</c> with
/// <c>415 "Content-Type 'null' is not supported"</c>, even when the operation takes no body, so every request this
/// system sends carries one.
/// </summary>
public class OsduContentTypeTests
{
    private const string RecordId = "dev:work-product-component--WellLog:abc";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    [Theory]
    [InlineData(RemovalScope.Record)]
    [InlineData(RemovalScope.History)]
    [InlineData(RemovalScope.Everything)]
    public async Task Gives_every_bodiless_removal_a_json_content_type(RemovalScope scope)
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, ":abc", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            await new OsduRecordProtocol(client, new ProtocolOptions()).DeleteAsync(RecordId, scope);

            var call = Assert.Single(handler.Calls);
            Assert.Equal("application/json", call.ContentType);
        }
    }

    [Fact]
    public async Task The_added_body_is_empty()
    {
        // Semantically still no body: only the header the service insists on is added.
        var handler = new FakeHttpHandler().On(HttpMethod.Get, ":abc", HttpStatusCode.OK, """{"id":"x","version":1}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            await new OsduRecordProtocol(client, new ProtocolOptions()).VerifyAsync(RecordId, 1);

            var call = Assert.Single(handler.Calls);
            Assert.Equal("application/json", call.ContentType);
            Assert.Equal(string.Empty, call.Body);
        }
    }

    [Fact]
    public async Task Leaves_an_existing_body_alone()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[]}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            await new OsduRecordProtocol(client, new ProtocolOptions()).VerifyBatchAsync([new VerifyRequest(RecordId, 1)]);

            var call = Assert.Single(handler.Calls);
            Assert.Equal("application/json", call.ContentType);
            Assert.StartsWith("{\"attributes\":", call.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Does_not_override_a_different_content_type()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            await client.SendStreamAsync(
                HttpMethod.Post, client.Url("/ddms/v3/welllogs/{id}/data", RecordId),
                () => new MemoryStream(Encoding.UTF8.GetBytes("PAR1")), "application/x-parquet", 4, CancellationToken.None);

            Assert.Equal("application/x-parquet", Assert.Single(handler.Calls).ContentType);
        }
    }

    [Fact]
    public async Task The_capture_connection_sends_the_header_on_bodiless_calls_too()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/schema/osdu:wks:Thing:1.0.0", HttpStatusCode.OK, "{}");
        using var osdu = await OsduConnection.CreateAsync(
            "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
            new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), handler, allowLoopback: true);

        await osdu.GetJsonAsync("/api/schema-service/v1/schema/osdu:wks:Thing:1.0.0", CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal(string.Empty, call.Body);
    }
}

/// <summary>
/// The error an OSDU service sent, as the message a failed request carries into logs and the ledger. Ported in
/// spirit from the OSDU C# client's ExhaustedRetries_PreserveKiotaTypedErrors, which keeps AppError's message and
/// reason typed rather than as a raw body.
/// </summary>
public class OsduErrorTests
{
    [Fact]
    public void An_app_error_reads_as_its_message_and_reason()
    {
        Assert.Equal("busy (try later)", OsduError.Describe("""{"code":503,"message":"busy","reason":"try later"}"""));
        Assert.Equal("Invalid legal tags: dev-missing", OsduError.Describe("""{"code":400,"reason":"Bad Request","message":"Invalid legal tags: dev-missing"}""").Split(" (")[0]);
        Assert.Equal("Not Found", OsduError.Describe("""{"code":404,"reason":"Not Found"}"""));
        Assert.Equal("Record not found", OsduError.Describe("""{"code":404,"reason":"record not found","message":"Record not found"}"""));
    }

    [Fact]
    public void A_ddms_detail_reads_as_the_sentence_or_the_fields_it_names()
    {
        Assert.Equal("not found", OsduError.Describe("""{"detail":"not found"}"""));
        Assert.Equal(
            "state: value is not a valid enumeration member; mode: field required",
            OsduError.Describe("""{"detail":[{"loc":["body","state"],"msg":"value is not a valid enumeration member","type":"type_error.enum"},{"loc":["body","mode"],"msg":"field required","type":"value_error.missing"}]}"""));
    }

    [Fact]
    public void A_spring_problem_document_reads_as_its_title_and_detail()
    {
        // The 415 storage sends for a request without a Content-Type, quoted from the OSDU C# client's notes.
        Assert.Equal(
            "Unsupported Media Type: Content-Type 'null' is not supported.",
            OsduError.Describe("""{"title":"Unsupported Media Type","detail":"Content-Type 'null' is not supported.","status":415}"""));
    }

    [Fact]
    public void Anything_else_is_kept_as_a_bounded_single_line_preview()
    {
        Assert.Equal("<html><body>502 Bad Gateway</body></html>", OsduError.Describe("<html><body>502 Bad Gateway</body></html>"));
        Assert.Equal("{\"error\":\"bad acl\"}", OsduError.Describe("{\"error\":\"bad acl\"}"));
        Assert.Equal("(empty response body)", OsduError.Describe(string.Empty));
        Assert.Equal("line one line two", OsduError.Describe("line one\nline two"));

        var huge = OsduError.Describe(new string('x', 5000));
        Assert.Equal(OsduError.MaxLength + 3, huge.Length);
        Assert.EndsWith("...", huge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_request_surfaces_with_what_the_service_said()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Put, "/records", HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad Request","message":"Invalid legal tags: dev-missing"}""");
        using var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1 } }, new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);

        var ex = await Assert.ThrowsAsync<SqlFlow.Delivery.OsduStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, "http://localhost/records")));

        Assert.Equal(400, ex.StatusCode);
        Assert.EndsWith("Invalid legal tags: dev-missing (Bad Request)", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>The signed-URL upload's headers: what Azure Blob Storage requires, and nothing more anywhere else.</summary>
public class SignedUploadHeaderTests
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("https://osdulandingzone.blob.core.windows.net/staging/file?sv=2021&sig=x")]
    [InlineData("https://account.blob.core.usgovcloudapi.net/c/f?sig=x")]
    [InlineData("https://account.blob.core.chinacloudapi.cn/c/f?sig=x")]
    public void An_azure_blob_url_gets_the_block_blob_type(string url)
    {
        var headers = FileUploads.SignedUploadHeaders(new Uri(url), None);
        Assert.Equal("BlockBlob", headers[FileUploads.AzureBlobTypeHeader]);
    }

    [Theory]
    [InlineData("https://landing-zone.s3.eu-west-1.amazonaws.com/staging/file?X-Amz-Signature=x")]
    [InlineData("https://storage.googleapis.com/landing/file?X-Goog-Signature=x")]
    [InlineData("https://minio.internal.example.com/landing/file?sig=x")]
    public void Any_other_landing_zone_gets_only_what_the_flow_declared(string url)
    {
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-custom"] = "1" };
        var headers = FileUploads.SignedUploadHeaders(new Uri(url), declared);
        Assert.Same(declared, headers);
        Assert.False(headers.ContainsKey(FileUploads.AzureBlobTypeHeader));
    }

    [Fact]
    public void A_blob_type_the_flow_declares_is_kept()
    {
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-MS-Blob-Type"] = "AppendBlob" };
        var headers = FileUploads.SignedUploadHeaders(new Uri("https://account.blob.core.windows.net/c/f?sig=x"), declared);
        Assert.Equal("AppendBlob", headers["x-ms-blob-type"]);
        Assert.Single(headers);
    }
}

/// <summary>
/// The legal tag check against the legal service's validate endpoint (openapi legal v1,
/// POST /legaltags:validate), with the batching, caching and 404 semantics that endpoint actually has.
/// </summary>
public class LegalTagValidatorTests
{
    private const string ValidatePath = "/api/legal/v1/legaltags:validate";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler, TestClock clock)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), clock, handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    private static List<string> NamesIn(FakeHttpHandler.Request call)
        => System.Text.Json.Nodes.JsonNode.Parse(call.Body!)!["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public async Task Valid_tags_come_back_empty_after_one_request()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.OK, """{"invalidLegalTags":[]}""");
        var clock = new TestClock();
        var (client, runtime) = Client(handler, clock);
        using (runtime)
        {
            var invalid = await new LegalTagValidator(client, time: clock).InvalidAsync(["dev-public", "dev-public", " ", "dev-private"]);

            Assert.Empty(invalid);
            var call = Assert.Single(handler.Calls);
            Assert.Equal(["dev-public", "dev-private"], NamesIn(call));
            Assert.Equal("dev", call.Headers["data-partition-id"]);
        }
    }

    [Fact]
    public async Task Refused_tags_come_back_with_the_reason_the_service_gives()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.OK,
            """{"invalidLegalTags":[{"name":"dev-expired","reason":"LegalTag has expired"},{"name":"dev-noreason"}]}""");
        var clock = new TestClock();
        var (client, runtime) = Client(handler, clock);
        using (runtime)
        {
            var invalid = await new LegalTagValidator(client, time: clock).InvalidAsync(["dev-public", "dev-expired", "dev-noreason"]);

            Assert.Equal(2, invalid.Count);
            Assert.Equal("LegalTag has expired", invalid["dev-expired"]);
            Assert.Contains("invalid", invalid["dev-noreason"], StringComparison.Ordinal);
            Assert.False(invalid.ContainsKey("dev-public"));
        }
    }

    [Fact]
    public async Task More_names_than_one_request_takes_are_asked_in_batches_of_25()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.OK, """{"invalidLegalTags":[]}""");
        var clock = new TestClock();
        var (client, runtime) = Client(handler, clock);
        using (runtime)
        {
            var names = Enumerable.Range(0, 30).Select(i => "dev-tag-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            await new LegalTagValidator(client, time: clock).InvalidAsync(names);

            Assert.Equal(2, handler.Calls.Count);
            Assert.Equal(25, NamesIn(handler.Calls[0]).Count);
            Assert.Equal(5, NamesIn(handler.Calls[1]).Count);
        }
    }

    [Fact]
    public async Task A_verdict_is_trusted_for_a_while_and_asked_again_after()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.OK, """{"invalidLegalTags":[]}""");
        var clock = new TestClock();
        var (client, runtime) = Client(handler, clock);
        using (runtime)
        {
            var validator = new LegalTagValidator(client, time: clock);
            await validator.InvalidAsync(["dev-public"]);
            clock.Advance(TimeSpan.FromMinutes(9));
            await validator.InvalidAsync(["dev-public"]);
            Assert.Single(handler.Calls);

            clock.Advance(TimeSpan.FromMinutes(2));
            await validator.InvalidAsync(["dev-public"]);
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_404_for_several_names_is_attributed_by_asking_about_each()
    {
        // The service answers 404 "LegalTag names were not found" without saying which names it means.
        var handler = new FakeHttpHandler().OnMatch(
            r => r.RequestUri!.AbsolutePath.EndsWith(ValidatePath, StringComparison.Ordinal),
            _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"code":404,"reason":"Not Found","message":"LegalTag names were not found"}"""));
        var clock = new TestClock();
        var (client, runtime) = Client(handler, clock);
        using (runtime)
        {
            var invalid = await new LegalTagValidator(client, time: clock).InvalidAsync(["dev-a", "dev-b"]);

            Assert.Equal(2, invalid.Count);
            Assert.Contains("does not know", invalid["dev-a"], StringComparison.Ordinal);
            Assert.Equal(3, handler.Calls.Count);
            Assert.Equal(["dev-a"], NamesIn(handler.Calls[1]));
            Assert.Equal(["dev-b"], NamesIn(handler.Calls[2]));
        }
    }

    [Fact]
    public async Task A_404_that_is_not_the_legal_service_speaking_is_a_failure_to_reach_it_not_a_verdict()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.NotFound, null);
        var clock = new TestClock();
        var (client, runtime) = Client(handler, clock);
        using (runtime)
        {
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => new LegalTagValidator(client, time: clock).InvalidAsync(["dev-public"]));
            Assert.Contains("not reachable", ex.Message, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// A well log flow pointed at the OSDU platform root: the wellbore DDMS under /api/os-wellbore-ddms, as the platform
/// routes it and the OSDU C# client's WellboreDdmsBulkClientTests address it, and storage under its own prefix.
/// </summary>
public class WellboreDdmsRootTests
{
    private const string RecordId = "dev:work-product-component--WellLog:abc";

    private static readonly ProtocolOptions PlatformRoot = new() { DdmsRoot = "/api/os-wellbore-ddms" };

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    [Fact]
    public async Task Reads_probes_and_removals_go_under_the_ddms_root()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/api/os-wellbore-ddms/ddms/v3/welllogs/" + RecordId, HttpStatusCode.OK, """{"id":"x","version":4}""")
            .On(HttpMethod.Get, "/api/os-wellbore-ddms/about", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Delete, "/api/os-wellbore-ddms/ddms/v3/welllogs/" + RecordId, HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, PlatformRoot, Samples.Logger<OsduDdmsProtocol>());

            Assert.Equal(SqlFlow.Delivery.Ledger.VerifyOutcome.Match, (await protocol.VerifyAsync(RecordId, 4)).Outcome);
            Assert.True((await protocol.ProbeAsync()).Reachable);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Record)).Deleted);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Everything)).Deleted);

            Assert.Equal(
                ["/api/os-wellbore-ddms/ddms/v3/welllogs/" + RecordId, "/api/os-wellbore-ddms/about", "/api/os-wellbore-ddms/ddms/v3/welllogs/" + RecordId, "/api/os-wellbore-ddms/ddms/v3/welllogs/" + RecordId],
                handler.Calls.Select(c => c.Uri.AbsolutePath));
            Assert.Equal("?purge=true", handler.Calls[3].Uri.Query);
        }
    }

    [Fact]
    public async Task A_record_no_ddms_serves_is_read_with_its_versions_from_storage_under_the_platform_root()
    {
        const string Linked = "dev:reference-data--WellLogSamplingDomainType:Depth";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/api/storage/v2/records/" + Linked, HttpStatusCode.OK, """{"id":"dev:reference-data--WellLogSamplingDomainType:Depth","version":7}""")
            .On(HttpMethod.Get, "/api/storage/v2/records/versions/" + Linked, HttpStatusCode.OK, """{"recordId":"x","versions":[3,7]}""")
            .On(HttpMethod.Get, "/api/storage/v2/records/" + Linked + "/3", HttpStatusCode.OK, """{"id":"x","version":3}""")
            .On(HttpMethod.Get, "/api/storage/v2/records/versions/" + RecordId, HttpStatusCode.OK, """{"recordId":"x","versions":[5,9]}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, PlatformRoot, Samples.Logger<OsduDdmsProtocol>());

            Assert.Equal(7, (int?)(await protocol.ReadAsync(Linked))?["version"]);
            Assert.Equal([7L, 3L], await protocol.VersionsAsync(Linked));
            Assert.Equal(3, (int?)(await protocol.ReadVersionAsync(Linked, 3))?["version"]);

            // A Wellbore DDMS record is a storage record, whose versions name their bulk data: they are read there too.
            Assert.Equal([9L, 5L], await protocol.VersionsAsync(RecordId));
        }
    }

    [Fact]
    public async Task A_record_no_ddms_serves_is_not_read_when_the_endpoint_is_the_ddms_itself()
    {
        var handler = new FakeHttpHandler();
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());

            var ex = await Assert.ThrowsAsync<DeliveryException>(() => protocol.ReadAsync("dev:reference-data--WellLogSamplingDomainType:Depth"));
            Assert.Contains("no DDMS this flow reaches serves reference-data--WellLogSamplingDomainType", ex.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task The_history_purge_resolves_to_storage_under_the_platform_root()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "/api/storage/v2/records/" + RecordId + "/versions", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await new OsduDdmsProtocol(client, PlatformRoot, Samples.Logger<OsduDdmsProtocol>()).DeleteAsync(RecordId, RemovalScope.History);

            Assert.True(outcome.Deleted);
            Assert.Equal("/api/storage/v2/records/" + RecordId + "/versions", Assert.Single(handler.Calls).Uri.AbsolutePath);
        }
    }

    [Fact]
    public async Task An_explicit_path_option_is_used_as_written_even_with_a_root()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/facade-api/welllogs/" + RecordId, HttpStatusCode.OK, """{"id":"x","version":1}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var options = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms", VerifyPath = "/facade-api/welllogs/{id}" };
            await new OsduDdmsProtocol(client, options, Samples.Logger<OsduDdmsProtocol>()).VerifyAsync(RecordId, 1);

            Assert.Equal("/facade-api/welllogs/" + RecordId, Assert.Single(handler.Calls).Uri.AbsolutePath);
        }
    }

    [Fact]
    public void The_endpoints_a_removal_would_call_follow_the_root()
    {
        var endpoints = SqlFlow.Delivery.Engine.RemovalEndpoints.Of(
            Samples.Targeting(new FlowTarget
            {
                Endpoint = "https://osdu.example.com",
                Protocol = DeliveryProtocol.Ddms,
                ProtocolOptions = PlatformRoot,
            }),
            "osdu:wks:work-product-component--WellLog:1.4.0");

        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}", endpoints.Record);
        Assert.Equal("/api/storage/v2/records/{id}/versions", endpoints.History);
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}?purge=true", endpoints.Everything);
    }

    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.arc.WellLog
            key: [log_id]
            scope: { log_source: logSource }
          payloads:
            curves:
              root: curves/{logSource}
              locationColumn: curve_folder
              pattern: "chunk_*.parquet"
              hashColumn: payload_hash
          work: work/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          protocol: ddms
          protocolOptions: { payload: curves, ddmsRoot: /api/os-wellbore-ddms/ }
        """;

    [Fact]
    public void A_flow_declares_the_root_and_it_is_read_without_its_trailing_slash()
    {
        var flow = new SqlFlow.Delivery.Documents.DeliveryDocumentLoader().ParseFlow(Flow, "inline.yaml");
        Assert.Equal("/api/os-wellbore-ddms", flow.Target.ProtocolOptions.DdmsRoot);
    }

    [Theory]
    [InlineData("ddms", "api/os-wellbore-ddms", "starting with '/'")]
    [InlineData("ddms", "https://osdu.example.com/api/os-wellbore-ddms", "starting with '/'")]
    [InlineData("storage", "/api/os-wellbore-ddms", "only applies to the ddms route")]
    public void A_root_that_cannot_mean_what_it_says_is_refused_when_read(string protocol, string root, string expected)
    {
        var yaml = Flow
            .Replace("protocol: ddms", "protocol: " + protocol, StringComparison.Ordinal)
            .Replace("ddmsRoot: /api/os-wellbore-ddms/", "ddmsRoot: \"" + root + "\"", StringComparison.Ordinal);
        var ex = Assert.Throws<SqlFlow.Core.FlowValidationException>(() => new SqlFlow.Delivery.Documents.DeliveryDocumentLoader().ParseFlow(yaml, "inline.yaml"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>Which targets ask the legal service before a run, and where.</summary>
public class LegalTagCheckTests
{
    private const string ValidatePath = "/api/legal/v1/legaltags:validate";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    [Theory]
    [InlineData(DeliveryProtocol.Storage, null, null, true, ValidatePath)]
    [InlineData(DeliveryProtocol.File, null, null, true, ValidatePath)]
    [InlineData(DeliveryProtocol.Manifest, null, null, true, ValidatePath)]
    [InlineData(DeliveryProtocol.Ddms, "/api/os-wellbore-ddms", null, true, ValidatePath)]
    [InlineData(DeliveryProtocol.Ddms, null, null, true, null)]
    [InlineData(DeliveryProtocol.Ddms, null, "https://osdu.example.com/api/legal/v1/legaltags:validate", true, "https://osdu.example.com/api/legal/v1/legaltags:validate")]
    [InlineData(DeliveryProtocol.Storage, null, null, false, null)]
    public void The_check_goes_where_the_target_can_reach_the_legal_service(DeliveryProtocol protocol, string? ddmsRoot, string? legalPath, bool validate, string? expected)
    {
        // Every endpoint but a DDMS's own is the platform root; the ddms route knows which its endpoint is.
        var options = new ProtocolOptions { DdmsRoot = ddmsRoot, LegalValidatePath = legalPath, ValidateLegalTags = validate };
        var path = protocol == DeliveryProtocol.Ddms
            ? SqlFlow.Delivery.Engine.DdmsRouting.Of(options).LegalValidatePath
            : LegalTagValidator.PathFor(options, platformEndpoint: true);
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task A_storage_target_asks_the_legal_service_under_its_endpoint()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.OK, """{"invalidLegalTags":[{"name":"dev-expired","reason":"LegalTag has expired"}]}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            IDeliveryProtocol protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var invalid = await protocol.InvalidLegalTagsAsync(["dev-public", "dev-expired"]);

            Assert.NotNull(invalid);
            Assert.Equal("LegalTag has expired", Assert.Single(invalid).Value);
            Assert.Equal(ValidatePath, Assert.Single(handler.Calls).Uri.AbsolutePath);
        }
    }

    [Fact]
    public async Task A_well_log_target_on_the_ddms_itself_does_not_ask_and_says_so_with_null()
    {
        var handler = new FakeHttpHandler();
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            IDeliveryProtocol protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            Assert.Null(await protocol.InvalidLegalTagsAsync(["dev-public"]));
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task A_well_log_target_on_the_platform_root_asks_under_the_endpoint()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ValidatePath, HttpStatusCode.OK, """{"invalidLegalTags":[]}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            IDeliveryProtocol protocol = new OsduDdmsProtocol(client, new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }, Samples.Logger<OsduDdmsProtocol>());
            var invalid = await protocol.InvalidLegalTagsAsync(["dev-public"]);

            Assert.NotNull(invalid);
            Assert.Empty(invalid);
            Assert.Equal(ValidatePath, Assert.Single(handler.Calls).Uri.AbsolutePath);
        }
    }

    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.arc.WellLog
            key: [log_id]
            scope: { log_source: logSource }
          work: work/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          protocol: storage
          protocolOptions: { validateLegalTags: false, legalValidatePath: "__PATH__" }
        """;

    [Fact]
    public void A_flow_can_turn_the_check_off_and_name_the_endpoint()
    {
        var flow = new SqlFlow.Delivery.Documents.DeliveryDocumentLoader().ParseFlow(Flow.Replace("__PATH__", "https://osdu.example.com/api/legal/v1/legaltags:validate", StringComparison.Ordinal), "inline.yaml");
        Assert.False(flow.Target.ProtocolOptions.ValidateLegalTags);
        Assert.Equal("https://osdu.example.com/api/legal/v1/legaltags:validate", flow.Target.ProtocolOptions.LegalValidatePath);
    }

    [Fact]
    public void A_legal_path_that_is_neither_a_path_nor_a_url_is_refused_when_read()
    {
        var ex = Assert.Throws<SqlFlow.Core.FlowValidationException>(() => new SqlFlow.Delivery.Documents.DeliveryDocumentLoader().ParseFlow(Flow.Replace("__PATH__", "api/legal/v1/legaltags:validate", StringComparison.Ordinal), "inline.yaml"));
        Assert.Contains("legalValidatePath", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>Skipping duplicates at storage is something a flow opts in to, never the default.</summary>
public class SkipDuplicatesOptionTests
{
    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.arc.WellLog
            key: [log_id]
            scope: { log_source: logSource }
          work: work/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          protocol: storage
          protocolOptions: { __OPTIONS__ }
        """;

    [Fact]
    public void A_flow_that_says_nothing_does_not_skip_duplicates()
    {
        Assert.False(new ProtocolOptions().SkipDuplicates);
        var flow = new SqlFlow.Delivery.Documents.DeliveryDocumentLoader().ParseFlow(Flow.Replace("__OPTIONS__", "batchSize: 10", StringComparison.Ordinal), "inline.yaml");
        Assert.False(flow.Target.ProtocolOptions.SkipDuplicates);
    }

    [Fact]
    public void A_flow_can_opt_in()
    {
        var flow = new SqlFlow.Delivery.Documents.DeliveryDocumentLoader().ParseFlow(Flow.Replace("__OPTIONS__", "skipDuplicates: true", StringComparison.Ordinal), "inline.yaml");
        Assert.True(flow.Target.ProtocolOptions.SkipDuplicates);
    }
}

/// <summary>
/// The connection schema snapshots, reference captures and a retrieval flow's cache refresh reach OSDU through. A flow
/// declares its endpoint and headers as references; the connection resolves them, so no caller can hand it a
/// reference to use as a URL. A live retrieval flow whose endpoint was <c>${env:OSDU_URL}</c> crashed its run on the
/// first capture request because the cache refresh passed the declared value straight through.
/// </summary>
public class CaptureConnectionTests
{
    private static Task<OsduConnection> ConnectAsync(string endpoint, IReadOnlyDictionary<string, string> headers, FakeHttpHandler? handler = null)
        => OsduConnection.CreateAsync(
            endpoint, new TargetAuth { Type = TargetAuthType.None }, headers, new FlowReliability(),
            new SecretResolver([new EnvSecretProvider()]), handler, allowLoopback: true);

    [Fact]
    public async Task The_endpoint_and_headers_are_resolved_before_anything_is_sent()
    {
        var endpointVariable = "SQLFLOW_TEST_CAPTURE_ENDPOINT_" + Guid.NewGuid().ToString("N");
        var partitionVariable = "SQLFLOW_TEST_CAPTURE_PARTITION_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(endpointVariable, "http://localhost/osdu/");
        Environment.SetEnvironmentVariable(partitionVariable, "dev");
        try
        {
            var handler = new FakeHttpHandler().On(HttpMethod.Get, "/schema/osdu:wks:Thing:1.0.0", HttpStatusCode.OK, "{}");
            using var osdu = await ConnectAsync(
                "${env:" + endpointVariable + "}",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "${env:" + partitionVariable + "}" },
                handler);

            await osdu.GetJsonAsync("/api/schema-service/v1/schema/osdu:wks:Thing:1.0.0", CancellationToken.None);

            var call = Assert.Single(handler.Calls);
            Assert.Equal("http://localhost/osdu/api/schema-service/v1/schema/osdu:wks:Thing:1.0.0", call.Uri.AbsoluteUri);
            Assert.Equal("dev", call.Headers["data-partition-id"]);
            Assert.Equal("http://localhost/osdu", osdu.Endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(endpointVariable, null);
            Environment.SetEnvironmentVariable(partitionVariable, null);
        }
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://osdu.example.com")]
    [InlineData("/api/storage/v2")]
    public async Task An_endpoint_that_is_not_an_http_url_is_refused_naming_what_the_flow_declared(string endpoint)
    {
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => ConnectAsync(endpoint, new Dictionary<string, string>()));

        Assert.Contains($"'{endpoint}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_reference_that_does_not_resolve_fails_naming_the_variable()
    {
        var missing = "SQLFLOW_TEST_UNSET_" + Guid.NewGuid().ToString("N");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => ConnectAsync("${env:" + missing + "}", new Dictionary<string, string>()));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The correlation id that ties a delivery attempt to OSDU's own logs. Storage answered a live request with the
/// correlation-id it was sent; the OpenAPI descriptions of the services this system calls do not declare the header.
/// </summary>
public class CorrelationIdTests
{
    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string> { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    [Fact]
    public async Task Every_request_of_a_run_carries_the_runs_correlation_id_and_one_outside_any_an_id_of_its_own()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/records/r1", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            await client.SendJsonAsync(HttpMethod.Get, client.Url("/records/{id}", "r1"), null, null, CancellationToken.None);
            await client.SendJsonAsync(HttpMethod.Get, client.Url("/records/{id}", "r1"), null, null, CancellationToken.None);
            var runId = Guid.CreateVersion7();
            using (var run = OsduCorrelation.ForRun("wl", FlowTelemetry.Delivery, runId))
            {
                Assert.Equal($"osdu-delivery-wl-dlv-{runId:D}", run.Id);
                await client.SendJsonAsync(HttpMethod.Get, client.Url("/records/{id}", "r1"), null, null, CancellationToken.None);
                await client.SendJsonAsync(HttpMethod.Get, client.Url("/records/{id}", "r1"), null, null, CancellationToken.None);
            }

            // Outside a run each request is its own: the product, and an id of the request's.
            var loose = handler.Calls.Take(2).Select(c => c.Headers[OsduCorrelation.HeaderName]).ToList();
            Assert.All(loose, value => Assert.True(Guid.TryParseExact(value["osdu-delivery-".Length..], "D", out _) && value.StartsWith("osdu-delivery-", StringComparison.Ordinal), value));
            Assert.NotEqual(loose[0], loose[1]);
            Assert.Equal($"osdu-delivery-wl-dlv-{runId:D}", handler.Calls[2].Headers[OsduCorrelation.HeaderName]);
            Assert.Equal($"osdu-delivery-wl-dlv-{runId:D}", handler.Calls[3].Headers[OsduCorrelation.HeaderName]);
            Assert.Null(OsduCorrelation.Current);
        }
    }

    [Fact]
    public async Task A_signed_upload_url_is_not_sent_the_correlation_id()
    {
        // The landing zone is blob storage, not an OSDU service: the URL carries its own authorisation and nothing else.
        var handler = new FakeHttpHandler().On(HttpMethod.Put, "/landing/file.las", HttpStatusCode.Created, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        using (OsduCorrelation.ForRun("wl", FlowTelemetry.Delivery, Guid.CreateVersion7()))
        {
            await client.SendToSignedUrlAsync(
                HttpMethod.Put, new Uri("http://localhost/landing/file.las?sig=abc"), () => new MemoryStream([1, 2, 3]),
                "application/octet-stream", 3, new Dictionary<string, string>(), CancellationToken.None);
        }

        Assert.False(Assert.Single(handler.Calls).Headers.ContainsKey(OsduCorrelation.HeaderName));
    }

    [Fact]
    public async Task A_refused_request_names_the_correlation_id_the_service_answered_with()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/records/r1", _ =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad Request","message":"refused"}""");
            response.Headers.TryAddWithoutValidation(OsduCorrelation.HeaderName, "osdu-assigned-7");
            return response;
        });
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var ex = await Assert.ThrowsAsync<OsduStatusException>(
                () => client.SendJsonAsync(HttpMethod.Get, client.Url("/records/{id}", "r1"), null, null, CancellationToken.None));

            Assert.Contains("(correlation-id osdu-assigned-7)", ex.Message, StringComparison.Ordinal);
        }
    }
}
