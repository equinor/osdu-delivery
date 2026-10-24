using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The tables a ledger move reaches (<see cref="LedgerMoves"/>, osdu/docs/reference/concepts/renaming-a-pipeline.md),
/// held to the module's model: every table that carries a ledger identity is one a move takes, one whose rows it points at the
/// new identity, or one it leaves alone on purpose, so a table added later is not left behind under the old identity.
/// </summary>
public sealed class LedgerMoveTablesTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    private static readonly string[] IdentityColumns = ["FlowId", "LedgerFlowId", "CursorFlowId"];

    private static IReadOnlyList<IEntityType> Entities()
    {
        using var context = new OsduDbContext(OsduDbContext.SqlServerOptions(Placeholder));
        return context.GetService<IDesignTimeModel>().Model.GetEntityTypes().ToList();
    }

    [Fact]
    public void Every_table_that_carries_a_ledger_identity_is_one_a_move_takes_points_or_leaves_alone()
    {
        var carrying = Entities()
            .Where(e => e.GetProperties().Any(p => IdentityColumns.Contains(p.Name, StringComparer.Ordinal)))
            .Select(e => e.GetTableName()!)
            .Order(StringComparer.Ordinal)
            .ToList();
        var listed = LedgerMoves.KeyedTables.Concat(LedgerMoves.NamingTables).Concat(LedgerMoves.UntouchedTables).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.True(
            listed.SequenceEqual(carrying, StringComparer.Ordinal),
            $"Tables that carry a ledger identity and no list of LedgerMoves names: {string.Join(", ", carrying.Except(listed, StringComparer.Ordinal))}. "
            + $"Listed and not in the model: {string.Join(", ", listed.Except(carrying, StringComparer.Ordinal))}.");
    }

    [Fact]
    public void A_table_a_move_takes_keeps_a_ledger_s_rows_in_one_range_of_an_index_led_by_the_partition_and_the_identity()
    {
        var entities = Entities().ToDictionary(e => e.GetTableName()!, StringComparer.Ordinal);
        foreach (var table in LedgerMoves.KeyedTables)
        {
            var entity = entities[table];
            var keys = entity.GetIndexes().Select(i => i.Properties).Append(entity.FindPrimaryKey()!.Properties);
            Assert.True(
                keys.Any(k => k.Count >= 2 && k[0].Name == "PartitionId" && k[1].Name == "FlowId"),
                $"osdu.{table} has no index led by PartitionId and FlowId, so a move would read the whole table for every slice.");
        }

        foreach (var table in LedgerMoves.NamingTables)
        {
            Assert.Contains(entities[table].GetProperties(), p => p.Name == "LedgerFlowId");
            Assert.Contains(entities[table].GetProperties(), p => p.Name == "PartitionId");
        }
    }
}
