using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// A versioned, immutable capture of the OSDU reference and master data a mapping resolves against
/// (osdu/docs/reference/concepts/partition-cache.md, Versions), with the partition's own system properties as the
/// platform reported them at the capture. Replaces the per-replica in-memory cache: every render under one version sees
/// the same items and the same properties.
/// </summary>
public sealed class ReferenceSnapshot
{
    private readonly Dictionary<string, ReferenceType> _types;

    /// <param name="version">The version label.</param>
    /// <param name="capturedUtc">When the capture was made.</param>
    /// <param name="types">The cached types with their records.</param>
    /// <param name="systemProperties">The partition's system properties; none for a cache no capture has asked the platform about.</param>
    public ReferenceSnapshot(string version, DateTimeOffset capturedUtc, IEnumerable<ReferenceType> types, IEnumerable<SystemProperty>? systemProperties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(types);
        Version = version;
        CapturedUtc = capturedUtc;
        _types = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        SystemProperties = Snapshots.SystemProperties.Ordered(systemProperties ?? []);
    }

    public string Version { get; }

    public DateTimeOffset CapturedUtc { get; }

    public IReadOnlyCollection<ReferenceType> Types => _types.Values;

    /// <summary>
    /// The partition's system properties as the capture that wrote the version found them, by service and name. They are
    /// kept apart from the types: they describe how the platform indexes and searches the partition, not any record of it.
    /// </summary>
    public IReadOnlyList<SystemProperty> SystemProperties { get; }

    public bool HasType(string name) => _types.ContainsKey(name);

    public ReferenceType? Type(string name) => _types.GetValueOrDefault(name);

    /// <summary>The system property <paramref name="name"/> of <paramref name="service"/>, or null when the version does not name it.</summary>
    public SystemProperty? SystemPropertyOf(string service, string name)
        => SystemProperties.FirstOrDefault(p => string.Equals(p.Service, service, StringComparison.Ordinal) && string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>An empty snapshot, for mappings that resolve no references.</summary>
    public static ReferenceSnapshot Empty { get; } = new("none", DateTimeOffset.UnixEpoch, []);

    /// <summary>
    /// Hash of the whole snapshot content, so a version label can be checked against what it holds. The system properties
    /// enter it by service, name and state, which is what a render reads of them, and not by the words a service or a
    /// failed read explained them with, which change without the property changing. A version without system properties
    /// hashes as every version did before a capture recorded them, so the versions written then still load.
    /// </summary>
    public string ContentHash() => Hashes().Content;

    /// <summary>
    /// The hash of the whole content (<see cref="ContentHash"/>) and of each type's content
    /// (<see cref="ReferenceType.ContentHash"/>), from one pass over the records: a type's hash is taken over exactly the bytes
    /// the type contributes to the whole, so the two never disagree about what a type holds. The whole hash says whether a
    /// version changed at all; a type's hash says whether that type did, whatever else the version changed. Worked out once
    /// per snapshot, which never changes once built.
    /// </summary>
    public SnapshotHashes Hashes() => LazyInitializer.EnsureInitialized(ref _hashes, ComputeHashes);

    private SnapshotHashes? _hashes;

    private SnapshotHashes ComputeHashes()
    {
        // The document is { <type>: { entityType, items: [...], key? } } by type name, or with system properties
        // { systemProperties: [...], types: { ... } }, in canonical JSON. It is written a record at a time into the hash
        // rather than built whole: a type of hundreds of thousands of records would otherwise be drawn as JSON twice over
        // on every load. The bytes are the ones the whole document would give, so every version written before still loads.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using (var writer = CanonicalJson.CreateWriter(buffer))
        {
            // While a type's value is being written its bytes go to the type's own hash as well as to the whole one. The
            // writer is not indented, so the property name and the separator before it are drained before the value
            // starts, and the value's bytes are exactly those the type written on its own gives.
            IncrementalHash? typeHash = null;
            void Drain()
            {
                writer.Flush();
                hash.AppendData(buffer.WrittenSpan);
                typeHash?.AppendData(buffer.WrittenSpan);
                buffer.ResetWrittenCount();
            }

            writer.WriteStartObject();
            if (SystemProperties.Count > 0)
            {
                var properties = new JsonArray();
                foreach (var property in SystemProperties)
                {
                    properties.Add(new JsonObject
                    {
                        ["service"] = property.Service,
                        ["name"] = property.Name,
                        ["state"] = property.State.ToString(),
                    });
                }

                writer.WritePropertyName("systemProperties");
                CanonicalJson.WriteTo(writer, properties);
                writer.WritePropertyName("types");
                writer.WriteStartObject();
            }

            foreach (var type in _types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(type.Name);
                Drain();
                using var own = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                typeHash = own;
                try
                {
                    type.WriteCanonical(writer, Drain);
                    Drain();
                }
                finally
                {
                    typeHash = null;
                }

                var typed = Convert.ToHexStringLower(own.GetHashAndReset());
                type.KeepContentHash(typed);
                types[type.Name] = typed;
            }

            if (SystemProperties.Count > 0)
            {
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            Drain();
        }

        return new SnapshotHashes(Convert.ToHexStringLower(hash.GetHashAndReset()), types);
    }

    /// <summary>
    /// The same content with every type's records in ordinal order of their ids: the order a version is stored and loaded
    /// in, so the content hash of what a capture found and of what the catalog holds for it can be compared.
    /// </summary>
    public ReferenceSnapshot Normalized()
        => new(
            Version,
            CapturedUtc,
            _types.Values.Select(t => new ReferenceType(t.Name, t.EntityType, t.Items.OrderBy(i => i.Id, StringComparer.Ordinal), t.Key)),
            SystemProperties);
}

/// <summary>
/// A snapshot's content hashes: <see cref="Content"/> over everything it holds, which the version is checked against, and
/// one per type by name (ignoring case, as mappings name types), over what that type holds.
/// </summary>
public sealed record SnapshotHashes(string Content, IReadOnlyDictionary<string, string> Types)
{
    /// <summary>The content hash of the type <paramref name="name"/>, or null when the snapshot holds no such type.</summary>
    public string? Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Types.GetValueOrDefault(name);
    }
}

/// <summary>All items of one reference (or master-data) type, indexed on the fields a mapping may match by.</summary>
public sealed class ReferenceType
{
    /// <summary>
    /// The entity type prefix of a type whose records are not OSDU records: a lookup table filled from an ingestion table or
    /// a dictionary file, or a replace's own table. No OSDU group is called lookup, so it can never be mistaken for one.
    /// </summary>
    public const string LookupEntityTypePrefix = "lookup--";

    private readonly List<ReferenceItem> _items;

