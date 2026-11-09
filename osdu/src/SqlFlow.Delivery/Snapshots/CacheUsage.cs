using System.Text.Encodings.Web;
using System.Text.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>How a render depended on one cached value.</summary>
public enum CacheUsageKind
{
    /// <summary>The value the source was matched against: if it disappears, the record stops resolving.</summary>
    Match,

    /// <summary>A value read out of the cached record and written into the document: if it changes, the document changes.</summary>
    Value,

    /// <summary>
    /// A path read out of the cached record that held no value, so the document was built without one: a dictionary entry
    /// replacing a spelling with no value, say. Its value is empty. If the path gains a value, the document changes.
    /// </summary>
    Empty,

    /// <summary>
    /// A key looked up in a lookup table that listed no row under it, so the document was built without what a row would
    /// have given: a spelling a dictionary does not list yet. The item is the key as <see cref="CacheUsage.ListingKey"/>
    /// folds it, the path the field a row would have given, and the value the key as it was looked up. If the table
    /// comes to list the key, the document changes.
    /// </summary>
    Unlisted,

    /// <summary>
    /// An id a node built and wrote although the cache held records of its entity type and not this one, because the node
    /// lets an unverified reference go out (<c>$unverified</c>). The item is the record id, the path <c>id</c> and the value
    /// the id as written. If a later version holds the record, the reference is verified, and the record is built again.
    /// </summary>
    Unverified,

    /// <summary>
    /// The rows a <c>$findAll</c> found by one key: every record of the type whose field holds the key, whatever case either
    /// is written in. The item is the key as <see cref="CacheUsage.ListingKey"/> folds it, the path the field compared, and
    /// the value the ids of the rows found, in ordinal order and joined by <see cref="CacheUsage.ListedSeparator"/>, empty
    /// when none was. It is recorded whether or not a row was found, so a later version that lists another row under the
    /// key, drops one, or lists the first, reaches every record built without it.
    /// </summary>
    Listed,

    /// <summary>
    /// An id a node built that names a record the cache did not hold, although it held records of its entity type, so the
    /// record was held (osdu/docs/reference/concepts/partition-cache.md, A record held for a reference waits for it). The item
    /// is the record id, the path <c>id</c> and the value the id as built. When a later version holds the record, by a
    /// refresh or a pipeline's cache step, the held record is planned again and built against it.
    /// </summary>
    Missing,
}

/// <summary>
/// One dependency of a rendered record on the cache: which cached record it used, which path it read, and what
/// that path held at the time. Recorded per delivered record so a later cache version can say exactly which
/// records its changes touch, without re-rendering the estate and without guessing
/// (osdu/docs/reference/concepts/partition-cache.md, From a cache change to OSDU).
/// </summary>
public sealed record CacheUsage(string TypeName, string ItemId, string Path, string Value, CacheUsageKind Kind)
{
    /// <summary>The value truncated for a ledger column and for display; the hash covers the whole of it.</summary>
    public const int MaxValueLength = 400;

    public string Display => Value.Length <= MaxValueLength ? Value : Value[..MaxValueLength];

    public string ValueHash => Hashing.ContentHash.Of(Value);

