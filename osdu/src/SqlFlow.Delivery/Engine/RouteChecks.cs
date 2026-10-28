using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What a flow's route can deliver, checked against the kind its mapping renders before anything is sent. The routes that
/// reach a DDMS send a record to the collection of the DDMS serving its entity type (<see cref="DdmsRouting"/>): a kind
/// no DDMS the flow reaches serves could never be written, verified or removed through it, and bulk data sent to a
/// collection that holds records alone would never land (osdu/specs/wellbore-ddms/INTEGRATION.md section 2). The
/// Dataset service registers records of the dataset group only (osdu/specs/core/INTEGRATION.md section 2.5), so a route
/// that registers the record itself needs a dataset kind, and the file route, whose service mints its own dataset ids,
/// cannot deliver one.
/// </summary>
public static class RouteChecks
{
    /// <summary>
    /// Throws a <see cref="DeliveryException"/> when the flow's route cannot deliver records of <paramref name="kind"/>.
    /// <paramref name="routing"/> is the ddms route's routing with its registrations read (the protocol's); without it
    /// the flow's declarations are checked as they stand, and a type a DDMS named by registration may serve is let through.
    /// </summary>
    public static void Check(FlowDefinition flow, string kind, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var keys = KeyPaths.Of(flow);
        var route = DeliveryProtocols.Name(flow.Target.Protocol);
        var mapping = $"{keys.Name("render.mapping")} {flow.Render.Mapping}";
        var entityType = OsduKind.EntityType(kind)
            ?? throw new DeliveryException(
                $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, which names no entity type, so the {route} route cannot tell where it goes.");
        // A DSPDM business object row is no OSDU record: the dspdm route writes rows alone, and no other route writes them.
        var row = DspdmKinds.Is(kind);
        if (row != (flow.Target.Protocol == DeliveryProtocol.Dspdm))
        {
            throw new DeliveryException(row
                ? $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, a row of a DSPDM business object (its template's source is '{DspdmKinds.Source}'), which only the dspdm route writes. "
                  + $"Deliver it by the dspdm route ({keys.Shared("route")}: dspdm)."
                : $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, an OSDU record, and the dspdm route writes rows of DSPDM business objects, whose templates' kinds have the source '{DspdmKinds.Source}' "
                  + "(osdu/specs/production-dspdm/INTEGRATION.md section 2).");
        }

        // An Energistics object is no OSDU record either: the etp route writes objects alone, and no other route writes them.
        var energistics = EtpKinds.Is(kind);
        if (energistics != (flow.Target.Protocol == DeliveryProtocol.Etp))
        {
            throw new DeliveryException(energistics
                ? $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, an Energistics object (its template's source is '{EtpKinds.Source}'), which only the etp route writes. "
                  + $"Deliver it by the etp route ({keys.Shared("route")}: etp)."
                : $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, an OSDU record, and the etp route writes Energistics objects into a dataspace of the Reservoir DDMS, whose templates' kinds have the source "
                  + $"'{EtpKinds.Source}' (osdu/specs/reservoir-ddms/INTEGRATION.md section 5.1).");
        }

        var dataset = Protocols.DatasetService.IsDatasetType(entityType);
        if (EdsRecordRules.IsProxyDataset(entityType) && RegistersFiles(flow))
        {
            throw new DeliveryException(
                $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, an External Data Services proxy dataset: the dataset it names stays in the external source, "
                + $"where eds-dms retrieves it, so there are no files to register for it. Deliver it by the storage route ({keys.Shared("route")}: storage) "
                + "(osdu/specs/eds-dms/INTEGRATION.md section 2.3).");
        }

        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.File when dataset:
                throw new DeliveryException(
                    $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, a dataset, and the file route registers each file as a dataset of its own and writes the record beside them. "
                    + $"Deliver a dataset with its files by the dataset route ({keys.Shared("route")}: dataset), which registers it under its own id.");
            case DeliveryProtocol.Dataset when !dataset && !Protocols.DatasetService.IsDatasetType(Identity.TargetId.EntityTypeFromKind(flow.Target.ProtocolOptions.DatasetKind)):
                throw new DeliveryException(
                    $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, which is not a dataset, so its files are registered as a dataset of {keys.Shared("target.protocolOptions.datasetKind")}, "
                    + $"and '{flow.Target.ProtocolOptions.DatasetKind}' is not a dataset kind.");
            case DeliveryProtocol.Workflow when flow.Target.Workflow is { Anchor: WorkflowAnchor.Dataset } && !dataset:
                throw new DeliveryException(
                    $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, which is not a dataset, and the workflow is anchored on a dataset registered with its files. "
                    + "Render the dataset the workflow reads (a descriptor such as dataset--File.Generic), or anchor the workflow on a storage record (anchor: storage).");
        }

        if (!DeliveryProtocols.ReachesDdms(flow.Target.Protocol))
        {
            return;
        }

        var sendsBulk = flow.Target.Protocol != DeliveryProtocol.Ddms || PayloadParts.Streamed(flow) is not null;
        if ((routing ?? DdmsRouting.Of(flow)).Problem(entityType, sendsBulk) is { } problem)
        {
            throw new DeliveryException($"{problem} ({mapping} renders {kind}.)");
        }
    }

    /// <summary>
    /// Throws a <see cref="FlowValidationException"/> when the mapping writes under a data key the flow preserves
    /// (<c>target.protocolOptions.preserveDataKeys</c>): every update carries that key over from the record OSDU holds in
    /// place of what was rendered, so what the mapping writes there would reach a record when it is created and never
    /// after, whatever it renders on later runs.
    /// </summary>
    /// <param name="flow">The flow being planned.</param>
    /// <param name="mapping">The mapping the flow pins.</param>
    /// <param name="where">The flow file, for messages.</param>
    public static void CheckPreserved(FlowDefinition flow, MappingDefinition mapping, string where)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var preserved = flow.Target.ProtocolOptions.PreserveDataKeys;
        if (preserved.Count == 0)
        {
            return;
        }

        var written = mapping.Entries
            .Where(entry => entry.Target.Root == "data" && entry.Target.Segments.Count > 1 && preserved.Contains(entry.Target.Segments[1].Name, StringComparer.Ordinal))
            .Where(entry => !LinksAlone(entry))
            .Select(entry => entry.Target)
            .ToList();
        if (written.Count == 0)
        {
            return;
        }

        var keys = written.Select(target => target.Segments[1].Name).Distinct(StringComparer.Ordinal).ToList();
        const int Named = 5;
        var named = string.Join(", ", written.Take(Named).Select(target => target.Text))
            + (written.Count > Named ? $" and {written.Count - Named} more" : string.Empty);
        throw new FlowValidationException(
            $"{where}: {KeyPaths.Of(flow).Shared("target.protocolOptions.preserveDataKeys")} carries data.{string.Join(", data.", keys)} over from the record OSDU holds "
            + $"into every update, in place of what the mapping renders, and {mapping.Reference} writes {named}: what it writes there would reach a record only when it is created. "
            + $"Take {string.Join(", ", keys)} out of preserveDataKeys, or leave {(keys.Count == 1 ? "it" : "them")} out of the mapping.");
    }

    /// <summary>
    /// Whether <paramref name="entry"/> writes nothing but the links to the records the mapping's enrichments add (<c>$link</c>)
    /// at a preserved key itself (<c>data.Datasets</c>): an update merges those with what OSDU holds there
    /// (osdu/docs/reference/flow/enrich-records.md, Preserved keys), so they reach every update, not only the record's creation.
    /// A literal beside them, or a link inside the key, would not, and is refused with the rest.
    /// </summary>
    private static bool LinksAlone(MappingEntry entry)
        => entry.Target.Segments.Count == 2
            && (entry.Source?.Kind == MappingSourceKind.Link || (entry.IsList && entry.Parts.All(part => part.Source?.Kind == MappingSourceKind.Link)));

    /// <summary>
    /// Whether the flow's route registers files for the records it delivers: the record's own files, or the files of the
    /// dataset it registers the record as. A workflow's inputs are datasets of their own and do not count.
    /// </summary>
    private static bool RegistersFiles(FlowDefinition flow) => flow.Target.Protocol switch
    {
        DeliveryProtocol.File or DeliveryProtocol.Dataset or DeliveryProtocol.FileAndDdms => true,
        DeliveryProtocol.Manifest => PayloadParts.Streamed(flow) is not null,
        DeliveryProtocol.ManifestAndDdms => flow.Source.Payloads.ContainsKey(PayloadParts.Files),
        DeliveryProtocol.Workflow => flow.Target.Workflow?.Anchor == WorkflowAnchor.Dataset || flow.Source.Payloads.ContainsKey(PayloadParts.Files),
        _ => false,
    };
}
