using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Expressions;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A mapping (osdu/docs/reference/flow/mapping.md) as it is loaded: which template version it fills, what
/// identifies a record of the incoming dataset, and one entry per template variable its record tree fills, each saying
/// where the value comes from.
/// </summary>
public sealed record MappingDefinition
{
    public const string DocumentTypeName = "mapping";

    public string? SourcePath { get; init; }

    /// <summary>
    /// The file the mapping was read from, as a full path, when it was read from disk: where the template folders it reads
    /// are walked from. Null for a mapping read from the catalog or from text, whose <see cref="SourcePath"/> names it.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// The document the mapping's templates are read for: the file it was read from, or the path the catalog names it by,
    /// which the folders the catalog recorded for its repository are walked from the same way.
    /// </summary>
    public string? TemplateDocument => Location ?? SourcePath;

    public required string Name { get; init; }

    /// <summary>Semantic version of the mapping. Part of the render context and so of every record's render.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// What the document says about how records render (<see cref="Documents.MappingFingerprint"/>), so an edit made in place
    /// under the same name and version still moves the render context. Null for a definition built in code.
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// The template version the mapping fills: the one it names, or, when it names the kind alone, the one version of the
    /// kind the nearest template folder on its path holds (osdu/docs/reference/concepts/templates.md, Template folders).
    /// </summary>
    public required TemplateReference Template { get; init; }

    /// <summary>
    /// Whether the document names its template's version (<c>template.version</c>), rather than following the version the
    /// template folders on its path hold. True for a definition built in code.
    /// </summary>
    public bool TemplateVersionNamed { get; init; } = true;

    public string? Description { get; init; }

    public required MappingDataset Dataset { get; init; }

    /// <summary>Parameters the mapping accepts from the flow under <c>render.parameters</c>.</summary>
    public IReadOnlyDictionary<string, MappingParameter> Parameters { get; init; } = new Dictionary<string, MappingParameter>(StringComparer.Ordinal);

    /// <summary>
    /// What each <c>search.&lt;name&gt;</c> source searches, by the name entries write it as. Empty for a mapping that
    /// resolves everything out of the cache.
    /// </summary>
    public IReadOnlyDictionary<string, MappingSearch> Searches { get; init; } = new Dictionary<string, MappingSearch>(StringComparer.Ordinal);

    /// <summary>
    /// The records a row is matched to once and read wherever the record needs them (<c>lookups.&lt;name&gt;</c>), by the name
    /// nodes write them as. Empty for a mapping that finds every record where it reads it.
    /// </summary>
    public IReadOnlyDictionary<string, MappingLookup> Lookups { get; init; } = new Dictionary<string, MappingLookup>(StringComparer.Ordinal);

    /// <summary>The entries, in the order the document lists them.</summary>
    public required IReadOnlyList<MappingEntry> Entries { get; init; }

    /// <summary>
    /// Every type of the partition's cache the mapping reads, in name order: those its cache sources read and their findBy
    /// lines compare, those a replace reads its table from, and those the tokens of an id modifier read, whatever the entry's
    /// source. A mapping that reads none of
    /// them renders against no cache at all; one that reads any renders against a version, whose label enters its records'
    /// render context. The render, lineage and the intake all ask this one question, so none of them misses a type.
    /// </summary>
    public IReadOnlyList<string> CacheTypesRead() => CacheTypesReadBy(Entries);

    /// <summary>The cached types <paramref name="entries"/> read, as <see cref="CacheTypesRead"/> counts them.</summary>
    public static IReadOnlyList<string> CacheTypesReadBy(IEnumerable<MappingEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.SelectMany(e => e.ValueNodes))
        {
            if (entry.Source is { Kind: MappingSourceKind.Cache, CacheType: { } type })
            {
                types.Add(type);
                foreach (var find in entry.FindBy)
                {
                    types.Add(find.Type);
                }
            }

            var operandLookup = entry.FindAll?.Operand.Lookup;
            if (operandLookup is not null)
            {
                types.Add(operandLookup.CacheType);
            }

            foreach (var modifier in entry.Modifiers.Concat(operandLookup?.Modifiers ?? []))
            {
                if (modifier.Table is { } table)
                {
                    types.Add(table.CacheType);
                }

                if (modifier.Id is { } id)
                {
                    types.UnionWith(id.CacheTypes);
                }
            }
        }

