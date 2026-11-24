using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// One flow, resolved and ready: parameters applied, render inputs pinned (the mapping from the flow's repository, the
/// template and the cache from the catalog), and (when a ledger and a target are wired) the source, protocol, worker and
/// verifier over them. Every operation an operator can trigger goes through here and is recorded in the ledger's
/// activity trail with the <see cref="Actor"/> that asked for it and the platform <see cref="RunId"/> it ran as. A
/// deliver run coordinates its own fan-out (osdu/docs/reference/concepts/submissions.md, Many nodes): the flow queued again
/// once per share of its key slices, each a run that delivers its share from whichever node takes it, waited for and
/// completed here (FlowRuntime.FanOut.cs).
/// </summary>
public sealed partial class FlowRuntime : IDisposable
{
    /// <summary>How long past a stopped run's lease a run waits before reclaiming it, so the lease has run out by then.</summary>
    private static readonly TimeSpan LeaseExpiryMargin = TimeSpan.FromSeconds(1);

    /// <summary>The owner a removal holds its records under while it takes them out of OSDU.</summary>
    private static readonly string RemovalOwner = Truncate($"{Environment.MachineName}/{Environment.ProcessId}/removal", 120);

    /// <summary>Why a removal passes over a record another lease holds.</summary>
    private const string HeldElsewhere =
        "the record is held under a lease right now (a worker is delivering it, or another removal is taking it out); remove it once that work settles";

    /// <summary>
    /// Submissions no run works on any more that a deliver run reads at a time when it sends what they still hold after its
    /// own; it reads as many such pages as there are submissions to send.
    /// </summary>
    private const int UnattendedSubmissionsPerPage = 10;

    /// <summary>The records waiting to be planned again without a source key that a runtime names; it counts the rest.</summary>
    private const int KeylessNamed = 10;

    /// <summary>How many records deleted from the ledger one settling of a pass names in the log, per outcome, before it only counts them.</summary>
    private const int PurgedNamed = 10;

    /// <summary>How many keyless records this runtime has named so far.</summary>
    private int _keylessNamed;

    private readonly EngineContext _context;
    // Resolved when the runtime opens, and again as a run starts when the partition's cache moved since: a source opens every
    // interface before the first runs, and an interface renders against what the ones before it put into the cache.
    private ResolvedMapping? _mapping;

    /// <summary>The pipeline's cache steps resolved against its partition's cache, once per runtime.</summary>
    private (string Scope, Snapshots.CacheStepPlan Plan)? _cacheSteps;
    private readonly ILogger _log;
    private readonly TargetConnection _target;

    /// <summary>Where the mapping's search sources are answered; disposed with the runtime when it holds anything to release.</summary>
    private readonly IRecordSearch? _search;
    private IDeliveryProtocol? _protocol;
    private IIngestionSource? _source;
    private Planner? _planner;

    /// <summary>True once the mapping's legal tags were asked about for this runtime (a source's preflight asks up front).</summary>
    private bool _legalTagsChecked;
    private bool _restrictedAccessChecked;

    /// <summary>Which records this interface's records wait for, worked out once per runtime.</summary>
    private WaitRules? _waits;

    /// <summary>True once this runtime's ledger is registered in the partition it delivers to.</summary>
    private bool _ledgerRegistered;

    /// <summary>The partition this runtime's ledger is kept under, once it is registered.</summary>
    private string? _ledgerPartition;

    /// <summary>The submissions this runtime holds, created when it first holds one.</summary>
    private SubmissionHolds? _holds;

    /// <summary>
    /// Why a runtime opened for target operations is not bound to its mapping's enrichments: the mapping could not be read or
    /// bound. Only its worker needs the binding, and refuses with this.
    /// </summary>
    private FlowValidationException? _unbound;

