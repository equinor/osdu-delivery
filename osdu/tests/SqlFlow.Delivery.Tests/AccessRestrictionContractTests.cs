using System.Net;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Restrictions;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What restricting records' access promises without a database (osdu/docs/reference/concepts/access-restriction.md): the
/// wire names and the run payload, which routes can restrict, how an ACL and the caller's groups are read and compared, and
/// what storage's metadata patch answers mean for each record.
/// </summary>
public sealed class AccessRestrictionContractTests
{
    private const string Hold = "data.restricted.owners@dev.dataservices.energy";

    [Theory]
    [InlineData("restrict", RemovalChoice.Restrict)]
    [InlineData("unrestrict", RemovalChoice.Unrestrict)]
    public void The_access_scopes_round_trip_through_their_wire_names(string wire, RemovalChoice scope)
    {
        Assert.Equal(wire, RemovalScopes.Wire(scope));
        Assert.True(RemovalScopes.TryParse(wire, out var parsed));
        Assert.Equal(scope, parsed);
        Assert.True(scope.ChangesAccess());
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.Scope());
    }

    [Fact]
    public void A_restrict_run_names_its_restriction_and_no_other_run_does()
    {
        const string Keys = "\"recordKeys\":[\"11111111-1111-1111-1111-111111111111\"]";
        var payload = DeliveryRunPayload.Parse($$"""{"scope":"restrict","restriction":"legal-hold",{{Keys}}}""");
        payload.Validate(DeliveryOperations.Remove);
        Assert.Equal((RemovalChoice.Restrict, "legal-hold"), (payload.Scope, payload.Restriction));
        Assert.Contains("\"restriction\":\"legal-hold\"", payload.ToJson(), StringComparison.Ordinal);

        var none = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($$"""{"scope":"restrict",{{Keys}}}""").Validate(DeliveryOperations.Remove));
        Assert.Contains("payload restriction is required", none.Message, StringComparison.Ordinal);
        var other = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($$"""{"scope":"record","restriction":"legal-hold",{{Keys}}}""").Validate(DeliveryOperations.Remove));
        Assert.Contains("restriction", other.Message, StringComparison.Ordinal);
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"restriction":"legal-hold"}""").Validate(DeliveryOperations.Deliver));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($$"""{"scope":"restrict","restriction":"   ",{{Keys}}}"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($$"""{"scope":"restrict","restriction":"{{new string('x', 101)}}",{{Keys}}}"""));

        var lift = DeliveryRunPayload.Parse($$"""{"scope":"unrestrict",{{Keys}}}""");
        lift.Validate(DeliveryOperations.Remove);
        Assert.Equal(RemovalChoice.Unrestrict, lift.Scope);
    }

    [Fact]
    public void Only_a_route_whose_records_access_is_one_storage_acl_restricts()
    {
        var storage = Flow(DeliveryProtocol.Storage);
        var route = RestrictionRoute.Of(storage, "osdu:wks:master-data--Wellbore:1.3.0");
        Assert.True(route.Restricts);
        Assert.StartsWith("PATCH /api/storage/v2/records", route.Call, StringComparison.Ordinal);

        var facade = storage with { Target = storage.Target with { ProtocolOptions = new ProtocolOptions { RecordMethod = "POST", RecordPath = "/facade/records" } } };
        Assert.Contains("not the storage service's PUT", RestrictionRoute.Of(facade, null).Refusal, StringComparison.Ordinal);

        foreach (var protocol in new[] { DeliveryProtocol.File, DeliveryProtocol.Dataset, DeliveryProtocol.Manifest, DeliveryProtocol.Workflow })
        {
            Assert.Contains("ACL of its own", RestrictionRoute.Of(Flow(protocol), null).Refusal, StringComparison.Ordinal);
        }

        Assert.Contains("carry no OSDU ACL", RestrictionRoute.Of(Flow(DeliveryProtocol.Dspdm), null).Refusal, StringComparison.Ordinal);
        Assert.Contains("no OSDU ACL", RestrictionRoute.Of(Flow(DeliveryProtocol.Etp), null).Refusal, StringComparison.Ordinal);
        Assert.Contains("synced", RestrictionRoute.Of(Flow(DeliveryProtocol.Ddms), null).Refusal, StringComparison.Ordinal);
        var dspdm = RestrictionRoute.Of(Flow(DeliveryProtocol.Dspdm), null);
        Assert.Equal($"({dspdm.Refusal})", dspdm.Call);
    }

    [Fact]
    public void An_acl_reads_from_a_stored_record_and_back_and_compares_whatever_the_order_and_case()
    {
        var record = FakeOsduPlatform.Record("dev:master-data--Wellbore:1", "osdu:wks:master-data--Wellbore:1.3.0");
        var acl = RecordAcl.OfRecord(record)!;
        Assert.Equal(["data.default.viewers@dev.example.com"], acl.Viewers);
        Assert.True(RecordAcl.Parse(acl.ToJson()).SameAs(acl));
        Assert.True(new RecordAcl(["B@x.com", "a@x.com"], ["c@x.com"]).SameAs(new RecordAcl(["A@X.COM", "b@x.com", "b@x.com"], ["C@x.com"])));
        Assert.False(new RecordAcl(["a@x.com"], ["c@x.com"]).SameAs(new RecordAcl(["a@x.com"], ["d@x.com"])));
        Assert.Throws<DeliveryException>(() => RecordAcl.Parse("""{"viewers":["a@x.com"]}"""));
        Assert.Throws<DeliveryException>(() => RecordAcl.Parse("not json"));

        Assert.True(EntitlementGroups.IsDataGroup(Hold));
        Assert.False(EntitlementGroups.IsDataGroup("users.data.root@dev.dataservices.energy"));
        Assert.False(EntitlementGroups.IsDataGroup("data.hold"));
    }

    [Fact]
    public void The_flow_may_change_a_record_it_owns_by_group_name_or_as_a_data_manager()
    {
        var caller = new CallerGroups("delivery@dev.example.com", ["DATA.Restricted.Owners@other.example.com", "data.default.viewers@dev.example.com"]);
        Assert.True(caller.MayChange([Hold]));
        Assert.False(caller.MayChange(["data.default.owners@dev.example.com"]));
        Assert.True(new CallerGroups(null, ["users.data.root@dev.dataservices.energy"]).MayChange(["data.anyone@dev.example.com"]));
        Assert.Equal("the flow's identity", new CallerGroups(null, []).Who);
    }

    [Fact]
    public async Task Storage_says_for_each_record_whether_its_access_was_replaced()
    {
        using var platform = new FakeOsduPlatform();
        platform.Put(FakeOsduPlatform.Record("dev:master-data--Wellbore:mine", "osdu:wks:master-data--Wellbore:1.3.0"));
        var theirs = FakeOsduPlatform.Record("dev:master-data--Wellbore:theirs", "osdu:wks:master-data--Wellbore:1.3.0");
        theirs["acl"]!["owners"] = new System.Text.Json.Nodes.JsonArray("data.another-team.owners@dev.example.com");
        platform.Put(theirs);
        using var rig = new Rig(platform);
        var hold = new RecordAcl([Hold], [Hold]);

        var read = await rig.Access.ReadAsync(["dev:master-data--Wellbore:mine", "dev:master-data--Wellbore:gone"], CancellationToken.None);
        Assert.Equal(["dev:master-data--Wellbore:mine"], read.Keys);
        Assert.Equal(["data.default.owners@dev.example.com"], read["dev:master-data--Wellbore:mine"].Acl.Owners);

        var changes = await rig.Access.ReplaceAsync(["dev:master-data--Wellbore:mine", "dev:master-data--Wellbore:theirs", "dev:master-data--Wellbore:gone"], hold, CancellationToken.None);
        Assert.Equal([AclChangeOutcome.Replaced, AclChangeOutcome.Refused, AclChangeOutcome.NotFound], changes.Select(c => c.Outcome));
        Assert.Contains("unAuthorizedRecordIds", changes[1].Detail, StringComparison.Ordinal);
        var patch = Assert.Single(platform.AclPatches);
        Assert.Equal(["/acl/viewers", "/acl/owners"], patch.Ops.Select(o => o!["path"]!.GetValue<string>()));
        Assert.All(patch.Ops, o => Assert.Equal("replace", o!["op"]!.GetValue<string>()));
        var sent = platform.Calls.Single(c => c.Method == HttpMethod.Patch);
        Assert.Equal("application/json", sent.ContentType);

        platform.AclPatchRefused = HttpStatusCode.BadRequest;
        Assert.All(await rig.Access.ReplaceAsync(["dev:master-data--Wellbore:mine"], hold, CancellationToken.None), c => Assert.Equal(AclChangeOutcome.Refused, c.Outcome));
        platform.AclPatchRefused = null;
        platform.AclPatchAnswerLost = true;
        Assert.All(await rig.Access.ReplaceAsync(["dev:master-data--Wellbore:mine"], hold, CancellationToken.None), c => Assert.Equal(AclChangeOutcome.Unknown, c.Outcome));

        var groups = await rig.Access.CallerGroupsAsync(CancellationToken.None);
        Assert.Equal(platform.MemberEmail, groups.MemberEmail);
        Assert.Contains(Hold, groups.Groups);
    }

    private static FlowDefinition Flow(DeliveryProtocol protocol)
    {
        var flow = Samples.LocalFlow(Path.GetTempPath());
        return flow with { Target = flow.Target with { Protocol = protocol, ProtocolOptions = new ProtocolOptions() } };
    }

    /// <summary>The storage access over the fake platform, as the storage route builds it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly HttpRuntime _runtime;

        public Rig(FakeOsduPlatform platform)
        {
            _runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new Core.Secrets.SecretResolver([new Core.Secrets.EnvSecretProvider()]), TimeProvider.System, platform, allowLoopback: true);
            var client = new OsduHttpClient(
                _runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Access = new OsduRecordProtocol(client, new ProtocolOptions()).Access!;
        }

        public IRecordAccess Access { get; }

        public void Dispose() => _runtime.Dispose();
    }
}
