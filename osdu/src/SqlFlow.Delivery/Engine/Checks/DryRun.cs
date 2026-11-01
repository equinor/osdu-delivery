namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>What a dry run says of one staged row, in plain terms.</summary>
public static class DryRunOutcomes
{
    /// <summary>A delivery would send the record, and nothing was found wrong with it.</summary>
    public const string Ready = "ready";

    /// <summary>A delivery would send the record, and something about it is worth a look first: a broken rule, a failed assertion, a reference nothing holds.</summary>
    public const string Attention = "attention";

    /// <summary>A delivery would not send the record: the row, the render or the check before sending holds it.</summary>
    public const string Held = "held";

    /// <summary>A delivery never renders the row: the ingestion table marks it deleted, or the source no longer holds its key.</summary>
    public const string PassedOver = "passedOver";
}

/// <summary>
/// The bounds that keep a dry run's answer the same size however many rows it reads: every count is exact, and what is listed
/// is capped, the rows with something to say first.
/// </summary>
public sealed record DryRunLimits
{
    public static DryRunLimits Default { get; } = new();

    /// <summary>The most records listed: the rows a delivery would not send first, then those worth a look, then clean rows, each in the order they were read.</summary>
    public int MaxListed { get; init; } = 5000;

    /// <summary>The most rules broken a dry run names, the most records first.</summary>
    public int MaxRules { get; init; } = 50;

    /// <summary>The most distinct rules counted; what breaks others is counted together.</summary>
    public int TrackedRules { get; init; } = 1000;

    /// <summary>The longest reason a record's line quotes.</summary>
    public int MaxReasonChars { get; init; } = 400;

    /// <summary>
    /// The most ids of the run's own records kept to answer references between them: a reference to a record this run would
    /// deliver is no reference to nothing. Past it, a reference to a record read long before reads as not found.
    /// </summary>
    public int MaxOwnIds { get; init; } = 200_000;
}

/// <summary>How many of a dry run's rows came to each outcome.</summary>
public sealed record DryRunOutcomeCounts
{
    public long Rows { get; init; }

    public long Ready { get; init; }

    public long Attention { get; init; }

    public long Held { get; init; }

    public long PassedOver { get; init; }
}

/// <summary>What the next run of the flow would do with the rows read, from the ledger as it stands.</summary>
public sealed record DryRunNextRun
{
    /// <summary>Records the ledger does not hold yet.</summary>
    public long Create { get; init; }

    /// <summary>Records OSDU holds that render differently now.</summary>
    public long Update { get; init; }

    /// <summary>Records that render what OSDU holds, or that the ledger keeps out.</summary>
    public long Unchanged { get; init; }

    /// <summary>Records it would hold, or that the ledger keeps blocked.</summary>
    public long Hold { get; init; }
}

/// <summary>
/// What a schema's own rules said of the documents a dry run held to it. A record a document refers to that nothing holds is no
/// rule broken: it is counted apart (<see cref="DryRun.WithMissingReferences"/>).
/// </summary>
public sealed record DryRunVerdictCounts
{
    public long Valid { get; init; }

    public long Invalid { get; init; }

    public long Unverified { get; init; }

    /// <summary>Documents not held to it: the record was held before it was whole, or no schema could be had.</summary>
    public long NotChecked { get; init; }
}

/// <summary>A rule the documents of a dry run break, how many records break it, and one example.</summary>
/// <param name="At">The property path every record shares.</param>
/// <param name="Rule">The rule: the JSON Schema keyword broken, or <c>relationship</c>, or <c>reference</c>.</param>
/// <param name="Records">How many records break it.</param>
/// <param name="ExampleKey">The source key of the example.</param>
/// <param name="ExampleMessage">What is wrong in the example.</param>
/// <param name="ExampleValue">The value behind it.</param>
public sealed record DryRunRule(string At, string Rule, long Records, string ExampleKey, string ExampleMessage, string ExampleValue);

/// <summary>What the legal service says of the legal tags the mapping puts on every record.</summary>
public static class DryRunLegalStates
{
    public const string Accepted = "accepted";
    public const string Refused = "refused";

    /// <summary>The target does not ask the legal service: <c>validateLegalTags</c> is off, or the route has no path to it.</summary>
    public const string NotAsked = "notAsked";

    /// <summary>The legal service could not be asked, or refused to answer.</summary>
    public const string NotChecked = "notChecked";

    /// <summary>The mapping names no legal tags: its rows are no OSDU records.</summary>
    public const string NotApplicable = "notApplicable";
}

