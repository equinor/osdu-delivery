using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Data;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Ledger;

/// <summary>How a run-time table came out of being brought to its columns: the plan, whose drift names what the table holds beyond them, and whether anything changed.</summary>
internal sealed record RunTimeTableChange(EvolutionPlan Plan, bool Changed);

/// <summary>
/// The tables of the <c>osdu</c> schema the module makes while it runs rather than through a migration: a dimension's table
/// (osdu/docs/reference/flow/dimension.md, The dimension's table) and a duplicate check's
/// (osdu/docs/reference/flow/inventory-duplicates.md, The check's table). Each is brought to the columns it is to have through
/// SQLFlow's schema evolution, which makes a table that is missing and adds a column it lacks, and never drops or narrows one.
/// </summary>
internal static class RunTimeTables
{
    /// <summary>
    /// Brings <paramref name="table"/> in the <c>osdu</c> schema to <paramref name="desired"/>, keyed by <paramref name="keyColumns"/>,
    /// on an open connection outside any transaction.
    /// </summary>
    public static async Task<RunTimeTableChange> EvolveAsync(
        SqlConnection connection, string table, IReadOnlyList<SqlColumn> desired, IReadOnlySet<string> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keyColumns);
        var target = new RelationalObject { Database = connection.Database, Schema = DeliveryModel.SchemaName, Name = table };
        var plan = await new SchemaSyncService(new SqlServerCatalogReader()).PlanAsync(connection, target, desired, keyColumns, ct).ConfigureAwait(false);
        var batch = EvolutionDdlGenerator.Generate(target, plan, allowTableRewrite: false);
        await SqlServerSchemaProvider.ApplyDdlAsync(connection, batch, new SchemaApplyOptions(), ct).ConfigureAwait(false);
        return new RunTimeTableChange(plan, batch.HasChanges);
    }

    /// <summary>A type with no length, such as <c>bigint</c>.</summary>
    public static SqlDataType Type(string name) => new() { BaseType = name };

    /// <summary>Text of at most <paramref name="length"/> characters; -1 for <c>nvarchar(max)</c>.</summary>
    public static SqlDataType Text(int length) => new() { BaseType = "nvarchar", Length = length };

    /// <summary>Exactly <paramref name="length"/> bytes.</summary>
    public static SqlDataType Binary(int length) => new() { BaseType = "binary", Length = length };

    /// <summary>A table's name with its schema, as a statement names it: <c>[osdu].[...]</c>.</summary>
    public static string Qualified(string name) => $"[{DeliveryModel.SchemaName}].{Quoted(name)}";

    /// <summary>An identifier in brackets, a closing bracket inside it doubled.</summary>
    public static string Quoted(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
