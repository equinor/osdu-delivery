using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Ordering;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Templates;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a delivery flow contributes to SQLFlow's lineage (osdu/docs/reference/concepts/lineage.md, What each kind reads
/// and writes). It reads its record table and every child dataset table on the server its <c>source.connection</c> names,
/// so an ingestion flow writing those tables through the same reference is ordered before it. It reads the payload files
/// under the root of the payload set its protocol streams, and the mapping it pins, which is a node of its own
/// (osdu/docs/reference/concepts/lineage.md, The mapping is a node between the cache and the flow). It writes the OSDU
/// type its mapping fills in the partition it delivers to and, for the file and manifest protocols, the dataset kind it
/// registers each file as.
/// <para>
/// The mapping reads what a render reads through it: every partition cache type it names, every cache type holding
/// records of an entity type the ids it builds are checked against, and every OSDU kind its searches look in. Those are
/// reads of the mapping's node, so the graph draws each into the mapping and the mapping into the flow, and the flow
/// inherits them: the cache flow filling a type, and the flow delivering the records a search looks for, are ordered
/// before it.
/// </para>
/// The mapping is the one the flow pins, read from the checkout being scanned; the template it fills is read from the
/// template folders on the mapping's path in the same checkout (<see cref="MappingTemplateSource"/>). A mapping that cannot
/// be read costs the flow its OSDU nodes with a warning, never the rest of its lineage.
/// </summary>
public static class DeliveryLineage
{
    /// <summary>How many template variables a warning names before it counts the rest.</summary>
    private const int MaxNamedVariables = 5;

    /// <summary>The objects the flow reads, the record table first and the datasets in name order.</summary>
    public static IReadOnlyList<DeclaredDataObject> DeclaredObjects(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var source = flow.Source;
        var objects = new List<DeclaredDataObject>(1 + source.Datasets.Count)
        {
            DeclaredDataObject.Reads(source.Connection, source.Record.Object),
        };

        foreach (var (_, dataset) in source.Datasets.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            objects.Add(DeclaredDataObject.Reads(source.Connection, dataset.Object));
        }

        return objects;
    }

