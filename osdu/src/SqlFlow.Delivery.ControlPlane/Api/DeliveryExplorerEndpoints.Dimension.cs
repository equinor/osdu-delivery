using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
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
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>What the dimension builder asks of the keys a drafted dimension reads.</summary>
/// <param name="Kind">The kind whose records are read, wildcards allowed per segment.</param>
/// <param name="Query">The query narrowing them, as the dimension would write it; null for every record of the kind.</param>
/// <param name="Path">The key's path: its commonest values are read, each with the records holding it.</param>
public sealed record DeliveryDimensionKeysRequest(string? Kind, string? Query = null, string? Path = null);

/// <summary>A drafted dimension to write as YAML and check, and the key to make an example row of.</summary>
public sealed record DeliveryDimensionComposeRequest(DimensionDraft? Draft, string? Example = null);

/// <summary>
/// One key of a drafted dimension made into its row as a build makes it: the key, its label and the record it came from, why
/// it has none, its value or why it is left out of every value, its attributes, the records holding it and the filter
/// finding them, and what the reads had to say.
/// </summary>
public sealed record DeliveryDimensionExampleDto(
    string Key, string? Label, string? LabelFrom, string? Problem, string? Value, string? LeftOut, string? Note, IReadOnlyList<DeliveryDimensionAttributeDto> Attributes,
    long? Records, string? Filter, IReadOnlyList<string> Notes);

/// <summary>
/// A drafted dimension as the builder shows it: the item a flow lists under <c>dimensions</c>, its lines with where each
/// thing it declares is written, the dimension as the dimension pages describe one (null until the item loads), how a build
/// would make each column (null until it loads), the table it would write, what is wrong with it, whether it loads with no
/// error, and the example row (or why there is none).
/// </summary>
public sealed record DeliveryDimensionComposeDto(
    string Yaml, DeliveryDimensionYamlDto Item, DeliveryDimensionDto? Dimension, DimensionBlueprint? Blueprint, string? Table,
    IReadOnlyList<DimensionDraftIssue> Issues, bool Valid, DeliveryDimensionExampleDto? Example, string? ExampleProblem);

/// <summary>
/// The explorer's dimension builder (osdu/docs/reference/concepts/explorer.md, Building a dimension): what a person
/// picks from the records OSDU holds, written as the item a dimension flow lists and checked by the same loader and
/// blueprint a flow's own dimension is. The explorer's own reads show the records a person picks from; here, the keys a
/// kind's template suggests, the commonest keys of a path (through the partition's own connection, as the explorer reads it),
/// and a compose that writes the draft, checks it, and makes one key into its row as a build would. Nothing is saved:
/// the YAML is the builder's output.
/// </summary>
public static partial class DeliveryExplorerEndpoints
{
    /// <summary>The longest query the builder reads by, as long as a search the explorer asks.</summary>
    public const int MaxQueryLength = ExplorerSearch.MaxTextLength;

    private static void MapDimensionBuilderEndpoints(RouteGroupBuilder delivery)
    {
        delivery.MapGet("/explorer/dimension/candidates", DimensionCandidatesAsync).WithName("SuggestDeliveryDimensionKeys");
        delivery.MapPost("/explorer/dimension/keys", DimensionKeysAsync).WithName("ReadDeliveryDimensionKeys");
        delivery.MapPost("/explorer/dimension/compose", ComposeDimensionAsync).WithName("ComposeDeliveryDimensionDraft");
    }

    /// <summary>
    /// The keys the template of a kind suggests for a dimension, the likeliest first: the properties whose values name
    /// other records. Read from the templates the synced repositories hold, from their files, alone; nothing is asked of OSDU.
    /// </summary>
    private static async Task<Results<Ok<DimensionKeySuggestions>, ProblemHttpResult>> DimensionCandidatesAsync(
        [FromQuery] string? kind, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        var trimmed = kind?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DeliveryEndpoints.Invalid("Name the kind whose records the dimension reads.");
        }

        if (ExplorerKinds.Problem(trimmed) is { } wrong)
        {
            return DeliveryEndpoints.Invalid(wrong);
        }

