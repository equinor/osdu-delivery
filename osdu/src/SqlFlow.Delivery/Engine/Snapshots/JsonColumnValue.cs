using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// The value of a table type's column read with <c>decode: json</c> (osdu/docs/reference/flow/cache-origins.md, A column
/// holding a JSON object): the text of one JSON object, kept as that object in the form the cache keeps every object
/// (<see cref="CanonicalJson.Normalize"/>): its properties in ordinal order of their names, a property holding null left out,
/// each number in its shortest form. Every property name is kept exactly as the text spells it, a name holding a dot
/// (<c>lra.dataID</c>) included, and every string, number, boolean, array and object keeps its JSON type, so a mapping that
/// writes the object whole writes what the column holds. Text that is not one JSON object is refused with the reason and
/// where in the text it lies, as the JSON reader reports them, rather than with the text.
/// </summary>
internal static class JsonColumnValue
{
    /// <summary>How deeply the object may nest: deeper than any OSDU record does.</summary>
    public const int MaxDepth = 64;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxDepth,
    };

    /// <summary>
    /// The object <paramref name="text"/> holds, or null with <paramref name="problem"/> saying why it holds none, written to
    /// follow "the value" (<c>is a JSON array, not an object</c>).
    /// </summary>
    public static JsonObject? Decode(string text, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "is empty text, which is no JSON object; a row that has no object holds NULL in the column";
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            problem = $"is not valid JSON ({ex.Message})";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = $"is a JSON {KindName(root.ValueKind)}, not an object";
                return null;
            }

            var node = Read(root, "$", out problem);
            return problem is null ? (JsonObject)CanonicalJson.Normalize(node)! : null;
        }
    }

    /// <summary>
    /// The node an element holds, read element by element so a name an object holds twice is refused (the JSON a parser keeps
    /// would hold only one of its values) and a number the cache could not write is named where it is.
    /// </summary>
    private static JsonNode? Read(JsonElement element, string at, out string? problem)
    {
        problem = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var obj = new JsonObject();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        var path = Member(at, property.Name);
                        if (!names.Add(property.Name))
                        {
                            problem = $"holds the property {path} twice";
                            return null;
                        }

                        var child = Read(property.Value, path, out problem);
                        if (problem is not null)
                        {
                            return null;
                        }

                        if (child is not null)
                        {
                            obj[property.Name] = child;
                        }
                    }

                    return obj;
                }

            case JsonValueKind.Array:
                {
                    var array = new JsonArray();
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        // A null element is kept, since removing it would move the elements after it.
                        var child = Read(item, $"{at}[{index++}]", out problem);
                        if (problem is not null)
                        {
                            return null;
                        }

                        array.Add(child);
                    }

                    return array;
                }

            case JsonValueKind.String:
                return JsonValue.Create(element.GetString()!);
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var whole))
                {
                    return JsonValue.Create(whole);
                }

                if (element.TryGetDouble(out var real) && double.IsFinite(real))
                {
                    return JsonValue.Create(real);
                }

                problem = $"holds a number at {at} beyond the range of a JSON number the cache keeps";
                return null;
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    /// <summary>The path of a property in a message: <c>$.a.b</c>, or <c>$['lra.dataID']</c> for a name that is not a plain word.</summary>
    private static string Member(string at, string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? $"{at}.{name}"
            : $"{at}['{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };
}
