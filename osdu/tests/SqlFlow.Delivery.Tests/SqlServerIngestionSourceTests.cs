using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The SQL Server source over record tables of their own, in the chain fixture's database and ingestion schema: a read
/// paged and cut on the table's identity primary key, and the tables whose declared primary key a read cannot rely on.
/// Runs on the suites' test database (<see cref="OsduTestServer"/>).
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerIngestionSourceTests
{
    private static readonly DateTime Loaded = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Changed = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public async Task A_read_is_cut_on_the_identity_key_into_ranges_that_hold_every_candidate_once()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Item",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Item] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]); CREATE NONCLUSTERED INDEX [NCI_UpdatedDate_DW] ON {0} ([UpdatedDate_DW]);");

        // 6,000 rows, then a hole of 2,000 identity values, then 1,000 rows after the identity jumped far ahead: the values
        // the ranges are cut on are nowhere near dense. Every seventh value changed after the load.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [UpdatedDate_DW])
            SELECT CONCAT(N'item-', FORMAT(n.rn, 'D6')), @loaded
            FROM (SELECT TOP (6000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            ORDER BY n.rn;
            DELETE FROM {table} WHERE [RecId] BETWEEN 2001 AND 4000;
            DBCC CHECKIDENT ('[{estate.SilverSchema}].[Item]', RESEED, 100000) WITH NO_INFOMSGS;
            INSERT INTO {table} ([item_key], [UpdatedDate_DW])
            SELECT CONCAT(N'late-', FORMAT(n.rn, 'D6')), @loaded
            FROM (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            ORDER BY n.rn;
            UPDATE {table} SET [UpdatedDate_DW] = @changed WHERE [RecId] % 7 = 0;
            """);
        var ids = Enumerable.Range(1, 2000).Concat(Enumerable.Range(4001, 2000)).Concat(Enumerable.Range(100001, 1000)).Select(i => (long)i).ToList();
        var flow = ItemFlow(estate, "Item", pageSize: 250);

        // The whole table: eight ranges of the identity, each holding its share give or take one counted range of values.
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var full = await source.OpenAsync(SourceSelection.Full(), null);
        Assert.Equal((5000L, "RecId"), (full.EstimatedCandidates, full.PrimaryKey));
        var ranges = await source.SliceBoundsAsync(full, 8);
        Assert.Equal(8, ranges.Count);
        Assert.All(ranges, r => Assert.Equal("RecId", r.On));
        Assert.Equal((null, null), (ranges[0].From, ranges[^1].To));
        var width = ((101_000L - 1) / (8 * KeySlices.BucketsPerSlice)) + 1;
        var read = new List<long>();
        foreach (var range in ranges)
        {
            var slice = await ReadIdsAsync(source, full, range);
            Assert.InRange(slice.Count, 625 - width, 625 + width);
            Assert.Equal(slice.Order(), slice);
            read.AddRange(slice);
        }

        Assert.Equal(ids, read.Order());

        // Read whole, a page of 250 at a time, it meets every row once in identity order.
        Assert.Equal(ids, await ReadIdsAsync(source, full, null));

        // What changed since the load: a seventh of the rows, cut and read the same way.
        var changedSource = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var changed = await changedSource.OpenAsync(SourceSelection.Incremental(Loaded), null);
        var expected = ids.Where(i => i % 7 == 0).ToList();
        Assert.Equal(expected.Count, changed.EstimatedCandidates);
        var changedRanges = await changedSource.SliceBoundsAsync(changed, 4);
        Assert.Equal(4, changedRanges.Count);
        var changedRead = new List<long>();
        foreach (var range in changedRanges)
        {
            changedRead.AddRange(await ReadIdsAsync(changedSource, changed, range));
        }

        Assert.Equal(expected, changedRead.Order());

        // Named records are found through the record key and read in identity order.
        var keysSource = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var named = await keysSource.OpenAsync(SourceSelection.ForKeys([KeyTuple.Of("late-000001"), KeyTuple.Of("item-000005"), KeyTuple.Of("item-004001")]), null);
        Assert.Equal(3, named.EstimatedCandidates);
        Assert.Equal([5L, 4001L, 100001L], await ReadIdsAsync(keysSource, named, null));

        // A slice cut on the record key belongs to another way of reading, and is refused.
        var foreign = await Assert.ThrowsAsync<DeliveryException>(() => ReadIdsAsync(source, full, new KeyRange(0, null, KeyTuple.Of("item-000100"))));
        Assert.Contains("was cut on the record key", foreign.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_soft_deleted_in_the_window_is_a_candidate_although_its_update_column_did_not_move()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Tagged",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Tagged] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");

        // Three rows loaded; then the ingestion flow's key match tags one deleted, which stamps the delete column and
        // leaves the update column where the load put it, and another changes. One row was tagged before the window.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [UpdatedDate_DW], [DeletedDate_DW])
            VALUES (N'kept', @loaded, NULL), (N'tagged', @loaded, NULL), (N'changed', @loaded, NULL), (N'tagged-before', @loaded, @loaded);
            UPDATE {table} SET [DeletedDate_DW] = @changed WHERE [item_key] = N'tagged';
            UPDATE {table} SET [UpdatedDate_DW] = @changed WHERE [item_key] = N'changed';
            """);
        var flow = ItemFlow(estate, "Tagged");

        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var header = await source.OpenAsync(SourceSelection.Incremental(Loaded), null);
        Assert.Equal(2, header.EstimatedCandidates);
        Assert.True(header.HasChanges);
        var read = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        await foreach (var record in source.ReadAsync(header, null))
        {
            read[record.Row.GetString("item_key")!] = record.DeletedUtc;
        }

        Assert.Equal(["changed", "tagged"], read.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Changed, read["tagged"]);
        Assert.Null(read["changed"]);
    }

    [Fact]
    public async Task A_row_is_read_with_when_it_first_reached_the_table_and_a_column_that_cannot_say_so_is_refused()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        const string Unique = "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);";
        var table = await CreateTableAsync(
            estate,
            "Arrived",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Arrived] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [InsertedDate_DW] datetime NULL, [UpdatedDate_DW] datetime NULL",
            Unique);
        await CreateTableAsync(
            estate, "Unstamped", "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Unstamped] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);
        await CreateTableAsync(
            estate,
            "TextArrival",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_TextArrival] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [InsertedDate_DW] nvarchar(30) NULL, [UpdatedDate_DW] datetime NULL",
            Unique);

        // Inserted by one load and changed by the next: the insert stamp stays where the first load put it.
        await ExecuteAsync(estate, $"INSERT INTO {table} ([item_key], [InsertedDate_DW], [UpdatedDate_DW]) VALUES (N'item', @loaded, @changed);");
        await ExecuteAsync(estate, $"INSERT INTO [{estate.SilverSchema}].[Unstamped] ([item_key], [UpdatedDate_DW]) VALUES (N'item', @changed);");

        async Task<SqlFlow.Delivery.Rendering.SourceOrigin> OriginAsync(FlowDefinition flow)
        {
            var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
            var header = await source.OpenAsync(SourceSelection.Full(), null);
            await foreach (var record in source.ReadAsync(header, null))
            {
                return record.Origin;
            }

            throw new InvalidOperationException("The table holds one row, and the read found none.");
        }

        var arrived = await OriginAsync(ItemFlow(estate, "Arrived"));
        Assert.Equal((Loaded, Changed), (arrived.InsertedUtc, arrived.UpdatedUtc));

        // A table without the column is read without an arrival, unless the flow named the column itself.
        Assert.Null((await OriginAsync(ItemFlow(estate, "Unstamped"))).InsertedUtc);
        FlowDefinition Declared(string table)
        {
            var flow = ItemFlow(estate, table);
            return flow with { Source = flow.Source with { SystemColumns = flow.Source.SystemColumns with { InsertedDeclared = true } } };
        }

        var missing = await Assert.ThrowsAsync<FlowValidationException>(() => OriginAsync(Declared("Unstamped")));
        Assert.Contains("names column 'InsertedDate_DW', which the record table", missing.Message, StringComparison.Ordinal);

        // A column of that name that holds no moment is refused, and the message says how to opt out.
        var text = await Assert.ThrowsAsync<FlowValidationException>(() => OriginAsync(ItemFlow(estate, "TextArrival")));
        Assert.Contains("which is nvarchar(30)", text.Message, StringComparison.Ordinal);
        Assert.Contains("inserted: ~", text.Message, StringComparison.Ordinal);

        // Opted out, the same table reads without one.
        var optedOut = ItemFlow(estate, "TextArrival");
        optedOut = optedOut with { Source = optedOut.Source with { SystemColumns = optedOut.Source.SystemColumns with { Inserted = null } } };
        await ExecuteAsync(estate, $"INSERT INTO [{estate.SilverSchema}].[TextArrival] ([item_key], [InsertedDate_DW], [UpdatedDate_DW]) VALUES (N'item', N'yesterday', @changed);");
        Assert.Null((await OriginAsync(optedOut)).InsertedUtc);
    }

    [Fact]
    public async Task A_primary_key_a_read_cannot_rely_on_is_refused_with_what_it_lacks()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        const string Unique = "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);";
        await CreateTableAsync(estate, "PlainId", "[RecId] int NOT NULL, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);
        await CreateTableAsync(
            estate, "KeyedByName", "[RecId] int IDENTITY(1, 1) NOT NULL, [item_key] nvarchar(50) NOT NULL CONSTRAINT [PK_KeyedByName] PRIMARY KEY, [UpdatedDate_DW] datetime NULL", string.Empty);
        await CreateTableAsync(
            estate,
            "Composite",
            "[RecId] int IDENTITY(1, 1) NOT NULL, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, CONSTRAINT [PK_Composite] PRIMARY KEY ([RecId], [item_key])",
            Unique);
        await CreateTableAsync(
            estate, "NoUniqueKey", "[RecId] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_NoUniqueKey] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", string.Empty);
        await CreateTableAsync(
            estate,
            "History",
            "[RecId] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_History] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, [IsCurrent_DW] bit NOT NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]) WHERE [IsCurrent_DW] = 1;");
        await CreateTableAsync(
            estate, "TextId", "[RecId] nvarchar(20) NOT NULL CONSTRAINT [PK_TextId] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);
        await CreateTableAsync(
            estate, "Good", "[RecId] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Good] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);

        async Task<string> RefusedAsync(string table)
        {
            var source = estate.Engine.Sources.Open(ItemFlow(estate, table), NoValues, NullLoggerFactory.Instance);
            return (await Assert.ThrowsAsync<FlowValidationException>(() => source.OpenAsync(SourceSelection.Full(), null))).Message;
        }

        var plain = await RefusedAsync("PlainId");
        Assert.Contains("which is neither an identity column nor the table's primary key", plain, StringComparison.Ordinal);
        Assert.Contains($"ALTER TABLE [{estate.DatabaseName}].[{estate.SilverSchema}].[PlainId] ADD [RecId] bigint IDENTITY(1, 1) NOT NULL", plain, StringComparison.Ordinal);
        Assert.Contains("which is not the table's single-column primary key", await RefusedAsync("KeyedByName"), StringComparison.Ordinal);
        Assert.Contains("which is not the table's single-column primary key", await RefusedAsync("Composite"), StringComparison.Ordinal);
        Assert.Contains("has no unique index without a filter", await RefusedAsync("NoUniqueKey"), StringComparison.Ordinal);
        Assert.Contains("has no unique index without a filter", await RefusedAsync("History"), StringComparison.Ordinal);
        Assert.Contains("which is nvarchar(20). A read is paged and cut on an integer identity column", await RefusedAsync("TextId"), StringComparison.Ordinal);

        var good = estate.Engine.Sources.Open(ItemFlow(estate, "Good"), NoValues, NullLoggerFactory.Instance);
        Assert.Equal("RecId", (await good.OpenAsync(SourceSelection.Full(), null)).PrimaryKey);
        Assert.Equal(0, await CountRangesAsync(good));
    }

    [Fact]
    public async Task A_scope_parameter_offers_the_values_its_column_holds_most_rows_first_and_each_one_reads_its_rows()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Scoped",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Scoped] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NULL, [log_source] nvarchar(40) NULL, "
            + "[region_id] int NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");

        // Three COMPOSITE rows, two CPI, one with no source, and a CORE row the ingestion flow marked deleted. The
        // key column may hold a null, as SQLFlow's ingestion leaves it: a run checks its scope's rows for one, which needs the
        // scope's values, and the listing of what those values can be must not.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [log_source], [region_id], [UpdatedDate_DW], [DeletedDate_DW])
            VALUES (N'a', N'COMPOSITE', 7, @loaded, NULL), (N'b', N'COMPOSITE', 7, @loaded, NULL), (N'c', N'COMPOSITE', 9, @loaded, NULL),
                   (N'd', N'CPI', 9, @loaded, NULL), (N'e', N'CPI', NULL, @loaded, NULL), (N'f', NULL, 7, @loaded, NULL),
                   (N'g', N'CORE', 7, @loaded, @changed);
            """);
        var item = ItemFlow(estate, "Scoped");
        var flow = item with
        {
            Parameters = new Dictionary<string, FlowParameter>(StringComparer.Ordinal)
            {
                ["logSource"] = new FlowParameter { Required = true },
                ["region"] = new FlowParameter { Required = true },
            },
            Source = item.Source with
            {
                Record = item.Source.Record with
                {
                    Scope = new Dictionary<string, string>(StringComparer.Ordinal) { ["log_source"] = "logSource", ["region_id"] = "region" },
                },
            },
        };

        var listed = await estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance).ScopeValuesAsync(10);
        Assert.Equal(["logSource", "region"], listed.Select(p => p.Parameter));
        var logSource = listed[0];
        Assert.Equal("log_source", logSource.Column);
        Assert.Equal([new ScopeValue("COMPOSITE", 3), new ScopeValue("CPI", 2)], logSource.Values);
        Assert.False(logSource.More);
        Assert.Equal([new ScopeValue("7", 3), new ScopeValue("9", 2)], listed[1].Values);

        // A listing capped below what the column holds says there is more.
        var capped = (await estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance).ScopeValuesAsync(1))[0];
        Assert.Equal([new ScopeValue("COMPOSITE", 3)], capped.Values);
        Assert.True(capped.More);

        // A value listed is one the scope reads by: CPI in region 9 is one row.
        var scoped = estate.Engine.Sources.Open(
            flow, new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "CPI", ["region"] = "9" }, NullLoggerFactory.Instance);
        Assert.Equal(1, (await scoped.OpenAsync(SourceSelection.Full(), null)).EstimatedCandidates);

        // Several values select the rows holding any of them, each bound as a value of the column's own type: CPI and
        // COMPOSITE in region 9 are two rows, and CPI in regions 7 and 9 is one.
        var sources = estate.Engine.Sources.Open(
            flow,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = RunValueList.Of(["CPI", "COMPOSITE"]), ["region"] = "9" },
            NullLoggerFactory.Instance);
        Assert.Equal(2, (await sources.OpenAsync(SourceSelection.Full(), null)).EstimatedCandidates);
        var regions = estate.Engine.Sources.Open(
            flow,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "CPI", ["region"] = RunValueList.Of(["7", "9"]) },
            NullLoggerFactory.Instance);
        Assert.Equal(1, (await regions.OpenAsync(SourceSelection.Full(), null)).EstimatedCandidates);

        // A scope bound to a column the table does not hold is refused, naming the column and the parameter.
        var missing = flow with
        {
            Source = flow.Source with
            {
                Record = flow.Source.Record with { Scope = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_name"] = "logSource" } },
            },
        };
        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => estate.Engine.Sources.Open(missing, NoValues, NullLoggerFactory.Instance).ScopeValuesAsync(10));
        Assert.Contains("binds parameter 'logSource' to column 'source_name', which the table", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The sample flow, reading one of this test's tables by its item key and its identity primary key.</summary>
    // SQL Server keeps a session's isolation level when the pool lends the connection again: a session a page read left at
    // snapshot read the next borrower's statements at snapshot too, and failed every one on a database that refuses it.
    [Fact]
    public async Task A_page_read_under_snapshot_hands_its_connection_back_at_read_committed()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Pooled",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Pooled] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        await ExecuteAsync(estate, $"INSERT INTO {table} ([item_key], [UpdatedDate_DW]) VALUES (N'one', @loaded), (N'two', @loaded);");

        // The flow reads over a pool of one session, so the borrower after the read is the read's own session.
        var variable = "SQLFLOW_POOLED_SOURCE_" + estate.Suffix;
        var pooled = new SqlConnectionStringBuilder(estate.ConnectionString) { ApplicationName = "pooled-source-" + estate.Suffix, MaxPoolSize = 1 }.ConnectionString;
        var item = ItemFlow(estate, "Pooled");
        var flow = item with { Source = item.Source with { Connection = $"${{env:{variable}}}" } };
        var opened = IngestionConnection.Canonicalize(pooled, flow.Name);
        Environment.SetEnvironmentVariable(variable, pooled);
        try
        {
            Assert.Equal(SourceIsolation.Snapshot, flow.Source.Incremental.Isolation);
            var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
            var header = await source.OpenAsync(SourceSelection.Full(), null);
            Assert.Equal([1L, 2L], await ReadIdsAsync(source, header, null));

            await using var next = new SqlConnection(opened);
            await next.OpenAsync();
            await using var level = new SqlCommand("SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;", next);
            Assert.Equal((short)2, await level.ExecuteScalarAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            await using var pool = new SqlConnection(opened);
            SqlConnection.ClearPool(pool);
        }
    }

    private static FlowDefinition ItemFlow(SqlServerIngestionFixture estate, string table, int pageSize = FlowIncremental.DefaultPageSize)
    {
        var sample = estate.DeliveryFlow();
        return sample with
        {
            Source = sample.Source with
            {
                Record = new FlowSourceTable
                {
                    Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[{table}]",
                    Key = ["item_key"],
                    PrimaryKey = "RecId",
                },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal),
                Incremental = sample.Source.Incremental with { PageSize = pageSize },
            },
        };
    }

    private static async Task<int> CountRangesAsync(IIngestionSource source)
    {
        var header = await source.OpenAsync(SourceSelection.Full(), null);
        return header.EstimatedCandidates == 0 ? 0 : (await source.SliceBoundsAsync(header, 4)).Count;
    }

    private static async Task<List<long>> ReadIdsAsync(IIngestionSource source, SourceHeader header, KeyRange? range)
    {
        var ids = new List<long>();
        await foreach (var record in source.ReadAsync(header, range))
        {
            ids.Add(Convert.ToInt64(record.Row.Get("RecId"), CultureInfo.InvariantCulture));
        }

        return ids;
    }

    /// <summary>Creates a table in the fixture's ingestion schema, with the statements that follow it; <c>{0}</c> names the table.</summary>
    private static async Task<string> CreateTableAsync(SqlServerIngestionFixture estate, string name, string columns, string then)
    {
        var table = $"[{estate.SilverSchema}].[{name}]";
        await ExecuteAsync(estate, $"CREATE TABLE {table} ({columns});");
        if (then.Length > 0)
        {
            await ExecuteAsync(estate, string.Format(CultureInfo.InvariantCulture, then, table));
        }

        return table;
    }

    private static async Task ExecuteAsync(SqlServerIngestionFixture estate, string sql)
    {
        await using var connection = new SqlConnection(estate.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        command.Parameters.Add(new SqlParameter("@loaded", System.Data.SqlDbType.DateTime) { Value = Loaded });
        command.Parameters.Add(new SqlParameter("@changed", System.Data.SqlDbType.DateTime) { Value = Changed });
        await command.ExecuteNonQueryAsync();
    }
}
