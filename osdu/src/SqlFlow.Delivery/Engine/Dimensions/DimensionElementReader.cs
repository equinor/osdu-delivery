using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a build read of its dimension's elements: each key's objects in their order, the pages and ranges it read, and its notes.</summary>
internal sealed record ElementRead(
    IReadOnlyDictionary<string, IReadOnlyList<DimensionElementState>> Keys, long Elements, int Pages, int Ranges, IReadOnlyList<string> Notes);

/// <summary>
/// Keys a read covers and the query that finds their records: every key and the dimension's query for a full load; for an
/// incremental load, the keys it reads again, a query's worth at a time, each group with the filter finding its records.
/// The keys of two scopes are never the same.
/// </summary>
internal sealed record DimensionScope(string? Query, DistinctRead Keys);

/// <summary>
/// Reads the objects of a dimension's nested array (<see cref="DimensionElementsSpec"/>): one pass over the dimension's
/// records through the search cursor, each record's key, id and objects read together, every field of an object kept as the
/// field keeps it. The pass is cut into ranges of the dimension's keys read side by side, as a collected attribute's pass is
/// (<see cref="DimensionCollector.Ranges"/>); a record holding keys of two ranges is read in both and kept under the keys of
/// each range alone. A key's objects are numbered from 1 in the order of their records' ids and then of the objects in
/// each record, so the same records give the same numbers build after build.
/// </summary>
internal sealed class DimensionElementReader(OsduSearch search, ILogger log, int concurrency)
{
    /// <summary>Reads the elements of the keys <paramref name="keys"/> found, held at <paramref name="keyField"/>.</summary>
    /// <exception cref="DeliveryException">The records hold more objects than a build reads.</exception>
    public Task<ElementRead> ReadAsync(DimensionSpec dimension, string? query, OsduField keyField, DistinctRead keys, CancellationToken ct)
        => ReadAsync(dimension, [new DimensionScope(query, keys)], keyField, ct);

