using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The partition registry in the module's database (<c>osdu.Partition</c>, osdu/docs/reference/concepts/partitions.md,
/// The partition registry): the OSDU partitions a flow that names none serves, and the default one a run that names
/// none runs in. It is read by every run of such a flow, and kept by the control plane's configuration
/// (<see cref="ConfiguredPartitions"/>), the Partitions page, the API and <c>sqlflow partition</c>. At most one
/// partition is the default; the first one registered becomes it, so a registry that holds partitions always has one to
/// run in until someone removes it. A partition the configuration names is the configuration's: it is not described,
/// made or unmade the default, or removed here (<see cref="SetByConfigurationException"/>).
/// </summary>
public sealed class DeliveryPartitionRegistry : IPartitionRegistry
{
    private readonly Func<OsduDbContext>? _contexts;

    /// <param name="contexts">Opens the module's database; null for a host started without it, where every call says so.</param>
    public DeliveryPartitionRegistry(Func<OsduDbContext>? contexts) => _contexts = contexts;

    /// <summary>Whether this host has the module's database, and so a registry to read and keep.</summary>
    public bool Available => _contexts is not null;

    private OsduDbContext Open() => (_contexts ?? throw new DeliveryException(
        $"The partition registry lives in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}"))();

    public async Task<RegisteredPartitions> ReadAsync(CancellationToken ct = default)
    {
        await using var db = Open();
        return await ReadAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>The registry as <paramref name="db"/> reads it: for a caller already working in the module's context, such as the sync.</summary>
    public static async Task<RegisteredPartitions> ReadAsync(OsduDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.DeliveryPartitions.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false);
        return new RegisteredPartitions(rows.Select(r => new RegisteredPartition(r.Name, r.Description, r.IsDefault)));
    }

