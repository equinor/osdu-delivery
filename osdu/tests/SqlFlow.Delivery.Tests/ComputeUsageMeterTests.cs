using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.ComputeUsage;
using SqlFlow.Delivery.Documents;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a node's work is measured (osdu/docs/reference/concepts/compute-usage.md): the compute unit, the wall-clock time
/// from the poll that hands work out to the outcome the node reports, the slots the work held, who it is charged to, and
/// the work a node gives up without an outcome.
/// </summary>
public sealed class ComputeUsageMeterTests
{
    private const string Node = "node-1";

    private static readonly Guid Repo = Guid.Parse("7d0c5e3a-2b1f-4a6e-9c8d-1f2e3a4b5c6d");

    private readonly TestClock _clock = new();

    [Theory]
    [InlineData(3_600_000, 1, 1, 1, 1.0)]
    [InlineData(3_600_000, 8, 6, 6, 8.0)]
    [InlineData(1_800_000, 8, 1, 6, 8.0 * 0.5 / 6)]
    [InlineData(3_600_000, 8, 4, 6, 8.0 * 4 / 6)]
    [InlineData(3_600_000, 8, 9, 6, 8.0)]
    [InlineData(0, 8, 1, 6, 0.0)]
    public void A_compute_unit_is_one_core_held_for_one_hour(long wallClockMs, int cores, int slots, int nodeSlots, double units)
        => Assert.Equal(units, ComputeUnits.Of(wallClockMs, cores, slots, nodeSlots), 12);

