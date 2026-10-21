using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>A kind's schema as a partition's Schema service holds it, bundled, with what bundling it could not resolve.</summary>
/// <param name="Schema">The schema, every reference the service could answer for made local; null when the service holds no schema of the kind.</param>
/// <param name="Read">The schema ids read from the service: the kind's, then each one it refers to, in the order they were read.</param>
/// <param name="Unresolved">The references left as they were, each with why: a schema the service does not hold, a reference of a form the reader does not follow, or a bound reached.</param>
public sealed record SchemaServiceRead(SchemaSnapshot? Schema, IReadOnlyList<string> Read, IReadOnlyList<string> Unresolved);

/// <summary>One schema the Schema service lists (openapi schema_service, <c>SchemaInfo</c>): its id, the parts of its identity, its status and scope.</summary>
/// <param name="Id">The schema id, <c>authority:source:entityType:major.minor.patch</c>: the kind of the records it describes.</param>
/// <param name="Authority">The authority (<c>osdu</c>).</param>
/// <param name="Source">The source (<c>wks</c>).</param>
/// <param name="EntityType">The entity type (<c>master-data--Wellbore</c>, or <c>AbstractFacility</c> for a schema no record is of).</param>
/// <param name="Version">The version, <c>major.minor.patch</c>.</param>
/// <param name="Status"><c>PUBLISHED</c>, <c>DEVELOPMENT</c> or <c>OBSOLETE</c>.</param>
/// <param name="Scope"><c>INTERNAL</c> (the partition's own) or <c>SHARED</c> (the platform's).</param>
public sealed record SchemaListing(string Id, string Authority, string Source, string EntityType, string Version, string Status, string Scope)
{
    /// <summary>Whether records are of this schema's kind: its entity type names a group (<c>master-data--Wellbore</c>), as an abstract schema's does not.</summary>
    public bool IsKind => EntityType.Contains("--", StringComparison.Ordinal);

