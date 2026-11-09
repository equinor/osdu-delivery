using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Planning;

/// <summary>
/// Reads, from a rendered record, the OSDU ids it refers to (osdu/docs/reference/flow/interfaces.md, Records that wait
/// for records): the values of every property its template declares a relationship for (<c>x-osdu-relationship</c>),
/// without their version, each with the property holding it. A value that is not a record id is not a reference. Built
/// once per template and shared by the renderers of a plan.
/// </summary>
public sealed class ReferenceReader
{
    private readonly IReadOnlyList<TemplatePath> _paths;

    private ReferenceReader(IReadOnlyList<TemplatePath> paths)
    {
        _paths = paths;
    }

    /// <summary>A reader for no relationship at all: a record read by it refers to nothing.</summary>
    public static ReferenceReader None { get; } = new([]);

    /// <summary>The properties of <paramref name="template"/> a mapping can fill whose schema declares a relationship.</summary>
    public static ReferenceReader Of(OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new ReferenceReader(template.Variables
            .Where(v => v.Relationships.Count > 0 && v.Role == TemplateVariableRole.Mapping && !v.Nested)
            .Select(v => v.Path)
            .ToList());
    }

    /// <summary>
    /// The distinct ids <paramref name="document"/> refers to, in the order the template declares the properties, leaving
    /// out <paramref name="ownId"/>, the record's own.
    /// </summary>
    public IReadOnlyList<RecordReference> Read(JsonObject document, string? ownId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var references = new List<RecordReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _paths)
        {
            foreach (var value in Values(document, path))
            {
                if (!TargetId.IsRecordReference(value))
                {
                    continue;
                }

                var id = TargetId.WithoutVersion(value);
                if (!string.Equals(id, ownId, StringComparison.Ordinal) && seen.Add(id))
                {
                    references.Add(new RecordReference(id, path.RecordPath));
                }
            }
        }

        return references;
    }

    /// <summary>The text values at <paramref name="path"/>: one per item of the arrays it steps into, and each item of a list of values.</summary>
    private static IEnumerable<string> Values(JsonObject document, TemplatePath path)
    {
        IEnumerable<JsonNode?> nodes = [document];
        foreach (var segment in path.Segments)
        {
            nodes = nodes.OfType<JsonObject>().Select(o => o[segment.Name]);
            if (segment.IntoArray)
            {
                nodes = nodes.OfType<JsonArray>().SelectMany(items => items);
            }
        }

        foreach (var node in nodes)
        {
            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    yield return text;
                    break;
                case JsonArray items:
                    foreach (var item in items)
                    {
                        if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
                        {
                            yield return itemText;
                        }
                    }

                    break;
            }
        }
    }
}
