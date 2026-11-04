using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// What a change to a cached value does to the records that were built from it. The cache is an input to every
/// document it touches, so a changed value means delivered records no longer match what the cache says; the only
/// question is whether that update goes out on its own or waits for someone to approve it.
/// </summary>
public enum CacheChangeMode
{
    /// <summary>Tag the affected records and wait: nothing reaches OSDU until an operator approves the tag.</summary>
    Approve,

    /// <summary>Tag the affected records and let the next run carry them, no approval step.</summary>
    Auto,
}

/// <summary>What a capture reads into a version of a cache: one entry per reference or master-data type.</summary>
public sealed record ReferenceCaptureSpec
{
    public required IReadOnlyList<ReferenceTypeSpec> Types { get; init; }
}

/// <summary>
/// One type a cache holds, as its cache flow declares it: where its records come from (<see cref="Origin"/>) and what of each
/// record the cache keeps. An OSDU type is searched for (<see cref="Kind"/>, <see cref="Query"/>) and keeps paths of each
/// record; a table type reads columns of an ingestion table (<see cref="Table"/>) keyed by one of them; a dictionary type
/// holds the entries of a dictionary document (<see cref="Dictionary"/>), whose key and fields the document names; a
/// dimension type holds the members of a dimension flow's dimension (<see cref="Dimension"/> of <see cref="DimensionFlow"/>),
/// each keyed by its clean value with its originals and records.
/// </summary>
public sealed record ReferenceTypeSpec
{
    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public required string Name { get; init; }

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure), or lookup--&lt;Name&gt; for a type that holds no OSDU records.</summary>
    public required string EntityType { get; init; }

    /// <summary>Where the type's records come from.</summary>
    public CacheOrigin Origin { get; init; } = CacheOrigin.Osdu;

    /// <summary>For an OSDU type: the search kind pattern (osdu:wks:reference-data--UnitOfMeasure:*).</summary>
    public string? Kind { get; init; }

    /// <summary>For a table type: the three-part name of the ingestion table read.</summary>
    public string? Table { get; init; }

    /// <summary>
    /// For a table type: the column each row is keyed by; for a dictionary type, the name its document gives the key, known
    /// once the document is read. The key is also kept as a field under this name, so a mapping matches on it by name.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>For a dictionary type: the name of the dictionary document whose entries the type holds.</summary>
    public string? Dictionary { get; init; }

    /// <summary>For a dimension type: the name of the dimension whose members the type holds.</summary>
    public string? Dimension { get; init; }

    /// <summary>For a dimension type: the name of the dimension flow declaring the dimension.</summary>
    public string? DimensionFlow { get; init; }

    /// <summary>
    /// For a dictionary type whose document has been found: the document's file, relative to the repository root, as the
    /// catalog records it and messages name it. Null as a cache flow declares the type.
    /// </summary>
    public string? DictionaryPath { get; init; }

    /// <summary>
    /// What the cache keeps of each record: for an OSDU type, paths into it (data.Code, data.NameAlias.AliasName), whatever a
    /// path yields cached as it is; for a table type, columns beside the key. The id is always kept. A dictionary type's
    /// cache flow lists none, since its document names them; they are known once the document is read.
    /// </summary>
    public IReadOnlyList<ReferenceFieldSpec> Fields { get; init; } = [];

    /// <summary>For an OSDU type: the search query narrowing the capture (default *).</summary>
    public string Query { get; init; } = "*";

    /// <summary>
    /// For an OSDU type: read each record the search finds back whole from the storage service, and keep the declared paths of
    /// the record as storage holds it rather than of the projection the index returns (osdu/docs/reference/flow/cache-records.md).
    /// The index holds only what the kind's schema describes, so a path the schema leaves open, such as a property of
    /// <c>data.ExtensionProperties</c>, is read this way.
    /// </summary>
    public bool FetchRecords { get; init; }

    /// <summary>
    /// What a change to this type's cached values does to the records built from them: update on the next run (the default)
    /// or, where a flow opts in, wait for an operator to approve the update.
    /// </summary>
    public CacheChangeMode OnChange { get; init; } = CacheChangeMode.Auto;

    /// <summary>
    /// The partitions of its cache flow the type is built for (<c>types[].partitions</c>,
    /// osdu/docs/reference/flow/cache.md, Which partitions a flow fills); empty when it is built for every partition the
    /// flow names, and for a flow that names none.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>True for a type whose records are not OSDU records: a table, a dictionary or a dimension.</summary>
    public bool IsLookup => Origin != CacheOrigin.Osdu;

    /// <summary>Whether the type is built for <paramref name="partition"/>: every type is, unless it names its partitions.</summary>
    public bool IsBuiltFor(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        return Partitions.Count == 0 || Partitions.Contains(partition, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Where the records come from, as a person reads it: the kind searched, the table read, or the dictionary held.</summary>
    public string Describe() => Origin switch
    {
        CacheOrigin.Table => $"table {Table}",
        CacheOrigin.Dictionary => $"dictionary {Dictionary}",
        CacheOrigin.Dimension => $"dimension {Dimension} of {DimensionFlow}",
        _ => $"kind {Kind}",
    };

    /// <summary>Rejects a type that captures nothing, two values kept under one name, or a setting of another origin.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new FlowValidationException("A cached type needs a name.");
        }

        if (string.IsNullOrWhiteSpace(EntityType))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs an entityType.");
        }

        switch (Origin)
        {
            case CacheOrigin.Osdu:
                ValidateOsdu();
                break;
            case CacheOrigin.Table:
                ValidateTable();
                break;
            case CacheOrigin.Dimension:
                ValidateDimension();
                break;
            default:
                ValidateDictionary();
                break;
        }
    }

    private void ValidateOsdu()
    {
        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs a kind to search.");
        }

        Refuse(Table, "table");
        Refuse(Key, "key");
        Refuse(Dictionary, "dictionary");
        Refuse(Dimension, "dimension");
        if (Fields.Count == 0)
        {
            throw new FlowValidationException($"Cached type '{Name}' declares no fields to capture.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            // A field named ID (OSDU reference data has one) is fine and shadows the record id under that name;
            // the exact key 'id' is not, because that is the key the record id itself is written under.
            if (field.Name.Equals("id", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"Cached type '{Name}' caches '{field.Path}' as 'id', which is the key the record id is written under; cache it under another name.");
            }

            if (!seen.Add(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' caches two paths under the name '{field.Name}'; give one of them a different 'as'.");
            }
        }
    }

    /// <summary>
    /// A lookup table holds rows, not OSDU records: there is no record of it in storage to read whole, and no reference of a
    /// record to read an attribute through.
    /// </summary>
    private void RefuseFetchRecords()
    {
        if (FetchRecords)
        {
            throw new FlowValidationException(
                $"Cached type '{Name}' comes from {Describe()}, which takes no 'fetchRecords': only a type searched on OSDU, by its kind, has records storage holds to read whole.");
        }

        if (Fields.FirstOrDefault(f => f.IsAttribute) is { } attribute)
        {
            throw new FlowValidationException(
                $"Cached type '{Name}' comes from {Describe()}, which takes no attribute ({attribute.Name}): only a type searched on OSDU, by its kind, has records an attribute is read through.");
        }
    }

    private void ValidateTable()
    {
        if (string.IsNullOrWhiteSpace(Table))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the ingestion table it reads.");
        }

        RefuseFetchRecords();

        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the key: the column each row of {Table} is keyed by.");
        }

        Refuse(Kind, "kind");
        Refuse(Dictionary, "dictionary");
        Refuse(Dimension, "dimension");
        RefuseQuery();
        ValidateLookupEntityType();
        if (Fields.Count == 0)
        {
            throw new FlowValidationException($"Cached type '{Name}' reads no column of {Table} beside its key; list the columns a mapping reads under fields.");
        }

        ValidateLookupNames(Key.Trim(), $"column of {Table}");
    }

    private void ValidateDictionary()
    {
        if (string.IsNullOrWhiteSpace(Dictionary))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the name of the dictionary it holds.");
        }

        RefuseFetchRecords();
        Refuse(Kind, "kind");
        Refuse(Table, "table");
        Refuse(Dimension, "dimension");
        RefuseQuery();
        ValidateLookupEntityType();

        // As a cache flow declares it, a dictionary type names only its dictionary; once the document is read, it carries
        // the key and the fields the document names.
        if (Key is null)
        {
            if (Fields.Count > 0)
            {
                throw new FlowValidationException($"Cached type '{Name}' holds dictionary {Dictionary}, whose document names its fields; the cache flow lists none.");
            }

            return;
        }

        ValidateLookupNames(Key.Trim(), $"entry of dictionary {Dictionary}");
    }

    /// <summary>
    /// A dimension type names the dimension and the flow declaring it, and holds the values' fixed columns (the value as its
    /// key, its keys, records and filter beside it), then the attributes of the dimension it names.
    /// </summary>
    private void ValidateDimension()
    {
        if (string.IsNullOrWhiteSpace(Dimension) || string.IsNullOrWhiteSpace(DimensionFlow))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the dimension it holds and the dimension flow that declares it.");
        }

        RefuseFetchRecords();
        Refuse(Kind, "kind");
        Refuse(Table, "table");
        Refuse(Dictionary, "dictionary");
        RefuseQuery();
        ValidateLookupEntityType();
        if (!string.Equals(Key, DimensionColumns.Value, StringComparison.Ordinal)
            || !Fields.Take(DimensionColumns.Fields.Count).Select(f => f.Name).SequenceEqual(DimensionColumns.Fields, StringComparer.Ordinal)
            || Fields.Skip(DimensionColumns.Fields.Count).Any(f => !DimensionAttributeSpec.IsName(f.Name) || DimensionAttributeSpec.Reserved.Contains(f.Name)))
        {
            throw new FlowValidationException(
                $"Cached type '{Name}' holds dimension {Dimension}, whose rows are its values: keyed by '{DimensionColumns.Value}' with '{string.Join("' and '", DimensionColumns.Fields)}' beside it, then the attributes of the dimension it carries.");
        }
    }

    /// <summary>A lookup row's key is its id: neither the key nor a field is called id, and no two share a name.</summary>
    private void ValidateLookupNames(string key, string what)
    {
        if (LookupKeys.IsIdName(key))
        {
            throw new FlowValidationException($"Cached type '{Name}' is keyed by '{key}'; a lookup row's key is its id, so the key of each {what} needs another name.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        foreach (var field in Fields)
        {
            if (LookupKeys.IsIdName(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' keeps '{field.Path}' as '{field.Name}'; a lookup row's key is its id, so keep it under another name.");
            }

            if (string.Equals(field.Path, key, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"Cached type '{Name}' lists its key '{key}' among its fields; the key is kept under its own name already.");
            }

            if (!seen.Add(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' keeps two values under the name '{field.Name}'; give one of them a different name.");
            }
        }
    }

    private void ValidateLookupEntityType()
    {
        var expected = ReferenceType.LookupEntityType(Name);
        if (!string.Equals(EntityType, expected, StringComparison.Ordinal))
        {
            throw new FlowValidationException($"Cached type '{Name}' holds no OSDU records, so it is kept as {expected}, not as {EntityType}; leave entityType out.");
        }
    }

    private void RefuseQuery()
    {
        if (!string.IsNullOrWhiteSpace(Query) && Query.Trim() != "*")
        {
            throw new FlowValidationException($"Cached type '{Name}' comes from {Describe()}, which is not searched, so it takes no query: it holds every row.");
        }
    }

    private void Refuse(string? value, string setting)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            throw new FlowValidationException($"Cached type '{Name}' comes from {Describe()}, which takes no '{setting}'. A type has one origin: kind, table, dictionary or dimension.");
        }
    }
}

/// <summary>
/// One value to cache, and the name the cache stores it under. A path of the record is written either bare
/// (<c>data.Code</c>, stored as <c>Code</c>) or with an explicit name (<c>{ path: data.NameAlias.AliasName, as: Alias }</c>);
/// it crosses arrays implicitly, so a path through an array of objects yields the set of values found along it, and whatever
/// it yields is kept as it is. An attribute (<c>types[].attributes</c>, osdu/docs/reference/flow/cache-attributes.md) is
/// read exactly as a dimension's attribute is (<see cref="DimensionAttributeSpec"/>): from the cached record, through the
/// records its steps' references name, each step's objects filtered, the value kept, chosen and decoded as the attribute
/// says; its <see cref="Path"/> is its definition, as messages and the Cache page show it.
/// </summary>
public sealed record ReferenceFieldSpec
{
    /// <summary>What an attribute's definition text starts with, which no path of a record does.</summary>
    private const string AttributeOpen = "{ path: ";

    public ReferenceFieldSpec(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? ReferenceField.Normalize(Path) : name.Trim();
    }

    /// <summary>An attribute read as a dimension's is, the records of an entity type its steps reach looked up in <paramref name="lookupKinds"/>.</summary>
    public ReferenceFieldSpec(DimensionAttributeSpec attribute, IReadOnlyDictionary<string, string>? lookupKinds = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (attribute.IsCollected || attribute.Steps.Count == 0)
        {
            throw new ArgumentException($"Attribute {attribute.Name} is collected; a cached type's attribute is read through steps.", nameof(attribute));
        }

        Attribute = attribute;
        LookupKinds = lookupKinds is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(lookupKinds, StringComparer.Ordinal);
        Name = attribute.Name;
        Path = Definition(attribute, LookupKinds);
    }

    /// <summary>
    /// The path into the OSDU record, from its root (<c>data.Code</c>, <c>legal.legaltags</c>); for an attribute, its
    /// definition (<c>{ path: [data.WellID, data.FacilityName], keep: id }</c>), every setting it is read with.
    /// </summary>
    public string Path { get; }

    /// <summary>The name the value is cached under, and the name a mapping matches or selects by.</summary>
    public string Name { get; }

    /// <summary>For an attribute: how it is read, as a dimension's attribute is; null for a path of the record.</summary>
    public DimensionAttributeSpec? Attribute { get; }

    /// <summary>For an attribute: the kind the records of each entity type its steps reach are looked up in, by entity type.</summary>
    public IReadOnlyDictionary<string, string> LookupKinds { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True for an attribute, read through the dimension's labeler rather than as a path of the record.</summary>
    public bool IsAttribute => Attribute is not null;

    /// <summary>The lookup kinds as one text, so attributes read with the same ones are read together.</summary>
    public string LookupSignature => string.Join(',', LookupKinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value));

    /// <summary>Two fields are one when they cache one name from one definition: the path, or every setting of an attribute.</summary>
    public bool Equals(ReferenceFieldSpec? other)
        => other is not null && string.Equals(Path, other.Path, StringComparison.Ordinal) && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && IsAttribute == other.IsAttribute;

    public override int GetHashCode() => HashCode.Combine(Path, Name, IsAttribute);

    /// <summary>
    /// The field as the catalog keeps it: <c>{ "path": ..., "as": ... }</c>, and for an attribute its steps and settings under
    /// <c>attribute</c> and the kinds it looks records up in under <c>lookupKinds</c>.
    /// </summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["path"] = Path, ["as"] = Name };
        if (Attribute is { } attribute)
        {
            json["attribute"] = new JsonObject
            {
                ["steps"] = new JsonArray(attribute.Steps.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["keep"] = Text(attribute.Keep),
                ["many"] = Text(attribute.Many),
                ["decode"] = Text(attribute.Decode),
                ["maxReferences"] = attribute.MaxReferences,
                ["trim"] = attribute.Trim,
            };
            if (LookupKinds.Count > 0)
            {
                json["lookupKinds"] = new JsonArray(LookupKinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k.Value)).ToArray());
            }
        }

        return json;
    }

    /// <summary>A field as <see cref="ToJson"/> writes it; null for an entry holding no path, which names no field.</summary>
    /// <exception cref="FormatException">An attribute's settings are not ones <see cref="ToJson"/> writes.</exception>
    public static ReferenceFieldSpec? FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json["path"] is not JsonValue path || !path.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var name = json["as"] is JsonValue alias && alias.TryGetValue<string>(out var given) && !string.IsNullOrWhiteSpace(given) ? given : null;
        if (json["attribute"] is not JsonObject attribute)
        {
            return new ReferenceFieldSpec(text, name);
        }

        if (name is null)
        {
            throw new FormatException($"Attribute '{text}' names no 'as'.");
        }

        var steps = (attribute["steps"] as JsonArray ?? []).Select(s => s is JsonValue v && v.TryGetValue<string>(out var step) ? step : null).ToList();
        if (steps.Count == 0 || steps.Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException($"Attribute {name} holds no steps.");
        }

        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in (json["lookupKinds"] as JsonArray ?? []).OfType<JsonValue>().Select(k => k.TryGetValue<string>(out var s) ? s : null).OfType<string>())
        {
            kinds[kind.Split(':') is { Length: 4 } parts ? parts[2] : throw new FormatException($"Attribute {name} looks records up in '{kind}', which is no kind.")] = kind;
        }

        return new ReferenceFieldSpec(
            new DimensionAttributeSpec(
                name, steps!, null,
                Parse<DimensionValueKeep>(attribute["keep"], DimensionValueKeep.Value, name),
                Parse<DimensionValueMany>(attribute["many"], DimensionValueMany.First, name),
                Parse<DimensionValueDecode>(attribute["decode"], DimensionValueDecode.None, name),
                attribute["maxReferences"] is JsonValue max && max.TryGetValue<int>(out var most) ? most : DimensionAttributeSpec.DefaultMaxReferences,
                attribute["trim"] is not JsonValue trim || !trim.TryGetValue<bool>(out var trims) || trims),
            kinds);
    }

    /// <summary>True when <paramref name="path"/> is an attribute's definition, as <see cref="Path"/> gives it, not a path of a record.</summary>
    public static bool IsDefinition(string path) => path.StartsWith(AttributeOpen, StringComparison.Ordinal);

    /// <summary>
    /// An attribute's definition as one line: its steps, and each setting it is read with that is not the default, in the
    /// order a document writes them, so two flows declaring one attribute alike give one text.
    /// </summary>
    private static string Definition(DimensionAttributeSpec attribute, IReadOnlyDictionary<string, string> lookupKinds)
    {
        var parts = new List<string> { attribute.Steps.Count == 1 ? attribute.Steps[0] : $"[{string.Join(", ", attribute.Steps)}]" };
        if (attribute.Keep != DimensionValueKeep.Value)
        {
            parts.Add($"keep: {Text(attribute.Keep)}");
        }

        if (attribute.Many != DimensionValueMany.First)
        {
            parts.Add($"many: {Text(attribute.Many)}");
        }

        if (attribute.Decode != DimensionValueDecode.None)
        {
            parts.Add($"decode: {Text(attribute.Decode)}");
        }

        if (attribute.MaxReferences != DimensionAttributeSpec.DefaultMaxReferences)
        {
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"maxReferences: {attribute.MaxReferences}"));
        }

        if (!attribute.Trim)
        {
            parts.Add("trim: false");
        }

        if (lookupKinds.Count > 0)
        {
            parts.Add($"lookupKinds: [{string.Join(", ", lookupKinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value))}]");
        }

        return AttributeOpen + string.Join(", ", parts) + " }";
    }

    /// <summary>A setting as a document writes it: the enum's name, its first letter lower-cased.</summary>
    private static string Text<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static T Parse<T>(JsonNode? node, T fallback, string name) where T : struct, Enum
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return fallback;
        }

        return Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException($"Attribute {name} holds '{text}', which is no {typeof(T).Name}.");
    }
}

/// <summary>
/// The columns of a cached type holding a dimension's values: each row keyed by a human-friendly value, with the keys it stands
/// for exactly as the index holds them (a set, which a lookup matches on any one of), the records holding them, the search
/// filter finding those records, and the attributes the type names, each under its name (a set when the value's keys hold
/// several).
/// </summary>
public static class DimensionColumns
{
    /// <summary>The row's key: the dimension's value.</summary>
    public const string Value = "value";

    /// <summary>The keys the value stands for, exactly as the index holds them.</summary>
    public const string Keys = "keys";

    /// <summary>The records holding any of them, as the dimension counts them.</summary>
    public const string Records = "records";

    /// <summary>The search filter finding those records, when one query holds it.</summary>
    public const string Filter = "filter";

    /// <summary>The columns kept beside the key, in the order the type lists them.</summary>
    public static IReadOnlyList<string> Fields { get; } = [Keys, Records, Filter];

    /// <summary>The fields a dimension type keeps beside its key, as a cache flow's mapper declares them.</summary>
    public static IReadOnlyList<ReferenceFieldSpec> FieldSpecs() => Fields.Select(field => new ReferenceFieldSpec(field, field)).ToList();
}
