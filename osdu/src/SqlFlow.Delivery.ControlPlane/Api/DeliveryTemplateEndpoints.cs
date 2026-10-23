using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A file of a synced repository's template folder (osdu/docs/reference/concepts/templates.md, Template folders), as the
/// repository's last sync found it: the template it holds, or why it holds none. The file is the template: whoever reads
/// the template reads the file, in the folder the repository was synced into, and the database holds none of it.
/// </summary>
/// <param name="RepoId">The repository that holds it.</param>
/// <param name="Repository">The repository's name.</param>
/// <param name="Path">The file's path in the repository.</param>
/// <param name="Folder">The template folder it is in.</param>
/// <param name="Kind">The kind it holds; null when it holds no template.</param>
/// <param name="Version">The template version it holds, the hash of its schema; null when it holds none.</param>
/// <param name="Problem">Why it holds no template; null when it holds one.</param>
/// <param name="SeenUtc">When the repository's last sync saw it.</param>
/// <param name="Parent">
/// The kind the template is linked to as its parent (osdu/docs/reference/concepts/templates.md, Linked templates); null for
/// a template linked to none.
/// </param>
public sealed record DeliveryTemplateDto(
    Guid RepoId, string Repository, string Path, string Folder, string? Kind, string? Version, string? Problem, DateTime SeenUtc, string? Parent);

/// <summary>The repository file a template laid out was read from.</summary>
public sealed record DeliveryTemplateFileDto(Guid RepoId, string Repository, string Path);

/// <summary>
/// What a template a document of a synced repository names settles to, from the template folders the repository's last
/// sync recorded on the document's path: the version and the file holding it, or why none does. <c>SuggestedPath</c> is
/// where a file of the template goes for the document to read it: the template folder of the document's project, beside
/// the folder the document is in.
/// </summary>
public sealed record DeliveryTemplateResolutionDto(bool Found, string Kind, string? Version, string? File, string? Problem, string SuggestedPath);

/// <summary>One template variable as the Templates page and the mapping builder show it.</summary>
public sealed record DeliveryTemplateVariableDto(
    string Path, string Shape, string Type, string? ItemType, string? Format, bool Required, string Role, IReadOnlyList<string> Relationships,
    string? Pattern, string? UnitContext, string? Title, string? Description, string? KeyValueType, bool Nested, IReadOnlyList<string> CacheTypes, bool Open);

/// <summary>
/// A template laid out variable by variable. <c>File</c> is the repository file it was read from, null for a schema of the
/// OSDU data definitions being looked at; <c>CacheTypes</c> on a variable names the types of the partition's cache it can be
/// read from, when a partition was named.
/// </summary>
public sealed record DeliveryTemplateDetailDto(
    string Kind, string Version, string? Title, string? Description, DeliveryTemplateFileDto? File, IReadOnlyList<DeliveryTemplateVariableDto> Variables)
{
    /// <summary>
    /// The kind the template is linked to as its parent (osdu/docs/reference/concepts/templates.md, Linked templates), read
    /// from its <c>x-osdu-inheriting-from-kind</c>; null for a template linked to none.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>
    /// The kinds of the repository's templates linked to this template's entity type as their parent, in ordinal order: the
    /// children a mapping of this template can link with <c>$child</c> (osdu/docs/reference/flow/mapping-child.md). Empty for a
    /// template none is linked to, and for one that is not a repository's file.
    /// </summary>
    public IReadOnlyList<string> Children { get; init; } = [];
}

/// <summary>A release of the OSDU data definitions: its tag, the commit it names, when that was committed, and its schema folder on the web.</summary>
public sealed record DeliveryOsduReleaseDto(string Name, string Commit, DateTimeOffset? PublishedUtc, Uri WebUrl, bool Local);

/// <summary>The releases of the OSDU data definitions, newest first, each marked when it is in the local copy, and when the list was read from the repository.</summary>
public sealed record DeliveryOsduReleasesDto(DateTimeOffset? SyncedUtc, IReadOnlyList<DeliveryOsduReleaseDto> Releases);

/// <summary>A sync: the release to have in the local copy afterwards, the newest when none is named.</summary>
public sealed record DeliveryOsduSyncRequest(string? Release);

/// <summary>What a sync did: the release list as read just now, and the releases it downloaded.</summary>
public sealed record DeliveryOsduSyncDto(DateTimeOffset SyncedUtc, IReadOnlyList<DeliveryOsduReleaseDto> Releases, IReadOnlyList<string> Downloaded);

/// <summary>A record schema a release publishes, with its file in the release and the file's page on the web.</summary>
public sealed record DeliveryOsduSchemaDto(string Kind, string EntityType, string Version, string? Status, string Path, Uri WebUrl);

/// <summary>Every record schema one release of the OSDU data definitions publishes.</summary>
public sealed record DeliveryOsduSchemaIndexDto(DeliveryOsduReleaseDto Release, IReadOnlyList<DeliveryOsduSchemaDto> Schemas);

/// <summary>
/// A kind's schema bundled from a release of the OSDU data definitions: <c>Version</c> is the template version its file
/// holds, <c>Origin</c> where it came from (the release, its commit and the file). Nothing is kept.
/// </summary>
public sealed record DeliveryOsduSchemaFileDto(
    string Kind, string Version, DeliveryOsduReleaseDto Release, string Path, Uri WebUrl, string Origin, JsonObject Schema);

/// <summary>
/// One side of a comparison: a kind's version in a release, the status the release gives it, the template version its file
/// holds, and its schema file exactly as the release publishes it.
/// </summary>
public sealed record DeliveryOsduComparisonSideDto(
    string Kind, DeliveryOsduReleaseDto Release, string Path, Uri WebUrl, string? Status, string TemplateVersion, string FileText);

/// <summary>A field of a variable that differs between the versions, its value in each, and what the difference means for a mapping.</summary>
public sealed record DeliveryTemplateFieldChangeDto(string Field, string? Before, string? After, string Impact);

/// <summary>A variable that differs between the versions: <c>Added</c>, <c>Removed</c> or <c>Changed</c>, and <c>Breaking</c>, <c>Additive</c> or <c>Wording</c>.</summary>
public sealed record DeliveryTemplateVariableChangeDto(string Path, string Change, string Impact, string Role, IReadOnlyList<DeliveryTemplateFieldChangeDto> Fields);

/// <summary>
/// A shared schema file the two versions refer to whose published text differs: its name without the version, and its path,
/// link and text on each side (null where a version does not refer to it).
/// </summary>
public sealed record DeliveryOsduReferencedFileDto(string Name, string? FromPath, string? ToPath, Uri? FromWebUrl, Uri? ToWebUrl, string? FromText, string? ToText);

