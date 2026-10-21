using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Ordering;

/// <summary>
/// One delivery the catalog holds: the repository and file it is synced from, the delivery as the OSDU order reads it, and
/// the relationships of the kind it writes when they are known.
/// </summary>
public sealed record CatalogDelivery(Guid RepoId, string RelativePath, OrderDelivery Delivery, SchemaNode? Schema);

/// <summary>
/// The deliveries the catalog holds (<see cref="OrderCatalog.ReadAsync"/>), and what could not be read as the order needs
/// it: a mapping that no longer loads, or a template whose copy cannot be read. Each problem names what it is about.
/// </summary>
public sealed record CatalogDeliveries(IReadOnlyList<CatalogDelivery> Deliveries, IReadOnlyList<string> Problems);

/// <summary>
/// What the catalog knows for the OSDU order beyond the folder a lineage scan reads (osdu/docs/reference/concepts/
/// osdu-order.md, OSDU's lineage by release): a kind's relationships from a built release of OSDU's lineage, for a kind
/// whose template is not saved.
/// </summary>
public interface IOrderCatalog
{
    /// <summary>
    /// The relationships of <paramref name="kind"/> from the newest built release of OSDU's lineage holding it, or null when
    /// none holds it or the catalog does not answer: either way the kind is not known, which the order reports itself.
    /// </summary>
    SchemaNode? Schema(string kind);
}

/// <summary>
/// The catalog the module database holds, read for the OSDU order: the built releases of OSDU's lineage
/// (<see cref="IOrderCatalog"/>), and the synced interfaces of the repositories with the mappings they pin and the
/// templates those pin (<see cref="ReadAsync"/>), which the repository sync checks a repository's waves against and the
/// order pages show.
/// </summary>
public sealed class OrderCatalog : IOrderCatalog
{
    private static readonly DeliveryDocumentLoader Documents = new();

    private readonly Func<OsduDbContext> _contexts;