        return types.ToList();
    }

    /// <summary>
    /// The paths the mapping reads in its records' parent (<c>$parent</c>), each once, in ordinal order: what a run reads of the
    /// parent record, and empty for a mapping that reads none (osdu/docs/reference/flow/mapping-parent.md).
    /// </summary>
    public IReadOnlyList<string> ParentPaths() => Entries
        .SelectMany(e => e.ValueNodes)
        .Where(n => n.Source is { Kind: MappingSourceKind.Parent, ParentPath: not null })
        .Select(n => n.Source!.ParentPath!)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>True when the mapping reads a section of its records' parent.</summary>
    public bool ReadsParent => Entries.SelectMany(e => e.ValueNodes).Any(n => n.Source?.Kind == MappingSourceKind.Parent);

    /// <summary>The access list and legal block, as the static entries for them declare them (parameter tokens unexpanded).</summary>
    public required MappingEnvelope Envelope { get; init; }

    public string Reference => Name + "@" + Version;

    /// <summary>The OSDU kind of the records the mapping renders.</summary>
    public string Kind => Template.Kind;

    public string EntityType => Identity.TargetId.EntityTypeFromKind(Kind);

    /// <summary>The child datasets the mapping's repeaters read, in the order they are first named.</summary>
    public IReadOnlyList<string> ChildDatasets => Entries
        .Where(e => e.IsRepeater)
        .Select(e => e.Source!.Child!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Every assertion the mapping states, with the entry that states it, in document order: those of every node, the
    /// properties of a list of objects' items included (osdu/docs/reference/flow/mapping-assertions.md).
    /// </summary>
    public IEnumerable<(MappingEntry Entry, NodeAssertion Assertion)> Assertions()
        => Entries.SelectMany(e => e.Fillers).SelectMany(entry => entry.Assertions.Select(assertion => (entry, assertion)));

    /// <summary>The entries written into each item of a repeater's array.</summary>
    public IEnumerable<MappingEntry> ItemEntries(MappingEntry repeater)
    {
        ArgumentNullException.ThrowIfNull(repeater);
        return Entries.Where(e => repeater.Target.Equals(e.Target.Repeater));
    }
}

/// <summary>
/// The four envelope values OSDU requires on every record, which a mapping gives as static lists. A mapping of a DSPDM
/// business object's rows (<see cref="DspdmKinds"/>) has none: its rows are no OSDU records.
/// </summary>
public sealed record MappingEnvelope(
    IReadOnlyList<string> Owners, IReadOnlyList<string> Viewers, IReadOnlyList<string> LegalTags, IReadOnlyList<string> OtherRelevantDataCountries);

/// <summary>What identifies a record of the incoming dataset.</summary>
public sealed record MappingDataset
{
    /// <summary>The source system, entering the delivery key.</summary>
    public required string System { get; init; }

    /// <summary>The columns of the dataset's row the delivery key is derived from, in order.</summary>
    public required IReadOnlyList<string> Key { get; init; }

    /// <summary>Display text with <c>{column}</c> tokens naming columns of the dataset's own row, for the ledger and the GUI; never part of the record.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The dataset's own columns whose values identify the record to a person (a wellbore id, a well name, a survey
    /// name), without the <c>dataset.</c> prefix. The ledger indexes each value so the record is found by it across
    /// every flow; search only, never part of the record.
    /// </summary>
    public IReadOnlyList<string> Identity { get; init; } = [];

    /// <summary>
    /// What the unique segment of every record's OSDU id is made from (<c>dataset.idFrom</c>): the delivery key, unless the
    /// mapping says the key's own values. Either way the ledger keys the record by its delivery key.
    /// </summary>
    public MappingIdSource IdFrom { get; init; } = MappingIdSource.DeliveryKey;
}

/// <summary>What the unique segment of a mapping's OSDU ids is made from (<c>dataset.idFrom</c>).</summary>
public enum MappingIdSource
{
    /// <summary>The delivery key, 32 hexadecimal digits (<c>dev:master-data--Wellbore:6f1c...</c>): every mapping's, unless it says otherwise.</summary>
    DeliveryKey,

    /// <summary>
    /// The values of <c>dataset.key</c>, percent-encoded (<c>dev:reference-data--ExternalUnitOfMeasure:WELLDB::G%2FCC</c>), so
    /// the id reads as the code it stands for (<see cref="Identity.TargetId.ComposeFromKey"/>).
    /// </summary>
    Key,
}

public sealed record MappingParameter
{
    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>A column of the dataset's row (<c>dataset.log_source</c>) or of a child dataset's row (<c>dataset.curves.curve_id</c>).</summary>
public sealed record DatasetColumn(string? Child, string Column)
{
    public const string Prefix = "dataset";

    public override string ToString() => Child is null ? $"{Prefix}.{Column}" : $"{Prefix}.{Child}.{Column}";
}

public enum MappingSourceKind
{
    /// <summary>A column of the dataset's row or of a child dataset's row.</summary>
    DatasetColumn,

