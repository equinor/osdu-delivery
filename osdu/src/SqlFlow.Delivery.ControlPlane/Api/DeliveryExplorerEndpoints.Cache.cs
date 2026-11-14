using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A drafted cached type to write as YAML and check.</summary>
public sealed record DeliveryCacheComposeRequest(CacheTypeDraft? Draft);

/// <summary>A drafted cached type whose first records are read as the cache would keep them.</summary>
public sealed record DeliveryCachePreviewRequest(CacheTypeDraft? Draft);

/// <summary>One value a drafted type keeps of each record: the name it is cached under, where it is read, and whether it is an attribute.</summary>
public sealed record DeliveryCacheDraftFieldDto(string Name, string Path, bool Attribute);

/// <summary>A drafted type as the loader read it: the name a mapping reads it by, its entity type, what it searches and what it keeps.</summary>
public sealed record DeliveryCacheDraftTypeDto(
    string Name, string EntityType, string Kind, string? Query, bool FetchRecords, string OnChange, IReadOnlyList<DeliveryCacheDraftFieldDto> Fields);

/// <summary>
/// A drafted cached type as the builder shows it: the item a cache flow lists under <c>types</c>, its lines, the type the
/// loader read (null until the item loads), what is wrong with it, whether it loads with no error, and the cache flows of
/// the catalog that declare a type of the same name already, whose paths a partition's cache keeps together with this one's.
/// </summary>
public sealed record DeliveryCacheComposeDto(
    string Yaml, IReadOnlyList<string> Lines, DeliveryCacheDraftTypeDto? Type, IReadOnlyList<DimensionDraftIssue> Issues, bool Valid,
    IReadOnlyList<string> DeclaredBy);

/// <summary>
/// The explorer's cache builder (osdu/docs/reference/concepts/explorer-cache-builder.md): what a person picks from the
/// records OSDU holds, written as the item a cache flow lists under <c>types</c> and checked by the same loader a flow's
/// own type is, as the dimension builder writes a dimension. A compose writes the draft and checks it, against the cache
/// flows of the catalog too, whose type of the same name it must agree with; a preview reads the first records through the
/// partition's own connection, as the explorer reads it, and makes them what a refresh would cache. Nothing is saved: the
/// YAML is the builder's output.
/// </summary>
public static partial class DeliveryExplorerEndpoints
{
    private static void MapCacheBuilderEndpoints(RouteGroupBuilder delivery)
    {
        delivery.MapPost("/explorer/cache/compose", ComposeCacheTypeAsync).WithName("ComposeDeliveryCacheTypeDraft");
        delivery.MapPost("/explorer/cache/preview", PreviewCacheTypeAsync).WithName("PreviewDeliveryCacheTypeDraft");
    }

    /// <summary>
    /// A drafted cached type written as YAML and checked: by the document loader, as a flow's own item is read, then against
    /// the cache flows of the catalog, since every cache flow of a partition fills the one type of a name together.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCacheComposeDto>, ProblemHttpResult>> ComposeCacheTypeAsync(
        DeliveryCacheComposeRequest? body, CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        if (body?.Draft is not { } draft)
        {
            return DeliveryEndpoints.Invalid("Send the draft to write and check.");
        }

        if (CacheDraftProblem(draft) is { } invalid)
        {
            return DeliveryEndpoints.Invalid(invalid);
        }

        var (item, type, issues) = LoadCacheDraft(draft, documents);
        IReadOnlyList<string> declaredBy = [];
        if (type is not null)
        {
            (declaredBy, var shared) = SharedType(type, null, await CatalogCacheFlowsAsync(db, documents, null, ct).ConfigureAwait(false));
            issues.AddRange(shared);
        }

