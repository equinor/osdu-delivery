using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one delivery flow document as one platform run. The run's operation (deliver, plan, intake, drain, verify,
/// replan, sync, reverse, undo, remove, delete-ledger: <see cref="DeliveryFlowKind.DeliveryOperationList"/>) and its payload
/// (the submission or run it works on, the records it is scoped to or removes, the key slices a member plans, the interfaces it runs)
/// decide what it reads out of the flow's ingestion tables and what it does with it; the engine's log becomes the run
/// log and the live trace; the outcome becomes <c>run.json</c> and the run row's projected counts. Every ledger activity
/// and attempt the run writes carries the run id, so a record's history links back to the run that produced it and the
/// run links forward to what it did to each record.
/// </summary>
public sealed class DeliveryExecutor : IFlowDocumentExecutor
{
    /// <summary>Records verified per verify run; the next run picks up where the ordering by last-verified left off.</summary>
    public const int VerifyBatch = 5000;

    /// <summary>A verify run skips records verified more recently than this unless forced.</summary>
    public static readonly TimeSpan VerifyInterval = TimeSpan.FromHours(24);

    /// <summary>How many plan entries a plan run writes to its trace before summarising the rest.</summary>
    public const int PlanEntriesLogged = 200;

    /// <summary>
    /// The records the ledger asked to plan again that one key-scoped pass of a deliver run reads; a run makes as many
    /// passes as there are such records, so a release or a redelivery of every record is planned by the next run whole.
    /// </summary>
    public const int RequestedPerPass = 5000;

    /// <summary>
    /// The most requests of records with no source key one run settles after its read of the whole scope; any beyond them
    /// keep their requests for the next run, which reads the whole scope again.
    /// </summary>
    public const int MaxKeyless = 100_000;

    private readonly IServiceProvider _provider;

