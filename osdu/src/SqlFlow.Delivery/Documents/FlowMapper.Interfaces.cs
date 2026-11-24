using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

// A delivery flow document in the interface form (osdu/docs/reference/flow/interfaces.md): each interface's view of
// the source mapped as a flow, with the source's shared blocks and its own settings laid over them, what belongs to an
// interface refused when the source declares it, and the order the interfaces wait for each other in.
internal static partial class FlowMapper
{
    /// <summary>
    /// The interfaces of a document in the interface form, in document order: each one's view of the source mapped as a
    /// flow, its route resolved from what it declares, its ledger identity, and what it waits for. Refuses what belongs to
    /// an interface written at the source level, two interfaces sharing a ledger identity, and interfaces that wait for
    /// each other.
    /// </summary>
    private static List<FlowDefinition> MapInterfaces(FlowYaml y, string name, string source, IReadOnlyList<DeclaredPartition> partitions)
    {
        var interfaces = y.Interfaces!;
        var src = y.Source ?? throw Missing("source", source);
        var target = y.Target ?? throw Missing("target", source);
        RefuseAtSourceLevel(src, y.Render, target, y.Ledger, y.Enrich, y.Workflows, source);
        if (y.Render is { } shared)
        {
            // What the source declares for every interface is checked once, whatever the interfaces make of it.
            MapCacheVersion(shared, source);
        }

        if (interfaces.Count == 0)
        {
            throw new FlowValidationException($"{source}: interfaces lists no interface. List each OSDU type the source delivers under it, or write the document in the single form.");
        }

        if (interfaces.Count > SourceDefinition.MaxInterfaces)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: interfaces lists {interfaces.Count} interfaces; one document declares at most {SourceDefinition.MaxInterfaces}. Split the source into several documents."));
        }