    /// <summary>The rows of a child dataset, one array item each: a repeater.</summary>
    DatasetRows,

    /// <summary>A field of a cached record.</summary>
    Cache,

    /// <summary>
    /// A field of a record the platform is searched for as the run needs it, rather than one captured into the
    /// partition's cache. For a set that is business data rather than a vocabulary: a partition's wellbores grow
    /// without bound and change constantly, so capturing them to answer one lookup costs more every day, while the
    /// units and the type codes a cache is for are closed sets that a capture holds cheaply.
    /// </summary>
    Search,

    /// <summary>A value an expression computes from the row, the dataset's own row and the parameters (<c>$expr</c>).</summary>
    Expression,

    /// <summary>
    /// A section of the record's parent (<c>$parent</c>, osdu/docs/reference/flow/mapping-parent.md): the record of the kind
    /// the mapping's template is linked to (<see cref="Templates.TemplateParent"/>), whose OSDU id is the record's own with
    /// the parent's entity type, read from the platform as the run needs it. A custom template's record takes from it what
    /// the canonical record holds, such as the bulk data a domain service wrote there.
    /// </summary>
    Parent,
}

/// <summary>Where an entry's value comes from.</summary>
public sealed record MappingSource
{
    public const string CachePrefix = "cache";

    /// <summary>The prefix of a source resolved by searching the platform: <c>search.Wellbore.id</c>.</summary>
    public const string SearchPrefix = "search";

    /// <summary>The prefix a section of the record's parent is named with: <c>parent.data.DDMSDatasets</c>.</summary>
    public const string ParentPrefix = "parent";

    public required MappingSourceKind Kind { get; init; }

    /// <summary>The column a dataset column source reads.</summary>
    public DatasetColumn? Column { get; init; }

    /// <summary>For an expression source: the expression that computes the value.</summary>
    public MappingExpression? Expression { get; init; }

    /// <summary>The child dataset a repeater reads.</summary>
    public string? Child { get; init; }

    /// <summary>The cached type a cache source reads (<c>UnitOfMeasure</c>).</summary>
    public string? CacheType { get; init; }

    /// <summary>The cached field a cache source reads: <c>id</c> for the record id, or a field or path into one.</summary>
    public string? CacheField { get; init; }

    /// <summary>
    /// For a parent source: the path of the section it reads in the parent record, from the record's root, as the record
    /// writes it (<c>data.DDMSDatasets</c>, <c>data.ExtensionProperties.wdms</c>, <c>acl</c>).
    /// </summary>
    public string? ParentPath { get; init; }

    /// <summary>
    /// For a node written with <c>$lookup</c>, the lookup it reads (<see cref="MappingDefinition.Lookups"/>): its cached type
    /// is the lookup's, and the entry carries the lookup's findBy lines and modifiers, so it finds the record every other
    /// reader of the lookup finds. Null for any other source.
    /// </summary>
    public string? Lookup { get; init; }

    /// <summary>The prefix this source is written with: <c>cache</c> or <c>search</c>.</summary>
    public string Prefix => Kind == MappingSourceKind.Search ? SearchPrefix : CachePrefix;

    public override string ToString() => Kind switch
    {
        MappingSourceKind.DatasetColumn => Column!.ToString(),
        MappingSourceKind.DatasetRows => $"{DatasetColumn.Prefix}.{Child}",
        MappingSourceKind.Expression => Expression!.Text,
        MappingSourceKind.Parent => $"{ParentPrefix}.{ParentPath}",
        _ when Lookup is not null => $"{MappingLookup.Prefix}.{Lookup}.{CacheField}",
        _ => $"{Prefix}.{CacheType}.{CacheField}",
    };

    /// <summary>True when the source reads the row it is evaluated for: a column, or an expression over it.</summary>
    public bool ReadsRow => Kind is MappingSourceKind.DatasetColumn or MappingSourceKind.Expression;

    /// <summary>True when a resolved source reads the record id, which renders in the reference form OSDU relationships use.</summary>
    public bool ReadsRecordId
        => Kind is MappingSourceKind.Cache or MappingSourceKind.Search && string.Equals(CacheField, "id", StringComparison.Ordinal);

    /// <summary>True when this source is resolved by matching a record, whether from the cache or from a search.</summary>
    public bool Resolves => Kind is MappingSourceKind.Cache or MappingSourceKind.Search;
}

/// <summary>
/// One line of a cache or a search source's <c>findBy</c>: the field compared, and the dataset value or literal it must
/// equal. <see cref="Type"/> is the cached type, or the search, the line compares a record of.
/// </summary>
public sealed record FindBy(string Type, string Field, DatasetColumn? Column, string? Literal)
{
    /// <summary>The prefix the line is written with: <c>cache</c>, or <c>search</c> for a line of a search source.</summary>
    public string Prefix { get; init; } = MappingSource.CachePrefix;

