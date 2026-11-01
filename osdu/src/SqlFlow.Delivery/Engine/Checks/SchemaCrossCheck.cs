using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>What the partition's Schema service says of a kind, set against the template a mapping pins.</summary>
public static class SchemaMatchStates
{
    /// <summary>The service holds the kind's schema, and it says what the template says, title and descriptions aside.</summary>
    public const string Matches = "matches";

    /// <summary>The service holds the kind's schema, and a property of it differs from the template.</summary>
    public const string Differs = "differs";

    /// <summary>The service holds the kind's schema, and no template was at hand to set it against: an added record's kind.</summary>
    public const string Read = "read";

    /// <summary>The service holds no schema of the kind. Storage takes the record all the same; search cannot index it.</summary>
    public const string Unregistered = "unregistered";

    /// <summary>The service could not be asked, or refused to answer, so nothing is known of the kind.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The kind is no OSDU record (a DSPDM row, an Energistics object), so no OSDU schema describes it.</summary>
    public const string NotApplicable = "notApplicable";
}

/// <summary>One thing about a property that differs: its value in the template, and in the Schema service's schema.</summary>
public sealed record SchemaDifferenceField(string Field, string? Template, string? Service);

/// <summary>A property the template and the Schema service's schema do not agree on, and what that means for a mapping.</summary>
/// <param name="Path">The property, as a template path.</param>
/// <param name="Change"><c>onlyInService</c>, <c>onlyInTemplate</c> or <c>different</c>.</param>
/// <param name="Impact"><c>breaking</c> when a document written for the template can be refused, else <c>additive</c>.</param>
/// <param name="Fields">Each thing about the property that differs.</param>
public sealed record SchemaDifference(string Path, string Change, string Impact, IReadOnlyList<SchemaDifferenceField> Fields);

/// <summary>
/// What the partition's Schema service holds for a kind, set against the template a mapping pins
/// (osdu/docs/reference/concepts/dry-run.md, The Schema service): what it says of the kind, its status and scope, how many
/// schemas bundling it read and which references could not be followed, and every property the two disagree on.
/// </summary>
public sealed record SchemaCrossMatch
{
    /// <summary>The kind asked about.</summary>
    public required string Kind { get; init; }

    /// <summary>What the service says of it (<see cref="SchemaMatchStates"/>).</summary>
    public required string State { get; init; }

    /// <summary>Why nothing could be judged against the service, for a state that leaves it so.</summary>
    public string? Why { get; init; }

    /// <summary><c>PUBLISHED</c>, <c>DEVELOPMENT</c> or <c>OBSOLETE</c>; null when the listing does not say.</summary>
    public string? Status { get; init; }

    /// <summary><c>INTERNAL</c> (the partition's own) or <c>SHARED</c> (the platform's); null when the listing does not say.</summary>
    public string? Scope { get; init; }

    /// <summary>The content version of the template the mapping pins; null where no template was at hand.</summary>
    public string? TemplateVersion { get; init; }

    /// <summary>The content version of the schema the service holds, once bundled.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>How many schemas bundling it read: the kind's, and each one it refers to.</summary>
    public int SchemasRead { get; init; }

    /// <summary>The references bundling could not follow, each with why; what they describe counts as not checked.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    /// <summary>How many properties differ, listed or not, leaving out what only reads differently.</summary>
    public int DifferenceCount { get; init; }

    /// <summary>Of those, how many can make the service's schema refuse a document written for the template.</summary>
    public int BreakingCount { get; init; }

    /// <summary>The properties that differ, the breaking ones first, at most <see cref="SchemaCrossCheck.MaxDifferencesListed"/>.</summary>
    public IReadOnlyList<SchemaDifference> Differences { get; init; } = [];
}

/// <summary>What a Schema service was found to hold for a kind: the read of it, and what its listing says of it.</summary>
internal sealed record SchemaServiceKind(SchemaServiceRead Read, SchemaListing? Listing);

/// <summary>
/// The schemas read from a partition's Schema service, kept for a few minutes per flow and kind, so going through a mapping's
/// records one at a time asks the service for a kind's schema, and every schema it refers to, once rather than for each
/// record. A kind the service holds none of is kept for a moment only, so one registered just now is seen; a read that failed is
/// not kept at all. A flow is another flow's credentials, so nothing is shared between flows.
/// </summary>
public sealed class SchemaServiceReads
{
    /// <summary>How long a schema the service answered with is kept.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(5);