    public DeliveryExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is DeliveryFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not DeliveryFlowDocument delivery)
        {
            throw new SqlFlowException($"The delivery executor cannot run a '{document.Kind}' document.");
        }

        // The file the node materialized is where the repository layout (mappings, snapshots) is resolved from.
        var declared = delivery.Source.WithSourcePath(Path.GetFullPath(flowFile));
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = DeliveryOperations.Of(parameters);
        var payload = DeliveryRunPayload.Parse(parameters);
        payload.Validate(operation);

        // The partition the run targets travels as a run value beside the flow's own, and never reaches its parameters: it
        // binds the source instead, so the ledger, the header, the cache and the configuration below are all that partition's.
        var (partition, values) = PartitionNames.SplitRunValues(
            parameters.Values, keptAsParameter: !declared.Partitioned && declared.Parameters.ContainsKey(PartitionNames.RunValue));
        parameters = parameters with { Values = values };

        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, declared.Name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, declared.Name);
        var log = loggers.CreateLogger("run");
        var context = _provider.GetRequiredService<EngineContext>()
            .ForRun(loggers)
            .WithFanOut(options.FanOut is { } fanOut ? new RunFanOutDispatcher(fanOut) : null);

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        LogStart(log, declared.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            // A hard-coded partition settles itself; a flow that leaves its partition to the registry runs in the one named,
            // which the registry has to hold, or in the registry's default.
            var registry = declared.NeedsRegistry(partition)
                ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
                : RegisteredPartitions.None;
            var source = declared.Resolve(partition, registry);
            if (source.Partition is { } bound)
            {
                LogPartition(log, bound);
            }

            // The control plane supplies what it holds centrally, the partition's own values first; the node answers the
            // rest from its own environment.
            var partitioned = context.WithSuppliedReferences(payload.ReferencesFor(source.Partition));
            if (source.Partitioned && StartsWholeRun(payload))
            {
                await _provider.GetRequiredService<PartitionLedgers>()
                    .CheckAsync(partitioned.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage), source, declared.Served(registry), ct)
                    .ConfigureAwait(false);
            }

            result = await ExecuteOperationAsync(partitioned, source, operation, parameters, payload, runId, actor, runLogger, log, ct).ConfigureAwait(false);
            success = true;
            LogDone(log, operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceRunIncompleteException incomplete)
        {
            // A source whose interfaces did not all complete ends failed, and its outcome still says what each one did.
            error = RunFailure.Describe(incomplete);
            LogFailed(log, operation, error, null);
            result = incomplete.Outcome;
        }
        catch (RecordsNotRemovedException refused)
        {
            // A removal OSDU refused records of ends failed, and its outcome still says what became of every record.
            error = RunFailure.Describe(refused);
            LogFailed(log, operation, error, null);
            result = refused.Outcome;
        }
        catch (Exception ex)
        {
            // The run boundary: every failure ends the run as a recorded one. An unexpected kind is a defect, so its
            // stack goes to the run log as well.
            error = RunFailure.Describe(ex);
            LogFailed(log, operation, error, RunFailure.IsExpected(ex) ? null : ex);
            result = new OperationFailure(operation, error);
        }

        stopwatch.Stop();
        var artifact = RunArtifacts.Artifact(FlowDefinition.FlowTypeName, declared.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, declared.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = declared.Name,
            FlowKind = FlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    /// <summary>True for the operations that read the ingestion tables and render.</summary>
    internal static bool ReadsSource(string operation)
        => operation is DeliveryOperations.Deliver or DeliveryOperations.Plan or DeliveryOperations.Intake or DeliveryOperations.Replan;

    /// <summary>True for the operations that reach the flow's OSDU target, whose service and credentials a preflight checks.</summary>
    internal static bool ReachesTarget(string operation)
        => operation is not (DeliveryOperations.Plan or DeliveryOperations.Sync);

    /// <summary>True for the operations that deliver records, which a failure guard watches.</summary>
    internal static bool Delivers(string operation)
        => operation is DeliveryOperations.Deliver or DeliveryOperations.Replan or DeliveryOperations.Drain;

    /// <summary>
    /// A run works on one interface when the flow is in the single form, when it names one, or when what it works on
    /// belongs to one (a submission, records, slices); otherwise it runs the source's interfaces through a
    /// <see cref="SourceRuntime"/>.
    /// </summary>
    private static async Task<object> ExecuteOperationAsync(
        EngineContext context, SourceDefinition source, string operation, RunParameters parameters, DeliveryRunPayload payload,
        Guid runId, string actor, RunLogger runLogger, ILogger log, CancellationToken ct)
    {
        var (flow, submission) = await TargetInterfaceAsync(context, source, operation, payload, ct).ConfigureAwait(false);
        if (flow is null)
        {
            var sources = new SourceRuntime(context, source)
            {
                Actor = actor,
                RunId = runId,
                ActivityLog = runLogger.Render,
                Values = parameters.Values,
            };
            return await sources.ExecuteAsync(operation, payload, ct).ConfigureAwait(false);
        }

        // A run that names a submission works on that submission's records with the parameter values it was registered
        // with, plus any override the trigger carried. A drain works on the submission's batches and reads no source.
        var values = parameters.Values;
        if (submission is not null)
        {
            values = Merge(ParseValues(submission.ParametersJson), parameters.Values);
            LogSubmission(log, submission.SubmissionId, submission.SourceObject);
        }

        // Deliver, plan, intake and replan read the ingestion tables and render; verify and drain only touch the target
        // and the ledger, and a sync reads rows it never renders, so none of them needs the flow's render inputs. A sync
        // reads a record no submission names in the run's own scope, so every runtime carries the run's values.
        var interfaceContext = flow.Interface is null ? context : context.ForInterface(flow.Interface);
        using var runtime = ReadsSource(operation)
            ? await FlowRuntime.CreateAsync(interfaceContext, flow, values, ct).ConfigureAwait(false)
            : FlowRuntime.ForTarget(interfaceContext, flow, values);
        runtime.Actor = actor;
        runtime.RunId = runId;
        runtime.ActivityLog = runLogger.Render;
        runtime.SourceDocument = source;
        if (runtime.ReadsSource)
        {
            await runtime.CheckRouteAsync(runtime.Mapping.Mapping.Kind, ct).ConfigureAwait(false);
        }

        LogRoute(log, operation, runtime);
        return await GuardedAsync(runtime, operation, token => ExecuteInterfaceAsync(runtime, operation, payload, submission, log, token), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The interface a run works on, and the submission it names, or no interface for a run of the whole source. A
    /// submission belongs to the interface whose ledger identity registered it, and a run naming records or slices has to
    /// name the interface they belong to when the source has several.
    /// </summary>
    private static async Task<(FlowDefinition? Flow, SubmissionState? Submission)> TargetInterfaceAsync(
        EngineContext context, SourceDefinition source, string operation, DeliveryRunPayload payload, CancellationToken ct)
    {
        var named = payload.Interface is { } name ? source.Interface(name) : null;
        SubmissionState? submission = null;
        if (payload.SubmissionId is { } submissionId)
        {
            // Read even for a drain, which needs nothing else of it: a submission is only ever worked on by the interface
            // whose ledger registered it.
            var ledger = context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
            submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Submission {submissionId:D} is not in the ledger.");
        }

        var flow = named
            ?? (source.DeclaresInterfaces ? null : source.First)
            ?? (submission is null ? null : source.ByFlowId(submission.FlowId)
                ?? throw new DeliveryException(
                    $"Submission {submission.SubmissionId:D} belongs to {Owner(submission.FlowName, submission.Partition)}, which is not an interface of '{source.Name}'{In(source.Partition)}."
                    + (source.Partitioned && submission.Partition is { } kept && !string.Equals(kept, source.Partition, StringComparison.OrdinalIgnoreCase) ? $" Run it in partition '{kept}'." : string.Empty)));

        if (flow is null)
        {
            if (payload.RecordKeys.Count > 0 || payload.Slices.Count > 0)
            {
                throw new DeliveryException(
                    $"Flow '{source.Name}' delivers {source.Interfaces.Count} interface(s) ({string.Join(", ", source.Names)}); a run on records or slices names the interface they belong to with 'interface' in its payload.");
            }

            if (operation == DeliveryOperations.Reverse)
            {
                throw new DeliveryException(
                    $"Flow '{source.Name}' delivers {source.Interfaces.Count} interface(s) ({string.Join(", ", source.Names)}); a reversal of a run works in one interface's ledger, which 'interface' names in its payload.");
            }

            if (operation == DeliveryOperations.Remove)
            {
                throw new DeliveryException(
                    $"Flow '{source.Name}' delivers {source.Interfaces.Count} interface(s) ({string.Join(", ", source.Names)}); a removal works in one interface's ledger, which 'interface' names in its payload.");
            }

            source.Select(payload.Interfaces);
            return (null, null);
        }

        if (payload.Interfaces.Count > 0)
        {
            // The single form has no interfaces to select; this says so by name.
            source.Select(payload.Interfaces);
        }

        if (submission is not null && submission.FlowId != flow.Id)
        {
            throw new DeliveryException(
                $"Submission {submission.SubmissionId:D} belongs to {Owner(submission.FlowName, submission.Partition)}, not '{flow.Label}'{In(flow.Partition)}."
                + (flow.Partitioned && submission.Partition is { } own && !string.Equals(own, flow.Partition, StringComparison.OrdinalIgnoreCase) ? $" Run it in partition '{own}'." : string.Empty));
        }

        return (flow, operation == DeliveryOperations.Drain ? null : submission);
    }

    /// <summary>A submission's flow as a message names it: with the partition its ledger keeps it in, when it has one.</summary>
    private static string Owner(string flowName, string? partition) => $"flow '{flowName}'{In(partition)}";

    /// <summary>" in partition 'p'", or nothing for no partition.</summary>
    private static string In(string? partition) => partition is null ? string.Empty : $" in partition '{partition}'";

    /// <summary>
    /// Runs one interface's operation under its failure guard when the operation delivers: the guard watches every
    /// record, and when it trips the work stops, hands its leases back and the interface ends stopped with the reason.
    /// </summary>
    internal static async Task<T> GuardedAsync<T>(FlowRuntime runtime, string operation, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(work);
        if (!Delivers(operation))
        {
            return await work(ct).ConfigureAwait(false);
        }

        using var guard = new FailureGuard(runtime.Flow.FailWhen);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, guard.Token);
        runtime.Guard = guard;
        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.Tripped && !ct.IsCancellationRequested)
        {
            throw new InterfaceStoppedException(runtime.Flow, guard.Reason!);
        }
        finally
        {
            runtime.Guard = null;
        }
    }

    /// <summary>One interface's operation on a runtime opened for it, as a run of that interface alone performs it.</summary>
    internal static async Task<object> ExecuteInterfaceAsync(
        FlowRuntime runtime, string operation, DeliveryRunPayload payload, SubmissionState? submission, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(log);
        var context = runtime.Context;
        var flow = runtime.Flow;
        var keys = payload.RecordKeys.Select(k => new DeliveryKey(k)).ToList();
        var selection = SourceSelection.Incremental(null);
        if (payload.RateShare is { } share)
        {
            // A part of a split delivery sends under its share of the flow's rate limit, so the delivery as a whole keeps to the rate.
            runtime.ShareRateLimit(share);
        }
        if (ReadsSource(operation))
        {
            runtime.SubmissionId = payload.SubmissionId;
            runtime.Slices = payload.Slices.Count > 0 ? payload.Slices : null;
            selection = payload.MaxRecords is { } first
                ? await FirstOfScopeAsync(runtime.Source, first, log, ct).ConfigureAwait(false)
                : await SelectionAsync(context, flow, runtime.Parameters, runtime.Mapping.Context, operation, submission, keys, payload.Rerender, log, ct).ConfigureAwait(false);
            runtime.Selection = selection;
        }

        var source = flow.Source.Record.Object;
        var force = ForcesReplan(payload, submission is not null) || operation == DeliveryOperations.Replan;

        switch (operation)
        {
            case DeliveryOperations.Deliver when payload.Slices.Count > 0:
                // A part of a split delivery: the delivery's first run brought records up to date, answered the run's requests
                // and closes the submission; this run delivers the rows of the slices its payload names.
                var sliceRun = await runtime.RunAsync(force, ct).ConfigureAwait(false);
                var sliceOutcome = DeliverOutcome.OfShare(sliceRun, operation, source, selection.Describe(), payload.Slices);
                LogOutcome(log, string.Create(
                    CultureInfo.InvariantCulture,
                    $"slices {sliceOutcome.Share!.Slices}: {sliceOutcome.Planned} planned, {sliceOutcome.Delivered} delivered, {sliceOutcome.SkippedUnchanged + sliceOutcome.UnchangedAtPush} unchanged, {sliceOutcome.Held} held, {sliceOutcome.Failed} failed of submission {sliceOutcome.SubmissionId:D}"));
                return sliceOutcome;

            case DeliveryOperations.Deliver:
            case DeliveryOperations.Replan:
                if (payload.Rerender)
                {
                    // Bringing records up to date: ask the named records, or without names every record the flow has
                    // delivered, to be rendered again; the plan sends only what renders differently.
                    var asked = await runtime.BringUpToDateAsync(keys.Count > 0 ? keys : null, ct).ConfigureAwait(false);
                    LogRerender(log, asked, keys.Count > 0 ? keys.Count : null);
                }
                else if (keys.Count > 0 || payload.Redeliver is not null)
                {
                    // A redelivery: forget what OSDU holds for the named records, or without names for every record the
                    // flow has delivered (all of it, or the part the run names), then let the plan re-send them.
                    var marked = await runtime.RedeliverAsync(keys.Count > 0 ? keys : null, RedeliverScopeOf(payload, flow), ct).ConfigureAwait(false);
                    LogRedeliver(log, marked, keys.Count > 0 ? keys.Count : null);
                }

                var requested = await DeliverRequestedAsync(runtime, selection, payload, log, ct).ConfigureAwait(false);

                // A record asked for again that carries no source key cannot be read by key: the run reads the whole scope
                // instead of a window, which meets its row and plans it, and then answers a request whose row it did not meet.
                if (requested.Keyless.Count > 0 && selection.Kind == SourceSelectionKind.Incremental)
                {
                    log.LogInformation(
                        "{Count} record(s) asked to be planned again carry no source key, so this run reads the whole scope rather than the rows changed since its watermark.",
                        requested.Keyless.Count);
                    selection = SourceSelection.Full();
                    runtime.Selection = selection;
                }

                var run = await runtime.RunAsync(force, ct).ConfigureAwait(false);
                if (requested.Keyless.Count > 0 && await runtime.SettleKeylessAsync(requested.Keyless, ct).ConfigureAwait(false) is > 0 and var gone)
                {
                    log.LogWarning("{Count} record(s) asked to be planned again carry no source key and the read of the whole scope did not meet their rows; each says so on its history.", gone);
                }

                var delivered = DeliverOutcome.From(run, operation, source, selection.Describe(), requested);
                LogOutcome(log, string.Create(
                    CultureInfo.InvariantCulture,
                    $"this run: {delivered.Planned} planned, {delivered.Delivered} delivered, {delivered.SkippedUnchanged + delivered.UnchangedAtPush} unchanged, {delivered.Held} held, {delivered.Failed} failed; submission {delivered.SubmissionId:D} {SubmissionIntake.Summarize(run.Submission)}"));
                return delivered;

            case DeliveryOperations.Intake:
                var intake = await runtime.IntakeAsync(force || submission is not null, ct).ConfigureAwait(false);
                LogOutcome(log, intake.AlreadyProcessed ? SubmissionIntake.Summarize(intake.Submission) : $"intake: {intake.Counts}");
                return IntakeOutcome.From(intake, source, selection.Describe(), payload.Slices);

            case DeliveryOperations.Drain:
                var drained = await runtime.WorkAsync(once: false, payload.SubmissionId, ct).ConfigureAwait(false);
                LogOutcome(log, $"drain: {drained}");
                return DrainOutcome.From(drained, payload.SubmissionId);

            case DeliveryOperations.Plan:
                return await PlanAsync(runtime, force, payload.Rerender, source, selection, log, ct).ConfigureAwait(false);

            case DeliveryOperations.Sync:
                var synced = await runtime.SyncAsync(keys.Count == 0 ? null : keys, ct).ConfigureAwait(false);
                LogOutcome(log, $"sync: {synced}");
                return SyncRunOutcome.From(operation, synced);

            case DeliveryOperations.Reverse:
                var reversed = payload.SubmissionId is { } reversedSubmission ? ReversalSource.Submission(reversedSubmission) : ReversalSource.Run(payload.RunId!.Value);
                var reversal = await runtime.ReverseAsync(reversed, ct).ConfigureAwait(false);
                LogOutcome(log, $"reverse: {reversal.Describe(reversed)}");
                return ReverseRunOutcome.From(operation, reversed, reversal);

            case DeliveryOperations.Undo:
                var undone = await runtime.UndoUnfinishedAsync(payload.Force, ct).ConfigureAwait(false);
                LogOutcome(log, $"undo: {undone.Describe()}");
                return UndoRunOutcome.From(operation, undone);

            case DeliveryOperations.Remove:
                var removing = payload.Scope ?? throw new DeliveryException("A remove run names how much of each record goes (payload scope), and this one names none.");
                var removal = await runtime.RemoveAsync(
                    keys.Count > 0 ? RemovalSelection.Of(keys) : RemovalSelection.Of(payload.Filter ?? throw new DeliveryException("A remove run names its records (payload recordKeys or filter), and this one names none.")),
                    removing, payload.PurgeLedger, ct).ConfigureAwait(false);
                LogOutcome(log, $"remove: {removal.Describe()}");
                var removed = RemoveRunOutcome.From(operation, removal);
                return removed.Failed > 0 ? throw new RecordsNotRemovedException(flow, removed) : removed;

            case DeliveryOperations.DeleteLedger:
                var confirm = payload.Confirm ?? throw new DeliveryException("A run deleting the ledger names the partition it acts in (payload confirm), and this one names none.");
                var cleared = await runtime.DeleteLedgerAsync(confirm, ct).ConfigureAwait(false);
                LogOutcome(log, $"delete ledger: {cleared.Describe()}");
                return DeleteLedgerOutcome.From(operation, cleared);

            case DeliveryOperations.Verify:
                var reconcile = flow.Verify.Reconcile;
                var summary = await runtime.VerifyAsync(VerifyBatch, payload.Force ? null : VerifyInterval, reconcile, keys.Count == 0 ? null : keys, ct).ConfigureAwait(false);
                LogOutcome(log, $"verify: {summary}");
                return new VerifyRunOutcome(operation, summary.Checked, summary.Matched, summary.Drifted, summary.Missing, summary.Errors, reconcile);

            default:
                throw new SqlFlowException($"'{operation}' is not an operation of a delivery flow.");
        }
    }

    /// <summary>
    /// Which records this run reads. A run on a submission reads the rows that submission recorded, a record-scoped run
    /// reads those records by their stored key tuples, a replan and a plan that renders every record read the whole scope,
    /// and an ordinary run reads what
    /// changed since the scope's watermark, less the flow's overlap, or the whole scope when the mapping, the template or
    /// the parameters it renders with moved since that watermark was written.
    /// </summary>
    private static async Task<SourceSelection> SelectionAsync(
        EngineContext context, FlowDefinition flow, IReadOnlyDictionary<string, string> values, Delivery.Snapshots.RenderContext rendering, string operation,
        SubmissionState? submission, IReadOnlyList<DeliveryKey> keys, bool rerender, ILogger log, CancellationToken ct)
    {
        if (submission is not null)
        {
            return SourceWindowDescription.Parse(submission.SourceWindowJson)?.ToSelection()
                ?? SourceSelection.Incremental(submission.WindowFromUtc);
        }

        if (keys.Count > 0)
        {
            var ledger = context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
            var records = await ledger.GetRecordsAsync(flow.Id, keys, ct).ConfigureAwait(false);
            var tuples = new List<KeyTuple>(keys.Count);
            foreach (var key in keys)
            {
                if (!records.TryGetValue(key, out var record))
                {
                    throw new DeliveryException($"Record {key} is not in this flow's ledger, so the row it is built from cannot be found; deliver the flow's scope to plan it.");
                }

                if (record.SourceKeyJson is not { } json)
                {
                    throw new DeliveryException(
                        $"Record {key} carries no source key, so the row it is built from cannot be looked up; deliver the flow's scope once to record it.");
                }

                tuples.Add(KeyTuple.FromJson(json));
            }

            return SourceSelection.ForKeys(tuples);
        }

        if (operation == DeliveryOperations.Replan || (operation == DeliveryOperations.Plan && rerender))
        {
            return SourceSelection.Full();
        }

        // The scope's own read is decided where 'sqlflow check --connect' decides it too, so the check reports what this reads.
        // Once a whole-scope read under new rules completes, the watermark carries the new rules and runs are incremental again.
        var read = await ScopeReads.DecideAsync(context.Ledger, flow, values, rendering, ct).ConfigureAwait(false);
        switch (read.Reason)
        {
            case ScopeReadReason.NoWatermark:
                log.LogInformation("No whole-scope plan of scope {Scope} has completed yet, so this run reads every row in scope.", read.Scope);
                break;

            case ScopeReadReason.RulesMoved:
                log.LogInformation(
                    "Scope {Scope} was last planned with other rules than {Mapping} renders with now (its mapping, template or parameters changed), so this run reads every row in scope; each record's own hashes decide what is sent.",
                    read.Scope, rendering.MappingReference);
                break;

            case ScopeReadReason.SinceWatermark:
                log.LogInformation(
                    "Scope {Scope} was planned through {Through:o}; reading the rows changed after {Lower:o} (an overlap of {Overlap}s).",
                    read.Scope, read.Watermark!.UpdatedThroughUtc, read.Selection.LowerUtc, flow.Source.Incremental.OverlapSeconds);
                break;

            case ScopeReadReason.NoLedger:
            default:
                break;
        }

        return read.Selection;
    }

    /// <summary>
    /// The first <paramref name="max"/> records of the run's scope (a payload's <c>maxRecords</c>), in the order a read of the
    /// scope pages in, as a read of those records by key: it plans each as a run of every row would, and leaves the scope's
    /// watermark and the records asked to be planned again for a run of the whole scope. The keys come from the ingestion
    /// tables themselves, so the records need not be in the ledger yet, as a first run's are not.
    /// </summary>
    internal static async Task<SourceSelection> FirstOfScopeAsync(IIngestionSource source, int max, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentNullException.ThrowIfNull(log);
        var keys = new List<KeyTuple>(max);
        await foreach (var key in source.ReadKeysAsync(max, ct).ConfigureAwait(false))
        {
            keys.Add(key.Key);
            if (keys.Count == max)
            {
                break;
            }
        }

        var selection = SourceSelection.FirstOfScope(keys, max);
        log.LogInformation(
            "maxRecords {Max}: this run reads {Selection}, by key, and leaves the scope's watermark where it is, so a run of the whole scope still reads every row.",
            max, selection.Describe());
        return selection;
    }

    /// <summary>
    /// Delivers the records the ledger asked to plan again (a release, a redelivery, a removal, a cache rollout), and the
    /// records deleted from the ledger whose rows wait to be read again, before the run's own pass. Their rows did not
    /// change, so an incremental read would not meet them; they are read by key,
    /// <see cref="FlowRuntime.RequestedPerPass"/> to a pass and each pass in a submission of its own, until every one has
    /// been read: a release of every record a problem kept blocked is planned by this run whole, not a page of it. The walk
    /// goes in key order and meets each record once, so one a pass could not plan (its row gone, or outside this run's
    /// scope) keeps its request for a run that can, and never holds this one up. The run reports what the passes did beside
    /// its own counts.
    /// </summary>
    internal static async Task<RequestedPasses> DeliverRequestedAsync(
        FlowRuntime runtime, SourceSelection selection, DeliveryRunPayload payload, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(log);
        if (!selection.CoversScope || runtime.Context.Ledger is null || payload.RecordKeys.Count > 0)
        {
            return RequestedPasses.None;
        }

        var passes = RequestedPasses.None;
        var keyless = new List<PlanRequestedRecord>();
        // The requests are walked forward in the order they were made and a planned record leaves them, so the walk ends;
        // a request made while it goes (a release, a cache rollout marking a page) is met too. The moments are no bound:
        // they were written by whichever control plane or node asked, whose clock is not this one's.
        PlanRequestedRecord? after = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await runtime.PlanRequestedKeysAsync(runtime.RequestedPerPass, after, ct).ConfigureAwait(false);
                if (page.Last is null)
                {
                    break;
                }

                after = page.Last;
                if (keyless.Count < MaxKeyless)
                {
                    keyless.AddRange(page.KeylessRequests.Take(MaxKeyless - keyless.Count));
                }

                if (page.Keys.Count == 0)
                {
                    continue;
                }

                log.LogInformation(
                    "Pass {Pass}: {Count} record(s) wait to be planned again; reading them by key before this run's own pass.",
                    passes.Passes + 1, page.Keys.Count);
                runtime.Selection = SourceSelection.ForKeys(page.Keys);
                runtime.SubmissionId = null;
                var run = await runtime.RunAsync(force: true, ct).ConfigureAwait(false);
                log.LogInformation(
                    "Pass {Pass}, the records asked for: {Summary} (submission {SubmissionId:D}).",
                    passes.Passes + 1, SubmissionIntake.Summarize(run.Submission), run.Submission.SubmissionId);

                // A request the pass could not answer (the row gone from the table, or in another run's scope) is read again
                // by a sync in its record's own scope: one whose row is gone is answered there, so no later run reads it again.
                if (await runtime.SettleUnansweredAsync(page.Requests, ct).ConfigureAwait(false) is { } settled)
                {
                    log.LogInformation("Pass {Pass}: records asked for that it could not plan, read again in their own scope: {Summary}.", passes.Passes + 1, settled);
                }

                // The records deleted from the ledger whose rows the pass read: a row planned is a record again, a row gone
                // can never be, and either answers the request its line carried; the rest wait for a run that can plan them.
                if (await runtime.SettlePurgedRequestsAsync(page.PurgedRequests, ct).ConfigureAwait(false) is { } purged)
                {
                    log.LogInformation("Pass {Pass}, the records deleted from the ledger it read again: {Summary}.", passes.Passes + 1, purged);
                }

                passes = passes.Add(run.Submission.SubmissionId, page.Keys.Count, run.Submission.Planned);
            }
        }
        finally
        {
            runtime.Selection = selection;
            runtime.SubmissionId = payload.SubmissionId;
        }

        if (passes.Passes > 1)
        {
            log.LogInformation(
                "Read {Read} record(s) asked to be planned again in {Passes} passes; {Planned} were planned.",
                passes.Read, passes.Passes, passes.Planned);
        }

        return passes with { Keyless = keyless };
    }

    /// <summary>
    /// Whether a run re-plans past the gates that skip a whole read: the tier-0 window gate and an already completed
    /// submission. A forced run, a submission re-run, a run scoped to record keys and a run that brings records up to date
    /// all do. The scoped run and the one bringing records up to date have to, or their marks are never seen. Forcing lifts
    /// only those two gates; each record's own hashes still decide what is sent.
    /// </summary>
    public static bool ForcesReplan(DeliveryRunPayload payload, bool reRunningSubmission)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Force || reRunningSubmission || payload.RecordKeys.Count > 0 || payload.Rerender;
    }

    /// <summary>
    /// What a deliver run of <paramref name="flow"/> sends again of the records it redelivers: the part the run's
    /// <c>redeliver</c> names, everything when unset. A part the flow's route does not send is refused.
    /// </summary>
    public static RedeliverSelection RedeliverScopeOf(DeliveryRunPayload payload, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return RedeliverScopes.Of(payload.Redeliver, flow);
    }

    /// <summary>A plan run streams its records, writes the first entries to its trace and counts the rest.</summary>
    private static async Task<PlanOutcome> PlanAsync(FlowRuntime runtime, bool force, bool rerender, string source, SourceSelection selection, ILogger log, CancellationToken ct)
    {
        var planner = runtime.Planner;
        var header = await planner.OpenAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, selection, gate: !force, stored: null, renderEvery: rerender, ct: ct).ConfigureAwait(false);
        var summary = new PlanSummary();
        var sample = new List<PlanSampleEntry>(PlanSampled);
        long shown = 0;
        await foreach (var entry in planner.EntriesAsync(header, null, runtime.Flow.Parallelism.EffectiveRenderThreads, summary, ct).ConfigureAwait(false))
        {
            if (shown++ < PlanEntriesLogged)
            {
                log.LogInformation(RunTrace.Bounded, "{Entry}", PlanFormatting.Describe(entry));
            }

            if (entry.Key is { } key && entry.IsDelivery && sample.Count < PlanSampled)
            {
                sample.Add(new PlanSampleEntry(key.Value, entry.Label, entry.SourceKey, entry.Action.ToString(), entry.Reason, entry.DeliverMetadata, entry.DeliverPayload));
            }
        }

        if (shown > PlanEntriesLogged)
        {
            log.LogInformation("... and {More} more record(s); the counts below cover them all.", shown - PlanEntriesLogged);
        }

        var issues = header.Issues.Select(i => i.ToString()).ToList();
        foreach (var missing in header.Source.MissingKeys)
        {
            issues.Add($"the record table {source} holds no row with key {missing}");
        }

        LogOutcome(log, header.SkippedWholeRun ? $"plan: whole run skipped, {header.SkipReason}" : $"plan: {summary}");
        return new PlanOutcome(
            DeliveryOperations.Plan, source, selection.Describe(), summary.Records, summary.Deliveries, summary.Skips, summary.AwaitingApproval,
            summary.Stale, summary.Holds, summary.Blocked, summary.Untracked, header.Slices, header.SkippedWholeRun, header.SkipReason, issues)
        {
            Metadata = summary.Metadata,
            Payload = summary.Payload,
            RenderedEvery = rerender,
            Sample = sample,
            FailingAssertions = summary.FailingAssertions,
            HeldByAssertions = summary.HeldByAssertions,
        };
    }

    /// <summary>How many of the records a plan would send its outcome names, so a reader sees which ones without the run's log.</summary>
    internal const int PlanSampled = 20;

    /// <summary>
    /// The flow parameter values a submission recorded as JSON (<see cref="RunParameters.ValuesToJson"/>: a parameter given
    /// several values recorded as their array); none when it recorded none.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseValues(string? json)
    {
        try
        {
            return RunParameters.ValuesFromJson(json);
        }
        catch (SqlFlowException ex)
        {
            throw new DeliveryException($"The submission's recorded parameters are not valid: {ex.Message}", ex);
        }
    }

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(recorded, StringComparer.Ordinal);
        foreach (var (name, value) in overrides)
        {
            merged[name] = value;
        }

        return merged;
    }

    /// <summary>
    /// True for a run that starts on the flow as a whole, not on part of what an earlier run began: no submission, no records
    /// and no slices named. The ledger guards of a flow that names its partitions are checked there, once, and not again by
    /// every member and every record-scoped run that follows.
    /// </summary>
    private static bool StartsWholeRun(DeliveryRunPayload payload)
        => payload.SubmissionId is null && payload.RecordKeys.Count == 0 && payload.Slices.Count == 0;

    private static void LogStart(ILogger log, string flow, string operation, string parameters, string actor, Guid runId)
        => log.LogInformation("delivery flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow, operation, parameters, actor, runId);

    private static void LogPartition(ILogger log, string partition)
        => log.LogInformation("delivering to partition '{Partition}': its header, its cache, its ledger and its configuration", partition);

    /// <summary>The route the run delivers by, what it carries there, and why the flow takes it when the flow says.</summary>
    private static void LogRoute(ILogger log, string operation, FlowRuntime runtime)
    {
        var flow = runtime.Flow;
        var kind = runtime.ReadsSource ? $" of {runtime.Mapping.Mapping.Kind}" : string.Empty;
        var reason = flow.RouteReason is { } why ? $" ({why})" : string.Empty;
        log.LogInformation("{Operation}{Kind} by the {Route} route{Reason}.", operation, kind, DeliveryProtocols.Name(flow.Target.Protocol), reason);
    }

    private static void LogSubmission(ILogger log, Guid submissionId, string source)
        => log.LogInformation("working on submission {SubmissionId} of {Source}", submissionId, source);

    private static void LogRedeliver(ILogger log, int marked, int? requested)
    {
        if (requested is { } named)
        {
            log.LogInformation("marked {Marked} of {Requested} record(s) for redelivery", marked, named);
        }
        else
        {
            log.LogInformation(
                "marked every record the flow has delivered, {Marked} in all, for redelivery; this run plans them by key, {PerPass} to a pass",
                marked, RequestedPerPass);
        }
    }

    private static void LogRerender(ILogger log, int asked, int? requested)
    {
        if (requested is { } named)
        {
            log.LogInformation(
                "asked {Asked} of {Requested} record(s) to be brought up to date (a record OSDU does not hold is left as it is); each is rendered again and sent only where it renders differently",
                asked, named);
        }
        else
        {
            log.LogInformation(
                "asked every record the flow has delivered, {Asked} in all, to be brought up to date; this run renders them again by key, {PerPass} to a pass, and sends only what renders differently",
                asked, RequestedPerPass);
        }
    }

    private static void LogOutcome(ILogger log, string outcome)
        => log.LogInformation("{Outcome}", outcome);

    private static void LogDone(ILogger log, string operation, double seconds)
        => log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, seconds);

    private static void LogFailed(ILogger log, string operation, string error, Exception? unexpected)
        => log.LogError(unexpected, "{Operation} failed: {Error}", operation, error);
}