    public override string ToString()
        => $"{Prefix}.{Type}.{Field} = {(Column is not null ? Column.ToString() : "'" + Literal + "'")}";
}

/// <summary>
/// A record a mapping finds once for a row and reads wherever the record needs it (<c>lookups.&lt;name&gt;</c>): a cached
/// type, and the findBy lines, tried in order, that find the row's record in it. Every node reading the lookup reads the
/// record those lines find, so an id the record writes and a value derived from that record never come from two
/// different records. A lookup reads the dataset's own row.
/// </summary>
public sealed record MappingLookup
{
    /// <summary>How a lookup is named where it is read: <c>$lookup: wellbore.id</c>, <c>$lookup.wellbore.GeoContexts.FieldID</c>.</summary>
    public const string Prefix = "lookup";

    /// <summary>The name nodes read the lookup by.</summary>
    public required string Name { get; init; }

    /// <summary>The cached type the record is found in.</summary>
    public required string CacheType { get; init; }

    /// <summary>The lines that find the record, tried in order until one finds exactly one.</summary>
    public required IReadOnlyList<FindBy> FindBy { get; init; }

    /// <summary>The modifiers applied to the dataset value each line compares.</summary>
    public IReadOnlyList<Modifier> Modifiers { get; init; } = [];

    /// <summary>A last matching attempt with punctuation and spacing folded away, for names.</summary>
    public bool IgnoreSeparators { get; init; }

    public string? Description { get; init; }

    /// <summary>Every dataset column the lookup's lines and modifiers read.</summary>
    public IEnumerable<DatasetColumn> Columns
        => FindBy.Where(f => f.Column is not null).Select(f => f.Column!)
            .Concat(Modifiers.Where(m => m.Id is not null).SelectMany(m => m.Id!.Columns));
}

/// <summary>
/// What the key condition of a <c>$findAll</c> compares a cached field with: a dataset column, a fixed text, or a path of
/// the record a lookup finds (<c>$lookup.wellbore.GeoContexts.FieldID</c>), whose every value is a key of its own.
/// </summary>
public sealed record FindAllOperand(DatasetColumn? Column, string? Literal, MappingLookup? Lookup, string? LookupPath)
{
    public override string ToString() => Column is not null
        ? Column.ToString()
        : Lookup is not null ? $"${MappingLookup.Prefix}.{Lookup.Name}.{LookupPath}" : $"'{Literal}'";
}

/// <summary>
/// A <c>$findAll</c>: the rows of a cached type a node reads every one of. A row is read when its <see cref="Field"/> holds
/// one of the values <see cref="Operand"/> gives (without regard to case, since a key names the same thing however it is
/// written) and every field <see cref="Empty"/> names holds nothing. The node gives the field it reads from each of those
/// rows, as a list.
/// </summary>
/// <param name="Type">The cached type whose rows are read.</param>
/// <param name="Field">The field the key condition compares.</param>
/// <param name="Operand">What the field must hold.</param>
/// <param name="Empty">The fields a row must hold nothing under, in the order they are written.</param>
public sealed record FindAllQuery(string Type, string Field, FindAllOperand Operand, IReadOnlyList<string> Empty)
{
    public override string ToString()
        => string.Join(" and ", Empty.Select(field => $"{field} is empty").Prepend($"{Field} = {Operand}"));
}

public enum ModifierKind
{
    Trim,
    Upper,
    Lower,
    Split,
    Replace,
    Equals,
    Date,
    Number,

    /// <summary>Builds an OSDU id from a template (<see cref="IdTemplate"/>); always the last modifier.</summary>
    Id,

    /// <summary>
    /// Builds a reference to a record of the entity type the property points to, in the flow's partition, whose code is
    /// the value: the id template <c>{$param.dataPartition}:&lt;entity type&gt;:{$value}:</c> written for you. Always the
    /// last modifier.
    /// </summary>
    Ref,
}

/// <summary>One change to an incoming dataset value.</summary>
public sealed record Modifier
{
    public required ModifierKind Kind { get; init; }

    /// <summary>For split: the separator; a single space splits on any run of whitespace.</summary>
    public string? Separator { get; init; }

    /// <summary>For split: which part to keep, counting from one.</summary>
    public int? Part { get; init; }

    /// <summary>
    /// For replace: incoming values and what each becomes. A null value becomes no value, so the entry's required flag
    /// decides; what a value the table does not list becomes is <see cref="Otherwise"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Replacements { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>For replace: what a value the table does not list becomes; by default it passes on unchanged.</summary>
    public ReplaceFallback Otherwise { get; init; } = ReplaceFallback.Keep;