    // Built lazily per field because a snapshot holds more fields than any one mapping matches by, and concurrently
    // because one snapshot is shared by every render worker of a run. A GetOrAdd race builds the index twice and
    // keeps one; the loser is discarded, which is wasted work rather than a wrong answer.
    private readonly ConcurrentDictionary<string, FieldIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);

    // The combinations of fields findBy lines compare together, each indexed on first use as a single field is, by the
    // fields' names joined by a line break, which no field name holds.
    private readonly ConcurrentDictionary<string, CompositeIndex> _composites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _recordIdPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _fieldNames;
    private Dictionary<string, ReferenceItem>? _byId;
    private IReadOnlyList<ReferenceItem>? _ordered;
    private string? _contentHash;

    /// <param name="name">The short name mappings use.</param>
    /// <param name="entityType">The OSDU entity type, or <see cref="LookupEntityType"/> of the name for a lookup table.</param>
    /// <param name="items">The records.</param>
    /// <param name="key">For a lookup table, the name its key is kept under; null for a type of OSDU records.</param>
    public ReferenceType(string name, string entityType, IEnumerable<ReferenceItem> items, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(items);
        Name = name;
        EntityType = entityType;
        _items = items.ToList();
        var lookup = entityType.StartsWith(LookupEntityTypePrefix, StringComparison.Ordinal);
        if (lookup != key is not null)
        {
            throw new DeliveryException(lookup
                ? $"Lookup table '{name}' ({entityType}) names no key; every lookup row is kept under its key."
                : $"Type '{name}' holds OSDU records ({entityType}), which are kept under their ids, so it names no key.");
        }

        Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>The entity type a lookup table named <paramref name="name"/> is kept under.</summary>
    public static string LookupEntityType(string name) => LookupEntityTypePrefix + name;

    /// <summary>Short name used by mappings (UnitOfMeasure, Wellbore).</summary>
    public string Name { get; }

    /// <summary>OSDU entity type (reference-data--UnitOfMeasure, master-data--Wellbore), or lookup--&lt;Name&gt; for a lookup table.</summary>
    public string EntityType { get; }

    /// <summary>
    /// For a lookup table, the name its key is kept under: each row's id is its key, and the key is a field under this name
    /// too, so a mapping matches on it by name. Null for a type of OSDU records.
    /// </summary>
    public string? Key { get; }

    /// <summary>True for a table whose rows are not OSDU records: filled from an ingestion table or a dictionary, it has no ids to write.</summary>
    public bool IsLookup => Key is not null;

    public IReadOnlyList<ReferenceItem> Items => _items;

    /// <summary>The captured field names, in the order a mapping would see them. <c>id</c> is always available too.</summary>
    public IReadOnlyList<string> FieldNames
    {
        get
        {
            if (_fieldNames is not null)
            {
                return _fieldNames;
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
            {
                foreach (var name in item.Fields.Keys)
                {
                    if (seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }

            names.Sort(StringComparer.Ordinal);
            _fieldNames = names;
            return names;
        }
    }

    /// <summary>True when at least one item carries the field (or path into a field), so a mapping can match on it.</summary>
    public bool HasField(string field) => Index(field).Count > 0;

    /// <summary>
    /// The value an item holds at <paramref name="path"/>: a cached field, a path into one, or the record id. A
    /// type that caches a field of its own called <c>ID</c> (OSDU reference data does) shadows the record id under
    /// that name, so a mapping matching by <c>ID</c> gets the cached field and one matching by <c>id</c> on a type
    /// that caches no such field gets the record id.
    /// </summary>
    public ReferenceValue? Value(ReferenceItem item, string path)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Select(path) ?? (MeansRecordId(path) ? ReferenceValue.Of(item.Id) : null);
    }

    /// <summary>True when the path names the record id rather than a cached field of the same name.</summary>
    public bool MeansRecordId(string path)
    {
        if (!ReferenceField.IsId(path))
        {
            return false;
        }

        // Asked of every value read by id, so each spelling is answered once rather than by a pass over every item.
        var name = ReferenceField.Normalize(path);
        return _recordIdPaths.GetOrAdd(name, n => !_items.Any(item => item.Fields.ContainsKey(n)));
    }

    /// <summary>
    /// The item whose <paramref name="field"/> holds <paramref name="value"/>, as <see cref="Find(string, string, bool)"/> matches it: null
    /// when nothing matches, and when the value names several items.
    /// </summary>
    public ReferenceItem? Match(string field, string value, bool ignoreSeparators = false)
        => Find(field, value, ignoreSeparators).Item;

    /// <summary>
    /// Matches <paramref name="value"/> (trimmed) against what <paramref name="field"/> holds. A field holding a set
    /// matches when any one of its values does, so an item with three aliases is found by any of them.
    /// <para>
    /// An exact match of exactly one item wins. OSDU codes that differ only by case are different records (<c>ft</c> is
    /// the foot and <c>fT</c> the femtotesla; <c>s/m</c> is second per metre and <c>S/m</c> siemens per metre), so case is
    /// ignored only when that finds exactly one item. A value that names several items, exactly or once case is ignored,
    /// matches none of them and lists them as <see cref="ReferenceMatch.CaseVariants"/>, instead of resolving to
    /// whichever comes first: picking one would write a reference nobody chose.
    /// </para>
    /// <para>
    /// With <paramref name="ignoreSeparators"/> a third and last attempt folds punctuation and spacing away on both
    /// sides (<see cref="ReferenceKeyFold"/>), so a name a source writes as <c>Wellbore A/1-2 ST</c> finds the record OSDU
    /// holds as <c>Wellbore_A_1-2_ST</c>. It is opt-in per mapping entry, because a fold that helps a facility name is
    /// exactly wrong for a unit code, and it keeps the same discipline as the case tier: several items under one folded
    /// key match none of them and are listed, rather than one being picked.
    /// </para>
    /// </summary>
    public ReferenceMatch Find(string field, string value, bool ignoreSeparators = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return value is null ? ReferenceMatch.None : Index(field).Lookup(value.Trim(), ignoreSeparators);
    }

    /// <summary>
    /// Matches several values at once, each against its own field: the item that holds every one of them, as a findBy line
    /// written with <c>and</c> asks (<c>Well = well_name and Mnemonic = curve_mnemonic</c>). One comparison is
    /// <see cref="Find(string, string, bool)"/>.
    /// <para>
    /// The tiers are a single field's, applied to the comparisons together: the items holding every value exactly, then,
    /// only when no item does, the items holding every value once case is ignored, then, only with
    /// <paramref name="ignoreSeparators"/> and only when that finds none either, the items holding every value once
    /// punctuation and spacing are folded away too. Exactly one item at the first tier that finds any is the match; several
    /// match none of them and are listed, as several answering one value are. A field holding a set meets a comparison when
    /// any one of its values does.
    /// </para>
    /// <para>
    /// Each combination of fields a line compares is indexed the first time a line asks for it, as a single field is: every
    /// item under the values it holds at those fields together, so a lookup is one probe however common each value is alone.
    /// The index lives with this version of the type, for the combinations mappings compare, and is built again from the
    /// next version a render loads; nothing of it is stored. A combination an item holds more than
    /// <see cref="MaxCombinationsPerItem"/> sets of values under (sets at several fields multiply) is not indexed, and is
    /// matched by reading the items the rarest of its values names instead, which finds the same items.
    /// </para>
    /// </summary>
    public ReferenceMatch Find(IReadOnlyList<(string Field, string Value)> comparisons, bool ignoreSeparators = false)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        if (comparisons.Count == 0)
        {
            throw new ArgumentException("A match compares at least one field.", nameof(comparisons));
        }

        if (comparisons.Count == 1)
        {
            return Find(comparisons[0].Field, comparisons[0].Value, ignoreSeparators);
        }

        var asked = new List<(string Field, string Value)>(comparisons.Count);
        foreach (var (field, value) in comparisons)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
            if (string.IsNullOrWhiteSpace(value))
            {
                return ReferenceMatch.None;
            }

            asked.Add((ReferenceField.Normalize(field), value.Trim()));
        }

        var fields = asked.Select(a => a.Field).ToArray();
        var values = asked.Select(a => a.Value).ToArray();
        var index = _composites.GetOrAdd(string.Join('\n', fields), _ => BuildComposite(fields));
        var tiers = ignoreSeparators
            ? new[] { ReferenceMatchKind.Exact, ReferenceMatchKind.IgnoringCase, ReferenceMatchKind.IgnoringSeparators }
            : [ReferenceMatchKind.Exact, ReferenceMatchKind.IgnoringCase];
        foreach (var tier in tiers)
        {
            var holders = index.IsIndexed ? index.Holders(values, tier) : Holding(asked, tier);
            if (holders.Count == 1)
            {
                return ReferenceMatch.Of(holders[0], tier);
            }

            if (holders.Count > 1)
            {
                return new ReferenceMatch(null, holders, tier);
            }
        }

        return ReferenceMatch.None;
    }

    /// <summary>
    /// The most sets of values one item may hold under a combination of fields for the combination to be indexed. A field
    /// holding a set gives one per value, and sets at several fields multiply (ten aliases beside ten names are a hundred),
    /// so a combination some item holds more of is matched by <see cref="Holding"/> instead of growing the index without
    /// bound.
    /// </summary>
    public const int MaxCombinationsPerItem = 64;

    /// <summary>
    /// The index of one combination of fields (<see cref="CompositeIndex"/>): each item under every set of values it holds
    /// at those fields together, exactly and without case, and with punctuation and spacing folded away on first use.
    /// <see cref="CompositeIndex.Unindexed"/> when an item holds more than <see cref="MaxCombinationsPerItem"/> sets.
    /// </summary>
    private CompositeIndex BuildComposite(string[] fields)
    {
        var exact = new Dictionary<string[], object>(TermsComparer.Ordinal);
        var folded = new Dictionary<string[], object>(TermsComparer.IgnoringCase);
        foreach (var item in _items)
        {
            if (Combinations(item, fields, fold: false) is not { } sets)
            {
                return CompositeIndex.Unindexed;
            }

            foreach (var set in sets)
            {
                FieldIndex.Add(exact, set, item);
                FieldIndex.Add(folded, set, item);
            }
        }

        return new CompositeIndex(exact, folded, () =>
        {
            var separated = new Dictionary<string[], object>(TermsComparer.Ordinal);
            foreach (var item in _items)
            {
                foreach (var set in Combinations(item, fields, fold: true) ?? [])
                {
                    FieldIndex.Add(separated, set, item);
                }
            }

            return separated;
        });
    }

    /// <summary>
    /// Every set of values <paramref name="item"/> holds at <paramref name="fields"/> together, one value per field, in the
    /// order of the fields: none when it holds nothing at one of them, and null when it holds more than
    /// <see cref="MaxCombinationsPerItem"/>. With <paramref name="fold"/> each value is folded as
    /// <see cref="ReferenceKeyFold.Separators"/> folds it, and one that folds to nothing is left out, as the single-field
    /// index leaves it out.
    /// </summary>
    private List<string[]>? Combinations(ReferenceItem item, string[] fields, bool fold)
    {
        var sets = new List<string[]> { new string[fields.Length] };
        for (var i = 0; i < fields.Length; i++)
        {
            var terms = (TermsOf(item, fields[i]) ?? [])
                .Select(term => fold ? ReferenceKeyFold.Separators(term) : term)
                .Where(term => term.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (terms.Count == 0)
            {
                return [];
            }

            if ((long)sets.Count * terms.Count > MaxCombinationsPerItem)
            {
                return null;
            }

            var grown = new List<string[]>(sets.Count * terms.Count);
            foreach (var set in sets)
            {
                foreach (var term in terms)
                {
                    var next = (string[])set.Clone();
                    next[i] = term;
                    grown.Add(next);
                }
            }

            sets = grown;
        }

        return sets;
    }

    /// <summary>
    /// The terms <paramref name="item"/> holds under <paramref name="field"/>, as the field's index is built from them
    /// (<see cref="Terms"/>): its values, or for the record id the id and its code; null when it holds nothing there.
    /// </summary>
    private IReadOnlyList<string>? TermsOf(ReferenceItem item, string field)
        => Value(item, field) is not { } held ? null : MeansRecordId(field) ? RecordIdTerms(item.Id).ToList() : held.Terms;

    /// <summary>
    /// The items, in snapshot order, that meet every comparison at <paramref name="tier"/>, for a combination too wide to
    /// index: the comparison whose value the fewest items hold names the candidates, and each is kept when it holds every
    /// other comparison's value too, compared as that tier compares.
    /// </summary>
    private List<ReferenceItem> Holding(List<(string Field, string Value)> asked, ReferenceMatchKind tier)
    {
        var candidates = new IReadOnlyList<ReferenceItem>[asked.Count];
        var fewest = 0;
        for (var i = 0; i < asked.Count; i++)
        {
            candidates[i] = Index(asked[i].Field).Holders(asked[i].Value, tier);
            if (candidates[i].Count == 0)
            {
                return [];
            }

            if (candidates[i].Count < candidates[fewest].Count)
            {
                fewest = i;
            }
        }

        var held = new List<ReferenceItem>();
        foreach (var item in candidates[fewest])
        {
            var meetsAll = true;
            for (var i = 0; i < asked.Count && meetsAll; i++)
            {
                meetsAll = i == fewest || Holds(item, asked[i].Field, asked[i].Value, tier);
            }

            if (meetsAll)
            {
                held.Add(item);
            }
        }

        return held;
    }

    /// <summary>
    /// Whether <paramref name="item"/> holds <paramref name="value"/> under <paramref name="field"/> as <paramref name="tier"/>
    /// compares: the terms the field's index is built from (<see cref="Terms"/>), compared ordinally, without case, or folded
    /// as <see cref="ReferenceKeyFold.Separators"/> folds them, exactly as the index itself finds them.
    /// </summary>
    private bool Holds(ReferenceItem item, string field, string value, ReferenceMatchKind tier)
    {
        if (TermsOf(item, field) is not { } terms)
        {
            return false;
        }

        if (tier == ReferenceMatchKind.IgnoringSeparators)
        {
            var key = ReferenceKeyFold.Separators(value);
            return key.Length > 0 && terms.Any(term => string.Equals(ReferenceKeyFold.Separators(term), key, StringComparison.Ordinal));
        }

        var comparison = tier == ReferenceMatchKind.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return terms.Any(term => string.Equals(term, value, comparison));
    }

    /// <summary>True when two or more items hold exactly the same value under this field, so matching on it is order-dependent.</summary>
    public bool IsAmbiguous(string field) => Index(field).Ambiguous;

    /// <summary>
    /// Every item whose <paramref name="field"/> holds <paramref name="value"/> (trimmed) without regard to case, in
    /// snapshot order: a field holding a set is held by an item when any one of its values is. Where <see cref="Find(string, string, bool)"/>
    /// names the one record a value stands for, this reads every record keyed by it: the access groups a data office lists
    /// for a field, however many there are. None when nothing holds the value.
    /// </summary>
    public IReadOnlyList<ReferenceItem> FindAll(string field, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return string.IsNullOrWhiteSpace(value) ? [] : Index(field).All(value.Trim());
    }

    /// <summary>True when <paramref name="item"/> holds nothing under <paramref name="field"/>: no value, or a set without one.</summary>
    public bool HoldsNothing(ReferenceItem item, string field)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Value(item, field) is not { } value || value.Terms.Count == 0;
    }

    /// <summary>
    /// The item whose record id is exactly <paramref name="id"/>, whatever the type caches under a field of its own called
    /// <c>ID</c>: where a value already is an OSDU record id, the record it names is looked for by that id, never by a
    /// captured field that happens to share the name. Built once, on first use, and shared by every render worker.
    /// </summary>
    public ReferenceItem? ById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var byId = LazyInitializer.EnsureInitialized(
            ref _byId,
            () => _items.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        return byId.GetValueOrDefault(id);
    }

    private FieldIndex Index(string field) => _indexes.GetOrAdd(ReferenceField.Normalize(field), BuildIndex);

    private FieldIndex BuildIndex(string field)
    {
        var exact = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
        // Terms that more than one distinct item holds exactly, with every such item in snapshot order.
        var duplicates = new Dictionary<string, List<ReferenceItem>>(StringComparer.Ordinal);
        // The same term under folded case, every distinct item that holds it, in snapshot order: a lookup that has no exact
        // match takes it only when there is exactly one. Nearly every term of a large type is held by one item, which is
        // kept as itself; a list is made only for a term several items hold.
        var folded = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = false;
        var recordId = MeansRecordId(field);
        foreach (var (item, term) in Terms(field, recordId))
        {
            if (!exact.TryAdd(term, item) && exact[term] != item)
            {
                ambiguous = true;
                if (!duplicates.TryGetValue(term, out var holders))
                {
                    holders = [exact[term]];
                    duplicates[term] = holders;
                }

                // An item's terms come together (Terms), so an item the term already lists is the last one listed. Asking the
                // last alone keeps a term thousands of items share (a wellbore alias "1") from costing a pass over the list
                // for every item that holds it.
                if (holders[^1] != item)
                {
                    holders.Add(item);
                }
            }

            FieldIndex.Add(folded, term, item);
        }

        return new FieldIndex(exact, duplicates, folded, ambiguous, () => SeparatorIndex(field, recordId));
    }

    /// <summary>
    /// Every term the field gives, with the item that gives it, in snapshot order: a scalar contributes one, a set one per
    /// element, so an item with three aliases is found by any of them. The record id is found by its code as well.
    /// </summary>
    private IEnumerable<(ReferenceItem Item, string Term)> Terms(string field, bool recordId)
    {
        foreach (var item in _items)
        {
            if (Value(item, field) is not { } value)
            {
                continue;
            }

            foreach (var term in recordId ? RecordIdTerms(item.Id) : value.Terms)
            {
                yield return (item, term);
            }
        }
    }

    /// <summary>
    /// The field's terms with punctuation and spacing folded away, for the last matching tier: built the first time a
    /// mapping asks for that tier, since most never do and it holds a folded key for every term of the field.
    /// </summary>
    private Dictionary<string, object> SeparatorIndex(string field, bool recordId)
    {
        var separatorFolded = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (item, term) in Terms(field, recordId))
        {
            // A term that folds to nothing (punctuation only) would collect every such term under one empty key and make
            // the fold tier useless, so it contributes nothing to it.
            if (ReferenceKeyFold.Separators(term) is { Length: > 0 } key)
            {
                FieldIndex.Add(separatorFolded, key, item);
            }
        }

        return separatorFolded;
    }

