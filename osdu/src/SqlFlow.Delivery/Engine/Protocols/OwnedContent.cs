using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The data keys another system writes into a record a flow delivers, and the content that is the flow's own. An update
/// carries those keys forward from the version OSDU holds (osdu/docs/reference/flow/routes.md, storage), and a delivery
/// that carries any records a hash of everything else it wrote, so a verify that finds a newer version can tell a write
/// that touched only those keys (External Data Services updating a data job's run state after a fetch,
/// osdu/specs/eds-dms/INTEGRATION.md section 2.1) from a change to what the flow delivered. The hash covers what a
/// client writes of a record (openapi storage v2, Record: id, kind, acl, the legal tags and countries, data, ancestry,
/// meta and tags), never what Storage adds to it (the version, the create and modify stamps, the legal status), and
/// counts an empty ancestry, meta or tags block as absent.
/// </summary>
internal static class OwnedContent
{
    /// <summary>The target state value holding the hash of the flow's own content, as its delivery wrote it.</summary>
    public const string HashValue = "ownedContent.hash";

    /// <summary>The target state value listing, as a JSON array, the data keys the hash leaves out.</summary>
    public const string ExcludedValue = "ownedContent.excluded";

    private static readonly string[] Envelope = ["id", "kind", "acl", "data", "ancestry", "meta", "tags"];

    /// <summary>
    /// The blocks of a record a client may leave out (openapi storage v2, Record: ancestry, meta and tags). A record read
    /// back can carry one of them null or empty, whoever wrote it; either way the record has none.
    /// </summary>
    public static IReadOnlyList<string> OptionalBlocks { get; } = ["ancestry", "meta", "tags"];

    /// <summary>The stamps Storage adds to a record: a first version has no modifier, and reads back with these null.</summary>
    public static IReadOnlyList<string> ModifyStamps { get; } = ["modifyUser", "modifyTime"];

    private static readonly string[] LegalLists = ["legaltags", "otherRelevantDataCountries"];

    /// <summary>
    /// The data keys an update of <paramref name="document"/> carries forward from the version OSDU holds: the flow's
    /// <c>preserveDataKeys</c>, and the keys External Data Services writes on records of the document's type.
    /// </summary>
    public static IReadOnlyList<string> PreservedKeys(ProtocolOptions options, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(document);
        var written = EdsRecordRules.WrittenByEds(EdsRecordRules.EntityTypeOf(document));
        return written.Count == 0
            ? options.PreserveDataKeys
            : options.PreserveDataKeys.Concat(written).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The hash of what a client writes of <paramref name="record"/>, the data keys in <paramref name="excluded"/> left out.</summary>
    public static string Hash(JsonObject record, IReadOnlyList<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(excluded);
        var left = new HashSet<string>(excluded, StringComparer.Ordinal);
        var projection = new JsonObject();
        foreach (var name in Envelope)
        {
            switch (record[name])
            {
                case null:
                    break;
                case JsonObject data when name == "data":
                    var kept = new JsonObject();
                    foreach (var (key, value) in data)
                    {
                        if (!left.Contains(key))
                        {
                            kept[key] = value?.DeepClone();
                        }
                    }

                    projection[name] = kept;
                    break;
                case var value when OptionalBlocks.Contains(name) && IsEmpty(value):
                    break;
                case var value:
                    projection[name] = value.DeepClone();
                    break;
            }
        }

        switch (record["legal"])
        {
            case JsonObject legal:
                var lists = new JsonObject();
                foreach (var name in LegalLists)
                {
                    if (legal[name] is { } list)
                    {
                        lists[name] = list.DeepClone();
                    }
                }

                projection["legal"] = lists;
                break;
            case { } other:
                projection["legal"] = other.DeepClone();
                break;
        }

        return ContentHash.Of(CanonicalJson.ToString(CanonicalJson.Normalize(projection)));
    }

    /// <summary>
    /// Puts the hash of <paramref name="written"/> on the values a delivery returns when it carries keys forward, and
    /// clears the one an earlier delivery recorded (<paramref name="earlier"/>) when it no longer does.
    /// </summary>
    public static void Record(IDictionary<string, string> returned, JsonObject written, IReadOnlyList<string> preserved, IReadOnlyDictionary<string, string> earlier)
    {
        ArgumentNullException.ThrowIfNull(returned);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(preserved);
        ArgumentNullException.ThrowIfNull(earlier);
        if (preserved.Count > 0)
        {
            returned[HashValue] = Hash(written, preserved);
            returned[ExcludedValue] = new JsonArray(preserved.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()).ToJsonString();
        }
        else if (earlier.TryGetValue(HashValue, out var recorded) && recorded.Length > 0)
        {
            returned[HashValue] = string.Empty;
            returned[ExcludedValue] = string.Empty;
        }
    }

    /// <summary>
    /// Puts the hash of <paramref name="written"/> on the values a plain storage write returns, whether or not it carries keys
    /// forward: storage keeps what such a write sends (openapi storage v2, PUT /records), so the hash always stands. With it,
    /// a verify that finds a newer version reads the record and tells a write of the same content by another writer (another
    /// instance of the delivery linked to the record, osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record
    /// OSDU already holds) from a change to what the flow delivered.
    /// </summary>
    public static void RecordWritten(IDictionary<string, string> returned, JsonObject written, IReadOnlyList<string> preserved)
    {
        ArgumentNullException.ThrowIfNull(returned);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(preserved);
        returned[HashValue] = Hash(written, preserved);
        returned[ExcludedValue] = new JsonArray(preserved.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()).ToJsonString();
    }

    /// <summary>The hash a delivery recorded and the keys it left out, or null when the state records none (or cannot be read).</summary>
    public static (string Hash, IReadOnlyList<string> Excluded)? Recorded(IReadOnlyDictionary<string, string>? state)
    {
        if (state is null
            || !state.TryGetValue(HashValue, out var hash) || string.IsNullOrEmpty(hash)
            || !state.TryGetValue(ExcludedValue, out var text) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonArray list)
            {
                return null;
            }

            var keys = new List<string>(list.Count);
            foreach (var item in list)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var key))
                {
                    return null;
                }

                keys.Add(key);
            }

            return (hash, keys);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="stored"/> still holds the content the delivery recorded in <paramref name="state"/> wrote:
    /// null when the state records none, true when only the keys the hash left out can differ.
    /// </summary>
    public static bool? Unchanged(JsonObject stored, IReadOnlyDictionary<string, string>? state)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return Recorded(state) is { } recorded
            ? string.Equals(Hash(stored, recorded.Excluded), recorded.Hash, StringComparison.Ordinal)
            : null;
    }

    /// <summary>The target state a verify needs of a record, parsed only when it records a hash.</summary>
    public static IReadOnlyDictionary<string, string>? StateOf(string? targetStateJson)
        => targetStateJson is not null && targetStateJson.Contains(HashValue, StringComparison.Ordinal)
            ? JsonMerge.ToValues(targetStateJson)
            : null;

    /// <summary>Whether <paramref name="node"/> holds nothing: null, an empty list, or an object whose every value holds nothing.</summary>
    public static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonObject obj => obj.All(p => IsEmpty(p.Value)),
        JsonArray array => array.Count == 0,
        _ => false,
    };
}
