using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the ledger keeps of a record's parent (osdu/docs/reference/flow/mapping-parent.md, What the ledger keeps; When the
/// parent changes): the parent a record reads and the version each document was rendered from, the record's and each try's;
/// and, when a record of any flow lands under a parent's id, the records that read it planned again: one held because the
/// parent was not there released under an intervention the trail shows, one rendered from it asked to render again, and one
/// held with its document kept left to its own hold.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class ParentLedgerTests : IAsyncLifetime, IDisposable
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

    /// <summary>Queues a canonical record: its OSDU id is the parent id of the custom record of the same key.</summary>
    private async Task<RecordState> StageCanonicalAsync(string name)
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission, _canonical));
        var record = Pending(_canonical, name, submission, LinkedTemplates.ParentId(name), parentId: null, parentVersion: null);
        await Ledger.UpsertPendingAsync(_canonical, [record]);
        return record;
    }

    /// <summary>Queues a custom record rendered from version <paramref name="parentVersion"/> of its parent.</summary>
    private async Task<RecordState> StageCustomAsync(string name, long? parentVersion, Guid? submission = null)
    {
        var id = submission ?? Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(id, _custom));
        var record = Pending(_custom, name, id, LinkedTemplates.RecordId(name), LinkedTemplates.ParentId(name), parentVersion);
        await Ledger.UpsertPendingAsync(_custom, [record]);
        return record;
    }

    private static RecordState Pending(Guid flowId, string name, Guid submission, string targetId, string? parentId, long? parentVersion) => new()
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
        PendingMetadataHash = "mh-" + parentVersion,
        PendingMetadata = true,
        ParentId = parentId,
        PendingParentVersion = parentVersion,
    };

    /// <summary>Holds a custom record at its render, its parent not found: no document, the parent it looked for kept.</summary>
    private async Task HoldCustomAsync(string name, long? parentVersion = null)
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission, _custom));
        await Ledger.MarkHeldAsync(_custom,
        [
            new RecordState
            {
                DeliveryKey = Key(name),
                FlowId = _custom,
                SourceKey = name,
                MappingName = "CustomThing",
                TargetId = LinkedTemplates.RecordId(name),
                LastSubmissionId = submission,
                PendingSourceFingerprint = "fp",
                LastError = $"osdu.data.Nested: the record's parent {LinkedTemplates.ParentId(name)} ({LinkedTemplates.ParentKind}) is not in OSDU, or the flow's identity may not read it, and the record takes data.Nested from it",
                ParentId = LinkedTemplates.ParentId(name),
                PendingParentVersion = parentVersion,
            },
        ]);
    }

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
    public async Task A_queued_record_keeps_the_parent_it_read_and_takes_its_version_once_delivered()
    {
        var record = await StageCustomAsync("x", parentVersion: 5);

        var queued = await Ledger.GetRecordAsync(_custom, record.DeliveryKey);
        Assert.Equal((LinkedTemplates.ParentId("x"), (long?)5, (long?)null), (queued!.ParentId, queued.PendingParentVersion, queued.ParentVersion));

        await CompleteAsync(_custom, record.DeliveryKey);

        var delivered = await Ledger.GetRecordAsync(_custom, record.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Equal((long?)5, delivered.ParentVersion);
        Assert.Equal((long?)5, Assert.Single(await Ledger.ListAttemptsAsync(_custom, record.DeliveryKey, 10)).ParentVersion);

        // Work rendered from a later version of the parent: the record keeps what OSDU holds until that work lands.
        await StageCustomAsync("x", parentVersion: 6);
        var again = await Ledger.GetRecordAsync(_custom, record.DeliveryKey);
        Assert.Equal(((long?)5, (long?)6), (again!.ParentVersion, again.PendingParentVersion));
        await CompleteAsync(_custom, record.DeliveryKey, version: 2);
        Assert.Equal((long?)6, (await Ledger.GetRecordAsync(_custom, record.DeliveryKey))!.ParentVersion);
    }

    [Fact]
    public async Task A_record_held_at_its_render_keeps_the_parent_it_found_missing()
    {
        await HoldCustomAsync("x");

        var held = await Ledger.GetRecordAsync(_custom, Key("x"));
        Assert.Equal(RecordStatus.Held, held!.Status);
        Assert.True(held.Blocked);
        Assert.Null(held.PendingDocumentRef);
        Assert.Equal((LinkedTemplates.ParentId("x"), (long?)null), (held.ParentId, held.PendingParentVersion));
        Assert.Null(Assert.Single(await Ledger.ListAttemptsAsync(_custom, Key("x"), 10)).ParentVersion);
    }

    [Fact]
    public async Task A_parent_landing_releases_the_records_held_without_it_and_the_trail_says_why()
    {
        await HoldCustomAsync("x");
        var parent = await StageCanonicalAsync("x");

        await CompleteAsync(_canonical, parent.DeliveryKey);

        var released = await Ledger.GetRecordAsync(_custom, Key("x"));
        Assert.False(released!.Blocked);
        Assert.NotNull(released.PlanRequestedUtc);

        var activity = Assert.Single(
            await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _custom, DeliveryKey = Key("x").Value }),
            a => a.Actor == OsduLedger.ParentActor);
        Assert.Equal(("release", "completed"), (activity.Kind, activity.Outcome));
        Assert.Contains(LinkedTemplates.ParentId("x"), activity.Summary, StringComparison.Ordinal);
        Assert.Contains("landed in OSDU", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parent_landing_again_asks_the_records_rendered_from_it_to_render_again()
    {
        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey);
        var child = await StageCustomAsync("x", parentVersion: 1);
        await CompleteAsync(_custom, child.DeliveryKey);
        Assert.Null((await Ledger.GetRecordAsync(_custom, child.DeliveryKey))!.PlanRequestedUtc);

        // The parent's next version lands: the child renders again on its flow's next run, and is sent if it renders differently.
        await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey, version: 2);

        var asked = await Ledger.GetRecordAsync(_custom, child.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, asked!.Status);
        Assert.NotNull(asked.PlanRequestedUtc);
        Assert.False(asked.Blocked);

        // Asking is no intervention on the record: nothing about it changed but that its flow plans it again.
        Assert.DoesNotContain(
            await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _custom, DeliveryKey = child.DeliveryKey.Value }),
            a => a.Actor == OsduLedger.ParentActor);
    }

    [Fact]
    public async Task A_record_held_with_its_document_kept_is_left_to_its_own_hold_when_its_parent_lands()
    {
        // The check before sending held it, keeping the document a release would send as it is: the parent is not why.
        var child = await StageCustomAsync("x", parentVersion: 1);
        await CompleteAsync(_custom, child.DeliveryKey, RecordStatus.Held);
        var before = await Ledger.GetRecordAsync(_custom, child.DeliveryKey);
        Assert.True(before!.Blocked);
        Assert.NotNull(before.PendingDocumentRef);

        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey);

        var after = await Ledger.GetRecordAsync(_custom, child.DeliveryKey);
        Assert.Equal((RecordStatus.Held, true, (DateTime?)null), (after!.Status, after.Blocked, after.PlanRequestedUtc));
    }

    [Fact]
    public async Task A_parent_landing_reaches_only_the_records_that_read_it()
    {
        await HoldCustomAsync("x");
        await HoldCustomAsync("y");
        var parent = await StageCanonicalAsync("x");

        await CompleteAsync(_canonical, parent.DeliveryKey);

        Assert.False((await Ledger.GetRecordAsync(_custom, Key("x")))!.Blocked);
        var other = await Ledger.GetRecordAsync(_custom, Key("y"));
        Assert.True(other!.Blocked);
        Assert.Null(other.PlanRequestedUtc);

        // A canonical record reads no parent itself, so its own landing asks nothing of it.
        Assert.Null((await Ledger.GetRecordAsync(_canonical, parent.DeliveryKey))!.PlanRequestedUtc);
    }

    [Fact]
    public async Task A_record_already_asked_to_be_planned_keeps_the_request_it_had()
    {
        var parent = await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey);
        var child = await StageCustomAsync("x", parentVersion: 1);
        await CompleteAsync(_custom, child.DeliveryKey);

        await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey, version: 2);
        var first = (await Ledger.GetRecordAsync(_custom, child.DeliveryKey))!.PlanRequestedUtc;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await StageCanonicalAsync("x");
        await CompleteAsync(_canonical, parent.DeliveryKey, version: 3);

        Assert.Equal(first, (await Ledger.GetRecordAsync(_custom, child.DeliveryKey))!.PlanRequestedUtc);
    }
}
