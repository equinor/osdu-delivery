using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>How a property of a duplicate check's key compares two records' values.</summary>
public enum DuplicateComparison
{
    /// <summary>The values as the records hold them: text exactly, case included; a number as a number; text and a number never alike.</summary>
    Exact,

    /// <summary>Text ignoring case, in Unicode's composed form, so <c>Wellbore A-1</c> and <c>WELLBORE A-1</c> are alike.</summary>
    IgnoreCase,

    /// <summary>
    /// Text ignoring case, the spaces at either end and how many spaces stand between words, empty text taken as no value,
    /// and text that is a number taken as that number, so <c> Wellbore  a-1 </c> and <c>WELLBORE A-1</c> are alike, and
    /// so are <c>"1520.50"</c> and <c>1520.5</c>.
    /// </summary>
    Loose,
}

/// <summary>One property of a duplicate check's key: its path from the record's root, and how its values compare.</summary>
public sealed record DuplicateKeyProperty(string Path, DuplicateComparison Compare);

/// <summary>
/// What a record holds at a key: the hash of its values as the key compares them (null when it holds no value at any property),
/// the values as it holds them (JSON, property to values, cut to what a row keeps), and which properties, by their place in the
/// key, it holds a value at.
/// </summary>
public sealed record DuplicateKeyRead(byte[]? Hash, string? ValuesJson, IReadOnlyList<int> Held);

