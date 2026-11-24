using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A delivery flow's record counts by state, drift, throughput, and its last submission: the flow's dashboard card. For one
/// interface it carries the interface and its ledger identity; for a source as a whole it adds its interfaces up, carries
/// no single ledger identity (<see cref="Guid.Empty"/>) and no interface. A flow that names its partitions keeps a ledger
/// per partition: counted in one partition, <c>Partition</c> names it; counted as a whole, every partition's ledgers are
/// added up the way a source's interfaces are, and <c>Partitions</c> lists the partitions the counts cover. Both are null
/// for a flow that names none. A flow whose partition is its data-partition-id header is counted in the partition its
/// ledger is kept under, which <c>HeaderPartition</c> names as the ledger's directory holds it (null until it has run), so
/// the workbench's partition filters it as it filters the rest; a request never names it, since such a flow takes none.
/// <c>Blocked</c> counts the records blocked until their row moves or someone releases them, and <c>Stranded</c> the held or
/// failed ones nothing will move (what a release answered by a plan that left the record parked used to leave): together,
/// what a release of every blocked record reaches.
/// </summary>
public sealed record DeliveryFlowStatsDto(
    Guid PipelineId, string FlowName, Guid FlowId, long Total, long Pending, long Delivering, long Delivered, long Held, long Failed,
    long Deleted, long Drifted, long DeliveredLast24h, DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, long Submissions,
    DeliverySubmissionDto? LastSubmission, string? Interface = null, int Interfaces = 1, long Waiting = 0, string? Partition = null,
    IReadOnlyList<string>? Partitions = null, string? HeaderPartition = null, IReadOnlyList<string>? RedeliverParts = null, long Reverted = 0,
    long Blocked = 0, long Stranded = 0, long Adoptable = 0, long StepsHeld = 0, long Restricted = 0, long RestrictedUntilComplete = 0,
    long AccessRestricted = 0);

/// <summary>
/// One interface of a delivery flow (osdu/docs/reference/flow/interfaces.md): its ledger identity, how it is delivered
/// and why, the mapping and kind it delivers, the record table it reads, what it waits for, and its record counts. A
/// flow in the single form lists one, with no name.
/// <para><c>After</c> is what the document declares; <c>Wave</c>, <c>WaitsFor</c> and <c>NotWaitedFor</c> are the order a
/// run takes, from <c>after:</c> and the relationships the mappings fill. When that order cannot be worked out (a mapping
/// or template the catalog does not hold, interfaces that wait for each other), <c>OrderProblem</c> says why and the
/// order shown is the one <c>after:</c> alone gives.</para>
/// <para><c>Partition</c> is the partition the interface is described in, for a flow that names its partitions: each
/// partition keeps a ledger of its own (osdu/docs/reference/concepts/partitions.md, A ledger per partition). Null for a
/// flow that names none.</para>
/// </summary>
public sealed record DeliveryInterfaceDto(
    string? Interface, Guid FlowId, string Ledger, string Route, string? RouteReason, string Mapping, string? Kind, string RecordObject,
    IReadOnlyList<string> After, DeliveryFlowStatsDto Stats,
    int Wave = 1, IReadOnlyList<DeliveryInterfaceWaitDto>? WaitsFor = null, IReadOnlyList<DeliveryInterfaceWaitDto>? NotWaitedFor = null,
    string? OrderProblem = null, IReadOnlyList<DeliveryParameterDto>? Parameters = null, IReadOnlyList<string>? KeyColumns = null,
    string? Partition = null);

/// <summary>
/// A parameter the flow declares, whose value fills its record scope: its name, whether a value is required, its default,
/// and the record table's column <c>source.record.scope</c> binds it to (null for a parameter the scope does not read, such
/// as one naming the work location), whose values a page can offer for it.
/// </summary>
public sealed record DeliveryParameterDto(string Name, bool Required, string? Default, string? Description, string? ScopeColumn = null);

/// <summary>One interface another waits for, or does not wait for, with where that comes from (<c>after</c> or <c>schema</c>) and why.</summary>
public sealed record DeliveryInterfaceWaitDto(string Interface, string Origin, string Why);

