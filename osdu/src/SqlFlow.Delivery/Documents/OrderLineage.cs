using System.Runtime.CompilerServices;
using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Ordering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The OSDU order of the estate a lineage scan walks (osdu/docs/reference/concepts/osdu-order.md, Lineage takes the order):
/// every delivery flow of the scanned repository, each interface in each partition it is described in, with the kind its
/// mapping fills and the properties it fills, ordered by the relationships the templates those mappings pin declare, or,
/// for a kind no template folder holds, a built release of OSDU's lineage (<see cref="IOrderCatalog"/>). It holds the
/// repository's own deliveries alone: a schedule runs flows of one repository, in that repository's waves, and another
/// repository's flows run in its own schedules. It is built once per scan, however many documents are described, and
/// lineage reads from it which entity types each delivery waits for.
/// </summary>
internal sealed class OrderLineage
{
    /// <summary>One order per scan: the estate list a scan hands every document is the same instance for all of them.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<RegisteredEstateDocument>, OrderLineage> Scans = new();

    private static readonly DeliveryDocumentLoader Documents = new();

    private readonly Dictionary<string, OrderPlan> _plans;
    private readonly HashSet<(string Repository, string Flow, string Interface)> _everyPartition;

    private OrderLineage(Dictionary<string, OrderPlan> plans, HashSet<(string, string, string)> everyPartition)
    {
        _plans = plans;
        _everyPartition = everyPartition;
    }