/// <summary>
/// Two versions of a kind from the OSDU data definitions compared: whether the published files are the same, or differ
/// only in their own version identifiers, whether they hold the same template version, how many variable changes of each
/// impact there are, every variable that differs, and the shared schema files they refer to that differ, with how many
/// are the same.
/// </summary>
public sealed record DeliveryOsduComparisonDto(
    DeliveryOsduComparisonSideDto From, DeliveryOsduComparisonSideDto To, bool SameFile, bool OnlyIdentifiersDiffer, bool SameTemplate,
    int Breaking, int Additive, int Wording, int Unchanged, IReadOnlyList<DeliveryTemplateVariableChangeDto> Changes,
    int SameReferencedFiles, IReadOnlyList<DeliveryOsduReferencedFileDto> ReferencedFiles);

/// <summary>A schema to lay out as a template, nothing kept.</summary>
/// <remarks>
/// A schema that refers to the shared schemas of the OSDU data definitions, as a file a release publishes does, is bundled
/// with them from <c>Release</c> (the newest release when it is null).
/// </remarks>
public sealed record DeliveryTemplatePreviewRequest(string Kind, JsonElement Schema, string? Scope, string? Release);

/// <summary>
/// A delivery flow of a repository, as the builder and the Templates page offer it: its connection, what it renders with, and
/// the partition whose cache it reads (the partition it delivers to; null when its target declares none a cache can be kept under).
/// </summary>
/// <param name="PipelineId">The pipeline the flow is.</param>
/// <param name="Name">The flow, or for a source with interfaces the interface as <c>flow/interface</c>.</param>
/// <param name="Mapping">The mapping it pins, <c>name@version</c>.</param>
/// <param name="Parameters">
/// The values a run of the flow renders with: what it supplies under <c>render.parameters</c>, and the kind's own reference
/// for each parameter the kind owns that it leaves out, resolved as a run of the flow resolves them (the repository's
/// central configuration first, then the control plane's environment). A value whose reference cannot be resolved here is
/// left out, and <paramref name="ParameterReferences"/> still names it.
/// </param>
/// <param name="ParameterReferences">The reference each value is read from, by parameter, for every value written as one.</param>
/// <param name="Endpoint">The OSDU endpoint it delivers to, as the flow writes it.</param>
/// <param name="CacheScope">The partition whose cache it reads, or null when its target names none a cache is kept under.</param>
/// <param name="Partition">
/// For a flow that names its partitions, the one partition this entry delivers to (a flow is offered once per partition it
/// names); null for a flow that names none.
/// </param>
public sealed record DeliveryBuilderFlowDto(
    Guid PipelineId,
    string Name,
    string Mapping,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, string> ParameterReferences,
    string Endpoint,
    string? CacheScope,
    string? Partition = null);

/// <summary>
/// A type a cache holds: the name a mapping reads it by, its entity type, the names its values are cached under, and for a
/// lookup table the name its key is kept under (null for a type of OSDU records).
/// </summary>
public sealed record DeliveryCachedTypeDto(string Name, string EntityType, IReadOnlyList<string> Fields, string? Key);

/// <summary>A repository as the mapping builder offers it: the source a proposal is opened against, and its delivery flows.</summary>
public sealed record DeliveryBuilderRepoDto(Guid RepoId, string Name, Guid? SourceId, string? SourceBranch, IReadOnlyList<DeliveryBuilderFlowDto> Flows);

/// <summary>A partition's cache as the mapping builder offers it: the partition, the cache flows filling it, its current version and its types.</summary>
public sealed record DeliveryBuilderCacheDto(string Scope, IReadOnlyList<string> Flows, string? CurrentVersion, IReadOnlyList<DeliveryCachedTypeDto> Types);

/// <summary>
/// Starts a mapping for the template repository <c>RepoId</c>'s file at <c>TemplatePath</c> holds, prefilled from the cache of
/// partition <c>Scope</c> when one is named.
/// </summary>
public sealed record DeliveryMappingDraftRequest(string? Scope, Guid RepoId, string TemplatePath, string Name, string MappingVersion, string System);

/// <summary>
/// A draft to write as YAML and check against the cache of partition <c>Scope</c>; <c>Parameters</c> are the values the check
/// renders static ids with. <c>RepoId</c> and <c>Path</c> name the synced repository the mapping is written for and its path
/// there, whose template folders settle its templates as they will once it is there; without them the templates are read
/// from every synced repository's, by the versions the draft names.
/// </summary>
public sealed record DeliveryMappingComposeRequest(
    string? Scope, MappingDraft Draft, IReadOnlyDictionary<string, string>? Parameters, Guid? RepoId = null, string? Path = null);

/// <summary>The draft as YAML, what the checks found, and whether the mapping loads and passes the preflight.</summary>
public sealed record DeliveryMappingComposeResult(string Yaml, IReadOnlyList<MappingDraftIssue> Issues, bool Valid);

/// <summary>
/// A mapping document to read back into a draft. <c>RepoId</c> names the synced repository it is a document of, whose
/// template folders settle a template it names by kind alone; without it the document has to name its versions, which are
/// read from every synced repository's template folders.
/// </summary>
public sealed record DeliveryMappingParseRequest(string Yaml, string? Path, Guid? RepoId = null);

public sealed record DeliveryMappingParseResult(MappingDraft? Draft, IReadOnlyList<MappingDraftIssue> Issues);

/// <summary>A mapping document to measure against the template version it pins.</summary>
public sealed record DeliveryMappingCoverageRequest(string Yaml, string? Path, Guid? RepoId = null);

/// <summary>
/// What a mapping fills of its template: the template version it pins, every variable a mapping may fill with how the
/// document reaches it, and what it leaves required and empty. <c>Variables</c> is empty when the document does not load
/// or its template cannot be read, and <c>Issues</c> says which.
/// </summary>
public sealed record DeliveryMappingCoverageResult(
    string? Kind, string? Version, IReadOnlyList<VariableCoverage> Variables, IReadOnlyList<MappingDraftIssue> Issues);

/// <summary>A mapping document to draw the record shape of, and the parameter values to draw it with.</summary>
public sealed record DeliveryMappingShapeRequest(string Yaml, string? Path, IReadOnlyDictionary<string, string>? Parameters, Guid? RepoId = null);

/// <summary>A parameter the mapping declares, and the value the shape was drawn with: the one given, else the default, else null.</summary>
public sealed record DeliveryMappingShapeParameterDto(string Name, bool Required, string? Default, string? Description, string? Value);

