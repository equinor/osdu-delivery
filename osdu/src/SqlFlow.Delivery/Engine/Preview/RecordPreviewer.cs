using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Preview;

/// <summary>The bounds of a preview's answer, so a record of any size, and a payload of any number of files, answers in a bounded result.</summary>
public sealed record RecordPreviewLimits
{
    public static RecordPreviewLimits Default { get; } = new();

    /// <summary>Rows a preview of the scope's first record passes over (deleted, keyless, held by the source) before it stops.</summary>
    public int MaxPassedOver { get; init; } = 1000;

    /// <summary>Rows of each child dataset shown.</summary>
    public int MaxChildRows { get; init; } = 100;

    /// <summary>Characters of one source value shown; a longer value is cut and says how long it was.</summary>
    public int MaxCellChars { get; init; } = 4000;

    /// <summary>Files of each payload part listed; the part still counts and sizes all of them.</summary>
    public int MaxFilesListed { get; init; } = 50;

    /// <summary>Parquet footers read, across all parts.</summary>
    public int MaxFooters { get; init; } = 10;

    /// <summary>
    /// The largest parquet file whose footer is read from a store that streams forward only: such a file is copied to a
    /// temporary file to be read, which a preview does only for a small one.
    /// </summary>
    public long MaxForwardOnlyFooterBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Column names of one parquet file shown.</summary>
    public int MaxColumnNames { get; init; } = 200;

    /// <summary>References the document makes that are looked up in the ledger.</summary>
    public int MaxReferences { get; init; } = 50;

    /// <summary>
    /// The largest rendered document returned, in characters of canonical JSON; a larger one is described and left out
    /// (<see cref="RecordPreviewer.Bound"/>). <see cref="Whole"/> keeps a document of any size, for a preview written to a
    /// file rather than shown.
    /// </summary>
    public int MaxDocumentChars { get; init; } = 2_000_000;

    /// <summary>The default bounds, except that the rendered document is kept whole whatever its size.</summary>
    public static RecordPreviewLimits Whole { get; } = new() { MaxDocumentChars = int.MaxValue };
}

/// <summary>
/// Renders one record of a flow as a delivery would render it and sends nothing (<see cref="RecordPreview"/>). The row is
/// the scope's first in key order, or the one a key names (<see cref="PreviewKeys"/>). It is planned by the same planner a
/// run plans with, twice over the one row: against the ledger, for what the next run would do with it, and without the
/// ledger, which renders it as a first delivery would, so the document is there even for a record a run would skip as
/// unchanged. Neither pass writes anything: a plan reads the ledger, the source and the payload files, and asks the
/// platform's search only what the mapping's searches ask a run.
/// </summary>
public sealed class RecordPreviewer
{
    /// <summary>How many of the rows passed over are named in the answer.</summary>
    private const int PassedOverNamed = 5;

    private readonly FlowRuntime _runtime;
    private readonly RecordPreviewLimits _limits;

    public RecordPreviewer(FlowRuntime runtime, RecordPreviewLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _limits = limits ?? RecordPreviewLimits.Default;
    }

