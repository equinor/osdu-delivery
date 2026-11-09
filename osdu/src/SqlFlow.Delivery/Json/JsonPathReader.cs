// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/JsonPathReader.cs. Namespace and exception type changed; the JsonElement behaviour is
// unchanged. SelectNodes was added here for the reference cache, which walks JsonNode trees and needs a property
// step to reach through an array of objects. A quoted name in brackets is read to its closing quote, escapes undone
// (RecordPathNotation), where SQLFlow's copy ends it at the first ']' and keeps a backslash escape as written.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Json;

/// <summary>
/// A focused JSONPath reader: enough to read a token out of an auth response, an id:version out of a write
/// response, or a version out of a record. Supports $ root, dotted properties ($.data.token), array indices
/// ($.items[0]) and the wildcard ($.data[*].id). A leading $ is optional.
/// </summary>
public static class JsonPathReader
{
    private static readonly string[] CommonRecordKeys = ["data", "items", "results", "records", "value"];

    /// <summary>All string-projected values a (possibly wildcard) path selects, skipping nulls/absent nodes.</summary>
    public static IReadOnlyList<string> SelectValues(JsonElement root, string path)
    {
        var values = new List<string>();
        foreach (var element in Evaluate(root, Parse(path)))
        {
            if (Stringify(element) is { } s)
            {
                values.Add(s);
            }
        }

        return values;
    }

    /// <summary>The first string-projected value a path selects, or null when the path matches nothing.</summary>
    public static string? SelectValue(JsonElement root, string path)
    {
        foreach (var element in Evaluate(root, Parse(path)))
        {
            if (Stringify(element) is { } s)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Every element a (possibly wildcard) path selects, in document order.</summary>
    public static IReadOnlyList<JsonElement> SelectElements(JsonElement root, string path)
        => Evaluate(root, Parse(path)).ToList();

    /// <summary>The first element a path selects, or null.</summary>
    public static JsonElement? SelectElement(JsonElement root, string path)
    {
        foreach (var element in Evaluate(root, Parse(path)))
        {
            return element;
        }

        return null;
    }

    /// <summary>
    /// Counts the records in a page for empty-page detection. When <paramref name="recordsPath"/> is set it is the
    /// array; otherwise the array is the whole body (if it is an array) or the first common wrapper key.
    /// </summary>
    public static int CountRecords(JsonElement root, string? recordsPath)
    {
        var array = LocateRecordArray(root, recordsPath);
        if (array is not { } element)
        {
            return root.ValueKind == JsonValueKind.Object ? 1 : 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Null or JsonValueKind.Undefined => 0,
            _ => 1,
        };
    }

    /// <summary>The record array for a page, honoring an explicit path or auto-locating the common wrapper keys.</summary>
    public static JsonElement? LocateRecordArray(JsonElement root, string? recordsPath)
    {
        if (!string.IsNullOrWhiteSpace(recordsPath))
        {
            return SelectElement(root, recordsPath);
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in CommonRecordKeys)
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Evaluate(JsonElement root, IReadOnlyList<PathSegment> segments)
    {
        IEnumerable<JsonElement> current = [root];
        foreach (var segment in segments)
        {
            current = Step(current, segment).ToList();
        }

        return current;
    }

    private static IEnumerable<JsonElement> Step(IEnumerable<JsonElement> elements, PathSegment segment)
    {
        foreach (var element in elements)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Property when element.ValueKind == JsonValueKind.Object
                                               && element.TryGetProperty(segment.Name!, out var property):
                    yield return property;
                    break;
                case SegmentKind.Index when element.ValueKind == JsonValueKind.Array
                                            && segment.Index < element.GetArrayLength():
                    yield return element[segment.Index];
                    break;
                case SegmentKind.Wildcard when element.ValueKind == JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        yield return item;
                    }

                    break;
                case SegmentKind.Wildcard when element.ValueKind == JsonValueKind.Object:
                    foreach (var item in element.EnumerateObject())
                    {
                        yield return item.Value;
                    }

                    break;
            }
        }
    }

    private static string? Stringify(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };

