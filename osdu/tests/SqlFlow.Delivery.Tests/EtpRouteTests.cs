using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests.Etp;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The etp route end to end against a fake Reservoir DDMS built from its brief
/// (osdu/specs/reservoir-ddms/INTEGRATION.md): the dataspace created with the record's own ACLs and legal tags, the
/// objects and their arrays written inside one transaction, a commit the server refuses rolled back, the object read
/// back and verified, and the removal that deletes it.
/// </summary>
public class EtpRouteTests : IDisposable
{
    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private readonly HttpRuntime _http = new(
        new FlowReliability { TimeoutSeconds = 20 }, Secrets, TimeProvider.System, handler: null, allowLoopback: true);

    [Fact]
    public async Task A_session_carries_the_correlation_id_of_the_run_that_opens_it_on_its_upgrade()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var runId = Guid.CreateVersion7();

        using (OsduCorrelation.ForRun("rddms", FlowTelemetry.Delivery, runId))
        {
            Assert.True((await route.DeliverAsync(Work(Guid.NewGuid(), "Top Field A"))).Succeeded);
        }

        Assert.Equal($"osdu-delivery-rddms-dlv-{runId:D}", server.UpgradeHeaders[OsduCorrelation.HeaderName]);
    }

    [Fact]
    public async Task A_record_creates_the_dataspace_its_own_access_declares_and_lands_as_an_object()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var uuid = Guid.NewGuid();

        var outcome = await route.DeliverAsync(Work(uuid, "Top Field A"));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.MetadataDelivered);
        var uri = EtpObjectXml.ObjectUri("demo/study", "resqml20.obj_Grid2dRepresentation", uuid);
        Assert.Equal(uri, outcome.Returned[OsduEtpProtocol.UriValue]);
        Assert.Equal("demo/study", outcome.Returned[OsduEtpProtocol.DataspaceValue]);
        Assert.Equal(uuid.ToString("D"), outcome.Returned[OsduEtpProtocol.UuidValue]);

        var space = Assert.Single(server.Spaces.Values);
        Assert.Equal("demo/study", space.Path);
        Assert.Equal(["data.default.viewers@dev.example.com"], space.CustomData["viewers"].Strings);
        Assert.Equal(["dev-public-usa-dataset-1"], space.CustomData["legaltags"].Strings);
        Assert.Equal("Top Field A", Assert.Single(space.Objects.Values).Name);
        Assert.False(server.InTransaction);

        // The server registers the dataspace's OSDU record itself; the route knows the id it will have.
        Assert.Equal("dev:dataset--ETPDataspace:demo-study", Assert.Single(server.StorageRecords));
        Assert.Equal("dev:dataset--ETPDataspace:demo-study", EtpDataspaceRecord.Id(server.Uri, "demo/study", "dev"));

        // Every write went inside one transaction, and it was committed, not left open.
        Assert.Contains(server.Received, f => f.Body is StartTransaction);
        Assert.Contains(server.Received, f => f.Body is CommitTransaction);
        Assert.DoesNotContain(server.Received, f => f.Body is RollbackTransaction);
    }

    [Fact]
    public async Task A_batch_shares_one_dataspace_one_transaction_and_one_message()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var works = Enumerable.Range(0, 5).Select(i => Work(Guid.NewGuid(), $"Horizon {i}")).ToList();

        var outcomes = await route.DeliverBatchAsync(works);

        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(5, Assert.Single(server.Spaces.Values).Objects.Count);
        Assert.Single(server.Received, f => f.Body is StartTransaction);
        Assert.Single(server.Received, f => f.Body is PutDataObjects);
        Assert.Single(server.Received, f => f.Body is PutDataspaces);
    }

    [Fact]
    public async Task An_existing_dataspace_is_used_as_it_stands_and_never_created_again()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        await route.DeliverAsync(Work(Guid.NewGuid(), "First"));
        await route.DeliverAsync(Work(Guid.NewGuid(), "Second"));

        Assert.Single(server.Received, f => f.Body is PutDataspaces);
        Assert.Equal(2, Assert.Single(server.Spaces.Values).Objects.Count);
        Assert.Single(server.StorageRecords);
    }

    [Fact]
    public async Task An_object_with_an_array_sends_both_and_the_array_lands_under_the_path_its_xml_names()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var uuid = Guid.NewGuid();
        var work = Work(uuid, "Surface", arrayPath: "RESQML/points", arrays: new JsonArray
        {
            new JsonObject
            {
                ["Path"] = "RESQML/points",
                ["Type"] = "arrayOfDouble",
                ["Dimensions"] = new JsonArray { 2, 3 },
                ["Values"] = new JsonArray { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 },
            },
        });

        var outcome = await route.DeliverAsync(work);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.ChunksSent);
        var array = Assert.Single(Assert.Single(server.Spaces.Values).Arrays);
        Assert.Equal("RESQML/points", array.Key);
        Assert.Equal([2, 3], array.Value.Dimensions);
        Assert.Equal(AnyArrayType.ArrayOfDouble, array.Value.Type);
        Assert.Equal([1.0, 2.0, 3.0, 4.0, 5.0, 6.0], array.Value.Data!.Doubles.ToArray());
    }

    [Fact]
    public async Task An_array_too_large_for_a_message_is_declared_and_then_filled_slice_by_slice()
    {
        await using var server = new FakeEtpServer { MaxMessageBytes = 70_000 };
        var route = Route(server);
        var uuid = Guid.NewGuid();
        var values = new JsonArray();
        for (var i = 0; i < 20_000; i++)
        {
            values.Add(i * 1.5);
        }

        var work = Work(uuid, "Deep surface", arrayPath: "RESQML/deep", arrays: new JsonArray
        {
            new JsonObject
            {
                ["Path"] = "RESQML/deep",
                ["Type"] = "arrayOfDouble",
                ["Dimensions"] = new JsonArray { 10_000, 2 },
                ["Values"] = values,
            },
        });

        var outcome = await route.DeliverAsync(work);

        Assert.True(outcome.Succeeded);
        Assert.Contains(server.Received, f => f.Body is PutUninitializedDataArrays);
        var slices = server.Received.Where(f => f.Body is PutDataSubarrays).ToList();
        Assert.True(slices.Count > 1, $"a 160 kB array crosses in more than one slice of a 70 kB session, and it crossed in {slices.Count}");

        var array = Assert.Single(Assert.Single(server.Spaces.Values).Arrays).Value;
        Assert.Equal([10_000, 2], array.Dimensions);
        Assert.Equal(20_000, array.Slices.Sum(s => s.Data.ElementCount));

        // The slices cover the array once, in order, each a contiguous run of it.
        var covered = 0L;
        foreach (var slice in array.Slices.OrderBy(s => s.Starts[0]))
        {
            Assert.Equal(covered / 2, slice.Starts[0]);
            Assert.Equal(2, slice.Counts[1]);
            covered += slice.Data.ElementCount;
        }

        Assert.Equal(20_000, covered);
    }

    [Fact]
    public async Task An_object_larger_than_a_message_follows_its_put_in_chunks()
    {
        await using var server = new FakeEtpServer { MaxMessageBytes = 64_000 };
        var route = Route(server);
        var uuid = Guid.NewGuid();
        var work = Work(uuid, "Padded", padding: 200_000);

        var outcome = await route.DeliverAsync(work);

        Assert.True(outcome.Succeeded);
        var chunks = server.Received.Where(f => f.Body is Chunk).ToList();
        Assert.True(chunks.Count >= 4, $"a 200 kB object crosses in chunks of a 64 kB session, and it crossed in {chunks.Count}");
        Assert.All(chunks, chunk => Assert.NotEqual(0, chunk.Header.CorrelationId));
        Assert.True(chunks[^1].IsFinal);
        Assert.Equal("Padded", Assert.Single(Assert.Single(server.Spaces.Values).Objects.Values).Name);
    }

    [Fact]
    public async Task A_commit_the_server_refuses_rolls_the_transaction_back_and_holds_the_record()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);

        // An object that names an array path nothing supplies is what the server refuses at commit; the route's own
        // guard catches that before sending, so this drives the refusal from the server instead.
        server.FailNext["Transaction.CommitTransaction"] = new ErrorInfo { Code = EtpErrorCodes.InvalidState, Message = "2 Missing array(s)" };

        var outcome = await route.DeliverAsync(Work(Guid.NewGuid(), "Refused"));

        var failure = Assert.IsType<EtpProtocolException>(outcome.Failure);
        Assert.Contains("Missing array(s)", failure.Message, StringComparison.Ordinal);
        Assert.Contains(server.Received, f => f.Body is RollbackTransaction);
        Assert.False(server.InTransaction);
        Assert.Empty(Assert.Single(server.Spaces.Values).Objects);
    }

    [Fact]
    public async Task A_record_whose_xml_the_store_would_file_unreachable_is_held_before_anything_is_sent()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var work = Work(Guid.NewGuid(), "Wrong type", type: "Grid2dRepresentation");

        var outcome = await route.DeliverAsync(work);

        var held = Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Contains("obj_Grid2dRepresentation", held.Message, StringComparison.Ordinal);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task A_record_that_declares_an_array_its_xml_names_nowhere_is_held_before_anything_is_sent()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var work = Work(Guid.NewGuid(), "Orphan", arrays: new JsonArray
        {
            new JsonObject
            {
                ["Path"] = "RESQML/nothing",
                ["Type"] = "arrayOfDouble",
                ["Dimensions"] = new JsonArray { 1 },
                ["Values"] = new JsonArray { 1.0 },
            },
        });

        var outcome = await route.DeliverAsync(work);

        var held = Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Contains("RESQML/nothing", held.Message, StringComparison.Ordinal);
        Assert.Contains("no object claims", held.Message, StringComparison.Ordinal);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task A_delivered_object_verifies_reads_back_and_is_removed_by_the_everything_scope()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var uuid = Guid.NewGuid();
        var outcome = await route.DeliverAsync(Work(uuid, "Round trip"));
        var state = outcome.Returned;

        var verified = await route.VerifyBatchAsync([new VerifyRequest("dev:etp:1", null, state)]);
        Assert.Equal(VerifyOutcome.Match, Assert.Single(verified).Outcome);
        Assert.NotNull(Assert.Single(verified).ObservedVersion);

        var read = await route.ReadAsync("dev:etp:1", state, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(state[OsduEtpProtocol.UriValue], read["uri"]!.GetValue<string>());
        Assert.Contains("Round trip", read["xml"]!.GetValue<string>(), StringComparison.Ordinal);

        var record = await Assert.ThrowsAsync<DeliveryException>(() => route.DeleteAsync("dev:etp:1", RemovalScope.Record, state));
        Assert.Contains("no reversible removal", record.Message, StringComparison.Ordinal);
        var history = await Assert.ThrowsAsync<DeliveryException>(() => route.DeleteAsync("dev:etp:1", RemovalScope.History, state));
        Assert.Contains("no history to purge", history.Message, StringComparison.Ordinal);

        var deleted = await route.DeleteAsync("dev:etp:1", RemovalScope.Everything, state);
        Assert.True(deleted.Deleted);
        Assert.Empty(Assert.Single(server.Spaces.Values).Objects);

        var gone = await route.VerifyBatchAsync([new VerifyRequest("dev:etp:1", null, state)]);
        Assert.Equal(VerifyOutcome.Missing, Assert.Single(gone).Outcome);
        var again = await route.DeleteAsync("dev:etp:1", RemovalScope.Everything, state);
        Assert.True(again.AlreadyGone);
    }

    [Fact]
    public async Task A_verify_says_drifted_when_the_store_wrote_the_object_after_the_ledger_did()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);
        var state = (await route.DeliverAsync(Work(Guid.NewGuid(), "Drift"))).Returned;

        var verified = await route.VerifyBatchAsync([new VerifyRequest("dev:etp:1", 1, state)]);
        var result = Assert.Single(verified);
        Assert.Equal(VerifyOutcome.Drifted, result.Outcome);
        Assert.Contains("last wrote", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_probe_says_what_the_server_is_and_what_the_session_settled_on()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server);

        var probe = await route.ProbeAsync();

        Assert.True(probe.Reachable);
        Assert.Equal(101, probe.Status);
        Assert.Contains("open-etp-server", probe.Detail, StringComparison.Ordinal);
        Assert.Contains("compression gzip", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_probe_against_an_endpoint_that_refuses_the_upgrade_says_so_rather_than_throwing()
    {
        await using var server = new FakeEtpServer { RefuseUpgradeWith = 401 };
        var route = Route(server);

        var probe = await route.ProbeAsync();

        Assert.False(probe.Reachable);
        Assert.Contains("token the flow presents", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_that_locks_its_dataspace_unlocks_it_to_write_and_locks_it_again()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server, locked: true);
        await route.DeliverAsync(Work(Guid.NewGuid(), "Published"));
        Assert.True(Assert.Single(server.Spaces.Values).Locked);

        await route.DeliverAsync(Work(Guid.NewGuid(), "Published again"));
        Assert.True(Assert.Single(server.Spaces.Values).Locked);
        Assert.Equal(2, Assert.Single(server.Spaces.Values).Objects.Count);
        Assert.Equal(3, server.Received.Count(f => f.Body is LockDataspaces));
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    private OsduEtpProtocol Route(FakeEtpServer server, bool locked = false)
    {
        var target = new EtpTarget { Dataspace = "demo/study", Lock = locked };
        var connection = new EtpConnection(
            _http,
            server.Uri,
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
            [],
            new EtpSessionOptions { MaxMessageBytes = 2_000_000, RequestTimeout = TimeSpan.FromSeconds(20), KeepAlive = TimeSpan.Zero },
            NullLogger.Instance);
        return new OsduEtpProtocol(connection, target, "dev", NullLogger<OsduEtpProtocol>.Instance);
    }

    private static DeliveryWork Work(
        Guid uuid,
        string title,
        string type = "obj_Grid2dRepresentation",
        string? arrayPath = null,
        JsonArray? arrays = null,
        int padding = 0)
    {
        var data = new JsonObject
        {
            ["Xml"] = EtpSamples.Object(uuid, title, type, arrayPath, padding),
        };
        if (arrays is not null)
        {
            data["Arrays"] = arrays;
        }

        return new DeliveryWork
        {
            Key = DeliveryKey.Derive("etp", [uuid.ToString("N")]),
            TargetId = $"dev:etp:{uuid:N}",
            SourceKey = title,
            Document = new JsonObject
            {
                ["kind"] = $"energistics:etp:{type}:2.0.1",
                ["acl"] = new JsonObject
                {
                    ["viewers"] = new JsonArray { "data.default.viewers@dev.example.com" },
                    ["owners"] = new JsonArray { "data.default.owners@dev.example.com" },
                },
                ["legal"] = new JsonObject
                {
                    ["legaltags"] = new JsonArray { "dev-public-usa-dataset-1" },
                    ["otherRelevantDataCountries"] = new JsonArray { "US" },
                    ["status"] = "compliant",
                },
                ["data"] = data,
            },
            DeliverMetadata = true,
            DeliverPayload = false,
        };
    }
}
