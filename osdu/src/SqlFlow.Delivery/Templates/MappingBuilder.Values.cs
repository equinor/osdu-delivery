using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.Templates;

// What one value lacks, as an entry of its own or as one alternative of a coalesce entry: its input and what that
// input reads, the parent section or the child records it links (osdu/docs/reference/flow/mapping-parent.md,
// osdu/docs/reference/flow/mapping-child.md), a fixed value, and a column read in the entry's scope.
public static partial class MappingBuilder
{
    /// <summary>
    /// What a parent input lacks (osdu/docs/reference/flow/mapping-parent.md): the path of the section it takes, its property
    /// names joined by dots and never into an array, read by an entry of the record rather than of a repeater's items, and
    /// nothing that finds or matches a record, since the parent is found by the record's own id. Whether the template is
    /// linked to a parent, and the parent's template declares the path, is the preflight's to judge.
    /// </summary>
    private static void ParentIssues(MappingDraftEntry entry, string name, string target, string? scope, Action<string, string?> error)
    {
        var path = entry.ParentPath?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            error($"{name}: name the section of the record's parent to take, such as data.DDMSDatasets.", target);
        }
        else if (path.Contains('[', StringComparison.Ordinal) || !MappingMapper.ParentPathPattern().IsMatch(path))
        {
            error($"{name}: '{path}' is not a path of the parent: its property names joined by dots, never into an array, such as data.DDMSDatasets or data.ExtensionProperties.wdms.", target);
        }

        if (scope is not null)
        {
            error($"{name}: a row of {scope} has no parent; take the whole array from the parent with a parent input on the array itself.", target);
        }

