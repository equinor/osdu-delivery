using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// Values a record takes from the records it is related to: a $parent node's section of the record's parent
// (osdu/docs/reference/flow/mapping-parent.md), a $link node's ids of the datasets its enrichment registers
// (osdu/docs/reference/flow/enrich-records.md), a $base node's section of the record an enrichment renders into
// (osdu/docs/reference/flow/enrich.md), and a $child node's reference to the record's child
// (osdu/docs/reference/flow/mapping-child.md).
internal static partial class EntryValues
{
    /// <summary>
    /// The value of a <c>$parent</c> node (osdu/docs/reference/flow/mapping-parent.md): the section at its path of the record's
    /// parent, the record of the kind the template is linked to whose OSDU id is the record's own with the parent's entity
    /// type. The parent is read from the platform between two renders, as a search is asked: a render that meets it first
    /// stops there, naming it, and the caller reads it and renders again. A parent the platform does not hold holds the
    /// record, whatever <c>$required</c> says: what the record takes from it is unknown, not absent. A parent without the
    /// section is an empty value, which <c>$required</c> decides. The section is copied whole, an object or a list as the
    /// parent holds it, and a single value passes through the node's modifiers like a column's.
    /// </summary>
    private static object? FromParent(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var section = entry.Source!.ParentPath!;
        if (renderer.ParentOf(root) is not { } question)
        {
            // A row without a complete key is held for that already; a template naming no parent is refused by the preflight,
            // and said here too, for a render that reaches this without one.
            if (renderer.Schema.Parent is null)
            {
                holds.Add(
                    $"{path}: $parent reads the record's parent, and template {renderer.Mapping.Template} is linked to none ({Templates.TemplateParent.Key}{(renderer.Schema.ParentProblem is { } problem ? ": " + problem : string.Empty)})");
            }

            return null;
        }

        if (!renderer.Search.TryRead(question, out var answer))
        {
            // Not read yet. The render stops here rather than guess, and the caller reads the parent and renders again.
            searched.Ask(question);
            return null;
        }

        searched.Read(question, answer);
        if (!answer.Found)
        {
            holds.Add(
                $"{path}: the record's parent {question.Id} ({question.Kind}) is not in OSDU, or the flow's identity may not read it, and the record takes {section} from it");
            return null;
        }

        var value = answer.At(section);
        if (value is null || (value is JsonValue text && text.TryGetValue<string>(out var said) && string.IsNullOrWhiteSpace(said)))
        {
            if (entry.Required)
            {
                holds.Add($"{path}: the record's parent {question.Id} holds no {section}, and the entry is required");
            }

            return null;
        }

        // The answer is shared by every render that reads the parent, so what is written is a copy of it.
        var copy = value.DeepClone();
        if (entry.Modifiers.Count == 0)
        {
            return copy;
        }

        if (copy is JsonObject or JsonArray)
        {
            holds.Add($"{path}: $modifiers change a single value, and the parent's {section} is {(copy is JsonArray ? "a list" : "an object")}");
            return null;
        }

        if (!TryModify(entry, Native(copy), root, item, renderer, holds, usages, searched, out var modified))
        {
            return null;
        }

        if (IsEmpty(modified))
        {
            if (entry.Required)
            {
                holds.Add($"{path}: the parent's {section} gives no value once modified, and the entry is required");
            }

            return null;
        }

        return modified;
    }

