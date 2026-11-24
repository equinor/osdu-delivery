using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

// The route an interface is delivered by (osdu/docs/reference/flow/routes.md, An interface's route), resolved from
// what its records carry or named outright, and the route names target.protocol takes, with the retired ones mapped
// to the route each became.
internal static partial class FlowMapper
{
    /// <summary>How an interface is delivered, the payload set its route sends (null for none, or for a route that sends parts), and why.</summary>
    private sealed record InterfaceRoute(DeliveryProtocol Protocol, string? Payload, string Reason);

    /// <summary>
    /// The route of an interface, from what its records carry (osdu/docs/reference/flow/routes.md, An interface's route):
    /// nothing beside the record goes through the storage service, files through the file service before the record, DDMS
    /// bulk data through a DDMS after the record, both through the composed route, and a workflow declaration through the
    /// workflow route. <c>route:</c> names one outright; a named route and a part only another route sends make the
    /// composed route of the two (manifest or file with bulk data, ddms with files), and a route that cannot deliver what
    /// the interface declares is refused, so nothing it declares is silently left unsent.
    /// </summary>
    private static InterfaceRoute ResolveRoute(InterfaceYaml i, string at, string source)
    {
        var files = i.Files is not null;
        var bulk = i.Bulk is not null;
        var workflow = i.Workflow is not null;
        var named = string.IsNullOrWhiteSpace(i.Route) ? null : (DeliveryProtocol?)ParseEnum<DeliveryProtocol>(i.Route!.Trim(), at + ".route", source);
        var filesKey = $"{at}.{FilesPayload}";
        var bulkKey = $"{at}.{BulkPayload}";
        var workflowKey = $"{at}.workflow";

        void Refuse(bool refused, string why)
        {
            if (refused)
            {
                throw new FlowValidationException($"{source}: {why}");
            }
        }

        Refuse(
            workflow && named is not (null or DeliveryProtocol.Workflow),
            $"{at}.route is {i.Route?.Trim()}, and {workflowKey} declares a workflow, which only the workflow route runs. Remove {at}.route, or name workflow.");
        switch (named)
        {
            case DeliveryProtocol.Storage:
                Refuse(files || bulk, $"{at}.route is storage, which writes the record alone, so {(files ? filesKey : bulkKey)} would never be sent. Remove it, or choose the route that delivers it.");
                return new InterfaceRoute(DeliveryProtocol.Storage, null, $"{at}.route names the storage route: each record is written through the storage service");
            case DeliveryProtocol.File when bulk:
            case DeliveryProtocol.Ddms when files:
            case DeliveryProtocol.FileAndDdms:
                Refuse(!files || !bulk, $"{at}.route is fileAndDdms, which registers each record's files and writes its bulk data through its DDMS, and the interface declares no {(files ? bulkKey : filesKey)}.");
                return new InterfaceRoute(
                    DeliveryProtocol.FileAndDdms,
                    null,
                    $"{at} is delivered by the fileAndDdms route ("
                    + (named == DeliveryProtocol.FileAndDdms ? $"{at}.route names it" : $"{at}.route names {i.Route!.Trim()} and the interface declares both {FilesPayload} and {BulkPayload}")
                    + "): each record's files are uploaded and registered through the file service, the record is written through its DDMS referring to them, then its bulk data");
            case DeliveryProtocol.File:
                Refuse(!files, $"{at}.route is file, which uploads and registers each record's files, and the interface declares none under {filesKey}.");
                return new InterfaceRoute(DeliveryProtocol.File, FilesPayload, $"{at}.route names the file route: each record's files are uploaded and registered through the file service before the record is written");
            case DeliveryProtocol.Dataset:
                Refuse(!files, $"{at}.route is dataset, which stores each record's files and registers them through the dataset service, and the interface declares none under {filesKey}.");
                Refuse(bulk, $"{at}.route is dataset, which writes no DDMS bulk data, so {bulkKey} would never be sent.");
                return new InterfaceRoute(
                    DeliveryProtocol.Dataset,
                    FilesPayload,
                    $"{at}.route names the dataset route: each record's files are stored where the dataset service says and registered with it, a record of a dataset kind as that dataset");
            case DeliveryProtocol.Manifest when bulk:
            case DeliveryProtocol.ManifestAndDdms:
                Refuse(!bulk, $"{at}.route is manifestAndDdms, which writes each record's bulk data through its DDMS after the manifest, and the interface declares none under {bulkKey}.");
                return new InterfaceRoute(
                    DeliveryProtocol.ManifestAndDdms,
                    null,
                    $"{at} is delivered by the manifestAndDdms route ("
                    + (named == DeliveryProtocol.ManifestAndDdms ? $"{at}.route names it" : $"{at}.route names manifest and the interface declares {BulkPayload}")
                    + "): the records go through the ingestion workflow in manifests" + (files ? ", their files registered first," : string.Empty) + " then each record's bulk data through its DDMS");
            case DeliveryProtocol.Manifest:
                return new InterfaceRoute(
                    DeliveryProtocol.Manifest,
                    files ? FilesPayload : null,
                    $"{at}.route names the manifest route: the records go through the ingestion workflow in manifests" + (files ? ", their files registered first" : string.Empty));
            case DeliveryProtocol.Ddms:
                return new InterfaceRoute(
                    DeliveryProtocol.Ddms,
                    bulk ? BulkPayload : null,
                    $"{at}.route names the ddms route: each record is written through its DDMS" + (bulk ? ", then its bulk data" : string.Empty));
            case DeliveryProtocol.Dspdm:
                Refuse(files || bulk, $"{at}.route is dspdm, which saves business object rows alone, so {(files ? filesKey : bulkKey)} would never be sent. Remove it, or choose the route that delivers it.");
                return new InterfaceRoute(
                    DeliveryProtocol.Dspdm,
                    null,
                    $"{at}.route names the dspdm route: each record is a row of a Production DDMS business object, found again by its unique key and saved through DSPDM");
            case DeliveryProtocol.Etp:
                // Only named: an Energistics object is told from an OSDU record by its kind, not by what beside it the
                // interface declares. Its XML and its arrays are optional parts, since a mapping can render either into
                // the document instead.
                return new InterfaceRoute(
                    DeliveryProtocol.Etp,
                    null,
                    $"{at}.route names the etp route: each record is an Energistics object written over ETP 1.2 into a dataspace of the Reservoir DDMS"
                    + (files && bulk ? ", its XML and its arrays sent with it" : files ? ", its XML sent with it" : bulk ? ", its arrays sent with it" : string.Empty));
            case DeliveryProtocol.Workflow:
                Refuse(!workflow, $"{at}.route is workflow, and the interface declares no workflow under {workflowKey}.");
                Refuse(bulk, $"{at}.route is workflow, which writes no DDMS bulk data, so {bulkKey} would never be sent.");
                return new InterfaceRoute(DeliveryProtocol.Workflow, null, $"{at}.route names the workflow route: {WorkflowReason(i)}");
        }

        if (workflow)
        {
            Refuse(bulk, $"{workflowKey} declares a workflow, and the workflow route writes no DDMS bulk data, so {bulkKey} would never be sent.");
            return new InterfaceRoute(DeliveryProtocol.Workflow, null, $"{at} declares a workflow, so it goes by the workflow route: {WorkflowReason(i)}");
        }

        if (files && bulk)
        {
            return new InterfaceRoute(
                DeliveryProtocol.FileAndDdms,
                null,
                $"{at} declares {FilesPayload} and {BulkPayload}, so each record's files are uploaded and registered through the file service, the record is written through its DDMS referring to them, then its bulk data");
        }

        if (files)
        {
            return new InterfaceRoute(DeliveryProtocol.File, FilesPayload, $"{at} declares {FilesPayload}, so each record's files are uploaded and registered through the file service before the record is written through the storage service");
        }

        if (bulk)
        {
            return new InterfaceRoute(DeliveryProtocol.Ddms, BulkPayload, $"{at} declares {BulkPayload}, so each record is written through its DDMS and its bulk data after it");
        }

        return new InterfaceRoute(DeliveryProtocol.Storage, null, $"{at} declares no {FilesPayload} and no {BulkPayload}, so each record is written through the storage service");
    }