        if (entry.FindBy.Count > 0 || entry.FindAll is not null || entry.IgnoreSeparators || entry.Unverified)
        {
            error($"{name}: the parent is found by the record's own id, so a parent input takes no findBy lines, find all, folded separators or unverified ids.", target);
        }
    }

    /// <summary>
    /// What a child input lacks (osdu/docs/reference/flow/mapping-child.md): the kind of the child it links, a record kind,
    /// read by an entry of the record rather than of a repeater's items, and nothing that finds, matches or changes a value,
    /// since the child is found by the record's own id and the link is its id. A child never holds its record, so the entry
    /// is never required. Whether the kind's template is linked to this one is the preflight's to judge.
    /// </summary>
    private static void ChildIssues(MappingDraftEntry entry, string name, string target, string? scope, Action<string, string?> error)
    {
        var kind = entry.ChildKind?.Trim() ?? string.Empty;
        if (kind.Length == 0)
        {
            error($"{name}: name the kind of the child to link, a custom template linked to this one, such as welldb:wks:work-product-component--WellLogDetail:1.0.0.", target);
        }
        else if (!FlowMapper.IsRecordKind(kind) || !Identity.TargetId.EntityTypeFromKind(kind).Contains("--", StringComparison.Ordinal))
        {
            error($"{name}: '{kind}' is not the kind of a record, 'authority:source:group--Entity:major.minor.patch'.", target);
        }

        if (scope is not null)
        {
            error($"{name}: a row of {scope} has no children of its own; link the child from an entry outside the repeated array.", target);
        }

        if (entry.FindBy.Count > 0 || entry.FindAll is not null || entry.Modifiers.Count > 0 || entry.IgnoreSeparators || entry.Unverified)
        {
            error($"{name}: the child is found by the record's own id and linked by its own, so a child input takes no findBy lines, find all, modifiers, folded separators or unverified ids.", target);
        }

        if (entry.Required)
        {
            error($"{name}: a child link never holds its record: it is written once OSDU holds the child and left out until then, so the entry is not required.", target);
        }

        if (entry.RestrictWhenMissing)
        {
            error($"{name}: a child link never restricts its record: it is written once OSDU holds the child and left out until then, which says nothing about who may read the record.", target);
        }
    }

    /// <summary>
    /// What one value lacks, as an entry of its own or as one alternative of a coalesce entry (<paramref name="name"/> says
    /// which): its input, its modifiers, and the id it builds.
    /// </summary>
    private static void ValueIssues(MappingDraft draft, MappingDraftEntry entry, string name, string target, string? scope, Action<string, string?> error)
    {
        switch (entry.Input)
        {
            case MappingDraftInput.Dataset when !DatasetColumnPattern().IsMatch(entry.Column ?? string.Empty):
                error($"{name}: choose the dataset column the value comes from.", target);
                break;
            case MappingDraftInput.Dataset:
                ScopeIssue(entry.Column!, scope, name, error);
                break;
            case MappingDraftInput.Cache:
                if (!ColumnName().IsMatch(entry.CacheType ?? string.Empty) || !FieldPath().IsMatch(entry.CacheField ?? string.Empty))
                {
                    error($"{name}: choose the cached type and the field to read.", target);
                }

                if (entry.FindAll is { } findAll)
                {
                    if (entry.FindBy.Count > 0)
                    {
                        error($"{name}: an entry finds one record with findBy lines or reads every matching row with find all, not both.", target);
                    }

                    if (entry.IgnoreSeparators)
                    {
                        error($"{name}: folding punctuation and spacing loosens how one record is found by name, and find all matches each key ignoring case only; turn it off.", target);
                    }

                    FindAllIssues(draft, entry, findAll, name, target, scope, error);
                    break;
                }

                if (entry.FindBy.Count == 0)
                {
                    error($"{name}: say which cached record to read with at least one findBy line.", target);
                }

                foreach (var find in entry.FindBy.SelectMany(line => CompositeIssues(line, name, target, error)))
                {
                    if (!FieldPath().IsMatch(find.Field ?? string.Empty) || find.Field!.StartsWith(Marker, StringComparison.Ordinal))
                    {
                        error($"{name}: a findBy line needs the cached field it compares.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                    {
                        error($"{name}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal))
                    {
                        ScopeIssue(find.Column!, scope, name, error);
                    }
                }

                break;
            case MappingDraftInput.Search:
                if (!draft.Searches.Any(s => string.Equals(s.Name, entry.CacheType, StringComparison.Ordinal)))
                {
                    error($"{name}: choose one of the mapping's searches to look in.", target);
                }

                if (!string.Equals(entry.CacheField, "id", StringComparison.Ordinal))
                {
                    error($"{name}: a search reads the id of the record it finds.", target);
                }

                if (entry.FindBy.Count == 0)
                {
                    error($"{name}: say which record to find with at least one findBy line.", target);
                }

                if (entry.FindBy.Any(find => find.And is { Count: > 0 }))
                {
                    error($"{name}: a search asks the platform for one property at a time, so each findBy line compares one; write each as a line of its own, tried in order.", target);
                }

                foreach (var find in entry.FindBy)
                {
                    if (!(find.Field ?? string.Empty).StartsWith("data.", StringComparison.Ordinal) || !Search.OsduPath.IsPath(find.Field))
                    {
                        error($"{name}: a findBy line compares a property under data, such as data.FacilityName.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                    {
                        error($"{name}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal))
                    {
                        ScopeIssue(find.Column!, scope, name, error);
                    }
                }

                break;
            case MappingDraftInput.Lookup:
                if (!draft.Lookups.Any(l => string.Equals(l.Name?.Trim(), entry.Lookup?.Trim(), StringComparison.Ordinal)))
                {
                    error(draft.Lookups.Count == 0
                        ? $"{name}: the mapping declares no lookup to read; add one under Lookups."
                        : $"{name}: choose one of the mapping's lookups to read.", target);
                }

                if (!FieldPath().IsMatch(entry.CacheField?.Trim() ?? string.Empty) || entry.CacheField!.Trim().StartsWith(Marker, StringComparison.Ordinal))
                {
                    error($"{name}: name the field of the lookup's record to read, such as id or GeoContexts.FieldID.", target);
                }

                if (entry.FindBy.Count > 0 || entry.FindAll is not null || entry.Modifiers.Count > 0 || entry.IgnoreSeparators)
                {
                    error($"{name}: a lookup input reads the record its lookup finds, so how the record is found (findBy lines, modifiers, folding separators) is the lookup's to say.", target);
                }

                break;
            case MappingDraftInput.Parent:
                ParentIssues(entry, name, target, scope, error);
                break;
            case MappingDraftInput.Child:
                ChildIssues(entry, name, target, scope, error);
                break;
            case MappingDraftInput.Static:
                StaticIssue(entry, name, error);
                break;
            case MappingDraftInput.Expression:
                if (string.IsNullOrWhiteSpace(entry.Expression))
                {
                    error($"{name}: write the expression the value is computed with, such as coalesce(log_name, log_source).", target);
                }
                else if (MappingMapper.ReadExpression(entry.Expression.Trim(), MappingMapper.ExprKey, scope, out var expressionProblem) is null)
                {
                    error($"{name}: {expressionProblem}", target);
                }

                break;
        }

        foreach (var modifier in entry.Modifiers)
        {
            ModifierIssue(modifier, scope, name, error);
        }

        // id and ref both build the id an entry writes, so the same rules hold for either.
        var builders = entry.Modifiers.Where(m => m.Kind is "id" or "ref").ToList();
        if (builders.Count > 0 && entry.Input is not (MappingDraftInput.Dataset or MappingDraftInput.Expression))
        {
            error($"{name}: the {builders[0].Kind} modifier builds the id an entry writes from a dataset value; choose a dataset column or an expression as the input.", target);
        }
        else if (builders.Count > 1)
        {
            error($"{name}: an entry builds one id, and this one lists {string.Join(" and ", builders.Select(m => m.Kind))}; keep one.", target);
        }
        else if (builders.Count == 1 && entry.Modifiers[^1].Kind is not ("id" or "ref"))
        {
            error($"{name}: {builders[0].Kind} builds what the entry writes, so it is the last modifier; move it to the end.", target);
        }

        if (entry.Unverified && builders.Count == 0)
        {
            error($"{name}: {MappingMapper.UnverifiedKey} lets an id built with id or ref go out when the cache holds no record under it, and this one builds no id.", target);
        }
    }

    private static void StaticIssue(MappingDraftEntry entry, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(entry.Static) ? null : JsonNode.Parse(entry.Static);
        }
        catch (JsonException)
        {
            error($"{target}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            error($"{target}: give the fixed value.", target);
        }
        else if (EnvelopeTargets.Contains(target, StringComparer.Ordinal) && (node is not JsonArray list || list.Count == 0))
        {
            error($"{target}: every record carries it, so give at least one value.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{target}: {problem}", target);
        }
    }

    /// <summary>
    /// A column an entry names read in the entry's scope: <c>child.column</c> names a column of a child dataset's rows, which
    /// only an entry inside the items of the array repeating that child reads.
    /// </summary>
    private static void ScopeIssue(string column, string? scope, string target, Action<string, string?> error)
    {
        var dot = column.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return;
        }

        var child = column[..dot];
        if (!string.Equals(child, scope, StringComparison.Ordinal))
        {
            error(
                $"{target}: {column.Trim()} reads a column of child dataset '{child}', which only an entry inside the items of the array repeating {child} reads.",
                target);
        }
    }
}
