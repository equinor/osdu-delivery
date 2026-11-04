using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A stand-in for what an assertion flow reads, built from the pinned contracts: the search service (an exact count, an
/// aggregation, a page of hits with the index block, and cursor paging with its release), storage's read by id, the legal
/// service's validation, and the Wellbore DDMS's bulk reads (describe and split pages). Its search understands the queries
/// the tests and the module write: clauses joined by AND and OR and grouped in parentheses, a field equal to a value (a
/// phrase, ignoring case), its keyword sub-field equal to one (exactly) or to any of a group of values, a keyword starting
/// with an escaped prefix, a range on the index status, and a range of dates. Every request is recorded as <see cref="FakeHttpHandler"/>
/// records them, so the contract harness checks them.
/// </summary>
internal sealed partial class FakeAssertionPlatform : HttpMessageHandler
{
    public const string Endpoint = "http://localhost";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (List<JsonObject> Hits, int Next, IReadOnlyList<string> Fields)> _cursors = new(StringComparer.Ordinal);
    private int _cursorCount;

    public List<FakeHttpHandler.Request> Calls { get; } = [];

    /// <summary>Every record the platform holds, in the order the index lists them.</summary>
    public List<JsonObject> Records { get; } = [];

    /// <summary>Records the index lists and storage does not return (deleted since they were indexed).</summary>
    public HashSet<string> NotStored { get; } = new(StringComparer.Ordinal);

    /// <summary>The index status of a record, when it is not 200.</summary>
    public Dictionary<string, int> IndexStatus { get; } = new(StringComparer.Ordinal);

    /// <summary>The legal tags the legal service would refuse, with its reason.</summary>
    public Dictionary<string, string> InvalidLegalTags { get; } = new(StringComparer.Ordinal);

    /// <summary>The bulk data the DDMS holds for a record: its columns and rows.</summary>
    public Dictionary<string, (string[] Columns, List<object?[]> Rows)> Bulk { get; } = new(StringComparer.Ordinal);

    /// <summary>The rows a record's bulk data is described as holding, where that differs from the rows its pages answer.</summary>
    public Dictionary<string, int> DescribedRows { get; } = new(StringComparer.Ordinal);

    /// <summary>The most hits one page returns, whatever the request asks for.</summary>
    public int PageCap { get; set; } = 1000;

    /// <summary>Answers a request with this status instead, when it says so.</summary>
    public Func<HttpRequestMessage, HttpStatusCode?>? Fail { get; set; }

    public JsonObject Add(string id, string kind, JsonObject data, string[]? legalTags = null, bool withLegal = true)
    {
        var record = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["version"] = 1_700_000_000_000_000,
            ["acl"] = new JsonObject
            {
                ["viewers"] = new JsonArray("data.default.viewers@dev.dataservices.energy"),
                ["owners"] = new JsonArray("data.default.owners@dev.dataservices.energy"),
            },
            ["data"] = data,
            ["createTime"] = "2026-09-01T10:00:00.000Z",
        };
        if (withLegal)
        {
            record["legal"] = new JsonObject
            {
                ["legaltags"] = new JsonArray((legalTags ?? ["dev-legal"]).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["otherRelevantDataCountries"] = new JsonArray("US"),
            };
        }

        Records.Add(record);
        return record;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            Calls.Add(new FakeHttpHandler.Request(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers));
            if (Fail?.Invoke(request) is { } status)
            {
                return FakeHttpHandler.Json(status, """{"code":500,"reason":"Internal error","message":"the service failed"}""");
            }

            var path = request.RequestUri!.AbsolutePath;
            var json = string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body) as JsonObject;
            if (request.Method == HttpMethod.Post && path.EndsWith("/api/search/v2/query", StringComparison.Ordinal))
            {
                return AggregatesText(json!)
                    ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad Request","message":"Aggregations are not supported for one or more of the specified fields"}""")
                    : Ok(Query(json!));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/search/v2/query_with_cursor", StringComparison.Ordinal))
            {
                return Ok(Cursor(json!));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/api/search/v2/query_with_cursor/", StringComparison.Ordinal))
            {
                _cursors.Remove(path[(path.LastIndexOf('/') + 1)..]);
                return Ok(new JsonObject { ["results"] = new JsonArray(), ["totalCount"] = 0 });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/storage/v2/query/records", StringComparison.Ordinal))
            {
                return Ok(Storage(json!));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/legal/v1/legaltags:validate", StringComparison.Ordinal))
            {
                var names = (json!["names"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
                return Ok(new JsonObject
                {
                    ["invalidLegalTags"] = new JsonArray(names.Where(InvalidLegalTags.ContainsKey)
                        .Select(n => (JsonNode?)new JsonObject { ["name"] = n, ["reason"] = InvalidLegalTags[n] }).ToArray()),
                });
            }

            if (request.Method == HttpMethod.Get && DataPath().Match(path) is { Success: true } data)
            {
                return BulkData(HttpUtility.UrlDecode(data.Groups["id"].Value), HttpUtility.ParseQueryString(request.RequestUri.Query));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route for " + request.RequestUri) };
        }
    }

    private const string KeywordSuffix = ".keyword";

    /// <summary>True when the query aggregates a string of data by itself: the index keeps no field data for text, only for its keyword sub-field.</summary>
    private bool AggregatesText(JsonObject body)
        => body["aggregateBy"]?.GetValue<string>() is { } field
            && field.StartsWith("data.", StringComparison.Ordinal)
            && !field.EndsWith(KeywordSuffix, StringComparison.Ordinal)
            && Records.Any(r => JsonPathReader.SelectNodes(r, field).Any(v => v is JsonValue text && text.TryGetValue<string>(out _)));

    private JsonObject Query(JsonObject body)
    {
        var matched = Matching(body).ToList();
        var limit = Math.Min(body["limit"]?.GetValue<int>() ?? 10, PageCap);
        var fields = Fields(body);
        var result = new JsonObject
        {
            ["results"] = new JsonArray(matched.Take(limit).Select(r => (JsonNode?)Project(r, fields)).ToArray()),
            ["totalCount"] = matched.Count,
        };
        if (body["aggregateBy"]?.GetValue<string>() is { } aggregateBy)
        {
            var field = aggregateBy.EndsWith(KeywordSuffix, StringComparison.Ordinal) ? aggregateBy[..^KeywordSuffix.Length] : aggregateBy;
            var groups = matched.SelectMany(r => JsonPathReader.SelectNodes(r, field).Select(v => v is JsonValue text && text.TryGetValue<string>(out var s) ? s : v.ToJsonString()))
                .GroupBy(k => k, StringComparer.Ordinal)
                .Select(g => (JsonNode?)new JsonObject { ["key"] = g.Key, ["count"] = g.Count() })
                .ToArray();
            result["aggregations"] = new JsonArray(groups);
        }

        return result;
    }

    private JsonObject Cursor(JsonObject body)
    {
        var limit = Math.Min(body["limit"]?.GetValue<int>() ?? 10, PageCap);
        string handle;
        if (body["cursor"]?.GetValue<string>() is { } named)
        {
            handle = named;
        }
        else
        {
            handle = "cursor" + (++_cursorCount).ToString(CultureInfo.InvariantCulture);
            _cursors[handle] = (Matching(body).ToList(), 0, Fields(body));
        }

        var (hits, next, fields) = _cursors[handle];
        var page = hits.Skip(next).Take(limit).ToList();
        _cursors[handle] = (hits, next + page.Count, fields);
        var result = new JsonObject
        {
            ["results"] = new JsonArray(page.Select(r => (JsonNode?)Project(r, fields)).ToArray()),
            ["cursor"] = page.Count == 0 ? null : handle,
        };
        if (body["cursor"] is null)
        {
            result["totalCount"] = hits.Count;
        }

        return result;
    }

    private JsonObject Storage(JsonObject body)
    {
        var ids = (body["records"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
        var found = ids.Select(id => Records.FirstOrDefault(r => r["id"]!.GetValue<string>() == id && !NotStored.Contains(id))).Where(r => r is not null).ToList();
        return new JsonObject
        {
            ["records"] = new JsonArray(found.Select(r => (JsonNode?)r!.DeepClone()).ToArray()),
            ["invalidRecords"] = new JsonArray(ids.Where(id => !found.Any(r => r!["id"]!.GetValue<string>() == id)).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["retryRecords"] = new JsonArray(),
        };
    }

    private HttpResponseMessage BulkData(string id, System.Collections.Specialized.NameValueCollection query)
    {
        if (!Bulk.TryGetValue(id, out var bulk))
        {
            return FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"detail":"Record not found"}""");
        }

        if (query["describe"] == "true")
        {
            return Ok(new JsonObject
            {
                ["columns"] = new JsonArray(bulk.Columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["numberOfRows"] = DescribedRows.TryGetValue(id, out var described) ? described : bulk.Rows.Count,
            });
        }

        var columns = query["curves"] is { Length: > 0 } curves ? curves.Split(',') : bulk.Columns;
        var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
        var limit = int.Parse(query["limit"] ?? bulk.Rows.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var indexes = columns.Select(c => Array.IndexOf(bulk.Columns, c)).ToList();
        var rows = bulk.Rows.Skip(offset).Take(limit).ToList();
        return Ok(new JsonObject
        {
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["index"] = new JsonArray(Enumerable.Range(offset, rows.Count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            ["data"] = new JsonArray(rows.Select(row => (JsonNode?)new JsonArray(indexes.Select(i => Cell(row[i])).ToArray())).ToArray()),
        });
    }

    // The live DDMS writes a gap in a float column as the text "NaN".
    private static JsonNode? Cell(object? value) => value switch
    {
        null => JsonValue.Create("NaN"),
        double d => JsonValue.Create(d),
        int n => JsonValue.Create(n),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
    };

    private IEnumerable<JsonObject> Matching(JsonObject body)
    {
        var kind = body["kind"]!.GetValue<string>();
        var pattern = new Regex("^" + string.Concat(kind.Select(c => c == '*' ? "[^:]*" : Regex.Escape(c.ToString()))) + "$", RegexOptions.IgnoreCase);
        var query = body["query"]?.GetValue<string>();
        return Records.Where(r => pattern.IsMatch(r["kind"]!.GetValue<string>()) && Holds(r, query));
    }

    private static IReadOnlyList<string> Fields(JsonObject body) => (body["returnedFields"] as JsonArray ?? []).Select(f => f!.GetValue<string>()).ToList();

    /// <summary>A hit as the index returns it: the fields asked for, or the whole record, with the index block when asked.</summary>
    private JsonObject Project(JsonObject record, IReadOnlyList<string> fields)
    {
        if (fields.Count == 0)
        {
            return (JsonObject)record.DeepClone();
        }

        var hit = new JsonObject();
        foreach (var field in fields)
        {
            if (field == "index")
            {
                var id = record["id"]!.GetValue<string>();
                var status = IndexStatus.GetValueOrDefault(id, 200);
                hit["index"] = new JsonObject
                {
                    ["statusCode"] = status,
                    ["trace"] = status == 200 ? new JsonArray() : new JsonArray("Unable to parse the value of data.SequenceNumber"),
                    ["lastUpdateTime"] = "2026-09-01T10:00:01Z",
                };
                continue;
            }

            var segments = field.Split('.');
            JsonNode? source = record;
            var target = hit;
            for (var i = 0; i < segments.Length && source is JsonObject from; i++)
            {
                var value = from[segments[i]];
                if (value is null)
                {
                    break;
                }

                if (i == segments.Length - 1 || value is not JsonObject)
                {
                    target[segments[i]] = value.DeepClone();
                    break;
                }

                target = target[segments[i]] as JsonObject ?? (JsonObject)(target[segments[i]] = new JsonObject());
                source = value;
            }
        }

        return hit;
    }

    /// <summary>
    /// Whether a record meets a query: clauses joined by OR and AND (AND binding tighter) and grouped in parentheses;
    /// field:"value" (the phrase, ignoring case); field.keyword:"value" (the whole value, exactly); field:("a" OR "b") (any of
    /// them); field.keyword:esc\:aped\-prefix* (a keyword starting with the unescaped prefix); and index.statusCode:[low TO high].
    /// </summary>
    private bool Holds(JsonObject record, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var text = query.Trim();
        while (text.StartsWith('(') && Closing(text) == text.Length - 1)
        {
            text = text[1..^1].Trim();
        }

        var any = SplitTop(text, " OR ");
        if (any.Count > 1)
        {
            return any.Any(p => Holds(record, p));
        }

        var all = SplitTop(text, " AND ");
        if (all.Count > 1)
        {
            return all.All(p => Holds(record, p));
        }

        if (RangeClause().Match(text) is { Success: true } range)
        {
            if (range.Groups["field"].Value == "index.statusCode")
            {
                var status = IndexStatus.GetValueOrDefault(record["id"]!.GetValue<string>(), 200);
                var low = range.Groups["low"].Value == "*" ? int.MinValue : int.Parse(range.Groups["low"].Value, CultureInfo.InvariantCulture);
                var high = range.Groups["high"].Value == "*" ? int.MaxValue : int.Parse(range.Groups["high"].Value, CultureInfo.InvariantCulture);
                return status >= low && status <= high;
            }

            // A range of dates: [ and ] take their bound in, { and } leave it out.
            var from = range.Groups["low"].Value == "*" ? (DateTimeOffset?)null : DateTimeOffset.Parse(Unquoted(range.Groups["low"].Value), CultureInfo.InvariantCulture);
            var to = range.Groups["high"].Value == "*" ? (DateTimeOffset?)null : DateTimeOffset.Parse(Unquoted(range.Groups["high"].Value), CultureInfo.InvariantCulture);
            return JsonPathReader.SelectNodes(record, range.Groups["field"].Value).Any(v =>
                v is JsonValue value && value.TryGetValue<string>(out var t)
                && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && (from is null || (range.Groups["open"].Value == "[" ? at >= from : at > from))
                && (to is null || (range.Groups["close"].Value == "]" ? at <= to : at < to)));
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var field = text[..colon];
        var operand = text[(colon + 1)..].Trim();
        var exact = field.EndsWith(KeywordSuffix, StringComparison.Ordinal);
        if (exact)
        {
            field = field[..^KeywordSuffix.Length];
        }

        var held = JsonPathReader.SelectNodes(record, field).Select(v => v is JsonValue s && s.TryGetValue<string>(out var t) ? t : v.ToJsonString()).ToList();
        if (operand.StartsWith('(') && operand.EndsWith(')'))
        {
            var values = SplitTop(operand[1..^1], " OR ").Select(Unquoted).ToList();
            return held.Any(h => values.Any(v => Same(h, v, exact)));
        }

        if (!operand.StartsWith('"') && operand.EndsWith('*'))
        {
            var prefix = Unescaped(operand[..^1]);
            return held.Any(h => h.StartsWith(prefix, StringComparison.Ordinal));
        }

        var value = Unquoted(operand);
        return held.Any(h => Same(h, value, exact));
    }

    private static bool Same(string held, string value, bool exact)
        => string.Equals(held, value, exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>A phrase without its quotes and escapes, or a bare term without its escapes.</summary>
    private static string Unquoted(string term)
    {
        var text = term.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : Unescaped(text);
    }

    private static string Unescaped(string term)
    {
        var text = new StringBuilder(term.Length);
        for (var i = 0; i < term.Length; i++)
        {
            if (term[i] == '\\' && i + 1 < term.Length)
            {
                i++;
            }

            text.Append(term[i]);
        }

        return text.ToString();
    }

    private static int Closing(string text)
    {
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                depth += c switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>The parts of <paramref name="text"/> joined by <paramref name="op"/> outside quotes, parentheses and brackets.</summary>
    private static List<string> SplitTop(string text, string op)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '(' or '[')
            {
                depth++;
            }
            else if (!quoted && c is ')' or ']')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && string.CompareOrdinal(text, i, op, 0, op.Length) == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + op.Length;
                i += op.Length - 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private static HttpResponseMessage Ok(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    [GeneratedRegex(@"^/api/os-wellbore-ddms/ddms/v3/[a-z]+/(?<id>[^/]+)/data$")]
    private static partial Regex DataPath();

    [GeneratedRegex(@"^(?<field>[\w.]+):(?<open>[\[{])(?<low>\*|\d+|""[^""]*"") TO (?<high>\*|\d+|""[^""]*"")(?<close>[\]}])$")]
    private static partial Regex RangeClause();
}
