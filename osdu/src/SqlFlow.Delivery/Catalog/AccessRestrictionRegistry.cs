using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Catalog;

/// <summary>A registered access restriction and how many records carry it now.</summary>
public sealed record RegisteredAccessRestriction(AccessRestrictionState Restriction, long Records);

/// <summary>An access restriction cannot be removed while records carry it: lifting them first gives each its access back.</summary>
public sealed class AccessRestrictionInUseException(string message) : DeliveryException(message);

/// <summary>
/// The access restrictions registered in the module's database (<c>osdu.AccessRestriction</c>,
/// osdu/docs/reference/concepts/access-restriction.md, The registry): per OSDU partition, a name, what it is for, and the
/// entitlements groups a restricted record's ACL in OSDU becomes. Registering checks the groups are data groups storage
/// accepts in an ACL; whether the flow's identity can lift a restriction is checked when records are restricted, under the
/// flow's own credentials. A restriction's groups never change once registered (OSDU holds them on its records), only what
/// it is for; and it is removed only while no record carries it.
/// </summary>
public static partial class AccessRestrictionRegistry
{
    /// <summary>The most groups a restriction names as its viewers, or as its owners.</summary>
    public const int MaxGroups = 20;

    /// <summary>The restrictions of <paramref name="partition"/>, or of every partition, by partition then name, each with its records.</summary>
    public static async Task<IReadOnlyList<RegisteredAccessRestriction>> ListAsync(OsduDbContext db, string? partition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.DeliveryAccessRestrictions.AsNoTracking()
            .Where(a => partition == null || a.Partition == partition)
            .OrderBy(a => a.Partition).ThenBy(a => a.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => (int?)r.RestrictionId).ToList();
        var counts = ids.Count == 0
            ? new Dictionary<int, long>()
            : await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.RestrictionId != null && ids.Contains(r.RestrictionId))
                .GroupBy(r => r.RestrictionId!.Value)
                .Select(g => new { Id = g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(c => c.Id, c => c.Count, ct).ConfigureAwait(false);
        return [.. rows.Select(r => new RegisteredAccessRestriction(AccessRestrictionState.Of(r), counts.GetValueOrDefault(r.RestrictionId)))];
    }

    /// <summary>One restriction, with its records; null when none has the id.</summary>
    public static async Task<RegisteredAccessRestriction?> GetAsync(OsduDbContext db, int restrictionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.DeliveryAccessRestrictions.AsNoTracking().FirstOrDefaultAsync(a => a.RestrictionId == restrictionId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var records = await db.DeliveryRecords.AsNoTracking().LongCountAsync(r => r.RestrictionId == restrictionId, ct).ConfigureAwait(false);
        return new RegisteredAccessRestriction(AccessRestrictionState.Of(row), records);
    }

    /// <summary>
    /// Registers a restriction for <paramref name="partition"/>, which the registry has to hold.
    /// </summary>
    /// <exception cref="DeliveryException">The partition, the name or a group is not one a restriction can take, or the name is taken.</exception>
    public static async Task<AccessRestrictionState> AddAsync(
        OsduDbContext db, string? partition, string? name, string? description, IReadOnlyList<string>? viewers, IReadOnlyList<string>? owners, string actor, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var where = partition?.Trim() ?? string.Empty;
        var named = name?.Trim() ?? string.Empty;
        if (where.Length == 0)
        {
            throw new DeliveryException("An access restriction is registered for one partition: name it, as runs name it (its data-partition-id).");
        }

        if (!Name().IsMatch(named))
        {
            throw new DeliveryException(
                $"'{named}' is not a name an access restriction can take: a letter or digit, then letters, digits, spaces, '.', '_' or '-', at most {DeliveryAccessRestriction.MaxNameLength} characters.");
        }

        var described = Described(description);
        var acl = new RecordAcl(viewers ?? [], owners ?? []);
        Check(acl.Viewers, "viewers");
        Check(acl.Owners, "owners");
        var viewersJson = AccessRestrictionState.GroupsJson(acl.Viewers);
        var ownersJson = AccessRestrictionState.GroupsJson(acl.Owners);
        if (viewersJson.Length > DeliveryAccessRestriction.MaxGroupsLength || ownersJson.Length > DeliveryAccessRestriction.MaxGroupsLength)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"An access restriction keeps at most {DeliveryAccessRestriction.MaxGroupsLength} characters of groups as its viewers, and as its owners; these take {viewersJson.Length} and {ownersJson.Length}."));
        }

