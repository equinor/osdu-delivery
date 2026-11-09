using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>What a value check is asked for: which variables, over how many rows, and how many example records it names.</summary>
public sealed record ValueCheckRequest
{
    /// <summary>
    /// The variables to check, as template paths (<c>osdu.data.WellboreID</c>). A variable checks every entry that fills
    /// it, something inside it, or a value holding it. Empty checks every entry of the mapping.
    /// </summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>The most rows read from the flow's scope, in the order a run reads them; 0 reads the whole scope.</summary>
    public long MaxRows { get; init; } = ValueCheckLimits.DefaultRows;

    /// <summary>How many example records each finding names.</summary>
    public int Samples { get; init; } = ValueCheckLimits.DefaultSamples;

    /// <summary>
    /// How many records of each finding are passed over before the examples are taken: the next page of a finding whose
    /// first examples were already shown.
    /// </summary>
    public long SkipSamples { get; init; }

    /// <summary>The mapping the check is asked of (<c>Name@version</c>), or null to take the flow's; the flow must render with it.</summary>
    public string? Mapping { get; init; }

    /// <summary>
    /// Whether to take the same rows through everything a delivery does short of sending them (<see cref="Checks.DryRun"/>), which
    /// reads every attribute of the mapping, so it takes no <see cref="Targets"/>.
    /// </summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// The bounds that keep a check's answer the same size however many rows it reads and however many of them fail: every
/// count is exact, and what is listed (findings, example records, values) is capped.
/// </summary>
public sealed record ValueCheckLimits
{
    /// <summary>The rows a check reads unless it is asked for another number.</summary>
    public const long DefaultRows = 10_000;

    /// <summary>The example records a finding names unless it is asked for another number.</summary>
    public const int DefaultSamples = 20;

    public static ValueCheckLimits Default { get; } = new();

    /// <summary>The most example records a finding names.</summary>
    public int MaxSamples { get; init; } = 500;

    /// <summary>The most findings listed for one variable; what the rest found is counted together.</summary>
    public int MaxFindings { get; init; } = 50;

    /// <summary>The most values a finding lists, the most frequent first.</summary>
    public int MaxFindingValues { get; init; } = 25;

    /// <summary>The most distinct values a finding counts; values beyond them are counted together.</summary>
    public int TrackedFindingValues { get; init; } = 500;

    /// <summary>The most values a variable lists of the ones it was written with, the most frequent first.</summary>
    public int MaxValues { get; init; } = 10;

    /// <summary>The most distinct values a variable counts; values beyond them are counted together.</summary>
    public int TrackedValues { get; init; } = 1000;

    /// <summary>The longest value an example or a listed value quotes; a longer one is cut and says so.</summary>
    public int MaxValueChars { get; init; } = 300;

    /// <summary>The most example records named for each reason rows are passed over.</summary>
    public int MaxPassedOverSamples { get; init; } = 5;

    /// <summary>Rows inspected together: one unit of parallelism, and one round of searches asked of the platform.</summary>
    public int Batch { get; init; } = 200;

    /// <summary>What a dry run of the same rows lists.</summary>
    public DryRunLimits DryRun { get; init; } = DryRunLimits.Default;
}

/// <summary>
/// The values a flow's mapping gives its rows, variable by variable (<see cref="ValueChecker"/>): how many rows each
/// variable is written for with a value the template accepts, how many leave it out, how many break a rule of the
/// template, and how many hold the record, with every reason grouped, the values that caused it and example records. A
/// check reads the ingestion tables, the cache and the platform's search as a run does, and writes nothing.
/// </summary>
public sealed record ValueCheck
{
    public required string Flow { get; init; }

