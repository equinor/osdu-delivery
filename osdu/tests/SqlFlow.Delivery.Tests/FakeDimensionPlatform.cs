using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A stand-in for the search service as a dimension build reads it, built from its source (<c>AggregationParserUtil</c>,
/// <c>CoreQueryBase</c>, <c>TypeMapper</c>) and the pinned contract: an aggregation that returns at most
/// <see cref="AggregationSize"/> groups, by count and then by key, over the records a query matches (every value of a
/// matched record counted, each object of a nested array counted as one); a keyword sub-field that keeps no text longer than
/// 256 characters and keeps a null as the text null; a plain number's groups without keys when <see cref="NumberKeysMissing"/>
/// says so; the first records a query matches, up to its limit, holding the fields it returns; cursor paging; and exact counts. Its query parser reads exactly the forms a dimension writes: clauses joined by
/// AND, NOT, a group of ORs, the nested form, _exists_, a range, a value and a list of values; and the phrase with no field
/// that the records naming an id are found by, searched in every text of a record but its id. Every request is recorded as
/// <see cref="FakeHttpHandler"/> records them, so the contract harness checks them.
/// </summary>
internal sealed class FakeDimensionPlatform : HttpMessageHandler
{
    public const string Endpoint = "http://localhost";

    private const string Keyword = ".keyword";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (List<JsonObject> Hits, int Next)> _cursors = new(StringComparer.Ordinal);
    private int _cursorCount;
    private int _answering;
    private int _mostAtOnce;

    public List<FakeHttpHandler.Request> Calls { get; } = [];

    /// <summary>How long the platform takes to answer a request, as a service across a network does; no time by default.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>The most requests the platform was answering at one time.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    public List<JsonObject> Records { get; } = [];

    /// <summary>The most groups an aggregation returns: the platform's AGGREGATION_SIZE.</summary>
    public int AggregationSize { get; set; } = 1000;

    /// <summary>When true, a plain number's groups come back without keys, as the service renders them from key_as_string.</summary>
    public bool NumberKeysMissing { get; set; }

    /// <summary>Answers a request with this status instead, when it says so.</summary>
    public Func<string?, HttpStatusCode?>? Fail { get; set; }

    /// <summary>
    /// Loses the answer to a cursor page when it says so: the cursor moves on as the page is read, and the answer is a 503,
    /// as from a gateway that gave up on a page the service went on to answer.
    /// </summary>
    public Func<string?, bool>? Lose { get; set; }

    public JsonObject Add(string id, string kind, JsonObject data, string[]? legalTags = null, JsonObject? tags = null)
    {
        var record = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["version"] = 1_700_000_000_000_000,
            ["acl"] = new JsonObject { ["viewers"] = new JsonArray("data.default.viewers@dev.dataservices.energy") },
            ["legal"] = new JsonObject { ["legaltags"] = new JsonArray((legalTags ?? ["dev-legal"]).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) },
            ["data"] = data,
        };
        if (tags is not null)
        {
            record["tags"] = tags;
        }

        Records.Add(record);
        return record;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var answering = Interlocked.Increment(ref _answering);
        try
        {
            int most;
            while (answering > (most = Volatile.Read(ref _mostAtOnce)) && Interlocked.CompareExchange(ref _mostAtOnce, answering, most) != most)
            {
            }

            if (Latency > TimeSpan.Zero)
            {
                await Task.Delay(Latency, cancellationToken);
            }

            return Answer(request, body, headers);
        }
        finally
        {
            Interlocked.Decrement(ref _answering);
        }
    }

    private HttpResponseMessage Answer(HttpRequestMessage request, string? body, Dictionary<string, string> headers)
    {
        lock (_gate)
        {
            Calls.Add(new FakeHttpHandler.Request(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers));
            if (Fail?.Invoke(body) is { } status)
            {
                return FakeHttpHandler.Json(status, """{"code":503,"reason":"Service Unavailable","message":"the service failed"}""");
            }

            var path = request.RequestUri!.AbsolutePath;
            var json = string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body) as JsonObject;
            if (request.Method == HttpMethod.Post && path.EndsWith("/api/search/v2/query", StringComparison.Ordinal))
            {
                return Ok(Query(json!));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/search/v2/query_with_cursor", StringComparison.Ordinal))
            {
                var page = Cursor(json!);
                return Lose?.Invoke(body) == true
                    ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"code":503,"reason":"Service Unavailable","message":"the gateway gave up"}""")
                    : Ok(page);
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/api/search/v2/query_with_cursor/", StringComparison.Ordinal))
            {
                _cursors.Remove(path[(path.LastIndexOf('/') + 1)..]);
                return Ok(new JsonObject { ["results"] = new JsonArray(), ["totalCount"] = 0 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route for " + request.RequestUri) };
        }
    }

    private static HttpResponseMessage Ok(JsonObject body) => FakeHttpHandler.Json(HttpStatusCode.OK, body.ToJsonString());

    private List<JsonObject> Matching(JsonObject body)
    {
        var kind = body["kind"]!.GetValue<string>();
        var query = body["query"]?.GetValue<string>();
        // The query is taken apart once for the request, not once for each record it is held against: a label search names
        // five hundred ids, and the records it is held against are every record of the platform.
        var parse = new QueryParse();
        return Records.Where(r => KindMatches(kind, r["kind"]!.GetValue<string>()) && Holds(r, query, parse)).ToList();
    }

    /// <summary>
    /// The parts of one request's query, each taken apart the first time a record is held against it. A part is found again
    /// by the text it was taken from, as the same string: every record is held against the very strings the first one was,
    /// so a query of five hundred ids is never read again to be found.
    /// </summary>
    private sealed class QueryParse
    {
        private readonly Dictionary<string, string> _stripped = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, List<string>> _ors = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, List<string>> _ands = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, LeafClause> _leaves = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string> _negated = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, (string Parent, string Inner)> _nested = new(ReferenceEqualityComparer.Instance);

        /// <summary>What a <c>NOT</c> clause negates.</summary>
        public string Negated(string clause)
            => _negated.TryGetValue(clause, out var negated) ? negated : _negated[clause] = Strip(clause[4..]);

        /// <summary>The array a <c>nested(</c> clause steps into, and the clause each of its objects is held against.</summary>
        public (string Parent, string Inner) Nested(string clause)
        {
            if (_nested.TryGetValue(clause, out var nested))
            {
                return nested;
            }

            var comma = clause.IndexOf(',', StringComparison.Ordinal);
            return _nested[clause] = (clause[7..comma], Strip(clause[(comma + 1)..^1].Trim()));
        }

        public string Strip(string clause)
            => _stripped.TryGetValue(clause, out var stripped) ? stripped : _stripped[clause] = FakeDimensionPlatform.Strip(clause);

        public List<string> Split(string text, string separator)
        {
            var parts = separator == " OR " ? _ors : separator == " AND " ? _ands : throw new ArgumentOutOfRangeException(nameof(separator), separator, null);
            return parts.TryGetValue(text, out var split) ? split : parts[text] = FakeDimensionPlatform.Split(text, separator);
        }

        /// <summary>A clause comparing one field, taken apart.</summary>
        public LeafClause Leaf(string clause)
            => _leaves.TryGetValue(clause, out var leaf) ? leaf : _leaves[clause] = LeafClause.Of(clause);
    }

    /// <summary>The ids of the records <paramref name="query"/> matches in <paramref name="kind"/>, as the service would find them.</summary>
    public IReadOnlyList<string> Find(string kind, string query)
    {
        lock (_gate)
        {
            return Matching(new JsonObject { ["kind"] = kind, ["query"] = query }).Select(r => r["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        }
    }

    private JsonObject Query(JsonObject body)
    {
        var matched = Matching(body);
        var limit = body["limit"]?.GetValue<int>() ?? 10;
        var offset = body["offset"]?.GetValue<int>() ?? 0;
        var fields = body["returnedFields"] is JsonArray returned ? returned.Select(f => f!.GetValue<string>()).ToList() : null;
        var hits = matched.Skip(offset).Take(limit).Select(r => (JsonNode?)(fields is null ? r.DeepClone() : Project(r, fields))).ToArray();
        var result = new JsonObject { ["results"] = new JsonArray(hits), ["totalCount"] = matched.Count };
        if (body["aggregateBy"]?.GetValue<string>() is { } aggregateBy)
        {
            result["aggregations"] = Aggregate(matched, aggregateBy);
        }

        return result;
    }

    /// <summary>
    /// A record holding only <paramref name="fields"/>, as the service returns it: each path's objects kept down to the value
    /// it names, and an array met on the way kept whole.
    /// </summary>
    private static JsonObject Project(JsonObject record, IReadOnlyList<string> fields)
    {
        var projected = new JsonObject();
        foreach (var field in fields)
        {
            Copy(record, projected, field.Split('.'), 0);
        }

        return projected;
    }

    private static void Copy(JsonObject source, JsonObject target, string[] path, int at)
    {
        if (!source.TryGetPropertyValue(path[at], out var child))
        {
            return;
        }

        if (at == path.Length - 1 || child is not JsonObject inner)
        {
            target[path[at]] = child?.DeepClone();
            return;
        }

        if (target[path[at]] is not JsonObject into)
        {
            into = new JsonObject();
            target[path[at]] = into;
        }

        Copy(inner, into, path, at + 1);
    }

    private JsonObject Cursor(JsonObject body)
    {
        var limit = body["limit"]?.GetValue<int>() ?? 10;
        string handle;
        if (body["cursor"]?.GetValue<string>() is { } named)
        {
            handle = named;
        }
        else
        {
            handle = "cursor" + (++_cursorCount).ToString(CultureInfo.InvariantCulture);
            _cursors[handle] = (Matching(body), 0);
        }

        var (hits, next) = _cursors[handle];
        var page = hits.Skip(next).Take(limit).ToList();
        _cursors[handle] = (hits, next + page.Count);
        var result = new JsonObject
        {
            ["results"] = new JsonArray(page.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
            ["cursor"] = page.Count == 0 ? null : handle,
        };
        if (body["cursor"] is null)
        {
            result["totalCount"] = hits.Count;
        }

        return result;
    }

    /// <summary>The groups of a field over the matched records, as the service answers <c>aggregateBy</c>.</summary>
    private JsonArray Aggregate(List<JsonObject> matched, string aggregateBy)
    {
        var (nested, field) = aggregateBy.StartsWith("nested(", StringComparison.Ordinal)
            ? (aggregateBy[7..aggregateBy.IndexOf(',', StringComparison.Ordinal)], aggregateBy[(aggregateBy.IndexOf(',', StringComparison.Ordinal) + 1)..^1].Trim())
            : ((string?)null, aggregateBy);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var numeric = false;
        foreach (var record in matched)
        {
            var units = nested is null ? [record] : Values(record, nested).Where(v => v.Node is JsonObject).Select(v => (JsonNode)v.Node!).ToList();
            foreach (var unit in units)
            {
                foreach (var key in Keys(unit, field).Distinct(StringComparer.Ordinal))
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }

            numeric |= units.Any(u => Values(u, field).Any(v => v.Node is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number));
        }

        var order = numeric ? DimensionValueText.Order(OsduFieldIndex.Number) : DimensionValueText.Order(OsduFieldIndex.Keyword);
        return new JsonArray(counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, order)
            .Take(AggregationSize)
            .Select(kv => (JsonNode?)new JsonObject { ["key"] = numeric && NumberKeysMissing ? null : JsonValue.Create(kv.Key), ["count"] = kv.Value })
            .ToArray());
    }

    /// <summary>The terms a field holds: the keyword sub-field's (no text over 256 characters, a null as the text null), or the value's own.</summary>
    private static IEnumerable<string> Keys(JsonNode root, string field)
    {
        var keyword = field.EndsWith(Keyword, StringComparison.Ordinal);
        var path = keyword ? field[..^Keyword.Length] : field;
        foreach (var (isNull, node) in Values(root, path))
        {
            if (isNull)
            {
                if (keyword)
                {
                    yield return OsduQuery.KeywordNullValue;
                }

                continue;
            }

            if (node is not JsonValue value)
            {
                continue;
            }

            var text = Text(value);
            if (!keyword || text.Length <= OsduQuery.KeywordIgnoreAbove)
            {
                yield return text;
            }
        }
    }

    private static string Text(JsonValue value) => value.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => DimensionValueText.CanonicalNumber(value.ToJsonString()) ?? value.ToJsonString(),
    };

    /// <summary>Every value a path reaches, arrays crossed wherever they are met, a null kept as one.</summary>
    private static List<(bool IsNull, JsonNode? Node)> Values(JsonNode? node, string path)
    {
        var found = new List<(bool, JsonNode?)>();
        Walk(node, path.Split('.'), 0, found);
        return found;
    }

    private static void Walk(JsonNode? node, string[] path, int at, List<(bool, JsonNode?)> found)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null && at == path.Length)
                {
                    found.Add((true, null));
                }
                else
                {
                    Walk(item, path, at, found);
                }
            }

            return;
        }

        if (at == path.Length)
        {
            found.Add((node is null, node));
            return;
        }

        if (node is JsonObject obj && obj.TryGetPropertyValue(path[at], out var child))
        {
            if (child is null)
            {
                if (at == path.Length - 1)
                {
                    found.Add((true, null));
                }

                return;
            }

            Walk(child, path, at + 1, found);
        }
    }

    private static bool KindMatches(string pattern, string kind)
    {
        var want = pattern.Split(':');
        var have = kind.Split(':');
        return want.Length == have.Length && want.Zip(have).All(p => p.First == "*" || string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a record holds for a query of the forms a dimension writes.</summary>
    private static bool Holds(JsonNode record, string? query, QueryParse parse)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim() == "*")
        {
            return true;
        }

        return parse.Split(parse.Strip(query), " AND ").All(clause => Clause(record, parse.Strip(clause), parse));
    }

    private static bool Clause(JsonNode record, string clause, QueryParse parse)
    {
        // OR binds looser than AND, as the service parses a group: a OR (b AND NOT c).
        var alternatives = parse.Split(clause, " OR ");
        if (alternatives.Count > 1)
        {
            return alternatives.Any(a => Clause(record, parse.Strip(a), parse));
        }

        var all = parse.Split(clause, " AND ");
        if (all.Count > 1)
        {
            return all.All(a => Clause(record, parse.Strip(a), parse));
        }

        if (clause.StartsWith("NOT ", StringComparison.Ordinal))
        {
            return !Clause(record, parse.Negated(clause), parse);
        }

        if (clause.StartsWith("nested(", StringComparison.Ordinal))
        {
            var (parent, inner) = parse.Nested(clause);
            return Values(record, parent).Where(v => v.Node is JsonObject).Any(v => Clause(v.Node!, inner, parse));
        }

        if (clause.StartsWith("_exists_:", StringComparison.Ordinal))
        {
            var field = clause[9..];
            return field.EndsWith(Keyword, StringComparison.Ordinal)
                ? Keys(record, field).Any()
                : Values(record, field).Any(v => !v.IsNull);
        }

        // A phrase with no field is searched in every value of the record, as the service searches its text: the record's own id
        // aside, which is a field of its own, any text holding the phrase, ignoring case.
        if (clause.StartsWith('"'))
        {
            var phrase = Unquote(clause);
            return record is JsonObject whole && whole.Where(p => p.Key != "id").Any(p => Phrased(p.Value, phrase));
        }

        var leaf = parse.Leaf(clause);
        var keys = Keys(record, leaf.Name).ToList();
        if (leaf.IsRange)
        {
            var (from, upto) = (leaf.From, leaf.Upto);
            var order = keys.Any(k => DimensionValueText.CanonicalNumber(k) is null) || (from is not null && DimensionValueText.CanonicalNumber(from) is null)
                ? DimensionValueText.Order(OsduFieldIndex.Keyword)
                : DimensionValueText.Order(OsduFieldIndex.Number);
            return keys.Any(k => (from is null || order.Compare(k, from) >= 0) && (upto is null || (leaf.UptoOpen ? order.Compare(k, upto) < 0 : order.Compare(k, upto) <= 0)));
        }

        if (leaf.Values is { } values)
        {
            return keys.Any(values.Contains);
        }

        var wanted = leaf.Wanted!;
        return keys.Any(k => k == wanted || DimensionValueText.CanonicalNumber(k) is { } n && n == DimensionValueText.CanonicalNumber(wanted));
    }

    /// <summary>
    /// A clause comparing one field, taken apart: the field, and a range (<c>[from TO to}</c>), a list of phrases
    /// (<c>("a" OR "b")</c>), or one value.
    /// </summary>
    private sealed record LeafClause(string Name, bool IsRange, string? From, string? Upto, bool UptoOpen, HashSet<string>? Values, string? Wanted)
    {
        public static LeafClause Of(string clause)
        {
            var colon = clause.IndexOf(':', StringComparison.Ordinal);
            var name = clause[..colon];
            var operand = clause[(colon + 1)..];
            if (operand.StartsWith('['))
            {
                var body = operand[1..^1];
                var to = body.IndexOf(" TO ", StringComparison.Ordinal);
                return new LeafClause(name, true, Bound(body[..to]), Bound(body[(to + 4)..]), operand[^1] == '}', null, null);
            }

            return operand.StartsWith('(')
                ? new LeafClause(name, false, null, null, false, Split(operand[1..^1], " OR ").Select(Unquote).ToHashSet(StringComparer.Ordinal), null)
                : new LeafClause(name, false, null, null, false, null, Unquote(operand));
        }
    }

    /// <summary>Whether any text inside <paramref name="node"/> holds <paramref name="phrase"/>, ignoring case.</summary>
    private static bool Phrased(JsonNode? node, string phrase) => node switch
    {
        JsonObject obj => obj.Any(p => Phrased(p.Value, phrase)),
        JsonArray array => array.Any(item => Phrased(item, phrase)),
        JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String => value.GetValue<string>().Contains(phrase, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static string? Bound(string text) => text.Trim() == "*" ? null : Unquote(text.Trim());

    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[^1] != '"')
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        for (var i = 1; i < trimmed.Length - 1; i++)
        {
            if (trimmed[i] == '\\' && i + 1 < trimmed.Length - 1)
            {
                i++;
            }

            builder.Append(trimmed[i]);
        }

        return builder.ToString();
    }

    /// <summary>A clause without the parentheses that wrap the whole of it.</summary>
    private static string Strip(string clause)
    {
        var text = clause.Trim();
        while (text.StartsWith('(') && Closing(text, 0) == text.Length - 1)
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    /// <summary>Splits on <paramref name="separator"/> where it stands outside every parenthesis, bracket and quote.</summary>
    private static List<string> Split(string text, string separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                default:
                    if (depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
                    {
                        parts.Add(text[start..i]);
                        start = i + separator.Length;
                        i += separator.Length - 1;
                    }

                    break;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/>, quotes honoured.</summary>
    private static int Closing(string text, int open)
    {
        var depth = 0;
        var quoted = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
