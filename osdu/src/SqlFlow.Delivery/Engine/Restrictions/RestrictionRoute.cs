using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Restrictions;

/// <summary>
/// Whether a flow's route can restrict its records' access (osdu/docs/reference/concepts/access-restriction.md, Which routes),
/// with the call it makes or why it cannot. Worked out from the flow alone, as <c>RemovalEndpoints</c> and <c>ReversalRoute</c>
/// are, so the removal dialog shows it before anything is queued, and the run passes over what the route cannot do with the
/// same reason. A record's access is restricted only where one storage ACL is all of it: a storage record, and a Wellbore DDMS
/// record under a platform endpoint, whose bulk data the DDMS serves only to whoever may read the record. A route that
/// registers datasets beside the record, a DDMS that keeps data under access of its own, and rows or objects outside storage
/// are refused, since restricting the record would leave the rest readable.
/// </summary>
/// <param name="Call">The call a restriction (and a lift) makes, or why the route cannot, in brackets.</param>
/// <param name="Refusal">Why the route cannot restrict the records, or null when it can.</param>
public sealed record RestrictionRoute(string Call, string? Refusal)
{
    /// <summary>Whether the route restricts the records.</summary>
    public bool Restricts => Refusal is null;

    /// <summary>What the route can do for records of <paramref name="kind"/>, the kind the flow's mapping renders (null while it is not known).</summary>
    public static RestrictionRoute Of(FlowDefinition flow, string? kind)
        => OfEntityType(flow, string.IsNullOrWhiteSpace(kind) ? null : OsduKind.EntityType(kind));

    /// <summary>What the route can do for the record <paramref name="targetId"/>, by the entity type its id names.</summary>
    public static RestrictionRoute ForRecord(FlowDefinition flow, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return OfEntityType(flow, DdmsRouting.EntityTypeOf(targetId));
    }

    private static RestrictionRoute OfEntityType(FlowDefinition flow, string? entityType)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var options = flow.Target.ProtocolOptions;
        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.Storage:
                var method = (options.RecordMethod ?? "PUT").ToUpperInvariant();
                var path = options.RecordPath ?? OsduRecordProtocol.DefaultRecordPath;
                return method == "PUT"
                    ? Can($"PATCH {path}")
                    : Cannot($"the flow writes its records with {method} {path}, not the storage service's PUT, so the storage service whose metadata patch replaces a record's ACL is not known to be behind it");

            case DeliveryProtocol.Ddms:
                return string.IsNullOrWhiteSpace(entityType)
                    ? Cannot("the collection serving the records' entity type is known once the flow's mapping is synced")
                    : OfDdms(DdmsRouting.Of(flow), entityType);

            case DeliveryProtocol.Dspdm:
                return Cannot("DSPDM keeps business object rows, which carry no OSDU ACL");

            case DeliveryProtocol.Etp:
                return Cannot("the Reservoir DDMS keeps Energistics objects, which carry no OSDU ACL of their own");

            default:
                return Cannot($"the {DeliveryProtocols.Name(flow.Target.Protocol)} route registers datasets or a workflow's records beside each record, each with an ACL of its own, which restricting the record alone would leave readable; remove the record instead");
        }
    }

    private static RestrictionRoute OfDdms(DdmsRouting routing, string entityType)
    {
        DdmsRecordPaths paths;
        try
        {
            paths = routing.For(entityType);
        }
        catch (DeliveryException ex)
        {
            return Cannot($"{entityType} records cannot be routed: {ex.Message}");
        }

        if (paths.Shape != DdmsShape.WellboreDdmsV3)
        {
            return Cannot($"{entityType} records go to a DDMS of the {DdmsCatalog.ShapeName(paths.Shape)} shape, which keeps data of its own beside the record under access of its own, which restricting the record would leave readable");
        }

        return routing.PlatformEndpoint
            ? Can($"PATCH {OsduRecordProtocol.DefaultRecordPath} (the storage service, past the DDMS)")
            : Cannot("the flow's endpoint is the DDMS itself, so the storage service that keeps the records' ACL is not under it; make the endpoint the platform root and give the DDMS its root under target.ddms");
    }

    private static RestrictionRoute Can(string call) => new($"{call}: acl.viewers and acl.owners replaced in place, no new version", null);

    private static RestrictionRoute Cannot(string why) => new($"({why})", why);
}