/// <summary>
/// The <c>result</c> of a deliver or replan run: what this run's intake planned and what its drain did (the counts the
/// run row projects), the pass it made for the records waiting to be planned again, how far it fanned out, and the
/// totals of the submission it worked on across every run so far.
/// </summary>
public sealed record DeliverOutcome(
    string Operation,
    Guid SubmissionId,
    string Source,
    string Selection,
    string Status,
    long RecordCount,
    long Planned,
    long SkippedUnchanged,
    long AwaitingApproval,
    long SkippedStale,
    long UnchangedAtPush,
    long Blocked,
    long Delivered,
    long Held,
    long Failed,
    long Retried,
    int Batches,
    int Members,
    bool NothingToDo,
    Guid? RequestedSubmissionId,
    long RequestedRecords,
    string? Error,
    SubmissionTotals Submission,
    long Waiting = 0,
    int RequestedPasses = 0)
{
    /// <summary>
    /// The run's headline count as the platform reads it from the run artifact (<c>result.rowsLoaded</c>), which the
    /// catalog projects onto the run row: the records this run actually delivered. Without it a delivery run would show
    /// no rows at all in the run lists, and a run that delivered thousands would be indistinguishable from an empty one.
    /// </summary>
    public long RowsLoaded => Delivered;

    /// <summary>What the sweep the run ended with undid of unfinished deliveries (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished deliveries and their undo).</summary>
    public UndoSummary Undone { get; init; } = UndoSummary.Empty;

    /// <summary>What the run's before step did (osdu/docs/reference/flow/delivery.md, cache); null when the pipeline names none.</summary>
    public Snapshots.CacheStepOutcome? CacheBefore { get; init; }

    /// <summary>What the run's after step did (osdu/docs/reference/flow/delivery.md, cache); null when the pipeline names none.</summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>
    /// What a part of a split delivery planned of the key slices it delivered, which the delivery's first run adds into the
    /// submission's totals (osdu/docs/reference/concepts/submissions.md, Many nodes); null for a run that delivers no share.
    /// </summary>
    public IntakeOutcome? Share { get; init; }

    /// <summary>The outcome of a part that delivered <paramref name="slices"/>: its own counts, and what it planned of them.</summary>
    public static DeliverOutcome OfShare(RunResult run, string operation, string source, string selection, IReadOnlyList<int> slices)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(slices);
        return From(run, operation, source, selection, Engine.RequestedPasses.None) with { Share = IntakeOutcome.From(run.Intake, source, selection, slices) };
    }

    /// <summary>
    /// What a part reported it planned, read back from its run row (the run's <c>result</c>); null when the row carries
    /// no outcome, or one with no share in it.
    /// </summary>
    public static IntakeOutcome? ShareOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(Share), StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                {
                    return IntakeOutcome.Parse(property.Value.GetRawText());
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A run's counts are its own work (<see cref="RunResult.Own"/>): a run re-sending two records of a delivered submission
    /// reports two, and one that found the submission already completed reports none, while a fan-out root reports the
    /// submission it covers. The submission's own totals come alongside.
    /// </summary>
    public static DeliverOutcome From(RunResult run, string operation, string source, string selection, RequestedPasses requested)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(requested);
        var s = run.Submission;
        var totals = new SubmissionTotals(s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked, s.Delivered, s.Held, s.Failed, s.BatchCount, s.Waiting);
        var own = run.Own;
        return new DeliverOutcome(
            operation, s.SubmissionId, source, selection, s.Status.ToString().ToLowerInvariant(), s.RecordCount,
            own.Planned, own.SkippedUnchanged, own.AwaitingApproval, own.SkippedStale, own.UnchangedAtPush, own.Blocked, own.Delivered, own.Held, own.Failed, own.Retried, own.Batches,
            run.Members, run.Intake.NothingToDo && run.Work.Processed == 0, requested.FirstSubmissionId, requested.Planned, s.Error, totals, own.Waiting,
            requested.Passes)
        {
            Undone = run.Undone,
            CacheBefore = run.CacheBefore,
            CacheAfter = run.CacheAfter,
        };
    }
}

