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
public sealed partial class DimensionStore
{
    /// <summary>
    /// Writes a build run's views (osdu/docs/reference/flow/dimension-views.md): each view its flow declares, in one
    /// transaction under the view's lock, written where its statement changed and checked in the run's partition, and each
    /// view the flow made before and no longer declares, dropped. A view that cannot be written keeps its last definition
    /// and says why; the others are still written.
    /// </summary>
    public async Task<DimensionViewsWritten> WriteViewsAsync(DimensionViewWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var partition = await WritePartitionAsync(write.FlowLedgerId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerDimensionViewStore.WriteAsync(
            db, write, partition, (connection, target, opening) => OpenTableDatabaseAsync(db, connection, target, write.Flow, write.Secrets, opening), ct).ConfigureAwait(false);
    }

    /// <summary>The views builds made, of one flow when <paramref name="flowName"/> names one, by name.</summary>
    public async Task<IReadOnlyList<DimensionViewState>> ListViewsAsync(string? flowName, CancellationToken ct = default)
    {
        await using var db = Open();
        return await SqlServerDimensionViewStore.ListAsync(db, string.IsNullOrWhiteSpace(flowName) ? null : flowName.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>The view named <paramref name="name"/>, ignoring case, with its newest checks; null when no build made one.</summary>
    public async Task<DimensionViewDetail?> GetViewAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        return await SqlServerDimensionViewStore.GetAsync(db, name.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>The views builds made that read the dimension table <paramref name="table"/>, by name: what refuses the removal of its dimension.</summary>
    public async Task<IReadOnlyList<string>> ViewsReadingAsync(string table, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        await using var db = Open();
        return await SqlServerDimensionViewStore.ReadingAsync(db, table.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the view named <paramref name="name"/> for good: the view from the database when it is there, and its record
    /// with its checks. Null when no build made one. Whether it may go (no flow declares it) is the caller's to settle
    /// (<see cref="Engine.Dimensions.DimensionViewRemoval"/>).
    /// </summary>
    public async Task<DimensionViewRemoved?> RemoveViewAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        return await SqlServerDimensionViewStore.RemoveAsync(
            db, name.Trim(), (connection, target, opening) => OpenTableDatabaseAsync(db, connection, target, $"view {name.Trim()}", null, opening), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What a build of flow <paramref name="flowName"/> would do with each of <paramref name="views"/>, writing nothing: what a
    /// plan says of them, looked at where <paramref name="target"/> keeps them (the module's database when null), its
    /// reference resolved through <paramref name="secrets"/> (the run's; the host's when null).
    /// </summary>
    public async Task<IReadOnlyList<DimensionViewProbe>> ProbeViewsAsync(
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

    /// <summary>
    /// Where a dimension flow keeps its tables and views (osdu/docs/reference/flow/dimension-target.md): the module's
    /// database when <paramref name="target"/> is null or reaches it, else the target, once this host has opened it, its
    /// reference resolved through <paramref name="secrets"/> (the run's; the host's when null).
    /// </summary>
    /// <exception cref="DeliveryException">The target cannot be resolved or opened, or names the module's database with another schema.</exception>
    public async Task<DimensionTableTarget> ResolveTargetAsync(
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
