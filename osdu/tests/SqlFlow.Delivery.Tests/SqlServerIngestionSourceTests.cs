using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
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

        // One parameter's values, as a run form looks them up: those whose text holds what is typed, ignoring case, the
        // typed text's wildcards taken as written; a column a lookup names that the table does not hold is refused.
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        Assert.Equal([new ScopeValue("COMPOSITE", 3)], (await source.ColumnValuesAsync("logSource", "log_source", "omp", 10)).Values);
        Assert.Equal([new ScopeValue("COMPOSITE", 3), new ScopeValue("CPI", 2)], (await source.ColumnValuesAsync("logSource", "log_source", "c", 10)).Values);
        Assert.Empty((await source.ColumnValuesAsync("logSource", "log_source", "%", 10)).Values);
        Assert.Empty((await source.ColumnValuesAsync("logSource", "log_source", "C_I", 10)).Values);
        Assert.Equal([new ScopeValue("9", 2)], (await source.ColumnValuesAsync("region", "region_id", "9", 10)).Values);
        var all = await source.ColumnValuesAsync("logSource", "log_source", null, 1);
        Assert.Equal(("log_source", true), (all.Column, all.More));
        var unknown = await Assert.ThrowsAsync<FlowValidationException>(() => source.ColumnValuesAsync("country", "country_code", null, 10));
        Assert.Contains("parameters.country.lookup names column 'country_code', which the table", unknown.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Staged_rows_are_paged_by_key_searched_in_the_database_and_counted_across_their_child_tables()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Staged",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Staged] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [item_name] nvarchar(100) NULL, "
                + "[region] nvarchar(20) NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL, [FileName_DW] nvarchar(400) NULL, [RowNumber_DW] bigint NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        var parts = await CreateTableAsync(
            estate,
            "StagedPart",
            "[item_key] nvarchar(50) NOT NULL, [part_no] int NOT NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            string.Empty);
        var notes = await CreateTableAsync(estate, "StagedNote", "[item_key] nvarchar(50) NOT NULL, [note] nvarchar(100) NULL, [UpdatedDate_DW] datetime NULL", string.Empty);

        // 2,500 rows in two regions, the fifth of them each with three parts (one of them deleted), the tenth with a note.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [item_name], [region], [UpdatedDate_DW], [FileName_DW], [RowNumber_DW])
            SELECT CONCAT(N'item-', FORMAT(n.rn, 'D6')), CONCAT(N'Thing ', n.rn), CASE WHEN n.rn % 2 = 0 THEN N'north' ELSE N'south' END, @loaded, N'staged_items.csv', n.rn
            FROM (SELECT TOP (2500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            ORDER BY n.rn;
            INSERT INTO {parts} ([item_key], [part_no], [DeletedDate_DW])
            SELECT r.[item_key], p.n, CASE WHEN p.n = 3 THEN @loaded END
            FROM {table} r CROSS JOIN (VALUES (1), (2), (3)) AS p(n) WHERE r.[RecId] % 5 = 0;
            INSERT INTO {notes} ([item_key], [note]) SELECT r.[item_key], N'a note' FROM {table} r WHERE r.[RecId] % 10 = 0;
            UPDATE {table} SET [DeletedDate_DW] = @changed WHERE [RecId] = 7;
            UPDATE {table} SET [item_name] = N'100% odd_name' WHERE [RecId] = 9;
            """);
        var sample = ItemFlow(estate, "Staged");
        var flow = sample with
        {
            Source = sample.Source with
            {
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal)
                {
                    ["parts"] = new FlowSourceDataset
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[StagedPart]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                    },
                    ["notes"] = new FlowSourceDataset
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[StagedNote]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                    },
                },
            },
        };
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);

        // Page by page, every row once and in the order a read pages in, the count on the first page alone.
        var seen = new List<StagedRow>();
        KeyTuple? after = null;
        var pages = 0;
        do
        {
            var page = await source.ReadStagedAsync(new StagedRowsQuery { Limit = 400, After = after, Columns = ["item_name", "no_such_column"] });
            Assert.Equal(after is null ? 2500 : null, page.Total);
            Assert.True(page.Rows.Count <= 400);
            seen.AddRange(page.Rows);
            after = page.Next;
            pages++;
        }
        while (after is not null);

        Assert.Equal(7, pages);
        Assert.Equal(2500, seen.Count);
        Assert.Equal(2500, seen.Select(row => row.Key.Values[0]).Distinct().Count());
        Assert.Equal("item-000001", seen[0].Key.Values[0]);
        Assert.Equal("item-002500", seen[^1].Key.Values[0]);

        // A row says where it came from, what the columns asked hold, and only those the table has.
        var first = seen[0];
        Assert.Equal("staged_items.csv", first.File);
        Assert.Equal(1, first.Row);
        Assert.Equal(Loaded, first.UpdatedUtc);
        Assert.Equal("Thing 1", first.Columns["item_name"]);
        Assert.False(first.Columns.ContainsKey("no_such_column"));
        Assert.Null(first.DeletedUtc);
        Assert.Equal(Changed, seen[6].DeletedUtc);

        // What the child tables hold of a row is counted, a child row marked deleted left out, and a table holding none says nothing.
        Assert.Empty(first.Children);
        Assert.Equal(new Dictionary<string, long> { ["parts"] = 2 }, seen[4].Children);
        Assert.Equal(new Dictionary<string, long> { ["parts"] = 2, ["notes"] = 1 }, seen[9].Children);

        // A search reads the key, the columns asked and the file, ignoring case, and takes wildcards as written.
        var key = await source.ReadStagedAsync(new StagedRowsQuery { Limit = 10, Search = "ITEM-000123" });
        Assert.Equal(("item-000123", 1L, (object?)null), (Assert.Single(key.Rows).Key.Values[0], key.Total!.Value, (object?)key.Next));
        var named = await source.ReadStagedAsync(new StagedRowsQuery { Limit = 10, Search = "thing 25", Columns = ["item_name"] });
        Assert.Equal(1 + 10 + 1, named.Total);
        Assert.Equal(10, named.Rows.Count);
        Assert.NotNull(named.Next);
        var rest = await source.ReadStagedAsync(new StagedRowsQuery { Limit = 10, Search = "thing 25", Columns = ["item_name"], After = named.Next });
        Assert.Equal(2, rest.Rows.Count);
        Assert.Null(rest.Next);
        Assert.Null(rest.Total);
        var literal = await source.ReadStagedAsync(new StagedRowsQuery { Limit = 10, Search = "100% odd_name", Columns = ["item_name"] });
        Assert.Equal("item-000009", Assert.Single(literal.Rows).Key.Values[0]);
        Assert.Equal(2500, (await source.ReadStagedAsync(new StagedRowsQuery { Limit = 1, Search = "staged_items" })).Total);
        Assert.Equal(0, (await source.ReadStagedAsync(new StagedRowsQuery { Limit = 10, Search = "no such thing" })).Total);

        // A scope narrows the page, the count and the search alike.
        var scoped = flow with
        {
            Parameters = new Dictionary<string, FlowParameter>(StringComparer.Ordinal) { ["region"] = new FlowParameter { Required = true } },
            Source = flow.Source with
            {
                Record = flow.Source.Record with { Scope = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "region" } },
            },
        };
        var north = estate.Engine.Sources.Open(scoped, new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "north" }, NullLoggerFactory.Instance);
        var inScope = await north.ReadStagedAsync(new StagedRowsQuery { Limit = 5 });
        Assert.Equal(1250, inScope.Total);
        Assert.Equal("item-000002", inScope.Rows[0].Key.Values[0]);
    }

    [Fact]
    public async Task A_tables_first_rows_are_read_in_the_scope_without_deleted_rows_cut_by_the_database_and_counted_to_a_cap()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Looked",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Looked] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [item_name] nvarchar(100) NULL, [notes] nvarchar(max) NULL, "
                + "[region] nvarchar(20) NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL, [FileName_DW] nvarchar(400) NULL, [RowNumber_DW] bigint NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        var parts = await CreateTableAsync(
            estate,
            "LookedPart",
            "[item_key] nvarchar(50) NOT NULL, [part_no] int NOT NULL, [weight] decimal(9,3) NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            string.Empty);

        // 300 rows in two regions, the third marked deleted and the fifth holding a long note; the first ten with three parts, one marked deleted.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [item_name], [notes], [region], [UpdatedDate_DW], [FileName_DW], [RowNumber_DW])
            SELECT CONCAT(N'item-', FORMAT(n.rn, 'D6')), CONCAT(N'Thing ', n.rn), CASE WHEN n.rn = 5 THEN REPLICATE(CAST(N'x' AS nvarchar(max)), 5000) END,
                CASE WHEN n.rn % 2 = 0 THEN N'north' ELSE N'south' END, @loaded, N'looked.csv', n.rn
            FROM (SELECT TOP (300) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            ORDER BY n.rn;
            UPDATE {table} SET [DeletedDate_DW] = @changed WHERE [RecId] = 3;
            INSERT INTO {parts} ([item_key], [part_no], [weight], [DeletedDate_DW])
            SELECT r.[item_key], p.n, p.n * 1.5, CASE WHEN p.n = 3 THEN @loaded END
            FROM {table} r CROSS JOIN (VALUES (3), (2), (1)) AS p(n) WHERE r.[RecId] <= 10;
            """);
        var sample = ItemFlow(estate, "Looked");
        var flow = sample with
        {
            Source = sample.Source with
            {
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal)
                {
                    ["parts"] = new FlowSourceDataset
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[LookedPart]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                        OrderBy = ["part_no"],
                    },
                },
            },
        };
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);

        // The record table: every column but SQLFlow's, in the table's order, the row marked deleted left out, in the order a read pages in.
        var record = await source.ReadTableAsync(new TableRowsQuery { Limit = 100, MaxCharacters = 1000 });
        Assert.Equal(flow.Source.Record.Object, record.Table);
        Assert.Equal(["RecId", "item_key", "item_name", "notes", "region"], record.Columns.Select(c => c.Name));
        Assert.Equal("nvarchar(100)", record.Columns[2].SqlType);
        Assert.Equal(100, record.Rows.Count);
        Assert.Equal(["1", "item-000001", "Thing 1", null, "south"], record.Rows[0]);
        Assert.Equal("item-000004", record.Rows[2][1]);
        Assert.Equal((299L, false), (record.Counted, record.CountCapped));

        // A long value is cut to what the query keeps, and its column says so; the others say nothing.
        Assert.Equal(new string('x', 1000), record.Rows[3][3]);
        Assert.Equal([false, false, false, true, false], record.Columns.Select(c => c.Cut));

        // A count stops where the query asks, and says there are more.
        var capped = await source.ReadTableAsync(new TableRowsQuery { Limit = 5, CountUpTo = 250 });
        Assert.Equal((5, 250L, true), (capped.Rows.Count, capped.Counted, capped.CountCapped));

        // A child dataset: its rows of the records read, its own deleted rows and those of a deleted record left out, in its order.
        var child = await source.ReadTableAsync(new TableRowsQuery { Dataset = "parts", Limit = 100 });
        Assert.Equal(["item_key", "part_no", "weight"], child.Columns.Select(c => c.Name));
        Assert.Equal(18, child.Rows.Count);
        // A number reads as the text a delivery derives from it, in its shortest form.
        Assert.Equal(["item-000001", "1", "1.5"], child.Rows[0]);
        Assert.Equal(["item-000001", "2", "3"], child.Rows[1]);
        Assert.Equal("item-000002", child.Rows[2][0]);
        Assert.DoesNotContain(child.Rows, row => row[0] == "item-000003");
        Assert.Equal((18L, false), (child.Counted, child.CountCapped));

        // A scope narrows the record table and the child dataset alike.
        var scoped = flow with
        {
            Parameters = new Dictionary<string, FlowParameter>(StringComparer.Ordinal) { ["region"] = new FlowParameter { Required = true } },
            Source = flow.Source with
            {
                Record = flow.Source.Record with { Scope = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "region" } },
            },
        };
        var north = estate.Engine.Sources.Open(scoped, new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "north" }, NullLoggerFactory.Instance);
        Assert.Equal(150, (await north.ReadTableAsync(new TableRowsQuery { Limit = 1 })).Counted);
        Assert.Equal(10, (await north.ReadTableAsync(new TableRowsQuery { Dataset = "parts", Limit = 100 })).Rows.Count);

        // A dataset the flow does not read is refused by name.
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => source.ReadTableAsync(new TableRowsQuery { Dataset = "notes", Limit = 1 }));
        Assert.Contains("reads no child dataset 'notes'; it reads parts", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_query_of_an_attribute_runs_as_written_and_returns_the_rows_a_read_reads()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Queried",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Queried] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [depth] nvarchar(20) NULL, "
                + "[region] nvarchar(20) NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL, [FileName_DW] nvarchar(400) NULL, [RowNumber_DW] bigint NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        var parts = await CreateTableAsync(
            estate,
            "QueriedPart",
            "[item_key] nvarchar(50) NOT NULL, [curve_id] nvarchar(50) NULL, [part_no] int NOT NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            string.Empty);

        // Four rows: two in scope with one's key holding a quote, one of them with a deleted part; one out of scope; one deleted.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [depth], [region], [UpdatedDate_DW], [FileName_DW], [RowNumber_DW]) VALUES
                (N'item-1', N'10', N'it''s north', @loaded, N'items.csv', 1),
                (N'O''Brien', N'20', N'it''s north', @loaded, N'items.csv', 2),
                (N'item-3', N'30', N'south', @loaded, N'items.csv', 3),
                (N'item-4', N'40', N'it''s north', @loaded, N'items.csv', 4);
            UPDATE {table} SET [DeletedDate_DW] = @changed WHERE [item_key] = N'item-4';
            INSERT INTO {parts} ([item_key], [curve_id], [part_no], [DeletedDate_DW]) VALUES
                (N'item-1', N'GR', 1, NULL), (N'item-1', N'DT', 2, NULL), (N'item-1', N'OLD', 3, @changed);
            """);
        var sample = ItemFlow(estate, "Queried");
        var flow = sample with
        {
            Source = sample.Source with
            {
                Record = sample.Source.Record with { Scope = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "region" } },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal)
                {
                    ["curves"] = new FlowSourceDataset
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[QueriedPart]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                        OrderBy = ["part_no"],
                    },
                },
            },
        };
        var source = estate.Engine.Sources.Open(flow, new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "it's north" }, NullLoggerFactory.Instance);
        var described = await source.DescribeAsync();
        Assert.Equal(["FileName_DW", "RowNumber_DW"], described.Origin);
        var mapping = TestSchema.Mapping("""
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
            """);
        TemplatePath Path(string text) => TemplatePath.TryParse(text, out var path, out var error) ? path! : throw new InvalidOperationException(error);

        // The record's own column: the rows in scope that are not deleted, in the order a read pages in.
        var record = SourceQuery.For(described, mapping, flow.Label, null, Path("osdu.data.Depth"), 1000, []);
        Assert.Equal([["item-1", "10", "items.csv", "1"], ["O'Brien", "20", "items.csv", "2"]], await RunAsync(estate, record.Sql!));

        // A child's: its rows that are not deleted in their order, and a record with none still there, which is what a held record often is.
        var child = SourceQuery.For(described, mapping, flow.Label, null, Path("osdu.data.Curves[].CurveID"), 1000, []);
        Assert.Equal(
            [["item-1", "GR", "items.csv", "1"], ["item-1", "DT", "items.csv", "1"], ["O'Brien", null, "items.csv", "2"]],
            await RunAsync(estate, child.Sql!));

        // Narrowed to a record a check named, a key holding a quote included, whatever the row budget.
        var named = SourceQuery.For(described, mapping, flow.Label, null, Path("osdu.data.Depth"), 1, [KeyTuple.Of("O'Brien")]);
        Assert.Equal([["O'Brien", "20", "items.csv", "2"]], await RunAsync(estate, named.Sql!));

        // A row budget bounds what comes back.
        var first = SourceQuery.For(described, mapping, flow.Label, null, Path("osdu.data.Depth"), 1, []);
        Assert.Equal([["item-1", "10", "items.csv", "1"]], await RunAsync(estate, first.Sql!));
    }

    [Fact]
    public async Task A_findings_proof_runs_as_written_and_reads_the_rows_holding_its_values_or_leaving_its_columns_empty()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Proved",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Proved] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [depth] int NULL, [unit] nvarchar(20) NULL, "
                + "[region] nvarchar(20) NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL, [FileName_DW] nvarchar(400) NULL, [RowNumber_DW] bigint NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        var parts = await CreateTableAsync(
            estate,
            "ProvedPart",
            "[item_key] nvarchar(50) NOT NULL, [curve_unit] nvarchar(20) NULL, [part_no] int NOT NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL, "
                + "[FileName_DW] nvarchar(400) NULL, [RowNumber_DW] bigint NULL",
            string.Empty);

        // In scope: item-1, O'Brien (a key and a value holding a quote) and item-3, whose unit is blank and depth missing. Out of
        // it: item-4, of another region, and item-5, deleted. One of item-1's parts is deleted.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [depth], [unit], [region], [UpdatedDate_DW], [FileName_DW], [RowNumber_DW]) VALUES
                (N'item-1', 10, N'FT', N'north', @loaded, N'items.csv', 1),
                (N'O''Brien', 20, N'it''s', N'north', @loaded, N'items.csv', 2),
                (N'item-3', NULL, N'  ', N'north', @loaded, N'items.csv', 3),
                (N'item-4', 10, N'FT', N'south', @loaded, N'items.csv', 4),
                (N'item-5', 10, N'FT', N'north', @loaded, N'items.csv', 5);
            UPDATE {table} SET [DeletedDate_DW] = @changed WHERE [item_key] = N'item-5';
            INSERT INTO {parts} ([item_key], [curve_unit], [part_no], [DeletedDate_DW], [FileName_DW], [RowNumber_DW]) VALUES
                (N'item-1', N'FURLONG', 1, NULL, N'curves.csv', 11), (N'item-1', N'm', 2, NULL, N'curves.csv', 12),
                (N'item-1', N'FURLONG', 3, @changed, N'curves.csv', 13), (N'O''Brien', N'FURLONG', 1, NULL, N'curves.csv', 21);
            """);
        var sample = ItemFlow(estate, "Proved");
        var flow = sample with
        {
            Source = sample.Source with
            {
                Record = sample.Source.Record with { Scope = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "region" } },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal)
                {
                    ["curves"] = new FlowSourceDataset
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[ProvedPart]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                        OrderBy = ["part_no"],
                    },
                },
            },
        };
        var source = estate.Engine.Sources.Open(flow, new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "north" }, NullLoggerFactory.Instance);
        var described = await source.DescribeAsync();
        Assert.Equal("int", described.TypeOf(SourceDatasets.Record, "depth"));

        // Each statement answers the same run here as copied into SSMS, and both read only the scope's rows that are not deleted.
        async Task<List<List<string?>>> BothAsync(string column, bool empty, params string[] held)
        {
            var notes = new List<string>();
            var named = column.Split('.');
            var (read, values) = ValueProver.ReadOf(
                described,
                [new DatasetColumn(named.Length == 3 ? named[1] : null, named[^1])],
                held.Select(v => new ValueCheckSourceValue(column, v, 1)).ToList(),
                empty,
                notes);
            Assert.NotNull(read);
            var answers = new List<List<string?>>();
            foreach (var records in new[] { false, true })
            {
                var statement = records ? IngestionSql.ProofRecords(described.Layout, read) : IngestionSql.ProofValues(described.Layout, read);
                var script = await RunAsync(estate, ValueProver.SourceScript(flow.Label, "Proof", "What it proves.", described, read, values, statement));
                var run = await source.ReadProofAsync(new SourceProofQuery { Read = read, Records = records, Values = values, Limit = 100 });
                Assert.False(run.More);
                Assert.Equal(script, run.Rows.Select(r => r.ToList()).ToList());
                answers.AddRange(run.Rows.Select(r => r.ToList()));
            }

            return answers;
        }

        // A record's column holding the values the check met: counted by value, then listed with key, value and origin.
        Assert.Equal(
            [
                ["dataset.unit", "FT", "1"], ["dataset.unit", "it's", "1"],
                ["item-1", "FT", "items.csv", "1"], ["O'Brien", "it's", "items.csv", "2"],
            ],
            await BothAsync("dataset.unit", empty: false, "FT", "it's"));

        // A number is compared as a number, whatever text the check met it as.
        Assert.Equal([["dataset.depth", "10", "1"], ["item-1", "10", "items.csv", "1"]], await BothAsync("dataset.depth", empty: false, "10"));

        // An empty column: blank text and a missing number alike.
        Assert.Equal([["1"], ["item-3", "  ", "items.csv", "3"]], await BothAsync("dataset.unit", empty: true));
        Assert.Equal([["1"], ["item-3", null, "items.csv", "3"]], await BothAsync("dataset.depth", empty: true));

        // A child's column: its rows that are not deleted, each with its own file and row beside its record's.
        Assert.Equal(
            [
                ["dataset.curves.curve_unit", "FURLONG", "2"],
                ["item-1", "FURLONG", "curves.csv", "11", "items.csv", "1"],
                ["O'Brien", "FURLONG", "curves.csv", "21", "items.csv", "2"],
            ],
            await BothAsync("dataset.curves.curve_unit", empty: false, "FURLONG"));
    }

    /// <summary>Runs a script as a person does, parameters and all declared in it, and answers every row of every statement as text.</summary>
    private static async Task<List<List<string?>>> RunAsync(SqlServerIngestionFixture estate, string script)
    {
        await using var connection = new SqlConnection(estate.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<List<string?>>();
        do
        {
            while (await reader.ReadAsync())
            {
                var row = new List<string?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                }

                rows.Add(row);
            }
        }
        while (await reader.NextResultAsync());
        return rows;
    }

    [Fact]
    public async Task A_filter_narrows_every_read_to_the_rows_it_keeps_while_a_removal_reads_and_marks_the_whole_scope()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Filtered",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Filtered] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [status] nvarchar(20) NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        var child = await CreateTableAsync(
            estate,
            "FilteredChild",
            "[item_key] nvarchar(50) NOT NULL, [line] int NOT NULL, [UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL",
            string.Empty);

        // A live row, a row marked deleted and a draft; the live row has a child row of each kind too.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [status], [UpdatedDate_DW], [DeletedDate_DW])
            VALUES (N'live', N'active', @loaded, NULL), (N'marked', N'active', @loaded, @changed), (N'draft', N'draft', @loaded, NULL);
            INSERT INTO {child} ([item_key], [line], [UpdatedDate_DW], [DeletedDate_DW])
            VALUES (N'live', 1, @loaded, NULL), (N'live', 2, @loaded, NULL);
            """);
        var item = ItemFlow(estate, "Filtered");
        var flow = item with
        {
            Source = item.Source with
            {
                Record = item.Source.Record with { Filter = "AND DeletedDate_DW IS NULL AND [status] <> 'draft'" },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal)
                {
                    ["lines"] = new()
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[FilteredChild]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                        OrderBy = ["line"],
                        Filter = "AND [line] <> 2",
                    },
                },
            },
        };
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);

        // Every read takes the rows the filter keeps, and the child rows its own filter keeps: a whole read, and a read by key,
        // which finds the rows the filter leaves out in the table, outside the run's scope.
        Assert.Equal(["live"], await ReadKeysAsync(source, SourceSelection.Full()));
        var named = await source.OpenAsync(SourceSelection.ForKeys([KeyTuple.Of("live"), KeyTuple.Of("marked"), KeyTuple.Of("draft")]), null);
        Assert.Equal(["draft", "marked"], named.OutOfScopeKeys.Select(k => k.Values[0]).Order(StringComparer.Ordinal));
        await foreach (var record in source.ReadAsync(named, null))
        {
            Assert.Equal("live", record.Row.GetString("item_key"));
            Assert.Equal([1L], record.Scopes["lines"].Select(r => Convert.ToInt64(r.Get("line"), CultureInfo.InvariantCulture)));
        }

        // The first records of a scope are the ones the filter keeps; a removal reads every row of it, each with its mark.
        Assert.Equal(["live"], await KeysAsync(source, SourceKeyRead.Read));
        var whole = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        await foreach (var key in source.ReadKeysAsync(10, SourceKeyRead.Whole))
        {
            whole[key.Key.Values[0]] = key.DeletedUtc;
        }

        Assert.Equal(["draft", "live", "marked"], whole.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((null, Changed), (whole["live"], whole["marked"]));

        // Marking writes only the rows not marked yet, and the filter then leaves them out.
        Assert.Equal(1L, await source.MarkDeletedAsync([KeyTuple.Of("live"), KeyTuple.Of("marked")], deleted: true));
        Assert.Empty(await ReadKeysAsync(source, SourceSelection.Full()));

        // Taking the mark off writes only the marked rows, and stamps their update column, so an incremental read meets them.
        var before = DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal(2L, await source.MarkDeletedAsync([KeyTuple.Of("live"), KeyTuple.Of("marked"), KeyTuple.Of("draft")], deleted: false));
        Assert.Equal(["live", "marked"], await ReadKeysAsync(source, SourceSelection.Incremental(Changed)));
        Assert.All(await KeysWithMomentsAsync(source), k => Assert.True(k.Key.Values[0] == "draft" || k.UpdatedUtc > before));

        // A table without the soft-delete column has no row to mark.
        _ = await CreateTableAsync(
            estate,
            "Unmarkable",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Unmarkable] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");
        var plain = estate.Engine.Sources.Open(ItemFlow(estate, "Unmarkable"), NoValues, NullLoggerFactory.Instance);
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => plain.MarkDeletedAsync([KeyTuple.Of("x")], deleted: true));
        Assert.Contains("carries no soft-delete column", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ReadKeysAsync(IIngestionSource source, SourceSelection selection)
    {
        var header = await source.OpenAsync(selection, null);
        var keys = new List<string>();
        await foreach (var record in source.ReadAsync(header, null))
        {
            keys.Add(record.Row.GetString("item_key")!);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static async Task<List<string>> KeysAsync(IIngestionSource source, SourceKeyRead read)
    {
        var keys = new List<string>();
        await foreach (var key in source.ReadKeysAsync(10, read))
        {
            keys.Add(key.Key.Values[0]);
        }

        return keys;
    }

    private static async Task<List<IngestionKey>> KeysWithMomentsAsync(IIngestionSource source)
    {
        var keys = new List<IngestionKey>();
        await foreach (var key in source.ReadKeysAsync(10, SourceKeyRead.Whole))
        {
            keys.Add(key);
        }

        return keys;
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
