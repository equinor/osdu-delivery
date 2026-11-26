using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Probes the target of every active delivery flow on a schedule, so a deployment learns that an OSDU stopped answering
/// without an operator pressing "Probe target" (go-live map OPS-2). Each pass runs the probe the operator's button runs
/// (<see cref="TargetProbes.RunAsync"/>, <c>delivery-probe</c> in this process under the flow's own credentials), once per
/// interface of each flow, since each interface has a target of its own. What comes back is recorded in the ledger's audit
/// trail as a <c>probe</c> activity by <c>service:schedule</c>, as an operator's probe is by the operator, and counted on
/// <c>osdu_delivery.probes</c>, so the last result per flow and interface is readable without asking for anything and an
/// alert can be built on either.
/// </summary>
/// <remarks>
/// <para>Off unless <c>Osdu:TargetProbe:Enabled</c> says otherwise: a pass costs a token exchange and a request against a
/// live OSDU for every interface it covers. <c>:IntervalMinutes</c> paces it (never under
/// <see cref="TargetProbeOptions.MinimumIntervalMinutes"/>), <c>:Pipelines</c> narrows it to named flows and
/// <c>:MaxPerPass</c> bounds one pass of a large estate.</para>
/// <para>A probe answers within its own bounded wait (<see cref="TargetClients.Interactive"/>), so a pass runs each probe,
/// which opens its activity and closes it with what it found, <see cref="Concurrency"/> at a time. A probe's activity still
/// open <see cref="TargetProbes.UnfinishedAfter"/> after it started, scheduled or asked for (the host stopped while it
/// ran), is found again from the ledger by a later pass and closed as unfinished, so nothing stays open forever and nothing
/// is lost across a restart; one younger than that may still be running, here or on another replica, and is left to
/// finish.</para>
/// <para>Hosted on every replica. The control plane runs one today (go-live map OPS-3); were it ever more, each replica
/// would keep its own schedule, which costs one extra probe per interval per replica and records each outcome correctly,
/// since a pass only settles the probes whose activities it can still find open.</para>
/// </remarks>
public sealed partial class ScheduledTargetProbeService : BackgroundService
{
    /// <summary>The activity kind every probe is recorded under, scheduled or not.</summary>
    public const string ActivityKind = TargetProbes.ActivityKind;

    /// <summary>The actor the scheduled probe records its activities under.</summary>
    public const string ScheduleActor = "service:schedule";

    /// <summary>The probes one pass runs at once: enough that a few silent targets do not hold up the estate.</summary>
    public const int Concurrency = 4;

    /// <summary>Delivery pipelines one pass reads. Past this an estate is narrowed with <c>:Pipelines</c>.</summary>
    private const int MaxPipelinesPerPass = 1000;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TargetProbeOptions _options;
    private readonly ILogger<ScheduledTargetProbeService> _logger;

