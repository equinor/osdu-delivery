namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// Where a cached type's records come from. Every origin fills the same partition cache, so a mapping reads a type the same
/// way whatever filled it, and every type is versioned, traced and rolled out alike.
/// </summary>
public enum CacheOrigin
{
    /// <summary>OSDU records a search of the platform returns: reference and master data, each record an OSDU id.</summary>
    Osdu,

    /// <summary>The rows of an ingestion table SQLFlow's own flows load, each row keyed by its key column.</summary>
    Table,

    /// <summary>The entries of a dictionary document kept in the repository, each entry keyed by its key.</summary>
    Dictionary,

    /// <summary>
    /// The members a dimension flow's dimension holds in the partition, each keyed by its clean value with the originals it
    /// gathers and the records holding them (osdu/docs/reference/flow/cache.md, Dimension values (<c>dimension</c>,
    /// <c>dimensionFlow</c>)).
    /// </summary>
    Dimension,
}

/// <summary>How an origin is written in a cache flow, the catalog and the API.</summary>
public static class CacheOrigins
{
    public const string OsduText = "osdu";

    public const string TableText = "table";

    public const string DictionaryText = "dictionary";

    public const string DimensionText = "dimension";

    public static string Text(CacheOrigin origin) => origin switch
    {
        CacheOrigin.Table => TableText,
        CacheOrigin.Dictionary => DictionaryText,
        CacheOrigin.Dimension => DimensionText,
        _ => OsduText,
    };

    /// <summary>The origin a catalog row names; a row written before origins existed names none and is an OSDU type.</summary>
    public static CacheOrigin Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        TableText => CacheOrigin.Table,
        DictionaryText => CacheOrigin.Dictionary,
        DimensionText => CacheOrigin.Dimension,
        null or "" or OsduText => CacheOrigin.Osdu,
        _ => throw new DeliveryException($"'{text}' is not a cache origin; a cached type comes from osdu, a table, a dictionary or a dimension."),
    };
}

/// <summary>
/// The rules a lookup table's keys keep. A key is what a lookup row is stored and matched under, so it has to be one text the
/// catalog holds exactly once: trimmed, because a value is matched trimmed and SQL Server compares text without its trailing
/// spaces; not empty; and short enough that the cache's composite indexes stay inside SQL Server's key-size limits.
/// </summary>
public static class LookupKeys
{
    /// <summary>The longest key a lookup row may have.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// The most entries a dictionary holds, and the most values a dimension type holds: every row is loaded with the cache
    /// version a render reads, so a set beyond this belongs in an ingestion table, which a table type reads
    /// (<see cref="DefaultTableRows"/>) or a delivery joins.
    /// </summary>
    public const int MaxEntries = 100_000;

    /// <summary>
    /// The most rows a table type captures when its cache flow does not say otherwise (<c>types[].maxRows</c>,
    /// osdu/docs/reference/flow/cache-origins.md, A table an ingestion flow loads). A table holding more refuses the capture.
    /// </summary>
    public const int DefaultTableRows = 1_000_000;

    /// <summary>
    /// The most a table type's <c>maxRows</c> may name. Every row is held in the memory of each process that renders against
    /// the partition's cache, once for each version it keeps (osdu/docs/reference/concepts/partition-cache.md, Versions), so
    /// a bound keeps a mistyped number from asking a node for more than it can hold.
    /// </summary>
    public const int MaxTableRows = 5_000_000;

    /// <summary>What is wrong with <paramref name="key"/> as a lookup key, or null when nothing is.</summary>
    public static string? Problem(string? key)
    {
        if (key is null || key.Trim().Length == 0)
        {
            return "is empty";
        }

        if (key.Length != key.Trim().Length)
        {
            return $"'{key}' has spaces around it, and a value is matched trimmed";
        }

        if (key.Length > MaxLength)
        {
            return $"'{key[..40]}...' is longer than the {MaxLength} characters a key may have";
        }

        return key.Any(char.IsControl) ? $"'{key}' holds a control character" : null;
    }

    /// <summary>
    /// Whether a field or key name is <c>id</c> in any casing. A lookup row's key is its record id, and a cached field called
    /// <c>ID</c> would shadow it under that name, so neither a key nor a field of a lookup table may be called that.
    /// </summary>
    public static bool IsIdName(string name) => string.Equals(name.Trim(), "id", StringComparison.OrdinalIgnoreCase);
}
