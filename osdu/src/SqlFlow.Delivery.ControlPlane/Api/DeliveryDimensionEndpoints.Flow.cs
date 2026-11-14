using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// Where a dimension flow keeps its tables and views, as its <c>target</c> writes it: the connection reference (null for the
/// module's own), the database (null for the connection's own), the schema, and whether that is the module's database,
/// beside the catalog, as the spelling tells (a build settles it by asking the server).
/// </summary>
public sealed record DeliveryDimensionFlowTargetDto(string? Connection, string? Database, string Schema, bool InModuleDatabase);

/// <summary>
/// A dimension flow of the catalog, as a new one is modelled on it: where its file is, its batch, the partitions it builds
/// in, its parameters, where it keeps its tables, the dimensions and views it declares and the kinds they read, and its
/// document without its <c>dimensions</c> and <c>views</c> (the source, its auth, parallelism, schedule and target, as
/// written). <c>Problem</c> says why the document does not load, when it does not.
/// </summary>
public sealed record DeliveryDimensionFlowPatternDto(
    Guid PipelineId, string Name, string? Description, Guid RepoId, string Repository, string RelativePath, string? Batch,
    IReadOnlyList<string> Partitions, IReadOnlyList<string> Parameters, DeliveryDimensionFlowTargetDto? Target,
    IReadOnlyList<string> Dimensions, IReadOnlyList<string> Views, IReadOnlyList<string> Kinds, string Envelope, bool EnvelopeCut, string? Problem);

/// <summary>
/// A connection reference the catalog's flows read from or write to, with how many flows of each kind do: where the estate
/// keeps its data. <c>Module</c> marks the module's own, the catalog's database.
/// </summary>
public sealed record DeliveryDimensionFlowConnectionDto(
    string Reference, bool Module, IReadOnlyDictionary<string, int> WrittenBy, IReadOnlyDictionary<string, int> ReadBy);

/// <summary>
/// What a new dimension flow is modelled on: the dimension flows the catalog holds (at most <c>MaxPatternFlows</c>, with how many
/// more there are), the connections the estate's flows keep their data through, and the module's own connection reference.
/// </summary>
public sealed record DeliveryDimensionFlowPatternsDto(
    IReadOnlyList<DeliveryDimensionFlowPatternDto> Flows, int MoreFlows, IReadOnlyList<DeliveryDimensionFlowConnectionDto> Connections, string ModuleConnection);

/// <summary>A whole dimension flow document to check, and the file it is meant for (relative to its repository), which messages name.</summary>
public sealed record DeliveryDimensionFlowCheckRequest(string? Yaml, string? Path = null);

/// <summary>Something the check found: an error keeps the flow from loading or building, a warning does not; the dimension and the part of it the finding is about.</summary>
public sealed record DeliveryDimensionFlowIssueDto(string Severity, string Message, string? Dimension = null, string? Target = null);

/// <summary>A dimension the checked flow declares: the kind it reads, its key, and its table where the flow's target keeps it.</summary>
public sealed record DeliveryDimensionFlowCheckDimensionDto(string Name, string Kind, string Path, string Table);

/// <summary>The catalog's flow of the checked flow's name, which a file of that name replaces: its pipeline, repository, file and kind.</summary>
public sealed record DeliveryDimensionFlowExistingDto(Guid PipelineId, Guid RepoId, string Repository, string RelativePath, string Kind);

/// <summary>
/// A whole dimension flow document checked: whether it loads, whether it loads and builds with no error, its name, where it
/// keeps its tables, its dimensions with their tables, its views, the catalog's flow of the same name, and every finding.
/// </summary>
public sealed record DeliveryDimensionFlowCheckDto(
    bool Loads, bool Valid, string? Name, DeliveryDimensionFlowTargetDto? Target, IReadOnlyList<DeliveryDimensionFlowCheckDimensionDto> Dimensions,
    IReadOnlyList<string> Views, DeliveryDimensionFlowExistingDto? Existing, IReadOnlyList<DeliveryDimensionFlowIssueDto> Issues);

/// <summary>
/// Writing a whole dimension flow file (osdu/docs/reference/guides/dimension-flow-file.md): the patterns a new flow follows,
/// read from the dimension flows and the connections the catalog holds, and a check of the whole document by the loader a
/// flow is read by, the templates its dimensions read, and the tables the catalog's other flows write. Both read; nothing
/// is written anywhere.
/// </summary>
public static partial class DeliveryDimensionEndpoints
{
    /// <summary>The most dimension flows the patterns list.</summary>
    public const int MaxPatternFlows = 50;