    /// <summary>Whether the service keeps the schema as it is: a published or obsolete schema never changes, one in development may.</summary>
    public bool Settled => !string.Equals(Status, SchemaServiceReader.Development, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The schemas one listing of the service answered, and how many of its entries named no schema.</summary>
public sealed record SchemaServiceListing(IReadOnlyList<SchemaListing> Schemas, int Malformed);

/// <summary>
/// The schemas a pass over many kinds reads by reference, each read from the service once and shared by every bundle that
/// refers to it (<c>osdu:wks:AbstractCommonResources:1.0.0</c> is referred to by nearly every kind). A schema the service
/// holds none of is kept as such; a read that fails is not kept, so the next bundle that refers to it reads it again.
/// </summary>
public sealed class SchemaServiceTexts
{
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _texts = new(StringComparer.Ordinal);

    /// <summary>How many schemas are kept.</summary>
    public int Count => _texts.Count;

    /// <summary>The text of the schema <paramref name="id"/>, as the service answered it: the one kept, or <paramref name="read"/>'s, kept from now on; null when the service holds none.</summary>
    internal async Task<byte[]?> GetAsync(string id, Func<Task<byte[]?>> read)
    {
        var held = _texts.GetOrAdd(id, _ => new Lazy<Task<byte[]?>>(read, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await held.Value.ConfigureAwait(false);
        }
        catch
        {
            _texts.TryRemove(KeyValuePair.Create(id, held));
            throw;
        }
    }
}

/// <summary>
/// Reads a kind's schema from the Schema service (openapi schema_service, <c>GET /schema/{id}</c>) and bundles it as a
/// template file is bundled: a reference to another schema by its id (<c>osdu:wks:AbstractCommonResources:1.0.0</c>,
/// with or without a fragment into it) is read from the service too and placed among the bundle's definitions, so every
/// reference the bundle holds is local. The OpenAPI description does not say whether the service answers with references
/// already resolved; a schema that comes back bundled needs nothing more, and one that does not is bundled here.
/// </summary>
/// <remarks>
/// <para>
/// Each schema is read once per bundle; a schema that refers back to one already read is answered from what was read. A
/// definition another schema names the same and holds differently is kept under a name of its own. A reference of a form
/// the reader does not follow (a web address, a pointer into a part of another schema other than its definitions) is left
/// as it is and named, and a check of a value it describes reports that part as not checked. The bundle is bounded:
/// <see cref="MaxSchemas"/> schemas at most. Given a <see cref="SchemaServiceTexts"/>, the schemas a kind refers to are read
/// through it, so a pass over every kind of a partition reads each of them once.
/// </para>
/// <para>
/// It also lists the schemas the service holds (<c>GET /schema</c>), one status and scope at a time, a page of
/// <see cref="ListPage"/> after another; a listing carries no schema's content.
/// </para>
/// </remarks>
public sealed partial class SchemaServiceReader
{
    /// <summary>The Schema service's read of one schema by its id.</summary>
    public const string DefaultPath = "/api/schema-service/v1/schema";

    /// <summary>The most schemas one bundle reads: the kind's and the ones it refers to, however deep.</summary>
    public const int MaxSchemas = 200;

    /// <summary>The most schemas one page of a listing holds (openapi schema_service, <c>limit</c> maximum 100).</summary>
    public const int ListPage = 100;

    /// <summary>The most schemas one listing reads, a bound no partition's Schema service comes near.</summary>
    public const int MaxListed = 50_000;

    public const string Published = "PUBLISHED";
    public const string Development = "DEVELOPMENT";
    public const string Obsolete = "OBSOLETE";

    /// <summary>Every status a schema can be in (<c>SchemaInfo.status</c>); a listing names one, published when it names none.</summary>
    public static IReadOnlyList<string> Statuses { get; } = [Published, Development, Obsolete];

    /// <summary>Every scope a schema can be in (<c>SchemaInfo.scope</c>); a listing names one, internal when it names none.</summary>
    public static IReadOnlyList<string> Scopes { get; } = ["INTERNAL", "SHARED"];

    private readonly OsduHttpClient _client;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SchemaServiceTexts? _texts;

    public SchemaServiceReader(OsduHttpClient client, TimeProvider time, string? path = null, SchemaServiceTexts? texts = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        _client = client;
        _time = time;
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.TrimEnd('/');
        _texts = texts;
    }

    /// <summary>
    /// The schemas the service lists in <paramref name="status"/> and <paramref name="scope"/>, every page of them, each once.
    /// A listing answered with 404 holds none; any other refusal throws. A page holding no schema not listed already means
    /// the service pages no further, and the listing ends there.
    /// </summary>
    public async Task<SchemaServiceListing> ListAsync(string status, string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var listed = new List<SchemaListing>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var malformed = 0;
        for (var offset = 0; ;)
        {
            ct.ThrowIfCancellationRequested();
            var url = OsduHttpClient.WithQuery(OsduHttpClient.WithQuery(_client.Url(_path), "status", status), "scope", scope);
            url = OsduHttpClient.WithQuery(url, "limit", ListPage.ToString(CultureInfo.InvariantCulture));
            url = OsduHttpClient.WithQuery(url, "offset", offset.ToString(CultureInfo.InvariantCulture));
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
            if ((int)result.Status == 404)
            {
                break;
            }

            var infos = Infos(result.Body);
            var fresh = 0;
            foreach (var info in infos)
            {
                if (Info(info, status, scope) is not { } schema)
                {
                    malformed++;
                }
                else if (seen.Add(schema.Id))
                {
                    listed.Add(schema);
                    fresh++;
                }
            }

            if (infos.Count < ListPage || fresh == 0)
            {
                break;
            }

            if (listed.Count >= MaxListed)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"The Schema service lists more than {MaxListed} {status} {scope} schemas, more than a listing reads."));
            }

            offset += infos.Count;
        }

        return new SchemaServiceListing(listed, malformed);
    }

    /// <summary>The entries of one page of a listing (<c>SchemaInfoResponse.schemaInfos</c>).</summary>
    private static JsonArray Infos(byte[] body)
    {
        JsonNode? page;
        try
        {
            page = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The Schema service listed its schemas in text that is not JSON: {ex.Message}", ex);
        }

        return page is JsonObject { } answer && answer["schemaInfos"] is JsonArray infos
            ? infos
            : throw new DeliveryException("The Schema service listed its schemas in an answer that holds no schemaInfos.");
    }

