using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// A value converted to the type its template variable declares: a number, an integer, a boolean, a date in the
// variable's format, a list of one, or a set of values filling a list.
internal static partial class EntryValues
{
    /// <summary>
    /// Converts a value to the type its variable declares: text becomes a number, an integer or a boolean where the schema
    /// says so, a single value bound to a list becomes a list of one, and a set of values fills a list or holds a single
    /// value. A date is written in the form the variable's format takes. A value that cannot take the type holds the record
    /// with a reason naming the target.
    /// </summary>
    private static JsonNode? Convert(object raw, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (raw is JsonArray set)
        {
            return ConvertSet(set, property, path, holds);
        }

        if (raw is JsonObject obj)
        {
            if (type is SchemaType.Object or SchemaType.Any)
            {
                return obj;
            }

            // An object bound to a list of objects is a list of one, as a single value bound to a list of values is: a
            // literal item of a list of objects, or a cached field holding one object.
            if (type == SchemaType.Array && property?.ItemScalarType is SchemaType.Object)
            {
                return new JsonArray(obj.DeepClone());
            }

            holds.Add($"{path}: an object cannot be written where the template takes a {Name(type)}");
            return null;
        }

        var scalar = raw is JsonValue value ? Native(value) : raw;
        if (type == SchemaType.Array && property?.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var single = Scalar(scalar, itemType, property?.ItemFormat, path, holds);
            return single is null ? null : new JsonArray(single);
        }

        return Scalar(scalar, type, property?.Format, path, holds);
    }

    private static JsonNode? ConvertSet(JsonArray set, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (type is SchemaType.Array && property?.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var items = new JsonArray();
            foreach (var element in set)
            {
                if (element is not null && Scalar(Native(element), itemType, property?.ItemFormat, path, holds) is { } converted)
                {
                    items.Add(converted);
                }
            }

            return items.Count > 0 ? items : null;
        }

        if (type is SchemaType.Array or SchemaType.Object or SchemaType.Any)
        {
            return set.DeepClone();
        }

        if (set.Count == 1 && set[0] is { } only)
        {
            return Scalar(Native(only), type, property?.Format, path, holds);
        }

        holds.Add($"{path}: {set.Count} values were given but the template takes one {Name(type)}; read one value or fill a list");
        return null;
    }

    private static JsonNode? Scalar(object raw, SchemaType type, string? format, string path, List<string> holds)
    {
        if (DateValues.IsDate(raw) && type is SchemaType.String or SchemaType.Any)
        {
            var written = DateValues.Write(raw, format, out var dateProblem);
            if (written is null)
            {
                holds.Add($"{path}: {dateProblem}");
                return null;
            }

            return JsonValue.Create(written);
        }

        // A number is written in the form its property takes: a number, an integer in its format's range, or its shortest text.
        string? problem;
        switch (type)
        {
            case SchemaType.Number:
                return Written(NumberValues.ToNumber(raw, out problem), problem, path, holds);
            case SchemaType.Integer:
                return Written(NumberValues.ToInteger(raw, format, out problem), problem, path, holds);
            case SchemaType.Any when NumberValues.IsNumber(raw):
                return Written(NumberValues.ToNumber(raw, out problem), problem, path, holds);
            case SchemaType.String when NumberValues.IsNumber(raw):
                return Written(NumberValues.ToText(raw, out problem) is { } text ? JsonValue.Create(text) : null, problem, path, holds);
        }

        try
        {
            switch (type)
            {
                case SchemaType.String:
                    return JsonValue.Create(SourceRow.Stringify(raw));
                case SchemaType.Boolean:
                    return raw switch
                    {
                        bool b => JsonValue.Create(b),
                        _ => JsonValue.Create(ParseBool(SourceRow.Stringify(raw)!)),
                    };
                case SchemaType.Object:
                case SchemaType.Array:
                    throw new FormatException($"a single value cannot be written where the template takes an {Name(type)}");
                default:
                    return JsonValue.Create(Clr(raw));
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            holds.Add($"{path}: value '{SourceRow.Stringify(raw)}' is not a valid {Name(type)} ({ex.Message})");
            return null;
        }
    }

    /// <summary>A converted value, or the reason it could not be converted added as a hold naming the target.</summary>
    private static JsonNode? Written(JsonNode? value, string? problem, string path, List<string> holds)
    {
        if (value is null)
        {
            holds.Add($"{path}: {problem}");
        }

        return value;
    }

    /// <summary>A JSON value as the CLR value the conversions expect, so a cached or static value converts like a dataset value.</summary>
    private static object Native(JsonNode node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            JsonValueKind.Number => value.TryGetValue<long>(out var whole) ? whole : value.GetValue<double>(),
            _ => node,
        }
        : node;

    /// <summary>A value that is not a number or a date, for a variable the template does not type: text and booleans as they are, anything else as its text.</summary>
    private static object Clr(object raw) => raw is string or bool ? raw : SourceRow.Stringify(raw)!;

    private static bool ParseBool(string text)
    {
        var t = text.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t is "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t is "0" || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new FormatException($"'{text}' is not a boolean");
    }

    private static string Name(SchemaType type) => type.ToString().ToLowerInvariant();
}
