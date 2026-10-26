using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a dimension build stages in the module's database as it reads (osdu/docs/reference/flow/dimension-build.md, What a
/// build holds): tables of the build's own, heaps until a table is read back in order; keys read back in the order staged
/// however many batches they filled, rows staged from several threads at once each kept once, a build's tables dropped when
/// it ends, what a build whose process stopped left dropped by the next build of its dimension unless that build may still
/// be running or is writing, and a build whose tables were dropped under it failing, writing nothing.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionStageTests : IDisposable
{
    private static readonly Guid FlowId = Guid.Parse("4c1f9e27-8a3d-4b6e-9d21-5e7f8a9b0c13");

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    public void Dispose() => _db.Dispose();

    private async Task<OsduLedger> LedgerAsync()
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = FlowId,
            Partition = "dev",
            Kind = LedgerKinds.Dimension,
            FlowName = "wells-dimensions",
            LedgerName = "wells-dimensions@dev",
        });
        return ledger;
    }

    private static DimensionDeclaration Declaration(string name = "CurveMnemonic") => new()
    {
        FlowId = FlowId,
        FlowName = "wells-dimensions",
        Name = name,
        Kind = "osdu:wks:work-product-component--WellLog:*",
        Path = "data.Curves.Mnemonic",
        CleanJson = """[{"kind":"upper"}]""",
        DefinitionHash = "0123456789abcdef",
    };

    /// <summary>Starts a build of <paramref name="name"/> now, and its staging.</summary>
    private async Task<DimensionStage> StageAsync(OsduLedger ledger, string name = "CurveMnemonic")
    {
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(name), Guid.NewGuid(), "tests", _clock.GetUtcNow().UtcDateTime);
        return await ledger.StageDimensionAsync(FlowId, dimension.DimensionId, run.DimensionRunId);
    }

    /// <summary>Stages <paramref name="count"/> keys, each a value of its own, and copies them to the staging tables.</summary>
    private static async Task StageValuesAsync(DimensionStage stage, int count)
    {
        var keys = Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"K{i:D6}")).ToList();
        await stage.AddKeysAsync(keys.Select(k => new KeyValuePair<string, long>(k, 1)).ToList(), CancellationToken.None);
        await stage.AddValuesAsync(keys.Select((k, i) => ((long)i, new DimensionOriginalWrite(k, k, null, null, 1, true))).ToList(), CancellationToken.None);
        await stage.AddMembersAsync(keys.Select(k => new DimensionMemberWrite(k, 1, false, 1, 0, $"filter of {k}", 1)).ToList(), CancellationToken.None);
        await stage.FlushAsync(CancellationToken.None);
    }

    /// <summary>The rows the staging tables hold, by the build whose tables they are: every build the database holds tables of.</summary>
    private async Task<Dictionary<long, int>> StagedAsync()
    {
        var staged = new Dictionary<long, int>();
        foreach (var (table, build, _) in await StagingTablesAsync())
        {
            staged[build] = staged.GetValueOrDefault(build) + Convert.ToInt32(await ScalarAsync($"SELECT COUNT_BIG(*) FROM [osdu].[{table}];"), CultureInfo.InvariantCulture);
        }

        return staged;
    }

    /// <summary>Every staging table the database holds: its name, its build, and whether it has a clustered index or is a heap.</summary>
    private async Task<List<(string Table, long Build, bool Clustered)>> StagingTablesAsync()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT t.[name], CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.[object_id] = t.[object_id] AND i.[index_id] = 1) THEN 1 ELSE 0 END)
            FROM sys.tables AS t
            WHERE t.[schema_id] = SCHEMA_ID(N'osdu') AND t.[name] LIKE N'dimstage[_]%';
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<(string, long, bool)>();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var parts = name[DimensionStage.TablePrefix.Length..].Split('_');
            tables.Add((name, long.Parse(parts[1], CultureInfo.InvariantCulture), reader.GetBoolean(1)));
        }

        return tables;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>The name of the stage's table <paramref name="table"/>.</summary>
    private static string TableOf(DimensionStage stage, string table)
        => string.Create(CultureInfo.InvariantCulture, $"{DimensionStage.TablePrefix}{stage.DimensionId}_{stage.DimensionRunId}_{table}");

    private static string Gone(DimensionStage stage) => string.Create(CultureInfo.InvariantCulture,
        $"The staging tables of dimension build {stage.DimensionRunId} are gone: another build of the dimension dropped them, taking this build, running for more than 48 hours, for one its process left, or the dimension was removed while it ran. Nothing was written; build the dimension again.");

    private static DimensionStagedWrite Write(DateTime completedUtc) => new()
    {
        Field = new DimensionFieldState("text", null, "data.Curves.Mnemonic.keyword", Repeats: true),
        Read = new DimensionReadCounts { Records = 3 },
        CompletedUtc = completedUtc,
    };

    [Fact]
    public async Task Keys_are_read_back_a_batch_at_a_time_in_the_order_staged_across_every_batch_the_staging_filled()
    {
        var ledger = await LedgerAsync();
        await using var stage = await StageAsync(ledger);

        // More keys than a staging batch holds, staged a thousand at a time, and some of them found gone, which are read back
        // with none of the others.
        var keys = Enumerable.Range(0, 12_345).Select(i => string.Create(CultureInfo.InvariantCulture, $"dev:master-data--Wellbore:{12_345 - i:D5}:")).ToList();
        foreach (var chunk in keys.Chunk(1000))
        {
            await stage.AddKeysAsync(chunk.Select(k => new KeyValuePair<string, long>(k, 2)).ToList(), CancellationToken.None);
        }

        await stage.AddGoneAsync(["gone-1", "gone-2", "gone-1"], CancellationToken.None);

        var batches = new List<IReadOnlyList<StagedKey>>();
        await foreach (var batch in stage.KeysAsync(1000, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(13, batches.Count);
        Assert.All(batches.SkipLast(1), b => Assert.Equal(1000, b.Count));
        var read = batches.SelectMany(b => b).ToList();
        Assert.Equal(keys, read.Select(k => k.Key));
        Assert.All(read, k => Assert.Equal(2L, k.Count));
        Assert.True(read.Zip(read.Skip(1)).All(p => p.First.Seq < p.Second.Seq), "The keys came back out of the order they were staged in.");
        Assert.Equal(12_345L, stage.Keys);

        // A read that starts over deletes the keys, and the keys staged after take places after theirs.
        await stage.ResetKeysAsync(CancellationToken.None);
        Assert.Equal(0L, stage.Keys);
        await stage.AddKeysAsync([new("again-1", 1), new("again-2", 1)], CancellationToken.None);
        var again = new List<StagedKey>();
        await foreach (var batch in stage.KeysAsync(1000, CancellationToken.None))
        {
            again.AddRange(batch);
        }

        Assert.Equal(["again-1", "again-2"], again.Select(k => k.Key));
        Assert.True(again[0].Seq > read[^1].Seq);
        await stage.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Rows_staged_from_several_threads_at_once_are_each_staged_once()
    {
        var ledger = await LedgerAsync();
        await using var stage = await StageAsync(ledger);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(async () =>
        {
            for (var chunk = 0; chunk < 25; chunk++)
            {
                var rows = Enumerable.Range(0, 100)
                    .Select(i => (string.Create(CultureInfo.InvariantCulture, $"K{thread}-{chunk}-{i}"), new DimensionAttributeState("Country", "Norway", null, Records: 1)))
                    .ToList();
                await stage.AddAttributesAsync(rows, CancellationToken.None);
            }
        })));
        await stage.FlushAsync(CancellationToken.None);

        Assert.Equal(20_000L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT_BIG(*) FROM [osdu].[{TableOf(stage, "attribute")}];"), CultureInfo.InvariantCulture));
        Assert.Equal(20_000L, Convert.ToInt64(await ScalarAsync($"SELECT COUNT_BIG(DISTINCT [Seq]) FROM [osdu].[{TableOf(stage, "attribute")}];"), CultureInfo.InvariantCulture));
        await stage.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_build_stages_into_heaps_of_its_own_and_indexes_a_table_only_once_it_is_read_back_in_order()
    {
        var ledger = await LedgerAsync();
        await using var stage = await StageAsync(ledger);
        var other = await StageAsync(ledger, "Unit");

        // Each build has its seven tables, heaps a copy fills under a bulk update lock.
        var tables = await StagingTablesAsync();
        Assert.Equal(7, tables.Count(t => t.Build == stage.DimensionRunId));
        Assert.Equal(7, tables.Count(t => t.Build == other.DimensionRunId));
        Assert.All(tables, t => Assert.False(t.Clustered, $"{t.Table} has a clustered index before anything was read from it."));

        // Read back in order, the keys are indexed on their place once staged whole; the other tables stay heaps.
        await stage.AddKeysAsync([new("A", 1), new("B", 1)], CancellationToken.None);
        await foreach (var _ in stage.KeysAsync(1000, CancellationToken.None))
        {
        }

        tables = await StagingTablesAsync();
        Assert.Equal([TableOf(stage, "key")], tables.Where(t => t.Clustered).Select(t => t.Table));
        await other.DiscardAsync(CancellationToken.None);
        await other.DisposeAsync();
        await stage.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_build_drops_its_staging_when_it_ends_written_or_not()
    {
        var ledger = await LedgerAsync();

        // A build of more rows than a batch copies, which ends without writing, with rows still held that it never copies.
        var failed = await StageAsync(ledger);
        await StageValuesAsync(failed, 12_345);
        Assert.Equal(3 * 12_345, (await StagedAsync())[failed.DimensionRunId]);
        await failed.AddKeysAsync([new("held", 1)], CancellationToken.None);
        await failed.DiscardAsync(CancellationToken.None);
        await failed.DisposeAsync();
        Assert.True(failed.Discarded);
        Assert.Empty(await StagedAsync());

        // A build written whole from memory stages its rows and drops them once written.
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(), Guid.NewGuid(), "tests", _clock.GetUtcNow().UtcDateTime);
        var written = await ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = FlowId,
            Field = new DimensionFieldState("text", null, "data.Curves.Mnemonic.keyword", Repeats: true),
            Originals = [new DimensionOriginalWrite("GR", "GR", null, null, 2, true, Attributes: [new DimensionAttributeState("Unit", "API", null)])],
            Members = [new DimensionMemberWrite("GR", 2, false, 1, 0, "filter of GR", 1)],
            KeyRecords = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["GR"] = ["dev:reference-data--LogCurveType:GR"] },
            Read = new DimensionReadCounts { Records = 2 },
            CompletedUtc = _clock.GetUtcNow().UtcDateTime,
        });

        Assert.Equal((DimensionRunStatus.Completed, 1L, 1L), (written.Status, written.Members, written.Originals));
        Assert.Empty(await StagedAsync());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 49)]
    public async Task What_a_build_whose_process_stopped_left_is_dropped_by_the_next_build_of_its_dimension(bool closed, int hoursLater)
    {
        var ledger = await LedgerAsync();
        var stopped = await StageAsync(ledger);
        await StageValuesAsync(stopped, 20);
        await stopped.DisposeAsync();
        var other = await StageAsync(ledger, "Unit");
        await StageValuesAsync(other, 5);
        await other.DisposeAsync();
        if (closed)
        {
            await ledger.CloseDimensionRunAsync(stopped.DimensionRunId, DimensionRunStatus.Failed, new DimensionReadCounts(), "the worker stopped", _clock.GetUtcNow().UtcDateTime);
        }

        // Closed, or running for longer than any build takes: either way no process is building it now.
        _clock.Advance(TimeSpan.FromHours(hoursLater));
        await using var next = await StageAsync(ledger);

        var staged = await StagedAsync();
        Assert.False(staged.ContainsKey(stopped.DimensionRunId));
        Assert.Equal(15, staged[other.DimensionRunId]);
        await next.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task What_a_build_that_may_still_be_running_staged_is_left_to_it()
    {
        var ledger = await LedgerAsync();
        await using var running = await StageAsync(ledger);
        await StageValuesAsync(running, 20);

        _clock.Advance(DimensionStage.AbandonedAfter - TimeSpan.FromMinutes(1));
        await using var next = await StageAsync(ledger);

        Assert.Equal(60, (await StagedAsync())[running.DimensionRunId]);
        await next.DiscardAsync(CancellationToken.None);
        await running.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task What_a_stopped_build_left_waits_while_another_build_writes_the_dimension_and_goes_with_the_next_build()
    {
        var ledger = await LedgerAsync();
        var stopped = await StageAsync(ledger);
        await StageValuesAsync(stopped, 20);
        await stopped.DisposeAsync();
        await ledger.CloseDimensionRunAsync(stopped.DimensionRunId, DimensionRunStatus.Failed, new DimensionReadCounts(), "the worker stopped", _clock.GetUtcNow().UtcDateTime);

        // A build writing the dimension holds its write lock: the next build's staging starts at once, dropping nothing.
        await using (var writing = new SqlConnection(_db.ConnectionString))
        {
            await writing.OpenAsync();
            await using var tx = (SqlTransaction)await writing.BeginTransactionAsync();
            await using (var locking = new SqlCommand("EXEC sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction';", writing, tx))
            {
                locking.Parameters.AddWithValue("@resource", string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{stopped.PartitionId}:{stopped.DimensionId}"));
                await locking.ExecuteNonQueryAsync();
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await using var during = await StageAsync(ledger);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The staging waited {watch.Elapsed.TotalSeconds:N1} s for the build writing the dimension.");
            Assert.Equal(60, (await StagedAsync())[stopped.DimensionRunId]);
            await during.DiscardAsync(CancellationToken.None);
            await tx.RollbackAsync();
        }

        await using var after = await StageAsync(ledger);
        Assert.False((await StagedAsync()).ContainsKey(stopped.DimensionRunId));
        await after.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_write_whose_staging_a_later_build_dropped_is_refused_and_writes_nothing()
    {
        var ledger = await LedgerAsync();
        await using var slow = await StageAsync(ledger);
        await StageValuesAsync(slow, 3);

        // The build runs past the time any build takes, and a later build takes it for one its process left.
        _clock.Advance(DimensionStage.AbandonedAfter + TimeSpan.FromHours(1));
        await using (var later = await StageAsync(ledger))
        {
            await later.DiscardAsync(CancellationToken.None);
        }

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => ledger.WriteDimensionAsync(slow, Write(_clock.GetUtcNow().UtcDateTime)));

        Assert.Equal(Gone(slow), refused.Message);
        Assert.Empty(await ledger.ListDimensionMembersAsync(slow.DimensionId, new DimensionMemberQuery(null, true, null, 10)));
        var run = Assert.Single(await ledger.ListDimensionRunsAsync(slow.DimensionId, 10), r => r.DimensionRunId == slow.DimensionRunId);
        Assert.Equal(DimensionRunStatus.Running, run.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_build_whose_staging_was_dropped_under_it_fails_saying_so_and_ends_without_another_error(bool removed)
    {
        var ledger = await LedgerAsync();
        await using var slow = await StageAsync(ledger);
        await StageValuesAsync(slow, 3);

        // A later build takes it for one its process left, or the dimension is removed while it runs.
        if (removed)
        {
            await ledger.RemoveDimensionAsync(slow.DimensionId);
        }
        else
        {
            _clock.Advance(DimensionStage.AbandonedAfter + TimeSpan.FromHours(1));
            await using var later = await StageAsync(ledger);
            await later.DiscardAsync(CancellationToken.None);
        }

        // Staging more copies into a table that is gone, as does reading the keys back.
        await slow.AddKeysAsync([new("more", 1)], CancellationToken.None);
        var copy = await Assert.ThrowsAsync<DeliveryException>(() => slow.FlushAsync(CancellationToken.None));
        Assert.Equal(Gone(slow), copy.Message);
        var read = await Assert.ThrowsAsync<DeliveryException>(async () =>
        {
            await foreach (var _ in slow.KeysAsync(10, CancellationToken.None))
            {
            }
        });
        Assert.Equal(Gone(slow), read.Message);

        // The build ends as any other does, its staging dropped with nothing left to drop.
        await slow.DiscardAsync(CancellationToken.None);
        Assert.True(slow.Discarded);
        Assert.False((await StagedAsync()).ContainsKey(slow.DimensionRunId));
    }

    [Fact]
    public async Task A_staged_write_merges_what_its_build_staged_and_leaves_the_staging_to_the_build()
    {
        var ledger = await LedgerAsync();
        await using var stage = await StageAsync(ledger);
        await StageValuesAsync(stage, 3);

        var written = await ledger.WriteDimensionAsync(stage, Write(_clock.GetUtcNow().UtcDateTime));

        Assert.Equal((DimensionRunStatus.Completed, 3L, 3L), (written.Status, written.Members, written.Originals));
        Assert.Equal(["K000000", "K000001", "K000002"], (await ledger.ListDimensionMembersAsync(stage.DimensionId, new DimensionMemberQuery(null, false, null, 10))).Select(m => m.Value));
        Assert.Equal(9, (await StagedAsync())[stage.DimensionRunId]);
        await stage.DiscardAsync(CancellationToken.None);
        Assert.Empty(await StagedAsync());
    }
}
