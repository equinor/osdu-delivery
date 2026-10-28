using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Binds a delivery flow to the enrichments its mapping declares (osdu/docs/reference/flow/enrich.md). The mapping says what
/// is added to each record: each enrichment's rows, the enrichment mapping it renders (paths of the record's own kind, or
/// records of any other kind), and where the record links the records it adds (<c>$link</c>). The flow says where those rows and files are (<c>source.datasets.&lt;name&gt;</c> and its
/// <c>files</c>) and which workflow runs over them (<c>workflows.&lt;name&gt;</c>). Binding joins the two into the flow's
/// <see cref="FlowDefinition.Enrich"/>, which every part of a run reads, and refuses what the two cannot do together, each
/// message naming the flow and the mapping's key.
/// </summary>
public static class EnrichBinding
{
    /// <summary>The flow bound to the enrichments of the mapping it pins, read with <paramref name="mappings"/>.</summary>
    public static FlowDefinition Bind(FlowDefinition flow, MappingCatalog mappings)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mappings);
        return Bind(flow, mappings.Load(flow.Render.Mapping), mappings.Load);
    }

    /// <summary>
    /// The flow bound to the enrichments <paramref name="mapping"/> declares, each enrichment mapping read with
    /// <paramref name="load"/>. A flow bound already is returned as it is, so binding twice changes nothing.
    /// </summary>
    public static FlowDefinition Bind(FlowDefinition flow, MappingDefinition mapping, Func<string, MappingDefinition> load)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(load);
        if (flow.Enrich is not null)
        {
            return flow;
        }

        var where = KeyPaths.Where(flow);
        var paths = KeyPaths.Of(flow);
        foreach (var name in flow.EnrichWorkflows.Keys.Where(name => !mapping.Enrich.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
        {
            throw new FlowValidationException(mapping.Enrich.Count == 0
                ? $"{where}: {paths.Workflows}.{name} runs a workflow over the records of an enrichment '{name}', and the mapping {mapping.Reference} declares no enrichment; declare it under the mapping's enrich:, or remove the workflow."
                : $"{where}: {paths.Workflows}.{name} runs a workflow over the records of an enrichment '{name}', which the mapping {mapping.Reference} does not declare; it declares {string.Join(", ", mapping.Enrich.Select(e => e.Name))}.");
        }

        foreach (var (name, _) in flow.Source.Datasets.Where(d => d.Value.Files is not null && !mapping.Enrich.Any(e => e.Name.Equals(d.Key, StringComparison.Ordinal))))
        {
            throw new FlowValidationException(
                $"{where}: {paths.Datasets}.{name}.files names the files of each row of {name}, and no enrichment of the mapping {mapping.Reference} reads those rows to register them or send them as the record's bulk data; declare enrich.{name} in the mapping, or remove files.");
        }

        // The loader lets a ddms flow name the payload set bulk before any is declared, for the bulk data an enrichment names.
        if (flow.Target.ProtocolOptions.Payload == PayloadParts.Bulk && !flow.Source.Payloads.ContainsKey(PayloadParts.Bulk) && !mapping.Enrich.Any(e => e.Bulk))
        {
            throw new FlowValidationException(
                $"{where}: target.protocolOptions.payload names '{PayloadParts.Bulk}', and the flow declares no payload set of that name, nor does any enrichment of the mapping {mapping.Reference} name the record's bulk data (bulk: true); declare one, or remove target.protocolOptions.payload.");
        }

        if (mapping.Enrich.Count == 0)
        {
            return flow;
        }

        // An enrichment adds to an OSDU record; a DSPDM row and an Energistics object are written whole by routes of their own.
        if (flow.Target.Protocol is DeliveryProtocol.Dspdm or DeliveryProtocol.Etp)
        {
            throw new FlowValidationException(
                $"{where}: the mapping {mapping.Reference} declares enrich:, which adds to the OSDU records the flow renders, and the {DeliveryProtocols.Name(flow.Target.Protocol)} route writes "
                + (flow.Target.Protocol == DeliveryProtocol.Dspdm ? "rows of DSPDM business objects" : "Energistics objects into a dataspace of the Reservoir DDMS")
                + ", which no enrichment extends. Map what the enrichments would add in the mapping itself.");
        }

        var steps = new List<EnrichStep>(mapping.Enrich.Count);
        foreach (var enrichment in mapping.Enrich)
        {
            steps.Add(Step(flow, mapping, enrichment, load, where, paths));
        }

        foreach (var name in mapping.LinkNames())
        {
            var linked = steps.First(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (linked.Records is null)
            {
                throw new FlowValidationException(
                    $"{where}: the mapping {mapping.Reference} links the records of enrich.{linked.Name} with $link, and that enrichment adds none: its mapping fills the record's own kind, so it adds paths to the record itself.");
            }
        }

        // The Dataset and storage services are reached under the flow's endpoint, which a ddms flow without ddmsRoot points at
        // the DDMS itself.
        if (steps.Any(s => s.Records is not null) && DeliveryProtocols.ReachesDdms(flow.Target.Protocol) && flow.Interface is null
            && string.IsNullOrWhiteSpace(flow.Target.ProtocolOptions.DdmsRoot) && flow.Target.Ddms.Count == 0)
        {
            var adding = steps.First(s => s.Records is not null);
            throw new FlowValidationException(
                $"{where}: enrich.{adding.Name} of the mapping {mapping.Reference} adds records through the platform's Dataset and storage services, which a flow reaches under its endpoint, and this flow's endpoint is the DDMS itself (no target.protocolOptions.ddmsRoot, no target.ddms). Make the endpoint the platform and name the DDMS under it with ddmsRoot.");
        }

        return flow with { Enrich = steps, Source = WithBulk(flow, mapping, steps, where, paths) };
    }

    /// <summary>
    /// One enrichment bound: its rows, the flow's child dataset of its name; what its mapping does with them, which the
    /// mapping's kind says (the record's own kind adds paths, any other kind adds a record of its own for each row, a dataset
    /// registered with the files the row names when the flow binds them); the record's bulk data its row names; and the
    /// workflow the flow runs over it.
    /// </summary>
    private static EnrichStep Step(
        FlowDefinition flow, MappingDefinition mapping, MappingEnrichment enrichment, Func<string, MappingDefinition> load, string where, KeyPaths paths)
    {
        var at = $"{where}: enrich.{enrichment.Name} of the mapping {mapping.Reference}";
        FlowSourceDataset? rows = null;
        if (enrichment.Rows is not null)
        {
            rows = flow.Source.Datasets.GetValueOrDefault(enrichment.Name)
                ?? throw new FlowValidationException(
                    $"{at} reads the rows of the child dataset {enrichment.Name}, which the flow does not bind; declare {paths.Datasets}.{enrichment.Name} with the table holding them (object) and how they join the record table (join).");
        }

        if (mapping.ChildDatasets.FirstOrDefault(c => c.Equals(enrichment.Name, StringComparison.OrdinalIgnoreCase)) is { } repeated)
        {
            throw new FlowValidationException(
                $"{at}: the mapping repeats the rows of {repeated} with $forEach, and they are the enrichment's own rows; read them in the enrichment's mapping, so what they add is the enrichment's.");
        }

        string? fragment = null;
        EnrichRecords? records = null;
        if (enrichment.Mapping is { } reference)
        {
            var rendered = load(reference);
            if (!rendered.Fragment)
            {
                throw new FlowValidationException(
                    $"{at} names {rendered.Reference}, a mapping that keys records of its own; an enrichment renders an enrichment mapping (enrichment: true), which renders paths of the record or the records it adds to it, and declares no dataset.");
            }

            if (string.Equals(rendered.Kind, mapping.Kind, StringComparison.Ordinal))
            {
                fragment = reference;
            }
            else
            {
                records = Records(enrichment, rendered, rows, enrichment.Bulk, at, paths);
            }
        }

        if (enrichment.Key.Count > 0 && records is null)
        {
            throw new FlowValidationException(
                $"{at}: key tells apart the records an enrichment adds from many rows, and this one adds none; remove key.");
        }

        FlowPayload? bulk = null;
        if (enrichment.Bulk)
        {
            if (records is { RegistersFiles: true })
            {
                throw new FlowValidationException(
                    $"{at} registers the files its rows name with the datasets it adds and names them as the record's bulk data (bulk: true); one enrichment does one of them with its files, so declare two, each reading rows of its own.");
            }

            bulk = (rows!.Files ?? throw new FlowValidationException(
                $"{at} names the record's bulk data by its row (bulk: true), and the flow binds no files for {enrichment.Name}; declare {paths.Datasets}.{enrichment.Name}.files with where each row's bulk data is (root, locationColumn, pattern, hashColumn).")) with
            {
                Step = enrichment.Name,
                ContentType = null,
            };
        }

        if (rows?.Files is not null && records is not { RegistersFiles: true } && bulk is null)
        {
            throw new FlowValidationException(
                $"{where}: {paths.Datasets}.{enrichment.Name}.files names the files of each row, and enrich.{enrichment.Name} of the mapping {mapping.Reference} neither registers them (with a mapping of a dataset kind) nor names them as the record's bulk data (bulk: true); remove files, or say in the mapping what they become.");
        }

        return new EnrichStep
        {
            Name = enrichment.Name,
            Description = enrichment.Description,
            Mapping = fragment,
            Records = records,
            After = enrichment.After,
            ReadsRows = enrichment.Rows is not null,
            Rows = enrichment.Rows ?? EnrichRows.One,
            OnHold = enrichment.OnHold,
            Bulk = bulk,
            Workflow = flow.EnrichWorkflows.FirstOrDefault(w => w.Key.Equals(enrichment.Name, StringComparison.OrdinalIgnoreCase)).Value,
        };
    }

    /// <summary>
    /// The records an enrichment whose mapping fills another kind than the record's adds: one for each row (one for the
    /// record, for an enrichment reading none), under ids its key tells apart for an enrichment taking many rows. A dataset
    /// kind whose rows the flow binds files for registers them with each dataset through the Dataset service, and needs the
    /// column holding their hash; every other record, a dataset whose rows name no files among them, is written through
    /// storage as it renders. Files belong to a dataset alone (and to the record's bulk data, which <paramref name="bulk"/> names).
    /// </summary>
    private static EnrichRecords Records(MappingEnrichment enrichment, MappingDefinition rendered, FlowSourceDataset? rows, bool bulk, string at, KeyPaths paths)
    {
        var entityType = TargetId.EntityTypeFromKind(rendered.Kind);
        var registers = DatasetService.IsDatasetType(entityType) && !EdsRecordRules.IsProxyDataset(entityType);
        // Files a dataset kind's rows name are registered with its datasets, which the caller refuses beside bulk data; files
        // the rows of another kind name are the record's bulk data when it names them so, and refused otherwise.
        FlowPayload? files = null;
        if (rows?.Files is { } bound && (registers || !bulk))
        {
            if (!registers)
            {
                throw new FlowValidationException(
                    $"{at}: {paths.Datasets}.{enrichment.Name}.files names files for each row, and {rendered.Reference} fills {rendered.Kind}, which the Dataset service registers no files with; files go with a dataset kind (such as osdu:wks:dataset--File.Generic:1.0.0). Remove files to write the records through storage as they render.");
            }

            if (bound.HashColumn is null)
            {
                throw new FlowValidationException(
                    $"{at}: {paths.Datasets}.{enrichment.Name}.files.hashColumn names the column holding the content hash of each row's files, which says when they are registered again; a dataset goes again only when it moves.");
            }

            files = bound;
        }

        if (enrichment.Rows == EnrichRows.Many && enrichment.Key.Count == 0)
        {
            throw new FlowValidationException(
                $"{at}: key names the columns telling each row's record from the enrichment's other rows' (such as key: [file_name]), and one taking many rows needs them, since every row adds a record of its own under an id they make.");
        }

        return new EnrichRecords
        {
            Files = files,
            Kind = rendered.Kind,
            Key = enrichment.Key,
            Mapping = rendered.Reference,
        };
    }

    /// <summary>
    /// The flow's source with the record's bulk data an enrichment names (osdu/docs/reference/flow/enrich-bulk.md): the payload
    /// set <c>bulk</c>, read from the enrichment's row. One enrichment supplies it, on the <c>ddms</c> route, for a flow whose
    /// record row names no payload of its own.
    /// </summary>
    private static FlowSource WithBulk(FlowDefinition flow, MappingDefinition mapping, IReadOnlyList<EnrichStep> steps, string where, KeyPaths paths)
    {
        var suppliers = steps.Where(s => s.Bulk is not null).ToList();
        if (suppliers.Count == 0)
        {
            return flow.Source;
        }

        if (suppliers.Count > 1)
        {
            throw new FlowValidationException(
                $"{where}: the enrichments {string.Join(", ", suppliers.Select(s => s.Name))} of the mapping {mapping.Reference} each name the record's bulk data, and a record has one set of it; let one of them name it.");
        }

        var supplier = suppliers[0];
        if (flow.Target.Protocol != DeliveryProtocol.Ddms)
        {
            throw new FlowValidationException(
                $"{where}: enrich.{supplier.Name} of the mapping {mapping.Reference} names the record's DDMS bulk data, which the ddms route sends as the record's payload, and this flow's route is {DeliveryProtocols.Name(flow.Target.Protocol)}. "
                + "A route that sends files and bulk data as parts (fileAndDdms, manifestAndDdms) reads both from the record's row under source.payloads; another route sends no bulk data.");
        }

        if (flow.Source.Payloads.Count > 0)
        {
            throw new FlowValidationException(
                $"{where}: enrich.{supplier.Name} of the mapping {mapping.Reference} names the record's bulk data, and {string.Join(", ", flow.Source.Payloads.Keys.Select(paths.Payload))} names its payload already; the record's payload comes from one place, so declare it once.");
        }

        if (flow.Target.ProtocolOptions.Payload is { } named && !string.Equals(named, PayloadParts.Bulk, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{where}: target.protocolOptions.payload names '{named}', and enrich.{supplier.Name} of the mapping {mapping.Reference} supplies the payload set {PayloadParts.Bulk}; remove target.protocolOptions.payload, or name {PayloadParts.Bulk}.");
        }

        return flow.Source with
        {
            Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) { [PayloadParts.Bulk] = supplier.Bulk! },
        };
    }
}
