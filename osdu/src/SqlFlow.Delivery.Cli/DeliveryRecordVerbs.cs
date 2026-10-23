using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>records</c> verb: an interface's records from the ledger, one record with every try it took, the issues that
/// keep its records blocked, and their release. The GUI and the API have shown a record's history from the start; this is
/// the same history where an operator already is, on a node or at a terminal, without a control plane to reach (the
/// project's traceability rule, CLAUDE.md).
/// </summary>
internal static class DeliveryRecordVerbs
{
    /// <summary>Records listed when the command line asks for no count.</summary>
    private const int DefaultMax = 50;

    /// <summary>Tries shown for one record when the command line asks for no count.</summary>
    private const int DefaultAttempts = 20;

    /// <summary>Issues listed when the command line asks for no count.</summary>
    private const int DefaultIssues = 50;

    /// <summary>Files named for one issue.</summary>
    private const int IssueFiles = 20;

    /// <summary>Records of one issue shown side by side, spread across it.</summary>
    private const int IssueSamples = 5;

    public static async Task<int> RecordsAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the flow document whose records these are.");
        }

        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Records live in the module's database. Run 'sqlflow records' with --db <conn-ref>, or set the catalog variable.");

        // A flow that works in partitions keeps a ledger per partition: the one --partition names, or the one a run would take.
        var source = await CliPartitions.AsNamedAsync(context, engine.Documents.LoadSource(flowPath), ct).ConfigureAwait(false);
        var flow = source.Interface(context.Arguments.GetOption("--interface"));
        var flowId = flow.Id;
        var label = flow.Interface is null ? flow.Name : $"{source.Name} / {flow.Interface}";

        return verb switch
        {
            "list" => await ListAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "show" => await ShowAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "issues" => await IssuesAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "release" => await ReleaseAsync(context, ledger, engine, flow, label, ct).ConfigureAwait(false),
            "adopt" => await AdoptAsync(context, ledger, engine, flow, label, ct).ConfigureAwait(false),
            "reverse" => await DeliveryReversalVerbs.ReverseAsync(context, ledger, engine, flow, label, ct).ConfigureAwait(false),
            "reversals" => await DeliveryReversalVerbs.ListAsync(context, ledger, flow, label, ct).ConfigureAwait(false),
            "artifacts" => await DeliveryArtifactVerbs.ArtifactsAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "undos" => await DeliveryArtifactVerbs.UndosAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "removed" => await DeliveryRemovalVerbs.RemovedAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "lift-removal" => await DeliveryRemovalVerbs.LiftAsync(context, engine, flow, label, ct).ConfigureAwait(false),
            _ => context.UsageError("say what to do with the records: list, show, issues, release, adopt, reverse, reversals, artifacts, undos, removed or lift-removal."),
        };
    }

    /// <summary>The interface's records, newest first, with what each is waiting on or was refused for.</summary>
    private static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        var status = context.Arguments.GetOption("--status");
        var query = new RecordQuery
        {
            Search = context.Arguments.GetOption("--search"),
            Mode = context.Arguments.HasFlag("--contains") ? SearchMode.Contains : SearchMode.Prefix,
            Status = Status(status),
            Problem = Issue(context.Arguments.GetOption("--issue")),
            Max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max"),
        };

        var records = await ledger.ListAsync(flowId, query, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["records"] = new JsonArray(records.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {records.Count} record(s)"));
        if (records.Count == 0)
        {
            context.Out.WriteLine(
                "  none. A flow's records appear once a submission has staged them: an intake or a deliver run does that, "
                + "while a plan run reports what it would do and stages nothing.");
            return 0;
        }

        foreach (var record in records)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {record.DeliveryKey.Value:N}  {record.Status,-10}  {record.SourceKey}"));
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"      {record.TargetId ?? "(no OSDU id)"}{Version(record)}{Waiting(record)}{Standing(record)}"));
            if (record.LastError is { Length: > 0 } error)
            {
                context.Out.WriteLine("      " + One(error));
            }
        }

        return 0;
    }

    /// <summary>One record, and every try it took: what each sent, what the target answered, and why it stopped.</summary>
    private static async Task<int> ShowAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        if (context.Arguments.GetOption("--key") is not { } asked)
        {
            return context.UsageError("name the record with --key <delivery key or source key>.");
        }

        var max = Count(context.Arguments.GetOption("--attempts"), DefaultAttempts, "--attempts");
        var record = Guid.TryParse(asked, CultureInfo.InvariantCulture, out var key)
            ? await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false)
            : null;
        if (record is null)
        {
            // An operator holds the source key far more often than the delivery key, so the source key finds it too.
            var found = await ledger.ListAsync(flowId, new RecordQuery { Search = asked, Max = 2 }, ct).ConfigureAwait(false);
            record = found.Count switch
            {
                1 => found[0],
                0 => throw new FlowValidationException($"{label} has no record for '{asked}'."),
                _ => throw new FlowValidationException(
                    $"'{asked}' matches {found.Count} records of {label} ({string.Join(", ", found.Select(r => r.SourceKey))}); name one by its delivery key."),
            };
        }

        var attempts = await ledger.ListAttemptsAsync(flowId, record.DeliveryKey, max, ct).ConfigureAwait(false);
        if (context.Json)
        {
            var described = Described(record);
            described["attempts"] = new JsonArray(attempts.Select(a => (JsonNode)Described(a)).ToArray());
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["record"] = described,
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {record.SourceKey}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  key        {record.DeliveryKey.Value:N}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  status     {record.Status}{Standing(record)}{Waiting(record)}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  osdu id    {record.TargetId ?? "(none)"}{Version(record)}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  mapping    {record.MappingName}"));
        if (record.SourceFileName is { Length: > 0 } file)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  from       {file}{(record.SourceRowNumber is { } row ? $" row {row}" : string.Empty)}"));
        }

        if (record.LastDeliveredUtc is { } delivered)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  delivered  {delivered:u}"));
        }

        if (record.LastVerifiedUtc is { } verified)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  verified   {verified:u} {record.LastVerifyOutcome?.ToString() ?? string.Empty}"));
        }

        if (record.LastError is { Length: > 0 } lastError)
        {
            context.Out.WriteLine("  error      " + One(lastError));
        }

        if (record.ValidationOutcome is { } outcome)
        {
            var asserted = record.AssertionFailures is { } failures
                ? string.Create(CultureInfo.InvariantCulture, $", {failures} assertion failure(s)")
                : string.Empty;
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  checked    {outcome}{asserted}{(record.ValidatedUtc is { } at ? $" at {at:u}" : string.Empty)}"));
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"  {attempts.Count} attempt(s) shown, {record.AttemptCount} on the work pending now; newest first:"));
        foreach (var attempt in attempts)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {attempt.CompletedUtc:u}  {attempt.Outcome,-9}  {attempt.Phase,-8}  {(attempt.CompletedUtc - attempt.StartedUtc).TotalSeconds:0.00}s  {attempt.Worker}"));
            if (attempt.TargetVersion is { } version)
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        version {version}"));
            }

            foreach (var step in Steps(attempt))
            {
                context.Out.WriteLine("        " + step);
            }

            foreach (var line in VerdictLines(attempt))
            {
                context.Out.WriteLine("        " + line);
            }

            if (attempt.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("        " + One(error));
            }
        }

        return 0;
    }

    /// <summary>
    /// The issues keeping the interface's records blocked, the most records first: each with its records, held and
    /// failed, when they last changed, the pattern its records' errors share, and an example record with its own error.
    /// With <c>--issue</c>, that one issue, the files its records came from and samples spread across it.
    /// </summary>
    private static async Task<int> IssuesAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        if (Issue(context.Arguments.GetOption("--issue")) is { } named)
        {
            var problem = await ledger.GetProblemAsync(flowId, named, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"{label} has no record blocked by issue {ProblemSignature.Format(named)}; 'records issues' lists the issues it has.");
            var files = await ledger.ListProblemFilesAsync(flowId, named, IssueFiles, ct).ConfigureAwait(false);
            var samples = await ledger.ListProblemSamplesAsync(flowId, named, IssueSamples, ct).ConfigureAwait(false);

            // The samples know more of where the issue lies than the listing's newest and oldest record.
            problem = problem with { Shape = ProblemSignature.ShapeOf(samples.Select(s => s.LastError).Append(problem.Example?.LastError)) };
            if (context.Json)
            {
                var described = Described(problem);
                described["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["fileName"] = f.FileName, ["records"] = f.Records }).ToArray());
                described["samples"] = new JsonArray(samples.Select(s =>
                {
                    var sample = Described(s);
                    sample["values"] = new JsonArray(ProblemSignature.Values(s.LastError).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
                    return (JsonNode)sample;
                }).ToArray());
                context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
                {
                    ["flow"] = label,
                    ["flowId"] = flowId.ToString(),
                    ["issue"] = described,
                }));
                return 0;
            }

            WriteIssue(context, problem);
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      from {files.Count} file(s), the most records first:"));
            foreach (var file in files)
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {file.Records,10}  {file.FileName ?? "(no file recorded)"}"));
            }

            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {samples.Count} sample(s) spread across it, newest first:"));
            foreach (var sample in samples)
            {
                var values = ProblemSignature.Values(sample.LastError);
                context.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"        {sample.DeliveryKey.Value:N}  {sample.SourceKey}{(values.Count > 0 ? "  " + string.Join(", ", values.Select(v => $"'{v}'")) : string.Empty)}"));
            }

            return 0;
        }

        var listing = await ledger.ListProblemsAsync(flowId, Count(context.Arguments.GetOption("--max"), DefaultIssues, "--max"), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["totalIssues"] = listing.TotalProblems,
                ["totalRecords"] = listing.TotalRecords,
                ["unsorted"] = listing.Unsorted,
                ["issues"] = new JsonArray(listing.Problems.Select(p => (JsonNode)Described(p)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {listing.TotalRecords} blocked record(s) in {listing.TotalProblems} issue(s){(listing.Problems.Count < listing.TotalProblems ? $", the {listing.Problems.Count} largest shown" : string.Empty)}"));
        if (listing.Unsorted > 0)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  and {listing.Unsorted} blocked record(s) not sorted into an issue yet: blocked before the ledger kept issues, and sorted by the control plane a page at a time."));
        }

        if (listing.TotalProblems == 0 && listing.Unsorted == 0)
        {
            context.Out.WriteLine("  none. A record is blocked when it is held or fails, and stays so until its source changes or it is released.");
            return 0;
        }

        foreach (var problem in listing.Problems)
        {
            WriteIssue(context, problem);
        }

        return 0;
    }

    /// <summary>
    /// One issue as the terminal shows it: its id, where it lies and its counts, its pattern, and the example with its own
    /// error.
    /// </summary>
    private static void WriteIssue(CliVerbContext context, ProblemGroup problem)
    {
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {ProblemSignature.Format(problem.Problem)}  {Shape(problem.Shape)}, {problem.Records} record(s): {problem.Held} held, {problem.Failed} failed; last changed {problem.OldestUtc:u} to {problem.NewestUtc:u}"));
        context.Out.WriteLine("      " + One(problem.Pattern));
        if (problem.Example is { } example)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"      for example {example.DeliveryKey.Value:N}  {example.SourceKey}{(example.PendingSourceFileName is { Length: > 0 } file ? $"  ({file}{(example.PendingSourceRowNumber is { } row ? $" row {row}" : string.Empty)})" : string.Empty)}"));
            if (example.LastError is { Length: > 0 } error)
            {
                context.Out.WriteLine("        " + One(error));
            }
        }
    }

    /// <summary>
    /// Releases the interface's blocked records (held, failed, reverted, or removed by a reversal) and its held or failed ones
    /// nothing moves: every one, the ones named by <c>--key</c>, or every one an issue keeps blocked (<c>--issue</c>). A
    /// record that still holds a rendered document goes back to pending and is sent as it is; the rest are planned again
    /// from their rows by the next run, which holds a record again if its row still calls for it. Fixing what blocked them is the operator's job; this is the verb that says "try again". It is recorded
    /// on the audit trail as <c>cli:&lt;user&gt;@&lt;machine&gt;</c>'s (<see cref="RunActors.LocalAccount"/>), with every
    /// record it released named under it.
    /// </summary>
    private static async Task<int> ReleaseAsync(
        CliVerbContext context, ILedger ledger, EngineContext engine, FlowDefinition flow, string label, CancellationToken ct)
    {
        var asked = context.Arguments.GetOptions("--key").ToList();
        var keys = new List<DeliveryKey>(asked.Count);
        foreach (var value in asked)
        {
            keys.Add(Guid.TryParse(value, CultureInfo.InvariantCulture, out var key)
                ? new DeliveryKey(key)
                : throw new FlowValidationException($"--key '{value}' is not a delivery key; 'records list' prints them."));
        }

        var problem = Issue(context.Arguments.GetOption("--issue"));
        if (problem is not null && keys.Count > 0)
        {
            return context.UsageError("release the records --key names or the ones --issue keeps blocked, not both.");
        }

        // The pattern the release records is the issue as the ledger names it now, which is what was listed.
        var group = problem is { } hash
            ? await ledger.GetProblemAsync(flow.Id, hash, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"{label} has no record blocked by issue {ProblemSignature.Format(hash)}; 'records issues' lists the issues it has.")
            : null;

        using var runtime = FlowRuntime.ForTarget(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow);
        runtime.Actor = RunActors.LocalAccount();
        var released = group is not null
            ? await runtime.ReleaseProblemAsync(group.Problem, group.Pattern, ct).ConfigureAwait(false)
            : await runtime.ReleaseAsync(keys.Count > 0 ? keys : null, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flow.Id.ToString(),
                ["issue"] = group is null ? null : ProblemSignature.Format(group.Problem),
                ["released"] = released,
            }));
            return 0;
        }

        var named = group is not null
            ? $" issue {ProblemSignature.Format(group.Problem)} kept blocked"
            : keys.Count > 0 ? $" of the {keys.Count.ToString(CultureInfo.InvariantCulture)} named" : string.Empty;
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {released} record(s) released{named}. The ones that still hold a rendered document are queued now; the rest are planned again by the next run."));
        return 0;
    }

    /// <summary>
    /// <c>sqlflow records adopt</c>: asks the records held because OSDU already holds a record at the id each one's key makes to
    /// adopt that record (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds): every such
    /// record of the interface, the ones <c>--issue</c> keeps held, or the ones <c>--key</c> names. The flow's next run takes
    /// them over, each at the version OSDU holds then. Asking every record or an issue's takes the partition typed back with
    /// <c>--confirm</c>. It is recorded on the audit trail as <c>cli:&lt;user&gt;@&lt;machine&gt;</c>'s, with every record it
    /// asked named under it.
    /// </summary>
    private static async Task<int> AdoptAsync(
        CliVerbContext context, ILedger ledger, EngineContext engine, FlowDefinition flow, string label, CancellationToken ct)
    {
        var given = context.Arguments.GetOptions("--key").ToList();
        var keys = new List<DeliveryKey>(given.Count);
        foreach (var value in given)
        {
            keys.Add(Guid.TryParse(value, CultureInfo.InvariantCulture, out var key)
                ? new DeliveryKey(key)
                : throw new FlowValidationException($"--key '{value}' is not a delivery key; 'records list' prints them."));
        }

        var problem = Issue(context.Arguments.GetOption("--issue"));
        if (problem is not null && keys.Count > 0)
        {
            return context.UsageError("adopt the records --key names or the ones --issue keeps held, not both; name neither to adopt every record held for an id OSDU holds.");
        }

        var group = problem is { } hash
            ? await ledger.GetProblemAsync(flow.Id, hash, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"{label} has no record held by issue {ProblemSignature.Format(hash)}; 'records issues' lists the issues it has.")
            : null;
        if (keys.Count == 0)
        {
            var kept = (await ledger.GetLedgerAsync(flow.Id, ct).ConfigureAwait(false))?.Partition;
            if (kept is null)
            {
                throw new FlowValidationException($"{label} keeps no records in a partition yet, so there is nothing to adopt.");
            }

            var typed = context.Arguments.GetOption("--confirm")?.Trim();
            if (string.IsNullOrEmpty(typed) || !string.Equals(typed, kept, StringComparison.OrdinalIgnoreCase))
            {
                var what = group is null ? "every record held for an id OSDU holds" : $"the records of issue {ProblemSignature.Format(group.Problem)}";
                return context.UsageError(
                    $"adopting {what} takes over the records OSDU holds at their ids in partition {kept}; type the partition back with --confirm {kept}.");
            }
        }

        using var runtime = FlowRuntime.ForTarget(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow);
        runtime.Actor = RunActors.LocalAccount();
        var asked = group is not null
            ? await runtime.AdoptProblemAsync(group.Problem, group.Pattern, ct).ConfigureAwait(false)
            : keys.Count > 0
                ? await runtime.AdoptAsync(keys, ct).ConfigureAwait(false)
                : await runtime.AdoptEveryAsync(ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flow.Id.ToString(),
                ["issue"] = group is null ? null : ProblemSignature.Format(group.Problem),
                ["asked"] = new JsonArray(asked.Select(k => (JsonNode)JsonValue.Create(k.ToString())!).ToArray()),
            }));
            return 0;
        }

        var reached = group is not null
            ? $" issue {ProblemSignature.Format(group.Problem)} kept held"
            : keys.Count > 0 ? $" of the {keys.Count.ToString(CultureInfo.InvariantCulture)} named" : " held for an id OSDU holds";
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {asked.Count} record(s){reached} asked to adopt the records OSDU holds at their ids. The next run takes each over, at the version OSDU holds then, and writes its row there; a record held for anything else was left as it is."));
        return 0;
    }

    /// <summary>
    /// What a try's result records, one line each: the steps it took with what the target answered, for an undo what became
    /// of each artifact, and what a try that sent nothing had to say.
    /// </summary>
    /// <summary>
    /// What an attempt's verdict says, as the terminal shows it under the attempt: one line for the check of its document,
    /// then each failure of the mapping's assertions it lists (osdu/docs/reference/flow/mapping-assertions.md); nothing for a
    /// try that checked nothing.
    /// </summary>
    internal static IEnumerable<string> VerdictLines(AttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (Validation.ValidationVerdict.FromJson(ResultNode(attempt)?["validation"]) is not { Outcome: not Validation.ValidationOutcome.NotValidated } verdict)
        {
            yield break;
        }

        yield return "checked " + One(verdict.Summary());
        if (verdict.Assertions is { Failed: > 0 } findings)
        {
            foreach (var line in FindingLines.Of(findings))
            {
                yield return "  " + One(line);
            }
        }
    }

    /// <summary>An attempt's result as JSON, or null when it holds none or holds text that is not JSON.</summary>
    private static JsonNode? ResultNode(AttemptRecord attempt)
    {
        if (attempt.ResultJson is not { Length: > 0 } text)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Steps(AttemptRecord attempt)
    {
        if (attempt.ResultJson is not { Length: > 0 } text)
        {
            yield break;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }

        if (parsed?["steps"] is JsonArray steps)
        {
            foreach (var step in steps.OfType<JsonObject>())
            {
                var name = step["name"]?.GetValue<string>() ?? "step";
                var status = step["status"] is JsonValue value && value.TryGetValue<int>(out var code) ? $" {code}" : string.Empty;
                var returned = step["returned"] is JsonObject values && values.Count > 0
                    ? " " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"))
                    : string.Empty;
                yield return string.Create(CultureInfo.InvariantCulture, $"{name}{status}{returned}");
            }
        }

        if (parsed?["undo"] is JsonObject undo)
        {
            foreach (var line in DeliveryArtifactVerbs.UndoLines(undo))
            {
                yield return line;
            }
        }

        if (parsed?["detail"] is JsonValue detail && detail.TryGetValue<string>(out var said) && !string.IsNullOrWhiteSpace(said))
        {
            yield return One(said);
        }
    }

    private static JsonObject Described(RecordState record) => new()
    {
        ["deliveryKey"] = record.DeliveryKey.Value.ToString("N", CultureInfo.InvariantCulture),
        ["sourceKey"] = record.SourceKey,
        ["label"] = record.Label,
        ["status"] = record.Status.ToString(),
        ["blocked"] = record.Blocked,
        ["planRequestedUtc"] = record.PlanRequestedUtc,
        ["targetId"] = record.TargetId,
        ["targetVersion"] = record.TargetVersion,
        ["waitingFor"] = record.WaitingFor,
        ["mapping"] = record.MappingName,
        ["attemptCount"] = record.AttemptCount,
        ["lastDeliveredUtc"] = record.LastDeliveredUtc,
        ["lastVerifiedUtc"] = record.LastVerifiedUtc,
        ["lastVerifyOutcome"] = record.LastVerifyOutcome?.ToString(),
        ["lastError"] = record.LastError,
        ["sourceFileName"] = record.SourceFileName,
        ["sourceRowNumber"] = record.SourceRowNumber,
        ["issue"] = record.ProblemHash is { } issue ? ProblemSignature.Format(issue) : null,
        ["validationOutcome"] = record.ValidationOutcome,
        ["validatedUtc"] = record.ValidatedUtc,
        ["assertionFailures"] = record.AssertionFailures,
    };

    /// <summary>Where an issue lies, in the words the terminal and the JSON use.</summary>
    private static string Shape(ProblemShape shape) => shape == ProblemShape.Rows ? "row errors" : "set error";

    private static JsonObject Described(ProblemGroup problem) => new()
    {
        ["issue"] = ProblemSignature.Format(problem.Problem),
        ["shape"] = problem.Shape == ProblemShape.Rows ? "rows" : "set",
        ["values"] = new JsonArray(problem.Values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        ["pattern"] = problem.Pattern,
        ["records"] = problem.Records,
        ["held"] = problem.Held,
        ["failed"] = problem.Failed,
        ["oldestUtc"] = problem.OldestUtc,
        ["newestUtc"] = problem.NewestUtc,
        ["example"] = problem.Example is { } example ? Described(example) : null,
    };

    private static JsonObject Described(AttemptRecord attempt) => new()
    {
        ["attemptId"] = attempt.AttemptId,
        ["startedUtc"] = attempt.StartedUtc,
        ["completedUtc"] = attempt.CompletedUtc,
        ["outcome"] = attempt.Outcome.ToString(),
        ["phase"] = attempt.Phase,
        ["worker"] = attempt.Worker,
        ["submissionId"] = attempt.SubmissionId,
        ["runId"] = attempt.RunId,
        ["targetVersion"] = attempt.TargetVersion,
        ["error"] = attempt.Error,
        ["result"] = attempt.ResultJson is { Length: > 0 } text ? JsonNode.Parse(text) : null,
    };

    private static string Version(RecordState record)
        => record.TargetVersion is { } version ? string.Create(CultureInfo.InvariantCulture, $" v{version}") : string.Empty;

    private static string Waiting(RecordState record)
        => record.WaitingFor is { Length: > 0 } waiting ? $" (waiting for {waiting})" : string.Empty;

    /// <summary>
    /// What moves the record next, where its status does not say: blocked until its row changes or it is released, asked to
    /// be planned again by the next run, or, held or failed with no document to send, nothing at all until it is released.
    /// </summary>
    private static string Standing(RecordState record)
        => record.Blocked ? " (blocked)"
            : record.PlanRequestedUtc is not null ? " (asked to be planned again)"
            : record.Status is RecordStatus.Held or RecordStatus.Failed && record.PendingDocumentRef is null ? " (nothing will move it: release it)"
            : string.Empty;

    /// <summary>An error as one line, so a record's line stays a line whatever the target wrote.</summary>
    private static string One(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 240 ? single : single[..237] + "...";
    }

    private static RecordStatus? Status(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<RecordStatus>(value.Trim(), ignoreCase: true, out var status)
            ? status
            : throw new FlowValidationException(
                $"--status '{value}' is not a record status; it is one of {string.Join(", ", Enum.GetNames<RecordStatus>().Select(n => n.ToLowerInvariant()))}.");
    }

    /// <summary>An issue as <c>--issue</c> names it: the sixteen characters 'records issues' prints.</summary>
    private static long? Issue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ProblemSignature.TryParse(value.Trim().ToLowerInvariant(), out var problem)
            ? problem
            : throw new FlowValidationException(
                $"--issue '{value}' is not an issue; it is the {ProblemSignature.TextLength} characters 'records issues' prints for one.");
    }

    private static int Count(string? value, int fallback, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? Math.Min(count, 1000)
            : throw new FlowValidationException($"{option} '{value}' is not a whole number of records above zero.");
    }
}