/// <summary>
/// What a deliver run's key-scoped passes over the records the ledger asked to plan again did: how many passes, the
/// submission of the first, how many records they read by key and how many of those they planned.
/// </summary>
public sealed record RequestedPasses(int Passes, Guid? FirstSubmissionId, long Read, long Planned)
{
    /// <summary>
    /// The requests of records that carry no source key, which no pass reads by key: the run reads the whole scope to plan
    /// them, at most <see cref="DeliveryExecutor.MaxKeyless"/> of them settled by one run.
    /// </summary>
    public IReadOnlyList<PlanRequestedRecord> Keyless { get; init; } = [];

    /// <summary>No pass: nothing was asked to be planned again, or the run does not read the whole scope.</summary>
    public static RequestedPasses None { get; } = new(0, null, 0, 0);

    /// <summary>These passes and one more, of <paramref name="submissionId"/>, which read and planned as many records as given.</summary>
    public RequestedPasses Add(Guid submissionId, long read, long planned) => new(Passes + 1, FirstSubmissionId ?? submissionId, Read + read, Planned + planned);
}

/// <summary>A submission's counts across every run that has worked on it; <c>Waiting</c> counts its records still waiting for a record they refer to.</summary>
public sealed record SubmissionTotals(
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, int Batches,
    long Waiting = 0);