    /// <summary>
    /// Reads the elements of the keys of every scope, held at <paramref name="keyField"/>: each scope's records found by its
    /// own query and cut into ranges of its own keys, every range of every scope read side by side.
    /// </summary>
    /// <exception cref="DeliveryException">The records hold more objects than a build reads.</exception>
    public async Task<ElementRead> ReadAsync(DimensionSpec dimension, IReadOnlyList<DimensionScope> scopes, OsduField keyField, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(keyField);
        var elements = dimension.Elements ?? throw new ArgumentException($"Dimension {dimension.Name} reads no elements.", nameof(dimension));
        var order = DimensionValueText.Order(keyField.Index);
        var work = scopes
            .SelectMany(scope => DimensionCollector.Ranges(keyField, scope.Keys.Values, order, Math.Max(1, concurrency)).Select(range => (Scope: scope, Range: range)))
            .ToList();
        var returned = new List<string> { "id", keyField.Path };
        returned.AddRange(elements.ReturnedFields());
        returned = returned.Distinct(StringComparer.Ordinal).ToList();
        var path = elements.Parsed;
        var fieldReaders = elements.Fields.Select(FieldReader.Of).ToList();

        var found = new Dictionary<string, List<(string Record, int Ordinal, List<KeyValuePair<string, string>> Values)>>(StringComparer.Ordinal);
        long total = 0;
        var cut = 0;
        var tooLong = 0;
        var pages = 0;
        var gate = new Lock();
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (item, token) =>
            {
                var (scope, range) = item;
                var keys = scope.Keys;
                var own = string.IsNullOrWhiteSpace(scope.Query) || scope.Query.Trim() == "*" ? null : scope.Query.Trim();
                var request = new OsduSearchQuery
                {
                    Kind = dimension.Kind,
                    Query = range.IsWhole ? own : OsduSearch.Within(own, OsduQuery.Range(keyField, range.From, range.To).Text),
                    ReturnedFields = returned,
                };

                await foreach (var page in search.PagesAsync(request, OsduSearch.MaxPage, token).ConfigureAwait(false))
                {
                    var read = new List<(string Key, string Record, int Ordinal, List<KeyValuePair<string, string>> Values)>();
                    var pageCut = 0;
                    var pageTooLong = 0;
                    foreach (var hit in page.Hits)
                    {
                        if (!hit.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String || idElement.GetString() is not { Length: > 0 } record)
                        {
                            continue;
                        }

                        var held = DimensionCollector.TextsOf(hit, keyField).Where(key => keys.Values.ContainsKey(key) && range.Contains(key, order)).ToList();
                        if (held.Count == 0)
                        {
                            continue;
                        }

                        var objects = new List<(JsonNode Element, IReadOnlyList<JsonObject> Ancestors)>();
                        Walk(JsonObject.Create(hit), path, 0, [], objects, elements.KeepNulls);
                        for (var ordinal = 0; ordinal < objects.Count; ordinal++)
                        {
                            var (element, ancestors) = objects[ordinal];
                            var values = new List<KeyValuePair<string, string>>(fieldReaders.Count);
                            foreach (var field in fieldReaders)
                            {
                                var (kept, wasCut, over) = field.Read(element, ancestors);
                                pageCut += wasCut ? 1 : 0;
                                pageTooLong += over ? 1 : 0;
                                if (kept is not null)
                                {
                                    values.Add(new KeyValuePair<string, string>(field.Name, kept));
                                }
                            }

                            read.AddRange(held.Select(key => (key, record, ordinal, values)));
                        }
                    }

                    // A page is kept in one go, under the lock the ranges share.
                    lock (gate)
                    {
                        pages++;
                        cut += pageCut;
                        tooLong += pageTooLong;
                        foreach (var (key, record, ordinal, values) in read)
                        {
                            (found.TryGetValue(key, out var list) ? list : found[key] = []).Add((record, ordinal, values));
                        }

                        total += read.Count;
                        if (total > DimensionElementsSpec.MaxElements)
                        {
                            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                                $"Dimension {dimension.Name} reads more than {DimensionElementsSpec.MaxElements:N0} objects at {elements.Path}, the most a build reads. Narrow its query, or split it over dimensions with narrower queries."));
                        }
                    }
                }
            }).ConfigureAwait(false);

        var byKey = new Dictionary<string, IReadOnlyList<DimensionElementState>>(found.Count, StringComparer.Ordinal);
        foreach (var (key, list) in found)
        {
            byKey[key] = list
                .OrderBy(e => e.Record, StringComparer.Ordinal).ThenBy(e => e.Ordinal)
                .Select((e, i) => new DimensionElementState(i + 1, e.Values))
                .ToList();
        }

        var notes = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"{total} object(s) at {elements.Path} read for {byKey.Count} of {scopes.Sum(s => s.Keys.Values.Count)} key(s); a key whose records hold none is one row with no element."),
        };
        if (cut > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{cut} element value(s) were longer than the {DimensionSpec.MaxElementValueLength} characters a dimension keeps of a field and were cut."));
        }

        if (tooLong > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{tooLong} element value(s) kept as a key or an id were longer than the {DimensionSpec.MaxElementValueLength} characters a dimension keeps of a field, and are left out rather than cut, since a key cut joins to nothing."));
        }

        log.LogInformation(
            "dimension {Dimension}: {Elements} object(s) at {Path} for {Keys} key(s), read in one pass of {Pages} page(s) over {Ranges} range(s) of keys",
            dimension.Name, total, elements.Path, byKey.Count, pages, work.Count);
        return new ElementRead(byKey, total, pages, work.Count, notes);
    }

    /// <summary>
    /// Every element the path reaches, in the order the record holds them, with the objects passed on the way to it (the
    /// record first, the object holding the element's array last): an array on the way stepped into, a filter keeping the
    /// objects it matches, and what the path ends at taken element by element, an object or a plain value. A null where an
    /// element is is no element, unless <paramref name="keepNulls"/> makes it one holding nothing, so every field it reads
    /// in the element is null and those read up the path are not.
    /// </summary>
    internal static void Walk(
        JsonNode? node, DimensionPath path, int at, IReadOnlyList<JsonObject> passed, List<(JsonNode Element, IReadOnlyList<JsonObject> Ancestors)> into,
        bool keepNulls = false)
    {
        switch (node)
        {
            case null:
                if (keepNulls && at == path.Segments.Count && passed.Count > 0)
                {
                    into.Add((new JsonObject(), passed));
                }

                return;
            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, path, at, passed, into, keepNulls);
                }

                return;
            case not null when at == path.Segments.Count:
                into.Add((node, passed));
                return;
            case JsonObject obj:
                var segment = path.Segments[at];
                if (!obj.TryGetPropertyValue(segment.Name, out var child) || child is null)
                {
                    return;
                }

                IReadOnlyList<JsonObject> through = [.. passed, obj];
                if (segment.FilterProperty is null)
                {
                    Walk(child, path, at + 1, through, into, keepNulls);
                    return;
                }

                foreach (var item in child is JsonArray items ? items.Select(i => i) : [child])
                {
                    if (segment.Keeps(item))
                    {
                        Walk(item, path, at + 1, through, into, keepNulls);
                    }
                }

                return;
            default:
                return;
        }
    }

    /// <summary>One field of the elements, ready to read: its name, its path parsed, where it is read from, and how it is kept.</summary>
    internal sealed class FieldReader
    {
        private readonly DimensionElementField _field;
        private readonly DimensionPath? _path;

        private FieldReader(DimensionElementField field, DimensionPath? path)
        {
            _field = field;
            _path = path;
        }

        public string Name => _field.Name;

        /// <summary>The reader of <paramref name="field"/>; the document mapper refuses a path that does not parse.</summary>
        public static FieldReader Of(DimensionElementField field)
            => new(field, field.Path == DimensionElementsSpec.Self
                ? null
                : DimensionPath.Parse(field.Path).Path ?? throw new InvalidOperationException($"'{field.Path}' is not a field path."));

        /// <summary>
        /// The field's value in <paramref name="element"/>, or in the object <see cref="DimensionElementField.Up"/> objects up
        /// its path: the first value it reaches, every one joined, or the greatest, decoded and kept as the field says;
        /// whether it was cut, and whether it was left out for its length.
        /// </summary>
        public (string? Value, bool Cut, bool TooLong) Read(JsonNode element, IReadOnlyList<JsonObject> ancestors)
        {
            var from = _field.Up == 0 ? element : _field.Up <= ancestors.Count ? ancestors[^_field.Up] : null;
            List<string> texts = _path is null
                ? element is JsonValue value && Scalar(value) is { } self ? [self] : []
                : from is null ? [] : _path.Read(from);
            var (kept, _, cut, tooLong) = DimensionKeeping.KeepMany(
                texts, _field.Keep, _field.Many, _field.Decode, DimensionSpec.MaxElementValueLength, _field.Trim);
            return (kept, cut, tooLong);
        }

        private static string? Scalar(JsonValue value)
            => value.TryGetValue<string>(out var text) ? text : value.GetValueKind() is JsonValueKind.Null ? null : value.ToJsonString();
    }
}