    /// <summary>How long it is kept that the service holds no schema of a kind.</summary>
    public static readonly TimeSpan KeepAbsent = TimeSpan.FromSeconds(30);

    /// <summary>The most kinds kept; past it, what is oldest goes first.</summary>
    public const int MaxKept = 128;

    private readonly ConcurrentDictionary<string, Entry> _kept = new(StringComparer.Ordinal);

    /// <summary>What is kept for <paramref name="key"/>, or <paramref name="read"/>'s answer, kept from now on.</summary>
    internal async Task<SchemaServiceKind> GetAsync(string key, TimeProvider time, Func<Task<SchemaServiceKind>> read)
    {
        var now = time.GetUtcNow();
        var entry = _kept.AddOrUpdate(
            key,
            _ => new Entry(new Lazy<Task<SchemaServiceKind>>(read, LazyThreadSafetyMode.ExecutionAndPublication), now),
            (_, held) => Expired(held, now) ? new Entry(new Lazy<Task<SchemaServiceKind>>(read, LazyThreadSafetyMode.ExecutionAndPublication), now) : held);
        try
        {
            var answer = await entry.Read.Value.ConfigureAwait(false);
            Trim();
            return answer;
        }
        catch
        {
            _kept.TryRemove(KeyValuePair.Create(key, entry));
            throw;
        }
    }

    private static bool Expired(Entry entry, DateTimeOffset now)
    {
        var read = entry.Read.IsValueCreated ? entry.Read.Value : null;
        return read is { IsCompletedSuccessfully: true } && now - entry.Born >= (read.Result.Read.Schema is null ? KeepAbsent : Keep);
    }

    private void Trim()
    {
        if (_kept.Count <= MaxKept)
        {
            return;
        }

        foreach (var (key, entry) in _kept.OrderBy(pair => pair.Value.Born).Take(_kept.Count - MaxKept).ToList())
        {
            _kept.TryRemove(KeyValuePair.Create(key, entry));
        }
    }

    private sealed record Entry(Lazy<Task<SchemaServiceKind>> Read, DateTimeOffset Born);
}

/// <summary>A kind as a check has it: what the service says of it, and its rules when the service holds its schema.</summary>
internal sealed record KindSchema(SchemaCrossMatch Match, SchemaRules? Rules);

/// <summary>
/// Sets what a mapping renders against what the partition's Schema service holds. The Schema service validates no record: it
/// stores and serves schemas (openapi schema_service, and <c>SchemaController</c> at commit a857439 of
/// community.opengroup.org/osdu/platform/system/schema-service, whose routes create, read, list and update them). So a kind's
/// schema is read from it (<see cref="SchemaServiceReader"/>) and a document is checked here, by the check every validation
/// shares (<see cref="RecordValidator"/>), as the route will send it: the gate before sending and the explorer judge with the
/// same rules, so a verdict here is the one they would reach against that schema.
/// </summary>
/// <remarks>
/// Nothing a check asks of the service fails what asked: a kind the service holds no schema of, a service that refuses or does
/// not answer, and a kind no OSDU schema describes each come back as a state saying so, with why, and the documents are left
/// unjudged rather than passed.
/// </remarks>
public sealed class SchemaCrossCheck
{
    /// <summary>The most differing properties a cross match lists; every one is counted.</summary>
    public const int MaxDifferencesListed = 50;

    private readonly FlowRuntime _runtime;
    private readonly Dictionary<string, KindSchema> _kinds = new(StringComparer.Ordinal);

    private SchemaCrossCheck(FlowRuntime runtime) => _runtime = runtime;

    /// <summary>What the service says of the kind the flow's mapping renders.</summary>
    public SchemaCrossMatch Match { get; private set; } = null!;

    /// <summary>What it says of the kinds of the records the flow's enrichments add, as far as they were met.</summary>
    public IReadOnlyList<SchemaCrossMatch> Added => _kinds
        .Where(k => !string.Equals(k.Key, Match.Kind, StringComparison.Ordinal))
        .OrderBy(k => k.Key, StringComparer.Ordinal)
        .Select(k => k.Value.Match)
        .ToList();

    internal SchemaRules? Rules => _kinds[Match.Kind].Rules;