        if (!await db.DeliveryPartitions.AsNoTracking().AnyAsync(p => p.Name == where, ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Partition '{where}' is not registered, so no access restriction can be registered for it. Register the partition first.");
        }

        if (await db.DeliveryAccessRestrictions.AsNoTracking().AnyAsync(a => a.Partition == where && a.Name == named, ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Partition '{where}' has an access restriction named '{named}' already. Name this one otherwise.");
        }

        var row = new DeliveryAccessRestriction
        {
            Partition = where,
            Name = named,
            Description = described,
            Viewers = viewersJson,
            Owners = ownersJson,
            CreatedUtc = nowUtc,
            CreatedBy = actor,
            UpdatedUtc = nowUtc,
            UpdatedBy = actor,
        };
        db.DeliveryAccessRestrictions.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DeliveryException($"Partition '{where}' has an access restriction named '{named}' already: one was registered under that name meanwhile. Name this one otherwise.", ex);
        }

        return AccessRestrictionState.Of(row);
    }

    /// <summary>Says what a restriction is for; its groups stay as registered. Null when no restriction has the id.</summary>
    /// <exception cref="DeliveryException">The description is longer than a restriction keeps.</exception>
    public static async Task<AccessRestrictionState?> DescribeAsync(OsduDbContext db, int restrictionId, string? description, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var described = Described(description);
        var row = await db.DeliveryAccessRestrictions.AsTracking().FirstOrDefaultAsync(a => a.RestrictionId == restrictionId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        row.Description = described;
        row.UpdatedUtc = nowUtc;
        row.UpdatedBy = actor;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return AccessRestrictionState.Of(row);
    }

    /// <summary>
    /// Removes a restriction no record carries, in one serializable transaction with the count that says so, so a run that
    /// restricts records under it meanwhile either finds it gone or keeps it. False when no restriction has the id.
    /// </summary>
    /// <exception cref="AccessRestrictionInUseException">Records carry it: lift their restrictions first.</exception>
    public static async Task<bool> RemoveAsync(OsduDbContext db, int restrictionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var row = await db.DeliveryAccessRestrictions.AsTracking().FirstOrDefaultAsync(a => a.RestrictionId == restrictionId, ct).ConfigureAwait(false);
            if (row is null)
            {
                return false;
            }

            var records = await db.DeliveryRecords.AsNoTracking().LongCountAsync(r => r.RestrictionId == restrictionId, ct).ConfigureAwait(false);
            if (records > 0)
            {
                throw new AccessRestrictionInUseException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{records} record(s) carry access restriction '{row.Name}' of partition '{row.Partition}', so it was not removed: lift their restrictions first (their flows' Records tabs, Restricted, then Remove, Lift the restriction)."));
            }

            db.DeliveryAccessRestrictions.Remove(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static string? Described(string? description)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= DeliveryAccessRestriction.MaxDescriptionLength
            ? text
            : throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"What an access restriction is for is said in at most {DeliveryAccessRestriction.MaxDescriptionLength} characters; this takes {text.Length}."));
    }

    /// <summary>Refuses a list of groups that is empty, too long, or names one storage does not accept in an ACL.</summary>
    private static void Check(IReadOnlyList<string> groups, string list)
    {
        if (groups.Count == 0)
        {
            throw new DeliveryException($"An access restriction names at least one group as its {list}: storage keeps no record with an empty ACL list.");
        }

        if (groups.Count > MaxGroups)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"An access restriction names at most {MaxGroups} groups as its {list}; this names {groups.Count}."));
        }

        var invalid = EntitlementGroups.NotDataGroups(groups);
        if (invalid.Count > 0)
        {
            throw new DeliveryException(
                $"{string.Join(", ", invalid)} {(invalid.Count == 1 ? "is not a data group" : "are not data groups")} storage accepts in an ACL, as the restriction's {list}: a data group's email, data.<name>@<domain>, as the partition's entitlements name it.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,99}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Name();
}
