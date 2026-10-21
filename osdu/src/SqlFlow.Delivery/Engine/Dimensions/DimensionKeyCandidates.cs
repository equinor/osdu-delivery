using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A property of a kind's records a dimension's key can be read at, as the kind's template declares it: a value naming
/// another record (<c>x-osdu-relationship</c>), whose record then gives each key its value and attributes.
/// </summary>
/// <param name="Path">The path as a dimension writes it, a list stepped into by its name (<c>data.GeoContexts.GeoPoliticalEntityID</c>).</param>
/// <param name="Names">The entity types the value names (<c>master-data--Wellbore</c>), or a group type alone (<c>reference-data</c>).</param>
/// <param name="Repeated">Whether the path holds several values in one record: a list of ids, or a property of the items of a list.</param>
/// <param name="Title">The template's title of the property; null where it gives none.</param>
/// <param name="Description">The template's description of the property; null where it gives none.</param>
public sealed record DimensionKeyCandidate(string Path, IReadOnlyList<string> Names, bool Repeated, string? Title, string? Description);

/// <summary>
/// The keys a kind's template suggests for a dimension, the likeliest first, and the template they were read from; or why
/// there are none to suggest.
/// </summary>
/// <param name="Kind">The kind, or kind pattern, asked about.</param>
/// <param name="Template">The saved template the suggestions are read from, as a blueprint reads the same kind; null where none is saved.</param>
/// <param name="Keys">The suggestions, the likeliest first.</param>
/// <param name="Missing">Why nothing could be suggested; null where the template was read.</param>
public sealed record DimensionKeySuggestions(string Kind, BlueprintTemplate? Template, IReadOnlyList<DimensionKeyCandidate> Keys, string? Missing);

/// <summary>
/// The keys a kind's records suggest for a dimension (osdu/docs/reference/concepts/explorer.md, Building a dimension),
/// read from the saved template a dimension of that kind is described by: every property of <c>data</c> whose value
/// names another record, since a dimension's key is most often the record its records belong to (a log's wellbore, a
/// wellbore's well), whose name is the value. They are ordered the way a person picks one: a single value before a list
/// of them, a property of <c>data</c> itself before one nested in an object of it, master data before work products and
/// those before reference data, a property named after the type it names (<c>WellID</c> naming a Well) before another
/// of the same group, and then as the schema declares them. Nothing is read from OSDU.
/// </summary>
public static class DimensionKeyCandidates
{
    /// <summary>
    /// The suggestions for <paramref name="kind"/> (wildcards allowed per segment), from the templates
    /// <paramref name="templates"/> offers: the saved templates for a dimension drafted in the explorer, before any flow
    /// holds it.
    /// </summary>
    public static async Task<DimensionKeySuggestions> SuggestAsync(string kind, ITemplateSource templates, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(templates);
        var (matching, problems) = await DimensionBlueprints.MatchingAsync(kind, templates, document: null, ct).ConfigureAwait(false);
        if (matching.Count == 0)
        {
            return new DimensionKeySuggestions(
                kind, null, [],
                problems.Count > 0
                    ? $"No key can be suggested for {kind}: {problems[0]}"
                    : $"No saved template matches {kind}, so no key can be suggested. Pick a property of one of its records, or save the kind's template on the Templates page.");
        }

        var newest = matching[0];
        return new DimensionKeySuggestions(kind, new BlueprintTemplate(newest.Template.Kind, newest.Template.Version), Of(OsduTemplate.From(newest.Schema)), null);
    }

    /// <summary>The suggestions a template makes, the likeliest first.</summary>
    public static IReadOnlyList<DimensionKeyCandidate> Of(OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Variables
            .Select((variable, order) => (Variable: variable, Order: order))
            .Where(v => v.Variable.Role == TemplateVariableRole.Mapping
                && v.Variable.Path.Root == "data"
                && v.Variable.Path.Segments.Count > 1
                && !v.Variable.Nested
                && v.Variable.Relationships.Count > 0
                && v.Variable.Shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList)
            .Select(v => (
                Candidate: new DimensionKeyCandidate(
                    v.Variable.Path.SchemaPath,
                    v.Variable.Relationships,
                    v.Variable.Shape == TemplateVariableShape.ValueList || v.Variable.Path.IsRepeated,
                    v.Variable.Title,
                    v.Variable.Description),
                v.Order,
                Depth: v.Variable.Path.Segments.Count,
                Named: NamedAfter(v.Variable.Path.Leaf, v.Variable.Relationships),
                Group: v.Variable.Relationships.Min(GroupRank)))
            .OrderBy(c => c.Candidate.Repeated)
            .ThenBy(c => c.Depth)
            .ThenBy(c => c.Group)
            .ThenBy(c => !c.Named)
            .ThenBy(c => c.Order)
            .Select(c => c.Candidate)
            .ToList();
    }

    /// <summary>Whether a property is named after a type it names: <c>WellboreID</c> or <c>WellboreIDs</c> naming a Wellbore.</summary>
    private static bool NamedAfter(string property, IReadOnlyList<string> names)
        => names.Any(name =>
        {
            var type = TypeOf(name);
            return type.Length > 0
                && (string.Equals(property, type + "ID", StringComparison.OrdinalIgnoreCase) || string.Equals(property, type + "IDs", StringComparison.OrdinalIgnoreCase));
        });

    /// <summary>The type of an entity type without its group: <c>Wellbore</c> of <c>master-data--Wellbore</c>; empty for a group alone.</summary>
    private static string TypeOf(string name)
    {
        var at = name.IndexOf("--", StringComparison.Ordinal);
        return at < 0 ? string.Empty : name[(at + 2)..];
    }

    /// <summary>How likely a key naming records of a group is: the records a record belongs to first, the codes it is described by last.</summary>
    private static int GroupRank(string name)
    {
        var at = name.IndexOf("--", StringComparison.Ordinal);
        var group = at < 0 ? name : name[..at];
        return group switch
        {
            "master-data" => 0,
            "work-product-component" or "work-product" => 1,
            "reference-data" => 2,
            _ => 3,
        };
    }
}