    /// <summary>One <c>SchemaInfo</c> as a listing, its id written from its parts where the service leaves it out; null when it names no schema.</summary>
    private static SchemaListing? Info(JsonNode? entry, string status, string scope)
    {
        if (entry is not JsonObject info || info["schemaIdentity"] is not JsonObject identity)
        {
            return null;
        }

        var (authority, source, entityType) = (Text(identity, "authority"), Text(identity, "source"), Text(identity, "entityType"));
        var (major, minor, patch) = (Whole(identity, "schemaVersionMajor"), Whole(identity, "schemaVersionMinor"), Whole(identity, "schemaVersionPatch"));
        var id = Text(identity, "id")
            ?? (authority is null || source is null || entityType is null || major is null || minor is null || patch is null
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{authority}:{source}:{entityType}:{major}.{minor}.{patch}"));
        if (id is null || !SchemaId().IsMatch(id))
        {
            return null;
        }

        var parts = id.Split(':');
        return new SchemaListing(
            id, parts[0], parts[1], parts[2], parts[3],
            (Text(info, "status") ?? status).ToUpperInvariant(),
            (Text(info, "scope") ?? scope).ToUpperInvariant());
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static long? Whole(JsonObject node, string key)
        => node[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var whole) ? whole : null;

    /// <summary>
    /// The schema of <paramref name="kind"/>, bundled; its <see cref="SchemaServiceRead.Schema"/> is null when the service
    /// holds no schema of the kind. A refusal other than a missing schema (403, 5xx) throws, so a reader sees the service's
    /// answer rather than a schema that is merely absent.
    /// </summary>
    public async Task<SchemaServiceRead> ReadAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var root = await FetchAsync(kind.Trim(), ct).ConfigureAwait(false);
        if (root is null)
        {
            return new SchemaServiceRead(null, [], []);
        }

        var bundle = new Bundle(kind.Trim(), root);
        var queue = new Queue<(string Id, JsonObject Schema, string? Prefix)>();
        queue.Enqueue((kind.Trim(), root, null));
        var read = new List<string> { kind.Trim() };
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, schema, prefix) = queue.Dequeue();
            // Each id Rewrite answers is one it has just named and no earlier schema asked for, so each is read once.
            foreach (var target in bundle.Rewrite(id, schema, prefix))
            {
                if (read.Count >= MaxSchemas)
                {
                    bundle.Unresolve(target, $"'{target}' was not read: a bundle reads at most {MaxSchemas} schemas");
                    continue;
                }

                var fetched = await ReferredAsync(target, ct).ConfigureAwait(false);
                read.Add(target);
                if (fetched is null)
                {
                    bundle.Unresolve(target, $"the Schema service holds no schema '{target}'");
                    continue;
                }

                var name = bundle.Adopt(target, fetched);
                queue.Enqueue((target, fetched, name));
            }
        }