    /// <summary>
    /// For replace: the cached type the table is read from, in place of <see cref="Replacements"/>: a value is matched on
    /// one of its fields and replaced by another of the matched row. Null for a table written in the mapping.
    /// </summary>
    public CachedReplaceTable? Table { get; init; }

    /// <summary>For equals: the text the value is compared with; for date: the .NET format the value is written in, or null for an ISO 8601 date or date-time.</summary>
    public string? Text { get; init; }

    /// <summary>For number: the separator before the decimals, '.' or ','.</summary>
    public string? DecimalSeparator { get; init; }

    /// <summary>For number: the separator between groups of three digits, or null when the value is written without one.</summary>
    public string? GroupSeparator { get; init; }

    /// <summary>
    /// For id: the template the id is built from. For ref: the same, once the entity type is known; null until a render or
    /// the preflight reads it off the template variable the node fills.
    /// </summary>
    public IdTemplate? Id { get; init; }

    /// <summary>
    /// For ref: the entity type the reference names, as written (<c>UnitOfMeasure</c>, or in full
    /// <c>reference-data--UnitOfMeasure</c>), or null to take the one the template variable points to.
    /// </summary>
    public string? EntityType { get; init; }

    /// <summary>True for the modifiers that build the id a node writes, id and ref, which are always last.</summary>
    public bool BuildsId => Kind is ModifierKind.Id or ModifierKind.Ref;

