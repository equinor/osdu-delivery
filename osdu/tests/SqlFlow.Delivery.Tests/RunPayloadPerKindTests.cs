using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a run of each of the module's kinds takes in its payload, and what a delivery flow mints its ids with: every kind
/// refuses every payload property it does not take, in the one message shape the delivery kind refuses with; the partition
/// a flow is bound to, however it is bound, is the partition its ids are minted in; and the hold a record whose id moved
/// meets names what can move an id.
/// </summary>
public sealed class RunPayloadPerKindTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    /// <summary>One payload per property a run asks something by, each with a value its parsing accepts.</summary>
    private static readonly IReadOnlyDictionary<string, string> Payloads = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DeliveryRunPayload.ForceProperty] = """{"force":true}""",
        [DeliveryRunPayload.SubmissionIdProperty] = """{"submissionId":"0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f70"}""",
        [DeliveryRunPayload.RunIdProperty] = """{"runId":"0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f71"}""",
        [DeliveryRunPayload.RecordKeysProperty] = """{"recordKeys":["0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f72"]}""",
        [DeliveryRunPayload.RedeliverProperty] = """{"redeliver":"all"}""",
        [DeliveryRunPayload.RerenderProperty] = """{"rerender":true}""",
        [DeliveryRunPayload.SlicesProperty] = """{"slices":[0]}""",
        [DeliveryRunPayload.RateShareProperty] = """{"rateShare":3}""",
        [DeliveryRunPayload.InterfaceProperty] = """{"interface":"logs"}""",
        [DeliveryRunPayload.InterfacesProperty] = """{"interfaces":["logs"]}""",
        [DeliveryRunPayload.ConfirmProperty] = """{"confirm":"dev"}""",
        [DeliveryRunPayload.TestsProperty] = """{"tests":["wellbores"]}""",
        [DeliveryRunPayload.TagsProperty] = """{"tags":["smoke"]}""",
        [DeliveryRunPayload.DimensionsProperty] = """{"dimensions":["Field"]}""",
        [DeliveryRunPayload.InventoriesProperty] = """{"inventories":["WellLogs"]}""",
        [DeliveryRunPayload.RemovalProperty] = """{"removal":{"finding":"orphan","scope":"record","expected":1}}""",
        [DeliveryRunPayload.SelectionProperty] = """{"selection":42}""",
        [DeliveryRunPayload.EntriesProperty] = """{"entries":["wellbores"]}""",
        [DeliveryRunPayload.ApproveProperty] = """{"approve":{"expected":3}}""",
    };

    /// <summary>What each kind's runs take besides the central configuration, as its documentation lists them.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Takes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [FlowDefinition.FlowTypeName] =
        [
            DeliveryRunPayload.ForceProperty, DeliveryRunPayload.SubmissionIdProperty, DeliveryRunPayload.RunIdProperty, DeliveryRunPayload.RecordKeysProperty,
            DeliveryRunPayload.RedeliverProperty, DeliveryRunPayload.RerenderProperty, DeliveryRunPayload.SlicesProperty, DeliveryRunPayload.RateShareProperty,
            DeliveryRunPayload.InterfaceProperty, DeliveryRunPayload.InterfacesProperty, DeliveryRunPayload.ConfirmProperty,
        ],
        [RetrievalDefinition.FlowTypeName] = [DeliveryRunPayload.ForceProperty],
        [CacheDefinition.FlowTypeName] = [],
        [AssertionFlowDefinition.FlowTypeName] = [DeliveryRunPayload.TestsProperty, DeliveryRunPayload.TagsProperty],
        [DimensionFlowDefinition.FlowTypeName] = [DeliveryRunPayload.DimensionsProperty],
        [InventoryFlowDefinition.FlowTypeName] =
            [DeliveryRunPayload.InventoriesProperty, DeliveryRunPayload.RemovalProperty, DeliveryRunPayload.ConfirmProperty, DeliveryRunPayload.SelectionProperty],
        [RemovalFlowDefinition.FlowTypeName] = [DeliveryRunPayload.EntriesProperty, DeliveryRunPayload.ApproveProperty, DeliveryRunPayload.ConfirmProperty],
    };

    private const string Configuration = """
        "references":{"OSDU_URL":"https://osdu.example.test"},"partitionReferences":{"test":{"OSDU_LEGAL_TAG":"test-legal"}}
        """;

    private static IFlowDocumentKind Kind(string flowType) => flowType switch
    {
        FlowDefinition.FlowTypeName => new DeliveryFlowKind(Loader),
        RetrievalDefinition.FlowTypeName => new RetrievalFlowKind(Loader),
        CacheDefinition.FlowTypeName => new CacheFlowKind(Loader),
        AssertionFlowDefinition.FlowTypeName => new AssertionFlowKind(Loader),
        DimensionFlowDefinition.FlowTypeName => new DimensionFlowKind(Loader),
        InventoryFlowDefinition.FlowTypeName => new InventoryFlowKind(Loader),
        RemovalFlowDefinition.FlowTypeName => new RemovalFlowKind(Loader),
        _ => throw new ArgumentOutOfRangeException(nameof(flowType), flowType, "not one of the module's kinds"),
    };

    /// <summary>Every kind of the module with every payload property it does not take.</summary>
    public static TheoryData<string, string> Refused()
    {
        var data = new TheoryData<string, string>();
        foreach (var (flowType, takes) in Takes)
        {
            foreach (var property in Payloads.Keys.Where(p => !takes.Contains(p, StringComparer.Ordinal)))
            {
                data.Add(flowType, property);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Every_kind_refuses_every_payload_property_it_does_not_take_in_the_delivery_kinds_message_shape(string flowType, string property)
    {
        var refused = Assert.Throws<SqlFlowException>(() => Kind(flowType).ValidateParameters(new RunParameters { Payload = Payloads[property] }));

        // The property, the kind, the runs that do take it, and what the kind's payload names: one shape for every kind.
        var kind = $"{(flowType is AssertionFlowDefinition.FlowTypeName or InventoryFlowDefinition.FlowTypeName ? "an" : "a")} {flowType} flow";
        Assert.StartsWith($"payload {property} does not apply to {kind}: only ", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"; {kind}'s payload ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(".", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_keys_each_kind_once_accepted_silently_are_refused_and_name_what_the_kind_takes()
    {
        static string Refusal(IFlowDocumentKind kind, string payload)
            => Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = payload })).Message;

        var retrieval = new RetrievalFlowKind(Loader);
        Assert.Equal(
            "payload interface does not apply to a retrieval flow: only a delivery flow's runs name an interface; a retrieval flow's payload names only force.",
            Refusal(retrieval, Payloads[DeliveryRunPayload.InterfaceProperty]));
        Assert.StartsWith("payload interfaces does not apply to a retrieval flow", Refusal(retrieval, Payloads[DeliveryRunPayload.InterfacesProperty]), StringComparison.Ordinal);
        Assert.StartsWith("payload runId does not apply to a retrieval flow", Refusal(retrieval, Payloads[DeliveryRunPayload.RunIdProperty]), StringComparison.Ordinal);
        Assert.StartsWith("payload confirm does not apply to a retrieval flow", Refusal(retrieval, Payloads[DeliveryRunPayload.ConfirmProperty]), StringComparison.Ordinal);

        var assertion = new AssertionFlowKind(Loader);
        Assert.Equal(
            "payload runId does not apply to an assertion flow: only a delivery flow's reverse run names the run it reverses; an assertion flow's payload names only tests and tags.",
            Refusal(assertion, Payloads[DeliveryRunPayload.RunIdProperty]));
        Assert.StartsWith("payload confirm does not apply to an assertion flow", Refusal(assertion, Payloads[DeliveryRunPayload.ConfirmProperty]), StringComparison.Ordinal);

        var dimension = new DimensionFlowKind(Loader);
        Assert.StartsWith("payload runId does not apply to a dimension flow", Refusal(dimension, Payloads[DeliveryRunPayload.RunIdProperty]), StringComparison.Ordinal);
        Assert.Equal(
            "payload confirm does not apply to a dimension flow: only a delivery flow's run deleting the ledger, an inventory flow's remove run and a removal flow's approved run name the partition they act in; a dimension flow's payload names only dimensions.",
            Refusal(dimension, Payloads[DeliveryRunPayload.ConfirmProperty]));

        Assert.Equal(
            "payload runId does not apply to an inventory flow: only a delivery flow's reverse run names the run it reverses; an inventory flow's payload names only inventories, removal, confirm and selection.",
            Refusal(new InventoryFlowKind(Loader), Payloads[DeliveryRunPayload.RunIdProperty]));

        Assert.Equal(
            "payload submissionId does not apply to a cache flow: only a delivery flow's runs name a submission; a cache flow's payload names only types.",
            Refusal(new CacheFlowKind(Loader), Payloads[DeliveryRunPayload.SubmissionIdProperty]));

        Assert.Equal(
            "payload inventories does not apply to a removal flow: only an inventory flow's runs select inventories; a removal flow's payload names only entries, approve and confirm.",
            Refusal(new RemovalFlowKind(Loader), Payloads[DeliveryRunPayload.InventoriesProperty]));
        Assert.Equal(
            "payload entries does not apply to a delivery flow: only a removal flow's runs select removals; a delivery flow's payload names only force, submissionId, runId, recordKeys, redeliver, rerender, slices, rateShare, interface, interfaces and confirm.",
            Refusal(new DeliveryFlowKind(Loader), Payloads[DeliveryRunPayload.EntriesProperty]));
    }

    [Fact]
    public void Every_kind_takes_what_it_names_beside_the_central_configuration()
    {
        static RunParameters With(string? operation, string named)
            => new() { Operation = operation, Payload = "{" + string.Join(",", new[] { named, Configuration.Trim() }.Where(p => p.Length > 0)) + "}" };

        new DeliveryFlowKind(Loader).ValidateParameters(With(null, "\"force\":true,\"interfaces\":[\"logs\"]"));
        new DeliveryFlowKind(Loader).ValidateParameters(With(DeliveryOperations.Reverse, "\"runId\":\"0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f71\",\"interface\":\"logs\""));
        new DeliveryFlowKind(Loader).ValidateParameters(With(DeliveryOperations.DeleteLedger, "\"confirm\":\"dev\""));
        new RetrievalFlowKind(Loader).ValidateParameters(With(null, "\"force\":true"));
        new CacheFlowKind(Loader).ValidateParameters(With(null, string.Empty));
        new CacheFlowKind(Loader).ValidateParameters(With(DeliveryOperations.Plan, "\"types\":[\"UnitOfMeasure\"]"));
        new AssertionFlowKind(Loader).ValidateParameters(With(null, "\"tests\":[\"wellbores\"],\"tags\":[\"smoke\"]"));
        new DimensionFlowKind(Loader).ValidateParameters(With(DeliveryOperations.Plan, "\"dimensions\":[\"Field\"]"));
        new InventoryFlowKind(Loader).ValidateParameters(With(DeliveryOperations.Reconcile, "\"inventories\":[\"WellLogs\"]"));
        new InventoryFlowKind(Loader).ValidateParameters(With(
            DeliveryOperations.Remove, "\"inventories\":[\"WellLogs\"],\"removal\":{\"finding\":\"orphan\",\"scope\":\"record\",\"expected\":1},\"confirm\":\"dev\""));
        new InventoryFlowKind(Loader).ValidateParameters(With(DeliveryOperations.Match, "\"inventories\":[\"WellLogs\"],\"selection\":7"));
        new RemovalFlowKind(Loader).ValidateParameters(With(null, "\"entries\":[\"wellbores\"]"));
        new RemovalFlowKind(Loader).ValidateParameters(With(DeliveryOperations.Plan, "\"entries\":[\"wellbores\"]"));
        new RemovalFlowKind(Loader).ValidateParameters(With(
            DeliveryOperations.Remove, "\"entries\":[\"wellbores\"],\"approve\":{\"expected\":3},\"confirm\":\"dev\""));

        // A property written as false or empty asks for nothing, so no kind refuses it, as the delivery kind never has.
        new CacheFlowKind(Loader).ValidateParameters(new RunParameters { Payload = """{"force":false,"recordKeys":[],"tests":[]}""" });
    }

    /// <summary>
    /// How many runs of a fan-out share the flow's rate limit travels with each member: only an intake or drain member on its
    /// coordinating run's submission carries it, and it counts the coordinating run and at most every member a flow allows.
    /// </summary>
    [Fact]
    public void A_rate_share_travels_with_a_fan_out_member_and_counts_the_runs_that_share_the_rate()
    {
        const string Submission = "\"submissionId\":\"0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f70\"";
        static string Refusal(string payload, string operation)
            => Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(payload).Validate(operation)).Message;

        var drain = DeliveryRunPayload.Parse("{" + Submission + ",\"rateShare\":9}");
        drain.Validate(DeliveryOperations.Drain);
        Assert.Equal(9, drain.RateShare);
        Assert.Equal("{" + Submission + ",\"rateShare\":9}", drain.ToJson());
        DeliveryRunPayload.Parse("{" + Submission + ",\"slices\":[1],\"rateShare\":65}").Validate(DeliveryOperations.Intake);
        Assert.Null(DeliveryRunPayload.Parse("{" + Submission + "}").RateShare);

        Assert.Equal(
            "payload rateShare does not apply to the deliver operation: only a fan-out member shares its coordinating run's rate limit.",
            Refusal("""{"rateShare":2}""", DeliveryOperations.Deliver));
        Assert.Equal(
            "payload rateShare does not apply to the verify operation: only a fan-out member shares its coordinating run's rate limit.",
            Refusal("""{"rateShare":2}""", DeliveryOperations.Verify));
        Assert.Equal(
            "payload rateShare does not apply to the drain operation: a member shares the rate of the run whose submission it works on, which submissionId names.",
            Refusal("""{"rateShare":2}""", DeliveryOperations.Drain));
        Assert.Equal(
            "payload rateShare is 1; a fan-out's runs number 2 to 65, its coordinating run and at most 64 members.",
            Refusal("{" + Submission + ",\"rateShare\":1}", DeliveryOperations.Drain));
        Assert.StartsWith("payload rateShare is 66;", Refusal("{" + Submission + ",\"rateShare\":66}", DeliveryOperations.Intake), StringComparison.Ordinal);
        Assert.Equal(
            "payload rateShare must be a whole number: how many runs of the fan-out share the rate limit.",
            Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"rateShare":"two"}""")).Message);
    }

    [Fact]
    public void A_payload_names_every_property_it_asks_by_once_and_the_central_configuration_never()
    {
        foreach (var (property, json) in Payloads)
        {
            var payload = DeliveryRunPayload.Parse(json);
            Assert.Equal([property], payload.Named());
            Assert.False(payload.CarriesOnlyConfiguration, property);
            Assert.False(payload.IsEmpty, property);
            Assert.Equal(json, payload.ToJson());
        }

        var configured = DeliveryRunPayload.Parse("{" + Configuration.Trim() + "}");
        Assert.Empty(configured.Named());
        Assert.True(configured.CarriesOnlyConfiguration);
        Assert.False(configured.IsEmpty);
        Assert.True(DeliveryRunPayload.Parse("""{"force":false,"slices":[]}""").IsEmpty);
        Assert.True(DeliveryRunPayload.None.IsEmpty);
    }

    [Fact]
    public void A_flow_bound_by_its_header_mints_its_ids_in_the_partition_its_header_names()
    {
        static FlowDefinition HeaderBound(string partition) => Samples.Targeting(new FlowTarget
        {
            Endpoint = "https://osdu.example.test",
            Protocol = DeliveryProtocol.Storage,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = partition },
        });

        // A literal header is the partition, as written; a reference is resolved like every other reference the flow names,
        // so the ids and the requests read the same value. The partition never comes from OSDU_DATA_PARTITION instead.
        var literal = DeliveryDestination.Supplied(HeaderBound(" opendes "), [DeliveryDestination.DataPartitionParameter, DeliveryDestination.LegalTagParameter]);
        Assert.Equal("opendes", literal[DeliveryDestination.DataPartitionParameter]);
        Assert.Equal("${env:OSDU_LEGAL_TAG}", literal[DeliveryDestination.LegalTagParameter]);
        Assert.Equal("${env:TARGET_PARTITION}", DeliveryDestination.Supplied(HeaderBound("${env:TARGET_PARTITION}"), [DeliveryDestination.DataPartitionParameter])[DeliveryDestination.DataPartitionParameter]);
        Assert.Equal("opendes", DeliveryDestination.BoundPartition(HeaderBound("opendes")));

        // A value the flow supplies still wins.
        var pinned = HeaderBound("opendes") with
        {
            Render = new FlowRender
            {
                Mapping = "Thing@1.0.0",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [DeliveryDestination.DataPartitionParameter] = "elsewhere" },
            },
        };
        Assert.Equal("elsewhere", DeliveryDestination.Supplied(pinned, [DeliveryDestination.DataPartitionParameter])[DeliveryDestination.DataPartitionParameter]);
    }

    [Fact]
    public void A_flow_that_works_in_partitions_mints_in_the_one_it_is_bound_to_and_one_bound_to_none_falls_back_to_the_reference()
    {
        var named = Samples.Targeting(new FlowTarget { Endpoint = "https://osdu.example.test", Protocol = DeliveryProtocol.Storage }) with
        {
            Partitions = [new DeclaredPartition("dev"), new DeclaredPartition("test")],
        };

        var bound = named.ForPartition("test");
        Assert.Equal("test", DeliveryDestination.BoundPartition(bound));
        Assert.Equal("test", DeliveryDestination.Supplied(bound, DeliveryDestination.Parameters)[DeliveryDestination.DataPartitionParameter]);

        Assert.Null(DeliveryDestination.BoundPartition(named));
        Assert.Equal("${env:OSDU_DATA_PARTITION}", DeliveryDestination.Supplied(named, [DeliveryDestination.DataPartitionParameter])[DeliveryDestination.DataPartitionParameter]);
    }

    [Fact]
    public void A_record_whose_id_moved_is_told_what_moves_an_id_and_not_what_makes_another_record()
    {
        var reason = Planner.ChangedTargetId("dev:master-data--Wellbore:a", "test:master-data--Wellbore:a");

        Assert.StartsWith("the mapping now gives this record the OSDU id test:master-data--Wellbore:a, and the record claimed dev:master-data--Wellbore:a", reason, StringComparison.Ordinal);
        Assert.EndsWith(
            "or put back what moved the id: the mapping's dataset.idFrom, the entity type of the kind it renders, or the partition the flow mints ids in (dataPartition)",
            reason, StringComparison.Ordinal);

        // A changed system or key derives another delivery key, so another record: it never meets this hold, and the hold
        // never sends anyone to put them back.
        Assert.DoesNotContain("system", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("and key", reason, StringComparison.Ordinal);
    }
}
