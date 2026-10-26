using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A build of one dimension never waits for a build of another (osdu/docs/reference/flow/dimension-build.md, What a build
/// does): the tables holding a dimension's rows as one range of their clustered key lock a row at a time, never a page,
/// where page locks on the page two ranges share deadlocked one build with the other, and every statement of a build seeks
/// its own rows. Every index of every such table, the ones a later migration adds included, takes no page lock, and a
/// build runs to the end while another dimension's rows are held by a write that has not committed.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionRowLockTests : IDisposable
{
    private const string Yaml = """
        flowType: dimension
        name: lock-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 1000
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        dimensions:
          - name: Wells
            kind: "osdu:wks:master-data--Well:*"
            path: id
            label: data.FacilityName
            columns: { key: WellID, value: WellName }
            attributes:
              Operator: data.Operator
          - name: Wellbores
            kind: "osdu:wks:master-data--Wellbore:*"
            path: id
            label: data.FacilityName
            columns: { key: WellboreID, value: WellboreName }
            attributes:
              Operator: data.Operator
        """;

    /// <summary>The records of each kind.</summary>
    private const int Records = 20_000;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 1000, IndexIds = true };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private DimensionRunner Runner(OsduLedger ledger) => new(
        Samples.Engine(ledger, _clock), new DeliveryDocumentLoader().ParseDimension(Yaml, "flows/dims.yaml").ForPartition("dev"),
        new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true);

    [Fact]
    public async Task A_build_runs_to_the_end_while_another_dimensions_rows_are_held_by_a_write_not_yet_committed()
    {
        // Enough rows that a statement over one dimension's range reads it a page at a time where pages may be locked.
        var wells = new List<JsonObject>(Records);
        for (var i = 0; i < Records; i++)
        {
            wells.Add(_platform.Add($"dev:master-data--Well:W{i:D5}", "osdu:wks:master-data--Well:1.0.0", new JsonObject { ["FacilityName"] = $"WELL {i}", ["Operator"] = $"OP {i % 7}" }));
            _platform.Add($"dev:master-data--Wellbore:B{i:D5}", "osdu:wks:master-data--Wellbore:1.0.0", new JsonObject { ["FacilityName"] = $"BORE {i}", ["Operator"] = $"OP {i % 5}" });
        }

        var ledger = _db.Ledger(_clock);
        await Runner(ledger).BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.ListDimensionsAsync("dev", null)).Single(d => d.Name == "Wellbores");

        // A write of the wellbores holds the rows of theirs it writes, as a build's write does until it commits: its dimension's
        // row, and in each table the first rows of the wellbores' range, which share a page with the last rows of the wells'.
        // Each statement writes fewer rows than make SQL Server take the table whole instead.
        await using var holding = new SqlConnection(_db.ConnectionString);
        await holding.OpenAsync();
        await using var tx = (SqlTransaction)await holding.BeginTransactionAsync();
        await using (var hold = new SqlCommand("""
            UPDATE [osdu].[Dimension] SET [TableVersion] = [TableVersion] WHERE [DimensionId] = @d;
            WITH h AS (SELECT TOP (4000) * FROM [osdu].[DimensionMember] WHERE [PartitionId] = @p AND [DimensionId] = @d ORDER BY [MemberId])
            UPDATE h SET [Records] = [Records];
            WITH h AS (SELECT TOP (4000) * FROM [osdu].[DimensionValue] WHERE [PartitionId] = @p AND [DimensionId] = @d ORDER BY [ValueId])
            UPDATE h SET [Count] = [Count];
            WITH h AS (SELECT TOP (4000) * FROM [osdu].[DimensionAttribute] WHERE [PartitionId] = @p AND [DimensionId] = @d ORDER BY [AttributeValueId])
            UPDATE h SET [Value] = [Value];
            WITH h AS (SELECT TOP (4000) * FROM [osdu].[DimensionKeyRecord] WHERE [PartitionId] = @p AND [DimensionId] = @d ORDER BY [KeyRecordId])
            UPDATE h SET [EntityType] = [EntityType];
            """, holding, tx))
        {
            hold.Parameters.AddWithValue("@d", wellbores.DimensionId);
            hold.Parameters.AddWithValue("@p", await PartitionIdAsync(wellbores.DimensionId));
            await hold.ExecuteNonQueryAsync();
        }

        // The wells change and are built again, in full, beside it.
        for (var i = 0; i < Records; i += 3)
        {
            wells[i]["data"]!["Operator"] = $"NEW OP {i}";
        }

        var build = Runner(ledger).BuildAsync(["Wells"], new DimensionLoadRequest(true, null, null), Guid.NewGuid(), "tests", CancellationToken.None);
        var finished = await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(60)));
        await tx.RollbackAsync();

        Assert.True(finished == build, "The build of the wells waited for the write holding the wellbores' rows.");
        var outcome = await build;
        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
    }

    private async Task<short> PartitionIdAsync(int dimensionId)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT [PartitionId] FROM [osdu].[Dimension] WHERE [DimensionId] = @d;", connection);
        command.Parameters.AddWithValue("@d", dimensionId);
        return (short)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Every_index_of_a_table_holding_a_dimensions_rows_as_one_range_takes_no_page_lock()
    {
        // The tables whose clustered key is a dimension's range: partition, dimension, then the row's own id.
        await using var context = _db.CreateDbContext();
        var ranged = context.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.FindPrimaryKey() is { } key && key.Properties.Count > 2
                && key.Properties[0].Name == "PartitionId" && key.Properties[1].Name == "DimensionId")
            .Select(e => e.GetTableName()!)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ["DimensionAttribute", "DimensionAttributeName", "DimensionChange", "DimensionCollectedText", "DimensionElement",
                "DimensionKeyRecord", "DimensionMember", "DimensionRecord", "DimensionRun", "DimensionValue"],
            ranged);

        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT t.[name], i.[name], i.[allow_page_locks]
            FROM sys.indexes AS i
            JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
            WHERE t.[schema_id] = SCHEMA_ID(N'osdu') AND i.[index_id] > 0;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<(string Table, string Index, bool PageLocks)>();
        while (await reader.ReadAsync())
        {
            indexes.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        var ofRanged = indexes.Where(i => ranged.Contains(i.Table, StringComparer.Ordinal)).ToList();
        Assert.Equal(ranged, ofRanged.Select(i => i.Table).Distinct().Order(StringComparer.Ordinal));
        Assert.True(
            ofRanged.All(i => !i.PageLocks),
            "Indexes of a dimension's range that take page locks: " + string.Join(", ", ofRanged.Where(i => i.PageLocks).Select(i => $"{i.Table}.{i.Index}")));
    }
}
