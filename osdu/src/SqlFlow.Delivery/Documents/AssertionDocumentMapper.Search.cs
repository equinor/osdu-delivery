using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// How a test finds its records: the ids it reads by id, the order its search sorts them in, and the search's
// spatial filter (openapi search v2, SpatialFilter) with the points and numbers it is written with.
internal static partial class AssertionMapper
{
    private static IReadOnlyList<string> MapIds(List<string>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count == 0 || declared.Count > MaxIds)
        {
            throw new FlowValidationException($"{source}: {where}: ids lists between 1 and {MaxIds} record ids.");
        }

        var ids = new List<string>(declared.Count);
        foreach (var raw in declared)
        {
            var id = raw?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Length > 1024 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new FlowValidationException($"{source}: {where}: ids holds '{Shown(id)}', which is not a record id (1 to 1024 characters, no whitespace).");
            }

            if (ids.Contains(id, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {where}: ids names '{Shown(id)}' more than once.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static IReadOnlyList<AssertionSort> MapSort(List<AssertionSortYaml>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count is 0 or > 5)
        {
            throw new FlowValidationException($"{source}: {where}: sort lists between 1 and 5 fields.");
        }

        var sort = new List<AssertionSort>(declared.Count);
        foreach (var s in declared)
        {
            var field = ConditionReader.RecordPath(FlowMapper.Require(s?.Field, where + ".sort[].field", source), where + ": sort", source);
            var order = s!.Order?.Trim().ToLowerInvariant() ?? "asc";
            if (order is not ("asc" or "desc"))
            {
                throw new FlowValidationException($"{source}: {where}: sort order '{s.Order}' is not asc or desc.");
            }

            sort.Add(new AssertionSort(field, order == "desc"));
        }

        return sort;
    }

    /// <summary>
    /// The search's spatial filter as written (openapi search v2, <c>SpatialFilter</c>): a geo field, and exactly one of the
    /// filter types, each point with its latitude and longitude in range.
    /// </summary>
    private static JsonObject? MapSpatial(object? declared, string where, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var at = where + ": spatial";
        if (ToJson(declared, at, source) is not JsonObject spatial)
        {
            throw new FlowValidationException($"{source}: {at} must be a mapping: a field and one filter (byBoundingBox, byDistance, byGeoPolygon, byIntersection, byWithinPolygon).");
        }

        string[] filters = ["byBoundingBox", "byDistance", "byGeoPolygon", "byIntersection", "byWithinPolygon"];
        foreach (var (key, _) in spatial)
        {
            if (key != "field" && !filters.Contains(key, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at} has '{key}', which is not field or one of {string.Join(", ", filters)}.");
            }
        }

        if (spatial["field"] is not JsonValue fieldValue || !fieldValue.TryGetValue<string>(out var field) || string.IsNullOrWhiteSpace(field))
        {
            throw new FlowValidationException($"{source}: {at}.field is required: the geo field the filter applies to, such as data.SpatialLocation.Wgs84Coordinates.");
        }

        _ = ConditionReader.RecordPath(field, at + ".field", source);
        var named = filters.Where(f => spatial.ContainsKey(f)).ToList();
        if (named.Count != 1)
        {
            throw new FlowValidationException($"{source}: {at} names {(named.Count == 0 ? "no filter" : string.Join(" and ", named))}; the search takes exactly one of {string.Join(", ", filters)}.");
        }

        var filter = spatial[named[0]] as JsonObject
            ?? throw new FlowValidationException($"{source}: {at}.{named[0]} must be a mapping.");
        switch (named[0])
        {
            case "byBoundingBox":
                Point(filter["topLeft"], $"{at}.byBoundingBox.topLeft", source);
                Point(filter["bottomRight"], $"{at}.byBoundingBox.bottomRight", source);
                Only(filter, ["topLeft", "bottomRight"], $"{at}.byBoundingBox", source);
                break;
            case "byDistance":
                Point(filter["point"], $"{at}.byDistance.point", source);
                if (!IsNumber(filter["distance"], out var distance) || distance < 0)
                {
                    throw new FlowValidationException($"{source}: {at}.byDistance.distance must be a distance in metres, zero or more.");
                }

                Only(filter, ["point", "distance"], $"{at}.byDistance", source);
                break;
            case "byIntersection":
                if (filter["polygons"] is not JsonArray polygons || polygons.Count == 0)
                {
                    throw new FlowValidationException($"{source}: {at}.byIntersection.polygons must list at least one polygon.");
                }

                for (var i = 0; i < polygons.Count; i++)
                {
                    var polygonAt = string.Create(CultureInfo.InvariantCulture, $"{at}.byIntersection.polygons[{i}]");
                    var polygon = polygons[i] as JsonObject ?? throw new FlowValidationException($"{source}: {polygonAt} must be a mapping with points.");
                    Points(polygon["points"], polygonAt + ".points", source);
                    Only(polygon, ["points"], polygonAt, source);
                }

                Only(filter, ["polygons"], $"{at}.byIntersection", source);
                break;
            default:
                Points(filter["points"], $"{at}.{named[0]}.points", source);
                Only(filter, ["points"], $"{at}.{named[0]}", source);
                break;
        }

        return spatial;
    }

    /// <summary>
    /// A finite number the document wrote, whatever YAML read it as: <c>72</c> arrives as a whole number and <c>72.5</c> as a
    /// fraction, and each is read from the JSON it writes, so a whole-number latitude or distance counts as surely as a decimal.
    /// </summary>
    private static bool IsNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue number
            && number.GetValueKind() == JsonValueKind.Number
            && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static void Only(JsonObject node, string[] keys, string at, string source)
    {
        foreach (var (key, _) in node)
        {
            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at} has '{key}', which is not one of {string.Join(", ", keys)}.");
            }
        }
    }

    private static void Points(JsonNode? node, string at, string source)
    {
        if (node is not JsonArray points || points.Count == 0)
        {
            throw new FlowValidationException($"{source}: {at} must list at least one point.");
        }

        for (var i = 0; i < points.Count; i++)
        {
            Point(points[i], string.Create(CultureInfo.InvariantCulture, $"{at}[{i}]"), source);
        }
    }

    private static void Point(JsonNode? node, string at, string source)
    {
        if (node is not JsonObject point
            || !IsNumber(point["latitude"], out var latitude)
            || !IsNumber(point["longitude"], out var longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new FlowValidationException($"{source}: {at} must be a point: latitude between -90 and 90, longitude between -180 and 180.");
        }

        Only(point, ["latitude", "longitude"], at, source);
    }

    /// <summary>A YAML value (scalars, mappings, sequences) as JSON, for what the document passes to the platform as written.</summary>
    private static JsonNode? ToJson(object? value, string at, string source) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int or long or short or byte or sbyte or uint or ushort => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => JsonValue.Create(u),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        float f when float.IsFinite(f) => JsonValue.Create(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        decimal m => JsonValue.Create(m),
        IDictionary map => ToJsonObject(map, at, source),
        IList list => new JsonArray(list.Cast<object?>().Select(item => ToJson(item, at, source)).ToArray()),
        _ => throw new FlowValidationException($"{source}: {at} holds a value that is not text, a number, true/false, a mapping or a list."),
    };

    private static JsonObject ToJsonObject(IDictionary map, string at, string source)
    {
        var node = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            var key = entry.Key as string ?? throw new FlowValidationException($"{source}: {at} has a key that is not text.");
            node[key] = ToJson(entry.Value, $"{at}.{key}", source);
        }

        return node;
    }
}