        var snapshot = new SchemaSnapshot(kind.Trim(), bundle.Root, _time.GetUtcNow());
        return new SchemaServiceRead(snapshot, read, bundle.Unresolved);
    }

    /// <summary>A schema as the service answers for <paramref name="id"/>, or null when it holds none (404).</summary>
    private async Task<JsonObject?> FetchAsync(string id, CancellationToken ct)
        => Parse(id, await FetchTextAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// A schema a bundle refers to, read through the texts a pass shares where it is given them; each is parsed afresh, since
    /// a bundle rewrites the schemas it adopts.
    /// </summary>
    private async Task<JsonObject?> ReferredAsync(string id, CancellationToken ct)
        => _texts is null
            ? await FetchAsync(id, ct).ConfigureAwait(false)
            : Parse(id, await _texts.GetAsync(id, () => FetchTextAsync(id, ct)).ConfigureAwait(false));

    /// <summary>The service's answer for <paramref name="id"/> as it came, or null when it holds none (404).</summary>
    private async Task<byte[]?> FetchTextAsync(string id, CancellationToken ct)
    {
        var result = await _client.SendJsonAsync(HttpMethod.Get, _client.Url(_path + "/{id}", id), null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        return (int)result.Status == 404 ? null : result.Body;
    }

    private static JsonObject? Parse(string id, byte[]? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new DeliveryException($"The Schema service answered for '{id}' with something other than a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The Schema service answered for '{id}' with text that is not JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The bundle being made: the kind's schema, its definitions gaining every schema it refers to, and every reference
    /// rewritten to the definition it now names.
    /// </summary>
    private sealed class Bundle
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _unresolved = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<JsonObject>> _hoisted = new(StringComparer.Ordinal);
        private readonly JsonObject _definitions;

        public Bundle(string kind, JsonObject root)
        {
            Root = root;

            // Draft 2019 writes $defs where draft-07 writes definitions: the bundle keeps one, as a template file does.
            if (root["definitions"] is null && root["$defs"] is JsonObject defs)
            {
                root.Remove("$defs");
                root["definitions"] = defs;
            }

            _definitions = root["definitions"] as JsonObject ?? new JsonObject();
            root["definitions"] = _definitions;
            _names[kind] = string.Empty;
        }

        public JsonObject Root { get; }

        public IReadOnlyList<string> Unresolved => _unresolved.Values.ToList();

        /// <summary>Whether <paramref name="id"/> was read already, or was found not to be readable.</summary>
        public bool Has(string id) => _names.ContainsKey(id) || _unresolved.ContainsKey(id);

        public void Unresolve(string id, string why) => _unresolved.TryAdd(id, why);

        /// <summary>
        /// Places a schema read for <paramref name="id"/> among the definitions, its own definitions with it, under a name no
        /// other definition holds differently; answers the prefix its own local references are rewritten with.
        /// </summary>
        public string Adopt(string id, JsonObject schema)
        {
            // The name was reserved when the first reference to the schema was rewritten; the schema takes it.
            var name = _names.TryGetValue(id, out var reserved) && reserved.Length > 0 ? reserved : Free(DefinitionName(id), schema);
            _names[id] = name;
            var hoisted = new List<JsonObject>();
            _hoisted[id] = hoisted;

            if ((schema["definitions"] ?? schema["$defs"]) is JsonObject inner)
            {
                schema.Remove("definitions");
                schema.Remove("$defs");
                foreach (var (key, value) in inner.ToList())
                {
                    inner.Remove(key);
                    if (value is not JsonObject definition)
                    {
                        continue;
                    }

                    var placed = Free(key, definition);
                    if (_definitions[placed] is null)
                    {
                        _definitions[placed] = definition;
                        hoisted.Add(definition);
                    }

                    _names[$"{id}#/definitions/{key}"] = placed;
                }
            }

            _definitions[name] = schema;
            return name;
        }

        /// <summary>
        /// Rewrites every reference inside <paramref name="schema"/> to a local one, and answers the schema ids it refers to
        /// that are not read yet. <paramref name="prefix"/> is null for the kind's own schema.
        /// </summary>
        public IReadOnlyList<string> Rewrite(string id, JsonObject schema, string? prefix)
        {
            var wanted = new List<string>();
            var parts = _hoisted.TryGetValue(id, out var hoisted) ? hoisted.Prepend(schema) : [schema];
            foreach (var node in parts.SelectMany(Walk))
            {
                if (node["$ref"] is not JsonValue value || !value.TryGetValue<string>(out var reference))
                {
                    continue;
                }

                var local = Local(id, prefix, reference, wanted);
                if (local is not null)
                {
                    node["$ref"] = local;
                }
            }

            return wanted;
        }

        /// <summary>The local reference <paramref name="reference"/> becomes, or null to leave it as it is.</summary>
        private string? Local(string id, string? prefix, string reference, List<string> wanted)
        {
            if (reference.StartsWith("#/definitions/", StringComparison.Ordinal) || reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                // A reference into the schema's own definitions: the kind's are where they were; an adopted schema's moved.
                if (prefix is null)
                {
                    return reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? "#/definitions/" + reference["#/$defs/".Length..] : null;
                }

                var key = reference[(reference.IndexOf('/', 2) + 1)..];
                return _names.TryGetValue($"{id}#/definitions/{key}", out var placed) ? "#/definitions/" + placed : null;
            }

            if (reference == "#")
            {
                return prefix is null ? null : "#/definitions/" + prefix;
            }

            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            var target = hash < 0 ? reference : reference[..hash];
            var fragment = hash < 0 ? string.Empty : reference[hash..];
            if (target.Length == 0)
            {
                Unresolve(reference, $"the reference '{reference}' points into the schema itself other than its definitions, which the checks do not follow");
                return null;
            }

            if (!SchemaId().IsMatch(target))
            {
                Unresolve(reference, $"the reference '{reference}' is not to a schema by its id, which the reader does not follow");
                return null;
            }

            if (fragment.Length > 1 && !fragment.StartsWith("#/definitions/", StringComparison.Ordinal))
            {
                Unresolve(reference, $"the reference '{reference}' points into a part of '{target}' other than its definitions, which the reader does not follow");
                return null;
            }

            if (!Has(target))
            {
                wanted.Add(target);

                // Named now and placed when it is read; a schema that turns out not to be held leaves the reference dangling,
                // which a check reports as not checked.
                _names[target] = Free(DefinitionName(target), null);
            }

            if (fragment.Length > 1)
            {
                var key = fragment["#/definitions/".Length..];
                return "#/definitions/" + (_names.TryGetValue($"{target}#/definitions/{key}", out var placed) ? placed : key);
            }

            // The kind's own schema is the bundle's root.
            return _names.TryGetValue(target, out var name) ? (name.Length > 0 ? "#/definitions/" + name : "#") : null;
        }

        /// <summary>A definition name for <paramref name="wanted"/> that no other definition holds differently.</summary>
        private string Free(string wanted, JsonObject? content)
        {
            var name = wanted;
            for (var i = 2; _definitions[name] is JsonObject held && (content is null || !JsonNode.DeepEquals(held, content)); i++)
            {
                name = $"{wanted}~{i}";
            }

            return name;
        }

        /// <summary>How a schema id is named among the definitions, as the data definitions name their bundled ones.</summary>
        private static string DefinitionName(string id)
        {
            var parts = id.Split(':');
            return parts.Length == 4 ? $"{parts[2]}.{parts[3]}" : id.Replace(':', '.');
        }

        private static IEnumerable<JsonObject> Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    yield return obj;
                    foreach (var (_, child) in obj.ToList())
                    {
                        foreach (var inner in Walk(child))
                        {
                            yield return inner;
                        }
                    }

                    break;
                case JsonArray list:
                    foreach (var item in list.ToList())
                    {
                        foreach (var inner in Walk(item))
                        {
                            yield return inner;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>A schema id: <c>authority:source:entity:major.minor.patch</c>.</summary>
    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaId();
}
