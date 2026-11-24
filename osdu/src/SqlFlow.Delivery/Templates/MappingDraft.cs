using System.Text.Json.Serialization;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

/// <summary>Where a draft entry's value comes from, as the mapping builder edits it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MappingDraftInput>))]
public enum MappingDraftInput
{
    /// <summary>A column of the dataset's row, or of a child dataset's row inside a repeater.</summary>
    Dataset,

    /// <summary>The rows of a child dataset, one array item each.</summary>
    Repeat,

    /// <summary>A field of a cached record.</summary>
    Cache,

    /// <summary>A fixed value.</summary>
    Static,

    /// <summary>The id of a record found on the platform by searching, as the render needs it.</summary>
    Search,

    /// <summary>A value an expression computes from the row (<c>$expr</c>).</summary>
    Expression,

    /// <summary>
    /// The first of several alternatives that gives a value (<c>$coalesce</c>): each a dataset column, an expression, the
    /// cache, a lookup, a search or a fixed value of its own, tried in order.
    /// </summary>
    Coalesce,

    /// <summary>A field of the record one of the mapping's lookups finds (<c>$lookup</c>).</summary>
    Lookup,

    /// <summary>
    /// A list: its items in order, the list being everything they give, a value given twice written once. The items of a
    /// list of values are each a fixed value or a node reading values of its own (a <c>$findAll</c> among them); those of a
    /// list of objects are each an object input or a fixed object.
    /// </summary>
    List,

    /// <summary>
    /// An item of a list of objects, a group of properties: the object its properties give, each property an entry of its
    /// own input filling a variable inside the list's items (<c>osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID</c>).
    /// </summary>
    Group,

    /// <summary>
    /// A section of the record's parent (<c>$parent</c>, osdu/docs/reference/flow/mapping-parent.md): the record of the kind
    /// the template is linked to with the same minted id, read from the platform as the record renders.
    /// </summary>
    Parent,

    /// <summary>
    /// The reference to one of the record's children (<c>$child</c>, osdu/docs/reference/flow/mapping-child.md): the record of a
    /// kind whose template is linked to this one, with the same minted id, written once the platform holds it.
    /// </summary>
    Child,
}

/// <summary>
/// A mapping as the builder edits it: the header, the parameters, and one entry per variable it fills
/// (osdu/docs/reference/flow/mapping.md). The entries are flat, each naming its variable by target path; the document
/// the draft is written as lays them out as the record tree. Every text a person types is written in the mapping
/// language as the document reads it: a literal's <c>{$param.name}</c>, an id template's tokens and the label's
/// <c>{column}</c>.
/// </summary>
public sealed record MappingDraft
{
    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string TemplateKind { get; init; } = string.Empty;

    public string TemplateVersion { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string System { get; init; } = string.Empty;

    /// <summary>The dataset columns of the key, without the <c>dataset.</c> prefix.</summary>
    public IReadOnlyList<string> Key { get; init; } = [];

    /// <summary>The label as written, with <c>{column}</c> tokens naming columns of the dataset's own row.</summary>
    public string? Label { get; init; }

    /// <summary>The dataset columns an operator finds a record by, without the <c>dataset.</c> prefix.</summary>
    public IReadOnlyList<string> Identity { get; init; } = [];

    /// <summary>
    /// What the OSDU id's unique segment is made from, as <c>dataset.idFrom</c> writes it: <c>key</c> for the key's own values,
    /// <c>deliveryKey</c> or null for the delivery key.
    /// </summary>
    public string? IdFrom { get; init; }

    /// <summary>The rules on the rows the mapping renders from (<c>dataset.assert</c>), in the order the document writes them.</summary>
    public IReadOnlyList<MappingDraftRowRule> Rules { get; init; } = [];

    public IReadOnlyList<MappingDraftParameter> Parameters { get; init; } = [];

    /// <summary>The record sets the mapping's search entries look in.</summary>
    public IReadOnlyList<MappingDraftSearch> Searches { get; init; } = [];

    /// <summary>The records the mapping finds once for a row and reads wherever the record needs them (<c>lookups</c>).</summary>
    public IReadOnlyList<MappingDraftLookup> Lookups { get; init; } = [];

    /// <summary>
    /// The access restriction a record goes out under while a value an entry marks <c>$restrictWhenMissing</c> is missing
    /// (<c>restrictedAccess</c>, osdu/docs/reference/flow/mapping-restricted-access.md), as the mapping writes it; null for none.
    /// </summary>
    public string? RestrictedAccess { get; init; }

    public IReadOnlyList<MappingDraftEntry> Entries { get; init; } = [];
}

public sealed record MappingDraftParameter(string Name, bool Required, string? Default, string? Description);

/// <summary>One search: the name entries read it by, the kind it looks in, and the template whose schema says how that kind is indexed.</summary>
public sealed record MappingDraftSearch(string Name, string Kind, string SchemaKind, string SchemaVersion, string? Description);

/// <summary>
/// One lookup: the name nodes read it by, the cached type its record is found in, the findBy lines that find it against the
/// dataset's own row, and the modifiers and separator fold a cache entry takes.
/// </summary>
public sealed record MappingDraftLookup
{
    public string Name { get; init; } = string.Empty;

