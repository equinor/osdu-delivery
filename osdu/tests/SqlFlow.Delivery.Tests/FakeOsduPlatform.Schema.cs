using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Schema service of the fake platform (openapi schema_service): <c>GET /schema/{id}</c> answers the schemas it holds
/// as they were put, a 404 for any other, and an answer of raw text or a failure where a test needs one; <c>GET /schema</c>
/// lists them as <c>SchemaInfo</c>, one status and scope at a time (published and internal when the listing names neither,
/// as the specification defaults them), a page of <c>limit</c> from <c>offset</c>, in id order.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    private const string SchemaList = "/api/schema-service/v1/schema";
    private const string SchemaRoot = SchemaList + "/";

    /// <summary>The schemas the service holds, by schema id (<c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    public Dictionary<string, JsonObject> Schemas { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers the service gives as raw text for an id, in place of a schema.</summary>
    public Dictionary<string, string> SchemaTexts { get; } = new(StringComparer.Ordinal);

    /// <summary>The status and scope each schema is listed in; a schema not named here is published and shared.</summary>
    public Dictionary<string, (string Status, string Scope)> SchemaInfos { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads of a schema by id answered with a failure of this status instead.</summary>
    public Dictionary<string, HttpStatusCode> SchemaFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>Listings answered with a refusal of this status instead, by <c>STATUS SCOPE</c> (<c>PUBLISHED SHARED</c>).</summary>
    public Dictionary<string, HttpStatusCode> SchemaListingRefusals { get; } = new(StringComparer.Ordinal);

    /// <summary>The reads of one schema by id the service answered, of a schema it holds or not.</summary>
    public int SchemaReads(string id)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.Method == HttpMethod.Get && Uri.UnescapeDataString(c.Uri.AbsolutePath) == SchemaRoot + id);
        }
    }

    /// <summary>The listings of the service, one per page asked.</summary>
    public IReadOnlyList<Uri> SchemaListings()
    {
        lock (_gate)
        {
            return Calls.Where(c => c.Method == HttpMethod.Get && c.Uri.AbsolutePath == SchemaList).Select(c => c.Uri).ToList();
        }
    }

    /// <summary>The token endpoint of the platform's identity provider: a client credentials grant answered with a token.</summary>
    public const string TokenPath = "/oauth2/token";

    /// <summary>Writes refused with this status and message instead, by schema id: an entitlement, or a breaking change the service found.</summary>
    public Dictionary<string, (HttpStatusCode Status, string Message)> SchemaWriteRefusals { get; } = new(StringComparer.Ordinal);

    /// <summary>Writes the service takes and a gateway answers with a 504 once, by schema id: an answer lost on the way, which a client sends again.</summary>
    public HashSet<string> SchemaWriteLostAnswers { get; } = new(StringComparer.Ordinal);

    /// <summary>The body of every <c>PUT /schema</c> the service was sent, in order.</summary>
    public IReadOnlyList<JsonObject> SchemaWrites()
    {
        lock (_gate)
        {
            return Calls.Where(c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == SchemaList && c.Body is not null)
                .Select(c => (JsonObject)JsonNode.Parse(c.Body!)!)
                .ToList();
        }
    }

    private HttpResponseMessage? SchemaServiceRoute(string method, string path, Uri uri, string? body)
    {
        if (method == "POST" && path == TokenPath)
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = "fake-platform-token", ["token_type"] = "Bearer", ["expires_in"] = 3600 });
        }

        if (method == "PUT" && path == SchemaList)
        {
            return SchemaWrite(body);
        }

        if (method != "GET")
        {
            return null;
        }

        if (path == SchemaList)
        {
            return SchemaListing(uri);
        }

        if (!path.StartsWith(SchemaRoot, StringComparison.Ordinal))
        {
            return null;
        }

        var id = Uri.UnescapeDataString(path[SchemaRoot.Length..]);
        if (SchemaFailures.TryGetValue(id, out var failure))
        {
            return Json(failure, new JsonObject { ["code"] = (int)failure, ["reason"] = "Failed", ["message"] = $"Schema {id} could not be read" });
        }

        if (SchemaTexts.TryGetValue(id, out var text))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }

        return Schemas.TryGetValue(id, out var schema)
            ? Json(HttpStatusCode.OK, (JsonObject)schema.DeepClone())
            : Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = 404, ["reason"] = "Not found", ["message"] = $"Schema {id} is not present" });
    }

    /// <summary>
    /// <c>PUT /schema</c> (upsertSchema) as the service's own code answers it (schema-service SchemaService.java, update and
    /// create; SchemaResolver.java): a schema the partition holds in development is replaced, in the status asked; one it
    /// holds published or obsolete is refused; a new one is created in development only, and refused when the platform's
    /// shared schemas hold the id. A local reference must end in the name of a top-level definition. What is stored is the
    /// schema re-serialised, members holding null dropped and keys reordered, as the service's serialisers leave it.
    /// </summary>
    private HttpResponseMessage SchemaWrite(string? body)
    {
        if ((body is null ? null : JsonNode.Parse(body)) is not JsonObject request
            || request["schemaInfo"] is not JsonObject info
            || info["schemaIdentity"] is not JsonObject identity
            || info["status"]?.GetValue<string>() is not { } status
            || new[] { "authority", "source", "entityType", "schemaVersionMajor", "schemaVersionMinor", "schemaVersionPatch" }.Any(k => identity[k] is null))
        {
            return SchemaError(HttpStatusCode.BadRequest, "The input is invalid. schemaInfo.schemaIdentity and schemaInfo.status are required");
        }

        if (request["schema"] is not JsonObject schema)
        {
            return SchemaError(HttpStatusCode.BadRequest, "The input is invalid. Schema is not a valid JSON Object");
        }

        var id = string.Create(
            CultureInfo.InvariantCulture,
            $"{identity["authority"]}:{identity["source"]}:{identity["entityType"]}:{identity["schemaVersionMajor"]}.{identity["schemaVersionMinor"]}.{identity["schemaVersionPatch"]}");
        if (SchemaWriteRefusals.TryGetValue(id, out var refusal))
        {
            return SchemaError(refusal.Status, refusal.Message);
        }

        if (UnresolvedReference(schema) is { } unresolved)
        {
            return SchemaError(HttpStatusCode.BadRequest, unresolved);
        }

        var held = Schemas.ContainsKey(id);
        var (heldStatus, heldScope) = held && SchemaInfos.TryGetValue(id, out var listed) ? listed : ("PUBLISHED", "SHARED");
        HttpStatusCode answered;
        if (held && heldScope == "INTERNAL")
        {
            if (heldStatus != "DEVELOPMENT")
            {
                return SchemaError(HttpStatusCode.BadRequest, "Only schema in developement stage can be updated");
            }

            answered = HttpStatusCode.OK;
        }
        else if (status != "DEVELOPMENT")
        {
            return SchemaError(HttpStatusCode.BadRequest, "Only schema in developement stage can be created through put");
        }
        else if (held)
        {
            return SchemaError(HttpStatusCode.BadRequest, "Update/Create failed because schema id is present in another tenant");
        }
        else
        {
            answered = HttpStatusCode.Created;
        }

        Schemas[id] = Reserialized(schema);
        SchemaInfos[id] = (status, "INTERNAL");
        if (SchemaWriteLostAnswers.Remove(id))
        {
            return SchemaError(HttpStatusCode.GatewayTimeout, "The gateway gave up waiting for the answer");
        }

        return Json(answered, new JsonObject
        {
            ["schemaIdentity"] = identity.DeepClone(),
            ["createdBy"] = "fake@example.com",
            ["dateCreated"] = "2026-01-01T00:00:00Z",
            ["status"] = status,
            ["scope"] = "INTERNAL",
        });
    }

    /// <summary>The first reference the service would refuse, as it says so; null when it takes them all.</summary>
    private static string? UnresolvedReference(JsonObject schema)
    {
        var definitions = schema["definitions"] as JsonObject;
        var pending = new Stack<JsonNode?>([schema]);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case JsonObject node:
                    foreach (var (name, value) in node)
                    {
                        if (name == "$ref" && value is JsonValue reference && reference.TryGetValue<string>(out var text))
                        {
                            if (text.StartsWith('#') && definitions?.ContainsKey(text[(text.LastIndexOf('/') + 1)..]) != true)
                            {
                                return $"Invalid input, no '{text[(text.LastIndexOf('/') + 1)..]}' definition found but provided as a reference";
                            }

                            if (!text.StartsWith('#') && !text.StartsWith("https", StringComparison.Ordinal))
                            {
                                return $"Invalid input, {text} not registered but provided as reference";
                            }
                        }
                        else
                        {
                            pending.Push(value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var element in array)
                    {
                        pending.Push(element);
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>A schema as the service stores it: members holding null dropped, and its keys in reverse order.</summary>
    private static JsonObject Reserialized(JsonObject schema)
    {
        static JsonNode? Copy(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Where(p => p.Value is not null).Reverse().Select(p => KeyValuePair.Create(p.Key, Copy(p.Value)))),
            JsonArray array => new JsonArray([.. array.Select(Copy)]),
            _ => node?.DeepClone(),
        };

        return (JsonObject)Copy(schema)!;
    }

    /// <summary>A refusal as the Schema service writes one: <c>{"error":{"code","message","errors"}}</c>.</summary>
    private static HttpResponseMessage SchemaError(HttpStatusCode status, string message)
        => Json(status, new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = (int)status, ["message"] = message, ["errors"] = new JsonArray() },
        });

    private HttpResponseMessage SchemaListing(Uri uri)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        var status = (query["status"] ?? "PUBLISHED").ToUpperInvariant();
        var scope = (query["scope"] ?? "INTERNAL").ToUpperInvariant();
        if (SchemaListingRefusals.TryGetValue($"{status} {scope}", out var refusal))
        {
            return Json(refusal, new JsonObject { ["code"] = (int)refusal, ["reason"] = "Refused", ["message"] = $"Listing {status} {scope} schemas is refused" });
        }

        var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
        var limit = int.Parse(query["limit"] ?? "100", CultureInfo.InvariantCulture);
        var held = Schemas.Keys.Concat(SchemaTexts.Keys).Concat(SchemaFailures.Keys).Distinct(StringComparer.Ordinal)
            .Where(id => SchemaInfos.TryGetValue(id, out var info) ? info.Status == status && info.Scope == scope : status == "PUBLISHED" && scope == "SHARED")
            .Order(StringComparer.Ordinal)
            .ToList();
        var page = held.Skip(offset).Take(limit).ToList();
        var infos = new JsonArray();
        foreach (var id in page)
        {
            var parts = id.Split(':');
            var version = parts[3].Split('.').Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            infos.Add(new JsonObject
            {
                ["schemaIdentity"] = new JsonObject
                {
                    ["authority"] = parts[0],
                    ["source"] = parts[1],
                    ["entityType"] = parts[2],
                    ["schemaVersionMajor"] = version[0],
                    ["schemaVersionMinor"] = version[1],
                    ["schemaVersionPatch"] = version[2],
                    ["id"] = id,
                },
                ["createdBy"] = "fake@example.com",
                ["dateCreated"] = "2026-01-01T00:00:00Z",
                ["status"] = status,
                ["scope"] = scope,
            });
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["schemaInfos"] = infos, ["offset"] = offset, ["count"] = page.Count, ["totalCount"] = held.Count });
    }
}