    /// <summary>
    /// A key as an <see cref="CacheUsageKind.Unlisted"/> usage holds it: without case, since a table matches a key without
    /// case when only one row answers, so a row added under any spelling of it may be the one a later render finds.
    /// </summary>
    public static string ListingKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Trim().ToUpperInvariant();
    }

    /// <summary>What separates the ids a <see cref="CacheUsageKind.Listed"/> usage holds.</summary>
    public const string ListedSeparator = ", ";

    /// <summary>
    /// The usage a <c>$findAll</c> records for one key: the rows it found under <paramref name="field"/>, by id in ordinal
    /// order, however many (none included).
    /// </summary>
    public static CacheUsage Listing(string typeName, string field, string key, IEnumerable<string> ids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ids);
        return new CacheUsage(typeName, ListingKey(key), field, ListedIds(ids), CacheUsageKind.Listed);
    }

    /// <summary>The ids a listing holds, as <see cref="Listing"/> writes them: distinct, in ordinal order, joined.</summary>
    public static string ListedIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return string.Join(ListedSeparator, ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>The longest item id the ledger keeps whole (<c>osdu.CacheSetEntry.ItemId</c>).</summary>
    public const int MaxItemIdLength = 512;

    /// <summary>The longest path the ledger keeps whole (<c>osdu.CacheSetEntry.Path</c>).</summary>
    public const int MaxPathLength = 400;

    /// <summary>
    /// What the item id of a composite miss starts with. A listing key is trimmed and an OSDU record id holds no space, so
    /// no other item id starts with one, and the ledger finds a type's composite misses by this prefix alone.
    /// </summary>
    public const string CompositeMarker = " ";

    /// <summary>Follows <see cref="CompositeMarker"/> in the key of a miss whose node also folded punctuation and spacing away.</summary>
    private const string FoldedMarker = "~";

    /// <summary>What joins the fields of a composite miss in its path: field names hold no space, so the path splits back exactly.</summary>
    public const string CompositeFieldSeparator = " and ";

    private static readonly JsonSerializerOptions CompositeJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The usage a findBy line of several comparisons (<c>Well = well_name and Mnemonic = curve_mnemonic</c>) records when no
    /// row met them all: an <see cref="CacheUsageKind.Unlisted"/> usage whose item is <see cref="CompositeKey"/> of the values,
    /// whose path is the fields joined by <see cref="CompositeFieldSeparator"/>, and whose value is the values as they were
    /// looked up, as a JSON array. A later version of the type is asked for the same values together, so only a row meeting
    /// every comparison reaches the record, whichever of its values another row shares. Null when the key or the path would
    /// not fit the ledger's columns whole: a miss that could not be asked for again is not noted, as a key longer than any a
    /// table can hold is not.
    /// </summary>
    public static CacheUsage? CompositeMiss(string typeName, IReadOnlyList<(string Field, string Value)> comparisons, bool ignoreSeparators)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(comparisons);
        if (comparisons.Count < 2)
        {
            throw new ArgumentException("A composite miss compares at least two fields; one is noted as the field and value it compared.", nameof(comparisons));
        }

        var key = CompositeKey(comparisons.Select(c => c.Value).ToList(), ignoreSeparators);
        var path = string.Join(CompositeFieldSeparator, comparisons.Select(c => ReferenceField.Normalize(c.Field)));
        if (key.Length > MaxItemIdLength || path.Length > MaxPathLength)
        {
            return null;
        }

        return new CacheUsage(typeName, key, path, JsonSerializer.Serialize(comparisons.Select(c => c.Value.Trim()).ToArray(), CompositeJson), CacheUsageKind.Unlisted);
    }

    /// <summary>
    /// The item a composite miss is kept under: <see cref="CompositeMarker"/>, <c>~</c> when the node folded punctuation and
    /// spacing away, and the values as <see cref="ListingKey"/> folds them, as a JSON array, so two lines looking up the same
    /// values in any case are one dependency.
    /// </summary>
    public static string CompositeKey(IReadOnlyList<string> values, bool ignoreSeparators)
    {
        ArgumentNullException.ThrowIfNull(values);
        return CompositeMarker + (ignoreSeparators ? FoldedMarker : string.Empty) + JsonSerializer.Serialize(values.Select(ListingKey).ToArray(), CompositeJson);
    }

    /// <summary>True when <paramref name="itemId"/> is the item of a composite miss (<see cref="CompositeMiss"/>).</summary>
    public static bool IsComposite(string itemId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        return itemId.StartsWith(CompositeMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a stored composite miss compared: each field with the value looked up, and whether the node folded punctuation
    /// and spacing away. The values are read from <paramref name="valueText"/> when the ledger kept it whole, else from the
    /// key, folded as it keeps them, which a version is asked for ignoring case just the same. Null for an item that is not
    /// a composite miss, or one whose parts no longer agree (a key cut short).
    /// </summary>
    public static CompositeLookup? Composite(string itemId, string path, string valueText)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(path);
        if (!IsComposite(itemId))
        {
            return null;
        }

        var keyed = itemId[CompositeMarker.Length..];
        var folds = keyed.StartsWith(FoldedMarker, StringComparison.Ordinal);
        var fields = path.Split(CompositeFieldSeparator);
        var values = Values(valueText, fields.Length) ?? Values(folds ? keyed[FoldedMarker.Length..] : keyed, fields.Length);
        if (values is null || fields.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return new CompositeLookup(fields.Zip(values, (field, value) => (field, value)).ToList(), folds);
    }

    /// <summary>
    /// A composite miss as a sentence part, each field with the value looked up (<c>Well = 'A-12' and Mnemonic = 'GR'</c>),
    /// or null when <paramref name="path"/> and <paramref name="valueText"/> are not one.
    /// </summary>
    public static string? CompositeText(string path, string? valueText)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains(CompositeFieldSeparator, StringComparison.Ordinal) || valueText is null)
        {
            return null;
        }

        var fields = path.Split(CompositeFieldSeparator);
        return Values(valueText, fields.Length) is { } values
            ? string.Join(CompositeFieldSeparator, fields.Zip(values, (field, value) => $"{field} = '{value}'"))
            : null;
    }

    /// <summary>
    /// The items a composite miss of the fields <paramref name="path"/> joins and the values <paramref name="valueText"/>
    /// holds is kept under, by a node that folds separators and by one that does not: what a change listing those values
    /// answers. None when the two are not a composite miss's.
    /// </summary>
    public static IReadOnlyList<string> CompositeKeys(string path, string? valueText)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains(CompositeFieldSeparator, StringComparison.Ordinal)
            || Values(valueText, path.Split(CompositeFieldSeparator).Length) is not { } values)
        {
            return [];
        }

        return [CompositeKey(values, ignoreSeparators: false), CompositeKey(values, ignoreSeparators: true)];
    }

    /// <summary>The values a JSON array of <paramref name="count"/> texts holds, or null when the text is not one.</summary>
    private static IReadOnlyList<string>? Values(string? json, int count)
    {
        if (string.IsNullOrEmpty(json) || json[0] != '[')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, CompositeJson) is { } values && values.Length == count && values.All(v => !string.IsNullOrWhiteSpace(v))
                ? values
                : null;
        }
        catch (JsonException)
        {
            // A value the ledger cut short at its column's length: the caller reads the values from the key instead.
            return null;
        }
    }
}

/// <summary>What a composite miss compared (<see cref="CacheUsage.Composite"/>): each field with its value, and whether it folded separators.</summary>
public sealed record CompositeLookup(IReadOnlyList<(string Field, string Value)> Comparisons, bool IgnoreSeparators);
