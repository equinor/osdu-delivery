using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Checks a JSON value against a schema of an OSDU contract: the JSON Schema keywords the pinned OpenAPI 3.0, 3.1 and
/// Swagger 2.0 documents use (<c>$ref</c>, <c>type</c>, <c>nullable</c>, <c>enum</c>, <c>const</c>, the string, number,
/// object and array bounds, <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>not</c>). It checks what a client sends, so a
/// <c>readOnly</c> property is never required. <c>oneOf</c> passes when any alternative does: OSDU contracts list
/// alternatives that overlap (a string or a pattern-restricted string), and a request that matches two of them is
/// still one the service accepts. A contract defect the check cannot evaluate (a reference to nothing, a pattern .NET
/// cannot compile) is noted, not reported as the request's fault. A contract is loaded once for the test process, so its
/// check serves every test class running at once: what it keeps between checks (the patterns it compiled) is safe to share.
/// </summary>
internal sealed class SchemaCheck
{
    private const int MaxDepth = 64;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    private readonly JsonObject _document;
    private readonly bool _nullableKeyword;
    private readonly Action<string> _note;

    // Each pattern is compiled once, however many checks ask for it at once, so one that does not compile is noted once.
    private readonly ConcurrentDictionary<string, Lazy<Regex?>> _patterns = new(StringComparer.Ordinal);

    /// <param name="document">The whole contract, which <c>$ref</c> pointers resolve against.</param>
    /// <param name="nullableKeyword">True for OpenAPI 3.0 and Swagger 2.0 documents, where <c>nullable</c> admits null.</param>
    /// <param name="note">Notes a contract defect; called by every check running at once, so it keeps its notes safely.</param>
    public SchemaCheck(JsonObject document, bool nullableKeyword, Action<string> note)
    {
        _document = document;
        _nullableKeyword = nullableKeyword;
        _note = note;
    }

