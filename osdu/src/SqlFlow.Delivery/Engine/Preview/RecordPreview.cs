using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SqlFlow.Delivery.Engine.Preview;

/// <summary>
/// One record of a flow rendered as a delivery would render it, and nothing sent: which row it is, what the next run would
/// do with it, the document the mapping renders, the document the route sends with the values only the platform can give
/// shown as placeholders, what the record refers to, the files it would upload and the requests the route would make.
/// Nothing of it is written anywhere: the ledger, the work location and OSDU are left as they were.
/// </summary>
public sealed record RecordPreview
{
    /// <summary>The flow (and interface) the record belongs to, as a run names it.</summary>
    public required string Flow { get; init; }

    public string? Interface { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>What was asked for, and how the record was picked.</summary>
    public required PreviewAsked Asked { get; init; }

    /// <summary>True when a row was found to preview; false with <see cref="Reason"/> when none was.</summary>
    public required bool Found { get; init; }

    /// <summary>Why no row was previewed: an unknown key, an empty scope, a key that could not be read.</summary>
    public string? Reason { get; init; }

    /// <summary>The render inputs every document of this preview comes from.</summary>
    public required PreviewInputs Inputs { get; init; }

    /// <summary>The route the flow delivers by.</summary>
    public required PreviewRoute Route { get; init; }

    /// <summary>The record's rows as the ingestion tables hold them now.</summary>
    public PreviewSource? Source { get; init; }

    /// <summary>What the next run would do with the record, against what the ledger holds for it.</summary>
    public PreviewDecision? Decision { get; init; }

    /// <summary>The rendered document, or null when the row does not render (see <see cref="NoDocument"/>).</summary>
    public PreviewDocument? Document { get; init; }

    /// <summary>Why the row renders no document: a deleted row, a payload with no files, a value the source cannot give.</summary>
    public string? NoDocument { get; init; }

    /// <summary>The OSDU ids the document refers to, each with the record of the ledger that holds it, when one does.</summary>
    public IReadOnlyList<PreviewReference> References { get; init; } = [];

    /// <summary>How many references the document makes; more than <see cref="References"/> lists when it caps them.</summary>
    public int ReferenceCount { get; init; }

    /// <summary>The payload files a delivery of the record uploads, part by part.</summary>
    public IReadOnlyList<PreviewPayloadPart> Payload { get; init; } = [];

    /// <summary>The requests a delivery of the record makes, in order.</summary>
    public IReadOnlyList<PreviewStep> Steps { get; init; } = [];

    /// <summary>What the route does beyond its requests that a reader of the document should know.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The preflight's warnings about the mapping against its template and cache, which do not stop a run.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    public required DateTime PreviewedUtc { get; init; }
}

/// <summary>
/// The JSON a preview is written as, by a node answering the GUI and by the CLI alike: camelCase names, nulls left out,
/// and text kept as it is (a placeholder's angle brackets, a well name in letters beyond ASCII) rather than escaped, since the answer is
/// read as JSON and never embedded in a page.
/// </summary>
public static class RecordPreviewJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };

    public static string Write(RecordPreview preview, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return JsonSerializer.Serialize(preview, indented ? Indented : Options);
    }
}

/// <summary>
/// What a preview was asked for and how it picked its row: the key as typed and how it was read (<see cref="PreviewKeyForms"/>),
/// the key parts it looked up, and for a preview of the scope's first record, how many rows it passed over and why.
/// </summary>
public sealed record PreviewAsked
{
    public string? Key { get; init; }

    /// <summary>How the key was read, one of <see cref="PreviewKeyForms"/>.</summary>
    public required string How { get; init; }

    public IReadOnlyList<string>? KeyParts { get; init; }

    /// <summary>The flow's key columns, in the order a key's parts are given.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Rows before the first renderable one that a preview of the first record passed over.</summary>
    public int PassedOver { get; init; }

    /// <summary>Why the first of those rows were passed over.</summary>
    public IReadOnlyList<string> PassedOverWhy { get; init; } = [];

    /// <summary>How many records the scope holds, as the source counted them when the read opened.</summary>
    public long? ScopeRecords { get; init; }

    /// <summary>The flow parameter values the scope was read with, a parameter given several as their array.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))]
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>The ways a preview reads the key it is given.</summary>
public static class PreviewKeyForms
{
    /// <summary>No key: the first record of the scope, in key order.</summary>
    public const string First = "first";

    /// <summary>A record the ledger holds, named by its delivery key.</summary>
    public const string DeliveryKey = "delivery key";

    /// <summary>A record the ledger holds, named by the OSDU id it is delivered to.</summary>
    public const string OsduId = "osdu id";

