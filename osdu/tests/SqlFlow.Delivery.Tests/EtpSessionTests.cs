using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests.Etp;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ETP session against a real WebSocket on the loopback interface (osdu/specs/reservoir-ddms/INTEGRATION.md
/// sections 1.3, 2, 3 and 8): the upgrade and what it carries, the negotiation, the framing and correlation of replies
/// until FIN, gzip, the keep-alive, and what a refused upgrade or a closed session does to a call in flight.
/// </summary>
public class EtpSessionTests : IDisposable
{
    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private readonly HttpRuntime _http = new(
        new FlowReliability { TimeoutSeconds = 20 }, Secrets, TimeProvider.System, handler: null, allowLoopback: true);

    private static readonly EtpSessionOptions Options = new()
    {
        MaxMessageBytes = 2_000_000,
        RequestTimeout = TimeSpan.FromSeconds(20),
        KeepAlive = TimeSpan.FromMilliseconds(200),
    };

    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer test-token",
        ["data-partition-id"] = "dev",
    };

    [Fact]
    public async Task A_session_opens_with_the_subprotocol_and_headers_and_settles_on_the_smaller_size()
    {
        await using var server = new FakeEtpServer { MaxMessageBytes = 500_000 };
        await using var session = await OpenAsync(server);

        Assert.True(session.IsOpen);
        Assert.Equal(500_000, session.MaxMessageBytes);
        Assert.True(session.Compressed);
        Assert.Equal("open-etp-server 1.3.0", session.Server);
        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Contains(EtpProtocols.Store, session.Protocols);

        Assert.Equal("Bearer test-token", server.UpgradeHeaders["Authorization"]);
        Assert.Equal("dev", server.UpgradeHeaders["data-partition-id"]);
        Assert.Equal(EtpSessionOptions.SubProtocol, server.UpgradeHeaders["Sec-WebSocket-Protocol"]);
    }

    [Fact]
    public async Task Every_message_this_client_sends_carries_an_even_id_above_the_last_one()
    {
        await using var server = new FakeEtpServer();
        await using var session = await OpenAsync(server, WithoutKeepAlive);
        await session.CallAsync(Info("eml:///dataspace('demo/study')"));
        await session.CallAsync(Info("eml:///dataspace('demo/other')"));

        // The session opened and made two calls, and sends nothing of its own: exactly three messages reached the server.
        var ids = server.Received.Select(frame => frame.Header.MessageId).ToList();
        Assert.Equal(3, ids.Count);
        Assert.All(ids, id => Assert.Equal(0, id % 2));
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(2, ids[0]);
    }

    [Theory]
    [InlineData(401, "the token the flow presents")]
    [InlineData(412, "subprotocol")]
    [InlineData(400, "data partition header")]
    [InlineData(404, "endpoint path is not the ETP one")]
    public async Task An_upgrade_the_endpoint_refuses_says_what_to_fix(int status, string expected)
    {
        await using var server = new FakeEtpServer { RefuseUpgradeWith = status };
        var failure = await Assert.ThrowsAsync<DeliveryException>(() => OpenAsync(server));
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        Assert.Contains(server.Uri.ToString(), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_that_does_not_speak_etp_is_refused_rather_than_used()
    {
        await using var server = new FakeEtpServer { SubProtocol = null };
        var failure = await Assert.ThrowsAsync<DeliveryException>(() => OpenAsync(server));
        Assert.Contains("ETP 1.2 endpoint", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_does_not_serve_a_protocol_this_route_needs_is_named_before_any_write()
    {
        await using var server = new FakeEtpServer
        {
            Served = [EtpProtocols.Core, EtpProtocols.Store, EtpProtocols.Dataspace, EtpProtocols.DataspaceOsdu, EtpProtocols.Discovery],
        };

        var failure = await Assert.ThrowsAsync<DeliveryException>(() => OpenAsync(server));
        Assert.Contains("does not serve ETP protocol(s) 9, 18", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_worth_compressing_crosses_compressed_once_both_sides_offered_gzip()
    {
        await using var server = new FakeEtpServer();
        await using var session = await OpenAsync(server);
        await session.CallAsync(new PutDataspaces
        {
            Dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal) { ["0"] = EtpSamples.Dataspace("demo/study") },
        });

        var xml = Encoding.UTF8.GetBytes(EtpSamples.Object(Guid.NewGuid(), "Padded", padding: 4000));
        var reply = await session.CallAsync(new PutDataObjects
        {
            DataObjects = new Dictionary<string, DataObject>(StringComparer.Ordinal)
            {
                ["0"] = new DataObject
                {
                    Resource = Resource("eml:///dataspace('demo/study')/resqml20.obj_Grid2dRepresentation(11111111-1111-1111-1111-111111111111)"),
                    Data = xml,
                },
            },
        });

        Assert.Single(reply.Part<PutDataObjectsResponse>().Success);
        Assert.True(server.SawCompressedBody);
    }

    [Fact]
    public async Task A_reply_in_parts_collects_the_errors_and_the_response_that_follows_them()
    {
        await using var server = new FakeEtpServer();
        await using var session = await OpenAsync(server);
        await session.CallAsync(new PutDataspaces
        {
            Dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal) { ["0"] = EtpSamples.Dataspace("demo/study") },
        });

        var reply = await session.CallAsync(new GetDataspaceInfo
        {
            Uris = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["there"] = "eml:///dataspace('demo/study')",
                ["missing"] = "eml:///dataspace('demo/gone')",
            },
        });

        Assert.Equal(2, reply.Parts.Count + reply.Errors.Count);
        Assert.Null(reply.Error);
        Assert.Equal(EtpErrorCodes.NotFound, reply.Errors["missing"].Code);
        Assert.Single(reply.Part<GetDataspaceInfoResponse>().Dataspaces);
    }

    [Fact]
    public async Task A_request_the_server_fails_as_a_whole_throws_with_its_code_and_text()
    {
        await using var server = new FakeEtpServer();
        await using var session = await OpenAsync(server);
        server.FailNext["Transaction.StartTransaction"] = new ErrorInfo
        {
            Code = EtpErrorCodes.MaxTransactionsExceeded,
            Message = "Cannot start transaction, too many write transaction URI(s): eml:///dataspace('demo/study')",
        };

        var reply = await session.CallAsync(new StartTransaction { ReadOnly = false, DataspaceUris = ["eml:///dataspace('demo/study')"] });
        var failure = Assert.Throws<EtpProtocolException>(() => reply.Part<StartTransactionResponse>());

        Assert.Equal(EtpErrorCodes.MaxTransactionsExceeded, failure.Code);
        Assert.True(failure.Retryable);
        Assert.Contains("EMAX_TRANSACTIONS_EXCEEDED", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Transaction.StartTransaction", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dataspace_the_server_refuses_outright_comes_back_as_the_reason_it_gave()
    {
        await using var server = new FakeEtpServer();
        await using var session = await OpenAsync(server);

        var reply = await session.CallAsync(new PutDataspaces
        {
            Dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal)
            {
                ["0"] = EtpSamples.Dataspace("demo/study") with { CustomData = new Dictionary<string, DataValue>(StringComparer.Ordinal) },
            },
        });

        var failure = Assert.Throws<EtpProtocolException>(reply.Failed);
        Assert.Equal(EtpErrorCodes.RequestDenied, failure.Code);
        Assert.False(failure.Retryable);
        Assert.Contains("viewers, owners, legaltags, otherRelevantDataCountries", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ping_from_the_server_is_answered_and_an_idle_session_pings_by_itself()
    {
        await using var server = new FakeEtpServer();
        await using var session = await OpenAsync(server);
        await server.Connected!.SendAsync(new Ping { CurrentDateTime = EtpTime.Microseconds(DateTimeOffset.UtcNow) }, correlationId: 0, CancellationToken.None);

        await WaitFor(() => server.Received.Any(f => f.Body is Pong), "the client to answer the ping");
        await WaitFor(() => server.Received.Any(f => f.Body is Ping), "the idle session to ping by itself");
    }

    [Fact]
    public async Task A_session_the_server_closes_ends_the_calls_that_were_waiting()
    {
        await using var server = new FakeEtpServer();
        var session = await OpenAsync(server);
        await using (session)
        {
            server.FailNext["Dataspace.PutDataspaces"] = new ErrorInfo { Code = 0, Message = string.Empty };
            await server.Connected!.SendAsync(new CloseSession { Reason = "going away" }, correlationId: 0, CancellationToken.None);
            await WaitFor(() => !session.IsOpen, "the session to notice the close");

            var failure = await Assert.ThrowsAsync<DeliveryException>(
                () => session.CallAsync(Info("eml:///dataspace('demo/study')")));
            Assert.Contains("going away", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Closing_the_session_tells_the_server_before_the_socket_goes()
    {
        await using var server = new FakeEtpServer();
        var session = await OpenAsync(server);
        await session.CloseAsync("done");
        await session.DisposeAsync();

        await WaitFor(
            () => server.Received.Any(f => f.Body is CloseSession),
            "the server to see the close",
            () => "the server had received " + string.Join(", ", server.Received.Select(f => f.Body?.GetType().Name ?? "an unknown message")));
        Assert.Equal("done", server.Received.OfType<EtpFrame>().Select(f => f.Body).OfType<CloseSession>().Single().Reason);
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<EtpSession> OpenAsync(FakeEtpServer server, EtpSessionOptions? options = null)
        => EtpSession.OpenAsync(server.Uri, Headers, _http.Invoker, new UrlGuard([], _http.Network), options ?? Options, NullLogger.Instance);

    /// <summary>
    /// The session options with the keep-alive off, for a test that counts what reached the server. A session pings when
    /// it has been idle for <see cref="EtpSessionOptions.KeepAlive"/>, which this class keeps at 200ms so the keep-alive
    /// itself can be tested; every ping is a message the server records, so a test asserting an exact frame count would
    /// otherwise be counting how long the machine took between two calls.
    /// </summary>
    private static EtpSessionOptions WithoutKeepAlive => Options with { KeepAlive = TimeSpan.Zero };

    private static GetDataspaceInfo Info(string uri) => new()
    {
        Uris = new Dictionary<string, string>(StringComparer.Ordinal) { ["0"] = uri },
    };

    private static Resource Resource(string uri) => new()
    {
        Uri = uri,
        Name = "Grid",
        LastChanged = 0,
        StoreLastWrite = 0,
        StoreCreated = 0,
        ActiveStatus = ActiveStatusKind.Active,
    };

    /// <summary>
    /// Waits for something the other side does, so a test never races the socket. The cap is generous because the unit
    /// tier runs every test assembly at once, and a machine that busy has stalled a loopback round trip past 10 s: a late
    /// answer only makes the test slower, and a failure says what had arrived by then.
    /// </summary>
    private static async Task WaitFor(Func<bool> done, string what, Func<string>? seen = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out after 30 s waiting for {what}{(seen is null ? "." : $"; {seen()}.")}");
            }

            await Task.Delay(20);
        }
    }
}