    /// <summary>What the workflow route does for an interface, in the words its route reason gives.</summary>
    private static string WorkflowReason(InterfaceYaml i)
    {
        var stages = (i.Workflow?.Stages ?? []).Select(s => s?.Workflow?.Trim()).OfType<string>().ToList();
        var anchor = AnchorOf(i.Workflow?.Anchor, i.Files is not null) == WorkflowAnchor.Storage
            ? "each record is written through the storage service"
            : "each record is registered with its files through the dataset service";
        return $"{anchor}, its inputs registered, then {(stages.Count == 0 ? "its workflow" : string.Join(" and then ", stages))} run and what it created read back";
    }

    /// <summary>How the workflow route writes the record: as declared, or as a dataset when the record carries files and as a storage record otherwise.</summary>
    private static WorkflowAnchor AnchorOf(string? declared, bool files)
    {
        if (string.Equals(declared?.Trim(), "storage", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowAnchor.Storage;
        }

        if (string.Equals(declared?.Trim(), "dataset", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowAnchor.Dataset;
        }

        return files ? WorkflowAnchor.Dataset : WorkflowAnchor.Storage;
    }

    /// <summary>
    /// The route names retired when the routes took the names they carry everywhere else, mapped to the route each one
    /// named, so flow documents written before that keep loading. The current spelling of every one of them is the route
    /// name itself (<c>osduRecord</c> was the storage route, <c>osduWellLog</c> the ddms route serving every DDMS).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DeliveryProtocol> LegacyProtocolNames =
        new Dictionary<string, DeliveryProtocol>(StringComparer.OrdinalIgnoreCase)
        {
            ["osduRecord"] = DeliveryProtocol.Storage,
            ["osduWellLog"] = DeliveryProtocol.Ddms,
            ["osduFile"] = DeliveryProtocol.File,
            ["osduManifest"] = DeliveryProtocol.Manifest,
            ["osduDataset"] = DeliveryProtocol.Dataset,
            ["osduFileAndDdms"] = DeliveryProtocol.FileAndDdms,
            ["osduManifestAndDdms"] = DeliveryProtocol.ManifestAndDdms,
            ["osduWorkflow"] = DeliveryProtocol.Workflow,
            ["osduDspdm"] = DeliveryProtocol.Dspdm,
            ["osduEtp"] = DeliveryProtocol.Etp,
        };

    /// <summary>
    /// The route <c>target.protocol</c> names: a route name (<c>storage</c>, <c>file</c>, <c>dataset</c>,
    /// <c>manifest</c>, <c>ddms</c>, <c>fileAndDdms</c>, <c>manifestAndDdms</c>, <c>workflow</c>, <c>dspdm</c>,
    /// <c>etp</c>), or one of the names those routes carried before (<see cref="LegacyProtocolNames"/>).
    /// </summary>
    private static DeliveryProtocol ParseProtocol(string value, string source)
    {
        foreach (var route in Enum.GetValues<DeliveryProtocol>())
        {
            if (string.Equals(value, route.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return route;
            }
        }

        if (LegacyProtocolNames.TryGetValue(value, out var retired))
        {
            return retired;
        }

        throw new FlowValidationException(
            $"{source}: 'target.protocol' value '{value}' is not one of {string.Join(", ", Enum.GetNames<DeliveryProtocol>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.");
    }
}
