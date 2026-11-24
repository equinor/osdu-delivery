using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.Templates;

// A fixed value as the mapping writes it: a literal at its property, a fixed item of a list, and an object or a
// list of them, each value plain where YAML reads it back as the same value.
public static partial class MappingBuilder
{
    /// <summary>
    /// A fixed value as an item of a list: the bare value where it can be, and otherwise <c>- $value: ...</c>, an object or a
    /// list of them under it, followed by the item's condition and description.
    /// </summary>
    private static void WriteLiteralItem(MappingDraftEntry item, int indent, bool valueNode, Action<string> line)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(item.Static) ? null : JsonNode.Parse(item.Static);
        }
        catch (JsonException)
        {
            node = JsonValue.Create(item.Static ?? string.Empty);
        }

        var settled = string.IsNullOrWhiteSpace(item.When) && string.IsNullOrWhiteSpace(item.Description) && item.Assertions.Count == 0;
        if (!valueNode && settled && node is JsonValue bareValue)
        {
            line(new string(' ', indent) + "- " + ValueText(bareValue, flow: false));
            return;
        }

        if (!valueNode && settled && node is JsonObject { Count: > 0 } bareObject)
        {
            // A fixed item of a list of objects is the object it is, laid out as the tree lays out a literal.
            var written = new List<string>();
            WriteObject(bareObject, indent + 2, escape: true, written.Add);
            WriteDashed(written, indent, line);
            return;
        }

        WriteLiteralNode(node, indent, line);
        if (!settled)
        {
            WriteSettings(item, new string(' ', indent + 2), line);
        }
    }

    /// <summary>A fixed value as a list item under <c>$value</c>: <c>- $value: ...</c>, an object or a list of them under it.</summary>
    private static void WriteLiteralNode(JsonNode? node, int indent, Action<string> line)
    {
        var dash = new string(' ', indent) + "- " + MappingMapper.ValueKey + ":";
        switch (node)
        {
            case null:
                line(dash + " \"\"");
                break;
            case JsonObject { Count: 0 }:
                line(dash + " {}");
                break;
            case JsonObject obj:
                line(dash);
                WriteObject(obj, indent + 4, escape: false, line);
                break;
            case JsonArray array when array.All(item => item is JsonValue):
                line(dash + " [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                break;
            case JsonArray array:
                line(dash);
                WriteArray(array, indent + 4, escape: false, line);
                break;
            case JsonValue value:
                line(dash + " " + ValueText(value, flow: false));
                break;
        }
    }

    /// <summary>
    /// A literal: a text, number, boolean or list as the value it is, and an object, or a literal with a condition or a
    /// description, under <c>$value</c>, which holds it verbatim and keeps it one entry.
    /// </summary>
    private static void WriteLiteral(string key, MappingDraftEntry entry, int indent, string? scope, Action<string> line)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(entry.Static) ? null : JsonNode.Parse(entry.Static);
        }
        catch (JsonException)
        {
            // A value that is not JSON is written as the text it is, so the page shows what was typed and the loader reports it.
            node = JsonValue.Create(entry.Static ?? string.Empty);
        }

        var inner = new string(' ', indent + 2);
        var settled = string.IsNullOrWhiteSpace(entry.When) && string.IsNullOrWhiteSpace(entry.Description) && entry.Assertions.Count == 0;
        switch (node)
        {
            case null:
                line(key + ": \"\"");
                return;
            case JsonValue value when settled:
                line(key + ": " + ValueText(value, flow: false));
                return;
            case JsonArray array when settled && array.All(item => item is JsonValue):
                line(key + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                return;
            case JsonArray array when settled:
                line(key + ":");
                WriteArray(array, indent + 2, escape: true, line);
                return;
        }

        line(key + ":");
        switch (node)
        {
            case JsonObject { Count: 0 }:
                line(inner + MappingMapper.ValueKey + ": {}");
                break;
            case JsonObject obj:
                line(inner + MappingMapper.ValueKey + ":");
                WriteObject(obj, indent + 4, escape: false, line);
                break;
            case JsonArray array when array.All(item => item is JsonValue):
                line(inner + MappingMapper.ValueKey + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                break;
            case JsonArray array:
                line(inner + MappingMapper.ValueKey + ":");
                WriteArray(array, indent + 4, escape: false, line);
                break;
            case JsonValue value:
                line(inner + MappingMapper.ValueKey + ": " + ValueText(value, flow: false));
                break;
        }

        WriteSettings(entry, inner, line, scope);
    }

    /// <summary>
    /// An object's properties, one per line. A literal in the tree names a property that starts with '$' with one more
    /// (<paramref name="escape"/>); what <c>$value</c> holds is written verbatim.
    /// </summary>
    private static void WriteObject(JsonObject obj, int indent, bool escape, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (name, child) in obj)
        {
            var key = pad + Scalar(escape ? MappingMapper.KeyOfProperty(name) : name);
            switch (child)
            {
                case JsonObject { Count: 0 }:
                    line(key + ": {}");
                    break;
                case JsonObject nested:
                    line(key + ":");
                    WriteObject(nested, indent + 2, escape, line);
                    break;
                case JsonArray array when array.All(item => item is JsonValue):
                    line(key + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                    break;
                case JsonArray array:
                    line(key + ":");
                    WriteArray(array, indent + 2, escape, line);
                    break;
                case JsonValue value:
                    line(key + ": " + ValueText(value, flow: false));
                    break;
                default:
                    line(key + ": null");
                    break;
            }
        }
    }

    private static void WriteArray(JsonArray array, int indent, bool escape, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var item in array)
        {
            switch (item)
            {
                case JsonObject obj:
                    var inner = new List<string>();
                    WriteObject(obj, indent + 2, escape, inner.Add);
                    if (inner.Count == 0)
                    {
                        line(pad + "- {}");
                    }
                    else
                    {
                        WriteDashed(inner, indent, line);
                    }

                    break;
                case JsonArray nested when nested.All(value => value is JsonValue):
                    line(pad + "- [" + string.Join(", ", nested.Select(value => ValueText((JsonValue)value!, flow: true))) + "]");
                    break;
                case JsonArray nested:
                    line(pad + "-");
                    WriteArray(nested, indent + 2, escape, line);
                    break;
                case JsonValue value:
                    line(pad + "- " + ValueText(value, flow: false));
                    break;
                default:
                    line(pad + "- null");
                    break;
            }
        }
    }

    private static string ValueText(JsonValue value, bool flow) => value.GetValueKind() switch
    {
        JsonValueKind.String => flow ? FlowScalar(value.GetValue<string>()) : Scalar(value.GetValue<string>()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.ToJsonString(),
        _ => "null",
    };
}
