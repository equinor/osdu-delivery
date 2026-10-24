using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledgers of the kinds that deliver nothing (osdu/docs/reference/concepts/renaming-a-pipeline.md) on SQL Server: the
/// repository sync describes every retrieval, assertion, dimension and inventory flow with its document's hash
/// (<see cref="LedgerFlowCatalog"/>), so a flow renamed and nothing else takes its history along once the sync committed;
/// one renamed and changed leaves a missing document a person has its new flow take over (<see cref="LedgerBindings"/>),
/// a dimension's views with it; and a takeover into a flow holding a dimension of the same name is refused, leaving the
/// old ledger orphaned.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class LedgerFlowTakeoverTests : IDisposable
{
    private const string Assertion = "welldb-well-04-assertion";
    private const string RenamedAssertion = "welldb-well-04-v2-assertion";
    private const string Dimensions = "welldb-welllog-05-dimensions";
    private const string RenamedDimensions = "welldb-welllog-05-v2-dimensions";

    private static readonly DateTime First = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc);

    private readonly OsduTestDatabase _module = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-ledger-flows-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _repo = Guid.NewGuid();

    public void Dispose()
    {
        _module.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Sample(string file, string name) => File.ReadAllText(Path.Combine(Samples.Source, "flows", file)).ReplaceLineEndings("\n")
        .Replace($"name: {Path.GetFileNameWithoutExtension(file)}\n", $"name: {name}\n", StringComparison.Ordinal);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Delete(string relative) => File.Delete(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private async Task<IReadOnlyList<string>> SyncAsync(DateTime now)
    {
        var warnings = new List<string>();
        await using var db = _module.CreateDbContext();
        await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(db, _repo, _root, now, warnings, CancellationToken.None);
        return warnings;
    }

    /// <summary>A ledger of <paramref name="kind"/> that <paramref name="flow"/>, which works in partitions, keeps in dev; answers its partition number.</summary>
    private static async Task<short> RegisterAsync(OsduLedger ledger, OsduDbContext db, string kind, string flow)
    {
        await ledger.RegisterLedgerAsync(new LedgerEntry { FlowId = FlowId.Of(flow, "dev"), Partition = "dev", Kind = kind, FlowName = flow, LedgerName = $"{flow}@dev" });
        return await db.DeliveryLedgers.Where(l => l.FlowId == FlowId.Of(flow, "dev")).Select(l => l.PartitionId).SingleAsync();
    }

    private static DeliveryAssertionRun Run(short partition, string flow) => new()
    {
        PartitionId = partition,
        FlowId = FlowId.Of(flow, "dev"),
        FlowName = flow,
        Actor = "schedule:nightly",
        Status = "completed",
        Tests = 2,
        Passed = 2,
        StartedUtc = First,
        CompletedUtc = First,
    };

    private static DeliveryDimension Dimension(short partition, string flow, string name) => new()
    {
        PartitionId = partition,
        FlowId = FlowId.Of(flow, "dev"),
        FlowName = flow,
        Name = name,
        Kind = "osdu:wks:work-product-component--WellLog:1.4.0",
        Path = "data.Name",
        CleanJson = "[]",
        DefinitionHash = new string('a', 16),
        CreatedUtc = First,
    };

    [Fact]
    public async Task A_flow_renamed_and_nothing_else_takes_its_history_along_once_the_sync_committed()
    {
        Write($"flows/{Assertion}.yaml", Sample($"{Assertion}.yaml", Assertion));
        Assert.Empty(await SyncAsync(First));
        var ledger = _module.Ledger(new TestClock());
        await using (var db = _module.CreateDbContext())
        {
            var partition = await RegisterAsync(ledger, db, LedgerKinds.Assertion, Assertion);
            db.DeliveryAssertionRuns.AddRange(Run(partition, Assertion), Run(partition, Assertion));
            await db.SaveChangesAsync();
        }

        // The file renamed with its flow: one document under a new name, so the sync asks the move and makes it once it committed.
        Delete($"flows/{Assertion}.yaml");
        Write($"flows/{RenamedAssertion}.yaml", Sample($"{Assertion}.yaml", RenamedAssertion));
        Assert.Empty(await SyncAsync(Later));
        var warnings = new List<string>();
        await new DeliveryCatalogSync(new DeliveryDocumentLoader(), ledger: () => ledger).CommittedAsync(null!, _repo, _root, Later, warnings, CancellationToken.None);
        Assert.Equal(
            $"Partition 'dev': moved 2 assertion run(s) of '{Assertion}' to '{RenamedAssertion}', the same document under its newest name, with their history.",
            Assert.Single(warnings));

        await using var check = _module.CreateDbContext();
        Assert.All(await check.DeliveryAssertionRuns.AsNoTracking().ToListAsync(), r => Assert.Equal(FlowId.Of(RenamedAssertion, "dev"), r.FlowId));
        var flows = await check.DeliveryLedgerFlows.AsNoTracking().OrderBy(f => f.FlowName).ToListAsync();
        Assert.Equal([(Assertion, false), (RenamedAssertion, true)], flows.Select(f => (f.FlowName, f.Active)));
        Assert.All(flows, f => Assert.True(f.Partitioned));
        Assert.Single(flows.Select(f => f.DocumentHash).Distinct());
        Assert.Empty(await ledger.ListOrphanedDocumentsAsync(null));
    }

    [Fact]
    public async Task A_dimension_flow_renamed_and_changed_is_taken_over_with_its_dimensions_and_views()
    {
        var ledger = await OrphanedDimensionsAsync();

        // Offered first: the declared dimension flow in the folder the missing one was declared in.
        var missing = Assert.Single(await ledger.ListOrphanedDocumentsAsync("dev"));
        Assert.Equal((LedgerKinds.Dimension, Dimensions, $"flows/{Dimensions}.yaml", 2L, 0L), (missing.Kind, missing.FlowName, missing.RelativePath, missing.Rows, missing.Claimed));
        var candidate = Assert.Single(missing.Candidates);
        Assert.Equal((RenamedDimensions, true, 1), (candidate.FlowName, candidate.Suggested, candidate.Takes));
        Assert.Equal(["folder"], candidate.Shares);

        var taken = Assert.Single(await ledger.TakeOverLedgersAsync(new LedgerTakeoverRequest(LedgerKinds.Dimension, Dimensions, RenamedDimensions), "user:alice"));
        Assert.Equal((LedgerMoves.Completed, 2L, FlowId.Of(RenamedDimensions, "dev")), (taken.Outcome, taken.Records, taken.To));
        Assert.Equal($"moved 2 dimension(s) of '{Dimensions}' to '{RenamedDimensions}', to which it was bound by hand, with their history", taken.Summary);

        // The dimensions are the new flow's under its name, and so is the view its builds write.
        await using var check = _module.CreateDbContext();
        Assert.All(await check.DeliveryDimensions.AsNoTracking().ToListAsync(), d => Assert.Equal((FlowId.Of(RenamedDimensions, "dev"), RenamedDimensions), (d.FlowId, d.FlowName)));
        var view = Assert.Single(await check.DeliveryDimensionViews.AsNoTracking().ToListAsync());
        Assert.Equal((FlowId.Of(RenamedDimensions, "dev"), RenamedDimensions), (view.LedgerId, view.FlowName));
        Assert.Empty(await ledger.ListOrphanedDocumentsAsync("dev"));
    }

    [Fact]
    public async Task A_takeover_into_a_flow_holding_a_dimension_of_the_same_name_is_refused_and_the_ledger_stays_orphaned()
    {
        var ledger = await OrphanedDimensionsAsync();
        await using (var db = _module.CreateDbContext())
        {
            var partition = await RegisterAsync(ledger, db, LedgerKinds.Dimension, RenamedDimensions);
            db.DeliveryDimensions.Add(Dimension(partition, RenamedDimensions, "welldb_logs"));
            await db.SaveChangesAsync();
        }

        var refused = Assert.Single(await ledger.TakeOverLedgersAsync(new LedgerTakeoverRequest(LedgerKinds.Dimension, Dimensions, RenamedDimensions, "dev"), "user:alice"));
        Assert.Equal(LedgerMoves.Failed, refused.Outcome);
        Assert.Equal(
            $"not moved: '{RenamedDimensions}' holds dimension(s) 'welldb_logs' of its own that '{Dimensions}' holds too, and a flow holds one of each name, so the two ledgers stay apart and the old one stays orphaned.",
            refused.Summary);

        await using var check = _module.CreateDbContext();
        Assert.Equal(2, await check.DeliveryDimensions.CountAsync(d => d.FlowId == FlowId.Of(Dimensions, "dev")));
        Assert.Equal(FlowId.Of(Dimensions, "dev"), (await check.DeliveryDimensionViews.AsNoTracking().SingleAsync()).LedgerId);
        Assert.Equal(Dimensions, Assert.Single(await ledger.ListOrphanedDocumentsAsync("dev")).FlowName);
    }

    /// <summary>
    /// The dimension flow synced and built (two dimensions and a view), then replaced by one renamed and changed at once:
    /// another document, so the sync moves nothing and its ledger is orphaned.
    /// </summary>
    private async Task<OsduLedger> OrphanedDimensionsAsync()
    {
        Write($"flows/{Dimensions}.yaml", Sample($"{Dimensions}.yaml", Dimensions));
        Assert.Empty(await SyncAsync(First));
        var ledger = _module.Ledger(new TestClock());
        await using (var db = _module.CreateDbContext())
        {
            var partition = await RegisterAsync(ledger, db, LedgerKinds.Dimension, Dimensions);
            db.DeliveryDimensions.AddRange(Dimension(partition, Dimensions, "welldb_logs"), Dimension(partition, Dimensions, "welldb_wellbores"));
            db.DeliveryDimensionViews.Add(new DeliveryDimensionView
            {
                Name = "welldb_logs_by_wellbore",
                ViewName = "v_dim_welldb_logs_by_wellbore",
                FlowName = Dimensions,
                LedgerId = FlowId.Of(Dimensions, "dev"),
                DeclarationJson = "{}",
                Sql = "SELECT 1 AS one",
                TablesJson = "[]",
                ColumnsJson = "[]",
                WrittenBy = "schedule:nightly",
                WrittenUtc = First,
                CreatedUtc = First,
            });
            await db.SaveChangesAsync();
        }

        Delete($"flows/{Dimensions}.yaml");
        Write($"flows/{RenamedDimensions}.yaml", Sample($"{Dimensions}.yaml", RenamedDimensions)
            .Replace("ready for a search panel.\n", "ready for the search panel of the renamed flow.\n", StringComparison.Ordinal));
        Assert.Empty(await SyncAsync(Later));
        await using var moves = _module.CreateDbContext();
        Assert.False(await moves.DeliveryActivities.AnyAsync(a => a.Kind == LedgerMoves.Kind));
        return ledger;
    }
}
