namespace SqlFlow.Delivery.Identity;

/// <summary>
/// A client-supplied OSDU record id: <c>{partition}:{entityType}:{unique}</c> (osdu/docs/reference/flow/mapping.md,
/// The OSDU id). The unique segment is the delivery key, or, for a mapping whose <c>dataset.idFrom</c> is <c>key</c>, the
/// key's own values, so upsert is native and no id bookkeeping is needed.
/// </summary>
public static class TargetId
{
    /// <summary>The longest OSDU id the ledger keeps for a record.</summary>
    public const int MaxLength = Data.DeliveryModel.MaxTargetIdLength;

    /// <summary>The id whose unique segment is the delivery key, without hyphens: every mapping's, unless it derives its ids from its key.</summary>
    public static string Compose(string dataPartition, string entityType, DeliveryKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return $"{dataPartition}:{entityType}:{key.Value:N}";
    }

    /// <summary>The namespace of the ids a repair gives records in place of ones OSDU lost. Never change it.</summary>
    private static readonly Guid ReassignedNamespace = DeterministicGuid.Namespace("reassigned-target-id");

    /// <summary>
    /// The OSDU id a record is given in place of <paramref name="lost"/>, an id it claimed that OSDU no longer holds: the same
    /// partition and entity type, and a unique segment made from the record's delivery key and the id it replaces, without
    /// hyphens. Asked twice for the same id it gives the same one, and a record given a new id more than once gets a
    /// different one each time. Null when <paramref name="lost"/> is not an id of three segments.
    /// </summary>
    public static string? Reassigned(string lost, DeliveryKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lost);
        var parts = lost.Split(':', 3);
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return null;
        }

        return $"{parts[0]}:{parts[1]}:{DeterministicGuid.V5(ReassignedNamespace, $"{key.Value:N}|{lost}"):N}";
    }

    /// <summary>
    /// The id whose unique segment is the key's own values (<c>dataset.idFrom: key</c>), or null with why the values give
    /// none. Each value is trimmed, as the delivery key trims it, and keeps its case, since OSDU compares ids exactly. One
    /// value is written as it stands, its colons kept, so a key such as <c>WELLDB::GAPI</c> gives the code OSDU's catalogs
    /// write (<c>LIS-LAS::GAPI</c>); with several, each value's own colons are escaped and the values are joined with ':'.
    /// Every other character an id does not carry, '%' among them, is percent-encoded (<see cref="IdSegment.Encode"/>), so
    /// two different keys of one mapping never give one id and the key can be read back from it (<see cref="KeyValues"/>).
    /// Keys of different lengths can: <c>[a:b]</c> and <c>[a, b]</c> both give <c>a:b</c>. Those are two mappings writing
    /// one entity type, and the ledger refuses the second record that claims the id, as it refuses any id claimed twice.
    /// </summary>
    /// <param name="dataPartition">The partition the id is minted in.</param>
    /// <param name="entityType">The entity type the mapping renders.</param>
    /// <param name="keyValues">The key's values, in key order.</param>
    /// <param name="problem">Why no id is given: a value that is empty or not valid Unicode, or an id longer than <see cref="MaxLength"/>.</param>
    public static string? ComposeFromKey(string dataPartition, string entityType, IReadOnlyList<string?> keyValues, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(keyValues);
        problem = null;
        if (keyValues.Count == 0)
        {
            problem = "the key has no values to make the OSDU id from";
            return null;
        }

        var parts = new List<string>(keyValues.Count);
        for (var i = 0; i < keyValues.Count; i++)
        {
            var value = keyValues[i]?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                problem = keyValues.Count == 1
                    ? "the key is empty, and the OSDU id is made from it"
                    : $"key value {i + 1} of {keyValues.Count} is empty, and the OSDU id is made from every key value";
                return null;
            }

            if (IdSegment.Encode(value, keepEscapes: false, keepColons: keyValues.Count == 1) is not { } encoded)
            {
                problem = $"key value {i + 1} holds text that is not valid Unicode, which no OSDU id can carry";
                return null;
            }

            parts.Add(encoded);
        }

        var id = $"{dataPartition}:{entityType}:{string.Join(':', parts)}";
        if (id.Length > MaxLength)
        {
            problem = $"the OSDU id the key gives is {id.Length} characters long ({id[..60]}...), and the ledger keeps ids of at most {MaxLength}; "
                + "a character outside ASCII letters, digits and _ - . : is written as up to 12 characters of percent-escapes";
            return null;
        }

        return id;
    }

    /// <summary>
    /// The key values an id made by <see cref="ComposeFromKey"/> was made from, for a mapping keyed by
    /// <paramref name="keyCount"/> columns: null when the id is not of <paramref name="dataPartition"/> and
    /// <paramref name="entityType"/>, or its unique segment does not read back as that many values.
    /// </summary>
    public static IReadOnlyList<string>? KeyValues(string id, string dataPartition, string entityType, int keyCount)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(keyCount, 1);
        var prefix = $"{dataPartition}:{entityType}:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length == prefix.Length)
        {
            return null;
        }

        var unique = id[prefix.Length..];
        var parts = keyCount == 1 ? new[] { unique } : unique.Split(':');
        if (parts.Length != keyCount)
        {
            return null;
        }

        var values = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (IdSegment.Decode(part) is not { Length: > 0 } value || value.Trim().Length != value.Length)
            {
                return null;
            }

            values.Add(value);
        }

        return values;
    }

    /// <summary>Extracts the entity type ("work-product-component--WellLog") from a kind ("osdu:wks:work-product-component--WellLog:1.4.0").</summary>
    public static string EntityTypeFromKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var parts = kind.Split(':');
        if (parts.Length != 4)
        {
            throw new ArgumentException($"Kind '{kind}' is not in the form authority:source:entityType:version.", nameof(kind));
        }

        return parts[2];
    }

    /// <summary>The schema version segment of a kind ("1.4.0").</summary>
    public static string VersionFromKind(string kind)
    {
        var parts = kind.Split(':');
        return parts.Length == 4 ? parts[3] : throw new ArgumentException($"Kind '{kind}' is malformed.", nameof(kind));
    }

    /// <summary>
    /// Formats a reference to another OSDU record (<c>{partition}:{entityType}:{unique}:</c>, trailing colon for
    /// "latest version"), the form OSDU uses for relationship properties.
    /// </summary>
    public static string Reference(string dataPartition, string entityType, string unique)
        => $"{dataPartition}:{entityType}:{unique}:";

    /// <summary>
    /// The unique segment of an OSDU id without a version, everything after its entity type (<c>p:t:k</c> gives <c>k</c>, and
    /// <c>p:t:a:b</c> gives <c>a:b</c>): what a record and the records of a linked template minted from the same key share.
    /// Null when <paramref name="id"/> is not an id of three segments.
    /// </summary>
    public static string? Unique(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var parts = id.Split(':', 3);
        return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0 && parts[2].Length > 0 ? parts[2] : null;
    }

    /// <summary>
    /// A record reference without its version: <c>p:t:k:</c> and <c>p:t:k:123</c> become <c>p:t:k</c>. The unique
    /// segment of an OSDU id may itself hold colons, so only a trailing empty or all-digit segment is taken as the version.
    /// </summary>
    public static string WithoutVersion(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var last = reference.LastIndexOf(':');
        if (last < 0)
        {
            return reference;
        }

        var tail = reference[(last + 1)..];
        var colons = reference.Count(c => c == ':');
        return colons >= 3 && tail.All(char.IsAsciiDigit) ? reference[..last] : reference;
    }

    /// <summary>
    /// Whether <paramref name="value"/> reads as a reference to an OSDU record: a partition, an entity type
    /// (<c>group--Entity</c>) and a unique segment, with or without the version.
    /// </summary>
    public static bool IsRecordReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parts = value.Split(':');
        return parts.Length >= 3
            && parts[0].Length > 0
            && parts[1].IndexOf("--", StringComparison.Ordinal) is > 0 and var at && at < parts[1].Length - 2
            && parts[2].Length > 0;
    }
}