    /// <summary>
    /// Everything a source contributes (osdu/docs/reference/concepts/lineage.md, A source with interfaces): what each of
    /// its interfaces reads and writes, in document order, each declaration once however many interfaces make it. A source
    /// that names its partitions contributes for every one of them, each interface bound to the partition, so its OSDU,
    /// cache and mapping nodes are the partition's (osdu/docs/reference/concepts/lineage.md, The nodes the module adds);
    /// one that leaves them to the registry contributes once, under <see cref="PartitionNames.Every"/>.
    /// </summary>
    public static RegisteredFlowLineage Describe(
        SourceDefinition source,
        RegisteredLineageContext context,
        DeliveryDocumentLoader documents,
        MappingTemplateSource? templates = null,
        Func<IOrderCatalog?>? order = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        var scan = new Scan(context, documents, templates, order);

        // A source that leaves its partitions to the registry is described once, under the partition that stands for every
        // registered one: lineage is computed from the documents, and the registry is the catalog's.
        var described = (source.FollowsRegistry ? source.Interfaces : source.EveryLedger(RegisteredPartitions.None))
            .Select(i => Describe(i, scan))
            .ToList();
        return new RegisteredFlowLineage
        {
            Objects = described.SelectMany(d => d.Objects).Distinct().ToList(),
            Files = described.SelectMany(d => d.Files).Distinct().ToList(),
            Datasets = described.SelectMany(d => d.Datasets).Distinct().ToList(),

            // Two interfaces pinning one mapping for one partition read one node; what each says it reads adds up.
            Derivations = described.SelectMany(d => d.Derivations)
                .GroupBy(d => d.Dataset)
                .Select(g => new DeclaredDerivation { Dataset = g.Key, From = g.SelectMany(d => d.From).Distinct().ToList() })
                .ToList(),
            Warnings = described.SelectMany(d => d.Warnings).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Everything one flow (an interface of its source) contributes, in a stable order.</summary>
    public static RegisteredFlowLineage Describe(
        FlowDefinition flow, RegisteredLineageContext context, DeliveryDocumentLoader documents, MappingTemplateSource? templates = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        return Describe(flow, new Scan(context, documents, templates, null));
    }

    private static RegisteredFlowLineage Describe(FlowDefinition flow, Scan scan)
    {
        var who = $"delivery flow '{flow.Label}'";
        var warnings = new List<string>();

        var files = new List<DeclaredFileLocation>();
        var sent = PayloadParts.Of(flow)?.Select(p => p.Payload).ToList()
            ?? (PayloadParts.Streamed(flow) is { } payloadName ? [payloadName] : []);
        foreach (var name in sent)
        {
            if (flow.Source.Payloads.TryGetValue(name, out var payload))
            {
                files.Add(new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = payload.Root, FilePattern = payload.Pattern });
            }
        }

        var datasets = new List<DeclaredDataset>();
        var derivations = new List<DeclaredDerivation>();
        var partition = flow.FollowsRegistry && flow.Partition is null
            ? PartitionNames.Every
            : OsduLineage.Partition(flow.Target.Headers, who, "target.headers", warnings);
        var pinned = PinnedMapping(flow, scan, who, warnings);
        if (partition is not null)
        {
            var endpoint = flow.Target.Endpoint;
            if (pinned is { } found)
            {
                var mapping = found.Mapping;
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, mapping.Kind, who, $"the kind of mapping '{mapping.Reference}'", warnings));

                // The pipeline's cache steps write the types they bring up to date (osdu/docs/reference/flow/delivery.md,
                // cache): a type its after step puts what it delivered into is read by the flows after it, and a type its
                // before step searches is read from the kinds that type captures, so the flows delivering them run first.
                foreach (var type in scan.StepTypes(partition, flow.Cache.After, h => h.Kind is { } pattern && Engine.Dimensions.KindPatterns.Matches(pattern, mapping.Kind)))
                {
                    Add(datasets, OsduLineage.CacheType(LineageRelation.Writes, type.Partition, type.Name, who, warnings));
                }

                if (!flow.Cache.Before.IsEmpty)
                {
                    var cacheReads = MappingCacheReads.Of(mapping, scan.Template(mapping).Schema);
                    foreach (var type in scan.StepTypes(
                                 partition,
                                 flow.Cache.Before,
                                 h => h.Kind is not null
                                     && (cacheReads.Types.Contains(h.Name, StringComparer.OrdinalIgnoreCase) || cacheReads.EntityTypes.Contains(h.EntityType, StringComparer.OrdinalIgnoreCase))))
                    {
                        Add(datasets, OsduLineage.CacheType(LineageRelation.Writes, type.Partition, type.Name, who, warnings));
                        if (type.Kind is { } captured)
                        {
                            Add(datasets, OsduLineage.Type(
                                LineageRelation.Reads, endpoint, partition, captured, who, $"cache type '{type.Name}', which cache.before brings up to date", warnings));
                        }
                    }
                }

                var reads = MappingReads(mapping, endpoint, partition, scan, who, warnings);
                reads = [.. reads, .. OrderReads(flow, mapping, endpoint, partition, scan, who, warnings).Where(r => !reads.Contains(r))];
                if (OsduLineage.Mapping(endpoint, partition, found.Directory, mapping.Reference, who, warnings) is { } node)
                {
                    derivations.Add(new DeclaredDerivation { Dataset = node, From = reads });
                }
                else
                {
                    // A mapping that cannot be a node of its own still orders the flow: the flow reads what it reads.
                    datasets.AddRange(reads);
                }

                // Each step of enrich: renders its fragment into the record (osdu/docs/reference/flow/enrich.md): a mapping node
                // of its own, read by the flow, reading what the fragment reads, so the flows capturing those run first. A step
                // that registers files reads them where its rows say, writes their dataset kind, and renders its dataset
                // fragment the same way.
                var mappings = DeliveryLayout.ResolveWithin(flow, scan.Context.DocumentFolder, scan.Context.Contains)!.MappingsDirectory;
                foreach (var step in flow.Enrich ?? [])
                {
                    if (step.Mapping is { } reference)
                    {
                        StepFragment(reference, $"enrich.{step.Name}");
                    }

                    if (step.Files is { } stepFiles)
                    {
                        files.Add(new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = stepFiles.Payload.Root, FilePattern = stepFiles.Payload.Pattern });
                        Add(datasets, OsduLineage.Type(
                            LineageRelation.Writes, endpoint, partition, stepFiles.Kind, who, $"enrich.{step.Name}.files.kind", warnings));
                        if (stepFiles.Mapping is { } filesReference)
                        {
                            StepFragment(filesReference, $"enrich.{step.Name}.files");
                        }
                    }
                }

                void StepFragment(string reference, string where)
                {
                    var (fragment, reason) = scan.Mapping(mappings, reference);
                    if (fragment is null)
                    {
                        warnings.Add($"{who} shows fewer reads in lineage than it makes: fragment '{reference}' of {where} could not be read ({reason}).");
                        return;
                    }

                    var stepReads = MappingReads(fragment, endpoint, partition, scan, who, warnings);
                    if (OsduLineage.Mapping(endpoint, partition, found.Directory, fragment.Reference, who, warnings) is { } stepNode)
                    {
                        derivations.Add(new DeclaredDerivation { Dataset = stepNode, From = stepReads });
                    }
                    else
                    {
                        datasets.AddRange(stepReads.Where(r => !datasets.Contains(r)));
                    }
                }
            }