/// <summary>The <c>result</c> of a plan run: what a deliver would do, the first records in the run log, the counts here.</summary>
public sealed record PlanOutcome(
    string Operation,
    string Source,
    string Selection,
    long Records,
    long Deliveries,
    long Skips,
    long AwaitingApproval,
    long Stale,
    long Holds,
    long Blocked,
    long Untracked,
    int Slices,
    bool SkippedWholeRun,
    string? SkipReason,
    IReadOnlyList<string> Issues)
{
    /// <summary>Of the deliveries, those that would send the record's document.</summary>
    public long Metadata { get; init; }

    /// <summary>Of the deliveries, those that would send the record's payload.</summary>
    public long Payload { get; init; }

    /// <summary>Whether every record read was rendered, an unchanged one included: a plan of what bringing records up to date would send.</summary>
    public bool RenderedEvery { get; init; }

    /// <summary>The first records the plan would send, at most <see cref="DeliveryExecutor.PlanSampled"/>.</summary>
    public IReadOnlyList<PlanSampleEntry> Sample { get; init; } = [];

    /// <summary>Of the deliveries, those whose document fails an assertion of the mapping (osdu/docs/reference/flow/mapping-assertions.md).</summary>
    public long FailingAssertions { get; init; }

    /// <summary>Of the deliveries, those the check before sending would hold for an assertion whose action is hold.</summary>
    public long HeldByAssertions { get; init; }
}