        var distinct = issues.DistinctBy(i => (i.Severity, i.Message, i.Target)).ToList();
        return TypedResults.Ok(new DeliveryCacheComposeDto(
            item,
            item.TrimEnd('\n').Split('\n'),
            type is null ? null : TypeDto(type),
            distinct,
            type is not null && distinct.All(i => i.Severity != DimensionDraftIssue.Error),
            declaredBy));
    }

    /// <summary>
    /// The first records of a drafted cached type as the cache would keep them, through the partition's own connection, as
    /// the explorer reads it: the draft written and read back as a compose reads it, and refused, with the first thing to put
    /// right, while a refresh would refuse it.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> PreviewCacheTypeAsync(
        DeliveryCachePreviewRequest? body, [FromQuery] string? partition, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (body?.Draft is not { } draft)
        {
            return DeliveryEndpoints.Invalid("Send the draft whose records to preview.");
        }

        if (CacheDraftProblem(draft) is { } invalid)
        {
            return DeliveryEndpoints.Invalid(invalid);
        }

        var (item, type, issues) = LoadCacheDraft(draft, documents);
        var error = issues.FirstOrDefault(i => i.Severity == DimensionDraftIssue.Error);
        if (type is null || error is not null)
        {
            return DeliveryEndpoints.Invalid($"The records are previewed once a refresh would cache them: {error?.Message ?? "the draft does not load."}");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            LongArgument.Put(arguments, ExploreOperation.ItemArgument, item);
        }
        catch (SqlFlowException ex)
        {
            return DeliveryEndpoints.Invalid(ex.Message);
        }

        return await ExploreAsync(ExploreOperation.CachePreviewAction, arguments, partition, partitions, config, direct, loggers, request, user, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A drafted cached type written as its item and read back: the item, the type the document loader reads (null while the
    /// draft is incomplete or the loader refuses it), and what is wrong with it.
    /// </summary>
    private static (string Item, ReferenceTypeSpec? Type, List<DimensionDraftIssue> Issues) LoadCacheDraft(CacheTypeDraft draft, DeliveryDocumentLoader documents)
    {
        var item = CacheTypeBuilder.ToYaml(draft);
        var issues = new List<DimensionDraftIssue>(CacheTypeBuilder.Incomplete(draft));
        if (issues.Count > 0)
        {
            return (item, null, issues);
        }

        try
        {
            return (item, documents.ParseCache(CacheTypeBuilder.Document(item), CacheTypeBuilder.CheckSource).Types[0], issues);
        }
        catch (FlowValidationException ex)
        {
            issues.Add(CacheTypeBuilder.FromLoader(ex.Message));
            return (item, null, issues);
        }
    }

    /// <summary>Why a draft is too large to be read, part by part; null when it is not. What each part may hold is the loader's to say.</summary>
    internal static string? CacheDraftProblem(CacheTypeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var texts = new (string Part, string? Text)[] { ("name", draft.Name), ("kind", draft.Kind), ("query", draft.Query), ("onChange", draft.OnChange) };
        if (texts.FirstOrDefault(t => t.Text is { Length: > DimensionBuilder.MaxTextLength }) is { Part: { } part })
        {
            return string.Create(CultureInfo.InvariantCulture, $"The draft's {part} is longer than the {DimensionBuilder.MaxTextLength:N0} characters the builder reads.");
        }

        var lists = new (string Part, int Count)[] { ("fields", draft.Fields?.Count ?? 0), ("attributes", draft.Attributes?.Count ?? 0) };
        if (lists.FirstOrDefault(l => l.Count > DimensionBuilder.MaxListLength) is { Part: { } list })
        {
            return $"The draft's {list} lists more than the {DimensionBuilder.MaxListLength} entries the builder reads.";
        }

        var nested = (draft.Fields ?? []).Where(f => f is not null).SelectMany(f => new[] { f.Path, f.As })
            .Concat((draft.Attributes ?? []).Where(a => a is not null).SelectMany(a => (a.Steps ?? []).Append(a.Name)));
        if (nested.Any(text => text is { Length: > DimensionBuilder.MaxTextLength }))
        {
            return string.Create(CultureInfo.InvariantCulture, $"A path or a name of the draft is longer than the {DimensionBuilder.MaxTextLength:N0} characters the builder reads.");
        }

        return (draft.Attributes ?? []).Any(a => a?.Steps is { Count: > DimensionBuilder.MaxListLength })
            ? $"An attribute of the draft lists more than the {DimensionBuilder.MaxListLength} paths the builder reads."
            : null;
    }

    private static DeliveryCacheDraftTypeDto TypeDto(ReferenceTypeSpec type)
        => new(
            type.Name, type.EntityType, type.Kind ?? string.Empty, type.Query == "*" ? null : type.Query, type.FetchRecords,
            type.OnChange == CacheChangeMode.Approve ? "approve" : "auto",
            [.. type.Fields.Select(f => new DeliveryCacheDraftFieldDto(f.Name, f.Path, f.IsAttribute))]);

    /// <summary>The cache flows the catalog holds, as the loader reads each; one whose copy does not parse declares nothing a refresh would cache, and is left out.</summary>
    internal static async Task<List<CacheDefinition>> CatalogCacheFlowsAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, string? except, CancellationToken ct)
    {
        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == CacheDefinition.FlowTypeName && p.Active && (except == null || p.Name != except))
            .OrderBy(p => p.Name)
            .Select(p => new { p.Yaml, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var read = new List<CacheDefinition>(flows.Count);
        foreach (var flow in flows)
        {
            try
            {
                read.Add(documents.ParseCache(flow.Yaml, flow.RelativePath));
            }
            catch (FlowValidationException)
            {
                // Its own page says why it does not load.
            }
        }

        return read;
    }

    /// <summary>
    /// The cache flows among <paramref name="others"/> declaring a type of <paramref name="type"/>'s name, and where the type
    /// disagrees with one of them. A partition has one cache, which every cache flow filling it fills together: the type of
    /// a name is one type there, its paths added up, so two declarations of one name must be of one entity type and read each
    /// value they both name alike (osdu/docs/reference/concepts/partition-cache.md). A refresh into a partition both fill
    /// refuses a disagreement: an error where the type's own flow (<paramref name="owner"/>) surely shares a partition with
    /// the other, a warning where it may, and for a draft, which is in no flow yet.
    /// </summary>
    internal static (IReadOnlyList<string> DeclaredBy, List<DimensionDraftIssue> Issues) SharedType(
        ReferenceTypeSpec type, CacheDefinition? owner, IReadOnlyList<CacheDefinition> others)
    {
        var declaredBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<DimensionDraftIssue>();
        foreach (var declared in others)
        {
            foreach (var other in declared.Types.Where(t => string.Equals(t.Name, type.Name, StringComparison.OrdinalIgnoreCase)))
            {
                declaredBy.Add(declared.Name);
                var severity = owner is not null && SurelyShare(owner, type, declared, other) ? DimensionDraftIssue.Error : DimensionDraftIssue.Warning;
                if (!string.Equals(other.EntityType, type.EntityType, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new DimensionDraftIssue(
                        severity,
                        $"Cache flow {declared.Name} caches a type named {other.Name} too, from {other.Describe()}, and this one is cached from {type.Describe()}. A partition's cache keeps one type of a name, so a refresh into a partition both flows fill is refused: give this type another name.",
                        "name"));
                    continue;
                }

                foreach (var field in type.Fields)
                {
                    var theirs = other.Fields.FirstOrDefault(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                    if (theirs is not null && !string.Equals(theirs.Path, field.Path, StringComparison.Ordinal))
                    {
                        issues.Add(new DimensionDraftIssue(
                            severity,
                            $"Cache flow {declared.Name} caches {other.Name}.{field.Name} from {theirs.Path}, and this reads it from {field.Path}. A partition's cache keeps one value under a name, so in a partition both flows fill the two must read it alike: read it as that flow does, or keep it under another name.",
                            field.IsAttribute ? $"attributes.{field.Name}" : "fields"));
                    }
                }
            }
        }

        return ([.. declaredBy], issues);
    }

    /// <summary>
    /// Whether two types of two cache flows are surely filled into one partition: both flows name their partitions and the
    /// types are built for one in common, or both serve every registered partition and neither type narrows them.
    /// </summary>
    private static bool SurelyShare(CacheDefinition a, ReferenceTypeSpec typeA, CacheDefinition b, ReferenceTypeSpec typeB)
    {
        if (a.DeclaresPartitions && b.DeclaresPartitions)
        {
            var mine = typeA.Partitions.Count > 0 ? typeA.Partitions : a.Partitions;
            var theirs = typeB.Partitions.Count > 0 ? typeB.Partitions : b.Partitions;
            return mine.Intersect(theirs, StringComparer.OrdinalIgnoreCase).Any();
        }

        return a.FollowsRegistry && b.FollowsRegistry && typeA.Partitions.Count == 0 && typeB.Partitions.Count == 0;
    }
}
