using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension build spread over nodes on SQL Server (osdu/docs/reference/flow/dimension-build.md, Spread over nodes): the
/// run splitting it builds its own part, queues a run of the flow for each other part on a node of its own, takes over a
/// part no node started or that failed before it built anything, and writes the views once every dimension is built; a
/// part builds what it names and writes no view; a build reading too few records is not split.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionBuildSpreadTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Unit = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";

    /// <summary>The variable <c>target.connection</c> names, set to the test database's connection string for the suite's runs.</summary>
    private const string TargetVariable = "OSDU_SPREAD_TESTS_DB";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public DimensionBuildSpreadTests()
    {
        Environment.SetEnvironmentVariable(TargetVariable, _db.ConnectionString);
        _platform.Add("dev:reference-data--UnitOfMeasure:m", Unit, new JsonObject { ["Code"] = "m" });
        _platform.Add("dev:reference-data--UnitOfMeasure:gAPI", Unit, new JsonObject { ["Code"] = "gAPI" });
        _platform.Add("dev:reference-data--UnitOfMeasure:ft", Unit, new JsonObject { ["Code"] = "ft" });
        JsonObject Curve(string mnemonic, string unit) => new() { ["Mnemonic"] = mnemonic, ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{unit}:" };
        _platform.Add("dev:work-product-component--WellLog:1", WellLog, new JsonObject
        {
            ["Name"] = "LOG-1", ["Curves"] = new JsonArray(Curve("MD", "m"), Curve("GR", "gAPI")),
        });
        _platform.Add("dev:work-product-component--WellLog:2", WellLog, new JsonObject { ["Name"] = "LOG-2", ["Curves"] = new JsonArray(Curve("MD", "ft")) });
        _platform.Add("dev:work-product-component--WellLog:3", WellLog, new JsonObject { ["Name"] = "LOG-3" });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TargetVariable, null);
        _db.Dispose();
        _platform.Dispose();
    }

    /// <summary>
    /// Three dimensions reading three records each, and a view joining all three. With a part per record and at most three
    /// nodes, the build is split into three parts, one dimension each, dealt in the order the flow declares them.
    /// </summary>
    private static string Flow(int nodes = 3, long minRecordsPerNode = 1) => $$"""
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        target:
          connection: ${env:{{TargetVariable}}}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2, nodes: {{nodes}}, minRecordsPerNode: {{minRecordsPerNode}} }
        dimensions:
          - name: SpreadCurve
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                CurveUnitID: { path: CurveUnit, keep: id }
          - name: SpreadLog
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: LogName }
          - name: SpreadUnit
            kind: "osdu:wks:reference-data--UnitOfMeasure:*"
            path: id
            label: data.Code
            columns: { key: UnitID, value: UnitCode }
        views:
          - name: SpreadCurveView
            from: SpreadCurve
            join:
              - { on: WellLogID, to: SpreadLog, as: Log }
              - { on: CurveUnitID, to: SpreadUnit, as: Unit }
            columns:
              Log: Log.LogName
              Curve: Mnemonic
              Unit: Unit.UnitCode
        """;

    private static DimensionFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml").ForPartition("dev");

    /// <summary>The parameters a run of the flow is given in partition dev, as the platform hands them over.</summary>
    private static RunParameters BuildRun(string? payload = null) => new()
    {
        Operation = DeliveryOperations.Build,
        Values = new Dictionary<string, string>(StringComparer.Ordinal) { [PartitionNames.RunValue] = "dev" },
        Payload = payload,
    };

    /// <summary>A runner over the suite's ledger and platform, whose run is handed <paramref name="dispatcher"/> as its fan-out.</summary>
    private (DimensionRunner Runner, OsduLedger Ledger) Runner(DimensionFlowDefinition flow, IFanOutDispatcher? dispatcher)
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock).WithFanOut(dispatcher);
        var runner = new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true)
        {
            MemberPoll = TimeSpan.Zero,
            MemberStartGrace = TimeSpan.Zero,
        };
        return (runner, ledger);
    }

    /// <summary>
    /// A part as a node runs it: its parameters checked as the platform checks them before it queues the run, then its
    /// dimensions built by a run of the flow of its own, in the partition it names, handed no fan-out.
    /// </summary>
    private async Task RunPartAsync(DimensionFlowDefinition flow, RunParameters part, Guid runId)
    {
        new DimensionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(part);
        Assert.Equal("dev", part.Values[PartitionNames.RunValue]);
        var (runner, _) = Runner(flow, null);
        var payload = DeliveryRunPayload.Parse(part);
        await runner.BuildAsync(payload.Dimensions, DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(part), runId, "tests", CancellationToken.None);
    }

    [Fact]
    public async Task A_build_is_split_into_parts_each_on_a_node_of_its_own_and_the_views_are_written_once_every_dimension_is_built()
    {
        var flow = Parse(Flow());
        var dispatcher = new PartDispatcher((part, id) => RunPartAsync(flow, part, id));
        var (runner, ledger) = Runner(flow, dispatcher);
        var runId = Guid.NewGuid();

        var outcome = await runner.BuildAsync([], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(BuildRun()), runId, "tests", CancellationToken.None);

        // Three parts: this run builds the first dimension, and a run of the flow is queued for each of the other two, each
        // asking for a node of its own and naming the partition, its dimension, and the three runs sharing the rate.
        Assert.Equal(2, dispatcher.Queued.Count);
        Assert.All(dispatcher.Queued, q => Assert.True(q.WholeNode));
        Assert.All(dispatcher.Queued, q => Assert.Equal("dev", q.Parameters.Values[PartitionNames.RunValue]));
        var payloads = dispatcher.Queued.Select(q => DeliveryRunPayload.Parse(q.Parameters)).ToList();
        Assert.Equal([["SpreadLog"], ["SpreadUnit"]], payloads.Select(p => p.Dimensions.ToArray()).ToArray());
        Assert.All(payloads, p => Assert.Equal(3, p.RateShare));

        // Every dimension is built, each kept in the ledger under the run that built it.
        Assert.Equal((3, 0), (outcome.Built, outcome.Failed));
        Assert.Equal(["SpreadCurve", "SpreadLog", "SpreadUnit"], outcome.Dimensions.Select(d => d.Dimension).ToArray());
        Assert.All(outcome.Dimensions, d => Assert.Equal(DimensionRunStatus.Completed, d.Status));
        Assert.Equal([outcome.Dimensions[0].BuildId], (await ledger.DimensionRunsOfAsync(runId)).Select(b => (long?)b.DimensionRunId).ToArray());
        for (var i = 0; i < 2; i++)
        {
            var built = Assert.Single(await ledger.DimensionRunsOfAsync(dispatcher.Queued[i].RunId));
            Assert.Equal(outcome.Dimensions[i + 1].BuildId, built.DimensionRunId);
            Assert.Equal(outcome.Dimensions[i + 1].Keys, built.Originals);
        }

        // The view, written by this run once the parts ended, joins the tables the parts wrote.
        var view = Assert.Single(outcome.Views);
        Assert.False(view.Failed);
        Assert.Equal(
            ["LOG-1|GR|gAPI", "LOG-1|MD|m", "LOG-2|MD|ft", "LOG-3|(null)|(null)"],
            await SqlAsync("SELECT [Log], [Curve], [Unit] FROM [osdu].[v_dim_SpreadCurveView] WHERE [partition] = N'dev' ORDER BY [Log], [Curve];"));
    }

    [Fact]
    public async Task Parts_no_node_starts_are_stopped_and_their_dimensions_built_by_the_run_that_split_the_build()
    {
        var flow = Parse(Flow());
        var dispatcher = new PartDispatcher((_, _) => Task.CompletedTask) { Unstarted = true };
        var (runner, ledger) = Runner(flow, dispatcher);
        var runId = Guid.NewGuid();

        var outcome = await runner.BuildAsync([], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(BuildRun()), runId, "tests", CancellationToken.None);

        Assert.Equal(2, dispatcher.Queued.Count);
        Assert.Equal(1, dispatcher.Cancels);
        Assert.Equal((3, 0), (outcome.Built, outcome.Failed));
        Assert.Equal(3, (await ledger.DimensionRunsOfAsync(runId)).Count);
        Assert.False(Assert.Single(outcome.Views).Failed);
    }

    [Fact]
    public async Task A_part_that_failed_before_it_built_anything_has_its_dimensions_built_here_and_the_others_keep_what_they_built()
    {
        var flow = Parse(Flow());
        var dispatcher = new PartDispatcher((part, id) => RunPartAsync(flow, part, id)) { Failing = new HashSet<int> { 1 } };
        var (runner, ledger) = Runner(flow, dispatcher);
        var runId = Guid.NewGuid();

        var outcome = await runner.BuildAsync([], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(BuildRun()), runId, "tests", CancellationToken.None);

        Assert.Equal((3, 0), (outcome.Built, outcome.Failed));
        Assert.Equal(0, dispatcher.Cancels);
        var here = await ledger.DimensionRunsOfAsync(runId);
        Assert.Equal(
            new[] { outcome.Dimensions[0].BuildId, outcome.Dimensions[1].BuildId }.Order().ToArray(),
            here.Select(b => (long?)b.DimensionRunId).Order().ToArray());
        Assert.Empty(await ledger.DimensionRunsOfAsync(dispatcher.Queued[0].RunId));
        Assert.Equal(outcome.Dimensions[2].BuildId, Assert.Single(await ledger.DimensionRunsOfAsync(dispatcher.Queued[1].RunId)).DimensionRunId);
    }

    [Fact]
    public async Task A_build_reading_fewer_records_than_two_parts_take_is_built_by_one_run()
    {
        var flow = Parse(Flow(minRecordsPerNode: 5));
        var dispatcher = new PartDispatcher((_, _) => Task.CompletedTask);
        var (runner, ledger) = Runner(flow, dispatcher);
        var runId = Guid.NewGuid();

        // Nine records to read, a part per five: one part.
        var outcome = await runner.BuildAsync([], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(BuildRun()), runId, "tests", CancellationToken.None);

        Assert.Empty(dispatcher.Queued);
        Assert.Equal((3, 0), (outcome.Built, outcome.Failed));
        Assert.Equal(3, (await ledger.DimensionRunsOfAsync(runId)).Count);
    }

    [Fact]
    public async Task A_flow_keeping_its_builds_on_one_node_and_a_run_outside_the_fleet_are_never_split()
    {
        var dispatcher = new PartDispatcher((_, _) => Task.CompletedTask);
        var (oneNode, _) = Runner(Parse(Flow(nodes: 1)), dispatcher);
        Assert.Equal(3, (await oneNode.BuildAsync([], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(BuildRun()), Guid.NewGuid(), "tests", CancellationToken.None)).Built);

        var (outside, _) = Runner(Parse(Flow()), null);
        Assert.Equal(3, (await outside.BuildAsync([], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(BuildRun()), Guid.NewGuid(), "tests", CancellationToken.None)).Built);

        Assert.Empty(dispatcher.Queued);
    }

    [Fact]
    public async Task A_part_builds_the_dimensions_it_names_is_never_split_again_and_writes_no_view()
    {
        var dispatcher = new PartDispatcher((_, _) => Task.CompletedTask);
        var (runner, _) = Runner(Parse(Flow()), dispatcher);
        var part = BuildRun("""{"dimensions":["SpreadLog","SpreadUnit"],"rateShare":2}""");
        new DimensionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(part);

        var outcome = await runner.BuildAsync(["SpreadLog", "SpreadUnit"], DimensionLoadRequest.AsDeclared, DimensionBuildSpread.Of(part), Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Empty(dispatcher.Queued);
        Assert.Equal(["SpreadLog", "SpreadUnit"], outcome.Dimensions.Select(d => d.Dimension).ToArray());
        Assert.Empty(outcome.Views);
        Assert.Equal(["(null)"], await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_SpreadCurveView]');"));
    }

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    /// <summary>
    /// A fan-out whose parts a node runs as soon as they are queued (<c>runPart</c>), one by one; a slot named in
    /// <see cref="Failing"/> fails before it builds anything, and with <see cref="Unstarted"/> no node ever starts one.
    /// </summary>
    private sealed class PartDispatcher(Func<RunParameters, Guid, Task> runPart) : IFanOutDispatcher
    {
        private readonly Dictionary<Guid, FanOutMemberState> _members = [];
        private readonly Dictionary<Guid, List<Guid>> _groups = [];

        /// <summary>Every part queued, in order: its parameters, whether it asked for a node of its own, and its run.</summary>
        public List<(RunParameters Parameters, bool WholeNode, Guid RunId)> Queued { get; } = [];

        public IReadOnlySet<int> Failing { get; init; } = new HashSet<int>();

        public bool Unstarted { get; init; }

        public int Cancels { get; private set; }

        public bool Available => true;

        public async Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, bool wholeNode, CancellationToken ct = default)
        {
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                var runId = Guid.NewGuid();
                ids.Add(runId);
                var slot = ids.Count;
                Queued.Add((member, wholeNode, runId));
                if (Unstarted)
                {
                    _members[runId] = new FanOutMemberState(runId, slot, "queued", null, null);
                    continue;
                }

                if (Failing.Contains(slot))
                {
                    _members[runId] = new FanOutMemberState(runId, slot, "failed", "the node lost its connection to the ledger", null);
                    continue;
                }

                await runPart(member, runId);
                _members[runId] = new FanOutMemberState(runId, slot, "succeeded", null, null);
            }

            _groups[groupId] = ids;
            return new FanOutHandle(groupId, ids);
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
            => Task.FromResult(new FanOutState(_groups[handle.GroupId].Select(id => _members[id]).ToList()));

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            Cancels++;
            foreach (var id in _groups[handle.GroupId].Where(id => _members[id].Status == "queued"))
            {
                _members[id] = _members[id] with { Status = "cancelled", Error = "cancelled: the run that fanned it out cancelled its fan-out." };
            }

            return Task.CompletedTask;
        }
    }
}
