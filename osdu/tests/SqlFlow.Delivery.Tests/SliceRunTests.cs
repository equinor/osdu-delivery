using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A fan-out's slice runs (osdu/docs/reference/concepts/submissions.md, Many nodes): each delivers a share of neighbouring
/// key slices, works on that share's batches alone, and reports what it planned for its coordinating run to add up.
/// </summary>
public sealed class SliceRunTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_share_works_on_the_batches_of_its_slices_and_on_no_other_share_s()
    {
        Assert.Equal(new BatchRange(0, 2 * SubmissionIntake.BatchesPerPartition), FlowRuntime.BatchesOf([0, 1]));
        Assert.Equal(new BatchRange(3 * SubmissionIntake.BatchesPerPartition, 5 * SubmissionIntake.BatchesPerPartition), FlowRuntime.BatchesOf([4, 3]));

        var share = FlowRuntime.BatchesOf([3, 4]);
        Assert.True(share.Contains(3 * SubmissionIntake.BatchesPerPartition));
        Assert.True(share.Contains((5 * SubmissionIntake.BatchesPerPartition) - 1));
        Assert.False(share.Contains((3 * SubmissionIntake.BatchesPerPartition) - 1));
        Assert.False(share.Contains(5 * SubmissionIntake.BatchesPerPartition));

        // The last slice a fan-out can cut still has its whole namespace.
        var last = FlowRuntime.BatchesOf([SubmissionIntake.MaxFanOutPartition]);
        Assert.Equal(SubmissionIntake.BatchBase(SubmissionIntake.MaxFanOutPartition) + SubmissionIntake.BatchesPerPartition, last.Beyond);
    }

    [Fact]
    public void A_share_of_slices_that_are_not_neighbours_is_refused_since_its_range_would_take_another_run_s_batches()
    {
        Assert.Contains("are not neighbours", Assert.Throws<DeliveryException>(() => FlowRuntime.BatchesOf([1, 3])).Message, StringComparison.Ordinal);
        Assert.Contains("at least one key slice", Assert.Throws<DeliveryException>(() => FlowRuntime.BatchesOf([])).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchRange(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchRange(4, 4));
    }

    [Fact]
    public void A_slice_run_reports_what_it_planned_of_its_share_and_its_coordinating_run_reads_it_back()
    {
        var submission = new SubmissionState
        {
            SubmissionId = Guid.NewGuid(),
            FlowId = Guid.NewGuid(),
            FlowName = "welldb-welllog-03-header-delivery",
            MappingReference = "WellLog@1.4.0",
            RenderContext = "{}",
            SourceObject = "[silver].[WellLog]",
            SourceConnection = "${env:OSDU_DATA_DB}",
            Status = SubmissionStatus.Planned,
        };
        var counts = new IntakeCounts(600, 590, 6, 2, 1, 1, 2, 0, 0);
        var run = new RunResult(new IntakeResult(submission, null, counts, AlreadyProcessed: false), new WorkerSummary(590, 588, 0, 0, 2, 2), submission);

        var outcome = DeliverOutcome.OfShare(run, DeliveryOperations.Deliver, "[silver].[WellLog]", "incremental", [2, 3]);
        Assert.Equal("2,3", outcome.Share!.Slices);
        Assert.Equal(588, outcome.Delivered);

        // What a node journals is the outcome as JSON, and what the coordinating run adds up comes back from it as it was.
        var share = DeliverOutcome.ShareOf(JsonSerializer.Serialize(outcome, Json));
        Assert.Equal(counts, share!.ToCounts());
        Assert.Equal(counts, DeliverOutcome.ShareOf(JsonSerializer.Serialize(outcome))!.ToCounts());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"operation\":\"deliver\"}")]
    [InlineData("{\"share\":null}")]
    [InlineData("{\"share\":{\"operation\":\"drain\"}}")]
    public void A_result_with_no_share_in_it_reports_none_and_the_share_is_delivered_again(string? json)
        => Assert.Null(DeliverOutcome.ShareOf(json));

    [Fact]
    public void A_slice_run_names_its_submission_and_a_share_of_neighbouring_slices_and_asks_for_nothing_its_coordinating_run_did()
    {
        var submission = Guid.NewGuid();
        new DeliveryRunPayload { SubmissionId = submission, Slices = [2, 3, 4], RateShare = 3 }.Validate(DeliveryOperations.Deliver);

        string Refused(DeliveryRunPayload payload) => Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Deliver)).Message;
        Assert.Contains("which submissionId names", Refused(new DeliveryRunPayload { Slices = [0] }), StringComparison.Ordinal);
        Assert.Contains("a share of neighbouring key slices", Refused(new DeliveryRunPayload { SubmissionId = submission, Slices = [0, 2] }), StringComparison.Ordinal);
        Assert.Contains("only a slice run of a fan-out shares", Refused(new DeliveryRunPayload { SubmissionId = submission, RateShare = 2 }), StringComparison.Ordinal);
        Assert.Contains("a run works on a submission or is scoped to records, not both", Refused(new DeliveryRunPayload { SubmissionId = submission, Slices = [0], RecordKeys = [Guid.NewGuid()] }), StringComparison.Ordinal);
        Assert.Contains("its coordinating run marked what is sent again", Refused(new DeliveryRunPayload { SubmissionId = submission, Slices = [0], Redeliver = RedeliverScopes.All }), StringComparison.Ordinal);
        Assert.Contains("asked for records to be rendered again", Refused(new DeliveryRunPayload { SubmissionId = submission, Slices = [0], Rerender = true }), StringComparison.Ordinal);
    }
}