    /// <summary>
    /// Every node a path selects from a <see cref="JsonNode"/> tree, in document order. A property step applied to
    /// an array steps into each element, so <c>data.NameAlias.AliasName</c> reaches through an array of objects
    /// without an explicit wildcard; <c>[*]</c> and <c>[n]</c> stay available where a path must be explicit. Null
    /// elements are skipped, so the result holds only nodes that exist.
    /// </summary>
    public static IReadOnlyList<JsonNode> SelectNodes(JsonNode? root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (root is null)
        {
            return [];
        }

        IReadOnlyList<JsonNode> current = [root];
        foreach (var segment in Parse(path))
        {
            current = StepNodes(current, segment);
            if (current.Count == 0)
            {
                return [];
            }
        }

        return current;
    }

    /// <summary>True when the path names a single property with no traversal, which needs no evaluation to resolve.</summary>
    public static bool IsSingleProperty(string path)
    {
        var segments = Parse(path);
        return segments.Count == 1 && segments[0].Kind == SegmentKind.Property;
    }

    private static IReadOnlyList<JsonNode> StepNodes(IReadOnlyList<JsonNode> nodes, PathSegment segment)
    {
        var next = new List<JsonNode>();
        foreach (var node in nodes)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Property:
                    StepProperty(node, segment.Name!, next);
                    break;
                case SegmentKind.Index when node is JsonArray array && segment.Index >= 0 && segment.Index < array.Count:
                    if (array[segment.Index] is { } indexed)
                    {
                        next.Add(indexed);
                    }

                    break;
                case SegmentKind.Wildcard when node is JsonArray array:
                    next.AddRange(array.Where(item => item is not null)!);
                    break;
                case SegmentKind.Wildcard when node is JsonObject obj:
                    next.AddRange(obj.Select(kv => kv.Value).Where(value => value is not null)!);
                    break;
            }
        }

        return next;
    }

    /// <summary>Reads a property, stepping into arrays so a path crosses an array of objects implicitly.</summary>
    private static void StepProperty(JsonNode node, string name, List<JsonNode> into)
    {
        switch (node)
        {
            case JsonObject obj when obj[name] is { } child:
                into.Add(child);
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        StepProperty(item, name, into);
                    }
                }

                break;
        }
    }

    private static IReadOnlyList<PathSegment> Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = new List<PathSegment>();
        var i = 0;
        if (path[0] == '$')
        {
            i = 1;
        }

        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                // A quoted name is read to its closing quote, so a ']' or a quote it holds is part of it, and its escapes are
                // undone: tags['lra.dataID'], data['a]b'], data['it\'s'].
                var quote = i + 1;
                while (quote < path.Length && path[quote] is ' ' or '\t')
                {
                    quote++;
                }

                if (quote < path.Length && path[quote] is '\'' or '"')
                {
                    if (!RecordPathNotation.TryReadQuoted(path, quote, out var quoted, out var after, out var why))
                    {
                        throw new DeliveryException($"JSON path '{path}': {why}.");
                    }

                    while (after < path.Length && path[after] is ' ' or '\t')
                    {
                        after++;
                    }

                    if (after >= path.Length || path[after] != ']')
                    {
                        throw new DeliveryException($"Unterminated '[' in JSON path '{path}'.");
                    }

                    segments.Add(PathSegment.OfProperty(quoted));
                    i = after + 1;
                    continue;
                }

                var close = path.IndexOf(']', i + 1);
                if (close < 0)
                {
                    throw new DeliveryException($"Unterminated '[' in JSON path '{path}'.");
                }

                var inner = path[(i + 1)..close].Trim().Trim('\'', '"');
                segments.Add(inner == "*"
                    ? PathSegment.OfWildcard()
                    : int.TryParse(inner, out var index)
                        ? PathSegment.OfIndex(index)
                        : PathSegment.OfProperty(inner));
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[')
            {
                i++;
            }

            var name = path[start..i];
            segments.Add(name == "*" ? PathSegment.OfWildcard() : PathSegment.OfProperty(name));
        }

        return segments;
    }

    private enum SegmentKind
    {
        Property,
        Index,
        Wildcard,
    }

    private readonly record struct PathSegment(SegmentKind Kind, string? Name, int Index)
    {
        public static PathSegment OfProperty(string name) => new(SegmentKind.Property, name, 0);

        public static PathSegment OfIndex(int index) => new(SegmentKind.Index, null, index);

        public static PathSegment OfWildcard() => new(SegmentKind.Wildcard, null, 0);
    }
}
