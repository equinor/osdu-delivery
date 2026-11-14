using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The tables an orphaned ledger's deletion empties (<see cref="LedgerDeletes"/>,
/// osdu/docs/reference/concepts/renaming-a-pipeline.md), held to the module's model: every table whose rows name a row a
/// deletion takes is one it takes too, so a table added later is not left holding rows of an inventory, a download or a run
/// that is gone; every table is found through an index led by the partition and the column its rows are found by; and a
/// kind's first table, the one that says whether its ledger holds anything, is the last a deletion empties.
/// </summary>
public sealed class LedgerDeleteTablesTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    private static IReadOnlyDictionary<string, IEntityType> Entities()
    {
        using var context = new OsduDbContext(OsduDbContext.SqlServerOptions(Placeholder));
        return context.GetService<IDesignTimeModel>().Model.GetEntityTypes().ToDictionary(e => e.GetTableName()!, StringComparer.Ordinal);
    }

    public static TheoryData<string> Kinds() => [.. LedgerDeletes.Trees.Keys];

    [Fact]
    public void Every_kind_but_delivery_and_dimension_has_its_tables_and_they_end_with_the_one_that_says_what_its_ledger_holds()
    {
        // A delivery ledger is deleted as a flow's own is, and a dimension ledger a dimension at a time: neither lists tables.
        var listed = LedgerDeletes.Trees.Keys.Order(StringComparer.Ordinal).ToList();
        var expected = LedgerMoves.OtherKinds.Keys.Where(k => k != LedgerKinds.Dimension).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, listed);
        foreach (var (kind, tree) in LedgerDeletes.Trees)
        {
            // The first table of a move is the one a listing counts: while a deletion has not reached it, the ledger is listed.
            Assert.Equal(LedgerMoves.OtherKinds[kind].Tables[0], tree[^1].Table);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_table_whose_rows_name_a_row_a_deletion_takes_is_one_it_takes_too(string kind)
    {
        var entities = Entities();
        var tables = LedgerDeletes.Flatten(LedgerDeletes.Trees[kind]).ToList();
        var listed = tables.Select(t => t.Table).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(tables.Count, listed.Count);
        Assert.All(tables, t => Assert.True(entities.ContainsKey(t.Table), $"osdu.{t.Table} is not in the model."));

        foreach (var parent in tables.Where(t => t.Key is not null))
        {
            var naming = entities.Values
                .Where(e => e.GetTableName() != parent.Table && e.GetProperties().Any(p => p.Name == parent.Key))
                .Select(e => e.GetTableName()!)
                .ToList();
            Assert.True(
                naming.All(listed.Contains),
                $"Tables whose rows name osdu.{parent.Table} by {parent.Key} and that no deletion of a {kind} ledger takes: {string.Join(", ", naming.Where(n => !listed.Contains(n)))}.");

            // Each child is found by the parent's key, which it carries under the same name.
            Assert.All(parent.Children, child => Assert.Contains(entities[child.Table].GetProperties(), p => p.Name == parent.Key));
            var keyType = entities[parent.Table].FindProperty(parent.Key!)!.ClrType;
            Assert.Equal(parent.WideKey ? typeof(long) : typeof(int), keyType);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_table_is_found_through_an_index_led_by_the_partition_and_the_column_its_rows_are_found_by(string kind)
    {
        var entities = Entities();
        Check(LedgerDeletes.Trees[kind], "FlowId");

        void Check(IReadOnlyList<LedgerDeleteTable> tables, string column)
        {
            foreach (var table in tables)
            {
                var entity = entities[table.Table];
                var keys = entity.GetIndexes().Select(i => i.Properties).Append(entity.FindPrimaryKey()!.Properties);
                Assert.True(
                    keys.Any(k => k.Count >= 2 && k[0].Name == "PartitionId" && k[1].Name == column),
                    $"osdu.{table.Table} has no index led by PartitionId and {column}, so a deletion would read the whole table for every slice.");
                if (table.Key is { } key)
                {
                    Check(table.Children, key);
                }

                // A paged table's rows go with the rows that name them in one statement, so those name nothing further.
                if (table.Paged)
                {
                    Assert.NotNull(table.Key);
                    Assert.All(table.Children, child => Assert.Empty(child.Children));
                }
            }
        }
    }
}
