using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ComputeUsage;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The compute usage the nodes record (osdu/docs/reference/concepts/compute-usage.md): each piece of work written once
/// and charged to the team of its flow's folder, the sums of a period over the work and the daily totals, the fold that
/// moves old work into its day without changing any sum, the list of work, the teams a sync records from the repository
/// documents, and the recorder that writes what a node's meter measured.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ComputeUsageStoreTests : IDisposable
{
    private static readonly Guid Estate = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Other = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");
    private static readonly DateTime Noon = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly OsduTestDatabase _db = new();
    private readonly ComputeUsageStore _store;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-compute-usage-" + Guid.NewGuid().ToString("N"));

    public ComputeUsageStoreTests() => _store = new ComputeUsageStore(_db.CreateDbContext);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Work_is_written_once_however_often_its_batch_is_sent()
    {
        var first = Work(Noon, minutes: 60, flow: "wells-delivery");
        var second = Work(Noon.AddHours(1), minutes: 30, flow: "logs-delivery");

        Assert.Equal(2, await _store.RecordAsync([first, second]));
        Assert.Equal(0, await _store.RecordAsync([first, second]));
        Assert.Equal(1, await _store.RecordAsync([second, Work(Noon.AddHours(2), minutes: 1, flow: "wells-delivery")]));
        Assert.Equal(0, await _store.RecordAsync([]));

        await using var db = _db.CreateDbContext();
        var rows = await db.DeliveryComputeUsages.AsNoTracking().OrderBy(u => u.EndedUtc).ToListAsync();
        Assert.Equal(3, rows.Count);
        var row = rows[0];
        Assert.Equal(first.MeasurementId, row.MeasurementId);
        Assert.Equal("node-1", row.Node);
        Assert.Equal(ComputeWorkKinds.Run, row.Kind);
        Assert.Equal(first.WorkId, row.WorkId);
        Assert.Equal(1, row.Attempt);
        Assert.Equal(Estate, row.RepoId);
        Assert.Equal("estate", row.Repo);
        Assert.Equal("wells", row.Project);
        Assert.Equal("wells-delivery", row.Flow);
        Assert.Equal("user:ada", row.RequestedBy);
        Assert.Equal(Noon.AddMinutes(-60), row.StartedUtc);
        Assert.Equal(Noon, row.EndedUtc);
        Assert.Equal(3_600_000, row.WallClockMs);
        Assert.Equal(8, row.Cores);
        Assert.Equal(1, row.Slots);
        Assert.Equal(6, row.NodeSlots);
        Assert.Equal(8.0 / 6, row.ComputeUnits, 12);
        Assert.Equal(ComputeOutcomes.Completed, row.Outcome);
        Assert.Equal(string.Empty, row.Team);
    }

    [Fact]
    public async Task Work_is_charged_to_the_team_of_the_nearest_folder_above_its_flow_as_the_last_sync_recorded_it()
    {
        Write("osdu-delivery.yaml", "documentType: repository\ncode: est\nteam: Subsurface data\n");
        Write("wells/ingest/osdu-delivery.yaml", "documentType: repository\ncode: ing\nteam: Ingestion\n");
        Write("logs/osdu-delivery.yaml", "documentType: repository\ncode: logs\n");
        Assert.Empty(await SyncAsync(Estate));

        await _store.RecordAsync(
        [
            Work(Noon, 10, "at-root", folder: ""),
            Work(Noon, 10, "in-wells", folder: "wells"),
            Work(Noon, 10, "in-ingest", folder: "wells/ingest/deep"),
            Work(Noon, 10, "in-logs", folder: "logs"),
            Work(Noon, 10, "elsewhere", folder: "wells", repoId: Other, repo: "other"),
            Work(Noon, 10, "a-task", folder: "", kind: ComputeWorkKinds.Task),
            Work(Noon, 10, "no-repository", folder: "", noRepository: true, repo: ""),
        ]);

        await using var db = _db.CreateDbContext();
        var teams = await db.DeliveryComputeUsages.AsNoTracking().ToDictionaryAsync(u => u.Flow, u => u.Team);
        Assert.Equal("Subsurface data", teams["at-root"]);
        Assert.Equal("Subsurface data", teams["in-wells"]);
        Assert.Equal("Ingestion", teams["in-ingest"]);
        Assert.Equal("Subsurface data", teams["in-logs"]);
        Assert.Equal(string.Empty, teams["elsewhere"]);
        Assert.Equal("Subsurface data", teams["a-task"]);
        Assert.Equal(string.Empty, teams["no-repository"]);
    }

    [Fact]
    public async Task A_sync_records_the_team_of_every_repository_document_and_drops_what_is_gone()
    {
        Write("osdu-delivery.yaml", "documentType: repository\ncode: est\nteam: Subsurface data\n");
        Write("wells/osdu-delivery.yml", "documentType: repository\ncode: wells\nteam: Wells\n");
        Write("logs/osdu-delivery.yaml", "documentType: repository\ncode: logs\n");
        Write("broken/osdu-delivery.yaml", "documentType: repository\ncode: BROKEN\nteam: Nobody\n");
        var warnings = await SyncAsync(Estate);
        Assert.Contains(warnings, w => w.StartsWith("broken/osdu-delivery.yaml: code 'BROKEN' is not a repository code", StringComparison.Ordinal)
            && w.EndsWith("The compute usage of the flows under it is charged to the team of the document above it, if any.", StringComparison.Ordinal));
        Assert.Equal([("", "Subsurface data"), ("wells", "Wells")], await TeamsAsync(Estate));

        // A second sync of the same tree changes nothing; another repository's rows are its own.
        await SyncAsync(Estate);
        Assert.Equal([("", "Subsurface data"), ("wells", "Wells")], await TeamsAsync(Estate));
        Assert.Empty(await TeamsAsync(Other));

        Write("osdu-delivery.yaml", "documentType: repository\ncode: est\nteam: Data platform\n");
        File.Delete(Path.Combine(_root, "wells", "osdu-delivery.yml"));
        Write("logs/osdu-delivery.yaml", "documentType: repository\ncode: logs\nteam: Logs\n");
        await SyncAsync(Estate);
        Assert.Equal([("", "Data platform"), ("logs", "Logs")], await TeamsAsync(Estate));
    }

    [Fact]
    public async Task A_period_sums_its_days_and_each_flows_share()
    {
        await _store.RecordAsync(
        [
            Work(Noon, 60, "wells-delivery"),
            Work(Noon.AddHours(2), 30, "wells-delivery"),
            Work(Noon.AddDays(1), 120, "logs-delivery", folder: "logs"),
            Work(Noon.AddDays(1), 6, "delivery-check-values", folder: "", kind: ComputeWorkKinds.Task),
            Work(Noon.AddDays(5), 600, "outside-the-period"),
        ]);

        var summary = await _store.SummaryAsync(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12));

        Assert.Equal(new DateOnly(2026, 11, 10), summary.From);
        Assert.Equal(new DateOnly(2026, 11, 12), summary.To);
        Assert.Equal(4, summary.Totals.Work);
        Assert.Equal(216 * 60_000, summary.Totals.WallClockMs);
        Assert.Equal(216 / 60.0 * 8 / 6, summary.Totals.ComputeUnits, 9);
        Assert.Equal([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11)], summary.Days.Select(d => d.Day));
        Assert.Equal([2L, 2L], summary.Days.Select(d => d.Work));
        Assert.Equal([90L * 60_000, 126L * 60_000], summary.Days.Select(d => d.WallClockMs));

        // The largest share first.
        Assert.Equal(["logs-delivery", "wells-delivery", "delivery-check-values"], summary.Shares.Select(s => s.Flow));
        var wells = summary.Shares[1];
        Assert.Equal(("", "estate", "wells", ComputeWorkKinds.Run, 2L, 90L * 60_000), (wells.Team, wells.Repo, wells.Project, wells.Kind, wells.Work, wells.WallClockMs));
        Assert.Equal(1.5 * 8 / 6, wells.ComputeUnits, 9);
        Assert.Equal(ComputeWorkKinds.Task, summary.Shares[2].Kind);
        Assert.Equal(Noon, summary.WorkKeptFromUtc);
    }

    [Fact]
    public async Task A_period_is_refused_when_it_ends_before_it_starts_or_is_too_long()
    {
        var backwards = await Assert.ThrowsAsync<FlowValidationException>(() => _store.SummaryAsync(new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 10)));
        Assert.Equal("The period ends (2026-11-10) before it starts (2026-11-12).", backwards.Message);

        var tooLong = await Assert.ThrowsAsync<FlowValidationException>(() => _store.SummaryAsync(new DateOnly(2020, 1, 1), new DateOnly(2026, 1, 1)));
        Assert.StartsWith("A period covers at most 1100 days;", tooLong.Message, StringComparison.Ordinal);

        var empty = await _store.SummaryAsync(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10));
        Assert.Equal(new ComputeUsageTotals(0, 0, 0), empty.Totals);
        Assert.Empty(empty.Days);
        Assert.Empty(empty.Shares);
        Assert.Null(empty.WorkKeptFromUtc);
    }

    [Fact]
    public async Task A_fold_moves_old_work_into_its_day_and_changes_no_sum()
    {
        Write("osdu-delivery.yaml", "documentType: repository\ncode: est\nteam: Subsurface data\n");
        await SyncAsync(Estate);
        var work = new List<ComputeWork>();
        for (var day = 0; day < 6; day++)
        {
            for (var n = 0; n < 5; n++)
            {
                work.Add(Work(Noon.AddDays(day).AddMinutes(n), 10 + n, n % 2 == 0 ? "wells-delivery" : "logs-delivery", folder: n % 2 == 0 ? "wells" : "logs"));
            }
        }

        await _store.RecordAsync(work);
        var from = new DateOnly(2026, 11, 10);
        var to = new DateOnly(2026, 11, 15);
        var before = await _store.SummaryAsync(from, to);
        Assert.Equal(30, before.Totals.Work);

        var cutoff = Noon.Date.AddDays(3);
        Assert.Equal(15, await _store.CountFoldableAsync(cutoff));

        // Folded 4 rows a statement, so the fold takes several, and a day's rows land in more than one of them.
        Assert.Equal(15, await _store.FoldAsync(cutoff, batch: 4, CancellationToken.None));
        Assert.Equal(0, await _store.CountFoldableAsync(cutoff));
        Assert.Equal(0, await _store.FoldAsync(cutoff));

        var after = await _store.SummaryAsync(from, to);
        Assert.Equal(before.Totals.Work, after.Totals.Work);
        Assert.Equal(before.Totals.WallClockMs, after.Totals.WallClockMs);
        Assert.Equal(before.Totals.ComputeUnits, after.Totals.ComputeUnits, 9);
        Assert.Equal(before.Days.Select(d => (d.Day, d.Work, d.WallClockMs)), after.Days.Select(d => (d.Day, d.Work, d.WallClockMs)));
        Assert.Equal(
            before.Shares.Select(s => (s.Team, s.Repo, s.Project, s.Flow, s.Kind, s.Work, s.WallClockMs)).OrderBy(s => s.Flow),
            after.Shares.Select(s => (s.Team, s.Repo, s.Project, s.Flow, s.Kind, s.Work, s.WallClockMs)).OrderBy(s => s.Flow));
        Assert.Equal(Noon.AddDays(3), after.WorkKeptFromUtc);

        await using var db = _db.CreateDbContext();
        Assert.Equal(15, await db.DeliveryComputeUsages.CountAsync());
        var days = await db.DeliveryComputeUsageDays.AsNoTracking().OrderBy(d => d.Day).ThenBy(d => d.Flow).ToListAsync();

        // Three days, two flows each: one row per day, team and flow, however many statements folded into it.
        Assert.Equal(6, days.Count);
        Assert.All(days, d => Assert.Equal("Subsurface data", d.Team));
        Assert.Equal((new DateOnly(2026, 11, 10), "logs-delivery", 2L, (11L + 13) * 60_000), (days[0].Day, days[0].Flow, days[0].WorkCount, days[0].WallClockMs));
        Assert.Equal((new DateOnly(2026, 11, 10), "wells-delivery", 3L, (10L + 12 + 14) * 60_000), (days[1].Day, days[1].Flow, days[1].WorkCount, days[1].WallClockMs));

        // A later fold adds to the days it reaches, and what is listed one by one shrinks to nothing.
        Assert.Equal(15, await _store.FoldAsync(Noon.AddDays(30)));
        var all = await _store.SummaryAsync(from, to);
        Assert.Equal(before.Totals.Work, all.Totals.Work);
        Assert.Equal(before.Totals.WallClockMs, all.Totals.WallClockMs);
        Assert.Null(all.WorkKeptFromUtc);
        Assert.Equal(12, await db.DeliveryComputeUsageDays.CountAsync());
    }

    [Fact]
    public async Task Work_is_listed_newest_first_a_page_at_a_time_and_by_what_it_is_charged_to()
    {
        Write("wells/osdu-delivery.yaml", "documentType: repository\ncode: wells\nteam: Wells\n");
        await SyncAsync(Estate);
        var work = Enumerable.Range(0, 7)
            .Select(n => Work(Noon.AddMinutes(n), 5, n % 2 == 0 ? "wells-delivery" : "logs-delivery", folder: n % 2 == 0 ? "wells" : "logs"))
            .ToList();
        await _store.RecordAsync(work);
        var period = new ComputeWorkQuery(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10));

        var first = await _store.WorkAsync(period, after: null, take: 3);
        Assert.Equal([work[6].WorkId, work[5].WorkId, work[4].WorkId], first.Rows.Select(r => r.WorkId));
        Assert.NotNull(first.Next);
        var second = await _store.WorkAsync(period, first.Next, take: 3);
        Assert.Equal([work[3].WorkId, work[2].WorkId, work[1].WorkId], second.Rows.Select(r => r.WorkId));
        var last = await _store.WorkAsync(period, second.Next, take: 3);
        Assert.Equal([work[0].WorkId], last.Rows.Select(r => r.WorkId));
        Assert.Null(last.Next);

        var row = first.Rows[0];
        Assert.Equal(("node-1", ComputeWorkKinds.Run, 1, "Wells", "estate", "wells", "wells-delivery", "user:ada"),
            (row.Node, row.Kind, row.Attempt, row.Team, row.Repo, row.Project, row.Flow, row.RequestedBy));
        Assert.Equal((Noon.AddMinutes(1), Noon.AddMinutes(6), 300_000L, 8, 1, 6, ComputeOutcomes.Completed),
            (row.StartedUtc, row.EndedUtc, row.WallClockMs, row.Cores, row.Slots, row.NodeSlots, row.Outcome));
        Assert.Equal(DateTimeKind.Utc, row.EndedUtc.Kind);

        Assert.Equal(4, (await _store.WorkAsync(period with { Team = "Wells" }, null, 50)).Rows.Count);
        Assert.Equal(3, (await _store.WorkAsync(period with { Team = "" }, null, 50)).Rows.Count);
        Assert.Equal(3, (await _store.WorkAsync(period with { Repo = "estate", Project = "logs" }, null, 50)).Rows.Count);
        Assert.Equal(4, (await _store.WorkAsync(period with { Flow = "wells-delivery" }, null, 50)).Rows.Count);
        Assert.Empty((await _store.WorkAsync(period with { Repo = "other" }, null, 50)).Rows);
        Assert.Empty((await _store.WorkAsync(new ComputeWorkQuery(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12)), null, 50)).Rows);

        Assert.Equal("A page of work holds 1 to 500 rows; 0 was asked for.",
            (await Assert.ThrowsAsync<FlowValidationException>(() => _store.WorkAsync(period, null, 0))).Message);
        Assert.Equal("'next' is not where a page of work continues; pass the value the page before answered with.",
            (await Assert.ThrowsAsync<FlowValidationException>(() => _store.WorkAsync(period, "next", 10))).Message);
    }

    [Fact]
    public async Task The_recorder_writes_what_the_nodes_meter_measured_and_what_it_still_held_when_the_node_stopped()
    {
        var clock = new TestClock(new DateTimeOffset(Noon));
        var meter = new ComputeUsageMeter(clock, cores: 8, ComputeUsageMeter.Capacity);
        using var recorder = new ComputeUsageRecorder(meter, _store, clock, NullLogger<ComputeUsageRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);

        var finished = Guid.NewGuid();
        var held = Guid.NewGuid();
        meter.Polled(
            new NodePollRequest("node-1", "1.0.0", [], 4, 4, 2, 2, [], [], DateTime.UnixEpoch, 30),
            new NodePollResponse([Handout(finished, "wells/a.yaml", "finished"), Handout(held, "wells/b.yaml", "held")], [], [], [], [], [], false, 90));
        clock.Advance(TimeSpan.FromMinutes(30));
        meter.RunEnded(finished, new RunOutcomeRequest("node-1", 1, RunOutcomeKind.Completed, null, "{}"));
        clock.Advance(TimeSpan.FromMinutes(15));

        // The host stops its services while the node still drains: the recorder writes on, and finishes only at the end.
        await recorder.StoppingAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        await recorder.StoppedAsync(CancellationToken.None);

        await using var db = _db.CreateDbContext();
        var rows = await db.DeliveryComputeUsages.AsNoTracking().OrderBy(u => u.WallClockMs).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("finished", ComputeOutcomes.Completed, 30L * 60_000), (rows[0].Flow, rows[0].Outcome, rows[0].WallClockMs));
        Assert.Equal(("held", ComputeOutcomes.Interrupted, 45L * 60_000), (rows[1].Flow, rows[1].Outcome, rows[1].WallClockMs));
        Assert.All(rows, r => Assert.Equal(8, r.Cores));
    }

    [Fact]
    public async Task A_host_without_the_modules_database_records_nothing_and_says_so_when_read()
    {
        var store = new ComputeUsageStore(null);
        Assert.False(store.Available);
        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => store.SummaryAsync(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10)));
        Assert.StartsWith("Compute usage is kept in the module's database, which this host was started without.", refused.Message, StringComparison.Ordinal);

        // The recorder of such a host starts, takes what the meter measured and stops, writing nothing.
        var clock = new TestClock();
        var meter = new ComputeUsageMeter(clock, cores: 2, ComputeUsageMeter.Capacity);
        using var recorder = new ComputeUsageRecorder(meter, store, clock, NullLogger<ComputeUsageRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);
        var run = Guid.NewGuid();
        meter.Polled(
            new NodePollRequest("node-1", "1.0.0", [], 4, 4, 2, 2, [], [], DateTime.UnixEpoch, 30),
            new NodePollResponse([Handout(run, "a.yaml", "a")], [], [], [], [], [], false, 90));
        meter.RunEnded(run, new RunOutcomeRequest("node-1", 1, RunOutcomeKind.Completed, null, "{}"));
        await recorder.StoppedAsync(CancellationToken.None);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.DeliveryComputeUsages.CountAsync());
    }

    private static RunHandout Handout(Guid runId, string path, string flow)
        => new(runId, 1, new RunSpec(Estate, Guid.NewGuid(), flow, "estate", null, null, path, null, null, RunParameters.None, null, null, "user:ada"));

    /// <summary>A piece of work of <paramref name="minutes"/> minutes that ended at <paramref name="endedUtc"/>, on an 8-core node of 6 slots.</summary>
    private static ComputeWork Work(
        DateTime endedUtc, int minutes, string flow, string folder = "wells", Guid? repoId = null, string repo = "estate",
        string kind = ComputeWorkKinds.Run, bool noRepository = false)
    {
        var slash = folder.IndexOf('/', StringComparison.Ordinal);
        return new ComputeWork(
            Guid.CreateVersion7(), "node-1", kind, Guid.NewGuid(), kind == ComputeWorkKinds.Run ? 1 : 0,
            noRepository ? null : repoId ?? Estate, repo, folder, slash < 0 ? folder : folder[..slash], flow, "user:ada",
            endedUtc.AddMinutes(-minutes), endedUtc, minutes * 60_000L, 8, 1, 6, ComputeOutcomes.Completed);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<IReadOnlyList<string>> SyncAsync(Guid repoId)
    {
        Directory.CreateDirectory(_root);
        var warnings = new List<string>();
        await using var db = _db.CreateDbContext();
        await new DeliveryCatalogSync(new DeliveryDocumentLoader())
            .ReconcileAsync(db, repoId, _root, new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return warnings;
    }

    private async Task<List<(string Folder, string Team)>> TeamsAsync(Guid repoId)
    {
        await using var db = _db.CreateDbContext();
        var rows = await db.DeliveryRepositoryTeams.AsNoTracking().Where(t => t.RepoId == repoId).OrderBy(t => t.Folder).ToListAsync();
        return [.. rows.Select(t => (t.Folder, t.Team))];
    }
}
