using Microsoft.Data.SqlClient;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Where a dimension's table and its flow's views are kept (osdu/docs/reference/flow/dimension-target.md): the module's
/// database, in its schema, unless the flow's <c>target</c> names another connection, another database on a server, or
/// another schema. The dimension itself (its keys, values, attributes and change log) is always in the module's database;
/// only its table and the views over it go to the target, written by a build after the dimension's merge commits.
/// </summary>
/// <param name="Connection">The flow's <c>target.connection</c> as written, a reference; null for the module's own connection.</param>
/// <param name="Database">The database on the connection's server; null for the connection's own.</param>
/// <param name="Schema">The schema the table and views are in.</param>
public sealed record DimensionTableTarget(string? Connection, string? Database, string Schema)
{
    /// <summary>The module's database and schema: where a flow naming no target keeps its tables and views.</summary>
    public static DimensionTableTarget Catalog { get; } = new(null, null, DeliveryModel.SchemaName);

    /// <summary>Whether the table is kept in the module's own database, on the ledger's connection.</summary>
    public bool InCatalog => Connection is null && Database is null;

    /// <summary>Whether <paramref name="other"/> is the same place, compared as SQL Server compares names: ignoring case.</summary>
    public bool Equals(DimensionTableTarget? other)
        => other is not null
            && string.Equals(Connection, other.Connection, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Database, other.Database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        Connection is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Connection),
        Database is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Database),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Schema));

    /// <summary>The target a flow declares, before a build has checked which database it reaches.</summary>
    public static DimensionTableTarget Of(DimensionTarget? declared)
        => declared is null ? Catalog : new(declared.Connection, declared.Database, declared.Schema ?? DeliveryModel.SchemaName);

    /// <summary>The target the ledger recorded for a dimension's table: the module's database where it recorded none.</summary>
    public static DimensionTableTarget Of(string? connection, string? database, string? schema)
        => new(connection, database, schema ?? DeliveryModel.SchemaName);

    /// <summary>A table's or view's name with its schema, as a statement names it: <c>[osdu].[dim_...]</c>.</summary>
    public string Qualified(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{DimensionTables.Quoted(Schema)}.{DimensionTables.Quoted(name)}";
    }

    /// <summary>Where the tables and views are kept, as a person types it before a name: <c>osdu</c>, or <c>Dimensions.dim</c> when the target names a database.</summary>
    public string Place => Database is null ? Schema : $"{Database}.{Schema}";

    /// <summary>A table's or view's name as a person types it: <c>osdu.dim_...</c>, the database too when the target names one.</summary>
    public string Shown(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{Place}.{name}";
    }

    /// <summary>The schema as a value a statement compares or builds a name from (<c>N'osdu'</c>).</summary>
    internal string SchemaLiteral => "N'" + Schema.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The connection string the target is opened by: the module's (<paramref name="catalog"/>) when the target names no
    /// connection, else its reference resolved through <paramref name="secrets"/>; the database the target names in place of
    /// the connection's own.
    /// </summary>
    internal async Task<string> ConnectionStringAsync(string catalog, ISecretResolver? secrets, string flowName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        string resolved;
        if (Connection is null)
        {
            resolved = catalog;
        }
        else
        {
            if (secrets is null)
            {
                throw new DeliveryException(
                    $"Flow '{flowName}' keeps its dimension tables on target.connection {Named}, and this host resolves no connection references, so it cannot reach them.");
            }

            try
            {
                resolved = await secrets.ResolveAsync(Connection, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlow.Core.SqlFlowException or InvalidOperationException or ArgumentException)
            {
                throw new DeliveryException(
                    $"Flow '{flowName}': target.connection {Named} could not be resolved on this host: {SecretHygiene.RedactedMessage(ex.Message)}", ex);
            }
        }

        var builder = new SqlConnectionStringBuilder(IngestionConnection.Canonicalize(resolved, flowName));
        if (Database is not null)
        {
            builder.InitialCatalog = Database;
        }

        return builder.ConnectionString;
    }

    /// <summary>The target's connection as a message names it: the reference, never what it resolves to.</summary>
    internal string Named => Connection is null
        ? "(the module's own)"
        : Connection.StartsWith("${", StringComparison.Ordinal) ? Connection : "(a connection string)";
}

/// <summary>
/// A dimension's table database, open: the ledger's own connection when the table is kept in the module's database, so a
/// statement on it joins the ledger's transaction, else a connection of its own to the target, which this disposes.
/// </summary>
internal sealed class DimensionTableDatabase : IAsyncDisposable
{
    private readonly bool _owned;

    private DimensionTableDatabase(SqlConnection connection, DimensionTableTarget target, bool owned)
    {
        Connection = connection;
        Target = target;
        _owned = owned;
    }

    /// <summary>The connection the table and views are read and written on.</summary>
    public SqlConnection Connection { get; }

    /// <summary>Where the table and views are kept.</summary>
    public DimensionTableTarget Target { get; }

    /// <summary>Whether the table is in the module's database, on the ledger's connection.</summary>
    public bool InCatalog => Target.InCatalog;

    /// <summary>The ledger's own connection, for a table kept in the module's database.</summary>
    public static DimensionTableDatabase OfCatalog(SqlConnection catalog, DimensionTableTarget target)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.InCatalog)
        {
            throw new ArgumentException("A target that names another connection or database is opened on a connection of its own.", nameof(target));
        }

        return new DimensionTableDatabase(catalog, target, owned: false);
    }

    /// <summary>A connection of its own to <paramref name="target"/>, opened from <paramref name="connectionString"/>.</summary>
    public static async Task<DimensionTableDatabase> OpenAsync(string connectionString, DimensionTableTarget target, string flowName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(target);
        var connection = await IngestionConnection.OpenResolvedAsync(connectionString, flowName, "dimension table", ct).ConfigureAwait(false);
        return new DimensionTableDatabase(connection, target, owned: true);
    }

    /// <summary>The transaction a statement on the table joins: the ledger's for a table in the module's database, none for another.</summary>
    public SqlTransaction? Joining(SqlTransaction? ledger) => InCatalog ? ledger : null;

    public ValueTask DisposeAsync() => _owned ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
