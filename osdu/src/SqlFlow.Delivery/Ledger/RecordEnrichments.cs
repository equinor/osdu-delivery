using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// One enrichment of a record as its ledger row holds it (osdu/docs/reference/flow/enrich.md, What the ledger keeps): what the
/// enrichment adds, the mapping that rendered it, whether it was set aside on its holds, where its rows came from, and every
/// record it adds with the version OSDU holds of it and every write made of it.
/// </summary>
public sealed record RecordEnrichment
{
    /// <summary>The enrichment's name, as the mapping's <c>enrich:</c> declares it.</summary>
    public required string Name { get; init; }

    /// <summary>What it adds to the record, in this order: <c>paths</c>, <c>records</c>, <c>bulk</c>, <c>workflow</c>.</summary>
    public required IReadOnlyList<string> Adds { get; init; }

    /// <summary>The enrichment mapping it rendered, <c>Name@version</c>, with the fingerprint of its document.</summary>
    public string? Mapping { get; init; }

    public string? MappingFingerprint { get; init; }

    /// <summary>For one that adds records: their kind, and how they are written (<c>dataset</c> with files, or <c>storage</c>).</summary>
    public string? RecordKind { get; init; }

    public string? WrittenThrough { get; init; }

    /// <summary>True when it held and was set aside on the record's last render, the record keeping what it added before.</summary>
    public bool SetAside { get; init; }

    /// <summary>Where the rows of the delivered document's enrichment came from; null for one reading no rows, or not delivered yet.</summary>
    public RecordEnrichmentRows? Rows { get; init; }

    /// <summary>Every record it added to the record, those the record links now first.</summary>
    public IReadOnlyList<RecordEnrichmentRecord> Records { get; init; } = [];
}

/// <summary>How many rows the delivered document's enrichment read, the ingestion files they came from (at most twenty listed), and for one row where it is.</summary>
public sealed record RecordEnrichmentRows(int Count, IReadOnlyList<string> Files, int FilesCounted, string? Row);

/// <summary>
/// One record an enrichment added: its OSDU id, whether the record links it now (a record no longer linked stays in OSDU, since
/// earlier versions of the record name it), the version the ledger last saw land, the hash of what was written and of its
/// files, the row it was rendered from, and every write the deliveries made of it, the newest first.
/// </summary>
public sealed record RecordEnrichmentRecord(
    string Id, bool Linked, long? Version, string? Hash, string? FilesHash, string? Source, IReadOnlyList<RecordEnrichmentWrite> Writes);

/// <summary>
/// One write a delivery made of a record an enrichment added, as the record's attempt holds it: the attempt, when the write
/// went, the run it was part of, how the attempt ended (a write that landed stays in OSDU however the record's own write went),
/// the version it landed as, and the version it replaced.
/// </summary>
public sealed record RecordEnrichmentWrite(long AttemptId, DateTime AtUtc, Guid? RunId, string Outcome, long? Version, long? PriorVersion);

/// <summary>
/// Reads a record's enrichments out of what its ledger row keeps (osdu/docs/reference/flow/enrich.md, What the ledger keeps):
/// the render context names each enrichment and the mapping that rendered it, the target state keeps the records it added
/// and where its rows came from, the held steps say which was set aside, and the record's attempts hold every write made of
/// each record, in the steps they took. Nothing is read from OSDU, so the answer is what the ledger alone can say, as every
/// reconstruction is.
/// </summary>
public static class RecordEnrichments
{
    /// <summary>
    /// The most of a record's attempts, the newest, read for the writes made of what its enrichments added: every write of a
    /// record whose deliveries have not tried more often than this.
    /// </summary>
    public const int MaxAttempts = 1000;

    /// <summary>The marker in the id of every record an enrichment adds, before the enrichment's name: <c>-enrich.</c>.</summary>
    private const string IdMarker = "-" + EnrichRecords.SuffixPrefix;

    /// <summary>
    /// The enrichments of <paramref name="record"/>, in the order its render context lists them (the order they render), each
    /// with the records it added and the writes made of them, read from <paramref name="attempts"/> (the record's attempts, in
    /// any order). Empty for a record rendered without enrichments.
    /// </summary>
    public static IReadOnlyList<RecordEnrichment> Of(RecordState record, IReadOnlyList<AttemptRecord> attempts)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(attempts);
        var context = record.RenderContext ?? record.PendingRenderContext;
        if (string.IsNullOrWhiteSpace(context))
        {
            return [];
        }

        var steps = RenderContext.Parse(context).Steps;
        if (steps.Count == 0)
        {
            return [];
        }

        var state = JsonMerge.ToValues(record.TargetStateJson);
        var linked = EnrichParts.Records(state).ToHashSet(StringComparer.Ordinal);
        var aside = (record.HeldSteps ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var writes = WritesOf(attempts);

        var enrichments = new List<RecordEnrichment>(steps.Count);
        foreach (var step in steps)
        {
            var rules = step.Records is { } text ? JsonNode.Parse(text) as JsonObject : null;
            var adds = new List<string>(4);
            if (step.MappingReference is not null)
            {
                adds.Add("paths");
            }

            if (rules is not null)
            {
                adds.Add("records");
            }

            if (step.SuppliesBulk)
            {
                adds.Add("bulk");
            }

            if (step.RunsWorkflow)
            {
                adds.Add("workflow");
            }

            var ids = linked.Where(id => NameOf(id) == step.Name)
                .Concat(writes.Keys.Where(id => NameOf(id) == step.Name && !linked.Contains(id)))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            enrichments.Add(new RecordEnrichment
            {
                Name = step.Name,
                Adds = adds,
                Mapping = step.MappingReference ?? Text(rules, "mapping"),
                MappingFingerprint = step.MappingFingerprint ?? Text(rules, "mappingFingerprint"),
                RecordKind = Text(rules, "kind"),
                WrittenThrough = Text(rules, "through"),
                SetAside = aside.Contains(step.Name),
                Rows = Rows(state.GetValueOrDefault(EnrichTrace.RowsKey(step.Name))),
                Records = ids.Select(id =>
                {
                    var made = writes.GetValueOrDefault(id) ?? [];
                    return new RecordEnrichmentRecord(
                        id,
                        linked.Contains(id),
                        Number(state.GetValueOrDefault(EnrichTrace.VersionKey(id))) ?? made.FirstOrDefault(w => w.Version is not null)?.Version,
                        state.GetValueOrDefault(EnrichParts.StateKey(id)),
                        state.GetValueOrDefault(EnrichParts.FilesStateKey(id)),
                        state.GetValueOrDefault(EnrichTrace.SourceKey(id)),
                        made);
                }).ToList(),
            });
        }

        return enrichments;
    }