/// <summary>
/// The key a duplicate check compares an inventory's records by (osdu/docs/reference/flow/inventory-duplicates.md, Finding duplicate
/// records): one to <see cref="InventoryDuplicates.MaxKeyProperties"/> properties, each a path from the record's root and how
/// its values compare. Two records are copies of one another when they hold alike values at every property of the key; a
/// property crossing a list holds every value the list yields, compared in any order, and a property a record holds no value
/// at is alike only in a record that holds none there either. A record holding no value at any of them is passed over. The
/// key is checked where it crosses a trust boundary (the API, and the module's database a run reads it from), and read the
/// same way on both sides of it.
/// </summary>
public sealed partial class InventoryDuplicateKey
{
    /// <summary>The roots a key's path starts at: a record's own blocks and the properties every record carries.</summary>
    public static IReadOnlyList<string> Roots { get; } = ["data", "meta", "tags", "ancestry", "acl", "legal", "kind", "createUser", "createTime", "modifyUser", "modifyTime"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private InventoryDuplicateKey(IReadOnlyList<DuplicateKeyProperty> properties)
    {
        Properties = properties;
    }

    /// <summary>The properties, in the order the operator wrote them.</summary>
    public IReadOnlyList<DuplicateKeyProperty> Properties { get; }

    /// <summary>The paths of the properties, which a search returns of each record and a page shows a column of.</summary>
    public IReadOnlyList<string> Paths => Properties.Select(p => p.Path).ToList();

    /// <summary>The name a comparison is written under.</summary>
    public static string NameOf(DuplicateComparison compare) => compare switch
    {
        DuplicateComparison.IgnoreCase => "ignoreCase",
        DuplicateComparison.Loose => "loose",
        _ => "exact",
    };

    /// <summary>
    /// The key the operator wrote, each property's path and comparison, checked; or why it is not one. A comparison left out is
    /// <c>exact</c>, and names are read ignoring case.
    /// </summary>
    public static (InventoryDuplicateKey? Key, string? Problem) Of(IReadOnlyList<(string? Path, string? Compare)>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return (null, "Name at least one property the copies of a record hold alike (key): the property's path from the record's root, such as data.FacilityName.");
        }

        if (properties.Count > InventoryDuplicates.MaxKeyProperties)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"A key compares at most {InventoryDuplicates.MaxKeyProperties} properties; {properties.Count} were named."));
        }

        var read = new List<DuplicateKeyProperty>(properties.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < properties.Count; i++)
        {
            var path = properties[i].Path?.Trim();
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

            var named = properties[i].Compare?.Trim();
            DuplicateComparison compare;
            if (string.IsNullOrEmpty(named))
            {
                compare = DuplicateComparison.Exact;
            }
            else if (!TryComparison(named, out compare))
            {
                return (null, $"key[{i}] '{path}' compares '{Shown(named)}'; a property compares exact, ignoreCase or loose.");
            }

            read.Add(new DuplicateKeyProperty(path, compare));
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

        var (key, problem) = Of(written?.Select(w => (w?.Path, w?.Compare)).ToList());
        return key ?? throw new DeliveryException($"The duplicate check's key cannot be read: {problem} Ask for the check again.");
    }

    /// <summary>The key in the one spelling a check keeps it in: each property's path and comparison, in order.</summary>
    public string ToJson() => JsonSerializer.Serialize(Properties.Select(p => new Written(p.Path, NameOf(p.Compare))).ToList(), Json);

    /// <summary>A SHA-256 of <see cref="ToJson"/> in hex: the same key, however it was spelled, hashes alike.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    /// <summary>What a record the search returned holds at the key (<see cref="DuplicateKeyRead"/>).</summary>
    public DuplicateKeyRead Read(JsonNode record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var key = new StringBuilder();
        var shown = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var held = new List<int>();
        for (var i = 0; i < Properties.Count; i++)
        {
            var property = Properties[i];
            var values = new List<string>();
            var written = new List<string>();
            foreach (var node in Leaves(JsonPathReader.SelectNodes(record, property.Path)))
            {
                if (Normal(node, property.Compare) is { } normal)
                {
                    values.Add(normal);
                    written.Add(RecordValues.Text(node));
                }
            }

            // Each property's values in one order, so a list compares whatever order it holds them in; each value is written
            // with its length, so no value can run into the next.
            values.Sort(StringComparer.Ordinal);
            key.Append(i.ToString(CultureInfo.InvariantCulture)).Append('[');
            foreach (var value in values)
            {
                key.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            }

            key.Append(']');
            if (values.Count > 0)
            {
                held.Add(i);
                shown[property.Path] = written;
            }
        }

        return held.Count > 0
            ? new DuplicateKeyRead(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())), InventoryRunner.FitValues(shown), held)
            : new DuplicateKeyRead(null, null, held);
    }

    /// <summary>Every value the nodes a path reached hold: a list's items, each in turn, and anything else as it is.</summary>
    private static IEnumerable<JsonNode> Leaves(IReadOnlyList<JsonNode> nodes)
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
    /// A value as its comparison reads it, tagged with what it is so text and a number never meet but where the comparison says
    /// they do; null for a value the comparison takes as none (empty text, compared loose).
    /// </summary>
    internal static string? Normal(JsonNode node, DuplicateComparison compare)
    {
        switch (node.GetValueKind())
        {
            case JsonValueKind.String:
                var text = node.GetValue<string>();
                if (compare == DuplicateComparison.Exact)
                {
                    return "s" + text;
                }

                var folded = text.Normalize(NormalizationForm.FormC);
                if (compare == DuplicateComparison.Loose)
                {
                    folded = Spaces().Replace(folded.Trim(), " ");
                    if (folded.Length == 0)
                    {
                        return null;
                    }

                    if (Number(folded) is { } number)
                    {
                        return "n" + number;
                    }
                }

                return "s" + folded.ToUpperInvariant();
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

    private static bool TryComparison(string named, out DuplicateComparison compare)
    {
        foreach (var candidate in Enum.GetValues<DuplicateComparison>())
        {
            if (string.Equals(NameOf(candidate), named, StringComparison.OrdinalIgnoreCase))
            {
                compare = candidate;
                return true;
            }
        }

        compare = DuplicateComparison.Exact;
        return false;
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
            return $"every record holds its own {root}, so no two records share it; a key names what the copies of a record hold alike.";
        }

        return Roots.Contains(root, StringComparer.Ordinal)
            ? null
            : $"a path starts at the record's root, one of {string.Join(", ", Roots)}.";
    }

    private static string Shown(string text) => text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 77), "...");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>One property as a check keeps it.</summary>
    private sealed record Written(string? Path, string? Compare);
}