    [Fact]
    public void Units_refuse_a_node_with_no_core_or_no_slot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComputeUnits.Of(-1, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComputeUnits.Of(1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComputeUnits.Of(1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComputeUnits.Of(1, 1, 1, 0));
    }

    [Fact]
    public void A_run_is_measured_from_the_poll_that_hands_it_out_to_the_outcome_the_node_reports()
    {
        var meter = Meter(cores: 8);
        var run = Guid.NewGuid();
        var started = _clock.GetUtcNow().UtcDateTime;
        meter.Polled(Poll(), Answer(runs: [Handout(run, attempt: 2, "wells/ingest/01-delivery.yaml", "wells-delivery", "user:ada")]));
        Assert.Equal(1, meter.InFlight);

        _clock.Advance(TimeSpan.FromMinutes(90));
        meter.RunEnded(run, new RunOutcomeRequest(Node, 2, RunOutcomeKind.Completed, null, "{}"));

        var work = Assert.Single(Drain(meter));
        Assert.Equal(Node, work.Node);
        Assert.Equal(ComputeWorkKinds.Run, work.Kind);
        Assert.Equal(run, work.WorkId);
        Assert.Equal(2, work.Attempt);
        Assert.Equal(Repo, work.RepoId);
        Assert.Equal("estate", work.Repo);
        Assert.Equal("wells/ingest", work.FlowFolder);
        Assert.Equal("wells", work.Project);
        Assert.Equal("wells-delivery", work.Flow);
        Assert.Equal("user:ada", work.RequestedBy);
        Assert.Equal(started, work.StartedUtc);
        Assert.Equal(started.AddMinutes(90), work.EndedUtc);
        Assert.Equal(90 * 60_000, work.WallClockMs);
        Assert.Equal(8, work.Cores);
        Assert.Equal(1, work.Slots);
        Assert.Equal(6, work.NodeSlots);
        Assert.Equal(ComputeOutcomes.Completed, work.Outcome);
        Assert.Equal(1.5 * 8 / 6, work.Units, 12);
        Assert.Equal(0, meter.InFlight);
    }

    [Fact]
    public void A_run_that_takes_the_whole_node_holds_every_run_slot()
    {
        var meter = Meter(cores: 4);
        var run = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(run, 1, "f.yaml", "f", null, wholeNode: true)]));
        _clock.Advance(TimeSpan.FromHours(1));
        meter.RunEnded(run, new RunOutcomeRequest(Node, 1, RunOutcomeKind.Failed, "boom", null));

        var work = Assert.Single(Drain(meter));
        Assert.Equal(4, work.Slots);
        Assert.Equal(6, work.NodeSlots);
        Assert.Equal(string.Empty, work.FlowFolder);
        Assert.Equal(string.Empty, work.Project);
        Assert.Equal(ComputeOutcomes.Failed, work.Outcome);
        Assert.Equal(4.0 * 4 / 6, work.Units, 12);
    }

    [Fact]
    public void A_compute_task_is_measured_with_the_repository_and_requester_its_arguments_name()
    {
        var meter = Meter(cores: 2);
        var task = Guid.NewGuid();
        meter.Polled(Poll(), Answer(tasks: [new TaskHandout(task, new TaskSpec("delivery-check-values", "wells-delivery", $$"""{"repoId":"{{Repo}}","actor":"user:ada","partition":"dev"}"""))]));
        _clock.Advance(TimeSpan.FromSeconds(30));
        meter.TaskEnded(task, new TaskOutcomeRequest(Node, TaskOutcomeKind.Succeeded, null, "{}"));

        var work = Assert.Single(Drain(meter));
        Assert.Equal(ComputeWorkKinds.Task, work.Kind);
        Assert.Equal(task, work.WorkId);
        Assert.Equal(0, work.Attempt);
        Assert.Equal(Repo, work.RepoId);
        Assert.Equal(string.Empty, work.Repo);
        Assert.Equal("delivery-check-values", work.Flow);
        Assert.Equal("user:ada", work.RequestedBy);
        Assert.Equal(30_000, work.WallClockMs);
        Assert.Equal(1, work.Slots);
        Assert.Equal(ComputeOutcomes.Completed, work.Outcome);
    }

    [Theory]
    [InlineData("select 1")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void A_task_whose_arguments_name_no_repository_is_measured_all_the_same(string arguments)
    {
        var meter = Meter(cores: 2);
        var task = Guid.NewGuid();
        meter.Polled(Poll(), Answer(tasks: [new TaskHandout(task, new TaskSpec("query", "warehouse", arguments))]));
        meter.TaskEnded(task, new TaskOutcomeRequest(Node, TaskOutcomeKind.Cancelled, null, null));

        var work = Assert.Single(Drain(meter));
        Assert.Null(work.RepoId);
        Assert.Null(work.RequestedBy);
        Assert.Equal(ComputeOutcomes.Cancelled, work.Outcome);
    }

    [Fact]
    public void An_outcome_reported_twice_is_measured_once()
    {
        var meter = Meter(cores: 2);
        var run = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(run, 1, "f.yaml", "f", null)]));
        var outcome = new RunOutcomeRequest(Node, 1, RunOutcomeKind.Cancelled, null, null);
        meter.RunEnded(run, outcome);
        meter.RunEnded(run, outcome);

        Assert.Equal(ComputeOutcomes.Cancelled, Assert.Single(Drain(meter)).Outcome);
    }

    [Fact]
    public void Work_the_node_no_longer_holds_on_its_next_poll_ended_without_an_outcome()
    {
        var meter = Meter(cores: 2);
        var kept = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var task = Guid.NewGuid();
        meter.Polled(Poll(), Answer(
            runs: [Handout(kept, 1, "a/f.yaml", "kept", null), Handout(gone, 1, "a/g.yaml", "gone", null)],
            tasks: [new TaskHandout(task, new TaskSpec("query", "warehouse", "{}"))]));

        _clock.Advance(TimeSpan.FromMinutes(5));
        meter.Polled(Poll(holdingRuns: [new HeldRun(kept, 1)]), Answer());

        var ended = Drain(meter);
        Assert.Equal(2, ended.Count);
        Assert.All(ended, w => Assert.Equal(ComputeOutcomes.Interrupted, w.Outcome));
        Assert.All(ended, w => Assert.Equal(5 * 60_000, w.WallClockMs));
        Assert.Contains(ended, w => w.WorkId == gone);
        Assert.Contains(ended, w => w.WorkId == task);
        Assert.Equal(1, meter.InFlight);
    }

    [Fact]
    public void A_lease_the_control_plane_revokes_ends_the_work_with_that_poll()
    {
        var meter = Meter(cores: 2);
        var run = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(run, 1, "f.yaml", "f", null)]));
        _clock.Advance(TimeSpan.FromMinutes(2));
        meter.Polled(
            Poll(holdingRuns: [new HeldRun(run, 1)]),
            new NodePollResponse([], [], [], [], [run], [], false, 90));

        var work = Assert.Single(Drain(meter));
        Assert.Equal(ComputeOutcomes.Interrupted, work.Outcome);
        Assert.Equal(120_000, work.WallClockMs);
    }

    [Fact]
    public void Another_nodes_poll_ends_nothing_of_this_node()
    {
        var meter = Meter(cores: 2);
        var run = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(run, 1, "f.yaml", "f", null)]));
        meter.Polled(Poll(node: "node-2"), Answer());

        Assert.Empty(Drain(meter));
        Assert.Equal(1, meter.InFlight);
    }

    [Fact]
    public void The_same_run_handed_out_again_ends_the_earlier_hand_out()
    {
        var meter = Meter(cores: 2);
        var run = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(run, 1, "f.yaml", "f", null)]));
        _clock.Advance(TimeSpan.FromMinutes(1));
        meter.Polled(Poll(holdingRuns: [new HeldRun(run, 1)]), Answer(runs: [Handout(run, 2, "f.yaml", "f", null)]));
        _clock.Advance(TimeSpan.FromMinutes(3));
        meter.RunEnded(run, new RunOutcomeRequest(Node, 2, RunOutcomeKind.Completed, null, "{}"));

        var ended = Drain(meter);
        Assert.Equal(2, ended.Count);
        Assert.Equal((1, ComputeOutcomes.Interrupted, 60_000L), (ended[0].Attempt, ended[0].Outcome, ended[0].WallClockMs));
        Assert.Equal((2, ComputeOutcomes.Completed, 180_000L), (ended[1].Attempt, ended[1].Outcome, ended[1].WallClockMs));
    }

    [Fact]
    public void A_stopping_node_ends_what_it_still_holds_and_measures_nothing_more()
    {
        var meter = Meter(cores: 2);
        var run = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(run, 1, "f.yaml", "f", null)]));
        _clock.Advance(TimeSpan.FromSeconds(10));
        meter.Stop();

        var work = Assert.Single(Drain(meter));
        Assert.Equal(ComputeOutcomes.Interrupted, work.Outcome);
        Assert.Equal(10_000, work.WallClockMs);
        Assert.True(meter.Measured.Completion.IsCompleted);
    }

    [Fact]
    public void A_full_queue_drops_the_measurement_and_counts_it()
    {
        var meter = new ComputeUsageMeter(_clock, cores: 2, capacity: 1);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        meter.Polled(Poll(), Answer(runs: [Handout(first, 1, "f.yaml", "f", null), Handout(second, 1, "g.yaml", "g", null)]));
        meter.RunEnded(first, new RunOutcomeRequest(Node, 1, RunOutcomeKind.Completed, null, "{}"));
        meter.RunEnded(second, new RunOutcomeRequest(Node, 1, RunOutcomeKind.Completed, null, "{}"));

        Assert.Equal(first, Assert.Single(Drain(meter)).WorkId);
        Assert.Equal(1, meter.Dropped);
    }

    [Theory]
    [InlineData(null, "", "")]
    [InlineData("", "", "")]
    [InlineData("flow.yaml", "", "")]
    [InlineData("wells/flow.yaml", "wells", "wells")]
    [InlineData("wells/ingest/flow.yaml", "wells/ingest", "wells")]
    [InlineData("wells\\ingest\\flow.yaml", "wells/ingest", "wells")]
    public void A_flows_folder_and_project_come_from_its_path_in_the_repository(string? path, string folder, string project)
    {
        Assert.Equal(folder, ComputeUsageMeter.FolderOf(path));
        Assert.Equal(project, ComputeUsageMeter.ProjectOf(path));
    }

    [Fact]
    public void A_project_longer_than_its_column_is_cut()
        => Assert.Equal(new string('p', 100), ComputeUsageMeter.ProjectOf(new string('p', 140) + "/flow.yaml"));

    [Theory]
    [InlineData("wells/ingest", "ingest-team")]
    [InlineData("wells/ingest/deep", "ingest-team")]
    [InlineData("wells/ingestion", "wells-team")]
    [InlineData("wells", "wells-team")]
    [InlineData("logs", "estate-team")]
    [InlineData("", "estate-team")]
    [InlineData("Wells/ingest", "estate-team")]
    public void A_flow_belongs_to_the_team_of_the_nearest_folder_above_it(string flowFolder, string team)
        => Assert.Equal(team, ComputeUsageStore.TeamOf(flowFolder, [("", "estate-team"), ("wells", "wells-team"), ("wells/ingest", "ingest-team")]));

    [Fact]
    public void A_flow_under_no_folder_that_names_a_team_has_none()
    {
        Assert.Equal(string.Empty, ComputeUsageStore.TeamOf("logs", [("wells", "wells-team")]));
        Assert.Equal(string.Empty, ComputeUsageStore.TeamOf("logs", []));
    }

    [Fact]
    public void A_repository_document_names_its_team()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Equal("Subsurface data", loader.ParseRepository("documentType: repository\ncode: wl\nteam: '  Subsurface data '\n", "osdu-delivery.yaml").Team);
        Assert.Null(loader.ParseRepository("documentType: repository\ncode: wl\n", "osdu-delivery.yaml").Team);
        Assert.Null(loader.ParseRepository("documentType: repository\ncode: wl\nteam: ''\n", "osdu-delivery.yaml").Team);

        var tooLong = new string('t', 65);
        Assert.Equal(
            $"osdu-delivery.yaml: team '{tooLong}' is not a team name: it is one line of at most 64 characters, the name compute usage is summed under.",
            Assert.Throws<FlowValidationException>(() => loader.ParseRepository($"documentType: repository\ncode: wl\nteam: {tooLong}\n", "osdu-delivery.yaml")).Message);
        Assert.Throws<FlowValidationException>(() => loader.ParseRepository("documentType: repository\ncode: wl\nteam: \"a\\nb\"\n", "osdu-delivery.yaml"));
    }

    [Fact]
    public async Task The_metered_transport_tells_the_meter_and_changes_nothing_a_node_sends_or_receives()
    {
        var meter = Meter(cores: 2);
        var run = Guid.NewGuid();
        var task = Guid.NewGuid();
        var answer = Answer(runs: [Handout(run, 1, "f.yaml", "f", null)], tasks: [new TaskHandout(task, new TaskSpec("query", "warehouse", "{}"))]);
        var inner = new RecordingTransport(answer);
        var transport = new MeteredNodeTransport(inner, meter);

        Assert.Same(answer, await transport.PollAsync(Poll(), CancellationToken.None));
        Assert.Equal(2, meter.InFlight);

        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(RunOutcomeStatus.StaleClaim, await transport.ReportRunOutcomeAsync(run, new RunOutcomeRequest(Node, 1, RunOutcomeKind.Completed, null, "{}"), CancellationToken.None));
        Assert.True(await transport.ReportTaskOutcomeAsync(task, new TaskOutcomeRequest(Node, TaskOutcomeKind.Failed, "no", null), CancellationToken.None));

        // The control plane dropped the run's report (the lease had moved on): the node did the work all the same.
        var ended = Drain(meter);
        Assert.Equal(2, ended.Count);
        Assert.All(ended, w => Assert.Equal(4_000, w.WallClockMs));
        Assert.Equal(["poll", "run-outcome", "task-outcome"], inner.Calls);
    }

    private ComputeUsageMeter Meter(int cores) => new(_clock, cores, ComputeUsageMeter.Capacity);

    private static NodePollRequest Poll(string node = Node, IReadOnlyList<HeldRun>? holdingRuns = null, IReadOnlyList<Guid>? holdingTasks = null)
        => new(node, "1.0.0", [], RunSlots: 4, FreeRunSlots: 4, TaskSlots: 2, FreeTaskSlots: 2, holdingRuns ?? [], holdingTasks ?? [], DateTime.UnixEpoch, 30);

    private static NodePollResponse Answer(IReadOnlyList<RunHandout>? runs = null, IReadOnlyList<TaskHandout>? tasks = null)
        => new(runs ?? [], tasks ?? [], [], [], [], [], false, 90);

    private static RunHandout Handout(Guid runId, int attempt, string path, string flow, string? requestedBy, bool wholeNode = false)
        => new(runId, attempt, new RunSpec(
            Repo, Guid.NewGuid(), flow, "estate", null, null, path, null, null, RunParameters.None, null, null, requestedBy, wholeNode));

    private static List<ComputeWork> Drain(ComputeUsageMeter meter)
    {
        var read = new List<ComputeWork>();
        while (meter.Measured.TryRead(out var work))
        {
            read.Add(work);
        }

        return read;
    }

    /// <summary>A transport that answers every poll with one answer and records what it was asked.</summary>
    private sealed class RecordingTransport(NodePollResponse answer) : INodeTransport
    {
        public List<string> Calls { get; } = [];

        public Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
        {
            Calls.Add("poll");
            return Task.FromResult(answer);
        }

        public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
        {
            Calls.Add("run-outcome");
            return Task.FromResult(RunOutcomeStatus.StaleClaim);
        }

        public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
        {
            Calls.Add("task-outcome");
            return Task.FromResult(true);
        }

        public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
            => throw new NotSupportedException("No test of the meter resolves a run's context.");

        public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct) => Task.FromResult(true);

        public Task<FanOutResponse> EnqueueFanOutAsync(Guid rootRunId, FanOutRequest request, CancellationToken ct)
            => throw new NotSupportedException("No test of the meter fans a run out.");

        public Task<FanOutStateResponse> GetFanOutStateAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
            => throw new NotSupportedException("No test of the meter fans a run out.");

        public Task<FanOutCancelResponse> CancelFanOutAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
            => throw new NotSupportedException("No test of the meter fans a run out.");
    }
}
