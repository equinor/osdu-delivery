using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A type a cache flow declares: the name a mapping reads it by, where its records come from (<c>osdu</c>, <c>table</c>,
/// <c>dictionary</c> or <c>dimension</c>, and the kind, table, dictionary or dimension itself), and the names it keeps
/// each value under.
/// </summary>
public sealed record DeliveryCacheFlowTypeDto(string Name, string Origin, string From, IReadOnlyList<string> Fields);

/// <summary>
/// A cache flow of the catalog, as a new one is modelled on it: where its file is, its batch, the partitions it fills, its
/// parameters, the connection its table types read, the types it declares, and its document without its <c>types</c> (the
/// source, its auth, partitions, retention and schedule, as written). <c>Problem</c> says why the document does not load,
/// when it does not.
/// </summary>
public sealed record DeliveryCacheFlowPatternDto(
    Guid PipelineId, string Name, string? Description, Guid RepoId, string Repository, string RelativePath, string? Batch,
    IReadOnlyList<string> Partitions, IReadOnlyList<string> Parameters, string? Connection, IReadOnlyList<DeliveryCacheFlowTypeDto> Types,
    string Envelope, bool EnvelopeCut, string? Problem);

/// <summary>
/// What a new cache flow is modelled on: the cache flows the catalog holds (at most
/// <see cref="DeliveryDimensionEndpoints.MaxPatternFlows"/>, with how many more there are), and the connections the estate's
/// flows keep their data through, which a table type reads.
/// </summary>
public sealed record DeliveryCacheFlowPatternsDto(
    IReadOnlyList<DeliveryCacheFlowPatternDto> Flows, int MoreFlows, IReadOnlyList<DeliveryDimensionFlowConnectionDto> Connections);

/// <summary>Something the check found: an error keeps the flow from loading or refreshing, a warning does not; the type and the part of it the finding is about.</summary>
public sealed record DeliveryCacheFlowIssueDto(string Severity, string Message, string? Type = null, string? Target = null);

/// <summary>
/// A whole cache flow document checked: whether it loads, whether it loads and refreshes with no error, its name, the
/// partitions it names, its types, the catalog's flow of the same name, and every finding.
/// </summary>
public sealed record DeliveryCacheFlowCheckDto(
    bool Loads, bool Valid, string? Name, IReadOnlyList<string> Partitions, IReadOnlyList<DeliveryCacheFlowTypeDto> Types,
    DeliveryDimensionFlowExistingDto? Existing, IReadOnlyList<DeliveryCacheFlowIssueDto> Issues);

/// <summary>
/// Writing a whole cache flow file (osdu/docs/reference/guides/cache-flow-file.md), as a dimension flow file is written
/// (<see cref="DeliveryDimensionEndpoints"/>): the patterns a new flow follows, read from the cache flows and the
/// connections the catalog holds, and a check of the whole document by the loader a flow is read by, the types the
/// catalog's other cache flows declare, the dimensions its dimension types name and the registry's partitions. Both read;
/// nothing is written anywhere.
/// </summary>
public static class DeliveryCacheFlowEndpoints
{
    internal static void Map(RouteGroupBuilder delivery)
    {
        delivery.MapGet("/cache/flow-patterns", GetFlowPatternsAsync).WithName("GetDeliveryCacheFlowPatterns");
        delivery.MapPost("/cache/flow-check", CheckFlowAsync).WithName("CheckDeliveryCacheFlow");
    }

    /// <summary>
    /// The patterns a new cache flow follows: the catalog's cache flows, each with its envelope as written, and the
    /// connections the estate's flows read and write their data through.
    /// </summary>
    private static async Task<Ok<DeliveryCacheFlowPatternsDto>> GetFlowPatternsAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var repositories = await db.Repos.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active)
            .Select(p => new { p.Id, p.RepoId, p.Name, p.Kind, p.Batch, p.RelativePath, p.SourceServer, p.TargetServer, p.LastSeenUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        // The flows a sync saw last come first, as the estate writes its flows now; then by repository and file.
        var cacheFlows = pipelines.Where(p => string.Equals(p.Kind, CacheDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastSeenUtc)
            .ThenBy(p => repositories.GetValueOrDefault(p.RepoId) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = cacheFlows.Take(DeliveryDimensionEndpoints.MaxPatternFlows).Select(p => p.Id).ToList();
        var texts = await db.Pipelines.AsNoTracking()
            .Where(p => shown.Contains(p.Id))
            .Select(p => new { p.Id, p.Yaml })
            .ToDictionaryAsync(p => p.Id, p => p.Yaml, ct).ConfigureAwait(false);

        var flows = new List<DeliveryCacheFlowPatternDto>(shown.Count);
        foreach (var pipeline in cacheFlows.Take(DeliveryDimensionEndpoints.MaxPatternFlows))
        {
            var yaml = texts.GetValueOrDefault(pipeline.Id) ?? string.Empty;
            var envelope = DeliveryDimensionEndpoints.EnvelopeOf(yaml, "types");
            var cut = envelope.Length > DeliveryDimensionEndpoints.MaxEnvelopeLength;
            CacheDefinition? definition = null;
            string? problem = null;
            try
            {
                definition = documents.ParseCache(yaml, pipeline.RelativePath);
            }
            catch (FlowValidationException ex)
            {
                problem = DeliveryDimensionEndpoints.WithoutSource(ex.Message, pipeline.RelativePath);
            }

            flows.Add(new DeliveryCacheFlowPatternDto(
                pipeline.Id,
                pipeline.Name,
                definition?.Description,
                pipeline.RepoId,
                repositories.GetValueOrDefault(pipeline.RepoId) ?? string.Empty,
                pipeline.RelativePath,
                pipeline.Batch,
                definition?.Partitions ?? [],
                definition?.Parameters.Keys.Order(StringComparer.Ordinal).ToList() ?? [],
                definition?.Source.Connection,
                definition?.Types.Select(TypeDto).ToList() ?? [],
                cut ? envelope[..DeliveryDimensionEndpoints.MaxEnvelopeLength] : envelope,
                cut,
                problem));
        }

        var connections = DeliveryDimensionEndpoints.ConnectionsOf(pipelines.Select(p => (p.SourceServer, p.TargetServer, p.Kind)));
        return TypedResults.Ok(new DeliveryCacheFlowPatternsDto(
            flows, Math.Max(0, cacheFlows.Count - DeliveryDimensionEndpoints.MaxPatternFlows), connections));
    }

    /// <summary>
    /// A whole cache flow document checked as a sync and a refresh would take it: read by the loader, its name against the
    /// catalog's flows, its partitions against the registry, each type against the type of that name the catalog's other
    /// cache flows declare, each dimension type against the dimension flows the catalog holds, and the connection its table
    /// types read against those the estate's flows name.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCacheFlowCheckDto>, ProblemHttpResult>> CheckFlowAsync(
        DeliveryDimensionFlowCheckRequest? body, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry registry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Yaml))
        {
            return DeliveryEndpoints.Invalid("Send the whole cache flow document to check.");
        }

        if (body.Yaml.Length > DeliveryDimensionEndpoints.MaxFlowYamlLength)
        {
            return DeliveryEndpoints.Invalid(string.Create(
                CultureInfo.InvariantCulture, $"The document is longer than the {DeliveryDimensionEndpoints.MaxFlowYamlLength:N0} characters the check reads."));
        }

        var source = string.IsNullOrWhiteSpace(body.Path) ? "the flow" : body.Path.Trim();
        var issues = new List<DeliveryCacheFlowIssueDto>();
        CacheDefinition definition;
        try
        {
            definition = documents.ParseCache(body.Yaml, source);
        }
        catch (FlowValidationException ex)
        {
            issues.Add(new DeliveryCacheFlowIssueDto(DimensionDraftIssue.Error, DeliveryDimensionEndpoints.WithoutSource(ex.Message, source)));
            return TypedResults.Ok(new DeliveryCacheFlowCheckDto(false, false, null, [], [], null, issues));
        }

        var existing = await DeliveryDimensionEndpoints.ExistingFlowAsync(definition.Name, db, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            issues.Add(string.Equals(existing.Kind, CacheDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase)
                ? new DeliveryCacheFlowIssueDto(
                    DimensionDraftIssue.Warning,
                    $"Flow '{definition.Name}' is already in repository {existing.Repository} at {existing.RelativePath}: this document replaces it there, types and all. Name a new flow otherwise.",
                    Target: "name")
                : new DeliveryCacheFlowIssueDto(
                    DimensionDraftIssue.Error,
                    $"A {existing.Kind} flow named '{definition.Name}' is already in repository {existing.Repository} at {existing.RelativePath}; a flow's name is its identity, so name this one otherwise.",
                    Target: "name"));
        }

        var named = definition.Partitions.Concat(definition.Types.SelectMany(t => t.Partitions)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        issues.AddRange((await DeliveryDimensionEndpoints.UnregisteredPartitionsAsync(named, registry, ct).ConfigureAwait(false))
            .Select(message => new DeliveryCacheFlowIssueDto(DimensionDraftIssue.Warning, message, Target: "partitions")));

        var others = await DeliveryExplorerEndpoints.CatalogCacheFlowsAsync(db, documents, definition.Name, ct).ConfigureAwait(false);
        foreach (var type in definition.Types)
        {
            issues.AddRange(DeliveryExplorerEndpoints.SharedType(type, definition, others).Issues
                .Select(i => new DeliveryCacheFlowIssueDto(i.Severity, i.Message, type.Name, i.Target)));
        }

        issues.AddRange(await DimensionTypeIssuesAsync(definition, db, documents, ct).ConfigureAwait(false));
        issues.AddRange(await ConnectionIssuesAsync(definition, db, ct).ConfigureAwait(false));

        var distinct = issues.DistinctBy(i => (i.Severity, i.Message, i.Type, i.Target)).ToList();
        return TypedResults.Ok(new DeliveryCacheFlowCheckDto(
            true,
            distinct.All(i => i.Severity != DimensionDraftIssue.Error),
            definition.Name,
            definition.Partitions,
            definition.Types.Select(TypeDto).ToList(),
            existing,
            distinct));
    }

    private static DeliveryCacheFlowTypeDto TypeDto(ReferenceTypeSpec type)
        => new(
            type.Name,
            type.Origin switch
            {
                CacheOrigin.Table => "table",
                CacheOrigin.Dictionary => "dictionary",
                CacheOrigin.Dimension => "dimension",
                _ => "osdu",
            },
            type.Origin switch
            {
                CacheOrigin.Table => type.Table ?? string.Empty,
                CacheOrigin.Dictionary => type.Dictionary ?? string.Empty,
                CacheOrigin.Dimension => $"{type.Dimension} of {type.DimensionFlow}",
                _ => type.Kind ?? string.Empty,
            },
            [.. type.Fields.Select(f => f.Name)]);

    /// <summary>
    /// The dimension types naming a dimension no dimension flow of the catalog declares: a refresh finds no build of it to
    /// cache, so each is a warning naming what the catalog does hold.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryCacheFlowIssueDto>> DimensionTypeIssuesAsync(
        CacheDefinition definition, CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var types = definition.Types.Where(t => t.Origin == CacheOrigin.Dimension).ToList();
        if (types.Count == 0)
        {
            return [];
        }

        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active)
            .Select(p => new { p.Name, p.Yaml, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var declared = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in flows)
        {
            try
            {
                declared[flow.Name] = documents.ParseDimension(flow.Yaml, flow.RelativePath).Dimensions.Select(d => d.Name).ToList();
            }
            catch (FlowValidationException)
            {
                // The flow is in the catalog, and what it declares is not known while its copy does not load.
                declared[flow.Name] = null;
            }
        }

        var issues = new List<DeliveryCacheFlowIssueDto>();
        foreach (var type in types)
        {
            if (!declared.TryGetValue(type.DimensionFlow ?? string.Empty, out var dimensions))
            {
                issues.Add(new DeliveryCacheFlowIssueDto(
                    DimensionDraftIssue.Warning,
                    declared.Count == 0
                        ? $"Type {type.Name} holds dimension {type.Dimension} of flow {type.DimensionFlow}, and the catalog holds no dimension flow: a refresh caches a dimension's last build, so the flow has to be synced and built first."
                        : $"Type {type.Name} holds dimension {type.Dimension} of flow {type.DimensionFlow}, which is no dimension flow of the catalog; it holds {string.Join(", ", declared.Keys.Order(StringComparer.OrdinalIgnoreCase))}.",
                    type.Name,
                    "dimensionFlow"));
            }
            else if (dimensions is not null && !dimensions.Contains(type.Dimension ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new DeliveryCacheFlowIssueDto(
                    DimensionDraftIssue.Warning,
                    $"Type {type.Name} holds dimension {type.Dimension}, which dimension flow {type.DimensionFlow} does not declare; it declares {string.Join(", ", dimensions)}.",
                    type.Name,
                    "dimension"));
            }
        }

        return issues;
    }

    /// <summary>
    /// The connection the flow's table types read, when it is a reference no flow of the catalog names: a refresh must find it
    /// set, so the flow is told the connections the estate's flows keep their data through.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryCacheFlowIssueDto>> ConnectionIssuesAsync(CacheDefinition definition, CatalogDbContext db, CancellationToken ct)
    {
        var reference = definition.Source.Connection;
        if (reference is null || !reference.StartsWith("${", StringComparison.Ordinal) || definition.Types.All(t => t.Origin != CacheOrigin.Table)
            || string.Equals(reference, DeliveryDimensionEndpoints.ModuleConnection, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var written = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && p.TargetServer != null && p.TargetServer != ServerIdentity.FileSystem)
            .Select(p => p.TargetServer!)
            .ToListAsync(ct).ConfigureAwait(false);
        var read = await DeliveryDimensionEndpoints.ReadServersAsync(db, ct).ConfigureAwait(false);
        if (written.Concat(read).Contains(reference, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var data = written
            .Where(s => !string.Equals(s, DeliveryDimensionEndpoints.ModuleConnection, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
        return
        [
            new DeliveryCacheFlowIssueDto(
                DimensionDraftIssue.Warning,
                data.Count == 0
                    ? $"source.connection {reference} is named by no flow of the catalog: a refresh resolves it from the partition's values or the node's environment, so it must be set there."
                    : $"source.connection {reference} is named by no flow of the catalog, whose flows write their data through {string.Join(", ", data)}: a refresh resolves it from the partition's values or the node's environment, so it must be set there.",
                Target: "source"),
        ];
    }
}