    /// <summary>
    /// Previews the record <paramref name="key"/> names, or the scope's first renderable record when it names none. A key
    /// that names no row, or a scope with no row, is an answer (<see cref="RecordPreview.Found"/> false, with the reason),
    /// not a failure; a flow that cannot render at all (a template no folder on the mapping's path holds, a cache that is not saved, a table without the columns
    /// the mapping reads) fails as a run of it would.
    /// </summary>
    public async Task<RecordPreview> PreviewAsync(string? key, CancellationToken ct = default)
    {
        var flow = _runtime.Flow;
        var resolved = _runtime.Mapping;
        var context = _runtime.Context;
        var ledger = context.Ledger;
        var inputs = Inputs(resolved);
        var route = new PreviewRoute(
            DeliveryProtocols.Name(flow.Target.Protocol),
            flow.RouteReason,
            DeliveryProtocols.ReachesDdms(flow.Target.Protocol) ? DdmsRouting.Of(flow).Explain(resolved.Mapping.Kind) : null);
        var asked = new PreviewAsked
        {
            Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            How = PreviewKeyForms.First,
            KeyColumns = flow.Source.Record.Key,
            Values = _runtime.Parameters,
        };

        PreviewKey? named = null;
        if (asked.Key is { } text)
        {
            named = await PreviewKeys.ResolveAsync(flow, resolved.Mapping, resolved.Context.DataPartition, ledger, text, ct).ConfigureAwait(false);
            asked = asked with { How = named.How, KeyParts = named.Candidates.Count == 1 ? named.Candidates[0].Values : null };
            if (named.Refusal is { } refusal)
            {
                return Missing(asked, inputs, route, refusal, []);
            }
        }

        var selection = named is null ? SourceSelection.Full() : SourceSelection.ForKeys(named.Candidates);
        var header = await _runtime.Planner.OpenAsync(flow, resolved, _runtime.Parameters, selection, gate: false, stored: null, ct: ct).ConfigureAwait(false);
        var issues = header.Issues.Select(i => i.ToString()).ToList();
        if (named is null)
        {
            asked = asked with { ScopeRecords = header.Source.EstimatedCandidates };
        }
        else if (Unreadable(named, header.Source, flow) is { } absent)
        {
            return Missing(asked, inputs, route, absent, issues);
        }

        var (row, passed, why, ambiguity) = await PickAsync(header, named, ct).ConfigureAwait(false);
        asked = asked with { PassedOver = passed, PassedOverWhy = why };
        if (row is null)
        {
            return Missing(asked, inputs, route, ambiguity ?? NothingToPreview(named, passed, header, flow), issues);
        }

        // The render pass has no ledger, so the row renders as a first delivery would: its document, what it refers to and
        // its payload files, whatever the ledger says. The run's pass is the ledger's view: what the next run would do.
        var logger = context.Loggers.CreateLogger<Planner>();
        var rendering = new Planner(_runtime.Source, context.Payloads, ledger: null, logger);
        var rendered = await OnlyEntryAsync(rendering.EntriesOfAsync(header, Once(row), 1, new PlanSummary(), ct), ct).ConfigureAwait(false);
        var decided = ledger is null
            ? rendered
            : await OnlyEntryAsync(_runtime.Planner.EntriesOfAsync(header, Once(row), 1, new PlanSummary(), ct), ct).ConfigureAwait(false);

        var payload = await PayloadAsync(header, rendered, ct).ConfigureAwait(false);
        var render = rendered.Render;
        var references = new List<PreviewReference>();
        var referenceCount = 0;
        PreviewDocument? document = null;
        RehearsedRoute? rehearsal = null;
        if (render is not null)
        {
            var read = header.References.Read(render.Document, render.TargetId);
            referenceCount = read.Count;
            foreach (var reference in read.Take(_limits.MaxReferences))
            {
                references.Add(new PreviewReference(reference.Id, reference.Property, await HolderAsync(ledger, flow.Id, reference.Id, ct).ConfigureAwait(false)));
            }

            var targetState = decided.Existing?.TargetStateJson is { } state ? JsonMerge.ToValues(state) : null;
            rehearsal = RouteRehearsal.Rehearse(flow, render.Document, render.TargetId, payload, targetState);
            document = Document(render, resolved.Mapping.Kind, rehearsal);
        }

        var preview = new RecordPreview
        {
            Flow = flow.Label,
            Interface = flow.Interface,
            FlowId = flow.Id,
            Asked = asked,
            Found = true,
            Inputs = inputs,
            Route = route,
            Source = Source(row, rendered, resolved.Mapping.Dataset.System),
            Decision = Decision(decided, render),
            Document = document,
            NoDocument = render is null ? rendered.Reason : null,
            References = references,
            ReferenceCount = referenceCount,
            Payload = payload,
            Steps = rehearsal?.Steps ?? [],
            Notes = rehearsal?.Notes ?? [],
            Issues = issues,
            PreviewedUtc = context.Time.GetUtcNow().UtcDateTime,
        };

        return Bound(preview, _limits.MaxDocumentChars);
    }

    /// <summary>
    /// <paramref name="preview"/> with its documents left out when the rendered document is over
    /// <paramref name="maxDocumentChars"/> characters of canonical JSON: the size, the hash and where to read it whole are
    /// said in their place, and everything else (the decision, the steps, the placeholders) stays. A preview within the
    /// bound, or one with no document, is returned as it is. A preview kept whole for a file (<see cref="RecordPreviewLimits.Whole"/>)
    /// is bounded this way for the screen.
    /// </summary>
    /// <param name="preview">The preview.</param>
    /// <param name="maxDocumentChars">The largest document shown.</param>
    /// <param name="writtenTo">The file the whole preview was written to, when it was; null says how to write one.</param>
    public static RecordPreview Bound(RecordPreview preview, int maxDocumentChars, string? writtenTo = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDocumentChars);
        if (preview.Document is not { Rendered: not null } document || document.Characters <= maxDocumentChars)
        {
            return preview;
        }