    /// <summary>Reads what the Schema service holds for the kind <paramref name="runtime"/>'s mapping renders.</summary>
    public static async Task<SchemaCrossCheck> OpenAsync(FlowRuntime runtime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var check = new SchemaCrossCheck(runtime);
        check.Match = (await check.KindAsync(runtime.Mapping.Mapping.Kind, runtime.Mapping.Schema, ct).ConfigureAwait(false)).Match;
        return check;
    }

    /// <summary>The kind <paramref name="kind"/> as the service holds it, read once for this check, set against <paramref name="template"/> when one is given.</summary>
    internal async Task<KindSchema> KindAsync(string kind, SchemaSnapshot? template, CancellationToken ct)
    {
        if (!_kinds.TryGetValue(kind, out var known))
        {
            known = await ReadAsync(kind, template, ct).ConfigureAwait(false);
            _kinds[kind] = known;
        }

        return known;
    }

    /// <summary>
    /// The one document <paramref name="render"/> rendered against the service's schema: what the service says of the kind, the
    /// verdict and how to meet it, what the template the mapping pins says of the same document, and the same of each record
    /// its enrichments add. A row that rendered no document, or a record held before it is whole, is said rather than judged,
    /// since what is wrong with it is that it is not finished.
    /// </summary>
    internal async Task<PreviewSchemaCheck> OfRecordAsync(RenderResult? render, CancellationToken ct)
    {
        if (render is null)
        {
            return new PreviewSchemaCheck { Schema = Match, Problem = "The row renders no document, so there is nothing to hold to a schema." };
        }

        if (render.IsHeld)
        {
            return new PreviewSchemaCheck { Schema = Match, Problem = "The record is held before it is whole, so its document is not held to a schema. Settle what holds it first." };
        }

        var flow = _runtime.Flow;
        var templateRules = SchemaRules.Of(_runtime.Mapping.Schema);
        var filled = ValidationGate.RouteFilled(flow, render.Document);
        var templateFindings = RecordValidator.Check(render.Document, templateRules, filled);
        var serviceRules = Rules;
        var serviceFindings = serviceRules is null ? null : RecordValidator.Check(render.Document, serviceRules, filled);
        var added = await AddedOfAsync(render, ct).ConfigureAwait(false);

        var asked = templateFindings.References
            .Concat(serviceFindings?.References ?? [])
            .Concat(added.SelectMany(a => a.Findings?.References ?? []))
            .ToList();
        var (answers, unasked) = await ReferencesAsync(asked, ct).ConfigureAwait(false);
        var now = _runtime.Context.Time.GetUtcNow().UtcDateTime;
        var templateVerdict = ValidationVerdict.Of(templateFindings, templateRules, VerdictSchema.Template, answers, now);
        var serviceVerdict = serviceFindings is null ? null : ValidationVerdict.Of(serviceFindings, serviceRules, VerdictSchema.SchemaService, answers, now);
        if (unasked is not null && serviceVerdict is not null)
        {
            serviceVerdict = serviceVerdict with { Notes = [.. serviceVerdict.Notes, unasked] };
        }

        ValidationGuidance? guidance = null;
        if (serviceVerdict is not null && serviceRules is not null)
        {
            var (example, note) = serviceVerdict.Problems.Count + serviceVerdict.Unverified.Count > 0
                ? await OfficialExampleReads.ReadAsync(_runtime.Context.Examples, Match.Kind, ct).ConfigureAwait(false)
                : (null, null);
            guidance = ValidationGuide.Of(serviceRules, serviceVerdict, render.Document, example, note);
        }

        return new PreviewSchemaCheck
        {
            Schema = Match,
            Verdict = serviceVerdict?.ToJson(),
            Guidance = guidance,
            Problem = serviceVerdict is null ? Match.Why : null,
            TemplateOutcome = ValidationOutcomes.Name(templateVerdict.Outcome),
            TemplateProblems = templateVerdict.ProblemCount,
            Added = added.Select(a => new PreviewAddedSchemaCheck(
                a.Record.Id, a.Record.Kind, a.Record.Row, a.Schema.Match,
                a.Findings is null ? null : ValidationVerdict.Of(a.Findings, a.Schema.Rules, VerdictSchema.SchemaService, answers, now).ToJson())).ToList(),
        };
    }