/// <summary>One record a plan would send: its key, how a reader knows it, what it would send and why.</summary>
public sealed record PlanSampleEntry(Guid Key, string? Label, string SourceKey, string Action, string Reason, bool Metadata, bool Payload);

/// <summary>The <c>result</c> of an intake run (a whole read, or the key slices of a submission), and what a part of a split delivery planned.</summary>
public sealed record IntakeOutcome(
    string Operation,
    Guid SubmissionId,
    string Source,
    string Selection,
    string? Slices,
    long Records,
    long Planned,
    long SkippedUnchanged,
    long AwaitingApproval,
    long SkippedStale,
    long Held,
    long Blocked,
    long Untracked,
    int Batches,
    bool AlreadyProcessed)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static IntakeOutcome From(IntakeResult intake, string source, string selection, IReadOnlyList<int> slices)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(slices);
        var c = intake.Counts;
        return new IntakeOutcome(
            DeliveryOperations.Intake, intake.Submission.SubmissionId, source, selection, slices.Count == 0 ? null : KeySlices.Describe(slices),
            c.Records, c.Planned, c.Skipped, c.AwaitingApproval, c.Stale, c.Held, c.Blocked, c.Untracked, c.Batches, intake.AlreadyProcessed);
    }

    public IntakeCounts ToCounts() => new(Records, Planned, SkippedUnchanged, Held, Blocked, Untracked, Batches, SkippedStale, AwaitingApproval);

    /// <summary>Reads a member run's outcome back from its run row; null when the row carries none or something else.</summary>
    public static IntakeOutcome? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var outcome = JsonSerializer.Deserialize<IntakeOutcome>(json, Json);
            return outcome is { Operation: DeliveryOperations.Intake } ? outcome : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The <c>result</c> of a drain run; <c>Waiting</c> counts the records its claims left waiting for a record they refer to.</summary>