    /// <summary>Resolves a local JSON pointer (<c>#/components/schemas/X</c>) in the contract; null when it names nothing.</summary>
    public JsonNode? Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            _note($"the reference '{reference}' is not local to the contract and is not checked");
            return null;
        }

        JsonNode? current = _document;
        foreach (var raw in reference[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (current is null)
            {
                _note($"the reference '{reference}' names nothing in the contract");
                return null;
            }
        }

        return current;
    }

    /// <summary>Follows <c>$ref</c> until a schema object without one is reached.</summary>
    public JsonObject? Dereference(JsonNode? node)
    {
        for (var hops = 0; hops < MaxDepth && node is JsonObject o; hops++)
        {
            if (o["$ref"] is not JsonValue reference || !reference.TryGetValue(out string? target))
            {
                return o;
            }

            node = Resolve(target);
        }

        return null;
    }

    public void Validate(JsonNode? instance, JsonNode? schema, string path, ICollection<string> violations)
        => Check(instance, schema, path, violations, 0);

    private void Check(JsonNode? instance, JsonNode? schema, string path, ICollection<string> violations, int depth)
    {
        if (depth > MaxDepth)
        {
            _note($"the schema at {path} nests deeper than {MaxDepth} levels and is not checked further");
            return;
        }

        switch (schema)
        {
            case null:
                return;
            case JsonValue flag when flag.TryGetValue(out bool allowed):
                if (!allowed)
                {
                    violations.Add($"{path}: the contract allows no value here");
                }

                return;
            case not JsonObject:
                _note($"the schema at {path} is not an object");
                return;
        }

        var s = (JsonObject)schema;
        if (s["$ref"] is JsonValue reference && reference.TryGetValue(out string? target))
        {
            var resolved = Resolve(target);
            if (resolved is not null)
            {
                Check(instance, resolved, path, violations, depth + 1);
            }

            // OpenAPI 3.0 and Swagger 2.0 ignore a reference's siblings; OpenAPI 3.1 applies them.
            if (_nullableKeyword)
            {
                return;
            }
        }

        if (instance is null && _nullableKeyword && Bool(s, "nullable"))
        {
            return;
        }

        foreach (var part in Array(s, "allOf"))
        {
            Check(instance, part, path, violations, depth + 1);
        }

        CheckAlternatives(instance, s, "anyOf", path, violations, depth);
        CheckAlternatives(instance, s, "oneOf", path, violations, depth);
        if (s["not"] is { } not)
        {
            var inner = new List<string>();
            Check(instance, not, path, inner, depth + 1);
            if (inner.Count == 0)
            {
                violations.Add($"{path}: the value matches a schema the contract excludes");
            }
        }

        if (s["enum"] is JsonArray allowedValues && !allowedValues.Any(v => JsonNode.DeepEquals(v, instance)))
        {
            violations.Add($"{path}: {Show(instance)} is not one of {allowedValues.ToJsonString()}");
        }

        if (s.ContainsKey("const") && !JsonNode.DeepEquals(s["const"], instance))
        {
            violations.Add($"{path}: {Show(instance)} is not {s["const"]?.ToJsonString() ?? "null"}");
        }

        if (!CheckType(instance, s, path, violations))
        {
            return;
        }

        switch (instance)
        {
            case JsonObject o:
                CheckObject(o, s, path, violations, depth);
                break;
            case JsonArray a:
                CheckArray(a, s, path, violations, depth);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                CheckString(v.GetValue<string>(), s, path, violations);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                CheckNumber(AsDouble(v), s, path, violations);
                break;
        }
    }

    private void CheckAlternatives(JsonNode? instance, JsonObject s, string keyword, string path, ICollection<string> violations, int depth)
    {
        var alternatives = Array(s, keyword).ToList();
        if (alternatives.Count == 0)
        {
            return;
        }

        var reasons = new List<string>();
        foreach (var alternative in alternatives)
        {
            var inner = new List<string>();
            Check(instance, alternative, path, inner, depth + 1);
            if (inner.Count == 0)
            {
                return;
            }

            reasons.AddRange(inner);
        }

        violations.Add($"{path}: the value matches none of the {keyword} alternatives ({string.Join("; ", reasons.Distinct().Take(5))})");
    }

    /// <summary>Checks <c>type</c>; false when the value has another type, so the type's own bounds are not checked.</summary>
    private static bool CheckType(JsonNode? instance, JsonObject s, string path, ICollection<string> violations)
    {
        var types = s["type"] switch
        {
            JsonValue single when single.TryGetValue(out string? name) => [name],
            JsonArray many => many.Select(t => t?.GetValue<string>() ?? string.Empty).ToList(),
            _ => new List<string>(),
        };
        if (types.Count == 0)
        {
            return true;
        }

        if (types.Any(t => Matches(instance, t)))
        {
            return true;
        }

        violations.Add($"{path}: {Show(instance)} is not of type {string.Join(" or ", types)}");
        return false;
    }

    private static bool Matches(JsonNode? instance, string type) => type switch
    {
        "null" => instance is null,
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => instance is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => instance is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Math.Abs(AsDouble(v) % 1) < double.Epsilon,
        // Swagger 2.0 declares uploads as type file; the upload itself is checked by its content type.
        "file" => true,
        _ => false,
    };

    private void CheckObject(JsonObject o, JsonObject s, string path, ICollection<string> violations, int depth)
    {
        var properties = s["properties"] as JsonObject;
        foreach (var required in Array(s, "required"))
        {
            var name = required?.GetValue<string>();
            if (name is null || o.ContainsKey(name))
            {
                continue;
            }

            // A property the service fills in itself is required in what it returns, never in what a client sends.
            if (properties?[name] is { } declared && Bool(Dereference(declared), "readOnly"))
            {
                continue;
            }

            violations.Add($"{path}: the required property '{name}' is missing");
        }

        foreach (var (name, value) in o)
        {
            var at = $"{path}.{name}";
            if (properties?[name] is { } propertySchema)
            {
                Check(value, propertySchema, at, violations, depth + 1);
                continue;
            }

            if (MatchesPatternProperty(s, name, value, at, violations, depth))
            {
                continue;
            }

            switch (s["additionalProperties"])
            {
                case JsonValue allowed when allowed.TryGetValue(out bool yes) && !yes:
                    violations.Add($"{at}: the contract allows no property '{name}' here");
                    break;
                case JsonObject extra:
                    Check(value, extra, at, violations, depth + 1);
                    break;
            }
        }

        CheckCount(o.Count, s, "minProperties", "maxProperties", "properties", path, violations);
    }

    private bool MatchesPatternProperty(JsonObject s, string name, JsonNode? value, string at, ICollection<string> violations, int depth)
    {
        if (s["patternProperties"] is not JsonObject patterns)
        {
            return false;
        }

        var matched = false;
        foreach (var (pattern, schema) in patterns)
        {
            if (Pattern(pattern) is { } regex && regex.IsMatch(name))
            {
                matched = true;
                Check(value, schema, at, violations, depth + 1);
            }
        }

        return matched;
    }

    private void CheckArray(JsonArray a, JsonObject s, string path, ICollection<string> violations, int depth)
    {
        if (s["items"] is { } items)
        {
            for (var i = 0; i < a.Count; i++)
            {
                Check(a[i], items, $"{path}[{i}]", violations, depth + 1);
            }
        }

        CheckCount(a.Count, s, "minItems", "maxItems", "items", path, violations);
        if (Bool(s, "uniqueItems"))
        {
            for (var i = 0; i < a.Count; i++)
            {
                for (var j = i + 1; j < a.Count; j++)
                {
                    if (JsonNode.DeepEquals(a[i], a[j]))
                    {
                        violations.Add($"{path}: items {i} and {j} are equal, and the contract wants them unique");
                    }
                }
            }
        }
    }

    private void CheckString(string value, JsonObject s, string path, ICollection<string> violations)
    {
        CheckCount(value.Length, s, "minLength", "maxLength", "characters", path, violations);
        if (s["pattern"] is JsonValue p && p.TryGetValue(out string? pattern) && Pattern(pattern) is { } regex)
        {
            try
            {
                if (!regex.IsMatch(value))
                {
                    violations.Add($"{path}: '{value}' does not match the pattern {pattern}");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                _note($"the pattern {pattern} at {path} took too long to evaluate and is not checked");
            }
        }
    }

    private static void CheckNumber(double value, JsonObject s, string path, ICollection<string> violations)
    {
        var minimum = Number(s, "minimum");
        var maximum = Number(s, "maximum");
        if (minimum is { } min && (value < min || (value <= min && Bool(s, "exclusiveMinimum"))))
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {value} is below the minimum {min}"));
        }

        if (maximum is { } max && (value > max || (value >= max && Bool(s, "exclusiveMaximum"))))
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {value} is above the maximum {max}"));
        }

        // OpenAPI 3.1 writes the exclusive bounds as numbers of their own.
        if (Number(s, "exclusiveMinimum") is { } exclusiveMin && value <= exclusiveMin)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {value} is not above {exclusiveMin}"));
        }

        if (Number(s, "exclusiveMaximum") is { } exclusiveMax && value >= exclusiveMax)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {value} is not below {exclusiveMax}"));
        }
    }

    private static void CheckCount(int count, JsonObject s, string minKeyword, string maxKeyword, string what, string path, ICollection<string> violations)
    {
        if (Number(s, minKeyword) is { } min && count < min)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {count} {what}, fewer than the {min} the contract wants"));
        }

        if (Number(s, maxKeyword) is { } max && count > max)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {count} {what}, more than the {max} the contract allows"));
        }
    }

    private Regex? Pattern(string pattern)
        => _patterns.GetOrAdd(pattern, p => new Lazy<Regex?>(() => Compile(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private Regex? Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException ex)
        {
            _note($"the pattern {pattern} does not compile ({ex.Message}) and is not checked");
            return null;
        }
    }

    private static IEnumerable<JsonNode?> Array(JsonObject? s, string keyword)
        => s?[keyword] is JsonArray a ? a : Enumerable.Empty<JsonNode?>();

    private static bool Bool(JsonObject? s, string keyword)
        => s?[keyword] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static double? Number(JsonObject s, string keyword)
        => s[keyword] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? AsDouble(v) : null;

    /// <summary>A number however the node holds it: parsed JSON, or a value the YAML reader created as an integer.</summary>
    private static double AsDouble(JsonValue v)
    {
        if (v.TryGetValue(out double d))
        {
            return d;
        }

        if (v.TryGetValue(out long l))
        {
            return l;
        }

        if (v.TryGetValue(out decimal m))
        {
            return (double)m;
        }

        return double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string Show(JsonNode? instance)
    {
        var text = instance?.ToJsonString() ?? "null";
        return text.Length <= 80 ? text : text[..77] + "...";
    }
}
