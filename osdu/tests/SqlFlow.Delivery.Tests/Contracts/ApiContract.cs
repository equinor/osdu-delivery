using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Tests;

/// <summary>One operation of a contract: its method, its path template and its definition.</summary>
internal sealed record ContractOperation(string Method, string Template, JsonObject Definition, JsonObject PathItem);

/// <summary>
/// A pinned OSDU API contract (OpenAPI 3.0 or 3.1, or Swagger 2.0) that checks the requests a delivery route sends: the
/// operation exists, every required parameter is there and every sent one is declared and valid, the content type is
/// one the operation accepts, and a JSON body matches the operation's schema. A request is matched on its path's end, so
/// a service mounted under a gateway prefix (<c>/api/os-wellbore-ddms</c>, a test host's own root) is matched as
/// deployed.
/// </summary>
internal sealed partial class ApiContract
{
    private readonly JsonObject _document;
    private readonly List<(ContractOperation Operation, Regex Pattern, int Literals)> _operations = [];
    // The contract is loaded once for the test process and checked by every test class running at once, each of which may note
    // a defect of it: the notes are kept under a lock and read as a copy.
    private readonly object _notesGate = new();
    private readonly List<string> _notes = [];
    private readonly SchemaCheck _schemas;

    private ApiContract(string name, string file, JsonObject document)
    {
        Name = name;
        File = file;
        _document = document;
        IsSwagger2 = document["swagger"] is not null;
        var openApi = document["openapi"]?.GetValue<string>() ?? string.Empty;
        _schemas = new SchemaCheck(document, nullableKeyword: IsSwagger2 || openApi.StartsWith("3.0", StringComparison.Ordinal), Note);
        BasePaths = ReadBasePaths(document);
        foreach (var (template, item) in document["paths"] as JsonObject ?? [])
        {
            if (item is not JsonObject pathItem)
            {
                continue;
            }

            foreach (var method in new[] { "get", "put", "post", "delete", "patch", "head", "options" })
            {
                if (pathItem[method] is JsonObject definition)
                {
                    var operation = new ContractOperation(method.ToUpperInvariant(), template, definition, pathItem);
                    foreach (var basePath in BasePaths)
                    {
                        var (pattern, literals) = Compile(basePath + template);
                        _operations.Add((operation, pattern, literals));
                    }
                }
            }
        }
    }

    /// <summary>The name the tests know the contract by (for example <c>core/storage</c>).</summary>
    public string Name { get; }

    public string File { get; }

    public bool IsSwagger2 { get; }

    /// <summary>The paths the operations are published under: the servers' paths (or the base path), and none.</summary>
    public IReadOnlyList<string> BasePaths { get; }

    /// <summary>Every operation the contract declares, once each.</summary>
    public IEnumerable<ContractOperation> Operations => _operations.Select(o => o.Operation).Distinct();

