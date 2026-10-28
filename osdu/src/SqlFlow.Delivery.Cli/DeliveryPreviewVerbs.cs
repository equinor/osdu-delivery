using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow preview &lt;flow.yaml&gt;</c>: one record rendered as a delivery would render it, and nothing sent
/// (<see cref="RecordPreviewer"/>), the same preview the flow's Preview tab shows. A source that declares interfaces is
/// previewed one interface at a time, each with its own first record, unless <c>--interface</c> names one.
/// </summary>
internal static class DeliveryPreviewVerbs
{
    public static async Task<int> PreviewAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Arguments.Positional(1) is not { } flowPath)
        {
            return context.UsageError("name the flow document whose record to preview.");
        }

        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var values = RunParameters.ParseValues(context.Arguments.GetOptions("--set"));
        var key = context.Arguments.GetOption("--key");
        var output = context.Arguments.GetOption("--out");
        // Previewed in the partition a run would take.
        var source = await CliPartitions.ResolveAsync(context, engine.Documents.LoadSource(flowPath), ct).ConfigureAwait(false);
        var named = context.Arguments.GetOption("--interface");
        var flows = named is null ? source.Interfaces : [source.Interface(named)];
        if (key is not null && flows.Count > 1)
        {
            return context.UsageError($"a key names a record of one interface; name it with --interface ({string.Join(", ", source.Names)}).");
        }

        // A file that cannot be written is said before the record is rendered, not after.
        if (output is not null)
        {
            OutputPath(output);
        }

        var previews = await PreviewsAsync(engine, flows, values, key, whole: output is not null, ct).ConfigureAwait(false);
        string? written = null;
        if (output is not null)
        {
            written = await WriteAsync(output, previews, ct).ConfigureAwait(false);
        }

        Show(context.Out, previews, context.Json, written, RecordPreviewLimits.Default.MaxDocumentChars);
        return previews.All(p => p.Found) ? 0 : 1;
    }

    /// <summary>
    /// The preview of each flow, one interface at a time. <paramref name="whole"/> keeps each document whatever its size,
    /// for a file (<see cref="RecordPreviewLimits.Whole"/>); otherwise a document over the preview's bound is left out.
    /// </summary>
    internal static async Task<IReadOnlyList<RecordPreview>> PreviewsAsync(
        EngineContext engine, IReadOnlyList<FlowDefinition> flows, IReadOnlyDictionary<string, string> values, string? key, bool whole, CancellationToken ct)
    {
        var previews = new List<RecordPreview>(flows.Count);
        foreach (var flow in flows)
        {
            using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow, values, ct).ConfigureAwait(false);
            previews.Add(await new RecordPreviewer(runtime, whole ? RecordPreviewLimits.Whole : RecordPreviewLimits.Default).PreviewAsync(key, ct).ConfigureAwait(false));
        }

        return previews;
    }

    /// <summary>
    /// The previews on the console, as JSON or as text, each document bounded by <paramref name="maxDocumentChars"/> as the
    /// GUI bounds it: a larger one is described by its size and hash, with the file it was written to whole, when it was.
    /// </summary>
    internal static void Show(TextWriter writer, IReadOnlyList<RecordPreview> previews, bool json, string? written, int maxDocumentChars)
    {
        var shown = previews.Select(p => RecordPreviewer.Bound(p, maxDocumentChars, written)).ToList();
        if (json)
        {
            writer.WriteLine(Json(shown));
            return;
        }

        foreach (var preview in shown)
        {
            WriteText(writer, preview, written);
        }
    }

    /// <summary>The previews as one JSON document: the one preview for one flow, an array for a source's interfaces.</summary>
    private static string Json(IReadOnlyList<RecordPreview> previews)
        => previews.Count == 1
            ? RecordPreviewJson.Write(previews[0], indented: true)
            : CanonicalJson.Pretty(new JsonArray(previews.Select(p => JsonNode.Parse(RecordPreviewJson.Write(p))).ToArray()));

    /// <summary>
    /// The whole preview written to a file, documents and all, however large: what the console and a node's answer to the
    /// GUI leave out for its size. Returns the file's full path.
    /// </summary>
    internal static async Task<string> WriteAsync(string output, IReadOnlyList<RecordPreview> previews, CancellationToken ct)
    {
        var path = OutputPath(output);
        try
        {
            await File.WriteAllTextAsync(path, Json(previews) + Environment.NewLine, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlFlowException($"The preview could not be written to {path}: {ex.Message}", ex);
        }

        return path;
    }

    /// <summary>The full path <c>--out</c> names, in a folder that exists.</summary>
    private static string OutputPath(string output)
    {
        string path;
        try
        {
            path = Path.GetFullPath(output);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SqlFlowException($"--out names '{output}', which is not a file path: {ex.Message}", ex);
        }

        var folder = Path.GetDirectoryName(path);
        return folder is not null && !Directory.Exists(folder)
            ? throw new SqlFlowException($"--out names {path}, in a folder that does not exist.")
            : path;
    }

    private static void WriteText(TextWriter writer, RecordPreview preview, string? written)
    {
        // The flow's label names its interface already (flow/interface), as a run names it.
        var name = preview.Flow;
        if (!preview.Found)
        {
            writer.WriteLine($"--  {name}: nothing to preview");
            writer.WriteLine($"    why         {preview.Reason}");
            return;
        }

        var source = preview.Source!;
        var decision = preview.Decision!;
        writer.WriteLine($"OK  {name}: {source.SourceKey}{(source.Label is null ? string.Empty : $" [{source.Label}]")}");
        writer.WriteLine($"    asked       {Asked(preview.Asked)}");
        foreach (var why in preview.Asked.PassedOverWhy)
        {
            writer.WriteLine($"    passed over {why}");
        }

        writer.WriteLine($"    from        {source.OriginFile ?? "no file recorded"}{(source.OriginRow is { } row ? string.Create(CultureInfo.InvariantCulture, $" row {row}") : string.Empty)}");
        writer.WriteLine($"    mapping     {preview.Inputs.Mapping} on {preview.Inputs.Kind}{(preview.Inputs.CacheVersion is { } cache ? $", cache {cache}" : string.Empty)}");
        writer.WriteLine($"    route       {preview.Route.Protocol}{(preview.Route.Ddms is { } ddms ? $": {ddms}" : string.Empty)}");
        writer.WriteLine($"    next run    {decision.Action}{(decision.SkipTier is { } tier ? $" ({tier})" : string.Empty)}: {decision.Reason}");
        if (decision.Ledger is { } ledger)
        {
            writer.WriteLine($"    ledger      {ledger.Status}{(ledger.TargetVersion is { } version ? string.Create(CultureInfo.InvariantCulture, $", OSDU version {version}") : string.Empty)}{(ledger.SameDocument is { } same ? same ? ", the document is the one it holds" : ", the document differs from the one it holds" : string.Empty)}");
        }

        var document = preview.Document;
        if (document is null)
        {
            writer.WriteLine($"    document    none: {preview.NoDocument}");
        }
        else
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    document    {document.TargetId ?? "no id"} ({document.Characters:N0} characters, hash {document.MetadataHash[..16]})"));
            foreach (var hold in document.Holds)
            {
                writer.WriteLine($"    held        {hold}");
            }

            if (document.Assertions is { } findings)
            {
                var held = findings.Holds ? "; the check before sending would hold the record, its document kept" : string.Empty;
                writer.WriteLine($"    asserted    {findings.Summary()}{held}");
                foreach (var line in FindingLines.Of(findings))
                {
                    writer.WriteLine($"                {line}");
                }
            }

            foreach (var choice in document.Choices)
            {
                var values = choice.Values == 1 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {choice.Values:N0} values");
                var unverified = choice.Unverified ? ", unverified" : string.Empty;
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    coalesce    {choice.Target} from alternative {choice.Alternative} of {choice.Of}: {choice.Origin}{values}{unverified}"));
            }

            foreach (var id in document.Unverified)
            {
                writer.WriteLine($"    unverified  {id}: the cache holds no such record");
            }

            // What each enrichment of the mapping added, in the order they render (osdu/docs/reference/flow/enrich.md).
            foreach (var step in document.Enrichment)
            {
                writer.WriteLine($"    enrich      {step.Step} ({step.Mapping ?? "its records"}): {EnrichOutcome(step)}");
                foreach (var effect in step.Effects)
                {
                    var value = effect.Value is null ? string.Empty : $" {Clip(effect.Value.ToJsonString(), 120)}";
                    writer.WriteLine($"                {effect.Kind} {effect.Path}{value}");
                }

                foreach (var added in step.Records)
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"                adds {added.Id} ({added.Kind}) from row {added.Row + 1}"));
                }

                foreach (var kept in step.KeptRecords)
                {
                    writer.WriteLine($"                keeps linking {kept}, added by its last delivery");
                }
            }
        }

        foreach (var reference in preview.References)
        {
            var holder = reference.Holder is { } h ? $"{h.Status} in the ledger as {h.SourceKey}" : "not a record of the ledger";
            writer.WriteLine($"    refers to   {reference.Id} ({reference.Property}): {holder}");
        }

        foreach (var part in preview.Payload)
        {
            var what = part.Problem ?? string.Create(CultureInfo.InvariantCulture, $"{part.TotalFiles} file(s), {part.TotalBytes:N0} bytes under {part.Location}");
            writer.WriteLine($"    payload     {part.Role ?? part.Payload}: {what}");
            foreach (var file in part.Files)
            {
                var shape = file.Parquet is { } parquet ? string.Create(CultureInfo.InvariantCulture, $", {parquet.Rows:N0} rows x {parquet.Columns} columns") : string.Empty;
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {file.Name} ({file.Size:N0} bytes{shape}){(file.FooterProblem is { } problem ? $": {problem}" : string.Empty)}"));
            }
        }

        foreach (var step in preview.Steps)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    step {step.Order,-6} {step.Service}: {step.Request}{(step.Repeats is { } repeats ? $" ({repeats})" : string.Empty)}"));
            writer.WriteLine($"                {step.What}");
        }

        foreach (var note in preview.Notes)
        {
            writer.WriteLine($"    note        {note}");
        }

        foreach (var issue in preview.Issues)
        {
            writer.WriteLine($"    issue       {issue}");
        }

        if (document is null)
        {
            return;
        }

        if (document.Omitted is { } omitted)
        {
            writer.WriteLine($"    {omitted}");
            return;
        }

        // The document as it goes out: the route's form when it adds to the rendered document, with its placeholders named.
        foreach (var placeholder in document.Placeholders)
        {
            writer.WriteLine($"    placeholder {placeholder.Path}: {placeholder.StandsFor}");
        }

        writer.WriteLine(document.Sent is null ? "    document as rendered and sent:" : "    document as the route sends it:");
        writer.WriteLine(CanonicalJson.Pretty(document.Sent ?? document.Rendered!));
        if (written is not null)
        {
            writer.WriteLine($"    written to {written}");
        }
    }

    private static string Asked(PreviewAsked asked)
        => asked.Key is null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"the scope's first record{(asked.ScopeRecords is { } total ? $" of {total:N0}" : string.Empty)}{(asked.PassedOver > 0 ? $", after {asked.PassedOver} row(s) that cannot render" : string.Empty)}")
            : $"'{asked.Key}' read as {asked.How}";

    /// <summary>What an enrichment came to for the record, as one phrase.</summary>
    private static string EnrichOutcome(PreviewEnrichStep step)
    {
        if (step.NotRendered)
        {
            return "not rendered, since an enrichment before it holds the record or waits for the platform";
        }

        if (step.SetAside)
        {
            return $"held and set aside, the record going ahead with what it added to its last delivery: {string.Join("; ", step.Holds)}";
        }

        if (step.Holds.Count > 0)
        {
            return $"holds the record: {string.Join("; ", step.Holds)}";
        }

        var rows = step.Rows is { } count ? string.Create(CultureInfo.InvariantCulture, $"{count} row(s), ") : string.Empty;
        return step.Effects.Count == 0
            ? $"{rows}adds nothing"
            : string.Create(CultureInfo.InvariantCulture, $"{rows}{step.Effects.Count} change(s)");
    }

    /// <summary><paramref name="text"/> cut to <paramref name="length"/> characters, its end marked when cut.</summary>
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";
}
