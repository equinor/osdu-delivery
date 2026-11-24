using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Retrieval;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one retrieval flow document as one platform run. The run's parameters select the operation: retrieve (the
/// default; a run triggered without an operation retrieves) or plan (count what the query matches, write nothing);
/// force restarts an incremental flow at its declared start. The engine's log becomes the run log and the live
/// trace, the outcome becomes <c>run.json</c>, and the ledger row the run opens carries the run id.
/// </summary>
public sealed class RetrievalExecutor : KindExecutor<RetrievalFlowDocument, RetrievalDefinition>
{
    public RetrievalExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => RetrievalDefinition.FlowTypeName;

    protected override RetrievalDefinition FlowOf(RetrievalFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(RetrievalDefinition flow) => flow.Name;

    protected override string TelemetryOperation(RetrievalDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => Operation(parameters);

    // A cache and a retrieval flow name their platform and partition the same way a delivery flow does, so a run
    // of one resolves them from the central configuration the control plane supplied before the node's own.
    protected override EngineContext ContextFor(EngineContext context, DocumentExecutionOptions options)
        => context.WithSuppliedReferences(DeliveryRunPayload.Parse(options.Parameters).References);

    /// <summary>The operation a retrieval flow runs: retrieve (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Retrieve) switch
        {
            DeliveryOperations.Retrieve => DeliveryOperations.Retrieve,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A retrieval flow runs the {DeliveryOperations.Retrieve} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }

    /// <summary>
    /// Registers a retrieval flow's ledger in the partition its <c>data-partition-id</c> header resolves to on this node, so
    /// every retrieval it records is kept under that partition (osdu/docs/reference/concepts/ledger.md, Ledgers, flows
    /// and partitions).
    /// </summary>
    private static async Task RegisterLedgerAsync(EngineContext context, Ledger.ILedger ledger, RetrievalDefinition flow, CancellationToken ct)
    {
        var declared = flow.Source.Headers.FirstOrDefault(h => h.Key.Equals(SqlFlow.Delivery.Snapshots.CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new SqlFlowException(
                $"Retrieval flow '{flow.Name}' names no '{SqlFlow.Delivery.Snapshots.CacheScope.PartitionHeader}' in its source.headers, so the partition its runs are kept under is unknown.");
        }

        var partition = SqlFlow.Delivery.Snapshots.CacheScope.Normalize(await context.Secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{flow.Name}: source.headers");
        await ledger.RegisterLedgerAsync(
            new Ledger.LedgerEntry
            {
                FlowId = flow.Id,
                Partition = partition,
                Kind = Ledger.LedgerKinds.Retrieval,
                FlowName = flow.Name,
                LedgerName = flow.Name,
            },
            ct).ConfigureAwait(false);
    }

    protected override async Task<object> ExecuteOperationAsync(EngineContext context, RetrievalDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        var forced = RetrievalFlowKind.Forced(parameters);

        // A retrieval flow reads the partition its headers name; it names no partitions, so a run targeting one (a schedule
        // whose values name a partition for its delivery and cache flows) is told so rather than refused as a stray parameter.
        if (!flow.Parameters.ContainsKey(PartitionNames.RunValue) && parameters.Values.TryGetValue(PartitionNames.RunValue, out var partition))
        {
            throw new SqlFlowException(
                $"Retrieval flow '{flow.Name}' names no partitions: it retrieves from the partition its source.headers name, so a run cannot target '{partition}'. Leave the partition out of its run.");
        }

        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, parameters.Values);

        // A retrieval keeps its runs in its ledger, in the partition its header resolves to: registered before the first.
        if (operation == DeliveryOperations.Retrieve && context.Ledger is { } ledger)
        {
            await RegisterLedgerAsync(context, ledger, flow, ct).ConfigureAwait(false);
        }

        using var http = new HttpRuntime(flow.Reliability, context.Secrets, context.Time, allowLoopback: EngineContext.LoopbackAllowed, observer: context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers, context.Secrets, ct).ConfigureAwait(false);
        var runner = new RetrievalRunner(flow, values, client, context.Stores, context.Ledger, context.Time, context.Loggers.CreateLogger<RetrievalRunner>());

        if (operation == DeliveryOperations.Plan)
        {
            var (window, estimates) = await runner.EstimateAsync(forced, ct).ConfigureAwait(false);
            foreach (var estimate in estimates)
            {
                log.LogInformation("plan {Kind}: {Total} matching record(s) (query: {Query})", estimate.Kind, estimate.TotalCount, estimate.Query ?? "none");
            }

            var total = estimates.Sum(e => e.TotalCount);
            log.LogInformation("plan: {Total} record(s) would be retrieved into {Location}", total, runner.Location(runId, context.Time.GetUtcNow().UtcDateTime));
            return new RetrievalPlanOutcome(DeliveryOperations.Plan, window?.Field, window?.From, window?.To, runner.Location(runId, context.Time.GetUtcNow().UtcDateTime), estimates, total);
        }

        var result = await runner.RunAsync(runId, actor, forced, ct).ConfigureAwait(false);
        return RetrieveOutcome.From(result);
    }



}

/// <summary>The <c>result</c> of a retrieve run: where the files went, the window, and the counts the ledger row carries.</summary>
public sealed record RetrieveOutcome(
    string Operation,
    long RetrievalId,
    string Location,
    string? Manifest,
    string? WindowField,
    DateTime? WindowFrom,
    DateTime? WindowTo,
    long Records,
    int Files,
    long Bytes,
    bool NothingToDo,
    IReadOnlyList<RetrievedKind> Kinds)
{
    public static RetrieveOutcome From(RetrievalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RetrieveOutcome(
            DeliveryOperations.Retrieve, result.RetrievalId, result.Location, result.ManifestLocation, result.Window?.Field, result.Window?.From, result.Window?.To,
            result.Records, result.Files, result.Bytes, result.NothingToDo, result.Kinds);
    }
}

/// <summary>The <c>result</c> of a plan run on a retrieval flow: what the query matches per kind, and where a retrieve would write.</summary>
public sealed record RetrievalPlanOutcome(
    string Operation, string? WindowField, DateTime? WindowFrom, DateTime? WindowTo, string Location, IReadOnlyList<RetrievalEstimate> Kinds, long Total);
