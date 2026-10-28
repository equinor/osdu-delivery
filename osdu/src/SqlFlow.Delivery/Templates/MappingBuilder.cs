using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
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

    public IReadOnlyList<MappingDraftParameter> Parameters { get; init; } = [];

    /// <summary>The record sets the mapping's search entries look in.</summary>
    public IReadOnlyList<MappingDraftSearch> Searches { get; init; } = [];

    /// <summary>The records the mapping finds once for a row and reads wherever the record needs them (<c>lookups</c>).</summary>
    public IReadOnlyList<MappingDraftLookup> Lookups { get; init; } = [];

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
/// One condition an assertion is judged under: what it reads (<c>field</c>, a path into the record, for the record stage;
/// <c>column</c>, a column as the draft names one, for the incoming stage), its operator and its operand.
/// </summary>
public sealed record MappingDraftAssertionFilter(string Reads, string Path, string Operator, string Operand, bool IgnoreCase = false, double? Tolerance = null);

/// <summary>One findBy line: the cached field, and the column (<c>column</c> or <c>child.column</c>) or literal it must equal.</summary>
public sealed record MappingDraftFind(string Field, string? Column, string? Literal);

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

/// <summary>
/// A type a cache holds: the name mappings read it by, its entity type, the fields it captures, and for a lookup table the
/// name its key is kept under (null for a type of OSDU records).
/// </summary>
public sealed record CachedTypeInfo(string Name, string EntityType, IReadOnlyList<string> Fields, string? Key = null);