    private FlowRuntime(
        EngineContext context,
        FlowDefinition flow,
        DeliveryLayout layout,
        MappingCatalog mappings,
        IReadOnlyDictionary<string, string> parameters,
        ResolvedMapping? mapping,
        TargetConnection target,
        IRecordSearch? search)
    {
        _context = context;
        Flow = flow;
        Layout = layout;
        Mappings = mappings;
        Parameters = parameters;
        _mapping = mapping;
        _target = target;
        _search = search;
        _log = context.Loggers.CreateLogger("run");
    }

    public FlowDefinition Flow { get; }

    public DeliveryLayout Layout { get; }

    public MappingCatalog Mappings { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The pinned render inputs. Only a runtime opened with <see cref="CreateAsync(EngineContext, FlowDefinition, IReadOnlyDictionary{string, string}?, CancellationToken)"/> has them.</summary>
    public ResolvedMapping Mapping => _mapping ?? throw new DeliveryException("This operation renders records and needs the flow's mapping, template and cache; the runtime was opened for target operations only.");

    /// <summary>True when the runtime can read the flow's source and render (deliver, plan, intake), false for target-only work.</summary>
    public bool ReadsSource => _mapping is not null;

    public EngineContext Context => _context;

    /// <summary>Which records this run reads: an incremental window by default, or what the run asked for.</summary>
    public SourceSelection Selection { get; set; } = SourceSelection.Incremental(null);

    /// <summary>Who is asking: for a run, its requester as SQLFlow records it (the token's subject for a run a person
    /// queued, schedule:&lt;name&gt; for one a schedule fired, cli:&lt;user&gt;@&lt;machine&gt; for a direct CLI run); for
    /// an intervention, user:&lt;subject&gt; through the control plane and cli:&lt;user&gt;@&lt;machine&gt; from the
    /// command line (<see cref="RunActors.LocalAccount"/>). Recorded on every activity.</summary>
    public string Actor { get; set; } = "unknown";

    /// <summary>The platform run this runtime executes for, when it is one; stamped on activities and attempts.</summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// The estate commit the run executes, when it is pinned to one (<c>DocumentExecutionOptions.Commit</c>): what a delivery's
    /// reason quotes beside the mapping it rendered with, and what the ledger keeps with each record's render context. Null
    /// for an unpinned run and a direct CLI run.
    /// </summary>
    public string? Commit { get; set; }

    /// <summary>Supplies the captured log for the activity being completed (the executor sets it).</summary>
    public Func<string?>? ActivityLog { get; set; }

    /// <summary>The submission the run works on, when it names one: a re-run, or a fan-out member's.</summary>
    public Guid? SubmissionId { get; set; }

    /// <summary>The key slices a part of a split delivery delivers of the delivery's submission; null reads the whole scope.</summary>
    public IReadOnlyList<int>? Slices { get; set; }

    /// <summary>
    /// Has this run send under <paramref name="runs"/>'s share of the flow's <c>reliability.rateLimitRps</c>: a part of a split
    /// delivery does for the whole of its run (its payload names the share). 1 takes the
    /// whole rate back.
    /// </summary>
    public void ShareRateLimit(int runs) => _target.ShareRateLimit(runs);

    /// <summary>
    /// What judges this run's records for the interface as a whole, or null when nothing does: the planning reports what it
    /// held, the worker every record it settles. Whoever sets it cancels the run's token when it trips.
    /// </summary>
    public FailureGuard? Guard { get; set; }

    /// <summary>
    /// The source document this interface belongs to, when the caller has it (the executor and a source's run set it). A
    /// runtime without it reads the document from the flow's file when it needs the other interfaces.
    /// </summary>
    public SourceDefinition? SourceDocument { get; set; }

    /// <summary>Loads and resolves a flow. Fails at parse time with the file path on every message.</summary>
    public static async Task<FlowRuntime> CreateAsync(EngineContext context, string flowPath, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var flow = context.Documents.LoadFlow(flowPath);
        return await CreateAsync(context, flow, parameters, ct).ConfigureAwait(false);
    }

    public static async Task<FlowRuntime> CreateAsync(EngineContext context, FlowDefinition flow, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);

        // A work location that names a reference (${env:LANDING_URL}/.work/...) is resolved once, as the run opens, with the
        // run's own values, by the rule SQLFlow resolves a file flow's location with; every member of a fan-out resolves it
        // the same way from the configuration it is handed, so all of them write and read the same batches.
        if (SourceLocations.NamesReference(flow.Source.Work))
        {
            var work = await SourceLocations.ResolveAsync(flow.Source.Work, "source.work", context.Secrets, ct).ConfigureAwait(false);
            flow = flow with { Source = flow.Source with { Work = work } };
        }

        var values = FlowParameters.Resolve(flow, parameters);
        var layout = DeliveryLayout.Resolve(flow);
        var mappings = new MappingCatalog(layout.MappingsDirectory, context.Documents);

        // What the mapping adds to each record, bound to what the flow says of its rows, files and workflows: every part of the run
        // reads the flow's enrichments from here (osdu/docs/reference/flow/enrich.md).
        flow = EnrichBinding.Bind(flow, mappings);

        // A search source asks the platform the flow delivers to, under the flow's own auth and partition, over the
        // connection the runtime delivers with; nothing is sent until a render asks its first question.
        var target = new TargetConnection(context, flow);
        IRecordSearch? search = null;
        try
        {
            search = context.RecordSearches.Create(flow, target.ClientAsync, context.Loggers);
            var resolver = new RenderResolver(mappings, context.Cache, context.DocumentTemplates, context.Secrets, search, RenderResolver.RestrictionsOf(context.Ledger));
            var mapping = await resolver.ResolveAsync(flow, ct).ConfigureAwait(false);
            var runtime = new FlowRuntime(context, flow, layout, mappings, values, mapping, target, search);
            if (context.Trace is not null)
            {
                runtime.LogRenderInputs();
            }

            return runtime;
        }
        catch
        {
            (search as IDisposable)?.Dispose();
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A runtime for the operations that touch the target and the ledger but never the source (verify, delete,
    /// release, redeliver, probe, drain): no parameters are required and no mapping is resolved, so they work for a
    /// flow whose parameters are unknown or whose mapping could not render right now. The flow is bound to its mapping's
    /// enrichments when the mapping reads; a drain needs that, and refuses when the mapping does not.
    /// </summary>
    public static FlowRuntime ForTarget(EngineContext context, FlowDefinition flow, IReadOnlyDictionary<string, string>? values = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        var layout = DeliveryLayout.Resolve(flow);
        var mappings = new MappingCatalog(layout.MappingsDirectory, context.Documents);

        // A drain writes what the mapping's enrichments add, so the flow is bound to them as a run's is; only the mapping document
        // is read for that, never its template or the cache. Verify, delete, release and the rest write nothing an enrichment adds,
        // so a mapping that does not read leaves them working, and the worker refuses with the reason.
        FlowValidationException? unbound = null;
        try
        {
            flow = EnrichBinding.Bind(flow, mappings);
        }
        catch (FlowValidationException ex)
        {
            unbound = ex;
        }

        return new FlowRuntime(
            context, flow, layout, mappings,
            values ?? new Dictionary<string, string>(StringComparer.Ordinal), null, new TargetConnection(context, flow), null)
        {
            _unbound = unbound,
        };
    }

    /// <summary>The flow's ingestion tables, opened once per runtime with the flow's own connection reference.</summary>
    public IIngestionSource Source => _source ??= _context.Sources.Open(Flow, Parameters, _context.Loggers);

    public Planner Planner => _planner ??= new Planner(Source, _context.Payloads, _context.Ledger, _context.Loggers.CreateLogger<Planner>()) { Commit = Commit };

    public SubmissionIntake Intake => new(RequireLedger(), Planner, _context.Stores, _context.Time, _context.Listener, _context.Loggers.CreateLogger<SubmissionIntake>())
    {
        RunId = RunId,
        Trace = _context.Trace,
        Target = ProtocolAsync,
        Holds = HeldSubmissions,
    };

    /// <summary>
    /// The submissions this runtime works on, each held while it does (<see cref="SubmissionHolds"/>), so no later run takes
    /// over a submission this one is planning or sending. An operation lets go of what it held when it ends.
    /// </summary>
    internal SubmissionHolds HeldSubmissions => _holds ??= new SubmissionHolds(RequireLedger());

    /// <summary>What this run asks the intake for: its selection, the submission it works on, and a member's slices.</summary>
    public IntakeRequest Request => new(Selection, SubmissionId, Slices);

    public async Task<IDeliveryProtocol> ProtocolAsync(CancellationToken ct = default)
    {
        if (_protocol is not null)
        {
            return _protocol;
        }

        _protocol = await _context.Protocols.CreateAsync(Flow, _target.Http, _context.Loggers, ct).ConfigureAwait(false);
        return _protocol;
    }

    /// <summary>
    /// Says on the run's trace what this runtime renders with: the mapping, the template it pins, the cache version it
    /// reads and the partition it mints ids in. The mapping's other parameter values stay off the trace, since a flow can
    /// supply a secret reference as one.
    /// </summary>
    private void LogRenderInputs()
    {
        var resolved = Mapping;
        var context = resolved.Context;
        var cache = context.CacheScope is { } scope
            ? $"the cache of {scope} at version {context.CacheVersion}"
            : "no cache";
        _log.LogInformation(
            "Rendering with mapping {Mapping} (template {Template}) and {Cache}; ids are minted in partition {Partition}.",
            resolved.Mapping.Reference, resolved.Mapping.Template, cache,
            context.Parameters.GetValueOrDefault(RenderContext.DataPartitionParameter, "(not supplied)"));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>The connection this runtime reaches the flow's target through, under the flow's credentials and partition.</summary>
    public Task<OsduHttpClient> TargetClientAsync(CancellationToken ct = default) => _target.ClientAsync(ct);

    /// <summary>How an activity records what it was asked: run values as every recorded run value is, a list as its array.</summary>
    private static readonly JsonSerializerOptions ActivityParametersJson = new() { Converters = { new RunValuesJsonConverter() } };

    /// <summary>
    /// Runs one operation under its activity: started before it, completed with what the operation says of itself (its
    /// summary, the submission it worked on, and whether it changed nothing), or settled as failed or cancelled. Only a run
    /// can be idle; an intervention (sync, release, redeliver, delete) always says false, so the trail always shows it.
    /// </summary>
    private Task<T> TrackAsync<T>(string kind, object? parameters, DeliveryKey? key, Func<Task<(T Result, string Summary, Guid? SubmissionId, bool Idle)>> action, CancellationToken ct)
        => TrackAsync(kind, parameters, key, _ => action(), ct);

    /// <summary>
    /// As the overload above, handing the action the id of the activity that records it (null with no ledger), so an
    /// intervention that changes many records can name each one under it.
    /// </summary>
    private async Task<T> TrackAsync<T>(string kind, object? parameters, DeliveryKey? key, Func<long?, Task<(T Result, string Summary, Guid? SubmissionId, bool Idle)>> action, CancellationToken ct)
    {
        var ledger = _context.Ledger;
        if (ledger is null)
        {
            return (await action(null).ConfigureAwait(false)).Result;
        }

        // Every operation registers its ledger before it writes a row of it, the activity that records it included.
        await RegisterLedgerAsync(ledger, ct).ConfigureAwait(false);
        var started = _context.Time.GetUtcNow().UtcDateTime;
        var activity = await ledger.StartActivityAsync(new ActivityRecord
        {
            FlowId = Flow.Id,
            FlowName = Flow.Label,
            Kind = kind,
            Actor = Actor,
            StartedUtc = started,
            ParametersJson = parameters is null ? null : JsonSerializer.Serialize(parameters, ActivityParametersJson),
            DeliveryKey = key?.Value,
            RunId = RunId,
        }, ct).ConfigureAwait(false);

        try
        {
            var (result, summary, submissionId, idle) = await action(activity.ActivityId).ConfigureAwait(false);
            await CompleteActivityAsync(ledger, activity.ActivityId, "completed", summary, submissionId, idle).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SettleAsync(ledger, activity.ActivityId, "cancelled", "cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await SettleAsync(ledger, activity.ActivityId, "failed", HeaderRedaction.RedactMessage(ex.Message)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Registers this interface's ledger in the partition this runtime delivers to, once, before the first row of it is
    /// written (<see cref="LedgerRegistration"/>): the partition the flow is bound to, or for a flow whose partition is its
    /// data-partition-id header, the one the header resolves to here, which the ledger refuses when the ledger belongs to
    /// another. A runtime opened for target operations whose header does not resolve here takes the partition the
    /// directory holds, so a release or a redelivery of records a run delivered is not stopped by a reference only the
    /// nodes resolve; whatever then calls the target resolves the header itself.
    /// </summary>
    private async Task RegisterLedgerAsync(ILedger ledger, CancellationToken ct)
    {
        if (_ledgerRegistered)
        {
            return;
        }

        var entry = await LedgerRegistration.RegisterAsync(ledger, Flow, _context.Secrets, keptWhenUnresolved: !ReadsSource, ct).ConfigureAwait(false);
        _ledgerPartition = entry.Partition;
        _ledgerRegistered = true;
    }

    /// <summary>
    /// Ends an activity the operation did not finish. Every start is settled, whatever was thrown and from wherever in
    /// the engine, so an action never keeps reading as running once its run is over; a ledger that cannot be written
    /// here is logged and never replaces the failure the caller is about to see.
    /// </summary>
    private async Task SettleAsync(ILedger ledger, long activityId, string outcome, string summary)
    {
        try
        {
            await CompleteActivityAsync(ledger, activityId, outcome, summary, null, idle: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            _log.LogWarning("Could not settle activity {ActivityId} as {Outcome} in the ledger: {Message}", activityId, outcome, ex.Message);
        }
    }

    /// <summary>Closes an activity; the submission it worked on is on the row itself, where every view of it links it.</summary>
    private async Task CompleteActivityAsync(ILedger ledger, long activityId, string outcome, string summary, Guid? submissionId, bool idle)
    {
        var log = ActivityLog?.Invoke();
        await ledger.CompleteActivityAsync(activityId, outcome, summary, log, _context.Time.GetUtcNow().UtcDateTime, submissionId, idle, CancellationToken.None).ConfigureAwait(false);
    }

    private Task EmitAsync(string kind, IReadOnlyList<DeliveryKey>? keys, string detail, CancellationToken ct)
        => EmitAsync(kind, keys, detail, "all blocked records", ct);

    /// <summary>
    /// Tells the listeners what an intervention did: one event per record it named, or with no keys one event for the flow,
    /// its detail saying which records <paramref name="reached"/> describes.
    /// </summary>
    private async Task EmitAsync(string kind, IReadOnlyList<DeliveryKey>? keys, string detail, string reached, CancellationToken ct)
    {
        if (keys is null)
        {
            await _context.Listener.OnEventAsync(new DeliveryEvent { AtUtc = _context.Time.GetUtcNow().UtcDateTime, FlowId = Flow.Id, FlowName = Flow.Label, Interface = Flow.Interface, Kind = kind, Worker = Actor, Detail = $"{detail} ({reached})" }, ct).ConfigureAwait(false);
            return;
        }

        foreach (var key in keys)
        {
            await _context.Listener.OnEventAsync(new DeliveryEvent { AtUtc = _context.Time.GetUtcNow().UtcDateTime, FlowId = Flow.Id, FlowName = Flow.Label, Interface = Flow.Interface, Kind = kind, DeliveryKey = key, Worker = Actor, Detail = detail }, ct).ConfigureAwait(false);
        }
    }

    private ILedger RequireLedger()
        => _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);

    public void Dispose()
    {
        _announcing.Dispose();
        _holds?.Dispose();
        (_search as IDisposable)?.Dispose();
        _target.Dispose();
    }
}
