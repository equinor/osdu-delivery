using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>
/// The key a duplicate check compares an inventory's records by (osdu/docs/reference/flow/inventory-duplicates.md, The key):
/// one to <see cref="InventoryDuplicates.MaxKeyProperties"/> properties, each a path from the record's root. Two records are
/// copies of one another when they hold equal values at every property of the key: text exactly as written, a number as a
/// number however it is written, and a property crossing a list every value the list yields, in any order. A property a record
/// holds no value at is equal only in a record that holds none there either, and a record holding no value at any of them is
/// passed over. A check downloads each property of every record once (<see cref="ReadAttribute"/>), hashed, and its table is
/// grouped by any key of them in SQL. The key is checked where it crosses a trust boundary (the API, and the module's database
/// a run reads it from), and read the same way on both sides of it.
/// </summary>
public sealed class InventoryDuplicateKey
{
    /// <summary>The roots a key's path starts at: a record's own blocks and the properties every record carries.</summary>
    public static IReadOnlyList<string> Roots { get; } = ["data", "meta", "tags", "ancestry", "acl", "legal", "kind", "createUser", "createTime", "modifyUser", "modifyTime"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>How a check's table writes a property's values: as the record holds them, readable, not escaped for a web page.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private InventoryDuplicateKey(IReadOnlyList<string> paths)
    {
        Paths = paths;
    }

    /// <summary>The paths of the properties, in the order the operator wrote them.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>The key the operator wrote, each property's path, checked; or why it is not one.</summary>
    public static (InventoryDuplicateKey? Key, string? Problem) Of(IReadOnlyList<string?>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return (null, "Name at least one property the copies of a record hold equal (key): the property's path from the record's root, such as data.FacilityName.");
        }

        if (paths.Count > InventoryDuplicates.MaxKeyProperties)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"A key compares at most {InventoryDuplicates.MaxKeyProperties} properties; {paths.Count} were named."));
        }

        var read = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i]?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                return (null, $"key[{i}] names no property; each property of a key is a path from the record's root, such as data.FacilityName.");
            }

            if (PathProblem(path) is { } problem)
            {
                return (null, $"key[{i}] '{Shown(path)}': {problem}");
            }

            if (!seen.Add(path))
            {
                return (null, $"key[{i}] names {path} again; a key compares each property once.");
            }

            read.Add(path);
        }

        return (new InventoryDuplicateKey(read), null);
    }

    /// <summary>The key a check keeps (<see cref="ToJson"/>), read back and checked again, since it crosses a trust boundary in the module's database.</summary>
    /// <exception cref="DeliveryException">The text is not a key as a check writes it.</exception>
    public static InventoryDuplicateKey Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        List<Written>? written;
        try
        {
            written = JsonSerializer.Deserialize<List<Written>>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The duplicate check's key is not the list of properties a check keeps ({ex.Message}); ask for the check again.", ex);
        }

        var (key, problem) = Of(written?.Select(w => w?.Path).ToList());
        return key ?? throw new DeliveryException($"The duplicate check's key cannot be read: {problem} Ask for the check again.");
    }

    /// <summary>The key in the one spelling a check keeps it in: each property's path, in order.</summary>
    public string ToJson() => JsonSerializer.Serialize(Paths.Select(p => new Written(p)).ToList(), Json);

    /// <summary>A SHA-256 of <see cref="ToJson"/> in hex: the same key, however it was spelled, hashes alike.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    /// <summary>
    /// What a record the search returned holds at one property a check downloads: its values as the record writes them (a JSON
    /// list of text) and their hash (<see cref="Hash(IReadOnlyList{JsonNode})"/>); null when it holds no value there.
    /// </summary>
    public static InventoryDuplicateValue? ReadAttribute(JsonNode record, string path)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var leaves = Leaves(JsonPathReader.SelectNodes(record, path)).ToList();
        var written = leaves.Where(n => n.GetValueKind() is not (JsonValueKind.Null or JsonValueKind.Undefined)).Select(RecordValues.Text).ToList();
        return written.Count == 0 ? null : new InventoryDuplicateValue(JsonSerializer.Serialize(written, Readable), Hash(leaves));
    }

    /// <summary>
    /// The SHA-256 of values as a key compares them (<see cref="Normal"/>), null when there are none: in one order, so a list
    /// compares whatever order it holds them in, and each written with its length, so no value can run into the next. Two
    /// records hold equal values at a property exactly when the hashes of their values there are equal.
    /// </summary>
    internal static byte[]? Hash(IReadOnlyList<JsonNode> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        var values = leaves.Select(Normal).OfType<string>().ToList();
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var value in values)
        {
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Every value the nodes a path reached hold: a list's items, each in turn, and anything else as it is.</summary>
    internal static IEnumerable<JsonNode> Leaves(IReadOnlyList<JsonNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is JsonArray list)
            {
                foreach (var item in Leaves(list.Where(i => i is not null).Select(i => i!).ToList()))
                {
                    yield return item;
                }
            }
            else
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// A value as a key compares it, tagged with what it is so text and a number are never equal: text exactly as written, a
    /// number in one form however it is written, true or false, and an object as its canonical JSON; null for no value.
    /// </summary>
    internal static string? Normal(JsonNode node)
    {
        switch (node.GetValueKind())
        {
            case JsonValueKind.String:
                return "s" + node.GetValue<string>();
            case JsonValueKind.Number:
                var written = node.ToJsonString();
                return "n" + (Number(written) ?? written);
            case JsonValueKind.True:
                return "btrue";
            case JsonValueKind.False:
                return "bfalse";
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            default:
                return "j" + CanonicalJson.ToString(node);
        }
    }

    /// <summary>
    /// A number in one form whatever way it is written (<c>1520.50</c>, <c>1.5205e3</c> and <c>1520.5</c> alike): as a decimal
    /// with no trailing zeros where it is one, exactly, else as the shortest double that reads back as it; null for text that is
    /// not a number.
    /// </summary>
    internal static string? Number(string text)
    {
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
        {
            // A decimal is written without an exponent, at the scale it was read with: the zeros its fraction ends with go.
            if (exact == 0)
            {
                return "0";
            }

            var plain = exact.ToString(CultureInfo.InvariantCulture);
            return plain.Contains('.', StringComparison.Ordinal) ? plain.TrimEnd('0').TrimEnd('.') : plain;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var approximate) && double.IsFinite(approximate)
            ? approximate.ToString("R", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Why a path cannot be a property of a key, or null when it can.</summary>
    private static string? PathProblem(string path)
    {
        if (!OsduPath.IsPath(path))
        {
            return path.Length > OsduPath.MaxLength
                ? string.Create(CultureInfo.InvariantCulture, $"a path is at most {OsduPath.MaxLength} characters.")
                : "a path is segments of letters, digits and underscores separated by dots, from the record's root, such as data.FacilityName; a list is crossed without [*].";
        }

        var root = path.Split('.')[0];
        if (root is "id" or "version")
        {
            return $"every record holds its own {root}, so no two records share it; a key names what the copies of a record hold equal.";
        }

        return Roots.Contains(root, StringComparer.Ordinal)
            ? null
            : $"a path starts at the record's root, one of {string.Join(", ", Roots)}.";
    }

    private static string Shown(string text) => text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 77), "...");

    /// <summary>One property as a check keeps it.</summary>
    private sealed record Written(string? Path);
}
