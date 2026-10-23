using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The central configuration a run is given, in the two layers a run resolves with: <see cref="Base"/>, the values set for
/// no partition, and <see cref="Partitions"/>, the values set for single partitions by partition name, which a run bound to
/// one of them reads first (osdu/docs/reference/concepts/control-plane.md, The central configuration on every run).
/// </summary>
public sealed record DeliveryConfiguration(
    IReadOnlyDictionary<string, string> Base,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Partitions)
{
    /// <summary>No configuration: every reference is left to the node.</summary>
    public static DeliveryConfiguration None { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>True when nothing is configured at any scope.</summary>
    public bool IsEmpty => Base.Count == 0 && Partitions.Count == 0;

    /// <summary>What a run bound to <paramref name="partition"/> resolves with: its values over the base; the base alone for none.</summary>
    public IReadOnlyDictionary<string, string> For(string? partition)
    {
        if (partition is null || !Partitions.TryGetValue(partition, out var own) || own.Count == 0)
        {
            return Base;
        }

        var merged = new Dictionary<string, string>(Base, StringComparer.Ordinal);
        foreach (var (name, value) in own)
        {
            merged[name] = value;
        }

        return merged;
    }
}

/// <summary>
/// The central configuration: the values the control plane supplies to the runs it queues, so a flow naming
/// <c>${env:NAME}</c> resolves it from one place rather than from whatever the node that picks the run up happens to
/// hold.
/// </summary>
/// <remarks>
/// <para>
/// A property is set for the whole control plane, and may be set again for one repository, which is an estate. The
/// repository's value wins for the flows that repository holds. Either may be set for one OSDU partition as well, and a
/// run bound to that partition resolves with it first: the repository's value for the partition, the control plane's for
/// the partition, the repository's, the control plane's.
/// </para>
/// <para>
/// A value is a non-secret value or a <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, which travels
/// unresolved and is resolved on the node. A literal secret here would be a secret in a catalog row and in a run
/// payload, which is a defect.
/// </para>
/// <para>
/// This store is the one place a property is checked: the API and <c>sqlflow config</c> both hand it what they were
/// given, as given, and it trims, bounds and refuses alike for both, so a value one of them accepts the other accepts too.
/// A value the control plane's configuration sets for a partition (<see cref="ConfiguredPartitions"/>) is the
/// configuration's, and neither changes nor removes it (<see cref="SetByConfigurationException"/>).
/// </para>
/// </remarks>
public sealed class DeliveryConfigStore
{
    /// <summary>The widest actor the row records: the width of its <c>UpdatedBy</c> column.</summary>
    private const int MaxActorLength = 200;

    private readonly Func<OsduDbContext>? _contexts;

    /// <param name="contexts">
    /// The module database, or null when the host was started without one. Reading then answers that nothing is
    /// configured, which leaves every reference to the nodes; setting one says the host has no database to set it in.
    /// </param>
    public DeliveryConfigStore(Func<OsduDbContext>? contexts) => _contexts = contexts;

    /// <summary>Whether this control plane has a module database to hold a configuration at all.</summary>
    public bool Available => _contexts is not null;

    /// <summary>The module database, or a failure naming what the host was started without.</summary>
    private OsduDbContext Open()
        => (_contexts ?? throw new FlowValidationException(
            $"The central configuration lives in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}"))();

    /// <summary>
    /// What a run of a flow of <paramref name="repoId"/> is given, in its two layers: the values set for no partition (the
    /// control plane's, with the repository's overriding them by name), and for each partition any value is set for, the
    /// values set for it (the control plane's, with the repository's overriding them). None when nothing is set.
    /// </summary>
    public async Task<DeliveryConfiguration> ConfigurationAsync(Guid? repoId, CancellationToken ct = default)
    {
        if (_contexts is null)
        {
            return DeliveryConfiguration.None;
        }

        await using var db = Open();
        var rows = await db.DeliveryConfigProperties
            .AsNoTracking()
            .Where(c => c.RepoId == null || c.RepoId == repoId)
            .Select(c => new { c.RepoId, c.Partition, c.Name, c.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var based = new Dictionary<string, string>(StringComparer.Ordinal);
        var partitions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // The control plane's rows first and the repository's second, so a repository's value replaces the control
        // plane's for the same name in the same layer.
        foreach (var row in rows.OrderBy(r => r.RepoId is not null))
        {
            if (row.Partition is null)
            {
                based[row.Name] = row.Value;
                continue;
            }

            if (!partitions.TryGetValue(row.Partition, out var own))
            {
                own = new Dictionary<string, string>(StringComparer.Ordinal);
                partitions[row.Partition] = own;
            }

            own[row.Name] = row.Value;
        }

        return new DeliveryConfiguration(
            based,
            partitions.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a run of a flow of <paramref name="repoId"/> bound to <paramref name="partition"/> resolves with (the values set
    /// for no partition when it is null): the view a person checks the configuration by, flattened.
    /// </summary>
    /// <exception cref="FlowValidationException">The partition is not a data-partition-id.</exception>
    public async Task<IReadOnlyDictionary<string, string>> EffectiveAsync(Guid? repoId, string? partition, CancellationToken ct = default)
    {
        var scope = CheckPartition(partition);
        return (await ConfigurationAsync(repoId, ct).ConfigureAwait(false)).For(scope);
    }

    /// <summary>
    /// The properties a listing shows, ordered by name, then repository, then partition: every property of every scope, or
    /// those set for <paramref name="repoId"/> (any partition) and those set for <paramref name="partition"/> (any scope),
    /// or with both, those set for that repository in that partition. A filter that matches nothing lists nothing; a
    /// repository is not looked up, so the properties of one no longer registered can still be found and removed.
    /// </summary>
    /// <exception cref="FlowValidationException">The partition is not a data-partition-id.</exception>
    public async Task<IReadOnlyList<DeliveryConfigProperty>> ListAsync(Guid? repoId, string? partition, CancellationToken ct = default)
    {
        var scope = CheckPartition(partition);
        if (_contexts is null)
        {
            return [];
        }

        await using var db = Open();
        var rows = db.DeliveryConfigProperties.AsNoTracking();
        if (repoId is { } repo)
        {
            rows = rows.Where(c => c.RepoId == repo);
        }

        if (scope is not null)
        {
            rows = rows.Where(c => c.Partition == scope);
        }

        return await rows
            .OrderBy(c => c.Name)
            .ThenBy(c => c.RepoId)
            .ThenBy(c => c.Partition)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <paramref name="name"/> at <paramref name="repoId"/> (null for the control plane's own) for
    /// <paramref name="partition"/> (null for no particular partition), replacing what was there. Returns the row as it now
    /// stands. The value and the description are trimmed; an empty description clears it.
    /// </summary>
    /// <remarks>
    /// <paramref name="catalog"/> is the catalog a repository is checked in: a property set for a repository the catalog does
    /// not hold would never be read by a run. It is read only when <paramref name="repoId"/> names one, and null then refuses
    /// the property. <paramref name="actor"/> is recorded as who set it, cut to the width of its column.
    /// </remarks>
    /// <exception cref="FlowValidationException">The name, the value, the description or the partition is not one a property may hold.</exception>
    /// <exception cref="RepositoryNotRegisteredException">The repository is not registered with the catalog.</exception>
    /// <exception cref="SetByConfigurationException">The control plane's configuration sets the property at this scope.</exception>
    /// <exception cref="DeliveryException">The catalog could not be read, or the property could not be saved.</exception>
    public async Task<DeliveryConfigProperty> SetAsync(
        Guid? repoId,
        string? partition,
        string name,
        string? value,
        string? description,
        string actor,
        DateTime nowUtc,
        CatalogDbContext? catalog,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var scope = CheckPartition(partition);
        if (!DeliveryConfigNames.IsName(name))
        {
            throw new FlowValidationException(
                $"'{name}' does not name a configuration property: a property is named as a flow spells it in ${{env:NAME}}, which is a letter or underscore followed by letters, digits and underscores, at most {DeliveryConfigNames.MaxNameLength} characters.");
        }

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new FlowValidationException($"'{name}' needs a value; remove the property instead of setting it to nothing.");
        }

        if (!DeliveryConfigNames.IsValue(trimmed))
        {
            throw new FlowValidationException(
                $"the value of '{name}' is longer than {DeliveryConfigNames.MaxValueLength} characters ({trimmed.Length}) or holds a control character; a property holds an identifier, a URL or a ${{env:...}} or ${{keyvault:...}} reference.");
        }

        var described = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (described is { Length: > DeliveryConfigNames.MaxDescriptionLength })
        {
            throw new FlowValidationException(
                $"the description of '{name}' is {described.Length} characters; a property's description is at most {DeliveryConfigNames.MaxDescriptionLength}.");
        }

        if (repoId is { } repo)
        {
            await CheckRepositoryAsync(catalog, repo, ct).ConfigureAwait(false);
        }

        var by = actor.Length <= MaxActorLength ? actor : actor[..MaxActorLength];
        for (var attempt = 1; ; attempt++)
        {
            await using var db = Open();
            var row = await db.DeliveryConfigProperties
                .FirstOrDefaultAsync(c => c.RepoId == repoId && c.Partition == scope && c.Name == name, ct)
                .ConfigureAwait(false);

            RefuseConfigured(row);
            var added = row is null;
            if (row is null)
            {
                row = new DeliveryConfigProperty { Id = Guid.NewGuid(), RepoId = repoId, Partition = scope, Name = name };
                db.DeliveryConfigProperties.Add(row);
            }

            row.Value = trimmed;
            row.Description = described;
            row.UpdatedUtc = nowUtc;
            row.UpdatedBy = by;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return row;
            }
            catch (DbUpdateException) when (added && attempt == 1)
            {
                // Another set of the same name at the same scope added the row between the read and the write, and the
                // unique index refused this one. Setting is replacing, so the set is made again over the row that won.
            }
            catch (DbUpdateException ex)
            {
                throw new DeliveryException(
                    $"Configuration property '{name}' {Scope(repoId, scope)} could not be saved; nothing was changed: {SecretHygiene.RedactedMessage(ex)}", ex);
            }
        }
    }

    /// <summary>
    /// Removes <paramref name="name"/> at <paramref name="repoId"/> (null for the control plane's own) for
    /// <paramref name="partition"/> (null for no particular partition). False when it was not set there, which is not an
    /// error: removing what is already absent leaves the same state. The repository is not looked up, so what was set for
    /// one since removed from the catalog can still be removed.
    /// </summary>
    /// <exception cref="FlowValidationException">The partition is not a data-partition-id.</exception>
    /// <exception cref="SetByConfigurationException">The control plane's configuration sets the property at this scope.</exception>
    public async Task<bool> RemoveAsync(Guid? repoId, string? partition, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var scope = CheckPartition(partition);
        await using var db = Open();
        RefuseConfigured(await db.DeliveryConfigProperties
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RepoId == repoId && c.Partition == scope && c.Name == name && c.IsConfigured, ct)
            .ConfigureAwait(false));

        // A row the configuration takes over between the check and the delete is the configuration's, and stays.
        var removed = await db.DeliveryConfigProperties
            .Where(c => c.RepoId == repoId && c.Partition == scope && c.Name == name && !c.IsConfigured)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return removed > 0;
    }

    /// <summary>Refuses to change or remove a value the control plane's configuration sets.</summary>
    private static void RefuseConfigured(DeliveryConfigProperty? row)
    {
        if (row is { IsConfigured: true, Partition: { } partition })
        {
            var setting = ConfiguredPartitions.KeyOf(partition, row.Name);
            throw new SetByConfigurationException(
                $"'{row.Name}' in partition '{partition}' is set by the control plane's configuration ({setting}); change it there, and the control plane writes it when it next starts.",
                setting);
        }

        if (row is { IsConfigured: true, Partition: null, RepoId: null })
        {
            var setting = ConfiguredPartitions.PropertyKey(row.Name);
            throw new SetByConfigurationException(
                $"'{row.Name}' is set by the control plane's configuration ({setting}); change it there, and the control plane writes it when it next starts.",
                setting);
        }
    }

    /// <summary>A partition a property is set for: null for none, or a data-partition-id written literally.</summary>
    /// <exception cref="FlowValidationException">The value is not a data-partition-id.</exception>
    private static string? CheckPartition(string? partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            return null;
        }

        var name = partition.Trim();
        return CacheScope.IsPartitionId(name)
            ? name
            : throw new FlowValidationException(
                $"'{name}' is not a data-partition-id a property can be set for: letters, digits, underscore, hyphen and dot, at most {CacheScope.MaxLength} characters.");
    }

    /// <summary>Refuses a repository the catalog does not hold, which no run would ever read a property of.</summary>
    private static async Task CheckRepositoryAsync(CatalogDbContext? catalog, Guid repoId, CancellationToken ct)
    {
        if (catalog is null)
        {
            throw new FlowValidationException(
                $"A property set for repository {repoId:D} is checked against the catalog's repositories, and this caller has no catalog connection to check it in.");
        }

        bool registered;
        try
        {
            registered = await catalog.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            throw new DeliveryException(
                $"The catalog could not be read to check that repository {repoId:D} is registered; nothing was set: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        if (!registered)
        {
            throw new RepositoryNotRegisteredException(repoId);
        }
    }

    /// <summary>The scope a property is set at, as a message names it.</summary>
    private static string Scope(Guid? repoId, string? partition)
        => (repoId is { } repo ? $"for repository {repo:D}" : "for the control plane")
            + (partition is null ? string.Empty : $" in partition '{partition}'");
}

/// <summary>A configuration property was set for a repository the catalog does not hold.</summary>
public sealed class RepositoryNotRegisteredException : DeliveryException
{
    public RepositoryNotRegisteredException(Guid repoId)
        : base(
            $"No repository {repoId:D} is registered with the catalog, so a configuration property set for it would never be read by a run. "
            + "'sqlflow repos list' and the Repositories page show the registered repositories and their ids.")
    {
        RepoId = repoId;
    }

    /// <summary>The repository that was named.</summary>
    public Guid RepoId { get; }
}