/// <summary>The legal tags a dry run asked about, and what the legal service said.</summary>
/// <param name="State">What it came to (<see cref="DryRunLegalStates"/>).</param>
/// <param name="Tags">The tags the mapping puts on every record.</param>
/// <param name="Why">What the service said of the tags it refuses, or why it could not be asked.</param>
public sealed record DryRunLegalTags(string State, IReadOnlyList<string> Tags, string? Why = null);

/// <summary>One staged row as a dry run found it: enough to list it and say what became of it, its document read on demand.</summary>
public sealed record DryRunRecord
{
    public required string SourceKey { get; init; }

    /// <summary>The key's parts as a JSON array of strings, which names the row to a preview; null for a row whose key is incomplete.</summary>
    public string? KeyJson { get; init; }

    public Guid? DeliveryKey { get; init; }

    public string? Label { get; init; }

    public string? File { get; init; }

    public long? Row { get; init; }

    /// <summary>What it comes to (<see cref="DryRunOutcomes"/>).</summary>
    public required string Outcome { get; init; }

    /// <summary>The first thing to know about it, in the words of what found it; null for a row with nothing to say.</summary>
    public string? Reason { get; init; }

    /// <summary>What the next run would do with it: <c>create</c>, <c>updateMetadata</c>, <c>updatePayload</c>, <c>updateBoth</c>, <c>skip</c>, <c>hold</c> or <c>blocked</c>.</summary>
    public required string NextRun { get; init; }

    public string? TargetId { get; init; }

    /// <summary>What the template the mapping pins says of the document, as an outcome's name.</summary>
    public string? Template { get; init; }

    /// <summary>What the Schema service's schema says of the document, as an outcome's name; null when no schema could be had.</summary>
    public string? Schema { get; init; }

    /// <summary>How many problems the Schema service's schema finds, the references nothing holds among them.</summary>
    public long SchemaProblems { get; init; }

    /// <summary>How many problems the template finds.</summary>
    public long TemplateProblems { get; init; }

    /// <summary>How many judgements of the mapping's assertions failed.</summary>
    public long AssertionFailures { get; init; }

    /// <summary>How many records it refers to were not found.</summary>
    public long MissingReferences { get; init; }

    /// <summary>Whether OSDU already holds a record at the id made from its key that no ledger claims.</summary>
    public bool Occupied { get; init; }

    /// <summary>How many records its enrichments add.</summary>
    public int AddedRecords { get; init; }

    /// <summary>How many problems the schemas of those records find.</summary>
    public long AddedProblems { get; init; }
}

/// <summary>
/// A dry run of a mapping over a flow's staged rows (osdu/docs/reference/concepts/dry-run.md): every step of a delivery short
/// of sending, what each row came to, and every record's document held to both the template the mapping pins and the
/// partition's Schema service. It travels with the value check of the same rows (<see cref="ValueCheck.DryRun"/>), which says
/// the same attribute by attribute. Nothing is written: not OSDU, not the ledger, not the work location.
/// </summary>
public sealed record DryRun
{
    public required DryRunOutcomeCounts Outcomes { get; init; }

    public required DryRunNextRun NextRun { get; init; }

    /// <summary>What the template the mapping pins says of the documents.</summary>
    public required DryRunVerdictCounts Template { get; init; }

    /// <summary>What the Schema service's schema says of them.</summary>
    public required DryRunVerdictCounts Schema { get; init; }

    /// <summary>The partition's Schema service against the template the mapping pins.</summary>
    public required SchemaCrossMatch SchemaMatch { get; init; }

    /// <summary>The same for each kind of record the mapping's enrichments added.</summary>
    public IReadOnlyList<SchemaCrossMatch> AddedSchemas { get; init; } = [];

    public required DryRunLegalTags LegalTags { get; init; }

    /// <summary>How many rows' documents refer to a record nothing holds.</summary>
    public long WithMissingReferences { get; init; }

    /// <summary>How many rows' ids OSDU already holds a record at, which no ledger claims.</summary>
    public long Occupied { get; init; }

    /// <summary>The rules broken most often, against the Schema service's schema where it could be had, else the template.</summary>
    public IReadOnlyList<DryRunRule> Rules { get; init; } = [];

    /// <summary>What the rules not named came to: records past <see cref="DryRunLimits.MaxRules"/> or <see cref="DryRunLimits.TrackedRules"/>.</summary>
    public long RulesUnlisted { get; init; }

    /// <summary>The rows, in the order they were read, those with something to say first when there are more than a dry run lists.</summary>
    public IReadOnlyList<DryRunRecord> Records { get; init; } = [];

    /// <summary>Whether more rows were read than are listed: <see cref="Outcomes"/> counts them all.</summary>
    public bool Cut { get; init; }

    /// <summary>What a reader should know of how the checks went: a lookup that failed, a list cut short.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
