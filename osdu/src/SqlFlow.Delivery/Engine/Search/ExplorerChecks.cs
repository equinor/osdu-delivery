using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>Which schema the explorer checks a record against.</summary>
public enum ExplorerSchemaSource
{
    /// <summary>What the partition's Schema service holds for the record's kind: what OSDU expects of it.</summary>
    Osdu,

    /// <summary>A template a synced repository holds in a template folder: the kind's, or the version named.</summary>
    Template,
}

/// <summary>The schema a record was checked against, and where it came from.</summary>
/// <param name="Kind">The kind whose schema it is.</param>
/// <param name="Version">Its content version.</param>
/// <param name="Source"><c>schema-service</c> or <c>template</c>.</param>
/// <param name="Read">The schema ids read from the Schema service to bundle it; empty for a repository's template.</param>
/// <param name="Unresolved">The references bundling could not resolve, each with why.</param>
/// <param name="Notes">What the schema states that no check asserts (<see cref="SchemaRules.Notes"/>).</param>
public sealed record ExplorerSchema(string Kind, string Version, string Source, IReadOnlyList<string> Read, IReadOnlyList<string> Unresolved, IReadOnlyList<string> Notes);

/// <summary>
/// A record OSDU holds checked against a schema (osdu/docs/reference/concepts/explorer.md, Validate): the record's id,
/// version and kind, the schema, the verdict (<see cref="ValidationVerdict.ToJson"/>), or why nothing could be checked,
/// with the template versions the synced repositories hold for the kind, which a reader can check it against instead.
/// </summary>
public sealed record ExplorerValidation
{
    public required string TargetId { get; init; }

    /// <summary>The version checked: the one asked, else the version storage answered with.</summary>
    public long? Version { get; init; }

    /// <summary>Whether storage holds the record (at that version).</summary>
    public bool Found { get; init; }

    public string? Kind { get; init; }

    public ExplorerSchema? Schema { get; init; }

    public JsonObject? Verdict { get; init; }

    /// <summary>Why the record was not checked; null when it was.</summary>
    public string? Problem { get; init; }

    /// <summary>The template versions the synced repositories hold for the record's kind, the most recently synced first.</summary>
    public IReadOnlyList<string> TemplateVersions { get; init; } = [];

    /// <summary>What each finding of the verdict comes to for the person fixing it (<see cref="ValidationGuide"/>); null when the record was not checked.</summary>
    public ValidationGuidance? Guidance { get; init; }
}

/// <summary>
/// A rule the records of a list break, how many records break it, and one example: where it is, what is wrong, what the
/// schema expects there (<paramref name="Expected"/>, in one line) and how to meet it (<paramref name="Advice"/>).
/// </summary>
public sealed record ExplorerRuleCount(
    string At, string Rule, long Records, long Problems, string ExampleId, string ExamplePath, string ExampleMessage, string ExampleValue,
    string? Expected = null, string? Advice = null);

/// <summary>One record of a list and what checking it came to; <paramref name="AssertionFailures"/> is how many judgements of the mapping's assertions failed on it, null when none were judged.</summary>
public sealed record ExplorerRecordVerdict(string Id, string? Kind, string Outcome, long Problems, long Unverified, string? First, long? AssertionFailures = null);

/// <summary>
/// The mapping whose assertions a check judges on the records OSDU holds (osdu/docs/reference/flow/mapping-assertions.md),
/// or why none could be read: the mapping, or the problem, never both.
/// </summary>
public sealed record ExplorerAssertions(MappingDefinition? Mapping, string? Problem);

/// <summary>
/// An assertion the records of a list fail: the property, the assertion and what its failure does, how many of the records
/// fail it and how many times, and one example.
/// </summary>
public sealed record ExplorerAssertionCount(
    string At, string Assertion, string Stage, string OnFail, long Records, long Failures, string ExampleId, string ExamplePath, string ExampleMessage, string ExampleValue);

