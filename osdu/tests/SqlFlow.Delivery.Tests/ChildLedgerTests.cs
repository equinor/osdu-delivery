using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the ledger keeps of the children a record links (osdu/docs/reference/flow/mapping-child.md, What the ledger keeps;
/// When a child lands): the unique segment a record and its children share, kept with whatever the render that looked for
/// them came to (work queued, a hold, a render that sent nothing); and, when a record of any flow lands under an id with that
/// segment in another entity type, the records linking it asked to render again, a record landing under its own id and one
/// held for something else left as they are.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class ChildLedgerTests : IAsyncLifetime, IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _canonical = FlowId.Of("canonical-things");
    private readonly Guid _custom = FlowId.Of("custom-things");

    private OsduLedger Ledger => _db.Ledger(_clock);

    public Task InitializeAsync() => Ledger.RegisterAsync(_canonical, _custom);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static DeliveryKey Key(string name) => DeliveryKey.Derive("test", [name]);

    /// <summary>The unique segment the canonical record of <paramref name="name"/> and its custom child share.</summary>
    private static string LinkKey(string name) => TargetId.Unique(LinkedTemplates.RecordId(name))!;

    private SubmissionState Submission(Guid id, Guid flowId) => new()
    {
        SubmissionId = id,
        FlowId = flowId,
        FlowName = flowId == _canonical ? "canonical-things" : "custom-things",
        MappingReference = flowId == _canonical ? "Thing@1.0.0" : "CustomThing@1.0.0",
        RenderContext = "{}",
        SourceConnection = "${env:OSDU_DATA_DB}",
        SourceObject = "OsduData.arc.Thing",
        RecordCount = 1,
    };

    /// <summary>Queues a canonical record, whose render looked for its children unless <paramref name="linksChildren"/> is false.</summary>
    private async Task<RecordState> StageCanonicalAsync(string name, string hash = "mh-unlinked", bool linksChildren = true)
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission, _canonical));
        var record = Pending(_canonical, name, submission, LinkedTemplates.ParentId(name), hash) with { ChildLinkKey = linksChildren ? LinkKey(name) : null };
        await Ledger.UpsertPendingAsync(_canonical, [record]);
        return record;
    }

    /// <summary>Queues a custom record, the child of the canonical record of the same key.</summary>
    private async Task<RecordState> StageCustomAsync(string name)
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission, _custom));
        var record = Pending(_custom, name, submission, LinkedTemplates.RecordId(name), "mh-custom") with { ParentId = LinkedTemplates.ParentId(name), PendingParentVersion = 1 };
        await Ledger.UpsertPendingAsync(_custom, [record]);
        return record;
    }

    private static RecordState Pending(Guid flowId, string name, Guid submission, string targetId, string hash) => new()
    {
        DeliveryKey = Key(name),
        FlowId = flowId,
        SourceKey = name,
        MappingName = "Thing",
        TargetId = targetId,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = hash,
        PendingMetadata = true,
    };

    /// <summary>Settles a record as a worker's completion does: delivered, or held with its document kept.</summary>
    private async Task CompleteAsync(Guid flowId, DeliveryKey key, RecordStatus status = RecordStatus.Delivered, long version = 1)
    {
        var record = await Ledger.GetRecordAsync(flowId, key) ?? throw new InvalidOperationException($"No record {key}.");
        await Ledger.CompleteAsync(flowId, new RecordCompletion
        {
            DeliveryKey = key,
            Status = status,
            Promote = status == RecordStatus.Delivered,
            TargetVersion = status == RecordStatus.Delivered ? version : null,
            TargetId = record.TargetId,
            Error = status == RecordStatus.Held ? "data.Name: the check before sending held it" : null,
            Claimed = ClaimedWork.Of(record),
            Attempt = new AttemptRecord
            {
                DeliveryKey = key,
                Worker = "test",
                StartedUtc = Now,
                CompletedUtc = Now,
                Outcome = status == RecordStatus.Delivered ? AttemptOutcome.Delivered : AttemptOutcome.Held,
                Phase = "metadata",
                ParentVersion = record.PendingParentVersion,
            },
        });
    }

    [Fact]
    public async Task A_queued_record_keeps_the_unique_segment_it_shares_with_the_children_it_looked_for()
    {
        var record = await StageCanonicalAsync("x");

        Assert.Equal(LinkKey("x"), (await Ledger.GetRecordAsync(_canonical, record.DeliveryKey))!.ChildLinkKey);

        // The same record rendered again under a mapping that links no child forgets it.
        await CompleteAsync(_canonical, record.DeliveryKey);
        await StageCanonicalAsync("x", hash: "mh-other", linksChildren: false);
        Assert.Null((await Ledger.GetRecordAsync(_canonical, record.DeliveryKey))!.ChildLinkKey);
    }

    [Fact]
    public async Task A_record_held_at_its_render_keeps_the_children_its_render_looked_for()
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission, _canonical));
        await Ledger.MarkHeldAsync(_canonical,
        [
            new RecordState
            {
                DeliveryKey = Key("x"),
                FlowId = _canonical,
                SourceKey = "x",
                MappingName = "Thing",
                TargetId = LinkedTemplates.ParentId("x"),
                LastSubmissionId = submission,
                PendingSourceFingerprint = "fp",
                LastError = "osdu.data.WellboreID: searching for the wellbore found no record, and the entry is required",
                ChildLinkKey = LinkKey("x"),
            },
        ]);

        var held = await Ledger.GetRecordAsync(_canonical, Key("x"));
        Assert.Equal((RecordStatus.Held, true, LinkKey("x")), (held!.Status, held.Blocked, held.ChildLinkKey));
    }

    [Fact]
    public async Task A_child_landing_asks_the_record_linking_it_to_render_again()
    {
        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey);
        Assert.Null((await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.PlanRequestedUtc);

        var child = await StageCustomAsync("x");
        await CompleteAsync(_custom, child.DeliveryKey);

        // The canonical record renders again on its flow's next run, links the child, and is sent because its document changed.
        var asked = await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey);
        Assert.Equal((RecordStatus.Delivered, false), (asked!.Status, asked.Blocked));
        Assert.NotNull(asked.PlanRequestedUtc);

        // The child links nothing itself, so its landing asks nothing of it.
        Assert.Null((await Ledger.GetRecordAsync(_custom, child.DeliveryKey))!.PlanRequestedUtc);

        // Asking is no intervention on the record: nothing about it changed but that its flow plans it again.
        Assert.DoesNotContain(
            await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _canonical, DeliveryKey = parent.DeliveryKey.Value }),
            a => a.Kind == "release");
    }

    [Fact]
    public async Task A_record_landing_under_its_own_id_does_not_ask_itself_to_render_again()
    {
        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey);
        await StageCanonicalAsync("x", hash: "mh-linked");
        await CompleteAsync(_canonical, parent.DeliveryKey, version: 2);

        Assert.Null((await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.PlanRequestedUtc);
    }

    [Fact]
    public async Task A_child_landing_reaches_only_the_records_that_link_it()
    {
        var linking = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, linking.DeliveryKey);
        var other = await StageCanonicalAsync("y");
        await CompleteAsync(_canonical, other.DeliveryKey);
        var plain = await StageCanonicalAsync("z", linksChildren: false);
        await CompleteAsync(_canonical, plain.DeliveryKey);

        var child = await StageCustomAsync("x");
        await CompleteAsync(_custom, child.DeliveryKey);
        var orphan = await StageCustomAsync("z");
        await CompleteAsync(_custom, orphan.DeliveryKey);

        Assert.NotNull((await Ledger.GetRecordAsync(_canonical, linking.DeliveryKey))!.PlanRequestedUtc);
        Assert.Null((await Ledger.GetRecordAsync(_canonical, other.DeliveryKey))!.PlanRequestedUtc);

        // A record whose mapping links no child is not asked, whatever lands beside it.
        Assert.Null((await Ledger.GetRecordAsync(_canonical, plain.DeliveryKey))!.PlanRequestedUtc);
    }

    [Fact]
    public async Task A_record_held_for_something_else_is_left_to_its_hold_when_its_child_lands()
    {
        // The check before sending held it, keeping its document: a child never holds its parent, so the child is not why.
        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey, RecordStatus.Held);

        var child = await StageCustomAsync("x");
        await CompleteAsync(_custom, child.DeliveryKey);

        var after = await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey);
        Assert.Equal((RecordStatus.Held, true, (DateTime?)null), (after!.Status, after.Blocked, after.PlanRequestedUtc));
    }

    [Fact]
    public async Task A_record_already_asked_to_be_planned_keeps_the_request_it_had()
    {
        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey);
        var child = await StageCustomAsync("x");
        await CompleteAsync(_custom, child.DeliveryKey);
        var first = (await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.PlanRequestedUtc;
        Assert.NotNull(first);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await StageCustomAsync("x");
        await CompleteAsync(_custom, child.DeliveryKey, version: 2);

        Assert.Equal(first, (await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.PlanRequestedUtc);
    }

    [Fact]
    public async Task A_render_that_sent_nothing_keeps_what_it_found_of_the_records_children()
    {
        // Delivered before its mapping linked children: rendered again under one that does, it renders what OSDU holds while
        // no child is there, and is sent nothing, yet a child landing later still finds it.
        var parent = await StageCanonicalAsync("x", linksChildren: false);
        await CompleteAsync(_canonical, parent.DeliveryKey);
        var delivered = await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey);
        Assert.Null(delivered!.ChildLinkKey);

        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission, _canonical));
        await Ledger.MarkSkippedAsync(_canonical,
        [
            new SkippedRecord
            {
                DeliveryKey = parent.DeliveryKey,
                Kind = SkipKind.Rendered,
                Basis = PlanBasis.Of(delivered),
                Reason = "the document rendered is the one OSDU holds",
                SourceFingerprint = "fp",
                RenderContext = "{\"mapping\":\"linking\"}",
                ChildLinkKey = LinkKey("x"),
            },
        ], submission);
        Assert.Equal(LinkKey("x"), (await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.ChildLinkKey);

        var child = await StageCustomAsync("x");
        await CompleteAsync(_custom, child.DeliveryKey);
        Assert.NotNull((await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.PlanRequestedUtc);
    }
}
