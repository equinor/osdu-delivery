using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// The record kind a template is linked to as its parent (osdu/docs/reference/concepts/templates.md, Linked templates): a
/// custom template, a non-canonical copy of an OSDU kind changed where the canonical schema falls short, names the canonical
/// kind its records stand beside. A record of the custom template has a parent record: the record of the parent kind whose
/// OSDU id is the record's own with the parent's entity type, the same minted value in another namespace. A mapping reads
/// sections of that record with <c>$parent</c> (osdu/docs/reference/flow/mapping-parent.md).
/// </summary>
/// <remarks>
/// The link is the key the OSDU data definitions use for it, <c>x-osdu-inheriting-from-kind</c>: a list of
/// <c>{ "name", "kind" }</c>. The data definitions list the abstract schemas a kind is built from there
/// (<c>osdu:wks:AbstractDataset:1.0.0</c>), which name no record and are left alone; the one entry naming a record kind,
/// one whose entity type has a group (<c>work-product-component--WellLog</c>), is the parent. A template without the key,
/// or listing abstract schemas only, has no parent, and is an ordinary template.
/// </remarks>
/// <param name="Name">The name the entry gives the parent, as written.</param>
/// <param name="Kind">The parent's kind, <c>authority:source:group--Entity:major.minor.patch</c>.</param>
public sealed record TemplateParent(string Name, string Kind)
{
    /// <summary>The key a schema names its parents under, as the OSDU data definitions write it.</summary>
    public const string Key = "x-osdu-inheriting-from-kind";

    /// <summary>The parent's entity type (<c>work-product-component--WellLog</c>): the namespace of its records' ids.</summary>
    public string EntityType => TargetId.EntityTypeFromKind(Kind);

    /// <summary>
    /// The parent <paramref name="schema"/> names, or null when it names none; <paramref name="problem"/> says why a link it
    /// writes cannot be read (an entry that is not a <c>{ name, kind }</c> object, a kind that is not a record kind, two record
    /// kinds), and is null when the schema names one parent or none.
    /// </summary>
    public static TemplateParent? Read(JsonObject schema, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(schema);
        problem = null;
        if (!schema.TryGetPropertyValue(Key, out var listed) || listed is null)
        {
            return null;
        }

        if (listed is not JsonArray entries)
        {
            problem = $"{Key} is {Describe(listed)}, and it lists the kinds a schema is linked to, as [{{ \"name\": \"WellLog\", \"kind\": \"osdu:wks:work-product-component--WellLog:1.4.0\" }}]";
            return null;
        }

        var parents = new List<TemplateParent>();
        foreach (var entry in entries)
        {
            if (entry is not JsonObject item
                || item["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) || string.IsNullOrWhiteSpace(kind))
            {
                problem = $"{Key} lists {Describe(entry)}, and each entry is an object naming a kind, as {{ \"name\": \"WellLog\", \"kind\": \"osdu:wks:work-product-component--WellLog:1.4.0\" }}";
                return null;
            }

            kind = kind.Trim();
            if (!FlowMapper.IsRecordKind(kind))
            {
                problem = $"{Key} lists '{kind}', which is not a kind: 'authority:source:entityType:major.minor.patch'";
                return null;
            }

            // An abstract schema the data definitions build a kind from names no record, so it is no parent.
            if (!TargetId.EntityTypeFromKind(kind).Contains("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = item["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var written) && !string.IsNullOrWhiteSpace(written)
                ? written.Trim()
                : TargetId.EntityTypeFromKind(kind);
            parents.Add(new TemplateParent(name, kind));
        }

        if (parents.Count > 1)
        {
            problem = $"{Key} names {parents.Count} record kinds ({string.Join(", ", parents.Select(p => p.Kind))}), and a template is linked to one parent";
            return null;
        }

        return parents.Count == 1 ? parents[0] : null;
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "a list",
        JsonObject => "an object without a kind",
        _ => $"the value {node.ToJsonString()}",
    };
}