    /// <summary>What the contract itself could not answer while checking (a broken reference, an uncompilable pattern).</summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            lock (_notesGate)
            {
                return [.. _notes];
            }
        }
    }

    private void Note(string note)
    {
        lock (_notesGate)
        {
            _notes.Add(note);
        }
    }

    public static ApiContract Load(string name, string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var text = System.IO.File.ReadAllText(file);
        JsonNode? node;
        try
        {
            node = text.TrimStart().StartsWith('{') ? JsonNode.Parse(text) : YamlJson.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException($"The contract {file} does not parse: {ex.Message}", ex);
        }

        if (node is not JsonObject document)
        {
            throw new InvalidOperationException($"The contract {file} is not an object.");
        }

        Fill(document);
        return new ApiContract(name, file, document);
    }

    /// <summary>
    /// Reads every object and array of a parsed document once. A parsed node fills its children on its first read, and the
    /// contract is read by every test class running at once, so it is filled whole here, before anything shares it.
    /// </summary>
    private static void Fill(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o)
                {
                    Fill(child);
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    Fill(child);
                }

                break;
        }
    }

    /// <summary>
    /// The operation a request is for, preferring the template with the most literal characters (a base path counts, so
    /// a request under the service's own prefix matches it more strongly than a bare suffix does); null when none.
    /// </summary>
    public (ContractOperation Operation, Match Match, int Literals)? Find(string method, string absolutePath)
    {
        (ContractOperation Operation, Match Match, int Literals)? best = null;
        var bestLiterals = -1;
        foreach (var (operation, pattern, literals) in _operations)
        {
            if (!string.Equals(operation.Method, method, StringComparison.OrdinalIgnoreCase) || literals <= bestLiterals)
            {
                continue;
            }

            var match = pattern.Match(absolutePath);
            if (match.Success)
            {
                best = (operation, match, literals);
                bestLiterals = literals;
            }
        }

        return best;
    }

    /// <summary>Everything about the request the contract does not accept; empty when the request conforms.</summary>
    public IReadOnlyList<string> Check(FakeHttpHandler.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        var path = request.Uri.AbsolutePath;
        var found = Find(request.Method.Method, path);
        if (found is not { } hit)
        {
            violations.Add($"{request.Method} {path}: {Name} declares no such operation");
            return violations;
        }

        var (operation, match, _) = hit;
        var label = $"{operation.Method} {operation.Template}";
        var query = Query(request.Uri);
        var declaredQuery = new HashSet<string>(StringComparer.Ordinal);
        var bodyChecked = false;
        foreach (var parameter in Parameters(operation))
        {
            var name = parameter["name"]?.GetValue<string>() ?? string.Empty;
            var location = parameter["in"]?.GetValue<string>() ?? string.Empty;
            var required = parameter["required"] is JsonValue r && r.TryGetValue(out bool isRequired) && isRequired;
            var schema = ParameterSchema(parameter);
            switch (location)
            {
                case "path":
                    var value = match.Groups[GroupName(name)].Value;
                    _schemas.Validate(Typed(Uri.UnescapeDataString(value), schema), schema, $"{label} path parameter {name}", violations);
                    break;

                case "query":
                    declaredQuery.Add(name);
                    if (query.TryGetValue(name, out var values))
                    {
                        if (values.Count > 1 && ArrayOf(schema) is null)
                        {
                            violations.Add($"{label}: the query parameter '{name}' is sent {values.Count} times and takes one value");
                        }

                        foreach (var sent in QueryValues(values, parameter, schema))
                        {
                            _schemas.Validate(sent, schema, $"{label} query parameter {name}", violations);
                        }
                    }
                    else if (required)
                    {
                        violations.Add($"{label}: the required query parameter '{name}' is missing");
                    }

                    break;

                case "header":
                    // Authentication is the security scheme's business and is exercised by the auth suites.
                    if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (Header(request, name) is { } header)
                    {
                        _schemas.Validate(Typed(header, schema), schema, $"{label} header {name}", violations);
                    }
                    else if (required)
                    {
                        violations.Add($"{label}: the required header '{name}' is missing");
                    }

                    break;

                case "body":
                    bodyChecked = true;
                    CheckBody(request, parameter["schema"], required, label, violations);
                    break;

                case "formData":
                    bodyChecked = true;
                    if (required && string.IsNullOrEmpty(request.Body))
                    {
                        violations.Add($"{label}: the required form field '{name}' is missing");
                    }

                    break;
            }
        }

        foreach (var name in query.Keys.Where(k => !declaredQuery.Contains(k)))
        {
            violations.Add($"{label}: the query parameter '{name}' is not declared");
        }

        if (!IsSwagger2)
        {
            var requestBody = _schemas.Dereference(operation.Definition["requestBody"]);
            if (requestBody is not null)
            {
                bodyChecked = true;
                var required = requestBody["required"] is JsonValue r && r.TryGetValue(out bool isRequired) && isRequired;
                var content = requestBody["content"] as JsonObject ?? [];
                CheckContent(request, content, required, label, violations);
            }
        }
        else if (!string.IsNullOrEmpty(request.Body) && Consumes(operation) is { Count: > 0 } consumes && !consumes.Any(c => MediaMatches(c, request.ContentType)))
        {
            violations.Add($"{label}: the content type '{request.ContentType}' is not one of {string.Join(", ", consumes)}");
        }

        if (!bodyChecked && !string.IsNullOrEmpty(request.Body) && request.Method != HttpMethod.Get)
        {
            violations.Add($"{label}: the operation takes no body, and one was sent");
        }

        return violations;
    }

    private void CheckContent(FakeHttpHandler.Request request, JsonObject content, bool required, string label, List<string> violations)
    {
        if (string.IsNullOrEmpty(request.Body))
        {
            if (required)
            {
                violations.Add($"{label}: the required body is missing");
            }

            return;
        }

        var accepted = content.Select(c => c.Key).ToList();
        var media = accepted.Where(a => MediaMatches(a, request.ContentType)).OrderByDescending(a => a.Length).FirstOrDefault();
        if (media is null)
        {
            violations.Add($"{label}: the content type '{request.ContentType}' is not one of {string.Join(", ", accepted)}");
            return;
        }

        CheckBody(request, (content[media] as JsonObject)?["schema"], required, label, violations);
    }

    private void CheckBody(FakeHttpHandler.Request request, JsonNode? schema, bool required, string label, List<string> violations)
    {
        if (string.IsNullOrEmpty(request.Body))
        {
            if (required)
            {
                violations.Add($"{label}: the required body is missing");
            }

            return;
        }

        if (!IsJson(request.ContentType) || schema is null)
        {
            return;
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(request.Body);
        }
        catch (JsonException ex)
        {
            violations.Add($"{label}: the body is sent as {request.ContentType} and is not JSON ({ex.Message})");
            return;
        }

        _schemas.Validate(body, schema, $"{label} body", violations);
    }

    private List<JsonObject> Parameters(ContractOperation operation)
    {
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var source in new[] { operation.PathItem["parameters"], operation.Definition["parameters"] })
        {
            foreach (var entry in source as JsonArray ?? [])
            {
                if (_schemas.Dereference(entry) is { } parameter)
                {
                    byKey[$"{parameter["in"]}:{parameter["name"]}"] = parameter;
                }
            }
        }

        return [.. byKey.Values];
    }

    /// <summary>A parameter's schema: its <c>schema</c> (OpenAPI 3), or the parameter itself (Swagger 2.0 declares the type inline).</summary>
    private JsonNode? ParameterSchema(JsonObject parameter)
    {
        if (parameter["schema"] is { } schema)
        {
            return schema;
        }

        if (!IsSwagger2)
        {
            return null;
        }

        var inline = new JsonObject();
        foreach (var (key, value) in parameter)
        {
            if (key is not ("name" or "in" or "required" or "description" or "collectionFormat" or "allowEmptyValue"))
            {
                inline[key] = value?.DeepClone();
            }
        }

        return inline;
    }

    private List<string> Consumes(ContractOperation operation)
        => (operation.Definition["consumes"] ?? _document["consumes"]) is JsonArray consumes
            ? consumes.Select(c => c?.GetValue<string>() ?? string.Empty).ToList()
            : [];

    /// <summary>What a query parameter's sent values are to be checked as: one array, or each single value on its own.</summary>
    private IEnumerable<JsonNode?> QueryValues(List<string> values, JsonObject parameter, JsonNode? schema)
    {
        if (ArrayOf(schema) is { } array)
        {
            var delimiter = ItemDelimiter(parameter);
            var items = delimiter is null ? values : values.SelectMany(v => v.Split(delimiter));
            return [new JsonArray(items.Select(v => Typed(v, array["items"])).ToArray())];
        }

        return values.Select(v => Typed(v, schema));
    }

    /// <summary>The array alternative a parameter's schema allows, or null when it takes a single value.</summary>
    private JsonObject? ArrayOf(JsonNode? schema) => Alternatives(schema).FirstOrDefault(a => TypesOf(a).Contains("array"));

    /// <summary>
    /// How an array query parameter's items travel: as repeated parameters (null) or joined by a delimiter, from the
    /// parameter's <c>style</c> and <c>explode</c> (OpenAPI 3, where a form parameter explodes by default) or its
    /// <c>collectionFormat</c> (Swagger 2.0, comma-separated by default).
    /// </summary>
    private string? ItemDelimiter(JsonObject parameter)
    {
        if (IsSwagger2)
        {
            return (parameter["collectionFormat"]?.GetValue<string>() ?? "csv") switch
            {
                "multi" => null,
                "ssv" => " ",
                "tsv" => "\t",
                "pipes" => "|",
                _ => ",",
            };
        }

        var style = parameter["style"]?.GetValue<string>() ?? "form";
        var explode = parameter["explode"] is JsonValue e && e.TryGetValue(out bool exploded) ? exploded : style == "form";
        return explode ? null : style switch { "spaceDelimited" => " ", "pipeDelimited" => "|", _ => "," };
    }

    /// <summary>
    /// A text value as a type its schema allows, so a numeric or boolean parameter is checked as one. The types are read
    /// through the alternatives an optional parameter is often written with (<c>anyOf: [{type: boolean}, {type: 'null'}]</c>),
    /// in the order the schema lists them; a value none of them parses stays text and fails validation by name.
    /// </summary>
    private JsonNode? Typed(string value, JsonNode? schema)
    {
        foreach (var type in Alternatives(schema).SelectMany(TypesOf).Distinct(StringComparer.Ordinal))
        {
            switch (type)
            {
                case "integer" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l):
                    return JsonValue.Create(l);
                case "number" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d):
                    return JsonValue.Create(d);
                case "boolean" when bool.TryParse(value, out var b):
                    return JsonValue.Create(b);
                case "string":
                    return JsonValue.Create(value);
            }
        }

        return JsonValue.Create(value);
    }

    /// <summary>A schema and every schema its <c>anyOf</c>, <c>oneOf</c> and <c>allOf</c> members reach, dereferenced.</summary>
    private IEnumerable<JsonObject> Alternatives(JsonNode? schema)
    {
        var pending = new Stack<(JsonNode? Node, int Depth)>();
        pending.Push((schema, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (depth > 32 || _schemas.Dereference(node) is not JsonObject effective)
            {
                continue;
            }

            yield return effective;
            foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
            {
                if (effective[keyword] is JsonArray members)
                {
                    for (var i = members.Count - 1; i >= 0; i--)
                    {
                        pending.Push((members[i], depth + 1));
                    }
                }
            }
        }
    }

    private static IEnumerable<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue single when single.TryGetValue(out string? name) => [name],
        JsonArray many => many.Select(t => t is JsonValue v && v.TryGetValue(out string? name) ? name : null).OfType<string>(),
        _ => [],
    };

    private static string? Header(FakeHttpHandler.Request request, string name)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            return request.ContentType;
        }

        return request.Headers.TryGetValue(name, out var value) ? value : null;
    }

    private static Dictionary<string, List<string>> Query(Uri uri)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((at < 0 ? pair : pair[..at]).Replace('+', ' '));
            var value = at < 0 ? string.Empty : Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            if (!result.TryGetValue(name, out var values))
            {
                values = [];
                result[name] = values;
            }

            values.Add(value);
        }

        return result;
    }

    private static bool IsJson(string? contentType)
        => contentType is not null
           && (contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
               || contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a sent content type is one a declared media range accepts (<c>*/*</c>, <c>application/*</c>, or exact).</summary>
    private static bool MediaMatches(string declared, string? sent)
    {
        if (sent is null)
        {
            return false;
        }

        var range = declared.Split(';')[0].Trim();
        if (range == "*/*")
        {
            return true;
        }

        if (range.EndsWith("/*", StringComparison.Ordinal))
        {
            return sent.StartsWith(range[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return range.Equals(sent, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ReadBasePaths(JsonObject document)
    {
        var paths = new List<string>();
        if (document["basePath"] is JsonValue basePath && basePath.TryGetValue(out string? swaggerBase))
        {
            paths.Add(Normalize(swaggerBase));
        }

        foreach (var server in document["servers"] as JsonArray ?? [])
        {
            if (server?["url"] is JsonValue url && url.TryGetValue(out string? text))
            {
                paths.Add(Normalize(ServerPath(text)));
            }
        }

        paths.Add(string.Empty);
        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The path of a server URL, absolute (<c>https://host/api/x</c>), templated (<c>{scheme}://...</c>) or relative.</summary>
    private static string ServerPath(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return url;
        }

        var slash = url.IndexOf('/', scheme + 3);
        return slash < 0 ? string.Empty : url[slash..];
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Trim().TrimEnd('/');
        return trimmed.Length == 0 || trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>
    /// A path template as a pattern matched against the end of a request path: literals as written, each <c>{name}</c> one
    /// path segment's worth of text (it may share a segment with literals, as in <c>/records/{id}:delete</c>).
    /// </summary>
    private static (Regex Pattern, int Literals) Compile(string template)
    {
        var pattern = new StringBuilder("(?:^|(?<=.))");
        var literals = 0;
        var at = 0;
        foreach (Match parameter in TemplateParameter().Matches(template))
        {
            var literal = template[at..parameter.Index];
            pattern.Append(Regex.Escape(literal));
            literals += literal.Length;
            pattern.Append("(?<").Append(GroupName(parameter.Groups[1].Value)).Append(">[^/]+?)");
            at = parameter.Index + parameter.Length;
        }

        var tail = template[at..];
        pattern.Append(Regex.Escape(tail)).Append("/?$");
        literals += tail.Length;
        return (new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), literals);
    }

    /// <summary>A regex group name for a template parameter; parameter names may carry characters a group name cannot.</summary>
    private static string GroupName(string parameter)
        => "p" + string.Concat(parameter.Select(c => char.IsAsciiLetterOrDigit(c) ? c.ToString() : "_" + ((int)c).ToString("x", CultureInfo.InvariantCulture)));

    [GeneratedRegex(@"\{([^}/]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateParameter();
}
