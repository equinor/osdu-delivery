using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Protocols;

/// <summary>
/// A record's access in OSDU: the entitlements groups that may read it (<c>acl.viewers</c>) and that may change or remove it
/// (<c>acl.owners</c>), each a group email. Storage keeps one ACL per record, whichever version is read, so replacing it
/// governs every version (osdu/specs/core/INTEGRATION.md, 2.3).
/// </summary>
public sealed record RecordAcl
{
    public RecordAcl(IReadOnlyList<string> viewers, IReadOnlyList<string> owners)
    {
        ArgumentNullException.ThrowIfNull(viewers);
        ArgumentNullException.ThrowIfNull(owners);
        Viewers = [.. viewers.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        Owners = [.. owners.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<string> Viewers { get; }

    public IReadOnlyList<string> Owners { get; }

    /// <summary>The ACL as the ledger keeps it: <c>{"viewers":[...],"owners":[...]}</c>.</summary>
    public string ToJson() => new JsonObject
    {
        ["viewers"] = new JsonArray([.. Viewers.Select(v => (JsonNode?)JsonValue.Create(v))]),
        ["owners"] = new JsonArray([.. Owners.Select(o => (JsonNode?)JsonValue.Create(o))]),
    }.ToJsonString();

    /// <summary>The ACL the ledger kept as <paramref name="json"/> (<see cref="ToJson"/>).</summary>
    /// <exception cref="DeliveryException">The text is not an ACL with viewers and owners.</exception>
    public static RecordAcl Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonNode.Parse(json) is JsonObject node && Of(node) is { } acl
                ? acl
                : throw new DeliveryException($"'{Shorten(json)}' is not an ACL: it needs viewers and owners, each a list of group emails.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"'{Shorten(json)}' is not an ACL: {ex.Message}", ex);
        }
    }

    /// <summary>The ACL a stored record carries under <c>acl</c>, or null when it carries none with both lists.</summary>
    public static RecordAcl? OfRecord(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record["acl"] is JsonObject acl ? Of(acl) : null;
    }

    /// <summary>The ACL two group lists make, or null when either is missing or not a list of strings.</summary>
    private static RecordAcl? Of(JsonObject node)
        => Groups(node["viewers"]) is { Count: > 0 } viewers && Groups(node["owners"]) is { Count: > 0 } owners ? new RecordAcl(viewers, owners) : null;

    private static List<string>? Groups(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var groups = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return null;
            }

            groups.Add(text);
        }

        return groups;
    }

    /// <summary>Whether <paramref name="other"/> names the same groups in each list, in any order and case.</summary>
    public bool SameAs(RecordAcl other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Same(Viewers, other.Viewers) && Same(Owners, other.Owners);
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count && a.All(g => b.Contains(g, StringComparer.OrdinalIgnoreCase));

    /// <summary>The ACL as a message names it: <c>viewers a, b; owners c</c>.</summary>
    public string Describe() => $"viewers {string.Join(", ", Viewers)}; owners {string.Join(", ", Owners)}";

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..120] + "...";
}

/// <summary>
/// Records beside a restricted record whose ACL the restriction replaced, and the ACL they had: <see cref="Acl"/> null when it
/// was the record's own, which is what a mapping's enrichments copy onto the records they add.
/// </summary>
public sealed record BesideAcl(IReadOnlyList<string> Ids, RecordAcl? Acl);

/// <summary>
/// What a restriction replaced, as the ledger keeps it to give back (osdu/docs/reference/concepts/access-restriction.md, What
/// a restriction reaches): the record's own ACL, and the ACL of each record OSDU holds beside it that the restriction reached,
/// the records the mapping's enrichments add. Kept as <c>{"viewers":[...],"owners":[...],"beside":[{"ids":[...]},
/// {"ids":[...],"viewers":[...],"owners":[...]}]}</c>: the record's ACL at the top, so a record with nothing beside it keeps
/// what <see cref="RecordAcl.ToJson"/> writes, and each group of records beside it with the ACL it had, left out when that was
/// the record's.
/// </summary>
public sealed record RestrictedAccess
{
    public RestrictedAccess(RecordAcl record, IReadOnlyList<BesideAcl>? beside = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        Beside = [.. (beside ?? []).Where(b => b.Ids.Count > 0)];
    }

    /// <summary>The record's own ACL.</summary>
    public RecordAcl Record { get; }

    /// <summary>The records beside it the restriction reached, grouped by the ACL they had.</summary>
    public IReadOnlyList<BesideAcl> Beside { get; }

    /// <summary>The records beside it the restriction reached, each with the ACL it had.</summary>
    public IReadOnlyList<(string Id, RecordAcl Acl)> BesideAcls => [.. Beside.SelectMany(b => b.Ids.Select(id => (id, b.Acl ?? Record)))];