        var flows = new List<FlowDefinition>(interfaces.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, declared) in interfaces)
        {
            var interfaceName = key ?? string.Empty;
            if (!SourceDefinition.IsInterfaceName(interfaceName))
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: interfaces names an interface '{interfaceName}'; an interface name is a letter followed by letters, digits, '_' and '-', at most {SourceDefinition.MaxInterfaceNameLength} characters."));
            }

            if (!names.Add(interfaceName))
            {
                throw new FlowValidationException($"{source}: interfaces declares '{interfaceName}' more than once (interface names are compared ignoring case, as the ledger identities made from them are).");
            }

            var at = $"interfaces.{interfaceName}";
            var i = declared ?? throw new FlowValidationException($"{source}: {at} declares nothing; an interface needs at least its record and its mapping.");
            RefuseMoved(
                i.Reliability, $"{at}.reliability", $"{at}.parallelism", was => was == "parallelInterfaces" ? null : DeliveryRename(was),
                _ => "how many interfaces run at once is the source's setting, parallelism.interfacesAtOnce. Set it there.", source);
            if (i.Parallelism is { } own && (own.InterfacesAtOnce is not null || own.Interfaces is not null))
            {
                var written = own.InterfacesAtOnce is not null ? "interfacesAtOnce" : "interfaces";
                throw new FlowValidationException(
                    $"{source}: {at}.parallelism.{written} is the source's setting: how many of its interfaces run at once. Set it under parallelism as interfacesAtOnce.");
            }

            RefuseRenamed(i.Parallelism, $"{at}.parallelism", source);

            var paths = KeyPaths.ForInterface(interfaceName);
            var route = ResolveRoute(i, at, source);
            var flow = MapFlow(InterfaceView(y, i, route), source, paths, partitions) with
            {
                Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim(),
                AdoptedLedger = MapLedger(i.Ledger, $"{at}.ledger", "the interface", source),
                After = (i.After ?? []).Select(a => a?.Trim() ?? string.Empty).ToList(),
                RouteReason = route.Reason,
            };

            if (DeliveryProtocols.ReachesDdms(flow.Target.Protocol) && flow.Target.ProtocolOptions is { DdmsRoot: null, RecordPath: null } && flow.Target.Ddms.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: {at} is delivered through a DDMS, and the source's endpoint is the platform every interface reaches its services under. "
                    + $"Say where the DDMS is under it: declare it under target.ddms with its root, or set target.protocolOptions.ddmsRoot or {at}.protocolOptions.ddmsRoot "
                    + "for the Wellbore DDMS (usually deployed under /api/os-wellbore-ddms).");
            }

            flows.Add(flow);
        }

        if (target.Dspdm is not null && !flows.Any(f => f.Target.Protocol == DeliveryProtocol.Dspdm))
        {
            throw new FlowValidationException(
                $"{source}: target.dspdm declares the Production DDMS core service the dspdm route writes rows to, and no interface is delivered by it (route: dspdm). "
                + "Remove target.dspdm, or route the interfaces it is meant for through it.");
        }

        if (target.Etp is not null && !flows.Any(f => f.Target.Protocol == DeliveryProtocol.Etp))
        {
            throw new FlowValidationException(
                $"{source}: target.etp declares the Reservoir DDMS the etp route writes Energistics objects to, and no interface is delivered by it (route: etp). "
                + "Remove target.etp, or route the interfaces it is meant for through it.");
        }

        if (!string.IsNullOrWhiteSpace(target.VerifyReferences) && flows.All(f => f.Target.Protocol == DeliveryProtocol.Dspdm))
        {
            throw new FlowValidationException(
                $"{source}: target.verifyReferences checks the references of the records sent through OSDU's storage service, and every interface of the "
                + "source is delivered by the dspdm route, whose rows refer to no storage record. Remove target.verifyReferences.");
        }

        if (target.Ddms is not null && !flows.Any(f => DeliveryProtocols.ReachesDdms(f.Target.Protocol)))
        {
            throw new FlowValidationException(
                $"{source}: target.ddms declares the DDMSs the source's interfaces are delivered to, and no interface is delivered through one "
                + "(an interface with a bulk part, or route: ddms). Remove target.ddms, or route the interfaces it is meant for through their DDMS.");
        }

        CheckAfter(flows, source);
        CheckLedgers(flows, source);
        CheckPartitionLedgers(flows, source);
        return flows;
    }

    /// <summary>What belongs to an interface, written at the source level, is refused rather than applied to every interface.</summary>
    private static void RefuseAtSourceLevel(
        FlowSourceYaml src, FlowRenderYaml? render, FlowTargetYaml target, string? ledger, object? enrich, OrderedDictionary<string, WorkflowYaml?>? workflows, string source)
    {
        var misplaced = new List<string>();
        if (ledger is not null)
        {
            misplaced.Add("ledger (each interface keeps a ledger of its own, <flow>/<interface>; one that keeps an earlier ledger names it as interfaces.<name>.ledger)");
        }

        if (enrich is not null)
        {
            misplaced.Add("enrich (what is added to an interface's records is declared in the enrich: block of its mapping, and its rows are bound under interfaces.<name>.datasets)");
        }

        if (workflows is not null)
        {
            misplaced.Add("workflows (the workflows run over an interface's enrichments are interfaces.<name>.workflows, since each interface's mapping declares enrichments of its own)");
        }

        if (src.Record is not null)
        {
            misplaced.Add("source.record (each interface's record table is interfaces.<name>.record)");
        }

        if (src.Datasets is not null)
        {
            misplaced.Add("source.datasets (each interface's child tables are interfaces.<name>.datasets)");
        }

        if (src.Payloads is not null)
        {
            misplaced.Add($"source.payloads (an interface's files are interfaces.<name>.{FilesPayload}, its DDMS bulk data interfaces.<name>.{BulkPayload})");
        }

        if (!string.IsNullOrWhiteSpace(render?.Mapping))
        {
            misplaced.Add("render.mapping (each interface pins its own mapping as interfaces.<name>.mapping)");
        }

        if (!string.IsNullOrWhiteSpace(target.Protocol))
        {
            misplaced.Add("target.protocol (each interface's route follows from what it declares, or is named with interfaces.<name>.route)");
        }

        if (!string.IsNullOrWhiteSpace(target.ProtocolOptions?.Payload))
        {
            misplaced.Add("target.protocolOptions.payload (an interface's route sends its own files or bulk data)");
        }

        if (target.Workflow is not null)
        {
            misplaced.Add("target.workflow (the workflow an interface runs is interfaces.<name>.workflow)");
        }

        if (misplaced.Count > 0)
        {
            throw new FlowValidationException($"{source}: the document declares interfaces, so these belong to an interface rather than the source: {string.Join("; ", misplaced)}.");
        }
    }

    /// <summary>
    /// One interface as a document in the single form: the source's shared blocks with the interface's own settings laid
    /// over them, its record table, child tables and payload parts as the source's, and the route it resolved to as the
    /// target's protocol.
    /// </summary>
    private static FlowYaml InterfaceView(FlowYaml y, InterfaceYaml i, InterfaceRoute route)
    {
        var src = y.Source!;
        var target = y.Target!;
        var payloads = new Dictionary<string, FlowPayloadYaml>(StringComparer.Ordinal);
        if (i.Files is not null)
        {
            payloads[FilesPayload] = i.Files;
        }

        if (i.Bulk is not null)
        {
            payloads[BulkPayload] = i.Bulk;
        }

        var options = YamlOverlay.Apply(target.ProtocolOptions, i.ProtocolOptions) ?? new ProtocolOptionsYaml();
        options.Payload = route.Payload;

        // Where a DDMS sits under the endpoint is a setting of the interfaces delivered through one; an interface delivered
        // another way leaves the source's value alone rather than being refused for it.
        if (!DeliveryProtocols.ReachesDdms(route.Protocol))
        {
            options.DdmsRoot = i.ProtocolOptions?.DdmsRoot;
        }

        var render = y.Render ?? new FlowRenderYaml();
        var parameters = new Dictionary<string, string>(render.Parameters ?? [], StringComparer.Ordinal);
        foreach (var (key, value) in i.Render?.Parameters ?? [])
        {
            parameters[key] = value;
        }

        return new FlowYaml
        {
            FlowType = y.FlowType,
            Name = y.Name,
            Batch = y.Batch,
            Parameters = y.Parameters,
            Source = new FlowSourceYaml
            {
                Connection = src.Connection,
                Record = i.Record,
                Datasets = i.Datasets,
                Payloads = payloads,
                LastModified = string.IsNullOrWhiteSpace(i.LastModified) ? src.LastModified : i.LastModified,
                SystemColumns = OverlaySystemColumns(src.SystemColumns, i.SystemColumns),
                Incremental = YamlOverlay.Apply(src.Incremental, i.Incremental),
                Work = src.Work,
            },
            Render = new FlowRenderYaml
            {
                Mapping = i.Mapping,
                CacheVersion = string.IsNullOrWhiteSpace(i.Render?.CacheVersion) ? render.CacheVersion : i.Render.CacheVersion,
                Parameters = parameters,
                Mappings = render.Mappings,
            },
            Change = YamlOverlay.Apply(y.Change, i.Change),
            Target = new FlowTargetYaml
            {
                Endpoint = target.Endpoint,
                Auth = target.Auth,
                Headers = target.Headers,
                Protocol = route.Protocol.ToString(),
                ProtocolOptions = options,

                // The DDMSs the source declares are where its ddms interfaces go; the other interfaces have no use for them.
                Ddms = DeliveryProtocols.ReachesDdms(route.Protocol) ? target.Ddms : null,
                Workflow = i.Workflow,
                Airflow = target.Airflow,
                Eds = target.Eds,

                // The DSPDM the source declares is where its dspdm interfaces go; the other interfaces have no use for it.
                Dspdm = route.Protocol == DeliveryProtocol.Dspdm ? target.Dspdm : null,

                // The Reservoir DDMS the source declares is where its etp interfaces go.
                Etp = route.Protocol == DeliveryProtocol.Etp ? target.Etp : null,

                // A DSPDM row refers to no storage record, so a dspdm interface has nothing for the storage check to read.
                VerifyReferences = route.Protocol == DeliveryProtocol.Dspdm ? null : target.VerifyReferences,

                // What the gate does with a verdict: the source's setting, with what the interface says laid over it.
                Validation = YamlOverlay.Apply(target.Validation, i.Validation),
            },
            Reliability = YamlOverlay.Apply(y.Reliability, i.Reliability),
            Parallelism = YamlOverlay.Apply(y.Parallelism, i.Parallelism),
            Verify = YamlOverlay.Apply(y.Verify, i.Verify),
            FailWhen = YamlOverlay.Apply(y.FailWhen, i.FailWhen),

            // An interface delivers a kind of its own, so its cache steps replace the source's whole rather than merge.
            Cache = i.Cache ?? y.Cache,

            // An interface's enrichments are its mapping's, and the workflows run over them the interface's own: the source level
            // declares none.
            Enrich = i.Enrich,
            Workflows = i.Workflows,

            // A run delivers every interface of the document, so its operation code is the document's.
            Telemetry = y.Telemetry,
        };
    }

    /// <summary>The system columns an interface names over the ones its source names; a column opted out of stays opted out.</summary>
    private static FlowSystemColumnsYaml? OverlaySystemColumns(FlowSystemColumnsYaml? shared, FlowSystemColumnsYaml? over)
    {
        if (over is null || shared is null)
        {
            return over ?? shared;
        }

        var merged = new FlowSystemColumnsYaml();
        if (over.HasUpdated || shared.HasUpdated)
        {
            merged.Updated = over.HasUpdated ? over.Updated : shared.Updated;
        }

        if (over.HasFileName || shared.HasFileName)
        {
            merged.FileName = over.HasFileName ? over.FileName : shared.FileName;
        }

        if (over.HasRowNumber || shared.HasRowNumber)
        {
            merged.RowNumber = over.HasRowNumber ? over.RowNumber : shared.RowNumber;
        }

        if (over.HasDeleted || shared.HasDeleted)
        {
            merged.Deleted = over.HasDeleted ? over.Deleted : shared.Deleted;
        }

        if (over.HasInserted || shared.HasInserted)
        {
            merged.Inserted = over.HasInserted ? over.Inserted : shared.Inserted;
        }

        return merged;
    }

    /// <summary>Every <c>after:</c> names another interface of the document, once, and the interfaces wait for no cycle.</summary>
    private static void CheckAfter(IReadOnlyList<FlowDefinition> flows, string source)
    {
        var names = flows.Select(f => f.Interface!).ToList();
        foreach (var flow in flows)
        {
            var at = $"interfaces.{flow.Interface}.after";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var after in flow.After)
            {
                if (!names.Contains(after, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException($"{source}: {at} names '{after}', which is not an interface of this document; it declares {string.Join(", ", names)}.");
                }

                if (string.Equals(after, flow.Interface, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException($"{source}: {at} names the interface itself.");
                }

                if (!seen.Add(after))
                {
                    throw new FlowValidationException($"{source}: {at} names '{after}' more than once.");
                }
            }
        }

        var declared = flows.SelectMany(f => f.After.Select(a => new InterfaceDependency(f.Interface!, a, string.Empty)));
        if (InterfaceOrder.Cycle(names, declared) is { } cycle)
        {
            throw new FlowValidationException($"{source}: the interfaces {string.Join(" -> ", cycle)} wait for each other through after:, so none of them could run first.");
        }
    }
}