    public override string ToString() => Kind switch
    {
        ModifierKind.Ref => EntityType is null ? "ref" : $"ref({EntityType})",
        ModifierKind.Split => $"split(separator '{Separator}', part {Part})",
        ModifierKind.Replace when Table is { } table => $"replace from {table}"
            + (Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $", otherwise {Otherwise}"),
        ModifierKind.Replace => "replace(" + string.Join(", ", Replacements.Select(kv => kv.Key + ": " + (kv.Value ?? "~")))
            + (Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $"; otherwise {Otherwise}") + ")",
        ModifierKind.Equals => $"equals({Text})",
        ModifierKind.Date => Text is null ? "date" : $"date({Text})",
        ModifierKind.Number when GroupSeparator is null && DecimalSeparator is null or "." => "number",
        ModifierKind.Number => $"number(decimal '{DecimalSeparator ?? "."}'" + (GroupSeparator is null ? string.Empty : $", group '{GroupSeparator}'") + ")",
        ModifierKind.Id => $"id({Id})",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// A replace reading its table from the partition's cache (<c>replace: $cache.CurveDictionary</c>): the value is matched on
/// <see cref="Match"/> and replaced by the matched row's <see cref="Field"/>. Either may be left for the cached type to
/// decide: a lookup table matches on its key, and replaces by the one field it holds beside it (a dictionary of pairs'
/// value).
/// </summary>
/// <param name="CacheType">The cached type the table is read from.</param>
/// <param name="Match">The field a value is matched on, or null for the type's key.</param>
/// <param name="Field">The field of the matched row a value is replaced by, or null for the one field beside the key.</param>
public sealed record CachedReplaceTable(string CacheType, string? Match, string? Field)
{
    /// <summary>The table as the mapping names it, with the fields it names: <c>$cache.CurveDictionary (mnemonic to log_curve_family_id)</c>.</summary>
    public override string ToString()
        => $"${MappingSource.CachePrefix}.{CacheType}" + (Match is null && Field is null ? string.Empty : $" ({Match ?? "its key"} to {Field ?? "its value"})");
}

/// <summary>What a replace does with a value its table does not list.</summary>
public enum ReplaceFallbackKind
{
    /// <summary>The value passes on unchanged, trimmed.</summary>
    Keep,

    /// <summary>The value becomes no value, so the entry's required flag decides what that does.</summary>
    Empty,

    /// <summary>The value becomes a fixed text.</summary>
    Text,
}

/// <summary>
/// A replace's <c>otherwise</c>: what a value its table does not list becomes. It applies only to a value that is there and
/// unlisted; an empty incoming value stays empty, and a listed value that becomes no value is not "unlisted".
/// </summary>
public sealed record ReplaceFallback(ReplaceFallbackKind Kind, string? Text = null)
{
    /// <summary>An unlisted value passes on unchanged: what a replace without <c>otherwise</c> does.</summary>
    public static ReplaceFallback Keep { get; } = new(ReplaceFallbackKind.Keep);

    /// <summary>An unlisted value becomes no value (<c>otherwise: ~</c>).</summary>
    public static ReplaceFallback Empty { get; } = new(ReplaceFallbackKind.Empty);

    /// <summary>An unlisted value becomes <paramref name="text"/>; text that is empty or blank is no value.</summary>
    public static ReplaceFallback Of(string? text)
        => string.IsNullOrWhiteSpace(text) ? Empty : new ReplaceFallback(ReplaceFallbackKind.Text, text);

    public override string ToString() => Kind switch
    {
        ReplaceFallbackKind.Keep => "keep",
        ReplaceFallbackKind.Empty => "~",
        _ => Text ?? string.Empty,
    };
}

/// <summary>One mapping entry: a template variable and where its value comes from.</summary>
public sealed partial record MappingEntry
{
    /// <summary>
    /// The entry's position in the record tree, in document order. An alternative or an item carries its node's; a property
    /// of an item of a list of objects is numbered by its place in the item.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>Where the document writes the entry (<c>record.data.Curves.item.CurveUnit</c>), for messages.</summary>
    public string? Location { get; init; }

    public required TemplatePath Target { get; init; }

    /// <summary>The source, or null for a static entry.</summary>
    public MappingSource? Source { get; init; }

    /// <summary>The static value, or null for an entry with a source.</summary>
    public JsonNode? Static { get; init; }

    public IReadOnlyList<FindBy> FindBy { get; init; } = [];

    /// <summary>
    /// For a cache node written with <c>$findAll</c> in place of <c>$findBy</c>: the rows it reads every one of, giving the
    /// field it reads from each as a list. Null for any other node.
    /// </summary>
    public FindAllQuery? FindAll { get; init; }

    public IReadOnlyList<Modifier> Modifiers { get; init; } = [];

    /// <summary>
    /// The node's <c>$when</c>: the condition that decides whether the property is written for a row. Null writes it
    /// always. A repeater's decides for the whole array, so it reads the dataset's own row.
    /// </summary>
    public MappingExpression? AppliesWhen { get; init; }

    /// <summary>
    /// For a repeater: its <c>$where</c>, the condition a child row must hold to become an item, tested against each row.
    /// Null repeats every row.
    /// </summary>
    public MappingExpression? RowFilter { get; init; }

    /// <summary>What an empty value does: true holds the record, false leaves the variable out.</summary>
    public bool Required { get; init; } = true;

    /// <summary>For a cache source: a last matching attempt with punctuation and spacing folded away, for names.</summary>
    public bool IgnoreSeparators { get; init; }

    /// <summary>
    /// For a node that builds an id with id or ref (<c>$unverified: true</c>): the id is written even when the cache the
    /// render reads holds records of its entity type and not this one. The render records it as an unverified reference,
    /// so a later capture that finds the record reaches the record built from it. Without it, such an id is a cache miss.
    /// </summary>
    public bool Unverified { get; init; }

    /// <summary>
    /// For a <c>$coalesce</c> node: the alternatives after the first, tried in order while the ones before them give no
    /// value. The entry itself is the first alternative: its source, findBy, modifiers and flags are that alternative's,
    /// and its target, <c>$when</c>, <c>$required</c> and description are the node's, which decide for all of them. Each
    /// alternative has the node's target and no condition or required flag of its own. Empty for any other node.
    /// </summary>
    public IReadOnlyList<MappingEntry> Alternatives { get; init; } = [];

    /// <summary>
    /// For a list whose items are not all literal: every item, in the order the list writes them, each with the list's
    /// target. The items of a list of values (<c>viewers: ["{$param.aclViewer}", {$cache: ...}]</c>) are each a literal or
    /// a value node; those of a list of objects (<c>TechnicalAssurances: [ { TechnicalAssuranceTypeID: { $expr: ... } } ]</c>)
    /// are each a literal object or an object whose properties read values (<see cref="Properties"/>). The list is what they
    /// give, one after another, with a value given twice (whatever its case) written once. <see cref="Static"/> holds the
    /// literal items alone, which is what anything that reads a list's fixed part (the envelope, the gate) reads. Empty for
    /// any other entry.
    /// </summary>
    public IReadOnlyList<MappingEntry> Parts { get; init; } = [];

    /// <summary>
    /// For an item of a list of objects some of whose properties read values: the item's properties, in the order the item
    /// writes them, each an entry laid out as the record tree lays out a property (a literal, a value node, a
    /// <c>$coalesce</c> node, or a list of values, an object's properties each an entry of their own) and filling a variable
    /// inside the list's items (<c>osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID</c>). They read the row the list
    /// is in. The item is the object they give, and one none of them gives a value to adds nothing to the list. Empty for
    /// any other entry.
    /// </summary>
    public IReadOnlyList<MappingEntry> Properties { get; init; } = [];

    public string? Description { get; init; }

    /// <summary>
    /// The node's <c>$assert</c>: what its value has to be (osdu/docs/reference/flow/mapping-assertions.md), each judged on
    /// the value the record carries or on the value the row gives it, and saying what a record that breaks it does. Empty for
    /// a node that states none, and for an alternative of a <c>$coalesce</c> node, whose assertions are the node's.
    /// </summary>
    public IReadOnlyList<NodeAssertion> Assertions { get; init; } = [];

    /// <summary>The node's assertions judged on the value the row gives it, before its modifiers.</summary>
    public IEnumerable<NodeAssertion> IncomingAssertions => Assertions.Where(a => a.Stage == AssertionStage.Incoming);

    /// <summary>True for a <c>$coalesce</c> node: one whose value is the first of its alternatives that gives one.</summary>
    public bool IsCoalesce => Alternatives.Count > 0;

    /// <summary>True for a list some of whose items are value nodes, or objects whose properties read values.</summary>
    public bool IsList => Parts.Count > 0;

    /// <summary>True for an item of a list of objects some of whose properties read values.</summary>
    public bool IsObject => Properties.Count > 0;

    /// <summary>
    /// The value nodes the entry reads its value with: the entry itself, then its alternatives for a <c>$coalesce</c> node,
    /// or for a list the value nodes among its items and the value nodes of its object items' properties. Anything that asks
    /// what an entry reads (columns, cached types, searches, the ids it builds) asks each of them.
    /// </summary>
    public IEnumerable<MappingEntry> ValueNodes => Alternatives.Count > 0
        ? [this, .. Alternatives]
        : Parts.Count > 0 ? [this, .. Parts.SelectMany(PartNodes)] : [this];

    /// <summary>
    /// The entries that fill variables of the template through this one, each at its own target: the entry itself, then the
    /// properties of its object items and the properties inside those in turn. Anything that asks which variables a mapping
    /// fills, rather than what it reads, asks each of them.
    /// </summary>
    public IEnumerable<MappingEntry> Fillers => [this, .. Parts.SelectMany(part => part.Properties).SelectMany(property => property.Fillers)];

    /// <summary>
    /// The value nodes one item of a list reads with: none for a literal, the item itself for a value node, and for an object
    /// those of each of its properties.
    /// </summary>
    private static IEnumerable<MappingEntry> PartNodes(MappingEntry part)
        => part.IsObject ? part.Properties.SelectMany(property => property.ValueNodes) : part.IsPlainLiteral ? [] : [part];

    /// <summary>An item of a list written as the literal it is: no source, and no condition of its own.</summary>
    public bool IsPlainLiteral => IsStatic && AppliesWhen is null;

    /// <summary>True for an entry that reads nothing: a literal, or a list whose items it holds. An object item's properties read values, so it is not.</summary>
    public bool IsStatic => Source is null && Properties.Count == 0;

    public bool IsRepeater => Source?.Kind == MappingSourceKind.DatasetRows;

    /// <summary>How messages name the entry: where the document writes it, or its template variable.</summary>
    public string Where => Location ?? Target.Text;

    /// <summary>
    /// The entry's expressions: the one its value is computed by, its <c>$when</c> and its <c>$where</c>, then those of its
    /// alternatives, its items and an object item's properties.
    /// </summary>
    public IEnumerable<MappingExpression> Expressions
    {
        get
        {
            if (Source?.Expression is { } computed)
            {
                yield return computed;
            }

            if (AppliesWhen is { } condition)
            {
                yield return condition;
            }

            if (RowFilter is { } filter)
            {
                yield return filter;
            }

            foreach (var alternative in Alternatives.Concat(Parts).Concat(Properties))
            {
                foreach (var expression in alternative.Expressions)
                {
                    yield return expression;
                }
            }
        }
    }

    /// <summary>
    /// Every dataset column the entry reads: the column its source reads, those its expressions read, its findBy values
    /// and the tokens of an id it builds, a <c>$findAll</c> operand and what its lookup reads, and for a <c>$coalesce</c>
    /// node or a list those of every alternative, item and object item's property.
    /// </summary>
    public IEnumerable<DatasetColumn> Columns
    {
        get
        {
            // The expressions include the alternatives' and the items' own, so their columns come through here once.
            foreach (var expressionColumn in Expressions.SelectMany(e => e.Columns))
            {
                yield return expressionColumn;
            }

            foreach (var node in ValueNodes)
            {
                if (node.Source?.Column is { } column)
                {
                    yield return column;
                }

                // An incoming assertion reads the row the node reads, so a column its conditions name is one the node reads.
                foreach (var assertionColumn in node.Assertions.SelectMany(a => a.Where).Select(w => w.Column).OfType<DatasetColumn>())
                {
                    yield return assertionColumn;
                }

                foreach (var find in node.FindBy)
                {
                    if (find.Column is { } findColumn)
                    {
                        yield return findColumn;
                    }
                }

                if (node.FindAll?.Operand is { } operand)
                {
                    if (operand.Column is { } operandColumn)
                    {
                        yield return operandColumn;
                    }

                    foreach (var lookupColumn in operand.Lookup?.Columns ?? [])
                    {
                        yield return lookupColumn;
                    }
                }

                foreach (var modifier in node.Modifiers)
                {
                    if (modifier.Id is { } id)
                    {
                        foreach (var idColumn in id.Columns)
                        {
                            yield return idColumn;
                        }
                    }
                }
            }
        }
    }

    /// <summary>A <c>{$param.name}</c> token inside a literal text.</summary>
    public const string ParameterTokenPattern = @"\{\$param\.(?<name>[A-Za-z0-9_]+)\}";

    /// <summary>Replaces <c>{$param.name}</c> tokens in a literal text; a parameter without a value leaves its token, which the preflight reports.</summary>
    public static string ExpandParameters(string text, Func<string, string?> parameter)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parameter);
        return ParameterToken().Replace(text, m => parameter(m.Groups["name"].Value) ?? m.Value);
    }