    /// <summary>A source key as the ledger and the Records page show it, or the key's parts.</summary>
    public const string SourceKey = "source key";

    /// <summary>A JSON array of the key's parts, in the flow's key order.</summary>
    public const string KeyParts = "key parts";
}

/// <summary>The render inputs: the mapping, the template it pins, the cache version it reads and the render context's hash.</summary>
public sealed record PreviewInputs(
    string Mapping, string Kind, string TemplateVersion, string? CachePartition, string? CacheVersion, string ContextHash);

/// <summary>The route the flow delivers by, why, and for a route that reaches a DDMS, where the record goes.</summary>
public sealed record PreviewRoute(string Protocol, string? Reason, string? Ddms);

/// <summary>The record's rows: the record row and each child dataset's, bounded, with where the row came from.</summary>
public sealed record PreviewSource
{
    public required string SourceKey { get; init; }

    public required IReadOnlyList<string?> KeyParts { get; init; }

    /// <summary>The delivery key the mapping derives, or null when a key part is empty.</summary>
    public Guid? DeliveryKey { get; init; }

    public string? Label { get; init; }

    public IReadOnlyList<string> Identities { get; init; } = [];

    public string? OriginFile { get; init; }

    public long? OriginRow { get; init; }

    public DateTime? OriginUpdatedUtc { get; init; }

    public string? Fingerprint { get; init; }

    public DateTime? DeletedUtc { get; init; }

    /// <summary>Why the source cannot deliver the record as it read it (a child dataset over its ceiling), or null.</summary>
    public string? Hold { get; init; }

    public required IReadOnlyDictionary<string, string?> Row { get; init; }

    public required IReadOnlyDictionary<string, SourceRowsView> Datasets { get; init; }

    /// <summary>Why rows were left out of the preview to keep its answer within bounds, or null when none were.</summary>
    public string? Omitted { get; init; }
}

/// <summary>What the next run would do with the record, and what the ledger holds for it now.</summary>
public sealed record PreviewDecision
{
    /// <summary>The action, one of <c>create</c>, <c>updateMetadata</c>, <c>updatePayload</c>, <c>updateBoth</c>, <c>skip</c>, <c>hold</c>, <c>blocked</c>.</summary>
    public required string Action { get; init; }

    /// <summary>For a skip, which gate skipped it: <c>fingerprint</c>, <c>contentHash</c>, <c>approval</c> or <c>stale</c>.</summary>
    public string? SkipTier { get; init; }

    public required string Reason { get; init; }

    public bool DeliverMetadata { get; init; }

    public bool DeliverPayload { get; init; }

    /// <summary>The record as the ledger holds it, or null when the ledger holds no record for the row.</summary>
    public PreviewLedgerRecord? Ledger { get; init; }
}

/// <summary>The ledger's record of the row: where it stands and what it was last delivered as.</summary>
public sealed record PreviewLedgerRecord
{
    public required string Status { get; init; }

    public bool Blocked { get; init; }

    public string? TargetId { get; init; }

    public long? TargetVersion { get; init; }

    public DateTime? LastDeliveredUtc { get; init; }

    public string? LastError { get; init; }

    /// <summary>The hash of the document the ledger last established for the record.</summary>
    public string? MetadataHash { get; init; }

    /// <summary>Whether the document rendered now is the one the ledger holds; null when it holds none or nothing rendered.</summary>
    public bool? SameDocument { get; init; }
}

/// <summary>
/// The document of the record: as the mapping renders it (what the ledger hashes and a storage write sends), and as the
/// route sends it where the route adds to it, with a placeholder for each value only the platform can give.
/// </summary>
public sealed record PreviewDocument
{
    public string? TargetId { get; init; }

    public required string Kind { get; init; }

    public required string MetadataHash { get; init; }

    /// <summary>The size of the rendered document as canonical JSON, in characters.</summary>
    public required int Characters { get; init; }

    /// <summary>True when the record would be held rather than sent; <see cref="Holds"/> says why.</summary>
    public bool Held { get; init; }

    public IReadOnlyList<string> Holds { get; init; } = [];

    /// <summary>The document as the mapping renders it, or null when it is too large to return (see <see cref="Omitted"/>).</summary>
    public JsonObject? Rendered { get; init; }

    /// <summary>The document as the route sends it, or null when the route sends the rendered document as it is.</summary>
    public JsonObject? Sent { get; init; }

    /// <summary>The values of <see cref="Sent"/> that the platform gives when the record is sent.</summary>
    public IReadOnlyList<PreviewPlaceholder> Placeholders { get; init; } = [];