    /// <summary>The longest envelope a pattern carries.</summary>
    public const int MaxEnvelopeLength = 8_000;

    /// <summary>The longest document the check reads.</summary>
    public const int MaxFlowYamlLength = 256 * 1024;

    /// <summary>A key at the document's top level, the line that starts its block.</summary>
    private static readonly Regex TopLevelKey = new(@"^([A-Za-z_][\w.-]*)\s*:", RegexOptions.CultureInvariant);

    /// <summary>The module's own connection reference, as a flow writes it.</summary>
    internal static string ModuleConnection { get; } = $"${{env:{OsduModuleDatabase.CatalogConnectionVariable}}}";

    private static void MapFlowWriting(RouteGroupBuilder delivery)
    {
        delivery.MapGet("/dimensions/flow-patterns", GetFlowPatternsAsync).WithName("GetDeliveryDimensionFlowPatterns");
        delivery.MapPost("/dimensions/flow-check", CheckFlowAsync).WithName("CheckDeliveryDimensionFlow");
    }

    /// <summary>
    /// The patterns a new dimension flow follows: the catalog's dimension flows, each with its envelope as written, and the
    /// connections the estate's flows read and write their data through.
    /// </summary>
    private static async Task<Ok<DeliveryDimensionFlowPatternsDto>> GetFlowPatternsAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var repositories = await db.Repos.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active)
            .Select(p => new { p.Id, p.RepoId, p.Name, p.Kind, p.Batch, p.RelativePath, p.SourceServer, p.TargetServer, p.LastSeenUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        // The flows a sync saw last come first, as the estate writes its flows now; then by repository and file.
        var dimensionFlows = pipelines.Where(p => string.Equals(p.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastSeenUtc)
            .ThenBy(p => repositories.GetValueOrDefault(p.RepoId) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = dimensionFlows.Take(MaxPatternFlows).Select(p => p.Id).ToList();
        var texts = await db.Pipelines.AsNoTracking()
            .Where(p => shown.Contains(p.Id))
            .Select(p => new { p.Id, p.Yaml, p.RelativePath })
            .ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);

        var flows = new List<DeliveryDimensionFlowPatternDto>(shown.Count);
        foreach (var pipeline in dimensionFlows.Take(MaxPatternFlows))
        {
            var yaml = texts.TryGetValue(pipeline.Id, out var text) ? text.Yaml : string.Empty;
            var envelope = EnvelopeOf(yaml);
            var cut = envelope.Length > MaxEnvelopeLength;
            DimensionFlowDefinition? definition = null;
            string? problem = null;
            try
            {
                definition = documents.ParseDimension(yaml, pipeline.RelativePath);
            }
            catch (FlowValidationException ex)
            {
                problem = WithoutSource(ex.Message, pipeline.RelativePath);
            }

            flows.Add(new DeliveryDimensionFlowPatternDto(
                pipeline.Id,
                pipeline.Name,
                definition?.Description,
                pipeline.RepoId,
                repositories.GetValueOrDefault(pipeline.RepoId) ?? string.Empty,
                pipeline.RelativePath,
                pipeline.Batch,
                definition?.Partitions ?? [],
                definition?.Parameters.Keys.Order(StringComparer.Ordinal).ToList() ?? [],
                definition is null ? null : TargetOf(definition.Target, db),
                definition?.Dimensions.Select(d => d.Name).ToList() ?? [],
                definition?.Views.Select(v => v.Name).ToList() ?? [],
                definition?.Dimensions.Select(d => d.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
                cut ? envelope[..MaxEnvelopeLength] : envelope,
                cut,
                problem));
        }

        var connections = new Dictionary<string, (Dictionary<string, int> Written, Dictionary<string, int> Read)>(StringComparer.OrdinalIgnoreCase);
        void Count(string? reference, string kind, bool written)
        {
            if (string.IsNullOrWhiteSpace(reference) || reference == ServerIdentity.FileSystem)
            {
                return;
            }

            if (!connections.TryGetValue(reference, out var counts))
            {
                counts = (new Dictionary<string, int>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));
                connections[reference] = counts;
            }

            var by = written ? counts.Written : counts.Read;
            by[kind] = by.GetValueOrDefault(kind) + 1;
        }

        foreach (var pipeline in pipelines)
        {
            Count(pipeline.SourceServer, pipeline.Kind, written: false);
            Count(pipeline.TargetServer, pipeline.Kind, written: true);
        }

        var listed = connections
            .Select(c => new DeliveryDimensionFlowConnectionDto(
                c.Key,
                string.Equals(c.Key, ModuleConnection, StringComparison.OrdinalIgnoreCase),
                Sorted(c.Value.Written),
                Sorted(c.Value.Read)))
            .OrderByDescending(c => c.WrittenBy.Values.Sum())
            .ThenByDescending(c => c.ReadBy.Values.Sum())
            .ThenBy(c => c.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new DeliveryDimensionFlowPatternsDto(flows, Math.Max(0, dimensionFlows.Count - MaxPatternFlows), listed, ModuleConnection));
    }

    /// <summary>
    /// A whole dimension flow document checked as a sync and a build would take it: read by the loader, each dimension's
    /// blueprint against the templates the synced repositories hold, its tables against those the catalog's other dimension
    /// flows and the ledger's dimensions write, its partitions against the registry, and where it keeps its tables against
    /// where the estate keeps its data.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionFlowCheckDto>, ProblemHttpResult>> CheckFlowAsync(
        DeliveryDimensionFlowCheckRequest? body, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger,
        IRepositoryFolders folders, IPartitionRegistry registry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Yaml))
        {
            return DeliveryEndpoints.Invalid("Send the whole dimension flow document to check.");
        }

        if (body.Yaml.Length > MaxFlowYamlLength)
        {
            return DeliveryEndpoints.Invalid(string.Create(CultureInfo.InvariantCulture, $"The document is longer than the {MaxFlowYamlLength:N0} characters the check reads."));
        }

        var source = string.IsNullOrWhiteSpace(body.Path) ? "the flow" : body.Path.Trim();
        var issues = new List<DeliveryDimensionFlowIssueDto>();
        DimensionFlowDefinition definition;
        try
        {
            definition = documents.ParseDimension(body.Yaml, source);
        }
        catch (FlowValidationException ex)
        {
            issues.Add(new DeliveryDimensionFlowIssueDto(DimensionDraftIssue.Error, WithoutSource(ex.Message, source)));
            return TypedResults.Ok(new DeliveryDimensionFlowCheckDto(false, false, null, null, [], [], null, issues));
        }

        var target = DimensionTableTarget.Of(definition.Target);
        var targetDto = TargetOf(definition.Target, db);
        var dimensions = definition.Dimensions
            .Select(d => new DeliveryDimensionFlowCheckDimensionDto(d.Name, d.Kind, d.Path, target.Shown(DimensionTables.NameOf(d.Name))))
            .ToList();

        var templates = await RepositoryTemplates.ReadAsync(ledger, folders, ct).ConfigureAwait(false);
        foreach (var spec in definition.Dimensions)
        {
            var blueprint = await DimensionBlueprints.DescribeAsync(spec, templates, [], ct).ConfigureAwait(false);
            issues.AddRange(DeliveryExplorerEndpoints.BlueprintIssues(spec, blueprint)
                .DistinctBy(i => (i.Severity, i.Message, i.Target))
                .Select(i => new DeliveryDimensionFlowIssueDto(i.Severity, i.Message, spec.Name, i.Target)));
        }

        issues.AddRange(await TableCollisionsAsync(definition, db, osdu, documents, ct).ConfigureAwait(false));

        var existing = await ExistingFlowAsync(definition.Name, db, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            issues.Add(string.Equals(existing.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase)
                ? new DeliveryDimensionFlowIssueDto(
                    DimensionDraftIssue.Warning,
                    $"Flow '{definition.Name}' is already in repository {existing.Repository} at {existing.RelativePath}: this document replaces it there, dimensions and all. Name a new flow otherwise.",
                    Target: "name")
                : new DeliveryDimensionFlowIssueDto(
                    DimensionDraftIssue.Error,
                    $"A {existing.Kind} flow named '{definition.Name}' is already in repository {existing.Repository} at {existing.RelativePath}; a flow's name is its identity, so name this one otherwise.",
                    Target: "name"));
        }

        issues.AddRange(await PartitionIssuesAsync(definition, registry, ct).ConfigureAwait(false));
        issues.AddRange(await TargetIssuesAsync(targetDto, db, ct).ConfigureAwait(false));

        var distinct = issues.DistinctBy(i => (i.Severity, i.Message, i.Dimension, i.Target)).ToList();
        return TypedResults.Ok(new DeliveryDimensionFlowCheckDto(
            true,
            distinct.All(i => i.Severity != DimensionDraftIssue.Error),
            definition.Name,
            targetDto,
            dimensions,
            definition.Views.Select(v => v.Name).ToList(),
            existing,
            distinct));
    }

    /// <summary>
    /// The document without its <c>dimensions</c> and <c>views</c> blocks: every top-level key's lines as written, comments
    /// and all, but those two lists, which a new flow writes its own of.
    /// </summary>
    internal static string EnvelopeOf(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kept = new StringBuilder();
        var skipping = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (TopLevelKey.Match(line) is { Success: true } key)
            {
                skipping = key.Groups[1].Value is "dimensions" or "views";
            }

            if (!skipping)
            {
                kept.Append(line).Append('\n');
            }
        }

        return kept.ToString().TrimEnd() + "\n";
    }

    /// <summary>The target a flow declares, as the patterns and the check say it; null for none, which keeps the tables in the module's database.</summary>
    private static DeliveryDimensionFlowTargetDto? TargetOf(DimensionTarget? declared, CatalogDbContext db)
    {
        if (declared is null)
        {
            return null;
        }

        var target = DimensionTableTarget.Of(declared);
        var moduleConnection = target.Connection is null || string.Equals(target.Connection, ModuleConnection, StringComparison.OrdinalIgnoreCase);
        var moduleDatabase = target.Database is null || string.Equals(target.Database, ModuleDatabaseName(db), StringComparison.OrdinalIgnoreCase);
        return new DeliveryDimensionFlowTargetDto(target.Connection, target.Database, target.Schema, moduleConnection && moduleDatabase);
    }

    /// <summary>The name of the module's database, the catalog's: what a target naming it on the module's connection reaches.</summary>
    private static string? ModuleDatabaseName(CatalogDbContext db)
    {
        try
        {
            return db.Database.GetDbConnection().Database;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tables of the flow's dimensions that a dimension of another flow writes, or would: one the ledger keeps under
    /// another flow, or one another active dimension flow of the catalog declares. A build of the second is refused, so each is
    /// an error; a dimension of the flow's own name is the flow replacing itself.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryDimensionFlowIssueDto>> TableCollisionsAsync(
        DimensionFlowDefinition definition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var mine = definition.Dimensions.ToDictionary(d => DimensionTables.NameOf(d.Name), d => d.Name, StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Own(string table, string flow, string dimension)
        {
            if (!mine.ContainsKey(table) || SameFlow(flow, definition.Name))
            {
                return;
            }

            if (!owners.TryGetValue(table, out var by))
            {
                by = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                owners[table] = by;
            }

            by.Add($"dimension {dimension} of flow {FlowOf(flow)}");
        }

        var kept = await osdu.DeliveryDimensions.AsNoTracking()
            .Select(d => new { d.FlowName, d.Name })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var dimension in kept)
        {
            Own(DimensionTables.NameOf(dimension.Name), dimension.FlowName, dimension.Name);
        }

        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active && p.Name != definition.Name)
            .Select(p => new { p.Yaml, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var flow in flows)
        {
            DimensionFlowDefinition declared;
            try
            {
                declared = documents.ParseDimension(flow.Yaml, flow.RelativePath);
            }
            catch (FlowValidationException)
            {
                // A flow whose copy does not parse declares nothing a build would write; its own page says why.
                continue;
            }

            foreach (var dimension in declared.Dimensions)
            {
                Own(DimensionTables.NameOf(dimension.Name), declared.Name, dimension.Name);
            }
        }

        return owners
            .OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(o => new DeliveryDimensionFlowIssueDto(
                DimensionDraftIssue.Error,
                $"{o.Key} is the table of {string.Join(", ", o.Value)}. A dimension's table is named after the dimension alone, so a build of this flow is refused: give dimension {mine[o.Key]} another name.",
                mine[o.Key],
                "name"))
            .ToList();
    }

    /// <summary>Whether a ledger's flow name (<c>name@partition</c> for a flow working in partitions) is the flow named <paramref name="name"/>.</summary>
    private static bool SameFlow(string ledgerName, string name)
        => string.Equals(FlowOf(ledgerName), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A ledger's flow name without the partition it is kept in.</summary>
    private static string FlowOf(string ledgerName)
    {
        var at = ledgerName.LastIndexOf('@');
        return at > 0 ? ledgerName[..at] : ledgerName;
    }

    /// <summary>The catalog's active flow named <paramref name="name"/>, of any kind: a file of that name replaces it, or collides with it.</summary>
    private static async Task<DeliveryDimensionFlowExistingDto?> ExistingFlowAsync(string name, CatalogDbContext db, CancellationToken ct)
    {
        var found = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && p.Name == name)
            .OrderBy(p => p.RelativePath)
            .Select(p => new { p.Id, p.RepoId, p.RelativePath, p.Kind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var repository = await db.Repos.AsNoTracking().Where(r => r.Id == found.RepoId).Select(r => r.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new DeliveryDimensionFlowExistingDto(found.Id, found.RepoId, repository ?? string.Empty, found.RelativePath, found.Kind);
    }

    /// <summary>The partitions the flow names that the registry does not hold: a run in one is refused until it is registered.</summary>
    private static async Task<IReadOnlyList<DeliveryDimensionFlowIssueDto>> PartitionIssuesAsync(
        DimensionFlowDefinition definition, IPartitionRegistry registry, CancellationToken ct)
    {
        var named = definition.Partitions.Concat(definition.Dimensions.SelectMany(d => d.Partitions)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (named.Count == 0)
        {
            return [];
        }

        RegisteredPartitions registered;
        try
        {
            registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (DeliveryException)
        {
            return [];
        }

        return named
            .Where(p => registered.Find(p) is null)
            .Select(p => new DeliveryDimensionFlowIssueDto(
                DimensionDraftIssue.Warning,
                registered.All.Count == 0
                    ? $"Partition '{p}' is named, and no partition is registered with the catalog yet: a run in it is refused until it is."
                    : $"Partition '{p}' is not registered with the catalog, which holds {PartitionNames.Listed(registered.Names)}: a run in it is refused until it is.",
                Target: "partitions"))
            .ToList();
    }

    /// <summary>
    /// Where the flow keeps its tables, against where the estate keeps its data: a flow keeping them in the module's database,
    /// beside the catalog, is told the connections the estate's flows write their data through; one keeping them on a
    /// reference no flow of the catalog names is told that a build must find it set.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryDimensionFlowIssueDto>> TargetIssuesAsync(
        DeliveryDimensionFlowTargetDto? target, CatalogDbContext db, CancellationToken ct)
    {
        var servers = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && p.TargetServer != null && p.TargetServer != ServerIdentity.FileSystem)
            .Select(p => p.TargetServer!)
            .ToListAsync(ct).ConfigureAwait(false);
        var data = servers
            .Where(s => !string.Equals(s, ModuleConnection, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

        if (target is null || target.InModuleDatabase)
        {
            var where = target is null ? "names no target, so it keeps" : "keeps";
            var instead = data.Count == 0
                ? "Name the database the estate keeps its data in under target.connection (and target.database, target.schema)."
                : $"The estate's flows write their data through {string.Join(", ", data)}: name it under target.connection (and target.database, target.schema) to keep them there.";
            return
            [
                new DeliveryDimensionFlowIssueDto(
                    DimensionDraftIssue.Warning,
                    $"The flow {where} its dimension tables and views in the module's database, beside the catalog. {instead}",
                    Target: "target"),
            ];
        }

        var reference = target.Connection;
        if (reference is not null && reference.StartsWith("${", StringComparison.Ordinal)
            && !string.Equals(reference, ModuleConnection, StringComparison.OrdinalIgnoreCase)
            && !servers.Concat(await ReadServersAsync(db, ct).ConfigureAwait(false)).Contains(reference, StringComparer.OrdinalIgnoreCase))
        {
            return
            [
                new DeliveryDimensionFlowIssueDto(
                    DimensionDraftIssue.Warning,
                    data.Count == 0
                        ? $"target.connection {reference} is named by no flow of the catalog: a build resolves it from the partition's values or the node's environment, so it must be set there."
                        : $"target.connection {reference} is named by no flow of the catalog, whose flows write their data through {string.Join(", ", data)}: a build resolves it from the partition's values or the node's environment, so it must be set there.",
                    Target: "target"),
            ];
        }

        return [];
    }

    /// <summary>The connection references the catalog's active flows read from.</summary>
    private static Task<List<string>> ReadServersAsync(CatalogDbContext db, CancellationToken ct)
        => db.Pipelines.AsNoTracking()
            .Where(p => p.Active && p.SourceServer != null)
            .Select(p => p.SourceServer!)
            .Distinct()
            .ToListAsync(ct);

    /// <summary>A loader's message without the document's name it starts with.</summary>
    private static string WithoutSource(string message, string source)
    {
        var text = message.Trim();
        var prefix = source + ": ";
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }

    private static IReadOnlyDictionary<string, int> Sorted(Dictionary<string, int> counts)
        => new SortedDictionary<string, int>(counts, StringComparer.Ordinal);
}
