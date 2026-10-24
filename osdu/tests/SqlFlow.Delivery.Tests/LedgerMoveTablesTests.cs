using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The tables a ledger move reaches (<see cref="LedgerMoves"/>, osdu/docs/reference/concepts/renaming-a-pipeline.md),
/// held to the module's model: every table that carries a ledger identity is one a move takes (a delivery ledger's, or one of
/// another kind's), one whose rows it points at the new identity, or one it leaves alone on purpose, so a table added later
/// is not left behind under the old identity.
/// </summary>
public sealed class LedgerMoveTablesTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    private static readonly string[] IdentityColumns = ["FlowId", "LedgerFlowId", "CursorFlowId", "LedgerId"];

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
        var delivery = LedgerMoves.KeyedTables.Concat(LedgerMoves.NamingTables).Concat(LedgerMoves.UntouchedTables).ToList();
        var listed = delivery
            .Concat(LedgerMoves.OtherKinds.Values.SelectMany(k => k.Tables))
            .Append(LedgerMoves.ViewTable)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // A table is in one of a delivery ledger's lists at most; the activity trail is every kind's, so a kind lists it too.
        Assert.Equal(delivery.Count, delivery.Distinct(StringComparer.Ordinal).Count());
        Assert.All(LedgerMoves.OtherKinds.Values, k => Assert.Equal(k.Tables.Count, k.Tables.Distinct(StringComparer.Ordinal).Count()));
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

    [Fact]
    public void A_table_of_another_kind_s_ledger_keeps_its_rows_under_the_partition_and_the_identity_a_move_reads_them_by()
    {
        var entities = Entities().ToDictionary(e => e.GetTableName()!, StringComparer.Ordinal);
        foreach (var kind in LedgerMoves.OtherKinds.Values)
        {
            foreach (var table in kind.Tables)
            {
                Assert.Contains(entities[table].GetProperties(), p => p.Name == "PartitionId");
                Assert.Contains(entities[table].GetProperties(), p => p.Name == "FlowId");
            }

            // A kind of named rows holds one of each name per ledger: what a move checks before it begins.
            if (kind.NamedRows)
            {
                Assert.Contains(entities[kind.Tables[0]].GetProperties(), p => p.Name == "Name");
                Assert.Contains(entities[kind.Tables[0]].GetProperties(), p => p.Name == "FlowName");
            }
        }

        Assert.Contains(entities[LedgerMoves.ViewTable].GetProperties(), p => p.Name == "LedgerId");
        Assert.Contains(entities[LedgerMoves.ViewTable].GetProperties(), p => p.Name == "FlowName");
    }
}