    /// <summary>Why the documents were left out of the preview: their size, with where to read them whole.</summary>
    public string? Omitted { get; init; }

    /// <summary>What the render found by searching the platform.</summary>
    public IReadOnlyList<PreviewSearch> Searches { get; init; } = [];

    /// <summary>How many values the render read from the cache.</summary>
    public int CacheValues { get; init; }

    /// <summary>Which alternative each <c>$coalesce</c> node of the mapping took the value it wrote from.</summary>
    public IReadOnlyList<PreviewChoice> Choices { get; init; } = [];

    /// <summary>
    /// The references the document carries that name no record the cache holds, written because the mapping says
    /// <c>$unverified</c> where they are built.
    /// </summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    /// <summary>
    /// What the mapping's assertions found of the document (osdu/docs/reference/flow/mapping-assertions.md), or null when it
    /// states none. A failure whose action is hold would hold the record at the check before sending, with the document
    /// kept; what a failure that leaves its value out left out is already out of <see cref="Rendered"/>.
    /// </summary>
    public Validation.AssertionFindings? Assertions { get; init; }

    /// <summary>
    /// What each enrichment of the flow's mapping added to the document, in the order they render
    /// (osdu/docs/reference/flow/enrich.md); empty for a mapping that declares none.
    /// </summary>
    public IReadOnlyList<PreviewEnrichStep> Enrichment { get; init; } = [];
}

/// <summary>
/// What one enrichment of the flow's mapping added to the previewed record: the enrichment mapping adding paths it rendered,
/// how many rows of its dataset the record had, each path it set or added items to, the records it adds, and what it holds the
/// record for.
/// </summary>
public sealed record PreviewEnrichStep
{
    public required string Step { get; init; }

    /// <summary>The enrichment mapping adding paths it rendered, as pinned; null for one that adds records of their own.</summary>
    public string? Mapping { get; init; }

    /// <summary>How many rows of its dataset the record had; null for an enrichment that reads no rows.</summary>
    public int? Rows { get; init; }

    public IReadOnlyList<PreviewEnrichEffect> Effects { get; init; } = [];

    /// <summary>
    /// The records the enrichment adds to the record (osdu/docs/reference/flow/enrich-records.md), each under the id the record
    /// links; empty for an enrichment that adds none.
    /// </summary>
    public IReadOnlyList<PreviewEnrichRecord> Records { get; init; } = [];

    public IReadOnlyList<string> Holds { get; init; } = [];

    /// <summary>True when the enrichment did not render: one before it held the record, or it waits for the platform.</summary>
    public bool NotRendered { get; init; }

    /// <summary>
    /// True when the enrichment held and was set aside (<c>onHold: step</c>, osdu/docs/reference/flow/enrich-holds.md): the
    /// record goes ahead with what it added to its last delivery, which <see cref="Effects"/> lists, and <see cref="Holds"/> says why.
    /// </summary>
    public bool SetAside { get; init; }

    /// <summary>For an enrichment set aside, the records its last delivery added, which the record keeps linking.</summary>
    public IReadOnlyList<string> KeptRecords { get; init; } = [];

    /// <summary>
    /// What the enrichment does beside adding paths, in the order it does it: <c>records</c> it adds
    /// (osdu/docs/reference/flow/enrich-records.md), the record's <c>bulk</c> data (enrich-bulk.md), a <c>workflow</c> the flow
    /// runs over it (enrich-workflow.md); empty for one that adds paths alone.
    /// </summary>
    public IReadOnlyList<string> Supplies { get; init; } = [];

    /// <summary>What <paramref name="step"/> does beside adding paths, as <see cref="Supplies"/> names it; none for an enrichment not found.</summary>
    public static IReadOnlyList<string> SuppliesOf(Model.EnrichStep? step)
    {
        if (step is null)
        {
            return [];
        }

        var supplies = new List<string>(3);
        if (step.Records is not null)
        {
            supplies.Add("records");
        }

        if (step.Bulk is not null)
        {
            supplies.Add("bulk");
        }

        if (step.Workflow is not null)
        {
            supplies.Add("workflow");
        }

        return supplies;
    }

    /// <summary>The preview of what <paramref name="contribution"/> says.</summary>
    public static PreviewEnrichStep Of(Rendering.StepContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return new PreviewEnrichStep
        {
            Step = contribution.Step,
            Mapping = contribution.Mapping,
            Rows = contribution.Rows,
            Effects = contribution.Effects
                .Select(e => new PreviewEnrichEffect(e.Kind == Rendering.CompositionEffectKind.Add ? "add" : "set", e.Path, e.Value.DeepClone()))
                .ToList(),
            Records = contribution.Records.Select(d => new PreviewEnrichRecord(d.Id, d.Kind, d.Row, d.Record.DeepClone())).ToList(),
            Holds = contribution.Holds,
            NotRendered = contribution.NotRendered,
            SetAside = contribution.SetAside,
            KeptRecords = contribution.KeptRecords,
        };
    }

