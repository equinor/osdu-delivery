using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Reads and replaces records' access through the storage service (osdu/docs/reference/concepts/access-restriction.md;
/// osdu/specs/core/INTEGRATION.md, 2.3 and W4). The ACL is replaced with storage's metadata patch, <c>PATCH /records</c>
/// with an <c>application/json</c> body of up to <see cref="MaxIds"/> ids and two <c>replace</c> operations, on
/// <c>/acl/viewers</c> and <c>/acl/owners</c>: the record keeps its version, every version is governed by the new ACL, and
/// storage publishes the change so the search index follows. Storage checks owner access per record and answers 206 with
/// the ids it did not change (not found, not the caller's to change, locked); a request it refused whole (an ACL it will not
/// take, a role the caller lacks) changed nothing. The current ACL is read with the batched read every route verifies with,
/// which answers each record with the ACL storage holds for it now, and the caller's groups with entitlements'
/// <c>GET /groups</c>.
/// </summary>
public sealed class StorageRecordAccess : IRecordAccess
{
    /// <summary>The most ids one metadata patch takes (osdu/specs/core/storage/openapi.yaml, <c>RecordQuery.ids</c>, maxItems 500).</summary>
    public const int MaxIds = 500;

    /// <summary>Entitlements' list of the caller's groups (osdu/specs/core/INTEGRATION.md, 2.8).</summary>
    public const string DefaultGroupsPath = "/api/entitlements/v2/groups";

    private readonly OsduHttpClient _client;
    private readonly string _patchPath;
    private readonly string _batchPath;
    private readonly string _groupsPath;