    /// <summary>
    /// The terms a record is found by through its id: the id itself, and the code it ends with, both as the id writes it
    /// (<c>Gamma%20Ray</c>) and decoded (<c>Gamma Ray</c>). A reference to OSDU reference data is the partition, the entity
    /// type and that code (<c>{partition}:reference-data--LogCurveFamily:Gamma%20Ray:</c>), so a table that names reference
    /// data by its code (a curve dictionary giving each mnemonic its family) finds the one record of the type it names.
    /// </summary>
    private static IEnumerable<string> RecordIdTerms(string id)
    {
        yield return id;
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[2].Length == 0)
        {
            yield break;
        }

        var code = parts[2];
        yield return code;
        // A malformed escape is left as the id writes it.
        var decoded = Uri.UnescapeDataString(code);
        if (!string.Equals(decoded, code, StringComparison.Ordinal))
        {
            yield return decoded;
        }
    }

    public JsonObject ToJson()
    {
        var items = new JsonArray();
        foreach (var item in _items)
        {
            items.Add(ItemJson(item));
        }

        // The key is written only for a lookup table, so a type of OSDU records hashes as it always has and every version
        // written before lookup tables existed still loads.
        var json = new JsonObject { ["entityType"] = EntityType };
        if (Key is not null)
        {
            json["key"] = Key;
        }

        json["items"] = items;
        return json;
    }

    /// <summary>
    /// Hash of what the type holds: its entity type, for a lookup table the name its key is kept under, and every record with
    /// the values captured for it, the records in ordinal order of their ids, so the order a capture found them in does not
    /// move it. It is taken over the bytes the type contributes to <see cref="ReferenceSnapshot.ContentHash"/>, and it does
    /// not cover the type's name, which the version keeps it under. Two versions whose hashes of a type agree hold exactly
    /// the same records of it with exactly the same values, however much else changed between them.
    /// </summary>
    public string ContentHash()
    {
        if (Volatile.Read(ref _contentHash) is { } known)
        {
            return known;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using (var writer = CanonicalJson.CreateWriter(buffer))
        {
            void Drain()
            {
                writer.Flush();
                hash.AppendData(buffer.WrittenSpan);
                buffer.ResetWrittenCount();
            }

            WriteCanonical(writer, Drain);
            Drain();
        }

        var computed = Convert.ToHexStringLower(hash.GetHashAndReset());
        KeepContentHash(computed);
        return computed;
    }

    /// <summary>Keeps the hash a pass over the whole snapshot took of this type's bytes, so asking for it again costs nothing.</summary>
    internal void KeepContentHash(string hash) => Volatile.Write(ref _contentHash, hash);

    /// <summary>
    /// Writes <see cref="ToJson"/> in canonical form, a record at a time, calling <paramref name="drain"/> every so many
    /// records so the writer's output can be taken away: the bytes <c>CanonicalJson.ToBytes(ToJson())</c> gives for the
    /// records in ordinal order of their ids, without drawing the whole type as JSON first. A type the store wrote or loaded
    /// already holds its records in that order, so what every version written before hashed to is what it hashes to now.
    /// </summary>
    internal void WriteCanonical(Utf8JsonWriter writer, Action drain)
    {
        const int RecordsPerDrain = 1024;
        var items = Ordered();
        writer.WriteStartObject();
        writer.WriteString("entityType", EntityType);
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        for (var i = 0; i < items.Count; i++)
        {
            CanonicalJson.WriteTo(writer, ItemJson(items[i]));
            if (i % RecordsPerDrain == RecordsPerDrain - 1)
            {
                drain();
            }
        }

        writer.WriteEndArray();
        if (Key is not null)
        {
            writer.WriteString("key", Key);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The records in ordinal order of their ids: the list itself when it is in that order already, as every type the store
    /// writes or loads is, and otherwise a copy sorted once (stably, so records sharing an id keep the order they came in).
    /// </summary>
    private IReadOnlyList<ReferenceItem> Ordered()
        => LazyInitializer.EnsureInitialized(ref _ordered, () =>
        {
            for (var i = 1; i < _items.Count; i++)
            {
                if (string.CompareOrdinal(_items[i - 1].Id, _items[i].Id) > 0)
                {
                    return _items.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
                }
            }

            return _items;
        });

    private static JsonObject ItemJson(ReferenceItem item)
    {
        var o = new JsonObject { ["id"] = item.Id };
        foreach (var f in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            o[f.Key] = f.Value.Node.DeepClone();
        }

        return o;
    }

    public static ReferenceType FromJson(string name, JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var entityType = node["entityType"]?.GetValue<string>() ?? throw new DeliveryException($"Reference type '{name}' has no entityType.");
        var items = new List<ReferenceItem>();
        var pool = new StringPool();
        if (node["items"] is JsonArray arr)
        {
            foreach (var element in arr.OfType<JsonObject>())
            {
                var id = element["id"]?.GetValue<string>() ?? throw new DeliveryException($"Reference type '{name}' has an item without id.");
                var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in element)
                {
                    // Any JSON shape survives the round trip: a scalar, a set of values, or a nested object.
                    if (kv.Key != "id" && kv.Value is { } value)
                    {
                        fields[kv.Key] = ReferenceValue.From(value, pool);
                    }
                }

                items.Add(new ReferenceItem(id, ReferenceFields.Of(fields, pool)));
            }
        }

        return new ReferenceType(name, entityType, items, node["key"]?.GetValue<string>());
    }

    /// <summary>
    /// One combination of fields' index (<see cref="BuildComposite"/>): each set of values the items hold at those fields
    /// together, exactly and without case, naming one item as itself or several as a list, in snapshot order; and, built on
    /// first use, the sets with punctuation and spacing folded away. <see cref="Unindexed"/> stands for a combination
    /// matched by reading candidates instead.
    /// </summary>
    private sealed class CompositeIndex
    {
        private readonly Dictionary<string[], object>? _exact;
        private readonly Dictionary<string[], object>? _folded;
        private readonly Func<Dictionary<string[], object>>? _buildSeparated;
        private Dictionary<string[], object>? _separated;

        public CompositeIndex(Dictionary<string[], object>? exact, Dictionary<string[], object>? folded, Func<Dictionary<string[], object>>? buildSeparated)
        {
            _exact = exact;
            _folded = folded;
            _buildSeparated = buildSeparated;
        }

        /// <summary>A combination some item holds too many sets of values under to index (<see cref="MaxCombinationsPerItem"/>).</summary>
        public static CompositeIndex Unindexed { get; } = new(null, null, null);

        public bool IsIndexed => _exact is not null;

        /// <summary>
        /// Every distinct item holding <paramref name="values"/> at the combination's fields together as
        /// <paramref name="tier"/> compares, in snapshot order: exactly, without case, or with punctuation and spacing folded
        /// away too. None for values one of which folds to nothing.
        /// </summary>
        public IReadOnlyList<ReferenceItem> Holders(string[] values, ReferenceMatchKind tier)
        {
            if (_exact is null || _folded is null || _buildSeparated is null)
            {
                throw new InvalidOperationException("A combination that is not indexed is matched by reading its candidates.");
            }

            switch (tier)
            {
                case ReferenceMatchKind.Exact:
                    return FieldIndex.Items(_exact, values);
                case ReferenceMatchKind.IgnoringCase:
                    return FieldIndex.Items(_folded, values);
                case ReferenceMatchKind.IgnoringSeparators:
                    var folded = values.Select(ReferenceKeyFold.Separators).ToArray();
                    if (folded.Any(value => value.Length == 0))
                    {
                        return [];
                    }

                    // Built once and shared by every render worker; a race builds it twice and keeps one.
                    return FieldIndex.Items(LazyInitializer.EnsureInitialized(ref _separated, _buildSeparated), folded);
                default:
                    throw new ArgumentOutOfRangeException(nameof(tier), tier, "Items are held by values exactly, without case, or folded.");
            }
        }
    }

    /// <summary>Compares two sets of values element by element, each pair as one string comparer compares them.</summary>
    private sealed class TermsComparer : IEqualityComparer<string[]>
    {
        private readonly StringComparer _comparer;

        private TermsComparer(StringComparer comparer) => _comparer = comparer;

        public static TermsComparer Ordinal { get; } = new(StringComparer.Ordinal);

        public static TermsComparer IgnoringCase { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Equals(string[]? x, string[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (var i = 0; i < x.Length; i++)
            {
                if (!_comparer.Equals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(string[] obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            var hash = new HashCode();
            foreach (var value in obj)
            {
                hash.Add(value, _comparer);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// One field's index: its terms exactly, the terms several items hold, and the terms under folded case, each naming one
    /// item as itself or several as a list; and, built on first use, the terms with punctuation and spacing folded away.
    /// </summary>
    private sealed class FieldIndex
    {
        private readonly Dictionary<string, ReferenceItem> _exact;
        private readonly Dictionary<string, List<ReferenceItem>> _duplicates;
        private readonly Dictionary<string, object> _folded;
        private readonly Func<Dictionary<string, object>> _buildSeparatorFolded;
        private Dictionary<string, object>? _separatorFolded;

        public FieldIndex(
            Dictionary<string, ReferenceItem> exact, Dictionary<string, List<ReferenceItem>> duplicates, Dictionary<string, object> folded,
            bool ambiguous, Func<Dictionary<string, object>> buildSeparatorFolded)
        {
            _exact = exact;
            _duplicates = duplicates;
            _folded = folded;
            _buildSeparatorFolded = buildSeparatorFolded;
            Ambiguous = ambiguous;
        }

        public int Count => _exact.Count;

        public bool Ambiguous { get; }

        /// <summary>
        /// Adds <paramref name="item"/> to the items <paramref name="key"/> names, once, keeping them in the order they are
        /// added. Items are added as <see cref="ReferenceType.Terms"/> gives them, every term of one item before the next
        /// item's, so an item the key already names is the last it names, and only the last is compared.
        /// </summary>
        public static void Add<TKey>(Dictionary<TKey, object> index, TKey key, ReferenceItem item)
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out var held))
            {
                index[key] = item;
            }
            else if (held is ReferenceItem one)
            {
                if (one != item)
                {
                    index[key] = new List<ReferenceItem>(2) { one, item };
                }
            }
            else if (held is List<ReferenceItem> many && many[^1] != item)
            {
                many.Add(item);
            }
        }

        /// <summary>Every distinct item holding the term under folded case, in snapshot order.</summary>
        public IReadOnlyList<ReferenceItem> All(string term) => Items(_folded, term);

        /// <summary>
        /// Every distinct item holding the term as <paramref name="tier"/> compares, in snapshot order: exactly, without case,
        /// or with punctuation and spacing folded away too. None for a term that folds to nothing.
        /// </summary>
        public IReadOnlyList<ReferenceItem> Holders(string term, ReferenceMatchKind tier)
        {
            switch (tier)
            {
                case ReferenceMatchKind.Exact:
                    return _duplicates.TryGetValue(term, out var holders) ? holders
                        : _exact.TryGetValue(term, out var item) ? [item]
                        : [];
                case ReferenceMatchKind.IgnoringCase:
                    return Items(_folded, term);
                case ReferenceMatchKind.IgnoringSeparators:
                    if (ReferenceKeyFold.Separators(term) is not { Length: > 0 } key)
                    {
                        return [];
                    }

                    // Built once and shared by every render worker; a race builds it twice and keeps one.
                    return Items(LazyInitializer.EnsureInitialized(ref _separatorFolded, _buildSeparatorFolded), key);
                default:
                    throw new ArgumentOutOfRangeException(nameof(tier), tier, "Items are held by a term exactly, without case, or folded.");
            }
        }

        public ReferenceMatch Lookup(string term, bool ignoreSeparators)
        {
            if (_duplicates.TryGetValue(term, out var holders))
            {
                return new ReferenceMatch(null, holders, ReferenceMatchKind.Exact);
            }

            if (_exact.TryGetValue(term, out var item))
            {
                return ReferenceMatch.Of(item, ReferenceMatchKind.Exact);
            }

            if (Items(_folded, term) is { Count: > 0 } variants)
            {
                return variants.Count == 1
                    ? ReferenceMatch.Of(variants[0], ReferenceMatchKind.IgnoringCase)
                    : new ReferenceMatch(null, variants, ReferenceMatchKind.IgnoringCase);
            }

            // Only now, and only when the mapping asked: the looser a tier is, the later it runs, so a value that
            // resolves exactly is never decided by a fold.
            if (!ignoreSeparators || ReferenceKeyFold.Separators(term) is not { Length: > 0 } key)
            {
                return ReferenceMatch.None;
            }

            // Built once and shared by every render worker; a race builds it twice and keeps one.
            var separatorFolded = LazyInitializer.EnsureInitialized(ref _separatorFolded, _buildSeparatorFolded);
            if (Items(separatorFolded, key) is not { Count: > 0 } folded)
            {
                return ReferenceMatch.None;
            }

            return folded.Count == 1
                ? ReferenceMatch.Of(folded[0], ReferenceMatchKind.IgnoringSeparators)
                : new ReferenceMatch(null, folded, ReferenceMatchKind.IgnoringSeparators);
        }

        public static IReadOnlyList<ReferenceItem> Items<TKey>(Dictionary<TKey, object> index, TKey key)
            where TKey : notnull => index.TryGetValue(key, out var held)
            ? held switch
            {
                ReferenceItem one => [one],
                List<ReferenceItem> many => many,
                _ => [],
            }
            : [];
    }
}

/// <summary>
/// How a value was matched against a cached field, loosest last. It is reported so a refusal can say which tier could
/// not decide, and so a caller can tell a name that matched as written from one that matched only after folding.
/// </summary>
public enum ReferenceMatchKind
{
    /// <summary>Nothing matched.</summary>
    None,

    /// <summary>The value is what the field holds, character for character.</summary>
    Exact,

    /// <summary>The value matches what the field holds once case is ignored.</summary>
    IgnoringCase,

    /// <summary>The value matches once case, punctuation and spacing are ignored. Only ever reached by a mapping that asked for it.</summary>
    IgnoringSeparators,
}

/// <summary>
/// The folded form of a name, for matching one system's spelling of it against another's.
/// <para>
/// Source systems and OSDU write the same facility name differently, because each grew its own convention for the
/// spaces, slashes, underscores and hyphens between the parts that carry the meaning: <c>Wellbore A/1-2 ST</c>,
/// <c>Wellbore_A_1-2_ST</c> and <c>wellbore-a-1-2-st</c> all name one wellbore. Folding keeps the letters and digits, in order,
/// and replaces every run of anything else with a single separator, so those three fold to one key while two genuinely
/// different names stay apart.
/// </para>
/// <para>
/// It is not a general normaliser and deliberately does nothing clever: no transliteration, no accent stripping, no
/// abbreviation. Letters outside ASCII are kept (names in many languages carry æ, ø and å), lower-cased invariantly, so folding
/// never depends on the machine's locale.
/// </para>
/// </summary>
public static class ReferenceKeyFold
{
    /// <summary>
    /// The folded key of <paramref name="value"/>, or the empty string when it carries no letter or digit at all (a
    /// value made only of punctuation folds to nothing, and nothing is not a key anything should match on).
    /// </summary>
    public static string Separators(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// What matching a value against one cached field found: the item it names, or nothing. When the value names several
/// items, <see cref="Item"/> is null and <see cref="CaseVariants"/> lists them in snapshot order, so the caller can say
/// which records it could not choose between.
/// </summary>
public readonly record struct ReferenceMatch(
    ReferenceItem? Item, IReadOnlyList<ReferenceItem> CaseVariants, ReferenceMatchKind Kind = ReferenceMatchKind.None)
{
    public static ReferenceMatch None => new(null, [], ReferenceMatchKind.None);

    /// <summary>Several items answer to the value, exactly or once a tier loosened the comparison, so none of them is taken.</summary>
    public bool IsCaseAmbiguous => Item is null && CaseVariants.Count > 1;

    /// <summary>How the tier that could not decide was comparing, for a refusal that says what it tried.</summary>
    public string Loosening => Kind switch
    {
        ReferenceMatchKind.Exact => "exactly",
        ReferenceMatchKind.IgnoringSeparators => "once case, punctuation and spacing are ignored",
        _ => "once case is ignored",
    };

    public static ReferenceMatch Of(ReferenceItem item, ReferenceMatchKind kind = ReferenceMatchKind.Exact) => new(item, [], kind);
}

/// <summary>
/// One reference item: the OSDU record id (without version) and the captured fields a mapping may match on or read
/// values from. A field holds whatever OSDU returned at its path: a scalar, a set of values, or a nested object.
/// </summary>
public sealed record ReferenceItem(string Id, IReadOnlyDictionary<string, ReferenceValue> Fields)
{
    /// <summary>An item whose fields are all plain text, which is what most reference types capture.</summary>
    public static ReferenceItem FromText(string id, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var values = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in fields)
        {
            values[name] = ReferenceValue.Of(value);
        }

        return new ReferenceItem(id, values);
    }

    /// <summary>
    /// The cached value at <paramref name="path"/>: a field by name, or a path into a field
    /// (<c>NameAlias.AliasName</c> where <c>NameAlias</c> was cached whole). Null when the item caches nothing
    /// there. The record id is not a cached field; <see cref="ReferenceType.Value"/> answers that too, because
    /// whether <c>id</c> means the record id depends on what the type caches.
    /// </summary>
    public ReferenceValue? Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalised = ReferenceField.Normalize(path);
        if (Fields.TryGetValue(normalised, out var direct))
        {
            return direct;
        }

        // The path may reach inside a field that was captured as a whole object or set.
        foreach (var prefix in ReferenceField.Prefixes(normalised))
        {
            if (Fields.TryGetValue(prefix.Field, out var value))
            {
                return value.Select(prefix.Remainder);
            }
        }

        return null;
    }
}

/// <summary>
/// One captured value: any JSON the path yielded, with the text and terms the cache matches and renders by. Nearly every
/// cached value is a string or a set of strings, and a cache of hundreds of thousands of records (every wellbore of a
/// partition) holds millions of them, so those two are kept as the strings themselves and their JSON is drawn when it
/// is asked for; anything else is kept as the JSON it is.
/// </summary>
public sealed class ReferenceValue
{
    private readonly string? _text;
    private readonly string[]? _texts;
    private readonly JsonNode? _node;
    private IReadOnlyList<string>? _terms;

    private ReferenceValue(string text) => _text = text;

    private ReferenceValue(string[] texts) => _texts = texts;

    private ReferenceValue(JsonNode node) => _node = node;

    /// <summary>
    /// The captured JSON: a scalar, an array (a set of values), or an object. A string or a set of strings is drawn afresh
    /// on every read, so what a caller does with it never reaches the cache.
    /// </summary>
    public JsonNode Node => _text is not null
        ? JsonValue.Create(_text)
        : _texts is not null
            ? new JsonArray(_texts.Select(text => (JsonNode?)JsonValue.Create(text)).ToArray())
            : _node!;

    /// <summary>True when the value holds a set rather than a single value.</summary>
    public bool IsSet => _texts is not null || _node is JsonArray;

    /// <summary>How many values the set holds; 1 for a scalar or an object.</summary>
    public int Count => _texts?.Length ?? (_node is JsonArray array ? array.Count : 1);

    /// <summary>
    /// The value as one string: the scalar itself, the single element of a one-element set, or canonical JSON for
    /// anything composite. Never null, so it is always renderable and always loggable.
    /// </summary>
    public string Text
    {
        get
        {
            if (_text is not null)
            {
                return _text;
            }

            if (_texts is not null)
            {
                return _texts.Length == 1 ? _texts[0] : CanonicalJson.ToString(Node);
            }

            return _node switch
            {
                JsonValue value => Scalar(value) ?? CanonicalJson.ToString(_node),
                JsonArray { Count: 1 } single when single[0] is JsonValue only => Scalar(only) ?? CanonicalJson.ToString(_node),
                _ => CanonicalJson.ToString(_node),
            };
        }
    }

    /// <summary>
    /// Every scalar the value holds, flattened out of arrays and objects, trimmed, without blanks or duplicates.
    /// These are the terms the cache indexes: one for a scalar, one per element for a set.
    /// </summary>
    public IReadOnlyList<string> Terms
    {
        get
        {
            // A string's or a set's terms are worked out when asked rather than kept: they are asked for once, when a
            // field is indexed, and keeping them would double what a large cache holds.
            if (_text is not null)
            {
                return string.IsNullOrWhiteSpace(_text) ? [] : [_text.Trim()];
            }

            var terms = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_texts is not null)
            {
                foreach (var text in _texts)
                {
                    if (!string.IsNullOrWhiteSpace(text) && seen.Add(text.Trim()))
                    {
                        terms.Add(text.Trim());
                    }
                }

                return terms;
            }

            if (_terms is not null)
            {
                return _terms;
            }

            Flatten(_node, terms, seen);
            _terms = terms;
            return terms;
        }
    }

    /// <summary>The value at a path inside this one, or null when it holds nothing there.</summary>
    public ReferenceValue? Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var hits = JsonPathReader.SelectNodes(Node, path);
        return hits.Count switch
        {
            0 => null,
            1 => From(hits[0]),
            _ => OfMany(hits),
        };
    }

    /// <summary>Captures a node as a value. The node is cloned, so the snapshot never aliases the response it came from.</summary>
    public static ReferenceValue From(JsonNode node) => From(node, null);

    /// <summary>
    /// Captures a node as a value, taking its strings from <paramref name="pool"/>: a capture or a load of a large type hands
    /// every value one pool, so a string many records hold (the field a thousand wellbores lie in) is kept once.
    /// </summary>
    internal static ReferenceValue From(JsonNode node, StringPool? pool)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return new ReferenceValue(Pooled(value.GetValue<string>(), pool));
        }

        if (node is JsonArray array && Strings(array, pool) is { } texts)
        {
            return new ReferenceValue(texts);
        }

        return new ReferenceValue(node.DeepClone());
    }

    /// <summary>Captures the nodes a path selected: one value, or a set when the path fanned out.</summary>
    public static ReferenceValue OfMany(IReadOnlyList<JsonNode> nodes) => OfMany(nodes, null);

    /// <summary>Captures the nodes a path selected, taking their strings from <paramref name="pool"/>.</summary>
    internal static ReferenceValue OfMany(IReadOnlyList<JsonNode> nodes, StringPool? pool)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 1)
        {
            return From(nodes[0], pool);
        }

        var array = new JsonArray(nodes.Select(n => n.DeepClone()).ToArray());
        return Strings(array, pool) is { } texts ? new ReferenceValue(texts) : new ReferenceValue(array);
    }

    /// <summary>Captures plain text.</summary>
    public static ReferenceValue Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ReferenceValue(text);
    }

    public override string ToString() => Text;

    /// <summary>The strings of an array that holds nothing but strings, or null for any other array.</summary>
    private static string[]? Strings(JsonArray array, StringPool? pool)
    {
        var texts = new string[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue element || element.GetValueKind() != JsonValueKind.String)
            {
                return null;
            }

            texts[i] = Pooled(element.GetValue<string>(), pool);
        }

        return texts;
    }

    private static string Pooled(string text, StringPool? pool) => pool is null ? text : pool.Get(text);

    private static void Flatten(JsonNode? node, List<string> terms, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonValue value:
                {
                    var text = Scalar(value);
                    if (!string.IsNullOrWhiteSpace(text) && seen.Add(text.Trim()))
                    {
                        terms.Add(text.Trim());
                    }

                    break;
                }

            case JsonArray array:
                foreach (var item in array)
                {
                    Flatten(item, terms, seen);
                }

                break;
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    Flatten(kv.Value, terms, seen);
                }

                break;
        }
    }

    private static string? Scalar(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag ? "true" : "false";
        }

        return CanonicalJson.ToString(value);
    }
}

