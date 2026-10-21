using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The schema files and mapping documents the template folder suites write into a repository's template folders and
/// mappings (osdu/docs/reference/concepts/templates.md, Template folders), shared by the delivery and control-plane suites.
/// </summary>
public static class TemplateFolderFixtures
{
    /// <summary>A bundled record schema of <paramref name="kind"/>, naming its kind; <paramref name="extra"/> names another property, so the version differs.</summary>
    public static string Schema(string kind, string? extra = null, bool declareKind = true)
    {
        var properties = new JsonObject { ["Name"] = new JsonObject { ["type"] = "string" } };
        if (extra is not null)
        {
            properties[extra] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject { ["type"] = "object", ["properties"] = properties },
            },
        };
        if (declareKind)
        {
            schema["x-osdu-schema-source"] = kind;
        }

        return schema.ToJsonString();
    }

    /// <summary>The version the schema text holds, as a template is saved from it.</summary>
    public static string VersionOf(string kind, string json) => SchemaSnapshot.Parse(kind, json, DateTimeOffset.UnixEpoch).Version;

    /// <summary>The test template as a template folder keeps it, naming its kind; <paramref name="comment"/> makes another version of it.</summary>
    public static string ThingFile(string? comment = null)
    {
        var root = TestSchema.Build().Root.DeepClone().AsObject();
        root["x-osdu-schema-source"] = TestSchema.Kind;
        if (comment is not null)
        {
            root["$comment"] = comment;
        }

        return root.ToJsonString();
    }

    /// <summary>The test mapping document with its template named by kind alone, or by <paramref name="version"/>.</summary>
    public static string KindAloneMapping(string? version = null)
        => TestSchema.MappingDocument().ReplaceLineEndings("\n").Replace(
            $"  version: {TestSchema.Build().Version}\n",
            version is null ? string.Empty : $"  version: {version}\n",
            StringComparison.Ordinal);
}
