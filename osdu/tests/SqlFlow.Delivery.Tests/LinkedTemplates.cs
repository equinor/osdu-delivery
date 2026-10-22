using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A custom template linked to the canonical kind it stands beside (osdu/docs/reference/concepts/templates.md, Linked
/// templates): the canonical test template is the parent, and the custom template is a full copy of it under an entity
/// type of its own, with what the canonical one cannot carry added, naming the parent in <c>x-osdu-inheriting-from-kind</c>
/// as the OSDU data definitions name a kind's parents.
/// </summary>
internal static class LinkedTemplates
{
    /// <summary>The canonical kind: the test template.</summary>
    public const string ParentKind = TestSchema.Kind;

    /// <summary>The custom kind: an entity type of its own, so its records' ids are minted in a namespace of their own.</summary>
    public const string CustomKind = "test:wks:work-product-component--CustomThing:1.0.0";

    /// <summary>The partition the test renders mint ids in.</summary>
    public const string Partition = "dev";

    /// <summary>The canonical template, as a template folder keeps it: naming its own kind.</summary>
    public static SchemaSnapshot Parent()
    {
        var root = (JsonObject)TestSchema.Build().Root.DeepClone();
        root["x-osdu-schema-source"] = ParentKind;
        return new SchemaSnapshot(ParentKind, root, DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// The custom template: the canonical one copied, its kind its own, linked to <paramref name="links"/> (the canonical kind
    /// when none is given), with what the canonical one cannot carry added to its data: the unit each curve came with.
    /// </summary>
    public static SchemaSnapshot Custom(params JsonNode?[] links) => Custom(CustomKind, links);

    /// <summary>The custom template under <paramref name="kind"/>, linked to <paramref name="links"/> (the canonical kind when none is given).</summary>
    public static SchemaSnapshot Custom(string kind, params JsonNode?[] links)
    {
        var root = (JsonObject)TestSchema.Build().Root.DeepClone();
        root["$id"] = "https://example.org/CustomThing.1.0.0.json";
        root["x-osdu-schema-source"] = kind;
        root[TemplateParent.Key] = new JsonArray(links.Length == 0 ? [Link("Thing", ParentKind)] : links);
        var data = (JsonObject)root["properties"]!["data"]!["allOf"]![1]!["properties"]!;
        data["OriginalUnits"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        return new SchemaSnapshot(kind, root, DateTimeOffset.UnixEpoch);
    }

    /// <summary>One entry of <c>x-osdu-inheriting-from-kind</c>.</summary>
    public static JsonObject Link(string? name, string kind)
    {
        var link = new JsonObject { ["kind"] = kind };
        if (name is not null)
        {
            link["name"] = name;
        }

        return link;
    }

    /// <summary>A mapping document over <paramref name="schema"/> (the custom template unless given), its data properties <paramref name="data"/> after the base ones.</summary>
    public static string MappingDocument(string data, SchemaSnapshot? schema = null, string baseData = TestSchema.BaseData, string idFrom = "")
    {
        var template = schema ?? Custom();
        var document = TestSchema.MappingDocument(data, baseData)
            .Replace($"kind: {TestSchema.Kind}", $"kind: {template.Kind}", StringComparison.Ordinal)
            .Replace($"version: {TestSchema.Build().Version}", $"version: {template.Version}", StringComparison.Ordinal);
        return idFrom.Length == 0 ? document : document.Replace("  key: [name]\n", $"  key: [name]\n  idFrom: {idFrom}\n", StringComparison.Ordinal);
    }

    /// <summary>The mapping <see cref="MappingDocument"/> writes.</summary>
    public static MappingDefinition Mapping(string data, SchemaSnapshot? schema = null, string baseData = TestSchema.BaseData, string idFrom = "")
        => new DeliveryDocumentLoader().ParseMapping(MappingDocument(data, schema, baseData, idFrom), "customthing.yaml");

    /// <summary>The render context of a record of <paramref name="schema"/> (the custom template unless given).</summary>
    public static RenderContext Context(SchemaSnapshot? schema = null)
        => TestSchema.Context("CustomThing@1.0.0") with { SchemaSnapshotVersion = (schema ?? Custom()).Version };

    /// <summary>The OSDU id of the parent of the record of key <paramref name="name"/>: the canonical kind's namespace, the same minted value.</summary>
    public static string ParentId(string name)
        => TargetId.Compose(Partition, TargetId.EntityTypeFromKind(ParentKind), DeliveryKey.Derive("test", [name]));

    /// <summary>The OSDU id of the custom record of key <paramref name="name"/>.</summary>
    public static string RecordId(string name)
        => TargetId.Compose(Partition, TargetId.EntityTypeFromKind(CustomKind), DeliveryKey.Derive("test", [name]));

    /// <summary>A parent record as storage holds it, at <paramref name="version"/>, with <paramref name="data"/> as its data.</summary>
    public static JsonObject ParentRecord(string name, long version, JsonObject data) => new()
    {
        ["id"] = ParentId(name),
        ["kind"] = ParentKind,
        ["version"] = version,
        ["acl"] = new JsonObject { ["owners"] = new JsonArray("owners@x"), ["viewers"] = new JsonArray("viewers@x") },
        ["legal"] = new JsonObject { ["legaltags"] = new JsonArray("tag"), ["otherRelevantDataCountries"] = new JsonArray("US") },
        ["data"] = data,
    };
}