/// <summary>
/// The strings one capture or one load of a cache version has met, so each distinct string is kept once however many
/// records hold it. It belongs to the one capture or load that fills it, never to a snapshot, and is not shared between
/// threads.
/// </summary>
internal sealed class StringPool
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    public string Get(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_strings.TryGetValue(text, out var kept))
        {
            return kept;
        }

        _strings[text] = text;
        return text;
    }
}

/// <summary>
/// A record's captured fields, kept as one array of names and values rather than a dictionary: a record captures a
/// handful of paths, so reading one by name is a short scan, and a cache of hundreds of thousands of records keeps a
/// dictionary's buckets and entries for none of them. Names compare without regard to case, as a dictionary of captured
/// fields always has.
/// </summary>
internal sealed class ReferenceFields : IReadOnlyDictionary<string, ReferenceValue>
{
    private readonly KeyValuePair<string, ReferenceValue>[] _fields;

    private ReferenceFields(KeyValuePair<string, ReferenceValue>[] fields) => _fields = fields;

    /// <summary>
    /// The fields <paramref name="fields"/> gives, a name given twice (in any case) keeping its last value, as setting a
    /// dictionary by name would, with the names taken from <paramref name="pool"/>.
    /// </summary>
    public static ReferenceFields Of(IEnumerable<KeyValuePair<string, ReferenceValue>> fields, StringPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var kept = new List<KeyValuePair<string, ReferenceValue>>();
        foreach (var (name, value) in fields)
        {
            var at = kept.FindIndex(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase));
            var entry = new KeyValuePair<string, ReferenceValue>(pool is null ? name : pool.Get(name), value);
            if (at >= 0)
            {
                kept[at] = entry;
            }
            else
            {
                kept.Add(entry);
            }
        }