    /// <summary>The order of a document described outside a scan: it sees no other flow, so it orders nothing.</summary>
    public static OrderLineage None { get; } = new(new Dictionary<string, OrderPlan>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>The partitions the estate's deliveries name, each with its order, in name order; the partition standing for every registered one is not among them.</summary>
    public IEnumerable<OrderPlan> Named => _plans
        .Where(p => !string.Equals(p.Key, PartitionNames.Every, StringComparison.Ordinal))
        .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
        .Select(p => p.Value);

    /// <summary>
    /// Whether <paramref name="delivery"/> is a flow that leaves its partitions to the registry: lineage keys its nodes under
    /// the partition standing for every registered one, whichever partition's order it is found in.
    /// </summary>
    public bool ServesEveryPartition(OrderDelivery delivery)
        => _everyPartition.Contains((delivery.Repository.ToUpperInvariant(), delivery.Flow.ToUpperInvariant(), (delivery.Interface ?? string.Empty).ToUpperInvariant()));

    /// <summary>The partitions each scan's deliveries name, read from the documents alone.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<RegisteredEstateDocument>, IReadOnlyList<string>> NamedPartitions = new();

    /// <summary>The order of the scan <paramref name="context"/> belongs to; none for a document described on its own.</summary>
    public static OrderLineage Of(RegisteredLineageContext context, MappingTemplateSource? templates, Func<IOrderCatalog?>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Estate.Count == 0 ? None : Scans.GetValue(context.Estate, _ => Build(context, templates, catalog?.Invoke()));
    }

    /// <summary>The order of <paramref name="partition"/>, or null when no delivery of the estate writes to it.</summary>
    public OrderPlan? Plan(string partition) => _plans.GetValueOrDefault(partition);

    /// <summary>
    /// <paramref name="lineage"/> with every OSDU type a flow described under the partition standing for every registered one
    /// reads also read in each partition the estate's deliveries name (osdu/docs/reference/concepts/lineage.md, Flows that
    /// serve every partition). Such a flow reads every registered partition, the named ones among them, so the flows
    /// delivering those kinds to a named partition are ordered before it, as the flows delivering them to every partition
    /// are. Reads of other systems, writes, and flows of a named partition are left as they are.
    /// </summary>
    public static RegisteredFlowLineage BindEveryPartition(RegisteredFlowLineage lineage, RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(context);
        var named = NamedPartitions.GetValue(context.Estate, _ => PartitionsNamedIn(context));
        if (named.Count == 0)
        {
            return lineage;
        }

        return lineage with
        {
            Datasets = Bind(lineage.Datasets, named),
            Derivations = [.. lineage.Derivations.Select(d => d with { From = Bind(d.From, named) })],
        };
    }

    /// <summary>The partitions the scan's delivery flows name, from the documents alone, in name order.</summary>
    private static IReadOnlyList<string> PartitionsNamedIn(RegisteredLineageContext context)
        => [.. context.Estate
            .Select(e => e.Document)
            .OfType<DeliveryFlowDocument>()
            .SelectMany(d => d.Source.FollowsRegistry ? d.Source.Interfaces : d.Source.EveryLedger(RegisteredPartitions.None))
            .Select(PartitionOf)
            .OfType<string>()
            .Where(p => !string.Equals(p, PartitionNames.Every, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static List<DeclaredDataset> Bind(IReadOnlyList<DeclaredDataset> datasets, IReadOnlyList<string> named)
    {
        var bound = datasets.ToList();
        foreach (var read in datasets.Where(d => d.Relation == LineageRelation.Reads
                     && string.Equals(d.System, OsduLineage.TypeSystem, StringComparison.Ordinal)
                     && string.Equals(d.Namespace, PartitionNames.Every, StringComparison.Ordinal)))
        {
            foreach (var partition in named)
            {
                var copy = read with { Namespace = partition };
                if (!bound.Contains(copy))
                {
                    bound.Add(copy);
                }
            }
        }

        return bound;
    }

    private static OrderLineage Build(RegisteredLineageContext context, MappingTemplateSource? fallback, IOrderCatalog? catalog)
    {
        var mappings = new Dictionary<(string Directory, string Reference), MappingDefinition?>();
        var templates = new Dictionary<(string? Document, TemplateReference Reference), OsduTemplate?>();
        var nodes = new Dictionary<string, SchemaNode>(StringComparer.OrdinalIgnoreCase);
        var deliveries = new List<OrderDelivery>();
        var released = new Dictionary<string, SchemaNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.Estate)
        {
            if (entry.Document is not DeliveryFlowDocument document)
            {
                continue;
            }

            var where = new RegisteredLineageContext(entry.Path, context.EstateRoot) { Estate = context.Estate };
            var source = document.Source;
            foreach (var flow in source.FollowsRegistry ? source.Interfaces : source.EveryLedger(RegisteredPartitions.None))
            {
                if (PartitionOf(flow) is not { } partition
                    || DeliveryLayout.ResolveWithin(flow, where.DocumentFolder, where.Contains) is not { } layout
                    || Mapping(mappings, layout.MappingsDirectory, flow.Render.Mapping, context.EstateRoot) is not { } mapping)
                {
                    // Its own description says why it has no OSDU nodes; with none it orders nothing either.
                    continue;
                }

                var template = Template(templates, document.Templates ?? fallback ?? FolderSource, mapping, context.EstateRoot);
                var node = template is not null ? OrderDeliveries.Node(template) : Released(released, catalog, mapping.Kind);
                if (node is not null)
                {
                    // Two mappings pinning one kind under different template versions read the first one's relationships.
                    nodes.TryAdd(node.Kind, node);
                }

                deliveries.Add(OrderDeliveries.Of(flow, partition, mapping, template, template is null ? node : null));
            }
        }

        // A flow that leaves its partitions to the registry delivers to every registered partition, so its records are part of
        // the order of each partition the estate names as well as of its own.
        var plans = OsduOrder.PlanPartitions(deliveries, OsduSchemaGraph.Of(nodes.Values)).ToDictionary(p => p.Partition, StringComparer.OrdinalIgnoreCase);
        var everyPartition = deliveries
            .Where(d => string.Equals(d.Partition, PartitionNames.Every, StringComparison.Ordinal))
            .Select(d => (d.Repository.ToUpperInvariant(), d.Flow.ToUpperInvariant(), (d.Interface ?? string.Empty).ToUpperInvariant()))
            .ToHashSet();
        return new OrderLineage(plans, everyPartition);
    }

    /// <summary>A kind's relationships from a built release of OSDU's lineage, asked once per scan; none without a catalog.</summary>
    private static SchemaNode? Released(Dictionary<string, SchemaNode?> cache, IOrderCatalog? catalog, string kind)
    {
        if (catalog is null)
        {
            return null;
        }

        if (!cache.TryGetValue(kind, out var node))
        {
            node = catalog.Schema(kind);
            cache[kind] = node;
        }

        return node;
    }

    /// <summary>The partition a flow is described in, as its own description binds it; null when it cannot be bound.</summary>
    private static string? PartitionOf(FlowDefinition flow) => OrderDeliveries.PartitionOf(flow);

    private static MappingDefinition? Mapping(Dictionary<(string, string), MappingDefinition?> cache, string directory, string reference, string root)
    {
        if (cache.TryGetValue((directory, reference), out var known))
        {
            return known;
        }

        MappingDefinition? mapping;
        try
        {
            mapping = new MappingCatalog(directory, Documents, new FolderTemplates(root)).Load(reference, path => path);
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
        {
            mapping = null;
        }

        cache[(directory, reference)] = mapping;
        return mapping;
    }

    /// <summary>The template folders on each mapping's path, which the order reads a template from when it was given no other source.</summary>
    private static readonly MappingTemplateSource FolderSource = new();

    private static OsduTemplate? Template(
        Dictionary<(string? Document, TemplateReference Reference), OsduTemplate?> cache, MappingTemplateSource source, MappingDefinition mapping, string root)
    {
        var key = (mapping.TemplateDocument, mapping.Template);
        if (cache.TryGetValue(key, out var known))
        {
            return known;
        }

        OsduTemplate? template = null;
        if (source.Read(mapping, root) is { Outcome: MappingTemplateOutcome.Found, Schema: { } schema })
        {
            try
            {
                template = OsduTemplate.From(schema);
            }
            catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException or ArgumentException)
            {
                template = null;
            }
        }

        cache[key] = template;
        return template;
    }
}