    /// <summary>The ids of the records beside it the restriction reached.</summary>
    public IReadOnlyList<string> BesideIds => [.. Beside.SelectMany(b => b.Ids)];

    /// <summary>
    /// What the restriction replaced for the record and the records beside it, read from OSDU: <paramref name="beside"/> holds
    /// each record beside it with the ACL OSDU held for it, grouped with the record's when it is the same.
    /// </summary>
    public static RestrictedAccess Of(RecordAcl record, IReadOnlyList<(string Id, RecordAcl Acl)> beside)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(beside);
        var groups = beside
            .GroupBy(b => b.Acl.SameAs(record) ? string.Empty : b.Acl.ToJson(), StringComparer.Ordinal)
            .Select(g => new BesideAcl([.. g.Select(b => b.Id).Distinct(StringComparer.Ordinal)], g.Key.Length == 0 ? null : g.First().Acl))
            .ToList();
        return new RestrictedAccess(record, groups);
    }

    /// <summary>The access as the ledger keeps it.</summary>
    public string ToJson()
    {
        var node = JsonNode.Parse(Record.ToJson())!.AsObject();
        if (Beside.Count > 0)
        {
            var groups = new JsonArray();
            foreach (var group in Beside)
            {
                var entry = new JsonObject { ["ids"] = new JsonArray([.. group.Ids.Select(id => (JsonNode?)JsonValue.Create(id))]) };
                if (group.Acl is { } acl)
                {
                    var own = JsonNode.Parse(acl.ToJson())!.AsObject();
                    entry["viewers"] = own["viewers"]!.DeepClone();
                    entry["owners"] = own["owners"]!.DeepClone();
                }

                groups.Add(entry);
            }

            node["beside"] = groups;
        }

        return node.ToJsonString();
    }

    /// <summary>The access the ledger kept as <paramref name="json"/> (<see cref="ToJson"/>), which a record restricted before the records beside it were reached keeps as its ACL alone.</summary>
    /// <exception cref="DeliveryException">The text is not an ACL with viewers and owners, or a group beside it names no ids or an incomplete ACL.</exception>
    public static RestrictedAccess Parse(string json)
    {
        var record = RecordAcl.Parse(json);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"'{Shorten(json)}' is not a restriction's kept access: {ex.Message}", ex);
        }

        if (node?["beside"] is not { } beside)
        {
            return new RestrictedAccess(record);
        }

        if (beside is not JsonArray groups)
        {
            throw new DeliveryException($"'{Shorten(json)}' is not a restriction's kept access: beside is not a list of records with the ACL they had.");
        }

        var parsed = new List<BesideAcl>(groups.Count);
        foreach (var group in groups)
        {
            if (group is not JsonObject entry || entry["ids"] is not JsonArray idsNode)
            {
                throw new DeliveryException($"'{Shorten(json)}' is not a restriction's kept access: a group beside the record names no ids.");
            }

            var ids = new List<string>(idsNode.Count);
            foreach (var id in idsNode)
            {
                if (id is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                {
                    throw new DeliveryException($"'{Shorten(json)}' is not a restriction's kept access: a group beside the record names an id that is not text.");
                }

                ids.Add(text);
            }

            RecordAcl? acl = null;
            if (entry["viewers"] is not null || entry["owners"] is not null)
            {
                acl = RecordAcl.Parse(new JsonObject { ["viewers"] = entry["viewers"]?.DeepClone(), ["owners"] = entry["owners"]?.DeepClone() }.ToJsonString());
            }

            parsed.Add(new BesideAcl(ids, acl));
        }

        return new RestrictedAccess(record, parsed);
    }

    /// <summary>The access as a message names it: the record's ACL, and how many records beside it had which.</summary>
    public string Describe()
    {
        if (Beside.Count == 0)
        {
            return Record.Describe();
        }

        var own = Beside.Where(b => b.Acl is null).Sum(b => b.Ids.Count);
        var other = Beside.Where(b => b.Acl is not null).ToList();
        var parts = new List<string>();
        if (own > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{own} record(s) beside it with the same"));
        }

        parts.AddRange(other.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Ids.Count} beside it with {b.Acl!.Describe()}")));
        return $"{Record.Describe()} ({string.Join("; ", parts)})";
    }

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..120] + "...";
}

/// <summary>
/// The rules an entitlements data group's email follows in an ACL, as storage checks them (osdu/specs/core/INTEGRATION.md,
/// 2.2: <c>acl</c>), and how a group is compared: by the name before <c>@</c>, whatever its case, as storage compares the
/// caller's groups with a record's owners (2.2, Access on update).
/// </summary>
public static partial class EntitlementGroups
{
    /// <summary>The group every data partition's data managers are members of: storage lets them change any record.</summary>
    public const string DataRoot = "users.data.root";