        var templates = await RepositoryTemplates.ReadAsync(osdu, folders, ct).ConfigureAwait(false);
        return TypedResults.Ok(await DimensionKeyCandidates.SuggestAsync(trimmed, templates, ct).ConfigureAwait(false));
    }

    /// <summary>The commonest keys of a drafted dimension's path, with the records holding each, which the builder's example steps through.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> DimensionKeysAsync(
        DeliveryDimensionKeysRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, ILedger ledger, DeliveryConfigStore config,
        DirectOperations direct, IRepositoryFolders folders, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (KeysProblem(body) is { } invalid)
        {
            return DeliveryEndpoints.Invalid(invalid);
        }

        var kind = body!.Kind!.Trim();
        var path = body.Path!.Trim();
        var templates = await RepositoryTemplates.ReadAsync(ledger, folders, ct).ConfigureAwait(false);
        var (field, guessed) = await KeyFieldAsync(templates, kind, path, ct).ConfigureAwait(false);
        var keys = new DimensionKeysRequest
        {
            Kind = kind,
            Query = string.IsNullOrWhiteSpace(body.Query) ? null : body.Query.Trim(),
            Path = path,
            KeyField = field is null ? null : DimensionFieldWire.Of(field),
            KeyFieldGuessed = guessed,
        };

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        LongArgument.Put(arguments, ExploreOperation.KeysArgument, JsonSerializer.Serialize(keys, ExploreOperation.BuilderJson));
        return await ExploreAsync(ExploreOperation.DimensionKeysAction, arguments, partition, partitions, config, direct, loggers, request, user, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A drafted dimension written as YAML and checked: by the document loader, as a flow's own item is read, then against the
    /// templates the synced repositories hold as its blueprint describes it, and against the dimensions of the database, whose tables it must not
    /// share; with one key made into its row through the partition's own connection, as the explorer reads it.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionComposeDto>, ProblemHttpResult>> ComposeDimensionAsync(
        DeliveryDimensionComposeRequest? body, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, ILedger ledger, DeliveryConfigStore config, DirectOperations direct, IRepositoryFolders folders, ILoggerFactory loggers,
        HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (body?.Draft is not { } draft)
        {
            return DeliveryEndpoints.Invalid("Send the draft to write and check.");
        }

        if (DraftProblem(draft) is { } invalid)
        {
            return DeliveryEndpoints.Invalid(invalid);
        }

        var item = DimensionBuilder.ToYaml(draft);
        var document = DimensionBuilder.Document(item);
        var issues = new List<DimensionDraftIssue>(DimensionBuilder.Incomplete(draft));
        DimensionSpec? spec = null;
        if (issues.Count == 0)
        {
            try
            {
                spec = documents.ParseDimension(document, DimensionBuilder.CheckSource).Dimensions[0];
            }
            catch (FlowValidationException ex)
            {
                issues.Add(DimensionBuilder.FromLoader(ex.Message));
            }
        }

        DimensionBlueprint? blueprint = null;
        if (spec is not null)
        {
            blueprint = await DimensionBlueprints.DescribeAsync(spec, await RepositoryTemplates.ReadAsync(ledger, folders, ct).ConfigureAwait(false), [], ct).ConfigureAwait(false);
            issues.AddRange(BlueprintIssues(spec, blueprint));
            if (await CollisionAsync(spec, db, osdu, documents, ct).ConfigureAwait(false) is { } collision)
            {
                issues.Add(collision);
            }
        }

        DeliveryDimensionExampleDto? example = null;
        string? exampleProblem = null;
        if (spec is not null && blueprint is not null && !string.IsNullOrEmpty(body.Example))
        {
            (example, exampleProblem) = await ExampleAsync(
                spec, blueprint, item, body.Example, partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
        }

        var distinct = issues.DistinctBy(i => (i.Severity, i.Message, i.Target)).ToList();
        return TypedResults.Ok(new DeliveryDimensionComposeDto(
            item,
            ItemOf(document, item, spec),
            spec is null ? null : DeliveryDimensionEndpoints.DraftDto(spec),
            blueprint,
            spec is null ? null : DimensionTables.Shown(DimensionTables.NameOf(spec.Name)),
            distinct,
            spec is not null && distinct.All(i => i.Severity != DimensionDraftIssue.Error),
            example,
            exampleProblem));
    }

    /// <summary>Why the keys cannot be asked for, as the operation would refuse them; null when they can.</summary>
    internal static string? KeysProblem(DeliveryDimensionKeysRequest? body)
    {
        var kind = body?.Kind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            return "Name the kind whose records the dimension reads.";
        }

        if (ExplorerKinds.Problem(kind) is { } wrong)
        {
            return wrong;
        }

        if (body!.Query is { Length: > MaxQueryLength })
        {
            return string.Create(CultureInfo.InvariantCulture, $"The query is longer than the {MaxQueryLength:N0} characters a search takes.");
        }

        var path = body.Path?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return "Name the key's path, such as data.WellboreID.";
        }

        return PathProblem(path);
    }

    /// <summary>Why a draft is too large to be read, part by part; null when it is not. What each part may hold is the loader's to say.</summary>
    internal static string? DraftProblem(DimensionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var texts = new (string Part, string? Text)[]
        {
            ("name", draft.Name), ("description", draft.Description), ("kind", draft.Kind), ("query", draft.Query), ("path", draft.Path),
            ("unlabelled", draft.Unlabelled), ("columns.key", draft.KeyColumn), ("columns.value", draft.ValueColumn),
        };
        if (texts.FirstOrDefault(t => t.Text is { Length: > DimensionBuilder.MaxTextLength }) is { Part: { } part })
        {
            return string.Create(CultureInfo.InvariantCulture, $"The draft's {part} is longer than the {DimensionBuilder.MaxTextLength:N0} characters the builder reads.");
        }

        var lists = new (string Part, int Count)[]
        {
            ("label", draft.Label?.Count ?? 0), ("attributes", draft.Attributes?.Count ?? 0), ("clean", draft.Clean?.Count ?? 0),
        };
        if (lists.FirstOrDefault(l => l.Count > DimensionBuilder.MaxListLength) is { Part: { } list })
        {
            return $"The draft's {list} lists more than the {DimensionBuilder.MaxListLength} entries the builder reads.";
        }

        var nested = (draft.Label ?? [])
            .Concat((draft.Attributes ?? []).Where(a => a is not null).SelectMany(a => (a.Steps ?? []).Append(a.Name).Append(a.Collect)))
            .Concat((draft.Clean ?? []).Where(c => c is not null).SelectMany(c => new[] { c.Step, c.Pattern, c.With }));
        if (nested.Any(text => text is { Length: > DimensionBuilder.MaxTextLength }))
        {
            return string.Create(CultureInfo.InvariantCulture, $"A path, a name or a clean step of the draft is longer than the {DimensionBuilder.MaxTextLength:N0} characters the builder reads.");
        }

        return (draft.Attributes ?? []).Any(a => a?.Steps is { Count: > DimensionBuilder.MaxListLength })
            ? $"An attribute of the draft lists more than the {DimensionBuilder.MaxListLength} paths the builder reads."
            : null;
    }

    /// <summary>Why a key's path cannot be read, as the loader says it; null when it can.</summary>
    private static string? PathProblem(string path)
    {
        if (!OsduPath.IsPath(path))
        {
            return $"'{path}' is not a property path: segments of letters, digits and underscores separated by dots, such as data.WellboreID.";
        }

        return SearchFields.IsDataPath(path) || SearchFields.RecordProperty(path).Problem is not { } problem ? null : $"The key {problem}";
    }

    /// <summary>
    /// How the platform indexes the key's path, as a build settles it: by the template of the kind a synced repository holds
    /// (a dimension's blueprint picks it the same way), or by the indexer's own mapping for a property of the record itself.
    /// A path of data no such template describes is read as text, which most are, and said to be a guess.
    /// </summary>
    private static async Task<(OsduField? Field, bool Guessed)> KeyFieldAsync(ITemplateSource templates, string kind, string path, CancellationToken ct)
    {
        var described = await DimensionBlueprints.DescribeAsync(new DimensionSpec { Name = "key", Kind = kind, Path = path }, templates, [], ct).ConfigureAwait(false);
        var key = described.Source.Reads.First(r => r.Id == BlueprintRoles.KeyRead);
        if (key.Index is { } index && new DimensionFieldWire(path, index.Index, index.NestedPath).Field() is { } field)
        {
            return (field, false);
        }

        return described.Source.Template is null && SearchFields.IsDataPath(path) ? (OsduField.Text(path), true) : (null, false);
    }

    /// <summary>
    /// What the blueprint finds wrong with a draft, as issues: a key or a collected path a build cannot read, and no template
    /// to read a path of data by, stop a build and are errors; a path of the records read by id that a template does not
    /// declare, or records no template a synced repository holds describes, are read as written and are warnings.
    /// </summary>
    internal static IEnumerable<DimensionDraftIssue> BlueprintIssues(DimensionSpec spec, DimensionBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(blueprint);
        if (blueprint.Source.Missing is { } missing)
        {
            var stops = SearchFields.IsDataPath(spec.Path) || spec.Attributes.Any(a => a.Collect is { } collect && SearchFields.IsDataPath(collect));
            yield return new DimensionDraftIssue(stops ? DimensionDraftIssue.Error : DimensionDraftIssue.Warning, missing, "kind", DimensionDraftIssue.TemplateCode);
        }

        foreach (var read in blueprint.Source.Reads.Where(r => r.Problem is not null))
        {
            var target = read.Id == BlueprintRoles.KeyRead ? "path" : $"attributes.{(read.Uses.Count > 0 ? read.Uses[0].Attribute : null)}";
            yield return new DimensionDraftIssue(DimensionDraftIssue.Error, read.Problem!, target);
        }

        foreach (var node in blueprint.Records)
        {
            if (node.Missing is { } why)
            {
                yield return new DimensionDraftIssue(DimensionDraftIssue.Warning, why, null, node.EntityTypes.Count > 0 ? DimensionDraftIssue.TemplateCode : null);
            }

            foreach (var read in node.Reads.Where(r => r.Problem is not null))
            {
                foreach (var use in read.Uses)
                {
                    var target = use.Role == BlueprintRoles.Label ? "label" : $"attributes.{use.Attribute}";
                    yield return new DimensionDraftIssue(DimensionDraftIssue.Warning, read.Problem!, target);
                }
            }
        }
    }

    /// <summary>
    /// Another flow's dimension that writes, or would write, the table the draft would: one a flow of the catalog declares
    /// under a name making the same table, or one the ledger keeps a table of. A build of the second is refused, so the draft
    /// is told before it is pasted anywhere; pasted into that same flow, it replaces the dimension there.
    /// </summary>
    private static async Task<DimensionDraftIssue?> CollisionAsync(
        DimensionSpec spec, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var table = DimensionTables.NameOf(spec.Name);
        var owners = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        var kept = await osdu.DeliveryDimensions.AsNoTracking()
            .Where(d => d.TableName == table || d.Name == spec.Name)
            .Select(d => new { d.FlowName, d.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var dimension in kept.Where(d => string.Equals(DimensionTables.NameOf(d.Name), table, StringComparison.OrdinalIgnoreCase)))
        {
            owners.Add(dimension.FlowName);
            named.Add(dimension.Name);
        }

        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active)
            .Select(p => new { p.Name, p.Yaml, p.RelativePath })
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

            foreach (var dimension in declared.Dimensions.Where(d => string.Equals(DimensionTables.NameOf(d.Name), table, StringComparison.OrdinalIgnoreCase)))
            {
                owners.Add(declared.Name);
                named.Add(dimension.Name);
            }
        }

        if (owners.Count == 0)
        {
            return null;
        }

        var flowsText = string.Join(", ", owners);
        return new DimensionDraftIssue(
            DimensionDraftIssue.Warning,
            $"{DimensionTables.Shown(table)} is the table of dimension {string.Join(", ", named)} of {flowsText}. A dimension's table is named after the dimension alone, so a second flow declaring {spec.Name} is refused when it builds: paste this item into {(owners.Count == 1 ? flowsText : "that flow")} to replace its dimension, or give this one another name.",
            "name",
            DimensionDraftIssue.NameCode);
    }

    /// <summary>
    /// The example row: <paramref name="key"/> made into its row by the explorer's operation, through the partition's own
    /// connection, reading the item exactly as written; or why there is none.
    /// </summary>
    private static async Task<(DeliveryDimensionExampleDto? Example, string? Problem)> ExampleAsync(
        DimensionSpec spec, DimensionBlueprint blueprint, string item, string key, string? partition, IPartitionRegistry partitions, DeliveryConfigStore config,
        DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (key.Length > DimensionSpec.MaxOriginalLength)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"The example key is longer than the {DimensionSpec.MaxOriginalLength:N0} characters a dimension keeps."));
        }

        var found = await ConnectAsync(partitions, config, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (found.Configured is not { } configured)
        {
            return (null, found.Reason);
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExploreOperation.ActionArgument] = ExploreOperation.DimensionExampleAction,
            [ExploreOperation.KeyArgument] = key,
        };
        try
        {
            LongArgument.Put(arguments, ExploreOperation.ItemArgument, item);
            LongArgument.Put(arguments, ExploreOperation.FieldsArgument, JsonSerializer.Serialize(FieldsOf(spec, blueprint), ExploreOperation.BuilderJson));
        }
        catch (SqlFlowException ex)
        {
            return (null, ex.Message);
        }

        var answered = await DirectOperationRunner.RunOnPartitionAsync(direct, found.Partition!, configured, arguments, user, loggers, ct).ConfigureAwait(false);
        switch (answered.Result)
        {
            case ContentHttpResult { ResponseContent: { } json }:
                using (var parsed = JsonDocument.Parse(json))
                {
                    if (!parsed.RootElement.TryGetProperty("answer", out var answer)
                        || answer.Deserialize<DimensionExample>(ExploreOperation.BuilderJson) is not { } made)
                    {
                        return (null, "The example was answered with nothing to show.");
                    }

                    return (new DeliveryDimensionExampleDto(
                        made.Key, made.Label, made.LabelFrom, made.Problem, made.Value, made.LeftOut, made.Note,
                        made.Attributes.Select(a => new DeliveryDimensionAttributeDto(a.Name, a.Value, a.From, a.Records)).ToList(),
                        made.Records, made.Filter, made.Notes), null);
                }

            case ProblemHttpResult problem:
                return (null, problem.ProblemDetails.Detail ?? problem.ProblemDetails.Title ?? "The example could not be made.");
            default:
                return (null, "The example could not be made.");
        }
    }

    /// <summary>How the key and each collected path are indexed, as the blueprint settled them from the templates the synced repositories hold; none where no template says.</summary>
    private static DimensionExampleFields FieldsOf(DimensionSpec spec, DimensionBlueprint blueprint)
    {
        DimensionFieldWire? WireOf(string id, string path)
            => blueprint.Source.Reads.FirstOrDefault(r => r.Id == id)?.Index is { } index ? new DimensionFieldWire(path, index.Index, index.NestedPath) : null;

        var collected = new Dictionary<string, DimensionFieldWire>(StringComparer.Ordinal);
        foreach (var attribute in spec.Attributes.Where(a => a.IsCollected))
        {
            if (WireOf(BlueprintRoles.CollectRead(attribute.Name), attribute.Collect!) is { } wire)
            {
                collected[attribute.Name] = wire;
            }
        }

        return new DimensionExampleFields(WireOf(BlueprintRoles.KeyRead, spec.Path), collected);
    }

    /// <summary>
    /// The item's lines, numbered from 1, with where each thing it declares is written: located in the document the item was
    /// read back in, by the same reader a flow's dimension page uses. An item that did not load is shown without its spans.
    /// </summary>
    private static DeliveryDimensionYamlDto ItemOf(string document, string item, DimensionSpec? spec)
    {
        if (spec is not null && DimensionYamlSource.Locate(document, spec.Name) is { } block)
        {
            var shift = block.FirstLine - 1;
            return new DeliveryDimensionYamlDto(
                null, 1, block.Lines,
                block.Spans.Select(s => s with { Line = s.Line - shift, EndLine = s.EndLine - shift }).ToList(),
                block.Cut);
        }

        return new DeliveryDimensionYamlDto(null, 1, item.TrimEnd('\n').Split('\n'), [], false);
    }
}
