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