/// <summary>A kind no schema could be had for, and why its records were not checked.</summary>
public sealed record ExplorerKindProblem(string Kind, string Why, long Records);

/// <summary>
/// The records a search finds checked against their schemas, up to a bound: how many the search matches, how many were
/// read and checked, their outcomes, the rules they break counted by records with an example of each, each record's outcome,
/// the schemas used, and the kinds no schema could be had for.
/// </summary>
public sealed record ExplorerListValidation
{
    public required string Reading { get; init; }

    public string? Query { get; init; }

    public string Kind { get; init; } = "*:*:*:*";

    /// <summary>How many records the search matches.</summary>
    public long Matched { get; init; }

    /// <summary>The most records the check reads.</summary>
    public int Asked { get; init; }

    /// <summary>How many records storage returned and the check read.</summary>
    public int Read { get; init; }

    /// <summary>The ids the search found that storage did not return (deleted since, or not the caller's to read).</summary>
    public IReadOnlyList<string> NotFound { get; init; } = [];

    public long Valid { get; init; }

    public long Invalid { get; init; }

    public long Unverified { get; init; }

    /// <summary>The records whose kind no schema could be had for.</summary>
    public long NotChecked { get; init; }

    public IReadOnlyList<ExplorerRuleCount> Rules { get; init; } = [];

    public IReadOnlyList<ExplorerRecordVerdict> Records { get; init; } = [];

    public IReadOnlyList<ExplorerSchema> Schemas { get; init; } = [];

    public IReadOnlyList<ExplorerKindProblem> Unavailable { get; init; } = [];

    /// <summary>Whether the search matches more records than the check read, so the counts are of the first ones.</summary>
    public bool Cut { get; init; }

    /// <summary>The mapping whose assertions were judged on the records, as <c>Name@version</c>; null when none was asked for or could be read.</summary>
    public string? Mapping { get; init; }

    /// <summary>The records the mapping's assertions were judged on: those of the entity type it renders.</summary>
    public long Asserted { get; init; }

    /// <summary>Of those, the records that failed one or more of them.</summary>
    public long FailingAssertions { get; init; }

    /// <summary>The assertions the records fail, the most records first, each with an example.</summary>
    public IReadOnlyList<ExplorerAssertionCount> Assertions { get; init; } = [];

    /// <summary>What the explorer did that the reader should know, and the service's words when it refused the search.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// The explorer's checks of what OSDU holds against what OSDU expects (osdu/docs/reference/concepts/explorer.md,
/// Validate): one record, at its latest or at a version, or the records a search finds, up to <see cref="MaxRecords"/>.
/// A record is checked by the module every validation shares (<see cref="RecordValidator"/>) against the schema the
/// partition's Schema service holds for its kind (<see cref="SchemaServiceReader"/>), or a template a synced repository
/// holds that the reader picks, and the records it refers to are looked up in OSDU's storage service, in one batch.
/// Nothing the delivery system keeps of a record (its ledger, its mapping, its cache) is read; a repository's template is
/// the one exception, read from its file, and only when the reader asks for it.
/// </summary>
public sealed class ExplorerChecks
{
    /// <summary>The most records one check of a search reads.</summary>
    public const int MaxRecords = 1000;

    /// <summary>The most rules a list's answer names.</summary>
    public const int MaxRules = 50;

    private readonly OsduHttpClient _client;
    private readonly OsduRecordProtocol _storage;
    private readonly string _batchPath;
    private readonly Catalog.RepositoryTemplates? _templates;
    private readonly TimeProvider _time;
    private readonly ValidationLimits _limits;
    private readonly IOfficialExamples? _examples;
    private readonly Dictionary<string, (SchemaSnapshot? Schema, ExplorerSchema? Described, string? Why)> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (OfficialExample? Example, string? Note)> _exampleOf = new(StringComparer.Ordinal);

