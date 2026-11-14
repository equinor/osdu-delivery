using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Deleting what a missing document left behind (osdu/docs/reference/concepts/renaming-a-pipeline.md, Deleting what a
/// missing document left behind; <see cref="LedgerDeletes"/>) on SQL Server: an orphaned ledger of every kind is emptied
/// table by table, another ledger's rows untouched, and its audit trail and directory entry stay with the deletion in
/// them, as the person who asked; a ledger that knows something of OSDU nothing else does is refused whole (a delivery
/// record OSDU may still hold, an inventory's removal); a ledger being deleted is neither listed nor taken over nor
/// moved; the pass finishes a deletion that stopped; and a flow declared again keeps what a stopped deletion left.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class LedgerDeleteTests : IDisposable
{
    private static readonly DateTime At = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly OsduTestDatabase _module = new();
    private int _next;

    public void Dispose() => _module.Dispose();

    /// <summary>A ledger of every slice size of one row, so each loop of a deletion goes round more than once.</summary>
    private OsduLedger Ledger() => new(_module.CreateDbContext, new TestClock()) { WriteSlice = 1 };

    /// <summary>A ledger of <paramref name="kind"/> that <paramref name="flow"/>, which no repository declares, kept in dev; answers its identity and partition number.</summary>
    private async Task<(Guid Id, short Partition)> RegisterAsync(OsduLedger ledger, string kind, string flow)
    {
        var id = FlowId.Of(flow, "dev");
        await ledger.RegisterLedgerAsync(new LedgerEntry { FlowId = id, Partition = "dev", Kind = kind, FlowName = flow, LedgerName = $"{flow}@dev" });
        await using var db = _module.CreateDbContext();
        return (id, await db.DeliveryLedgers.Where(l => l.FlowId == id).Select(l => l.PartitionId).SingleAsync());
    }

    /// <summary>
    /// Two rows of each of <paramref name="tables"/> for the ledger, and under each row two of every table that names it,
    /// every column the model requires filled; <paramref name="skip"/> names tables left empty, with all that names them.
    /// </summary>
    private async Task SeedAsync(
        OsduDbContext db, IReadOnlyList<LedgerDeleteTable> tables, short partition, Guid ledger, string flow, string column, object parent,
        IReadOnlySet<string> skip, Dictionary<string, int> seeded)
    {
        foreach (var table in tables.Where(t => !skip.Contains(t.Table)))
        {
            var entity = db.Model.GetEntityTypes().Single(e => e.GetTableName() == table.Table);
            var rows = new List<object>();
            for (var n = 0; n < 2; n++)
            {
                var row = Activator.CreateInstance(entity.ClrType)!;
                foreach (var property in entity.GetProperties())
                {
                    Fill(row, property, partition, ledger, flow, column, parent);
                }

                db.Add(row);
                rows.Add(row);
            }

            await db.SaveChangesAsync();
            seeded[table.Table] = seeded.GetValueOrDefault(table.Table) + rows.Count;
            if (table.Key is { } key)
            {
                foreach (var row in rows)
                {
                    var id = Convert.ToInt64(entity.FindProperty(key)!.PropertyInfo!.GetValue(row), CultureInfo.InvariantCulture);
                    await SeedAsync(db, table.Children, partition, ledger, flow, key, id, skip, seeded);
                }
            }
        }
    }

    /// <summary>Gives a row's property what places it under its ledger and its parent, and any other required property a value of its own.</summary>
    private void Fill(object row, IProperty property, short partition, Guid ledger, string flow, string column, object parent)
    {
        var info = property.PropertyInfo ?? throw new InvalidOperationException($"{property.DeclaringType.DisplayName()}.{property.Name} is no CLR property.");
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (property.Name == "PartitionId")
        {
            info.SetValue(row, partition);
        }
        else if (property.Name == column)
        {
            info.SetValue(row, Convert.ChangeType(parent, type, CultureInfo.InvariantCulture));
        }
        else if (property.Name == "FlowId")
        {
            info.SetValue(row, ledger);
        }
        else if (property.Name == "FlowName")
        {
            info.SetValue(row, flow);
        }
        else if (property.ValueGenerated == ValueGenerated.Never && !property.IsNullable)
        {
            var n = Interlocked.Increment(ref _next);
            object value = type == typeof(string) ? "v" + n.ToString(CultureInfo.InvariantCulture)
                : type == typeof(Guid) ? Guid.NewGuid()
                : type == typeof(DateTime) ? At
                : type == typeof(bool) ? false
                : type == typeof(byte[]) ? Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()
                : type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(byte) || type == typeof(decimal) || type == typeof(double)
                    ? Convert.ChangeType(n, type, CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"{property.DeclaringType.DisplayName()}.{property.Name} is a {type.Name}, which this seed gives no value.");
            info.SetValue(row, value);
        }
    }

    private async Task<int> CountAsync(string table)
    {
        await using var db = _module.CreateDbContext();
        // The name is one the model holds, never text a caller wrote.
        var known = db.Model.GetEntityTypes().Single(e => e.GetTableName() == table).GetTableName()!;
        var count = string.Concat("SELECT COUNT(*) AS [Value] FROM [osdu].[", known, "]");
        return await db.Database.SqlQueryRaw<int>(count).SingleAsync();
    }

    private async Task<List<DeliveryActivity>> DeletionsAsync(Guid ledger)
    {
        await using var db = _module.CreateDbContext();
        return await db.DeliveryActivities.AsNoTracking().Where(a => a.FlowId == ledger && a.Kind == LedgerDeletes.Kind).OrderBy(a => a.ActivityId).ToListAsync();
    }

    [Theory]
    [InlineData(LedgerKinds.Retrieval)]
    [InlineData(LedgerKinds.Assertion)]
    [InlineData(LedgerKinds.Download)]
    [InlineData(LedgerKinds.Inventory)]
    public async Task Every_table_of_an_orphaned_ledger_is_emptied_and_another_ledger_s_rows_and_its_own_trail_stay(string kind)
    {
        var ledger = Ledger();
        var (gone, kept) = ($"gone-{kind}", $"kept-{kind}");
        var (goneId, partition) = await RegisterAsync(ledger, kind, gone);
        var (keptId, _) = await RegisterAsync(ledger, kind, kept);
        var (seeded, others) = (new Dictionary<string, int>(), new Dictionary<string, int>());
        await using (var db = _module.CreateDbContext())
        {
            // The ledger that goes holds no removal, which would refuse it; the one that stays holds every table's rows.
            await SeedAsync(db, LedgerDeletes.Trees[kind], partition, goneId, gone, "FlowId", goneId, new HashSet<string> { "InventoryRemoval" }, seeded);
            await SeedAsync(db, LedgerDeletes.Trees[kind], partition, keptId, kept, "FlowId", keptId, new HashSet<string>(), others);
        }

        var first = LedgerMoves.OtherKinds[kind].Tables[0];
        var request = new LedgerDeleteRequest(kind, gone, "dev");
        var previewed = Assert.Single(await ledger.PreviewOrphanedLedgerDeleteAsync(request));
        Assert.Equal((goneId, "dev", (long)seeded[first]), (previewed.Ledger, previewed.Partition, previewed.Rows));
        Assert.Null(previewed.Refusal);

        var deleted = Assert.Single(await ledger.DeleteOrphanedLedgersAsync(request, "user:alice"));
        Assert.Equal((LedgerMoves.Completed, goneId, (long)seeded[first]), (deleted.Outcome, deleted.Ledger, deleted.Rows));
        Assert.Equal(
            $"deleted the orphaned ledger of '{gone}', which no repository declares: {seeded[first]} {LedgerMoves.OtherKinds[kind].Noun} with everything kept under them",
            deleted.Summary);

        // Every table holds what the other ledger holds, and nothing of the one deleted.
        foreach (var table in LedgerDeletes.Flatten(LedgerDeletes.Trees[kind]))
        {
            Assert.True(seeded.ContainsKey(table.Table) || table.Table.StartsWith("InventoryRemoval", StringComparison.Ordinal), $"osdu.{table.Table} was not seeded.");
            Assert.Equal(others[table.Table], await CountAsync(table.Table));
        }

        // The deletion is in the ledger's own trail, as the person who asked, and the ledger keeps its place in the directory.
        var activity = Assert.Single(await DeletionsAsync(goneId));
        Assert.Equal(("user:alice", LedgerMoves.Completed, deleted.Summary, deleted.ActivityId), (activity.Actor, activity.Outcome, activity.Summary, activity.ActivityId));
        Assert.NotNull(activity.CompletedUtc);
        Assert.NotNull(await ledger.GetLedgerAsync(goneId));

        // It is listed no more, the other one is, and asking again deletes nothing.
        Assert.Equal(kept, Assert.Single(await ledger.ListOrphanedDocumentsAsync("dev")).FlowName);
        var again = await Assert.ThrowsAsync<DeliveryException>(() => ledger.DeleteOrphanedLedgersAsync(request, "user:alice"));
        Assert.Equal(
            $"No {kind} flow '{gone}' left an orphaned ledger in partition 'dev', so nothing was deleted: the Orphaned ledgers page lists the documents that did.",
            again.Message);
    }

    [Fact]
    public async Task An_inventory_ledger_holding_a_removal_is_refused_and_keeps_everything()
    {
        var ledger = Ledger();
        const string Flow = "welldb-osdu-06-inventory";
        var (id, partition) = await RegisterAsync(ledger, LedgerKinds.Inventory, Flow);
        var seeded = new Dictionary<string, int>();
        await using (var db = _module.CreateDbContext())
        {
            await SeedAsync(db, LedgerDeletes.Trees[LedgerKinds.Inventory], partition, id, Flow, "FlowId", id, new HashSet<string>(), seeded);
        }

        var request = new LedgerDeleteRequest(LedgerKinds.Inventory, Flow, "dev");
        var refusal = $"The inventories of '{Flow}' in partition 'dev' hold {seeded["InventoryRemoval"]} removal(s), the record of what each took out of OSDU and what a restore brings back, so the ledger is not deleted. Have a declared inventory flow take it over.";
        var stays = Assert.Single(await ledger.PreviewOrphanedLedgerDeleteAsync(request));
        Assert.Equal((refusal, $"it holds {seeded["InventoryRemoval"]} removal(s) from OSDU"), (stays.Refusal, stays.RefusedFor));

        var refused = Assert.Single(await ledger.DeleteOrphanedLedgersAsync(request, "user:alice"));
        Assert.Equal((LedgerMoves.Failed, 0L, refusal), (refused.Outcome, refused.ActivityId, refused.Summary));
        foreach (var (table, rows) in seeded)
        {
            Assert.Equal(rows, await CountAsync(table));
        }

        // Nothing was asked, so nothing is in the trail, and the document is still listed.
        Assert.Empty(await DeletionsAsync(id));
        Assert.Equal(Flow, Assert.Single(await ledger.ListOrphanedDocumentsAsync("dev")).FlowName);
    }

    [Fact]
    public async Task A_delivery_ledger_is_refused_while_OSDU_may_hold_a_record_of_it_and_deleted_once_none_can()
    {
        var ledger = Ledger();
        const string Flow = "welldb-welllog-03-delivery";
        var id = FlowId.Of(Flow);
        await ledger.RegisterLedgerAsync(new LedgerEntry { FlowId = id, Partition = "dev", Kind = LedgerKinds.Delivery, FlowName = Flow, LedgerName = Flow });
        await ledger.UpsertPendingAsync(id, [Claiming(id, "a"), Claiming(id, "b")]);
        var request = new LedgerDeleteRequest(LedgerKinds.Delivery, Flow, "dev");

        // Both records queued a document under an OSDU id and neither is marked removed: OSDU may hold them.
        var refusal = $"2 record(s) of '{Flow}' in partition 'dev' claimed an OSDU id and are not marked removed, so OSDU may still hold them and the ledger is not deleted: it is the one place that accounts for them. Have a declared flow take the ledger over; or declare the flow again, sync its repository and delete its ledger from its Delivery tab, which removes its records from OSDU first.";
        var previewed = Assert.Single(await ledger.PreviewOrphanedLedgerDeleteAsync(request));
        Assert.Equal((2L, 2L, refusal, "OSDU may still hold 2 of its records"), (previewed.Rows, previewed.Claimed, previewed.Refusal, previewed.RefusedFor));
        var refused = Assert.Single(await ledger.DeleteOrphanedLedgersAsync(request, "user:alice"));
        Assert.Equal((LedgerMoves.Failed, refusal), (refused.Outcome, refused.Summary));
        Assert.Equal(2, (await ledger.ListAsync(id, new RecordQuery())).Count);
        Assert.Empty(await DeletionsAsync(id));

        // One removed from OSDU still leaves the other.
        await using (var db = _module.CreateDbContext())
        {
            var first = await db.DeliveryRecords.Where(r => r.FlowId == id).OrderBy(r => r.SourceKey).Select(r => r.DeliveryKey).FirstAsync();
            await db.DeliveryRecords.Where(r => r.FlowId == id && r.DeliveryKey == first).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "deleted"));
        }

        Assert.StartsWith("1 record(s) of ", Assert.Single(await ledger.PreviewOrphanedLedgerDeleteAsync(request)).Refusal, StringComparison.Ordinal);

        // Both marked removed: OSDU holds none of them, so the ledger goes, each record kept as one line under the deletion.
        await using (var db = _module.CreateDbContext())
        {
            await db.DeliveryRecords.Where(r => r.FlowId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "deleted"));
        }

        var clear = Assert.Single(await ledger.PreviewOrphanedLedgerDeleteAsync(request));
        Assert.Null(clear.Refusal);
        Assert.Null(clear.RefusedFor);
        var deleted = Assert.Single(await ledger.DeleteOrphanedLedgersAsync(request, "user:alice"));
        Assert.Equal((LedgerMoves.Completed, 2L), (deleted.Outcome, deleted.Rows));
        Assert.Equal(
            $"deleted the orphaned ledger of '{Flow}', which no repository declares: 2 record(s), none of which OSDU holds, each kept as one line, with their attempts, and its submissions, watermarks and the rest of its runs",
            deleted.Summary);
        Assert.Empty(await ledger.ListAsync(id, new RecordQuery()));
        await using var check = _module.CreateDbContext();
        var lines = await check.DeliveryPurgedRecords.AsNoTracking().Where(p => p.FlowId == id).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal((deleted.ActivityId, "user:alice"), (line.ActivityId, line.PurgedBy)));
        var activity = Assert.Single(await DeletionsAsync(id));
        Assert.Equal(("user:alice", LedgerMoves.Completed), (activity.Actor, activity.Outcome));
        Assert.Empty(await ledger.ListOrphanedDocumentsAsync(null));
    }

    [Fact]
    public async Task A_dimension_ledger_goes_with_its_dimensions_and_the_views_its_flow_wrote()
    {
        var ledger = Ledger();
        const string Flow = "welldb-welllog-05-dimensions";
        var (id, partition) = await RegisterAsync(ledger, LedgerKinds.Dimension, Flow);
        await using (var db = _module.CreateDbContext())
        {
            db.DeliveryDimensions.AddRange(Dimension(partition, id, Flow, "welldb_logs"), Dimension(partition, id, Flow, "welldb_wellbores"));
            db.DeliveryDimensionViews.Add(new DeliveryDimensionView
            {
                Name = "welldb_logs_by_wellbore",
                ViewName = "v_dim_welldb_logs_by_wellbore",
                FlowName = Flow,
                LedgerId = id,
                DeclarationJson = "{}",
                Sql = "SELECT 1 AS one",
                TablesJson = "[]",
                ColumnsJson = "[]",
                WrittenBy = "schedule:nightly",
                WrittenUtc = At,
                CreatedUtc = At,
            });
            await db.SaveChangesAsync();
        }

        var request = new LedgerDeleteRequest(LedgerKinds.Dimension, Flow, "dev");
        var step = Assert.Single(await ledger.PreviewOrphanedLedgerDeleteAsync(request));
        Assert.Equal(2L, step.Rows);
        Assert.Null(step.Refusal);
        var deleted = Assert.Single(await ledger.DeleteOrphanedLedgersAsync(request, "user:alice"));
        Assert.Equal(
            (LedgerMoves.Completed, $"deleted the orphaned ledger of '{Flow}', which no repository declares: 2 dimension(s) with everything kept under them"),
            (deleted.Outcome, deleted.Summary));

        await using var check = _module.CreateDbContext();
        Assert.Empty(await check.DeliveryDimensions.AsNoTracking().ToListAsync());
        Assert.Empty(await check.DeliveryDimensionViews.AsNoTracking().ToListAsync());
        Assert.Empty(await ledger.ListOrphanedDocumentsAsync("dev"));
    }

    [Fact]
    public async Task A_ledger_being_deleted_is_not_listed_taken_over_or_moved_and_the_pass_finishes_a_deletion_that_stopped()
    {
        var ledger = Ledger();
        const string Flow = "welldb-well-04-assertion";
        const string Declared = "welldb-well-04-v2-assertion";
        var (id, partition) = await RegisterAsync(ledger, LedgerKinds.Assertion, Flow);
        var repo = Guid.NewGuid();
        var seeded = new Dictionary<string, int>();
        await using (var db = _module.CreateDbContext())
        {
            await SeedAsync(db, LedgerDeletes.Trees[LedgerKinds.Assertion], partition, id, Flow, "FlowId", id, new HashSet<string>(), seeded);
            db.DeliveryLedgerFlows.Add(Declaration(repo, Declared));
            await db.SaveChangesAsync();
        }

        Assert.Equal(Declared, Assert.Single(Assert.Single(await ledger.ListOrphanedDocumentsAsync("dev")).Candidates).FlowName);

        // The deletion asked, and stopped before it deleted anything: a control plane that went down, a request cut off.
        var request = new LedgerDeleteRequest(LedgerKinds.Assertion, Flow, "dev");
        long asked;
        await using (var db = _module.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var step = Assert.Single(await LedgerBindings.PlanDeleteAsync(db, request, CancellationToken.None));
            asked = await LedgerBindings.RequestDeleteAsync(db, step, "user:alice", At, CancellationToken.None);

            // Asked again, it is the same deletion.
            Assert.Equal(asked, await LedgerBindings.RequestDeleteAsync(db, step, "user:bob", At, CancellationToken.None));
            await transaction.CommitAsync();
        }

        // While it runs the ledger is no missing document, no flow takes it over, and no sync moves it.
        Assert.Empty(await ledger.ListOrphanedDocumentsAsync("dev"));
        var taken = await Assert.ThrowsAsync<DeliveryException>(
            () => ledger.TakeOverLedgersAsync(new LedgerTakeoverRequest(LedgerKinds.Assertion, Flow, Declared, "dev"), "user:bob"));
        Assert.Contains($"No assertion flow '{Flow}' left orphaned ledgers in partition 'dev'", taken.Message, StringComparison.Ordinal);
        await using (var db = _module.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var move = new LedgerMoveRequest(partition, id, Flow, $"{Flow}@dev", FlowId.Of(Declared, "dev"), Declared, string.Empty, $"{Declared}@dev", repo, LedgerKinds.Assertion);
            Assert.Equal(LedgerMoveAsk.Waits, (await LedgerMoves.RequestAsync(db, move, At, CancellationToken.None)).Ask);
        }

        Assert.Equal(seeded["AssertionRun"], await CountAsync("AssertionRun"));

        // The control plane's pass takes it up and finishes it, as the person who asked.
        var finished = Assert.Single(await ledger.CompleteLedgerDeletesAsync());
        Assert.Equal((asked, LedgerMoves.Completed, id), (finished.ActivityId, finished.Outcome, finished.Ledger));
        Assert.Equal(0, await CountAsync("AssertionRun"));
        Assert.Equal(0, await CountAsync("AssertionResult"));
        var activity = Assert.Single(await DeletionsAsync(id));
        Assert.Equal(("user:alice", LedgerMoves.Completed), (activity.Actor, activity.Outcome));
        Assert.Empty(await ledger.CompleteLedgerDeletesAsync());
    }

    [Fact]
    public async Task A_flow_declared_again_keeps_what_a_stopped_deletion_left_of_its_ledger()
    {
        var ledger = Ledger();
        const string Flow = "welldb-well-04-assertion";
        var (id, partition) = await RegisterAsync(ledger, LedgerKinds.Assertion, Flow);
        var seeded = new Dictionary<string, int>();
        await using (var db = _module.CreateDbContext())
        {
            await SeedAsync(db, LedgerDeletes.Trees[LedgerKinds.Assertion], partition, id, Flow, "FlowId", id, new HashSet<string>(), seeded);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var step = Assert.Single(await LedgerBindings.PlanDeleteAsync(db, new LedgerDeleteRequest(LedgerKinds.Assertion, Flow, "dev"), CancellationToken.None));
            await LedgerBindings.RequestDeleteAsync(db, step, "user:alice", At, CancellationToken.None);
            await transaction.CommitAsync();

            // A sync finds the flow declared again before the deletion is taken up.
            db.DeliveryLedgerFlows.Add(Declaration(Guid.NewGuid(), Flow));
            await db.SaveChangesAsync();
        }

        var stopped = Assert.Single(await ledger.CompleteLedgerDeletesAsync());
        Assert.Equal(
            (LedgerMoves.Failed, $"not deleted whole: a repository declares '{Flow}' again, or a move takes its ledger, so what the ledger still holds stays with it."),
            (stopped.Outcome, stopped.Summary));
        Assert.Equal(seeded["AssertionRun"], await CountAsync("AssertionRun"));
        Assert.Equal(seeded["AssertionResult"], await CountAsync("AssertionResult"));
        Assert.Equal(LedgerMoves.Failed, Assert.Single(await DeletionsAsync(id)).Outcome);
        Assert.Empty(await ledger.CompleteLedgerDeletesAsync());
    }

    private static DeliveryDimension Dimension(short partition, Guid ledger, string flow, string name) => new()
    {
        PartitionId = partition,
        FlowId = ledger,
        FlowName = flow,
        Name = name,
        Kind = "osdu:wks:work-product-component--WellLog:1.4.0",
        Path = "data.Name",
        CleanJson = "[]",
        DefinitionHash = new string('a', 16),
        CreatedUtc = At,
    };

    /// <summary>An assertion flow a repository declares, which works in partitions, as a sync describes it.</summary>
    private static DeliveryLedgerFlow Declaration(Guid repo, string flow) => new()
    {
        Id = Guid.NewGuid(),
        RepoId = repo,
        FlowName = flow,
        Kind = LedgerKinds.Assertion,
        Partitioned = true,
        DocumentHash = new string('d', 64),
        RelativePath = $"flows/{flow}.yaml",
        Active = true,
        FirstSeenUtc = At,
        LastSeenUtc = At,
    };

    /// <summary>Work for a record of <paramref name="flow"/> that queues a document, which claims the record's OSDU id.</summary>
    private static RecordState Claiming(Guid flow, string key) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [key]),
        FlowId = flow,
        SourceKey = key,
        MappingName = "WellLog",
        TargetId = "dev:work-product-component--WellLog:" + key,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };
}