    /// <summary>Whether <paramref name="group"/> is a data group's email storage accepts in an ACL.</summary>
    public static bool IsDataGroup(string? group) => group is not null && DataGroup().IsMatch(group);

    /// <summary>The name a group is compared by: everything before <c>@</c>, lower-cased.</summary>
    public static string NameOf(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var at = group.IndexOf('@', StringComparison.Ordinal);
        return (at < 0 ? group : group[..at]).Trim().ToLowerInvariant();
    }

    /// <summary>The groups among <paramref name="groups"/> that are not data groups storage accepts, for a message.</summary>
    public static IReadOnlyList<string> NotDataGroups(IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return [.. groups.Where(g => !IsDataGroup(g))];
    }

    // The pattern of osdu/specs/core/INTEGRATION.md, 2.2 (acl), as storage's AclValidator writes it.
    [GeneratedRegex(@"^data\.[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DataGroup();
}

/// <summary>What OSDU holds of one record's access: the version it holds and the ACL it governs every version with.</summary>
public sealed record RecordAccessRead(long? Version, RecordAcl Acl);

/// <summary>How replacing one record's ACL came out.</summary>
public enum AclChangeOutcome
{
    /// <summary>OSDU holds the record under the new ACL.</summary>
    Replaced,

    /// <summary>OSDU holds no record of the id: nothing was changed.</summary>
    NotFound,

    /// <summary>OSDU refused the change and kept the ACL it had: the caller is no owner of the record, or the ACL was refused.</summary>
    Refused,

    /// <summary>Whether OSDU changed the record cannot be told: the request failed with no answer that says.</summary>
    Unknown,
}

/// <summary>What replacing one record's ACL came to, and why, in words a record's result can carry.</summary>
public sealed record AclChange(string TargetId, AclChangeOutcome Outcome, string Detail);

/// <summary>The caller's identity as entitlements names it, and the groups it is a member of, by email.</summary>
public sealed record CallerGroups(string? MemberEmail, IReadOnlyList<string> Groups)
{
    /// <summary>Whether the caller is a member of <paramref name="group"/>, compared by name as storage compares owners.</summary>
    public bool IsMemberOf(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var name = EntitlementGroups.NameOf(group);
        return Groups.Any(g => string.Equals(EntitlementGroups.NameOf(g), name, StringComparison.Ordinal));
    }

    /// <summary>Whether the caller is a data manager, whom storage lets change any record whatever its owners.</summary>
    public bool IsDataManager => Groups.Any(g => string.Equals(EntitlementGroups.NameOf(g), EntitlementGroups.DataRoot, StringComparison.Ordinal));

    /// <summary>Whether storage would let the caller change a record owned by <paramref name="owners"/> (osdu/specs/core/INTEGRATION.md, 2.8, item 3).</summary>
    public bool MayChange(IEnumerable<string> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return IsDataManager || owners.Any(IsMemberOf);
    }

    /// <summary>The caller as a message names it.</summary>
    public string Who => string.IsNullOrWhiteSpace(MemberEmail) ? "the flow's identity" : string.Create(CultureInfo.InvariantCulture, $"the flow's identity ({MemberEmail})");
}

/// <summary>
/// The calls that read and replace records' access in OSDU, through the storage service that keeps every record's ACL
/// (osdu/docs/reference/concepts/access-restriction.md): a batched read of what each record holds, the metadata patch that
/// replaces the ACL in place without a new version, and the caller's own groups, which say whether it could change the
/// records again under the ACL it sets.
/// </summary>
public interface IRecordAccess
{
    /// <summary>The call that replaces an ACL, as the GUI and a result name it.</summary>
    string ReplaceRequest { get; }

    /// <summary>
    /// What OSDU holds of each record of <paramref name="ids"/> that it serves, by id: its version and its ACL. A record OSDU
    /// does not serve (removed, purged, or one the caller cannot read) is not in the answer.
    /// </summary>
    Task<IReadOnlyDictionary<string, RecordAccessRead>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken ct);

    /// <summary>
    /// Replaces the ACL of every record of <paramref name="ids"/> with <paramref name="acl"/>. Results align with
    /// <paramref name="ids"/>; a request that failed is answered for each of its records rather than thrown.
    /// </summary>
    Task<IReadOnlyList<AclChange>> ReplaceAsync(IReadOnlyList<string> ids, RecordAcl acl, CancellationToken ct);

    /// <summary>The caller's identity and groups, as entitlements answers for the flow's credentials.</summary>
    Task<CallerGroups> CallerGroupsAsync(CancellationToken ct);
}