    /// <param name="client">The connection OSDU is read through.</param>
    /// <param name="storage">The storage protocol a record is read back with, as the explorer reads one.</param>
    /// <param name="batchPath">Storage's batch read, which the records of a list and the ids a record refers to are read through.</param>
    /// <param name="templates">The templates the synced repositories hold, read when a reader picks one; null where none can be read.</param>
    /// <param name="time">The clock a verdict is stamped with.</param>
    /// <param name="limits">The bounds a check of one record keeps to; <see cref="ValidationLimits.Default"/> when null.</param>
    /// <param name="examples">Where the OSDU data definitions' example records are read, which guidance quotes; null where none is kept.</param>
    public ExplorerChecks(
        OsduHttpClient client, OsduRecordProtocol storage, string batchPath, Catalog.RepositoryTemplates? templates, TimeProvider time, ValidationLimits? limits = null,
        IOfficialExamples? examples = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchPath);
        ArgumentNullException.ThrowIfNull(time);
        _client = client;
        _storage = storage;
        _batchPath = batchPath;
        _templates = templates;
        _time = time;
        _limits = limits ?? ValidationLimits.Default;
        _examples = examples;
    }

    /// <summary>
    /// Checks the record <paramref name="targetId"/>, at its latest or at <paramref name="version"/>, and judges on it the
    /// assertions of the mapping <paramref name="assertions"/> names, when it names one.
    /// </summary>
    public async Task<ExplorerValidation> RecordAsync(
        string targetId, long? version, ExplorerSchemaSource source, string? templateVersion, ExplorerAssertions? assertions = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var record = version is null
            ? await _storage.ReadAsync(targetId, ct).ConfigureAwait(false)
            : await _storage.ReadVersionAsync(targetId, version.Value, ct).ConfigureAwait(false);
        if (record is null)
        {
            return new ExplorerValidation
            {
                TargetId = targetId,
                Version = version,
                Found = false,
                Problem = version is null
                    ? $"OSDU's storage service holds no record {targetId}."
                    : string.Create(CultureInfo.InvariantCulture, $"OSDU's storage service holds no version {version} of {targetId}."),
            };
        }

        var kind = KindOf(record);
        var versions = kind is null ? [] : await TemplateVersionsAsync(kind, ct).ConfigureAwait(false);
        var read = version ?? (record["version"] is JsonValue v && v.TryGetValue<long>(out var held) ? held : null);
        if (kind is null)
        {
            return new ExplorerValidation { TargetId = targetId, Version = read, Found = true, Problem = "The record names no kind, so there is no schema to check it against." };
        }

        var (schema, described, why) = await SchemaAsync(kind, source, templateVersion, ct).ConfigureAwait(false);
        if (schema is null)
        {
            // Nothing to check the record against, but a mapping's assertions need no schema: they are judged, as a list's are.
            var judged = assertions?.Mapping is null ? null : Asserted(ValidationVerdict.NotChecked(why!, _time.GetUtcNow().UtcDateTime), record, assertions);
            return new ExplorerValidation { TargetId = targetId, Version = read, Found = true, Kind = kind, Problem = why, TemplateVersions = versions, Verdict = judged?.ToJson() };
        }

        var rules = SchemaRules.Of(schema);
        var findings = RecordValidator.Check(record, rules, limits: _limits, form: RecordForm.Stored);
        var answers = await References().ResolveAsync(findings.References, ct).ConfigureAwait(false);
        var verdict = Asserted(ValidationVerdict.Of(findings, rules, described!.Source, answers, _time.GetUtcNow().UtcDateTime), record, assertions);
        var (example, exampleNote) = verdict.Problems.Count + verdict.Unverified.Count > 0 ? await ExampleOfAsync(kind, ct).ConfigureAwait(false) : (null, null);
        return new ExplorerValidation
        {
            TargetId = targetId,
            Version = read,
            Found = true,
            Kind = kind,
            Schema = described,
            Verdict = verdict.ToJson(),
            TemplateVersions = versions,
            Guidance = ValidationGuide.Of(rules, verdict, record, example, exampleNote),
        };
    }