    /// <summary>The steps with the values they wrote left out, as a preview too large to show its document shows them: the paths stay.</summary>
    public static IReadOnlyList<PreviewEnrichStep> WithoutValues(IReadOnlyList<PreviewEnrichStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Select(s => s with
        {
            Effects = s.Effects.Select(e => e with { Value = null }).ToList(),
            Records = s.Records.Select(d => d with { Record = null }).ToList(),
        }).ToList();
    }
}

/// <summary>
/// One record an enrichment adds to the previewed record: its id, made from the record's id and the enrichment, its kind, the
/// row of the enrichment's dataset it is rendered from, and what the enrichment's mapping rendered into it, left out (null)
/// when the preview is too large to show its document. Its id, kind, access list and legal block are written when it is
/// delivered, and a dataset's file properties when the Dataset service registers it.
/// </summary>
public sealed record PreviewEnrichRecord(string Id, string Kind, int Row, JsonNode? Record);

/// <summary>
/// One thing a step did to the previewed record: <c>set</c> a path the record held nothing at, or <c>add</c> items to the list
/// there; and what it wrote, left out (null) when the preview is too large to show its document.
/// </summary>
public sealed record PreviewEnrichEffect(string Kind, string Path, JsonNode? Value);

/// <summary>
/// The alternative of a <c>$coalesce</c> node that gave the value it wrote: which one of how many, where it reads, how many
/// values it gave (one per item of a repeated array), and whether one of them is an unverified reference.
/// </summary>
public sealed record PreviewChoice(string Target, int Alternative, int Of, string Origin, int Values, bool Unverified);

/// <summary>A value of the sent document that the platform gives when the record is sent: where it is, and what it stands for.</summary>
public sealed record PreviewPlaceholder(string Path, string StandsFor);

/// <summary>One question the render asked the platform's search, and what it found.</summary>
public sealed record PreviewSearch(string Kind, string Field, string Value, string Outcome, string? Id);

/// <summary>An OSDU id the document refers to, the property it is in, and the record of the ledger that holds it, if any.</summary>
public sealed record PreviewReference(string Id, string Property, PreviewHolder? Holder);

/// <summary>The record of the ledger delivered to a referenced id: which flow, which record, and where it stands.</summary>
public sealed record PreviewHolder(Guid FlowId, Guid DeliveryKey, string SourceKey, string? Label, string Status);

/// <summary>One payload part of the record (the only one for a route that streams one payload set) and its files.</summary>
public sealed record PreviewPayloadPart
{
    /// <summary>The part's role on a route that sends parts (<c>files</c>, <c>bulk</c>), or null for the one payload set.</summary>
    public string? Role { get; init; }

    public required string Payload { get; init; }

    /// <summary>Where the files are listed from, or null for an optional part the row names no folder for.</summary>
    public string? Location { get; init; }

    public IReadOnlyList<PreviewFile> Files { get; init; } = [];

    public int TotalFiles { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>True when the part holds more files than <see cref="Files"/> lists.</summary>
    public bool Truncated { get; init; }

    /// <summary>Why the files could not be listed, or null.</summary>
    public string? Problem { get; init; }
}

/// <summary>One payload file: its name, size and time, and for a parquet file, the shape its footer declares.</summary>
public sealed record PreviewFile
{
    public required string Name { get; init; }

    public required long Size { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }

    public PreviewParquet? Parquet { get; init; }

    /// <summary>Why the parquet footer was not read, or what reading it found wrong.</summary>
    public string? FooterProblem { get; init; }
}

/// <summary>What a parquet file's footer declares: its rows and columns, the column names bounded.</summary>
public sealed record PreviewParquet(long Rows, int Columns, IReadOnlyList<string> ColumnNames, bool ColumnNamesTruncated);

/// <summary>
/// One request of a delivery, in order: the service it goes to, the request, what it carries, and what the service answers
/// that the delivery goes on with. <see cref="Body"/> is the request body where it is built from the record alone.
/// </summary>
public sealed record PreviewStep
{
    public required int Order { get; init; }

    public required string Service { get; init; }

    public required string Request { get; init; }

    public required string What { get; init; }

    public string? Returns { get; init; }

    /// <summary>How often the request is made for the record: once, or once per file.</summary>
    public string? Repeats { get; init; }

    public JsonObject? Body { get; init; }
}
