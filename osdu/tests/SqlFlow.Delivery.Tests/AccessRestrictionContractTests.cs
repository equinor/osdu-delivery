using System.Net;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Restrictions;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
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
        Assert.Equal(["dev:master-data--Wellbore:mine"], read.Held.Keys);
        Assert.Equal(["data.default.owners@dev.example.com"], read.Held["dev:master-data--Wellbore:mine"].Acl.Owners);
        Assert.Equal(RecordAccessAnswer.NotFound, read.AnswerFor("dev:master-data--Wellbore:gone"));

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

    [Fact]
    public async Task The_read_tells_a_record_storage_withheld_or_did_not_answer_for_from_one_it_no_longer_holds()
    {
        using var platform = new FakeOsduPlatform();
        foreach (var id in new[] { "dev:master-data--Wellbore:mine", "dev:master-data--Wellbore:hidden", "dev:master-data--Wellbore:busy", "dev:master-data--Wellbore:bare" })
        {
            platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:master-data--Wellbore:1.3.0"));
        }

        platform.Records["dev:master-data--Wellbore:bare"].Remove("acl");
        platform.ReadWithheld.Add("dev:master-data--Wellbore:hidden");
        platform.ReadRetried.Add("dev:master-data--Wellbore:busy");
        using var rig = new Rig(platform);

        var read = await rig.Access.ReadAsync(
            ["dev:master-data--Wellbore:mine", "dev:master-data--Wellbore:hidden", "dev:master-data--Wellbore:busy", "dev:master-data--Wellbore:bare", "dev:master-data--Wellbore:gone"],
            CancellationToken.None);

        Assert.Equal(RecordAccessAnswer.Held, read.AnswerFor("dev:master-data--Wellbore:mine"));
        Assert.Equal(RecordAccessAnswer.Withheld, read.AnswerFor("dev:master-data--Wellbore:hidden"));
        Assert.Equal(RecordAccessAnswer.Unanswered, read.AnswerFor("dev:master-data--Wellbore:busy"));
        Assert.Equal(RecordAccessAnswer.NoAcl, read.AnswerFor("dev:master-data--Wellbore:bare"));
        Assert.Equal(RecordAccessAnswer.NotFound, read.AnswerFor("dev:master-data--Wellbore:gone"));
        Assert.Null(RecordAccessReads.Describe(RecordAccessAnswer.Held));
        Assert.Contains("may not read", RecordAccessReads.Describe(RecordAccessAnswer.Withheld), StringComparison.Ordinal);
        Assert.Contains("invalidRecords", RecordAccessReads.Describe(RecordAccessAnswer.NotFound), StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_a_removal_leaves_names_the_first_skipped_records_with_why()
    {
        var results = Enumerable.Range(1, 5)
            .Select(n => RemovalRecordResult.Skipped(new DeliveryKey(Guid.NewGuid()), $"src-{n}", $"Log {n}", null, $"reason {n}", null))
            .Append(RemovalRecordResult.Restricted(new DeliveryKey(Guid.NewGuid()), "src-6", "Log 6", "dev:wpc--WellLog:6", "restricted", null))
            .ToList();

        var line = (RemovalSummary.Of(RemovalChoice.Restrict, 6, results) with { Restriction = "Hold" }).Describe();

        Assert.StartsWith("6 record(s) selected: 1 restricted under 'Hold' (reversible), 5 skipped, 0 failed; skipped Log 1: reason 1; Log 2: reason 2; Log 3: reason 3; and 2 more skipped", line, StringComparison.Ordinal);
        Assert.DoesNotContain("reason 4", line, StringComparison.Ordinal);
        Assert.Equal(
            "1 record(s) selected: 1 removed from OSDU (reversible), 0 already gone, 0 skipped, 0 failed",
            RemovalSummary.Of(RemovalChoice.Record, 1, [RemovalRecordResult.Removed(new DeliveryKey(Guid.NewGuid()), "a", "A", "dev:x:a", "removed", null)]).Describe());
    }

    [Fact]
    public void What_a_restriction_replaced_keeps_each_record_beside_it_with_the_acl_it_had_and_a_record_alone_keeps_its_acl_as_before()
    {
        var own = new RecordAcl(["data.welldb.viewers@dev.example.com"], ["data.welldb.owners@dev.example.com"]);
        var other = new RecordAcl(["data.welldb.viewers@dev.example.com", "data.extra.viewers@dev.example.com"], ["data.welldb.owners@dev.example.com"]);
        var access = RestrictedAccess.Of(own, [("dev:dataset--File.Generic:a", own), ("dev:dataset--File.Generic:b", other), ("dev:dataset--File.Generic:c", own)]);

        // The records sharing the record's ACL are kept without it; one that had its own keeps it.
        Assert.Equal(2, access.Beside.Count);
        Assert.Equal(["dev:dataset--File.Generic:a", "dev:dataset--File.Generic:c"], access.Beside[0].Ids);
        Assert.Null(access.Beside[0].Acl);
        Assert.True(access.Beside[1].Acl!.SameAs(other));
        var read = RestrictedAccess.Parse(access.ToJson());
        Assert.True(read.Record.SameAs(own));
        Assert.Equal(access.BesideIds, read.BesideIds);
        Assert.All(read.BesideAcls, b => Assert.True(b.Acl.SameAs(b.Id.EndsWith(":b", StringComparison.Ordinal) ? other : own)));
        Assert.Contains("2 record(s) beside it with the same", read.Describe(), StringComparison.Ordinal);

        // A record with nothing beside it keeps what it always kept, and what a record kept before reads as its ACL alone.
        Assert.Equal(own.ToJson(), new RestrictedAccess(own).ToJson());
        Assert.Empty(RestrictedAccess.Parse(own.ToJson()).Beside);
        Assert.True(RecordAcl.Parse(access.ToJson()).SameAs(own));

        Assert.Throws<DeliveryException>(() => RestrictedAccess.Parse("""{"viewers":["a@x.com"],"owners":["b@x.com"],"beside":{"ids":[]}}"""));
        Assert.Throws<DeliveryException>(() => RestrictedAccess.Parse("""{"viewers":["a@x.com"],"owners":["b@x.com"],"beside":[{"viewers":["a@x.com"]}]}"""));
        Assert.Throws<DeliveryException>(() => RestrictedAccess.Parse("""{"viewers":["a@x.com"],"owners":["b@x.com"],"beside":[{"ids":["x"],"viewers":["a@x.com"]}]}"""));
    }

    [Fact]
    public void A_flow_whose_enrichments_add_records_restricts_them_with_the_record_unless_a_workflow_creates_records_the_record_keeps()
    {
        using var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var beside = rig.Enriched.AccessBeside(
            "dev:work-product-component--WellLog:1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Rendering.EnrichParts.RegisteredValue] = """["dev:dataset--File.Generic:las","dev:work-product-component--WellLog:1","dev:dataset--File.Generic:las"]""",
            });
        Assert.Equal(["dev:dataset--File.Generic:las"], beside);
        Assert.NotNull(rig.Enriched.Access);
        Assert.Null(rig.Enriched.AccessRefusal);
        Assert.Empty(rig.Enriched.AccessBeside("dev:work-product-component--WellLog:1", null));

        var storage = Flow(DeliveryProtocol.Storage);
        var stage = new WorkflowStage { Workflow = "las_ingest", Context = new System.Text.Json.Nodes.JsonObject() };
        var kept = storage with { EnrichWorkflows = new Dictionary<string, WorkflowRoute>(StringComparer.Ordinal) { ["lasfile"] = new() { Anchor = WorkflowAnchor.Dataset, Stages = [stage] } } };
        var refused = RestrictionRoute.Of(kept, "osdu:wks:work-product-component--WellLog:1.4.0");
        Assert.False(refused.Restricts);
        Assert.Contains("'lasfile'", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("results.remove", refused.Refusal, StringComparison.Ordinal);
        Assert.Equal(refused.Refusal, RestrictionRoute.BesideRefusal(kept));

        var notTheirs = storage with
        {
            EnrichWorkflows = new Dictionary<string, WorkflowRoute>(StringComparer.Ordinal)
            {
                ["lasfile"] = new() { Anchor = WorkflowAnchor.Dataset, Stages = [stage], Results = new WorkflowResults { Remove = false } },
            },
        };
        Assert.True(RestrictionRoute.Of(notTheirs, "osdu:wks:work-product-component--WellLog:1.4.0").Restricts);
    }

    [Fact]
    public async Task The_check_says_for_each_restriction_whether_the_flow_could_lift_it_asking_entitlements_once()
    {
        using var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var flow = Flow(DeliveryProtocol.Storage);
        var owned = Restriction(1, "legal-hold", Hold);
        var foreign = Restriction(2, "foreign", "data.someone-else@dev.dataservices.energy");
        var invalid = Restriction(3, "not-a-group", "users.data.root@dev.dataservices.energy");

        var check = await RecordRestrictions.CheckAsync(flow, rig.Storage, [owned, foreign, invalid], CancellationToken.None);
        Assert.Equal((platform.MemberEmail, (string?)null), (check.Identity, check.Refusal));
        Assert.Equal([true, false, false], check.Uses.Select(u => u.Usable));
        Assert.Contains("could never be given its access back", check.Uses[1].Refusal, StringComparison.Ordinal);
        Assert.Contains("does not accept in an ACL", check.Uses[2].Refusal, StringComparison.Ordinal);
        Assert.Single(platform.Calls, c => c.Uri.AbsolutePath == StorageRecordAccess.DefaultGroupsPath);

        // Entitlements not answering leaves each unknown, which the run would refuse.
        platform.GroupsFail = HttpStatusCode.ServiceUnavailable;
        var unknown = await RecordRestrictions.CheckAsync(flow, rig.Storage, [owned], CancellationToken.None);
        Assert.Contains("Entitlements could not say", Assert.Single(unknown.Uses).Refusal, StringComparison.Ordinal);
        var thrown = await Assert.ThrowsAsync<DeliveryException>(() => RecordRestrictions.EnsureLiftableAsync(flow, rig.Storage, owned, CancellationToken.None));
        Assert.EndsWith("Nothing was restricted.", thrown.Message, StringComparison.Ordinal);
        platform.GroupsFail = null;

        // A flow that restricts nothing is refused whole, asking OSDU nothing.
        var calls = platform.Calls.Count;
        var stage = new WorkflowStage { Workflow = "las_ingest", Context = new System.Text.Json.Nodes.JsonObject() };
        var kept = flow with { EnrichWorkflows = new Dictionary<string, WorkflowRoute>(StringComparer.Ordinal) { ["lasfile"] = new() { Anchor = WorkflowAnchor.Dataset, Stages = [stage] } } };
        var refused = await RecordRestrictions.CheckAsync(kept, rig.Storage, [owned, foreign], CancellationToken.None);
        Assert.Null(refused.Identity);
        Assert.Contains("cannot restrict its records' access", refused.Refusal, StringComparison.Ordinal);
        Assert.All(refused.Uses, u => Assert.Equal(refused.Refusal, u.Refusal));
        Assert.Equal(calls, platform.Calls.Count);
        Assert.Empty((await RecordRestrictions.CheckAsync(flow, rig.Storage, [], CancellationToken.None)).Uses);
        Assert.Equal(calls, platform.Calls.Count);
    }

    [Fact]
    public void A_check_names_its_restrictions_by_id_each_once()
    {
        Assert.Equal([3, 1], CheckAccessRestrictionsOperation.Ids(" 3, 1,3 ,"));
        Assert.Empty(CheckAccessRestrictionsOperation.Ids(null));
        Assert.Throws<SqlFlowException>(() => CheckAccessRestrictionsOperation.Ids("1,x"));
        Assert.Throws<SqlFlowException>(() => CheckAccessRestrictionsOperation.Ids("0"));
        Assert.Throws<SqlFlowException>(() => CheckAccessRestrictionsOperation.Ids(string.Join(',', Enumerable.Range(1, CheckAccessRestrictionsOperation.MaxRestrictions + 1))));
    }

    private static Ledger.AccessRestrictionState Restriction(int id, string name, string group)
        => new(id, "dev", name, null, new RecordAcl([group], [group]), DateTime.UtcNow, "test", DateTime.UtcNow, "test");

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
            Storage = new OsduRecordProtocol(client, new ProtocolOptions());
            Access = Storage.Access!;
            Enriched = new EnrichPartsProtocol(Storage, client, new ProtocolOptions());
        }

        public IRecordAccess Access { get; }

        /// <summary>The storage route itself.</summary>
        public OsduRecordProtocol Storage { get; }

        /// <summary>The storage route as a flow whose mapping's enrichments add records wraps it.</summary>
        public EnrichPartsProtocol Enriched { get; }

        public void Dispose() => _runtime.Dispose();
    }
}
