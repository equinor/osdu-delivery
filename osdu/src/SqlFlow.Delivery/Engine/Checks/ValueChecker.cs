using System.Runtime.ExceptionServices;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>
/// Finds the rows of a flow's scope that will not give a variable the value its template expects (<see cref="ValueCheck"/>):
/// each row is inspected as a delivery renders it (<see cref="MappingRenderer.Inspect"/>), every value written is held to
/// the rules its template states (<see cref="TemplateValueRules"/>), and each variable's outcomes are added up, the rows
/// that fail grouped by reason with the values that caused it and example records. The rows are read by the planner's own
/// read of the ingestion tables, in the order a run reads them; the cache is the version the flow renders with; and a
/// search is asked of the platform in rounds, as a run asks it. Nothing is written: not the ledger, not the work location,
/// not OSDU.
/// </summary>
/// <remarks>
/// A check of any size answers in bounded memory: counts are exact, and what is listed is capped by
/// <see cref="ValueCheckLimits"/>. A caller that must name every failing row gives <c>each</c>, which meets every one of
/// them in the order the scope is read.
/// </remarks>
public sealed partial class ValueChecker
{
    private readonly FlowRuntime _runtime;
    private readonly ValueCheckLimits _limits;

    public ValueChecker(FlowRuntime runtime, ValueCheckLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _limits = limits ?? ValueCheckLimits.Default;
    }