        return new ReferenceFields([.. kept]);
    }

    public ReferenceValue this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The record captures no field '{key}'.");

    public IEnumerable<string> Keys => _fields.Select(f => f.Key);

    public IEnumerable<ReferenceValue> Values => _fields.Select(f => f.Value);

    public int Count => _fields.Length;

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ReferenceValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var (name, held) in _fields)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = held;
                return true;
            }
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, ReferenceValue>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, ReferenceValue>>)_fields).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// How a mapping names a cached field. Capture declares paths (<c>data.Code</c>, <c>data.NameAlias.AliasName</c>)
/// and stores them under the path without its <c>data.</c> root, so a mapping can match by either spelling.
/// </summary>
public static class ReferenceField
{
    private const string DataPrefix = "data.";

    /// <summary>The name a captured path is stored and matched under.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..].TrimStart('.');
        }

        return trimmed.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase) ? trimmed[DataPrefix.Length..] : trimmed;
    }

    /// <summary>True for the record id, which every item carries outside its captured fields.</summary>
    public static bool IsId(string field) => Normalize(field).Equals("id", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ways a dotted path can split into a captured field and a path inside it, longest field first, so
    /// <c>NameAlias.AliasName</c> resolves against a whole <c>NameAlias</c> capture.
    /// </summary>
    public static IEnumerable<(string Field, string Remainder)> Prefixes(string path)
    {
        var segments = Normalize(path).Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var take = segments.Length - 1; take >= 1; take--)
        {
            yield return (string.Join('.', segments[..take]), string.Join('.', segments[take..]));
        }
    }
}