    /// <summary>The records <paramref name="render"/>'s enrichments add, each held to the schema the service holds of its own kind.</summary>
    internal async Task<IReadOnlyList<AddedRecordCheck>> AddedOfAsync(RenderResult render, CancellationToken ct)
    {
        var added = new List<AddedRecordCheck>();
        foreach (var record in render.Enrichment.SelectMany(step => step.Records))
        {
            var kind = await KindAsync(record.Kind, TemplateOfAdded(record.Kind), ct).ConfigureAwait(false);
            added.Add(new AddedRecordCheck(record, kind, kind.Rules is { } rules ? RecordValidator.Check(record.Record, rules) : null));
        }

        return added;
    }

    /// <summary>
    /// Whether the records <paramref name="asked"/> names exist, and why it is not known when the lookup failed: a lookup that
    /// fails leaves them not checked rather than failing what asked.
    /// </summary>
    internal async Task<(IReadOnlyDictionary<string, ReferenceAnswer> Answers, string? Problem)> ReferencesAsync(IReadOnlyCollection<FoundReference> asked, CancellationToken ct)
    {
        if (asked.Count == 0)
        {
            return (new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal), null);
        }

        try
        {
            var resolver = await _runtime.StorageReferencesAsync(ct).ConfigureAwait(false);
            return (await resolver.ResolveAsync(asked, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (Unreached(ex, ct))
        {
            return (new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal), $"The records it refers to could not be looked up: {HeaderRedaction.RedactMessage(ex.Message)}");
        }
    }

    /// <summary>Whether <paramref name="ex"/> says a service could not be reached or refused, rather than that the work was stopped.</summary>
    internal static bool Unreached(Exception ex, CancellationToken ct)
        => ex is SqlFlowException or HttpRequestException or TimeoutException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private SchemaSnapshot? TemplateOfAdded(string kind)
        => _runtime.Mapping.Steps.Select(step => step.Records?.Schema).FirstOrDefault(schema => schema is not null && string.Equals(schema.Kind, kind, StringComparison.Ordinal));

    private async Task<KindSchema> ReadAsync(string kind, SchemaSnapshot? template, CancellationToken ct)
    {
        if (DspdmKinds.Is(kind) || EtpKinds.Is(kind))
        {
            var what = DspdmKinds.Is(kind) ? "a DSPDM business object's row" : "an Energistics object";
            return new KindSchema(
                new SchemaCrossMatch
                {
                    Kind = kind,
                    State = SchemaMatchStates.NotApplicable,
                    Why = $"{kind} is {what}, not an OSDU record, so the Schema service describes nothing of it. The template the mapping pins is what it is held to.",
                },
                null);
        }

        SchemaServiceKind read;
        try
        {
            var client = await _runtime.TargetClientAsync(ct).ConfigureAwait(false);
            var context = _runtime.Context;
            read = await context.SchemaReads.GetAsync($"{_runtime.Flow.Id:D}|{kind}", context.Time, async () =>
            {
                var reader = new SchemaServiceReader(client, context.Time);
                var schema = await reader.ReadAsync(kind, ct).ConfigureAwait(false);
                return new SchemaServiceKind(schema, schema.Schema is null ? null : await ListingAsync(reader, kind, ct).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (Unreached(ex, ct))
        {
            return new KindSchema(
                new SchemaCrossMatch
                {
                    Kind = kind,
                    State = SchemaMatchStates.Unavailable,
                    TemplateVersion = template?.Version,
                    Why = $"The Schema service could not be read: {HeaderRedaction.RedactMessage(ex.Message)}",
                },
                null);
        }

        if (read.Read.Schema is not { } service)
        {
            return new KindSchema(
                new SchemaCrossMatch
                {
                    Kind = kind,
                    State = SchemaMatchStates.Unregistered,
                    TemplateVersion = template?.Version,
                    Why = $"The Schema service holds no schema of {kind}. Storage takes the record all the same, but the indexer cannot index it, so search and the explorer will not find it by its data until the kind is registered.",
                },
                null);
        }

        var match = new SchemaCrossMatch
        {
            Kind = kind,
            State = template is null ? SchemaMatchStates.Read : SchemaMatchStates.Matches,
            Status = read.Listing?.Status,
            Scope = read.Listing?.Scope,
            TemplateVersion = template?.Version,
            ServiceVersion = service.Version,
            SchemasRead = read.Read.Read.Count,
            Unresolved = read.Read.Unresolved.Select(HeaderRedaction.RedactMessage).ToList(),
        };
        return new KindSchema(template is null ? match : Compared(match, template, service), SchemaRules.Of(service));
    }

    /// <summary>A schema's status and scope, which say something of it and decide nothing: a listing that fails leaves them unsaid.</summary>
    private static async Task<SchemaListing?> ListingAsync(SchemaServiceReader reader, string kind, CancellationToken ct)
    {
        try
        {
            return await reader.InfoAsync(kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Unreached(ex, ct))
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="match"/> with what differs between the template and the service's schema, property by property, what only
    /// reads differently left out. Two versions are compared as a mapping reads them, since a bundle made by the service's
    /// answers and one a template file holds are written differently when they say the same.
    /// </summary>
    private static SchemaCrossMatch Compared(SchemaCrossMatch match, SchemaSnapshot template, SchemaSnapshot service)
    {
        if (string.Equals(template.Version, service.Version, StringComparison.Ordinal))
        {
            return match;
        }

        var changes = TemplateComparer.Compare(OsduTemplate.From(template), OsduTemplate.From(service)).Changes
            .Where(change => change.Impact != TemplateChangeImpact.Wording)
            .OrderByDescending(change => change.Impact)
            .ToList();
        return changes.Count == 0
            ? match
            : match with
            {
                State = SchemaMatchStates.Differs,
                DifferenceCount = changes.Count,
                BreakingCount = changes.Count(change => change.Impact == TemplateChangeImpact.Breaking),
                Differences = changes.Take(MaxDifferencesListed).Select(Difference).ToList(),
            };
    }

    private static SchemaDifference Difference(TemplateVariableChange change)
        => new(
            change.Path,
            change.Change switch
            {
                TemplateVariableChangeKind.Added => "onlyInService",
                TemplateVariableChangeKind.Removed => "onlyInTemplate",
                _ => "different",
            },
            change.Impact == TemplateChangeImpact.Breaking ? "breaking" : "additive",
            change.Fields.Select(field => new SchemaDifferenceField(field.Field, field.Before, field.After)).ToList());
}

/// <summary>A record an enrichment adds, held to the schema of its own kind; no findings when the service holds none.</summary>
internal sealed record AddedRecordCheck(EnrichRecord Record, KindSchema Schema, RecordFindings? Findings);

/// <summary>A record one of the document's enrichments adds, against what the Schema service holds of its kind.</summary>
/// <param name="Id">The id it would be written under.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Row">The row of the enrichment's dataset it came from, counting from 0.</param>
/// <param name="Schema">What the service says of its kind.</param>
/// <param name="Verdict">The verdict (<see cref="ValidationVerdict.ToJson"/>); null when no schema could be had.</param>
public sealed record PreviewAddedSchemaCheck(string Id, string Kind, int Row, SchemaCrossMatch Schema, JsonObject? Verdict);

/// <summary>
/// One document against the partition's Schema service (osdu/docs/reference/concepts/dry-run.md, The Schema service): what the
/// service says of the kind, the verdict of the document against its schema and how to meet each rule broken, what the template
/// the mapping pins says of the same document, and why nothing was judged when nothing was.
/// </summary>
public sealed record PreviewSchemaCheck
{
    public required SchemaCrossMatch Schema { get; init; }

    /// <summary>The verdict against the service's schema (<see cref="ValidationVerdict.ToJson"/>); null when none was reached.</summary>
    public JsonObject? Verdict { get; init; }

    /// <summary>What each finding of the verdict comes to for the person fixing it; null when none was reached.</summary>
    public ValidationGuidance? Guidance { get; init; }

    /// <summary>Why no verdict was reached; null when one was.</summary>
    public string? Problem { get; init; }

    /// <summary>What the template the mapping pins says of the same document, as an outcome's name.</summary>
    public string? TemplateOutcome { get; init; }

    /// <summary>How many problems the template finds in it.</summary>
    public long TemplateProblems { get; init; }

    /// <summary>The records the document's enrichments add, each against the schema of its own kind.</summary>
    public IReadOnlyList<PreviewAddedSchemaCheck> Added { get; init; } = [];
}
