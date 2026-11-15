using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using Xunit;
using Xunit.Abstractions;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The migration that keeps each cached value a render read once (<c>CacheSetValues</c>), over a render context of the
/// shape a delivering estate has: many sets of a few hundred values each, most of them holding the same values. It checks
/// that every row is carried over, row for row, and says how long the migration took and what the tables hold before and
/// after. Run with <c>SQLFLOW_CACHE_SET_SCALE_SETS</c> and <c>SQLFLOW_CACHE_SET_SCALE_VALUES</c> set to
/// measure a size of your own (12500 and 320 is four million values); unset, it carries over 24,000.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheSetValuesScaleTests
{
    private const string SetsVariable = "SQLFLOW_CACHE_SET_SCALE_SETS";

    private const string ValuesVariable = "SQLFLOW_CACHE_SET_SCALE_VALUES";

    private const string BeforeKeys = "20261115141705_DimensionTextKeys";

    /// <summary>The cached records every set reads from: the reference data an estate's records share.</summary>
    private const int Pool = 400;

    // Every set reads @values places: a cached record of one of six types, at one of four paths, and never a place
    // twice. Seven in ten are of the records every set reads from (units, curve types), as an estate's records share
    // their reference data; the rest are of records four sets share, as the logs of one wellbore do. A place holds one
    // text, but one record in ten changed between the first half of the sets and the second, as a cached value that was
    // corrected reads to the sets built before and after. Each value comes with its hash, as the ledger wrote it.
    private const string FillSql = """
        SET IDENTITY_INSERT [osdu].[CacheSet] ON;
        INSERT INTO [osdu].[CacheSet] ([SetId], [SetHash], [EntryCount], [Gated], [FirstSeenUtc], [LastSeenUtc])
        SELECT n.[i] + 1, CONCAT(N'set-', n.[i] + 1), @values, 0, SYSUTCDATETIME(), SYSUTCDATETIME()
        FROM (SELECT TOP (@sets) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS [i]
              FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n;
        SET IDENTITY_INSERT [osdu].[CacheSet] OFF;

        INSERT INTO [osdu].[CacheSetEntry] ([SetId], [Scope], [TypeName], [ItemId], [Path], [Kind], [ValueHash], [ValueText])
        SELECT v.[SetId], N'dev', v.[TypeName], v.[ItemId], v.[Path], v.[Kind],
               LOWER(CONVERT(char(64), HASHBYTES('SHA2_256', v.[ValueText]), 2)), v.[ValueText]
        FROM (
            SELECT x.[s] + 1 AS [SetId], t.[TypeName], i.[ItemId], p.[Path],
                   CASE x.[v] % 4 WHEN 0 THEN N'match' ELSE N'value' END AS [Kind],
                   CAST(CONCAT(N'what ', p.[Path], N' of ', i.[ItemId], N' held',
                       CASE WHEN r.[Item] % 10 = 0 AND x.[s] * 2 >= @sets THEN N', as it reads now' ELSE N'' END) AS nvarchar(400)) AS [ValueText]
            FROM (SELECT n.[i] / @values AS [s], n.[i] % @values AS [v]
                  FROM (SELECT TOP (CAST(@sets AS bigint) * @values) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS [i]
                        FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c) AS n) AS x
            CROSS APPLY (SELECT CASE WHEN x.[v] * 10 < @values * 7 THEN (x.[v] / 4 + x.[s] % 5) % @pool
                                     ELSE @pool + (x.[s] / 4) * @values + x.[v] / 4 END AS [Item]) AS r
            CROSS APPLY (SELECT CHOOSE(1 + x.[v] % 6, N'UnitOfMeasure', N'CurveType', N'Wellbore', N'Organisation', N'LogCurveFamily', N'AliasNameType') AS [TypeName]) AS t
            CROSS APPLY (SELECT CAST(CONCAT(N'dev:reference-data--', t.[TypeName], N':', r.[Item], N':') AS nvarchar(512)) COLLATE Latin1_General_100_BIN2 AS [ItemId]) AS i
            CROSS APPLY (SELECT CHOOSE(1 + x.[v] % 4, N'id', N'Name', N'Code', N'NameAlias.AliasName') AS [Path]) AS p) AS v;
        """;

    // The whole render context as one number and a count, whichever shape it is kept in: every value with what it was
    // read from, its hash as lower-case hex.
    private const string FingerprintBeforeSql = """
        SELECT COUNT_BIG(*), ISNULL(SUM(CAST(CHECKSUM(e.[SetId], e.[Scope], e.[TypeName], e.[ItemId], e.[Path], e.[Kind], e.[ValueHash], e.[ValueText]) AS bigint)), 0)
        FROM [osdu].[CacheSetEntry] AS e;
        """;

    private const string FingerprintAfterSql = """
        SELECT COUNT_BIG(*), ISNULL(SUM(CAST(CHECKSUM(e.[SetId], v.[Scope], v.[TypeName], v.[ItemId], v.[Path], v.[Kind],
            CAST(LOWER(CONVERT(char(64), v.[ValueHash], 2)) AS nvarchar(64)), v.[ValueText]) AS bigint)), 0)
        FROM [osdu].[CacheSetEntry] AS e
        INNER JOIN [osdu].[CacheSetValue] AS v ON v.[ValueId] = e.[ValueId];
        """;

    private const string SizeSql = """
        SELECT t.[name], SUM(CASE WHEN ps.[index_id] IN (0, 1) THEN ps.[row_count] ELSE 0 END), SUM(ps.[used_page_count]) * 8
        FROM sys.dm_db_partition_stats AS ps
        INNER JOIN sys.tables AS t ON t.[object_id] = ps.[object_id]
        WHERE SCHEMA_NAME(t.[schema_id]) = N'osdu' AND t.[name] IN (N'CacheSetEntry', N'CacheSetValue')
        GROUP BY t.[name]
        ORDER BY t.[name];
        """;

    private readonly ITestOutputHelper _output;

    public CacheSetValuesScaleTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    private static int Asked(string variable, int otherwise)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && asked > 0 ? asked : otherwise;

    [Fact]
    public async Task The_render_context_of_many_sets_is_carried_over_row_for_row_into_a_fraction_of_the_space()
    {
        var sets = Asked(SetsVariable, 400);
        var values = Asked(ValuesVariable, 60);
        await using var database = await OsduScratchDatabase.CreateAsync();
        await MigrateAsync(database, BeforeKeys);
        await ExecuteAsync(database, FillSql, ("@sets", sets), ("@values", values), ("@pool", Pool));

        var before = await FingerprintAsync(database, FingerprintBeforeSql);
        var sizeBefore = await SizesAsync(database);
        var distinct = await ScalarAsync(database, "SELECT COUNT_BIG(*) FROM (SELECT DISTINCT [Scope], [TypeName], [ItemId], [Path], [Kind], [ValueHash] FROM [osdu].[CacheSetEntry]) AS d;");
        Assert.Equal((long)sets * values, before.Values);

        var clock = Stopwatch.StartNew();
        await MigrateAsync(database, null);
        clock.Stop();

        // Every row is there with what it read and held, each distinct value kept once.
        var after = await FingerprintAsync(database, FingerprintAfterSql);
        var sizeAfter = await SizesAsync(database);
        Assert.Equal(before, after);
        Assert.Equal(distinct, await ScalarAsync(database, "SELECT COUNT_BIG(*) FROM [osdu].[CacheSetValue];"));

        // A set reads as it did: the ledger's own read of the first, a middle and the last set holds every value.
        var ledger = new OsduLedger(() => new OsduDbContext(OsduDbContext.SqlServerOptions(database.ConnectionString)));
        foreach (var set in new long[] { 1, (sets / 2) + 1, sets })
        {
            Assert.Equal(values, (await ledger.ListCacheSetAsync(set)).Count);
        }

        var kbBefore = sizeBefore.Sum(s => s.Kb);
        var kbAfter = sizeAfter.Sum(s => s.Kb);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{before.Values:N0} rows of {sets:N0} sets holding {distinct:N0} distinct values: carried over in {clock.Elapsed.TotalSeconds:N1} s."));
        foreach (var (name, rows, kb) in sizeBefore)
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"before  osdu.{name}: {rows:N0} rows, {kb / 1024.0:N1} MB ({(rows == 0 ? 0 : kb * 1024.0 / rows):N0} bytes a row, its indexes with it)"));
        }

        foreach (var (name, rows, kb) in sizeAfter)
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"after   osdu.{name}: {rows:N0} rows, {kb / 1024.0:N1} MB ({(rows == 0 ? 0 : kb * 1024.0 / rows):N0} bytes a row, its indexes with it)"));
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"total   {kbBefore / 1024.0:N1} MB before, {kbAfter / 1024.0:N1} MB after: {(kbBefore == 0 ? 0 : 100.0 * kbAfter / kbBefore):N0}% of what it was."));

        // The values kept once and the entries naming them take well under half of what the rows took with their texts.
        Assert.True(kbAfter * 2 < kbBefore, string.Create(CultureInfo.InvariantCulture, $"The render context took {kbBefore} KB before and {kbAfter} KB after."));
    }

    private static async Task MigrateAsync(OsduScratchDatabase database, string? target)
    {
        await using var db = new OsduDbContext(OsduDbContext.SqlServerOptions(database.ConnectionString));
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
        if (target is null)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.GetService<IMigrator>().MigrateAsync(target);
        }
    }

    private static async Task ExecuteAsync(OsduScratchDatabase database, string sql, params (string Name, int Value)[] values)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1800;
        foreach (var (name, value) in values)
        {
            command.Parameters.Add(new SqlParameter(name, System.Data.SqlDbType.Int) { Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(OsduScratchDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1800;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<(long Values, long Sum)> FingerprintAsync(OsduScratchDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1800;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<(string Table, long Rows, long Kb)>> SizesAsync(OsduScratchDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SizeSql;
        var sizes = new List<(string, long, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sizes.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return sizes;
    }
}