public sealed record DrainOutcome(string Operation, Guid? SubmissionId, long Processed, long Delivered, long Unchanged, long Retried, long Held, long Failed, int Batches, long Waiting = 0)
{
    /// <summary>The run's headline count on the run row (<c>result.rowsLoaded</c>): the records this drain delivered.</summary>
    public long RowsLoaded => Delivered;

    /// <summary>What the sweep a drain of the whole flow ends with undid of unfinished deliveries; empty for a drain of one submission.</summary>
    public UndoSummary Undone { get; init; } = UndoSummary.Empty;

    /// <summary>What the drain's after step did (osdu/docs/reference/flow/delivery.md, cache); null when the pipeline names none.</summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    public static DrainOutcome From(DrainResult drained, Guid? submissionId)
    {
        ArgumentNullException.ThrowIfNull(drained);
        var summary = drained.Work;
        return new DrainOutcome(
            DeliveryOperations.Drain, submissionId, summary.Processed, summary.Delivered, summary.Unchanged, summary.Retried, summary.Held, summary.Failed, summary.Batches, summary.Waiting)
        {
            Undone = drained.Undone,
            CacheAfter = drained.CacheAfter,
        };
    }
}

/// <summary>
/// The <c>result</c> of an undo run (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished deliveries and
/// their undo): the records it took up and what became of what unfinished deliveries left of them.
/// </summary>
public sealed record UndoRunOutcome(string Operation, int Records, int Removed, int Restored, int Gone, int Kept, int Superseded, int Failed)
{
    /// <summary>The run's headline count on the run row (<c>result.rowsLoaded</c>): the artifacts it settled.</summary>
    public long RowsLoaded => Removed + Restored + Gone + Kept + Superseded;

    public static UndoRunOutcome From(string operation, UndoSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new UndoRunOutcome(operation, summary.Records, summary.Removed, summary.Restored, summary.Gone, summary.Kept, summary.Superseded, summary.Failed);
    }

    /// <summary>The run's counts as the summary they came from, so a source's interfaces add up.</summary>
    public UndoSummary ToSummary() => new(Records, Removed, Restored, Gone, Kept, Superseded, Failed);
}

/// <summary>The <c>result</c> of a verify run.</summary>
public sealed record VerifyRunOutcome(string Operation, int Checked, int Matched, int Drifted, int Missing, int Errors, bool Reconcile);