    /// <summary>The parameter names a literal text's tokens name.</summary>
    public static IEnumerable<string> ParameterNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParameterToken().Matches(text).Select(m => m.Groups["name"].Value);
    }

    [GeneratedRegex(ParameterTokenPattern)]
    private static partial Regex ParameterToken();
}

/// <summary>
/// One record set a mapping resolves by searching the platform. The kind is what a search is issued against; the name
/// is what entries write, so a mapping can search two sets of the same kind under different names.
/// </summary>
/// <param name="Name">The name nodes write, as in <c>$search: Wellbore</c>.</param>
/// <param name="Kind">
/// The OSDU kind searched: one entity type, at one version or at every version (<c>osdu:wks:master-data--Wellbore:*</c>).
/// </param>
/// <param name="Schema">
/// The template whose schema says how the searched kind's properties are indexed, settled the way a mapping settles its
/// own template, so the query a render sends is fixed by the mapping's version and not by whatever the platform says on
/// the day. It is a version of the same entity type as <paramref name="Kind"/>.
/// </param>
/// <param name="Description">What the set is, shown wherever the mapping is listed.</param>
public sealed record MappingSearch(string Name, string Kind, TemplateReference Schema, string? Description);

/// <summary>
/// Something a render needs from the platform before it can finish, which the run asks for between two renders of a row
/// (<see cref="Rendering.IRecordSearch"/>): a search (<see cref="SearchQuestion"/>), or the record's parent read by its id
/// (<see cref="ParentQuestion"/>). Two renders asking the same question ask one question, so a run asks it once.
/// </summary>
public abstract record PlatformQuestion;