/// <summary>
/// A delivery flow's dashboard card and its interfaces (osdu/docs/reference/flow/interfaces.md): the counts of one
/// ledger, or of a source's interfaces and a flow's partitions added up, and each interface with how it is delivered,
/// the order a run takes it in and what it waits for. Every count is read from the ledger.
/// </summary>
public static partial class DeliveryEndpoints
{
    /// <summary>
    /// A flow's dashboard card: one interface's counts when the request names it (or the flow has one), and the sum of
    /// every interface's otherwise, which is what a source as a whole holds. A flow that works in partitions is counted in
    /// the partition the request names, or, when it names none, as a whole: the ledgers of every partition it serves added
    /// up, as a source's interfaces are.
    /// </summary>
    private static async Task<Results<Ok<DeliveryFlowStatsDto>, ProblemHttpResult>> GetStatsAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return problem!;
        }

        var whole = unbound.Source.Partitioned && string.IsNullOrWhiteSpace(partition);
        var registry = whole && unbound.Source.FollowsRegistry
            ? await partitions.ReadAsync(ct).ConfigureAwait(false)
            : await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        var source = unbound;
        if (!whole)
        {
            (source, var unpartitioned) = await BindKeptAsync(ledger, unbound, partition, registry, ct).ConfigureAwait(false);
            if (unpartitioned is not null)
            {
                return unpartitioned;
            }
        }

        IReadOnlyList<FlowDefinition> flows;
        if (interfaceName is null)
        {
            flows = whole ? source.Source.EveryLedger(registry).ToList() : source.Source.Interfaces;
        }
        else if (Pick(source.Source, interfaceName, out var named) is { } unknown)
        {
            return unknown;
        }
        else
        {
            flows = whole
                ? source.Source.EveryLedger(registry).Where(f => string.Equals(f.Interface, named!.Interface, StringComparison.Ordinal)).ToList()
                : [named!];
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var stats = new List<(FlowDefinition Flow, FlowStats Stats)>(flows.Count);
        foreach (var flow in flows)
        {
            stats.Add((flow, await ledger.StatsAsync(flow.Id, now, ct).ConfigureAwait(false)));
        }

        return TypedResults.Ok(StatsDto(source.Pipeline, stats, await HeaderPartitionAsync(ledger, flows, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// The partition the ledgers of <paramref name="flows"/> are kept under, for flows whose partition is their data-partition-id
    /// header, as the ledger's directory holds it; null for flows bound to a partition, and while none of them has run.
    /// </summary>
    private static async Task<string?> HeaderPartitionAsync(ILedger ledger, IEnumerable<FlowDefinition> flows, CancellationToken ct)
    {
        foreach (var flow in flows)
        {
            if (flow.Partitioned)
            {
                return null;
            }

            if (await ledger.GetLedgerAsync(flow.Id, ct).ConfigureAwait(false) is { Partition: { } kept })
            {
                return kept;
            }
        }

        return null;
    }

    /// <summary>
    /// A flow's interfaces in document order, each with how it is delivered, the order a run takes it in and its counts. A
    /// flow that works in partitions lists them in the partition the request names, or, when it names none, in every
    /// partition it serves (the ones it names, in its order, or every registered one), each row naming its partition: the
    /// one listing that says which partitions a flow delivers to and how each stands.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryInterfaceDto>>, ProblemHttpResult>> ListInterfacesAsync(
        Guid pipelineId, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        EngineContext engine, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return problem!;
        }

        var whole = unbound.Source.Partitioned && string.IsNullOrWhiteSpace(partition);
        var registry = whole && unbound.Source.FollowsRegistry
            ? await partitions.ReadAsync(ct).ConfigureAwait(false)
            : await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        var source = unbound;
        if (!whole)
        {
            (source, var unpartitioned) = await BindKeptAsync(ledger, unbound, partition, registry, ct).ConfigureAwait(false);
            if (unpartitioned is not null)
            {
                return unpartitioned;
            }
        }

        // The kind each mapping fills is what the repository sync read; a mapping it could not read has none yet. The
        // interfaces are described once per partition, so each partition listed has its own rows.
        var listedPartitions = whole ? source.Source.Served(registry).Select(p => (string?)p).ToList() : [source.Source.Partition];
        var described = new List<DeliveryInterface>();
        foreach (var named in listedPartitions)
        {
            described.AddRange(await DeliveryInterfaceCatalog.OfFlowAsync(osdu, source.Pipeline.RepoId, source.Pipeline.Name, named, ct).ConfigureAwait(false));
        }

        var (order, orderProblem) = await OrderAsync(osdu, documents, engine, source, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var listed = whole ? source.Source.EveryLedger(registry).ToList() : source.Source.Interfaces;
        var result = new List<DeliveryInterfaceDto>(listed.Count);
        foreach (var flow in listed)
        {
            var stats = await ledger.StatsAsync(flow.Id, now, ct).ConfigureAwait(false);
            var kind = described.FirstOrDefault(d => d.LedgerFlowId == flow.Id)?.Kind;
            var name = flow.Interface ?? string.Empty;
            result.Add(new DeliveryInterfaceDto(
                flow.Interface, flow.Id, flow.LedgerName, DeliveryProtocols.Name(flow.Target.Protocol), flow.RouteReason, flow.Render.Mapping,
                string.IsNullOrEmpty(kind) ? null : kind, flow.Source.Record.Object, flow.After,
                StatsDto(source.Pipeline, [(flow, stats)], await HeaderPartitionAsync(ledger, [flow], ct).ConfigureAwait(false)),
                order.WaveOf(name),
                order.WaitsFor(name).Select(d => Wait(d, d.DependsOn)).ToList(),
                order.NotWaitedFor.Where(d => string.Equals(d.Interface, name, StringComparison.OrdinalIgnoreCase)).Select(d => Wait(d, d.DependsOn)).ToList(),
                orderProblem,
                flow.Parameters.Select(p => new DeliveryParameterDto(
                    p.Key, p.Value.Required, p.Value.Default, p.Value.Description,
                    flow.Source.Record.Scope.FirstOrDefault(s => string.Equals(s.Value, p.Key, StringComparison.Ordinal)).Key)).ToList(),
                flow.Source.Record.Key,
                flow.Partition));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryInterfaceDto>>(result);
    }

    private static DeliveryInterfaceWaitDto Wait(InterfaceDependency dependency, string other)
        => new(other, dependency.Origin == DependencyOrigin.After ? "after" : "schema", dependency.Why);

    /// <summary>
    /// The order a run takes the source's interfaces in, worked out as the run's preflight works it out: from <c>after:</c>
    /// and the relationships the mappings fill, read from the repository's synced mappings and the templates the template
    /// folders its last sync recorded on each mapping's path hold. When a mapping or template is missing, or the interfaces
    /// wait for each other, the order <c>after:</c> alone gives is returned with the reason.
    /// </summary>
    private static async Task<(InterfaceOrderPlan Order, string? Problem)> OrderAsync(
        OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, SourceContext source, CancellationToken ct)
    {
        var names = source.Source.Interfaces.Select(f => f.Interface ?? string.Empty).ToList();
        var declared = InterfaceOrder.Declared(source.Source);
        if (names.Count < 2)
        {
            return (InterfaceOrder.Plan(names, declared, []), null);
        }

        var references = source.Source.Interfaces.Select(f => f.Render.Mapping).Distinct(StringComparer.Ordinal).ToList();
        var rows = await osdu.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == source.Pipeline.RepoId && references.Contains(m.Reference) && m.Status == "valid")
            .Select(m => new { m.Reference, m.Yaml, m.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var schemas = new List<InterfaceSchema>(names.Count);
        Catalog.SyncedTemplates? folders = null;
        foreach (var flow in source.Source.Interfaces)
        {
            var row = rows.FirstOrDefault(r => r.Reference == flow.Render.Mapping);
            if (row is null)
            {
                return (InterfaceOrder.Plan(names, declared, []), $"Mapping {flow.Render.Mapping} of interface '{flow.Interface}' is not among the repository's valid mappings, so only after: orders the interfaces.");
            }

            try
            {
                folders ??= await Catalog.SyncedTemplates.ReadAsync(osdu, engine.RepositoryFolders, source.Pipeline.RepoId, ct).ConfigureAwait(false);
                var mapping = documents.ParseMapping(row.Yaml, row.RelativePath, folders.For(row.RelativePath));
                var resolved = await folders.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
                if (resolved.Schema is not { } schema)
                {
                    return (InterfaceOrder.Plan(names, declared, []), $"Only after: orders the interfaces, since the template of mapping {mapping.Reference} of interface '{flow.Interface}' cannot be read: {resolved.Problem}");
                }

                schemas.Add(InterfaceSchemas.Describe(flow.Interface ?? string.Empty, mapping, OsduTemplate.From(schema)));
            }
            catch (FlowValidationException ex)
            {
                return (InterfaceOrder.Plan(names, declared, []), $"Mapping {flow.Render.Mapping} of interface '{flow.Interface}' could not be read ({ex.Message}), so only after: orders the interfaces.");
            }
        }

        try
        {
            return (InterfaceOrder.Plan(names, declared, schemas), null);
        }
        catch (DeliveryException ex)
        {
            return (InterfaceOrder.Plan(names, declared, []), ex.Message + " Until then a run refuses to start, and only after: orders the interfaces here.");
        }
    }

    /// <summary>
    /// The dashboard card of one ledger, or of several added up: a source's interfaces, a flow's partitions, or both. It
    /// names the interface and the partition when every ledger counted shares one, and lists the partitions it covers.
    /// </summary>
    private static DeliveryFlowStatsDto StatsDto(CatalogPipeline pipeline, IReadOnlyList<(FlowDefinition Flow, FlowStats Stats)> stats, string? headerPartition)
    {
        var one = stats.Count == 1 ? stats[0].Flow : null;
        var interfaces = stats.Select(s => s.Flow.Interface ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        var partitions = stats.Select(s => s.Flow.Partition).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var last = stats.Select(s => s.Stats.LastSubmission).OfType<SubmissionState>().OrderByDescending(s => s.ReceivedUtc).FirstOrDefault();
        return new DeliveryFlowStatsDto(
            pipeline.Id, pipeline.Name, one?.Id ?? Guid.Empty,
            stats.Sum(s => s.Stats.Total), stats.Sum(s => s.Stats.Pending), stats.Sum(s => s.Stats.Delivering), stats.Sum(s => s.Stats.Delivered),
            stats.Sum(s => s.Stats.Held), stats.Sum(s => s.Stats.Failed), stats.Sum(s => s.Stats.Deleted), stats.Sum(s => s.Stats.Drifted),
            stats.Sum(s => s.Stats.DeliveredLast24h), stats.Max(s => s.Stats.LastDeliveredUtc), stats.Max(s => s.Stats.LastVerifiedUtc),
            stats.Sum(s => s.Stats.Submissions), last is null ? null : ToDto(last),
            interfaces.Count == 1 && stats.Count > 0 ? stats[0].Flow.Interface : null,
            interfaces.Count,
            stats.Sum(s => s.Stats.Waiting),
            partitions.Count == 1 ? partitions[0] : null,
            partitions.Count > 0 ? partitions : null,
            headerPartition,
            one is null ? null : RedeliverScopes.For(one),
            stats.Sum(s => s.Stats.Reverted),
            stats.Sum(s => s.Stats.Blocked),
            stats.Sum(s => s.Stats.Stranded),
            stats.Sum(s => s.Stats.Adoptable),
            stats.Sum(s => s.Stats.StepsHeld),
            stats.Sum(s => s.Stats.Restricted),
            stats.Sum(s => s.Stats.RestrictedUntilComplete),
            stats.Sum(s => s.Stats.AccessRestricted));
    }
}