    public ScheduledTargetProbeService(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<TargetProbeOptions> options,
        ILogger<ScheduledTargetProbeService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The floor is the option's own, applied again here so a value that reached the service another way cannot
        // turn the schedule into a flood of live calls.
        var interval = TimeSpan.FromMinutes(Math.Max(TargetProbeOptions.MinimumIntervalMinutes, _options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbePassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the DI container out from under this pass; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogPassError(TargetProbes.Redacted(ex));
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass: close what an earlier pass left open (a probe the host stopped while it ran), then probe every interface
    /// this pass covers, each settled as soon as it answers.
    /// </summary>
    public async Task ProbePassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedgerActivities>();
        var documents = scope.ServiceProvider.GetRequiredService<DeliveryDocumentLoader>();
        var partitions = scope.ServiceProvider.GetRequiredService<IPartitionRegistry>();

        await CloseUnfinishedAsync(ledger, ct).ConfigureAwait(false);
        var interfaces = await CoveredAsync(catalog, documents, partitions, ct).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            interfaces,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (flow, token) =>
            {
                try
                {
                    await ProbeOneAsync(flow, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One flow's target must never stop the rest of the estate being probed. What the probe could record of
                    // the failure it recorded; this says the pass went on without it.
                    LogProbeNotRun(flow.Flow.Label, TargetProbes.Redacted(ex));
                }
            }).ConfigureAwait(false);

        if (interfaces.Count > 0)
        {
            LogProbed(interfaces.Count);
        }
    }

    // ---- Covering ------------------------------------------------------------------------------------------------

    /// <summary>Every interface this pass covers, bound to the partition it delivers to, up to <c>:MaxPerPass</c>.</summary>
    private async Task<List<DeliveryEndpoints.FlowContext>> CoveredAsync(
        CatalogDbContext catalog, DeliveryDocumentLoader documents, IPartitionRegistry partitions, CancellationToken ct)
    {
        var names = _options.PipelineNames();
        var pipelines = await PipelinesAsync(catalog, names, ct).ConfigureAwait(false);
        if (names.Count > 0)
        {
            var missing = names
                .Where(name => !pipelines.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
            {
                LogUnknownPipelines(string.Join(", ", missing));
            }
        }

        var covered = new List<DeliveryEndpoints.FlowContext>();
        var budget = _options.MaxPerPass;
        var capped = false;
        RegisteredPartitions? registry = null;
        foreach (var pipeline in pipelines)
        {
            ct.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                capped = true;
                break;
            }

            var (source, problem) = await DeliveryEndpoints.ResolveSourceAsync(catalog, documents, pipeline.Id, ct).ConfigureAwait(false);
            if (source is null)
            {
                // A flow whose catalog copy does not parse has no target to probe; its pipeline page says the same.
                LogFlowUnreadable(pipeline.Name, problem?.ProblemDetails.Detail ?? "the catalog's copy of the flow could not be read.");
                continue;
            }

            // A flow that works in partitions has a target in each it serves, reached with that partition's configuration:
            // every one is probed, bound to its partition, so a partition whose platform is down is seen as such. The
            // registry is read once a pass, for the first flow that serves it.
            if (source.Source.FollowsRegistry)
            {
                registry ??= await partitions.ReadAsync(ct).ConfigureAwait(false);
            }

            foreach (var flow in source.Source.EveryLedger(registry ?? RegisteredPartitions.None))
            {
                if (budget <= 0)
                {
                    capped = true;
                    break;
                }

                budget--;
                var bound = flow.Partition is { } partition ? source.Source.ForPartition(partition) : source.Source;
                covered.Add(new DeliveryEndpoints.FlowContext(source.Pipeline, bound, flow));
            }
        }

        if (capped)
        {
            LogCapped(_options.MaxPerPass);
        }

        return covered;
    }

    /// <summary>The active delivery pipelines this pass covers, in name order.</summary>
    private static async Task<List<PipelineRef>> PipelinesAsync(CatalogDbContext catalog, IReadOnlyList<string> names, CancellationToken ct)
    {
        var query = catalog.Pipelines.AsNoTracking().Where(p => p.Kind == FlowDefinition.FlowTypeName && p.Active);
        if (names.Count > 0)
        {
            var wanted = names.ToList();
            query = query.Where(p => wanted.Contains(p.Name));
        }

        var rows = await query
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .Take(MaxPipelinesPerPass)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new PipelineRef(r.Id, r.Name)).ToList();
    }

    /// <summary>
    /// Probes one interface through the path the operator's button takes (<see cref="TargetProbes.RunAsync"/>), which records
    /// what it found in the ledger of the partition the interface delivers to under <see cref="ScheduleActor"/>, counts it and
    /// logs it. Each probe has a scope of its own, since probes run side by side and a database context serves one at a time.
    /// </summary>
    private async Task ProbeOneAsync(DeliveryEndpoints.FlowContext flow, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await TargetProbes.RunAsync(
            services.GetRequiredService<CatalogDbContext>(),
            services.GetRequiredService<ILedger>(),
            services.GetRequiredService<DeliveryConfigStore>(),
            services.GetRequiredService<DirectOperations>(),
            services.GetRequiredService<EngineContext>(),
            _clock,
            services.GetRequiredService<ILoggerFactory>(),
            flow,
            ScheduleActor,
            ct).ConfigureAwait(false);
    }

    // ---- Settling ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Closes the probes of every ledger left open past <see cref="TargetProbes.UnfinishedAfter"/>, scheduled or asked for
    /// (<see cref="TargetProbes.CloseUnfinishedAsync"/>), including those of flows no pass covers any more.
    /// </summary>
    private Task CloseUnfinishedAsync(ILedgerActivities ledger, CancellationToken ct)
        => TargetProbes.CloseUnfinishedAsync(
            ledger, null, _clock, _services.GetRequiredService<ILoggerFactory>().CreateLogger(TargetProbes.LogCategory), ct);

    /// <summary>An active delivery pipeline a pass covers.</summary>
    private sealed record PipelineRef(Guid Id, string Name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled probe probed {Probes} target(s).")]
    private partial void LogProbed(int probes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe skipped flow {Flow}: {Detail}")]
    private partial void LogFlowUnreadable(string flow, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe could not probe {Flow}: {Error}")]
    private partial void LogProbeNotRun(string flow, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe covered its whole budget of {MaxPerPass} interface(s) this pass; the rest are probed once the estate is narrowed with Osdu:TargetProbe:Pipelines or the budget is raised.")]
    private partial void LogCapped(int maxPerPass);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Osdu:TargetProbe:Pipelines names no active delivery pipeline: {Names}")]
    private partial void LogUnknownPipelines(string names);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled probe pass error: {Error}")]
    private partial void LogPassError(string error);
}