        var whole = writtenTo is null ? "'sqlflow preview' writes it whole with --out" : $"the whole preview is written to {writtenTo}";
        return preview with
        {
            Document = document with
            {
                Rendered = null,
                Sent = null,
                Omitted = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The rendered document is {document.Characters:N0} characters, more than the {maxDocumentChars:N0} a preview shows. Its hash is {document.MetadataHash}; {whole}."),
            },
        };
    }

    /// <summary>
    /// <paramref name="preview"/> made to fit <paramref name="maxChars"/> as <paramref name="measure"/> counts it: first the
    /// child rows go, then the documents, then the record row, each leaving a note of what went and why. What is left says
    /// everything but the bulk, so a record of any size answers with its decision, its steps and its files.
    /// </summary>
    public static RecordPreview Fit(RecordPreview preview, int maxChars, Func<RecordPreview, int> measure)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(measure);
        var size = measure(preview);
        if (size <= maxChars)
        {
            return preview;
        }

        var limit = maxChars.ToString("N0", CultureInfo.InvariantCulture);
        if (preview.Source is { Datasets.Count: > 0 } source)
        {
            preview = preview with
            {
                Source = source with
                {
                    Datasets = new Dictionary<string, SourceRowsView>(StringComparer.Ordinal),
                    Omitted = $"The child datasets' rows were left out: with them the preview is over the {limit} characters it may answer with. The record page's Source row reads them.",
                },
            };
            size = measure(preview);
        }

        if (size > maxChars && preview.Document is { Rendered: not null } document)
        {
            preview = preview with
            {
                Document = document with
                {
                    Rendered = null,
                    Sent = null,
                    Omitted = $"The document was left out: with it the preview is over the {limit} characters it may answer with. 'sqlflow preview' writes it whole with --out.",
                },
            };
            size = measure(preview);
        }

        if (size > maxChars && preview.Source is { } bare)
        {
            preview = preview with
            {
                Source = bare with
                {
                    Row = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Datasets = new Dictionary<string, SourceRowsView>(StringComparer.Ordinal),
                    Omitted = $"The record's rows were left out: with them the preview is over the {limit} characters it may answer with.",
                },
            };
            size = measure(preview);
        }

        return size <= maxChars
            ? preview
            : throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The preview of {preview.Source?.SourceKey ?? "the record"} is {size} characters without its rows and its document, over the {maxChars} it may answer with; preview it with 'sqlflow preview' and --out instead."));
    }

    /// <summary>The answer when no row is previewed: what was asked, the flow's render inputs and route, and why.</summary>
    private RecordPreview Missing(PreviewAsked asked, PreviewInputs inputs, PreviewRoute route, string reason, IReadOnlyList<string> issues)
        => new()
        {
            Flow = _runtime.Flow.Label,
            Interface = _runtime.Flow.Interface,
            FlowId = _runtime.Flow.Id,
            Asked = asked,
            Found = false,
            Reason = reason,
            Inputs = inputs,
            Route = route,
            Issues = issues,
            PreviewedUtc = _runtime.Context.Time.GetUtcNow().UtcDateTime,
        };

    private static PreviewInputs Inputs(ResolvedMapping resolved)
        => new(
            resolved.Mapping.Reference,
            resolved.Mapping.Kind,
            resolved.Mapping.Template.Version,
            resolved.Context.CacheScope,
            resolved.Context.CacheScope is null ? null : resolved.References.Version,
            resolved.Context.Hash()[..16]);

    /// <summary>Why none of the rows a key reads as can be previewed: none is in the table, or every one is outside the scope.</summary>
    private string? Unreadable(PreviewKey named, SourceHeader source, FlowDefinition flow)
    {
        var present = named.Candidates.Where(k => !source.MissingKeys.Contains(k)).ToList();
        if (present.Count == 0)
        {
            return named.Candidates.Count == 1
                ? $"The record table {flow.Source.Record.Object} holds no row with the key {Describe(named.Candidates[0])} ({string.Join(", ", flow.Source.Record.Key)})."
                : $"The record table {flow.Source.Record.Object} holds no row for any of the {named.Candidates.Count} ways the key splits into {string.Join(", ", flow.Source.Record.Key)}: {string.Join("; ", named.Candidates.Select(Describe))}.";
        }

        return present.All(source.OutOfScopeKeys.Contains)
            ? $"The row with the key {Describe(present[0])} is outside the scope the parameter values name ({Planner.ScopeKey(_runtime.Parameters)}); preview it with the values of its own scope."
            : null;
    }

    private static string Describe(KeyTuple key) => key.Values.Count == 1 ? $"'{key.Values[0]}'" : key.Json;

    /// <summary>
    /// The row to preview. A key's row is taken as it is, whatever it holds; two rows a key reads as are ambiguous. Without a
    /// key the first row of the scope that can render is taken, passing over rows that cannot: a row the ingestion table marked
    /// deleted, one whose key is incomplete, one the source holds. Passing over stops at <see cref="RecordPreviewLimits.MaxPassedOver"/>.
    /// </summary>
    private async Task<(SourceRecord? Row, int Passed, IReadOnlyList<string> Why, string? Ambiguity)> PickAsync(PlanHeader header, PreviewKey? named, CancellationToken ct)
    {
        var renderer = header.Mapping.Renderer;
        var system = header.Mapping.Mapping.Dataset.System;
        var passed = 0;
        var why = new List<string>();
        var matched = new List<SourceRecord>(2);
        await foreach (var record in _runtime.Source.ReadAsync(header.Source, null, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (named is not null)
            {
                matched.Add(record);
                if (matched.Count > 1)
                {
                    break;
                }

                continue;
            }

            var key = renderer.DeriveKey(record.Row, out var values);
            var display = SourceKey.Display(system, values);
            var unfit = record.DeletedUtc is not null
                ? $"{display}: the ingestion table marked the row deleted"
                : key is null
                    ? $"{display}: a key part is empty"
                    : record.Hold is { } hold ? $"{display}: {hold}" : null;
            if (unfit is null)
            {
                return (record, passed, why, null);
            }

            passed++;
            if (why.Count < PassedOverNamed)
            {
                why.Add(unfit);
            }

            if (passed >= _limits.MaxPassedOver)
            {
                break;
            }
        }

        return matched.Count switch
        {
            0 => (null, passed, why, null),
            1 => (matched[0], 0, [], null),
            _ => (null, 0, [], $"The key reads as more than one row of the record table ({string.Join("; ", matched.Select(m => m.SourceKeyJson ?? "?"))}); name one by its key parts as a JSON array."),
        };
    }

    private string NothingToPreview(PreviewKey? named, int passed, PlanHeader header, FlowDefinition flow)
    {
        if (named is not null)
        {
            return "The row was in the record table when the read opened and gone when it was read: it was removed while the preview ran.";
        }

        var scope = header.Parameters.Count == 0 ? string.Empty : $" for {Planner.ScopeKey(header.Parameters)}";
        if (passed == 0)
        {
            return $"The record table {flow.Source.Record.Object} holds no row in the flow's scope{scope}.";
        }

        return passed >= _limits.MaxPassedOver
            ? string.Create(CultureInfo.InvariantCulture, $"The first {passed} rows of {flow.Source.Record.Object}{scope} cannot render (deleted, keyless or held by the source); name a record by its key.")
            : string.Create(CultureInfo.InvariantCulture, $"Every row of {flow.Source.Record.Object}{scope} ({passed}) is deleted, keyless or held by the source, so none renders.");
    }

    private static async IAsyncEnumerable<SourceRecord> Once(SourceRecord record)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return record;
    }

    /// <summary>The one entry a plan of one row yields.</summary>
    private static async Task<PlanEntry> OnlyEntryAsync(IAsyncEnumerable<PlanEntry> entries, CancellationToken ct)
    {
        PlanEntry? only = null;
        await foreach (var entry in entries.WithCancellation(ct).ConfigureAwait(false))
        {
            only ??= entry;
        }

        return only ?? throw new DeliveryException("The plan of the previewed row gave no entry for it; a plan of one row always gives one.");
    }

    /// <summary>
    /// The record delivered to <paramref name="id"/>, when a ledger of the previewed flow's partition holds one: an id is
    /// referred to within its partition.
    /// </summary>
    private static async Task<PreviewHolder?> HolderAsync(ILedger? ledger, Guid flowId, string id, CancellationToken ct)
    {
        if (ledger is null)
        {
            return null;
        }

        var holders = await ledger.ListHoldersAsync(flowId, id, 1, ct).ConfigureAwait(false);
        return holders.Count == 0
            ? null
            : new PreviewHolder(holders[0].FlowId, holders[0].DeliveryKey.Value, holders[0].SourceKey, holders[0].Label, holders[0].Status.ToString().ToLowerInvariant());
    }

    private PreviewSource Source(SourceRecord row, PlanEntry rendered, string system)
    {
        var key = _runtime.Mapping.Renderer.DeriveKey(row.Row, out var values);
        return new PreviewSource
        {
            SourceKey = SourceKey.Display(system, values),
            KeyParts = values,
            DeliveryKey = key?.Value,
            Label = rendered.Label,
            Identities = rendered.Identities,
            OriginFile = row.Origin.FileName,
            OriginRow = row.Origin.RowNumber,
            OriginUpdatedUtc = row.Origin.UpdatedUtc,
            Fingerprint = row.Version.Fingerprint,
            DeletedUtc = row.DeletedUtc,
            Hold = row.Hold,
            Row = SourceRowView.Columns(row.Row, _limits.MaxCellChars),
            Datasets = SourceRowView.Datasets(row, _limits.MaxChildRows, _limits.MaxCellChars),
        };
    }

    private static PreviewDecision Decision(PlanEntry decided, RenderResult? render)
        => new()
        {
            Action = decided.Action switch
            {
                PlannedAction.Create => "create",
                PlannedAction.UpdateMetadata => "updateMetadata",
                PlannedAction.UpdatePayload => "updatePayload",
                PlannedAction.UpdateBoth => "updateBoth",
                PlannedAction.Skip => "skip",
                PlannedAction.Hold => "hold",
                PlannedAction.Blocked => "blocked",
                _ => decided.Action.ToString(),
            },
            SkipTier = decided.Action == PlannedAction.Skip
                ? decided.SkipTier switch
                {
                    SkipTier.Fingerprint => "fingerprint",
                    SkipTier.ContentHash => "contentHash",
                    SkipTier.Approval => "approval",
                    SkipTier.Stale => "stale",
                    _ => decided.SkipTier.ToString(),
                }
                : null,
            Reason = decided.Reason,
            DeliverMetadata = decided.DeliverMetadata,
            DeliverPayload = decided.DeliverPayload,
            Ledger = decided.Existing is { } state
                ? new PreviewLedgerRecord
                {
                    Status = state.Status.ToString().ToLowerInvariant(),
                    Blocked = state.Blocked,
                    TargetId = state.TargetId,
                    TargetVersion = state.TargetVersion,
                    LastDeliveredUtc = state.LastDeliveredUtc,
                    LastError = state.LastError is null ? null : HeaderRedaction.RedactMessage(state.LastError),
                    MetadataHash = state.MetadataHash,
                    SameDocument = state.MetadataHash is null || render is null ? null : string.Equals(state.MetadataHash, render.MetadataHash, StringComparison.Ordinal),
                }
                : null,
        };

    /// <summary>The record's document whole, as rendered and as the route sends it; <see cref="Bound"/> decides what is shown of it.</summary>
    private static PreviewDocument Document(RenderResult render, string kind, RehearsedRoute rehearsal)
        => new()
        {
            TargetId = render.TargetId,
            Kind = kind,
            MetadataHash = render.MetadataHash,
            Characters = render.Canonical.Length,
            Held = render.IsHeld,
            Holds = render.Holds,
            Rendered = render.Document,
            Sent = rehearsal.Sent,
            Placeholders = rehearsal.Placeholders,
            Searches = render.SearchUsages
                .Select(s => new PreviewSearch(s.Kind, s.Field, s.Value, s.Outcome.ToString().ToLowerInvariant(), s.Id))
                .ToList(),
            CacheValues = render.CacheUsages.Count,
            Choices = render.Choices
                .Select(c => new PreviewChoice(c.Target, c.Alternative, c.Of, c.Origin, c.Values, c.Unverified))
                .ToList(),
            Unverified = render.CacheUsages
                .Where(u => u.Kind == SqlFlow.Delivery.Snapshots.CacheUsageKind.Unverified)
                .Select(u => u.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            Assertions = render.Assertions,
        };

    /// <summary>
    /// The files each payload part of the record holds, as the render pass resolved where they are: listed, bounded, and for
    /// parquet, the shape each footer declares. A part whose files cannot be listed says why rather than failing the preview.
    /// </summary>
    private async Task<IReadOnlyList<PreviewPayloadPart>> PayloadAsync(PlanHeader header, PlanEntry rendered, CancellationToken ct)
    {
        if (rendered.PayloadLocation is not { } stored)
        {
            return [];
        }

        var footers = new FooterBudget(_limits.MaxFooters);
        if (header.Parts is not null)
        {
            CompositePayload composite;
            try
            {
                composite = CompositePayload.Decode(stored);
            }
            catch (DeliveryException ex)
            {
                return [new PreviewPayloadPart { Payload = PayloadParts.Files, Problem = ex.Message }];
            }

            var parts = new List<PreviewPayloadPart>(composite.Parts.Count);
            foreach (var part in composite.Parts)
            {
                parts.Add(part.Location is { } location
                    ? await ListAsync(part.Role, part.Payload, PayloadLocation.Parse(location), footers, ct).ConfigureAwait(false)
                    : new PreviewPayloadPart { Role = part.Role, Payload = part.Payload, Problem = "an optional part the row names no files for" });
            }

            return parts;
        }

        return header.PayloadName is { } name
            ? [await ListAsync(null, name, PayloadLocation.Parse(stored), footers, ct).ConfigureAwait(false)]
            : [];
    }

    private async Task<PreviewPayloadPart> ListAsync(string? role, string payload, PayloadLocation location, FooterBudget footers, CancellationToken ct)
    {
        IReadOnlyList<PayloadFile> files;
        try
        {
            files = await _runtime.Context.Payloads.ListAsync(location.Folder, location.Pattern, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A listing that fails is a finding about the payload, not a reason to lose the rest of the preview.
            return new PreviewPayloadPart { Role = role, Payload = payload, Location = location.ToString(), Problem = $"the files could not be listed: {HeaderRedaction.RedactMessage(ex.Message)}" };
        }

        var shown = new List<PreviewFile>(Math.Min(files.Count, _limits.MaxFilesListed));
        foreach (var file in files.Take(_limits.MaxFilesListed))
        {
            shown.Add(await DescribeAsync(file, footers, ct).ConfigureAwait(false));
        }

        return new PreviewPayloadPart
        {
            Role = role,
            Payload = payload,
            Location = location.ToString(),
            Files = shown,
            TotalFiles = files.Count,
            TotalBytes = files.Sum(f => f.Size),
            Truncated = files.Count > shown.Count,
        };
    }

    private async Task<PreviewFile> DescribeAsync(PayloadFile file, FooterBudget footers, CancellationToken ct)
    {
        var described = new PreviewFile { Name = FileUploads.FileName(file.Path), Size = file.Size, ModifiedUtc = file.Modified };
        if (!file.Path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            return described;
        }

        if (!footers.Take())
        {
            return described with { FooterProblem = string.Create(CultureInfo.InvariantCulture, $"not read: a preview reads the footers of the first {_limits.MaxFooters} parquet files") };
        }

        try
        {
            var opened = await _runtime.Context.Payloads.OpenAsync(file, ct).ConfigureAwait(false);
            await using (opened.ConfigureAwait(false))
            {
                if (!opened.CanSeek && file.Size > _limits.MaxForwardOnlyFooterBytes)
                {
                    return described with
                    {
                        FooterProblem = string.Create(CultureInfo.InvariantCulture, $"not read: the store streams the file forward only, and reading its footer would copy all {file.Size:N0} bytes to disk"),
                    };
                }

                var seekable = await ParquetFiles.EnsureSeekableAsync(opened, ct).ConfigureAwait(false);
                await using (seekable.ConfigureAwait(false))
                {
                    var shape = await ParquetFiles.ReadShapeAsync(seekable, ct).ConfigureAwait(false);
                    return described with
                    {
                        Parquet = new PreviewParquet(shape.Rows, shape.Columns, shape.ColumnNames.Take(_limits.MaxColumnNames).ToList(), shape.ColumnNames.Count > _limits.MaxColumnNames),
                        FooterProblem = shape.PandasDefect is { } defect ? $"a dataframe reader would refuse it, so a bulk service would too: {defect}" : null,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A footer that does not read is what the delivery would hold the record for; the preview says so and goes on.
            return described with { FooterProblem = $"the footer could not be read: {HeaderRedaction.RedactMessage(ex.Message)}" };
        }
    }

    /// <summary>How many more parquet footers the preview may read.</summary>
    private sealed class FooterBudget(int left)
    {
        private int _left = left;

        public bool Take()
        {
            if (_left <= 0)
            {
                return false;
            }

            _left--;
            return true;
        }
    }
}
