using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow values &lt;flow.yaml&gt;</c>: the rows of a flow's scope that will not give the variables of its mapping the
/// values the template expects (<see cref="ValueChecker"/>), the check the mapping page's Check values runs. Every row is
/// rendered as a delivery renders it and every value held to the template's rules, and nothing is written. <c>--rows</c>
/// writes every failing row to a CSV file as the check meets it, however many there are; the answer itself names the first
/// few of each finding.
/// </summary>
internal static class DeliveryValueCheckVerbs
{
    /// <summary>The columns of the <c>--rows</c> file, one line per row (or item) a variable is not written with a value the template accepts.</summary>
    internal static readonly string[] RowColumns =
        ["flow", "variable", "at", "outcome", "rule", "reason", "value", "source_key", "label", "delivery_key", "file", "row", "item"];

    public static async Task<int> ValuesAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Arguments.Positional(1) is not { } flowPath)
        {
            return context.UsageError("name the flow document whose values to check.");
        }

        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var values = RunParameters.ParseValues(context.Arguments.GetOptions("--set"));
        var targets = context.Arguments.GetOptions("--target").Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (Whole(context, "--max-rows", ValueCheckLimits.DefaultRows) is not { } maxRows
            || Whole(context, "--samples", ValueCheckLimits.DefaultSamples) is not { } samples
            || Whole(context, "--skip", 0) is not { } skip)
        {
            return context.UsageError("--max-rows, --samples and --skip each take a whole number from 0 (--max-rows 0 reads the whole scope).");
        }

        var source = await CliPartitions.ResolveAsync(context, engine.Documents.LoadSource(flowPath), ct).ConfigureAwait(false);
        var named = context.Arguments.GetOption("--interface");
        var flows = named is null ? source.Interfaces : [source.Interface(named)];
        if (targets.Count > 0 && flows.Count > 1)
        {
            return context.UsageError($"a variable is one of an interface's mapping; name the interface with --interface ({string.Join(", ", source.Names)}).");
        }

        var request = new ValueCheckRequest
        {
            Targets = targets,
            MaxRows = maxRows,
            Samples = (int)Math.Min(samples, int.MaxValue),
            SkipSamples = skip,
        };

        var rowsPath = context.Arguments.GetOption("--rows");
        await using var rows = rowsPath is null ? null : Open(rowsPath);
        if (rows is not null)
        {
            await rows.WriteLineAsync(string.Join(',', RowColumns)).ConfigureAwait(false);
        }

        var checks = await ChecksAsync(engine, flows, values, request, rows, ct).ConfigureAwait(false);
        if (context.Arguments.GetOption("--out") is { } output)
        {
            await WriteAsync(output, checks, ct).ConfigureAwait(false);
        }

        if (context.Json)
        {
            context.Out.WriteLine(Json(checks));
        }
        else
        {
            foreach (var check in checks)
            {
                WriteText(context.Out, check);
            }

            if (rowsPath is not null)
            {
                context.Out.WriteLine($"Every failing row is in {Path.GetFullPath(rowsPath)}.");
            }
        }

        // A row that holds its record, writes a value the template does not accept or fails an assertion that holds it fails
        // the check; a variable an optional entry leaves out, and an assertion that only reports or leaves a value out, are the
        // mapping's to decide, and are reported without failing it.
        return checks.All(c => c.Rows.WithHeld == 0 && c.Rows.WithInvalid == 0 && c.Rows.Keyless == 0 && c.Rows.HeldByAssertion == 0) ? 0 : 1;
    }

    /// <summary>
    /// The check of each flow, one interface at a time, every failing row written to <paramref name="rows"/> (the
    /// <c>--rows</c> file, its header already written) as the check meets it, under the flow's label: the flow, and its
    /// interface when it has one (<c>flow/interface</c>), as a run names it.
    /// </summary>
    internal static async Task<IReadOnlyList<ValueCheck>> ChecksAsync(
        EngineContext engine, IReadOnlyList<FlowDefinition> flows, IReadOnlyDictionary<string, string> values, ValueCheckRequest request, TextWriter? rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(flows);
        var checks = new List<ValueCheck>(flows.Count);
        foreach (var flow in flows)
        {
            using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow, values, ct).ConfigureAwait(false);
            checks.Add(await new ValueChecker(runtime).CheckAsync(request, rows is null ? null : occurrence => rows.WriteLine(Line(flow.Label, occurrence)), ct).ConfigureAwait(false));
        }

        return checks;
    }

    /// <summary>One line of the <c>--rows</c> file.</summary>
    internal static string Line(string flow, ValueCheckOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var record = occurrence.Record;
        return string.Join(',', new[]
        {
            flow, occurrence.Target, occurrence.At, occurrence.Outcome, occurrence.Rule, occurrence.Message, occurrence.Value,
            record.SourceKey, record.Label, record.DeliveryKey?.ToString("D"), record.File,
            record.Row?.ToString(CultureInfo.InvariantCulture), record.Item?.ToString(CultureInfo.InvariantCulture),
        }.Select(Cell));
    }

    /// <summary>A CSV cell (RFC 4180): quoted when it holds a separator, a quote or a line break, with its quotes doubled.</summary>
    internal static string Cell(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>A whole number option from 0, its fallback when it is not given, or null when it is given and is not one.</summary>
    private static long? Whole(CliVerbContext context, string option, long fallback)
    {
        if (context.Arguments.GetOption(option) is not { } text)
        {
            return fallback;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static StreamWriter Open(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (folder is not null && !Directory.Exists(folder))
        {
            throw new SqlFlowException($"--rows names {full}, in a folder that does not exist.");
        }

        try
        {
            return new StreamWriter(full, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlFlowException($"The failing rows could not be written to {full}: {ex.Message}", ex);
        }
    }

    private static string Json(IReadOnlyList<ValueCheck> checks)
        => checks.Count == 1
            ? JsonSerializer.Serialize(checks[0], RecordPreviewJson.Indented)
            : JsonSerializer.Serialize(checks, RecordPreviewJson.Indented);

    /// <summary>The whole check written to a file, as the JSON the node answers the GUI with.</summary>
    private static async Task WriteAsync(string output, IReadOnlyList<ValueCheck> checks, CancellationToken ct)
    {
        var path = Path.GetFullPath(output);
        var folder = Path.GetDirectoryName(path);
        if (folder is not null && !Directory.Exists(folder))
        {
            throw new SqlFlowException($"--out names {path}, in a folder that does not exist.");
        }

        try
        {
            await File.WriteAllTextAsync(path, Json(checks) + Environment.NewLine, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlFlowException($"The check could not be written to {path}: {ex.Message}", ex);
        }
    }

    internal static void WriteText(TextWriter writer, ValueCheck check)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(check);

        // The check's flow is the flow's label, which names its interface already (flow/interface).
        var name = check.Flow;
        var rows = check.Rows;
        var failing = rows.WithHeld + rows.WithInvalid + rows.HeldByAssertion;
        writer.WriteLine($"{(failing == 0 && rows.Keyless == 0 ? "OK " : "!! ")} {name}: {check.Inputs.Mapping} on {check.Inputs.Kind}{(check.Inputs.CacheVersion is { } cache ? $", cache {cache}" : string.Empty)}");
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    rows        {rows.Checked:N0} checked of {rows.Read:N0} read{(rows.Complete ? ", the whole scope" : $" (the scope holds about {rows.ScopeRecords:N0})")}"));
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    outcome     {rows.Clean:N0} clean, {rows.WithHeld:N0} held, {rows.WithInvalid:N0} with an invalid value, {rows.WithEmpty:N0} leaving a variable out"));
        if (rows.WithFailedAssertion > 0)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    asserted    {rows.WithFailedAssertion:N0} failing an assertion of the mapping, {rows.HeldByAssertion:N0} of them to be held before they are sent"));
        }
        if (rows.Keyless > 0)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    keyless     {rows.Keyless:N0} rows have an empty key part, so their records cannot be tracked"));
        }

        foreach (var passed in rows.PassedOverWhy)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    passed over {passed.Count:N0}: {passed.Reason}"));
        }

        foreach (var variable in check.Variables)
        {
            var counts = variable.Rows;
            var failed = counts.Held + counts.Invalid + counts.Empty + counts.Asserted;
            if (failed == 0)
            {
                continue;
            }

            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {variable.Target}: {counts.Held:N0} held, {counts.Invalid:N0} invalid, {counts.Empty:N0} empty, {counts.Valid:N0} valid, {counts.NotApplicable:N0} not applicable")
                + (counts.Asserted > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {counts.Asserted:N0} failing an assertion") : string.Empty));
            foreach (var finding in variable.Findings.Where(f => f.Outcome != "notApplicable"))
            {
                // What is wrong leads, in the words the page and the workbook use; the render's own reason follows it.
                var said = finding.Explanation ?? ValueCheckExplainer.Explain(finding, variable, check.Inputs);
                var at = finding.At == variable.Target ? string.Empty : $" at {finding.At}";
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {finding.Outcome,-8} {finding.Count,8:N0}{at}  {said.Headline}"));
                writer.WriteLine($"               effect  {said.Effect}");
                if (said.Detail is { } detail)
                {
                    writer.WriteLine($"               where   {detail}");
                }

                writer.WriteLine($"               reason  {finding.Message}");
                if (finding.Values.Count > 0)
                {
                    writer.WriteLine($"               values  {string.Join(", ", finding.Values.Take(5).Select(v => string.Create(CultureInfo.InvariantCulture, $"'{v.Value}' x{v.Count:N0}")))}");
                }

                if (finding.SourceValues.Count > 0)
                {
                    writer.WriteLine($"               source  {string.Join(", ", finding.SourceValues.Take(5).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Column} '{v.Value}' x{v.Count:N0}")))}");
                }

                for (var i = 0; i < said.Fixes.Count; i++)
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"               {(i == 0 ? "to fix" : string.Empty),-7} {i + 1}. {said.Fixes[i]}"));
                }

                foreach (var sample in finding.Samples.Take(3))
                {
                    var where = sample.File is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({sample.File}{(sample.Row is { } row ? $" row {row}" : string.Empty)})");
                    var item = sample.Item is { } number ? string.Create(CultureInfo.InvariantCulture, $", item {number}") : string.Empty;
                    writer.WriteLine($"               e.g.    {sample.SourceKey}{item}{where}");
                }
            }

            if (variable.Unlisted > 0)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {variable.Unlisted:N0} more occurrences under reasons not listed"));
            }
        }

        foreach (var note in check.Notes)
        {
            writer.WriteLine($"    note        {note}");
        }

        foreach (var issue in check.Issues)
        {
            writer.WriteLine($"    issue       {issue}");
        }
    }
}
