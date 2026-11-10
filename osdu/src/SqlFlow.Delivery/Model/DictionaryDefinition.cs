using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A dictionary document (<c>documentType: dictionary</c>): one lookup table kept in the repository, filed as
/// <c>dictionaries/&lt;name&gt;.yaml</c>. A cache flow declares a type holding it (<c>dictionary: &lt;name&gt;</c>), and a
/// refresh captures its entries into the partition's cache, where every mapping reads it the way it reads any cached type.
/// A dictionary of pairs maps each key to one value; a dictionary with <see cref="Fields"/> gives each key several named
/// values. Every key and value is text exactly as the document writes it; the template decides the type a value is written
/// as, so <c>NO</c>, <c>1.10</c> and <c>true</c> are the texts they read as, never a boolean or a number.
/// </summary>
public sealed record DictionaryDefinition
{
    public const string DocumentTypeName = "dictionary";

    /// <summary>The name a dictionary's key is kept under when the document names none.</summary>
    public const string DefaultKey = "key";

    /// <summary>The name the value of a dictionary of pairs is kept under.</summary>
    public const string ValueField = "value";

    /// <summary>The most entries a dictionary may hold: every entry is loaded with the cache version a render reads.</summary>
    public const int MaxEntries = LookupKeys.MaxEntries;

    /// <summary>The document's file, for messages; null for an inline document.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The dictionary's name, which its file is named by and a cache flow declares it by.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The name each entry's key is kept under (default <c>key</c>).</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The names of the values each entry carries: those the document lists under <c>fields</c>, or <c>value</c> alone for a
    /// dictionary of pairs.
    /// </summary>
    public required IReadOnlyList<string> Fields { get; init; }

    /// <summary>True for a dictionary of pairs, whose entries are one value each rather than a map of named values.</summary>
    public bool IsPairs { get; init; }

    /// <summary>The entries, in the order the document lists them.</summary>
    public required IReadOnlyList<DictionaryEntry> Entries { get; init; }

    /// <summary>
    /// The dictionary as the lookup table a cache type named <paramref name="typeName"/> holds: one row per entry, its id the
    /// key, carrying the key under <see cref="Key"/> and every value the entry gives. A value written as no value is not
    /// kept, so reading it gives no value.
    /// </summary>
    public ReferenceType ToLookup(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        var rows = new List<ReferenceItem>(Entries.Count);
        foreach (var entry in Entries)
        {
            var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [Key] = ReferenceValue.Of(entry.Key) };
            foreach (var (name, value) in entry.Values)
            {
                if (value is not null)
                {
                    fields[name] = ReferenceValue.Of(value);
                }
            }

            rows.Add(new ReferenceItem(entry.Key, fields));
        }

        return new ReferenceType(typeName, ReferenceType.LookupEntityType(typeName), rows.OrderBy(r => r.Id, StringComparer.Ordinal), Key);
    }

    /// <summary>The fields a cache type holding the dictionary keeps beside its key, as the catalog records them.</summary>
    public IReadOnlyList<ReferenceFieldSpec> FieldSpecs() => Fields.Select(field => new ReferenceFieldSpec(field, field)).ToList();
}

/// <summary>One entry of a dictionary: its key, and each named value it gives (null where it gives no value).</summary>
public sealed record DictionaryEntry(string Key, IReadOnlyDictionary<string, string?> Values);