/// <summary>
/// The mapping builder's logic (osdu/docs/reference/concepts/gui.md, Mapping builder): a draft prefilled from the
/// template and the cache, the checks that say what is still missing, the YAML the draft is written as, and the draft of
/// an existing mapping. The YAML is the one written form; what it loads to is decided by the document loader alone.
/// </summary>
public static partial class MappingBuilder
{
    /// <summary>The access and legal variables every record carries, which a mapping gives as static lists.</summary>
    public static readonly IReadOnlyList<string> EnvelopeTargets =
    [
        "osdu.acl.owners",
        "osdu.acl.viewers",
        "osdu.legal.legaltags",
        "osdu.legal.otherRelevantDataCountries",
    ];

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~",
    };

    /// <summary>
    /// A new draft for a template: the envelope entries to fill, and a cache entry for every variable outside a repeater
    /// that points to an entity type the cache holds, finding the record by the type's first cached field.
    /// </summary>
    public static MappingDraft Draft(OsduTemplate template, IReadOnlyList<CachedTypeInfo> cache, string name, string version, string system)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(cache);
        var entries = EnvelopeTargets.Select(target => new MappingDraftEntry { Target = target, Input = MappingDraftInput.Static, Static = "[]" }).ToList();
        foreach (var variable in template.Fillable)
        {
            if (variable.Path.IsRepeated || variable.Shape is not (TemplateVariableShape.Value or TemplateVariableShape.ValueList)
                || variable.Relationships.Count == 0 || EnvelopeTargets.Contains(variable.Path.Text, StringComparer.Ordinal))
            {
                continue;
            }

            if (CacheTypesFor(variable, cache) is not { Count: > 0 } answering)
            {
                continue;
            }

            var cached = answering[0];

            entries.Add(new MappingDraftEntry
            {
                Target = variable.Path.Text,
                Input = MappingDraftInput.Cache,
                CacheType = cached.Name,
                CacheField = "id",
                FindBy = [new MappingDraftFind(cached.Fields.Count > 0 ? cached.Fields[0] : "id", string.Empty, null)],
                Prefilled = true,
            });
        }

        return new MappingDraft
        {
            Name = name?.Trim() ?? string.Empty,
            Version = version?.Trim() ?? string.Empty,
            TemplateKind = template.Kind,
            TemplateVersion = template.Version,
            System = system?.Trim() ?? string.Empty,
            Parameters = [new MappingDraftParameter(Snapshots.RenderContext.DataPartitionParameter, true, null, "The OSDU data partition record ids and references are minted in.")],
            Entries = entries,
        };
    }

    /// <summary>The cached types whose entity type a variable's relationships allow.</summary>
    public static IReadOnlyList<CachedTypeInfo> CacheTypesFor(TemplateVariable variable, IReadOnlyList<CachedTypeInfo> cache)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(cache);
        return cache.Where(c => variable.Relationships.Any(r => string.Equals(r, c.EntityType, StringComparison.Ordinal)
                || (!r.Contains("--", StringComparison.Ordinal) && c.EntityType.StartsWith(r + "--", StringComparison.Ordinal))))
            .ToList();
    }


    /// <summary>
    /// What an entry's assertions lack before they can be written, as the loader would refuse them: a node that has a value
    /// of its own to judge, a condition the assertion flows know with its operand on one line, the words a stage and an
    /// action take, the incoming stage only where the row gives the value, <c>omit</c> only where the property may be left
    /// out and each value is judged on its own, a name of its own, and for each condition what it reads. Whether an operand
    /// suits the operator and the property is judged when the composed mapping is read and checked, as any mapping's is.
    /// </summary>
    private static IEnumerable<string> AssertionProblems(MappingDraftEntry entry)
    {
        if (entry.Assertions.Count == 0)
        {
            yield break;
        }

        if (entry.Input is MappingDraftInput.Static or MappingDraftInput.List or MappingDraftInput.Group)
        {
            yield return entry.Input == MappingDraftInput.Static
                ? "an assertion judges the value a record is given, and a fixed value gives every record the same one; remove its assertions, or read the value from the row."
                : "an assertion judges the value of one property, and a list has no value of its own to judge; give the assertions to the properties its items read.";
            yield break;
        }

        if (entry.Assertions.Count > MappingMapper.MaxNodeAssertions)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"it states {entry.Assertions.Count} assertions, and a property states at most {MappingMapper.MaxNodeAssertions}.");
        }

        var readsRow = entry.Input is MappingDraftInput.Dataset or MappingDraftInput.Expression;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entry.Assertions.Count; i++)
        {
            var assertion = entry.Assertions[i];
            var at = string.Create(CultureInfo.InvariantCulture, $"assertion {i + 1}");
            if (OperandProblem(assertion.Operator, assertion.Operand) is { } operand)
            {
                yield return $"{at}: {operand}";
            }

            var stage = string.IsNullOrWhiteSpace(assertion.Stage) ? AssertionWords.Record : assertion.Stage.Trim();
            var onFail = string.IsNullOrWhiteSpace(assertion.OnFail) ? AssertionWords.Hold : assertion.OnFail.Trim();
            if (!MappingMapper.AssertionStages.Contains(stage, StringComparer.Ordinal))
            {
                yield return $"{at}: its stage is record or incoming.";
            }

            if (!MappingMapper.AssertionActions.Contains(onFail, StringComparer.Ordinal))
            {
                yield return $"{at}: what a failure does is hold, report or omit.";
            }

            var incoming = stage == AssertionWords.Incoming;
            if (incoming && !readsRow)
            {
                yield return $"{at}: the incoming stage judges the value the row gives, which a dataset column or an expression reads; this property's value comes from elsewhere, so judge the value the record carries (stage: record).";
            }

            if (incoming && assertion.AnyValue)
            {
                yield return $"{at}: the incoming stage judges the one value the row gives, so it judges no set of values; judge every value.";
            }

            if (onFail == AssertionWords.Omit && entry.Required)
            {
                yield return $"{at}: omit leaves the value out of the record, and the property is required; make it not required, or hold or report the record.";
            }

            if (onFail == AssertionWords.Omit && assertion.AnyValue)
            {
                yield return $"{at}: omit leaves out each value that fails, and judging whether one of several values meets it judges them together; judge every value, or hold or report the record.";
            }

            if (!string.IsNullOrWhiteSpace(assertion.Name))
            {
                var name = assertion.Name.Trim();
                if (name.Length > NodeAssertion.MaxLabel)
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"{at}: its name is 1 to {NodeAssertion.MaxLabel} characters.");
                }
                else if (!names.Add(name))
                {
                    yield return $"{at}: another assertion of this property is named '{name}'; each has a name of its own, since what it finds is recorded under it.";
                }
            }

            if (assertion.Where.Count > MappingMapper.MaxAssertionConditions)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{at}: it is judged under at most {MappingMapper.MaxAssertionConditions} conditions.");
            }

            var reads = incoming ? MappingMapper.ColumnSetting : MappingMapper.FieldSetting;
            foreach (var filter in assertion.Where)
            {
                if (!string.Equals(filter.Reads?.Trim(), reads, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(filter.Path))
                {
                    yield return incoming
                        ? $"{at}: each of its conditions reads a column of the row, since it is judged before the record is written; name the column."
                        : $"{at}: each of its conditions reads a field of the record, such as data.Name; name the field.";
                }

                if (OperandProblem(filter.Operator, filter.Operand) is { } condition)
                {
                    yield return $"{at}, a condition: {condition}";
                }
            }
        }
    }

    /// <summary>Why an operator and its operand cannot be written as one condition, or null when they can.</summary>
    private static string? OperandProblem(string? op, string? operand)
        => !ConditionReader.Operators.Contains(op?.Trim() ?? string.Empty, StringComparer.Ordinal) || op!.Trim() == "resolves"
            ? $"'{op}' is not a condition; use one of {string.Join(", ", ConditionReader.Operators.Where(o => o != "resolves"))}."
            : string.IsNullOrWhiteSpace(operand)
                ? $"give {op.Trim()} what it compares with, such as {(op.Trim() == "between" ? "[0, 250]" : op.Trim() is "exists" or "empty" ? "true" : "'GR'")}."
                : operand.Any(char.IsControl)
                    ? $"write what {op.Trim()} compares with on one line, as a list in brackets or a map in braces."
                    : null;

    /// <summary>What the draft still lacks before it can be written as a mapping that loads, entry by entry.</summary>
    public static IReadOnlyList<MappingDraftIssue> Incomplete(MappingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<MappingDraftIssue>();
        void Error(string message, string? target = null) => issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, message, target));

        if (!FileNamePart().IsMatch(draft.Name))
        {
            Error("Give the mapping a name of letters, digits, underscore, hyphen and dot; it names the file as Name@version.yaml.");
        }

        if (!FileNamePart().IsMatch(draft.Version))
        {
            Error("Give the mapping a version of letters, digits, underscore, hyphen and dot, such as 1.0.0.");
        }

        if (string.IsNullOrWhiteSpace(draft.TemplateKind) || string.IsNullOrWhiteSpace(draft.TemplateVersion))
        {
            Error("Pick the template the mapping fills, among those the repository's template folders hold.");
        }

        if (string.IsNullOrWhiteSpace(draft.System))
        {
            Error("Name the source system; it is part of every record's key.");
        }

        if (draft.Key.Count == 0 || draft.Key.Any(k => !ColumnName().IsMatch(k)))
        {
            Error("Name the dataset columns that identify a record, such as log_id.");
        }

        if (draft.Identity.Any(k => !ColumnName().IsMatch(k)))
        {
            Error("Name each dataset column an operator finds a record by, such as wellbore_uwi.");
        }

        if (draft.IdFrom is { } idFrom && idFrom != MappingMapper.IdFromDeliveryKey && idFrom != MappingMapper.IdFromKey)
        {
            Error($"Make the OSDU id from the delivery key ({MappingMapper.IdFromDeliveryKey}) or from the key's values ({MappingMapper.IdFromKey}).");
        }

        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            foreach (Match token in LabelTokenPattern().Matches(draft.Label))
            {
                var column = token.Groups["token"].Value.Trim();
                if (!ColumnName().IsMatch(column) && !(column.StartsWith(DatasetReference + ".", StringComparison.Ordinal) && ColumnName().IsMatch(column[(DatasetReference.Length + 1)..])))
                {
                    Error($"The label's {token.Value} names no column; a label token is a column of the dataset's own row, such as {{wellbore_uwi}}.");
                }
            }
        }

        foreach (var search in draft.Searches)
        {
            if (!ColumnName().IsMatch(search.Name ?? string.Empty))
            {
                Error($"Name each search with letters, digits, underscores and hyphens; entries read it as {MappingMapper.SearchKey}: <name>.");
            }

            if (string.IsNullOrWhiteSpace(search.Kind))
            {
                Error($"Search '{search.Name}': give the kind it looks in, such as osdu:wks:master-data--Wellbore:*.");
            }

            if (string.IsNullOrWhiteSpace(search.SchemaKind) || string.IsNullOrWhiteSpace(search.SchemaVersion))
            {
                Error($"Search '{search.Name}': pick the template whose schema says how the kind it searches is indexed, among those the repository's template folders hold.");
            }
        }

        var lookupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lookup in draft.Lookups)
        {
            LookupIssues(lookup, lookupNames, Error);
        }

        var layout = Layout(draft);
        foreach (var left in layout.LeftOut.Where(l => l.Reason is not null))
        {
            Error(left.Reason!, left.Target);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, index) in draft.Entries.Select((entry, index) => (entry, index)))
        {
            var target = entry.Target.Trim();
            if (!TemplatePath.TryParse(target, out _, out var targetError))
            {
                Error(char.ToUpperInvariant(targetError![0]) + targetError[1..] + ".", target);
                continue;
            }

            if (!seen.Add(target))
            {
                Error($"{target} has more than one entry.", target);
            }

            var scope = layout.Scopes.GetValueOrDefault(index);
            switch (entry.Input)
            {
                case MappingDraftInput.Repeat when !ColumnName().IsMatch(entry.Child ?? string.Empty):
                    Error($"{target}: choose the child dataset whose rows become the items.", target);
                    break;
                case MappingDraftInput.Repeat when layout.EmptyRepeats.Contains(index):
                    Error($"{target}: add an entry for each property an item takes from its row, such as {target}[].Name.", target);
                    break;
                case MappingDraftInput.Repeat:
                    break;
                case MappingDraftInput.Coalesce:
                    CoalesceIssues(draft, entry, target, scope, Error);
                    break;
                case MappingDraftInput.List:
                    ListIssues(draft, entry, target, scope, Error);
                    break;
                case MappingDraftInput.Group:
                    Error($"{target}: an object input is an item of a list of objects; choose a list input and add the object as one of its items.", target);
                    break;
                default:
                    ValueIssues(draft, entry, target, target, scope, Error);
                    break;
            }

            foreach (var problem in AssertionProblems(entry))
            {
                Error($"{target}: {problem}", target);
            }

            // A repeat's own condition decides for the whole array, so it reads the row the array is in; its $where reads each child row.
            if (!string.IsNullOrWhiteSpace(entry.When)
                && MappingMapper.ReadCondition(entry.When.Trim(), MappingMapper.WhenKey, entry.Input == MappingDraftInput.Repeat ? null : scope, out var whenProblem) is null)
            {
                Error($"{target}: {whenProblem}", target);
            }

            if (!string.IsNullOrWhiteSpace(entry.Where))
            {
                if (entry.Input != MappingDraftInput.Repeat)
                {
                    Error($"{target}: {MappingMapper.WhereKey} keeps the child rows a repeat makes items of; only a repeat input takes it.", target);
                }
                else if (ColumnName().IsMatch(entry.Child?.Trim() ?? string.Empty)
                    && MappingMapper.ReadCondition(entry.Where.Trim(), MappingMapper.WhereKey, entry.Child!.Trim(), out var whereProblem) is null)
                {
                    Error($"{target}: {whereProblem}", target);
                }
            }
        }

        // The loader refuses a lookup nothing reads, since it says something no record uses.
        var read = draft.Entries.SelectMany(ValueNodesOf).SelectMany(LookupsRead).ToHashSet(StringComparer.Ordinal);
        foreach (var name in draft.Lookups.Select(l => l.Name?.Trim() ?? string.Empty).Where(n => ColumnName().IsMatch(n) && !read.Contains(n)).Distinct(StringComparer.Ordinal))
        {
            Error($"Lookup '{name}' is read by no entry; read a field of its record with a lookup input, or in a find all line, or remove it.", LookupTarget(name));
        }

        return issues;
    }

    /// <summary>The issue target that names a lookup, so the page can open the lookup an issue is about.</summary>
    public static string LookupTarget(string name) => "lookups." + name;

    /// <summary>
    /// An entry and the value nodes it reads through: a coalesce entry's alternatives, a list's items, and the properties of
    /// an item of a list of objects with what they read through in turn.
    /// </summary>
    private static IEnumerable<MappingDraftEntry> ValueNodesOf(MappingDraftEntry entry)
        => [entry, .. entry.Alternatives, .. entry.Items.SelectMany(ValueNodesOf), .. entry.Properties.SelectMany(ValueNodesOf)];

    /// <summary>The lookups one value node reads: the one a lookup input reads, and the one a find all line keys by.</summary>
    private static IEnumerable<string> LookupsRead(MappingDraftEntry node)
    {
        if (node.Input == MappingDraftInput.Lookup && !string.IsNullOrWhiteSpace(node.Lookup))
        {
            yield return node.Lookup.Trim();
        }

        if (node.Input == MappingDraftInput.Cache && node.FindAll?.Lookup is { } path && !string.IsNullOrWhiteSpace(path))
        {
            yield return path.Trim().Split('.')[0];
        }
    }

    /// <summary>
    /// What a lookup lacks: a name of its own, the cached type its record is found in, findBy lines comparing columns of the
    /// dataset's own row (the row a lookup reads wherever it is read), and modifiers that change what those lines compare.
    /// </summary>
    private static void LookupIssues(MappingDraftLookup lookup, HashSet<string> names, Action<string, string?> error)
    {
        var name = lookup.Name?.Trim() ?? string.Empty;
        var target = LookupTarget(name);
        var at = $"Lookup '{name}'";
        if (!ColumnName().IsMatch(name))
        {
            error($"Name each lookup with letters, digits, underscores and hyphens; entries read it as {MappingMapper.LookupKey}: <name>.<field>.", target);
            return;
        }

        if (!names.Add(name))
        {
            error($"{at} is declared twice; give each lookup a name of its own.", target);
        }

        if (!ColumnName().IsMatch(lookup.CacheType?.Trim() ?? string.Empty))
        {
            error($"{at}: choose the cached type its record is found in, such as Wellbore.", target);
        }

        if (lookup.FindBy.Count == 0)
        {
            error($"{at}: say how a row finds its record with at least one findBy line, such as FacilityName = wellbore_uwi.", target);
        }

        foreach (var find in lookup.FindBy)
        {
            if (!FieldPath().IsMatch(find.Field ?? string.Empty) || find.Field!.StartsWith(Marker, StringComparison.Ordinal))
            {
                error($"{at}: a findBy line needs the cached field it compares.", target);
            }
            else if (string.IsNullOrWhiteSpace(find.Literal) && !ColumnName().IsMatch(find.Column?.Trim() ?? string.Empty))
            {
                error(DatasetColumnPattern().IsMatch(find.Column?.Trim() ?? string.Empty)
                    ? $"{at}: findBy {find.Field} compares {find.Column!.Trim()}, a column of a child dataset; a lookup reads the dataset's own row, so it compares a column of that row."
                    : $"{at}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
            }
        }

        foreach (var modifier in lookup.Modifiers)
        {
            if (modifier.Kind is "id" or "ref")
            {
                error($"{at}: the {modifier.Kind} modifier builds an id to write, and a lookup finds a record in the cache; the record's id is read with {MappingMapper.LookupKey}: {name}.id.", target);
            }
            else
            {
                ModifierIssue(modifier, null, at, error);
            }
        }

        if (lookup.Modifiers.Count > 0 && lookup.FindBy.Count > 0 && lookup.FindBy.All(f => !string.IsNullOrWhiteSpace(f.Literal)))
        {
            error($"{at}: modifiers change the dataset value a findBy line compares, and every line compares a fixed text; remove the modifiers.", target);
        }
    }

    /// <summary>
    /// What a list lacks: items, each one value of its own input that is complete as an entry of that input would be, a
    /// fixed item holding one value; or, for a list of objects, items that are each an object input or a fixed object
    /// (<see cref="ObjectItemIssues"/>). The access lists keep a fixed value no record goes without, and the legal lists
    /// hold fixed values alone, since the legal service checks them before a run.
    /// </summary>
    private static void ListIssues(MappingDraft draft, MappingDraftEntry entry, string target, string? scope, Action<string, string?> error)
    {
        var objects = IsObjectList(entry);
        if (objects && TemplatePath.TryParse(target, out var path, out _) && path!.Repeater is { } outer)
        {
            error($"{target} is a list of objects inside the items of {outer.Text}, and a list of objects inside the items of another array is not supported.", target);
            return;
        }

        if (target is "osdu.legal.legaltags" or "osdu.legal.otherRelevantDataCountries")
        {
            error($"{target}: the legal service checks a record's tags and countries before a run, which it can only do for a list the same on every record; give fixed values.", target);
        }
        else if (target is "osdu.acl.owners" or "osdu.acl.viewers"
            && !entry.Items.Any(item => item.Input == MappingDraftInput.Static && string.IsNullOrWhiteSpace(item.When)))
        {
            error($"{target}: every record carries it, so list at least one fixed value without a condition beside the values entries read.", target);
        }

        if (entry.Items.Count == 0)
        {
            error($"{target}: add the list's items, each a fixed value or a value read from the dataset, the cache, a lookup or a search, or for a list of objects each an object whose properties read them.", target);
        }

        if (!string.IsNullOrWhiteSpace(entry.When))
        {
            error(objects
                ? $"{target}: a list of objects is written as its items alone, so it takes no condition; give the properties that need one a condition of their own."
                : $"{target}: a list of values is written as its items alone, so it takes no condition; give the items that need one a condition of their own.", target);
        }

        for (var i = 0; i < entry.Items.Count; i++)
        {
            var item = entry.Items[i];
            var name = $"{target} item {i + 1}";
            if (objects)
            {
                ObjectItemIssues(draft, item, name, target, scope, error);
                continue;
            }

            if (item.Input is MappingDraftInput.Repeat or MappingDraftInput.Coalesce or MappingDraftInput.List)
            {
                error($"{name}: an item gives values of its own; choose a fixed value, a dataset column, an expression, the cache, a lookup or a search.", target);
                continue;
            }

            if (item.Input == MappingDraftInput.Static)
            {
                ItemLiteralIssue(item, name, target, error);
            }
            else
            {
                ValueIssues(draft, item, name, target, scope, error);
            }

            if (item.Assertions.Count > 0)
            {
                error($"{name}: an assertion judges the value of a property, and an item gives one of the values of the list, which has no value of its own to judge; a list of values takes no assertions.", target);
            }

            if (!string.IsNullOrWhiteSpace(item.When) && MappingMapper.ReadCondition(item.When.Trim(), MappingMapper.WhenKey, scope, out var whenProblem) is null)
            {
                error($"{name}: {whenProblem}", target);
            }
        }
    }

    /// <summary>A fixed item of a list: one text, number or true/false, never an object or a list of its own.</summary>
    private static void ItemLiteralIssue(MappingDraftEntry item, string name, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(item.Static) ? null : JsonNode.Parse(item.Static);
        }
        catch (JsonException)
        {
            error($"{name}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            error($"{name}: give the fixed value.", target);
        }
        else if (node is not JsonValue)
        {
            error($"{name}: an item of a list of values is one value, a text, a number or true/false; a list of objects holds objects, each an object input or a fixed object.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{name}: {problem}", target);
        }
    }

    /// <summary>
    /// What an item of a list of objects lacks: an object input whose properties each fill a variable inside the list's items
    /// once and are complete as entries of their own input would be, or a fixed object. An item takes no condition of its
    /// own, since a condition goes on the properties it decides for. A property never repeats rows or holds a list of objects
    /// of its own, since a path steps into one array at most. What a property lacks concerns the list, whose entry it is
    /// part of, so it names the list's target.
    /// </summary>
    private static void ObjectItemIssues(MappingDraft draft, MappingDraftEntry item, string name, string target, string? scope, Action<string, string?> error)
    {
        if (item.Input == MappingDraftInput.Static)
        {
            ObjectLiteralIssue(item, name, target, error);
            return;
        }

        if (item.Input != MappingDraftInput.Group)
        {
            error($"{name}: the items of a list are all objects or all values, and this list holds objects; make the item an object whose properties read the values.", target);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.When) || !string.IsNullOrWhiteSpace(item.Description) || !item.Required)
        {
            error($"{name}: an object item is written as its properties alone, so it takes no condition, required flag or description; give them to its properties.", target);
        }

        if (item.Properties.Count == 0)
        {
            error($"{name}: add the item's properties, each a variable of the items of {target}, such as {target}[].Name.", target);
            return;
        }

        void AtList(string message, string? _) => error(message, target);
        var placed = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.Properties)
        {
            var propertyTarget = property.Target?.Trim() ?? string.Empty;
            if (!TemplatePath.TryParse(propertyTarget, out var path, out _) || path!.Repeater?.Text != target)
            {
                error($"{name}: {(propertyTarget.Length == 0 ? "a property names no variable" : $"{propertyTarget} is not a variable of the items of {target}")}; an item's property fills {target}[].<name>.", target);
                continue;
            }

            var at = $"{propertyTarget} in {name}";
            if (!seen.Add(propertyTarget))
            {
                error($"{at} has more than one entry.", target);
                continue;
            }

            Place(placed, path.WithinItem, property, propertyTarget, leftOut);
            switch (property.Input)
            {
                case MappingDraftInput.Repeat:
                    error($"{at} repeats a child dataset's rows, and a repeated array inside an item of a list is not supported.", target);
                    continue;
                case MappingDraftInput.Group:
                    error($"{at}: an object input is an item of a list of objects; the item's own properties are its entries.", target);
                    continue;
                case MappingDraftInput.List when IsObjectList(property):
                    error($"{at} is a list of objects inside an item of a list, which is not supported.", target);
                    continue;
                case MappingDraftInput.List:
                    ListIssues(draft, property, at, scope, AtList);
                    break;
                case MappingDraftInput.Coalesce:
                    CoalesceIssues(draft, property, at, scope, AtList);
                    break;
                default:
                    ValueIssues(draft, property, at, target, scope, error);
                    break;
            }

            foreach (var problem in AssertionProblems(property))
            {
                error($"{at}: {problem}", target);
            }

            if (!string.IsNullOrWhiteSpace(property.When) && MappingMapper.ReadCondition(property.When.Trim(), MappingMapper.WhenKey, scope, out var whenProblem) is null)
            {
                error($"{at}: {whenProblem}", target);
            }
        }

        foreach (var left in leftOut.Where(l => l.Reason is not null))
        {
            error($"{name}: {left.Reason}.", target);
        }
    }

    /// <summary>
    /// True for a list of objects: one with a group input, or a fixed object written as the object it is, among its items,
    /// which is how the loader reads the list the draft is written as. A fixed value with a condition or a description is
    /// written under <c>$value</c>, a node, so it makes no list one of objects.
    /// </summary>
    private static bool IsObjectList(MappingDraftEntry list)
        => list.Items.Any(item => item.Input == MappingDraftInput.Group
            || (item.Input == MappingDraftInput.Static && StaticNode(item.Static) is JsonObject { Count: > 0 }
                && string.IsNullOrWhiteSpace(item.When) && string.IsNullOrWhiteSpace(item.Description)));

    /// <summary>A fixed value's JSON, or null when it is empty or not JSON.</summary>
    private static JsonNode? StaticNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A fixed item of a list of objects: one object, written as the object it is, so it takes no condition or description.</summary>
    private static void ObjectLiteralIssue(MappingDraftEntry item, string name, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(item.Static) ? null : JsonNode.Parse(item.Static);
        }
        catch (JsonException)
        {
            error($"{name}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is not JsonObject { Count: > 0 })
        {
            error($"{name}: a fixed item of a list of objects is one object holding the item's properties, such as {{\"Name\": \"MD\"}}.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{name}: {problem}", target);
        }

        if (!string.IsNullOrWhiteSpace(item.When) || !string.IsNullOrWhiteSpace(item.Description))
        {
            error($"{name}: a fixed item is written as the object it is, so it takes no condition or description; make it an object item and give its properties one.", target);
        }
    }

    /// <summary>
    /// What a coalesce entry lacks: two alternatives or more, each one value of its own input that is complete as an entry
    /// of that input would be, and a fixed value only as the last, since it always gives a value.
    /// </summary>
    private static void CoalesceIssues(MappingDraft draft, MappingDraftEntry entry, string target, string? scope, Action<string, string?> error)
    {
        if (entry.Alternatives.Count < 2)
        {
            error($"{target}: {MappingMapper.CoalesceKey} takes the first of two or more alternatives that gives a value; add an alternative, or choose one input.", target);
        }

        for (var i = 0; i < entry.Alternatives.Count; i++)
        {
            var alternative = entry.Alternatives[i];
            var name = $"{target} alternative {i + 1}";
            if (alternative.Input is MappingDraftInput.Repeat or MappingDraftInput.Coalesce or MappingDraftInput.List or MappingDraftInput.Group)
            {
                error($"{name}: an alternative reads one value; choose a dataset column, an expression, the cache, a lookup, a search or a fixed value.", target);
                continue;
            }

            if (alternative.Input == MappingDraftInput.Child)
            {
                error($"{name}: a child link gives nothing while OSDU does not hold the child, which no other value stands in for; make it an entry of its own, or, to link several children, the items of a list.", target);
                continue;
            }

            if (i > 0 && entry.Alternatives[i - 1].Input == MappingDraftInput.Static)
            {
                error($"{name} is never tried: the fixed value before it always gives a value. Make the fixed value the last alternative.", target);
            }

            if (alternative.Assertions.Count > 0)
            {
                error($"{name}: an assertion decides for the whole {MappingMapper.CoalesceKey} entry, whichever alternative gives its value; give it to the entry, not to one of its alternatives.", target);
            }

            ValueIssues(draft, alternative, name, target, scope, error);
        }
    }

    /// <summary>
    /// What a parent input lacks (osdu/docs/reference/flow/mapping-parent.md): the path of the section it takes, its property
    /// names joined by dots and never into an array, read by an entry of the record rather than of a repeater's items, and
    /// nothing that finds or matches a record, since the parent is found by the record's own id. Whether the template is
    /// linked to a parent, and the parent's template declares the path, is the preflight's to judge.
    /// </summary>
    private static void ParentIssues(MappingDraftEntry entry, string name, string target, string? scope, Action<string, string?> error)
    {
        var path = entry.ParentPath?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            error($"{name}: name the section of the record's parent to take, such as data.DDMSDatasets.", target);
        }
        else if (path.Contains('[', StringComparison.Ordinal) || !MappingMapper.ParentPathPattern().IsMatch(path))
        {
            error($"{name}: '{path}' is not a path of the parent: its property names joined by dots, never into an array, such as data.DDMSDatasets or data.ExtensionProperties.wdms.", target);
        }

        if (scope is not null)
        {
            error($"{name}: a row of {scope} has no parent; take the whole array from the parent with a parent input on the array itself.", target);
        }

        if (entry.FindBy.Count > 0 || entry.FindAll is not null || entry.IgnoreSeparators || entry.Unverified)
        {
            error($"{name}: the parent is found by the record's own id, so a parent input takes no findBy lines, find all, folded separators or unverified ids.", target);
        }
    }

    /// <summary>
    /// What a child input lacks (osdu/docs/reference/flow/mapping-child.md): the kind of the child it links, a record kind,
    /// read by an entry of the record rather than of a repeater's items, and nothing that finds, matches or changes a value,
    /// since the child is found by the record's own id and the link is its id. A child never holds its record, so the entry
    /// is never required. Whether the kind's template is linked to this one is the preflight's to judge.
    /// </summary>
    private static void ChildIssues(MappingDraftEntry entry, string name, string target, string? scope, Action<string, string?> error)
    {
        var kind = entry.ChildKind?.Trim() ?? string.Empty;
        if (kind.Length == 0)
        {
            error($"{name}: name the kind of the child to link, a custom template linked to this one, such as welldb:wks:work-product-component--WellLogDetail:1.0.0.", target);
        }
        else if (!FlowMapper.IsRecordKind(kind) || !Identity.TargetId.EntityTypeFromKind(kind).Contains("--", StringComparison.Ordinal))
        {
            error($"{name}: '{kind}' is not the kind of a record, 'authority:source:group--Entity:major.minor.patch'.", target);
        }

        if (scope is not null)
        {
            error($"{name}: a row of {scope} has no children of its own; link the child from an entry outside the repeated array.", target);
        }

        if (entry.FindBy.Count > 0 || entry.FindAll is not null || entry.Modifiers.Count > 0 || entry.IgnoreSeparators || entry.Unverified)
        {
            error($"{name}: the child is found by the record's own id and linked by its own, so a child input takes no findBy lines, find all, modifiers, folded separators or unverified ids.", target);
        }

        if (entry.Required)
        {
            error($"{name}: a child link never holds its record: it is written once OSDU holds the child and left out until then, so the entry is not required.", target);
        }
    }

    /// <summary>
    /// What one value lacks, as an entry of its own or as one alternative of a coalesce entry (<paramref name="name"/> says
    /// which): its input, its modifiers, and the id it builds.
    /// </summary>
    private static void ValueIssues(MappingDraft draft, MappingDraftEntry entry, string name, string target, string? scope, Action<string, string?> error)
    {
        switch (entry.Input)
        {
            case MappingDraftInput.Dataset when !DatasetColumnPattern().IsMatch(entry.Column ?? string.Empty):
                error($"{name}: choose the dataset column the value comes from.", target);
                break;
            case MappingDraftInput.Dataset:
                ScopeIssue(entry.Column!, scope, name, error);
                break;
            case MappingDraftInput.Cache:
                if (!ColumnName().IsMatch(entry.CacheType ?? string.Empty) || !FieldPath().IsMatch(entry.CacheField ?? string.Empty))
                {
                    error($"{name}: choose the cached type and the field to read.", target);
                }

                if (entry.FindAll is { } findAll)
                {
                    if (entry.FindBy.Count > 0)
                    {
                        error($"{name}: an entry finds one record with findBy lines or reads every matching row with find all, not both.", target);
                    }

                    if (entry.IgnoreSeparators)
                    {
                        error($"{name}: folding punctuation and spacing loosens how one record is found by name, and find all matches each key ignoring case only; turn it off.", target);
                    }

                    FindAllIssues(draft, entry, findAll, name, target, scope, error);
                    break;
                }

                if (entry.FindBy.Count == 0)
                {
                    error($"{name}: say which cached record to read with at least one findBy line.", target);
                }

                foreach (var find in entry.FindBy)
                {
                    if (!FieldPath().IsMatch(find.Field ?? string.Empty) || find.Field!.StartsWith(Marker, StringComparison.Ordinal))
                    {
                        error($"{name}: a findBy line needs the cached field it compares.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                    {
                        error($"{name}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal))
                    {
                        ScopeIssue(find.Column!, scope, name, error);
                    }
                }

                break;
            case MappingDraftInput.Search:
                if (!draft.Searches.Any(s => string.Equals(s.Name, entry.CacheType, StringComparison.Ordinal)))
                {
                    error($"{name}: choose one of the mapping's searches to look in.", target);
                }

                if (!string.Equals(entry.CacheField, "id", StringComparison.Ordinal))
                {
                    error($"{name}: a search reads the id of the record it finds.", target);
                }

                if (entry.FindBy.Count == 0)
                {
                    error($"{name}: say which record to find with at least one findBy line.", target);
                }

                foreach (var find in entry.FindBy)
                {
                    if (!(find.Field ?? string.Empty).StartsWith("data.", StringComparison.Ordinal) || !Search.OsduPath.IsPath(find.Field))
                    {
                        error($"{name}: a findBy line compares a property under data, such as data.FacilityName.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                    {
                        error($"{name}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
                    }
                    else if (string.IsNullOrWhiteSpace(find.Literal))
                    {
                        ScopeIssue(find.Column!, scope, name, error);
                    }
                }

                break;
            case MappingDraftInput.Lookup:
                if (!draft.Lookups.Any(l => string.Equals(l.Name?.Trim(), entry.Lookup?.Trim(), StringComparison.Ordinal)))
                {
                    error(draft.Lookups.Count == 0
                        ? $"{name}: the mapping declares no lookup to read; add one under Lookups."
                        : $"{name}: choose one of the mapping's lookups to read.", target);
                }

                if (!FieldPath().IsMatch(entry.CacheField?.Trim() ?? string.Empty) || entry.CacheField!.Trim().StartsWith(Marker, StringComparison.Ordinal))
                {
                    error($"{name}: name the field of the lookup's record to read, such as id or GeoContexts.FieldID.", target);
                }

                if (entry.FindBy.Count > 0 || entry.FindAll is not null || entry.Modifiers.Count > 0 || entry.IgnoreSeparators)
                {
                    error($"{name}: a lookup input reads the record its lookup finds, so how the record is found (findBy lines, modifiers, folding separators) is the lookup's to say.", target);
                }

                break;
            case MappingDraftInput.Parent:
                ParentIssues(entry, name, target, scope, error);
                break;
            case MappingDraftInput.Child:
                ChildIssues(entry, name, target, scope, error);
                break;
            case MappingDraftInput.Static:
                StaticIssue(entry, name, error);
                break;
            case MappingDraftInput.Expression:
                if (string.IsNullOrWhiteSpace(entry.Expression))
                {
                    error($"{name}: write the expression the value is computed with, such as coalesce(log_name, log_source).", target);
                }
                else if (MappingMapper.ReadExpression(entry.Expression.Trim(), MappingMapper.ExprKey, scope, out var expressionProblem) is null)
                {
                    error($"{name}: {expressionProblem}", target);
                }

                break;
        }

        foreach (var modifier in entry.Modifiers)
        {
            ModifierIssue(modifier, scope, name, error);
        }

        // id and ref both build the id an entry writes, so the same rules hold for either.
        var builders = entry.Modifiers.Where(m => m.Kind is "id" or "ref").ToList();
        if (builders.Count > 0 && entry.Input is not (MappingDraftInput.Dataset or MappingDraftInput.Expression))
        {
            error($"{name}: the {builders[0].Kind} modifier builds the id an entry writes from a dataset value; choose a dataset column or an expression as the input.", target);
        }
        else if (builders.Count > 1)
        {
            error($"{name}: an entry builds one id, and this one lists {string.Join(" and ", builders.Select(m => m.Kind))}; keep one.", target);
        }
        else if (builders.Count == 1 && entry.Modifiers[^1].Kind is not ("id" or "ref"))
        {
            error($"{name}: {builders[0].Kind} builds what the entry writes, so it is the last modifier; move it to the end.", target);
        }

        if (entry.Unverified && builders.Count == 0)
        {
            error($"{name}: {MappingMapper.UnverifiedKey} lets an id built with id or ref go out when the cache holds no record under it, and this one builds no id.", target);
        }
    }

    /// <summary>
    /// What a find all lacks: the cached field its rows are found by, exactly one value that field is compared with (a
    /// column in the entry's scope, a fixed text, or a field of a declared lookup's record of another type), and fields to
    /// be empty that are named once each and are not the key.
    /// </summary>
    private static void FindAllIssues(
        MappingDraft draft, MappingDraftEntry entry, MappingDraftFindAll findAll, string name, string target, string? scope, Action<string, string?> error)
    {
        var field = findAll.Field?.Trim() ?? string.Empty;
        if (!IsCachedRowField(field))
        {
            error($"{name}: find all needs the cached field the rows are found by, such as FieldIDList.", target);
        }

        var column = findAll.Column?.Trim() ?? string.Empty;
        var lookupPath = findAll.Lookup?.Trim() ?? string.Empty;
        var operands = new[] { column, findAll.Literal ?? string.Empty, lookupPath }.Count(text => !string.IsNullOrWhiteSpace(text));
        if (operands != 1)
        {
            error($"{name}: find all compares {(field.Length == 0 ? "its field" : field)} with one value: a dataset column, a fixed text, or a field of a lookup's record.", target);
        }
        else if (column.Length > 0)
        {
            if (!DatasetColumnPattern().IsMatch(column))
            {
                error($"{name}: find all compares {field} with the dataset column '{column}', which is not a column name.", target);
            }
            else
            {
                ScopeIssue(column, scope, name, error);
            }
        }
        else if (lookupPath.Length > 0)
        {
            var parts = lookupPath.Split('.', 2);
            var lookup = draft.Lookups.FirstOrDefault(l => string.Equals(l.Name?.Trim(), parts[0], StringComparison.Ordinal));
            if (parts.Length < 2 || !FieldPath().IsMatch(parts[1]) || parts[1].StartsWith(Marker, StringComparison.Ordinal))
            {
                error($"{name}: find all reads a field of a lookup's record, named after the lookup, such as wellbore.GeoContexts.FieldID; '{lookupPath}' is not one.", target);
            }
            else if (lookup is null)
            {
                error($"{name}: find all reads lookup '{parts[0]}', which the mapping does not declare.", target);
            }
            else if (string.Equals(lookup.CacheType?.Trim(), entry.CacheType?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error($"{name}: find all finds {entry.CacheType?.Trim()} rows by a field of the {entry.CacheType?.Trim()} record lookup '{parts[0]}' finds; read that record's field with a lookup input instead.", target);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var empty in findAll.Empty.Select(e => e?.Trim() ?? string.Empty))
        {
            if (!IsCachedRowField(empty))
            {
                error($"{name}: find all names the fields a row must hold nothing under, such as FieldList; '{empty}' is not one.", target);
            }
            else if (!seen.Add(empty))
            {
                error($"{name}: find all asks for {empty} to be empty twice.", target);
            }
            else if (string.Equals(empty, field, StringComparison.OrdinalIgnoreCase))
            {
                error($"{name}: find all finds rows by {field} and asks for {field} to be empty; no row holds both.", target);
            }
        }

        if (entry.Modifiers.Count > 0 && column.Length == 0)
        {
            error($"{name}: modifiers change the dataset value find all compares, and this one compares none; remove the modifiers.", target);
        }
    }

    /// <summary>A field of a cached row as a find all line names it: a dotted path, never a word of the mapping language.</summary>
    private static bool IsCachedRowField(string field)
        => FieldPath().IsMatch(field) && !field.StartsWith(Marker, StringComparison.Ordinal)
            && !field.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal);


    /// <summary>
    /// The draft written as a mapping document, in the documented style: the header, then the record tree, each entry at
    /// the place its target names. Incomplete values are written as they stand, so the page shows what
    /// was typed and the loader reports it; an entry that has no place in the tree is left out with a comment saying why,
    /// and <see cref="Incomplete"/> reports it.
    /// </summary>
    public static string ToYaml(MappingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var yaml = new StringBuilder();
        void Line(string text) => yaml.Append(text).Append('\n');

        Line("documentType: mapping");
        Line("name: " + Scalar(draft.Name));
        Line("version: " + Scalar(draft.Version));
        Line("template:");
        Line("  kind: " + Scalar(draft.TemplateKind));
        Line("  version: " + Scalar(draft.TemplateVersion));
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            Line("description: " + Scalar(draft.Description.Trim()));
        }

        Line(string.Empty);
        Line("dataset:");
        Line("  system: " + Scalar(draft.System));
        Line("  key: [" + string.Join(", ", draft.Key.Select(k => FlowScalar(k.Trim()))) + "]");
        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            Line("  label: " + Scalar(draft.Label.Trim()));
        }

        if (draft.Identity.Count > 0)
        {
            Line("  identity: [" + string.Join(", ", draft.Identity.Select(k => FlowScalar(k.Trim()))) + "]");
        }

        if (draft.IdFrom is { } idFrom)
        {
            Line("  idFrom: " + Scalar(idFrom));
        }

        Line(string.Empty);
        Line("parameters:");
        foreach (var parameter in draft.Parameters)
        {
            Line("  " + Scalar(parameter.Name) + ":");
            Line("    required: " + (parameter.Required ? "true" : "false"));
            if (parameter.Default is not null)
            {
                Line("    default: " + Scalar(parameter.Default));
            }

            if (!string.IsNullOrWhiteSpace(parameter.Description))
            {
                Line("    description: " + Scalar(parameter.Description.Trim()));
            }
        }

        if (draft.Searches.Count > 0)
        {
            Line(string.Empty);
            Line("searches:");
            foreach (var search in draft.Searches)
            {
                Line("  " + Scalar(search.Name) + ":");
                Line("    kind: " + Scalar(search.Kind));
                Line("    schema:");
                Line("      kind: " + Scalar(search.SchemaKind));
                Line("      version: " + Scalar(search.SchemaVersion));
                if (!string.IsNullOrWhiteSpace(search.Description))
                {
                    Line("    description: " + Scalar(search.Description.Trim()));
                }
            }
        }

        if (draft.Lookups.Count > 0)
        {
            Line(string.Empty);
            Line("lookups:");
            foreach (var lookup in draft.Lookups)
            {
                WriteLookup(lookup, Line);
            }
        }

        Line(string.Empty);
        Line("record:");
        var layout = Layout(draft);
        foreach (var left in layout.LeftOut)
        {
            Line($"  # Left out: {Comment(left.Reason ?? $"{left.Target} has another entry, which is written")}.");
        }

        WriteProperties(layout.Root, 2, null, Line);
        return yaml.ToString();
    }

    /// <summary>
    /// The draft of a loaded mapping, for opening it in the builder: everything the document says, so writing the draft back
    /// gives the same mapping. Its lookups, the nodes reading them, the rows a <c>$findAll</c> reads, the items of a list of
    /// values, and the object items of a list of objects with their properties all have their place in the draft.
    /// </summary>
    public static MappingDraft FromDefinition(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        // The builder lays out a whole record: its key, its id, its access list and legal block. A fragment has none of them,
        // and its $base nodes read the record a step adds to, which no draft input stands for.
        if (mapping.Fragment)
        {
            throw new FlowValidationException(
                $"Mapping {mapping.Reference} is a fragment (fragment: true), the paths a step of a delivery flow's enrich: adds to a record, and the mapping builder lays out whole records; edit the fragment's file.");
        }

        return new MappingDraft
        {
            Name = mapping.Name,
            Version = mapping.Version,
            TemplateKind = mapping.Template.Kind,
            TemplateVersion = mapping.Template.Version,
            Description = mapping.Description,
            System = mapping.Dataset.System,
            Key = mapping.Dataset.Key,
            Label = mapping.Dataset.Label,
            Identity = mapping.Dataset.Identity,
            IdFrom = mapping.Dataset.IdFrom == MappingIdSource.Key ? MappingMapper.IdFromKey : null,
            Parameters = mapping.Parameters.Select(kv => new MappingDraftParameter(kv.Key, kv.Value.Required, kv.Value.Default, kv.Value.Description)).ToList(),
            Searches = mapping.Searches.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new MappingDraftSearch(s.Name, s.Kind, s.Schema.Kind, s.Schema.Version, s.Description))
                .ToList(),
            Lookups = mapping.Lookups.Values
                .OrderBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => new MappingDraftLookup
                {
                    Name = l.Name,
                    CacheType = l.CacheType,
                    FindBy = l.FindBy.Select(DraftFind).ToList(),
                    Modifiers = l.Modifiers.Select(DraftModifier).ToList(),
                    IgnoreSeparators = l.IgnoreSeparators,
                    Description = l.Description,
                })
                .ToList(),
            Entries = mapping.Entries.Select(Draft).ToList(),
        };
    }

    private static MappingDraftEntry Draft(MappingEntry entry)
    {
        if (entry.IsCoalesce)
        {
            // The node's settings stay with the entry, and each alternative is drafted as a value of its own without them.
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.Coalesce,
                Alternatives = entry.ValueNodes
                    .Select(node => DraftNode(node with { Alternatives = [], AppliesWhen = null, Required = true, Description = null, Assertions = [] }))
                    .ToList(),
                When = entry.AppliesWhen?.Text,
                Required = entry.Required,
                Description = entry.Description,
                Assertions = entry.Assertions.Select(DraftAssertion).ToList(),
            };
        }

        return DraftNode(entry);
    }

    private static MappingDraftEntry DraftNode(MappingEntry entry)
    {
        if (entry.IsList)
        {
            // A list is its items alone: each a literal, a node with its own condition, required flag and description, or an
            // object whose properties are entries of their own.
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.List,
                Items = entry.Parts.Select(DraftNode).ToList(),
            };
        }

        if (entry.IsObject)
        {
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.Group,
                Properties = entry.Properties.Select(Draft).ToList(),
            };
        }

        var source = entry.Source;
        if (source?.Lookup is { } lookup)
        {
            // The node carries its lookup's findBy lines and modifiers as the loader resolved them; the draft keeps them on the lookup.
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.Lookup,
                Lookup = lookup,
                CacheField = source.CacheField,
                When = entry.AppliesWhen?.Text,
                Required = entry.Required,
                Description = entry.Description,
                Assertions = entry.Assertions.Select(DraftAssertion).ToList(),
            };
        }

        return new MappingDraftEntry
        {
            Target = entry.Target.Text,
            Input = source is null
                ? MappingDraftInput.Static
                : source.Kind switch
                {
                    MappingSourceKind.DatasetColumn => MappingDraftInput.Dataset,
                    MappingSourceKind.DatasetRows => MappingDraftInput.Repeat,
                    MappingSourceKind.Search => MappingDraftInput.Search,
                    MappingSourceKind.Expression => MappingDraftInput.Expression,
                    MappingSourceKind.Parent => MappingDraftInput.Parent,
                    MappingSourceKind.Child => MappingDraftInput.Child,
                    _ => MappingDraftInput.Cache,
                },
            Expression = source?.Expression?.Text,
            ParentPath = source?.ParentPath,
            ChildKind = source?.ChildKind,
            Column = source?.Column is { } column ? ColumnText(column) : null,
            Child = source?.Child,
            CacheType = source?.CacheType,
            CacheField = source?.CacheField,
            FindBy = entry.FindBy.Select(DraftFind).ToList(),
            FindAll = entry.FindAll is { } all
                ? new MappingDraftFindAll
                {
                    Field = all.Field,
                    Column = all.Operand.Column is { } operand ? ColumnText(operand) : null,
                    Literal = all.Operand.Literal,
                    Lookup = all.Operand.Lookup is { } read ? $"{read.Name}.{all.Operand.LookupPath}" : null,
                    Empty = all.Empty,
                }
                : null,
            Modifiers = entry.Modifiers.Select(DraftModifier).ToList(),
            When = entry.AppliesWhen?.Text,
            Where = entry.RowFilter?.Text,
            Required = entry.Required,
            IgnoreSeparators = entry.IgnoreSeparators,
            Unverified = entry.Unverified,
            Static = entry.Static?.ToJsonString(),
            Description = entry.Description,
            Assertions = entry.Assertions.Select(DraftAssertion).ToList(),
        };
    }

    /// <summary>An assertion as the draft holds it: each condition written back as the document writes it.</summary>
    private static MappingDraftAssertion DraftAssertion(NodeAssertion assertion) => new()
    {
        Operator = AssertionText.Of(assertion.Condition.Operator),
        Operand = ConditionReader.Written(assertion.Condition),
        Stage = AssertionWords.Of(assertion.Stage),
        OnFail = AssertionWords.Of(assertion.OnFail),
        AnyValue = assertion.AnyValue,
        IgnoreCase = assertion.Condition.IgnoreCase,
        Tolerance = assertion.Condition.Tolerance,
        Where = assertion.Where
            .Select(filter => new MappingDraftAssertionFilter(
                filter.Field is null ? MappingMapper.ColumnSetting : MappingMapper.FieldSetting,
                filter.Field ?? ColumnText(filter.Column!),
                AssertionText.Of(filter.Condition.Operator),
                ConditionReader.Written(filter.Condition),
                filter.Condition.IgnoreCase,
                filter.Condition.Tolerance))
            .ToList(),
        Name = assertion.Named ? assertion.Label : null,
        Description = assertion.Description,
    };

    private static MappingDraftFind DraftFind(FindBy find) => new(find.Field, find.Column is null ? null : ColumnText(find.Column), find.Literal);

    private static MappingDraftModifier DraftModifier(Modifier modifier) => new(
        modifier.Kind.ToString().ToLowerInvariant(),
        modifier.Separator,
        modifier.Part,
        modifier.Replacements.Count == 0 ? null : modifier.Replacements.Select(kv => new MappingDraftReplacement(kv.Key, kv.Value)).ToList(),
        modifier.Kind switch
        {
            ModifierKind.Id => modifier.Id!.Text,
            ModifierKind.Ref => modifier.EntityType,
            _ => modifier.Text,
        },
        modifier.DecimalSeparator,
        modifier.GroupSeparator,
        modifier.Kind == ModifierKind.Replace ? FallbackKind(modifier.Otherwise) : null,
        modifier.Kind == ModifierKind.Replace && modifier.Otherwise.Kind == ReplaceFallbackKind.Text ? modifier.Otherwise.Text : null,
        modifier.Table?.CacheType,
        modifier.Table?.Match,
        modifier.Table?.Field);

    /// <summary>One property of the record tree the draft is written as: an object of properties, or an entry, which for a repeat holds the item's properties too.</summary>
    private sealed class TreeSlot
    {
        public MappingDraftEntry? Entry { get; init; }

        public List<(string Name, TreeSlot Slot)>? Properties { get; init; }
    }

    /// <summary>An entry the tree has no place for, with why: null when it only repeats a target another entry already fills.</summary>
    private sealed record LeftOutEntry(string Target, string? Reason);

    /// <summary>
    /// The draft's entries laid out as the record tree. <see cref="Scopes"/> gives, by entry index, the child dataset whose
    /// rows an entry inside a repeated array reads, and <see cref="EmptyRepeats"/> the repeats in the tree whose items hold
    /// no entry yet.
    /// </summary>
    private sealed record DraftLayout(
        List<(string Name, TreeSlot Slot)> Root, IReadOnlyList<LeftOutEntry> LeftOut, IReadOnlyDictionary<int, string?> Scopes, IReadOnlySet<int> EmptyRepeats);

    /// <summary>
    /// Places every entry of the draft at the property its target names. The entries outside repeated arrays go first, in
    /// draft order, so a repeat is in place before the entries of its items; each of those then goes under the repeat of its
    /// array. An entry that names a property inside one another entry fills whole, or that fills whole a property other
    /// entries fill inside, has no place, and neither has an entry of an array's items that nothing repeats.
    /// </summary>
    private static DraftLayout Layout(MappingDraft draft)
    {
        var root = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        var scopes = new Dictionary<int, string?>();
        var repeatSlots = new Dictionary<TreeSlot, int>();
        var parsed = draft.Entries
            .Select((entry, index) => (Entry: entry, Index: index, Path: TemplatePath.TryParse(entry.Target, out var path, out _) ? path : null))
            .ToList();

        foreach (var (entry, index, path) in parsed.Where(p => p.Path is { IsRepeated: false }))
        {
            if (Place(root, path!.Segments.Select(s => s.Name).ToList(), entry, path.Text, leftOut) is { } slot && entry.Input == MappingDraftInput.Repeat)
            {
                repeatSlots[slot] = index;
            }
        }

        foreach (var (entry, index, path) in parsed.Where(p => p.Path is { IsRepeated: true }))
        {
            var array = path!.Repeater!;
            var holder = Find(root, array.Segments.Select(s => s.Name).ToList());
            if (holder is not { Entry.Input: MappingDraftInput.Repeat } repeat)
            {
                leftOut.Add(new LeftOutEntry(path.Text, holder is { Entry.Input: MappingDraftInput.List }
                    ? $"{path.Text} fills a property of the items of {array.Text}, whose items a list writes; fill it in an item of that list"
                    : $"{path.Text} fills a property of the items of {array.Text}, and no entry repeats a child dataset's rows at {array.Text}"));
                continue;
            }

            if (Place(repeat.Properties!, path.WithinItem, entry, path.Text, leftOut) is not null)
            {
                scopes[index] = string.IsNullOrWhiteSpace(repeat.Entry!.Child) ? null : repeat.Entry.Child.Trim();
            }
        }

        var empty = repeatSlots.Where(kv => kv.Key.Properties is { Count: 0 }).Select(kv => kv.Value).ToHashSet();
        return new DraftLayout(root, leftOut, scopes, empty);
    }

    /// <summary>Places one entry at <paramref name="names"/> below <paramref name="properties"/>, or says why it has no place there.</summary>
    private static TreeSlot? Place(List<(string Name, TreeSlot Slot)> properties, IReadOnlyList<string> names, MappingDraftEntry entry, string target, List<LeftOutEntry> leftOut)
    {
        var current = properties;
        for (var i = 0; i < names.Count - 1; i++)
        {
            var existing = current.FirstOrDefault(p => p.Name == names[i]).Slot;
            if (existing?.Entry is { } whole)
            {
                leftOut.Add(new LeftOutEntry(target, $"{target} lies inside {whole.Target.Trim()}, which an entry fills whole"));
                return null;
            }

            if (existing is null)
            {
                existing = new TreeSlot { Properties = [] };
                current.Add((names[i], existing));
            }

            current = existing.Properties!;
        }

        var leaf = current.FirstOrDefault(p => p.Name == names[^1]).Slot;
        if (leaf is not null)
        {
            leftOut.Add(leaf.Entry is null
                ? new LeftOutEntry(target, $"{target} is filled whole, and other entries fill properties inside it; fill it whole or fill its properties")
                : new LeftOutEntry(target, null));
            return null;
        }

        var slot = new TreeSlot { Entry = entry, Properties = entry.Input == MappingDraftInput.Repeat ? [] : null };
        current.Add((names[^1], slot));
        return slot;
    }

    private static TreeSlot? Find(List<(string Name, TreeSlot Slot)> properties, IReadOnlyList<string> names)
    {
        TreeSlot? slot = null;
        var current = properties;
        foreach (var name in names)
        {
            slot = current?.FirstOrDefault(p => p.Name == name).Slot;
            if (slot is null)
            {
                return null;
            }

            current = slot.Properties;
        }

        return slot;
    }

    private static void WriteProperties(List<(string Name, TreeSlot Slot)> properties, int indent, string? scope, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (name, slot) in properties)
        {
            var key = pad + PropertyKey(name);
            if (slot.Entry is null)
            {
                line(key + ":");
                WriteProperties(slot.Properties!, indent + 2, scope, line);
                continue;
            }

            WriteNode(key, slot, indent, scope, line);
        }
    }

    /// <summary>A record property as a key of the tree: its name, with one more '$' when the name starts with one.</summary>
    private static string PropertyKey(string name) => Scalar(MappingMapper.KeyOfProperty(name));

    /// <summary>One entry at its property: a literal as the value it is, or a node of the mapping language with its settings.</summary>
    private static void WriteNode(string key, TreeSlot slot, int indent, string? scope, Action<string> line)
    {
        var entry = slot.Entry!;
        var inner = new string(' ', indent + 2);
        if (entry.Input == MappingDraftInput.Static)
        {
            WriteLiteral(key, entry, indent, scope, line);
            return;
        }

        if (entry.Input == MappingDraftInput.Repeat)
        {
            line(key + ":");
            line(inner + MappingMapper.ForEachKey + ": " + Scalar(entry.Child?.Trim() ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(entry.Where))
            {
                line(inner + MappingMapper.WhereKey + ": " + ExpressionScalar(entry.Where.Trim()));
            }

            WriteSettings(entry, inner, line, scope);
            if (slot.Properties is { Count: > 0 } items)
            {
                line(inner + MappingMapper.ItemKey + ":");
                WriteProperties(items, indent + 4, string.IsNullOrWhiteSpace(entry.Child) ? null : entry.Child.Trim(), line);
            }
            else
            {
                line(inner + MappingMapper.ItemKey + ": {}");
            }

            return;
        }

        if (entry.Input == MappingDraftInput.Coalesce)
        {
            // The alternatives in the order they are tried, each a node of its own; the node's settings decide for all of them.
            line(key + ":");
            line(inner + MappingMapper.CoalesceKey + ":");
            foreach (var alternative in entry.Alternatives)
            {
                WriteAlternative(alternative, indent + 4, scope, line);
            }

            WriteSettings(entry, inner, line, scope);
            return;
        }

        if (entry.Input == MappingDraftInput.Expression)
        {
            // Always the block form: an expression reads best on a line of its own, and a flow mapping would need it quoted.
            line(key + ":");
            line(inner + MappingMapper.ExprKey + ": " + ExpressionScalar(entry.Expression?.Trim() ?? string.Empty));
            WriteModifiers(entry.Modifiers, inner, line);
            WriteSettings(entry, inner, line, scope);
            return;
        }

        if (entry.Input == MappingDraftInput.List)
        {
            // The items alone, in order: a list has no settings of its own, and each item carries its own.
            if (entry.Items.Count == 0)
            {
                line(key + ": []");
                return;
            }

            line(key + ":");
            foreach (var item in entry.Items)
            {
                if (item.Input == MappingDraftInput.Group)
                {
                    WriteObjectItem(item, entry.Target.Trim(), indent + 2, scope, line);
                }
                else
                {
                    WriteItem(item, indent + 2, scope, valueNode: false, line);
                }
            }

            return;
        }

        if (entry.Input == MappingDraftInput.Lookup)
        {
            // How the record is found is the lookup's to say, so the node holds the field it reads and its own settings alone.
            var field = $"{entry.Lookup?.Trim()}.{entry.CacheField?.Trim()}";
            if (string.IsNullOrWhiteSpace(entry.When) && entry.Required && !entry.Unverified && string.IsNullOrWhiteSpace(entry.Description) && entry.Assertions.Count == 0)
            {
                line(key + ": { " + MappingMapper.LookupKey + ": " + FlowScalar(field) + " }");
                return;
            }

            line(key + ":");
            line(inner + MappingMapper.LookupKey + ": " + Scalar(field));
            WriteSettings(entry, inner, line, scope);
            return;
        }

        var (sourceKey, read) = entry.Input switch
        {
            MappingDraftInput.Dataset => (MappingMapper.FromKey, ScopedColumn(entry.Column ?? string.Empty, scope)),
            MappingDraftInput.Search => (MappingMapper.SearchKey, entry.CacheType?.Trim() ?? string.Empty),
            MappingDraftInput.Parent => (MappingMapper.ParentKey, entry.ParentPath?.Trim() ?? string.Empty),
            MappingDraftInput.Child => (MappingMapper.ChildKey, entry.ChildKind?.Trim() ?? string.Empty),
            _ => (MappingMapper.CacheKey, $"{entry.CacheType?.Trim()}.{entry.CacheField?.Trim()}"),
        };

        // A child link is never required, and the node says so by leaving $required out.
        var findAll = entry.Input == MappingDraftInput.Cache ? entry.FindAll : null;
        var bare = entry.FindBy.Count == 0 && findAll is null && entry.Modifiers.Count == 0 && string.IsNullOrWhiteSpace(entry.When)
            && (entry.Required || entry.Input == MappingDraftInput.Child)
            && !(entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators) && !entry.Unverified && string.IsNullOrWhiteSpace(entry.Description)
            && entry.Assertions.Count == 0;
        if (bare)
        {
            line(key + ": { " + sourceKey + ": " + FlowScalar(read) + " }");
            return;
        }

        line(key + ":");
        line(inner + sourceKey + ": " + Scalar(read));
        if (findAll is not null)
        {
            WriteLines(MappingMapper.FindAllKey, FindAllLines(findAll, scope), inner, line);
        }
        else if (entry.Input is MappingDraftInput.Cache or MappingDraftInput.Search)
        {
            WriteLines(MappingMapper.FindByKey, entry.FindBy.Select(f => FindByText(f, scope)).ToList(), inner, line);
        }

        WriteModifiers(entry.Modifiers, inner, line);
        WriteSettings(entry, inner, line, scope);
    }

    /// <summary>
    /// A lookup under <c>lookups</c>: the cached type its record is found in, the findBy lines, read against the dataset's
    /// own row, then the modifiers, the separator fold and the description.
    /// </summary>
    private static void WriteLookup(MappingDraftLookup lookup, Action<string> line)
    {
        const string Inner = "    ";
        line("  " + Scalar(lookup.Name?.Trim() ?? string.Empty) + ":");
        line(Inner + MappingMapper.CacheKey + ": " + Scalar(lookup.CacheType?.Trim() ?? string.Empty));
        WriteLines(MappingMapper.FindByKey, lookup.FindBy.Select(f => FindByText(f, null)).ToList(), Inner, line);
        WriteModifiers(lookup.Modifiers, Inner, line);
        if (lookup.IgnoreSeparators)
        {
            line(Inner + MappingMapper.IgnoreSeparatorsKey + ": true");
        }

        if (!string.IsNullOrWhiteSpace(lookup.Description))
        {
            line(Inner + MappingMapper.DescriptionKey + ": " + Scalar(lookup.Description.Trim()));
        }
    }

    /// <summary>A key holding lines of the mapping language (<c>$findBy</c>, <c>$findAll</c>): one line beside the key, several as a list under it.</summary>
    private static void WriteLines(string key, IReadOnlyList<string> lines, string inner, Action<string> line)
    {
        if (lines.Count == 1)
        {
            line(inner + key + ": " + Scalar(lines[0]));
        }
        else if (lines.Count > 1)
        {
            line(inner + key + ":");
            foreach (var text in lines)
            {
                line(inner + "  - " + Scalar(text));
            }
        }
    }

    /// <summary>
    /// A find all as the document writes it: the key line, comparing the field with a column in the entry's scope, a
    /// quoted text or <c>$lookup.&lt;lookup&gt;.&lt;field&gt;</c>, then one <c>&lt;field&gt; is empty</c> line per field.
    /// </summary>
    private static List<string> FindAllLines(MappingDraftFindAll findAll, string? scope)
    {
        var operand = !string.IsNullOrWhiteSpace(findAll.Literal)
            ? QuotedText(findAll.Literal)
            : !string.IsNullOrWhiteSpace(findAll.Lookup)
                ? MappingMapper.LookupReference + "." + findAll.Lookup.Trim()
                : ScopedColumn(findAll.Column ?? string.Empty, scope);
        return [$"{findAll.Field?.Trim()} = {operand}", .. findAll.Empty.Select(field => $"{field?.Trim()} is empty")];
    }

    /// <summary>
    /// One alternative of a coalesce entry, as an item of its <c>$coalesce</c> list at <paramref name="indent"/>: written as a
    /// node of its own is, with a fixed value always under <c>$value</c>, since a bare value in the list would not be a node.
    /// What only the whole node takes is never written here.
    /// </summary>
    private static void WriteAlternative(MappingDraftEntry alternative, int indent, string? scope, Action<string> line)
        => WriteItem(alternative with { When = null, Required = true, Description = null }, indent, scope, valueNode: true, line);

    /// <summary>
    /// One item of a list at <paramref name="indent"/>: a node written as it would be at a key of its own, with the item's
    /// dash in place of the key. A fixed value is the bare value, unless it carries a condition or a description, or
    /// <paramref name="valueNode"/> asks for a node, which puts it under <c>$value</c>.
    /// </summary>
    private static void WriteItem(MappingDraftEntry item, int indent, string? scope, bool valueNode, Action<string> line)
    {
        var dash = new string(' ', indent) + "- ";
        if (item.Input == MappingDraftInput.Static)
        {
            WriteLiteralItem(item, indent, valueNode, line);
            return;
        }

        var written = new List<string>();
        var key = new string(' ', indent) + "-";
        WriteNode(key, new TreeSlot { Entry = item }, indent, scope, written.Add);
        if (written[0].StartsWith(key + ": ", StringComparison.Ordinal))
        {
            // The one-line form: the node itself is the item.
            line(dash + written[0][(key.Length + 2)..]);
            return;
        }

        // The block form: the node's first key moves up beside the dash, and the rest keep their place under it.
        line(dash + written[1].TrimStart());
        foreach (var text in written.Skip(2))
        {
            line(text);
        }
    }

    /// <summary>
    /// A fixed value as an item of a list: the bare value where it can be, and otherwise <c>- $value: ...</c>, an object or a
    /// list of them under it, followed by the item's condition and description.
    /// </summary>
    private static void WriteLiteralItem(MappingDraftEntry item, int indent, bool valueNode, Action<string> line)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(item.Static) ? null : JsonNode.Parse(item.Static);
        }
        catch (JsonException)
        {
            node = JsonValue.Create(item.Static ?? string.Empty);
        }

        var settled = string.IsNullOrWhiteSpace(item.When) && string.IsNullOrWhiteSpace(item.Description) && item.Assertions.Count == 0;
        if (!valueNode && settled && node is JsonValue bareValue)
        {
            line(new string(' ', indent) + "- " + ValueText(bareValue, flow: false));
            return;
        }

        if (!valueNode && settled && node is JsonObject { Count: > 0 } bareObject)
        {
            // A fixed item of a list of objects is the object it is, laid out as the tree lays out a literal.
            var written = new List<string>();
            WriteObject(bareObject, indent + 2, escape: true, written.Add);
            WriteDashed(written, indent, line);
            return;
        }

        WriteLiteralNode(node, indent, line);
        if (!settled)
        {
            WriteSettings(item, new string(' ', indent + 2), line);
        }
    }

    /// <summary>
    /// An object item of a list of objects at <paramref name="indent"/>: its properties laid out as the record tree lays out
    /// an object's, each at the place its target names inside the items of <paramref name="list"/>, with the item's dash
    /// beside the first. A property the item has no place for is left out with a comment saying why, which the checks report.
    /// </summary>
    private static void WriteObjectItem(MappingDraftEntry item, string list, int indent, string? scope, Action<string> line)
    {
        var properties = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        foreach (var property in item.Properties)
        {
            var text = property.Target?.Trim() ?? string.Empty;
            if (TemplatePath.TryParse(text, out var path, out _) && path!.Repeater?.Text == list)
            {
                Place(properties, path.WithinItem, property, path.Text, leftOut);
            }
            else
            {
                leftOut.Add(new LeftOutEntry(text, $"{(text.Length == 0 ? "a property naming no variable" : text)} is not a variable of the items of {list}"));
            }
        }

        var pad = new string(' ', indent);
        foreach (var left in leftOut)
        {
            line($"{pad}# Left out: {Comment(left.Reason ?? $"{left.Target} has another entry in the item, which is written")}.");
        }

        var written = new List<string>();
        WriteProperties(properties, indent + 2, scope, written.Add);
        if (written.Count == 0)
        {
            // An item with no property is written as the empty object it is; the loader refuses it, and the checks name it.
            line(pad + "- {}");
            return;
        }

        WriteDashed(written, indent, line);
    }

    /// <summary>Lines laid out at <paramref name="indent"/> + 2 written as one list item at <paramref name="indent"/>: the item's dash beside the first of them.</summary>
    private static void WriteDashed(IReadOnlyList<string> lines, int indent, Action<string> line)
    {
        var pad = new string(' ', indent);
        for (var i = 0; i < lines.Count; i++)
        {
            line(i == 0 ? pad + "- " + lines[i].TrimStart() : lines[i]);
        }
    }

    /// <summary>A fixed value as a list item under <c>$value</c>: <c>- $value: ...</c>, an object or a list of them under it.</summary>
    private static void WriteLiteralNode(JsonNode? node, int indent, Action<string> line)
    {
        var dash = new string(' ', indent) + "- " + MappingMapper.ValueKey + ":";
        switch (node)
        {
            case null:
                line(dash + " \"\"");
                break;
            case JsonObject { Count: 0 }:
                line(dash + " {}");
                break;
            case JsonObject obj:
                line(dash);
                WriteObject(obj, indent + 4, escape: false, line);
                break;
            case JsonArray array when array.All(item => item is JsonValue):
                line(dash + " [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                break;
            case JsonArray array:
                line(dash);
                WriteArray(array, indent + 4, escape: false, line);
                break;
            case JsonValue value:
                line(dash + " " + ValueText(value, flow: false));
                break;
        }
    }

    private static void WriteModifiers(IReadOnlyList<MappingDraftModifier> modifiers, string inner, Action<string> line)
    {
        if (modifiers.Count == 0)
        {
            return;
        }

        line(inner + MappingMapper.ModifiersKey + ":");
        foreach (var modifier in modifiers)
        {
            var lines = ModifierLines(modifier);
            line(inner + "  - " + lines[0]);
            foreach (var setting in lines.Skip(1))
            {
                line(inner + "    " + setting);
            }
        }
    }

    /// <summary>
    /// The settings a node writes after its value: its condition, whether it may be left out, its assertions, and its
    /// description. <paramref name="scope"/> is the child dataset whose rows the node reads, which the columns of an incoming
    /// assertion's conditions are written in.
    /// </summary>
    private static void WriteSettings(MappingDraftEntry entry, string inner, Action<string> line, string? scope = null)
    {
        if (!string.IsNullOrWhiteSpace(entry.When))
        {
            line(inner + MappingMapper.WhenKey + ": " + ExpressionScalar(entry.When.Trim()));
        }

        if (entry.Input is not (MappingDraftInput.Static or MappingDraftInput.Child) && !entry.Required)
        {
            line(inner + MappingMapper.RequiredKey + ": false");
        }

        if (entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators)
        {
            line(inner + MappingMapper.IgnoreSeparatorsKey + ": true");
        }

        if (entry.Unverified)
        {
            line(inner + MappingMapper.UnverifiedKey + ": true");
        }

        if (entry.Assertions.Count > 0)
        {
            line(inner + MappingMapper.AssertKey + ":");
            foreach (var assertion in entry.Assertions)
            {
                WriteAssertion(assertion, inner + "  ", scope, line);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            line(inner + MappingMapper.DescriptionKey + ": " + Scalar(entry.Description.Trim()));
        }
    }

    /// <summary>
    /// One assertion, as an item of <c>$assert</c>: its condition first, then the settings that differ from their defaults,
    /// its conditions and its description.
    /// </summary>
    private static void WriteAssertion(MappingDraftAssertion assertion, string indent, string? scope, Action<string> line)
    {
        var more = indent + "  ";
        line(indent + "- " + OneLine(assertion.Operator) + ": " + OneLine(assertion.Operand));
        if (!string.IsNullOrWhiteSpace(assertion.Name))
        {
            line(more + MappingMapper.NameSetting + ": " + Scalar(assertion.Name.Trim()));
        }

        if (!string.Equals(assertion.Stage?.Trim(), AssertionWords.Record, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(assertion.Stage))
        {
            line(more + MappingMapper.StageSetting + ": " + assertion.Stage.Trim());
        }

        if (!string.Equals(assertion.OnFail?.Trim(), AssertionWords.Hold, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(assertion.OnFail))
        {
            line(more + MappingMapper.OnFailSetting + ": " + assertion.OnFail.Trim());
        }

        if (assertion.AnyValue)
        {
            line(more + MappingMapper.ValuesSetting + ": any");
        }

        WriteConditionSettings(assertion.IgnoreCase, assertion.Tolerance, more, line);
        if (assertion.Where.Count > 0)
        {
            line(more + MappingMapper.WhereSetting + ":");
            foreach (var filter in assertion.Where)
            {
                var reads = OneLine(filter.Reads);
                var path = reads == MappingMapper.ColumnSetting ? ScopedColumn(filter.Path ?? string.Empty, scope) : (filter.Path ?? string.Empty).Trim();
                line(more + "  - " + reads + ": " + Scalar(path));
                line(more + "    " + OneLine(filter.Operator) + ": " + OneLine(filter.Operand));
                WriteConditionSettings(filter.IgnoreCase, filter.Tolerance, more + "    ", line);
            }
        }

        if (!string.IsNullOrWhiteSpace(assertion.Description))
        {
            line(more + "description: " + Scalar(assertion.Description.Trim()));
        }
    }

    /// <summary>
    /// A word or an operand of an assertion as one line of YAML: what the draft lacks is reported as an issue, and the YAML
    /// written beside the issues keeps its shape.
    /// </summary>
    private static string OneLine(string? text) => Comment(text ?? string.Empty).Trim();

    private static void WriteConditionSettings(bool ignoreCase, double? tolerance, string indent, Action<string> line)
    {
        if (ignoreCase)
        {
            line(indent + "ignoreCase: true");
        }

        if (tolerance is { } value)
        {
            line(indent + "tolerance: " + value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// A literal: a text, number, boolean or list as the value it is, and an object, or a literal with a condition or a
    /// description, under <c>$value</c>, which holds it verbatim and keeps it one entry.
    /// </summary>
    private static void WriteLiteral(string key, MappingDraftEntry entry, int indent, string? scope, Action<string> line)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(entry.Static) ? null : JsonNode.Parse(entry.Static);
        }
        catch (JsonException)
        {
            // A value that is not JSON is written as the text it is, so the page shows what was typed and the loader reports it.
            node = JsonValue.Create(entry.Static ?? string.Empty);
        }

        var inner = new string(' ', indent + 2);
        var settled = string.IsNullOrWhiteSpace(entry.When) && string.IsNullOrWhiteSpace(entry.Description) && entry.Assertions.Count == 0;
        switch (node)
        {
            case null:
                line(key + ": \"\"");
                return;
            case JsonValue value when settled:
                line(key + ": " + ValueText(value, flow: false));
                return;
            case JsonArray array when settled && array.All(item => item is JsonValue):
                line(key + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                return;
            case JsonArray array when settled:
                line(key + ":");
                WriteArray(array, indent + 2, escape: true, line);
                return;
        }

        line(key + ":");
        switch (node)
        {
            case JsonObject { Count: 0 }:
                line(inner + MappingMapper.ValueKey + ": {}");
                break;
            case JsonObject obj:
                line(inner + MappingMapper.ValueKey + ":");
                WriteObject(obj, indent + 4, escape: false, line);
                break;
            case JsonArray array when array.All(item => item is JsonValue):
                line(inner + MappingMapper.ValueKey + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                break;
            case JsonArray array:
                line(inner + MappingMapper.ValueKey + ":");
                WriteArray(array, indent + 4, escape: false, line);
                break;
            case JsonValue value:
                line(inner + MappingMapper.ValueKey + ": " + ValueText(value, flow: false));
                break;
        }

        WriteSettings(entry, inner, line, scope);
    }

    /// <summary>
    /// An object's properties, one per line. A literal in the tree names a property that starts with '$' with one more
    /// (<paramref name="escape"/>); what <c>$value</c> holds is written verbatim.
    /// </summary>
    private static void WriteObject(JsonObject obj, int indent, bool escape, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (name, child) in obj)
        {
            var key = pad + Scalar(escape ? MappingMapper.KeyOfProperty(name) : name);
            switch (child)
            {
                case JsonObject { Count: 0 }:
                    line(key + ": {}");
                    break;
                case JsonObject nested:
                    line(key + ":");
                    WriteObject(nested, indent + 2, escape, line);
                    break;
                case JsonArray array when array.All(item => item is JsonValue):
                    line(key + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                    break;
                case JsonArray array:
                    line(key + ":");
                    WriteArray(array, indent + 2, escape, line);
                    break;
                case JsonValue value:
                    line(key + ": " + ValueText(value, flow: false));
                    break;
                default:
                    line(key + ": null");
                    break;
            }
        }
    }

    private static void WriteArray(JsonArray array, int indent, bool escape, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var item in array)
        {
            switch (item)
            {
                case JsonObject obj:
                    var inner = new List<string>();
                    WriteObject(obj, indent + 2, escape, inner.Add);
                    if (inner.Count == 0)
                    {
                        line(pad + "- {}");
                    }
                    else
                    {
                        WriteDashed(inner, indent, line);
                    }

                    break;
                case JsonArray nested when nested.All(value => value is JsonValue):
                    line(pad + "- [" + string.Join(", ", nested.Select(value => ValueText((JsonValue)value!, flow: true))) + "]");
                    break;
                case JsonArray nested:
                    line(pad + "-");
                    WriteArray(nested, indent + 2, escape, line);
                    break;
                case JsonValue value:
                    line(pad + "- " + ValueText(value, flow: false));
                    break;
                default:
                    line(pad + "- null");
                    break;
            }
        }
    }

    private static void StaticIssue(MappingDraftEntry entry, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(entry.Static) ? null : JsonNode.Parse(entry.Static);
        }
        catch (JsonException)
        {
            error($"{target}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            error($"{target}: give the fixed value.", target);
        }
        else if (EnvelopeTargets.Contains(target, StringComparer.Ordinal) && (node is not JsonArray list || list.Count == 0))
        {
            error($"{target}: every record carries it, so give at least one value.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{target}: {problem}", target);
        }
    }

    /// <summary>
    /// A column an entry names read in the entry's scope: <c>child.column</c> names a column of a child dataset's rows, which
    /// only an entry inside the items of the array repeating that child reads.
    /// </summary>
    private static void ScopeIssue(string column, string? scope, string target, Action<string, string?> error)
    {
        var dot = column.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return;
        }

        var child = column[..dot];
        if (!string.Equals(child, scope, StringComparison.Ordinal))
        {
            error(
                $"{target}: {column.Trim()} reads a column of child dataset '{child}', which only an entry inside the items of the array repeating {child} reads.",
                target);
        }
    }

    private static void ModifierIssue(MappingDraftModifier modifier, string? scope, string target, Action<string, string?> error)
    {
        switch (modifier.Kind)
        {
            case "trim" or "upper" or "lower":
                break;
            case "date" when !string.IsNullOrEmpty(modifier.Text) && Rendering.DateValues.FormatProblem(modifier.Text) is { } problem:
                error($"{target}: the date format '{modifier.Text}' {problem}.", target);
                break;
            case "date":
                break;
            case "number" when Rendering.NumberValues.SeparatorsProblem(modifier.DecimalSeparator, modifier.GroupSeparator) is { } separators:
                error($"{target}: number {separators}.", target);
                break;
            case "number":
                break;
            case "split" when string.IsNullOrEmpty(modifier.Separator) || modifier.Part is not > 0:
                error($"{target}: split needs a separator and the part to keep, counting from one.", target);
                break;
            case "split":
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Table) && modifier.Replacements is { Count: > 0 }:
                error($"{target}: a replace reads its table from the cache or lists its values, not both.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Table) && !ColumnName().IsMatch(modifier.Table.Trim()):
                error($"{target}: replace reads cache type '{modifier.Table}', and a cached type is named by letters, digits, '_' and '-', such as UnitAlias.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Match) && !FieldPath().IsMatch(modifier.Match.Trim()):
                error($"{target}: replace's match names the cached field a value is compared with, such as mnemonic, not '{modifier.Match}'.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Field) && !FieldPath().IsMatch(modifier.Field.Trim()):
                error($"{target}: replace's field names the cached field that replaces a value, such as curve_family, not '{modifier.Field}'.", target);
                break;
            case "replace" when string.IsNullOrWhiteSpace(modifier.Table) && (!string.IsNullOrWhiteSpace(modifier.Match) || !string.IsNullOrWhiteSpace(modifier.Field)):
                error($"{target}: match and field choose the fields of a table read from the cache; choose the cached type too.", target);
                break;
            case "replace" when string.IsNullOrWhiteSpace(modifier.Table)
                && (modifier.Replacements is not { Count: > 0 } || modifier.Replacements.Any(r => string.IsNullOrWhiteSpace(r.From))):
                error($"{target}: replace needs at least one incoming value and what it becomes, or a cached table to read them from.", target);
                break;
            case "replace" when (modifier.Replacements ?? []).GroupBy(r => r.From.Trim(), StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice:
                error($"{target}: replace lists '{twice.Key}' more than once; a value is matched trimmed, so each incoming value is listed once.", target);
                break;
            case "replace" when modifier.OtherwiseKind is not (null or FallbackKeep or FallbackEmpty or FallbackText):
                error($"{target}: replace's otherwise is keep, empty or text, not '{modifier.OtherwiseKind}'.", target);
                break;
            case "replace" when modifier.OtherwiseKind == FallbackText && string.IsNullOrWhiteSpace(modifier.OtherwiseText):
                error($"{target}: replace's otherwise needs the text an unlisted value becomes; choose no value for none.", target);
                break;
            case "replace":
                break;
            case "equals" when string.IsNullOrEmpty(modifier.Text):
                error($"{target}: equals needs the text to compare with.", target);
                break;
            case "equals":
                break;
            case "id" when IdTemplate.TryParse(modifier.Text, scope, out var idProblem) is null:
                error($"{target}: id: {idProblem}.", target);
                break;
            case "id":
                break;
            case "ref" when !string.IsNullOrWhiteSpace(modifier.Text) && !IdTemplate.IsEntityType(modifier.Text.Trim()) && !EntityName().IsMatch(modifier.Text.Trim()):
                error($"{target}: ref '{modifier.Text.Trim()}' is not an entity type; name it as the template does, such as UnitOfMeasure, or in full, such as reference-data--UnitOfMeasure.", target);
                break;
            case "ref":
                break;
            default:
                error($"{target}: '{modifier.Kind}' is not a modifier.", target);
                break;
        }
    }

    private const string FallbackKeep = "keep";

    private const string FallbackEmpty = "empty";

    private const string FallbackText = "text";

    private const string Marker = MappingMapper.Marker;

    private const string DatasetReference = MappingMapper.DatasetReference;

    private static string FallbackKind(ReplaceFallback fallback) => fallback.Kind switch
    {
        ReplaceFallbackKind.Keep => FallbackKeep,
        ReplaceFallbackKind.Empty => FallbackEmpty,
        _ => FallbackText,
    };

    /// <summary>
    /// A modifier as the lines of its list item: the modifier itself, then the settings written beside it. Only a replace has
    /// any: the fields of a cached table it names, and its <c>otherwise</c>, written beside the table so no incoming value is
    /// ever read as a setting.
    /// </summary>
    private static IReadOnlyList<string> ModifierLines(MappingDraftModifier modifier)
    {
        var lines = new List<string> { ModifierText(modifier) };
        if (modifier.Kind != "replace")
        {
            return lines;
        }

        if (!string.IsNullOrWhiteSpace(modifier.Table))
        {
            if (!string.IsNullOrWhiteSpace(modifier.Match))
            {
                lines.Add("match: " + modifier.Match.Trim());
            }

            if (!string.IsNullOrWhiteSpace(modifier.Field))
            {
                lines.Add("field: " + modifier.Field.Trim());
            }
        }

        switch (modifier.OtherwiseKind)
        {
            case FallbackEmpty:
                lines.Add("otherwise: ~");
                break;
            case FallbackText when !string.IsNullOrWhiteSpace(modifier.OtherwiseText):
                lines.Add("otherwise: " + Scalar(modifier.OtherwiseText));
                break;
        }

        return lines;
    }

    /// <summary>A replacement as a flow scalar: its text, or <c>~</c> for no value.</summary>
    private static string ReplacementText(string? to) => to is null ? "~" : FlowScalar(to);

    private static string ModifierText(MappingDraftModifier modifier) => modifier.Kind switch
    {
        "split" => "split: { separator: " + FlowScalar(modifier.Separator ?? string.Empty) + ", part: " + (modifier.Part ?? 0).ToString(CultureInfo.InvariantCulture) + " }",
        "replace" when !string.IsNullOrWhiteSpace(modifier.Table) => $"replace: {MappingMapper.CacheReference}.{modifier.Table.Trim()}",
        "replace" => "replace: { " + string.Join(", ", (modifier.Replacements ?? []).Select(r => FlowScalar(r.From) + ": " + ReplacementText(r.To))) + " }",
        "equals" => "equals: " + Scalar(modifier.Text ?? string.Empty),
        "date" when !string.IsNullOrEmpty(modifier.Text) => "date: " + Scalar(modifier.Text),
        "id" => "id: " + Quote(modifier.Text ?? string.Empty),
        "ref" when !string.IsNullOrWhiteSpace(modifier.Text) => "ref: " + Scalar(modifier.Text.Trim()),
        "number" when modifier.GroupSeparator is not null || modifier.DecimalSeparator is not (null or ".")
            => "number: { decimal: " + FlowScalar(modifier.DecimalSeparator ?? ".") + (modifier.GroupSeparator is null ? string.Empty : ", group: " + FlowScalar(modifier.GroupSeparator)) + " }",
        _ => modifier.Kind,
    };

    /// <summary>A findBy line as the document writes it: the field of the record read, then the column in the entry's scope or a quoted text.</summary>
    private static string FindByText(MappingDraftFind find, string? scope)
    {
        var operand = !string.IsNullOrWhiteSpace(find.Literal)
            ? QuotedText(find.Literal)
            : ScopedColumn(find.Column ?? string.Empty, scope);
        return $"{find.Field?.Trim()} = {operand}";
    }

    /// <summary>A fixed text a findBy or find all line compares with, in the quotes that do not occur in it.</summary>
    private static string QuotedText(string text) => text.Contains('\'', StringComparison.Ordinal) ? "\"" + text + "\"" : "'" + text + "'";

    /// <summary>
    /// An expression as a YAML value: plain where YAML reads it back as the same text (<c>curve_id != "MD"</c>), which is
    /// most expressions, and otherwise in single quotes, which keep the double quotes an expression writes its texts in as
    /// they stand. A plain value starts with a letter, a digit, '_', '$' or '(', and holds no ": " and no " #", which YAML
    /// would read as a key or a comment.
    /// </summary>
    internal static string ExpressionScalar(string value)
    {
        var plain = value.Length > 0
            && (char.IsAsciiLetterOrDigit(value[0]) || value[0] is '_' or '$' or '(')
            && value.Trim().Length == value.Length
            && value.AsSpan().IndexOfAny('\r', '\n', '\t') < 0
            && !value.Contains(": ", StringComparison.Ordinal)
            && !value.Contains(" #", StringComparison.Ordinal)
            && !value.EndsWith(':')
            && !ReservedWords.Contains(value)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (plain)
        {
            return value;
        }

        return value.AsSpan().IndexOfAny('\r', '\n') >= 0 ? Quote(value) : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// A draft column (<c>column</c> for the dataset's own row, <c>child.column</c> for a child dataset's) as the node that
    /// reads it in <paramref name="scope"/> writes it: inside the items of the array repeating <c>child</c>, its columns are
    /// named bare and the dataset's own row as <c>$dataset.column</c>. A child column read anywhere else is written as it
    /// stands, for the loader to report.
    /// </summary>
    private static string ScopedColumn(string column, string? scope)
    {
        var trimmed = column.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return scope is null || trimmed.Length == 0 ? trimmed : DatasetReference + "." + trimmed;
        }

        return string.Equals(trimmed[..dot], scope, StringComparison.Ordinal) ? trimmed[(dot + 1)..] : trimmed;
    }

    private static string ColumnText(DatasetColumn column) => column.Child is null ? column.Column : column.Child + "." + column.Column;

    private static string ValueText(JsonValue value, bool flow) => value.GetValueKind() switch
    {
        JsonValueKind.String => flow ? FlowScalar(value.GetValue<string>()) : Scalar(value.GetValue<string>()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.ToJsonString(),
        _ => "null",
    };

    /// <summary>Text for a YAML comment, on one line.</summary>
    private static string Comment(string text) => text.ReplaceLineEndings(" ");

    /// <summary>A text value as a YAML scalar: plain where YAML reads it back as the same text, double-quoted otherwise.</summary>
    internal static string Scalar(string value) => Plain(value, BlockPlain()) ? value : Quote(value);

    /// <summary>A text value as a YAML scalar inside a flow collection, where brackets, braces and commas are not plain.</summary>
    internal static string FlowScalar(string value) => Plain(value, FlowPlain()) ? value : Quote(value);

    private static bool Plain(string value, Regex pattern)
        => value.Length > 0
           && pattern.IsMatch(value)
           && !ReservedWords.Contains(value)
           && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
           && !value.StartsWith('.');

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^[A-Za-z0-9_$(][A-Za-z0-9_$.\/@%+()\[\]\-]*(?: [A-Za-z0-9_$.\/@%+()\[\]\-='=]+)*$")]
    private static partial Regex BlockPlain();

    [GeneratedRegex(@"^[A-Za-z0-9_$(][A-Za-z0-9_$.\/@%+()\-]*$")]
    private static partial Regex FlowPlain();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex FileNamePart();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex ColumnName();

    /// <summary>The name of an OSDU entity, as a relationship names it after its group.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex EntityName();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)?$")]
    private static partial Regex DatasetColumnPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+(\.[A-Za-z0-9_\-\$]+)*$")]
    private static partial Regex FieldPath();

    [GeneratedRegex(@"\{(?<token>[^{}]*)\}")]
    private static partial Regex LabelTokenPattern();
}