    public OrderCatalog(Func<OsduDbContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    public SchemaNode? Schema(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        try
        {
            // Lineage describes documents synchronously; the read runs off the caller's context so it cannot deadlock on it.
            return Task.Run(async () =>
            {
                await using var db = _contexts();
                return await SchemaLineageStore.FindKindAsync(db, kind).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or DeliveryException)
        {
            // The kind is then not known, which the order lists, and lineage warns of on each delivery of it.
            return null;
        }
    }

    /// <summary>
    /// The active delivery interfaces the catalog holds, on <paramref name="db"/>: those of repository
    /// <paramref name="repoId"/>, or of every repository when none is named. Each is read as the OSDU order reads it: the
    /// partition its records reach, and the mapping it pins, read for the properties it fills against the template the sync
    /// settled that mapping to from the template folders on its path (the copy the same sync kept of the file), or against
    /// the newest built release of OSDU's lineage holding the kind when that copy cannot be read. An interface whose mapping
    /// is not valid (a mapping whose template no folder on its path holds is not), names no OSDU kind, or whose partition
    /// cannot be read is left out; its own repository's sync reports why.
    /// </summary>
    public static async Task<CatalogDeliveries> ReadAsync(OsduDbContext db, Guid? repoId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var interfaces = db.DeliveryInterfaces.AsNoTracking().Where(i => i.Active && i.TargetPartition != string.Empty);
        if (repoId is { } only)
        {
            interfaces = interfaces.Where(i => i.RepoId == only);
        }

        var rows = await interfaces
            .Join(
                db.DeliveryMappings.AsNoTracking().Where(m => m.Status == "valid"),
                i => new { i.RepoId, Reference = i.MappingReference },
                m => new { m.RepoId, m.Reference },
                (i, m) => new { i.RepoId, i.FlowName, i.Interface, i.TargetPartition, i.RelativePath, m.Yaml, m.TemplateVersion, MappingPath = m.RelativePath })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // A source that follows the registry is described once per registered partition, and each of those rows names the
        // partition its records reach; one delivery per flow, interface and partition is what the order needs.
        var distinct = rows
            .GroupBy(r => (r.RepoId, Flow: r.FlowName.ToUpperInvariant(), Interface: r.Interface.ToUpperInvariant(), Partition: r.TargetPartition.ToUpperInvariant()))
            .Select(g => g.First())
            .OrderBy(r => r.RepoId).ThenBy(r => r.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Interface, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.TargetPartition, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deliveries = new List<CatalogDelivery>(distinct.Count);
        var problems = new List<string>();
        // A mapping that names its template's kind alone settles to what its repository's folders hold, so one document reads
        // as one mapping per repository.
        var mappings = new Dictionary<(Guid Repo, string Yaml), MappingDefinition?>();
        var folders = new Dictionary<Guid, Catalog.SyncedTemplates>();
        var schemas = new Dictionary<(Guid Repo, string Kind, string Version), (OsduTemplate? Template, SchemaNode? Node)>();
        foreach (var row in distinct)
        {
            if (!folders.TryGetValue(row.RepoId, out var held))
            {
                held = await Catalog.SyncedTemplates.ReadAsync(db, row.RepoId, ct).ConfigureAwait(false);
                folders[row.RepoId] = held;
            }

            if (!mappings.TryGetValue((row.RepoId, row.Yaml), out var mapping))
            {
                try
                {
                    mapping = Documents.ParseMapping(row.Yaml, row.MappingPath, held.For(row.MappingPath));
                }
                catch (SqlFlowException ex)
                {
                    // The sync that stored it as valid read it; a build that reads it otherwise leaves the delivery out.
                    var who = $"flow '{row.FlowName}'{(row.Interface.Length == 0 ? string.Empty : $" interface '{row.Interface}'")} ({row.RelativePath})";
                    problems.Add($"{who} is left out of the OSDU order: its mapping no longer loads ({SecretHygiene.RedactedMessage(ex.Message)}).");
                    mapping = null;
                }

                mappings[(row.RepoId, row.Yaml)] = mapping;
            }

            if (mapping is null || OsduEntity.OfKind(mapping.Kind) is null)
            {
                continue;
            }

            if (!schemas.TryGetValue((row.RepoId, mapping.Kind, mapping.Template.Version), out var schema))
            {
                schema = await SchemaAsync(db, held, row.MappingPath, mapping.Template, problems, ct).ConfigureAwait(false);
                schemas[(row.RepoId, mapping.Kind, mapping.Template.Version)] = schema;
            }

            var delivery = OrderDeliveries.Of(
                row.FlowName, row.Interface, row.TargetPartition, mapping, schema.Template, schema.Template is null ? schema.Node : null, row.RepoId.ToString("D"));
            deliveries.Add(new CatalogDelivery(row.RepoId, row.RelativePath, delivery, schema.Node));
        }

        return new CatalogDeliveries(deliveries, problems);
    }

    /// <summary>
    /// The template the mapping at <paramref name="mappingPath"/> fills, as its repository's last sync kept a copy of it,
    /// or the kind from a built release of OSDU's lineage when that copy cannot be read.
    /// </summary>
    private static async Task<(OsduTemplate? Template, SchemaNode? Node)> SchemaAsync(
        OsduDbContext db, Catalog.SyncedTemplates held, string mappingPath, TemplateReference template, List<string> problems, CancellationToken ct)
    {
        var resolved = await held.ResolveAsync(mappingPath, template.Kind, template.Version, ct).ConfigureAwait(false);
        if (resolved.Schema is { } schema)
        {
            try
            {
                var read = OsduTemplate.From(schema);
                return (read, OrderDeliveries.Node(read));
            }
            catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException or ArgumentException)
            {
                // A template that does not read as one is no better than none: the release's lineage is asked next.
                problems.Add($"The template {template} that {mappingPath} fills does not read as one ({SecretHygiene.RedactedMessage(ex.Message)}), so the OSDU order reads the kind from a built release of OSDU's lineage, or not at all.");
            }
        }
        else
        {
            problems.Add($"The template {template} that {mappingPath} fills cannot be read ({resolved.Problem}), so the OSDU order reads the kind from a built release of OSDU's lineage, or not at all.");
        }

        return (null, await SchemaLineageStore.FindKindAsync(db, template.Kind, ct).ConfigureAwait(false));
    }
}