    /// <summary>
    /// Checks the variables <paramref name="request"/> names over the rows of the flow's scope. A mapping that fails its
    /// preflight, or a variable no entry of the mapping reaches, fails the check as a run of it would fail.
    /// </summary>
    /// <param name="request">What to check.</param>
    /// <param name="each">Given every occurrence of a finding, in the order the scope is read; null when none is needed.</param>
    /// <param name="ct">Cancellation; a check stops between batches of rows.</param>
    public async Task<ValueCheck> CheckAsync(ValueCheckRequest request, Action<ValueCheckOccurrence>? each = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flow = _runtime.Flow;
        var resolved = _runtime.Mapping;
        var mapping = resolved.Mapping;
        var started = _runtime.Context.Time.GetUtcNow().UtcDateTime;
        if (request.Mapping is { } expected && !string.Equals(expected.Trim(), mapping.Reference, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"Flow '{flow.Label}' renders with mapping {mapping.Reference}, not {expected.Trim()}: the catalog and the repository have drifted. Sync the repository and check again.");
        }

        if (request.MaxRows < 0)
        {
            throw new DeliveryException($"A check reads a number of rows, or 0 for the whole scope; {request.MaxRows} is neither.");
        }

        if (request.Samples < 0 || request.Samples > _limits.MaxSamples)
        {
            throw new DeliveryException($"A finding names between 0 and {_limits.MaxSamples} example records; {request.Samples} is outside that.");
        }

        if (request.SkipSamples < 0)
        {
            throw new DeliveryException($"The example records a finding passes over are a count from 0; {request.SkipSamples} is not one.");
        }

        var selection = Select(request.Targets, mapping, resolved.Schema);
        var header = await _runtime.Planner.OpenAsync(flow, resolved, _runtime.Parameters, SourceSelection.Full(), gate: false, stored: null, ct: ct).ConfigureAwait(false);
        var tally = new ValueTally(mapping, resolved.Schema, selection, request, _limits, each);
        var parallelism = flow.Parallelism.EffectiveRenderers;
        var rounds = Planner.SearchRounds(mapping);
        var batch = new List<SourceRecord>(_limits.Batch);
        var read = 0L;
        var complete = true;
        await foreach (var record in _runtime.Source.ReadAsync(header.Source, null, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (request.MaxRows > 0 && read == request.MaxRows)
            {
                // One row past the rows asked for says the scope holds more than were read.
                complete = false;
                break;
            }

            read++;
            if (Unfit(record) is { } reason)
            {
                tally.PassOver(reason, record, resolved.Renderer);
                continue;
            }

            batch.Add(record);
            if (batch.Count == _limits.Batch)
            {
                await InspectAsync(batch, resolved.Renderer, selection, parallelism, rounds, tally, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await InspectAsync(batch, resolved.Renderer, selection, parallelism, rounds, tally, ct).ConfigureAwait(false);
        }

        return new ValueCheck
        {
            Flow = flow.Label,
            Interface = flow.Interface,
            FlowId = flow.Id,
            Inputs = new ValueCheckInputs(
                mapping.Reference,
                mapping.Kind,
                mapping.Template.Version,
                resolved.Context.CacheScope,
                resolved.Context.CacheScope is null ? null : resolved.References.Version),
            Asked = new ValueCheckAsked
            {
                Targets = selection.Paths,
                MaxRows = request.MaxRows,
                Samples = request.Samples,
                SkipSamples = request.SkipSamples,
                Values = _runtime.Parameters,
            },
            Rows = tally.Rows(header.Source.EstimatedCandidates, read, complete),
            Variables = tally.Variables(),
            Issues = header.Issues.Select(i => i.ToString()).ToList(),
            StartedUtc = started,
            CheckedUtc = _runtime.Context.Time.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>
    /// <paramref name="check"/> made to fit <paramref name="maxChars"/> as <paramref name="measure"/> counts it. Only what is
    /// listed goes, never a count: first the example records of each finding, halved until one is left and then dropped,
    /// then the values each finding and variable lists beyond the three most frequent, then the findings of each variable
    /// beyond its ten most frequent, whose occurrences are then counted as unlisted. Each step leaves a note saying what
    /// went, and a check of fewer variables or rows answers with more.
    /// </summary>
    public static ValueCheck Fit(ValueCheck check, int maxChars, Func<ValueCheck, int> measure)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(measure);
        var size = measure(check);
        if (size <= maxChars)
        {
            return check;
        }

        // Each step is measured with the note it leaves, so what is returned fits as it is written.
        var limit = maxChars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var most = check.Variables.SelectMany(v => v.Findings).Select(f => f.Samples.Count).Append(check.Rows.KeylessSamples.Count).Max();
        for (var kept = most / 2; ; kept /= 2)
        {
            var trimmed = Map(check, finding => finding with { Samples = finding.Samples.Take(kept).ToList() }, variable => variable) with
            {
                Rows = check.Rows with { KeylessSamples = check.Rows.KeylessSamples.Take(kept).ToList() },
                Notes = [.. check.Notes, kept == 0
                    ? $"The example records were left out: with them the answer is over the {limit} characters a check may answer with. Check fewer variables to see them."
                    : $"Each finding names its first {kept} example records, not the {most} asked for: with more the answer is over the {limit} characters a check may answer with."],
            };
            size = measure(trimmed);
            if (size <= maxChars || kept == 0)
            {
                check = trimmed;
                break;
            }
        }

        if (size > maxChars)
        {
            check = Map(
                check,
                finding => finding with { Values = finding.Values.Take(3).ToList(), OtherValues = finding.OtherValues + finding.Values.Skip(3).Sum(v => v.Count) },
                variable => variable with { Values = variable.Values.Take(3).ToList() });
            check = check with { Notes = [.. check.Notes, "Each finding and each variable lists its three most frequent values: with more the answer is too large."] };
            size = measure(check);
        }

        if (size > maxChars)
        {
            check = check with
            {
                Variables = check.Variables
                    .Select(v => v with { Findings = v.Findings.Take(10).ToList(), Unlisted = v.Unlisted + v.Findings.Skip(10).Sum(f => f.Count) })
                    .ToList(),
                Notes = [.. check.Notes, "Each variable lists its ten most frequent findings; the occurrences of the rest are counted as unlisted."],
            };
            size = measure(check);
        }

        return size <= maxChars
            ? check
            : throw new DeliveryException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The check of {check.Variables.Count} variables is {size:N0} characters with every listing cut down, over the {limit} it may answer with; check fewer variables at a time."));
    }

    private static ValueCheck Map(ValueCheck check, Func<ValueCheckFinding, ValueCheckFinding> finding, Func<ValueCheckVariable, ValueCheckVariable> variable)
        => check with { Variables = check.Variables.Select(v => variable(v with { Findings = v.Findings.Select(finding).ToList() })).ToList() };

    /// <summary>
    /// The entries a check evaluates: every entry, or those reaching the variables named. A name that is not a template
    /// path, or that no entry reaches (and that is no variable of <c>data</c> the schema requires), is refused, naming it:
    /// a check of it would find nothing to say.
    /// </summary>
    internal static EntrySelection Select(IReadOnlyList<string> targets, MappingDefinition mapping, SchemaSnapshot schema)
    {
        var paths = new List<TemplatePath>(targets.Count);
        foreach (var text in targets)
        {
            if (!TemplatePath.TryParse(text, out var path, out var error))
            {
                throw new DeliveryException($"'{text}' is not a variable a check can name: {error}.");
            }

            var one = EntrySelection.Of([path!]);
            var required = schema.RequiredAt("data").Select(name => $"{TemplatePath.Prefix}.data.{name}");
            if (!mapping.Entries.Any(entry => one.Includes(entry.Target.Text)) && !required.Any(one.Covers))
            {
                throw new DeliveryException(
                    $"{path!.Text}: no entry of mapping {mapping.Reference} fills it, anything inside it, or a value holding it, so a check has nothing to evaluate there.");
            }

            paths.Add(path!);
        }

        // An assertion of a variable checked reads the fields its where conditions name, so the entries filling them are
        // evaluated too, or a condition would read nothing and the assertion would never be judged.
        var selected = EntrySelection.Of(paths);
        var conditions = mapping.Assertions()
            .Where(a => selected.Includes(a.Entry.Target.Text) || selected.Covers(a.Entry.Target.Text))
            .SelectMany(a => a.Assertion.Where)
            .Select(filter => filter.Field)
            .OfType<string>()
            .Select(field => FieldBrackets().Replace(field, string.Empty))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var field in conditions)
        {
            paths.AddRange(mapping.Entries
                .Where(entry => entry.Target.SchemaPath == field
                    || field.StartsWith(entry.Target.SchemaPath + ".", StringComparison.Ordinal)
                    || entry.Target.SchemaPath.StartsWith(field + ".", StringComparison.Ordinal))
                .Select(entry => entry.Target));
        }

        return EntrySelection.Of(paths.Distinct().ToList());
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\*|\d+)\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FieldBrackets();

    /// <summary>Why a delivery never renders the row, or null when it does: a row the ingestion table marks deleted, or one the source cannot give whole.</summary>
    private static string? Unfit(SourceRecord record)
        => record.DeletedUtc is not null
            ? "the ingestion table marks the row deleted, and a deleted row is never delivered"
            : record.Hold;

    /// <summary>
    /// Inspects one batch of rows on up to <paramref name="parallelism"/> threads, asks the platform what their searches
    /// need in rounds (a round per findBy line a search may try, as a plan asks them), and adds each row to the tally in the
    /// order the scope was read. A search that is still unanswered after the last round holds the entry that asked it.
    /// </summary>
    private static async Task InspectAsync(
        List<SourceRecord> batch, MappingRenderer renderer, EntrySelection selection, int parallelism, int rounds, ValueTally tally, CancellationToken ct)
    {
        var inspections = new RecordInspection[batch.Count];
        Inspect(Enumerable.Range(0, batch.Count).ToList(), batch, renderer, selection, parallelism, inspections, ct);
        var waiting = Enumerable.Range(0, batch.Count).Where(i => inspections[i].IsIncomplete).ToList();
        for (var round = 1; waiting.Count > 0 && round <= rounds; round++)
        {
            var questions = waiting.SelectMany(i => inspections[i].Unanswered).Distinct().ToList();
            await renderer.Search.AnswerAsync(questions, ct).ConfigureAwait(false);
            Inspect(waiting, batch, renderer, selection, parallelism, inspections, ct);
            waiting = waiting.Where(i => inspections[i].IsIncomplete).ToList();
        }

        for (var i = 0; i < batch.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            tally.Add(batch[i], inspections[i], renderer);
        }
    }

    private static void Inspect(
        List<int> indices, List<SourceRecord> batch, MappingRenderer renderer, EntrySelection selection, int parallelism, RecordInspection[] inspections, CancellationToken ct)
    {
        if (parallelism <= 1 || indices.Count <= 1)
        {
            foreach (var i in indices)
            {
                ct.ThrowIfCancellationRequested();
                inspections[i] = renderer.Inspect(batch[i], selection);
            }

            return;
        }

        try
        {
            Parallel.ForEach(
                indices,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                i => inspections[i] = renderer.Inspect(batch[i], selection));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // What one row's inspection threw is what the check fails with, as a run's renderer failing a row fails the run.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }
}