    /// <summary>Every registered partition, ordered by name.</summary>
    public async Task<IReadOnlyList<DeliveryPartition>> ListAsync(CancellationToken ct = default)
    {
        await using var db = Open();
        return await db.DeliveryPartitions.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers <paramref name="name"/>, described by <paramref name="description"/>, and makes it the default when
    /// <paramref name="makeDefault"/> is set or when no partition is registered yet.
    /// </summary>
    /// <exception cref="FlowValidationException">The name is no partition id, or the description is too long.</exception>
    /// <exception cref="SetByConfigurationException">It is to become the default, and the configuration names the default.</exception>
    /// <exception cref="DeliveryException">The partition is registered already.</exception>
    public async Task<DeliveryPartition> AddAsync(string name, string? description, bool makeDefault, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        var partition = PartitionNames.Check(name, "The partition");
        var described = Description(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var db = Open();
        return await InTransactionAsync(db, async () =>
        {
            var existing = await db.DeliveryPartitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            if (existing.FirstOrDefault(p => string.Equals(p.Name, partition, StringComparison.OrdinalIgnoreCase)) is { } registered)
            {
                throw new DeliveryException($"Partition '{registered.Name}' is registered already.");
            }

            if (makeDefault)
            {
                RefuseConfiguredDefault(existing, partition);
            }

            var row = new DeliveryPartition
            {
                Name = partition,
                Description = described,
                IsDefault = makeDefault || existing.Count == 0,
                CreatedUtc = nowUtc,
                CreatedBy = Clip(actor),
                UpdatedUtc = nowUtc,
                UpdatedBy = Clip(actor),
            };
            if (row.IsDefault)
            {
                await ClearDefaultAsync(db, ct).ConfigureAwait(false);
            }

            db.DeliveryPartitions.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Sets what <paramref name="name"/> is for; an empty description clears it.</summary>
    /// <exception cref="PartitionNotRegisteredException">The partition is not registered.</exception>
    /// <exception cref="SetByConfigurationException">The configuration names the partition, and describes it.</exception>
    public async Task<DeliveryPartition> DescribeAsync(string name, string? description, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        var described = Description(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var db = Open();
        var row = await FindAsync(db, name, ct).ConfigureAwait(false);
        if (row.IsConfigured)
        {
            var setting = $"{ConfiguredPartitions.KeyOf(row.Name)}:Description";
            throw new SetByConfigurationException(
                $"Partition '{row.Name}' is set by the control plane's configuration, which describes it: change {setting} there.", setting);
        }

        row.Description = described;
        row.UpdatedUtc = nowUtc;
        row.UpdatedBy = Clip(actor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>Makes <paramref name="name"/> the partition a run that names none runs in.</summary>
    /// <exception cref="PartitionNotRegisteredException">The partition is not registered.</exception>
    /// <exception cref="SetByConfigurationException">The configuration names the default.</exception>
    /// <exception cref="DeliveryException">Another change of the registry got there first.</exception>
    public async Task<DeliveryPartition> MakeDefaultAsync(string name, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var db = Open();
        return await InTransactionAsync(db, async () =>
        {
            var row = await FindAsync(db, name, ct).ConfigureAwait(false);
            if (!row.IsDefault)
            {
                RefuseConfiguredDefault(await db.DeliveryPartitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false), row.Name);
                await ClearDefaultAsync(db, ct).ConfigureAwait(false);
                row.IsDefault = true;
                row.UpdatedUtc = nowUtc;
                row.UpdatedBy = Clip(actor);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return row;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes <paramref name="name"/> from the registry. Nothing kept under it is deleted: its caches, ledgers and runs stay,
    /// and a flow that follows the registry can no longer run in it. The default is removed only when it is the last
    /// partition, so a registry that holds partitions always has a default.
    /// </summary>
    /// <returns>False when the partition was not registered.</returns>
    /// <exception cref="SetByConfigurationException">The configuration names the partition.</exception>
    /// <exception cref="DeliveryException">The partition is the default and others are registered.</exception>
    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        var rows = await db.DeliveryPartitions.ToListAsync(ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return false;
        }

        if (row.IsConfigured)
        {
            var setting = ConfiguredPartitions.KeyOf(row.Name);
            throw new SetByConfigurationException(
                $"Partition '{row.Name}' is set by the control plane's configuration ({setting}). Take it out of the configuration; once the control plane has started without it, it is an ordinary registered partition an admin can remove.",
                setting);
        }

        if (row.IsDefault && rows.Count > 1)
        {
            throw new DeliveryException(
                $"Partition '{row.Name}' is the default, which a run that names no partition runs in; make another partition the default before removing it.");
        }

        db.DeliveryPartitions.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<DeliveryPartition> FindAsync(OsduDbContext db, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var wanted = name.Trim();
        var rows = await db.DeliveryPartitions.ToListAsync(ct).ConfigureAwait(false);
        return rows.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new PartitionNotRegisteredException(
                new RegisteredPartitions(rows.Select(r => new RegisteredPartition(r.Name, r.Description, r.IsDefault))).NotRegistered(wanted));
    }

    /// <summary>Refuses to move the default away from a partition the configuration makes the default.</summary>
    private static void RefuseConfiguredDefault(IReadOnlyList<DeliveryPartition> rows, string wanted)
    {
        if (rows.FirstOrDefault(r => r.IsConfigured && r.IsDefault) is { } configured
            && !string.Equals(configured.Name, wanted, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetByConfigurationException(
                $"The default partition is '{configured.Name}', which the control plane's configuration makes the default; to make '{wanted}' the default, configure it and set {ConfiguredPartitions.KeyOf(wanted)}:Default to true there instead.",
                $"{ConfiguredPartitions.KeyOf(configured.Name)}:Default");
        }
    }

    private static Task ClearDefaultAsync(OsduDbContext db, CancellationToken ct)
        => db.DeliveryPartitions.Where(p => p.IsDefault).ExecuteUpdateAsync(set => set.SetProperty(p => p.IsDefault, false), ct);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction, through the context's execution strategy. Two changes of the default
    /// at the same moment cannot both hold: the index that keeps one default refuses the second, which is told to try again.
    /// </summary>
    private static async Task<T> InTransactionAsync<T>(OsduDbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var result = await work().ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new DeliveryException(
                $"The partition registry changed while this change was made ({ex.GetBaseException().Message}); nothing was changed. Try again.", ex);
        }
    }

    private static string? Description(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= DeliveryPartition.MaxDescriptionLength
            ? trimmed
            : throw new FlowValidationException($"A partition's description is at most {DeliveryPartition.MaxDescriptionLength} characters; this one is {trimmed.Length}.");
    }

    private static string Clip(string actor) => actor.Length <= 200 ? actor : actor[..200];
}
