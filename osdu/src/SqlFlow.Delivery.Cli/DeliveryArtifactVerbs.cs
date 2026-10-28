using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow records artifacts</c> and <c>sqlflow records undos</c> (osdu/docs/reference/concepts/removal-and-reversal.md,
/// Unfinished deliveries and their undo): what deliveries of a record created in OSDU, each with where it stands, and the
/// artifacts unfinished deliveries left in an interface's ledger that an undo may still take, with the records that hold them.
/// Both read the ledger alone; the undo itself is the flow's <c>undo</c> operation, run as any operation is: <c>sqlflow run
/// &lt;flow.yaml&gt; --operation undo</c>.
/// </summary>
internal static class DeliveryArtifactVerbs
{
    /// <summary>Artifacts shown for one record when the command line asks for no count.</summary>
    private const int DefaultArtifacts = 100;

    /// <summary>Records with open artifacts listed when the command line asks for no count.</summary>
    private const int DefaultRecords = 50;

    /// <summary>The artifacts of one record, the newest first, each with its unit, its id or locator, its versions and where it stands.</summary>
    public static async Task<int> ArtifactsAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);
        if (context.Arguments.GetOption("--key") is not { } asked)
        {
            return context.UsageError("name the record with --key <delivery key or source key>.");
        }

        var max = Count(context.Arguments.GetOption("--max"), DefaultArtifacts, "--max");
        var (key, name) = await FindAsync(ledger, flowId, label, asked, ct).ConfigureAwait(false);
        var artifacts = await ledger.RecordArtifactsAsync(flowId, key, max, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["deliveryKey"] = key.Value.ToString("N", CultureInfo.InvariantCulture),
                ["record"] = name,
                ["artifacts"] = new JsonArray(artifacts.Select(a => (JsonNode)Described(a)).ToArray()),
            }));
            return 0;
        }

        var open = artifacts.Count(a => ArtifactStatuses.IsOpen(a.Status));
        var exhausted = artifacts.Count(Exhausted);
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {name}: {artifacts.Count} artifact(s) shown, newest first; {open} open{(exhausted > 0 ? $", {exhausted} whose undo has used its {ArtifactLimits.MaxUndoAttempts} tries" : string.Empty)}"));
        if (artifacts.Count == 0)
        {
            context.Out.WriteLine(
                "  none. A delivery records what it creates in OSDU beside the record (datasets, sessions, versions); a route that "
                + "writes the record in one call creates nothing else.");
            return 0;
        }

        foreach (var artifact in artifacts)
        {
            var versions = artifact.Version is { } version
                ? string.Create(CultureInfo.InvariantCulture, $" v{version}{(artifact.PriorVersion is { } prior ? $" (replaced v{prior})" : string.Empty)}")
                : artifact.PriorVersion is { } replaced ? string.Create(CultureInfo.InvariantCulture, $" (replaces v{replaced})") : string.Empty;
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {ArtifactStatuses.Name(artifact.Status),-10}  {artifact.Role,-9}  {artifact.Slot}  {artifact.TargetId ?? artifact.Locator ?? "(no id yet)"}{versions}"));
            if (artifact.TargetId is not null && artifact.Locator is { Length: > 0 } locator)
            {
                context.Out.WriteLine("      found by " + One(locator));
            }

            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"      unit {artifact.UnitId:N} began {artifact.UnitStartedUtc:u}; written {artifact.CreatedUtc:u}"));
            if (artifact.SettledUtc is { } settled)
            {
                context.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"      settled {settled:u} by {artifact.SettledBy ?? "(not recorded)"}{(artifact.SettledRunId is { } run ? $" in run {run:D}" : string.Empty)}"));
            }

            if (artifact.UndoAttempts > 0 && ArtifactStatuses.IsOpen(artifact.Status))
            {
                context.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"      undo tried {artifact.UndoAttempts} time(s){(Exhausted(artifact) ? "; no more tries: an undo run with force takes it" : artifact.NextUndoUtc is { } next ? $"; next try at {next:u}" : string.Empty)}"));
            }

            if (artifact.Note is { Length: > 0 } note)
            {
                context.Out.WriteLine("      " + One(note));
            }
        }

        return 0;
    }

    /// <summary>
    /// What the mapping's enrichments added to one record (osdu/docs/reference/flow/enrich.md, What the ledger keeps): each
    /// enrichment with its mapping and where its rows came from, and every record it added with whether the record links it,
    /// the version OSDU holds, the row it came from and every write made of it. Read from the ledger alone.
    /// </summary>
    public static async Task<int> EnrichmentsAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);
        if (context.Arguments.GetOption("--key") is not { } asked)
        {
            return context.UsageError("name the record with --key <delivery key or source key>.");
        }

        var (key, name) = await FindAsync(ledger, flowId, label, asked, ct).ConfigureAwait(false);
        var record = await ledger.GetRecordAsync(flowId, key, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"{label}: the ledger holds no record {key}.");
        var enrichments = RecordEnrichments.Of(record, await ledger.ListAttemptsAsync(flowId, key, RecordEnrichments.MaxAttempts, ct).ConfigureAwait(false));
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["deliveryKey"] = key.Value.ToString("N", CultureInfo.InvariantCulture),
                ["record"] = name,
                ["enrichments"] = new JsonArray(enrichments.Select(e => (JsonNode)Described(e)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {name}: {enrichments.Count} enrichment(s), in the order they render"));
        if (enrichments.Count == 0)
        {
            context.Out.WriteLine("  none. The record was rendered by a mapping that declares no enrich: block.");
            return 0;
        }

        foreach (var enrichment in enrichments)
        {
            context.Out.WriteLine(
                $"  {enrichment.Name}: adds {string.Join(", ", enrichment.Adds)}"
                + (enrichment.Mapping is { } mapping ? $" ({mapping})" : string.Empty)
                + (enrichment.RecordKind is { } kind ? $", records of {kind} written through {enrichment.WrittenThrough}" : string.Empty)
                + (enrichment.SetAside ? "; set aside on its holds on the last render" : string.Empty));
            if (enrichment.Rows is { } rows)
            {
                context.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"      {rows.Count} row(s){(rows.Row is { } row ? $" at {row}" : string.Empty)}{(rows.Files.Count > 0 ? $", from {string.Join(", ", rows.Files)}{(rows.FilesCounted > rows.Files.Count ? $" and {rows.FilesCounted - rows.Files.Count} more file(s)" : string.Empty)}" : string.Empty)}"));
            }

            foreach (var added in enrichment.Records)
            {
                context.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"      {(added.Linked ? "linked    " : "superseded")}  {added.Id}{(added.Version is { } version ? $" v{version}" : string.Empty)}{(added.Source is { } source ? $", from {source}" : string.Empty)}, {added.Writes.Count} write(s)"));
            }
        }

        return 0;
    }

    /// <summary>An enrichment as the JSON form names it.</summary>
    private static JsonObject Described(RecordEnrichment enrichment) => new()
    {
        ["name"] = enrichment.Name,
        ["adds"] = new JsonArray(enrichment.Adds.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),
        ["mapping"] = enrichment.Mapping,
        ["mappingFingerprint"] = enrichment.MappingFingerprint,
        ["recordKind"] = enrichment.RecordKind,
        ["writtenThrough"] = enrichment.WrittenThrough,
        ["setAside"] = enrichment.SetAside,
        ["rows"] = enrichment.Rows is { } rows
            ? new JsonObject
            {
                ["count"] = rows.Count,
                ["files"] = new JsonArray(rows.Files.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
                ["filesCounted"] = rows.FilesCounted,
                ["row"] = rows.Row,
            }
            : null,
        ["records"] = new JsonArray(enrichment.Records.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id,
            ["linked"] = r.Linked,
            ["version"] = r.Version,
            ["hash"] = r.Hash,
            ["filesHash"] = r.FilesHash,
            ["source"] = r.Source,
            ["writes"] = new JsonArray(r.Writes.Select(w => (JsonNode)new JsonObject
            {
                ["attemptId"] = w.AttemptId,
                ["atUtc"] = w.AtUtc,
                ["runId"] = w.RunId?.ToString("D"),
                ["outcome"] = w.Outcome,
                ["version"] = w.Version,
                ["priorVersion"] = w.PriorVersion,
            }).ToArray()),
        }).ToArray()),
    };

    /// <summary>
    /// The artifacts unfinished deliveries left in the interface's ledger that an undo may still take, counted by state, and the
    /// records that hold them, those whose undo has used its tries first.
    /// </summary>
    public static async Task<int> UndosAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);
        var max = Count(context.Arguments.GetOption("--max"), DefaultRecords, "--max");
        var counts = await ledger.ArtifactCountsAsync(flowId, ct).ConfigureAwait(false);
        var page = await ledger.OpenArtifactRecordsAsync(flowId, 0, max, ct).ConfigureAwait(false);
        IReadOnlyDictionary<DeliveryKey, RecordState> records = page.Records.Count == 0
            ? new Dictionary<DeliveryKey, RecordState>()
            : await ledger.GetRecordsAsync(flowId, page.Records.Select(r => r.Key).ToList(), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["intent"] = counts.Intent,
                ["pending"] = counts.Pending,
                ["due"] = counts.Due,
                ["failed"] = counts.Failed,
                ["failedExhausted"] = counts.FailedExhausted,
                ["toUndo"] = counts.ToUndo,
                ["totalRecords"] = page.Total,
                ["records"] = new JsonArray(page.Records.Select(r => (JsonNode)Described(r, records.GetValueOrDefault(r.Key))).ToArray()),
            }));
            return 0;
        }

        var underWay = counts.Intent + counts.Pending;
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {counts.ToUndo} artifact(s) to undo ({counts.Due} due, {counts.Failed} failed){(underWay > 0 ? $"; {underWay} more of deliveries under way or abandoned ({counts.Intent} intent, {counts.Pending} pending)" : string.Empty)}"));
        if (counts.FailedExhausted > 0)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {counts.FailedExhausted} failed {ArtifactLimits.MaxUndoAttempts} times and are no longer tried by the sweep: once what stops them is fixed, run 'sqlflow run <flow.yaml> --operation undo --payload {{\"force\":true}}'."));
        }

        if (page.Total == 0)
        {
            context.Out.WriteLine("  no record holds anything an undo may still take.");
            return 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {page.Total} record(s) hold them{(page.Records.Count < page.Total ? $", the first {page.Records.Count} shown" : string.Empty)}, those whose undo has used its tries first:"));
        foreach (var row in page.Records)
        {
            var record = records.GetValueOrDefault(row.Key);
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {row.Key.Value:N}  {record?.Status.ToString() ?? "(not in the ledger)",-10}  {record?.Label ?? record?.SourceKey ?? string.Empty}"));
            var parts = new List<string>();
            Part(parts, row.FailedExhausted, "used every try");
            Part(parts, row.Failed - row.FailedExhausted, "failed, tried again");
            Part(parts, row.Due, "due");
            Part(parts, row.Intent, "intent");
            Part(parts, row.Pending, "pending");
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"      {string.Join(", ", parts)}; the oldest written {row.OldestUtc:u}{(row.NextUndoUtc is { } next ? $"; next try at {next:u}" : string.Empty)}"));
        }

        return 0;
    }

    /// <summary>What an undo attempt's result says, one line each: why, what became of the artifacts, and each artifact's outcome.</summary>
    internal static IEnumerable<string> UndoLines(JsonObject undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        var reason = Text(undo["reason"]) ?? "unknown";
        var kept = undo["keptRecord"] is JsonValue keep && keep.TryGetValue<bool>(out var keptRecord) && keptRecord;
        yield return $"undo ({reason}{(kept ? ", the record left to the newer work" : string.Empty)}): {Text(undo["summary"]) ?? "no summary"}";
        if (undo["artifacts"] is not JsonArray artifacts)
        {
            yield break;
        }

        foreach (var artifact in artifacts.OfType<JsonObject>())
        {
            var id = Text(artifact["targetId"]) ?? Text(artifact["locator"]) ?? "(no id)";
            var note = Text(artifact["note"]);
            yield return One($"  {Text(artifact["outcome"]) ?? "?",-10}  {Text(artifact["role"]) ?? "?",-9}  {Text(artifact["slot"]) ?? "?"}  {id}{(note is null ? string.Empty : $": {note}")}");
        }
    }

    /// <summary>
    /// The record <paramref name="asked"/> names: a delivery key the ledger holds or once held (a record deleted from the ledger
    /// keeps its artifacts), or a source key that matches one record; with how the record is named on the console.
    /// </summary>
    private static async Task<(DeliveryKey Key, string Name)> FindAsync(ILedger ledger, Guid flowId, string label, string asked, CancellationToken ct)
    {
        if (Guid.TryParse(asked, CultureInfo.InvariantCulture, out var value))
        {
            var key = new DeliveryKey(value);
            if (await ledger.GetRecordAsync(flowId, key, ct).ConfigureAwait(false) is { } record)
            {
                return (key, record.Label ?? record.SourceKey);
            }

            if (await ledger.FindPurgedAsync(flowId, key, ct).ConfigureAwait(false) is { } purged)
            {
                return (key, $"{purged.Label ?? purged.SourceKey} (deleted from the ledger by {purged.PurgedBy} at {purged.PurgedUtc:u})");
            }
        }

        // An operator holds the source key far more often than the delivery key, so the source key finds it too.
        var found = await ledger.ListAsync(flowId, new RecordQuery { Search = asked, Max = 2 }, ct).ConfigureAwait(false);
        return found.Count switch
        {
            1 => (found[0].DeliveryKey, found[0].Label ?? found[0].SourceKey),
            0 => throw new FlowValidationException($"{label} has no record for '{asked}'."),
            _ => throw new FlowValidationException(
                $"'{asked}' matches {found.Count} records of {label} ({string.Join(", ", found.Select(r => r.SourceKey))}); name one by its delivery key."),
        };
    }

    private static bool Exhausted(LedgerArtifact artifact)
        => artifact.Status == ArtifactStatus.Failed && artifact.UndoAttempts >= ArtifactLimits.MaxUndoAttempts;

    private static JsonObject Described(LedgerArtifact a) => new()
    {
        ["artifactId"] = a.ArtifactId,
        ["unitId"] = a.UnitId.ToString("D"),
        ["unitStartedUtc"] = a.UnitStartedUtc,
        ["slot"] = a.Slot,
        ["role"] = a.Role,
        ["targetId"] = a.TargetId,
        ["locator"] = a.Locator,
        ["version"] = a.Version,
        ["priorVersion"] = a.PriorVersion,
        ["state"] = ArtifactStatuses.Name(a.Status),
        ["open"] = ArtifactStatuses.IsOpen(a.Status),
        ["exhausted"] = Exhausted(a),
        ["note"] = a.Note,
        ["undoAttempts"] = a.UndoAttempts,
        ["nextUndoUtc"] = a.NextUndoUtc,
        ["submissionId"] = a.SubmissionId?.ToString("D"),
        ["createdRunId"] = a.CreatedRunId?.ToString("D"),
        ["createdUtc"] = a.CreatedUtc,
        ["updatedUtc"] = a.UpdatedUtc,
        ["settledUtc"] = a.SettledUtc,
        ["settledRunId"] = a.SettledRunId?.ToString("D"),
        ["settledBy"] = a.SettledBy,
    };

    private static JsonObject Described(OpenArtifactRecord row, RecordState? record) => new()
    {
        ["deliveryKey"] = row.Key.Value.ToString("N", CultureInfo.InvariantCulture),
        ["sourceKey"] = record?.SourceKey,
        ["label"] = record?.Label,
        ["status"] = record?.Status.ToString(),
        ["targetId"] = record?.TargetId,
        ["intent"] = row.Intent,
        ["pending"] = row.Pending,
        ["due"] = row.Due,
        ["failed"] = row.Failed,
        ["failedExhausted"] = row.FailedExhausted,
        ["oldestUtc"] = row.OldestUtc,
        ["nextUndoUtc"] = row.NextUndoUtc,
    };

    private static void Part(List<string> parts, long count, string what)
    {
        if (count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {what}"));
        }
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A value as one line, so a line stays a line whatever the target wrote.</summary>
    private static string One(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 240 ? single : single[..237] + "...";
    }

    private static int Count(string? value, int fallback, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? Math.Min(count, 1000)
            : throw new FlowValidationException($"{option} '{value}' is not a whole number above zero.");
    }
}