    /// <param name="client">The flow's client, under its endpoint and credentials.</param>
    /// <param name="patchPath">Storage's record path, which takes the metadata patch.</param>
    /// <param name="batchPath">Storage's batched read.</param>
    /// <param name="groupsPath">Entitlements' list of the caller's groups.</param>
    public StorageRecordAccess(OsduHttpClient client, string patchPath, string batchPath, string groupsPath = DefaultGroupsPath)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupsPath);
        _client = client;
        _patchPath = patchPath;
        _batchPath = batchPath;
        _groupsPath = groupsPath;
    }

    public string ReplaceRequest => $"PATCH {_patchPath} (acl.viewers and acl.owners replaced in place; no new version)";

    public async Task<IReadOnlyDictionary<string, RecordAccessRead>> ReadAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // A projection that matches no data field: the read carries the record's header (its id, version and ACL), not its data.
        var stored = await RecordWriter.ReadManyAsync(_client, _batchPath, ids, ["id"], ct).ConfigureAwait(false);
        var read = new Dictionary<string, RecordAccessRead>(StringComparer.Ordinal);
        foreach (var (id, record) in stored)
        {
            if (RecordAcl.OfRecord(record) is { } acl)
            {
                var version = record["version"] is JsonValue value && value.TryGetValue<long>(out var number) ? number : (long?)null;
                read[id] = new RecordAccessRead(version, acl);
            }
        }

        return read;
    }

    public async Task<IReadOnlyList<AclChange>> ReplaceAsync(IReadOnlyList<string> ids, RecordAcl acl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(acl);
        var results = new List<AclChange>(ids.Count);
        foreach (var chunk in ids.Chunk(MaxIds))
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(await ReplaceChunkAsync(chunk, acl, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<AclChange>> ReplaceChunkAsync(IReadOnlyList<string> ids, RecordAcl acl, CancellationToken ct)
    {
        var url = _client.Url(_patchPath);
        var body = new JsonObject
        {
            ["query"] = new JsonObject { ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]) },
            ["ops"] = new JsonArray(
                Replace("/acl/viewers", acl.Viewers),
                Replace("/acl/owners", acl.Owners)),
        };

        HttpFetchResult result;
        try
        {
            // Replacing an ACL with the same groups again leaves it as it is, so the request may be repeated.
            result = await _client.SendJsonAsync(HttpMethod.Patch, url, body, new HashSet<int> { 206 }, ct, idempotent: true).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode is 400 or 403 or 404 or 405 or 415)
        {
            // Refused whole before anything was changed: an ACL storage will not take, a role the caller lacks, or a service
            // that does not take the patch at this path.
            var why = string.Create(
                CultureInfo.InvariantCulture,
                $"{HeaderRedaction.DescribeUrl(url)} refused the change with HTTP {ex.StatusCode}, so no record's access was changed: {HeaderRedaction.RedactMessage(ex.Message)}");
            return [.. ids.Select(id => new AclChange(id, AclChangeOutcome.Refused, why))];
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or TimeoutException)
        {
            // No answer that says what storage did: it may have changed some of the records before the failure.
            var why = $"{HeaderRedaction.DescribeUrl(url)} gave no answer that says whether it changed the record's access: {HeaderRedaction.RedactMessage(ex.Message)}";
            return [.. ids.Select(id => new AclChange(id, AclChangeOutcome.Unknown, why))];
        }

        return Settle(ids, result, url);
    }

    private static JsonObject Replace(string path, IReadOnlyList<string> groups) => new()
    {
        ["op"] = "replace",
        ["path"] = path,
        ["value"] = new JsonArray([.. groups.Select(g => (JsonNode?)JsonValue.Create(g))]),
    };

    /// <summary>
    /// What each id came to by storage's answer: 200 changed every one; 206 names those it did not change, and changed the
    /// ids it names under <c>recordIds</c>. An id a 206 names nowhere is not known to be changed.
    /// </summary>
    private static IReadOnlyList<AclChange> Settle(IReadOnlyList<string> ids, HttpFetchResult result, Uri url)
    {
        if ((int)result.Status != 206)
        {
            return [.. ids.Select(id => new AclChange(id, AclChangeOutcome.Replaced, "access replaced in OSDU"))];
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(result.Body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            var why = $"{HeaderRedaction.DescribeUrl(url)} answered 206, changing some of the records, with an answer that cannot be read ({ex.Message}), so which ones is not known";
            return [.. ids.Select(id => new AclChange(id, AclChangeOutcome.Unknown, why))];
        }

        var replaced = Listed(root, "recordIds");
        var notFound = Listed(root, "notFoundRecordIds");
        var unauthorized = Listed(root, "unAuthorizedRecordIds");
        var locked = Listed(root, "lockedRecordIds");
        return [.. ids.Select(id => replaced.Contains(id) ? new AclChange(id, AclChangeOutcome.Replaced, "access replaced in OSDU")
            : notFound.Contains(id) ? new AclChange(id, AclChangeOutcome.NotFound, "storage holds no record of the id (notFoundRecordIds)")
            : unauthorized.Contains(id) ? new AclChange(id, AclChangeOutcome.Refused, "storage refused it: the flow's identity is not an owner of the record, nor a data manager (unAuthorizedRecordIds)")
            : locked.Contains(id) ? new AclChange(id, AclChangeOutcome.Refused, "storage refused it: the record is locked (lockedRecordIds)")
            : new AclChange(id, AclChangeOutcome.Unknown, $"storage answered 206 and named the id in none of its lists ({Preview(result)}), so whether its access changed is not known"))];
    }

    private static HashSet<string> Listed(JsonElement root, string property)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    public async Task<CallerGroups> CallerGroupsAsync(CancellationToken ct)
    {
        var url = _client.Url(_groupsPath);
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
        var root = OsduHttpClient.ParseJson(result, url);
        var member = JsonPathReader.SelectValue(root, "memberEmail");
        var groups = new List<string>();
        foreach (var group in JsonPathReader.SelectElements(root, "groups[*]"))
        {
            if (group.ValueKind == JsonValueKind.Object && group.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                && email.GetString() is { Length: > 0 } text)
            {
                groups.Add(text);
            }
        }

        return new CallerGroups(string.IsNullOrWhiteSpace(member) ? null : member, groups);
    }

    /// <summary>The start of an answer's body, redacted, to quote in a message.</summary>
    private static string Preview(HttpFetchResult result)
    {
        var text = Encoding.UTF8.GetString(result.Body);
        return HeaderRedaction.RedactMessage(text.Length <= 200 ? text : text[..200] + "...");
    }
}
