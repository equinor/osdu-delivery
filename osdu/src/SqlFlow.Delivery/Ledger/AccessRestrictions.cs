using System.Text.Json;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// An access restriction registered for a partition (osdu/docs/reference/concepts/access-restriction.md): its name, what it is
/// for, and the ACL a restricted record's ACL in OSDU is replaced with.
/// </summary>
public sealed record AccessRestrictionState(
    int RestrictionId, string Partition, string Name, string? Description, RecordAcl Acl, DateTime CreatedUtc, string CreatedBy, DateTime UpdatedUtc, string UpdatedBy)
{
    /// <summary>The restriction as the ledger's row holds it.</summary>
    public static AccessRestrictionState Of(DeliveryAccessRestriction row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new AccessRestrictionState(
            row.RestrictionId, row.Partition, row.Name, row.Description, new RecordAcl(Groups(row.Viewers), Groups(row.Owners)),
            DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc), row.CreatedBy, DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc), row.UpdatedBy);
    }

    /// <summary>A group list as the registry keeps it: a JSON array of group emails.</summary>
    public static string GroupsJson(IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return JsonSerializer.Serialize(groups);
    }

    private static List<string> Groups(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? throw new DeliveryException($"An access restriction's groups '{json}' are not a list of group emails.");
}

/// <summary>
/// A record whose restriction is asked, with the ACL OSDU held for it and for each record beside it the restriction reaches
/// before, which lifting the restriction writes back.
/// </summary>
public sealed record RestrictionAsked(DeliveryKey Key, RestrictedAccess From);

/// <summary>A record whose restriction OSDU confirmed, with what the record's result says of it.</summary>
public sealed record RestrictionConfirmed(DeliveryKey Key, string Detail);

/// <summary>
/// A record whose restriction is lifted, with what the record's result says of it. <paramref name="Lost"/>: OSDU no longer held
/// the record when its ACL was to be written back, so the record is sent again by the flow's next run, as a verify that finds
/// it missing has it sent.
/// </summary>
public sealed record RestrictionLifted(DeliveryKey Key, string Detail, bool Lost = false);

/// <summary>What the ledger did with records it was asked to restrict or lift: those it wrote, and those it left as they were.</summary>
public sealed record RestrictionWrite(IReadOnlySet<DeliveryKey> Written, IReadOnlySet<DeliveryKey> Moved);