    public string? Interface { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>What the values were rendered with.</summary>
    public required ValueCheckInputs Inputs { get; init; }

    /// <summary>What was asked for.</summary>
    public required ValueCheckAsked Asked { get; init; }

    /// <summary>The rows read, and what became of them as records.</summary>
    public required ValueCheckRows Rows { get; init; }

    /// <summary>Each variable checked, in the order the mapping writes them.</summary>
    public required IReadOnlyList<ValueCheckVariable> Variables { get; init; }

    /// <summary>The preflight's warnings about the mapping, which do not stop a run.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>What the answer left out to stay within bounds, and why.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>What a dry run of the same rows came to, record by record; null when none was asked for.</summary>
    public DryRun? DryRun { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime CheckedUtc { get; init; }
}

/// <summary>The render inputs every value of a check comes from.</summary>
public sealed record ValueCheckInputs(string Mapping, string Kind, string TemplateVersion, string? CachePartition, string? CacheVersion);

/// <summary>What a check was asked for, as it read it.</summary>
public sealed record ValueCheckAsked
{
    /// <summary>The variables named; empty for every entry of the mapping.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>The most rows read; 0 for the whole scope.</summary>
    public long MaxRows { get; init; }

    public int Samples { get; init; }

    public long SkipSamples { get; init; }

    /// <summary>The flow parameter values the scope was read with, a parameter given several as their array.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))]
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>The rows a check read, and what became of them.</summary>
public sealed record ValueCheckRows
{
    /// <summary>How many records the scope holds, as the source estimated them when the read opened.</summary>
    public long? ScopeRecords { get; init; }

    /// <summary>Rows read from the scope.</summary>
    public long Read { get; init; }

    /// <summary>Rows inspected: the rows read less the ones passed over.</summary>
    public long Checked { get; init; }

    /// <summary>True when every row of the scope was read; false when the check stopped at the rows it was asked for.</summary>
    public bool Complete { get; init; }

    /// <summary>Rows a delivery never renders (a row the ingestion table marks deleted, a row the source cannot give whole).</summary>
    public long PassedOver { get; init; }

    public IReadOnlyList<ValueCheckPassedOver> PassedOverWhy { get; init; } = [];

    /// <summary>Rows whose key has an empty part: their record cannot be tracked, and a run holds it.</summary>
    public long Keyless { get; init; }

    public IReadOnlyList<ValueCheckSample> KeylessSamples { get; init; } = [];

    /// <summary>Rows every variable checked is written for with a value the template accepts, or means no value for.</summary>
    public long Clean { get; init; }

    /// <summary>Rows a variable checked holds: their record would not be delivered.</summary>
    public long WithHeld { get; init; }

    /// <summary>Rows a variable checked is written for with a value that breaks a rule of the template.</summary>
    public long WithInvalid { get; init; }

    /// <summary>Rows a variable checked is left out of, the record going without it.</summary>
    public long WithEmpty { get; init; }

    /// <summary>
    /// Rows whose record fails an assertion of the mapping on a variable checked (osdu/docs/reference/flow/mapping-assertions.md),
    /// whatever the failure does.
    /// </summary>
    public long WithFailedAssertion { get; init; }

    /// <summary>Rows whose record a failed assertion would hold at the check before sending (<c>onFail: hold</c>).</summary>
    public long HeldByAssertion { get; init; }
}

/// <summary>Why rows were passed over, how many, and the first of them.</summary>
public sealed record ValueCheckPassedOver(string Reason, long Count, IReadOnlyList<ValueCheckSample> Samples);

/// <summary>
/// How a variable's outcomes add up: for each row (or each item), whether it was written with a value the template accepts,
/// with one breaking a rule of it, left out, not meant for the row, or held.
/// </summary>
public sealed record ValueCheckCounts
{
    public long Valid { get; init; }

    public long Invalid { get; init; }

    public long Empty { get; init; }

    public long NotApplicable { get; init; }

    public long Held { get; init; }

    /// <summary>
    /// The rows (or items) a value of the variable failed an assertion of the mapping in, counted apart from the others: a value
    /// that fails an assertion is still a value the template accepts, or not.
    /// </summary>
    public long Asserted { get; init; }

    public long Total => Valid + Invalid + Empty + NotApplicable + Held;
}

/// <summary>One variable checked: how its rows came out, the values it was written with, and what went wrong where.</summary>
public sealed record ValueCheckVariable
{
    /// <summary>The variable, as a template path.</summary>
    public required string Target { get; init; }

    /// <summary>Where the mapping writes the entry filling it; null for a variable of <c>data</c> the schema requires that no entry fills directly.</summary>
    public string? Entry { get; init; }

    /// <summary>
    /// The source columns the entry filling it reads, as the mapping names them (<c>dataset.curves.curve_unit</c>): where a
    /// wrong value comes from. Empty for a variable no entry fills directly, and for one that reads no column.
    /// </summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>What an empty value does: true holds the record, false leaves the variable out.</summary>
    public bool Required { get; init; }

    /// <summary>For an entry of a repeated item: the array its items are written into.</summary>
    public string? Repeater { get; init; }

    /// <summary>Each row counted once, by the worst of what the variable came to in it: held, then invalid, then empty, then valid.</summary>
    public required ValueCheckCounts Rows { get; init; }

    /// <summary>For an entry of a repeated item: each item counted once.</summary>
    public ValueCheckCounts? Items { get; init; }

    /// <summary>The values it was written with that the template accepts, the most frequent first.</summary>
    public IReadOnlyList<ValueCheckValue> Values { get; init; } = [];

    /// <summary>How many distinct values it was written with, up to the number a check counts.</summary>
    public long DistinctValues { get; init; }

    /// <summary>True when it was written with more distinct values than a check counts.</summary>
    public bool MoreValues { get; init; }

    /// <summary>What went wrong, and what it means no value for, grouped by reason: the most frequent first.</summary>
    public IReadOnlyList<ValueCheckFinding> Findings { get; init; } = [];

    /// <summary>Occurrences in findings beyond the ones listed.</summary>
    public long Unlisted { get; init; }
}

/// <summary>
/// One reason a variable is not written with a value the template accepts, and every row (or item) it holds for: how many,
/// the values that caused it, and example records.
/// </summary>
public sealed record ValueCheckFinding
{
    /// <summary>
    /// What it amounts to: <c>held</c>, <c>invalid</c>, <c>empty</c>, <c>notApplicable</c>, or <c>asserted</c> for a value
    /// that fails an assertion of the mapping.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>The variable the reason is about: the one checked, or a property inside the value written there.</summary>
    public required string At { get; init; }

    /// <summary>For an invalid value, the rule of the template it breaks; for an asserted one, the assertion it fails.</summary>
    public string? Rule { get; init; }

    /// <summary>For an asserted value, what a failure of the assertion does: <c>hold</c>, <c>report</c> or <c>omit</c>.</summary>
    public string? OnFail { get; init; }

    /// <summary>The reason, as the first occurrence states it.</summary>
    public required string Message { get; init; }

    /// <summary>Occurrences: once per row, or once per item of a repeated array.</summary>
    public required long Count { get; init; }

    /// <summary>Rows it occurs in.</summary>
    public required long Rows { get; init; }

    /// <summary>The values that caused it, the most frequent first.</summary>
    public IReadOnlyList<ValueCheckValue> Values { get; init; } = [];

    /// <summary>Occurrences whose value is not among <see cref="Values"/>.</summary>
    public long OtherValues { get; init; }

    /// <summary>Example records, in the order the scope is read, after the ones <see cref="SamplesFrom"/> passed over.</summary>
    public IReadOnlyList<ValueCheckSample> Samples { get; init; } = [];

    /// <summary>How many occurrences were passed over before the examples were taken.</summary>
    public long SamplesFrom { get; init; }

    /// <summary>
    /// What it comes from, as the render met it: what was looked for and where, or the rule broken. Usually one; a
    /// <c>$coalesce</c> node none of whose alternatives gave a value has one for each alternative that looked for something.
    /// </summary>
    public IReadOnlyList<ValueCheckCause> Causes { get; init; } = [];

    /// <summary>What it means, in plain words, and what to do about it.</summary>
    public ValueCheckExplanation? Explanation { get; init; }

    /// <summary>
    /// What the source columns behind the first cause hold in the rows it occurs in, as the source holds them, before the
    /// mapping's modifiers: the values a person looks for in the source. The most frequent first.
    /// </summary>
    public IReadOnlyList<ValueCheckSourceValue> SourceValues { get; init; } = [];

    /// <summary>Occurrences whose source value is not among <see cref="SourceValues"/>.</summary>
    public long OtherSourceValues { get; init; }
}

/// <summary>
/// Why a finding's rows are not written with a value the template accepts, in parts a person (and a query) can act on: what the
/// render looked for and did not find, the cached types and fields it compared, the source columns the value came from, or the
/// rule a value breaks (osdu/docs/reference/concepts/value-proof.md).
/// </summary>
public sealed record ValueCheckCause
{
    /// <summary>The source value the attribute reads is empty.</summary>
    public const string Empty = "empty";

    /// <summary>An id the mapping wrote or built names no record of its entity type the cache holds.</summary>
    public const string IdNotHeld = "idNotHeld";

    /// <summary>A cache lookup found no record of the type holding the value at the fields it compared.</summary>
    public const string NoMatch = "noMatch";

    /// <summary>Several records of the type answer to the value once case is ignored, so none is taken.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>The cache version holds no type of the name the mapping reads.</summary>
    public const string NoType = "noType";

    /// <summary>The cached record found holds nothing at the field the mapping reads.</summary>
    public const string FieldEmpty = "fieldEmpty";

    /// <summary>A search of the platform found no record holding the value.</summary>
    public const string NotFound = "notFound";

    /// <summary>A value written breaks a rule of the template.</summary>
    public const string RuleBroken = "rule";

    /// <summary>A value fails an assertion of the mapping.</summary>
    public const string Assertion = "assertion";

    /// <summary>Anything else the render holds or leaves a value out for: <see cref="ValueCheckFinding.Message"/> says what.</summary>
    public const string Other = "other";

    /// <summary>One of the kinds above.</summary>
    public required string Kind { get; init; }

    /// <summary>The source columns the value came from, as the mapping names them (<c>dataset.curves.curve_unit</c>).</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>
    /// The cached types looked in, by the short name the cache keeps them under (<c>UnitOfMeasure</c>); for a search, the search the
    /// mapping declares.
    /// </summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>For an id, the entity type it names a record of; for a cache lookup, the type's; for a search, the kind searched.</summary>
    public string? EntityType { get; init; }

    /// <summary>The fields the value was compared with, in the order they were tried.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Whether punctuation and spacing were also folded away when comparing.</summary>
    public bool IgnoreSeparators { get; init; }

    /// <summary>
    /// Whether values were compared together, on a findBy line written with <c>and</c>: a record had to hold every one of them,
    /// so the finding is about the values together, never about one of them alone.
    /// </summary>
    public bool Together { get; init; }

    /// <summary>For several records answering to a value, or a record found that holds nothing at the field read, their ids.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];

    /// <summary>For a rule, the rule of the template; for an assertion, the assertion.</summary>
    public string? Rule { get; init; }
}

/// <summary>What a finding means and what to do about it, said the same on the page, in the workbook and on the command line.</summary>
/// <param name="Headline">What is wrong, in one sentence: what was looked for and not found, or the rule broken. Code in backticks.</param>
/// <param name="Effect">What it does to the records: held, sent without the attribute, or sent with a value the template refuses.</param>
/// <param name="Detail">Where it was looked for and what the source holds, when that adds something; null otherwise.</param>
/// <param name="Fixes">What can be done about it, the likeliest first, each a sentence naming where.</param>
public sealed record ValueCheckExplanation(string Headline, string Effect, string? Detail, IReadOnlyList<string> Fixes);

/// <summary>A value a source column holds in rows a finding occurs in, as the source holds it, and how many occurrences.</summary>
/// <param name="Column">The column, as the mapping names it.</param>
/// <param name="Value">What it holds.</param>
/// <param name="Count">Occurrences.</param>
public sealed record ValueCheckSourceValue(string Column, string Value, long Count);

/// <summary>One record a check names: the row it was read from, and what the variable came to there.</summary>
public sealed record ValueCheckSample
{
    public required string SourceKey { get; init; }

    /// <summary>The record's key as the source read it, a JSON array of its parts: what names the row to a query of the source.</summary>
    public string? KeyJson { get; init; }

    public string? Label { get; init; }

    /// <summary>The delivery key the mapping derives, or null when a key part is empty.</summary>
    public Guid? DeliveryKey { get; init; }

    /// <summary>The file the row was landed from: for an item of a repeated array, the file of its child row.</summary>
    public string? File { get; init; }

    /// <summary>The row's number in that file.</summary>
    public long? Row { get; init; }

    /// <summary>For an item of a repeated array: the child row it was written from, counting from 1.</summary>
    public int? Item { get; init; }

    /// <summary>
    /// The source columns to fix, as the mapping names them (<c>dataset.curves.curve_unit</c>): the ones the reason names, else
    /// the ones holding the value behind it, else the column the variable reads when it reads one, else the ones it reads
    /// that are empty in this row. Null when none of these says which.
    /// </summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>The value written, or the value that caused the reason, clipped.</summary>
    public string? Value { get; init; }

    /// <summary>The reason as this record states it.</summary>
    public string? Message { get; init; }
}

/// <summary>A value and how many times it was met.</summary>
public sealed record ValueCheckValue(string Value, long Count);

/// <summary>
/// One occurrence of a finding, as the check meets it: every row (or item) whose variable is not written with a value the
/// template accepts. A caller that lists every such row, however many there are, takes them one at a time from here.
/// </summary>
public sealed record ValueCheckOccurrence
{
    public required string Target { get; init; }

    public required string At { get; init; }

    public required string Outcome { get; init; }

    public string? Rule { get; init; }

    public required string Message { get; init; }

    public string? Value { get; init; }

    public required ValueCheckSample Record { get; init; }

    /// <summary>The row it was found in, which holds the source data behind it.</summary>
    public SourceRecord? Source { get; init; }

    /// <summary>For a repeated item: the child dataset whose rows <see cref="ValueCheckSample.Item"/> counts.</summary>
    public string? Child { get; init; }

    /// <summary>What it comes from (<see cref="ValueCheckFinding.Causes"/>).</summary>
    public IReadOnlyList<ValueCheckCause> Causes { get; init; } = [];
}