/// <summary>
/// The <c>result</c> of a sync run: what it compared, what it consolidated, and the first source keys of the rows it did not
/// find (<see cref="SourceSyncSummary"/>).
/// </summary>
public sealed record SyncRunOutcome(
    string Operation, int Checked, int InAgreement, int ArrivalsRecorded, int ChangedUnseen, int DeletedUnseen, int PlansRequested,
    int Restored, int NotFound, int NotFoundRecorded, int FoundAgain, int WithoutKey, int NotInLedger, IReadOnlyList<string> NotFoundSample)
{
    public static SyncRunOutcome From(string operation, SourceSyncSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new SyncRunOutcome(
            operation, summary.Checked, summary.InAgreement, summary.ArrivalsRecorded, summary.ChangedUnseen, summary.DeletedUnseen,
            summary.PlansRequested, summary.Restored, summary.NotFound, summary.NotFoundRecorded, summary.FoundAgain, summary.WithoutKey,
            summary.NotInLedger, summary.NotFoundSample);
    }
}

/// <summary>
/// The <c>result</c> of a run deleting the ledger (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the
/// ledger): the partition it confirmed, what it removed from OSDU on the way, and what it deleted of the ledger.
/// </summary>
public sealed record DeleteLedgerOutcome(
    string Operation, string Partition, int Removed, int AlreadyGone, int AlreadyRemoved, int NeverInOsdu,
    int Records, int Submissions, int WorkBatches, int Watermarks, int Reversals)
{
    /// <summary>The run's headline count on the run row (<c>result.rowsLoaded</c>): the records deleted from the ledger.</summary>
    public long RowsLoaded => Records;

    /// <summary>
    /// What was undone before anything was removed (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished
    /// deliveries and their undo): what unfinished deliveries left, and the ids committed deliveries minted that
    /// nothing names once the ledger is gone.
    /// </summary>
    public UndoSummary Undone { get; init; } = UndoSummary.Empty;

    public static DeleteLedgerOutcome From(string operation, LedgerDeleteSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var deleted = summary.Deleted;
        return new DeleteLedgerOutcome(
            operation, summary.Partition, summary.Removed, summary.AlreadyGone, summary.AlreadyRemoved, summary.NeverInOsdu,
            deleted.Records, deleted.Submissions, deleted.WorkBatches, deleted.Watermarks, deleted.Reversals)
        {
            Undone = summary.Undone,
        };
    }

    /// <summary>One line: what went from OSDU and from the ledger.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Removed} removed from OSDU (reversible), {AlreadyGone} already gone, {AlreadyRemoved} removed before, {NeverInOsdu} never in OSDU; {Records} record(s), {Submissions} submission(s) and {Watermarks} watermark(s) deleted from the ledger");
}

/// <summary>
/// The <c>result</c> of a remove run (osdu/docs/reference/concepts/removal-and-reversal.md, Removing records from OSDU): how
/// much of each record it took, how many it selected and what became of them, how many it deleted from the ledger as well,
/// and up to <see cref="RemovalLimits.MaxReported"/> records with their own outcome, failures first. Every record's outcome
/// is on its history whatever the cap leaves out.
/// </summary>
public sealed record RemoveRunOutcome(
    string Operation, string Scope, int Selected, int Removed, int Restored, int AlreadyGone, int Skipped, int Failed, int Purged,
    IReadOnlyList<RemovalRecordResult> Records, bool Truncated)
{
    /// <summary>The run's headline count on the run row (<c>result.rowsLoaded</c>): the records it changed in OSDU.</summary>
    public long RowsLoaded => Removed + Restored;

    /// <summary>What unfinished deliveries of the records left, undone before anything was removed.</summary>
    public UndoSummary Undone { get; init; } = UndoSummary.Empty;

    /// <summary>What the pipeline's after step did with what the removal changed in OSDU (osdu/docs/reference/flow/delivery.md, cache); null when it names none.</summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>The line the activity trail and the run log carry for the whole removal.</summary>
    public string Summary { get; init; } = string.Empty;

    public static RemoveRunOutcome From(string operation, RemovalSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new RemoveRunOutcome(
            operation, RemovalScopes.Wire(summary.Scope), summary.Selected, summary.Removed, summary.Restored, summary.AlreadyGone, summary.Skipped,
            summary.Failed, summary.Purged, summary.Records, summary.Truncated)
        {
            Undone = summary.Undone,
            CacheAfter = summary.CacheAfter,
            Summary = summary.Describe(),
        };
    }
}

/// <summary>
/// A remove run OSDU refused records of: the run fails, so the run board says the removal was not done whole, and its outcome
/// still says what became of every record, the ones it did remove included. Asking again for the records that failed does the
/// rest.
/// </summary>
public sealed class RecordsNotRemovedException : DeliveryException
{
    /// <summary>How many of the failed records the message names, with why each failed.</summary>
    public const int Named = 3;

    public RecordsNotRemovedException(FlowDefinition flow, RemoveRunOutcome outcome)
        : base(Describe(flow, outcome))
    {
        Outcome = outcome;
    }

    public RemoveRunOutcome Outcome { get; }

    private static string Describe(FlowDefinition flow, RemoveRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(outcome);
        var failed = outcome.Records.Where(r => r.Outcome == "failed").Take(Named)
            .Select(r => $"{r.Label ?? r.SourceKey ?? r.DeliveryKey.ToString("D")}: {r.Detail}")
            .ToList();
        var what = outcome.Scope == RemovalScopes.Wire(RemovalChoice.Previous) ? "put back at the version before the latest" : "removed from OSDU";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{outcome.Failed} of {outcome.Selected} record(s) of '{flow.Label}' could not be {what}: {string.Join("; ", failed)}{(outcome.Failed > failed.Count ? $"; and {outcome.Failed - failed.Count} more" : string.Empty)}. {outcome.Summary}. Each record's history says why; ask for the ones that failed again once what refused them is settled.");
    }
}

/// <summary>The <c>result</c> of a run that failed before producing an outcome.</summary>
public sealed record OperationFailure(string Operation, string Error);

/// <summary>
/// The <c>result</c> of a reverse run (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a
/// submission): the reversal and its source, what this run took of it and what came of those records, and the
/// reversal's records by outcome across every run that worked on it.
/// </summary>
public sealed record ReverseRunOutcome(
    string Operation,
    long ReversalId,
    string Source,
    Guid SourceId,
    long Records,
    long Taken,
    long Restored,
    long Removed,
    long Skipped,
    long Failed,
    long Open,
    IReadOnlyDictionary<string, long> Outcomes)
{
    /// <summary>The run's headline count as the platform projects it onto the run row: the records this run put back.</summary>
    public long RowsLoaded => Restored + Removed;

    public static ReverseRunOutcome From(string operation, ReversalSource source, Reversals.ReversalSummary summary)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(summary);
        return new ReverseRunOutcome(
            operation, summary.ReversalId, source.Kind, source.Id, summary.Records, summary.Taken, summary.Restored, summary.Removed, summary.Skipped, summary.Failed,
            summary.Counts.Open, summary.Counts.Outcomes);
    }
}