            // The routes that register datasets beside the record write the dataset kind too; the workflow route writes
            // each input's own kind.
            if (flow.Target.Protocol is DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.Dataset
                or DeliveryProtocol.FileAndDdms or DeliveryProtocol.ManifestAndDdms
                || (flow.Target.Protocol == DeliveryProtocol.Workflow && flow.Target.Workflow?.Anchor == WorkflowAnchor.Storage && flow.Source.Payloads.ContainsKey(PayloadParts.Files)))
            {
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, flow.Target.ProtocolOptions.DatasetKind, who,
                    "target.protocolOptions.datasetKind", warnings));
            }

            foreach (var input in flow.Target.Workflow?.Inputs ?? [])
            {
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, input.DatasetKind, who, $"workflow.inputs.{input.Name}.datasetKind", warnings));
            }
        }

        return new RegisteredFlowLineage
        {
            Objects = DeclaredObjects(flow),
            Files = files,
            Datasets = datasets,
            Derivations = derivations,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// What a mapping reads in a partition of a platform, each node once and in a stable order: the cache types it reads by
    /// name, then the cache types of the partition holding records of an entity type its ids are checked against, in name
    /// order, then the kinds its searches look in, in the searches' name order, then the kind its template is linked to,
    /// when it reads its records' parent (osdu/docs/reference/flow/mapping-parent.md).
    /// </summary>
    /// <remarks>
    /// Which types hold an entity type is what the cache flows of the scanned estate declare for the partition: a type a
    /// cache flow of another repository holds is not seen here. A search reads the platform's records of its kind as the
    /// delivery renders, not a capture of them.
    /// <para>
    /// A cache type every cache flow holding it in the partition fills from the very entity type the mapping writes is not
    /// read (osdu/docs/reference/concepts/lineage.md, Known limits): those flows capture what the delivery writes, so a
    /// read of their types would make each of the two read what the other writes, and lineage orders neither of such a
    /// pair. Left out, the cache flows run after the delivery and capture what it wrote, and the deliveries the mapping
    /// refers to are still ordered first by the OSDU order (<see cref="OrderReads"/>).
    /// </para>
    /// </remarks>
    private static List<DeclaredDataset> MappingReads(
        MappingDefinition mapping, string endpoint, string partition, Scan scan, string who, List<string> warnings)
    {
        var template = scan.Template(mapping);
        var cache = MappingCacheReads.Of(mapping, template.Schema);
        var reads = new List<DeclaredDataset>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = SqlFlow.Delivery.Order.OsduEntity.OfKind(mapping.Kind);
        foreach (var type in cache.Types)
        {
            if (named.Add(type.Trim()) && !scan.OnlyHeldWith(partition, type, written))
            {
                Add(reads, OsduLineage.CacheType(LineageRelation.Reads, partition, type, who, warnings));
            }
        }

        if (cache.EntityTypes.Count > 0)
        {
            var checkedAgainst = cache.EntityTypes.ToHashSet(StringComparer.Ordinal);
            foreach (var held in scan.Held
                         .Where(h => string.Equals(h.Partition, partition, StringComparison.OrdinalIgnoreCase) && checkedAgainst.Contains(h.EntityType))
                         .OrderBy(h => h.Name, StringComparer.Ordinal))
            {
                if (named.Add(held.Name) && !scan.OnlyHeldWith(partition, held.Name, written))
                {
                    Add(reads, OsduLineage.CacheType(LineageRelation.Reads, partition, held.Name, who, warnings));
                }
            }
        }

        foreach (var search in mapping.Searches.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            Add(reads, OsduLineage.Type(
                LineageRelation.Reads, endpoint, partition, search.Kind, who, $"search '{search.Name}' of mapping '{mapping.Reference}'", warnings));
        }

        // A record that takes sections of its parent is rendered from what the parent's delivery wrote, so the flow reads the
        // parent's kind and runs after the flow that delivers it.
        if (mapping.ReadsParent && template.Schema?.Parent is { } parent)
        {
            Add(reads, OsduLineage.Type(
                LineageRelation.Reads, endpoint, partition, parent.Kind, who, $"the parent of the records mapping '{mapping.Reference}' renders", warnings));
        }

        // A record that links its children ($child, osdu/docs/reference/flow/mapping-child.md) reads none of them: it is
        // written without the link while a child is not there, and the ledger plans it again when one lands. Read here, the
        // kinds it links would order the flow after the flows delivering its children, which run after it when they read
        // their parent, and neither of the two could run first.

        if (cache.Untold.Count > 0)
        {
            var listed = string.Join(", ", cache.Untold.Take(MaxNamedVariables))
                + (cache.Untold.Count > MaxNamedVariables ? $" and {cache.Untold.Count - MaxNamedVariables} more" : string.Empty);
            warnings.Add(
                $"{who} shows fewer cache types in lineage than its mapping reads: mapping '{mapping.Reference}' builds a reference with ref at {listed}, "
                + $"and the entity type of each is told by its template ({mapping.Template}), so the cache types those references are checked against are left out. "
                + $"Its template is not read: {template.Missing}");
        }

        return reads.Distinct().ToList();
    }

    /// <summary>
    /// The entity types the OSDU order says the flow's records wait for in the partition (osdu/docs/reference/concepts/
    /// osdu-order.md, Lineage takes the order), each read as every kind of it on the flow's platform, so lineage binds the
    /// read to the kinds the estate's other flows write and runs those flows first. A reference the order cut, to a type
    /// nothing in the partition delivers, or to the flow's own type, is not read. The warnings say where the order is short:
    /// a template that could not be read, and a circle of references the order could not resolve.
    /// </summary>
    private static List<DeclaredDataset> OrderReads(
        FlowDefinition flow, MappingDefinition mapping, string endpoint, string partition, Scan scan, string who, List<string> warnings)
    {
        var reads = new List<DeclaredDataset>();
        if (scan.Order.Plan(partition) is not { } plan)
        {
            return reads;
        }

        // A flow that leaves its partitions to the registry delivers to every partition the estate names too, so it waits for
        // what each of those partitions' orders says; a flow of a named partition waits for its own partition's order.
        var me = OrderDeliveries.Identity(flow, partition, mapping.Kind);
        var orders = string.Equals(partition, PartitionNames.Every, StringComparison.Ordinal) ? [plan, .. scan.Order.Named] : new[] { plan };
        foreach (var order in orders)
        {
            var mine = me with { Partition = order.Partition };
            foreach (var type in order.WaitsFor(mine).Order(StringComparer.Ordinal))
            {
                // The read is keyed where the writers' nodes are: the order's partition for the flows that name it, and the
                // partition standing for every registered one for the flows that leave theirs to the registry.
                var keyed = order.DeliveriesOf(type)
                    .Select(d => scan.Order.ServesEveryPartition(d) ? PartitionNames.Every : order.Partition)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal);
                foreach (var where in keyed)
                {
                    Add(reads, OsduLineage.Type(
                        LineageRelation.Reads, endpoint, where, OrderDeliveries.Pattern(type), who, $"the OSDU order of partition '{order.Partition}'", warnings));
                }
            }
        }

        if (plan.Unknown.Any(u => SameDelivery(u, me)) && scan.Template(mapping).Missing is { } missing)
        {
            warnings.Add(
                $"{who} is not ordered by what its records refer to: mapping '{mapping.Reference}' fills {mapping.Template}, "
                + $"and without that template the OSDU order cannot tell which of the partition's deliveries it waits for. Its template is not read: {missing}");
        }

        var entity = me.EntityType;
        var circle = plan.Cuts
            .Where(c => c.Reason == SqlFlow.Delivery.Order.OrderCutReason.Circular
                && (string.Equals(c.Edge.From, entity, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Edge.To, entity, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Why)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (circle.Count > 0)
        {
            warnings.Add(
                $"{who} is not ordered against every delivery it refers to: {string.Join("; ", circle)}. Lineage runs neither first, so a record waits for one it "
                + "refers to only once the other flow has put that record in the ledger.");
        }

        return reads;
    }

    private static bool SameDelivery(SqlFlow.Delivery.Order.OrderDelivery a, SqlFlow.Delivery.Order.OrderDelivery b)
        => string.Equals(a.Repository, b.Repository, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Flow, b.Flow, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Interface ?? string.Empty, b.Interface ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The partition cache types a mapping reads by name, from its cache sources and their lookups and from the tables its
    /// replaces read, in name order (<see cref="MappingDefinition.CacheTypesRead"/>). A search source's lookups name a
    /// search, not a cache type, and are not among them; neither are the types its ids are checked against, which are read
    /// by the entity type they hold (<see cref="MappingCacheReads"/>).
    /// </summary>
    public static IReadOnlyList<string> CacheTypes(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping.CacheTypesRead();
    }

    /// <summary>
    /// The mapping the flow pins, read from the mappings directory the flow's layout names inside the scanned checkout,
    /// with that directory as the mapping's node is grouped under it; or null with a warning saying why it could not be.
    /// </summary>
    private static (MappingDefinition Mapping, string Directory)? PinnedMapping(FlowDefinition flow, Scan scan, string who, List<string> warnings)
    {
        var reference = flow.Render.Mapping;
        var context = scan.Context;
        var layout = DeliveryLayout.ResolveWithin(flow, context.DocumentFolder, context.Contains);
        if (layout is null)
        {
            warnings.Add(
                $"{who} shows no OSDU type in lineage: render.mappings '{flow.Render.MappingsDirectory}' lies outside the repository, so mapping '{reference}' is not read.");
            return null;
        }

        var (mapping, reason) = scan.Mapping(layout.MappingsDirectory, reference);
        if (mapping is null)
        {
            warnings.Add($"{who} shows no OSDU type in lineage: mapping '{reference}' could not be read ({reason}).");
            return null;
        }

        var relative = Path.GetRelativePath(context.EstateRoot, layout.MappingsDirectory).Replace('\\', '/').Trim('/');
        return (mapping, relative.Length == 0 || relative == "." ? OsduLineage.RootGroup : relative);
    }

    private static void Add(List<DeclaredDataset> datasets, DeclaredDataset? dataset)
    {
        if (dataset is not null)
        {
            datasets.Add(dataset);
        }
    }

    /// <summary>
    /// What describing one document reads once however many interfaces and partitions ask: each mapping, each template,
    /// and what the cache flows of the estate hold.
    /// </summary>
    private sealed class Scan(RegisteredLineageContext context, DeliveryDocumentLoader documents, MappingTemplateSource? templates, Func<IOrderCatalog?>? order)
    {
        private readonly Dictionary<(string Directory, string Reference), (MappingDefinition? Mapping, string? Reason)> _mappings = [];
        private readonly Dictionary<(string? Document, TemplateReference Reference), MappingTemplateRead> _templates = [];
        private readonly MappingTemplateSource _source = templates ?? new MappingTemplateSource();
        private IReadOnlyList<CacheLineage.HeldType>? _held;
        private OrderLineage? _order;
        private IReadOnlyList<(string Flow, IReadOnlyList<CacheLineage.HeldType> Held)>? _heldByFlow;

        public RegisteredLineageContext Context => context;

        /// <summary>The OSDU order of the scan the document is described in (<see cref="OrderLineage"/>).</summary>
        public OrderLineage Order => _order ??= OrderLineage.Of(context, templates, order);

        /// <summary>
        /// Whether every cache flow of the estate holding <paramref name="type"/> in <paramref name="partition"/> also holds
        /// records of <paramref name="writtenEntity"/>, the entity type a delivery writes: false when no flow holds the type,
        /// or when the delivery writes no entity type.
        /// </summary>
        public bool OnlyHeldWith(string partition, string type, string? writtenEntity)
        {
            if (writtenEntity is null)
            {
                return false;
            }

            var holders = HeldByFlow
                .Where(f => f.Held.Any(h => SamePartition(h.Partition, partition) && string.Equals(h.Name, type.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return holders.Count > 0
                && holders.All(f => f.Held.Any(h => SamePartition(h.Partition, partition) && string.Equals(h.EntityType, writtenEntity, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// The types the cache flows of the scanned estate hold, in <paramref name="partition"/> or for every partition, that a
        /// cache step's <paramref name="entries"/> stand for: those <paramref name="auto"/> picks for <c>auto</c>, else each
        /// named by its name or by its entity type. For a delivery to every partition, those of any partition.
        /// </summary>
        public IReadOnlyList<CacheLineage.HeldType> StepTypes(string partition, Model.CacheStepEntries entries, Func<CacheLineage.HeldType, bool> auto)
        {
            if (entries.IsEmpty)
            {
                return [];
            }

            var held = Held.Where(h => SamePartition(h.Partition, partition) || h.Partition == PartitionNames.Every || partition == PartitionNames.Every);
            return entries.Auto
                ? [.. held.Where(auto)]
                : [.. held.Where(h => entries.Named.Any(entry => Model.CacheStepEntries.IsEntityType(entry)
                    ? string.Equals(h.EntityType, entry, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(h.Name, entry, StringComparison.OrdinalIgnoreCase)))];
        }

        /// <summary>Every cache flow of the scanned estate with the types it holds, as <see cref="Held"/> lists them together.</summary>
        private IReadOnlyList<(string Flow, IReadOnlyList<CacheLineage.HeldType> Held)> HeldByFlow => _heldByFlow ??= context.Estate
            .Select(entry => entry.Document)
            .OfType<CacheFlowDocument>()
            .Select(document =>
            {
                try
                {
                    return (document.Flow.Name, CacheLineage.Held(document.Flow));
                }
                catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException)
                {
                    return (document.Flow.Name, (IReadOnlyList<CacheLineage.HeldType>)[]);
                }
            })
            .ToList();

        private static bool SamePartition(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Every type the cache flows of the scanned estate hold, by the partition it is held in. A cache document that
        /// cannot say what it holds (a partition it cannot be bound to) holds nothing here; its own description reports it.
        /// </summary>
        public IReadOnlyList<CacheLineage.HeldType> Held => _held ??= context.Estate
            .Select(entry => entry.Document)
            .OfType<CacheFlowDocument>()
            .SelectMany(document =>
            {
                try
                {
                    return CacheLineage.Held(document.Flow);
                }
                catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException)
                {
                    return [];
                }
            })
            .Distinct()
            .ToList();

        /// <summary>The mapping <paramref name="reference"/> pins under <paramref name="directory"/>, or why it could not be read.</summary>
        public (MappingDefinition? Mapping, string? Reason) Mapping(string directory, string reference)
        {
            if (_mappings.TryGetValue((directory, reference), out var known))
            {
                return known;
            }

            (MappingDefinition?, string?) read;
            try
            {
                read = (new MappingCatalog(directory, documents, new FolderTemplates(context.EstateRoot)).Load(reference, Shown), null);
            }
            catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
            {
                read = (null, SecretHygiene.RedactedMessage(ex is SqlFlowException ? ex.Message : $"{ex.GetType().Name} reading it"));
            }

            _mappings[(directory, reference)] = read;
            return read;
        }

        /// <summary>
        /// The template <paramref name="mapping"/> fills, read from the template folders on its path up to the top of the
        /// checkout, or why lineage has none to read.
        /// </summary>
        public MappingTemplateRead Template(MappingDefinition mapping)
        {
            var key = (mapping.TemplateDocument, mapping.Template);
            if (!_templates.TryGetValue(key, out var read))
            {
                read = _source.Read(mapping, context.EstateRoot);
                _templates[key] = read;
            }

            return read;
        }

        /// <summary>A path as a warning names it: relative to the checkout, with forward slashes.</summary>
        private string Shown(string path) => Path.GetRelativePath(context.EstateRoot, path).Replace('\\', '/');
    }
}
