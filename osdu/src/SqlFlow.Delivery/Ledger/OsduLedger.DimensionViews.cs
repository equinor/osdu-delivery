using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The views dimension flows declare over their tables (osdu/docs/reference/flow/dimension-views.md): written,
/// checked, dropped and read through <see cref="SqlServerDimensionViewStore"/>. A view spans the partitions its tables
/// hold, so its record belongs to no ledger; each check is kept in the partition of the build that made it.
/// </summary>
public sealed partial class OsduLedger
{
    public async Task<DimensionViewsWritten> WriteDimensionViewsAsync(DimensionViewWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var partition = await WritePartitionAsync(write.FlowLedgerId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerDimensionViewStore.WriteAsync(
            db, write, partition, (connection, target, opening) => OpenTableDatabaseAsync(db, connection, target, write.Flow, write.Secrets, opening), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionViewState>> ListDimensionViewsAsync(string? flowName, CancellationToken ct = default)
    {
        await using var db = Open();
        return await SqlServerDimensionViewStore.ListAsync(db, string.IsNullOrWhiteSpace(flowName) ? null : flowName.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<DimensionViewDetail?> GetDimensionViewAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        return await SqlServerDimensionViewStore.GetAsync(db, name.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DimensionViewsReadingAsync(string table, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        await using var db = Open();
        return await SqlServerDimensionViewStore.ReadingAsync(db, table.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<DimensionViewRemoved?> RemoveDimensionViewAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        return await SqlServerDimensionViewStore.RemoveAsync(
            db, name.Trim(), (connection, target, opening) => OpenTableDatabaseAsync(db, connection, target, $"view {name.Trim()}", null, opening), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionViewProbe>> ProbeDimensionViewsAsync(
        string flowName, IReadOnlyList<DimensionViewSpec> views, DimensionTableTarget? target = null, SqlFlow.Core.Secrets.ISecretResolver? secrets = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(views);
        if (views.Count == 0)
        {
            return [];
        }

        await using var db = Open();
        return await SqlServerDimensionViewStore.ProbeAsync(
            db, flowName, views, target ?? DimensionTableTarget.Catalog,
            (connection, kept, opening) => OpenTableDatabaseAsync(db, connection, kept, flowName, secrets, opening), ct).ConfigureAwait(false);
    }

    public async Task<DimensionTableTarget> ResolveDimensionTargetAsync(
        string flowName, DimensionTarget? target, SqlFlow.Core.Secrets.ISecretResolver? secrets = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        var declared = DimensionTableTarget.Of(target);
        if (declared.InCatalog)
        {
            return declared;
        }

        await using var db = Open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var catalog = (SqlConnection)db.Database.GetDbConnection();
            await using var home = await OpenTableDatabaseAsync(db, catalog, declared, flowName, secrets, ct).ConfigureAwait(false);
            var module = await SqlServerDimensionViewStore.IdentityAsync(catalog, ct).ConfigureAwait(false);
            var there = await SqlServerDimensionViewStore.IdentityAsync(home.Connection, ct).ConfigureAwait(false);
            if (!module.SameAs(there))
            {
                return declared;
            }

            if (!string.Equals(declared.Schema, DeliveryModel.SchemaName, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException(
                    $"Flow {flowName}'s target ({declared.Named}) reaches the module's own database, with schema {declared.Schema}; in the module's database a dimension's table is kept in schema {DeliveryModel.SchemaName} only. Leave target.schema out, or name another database.");
            }

            return DimensionTableTarget.Catalog;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