/// <summary>
/// The record's parent, to be read from the platform's storage by its OSDU id: the record of the kind the mapping's template
/// is linked to whose id is the record's own with the parent's entity type (osdu/docs/reference/flow/mapping-parent.md).
/// </summary>
/// <param name="Id">The parent's OSDU id, without a version.</param>
/// <param name="Kind">The kind the template names as its parent.</param>
/// <param name="Sections">
/// The paths the mapping reads in it, joined by <c>|</c> in ordinal order (<see cref="Paths"/>): all a run keeps of the record
/// it reads, so a run reading a parent per record keeps what it renders from and not every parent whole.
/// </param>
public sealed record ParentQuestion(string Id, string Kind, string Sections) : PlatformQuestion
{
    /// <summary>The paths the mapping reads in the parent, as <see cref="Sections"/> joins them.</summary>
    public IReadOnlyList<string> Paths => Sections.Length == 0 ? [] : Sections.Split('|');

    /// <summary>The paths <paramref name="paths"/> as a question carries them.</summary>
    public static string SectionsOf(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return string.Join('|', paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }
}

/// <summary>
/// One question a render needs answered before it can finish: the query to run against a kind, with the property and
/// value it compares for anyone reading the answer. Two renders asking the same question are the same question, so a run
/// answers it once.
/// </summary>
/// <param name="Kind">The OSDU kind searched.</param>
/// <param name="Field">The property compared, as the mapping writes it.</param>
/// <param name="Value">What that property must equal, after the entry's modifiers have run.</param>
/// <param name="Query">The query sent, built from the property as the searched kind's schema says it is indexed.</param>
public sealed record SearchQuestion(string Kind, string Field, string Value, string Query) : PlatformQuestion;