    public string CacheType { get; init; } = string.Empty;

    /// <summary>The lines tried in order; a column is one of the dataset's own row, since a lookup reads that row wherever it is read.</summary>
    public IReadOnlyList<MappingDraftFind> FindBy { get; init; } = [];

    public IReadOnlyList<MappingDraftModifier> Modifiers { get; init; } = [];

    public bool IgnoreSeparators { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// A cache entry's <c>$findAll</c>: the cached field the rows are found by and what it holds, which is one of a column
/// (<c>column</c>, or <c>child.column</c> inside a repeater), a fixed text, or a field of a lookup's record written with the
/// lookup's name (<c>wellbore.GeoContexts.FieldID</c>); and the fields a row must hold nothing under.
/// </summary>
public sealed record MappingDraftFindAll
{
    public string Field { get; init; } = string.Empty;

    public string? Column { get; init; }

    public string? Literal { get; init; }

    public string? Lookup { get; init; }

    public IReadOnlyList<string> Empty { get; init; } = [];

    /// <summary>The lines comparing two fields of the same row, <c>GroupName contains CountryName</c>.</summary>
    public IReadOnlyList<MappingDraftFindAllContains> Contains { get; init; } = [];
}

/// <summary>A find all line a row meets when its text under <see cref="Field"/> holds its text under <see cref="Part"/>.</summary>
public sealed record MappingDraftFindAllContains
{
    public string Field { get; init; } = string.Empty;

    public string Part { get; init; } = string.Empty;
}

/// <summary>One entry as the builder edits it.</summary>
public sealed record MappingDraftEntry
{
    public string Target { get; init; } = string.Empty;

    public MappingDraftInput Input { get; init; }

    /// <summary>For a dataset input: <c>column</c>, or <c>child.column</c> inside a repeater.</summary>
    public string? Column { get; init; }

    /// <summary>For a repeat input: the child dataset.</summary>
    public string? Child { get; init; }

    /// <summary>For a cache input: the cached type. For a search input: the search, by the name the mapping declares it under.</summary>
    public string? CacheType { get; init; }

    /// <summary>For a cache or a lookup input: the field of the record read. For a search input: always id.</summary>
    public string? CacheField { get; init; }

    /// <summary>For a lookup input: the lookup read, by the name the mapping declares it under.</summary>
    public string? Lookup { get; init; }

    /// <summary>For a parent input: the path of the section of the record's parent it takes, from the record's root (<c>data.DDMSDatasets</c>).</summary>
    public string? ParentPath { get; init; }

    /// <summary>For a child input: the kind of the child it links (<c>welldb:wks:work-product-component--WellLogDetail:1.0.0</c>), whose template is linked to this one.</summary>
    public string? ChildKind { get; init; }

    public IReadOnlyList<MappingDraftFind> FindBy { get; init; } = [];

    /// <summary>For a cache input that reads every matching row (<c>$findAll</c>) in place of findBy lines; null for one that finds one record.</summary>
    public MappingDraftFindAll? FindAll { get; init; }

    public IReadOnlyList<MappingDraftModifier> Modifiers { get; init; } = [];

    /// <summary>For an expression input: the expression, as the record tree writes it in the entry's scope.</summary>
    public string? Expression { get; init; }

    /// <summary>The entry's condition (<c>$when</c>), as the record tree writes it in the entry's scope, or null to write it always.</summary>
    public string? When { get; init; }

    /// <summary>For a repeat input: the condition a child row must hold to become an item (<c>$where</c>), or null for every row.</summary>
    public string? Where { get; init; }

    public bool Required { get; init; } = true;

    /// <summary>
    /// <c>$restrictWhenMissing</c>: a value the entry should give and does not restricts the record (it goes out under the
    /// mapping's <c>restrictedAccess</c>, the property left out) rather than holding it. Beside a required entry only.
    /// </summary>
    public bool RestrictWhenMissing { get; init; }

    public bool IgnoreSeparators { get; init; }

    /// <summary>
    /// For an entry that builds an id with id or ref (<c>$unverified</c>): the id is written even when the cache holds
    /// records of its entity type and not this one, and recorded as an unverified reference.
    /// </summary>
    public bool Unverified { get; init; }

    /// <summary>
    /// For a coalesce input: the alternatives, in the order they are tried, each an entry of its own input with its own
    /// findBy, modifiers and flags. Their target, condition, required flag and description are the entry's, not theirs.
    /// </summary>
    public IReadOnlyList<MappingDraftEntry> Alternatives { get; init; } = [];

    /// <summary>
    /// For a list input: the items in the order they are written, each an entry of its own input; a fixed item of a list of
    /// values holds one value, with its own condition, required flag and description, and a fixed item of a list of objects
    /// one object. Their target is the entry's, and the entry itself takes no condition, since a list is written as the
    /// items alone.
    /// </summary>
    public IReadOnlyList<MappingDraftEntry> Items { get; init; } = [];

    /// <summary>
    /// For a group input, an item of a list of objects: its properties in the order they are written, each an entry of its
    /// own input (a fixed value, a value read from the dataset, the cache, a lookup or a search, the first of several, or a
    /// list of values) whose target is the variable inside the list's items it fills
    /// (<c>osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID</c>). The item takes no condition, required flag or
    /// description of its own; its properties do.
    /// </summary>
    public IReadOnlyList<MappingDraftEntry> Properties { get; init; } = [];

    /// <summary>For a static input: the value as JSON text.</summary>
    public string? Static { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// The node's assertions (<c>$assert</c>, osdu/docs/reference/flow/mapping-assertions.md), in the order they are written.
    /// A <c>$coalesce</c> node's are the node's, never an alternative's; a list, an item of a list of values and a literal
    /// take none.
    /// </summary>
    public IReadOnlyList<MappingDraftAssertion> Assertions { get; init; } = [];

    /// <summary>True when the builder proposed the entry from the cache, so the page can say so until someone edits it.</summary>
    public bool Prefilled { get; init; }
}

/// <summary>
/// One assertion of a node as the builder edits it (osdu/docs/reference/flow/mapping-assertions.md): the condition's
/// operator and its operand as the document writes it after the operator (<c>[0, 250]</c>, <c>'GR'</c>,
/// <c>{ atLeast: 1 }</c>), the value it judges, what a record that fails it does, and how it reads several values, the
/// conditions it is judged under, its name and its description.
/// </summary>
public sealed record MappingDraftAssertion
{
    public string Operator { get; init; } = "equals";

    /// <summary>The operand, one line of YAML as it follows the operator.</summary>
    public string Operand { get; init; } = string.Empty;

    /// <summary><c>record</c> (the default) or <c>incoming</c>.</summary>
    public string Stage { get; init; } = AssertionWords.Record;

    /// <summary><c>hold</c> (the default), <c>report</c> or <c>omit</c>.</summary>
    public string OnFail { get; init; } = AssertionWords.Hold;

    /// <summary>For a property holding several values: true when one of them has to meet it, false when every one does.</summary>
    public bool AnyValue { get; init; }

    public bool IgnoreCase { get; init; }

    public double? Tolerance { get; init; }

    public IReadOnlyList<MappingDraftAssertionFilter> Where { get; init; } = [];

    public string? Name { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// One rule on the rows a mapping renders from (<c>dataset.assert</c>, osdu/docs/reference/flow/mapping-dataset-assertions.md),
/// as the draft holds it: what it judges (<see cref="Kind"/>), over which rows, its condition and settings, each written back
/// as the document writes it.
/// </summary>
public sealed record MappingDraftRowRule
{
    public const string ValueKind = "value";
    public const string CountKind = "count";
    public const string UniqueKind = "unique";

    /// <summary><c>value</c> (a column and a condition), <c>count</c> or <c>unique</c>.</summary>
    public string Kind { get; init; } = ValueKind;

    /// <summary><c>record</c> (the default): judged as each record renders; <c>run</c>: once a run, over every row of its scope.</summary>
    public string Per { get; init; } = MappingMapper.PerRecord;

    /// <summary>The child dataset whose rows it reads; null for the record's own row of the record table.</summary>
    public string? Rows { get; init; }

    /// <summary>For a value rule: the column judged.</summary>
    public string? Column { get; init; }

    /// <summary>For a value rule: the condition's operator.</summary>
    public string Operator { get; init; } = "equals";

    /// <summary>For a value rule: the operand, one line of YAML as it follows the operator.</summary>
    public string Operand { get; init; } = string.Empty;

    /// <summary>For a count rule: the comparison, one line of YAML as it follows <c>count:</c> (<c>{ atLeast: 2 }</c>, or a number).</summary>
    public string? Count { get; init; }

    /// <summary>For a unique rule: the columns no two rows share.</summary>
    public IReadOnlyList<string> Unique { get; init; } = [];

    /// <summary>For a value rule over a child dataset's rows: true when one row has to meet it, false when every row does.</summary>
    public bool AnyRow { get; init; }

    public bool IgnoreCase { get; init; }

    public double? Tolerance { get; init; }

    /// <summary><c>hold</c> (the default) or <c>report</c>.</summary>
    public string OnFail { get; init; } = AssertionWords.Hold;

    /// <summary>The conditions selecting the rows it reads; each reads a column (<c>column</c>, or <c>child.column</c> for a child dataset's).</summary>
    public IReadOnlyList<MappingDraftAssertionFilter> Where { get; init; } = [];

    public string? Name { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// One condition an assertion is judged under: what it reads (<c>field</c>, a path into the record, for the record stage;
/// <c>column</c>, a column as the draft names one, for the incoming stage), its operator and its operand.
/// </summary>
public sealed record MappingDraftAssertionFilter(string Reads, string Path, string Operator, string Operand, bool IgnoreCase = false, double? Tolerance = null);

/// <summary>
/// One findBy line: the cached field, and the column (<c>column</c> or <c>child.column</c>) or literal it must equal. A line of
/// a cache entry or a lookup may compare more fields in <see cref="And"/>, each one comparison with none of its own, which a
/// record must meet together with this one (<c>Well = well_name and Mnemonic = curve_mnemonic</c>); null or empty for a line
/// of one.
/// </summary>
public sealed record MappingDraftFind(string Field, string? Column, string? Literal, IReadOnlyList<MappingDraftFind>? And = null)
{
    /// <summary>Every comparison of the line, this one first.</summary>
    [JsonIgnore]
    public IReadOnlyList<MappingDraftFind> Comparisons => And is { Count: > 0 } more ? [this, .. more] : [this];
}

/// <summary>
/// One modifier: trim, upper, lower, split, replace, equals, date, number or id, with its settings; an id carries its template
/// in <see cref="Text"/>. A replace carries its pairs,
/// or the cached type it reads its table from in <see cref="Table"/> with the fields it matches on (<see cref="Match"/>)
/// and replaces by (<see cref="Field"/>), either left out for the table to settle; and what an unlisted value becomes:
/// <see cref="OtherwiseKind"/> is keep (the default), empty (no value) or text, with the text in <see cref="OtherwiseText"/>.
/// </summary>
public sealed record MappingDraftModifier(
    string Kind, string? Separator = null, int? Part = null, IReadOnlyList<MappingDraftReplacement>? Replacements = null, string? Text = null,
    string? DecimalSeparator = null, string? GroupSeparator = null, string? OtherwiseKind = null, string? OtherwiseText = null,
    string? Table = null, string? Match = null, string? Field = null);

/// <summary>One pair of a replace: the incoming value, and what it becomes; a null <see cref="To"/> is no value.</summary>
public sealed record MappingDraftReplacement(string From, string? To);

/// <summary>What the builder found about a draft: an error stops the mapping from loading, a warning does not.</summary>
public sealed record MappingDraftIssue(string Severity, string Message, string? Target = null)
{
    public const string ErrorSeverity = "error";

    public const string WarningSeverity = "warning";
}