    /// <summary>
    /// The value of a <c>$link</c> node (osdu/docs/reference/flow/enrich-records.md): the ids of the datasets the mapping's
    /// enrichment registers for the record, each as OSDU names a record a list links, with the version separator and no
    /// version. Where the template takes a list, every one of them; where it takes one value, the one an enrichment taking one
    /// row registers. Nothing when it registers none, which leaves the property out: a link never holds its record.
    /// </summary>
    private static JsonNode? Linked(MappingEntry entry, MappingRenderer renderer, List<string> holds, RenderTrail searched)
    {
        var name = entry.Source!.LinkName!;
        var ids = searched.Links?.Ids(name, searched.OwnerId) ?? [];
        if (renderer.Schema.Resolve(entry.Target.SchemaPath)?.Type == SchemaType.Array)
        {
            return ids.Count == 0 ? null : new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(EnrichParts.Link(id))).ToArray());
        }

        switch (ids.Count)
        {
            case 0:
                return null;
            case 1:
                return JsonValue.Create(EnrichParts.Link(ids[0]));
            default:
                holds.Add($"{entry.Target.Text}: $link: {name} gives the {ids.Count} datasets the enrichment registers for this record, and the template takes one value here; link them from a list, or let the enrichment take one row (rows: one)");
                return null;
        }
    }

    /// <summary>
    /// The value of a <c>$base</c> node (osdu/docs/reference/flow/enrich.md): the section at its path of the record an
    /// enrichment mapping renders into, as the record's mapping and the enrichments before this one left it. It is read as the
    /// enrichment renders, never from the platform, so it never waits. A record without the section, or with an empty text
    /// there, is an empty value, which <c>$required</c> decides. The section is copied whole, an object or a list as the record
    /// holds it, and a single value passes through the node's modifiers like a column's.
    /// </summary>
    private static object? FromBase(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var section = entry.Source!.BasePath!;
        if (searched.Base is not { } record)
        {
            // A fragment always renders over a record, and every other mapping is refused for reading $base when it is read,
            // so this is an engine defect; the hold says so rather than writing the record without the value.
            holds.Add($"{path}: $base reads the record a fragment adds to, and this render was given none to read");
            return null;
        }

        var value = Section(record, section);
        if (value is null || (value is JsonValue text && text.TryGetValue<string>(out var said) && string.IsNullOrWhiteSpace(said)))
        {
            if (entry.Required)
            {
                holds.Add($"{path}: the record holds no {section} when the fragment renders, and the entry is required");
            }

            return null;
        }

        // The record is the one every later step adds to, so what is written is a copy of the section.
        var copy = value.DeepClone();
        if (entry.Modifiers.Count == 0)
        {
            return copy;
        }

        if (copy is JsonObject or JsonArray)
        {
            holds.Add($"{path}: $modifiers change a single value, and the record's {section} is {(copy is JsonArray ? "a list" : "an object")}");
            return null;
        }

        if (!TryModify(entry, Native(copy), root, item, renderer, holds, usages, searched, out var modified))
        {
            return null;
        }

        if (IsEmpty(modified))
        {
            if (entry.Required)
            {
                holds.Add($"{path}: the record's {section} gives no value once modified, and the entry is required");
            }

            return null;
        }

        return modified;
    }

    /// <summary>The node at <paramref name="path"/> (property names joined by dots) below <paramref name="record"/>, or null when the record holds none there.</summary>
    internal static JsonNode? Section(JsonObject record, string path)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonNode? node = record;
        foreach (var step in path.Split('.'))
        {
            if (node is not JsonObject holder || !holder.TryGetPropertyValue(step, out node))
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>
    /// The value of a <c>$child</c> node (osdu/docs/reference/flow/mapping-child.md): the reference to the record's child of the
    /// node's kind, the record whose OSDU id is the record's own with the child's entity type, with the version separator and no
    /// version, so the link names whichever version of the child is latest. Whether the platform holds the child is asked
    /// between two renders, as a search is: a render that meets it first stops there, naming it, and the caller asks and
    /// renders again. A child the platform does not hold gives no value and the property is left out, never a hold: the
    /// record is written without the link and linked once the child lands. The mapping refuses <c>$required</c> on the node,
    /// so the one evaluation that asks it as required is <see cref="WhyEmpty"/>'s, which says why from what is noted here.
    /// </summary>
    private static string? ToChild(MappingEntry entry, SourceRow root, MappingRenderer renderer, List<string> holds, RenderTrail searched)
    {
        // A row without a complete key has no id, and no child; it is held for its key already.
        if (renderer.ChildOf(root, entry.Source!.ChildKind!) is not { } question)
        {
            return null;
        }

        if (!renderer.Search.TryRead(question, out var answer))
        {
            searched.Ask(question);
            return null;
        }

        searched.Link(question, answer);
        if (answer.Found)
        {
            return WithVersionSeparator(question.Id);
        }

        if (entry.Required)
        {
            holds.Add($"{entry.Target.Text}: the record's child {question.Id} ({question.Kind}) is not in OSDU, or the flow's identity may not read it, so the record goes out without the link until it lands");
        }

        return null;
    }
}