    /// <summary>
    /// The enrichment an id an enrichment made names: the word after <c>-enrich.</c> up to the next dot (<c>lasfile</c> in
    /// <c>dev:dataset--File.Generic:4f1c...-enrich.lasfile</c>); null for an id no enrichment made.
    /// </summary>
    public static string? NameOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var at = id.LastIndexOf(IdMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var name = id[(at + IdMarker.Length)..];
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// Every write the attempts made of a record an enrichment added, by its id, the newest first: each registration step an
    /// attempt took (<see cref="EnrichPartsProtocol.RegisterStep"/>) with the version it landed as, and the version its intent
    /// said it replaces. A step an attempt resumed from an earlier one is that earlier attempt's write, and is not counted again.
    /// </summary>
    private static Dictionary<string, List<RecordEnrichmentWrite>> WritesOf(IReadOnlyList<AttemptRecord> attempts)
    {
        var writes = new Dictionary<string, List<RecordEnrichmentWrite>>(StringComparer.Ordinal);
        foreach (var attempt in attempts.OrderByDescending(a => a.CompletedUtc).ThenByDescending(a => a.AttemptId))
        {
            if (Steps(attempt.ResultJson) is not { } steps)
            {
                continue;
            }

            var replaced = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var (name, _, returned) in steps)
            {
                if (name.StartsWith(EnrichPartsProtocol.IntentStepPrefix, StringComparison.Ordinal) && Text(returned, "recordId") is { } intended)
                {
                    replaced[intended] = Number(Text(returned, "priorVersion"));
                }
            }

            foreach (var (name, node, returned) in steps)
            {
                if (!IsRegistration(name) || Resumed(node) || Text(returned, "recordId") is not { } id)
                {
                    continue;
                }

                var at = DateTime.TryParse(Text(node, "startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var started)
                    ? started
                    : attempt.CompletedUtc;
                if (!writes.TryGetValue(id, out var made))
                {
                    made = [];
                    writes[id] = made;
                }

                made.Add(new RecordEnrichmentWrite(
                    attempt.AttemptId, at, attempt.RunId, attempt.Outcome.ToString().ToLowerInvariant(),
                    Number(Text(returned, "version")), replaced.GetValueOrDefault(id)));
            }
        }

        return writes;
    }

    /// <summary>Whether <paramref name="name"/> is the step a part's registration is reported under, not its intent.</summary>
    private static bool IsRegistration(string name)
        => name.StartsWith(EnrichPartsProtocol.RegisterStepPrefix, StringComparison.Ordinal)
            && !name.StartsWith(EnrichPartsProtocol.IntentStepPrefix, StringComparison.Ordinal);

    /// <summary>Whether an attempt took <paramref name="step"/> over from an earlier attempt, which made the write.</summary>
    private static bool Resumed(JsonObject step)
        => step["resumed"] is JsonValue value && value.TryGetValue<bool>(out var resumed) && resumed;

    /// <summary>
    /// The steps an attempt's result lists, each with its name, its node and what it returned; null for an attempt that kept
    /// no steps, or whose result does not read as an attempt's result.
    /// </summary>
    private static List<(string Name, JsonObject Node, JsonObject? Returned)>? Steps(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        JsonNode? result;
        try
        {
            result = JsonNode.Parse(resultJson);
        }
        catch (JsonException)
        {
            // A result another version wrote that does not read says nothing of the writes; the attempt itself stays listed on the Timeline.
            return null;
        }

        if (result?["steps"] is not JsonArray steps)
        {
            return null;
        }

        var listed = new List<(string Name, JsonObject Node, JsonObject? Returned)>(steps.Count);
        foreach (var step in steps.OfType<JsonObject>())
        {
            if (Text(step, "name") is { } name)
            {
                listed.Add((name, step, step["returned"] as JsonObject));
            }
        }

        return listed;
    }

    private static string? Text(JsonObject? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? Number(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>Where an enrichment's rows came from, as the target state keeps it (<see cref="EnrichTrace.Rows"/>); null for none kept or a value that does not read.</summary>
    private static RecordEnrichmentRows? Rows(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject node)
            {
                return null;
            }

            var files = node["files"] is JsonArray list
                ? list.Select(f => f is JsonValue v && v.TryGetValue<string>(out var file) ? file : null).OfType<string>().ToList()
                : [];
            return new RecordEnrichmentRows(
                node["rows"]?.GetValue<int>() ?? 0,
                files,
                node["filesCounted"]?.GetValue<int>() ?? files.Count,
                Text(node, "row"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // A value written by another version of the trace says nothing this one can read; the enrichment is listed without it.
            return null;
        }
    }
}
