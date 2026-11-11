using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What an inventory's report reads back of what its runs kept (osdu/docs/reference/cli/inventory.md): the owners and the
/// counts by finding as the runner writes them, read leniently and never thrown on; every finding counted in report order; the
/// newest run, last build and last reconcile read once each; and the CSV export, paged through the ledger past one page, a
/// cell a spreadsheet would run made text.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryReportTests : IDisposable
{
    private const string Partition = TestLedgers.Partition;
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.4.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _inventoryFlow = FlowId.Of("report-inventory", Partition);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Owners_are_read_as_the_runner_writes_them_and_anything_else_reads_as_nothing()
    {
        var owners = InventoryReport.Owners("""{"source":"inferred","owners":[{"identity":"sp@contoso.com","records":12},{"identity":"","records":3},{"records":1}]}""");
        Assert.NotNull(owners);
        Assert.Equal("inferred", owners.Source);
        Assert.Equal([new InventoryOwner("sp@contoso.com", 12)], owners.Identities);

        // How they were known stands in when the JSON does not say it; nothing kept, and nothing known, is nothing.
        Assert.Equal("declared", InventoryReport.Owners("""{"owners":[]}""", "declared")!.Source);
        Assert.Equal(new InventoryOwners("none", []), InventoryReport.Owners(null, "none"));
        Assert.Null(InventoryReport.Owners(null));
        Assert.Null(InventoryReport.Owners("[1,2]"));
        Assert.Null(InventoryReport.Owners("{not json"));
    }

    [Fact]
    public void Counts_by_finding_list_every_finding_in_report_order_zeros_and_unknown_findings_included()
    {
        var findings = InventoryReport.Findings("""{"tracked":120,"orphan":3,"lost":2,"drifted":-1,"gone":"many"}""");
        Assert.NotNull(findings);
        Assert.Equal(3L, findings["orphan"]);
        Assert.False(findings.ContainsKey("drifted"));
        Assert.False(findings.ContainsKey("gone"));
        Assert.Null(InventoryReport.Findings(" "));
        Assert.Null(InventoryReport.Findings("{broken"));

        var counted = InventoryReport.Counted(findings);
        Assert.Equal([.. InventoryFindings.All, "lost"], counted.Select(c => c.Finding));
        Assert.Equal(new InventoryFindingCount(InventoryFindings.Orphan, 3, true), counted[0]);
        Assert.Equal(new InventoryFindingCount(InventoryFindings.Missing, 0, true), counted[1]);
        Assert.Equal(new InventoryFindingCount(InventoryFindings.Tracked, 120, false), counted.Single(c => c.Finding == InventoryFindings.Tracked));
        Assert.Equal(new InventoryFindingCount("lost", 2, false), counted[^1]);
    }

    [Fact]
    public async Task The_export_pages_through_every_id_of_a_finding_and_defuses_what_a_spreadsheet_would_run()
    {
        var (ledger, inventory) = await InventoryAsync();
        const int ids = InventoryExport.Page + 234;
        var rows = Enumerable.Range(0, ids)
            .Select(i => new InventoryScanRow(
                $"{Partition}:work-product-component--WellLog:{i:D5}", Kind, 1, i == 7 ? "=HYPERLINK(\"x\")" : "someone@elsewhere.com",
                new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), null, null, $"WellLog {i:D5}"))
            .ToList();
        var run = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "tests", "search", Now);
        await ledger.AppendInventoryScanAsync(_inventoryFlow, run, rows);
        await ledger.MergeInventoryAsync(_inventoryFlow, inventory.InventoryId, run, Now);
        await ledger.ReconcileInventoryAsync(_inventoryFlow, inventory.InventoryId, [], Now);

        using var all = new StringWriter();
        Assert.Equal(ids, await InventoryExport.WriteAsync(ledger, inventory, null, all, CancellationToken.None));
        var lines = all.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ids + 1, lines.Length);
        Assert.StartsWith("inventory_record_id,id,name,kind,version,finding,", lines[0], StringComparison.Ordinal);
        Assert.Equal(ids, lines.Skip(1).Select(l => l.Split(',')[1]).Distinct(StringComparer.Ordinal).Count());
        // Each id is named beside it, by the name the build read of its record.
        Assert.All(lines.Skip(1).Select(l => l.Split(',')), cells => Assert.Equal("WellLog " + cells[1][^5..], cells[2]));
        Assert.Contains(lines, l => l.Contains("\"'=HYPERLINK(\"\"x\"\")\"", StringComparison.Ordinal));

        using var foreign = new StringWriter();
        Assert.Equal(ids, await InventoryExport.WriteAsync(ledger, inventory, InventoryFindings.Foreign, foreign, CancellationToken.None));
        using var orphans = new StringWriter();
        Assert.Equal(0, await InventoryExport.WriteAsync(ledger, inventory, InventoryFindings.Orphan, orphans, CancellationToken.None));
        Assert.Single(orphans.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        await Assert.ThrowsAsync<DeliveryException>(() => InventoryExport.WriteAsync(ledger, inventory, "lost", new StringWriter(), CancellationToken.None));
        Assert.Equal("report-inventory-dev-WellLogs-foreign.csv", InventoryExport.FileName(inventory, InventoryFindings.Foreign));
        Assert.Equal("report-inventory-dev-WellLogs-all.csv", InventoryExport.FileName(inventory, null));
    }

    [Fact]
    public async Task The_recent_runs_are_the_newest_the_last_build_and_the_last_reconcile_each_read_once()
    {
        var (ledger, inventory) = await InventoryAsync();
        Assert.Equal(new InventoryRecentRuns(null, null, null), await InventoryReport.RecentRunsAsync(ledger, inventory, CancellationToken.None));

        var build = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "tests", "search", Now);
        await ledger.CompleteInventoryRunAsync(
            _inventoryFlow, build,
            new InventoryRunState
            {
                InventoryRunId = build, InventoryId = inventory.InventoryId, Operation = InventoryRunStatus.Build, Actor = "tests", Status = InventoryRunStatus.Completed,
                StartedUtc = Now, ReadMode = "search", FindingsJson = """{"tracked":1}""", OwnersJson = """{"source":"none","owners":[]}""",
            },
            "none", Now);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var running = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Reconcile, Guid.NewGuid(), "tests", "search", Now);

        var state = (await ledger.GetInventoryAsync(Partition, inventory.InventoryId))!;
        var runs = await InventoryReport.RecentRunsAsync(ledger, state, CancellationToken.None);

        Assert.Equal((running, InventoryRunStatus.Running), (runs.Latest!.InventoryRunId, runs.Latest.Status));
        Assert.Equal(build, runs.LastBuild!.InventoryRunId);
        Assert.Same(runs.LastBuild, runs.LastReconcile);
        Assert.Null(await ledger.GetInventoryRunAsync(Partition, running + 1000));
        Assert.Null(await ledger.GetInventoryRunAsync("prod", build));
    }

    private async Task<(OsduLedger Ledger, InventoryState Inventory)> InventoryAsync()
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = _inventoryFlow, Partition = Partition, Kind = LedgerKinds.Inventory, FlowName = "report-inventory", LedgerName = "report-inventory@" + Partition,
        });
        var inventory = await ledger.RegisterInventoryAsync(_inventoryFlow, "report-inventory", "WellLogs", Kind, null, "search", "latest");
        return (ledger, inventory);
    }
}