/// <summary>
/// The shape of the records a mapping renders: the record with a placeholder wherever a value comes from a row or the
/// cache, the parameters the mapping declares, what the placeholders cannot say, and what stopped a shape being drawn
/// (<c>Record</c> is null then).
/// </summary>
public sealed record DeliveryMappingShapeResult(
    JsonObject? Record, IReadOnlyList<DeliveryMappingShapeParameterDto> Parameters, IReadOnlyList<string> Notes, IReadOnlyList<MappingDraftIssue> Issues);

/// <summary>
/// Templates and the mapping builder (osdu/docs/reference/concepts/templates.md; the builder:
/// osdu/docs/reference/concepts/gui.md, Mapping builder). A template is a file of a synced repository's template folder,
/// and every template read here is read from its file; the database only lists which files hold which template. Listing
/// and laying out templates, browsing and downloading the OSDU data definitions (the public repository of OSDU schemas,
/// which needs no credential), drafting and checking a mapping are reads; nothing here writes a template.
/// </summary>
public static class DeliveryTemplateEndpoints
{
    /// <summary>Repositories the builder lists at once.</summary>
    private const int MaxBuilderRepos = 200;

    public static RouteGroupBuilder MapDeliveryTemplateReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapGet("/templates", ListTemplatesAsync).WithName("ListDeliveryTemplates");
        delivery.MapGet("/templates/detail", GetTemplateAsync).WithName("GetDeliveryTemplate");
        delivery.MapGet("/templates/schema", GetTemplateSchemaAsync).WithName("GetDeliveryTemplateSchema");
        delivery.MapGet("/templates/resolve", ResolveTemplateAsync).WithName("ResolveDeliveryTemplate");
        delivery.MapPost("/templates/preview", PreviewTemplateAsync).WithName("PreviewDeliveryTemplate");
        delivery.MapGet("/templates/osdu/releases", ListOsduReleasesAsync).WithName("ListDeliveryOsduReleases");
        delivery.MapGet("/templates/osdu/schemas", ListOsduSchemasAsync).WithName("ListDeliveryOsduSchemas");
        delivery.MapGet("/templates/osdu/schema", GetOsduSchemaAsync).WithName("GetDeliveryOsduSchema");
        delivery.MapGet("/templates/osdu/file", GetOsduSchemaFileAsync).WithName("GetDeliveryOsduSchemaFile");
        delivery.MapGet("/templates/osdu/compare", CompareOsduSchemasAsync).WithName("CompareDeliveryOsduSchemas");
        delivery.MapGet("/mapping-builder/repos", ListBuilderReposAsync).WithName("ListDeliveryMappingBuilderRepos");
        delivery.MapGet("/mapping-builder/caches", ListBuilderCachesAsync).WithName("ListDeliveryMappingBuilderCaches");
        delivery.MapPost("/mapping-builder/draft", DraftMappingAsync).WithName("DraftDeliveryMapping");
        delivery.MapPost("/mapping-builder/compose", ComposeMappingAsync).WithName("ComposeDeliveryMapping");
        delivery.MapPost("/mapping-builder/parse", ParseMappingAsync).WithName("ParseDeliveryMapping");
        delivery.MapPost("/mapping-builder/shape", ShapeMappingAsync).WithName("ShapeDeliveryMapping");
        delivery.MapPost("/mapping-builder/coverage", CoverMappingAsync).WithName("CoverDeliveryMapping");
        return group;
    }

    public static RouteGroupBuilder MapDeliveryTemplateOperateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapPost("/templates/osdu/sync", SyncOsduAsync).WithName("SyncDeliveryOsduDataDefinitions");
        return group;
    }

    /// <summary>
    /// Every file of every synced repository's template folders, as each repository's last sync found it, by repository and
    /// path: the list the Templates page shows. Nothing is read from a file here.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryTemplateDto>>> ListTemplatesAsync(OsduDbContext osdu, CatalogDbContext db, CancellationToken ct)
    {
        var files = await RepositoryTemplates.FilesAsync(osdu, null, ct).ConfigureAwait(false);
        var names = await RepositoryNamesAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryTemplateDto>>(files
            .Select(f => new DeliveryTemplateDto(
                f.RepoId, names.GetValueOrDefault(f.RepoId, f.RepoId.ToString("D")), f.Path, f.Folder,
                f.Kind.Length == 0 ? null : f.Kind, f.Version.Length == 0 ? null : f.Version, f.Problem, f.SeenUtc, f.Parent))
            .OrderBy(t => t.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>The template repository <paramref name="repoId"/>'s file at <paramref name="path"/> holds, read from the file and laid out.</summary>
    private static async Task<Results<Ok<DeliveryTemplateDetailDto>, ProblemHttpResult>> GetTemplateAsync(
        Guid? repoId, string? path, string? scope, OsduDbContext osdu, CatalogDbContext db, IRepositoryFolders folders, CancellationToken ct)
    {
        var (schema, problem) = await TemplateFileAsync(repoId, path, osdu, folders, ct).ConfigureAwait(false);
        if (schema is null)
        {
            return problem!;
        }

        var names = await RepositoryNamesAsync(db, ct).ConfigureAwait(false);
        var types = string.IsNullOrWhiteSpace(scope) ? [] : await CatalogCacheReader.TypesAsync(osdu, scope.Trim(), ct).ConfigureAwait(false);
        var file = new DeliveryTemplateFileDto(repoId!.Value, names.GetValueOrDefault(repoId.Value, repoId.Value.ToString("D")), path!.Trim());
        var children = await LinkedChildrenAsync(osdu, repoId.Value, schema.Kind, ct).ConfigureAwait(false);
        return TypedResults.Ok(Detail(OsduTemplate.From(schema), file, types) with { Parent = schema.Parent?.Kind, Children = children });
    }

    /// <summary>
    /// The kinds of repository <paramref name="repoId"/>'s templates whose recorded parent is of the entity type of
    /// <paramref name="kind"/>, each once, in ordinal order: the kinds a mapping of it can link as children. Read from the
    /// index the repository's last sync recorded, nothing from a file.
    /// </summary>
    private static async Task<IReadOnlyList<string>> LinkedChildrenAsync(OsduDbContext osdu, Guid repoId, string kind, CancellationToken ct)
    {
        // The sync records a kind and a parent only once it read them as kinds, so the entity type is the third segment.
        static string? EntityTypeOf(string? written) => written?.Split(':') is { Length: 4 } parts && parts[2].Length > 0 ? parts[2] : null;
        if (EntityTypeOf(kind) is not { } entityType)
        {
            return [];
        }

        var files = await RepositoryTemplates.FilesAsync(osdu, repoId, ct).ConfigureAwait(false);
        return files
            .Where(f => string.Equals(EntityTypeOf(f.Parent), entityType, StringComparison.Ordinal)
                && EntityTypeOf(f.Kind) is { } own && !string.Equals(own, entityType, StringComparison.Ordinal))
            .Select(f => f.Kind)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The template repository <paramref name="repoId"/>'s file at <paramref name="path"/> holds, read from the file, as a file
    /// to save under the file's own name (<see cref="TemplateSources.FileText"/>): what the Templates page shows as Schema JSON.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetTemplateSchemaAsync(
        Guid? repoId, string? path, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        var (schema, problem) = await TemplateFileAsync(repoId, path, osdu, folders, ct).ConfigureAwait(false);
        return schema is null
            ? problem!
            : TypedResults.File(Encoding.UTF8.GetBytes(TemplateSources.FileText(schema)), "application/json", System.IO.Path.GetFileName(path!.Trim()));
    }

    /// <summary>
    /// The template a synced repository's file holds, read from the file in the folder the repository was synced into: a
    /// 400 when the file is not named, a 404 when the repository's last sync recorded no such file, and a 422 when the file
    /// holds no template or cannot be read as the one recorded.
    /// </summary>
    private static async Task<(SchemaSnapshot? Schema, ProblemHttpResult? Problem)> TemplateFileAsync(
        Guid? repoId, string? path, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        if (repoId is not { } repo || string.IsNullOrWhiteSpace(path))
        {
            return (null, Problem("Name the template by its repository (repoId) and its file's path in it (path).", StatusCodes.Status400BadRequest));
        }

        var templates = await RepositoryTemplates.ReadAsync(osdu, folders, repo, ct).ConfigureAwait(false);
        var file = path.Trim();
        if (!templates.Files.Any(f => string.Equals(f.Path, file, StringComparison.Ordinal)))
        {
            return (null, Problem(
                $"The template folders of repository {repo:D} hold no file {file} as its last sync found them; sync the repository if the file is new.",
                StatusCodes.Status404NotFound,
                "Not found"));
        }

        var resolved = await templates.LoadAsync(repo, file, ct).ConfigureAwait(false);
        return resolved.Schema is { } schema
            ? (schema, null)
            : (null, Problem(resolved.Problem!, StatusCodes.Status422UnprocessableEntity, "Template unreadable"));
    }

    /// <summary>The name of every repository the catalog holds, by id.</summary>
    private static async Task<Dictionary<Guid, string>> RepositoryNamesAsync(CatalogDbContext db, CancellationToken ct)
        => await db.Repos.AsNoTracking().Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

    /// <summary>
    /// The template the document at <paramref name="path"/> of synced repository <paramref name="repoId"/> reads for
    /// <paramref name="kind"/> (version <paramref name="version"/> when it names one), settled from the template folders the
    /// repository's last sync recorded on the document's path, as a run settles it from the files.
    /// </summary>
    private static async Task<Results<Ok<DeliveryTemplateResolutionDto>, ProblemHttpResult>> ResolveTemplateAsync(
        Guid? repoId, string? path, string? kind, string? version, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        if (repoId is not { } repo || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(kind))
        {
            return Problem("Name the repository (repoId), the document's path in it (path) and the template's kind (kind).", StatusCodes.Status400BadRequest);
        }

        var wanted = kind.Trim();
        var document = path.Trim().Replace('\\', '/');
        string suggested;
        try
        {
            // Naming the file checks the kind: a record kind is what a template file is named after. The file goes in the
            // template folder of the document's project, beside the folder the document is in, which the document reads.
            suggested = $"{ProjectTemplateFolder(document)}/{TemplateSources.FileName(wanted)}";
        }
        catch (FlowValidationException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest);
        }

        var synced = await SyncedTemplates.ReadAsync(osdu, folders, repo, ct).ConfigureAwait(false);
        var pick = synced.ChainFor(document).Pick(wanted, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        return TypedResults.Ok(new DeliveryTemplateResolutionDto(pick.Found, wanted, pick.File?.Version, pick.File?.Shown, pick.Problem, suggested));
    }

    /// <summary>
    /// The template folder of the project a document at <paramref name="document"/> belongs to: the one beside the folder the
    /// document is in (<c>welldb/templates</c> for <c>welldb/mappings/WellLog@1.4.0.yaml</c>), or the repository's top one for
    /// a document at the top or in a top folder.
    /// </summary>
    internal static string ProjectTemplateFolder(string document)
    {
        var parts = document.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The document's folder is parts[..^1]; its project is the folder above that.
        var project = parts.Length >= 3 ? string.Join('/', parts[..^2]) : string.Empty;
        return project.Length == 0 ? TemplateFolders.FolderName : $"{project}/{TemplateFolders.FolderName}";
    }

    private static async Task<Results<Ok<DeliveryTemplateDetailDto>, ProblemHttpResult>> PreviewTemplateAsync(
        DeliveryTemplatePreviewRequest request, OsduDataDefinitions definitions, OsduDbContext osdu, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Kind) || request.Schema.ValueKind == JsonValueKind.Undefined)
        {
            return Problem("A preview needs the kind and the schema.", StatusCodes.Status400BadRequest);
        }

        var (imported, problem) = await ImportAsync(request.Schema, request.Kind, request.Release, definitions, ct).ConfigureAwait(false);
        if (imported is null)
        {
            return problem!;
        }

        var types = string.IsNullOrWhiteSpace(request.Scope) ? [] : await CatalogCacheReader.TypesAsync(osdu, request.Scope.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok(Detail(OsduTemplate.From(imported.Schema), null, types) with { Parent = imported.Schema.Parent?.Kind });
    }

    private static async Task<Results<Ok<DeliveryOsduReleasesDto>, ProblemHttpResult>> ListOsduReleasesAsync(
        OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            var releases = await definitions.ReleasesAsync(ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduReleasesDto(definitions.SyncedUtc, releases.Select(r => Release(definitions, r)).ToList()));
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
    }

    private static async Task<Results<Ok<DeliveryOsduSchemaIndexDto>, ProblemHttpResult>> ListOsduSchemasAsync(
        string? release, OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            var index = await definitions.IndexAsync(release, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduSchemaIndexDto(
                Release(definitions, index.Release),
                index.Schemas.Select(s => new DeliveryOsduSchemaDto(s.Kind, s.EntityType, s.Version, s.Status, s.Path, definitions.FileWebUrl(index.Release, s.Path))).ToList()));
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
    }

    private static async Task<Results<Ok<DeliveryOsduSchemaFileDto>, ProblemHttpResult>> GetOsduSchemaAsync(
        string? release, string? kind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return Problem("Name the schema with kind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var file = await definitions.FetchAsync(release, kind, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduSchemaFileDto(
                file.Schema.Kind, file.Schema.Version, Release(definitions, file.Release), file.Path, definitions.FileWebUrl(file.Release, file.Path), file.Origin, file.Schema.Root));
        }
        catch (FlowValidationException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
        catch (DeliveryException ex)
        {
            // The release's file is there but is not a record schema a template can be laid out from.
            return Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not a record schema");
        }
    }

    /// <summary>
    /// A kind's schema from a release of the OSDU data definitions (the newest when none is named), bundled, as the file a
    /// repository's template folder keeps (<see cref="TemplateSources.FileText"/>), named after its kind
    /// (<see cref="TemplateSources.FileName"/>): what the Templates page's Browse OSDU downloads, for a repository to hold in
    /// a template folder beside the documents that read it. Nothing is kept.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetOsduSchemaFileAsync(
        string? release, string? kind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return Problem("Name the schema with kind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var file = await definitions.FetchAsync(release, kind, ct).ConfigureAwait(false);
            return TypedResults.File(
                Encoding.UTF8.GetBytes(TemplateSources.FileText(file.Schema)), "application/json", TemplateSources.FileName(file.Schema.Kind));
        }
        catch (FlowValidationException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
        catch (DeliveryException ex)
        {
            // The release's file is there but is not a record schema a template can be laid out from.
            return Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not a record schema");
        }
    }

    /// <summary>Reads the release list again from the repository and downloads the release (the newest when none is named) when it is not on disk.</summary>
    private static async Task<Results<Ok<DeliveryOsduSyncDto>, ProblemHttpResult>> SyncOsduAsync(
        DeliveryOsduSyncRequest? request, OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            var sync = await definitions.SyncAsync(request?.Release, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduSyncDto(sync.SyncedUtc, sync.Releases.Select(r => Release(definitions, r)).ToList(), sync.Downloaded));
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
    }

    private static async Task<Results<Ok<DeliveryOsduComparisonDto>, ProblemHttpResult>> CompareOsduSchemasAsync(
        string? fromRelease, string? fromKind, string? toRelease, string? toKind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromKind) || string.IsNullOrWhiteSpace(toKind)
            || fromKind.Trim().Split(':').Length != 4 || toKind.Trim().Split(':').Length != 4)
        {
            return Problem("A comparison names both versions, fromKind and toKind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest);
        }

        var from = fromKind.Trim();
        var to = toKind.Trim();
        if (!string.Equals(from[..from.LastIndexOf(':')], to[..to.LastIndexOf(':')], StringComparison.Ordinal))
        {
            return Problem($"A comparison is between versions of one kind; '{from}' and '{to}' are different kinds.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var before = new ComparedSide(
                await definitions.PublishedFileAsync(fromRelease, from, ct).ConfigureAwait(false),
                await definitions.FetchAsync(fromRelease, from, ct).ConfigureAwait(false));
            var after = new ComparedSide(
                await definitions.PublishedFileAsync(toRelease, to, ct).ConfigureAwait(false),
                await definitions.FetchAsync(toRelease, to, ct).ConfigureAwait(false));
            var comparison = TemplateComparer.Compare(OsduTemplate.From(before.Bundled.Schema), OsduTemplate.From(after.Bundled.Schema));

            // The shared schemas each version refers to, as published: a kind's own file can be the same in two releases
            // while a schema it refers to changed under the same version, and that is where its template's changes come from.
            var referenced = new List<DeliveryOsduReferencedFileDto>();
            var sameReferenced = 0;
            foreach (var pair in TemplateComparer.PairReferencedFiles(before.Bundled.Files, after.Bundled.Files))
            {
                var beforeText = pair.BeforePath is null ? null : await definitions.FileTextAsync(before.File.Release, pair.BeforePath, ct).ConfigureAwait(false);
                var afterText = pair.AfterPath is null ? null : await definitions.FileTextAsync(after.File.Release, pair.AfterPath, ct).ConfigureAwait(false);
                if (beforeText is not null && afterText is not null && TemplateComparer.SameContent(beforeText, afterText))
                {
                    sameReferenced++;
                    continue;
                }

                referenced.Add(new DeliveryOsduReferencedFileDto(
                    pair.Name,
                    pair.BeforePath,
                    pair.AfterPath,
                    pair.BeforePath is null ? null : definitions.FileWebUrl(before.File.Release, pair.BeforePath),
                    pair.AfterPath is null ? null : definitions.FileWebUrl(after.File.Release, pair.AfterPath),
                    beforeText,
                    afterText));
            }

            return TypedResults.Ok(new DeliveryOsduComparisonDto(
                Side(definitions, before),
                Side(definitions, after),
                TemplateComparer.SameContent(before.File.Text, after.File.Text),
                TemplateComparer.DifferOnlyInIdentifiers(before.File.Text, from, after.File.Text, to),
                string.Equals(before.Bundled.Schema.Version, after.Bundled.Schema.Version, StringComparison.Ordinal),
                comparison.Count(TemplateChangeImpact.Breaking),
                comparison.Count(TemplateChangeImpact.Additive),
                comparison.Count(TemplateChangeImpact.Wording),
                comparison.Unchanged,
                comparison.Changes.Select(c => new DeliveryTemplateVariableChangeDto(
                    c.Path, c.Change.ToString(), c.Impact.ToString(), c.Role.ToString(),
                    c.Fields.Select(f => new DeliveryTemplateFieldChangeDto(f.Field, f.Before, f.After, f.Impact.ToString())).ToList())).ToList(),
                sameReferenced,
                referenced));
        }
        catch (FlowValidationException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
        catch (DeliveryException ex)
        {
            // A release's file is there but is not a record schema a template can be laid out from.
            return Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not a record schema");
        }
    }

    private static async Task<Ok<IReadOnlyList<DeliveryBuilderRepoDto>>> ListBuilderReposAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, CancellationToken ct)
    {
        RegisteredPartitions? registry = null;
        var repos = await db.Repos.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).Take(MaxBuilderRepos).ToListAsync(ct).ConfigureAwait(false);
        var sources = await db.RepoSources.AsNoTracking().Select(s => new { s.Id, s.Name, s.Branch }).ToListAsync(ct).ConfigureAwait(false);
        var sourceByRepo = new Dictionary<Guid, (Guid Id, string Branch)>();
        foreach (var source in sources)
        {
            // A tracked source's repository is identified by the source's name, exactly as the managed sync derives it.
            sourceByRepo.TryAdd(FlowIdentity.FromName(source.Name), (source.Id, source.Branch));
        }

        var ids = repos.Select(r => r.Id).ToList();
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => ids.Contains(p.RepoId) && p.Active && p.Kind == FlowDefinition.FlowTypeName)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.RepoId, p.Name, p.Yaml, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<DeliveryBuilderRepoDto>(repos.Count);
        foreach (var repo in repos)
        {
            var repoPipelines = pipelines.Where(p => p.RepoId == repo.Id).ToList();
            // A run of the repository's flows resolves references from its central configuration before the node's own
            // environment, the values set for the partition it is bound to first, so the check resolves them the same way
            // and renders with what a run would.
            var configuration = repoPipelines.Count == 0
                ? DeliveryConfiguration.None
                : await config.ConfigurationAsync(repo.Id, ct).ConfigureAwait(false);
            var flows = new List<DeliveryBuilderFlowDto>();
            foreach (var pipeline in repoPipelines)
            {
                try
                {
                    // A source offers one connection per interface, and a source that works in partitions one per interface
                    // and partition it serves: each pins its own mapping and render parameters, and reads its own
                    // partition's cache. The registry is read once, for the first source that serves it.
                    var parsed = documents.ParseSource(pipeline.Yaml, pipeline.RelativePath);
                    if (parsed.FollowsRegistry)
                    {
                        registry ??= await partitions.ReadAsync(ct).ConfigureAwait(false);
                    }

                    foreach (var flow in parsed.EveryLedger(registry ?? RegisteredPartitions.None))
                    {
                        var secrets = engine.WithSuppliedReferences(configuration.For(flow.Partition)).Secrets;
                        var (parameters, references) = await RenderParametersOfAsync(flow, secrets, ct).ConfigureAwait(false);
                        flows.Add(new DeliveryBuilderFlowDto(
                            pipeline.Id, flow.Label, flow.Render.Mapping, parameters, references, flow.Target.Endpoint,
                            await CacheScopeOfAsync(flow, secrets, ct).ConfigureAwait(false), flow.Partition));
                    }
                }
                catch (FlowValidationException)
                {
                    // A flow whose catalog copy does not parse is reported on its own page; it offers no connection here.
                }
            }

            var source = sourceByRepo.TryGetValue(repo.Id, out var s) ? s : ((Guid Id, string Branch)?)null;
            result.Add(new DeliveryBuilderRepoDto(repo.Id, repo.Name, source?.Id, source?.Branch, flows));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryBuilderRepoDto>>(result);
    }

    /// <summary>
    /// The values a run of <paramref name="flow"/> renders the kind's parameters and its own with (<see cref="DeliveryDestination.Supplied"/>,
    /// the rule the render resolver applies), each reference resolved by <paramref name="secrets"/>; and the reference each
    /// value is read from. A reference that cannot be resolved here (a variable only the nodes hold, a vault the control
    /// plane does not reach) leaves its value out, so the check asks for one instead of rendering with the reference text,
    /// and the builder says which reference it is.
    /// </summary>
    private static async Task<(IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, string> References)> RenderParametersOfAsync(
        FlowDefinition flow, SqlFlow.Core.Secrets.ISecretResolver secrets, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, supplied) in DeliveryDestination.Supplied(flow, DeliveryDestination.Parameters))
        {
            if (!supplied.Contains("${", StringComparison.Ordinal))
            {
                values[name] = supplied;
                continue;
            }

            references[name] = supplied;
            try
            {
                values[name] = await secrets.ResolveAsync(supplied, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reported by the value's absence beside its reference; the resolver's message may quote what it read,
                // so it is not passed on.
            }
        }

        return (values, references);
    }

    /// <summary>
    /// The partition whose cache a delivery flow reads: the partition it delivers to, with a reference resolved as a run of
    /// the flow resolves it (so a flow naming <c>${env:OSDU_DATA_PARTITION}</c> finds the cache its partition is kept under),
    /// the reference as it is written when it cannot be resolved here, or null when its target names none a cache is kept
    /// under.
    /// </summary>
    private static async Task<string?> CacheScopeOfAsync(FlowDefinition flow, SqlFlow.Core.Secrets.ISecretResolver secrets, CancellationToken ct)
    {
        var where = flow.SourcePath ?? flow.Name;
        string declared;
        try
        {
            declared = CacheScope.Of(flow.Target.Headers, where);
        }
        catch (FlowValidationException)
        {
            return null;
        }

        try
        {
            return CacheScope.Normalize(await secrets.ResolveAsync(declared, ct).ConfigureAwait(false), where);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return declared;
        }
    }

    /// <summary>Every partition's cache, with the flows filling it, its current version and its types, for the builder's cache picker.</summary>
    private static async Task<Ok<IReadOnlyList<DeliveryBuilderCacheDto>>> ListBuilderCachesAsync(OsduDbContext osdu, CancellationToken ct)
    {
        var declared = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .Select(c => new { c.Scope, c.FlowName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var scopes = declared.Select(c => c.Scope).Distinct().ToList();
        var current = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope) && v.Current)
            .ToDictionaryAsync(v => v.Scope, v => v.Version, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var result = new List<DeliveryBuilderCacheDto>(scopes.Count);
        foreach (var scope in scopes.Order(StringComparer.Ordinal))
        {
            var types = await CatalogCacheReader.TypesAsync(osdu, scope, ct).ConfigureAwait(false);
            result.Add(new DeliveryBuilderCacheDto(
                scope,
                declared.Where(c => c.Scope == scope).Select(c => c.FlowName).Distinct().Order(StringComparer.Ordinal).ToList(),
                current.GetValueOrDefault(scope),
                types.Select(t => new DeliveryCachedTypeDto(t.Name, t.EntityType, t.Fields, t.Key)).ToList()));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryBuilderCacheDto>>(result);
    }

    /// <summary>Starts a mapping for a template a synced repository's file holds, read from the file.</summary>
    private static async Task<Results<Ok<MappingDraft>, ProblemHttpResult>> DraftMappingAsync(
        DeliveryMappingDraftRequest request, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        if (request is null || request.RepoId == Guid.Empty || string.IsNullOrWhiteSpace(request.TemplatePath))
        {
            return Problem("A draft needs the template it fills: its repository (repoId) and its file's path in it (templatePath).", StatusCodes.Status400BadRequest);
        }

        var (schema, problem) = await TemplateFileAsync(request.RepoId, request.TemplatePath, osdu, folders, ct).ConfigureAwait(false);
        if (schema is null)
        {
            return problem!;
        }

        var types = string.IsNullOrWhiteSpace(request.Scope) ? [] : await CatalogCacheReader.TypesAsync(osdu, request.Scope.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok(MappingBuilder.Draft(OsduTemplate.From(schema), types, request.Name, request.MappingVersion, request.System));
    }

    private static async Task<Results<Ok<DeliveryMappingComposeResult>, ProblemHttpResult>> ComposeMappingAsync(
        DeliveryMappingComposeRequest request, OsduDbContext osdu, IRepositoryFolders folders, ICacheStore caches, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        if (request?.Draft is null)
        {
            return Problem("Compose needs the draft.", StatusCodes.Status400BadRequest);
        }

        var draft = request.Draft;
        var issues = MappingBuilder.Incomplete(draft).ToList();
        var yaml = MappingBuilder.ToYaml(draft);
        if (issues.Any(IsError))
        {
            return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, false));
        }

        // The mapping is checked where it will be: at its path in the repository it is written for, whose template folders on
        // that path settle its templates as a sync and a run will.
        var path = string.IsNullOrWhiteSpace(request.Path) ? $"mappings/{draft.Name}@{draft.Version}.yaml" : request.Path.Trim();
        var (source, pins) = await TemplatesForAsync(request.RepoId, path, osdu, folders, ct).ConfigureAwait(false);
        MappingDefinition mapping;
        try
        {
            mapping = documents.ParseMapping(yaml, path, pins);
        }
        catch (FlowValidationException ex)
        {
            issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, ex.Message));
            return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, false));
        }

        var resolved = await source.ResolveAsync(mapping.TemplateDocument ?? path, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        if (resolved.Schema is not { } schema)
        {
            issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, $"The mapping's template {mapping.Template} cannot be read: {resolved.Problem}"));
            return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, false));
        }

        // The check reads the partition's cache at its current version, as a delivery flow delivering there would render today.
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope.Trim();
        var references = ReferenceSnapshot.Empty;
        if (scope is not null)
        {
            if (await caches.CurrentVersionAsync(scope, ct).ConfigureAwait(false) is { } version)
            {
                references = await caches.LoadAsync(scope, version, ct).ConfigureAwait(false) ?? ReferenceSnapshot.Empty;
            }
            else
            {
                issues.Add(new MappingDraftIssue(
                    MappingDraftIssue.WarningSeverity,
                    $"The cache of partition '{scope}' has no version yet, so the cache entries are checked against no cached records. Run a cache flow of the partition to capture one."));
            }
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, declared) in mapping.Parameters)
        {
            if (declared.Default is not null)
            {
                parameters[name] = declared.Default;
            }
        }

        foreach (var (name, value) in request.Parameters ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[name] = value.Trim();
            }
        }

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            CacheScope = ReferenceEquals(references, ReferenceSnapshot.Empty) ? null : scope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = parameters,
        };

        try
        {
            // A search is checked against the schema it pins, and a $parent path against the parent's template, as a delivery
            // resolving the mapping would check them.
            var searches = await RenderResolver.SearchesAsync(source, mapping, schema, ct).ConfigureAwait(false);
            foreach (var issue in Preflight.Check(mapping, schema, references, context, sourceColumns: null, searches))
            {
                issues.Add(new MappingDraftIssue(
                    issue.Severity == IssueSeverity.Error ? MappingDraftIssue.ErrorSeverity : MappingDraftIssue.WarningSeverity, issue.Message, issue.Target));
            }
        }
        catch (Exception ex) when (ex is FlowValidationException or DeliveryException)
        {
            issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, ex.Message));
        }

        return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, !issues.Any(IsError)));
    }

    private static async Task<Ok<DeliveryMappingParseResult>> ParseMappingAsync(
        DeliveryMappingParseRequest request, DeliveryDocumentLoader documents, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Yaml))
        {
            return TypedResults.Ok(new DeliveryMappingParseResult(null, [new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, "There is no mapping document to open.")]));
        }

        try
        {
            var path = PathOf(request.Path);
            var (_, pins) = await TemplatesForAsync(request.RepoId, path, osdu, folders, ct).ConfigureAwait(false);
            var mapping = documents.ParseMapping(request.Yaml, path, pins);
            return TypedResults.Ok(new DeliveryMappingParseResult(MappingBuilder.FromDefinition(mapping), []));
        }
        catch (FlowValidationException ex)
        {
            return TypedResults.Ok(new DeliveryMappingParseResult(null, [new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, ex.Message)]));
        }
    }

    /// <summary>
    /// The shape of the records a mapping document renders, drawn against its template without a row or a cache
    /// (<see cref="MappingRenderer.Shape"/>): the template its repository's folders settle it to for a document of a synced
    /// repository, else the version it names as a synced repository holds it, read from the file. A document that does not load, or whose template cannot be read, answers
    /// with the issue and no record; it is not an HTTP error, because the document is what the caller is looking at.
    /// </summary>
    private static async Task<Ok<DeliveryMappingShapeResult>> ShapeMappingAsync(
        DeliveryMappingShapeRequest request, OsduDbContext osdu, IRepositoryFolders folders, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Yaml))
        {
            return ShapeIssue([], "There is no mapping document to draw the record shape of.");
        }

        MappingDefinition mapping;
        var path = PathOf(request.Path);
        var (source, pins) = await TemplatesForAsync(request.RepoId, path, osdu, folders, ct).ConfigureAwait(false);
        try
        {
            mapping = documents.ParseMapping(request.Yaml, path, pins);
        }
        catch (FlowValidationException ex)
        {
            return ShapeIssue([], ex.Message);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Parameters ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[name] = value.Trim();
            }
        }

        var declared = mapping.Parameters
            .Select(p => new DeliveryMappingShapeParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description, parameters.GetValueOrDefault(p.Key) ?? p.Value.Default))
            .ToList();

        var resolved = await source.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        if (resolved.Schema is not { } schema)
        {
            return ShapeIssue(declared, $"The mapping's template cannot be read: {resolved.Problem}");
        }

        try
        {
            var shape = MappingRenderer.Shape(mapping, schema, parameters);
            return TypedResults.Ok(new DeliveryMappingShapeResult(shape.Document, declared, shape.Notes, []));
        }
        catch (Exception ex) when (ex is FlowValidationException or DeliveryException)
        {
            return ShapeIssue(declared, ex.Message);
        }
    }

    /// <summary>
    /// What a mapping document fills of its template, variable by variable, and what it leaves required and empty
    /// (<see cref="MappingCoverage.Of"/>): the template its repository's folders settle it to for a document of a synced
    /// repository, else the version it names as a synced repository holds it, read from the file. Nothing is rendered and no cache is read, so the answer is the document
    /// against the schema alone. A document that does not load, or whose template cannot be read, answers with the issue and
    /// no variables; it is not an HTTP error, because the document is what the caller is looking at.
    /// </summary>
    private static async Task<Ok<DeliveryMappingCoverageResult>> CoverMappingAsync(
        DeliveryMappingCoverageRequest request, OsduDbContext osdu, IRepositoryFolders folders, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Yaml))
        {
            return CoverageIssue(null, null, "There is no mapping document to measure against its template.");
        }

        MappingDefinition mapping;
        var path = PathOf(request.Path);
        var (source, pins) = await TemplatesForAsync(request.RepoId, path, osdu, folders, ct).ConfigureAwait(false);
        try
        {
            mapping = documents.ParseMapping(request.Yaml, path, pins);
        }
        catch (FlowValidationException ex)
        {
            return CoverageIssue(null, null, ex.Message);
        }

        var resolved = await source.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        if (resolved.Schema is not { } schema)
        {
            return CoverageIssue(mapping.Template.Kind, mapping.Template.Version, $"The mapping's template cannot be read: {resolved.Problem}");
        }

        var coverage = MappingCoverage.Of(mapping, OsduTemplate.From(schema));
        var issues = coverage.Issues
            .Select(issue => new MappingDraftIssue(
                issue.Severity == IssueSeverity.Error ? MappingDraftIssue.ErrorSeverity : MappingDraftIssue.WarningSeverity, issue.Message, issue.Target))
            .ToList();
        return TypedResults.Ok(new DeliveryMappingCoverageResult(schema.Kind, schema.Version, coverage.Variables, issues));
    }

    /// <summary>The path a page names a mapping document by, or the name a document without one is given.</summary>
    private static string PathOf(string? path) => string.IsNullOrWhiteSpace(path) ? "mapping.yaml" : path.Trim();

    /// <summary>
    /// Where a mapping document a page sends reads its templates: for a document of synced repository
    /// <paramref name="repoId"/>, the template folders its last sync recorded on the document's path, which also settle a
    /// template named by kind alone (the pins); otherwise the templates every synced repository holds, with no pins, so the
    /// document names its versions. Either way a template is read from its file.
    /// </summary>
    private static async Task<(ITemplateSource Source, ITemplatePins? Pins)> TemplatesForAsync(
        Guid? repoId, string path, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        if (repoId is not { } repo)
        {
            return (await RepositoryTemplates.ReadAsync(osdu, folders, ct).ConfigureAwait(false), null);
        }

        var synced = await SyncedTemplates.ReadAsync(osdu, folders, repo, ct).ConfigureAwait(false);
        return (synced, synced.For(path));
    }

    private static Ok<DeliveryMappingCoverageResult> CoverageIssue(string? kind, string? version, string message)
        => TypedResults.Ok(new DeliveryMappingCoverageResult(kind, version, [], [new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, message)]));

    private static Ok<DeliveryMappingShapeResult> ShapeIssue(IReadOnlyList<DeliveryMappingShapeParameterDto> parameters, string message)
        => TypedResults.Ok(new DeliveryMappingShapeResult(null, parameters, [], [new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, message)]));

    private static bool IsError(MappingDraftIssue issue) => issue.Severity == MappingDraftIssue.ErrorSeverity;

    private static DeliveryTemplateDetailDto Detail(OsduTemplate template, DeliveryTemplateFileDto? file, IReadOnlyList<CachedTypeInfo> cache)
        => new(
            template.Kind,
            template.Version,
            template.Title,
            template.Description,
            file,
            template.Variables.Select(v => new DeliveryTemplateVariableDto(
                v.Path.Text, v.Shape.ToString(), v.Type, v.ItemType, v.Format, v.Required, v.Role.ToString(), v.Relationships, v.Pattern, v.UnitContext,
                v.Title, v.Description, v.KeyValueType, v.Nested, MappingBuilder.CacheTypesFor(v, cache).Select(c => c.Name).ToList(), v.Open)).ToList());

    private static DeliveryOsduReleaseDto Release(OsduDataDefinitions definitions, DataDefinitionsRelease release)
        => new(release.Name, release.Commit, release.PublishedUtc, definitions.ReleaseWebUrl(release), definitions.IsLocal(release));

    private static DeliveryOsduComparisonSideDto Side(OsduDataDefinitions definitions, ComparedSide side)
        => new(
            side.Bundled.Schema.Kind,
            Release(definitions, side.File.Release),
            side.File.Path,
            definitions.FileWebUrl(side.File.Release, side.File.Path),
            side.File.Status,
            side.Bundled.Schema.Version,
            side.File.Text);

    /// <summary>A version as a comparison reads it: its file as published, and bundled as a template.</summary>
    private sealed record ComparedSide(DataDefinitionsPublishedFile File, DataDefinitionsSchemaFile Bundled);

    /// <summary>
    /// The schema a preview was sent, as a template: taken as it is when it arrived bundled, or bundled with the
    /// shared schemas of the OSDU data definitions it refers to, read from <paramref name="release"/>. A schema that is not a
    /// record schema, or refers to a file or release the data definitions do not hold, is a 400; data definitions that could
    /// not be read are a 502.
    /// </summary>
    private static async Task<(ImportedSchema? Schema, ProblemHttpResult? Problem)> ImportAsync(
        JsonElement schema, string kind, string? release, OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            return (await definitions.ImportAsync(schema.GetRawText(), kind.Trim(), release, "the schema", ct).ConfigureAwait(false), null);
        }
        catch (FlowValidationException ex)
        {
            return (null, Problem(ex.Message, StatusCodes.Status400BadRequest, "Not a record schema"));
        }
        catch (DataDefinitionsException ex) when (ex.NotFound)
        {
            return (null, Problem(ex.Message, StatusCodes.Status400BadRequest, "Not in the OSDU data definitions"));
        }
        catch (DataDefinitionsException ex)
        {
            return (null, Unavailable(ex));
        }
        catch (DeliveryException ex)
        {
            return (null, Problem(ex.Message, StatusCodes.Status400BadRequest, "Not a record schema"));
        }
    }

    /// <summary>What the data definitions could not answer: 404 for what they do not hold, 502 when they could not be read.</summary>
    private static ProblemHttpResult Unavailable(DataDefinitionsException ex)
        => ex.NotFound
            ? Problem(ex.Message, StatusCodes.Status404NotFound, "Not found")
            : Problem(ex.Message, StatusCodes.Status502BadGateway, "OSDU data definitions unavailable");

    private static ProblemHttpResult Problem(string detail, int status, string title = "Bad request")
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