    /// <summary>
    /// Checks the records <paramref name="explorer"/> finds for <paramref name="search"/>, up to <paramref name="max"/> (at most
    /// <see cref="MaxRecords"/>): their ids paged from the search in its order, the records read from storage in batches,
    /// and each record checked against the schema of its kind, each schema read once.
    /// </summary>
    public async Task<ExplorerListValidation> ListAsync(
        RecordExplorer explorer, ExplorerSearch search, int max, ExplorerSchemaSource source, ExplorerAssertions? assertions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(explorer);
        ArgumentNullException.ThrowIfNull(search);
        max = Math.Clamp(max, 1, MaxRecords);

        // The ids, a page at a time in the search's own order, until enough are found or the search runs out.
        var ids = new List<string>(max);
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ExplorerPage? first = null;
        var offset = 0;
        while (ids.Count < max)
        {
            ct.ThrowIfCancellationRequested();
            var limit = Math.Min(ExplorerSearch.MaxLimit, max - ids.Count);
            if (offset + limit > OsduSearch.MaxWindow)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"The search service pages through the first {OsduSearch.MaxWindow:N0} records a query matches, so no more were read."));
                break;
            }

            var page = await explorer.SearchAsync(search with { Offset = offset, Limit = limit, Facet = null }, ct).ConfigureAwait(false);
            first ??= page;
            if (page.Refusal is { } refusal)
            {
                notes.Add(refusal);
                break;
            }

            // A service answering more than it was asked for is taken at its word for no more than was asked.
            ids.AddRange(page.Hits.Select(h => h.Id).Where(seen.Add).Take(max - ids.Count));
            offset += limit;
            if (page.Hits.Count < limit)
            {
                break;
            }
        }

        notes.AddRange(first?.Notes ?? []);
        var records = await RecordWriter.ReadManyAsync(_client, _batchPath, ids, null, ct).ConfigureAwait(false);
        var notFound = ids.Where(id => !records.ContainsKey(id)).ToList();

        var checkedRecords = new List<Checked>(records.Count);
        var unavailable = new Dictionary<string, (string Why, long Records)>(StringComparer.Ordinal);
        var asked = new List<FoundReference>();
        foreach (var id in ids.Where(records.ContainsKey))
        {
            var record = records[id];
            var kind = KindOf(record);
            if (kind is null)
            {
                Count(unavailable, "(no kind)", "the record names no kind");
                checkedRecords.Add(new Checked(id, null, null, null));
                continue;
            }

            var (schema, _, why) = await SchemaAsync(kind, source, null, ct).ConfigureAwait(false);
            if (schema is null)
            {
                Count(unavailable, kind, why!);
                checkedRecords.Add(new Checked(id, kind, null, null));
                continue;
            }

            var rules = SchemaRules.Of(schema);
            var findings = RecordValidator.Check(record, rules, limits: _limits, form: RecordForm.Stored);
            asked.AddRange(findings.References);
            checkedRecords.Add(new Checked(id, kind, findings, rules));
        }

        var answers = await References().ResolveAsync(asked, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        long valid = 0, invalid = 0, unverified = 0, notChecked = 0;
        var broken = new Dictionary<(string At, string Rule), (long Records, long Problems, SchemaFinding Example, string Id, string Kind, SchemaRules Rules)>();
        var absentBlocks = new Dictionary<string, long>(StringComparer.Ordinal);
        var verdicts = new List<ExplorerRecordVerdict>(checkedRecords.Count);
        var failedAssertions = new Dictionary<(string At, string Assertion), (long Records, long Failures, AssertionFailure Example, string Id)>();
        var assertionNotes = new List<string>();
        long asserted = 0, failingAssertions = 0;
        if (assertions is { Mapping: null, Problem: { } unread })
        {
            assertionNotes.Add($"No assertions were judged: {unread}.");
        }

        foreach (var (id, kind, findings, recordRules) in checkedRecords)
        {
            // The mapping's assertions are judged on every record of the type it renders, whether or not a schema could be had for it.
            long? assertionFailures = null;
            if (assertions?.Mapping is { } mapping && records.TryGetValue(id, out var stored))
            {
                var found = MappingAssertionJudge.OfStored(stored, mapping, out var judgedNotes);
                assertionNotes.AddRange(judgedNotes);
                if (found is not null)
                {
                    asserted++;
                    failingAssertions += found.Failed > 0 ? 1 : 0;
                    assertionFailures = found.Failed;
                    foreach (var group in found.Failures.GroupBy(f => (f.At, f.Assertion)))
                    {
                        var counted = failedAssertions.TryGetValue(group.Key, out var already) ? already : (0, 0, group.First(), id);
                        failedAssertions[group.Key] = (counted.Records + 1, counted.Failures + group.LongCount(), counted.Example, counted.Id);
                    }
                }
            }

            if (findings is null || recordRules is null)
            {
                notChecked++;
                verdicts.Add(new ExplorerRecordVerdict(id, kind, ValidationOutcomes.NotValidated, 0, 0, null, assertionFailures));
                continue;
            }

            var verdict = ValidationVerdict.Of(findings, recordRules, _schemas[Key(kind!, source, null)].Described!.Source, answers, now);
            foreach (var block in findings.ReadAsAbsent)
            {
                absentBlocks[block] = absentBlocks.GetValueOrDefault(block) + 1;
            }

            switch (verdict.Outcome)
            {
                case ValidationOutcome.Valid:
                    valid++;
                    break;
                case ValidationOutcome.Invalid:
                    invalid++;
                    break;
                default:
                    unverified++;
                    break;
            }

            foreach (var group in verdict.Problems.GroupBy(p => (p.At, p.Rule)))
            {
                var held = broken.TryGetValue(group.Key, out var counted) ? counted : (0, 0, group.First(), id, kind!, recordRules);
                broken[group.Key] = (held.Records + 1, held.Problems + group.LongCount(), held.Example, held.Id, held.Kind, held.Rules);
            }

            var firstFound = verdict.Problems.Count > 0 ? verdict.Problems[0] : verdict.Unverified.Count > 0 ? verdict.Unverified[0] : null;
            verdicts.Add(new ExplorerRecordVerdict(
                id, kind, ValidationOutcomes.Name(verdict.Outcome), verdict.ProblemCount, verdict.UnverifiedCount,
                firstFound is null ? null : $"{ValidationVerdict.Where(firstFound)} {firstFound.Rule}: {firstFound.Message}", assertionFailures));
        }

        // Each rule broken most often comes with what the schema expects where it is and how to meet it, worked out on its example.
        var ruleCounts = new List<ExplorerRuleCount>();
        foreach (var (key, count) in broken
            .OrderByDescending(r => r.Value.Records).ThenBy(r => r.Key.At, StringComparer.Ordinal).ThenBy(r => r.Key.Rule, StringComparer.Ordinal)
            .Take(MaxRules))
        {
            var (example, _) = await ExampleOfAsync(count.Kind, ct).ConfigureAwait(false);
            var (guide, expected) = ValidationGuide.For(count.Rules, count.Example, records.GetValueOrDefault(count.Id), example);
            ruleCounts.Add(new ExplorerRuleCount(
                key.At, key.Rule, count.Records, count.Problems, count.Id, count.Example.Path, count.Example.Message, count.Example.Value, expected?.Summary, guide.Advice));
        }

        notes.AddRange(assertionNotes.Distinct(StringComparer.Ordinal));
        notes.AddRange(absentBlocks.Select(b => string.Create(
            CultureInfo.InvariantCulture,
            $"{b.Value:N0} record(s) hold {b.Key} null or empty, which is how a stored record with no {b.Key} can read, so it was read as absent.")));
        return new ExplorerListValidation
        {
            Reading = first?.Reading ?? "everything",
            Query = first?.Query,
            Kind = first?.Kind ?? search.Kind ?? "*:*:*:*",
            Matched = first?.Total ?? 0,
            Asked = max,
            Read = records.Count,
            NotFound = notFound,
            Valid = valid,
            Invalid = invalid,
            Unverified = unverified,
            NotChecked = notChecked,
            Rules = ruleCounts,
            Records = verdicts,
            Schemas = _schemas.Values.Where(s => s.Described is not null).Select(s => s.Described!).DistinctBy(s => (s.Kind, s.Version)).ToList(),
            Unavailable = unavailable.Select(u => new ExplorerKindProblem(u.Key, u.Value.Why, u.Value.Records)).OrderByDescending(u => u.Records).ToList(),
            Cut = (first?.Total ?? 0) > ids.Count,
            Notes = notes.Distinct(StringComparer.Ordinal).ToList(),
            Mapping = assertions?.Mapping?.Reference,
            Asserted = asserted,
            FailingAssertions = failingAssertions,
            Assertions = failedAssertions
                .OrderByDescending(a => a.Value.Records).ThenBy(a => a.Key.At, StringComparer.Ordinal).ThenBy(a => a.Key.Assertion, StringComparer.Ordinal)
                .Take(MaxRules)
                .Select(a => new ExplorerAssertionCount(
                    a.Key.At, a.Key.Assertion, a.Value.Example.Stage, a.Value.Example.OnFail, a.Value.Records, a.Value.Failures,
                    a.Value.Id, a.Value.Example.Path, a.Value.Example.Message, a.Value.Example.Value))
                .ToList(),
        };
    }

    /// <summary>
    /// A verdict with what the mapping's assertions find of the record added (osdu/docs/reference/flow/mapping-assertions.md),
    /// and what was not judged among its notes; the verdict as it is when no mapping was asked for.
    /// </summary>
    private static ValidationVerdict Asserted(ValidationVerdict verdict, JsonObject record, ExplorerAssertions? assertions)
    {
        if (assertions is null)
        {
            return verdict;
        }

        if (assertions.Mapping is not { } mapping)
        {
            return verdict with { Notes = [.. verdict.Notes, $"No assertions were judged: {assertions.Problem}."] };
        }

        var findings = MappingAssertionJudge.OfStored(record, mapping, out var notes);
        return verdict with { Assertions = findings, Notes = [.. verdict.Notes, .. notes] };
    }

    /// <summary>The references of what is checked, looked up in OSDU's storage service alone: what OSDU holds is the answer.</summary>
    private ReferenceResolver References()
        => new(null, null, null, (ids, ct) => StoragePresence.PresentAsync(_client, _batchPath, ids, ct));

    /// <summary>
    /// The schema a record of <paramref name="kind"/> is checked against, read once per kind and source: the Schema
    /// service's, or a template a synced repository holds (the version named, else the one the most recently synced
    /// repository holds), read from its file. Null, with why, when there is none.
    /// </summary>
    /// <summary>
    /// The OSDU data definitions' example record of <paramref name="kind"/>, read once per check, and why none is quoted when
    /// one was looked for: the data definitions publish none for the kind, or could not be reached. A kind not of OSDU's
    /// own has no example to look for, and says nothing.
    /// </summary>
    private async Task<(OfficialExample? Example, string? Note)> ExampleOfAsync(string kind, CancellationToken ct)
    {
        if (_examples is null || OsduDataDefinitions.ExamplePath(kind) is null)
        {
            return (null, null);
        }

        if (_exampleOf.TryGetValue(kind, out var known))
        {
            return known;
        }

        (OfficialExample?, string?) found;
        try
        {
            var example = await _examples.ExampleAsync(kind, ct).ConfigureAwait(false);
            found = (example, example is null ? $"The OSDU data definitions publish no example record of {kind}." : null);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            found = (null, $"OSDU's example record of {kind} could not be read: {HeaderRedaction.RedactMessage(ex.Message)}");
        }

        _exampleOf[kind] = found;
        return found;
    }

    private async Task<(SchemaSnapshot? Schema, ExplorerSchema? Described, string? Why)> SchemaAsync(string kind, ExplorerSchemaSource source, string? templateVersion, CancellationToken ct)
    {
        var key = Key(kind, source, templateVersion);
        if (_schemas.TryGetValue(key, out var known))
        {
            return known;
        }

        (SchemaSnapshot?, ExplorerSchema?, string?) found;
        if (source == ExplorerSchemaSource.Osdu)
        {
            SchemaServiceRead read;
            try
            {
                read = await new SchemaServiceReader(_client, _time).ReadAsync(kind, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeliveryException or HttpRequestException && !ct.IsCancellationRequested)
            {
                read = new SchemaServiceRead(null, [], [$"the Schema service could not be read: {HeaderRedaction.RedactMessage(ex.Message)}"]);
            }

            found = read.Schema is { } schema
                ? (schema, new ExplorerSchema(kind, schema.Version, VerdictSchema.SchemaService, read.Read, read.Unresolved, SchemaRules.Of(schema).Notes), null)
                : (null, null, read.Unresolved.Count > 0 ? read.Unresolved[0] : $"The Schema service holds no schema of {kind}.");
        }
        else
        {
            found = await TemplateAsync(kind, templateVersion, ct).ConfigureAwait(false);
        }

        _schemas[key] = found;
        return found;
    }

    /// <summary>The template of <paramref name="kind"/> a synced repository holds, the version named or the kind's, read from its file.</summary>
    private async Task<(SchemaSnapshot?, ExplorerSchema?, string?)> TemplateAsync(string kind, string? templateVersion, CancellationToken ct)
    {
        if (_templates is null)
        {
            return (null, null, "No repository's template can be read here: this host keeps no module database.");
        }

        var version = string.IsNullOrWhiteSpace(templateVersion) ? null : templateVersion.Trim();
        var resolved = await _templates.ResolveAsync(null, kind, version, ct).ConfigureAwait(false);
        return resolved.Schema is { } schema
            ? (schema, new ExplorerSchema(kind, schema.Version, VerdictSchema.Template, [], [], SchemaRules.Of(schema).Notes), null)
            : (null, null, $"{Capitalized(resolved.Problem!)}, or check the record against the Schema service's.");
    }

    /// <summary>The template versions the synced repositories hold for <paramref name="kind"/>, the most recently synced first; none when they cannot be read.</summary>
    private async Task<IReadOnlyList<string>> TemplateVersionsAsync(string kind, CancellationToken ct)
    {
        if (_templates is null)
        {
            return [];
        }

        var all = await _templates.ListForAsync(null, ct).ConfigureAwait(false);
        return all.Where(t => string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase)).Select(t => t.Version).ToList();
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Key(string kind, ExplorerSchemaSource source, string? templateVersion)
        => $"{source}|{kind}|{templateVersion?.Trim()}";

    private static string? KindOf(JsonObject record)
        => record["kind"] is JsonValue value && value.TryGetValue<string>(out var kind) && !string.IsNullOrWhiteSpace(kind) ? kind.Trim() : null;

    private static void Count(Dictionary<string, (string Why, long Records)> unavailable, string kind, string why)
        => unavailable[kind] = unavailable.TryGetValue(kind, out var held) ? (held.Why, held.Records + 1) : (why, 1);

    /// <summary>One record of a list as it was checked: its findings and rules, or none when no schema could be had for its kind.</summary>
    private sealed record Checked(string Id, string? Kind, RecordFindings? Findings, SchemaRules? Rules);
}
