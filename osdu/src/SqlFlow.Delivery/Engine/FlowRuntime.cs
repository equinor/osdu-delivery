using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

/// <summary>The shared, flow-independent services the engine is composed from.</summary>
public sealed record EngineContext(
    DeliveryDocumentLoader Documents,
    IIngestionSourceFactory Sources,
    IPayloadFiles Payloads,
    FileStoreRegistry Stores,
    ISecretResolver Secrets,
    ILedger? Ledger,
    TimeProvider Time,
    ILoggerFactory Loggers,
    IProtocolFactory Protocols,
    IDeliveryListener Listener,
    IFanOutDispatcher? FanOut = null,
    ICacheStore? Cache = null,
    IRecordSearchFactory? Searches = null,
    RunTrace? Trace = null,
    IPartitionRegistry? Partitions = null,
    Operations.TargetClients? Clients = null,
    IOfficialExamples? Examples = null,
    Search.PartitionSchemaIndexes? SchemaIndexes = null)
{
    /// <summary>The environment switch that lets a flow target a loopback address (local OSDU emulators, tests).</summary>
    public const string AllowLoopbackVariable = "SQLFLOW_DELIVERY_ALLOW_LOOPBACK";

    /// <summary>Whether flows may target a loopback address in this process (the switch above, read once per check).</summary>
    public static bool LoopbackAllowed
        => Environment.GetEnvironmentVariable(AllowLoopbackVariable) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fan-out dispatcher, never null: a run the platform gave no fan-out does its work itself.</summary>
    public IFanOutDispatcher Dispatcher => FanOut ?? NoFanOutDispatcher.Instance;

    /// <summary>
    /// Where a repository document's templates are read (osdu/docs/reference/concepts/templates.md, Template folders): the
    /// template folders on the document's path, which every host reads. A run, a check and a lineage scan read here.
    /// </summary>
    public global::SqlFlow.Delivery.Templates.ITemplateSource DocumentTemplates { get; init; } = global::SqlFlow.Delivery.Templates.FolderTemplates.Default;

    /// <summary>
    /// Where a synced repository's files are on this host (<see cref="Catalog.IRepositoryFolders"/>): what reads a template of
    /// a repository other than the one a run executes from (a mapping an assertion names by its reference, the explorer)
    /// reads its file there. A host with no catalog knows none.
    /// </summary>
    public Catalog.IRepositoryFolders RepositoryFolders { get; init; } = Catalog.RepositoryFolders.None;

    /// <summary>Where a flow's search sources are answered, never null: the platform the flow delivers to, unless composed otherwise.</summary>
    public IRecordSearchFactory RecordSearches => Searches ?? PlatformRecordSearchFactory.Instance;

    /// <summary>
    /// The partition registry, never null: the module's database's when the host has one, and otherwise one that says, when a
    /// run needs it, that the host has none. A flow that names its partitions or a header partition never reads it.
    /// </summary>
    public IPartitionRegistry PartitionRegistry => Partitions ?? UnavailablePartitionRegistry.Instance;

    /// <summary>
    /// A context for one run: everything the engine logs while it serves the run (the source, the planner, the intake, the
    /// protocols, the workers, the calls to OSDU) goes to <paramref name="loggers"/>, the run's log and live trace, through
    /// the gate of a <see cref="RunTrace"/> of its own, which bounds what the run writes whatever its size. A context that
    /// serves no run has no trace, and writes no line per record or call.
    /// </summary>
    public EngineContext ForRun(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        var trace = new RunTrace(Time);
        return this with { Loggers = new RunTraceGate(loggers, trace), Trace = trace };
    }

    /// <summary>What watches the calls this context sends to OSDU: the run's trace, or nobody outside a run.</summary>
    public IHttpObserver? HttpObserver => Trace is { } trace ? new RunHttpTrace(trace, Loggers.CreateLogger<RunHttpTrace>()) : null;

    /// <summary>
    /// A context whose references resolve from <paramref name="supplied"/>, the central configuration of the partition it
    /// acts in, before anything else: every resolution runs inside a scope of these values (<see cref="SuppliedReferences"/>),
    /// laid over the values the run itself carries for every kind and, under both, the node's own environment. Given twice,
    /// the second set is laid over the first. A context given nothing still resolves inside a scope, so a run says on its
    /// trace where each reference took its value, the node's environment included: where a record goes and under whose
    /// access and legal terms (the endpoint, the partition, the ACL groups, the legal tag) are such references.
    /// </summary>
    /// <exception cref="SqlFlowException">The values break a rule a run's references are held to (<see cref="SuppliedReferences.Validate"/>).</exception>
    public EngineContext WithSuppliedReferences(IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        var (node, held) = Secrets is ScopedReferenceResolver scoped
            ? (scoped.Node, Over(scoped.Values, supplied))
            : (Secrets, supplied);
        SuppliedReferences.Validate(held, "the central configuration");
        return this with { Secrets = new ScopedReferenceResolver(held, node), Supplied = held };
    }

    /// <summary>
    /// The central configuration this context resolves with ahead of the run's own values and the node
    /// (<see cref="WithSuppliedReferences"/>): what a member run it queues carries on, so a member resolves every reference
    /// exactly as the run that queued it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Supplied { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// Every value this context resolves with right now: the run's own (the scope the executing run opened) with this
    /// context's laid over them. What tells two targets apart, and what is never written anywhere but hashed.
    /// </summary>
    public IReadOnlyDictionary<string, string> Effective => Over(SuppliedReferences.Current, Supplied);

    private static Dictionary<string, string> Over(IReadOnlyDictionary<string, string> under, IReadOnlyDictionary<string, string> over)
    {
        var merged = new Dictionary<string, string>(under, StringComparer.Ordinal);
        foreach (var (name, value) in over)
        {
            merged[name] = value;
        }

        return merged;
    }

    /// <summary>A context with the fan-out the platform handed this run; every run gets its own.</summary>
    public EngineContext WithFanOut(IFanOutDispatcher? dispatcher) => this with { FanOut = dispatcher };

    /// <summary>A context whose loggers say, on every line, that it is about <paramref name="interfaceName"/>.</summary>
    public EngineContext ForInterface(string interfaceName) => this with { Loggers = new InterfaceLoggerFactory(Loggers, interfaceName) };
}

/// <summary>
/// The resolver of a context given values of its own (<see cref="EngineContext.WithSuppliedReferences"/>): each resolution
/// runs inside a scope of them (<see cref="SuppliedReferences.Supply"/>), nested over whatever scope the executing run
/// opened, so the context's values win over the run's, neither chains into the other, and the run's own report says where
/// each value came from. Values only travel here as references and settings; a written-out credential never gets this far.
/// </summary>
internal sealed class ScopedReferenceResolver(IReadOnlyDictionary<string, string> values, ISecretResolver node) : ISecretResolver
{
    /// <summary>The values every resolution is scoped with.</summary>
    public IReadOnlyDictionary<string, string> Values { get; } = values;

    /// <summary>The process's own resolver, which reads the scope.</summary>
    public ISecretResolver Node { get; } = node;

    public string Resolve(string value)
    {
        using var scope = SuppliedReferences.Supply(Values);
        return Node.Resolve(value);
    }

    public async Task<string> ResolveAsync(string value, CancellationToken ct = default)
    {
        using var scope = SuppliedReferences.Supply(Values);
        return await Node.ResolveAsync(value, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// What a drain did: what its worker sent, and what the sweep a drain of the whole flow ends with undid of unfinished
/// deliveries (osdu/docs/reference/concepts/artifacts-and-undo.md, When the undo runs, and what it is given); a drain of
/// one submission does not sweep, and its undo is empty.
/// </summary>
public sealed record DrainResult(WorkerSummary Work, UndoSummary Undone)
{
    /// <summary>What the drain's after step did (osdu/docs/reference/flow/delivery.md, cache); null when the pipeline names none.</summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    public override string ToString() => Undone.Idle ? Work.ToString() : $"{Work}; {Undone.Describe()}";
}

/// <summary>What a deliver run did: the intake, the drain, the submission it left, and how far it fanned out.</summary>
public sealed record RunResult(IntakeResult Intake, WorkerSummary Work, SubmissionState Submission, int Members = 0)
{
    /// <summary>
    /// What the sweep the run ended with undid of unfinished deliveries
    /// (osdu/docs/reference/concepts/artifacts-and-undo.md, When the undo runs, and what it is given).
    /// </summary>
    public UndoSummary Undone { get; init; } = UndoSummary.Empty;

    /// <summary>
    /// What the run's before step did (osdu/docs/reference/flow/delivery.md, cache): the types it brought up to date from
    /// OSDU before it rendered. Null when the pipeline names none.
    /// </summary>
    public Snapshots.CacheStepOutcome? CacheBefore { get; init; }

    /// <summary>
    /// What the run's after step did (osdu/docs/reference/flow/delivery.md, cache): the records it changed in OSDU, read back
    /// into the types it names. Null when the pipeline names none.
    /// </summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>
    /// What the run did itself: a run re-sending two records of a delivered submission counts two, and one that found the
    /// submission already completed counts none. A fan-out root is the exception, because its members' planning and
    /// deliveries are summed only in the submission, so it counts the submission it covers.
    /// </summary>
    public RunCounts Own => Members > 0
        ? new RunCounts(
            Submission.Planned, Submission.SkippedUnchanged, Submission.AwaitingApproval, Submission.SkippedStale, Submission.UnchangedAtPush,
            Submission.Blocked, Submission.Delivered, Submission.Held, Submission.Failed, Work.Retried, Submission.BatchCount, Submission.Waiting)
        : new RunCounts(
            Intake.Counts.Planned, Intake.Counts.Skipped, Intake.Counts.AwaitingApproval, Intake.Counts.Stale, Work.Unchanged, Intake.Counts.Blocked,
            Work.Delivered, Intake.Counts.Held + Work.Held, Work.Failed, Work.Retried, Intake.Counts.Batches, Work.Waiting);

    /// <summary>
    /// Whether the run changed nothing: it planned, held, blocked and sent no record, and left none waiting on an approval
    /// or on a record it refers to. Rows it read and found unchanged or stale change nothing. The audit trail leaves such a
    /// run out unless asked for it.
    /// </summary>
    public bool Idle => Work.Processed == 0 && Undone.Idle
        && Own is { Planned: 0, AwaitingApproval: 0, UnchangedAtPush: 0, Blocked: 0, Delivered: 0, Held: 0, Failed: 0, Retried: 0, Waiting: 0 };
}

/// <summary>
/// The counts of what one deliver run did (<see cref="RunResult.Own"/>). <c>SkippedUnchanged</c> and <c>SkippedStale</c>
/// count rows it read and had no reason to send; <c>UnchangedAtPush</c> the records the final hash check found OSDU
/// already holding, settled without sending anything.
/// </summary>
public sealed record RunCounts(
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held,
    long Failed, long Retried, int Batches, long Waiting)
{
    /// <summary>The counts that are not zero, as the audit trail shows the run: "3 delivered, 2 unchanged".</summary>
    public override string ToString() => CountLine.Of(
        "nothing to deliver",
        (Planned, "planned"), (Delivered, "delivered"), (SkippedUnchanged + UnchangedAtPush, "unchanged"), (AwaitingApproval, "awaiting approval"),
        (SkippedStale, "stale"), (Blocked, "blocked"), (Held, "held"), (Failed, "failed"), (Retried, "retrying"), (Waiting, "waiting"));
}

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
            var resolver = new RenderResolver(mappings, context.Cache, context.DocumentTemplates, context.Secrets, search);
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

    /// <summary>
    /// What this runtime's interface delivers and what its records refer to: the kind its mapping fills and the
    /// relationships of the properties it fills, read from the template the mapping pins
    /// (osdu/docs/reference/flow/interfaces.md, Order). A runtime opened for target operations reads the mapping and its
    /// template here.
    /// </summary>
    public async Task<InterfaceSchema> SchemaAsync(CancellationToken ct = default)
    {
        var name = Flow.Interface ?? Flow.Name;
        if (_mapping is { } resolved)
        {
            return InterfaceSchemas.Describe(name, resolved.Mapping, Templates.OsduTemplate.From(resolved.Schema));
        }

        var mapping = Mappings.Load(Flow.Render.Mapping);
        var template = await _context.DocumentTemplates.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        var schema = template.Schema ?? throw new FlowValidationException(
            $"{KeyPaths.Where(Flow)}: a source's interfaces are ordered by the relationships in the templates their mappings fill, and mapping {mapping.Reference} has none to read: {template.Problem}");
        return InterfaceSchemas.Describe(name, mapping, Templates.OsduTemplate.From(schema));
    }

    /// <summary>The flow's ingestion tables, opened once per runtime with the flow's own connection reference.</summary>
    public IIngestionSource Source => _source ??= _context.Sources.Open(Flow, Parameters, _context.Loggers);

    public Planner Planner => _planner ??= new Planner(Source, _context.Payloads, _context.Ledger, _context.Loggers.CreateLogger<Planner>());

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

    /// <summary>Reads the source and reports what would change, every entry collected. Changes nothing, records nothing.</summary>
    public Task<DeliveryPlan> PlanAsync(bool force = false, CancellationToken ct = default)
        => Planner.PlanAsync(Flow, Mapping, Parameters, Selection, force, ct);

    /// <summary>
    /// Checks that the flow's route can deliver <paramref name="kind"/>, the kind its mapping renders
    /// (<see cref="RouteChecks"/>). A flow on the ddms route that names a DDMS by its registration has its protocol built
    /// first, which reads the registration, so the check sees what the DDMS registered. A flow on the dspdm route has its
    /// business object read from DSPDM's metadata, which says whether it exists and how its rows are found again.
    /// </summary>
    public async Task CheckRouteAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (Flow.Target.Protocol == DeliveryProtocol.Dspdm)
        {
            RouteChecks.Check(Flow, kind);
            if (await ProtocolAsync(ct).ConfigureAwait(false) is OsduDspdmProtocol dspdm)
            {
                await dspdm.CheckKindAsync(kind, ct).ConfigureAwait(false);
            }

            return;
        }
        if (DeliveryProtocols.ReachesDdms(Flow.Target.Protocol) && DdmsDiscovery.Needed(Flow))
        {
            var routing = await ProtocolAsync(ct).ConfigureAwait(false) switch
            {
                OsduDdmsProtocol ddms => ddms.Routing,
                OsduFileAndDdmsProtocol files => files.Routing,
                OsduManifestAndDdmsProtocol manifests => manifests.Routing,
                _ => null,
            };
            if (routing is not null)
            {
                RouteChecks.Check(Flow, kind, routing);
                return;
            }
        }

        RouteChecks.Check(Flow, kind);
    }

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

    public Task<DeliveryWorker> WorkerAsync(CancellationToken ct = default) => WorkerAsync(null, null, ct);

    /// <summary>
    /// A worker of this run, working on <paramref name="batches"/> alone and reading them from <paramref name="workRoot"/>
    /// when they are given: a part's, on the batches of its slices in the folder it wrote them to.
    /// </summary>
    private async Task<DeliveryWorker> WorkerAsync(BatchRange? batches, string? workRoot, CancellationToken ct)
    {
        if (_unbound is { } unbound)
        {
            // Pending work carries what the mapping's enrichments add to each record, which only a flow bound to them writes.
            throw new FlowValidationException(
                $"{Flow.SourcePath ?? Flow.Name}: the pending work is not sent, since it is written with what the mapping {Flow.Render.Mapping} adds to each record through its enrichments, and the flow could not be bound to them: {unbound.Message}",
                unbound);
        }

        return new(RequireLedger(), _context.Payloads, _context.Stores, await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<DeliveryWorker>())
        {
            RunId = RunId,
            Guard = Guard,
            Trace = _context.Trace,
            Partition = _ledgerPartition,
            Waits = await WaitRulesAsync(ct).ConfigureAwait(false),
            Gate = await GateAsync(ct).ConfigureAwait(false),
            Batches = batches,
            WorkRoot = workRoot,
        };
    }

    /// <summary>
    /// The gate every document of the flow goes through before it is sent (<see cref="ValidationGate"/>): its policy is the
    /// flow's <c>target.validation</c>; a document is checked against the template of its kind at the version it was rendered
    /// for, the runtime's own when that is the one, else that version from the template folders on the mapping's path; and the records it refers to are looked up in the
    /// ledger, then in OSDU's storage service under <c>target.verifyReferences: storage</c>, else in the cache the runtime
    /// renders with.
    /// </summary>
    public async Task<ValidationGate> GateAsync(CancellationToken ct = default)
    {
        var ledger = RequireLedger();
        var cache = _mapping?.References;
        var label = _mapping?.Context is { } context
            ? context.CacheScope is null ? $"cache version {context.CacheVersion}" : $"version {context.CacheVersion} of the cache of partition '{context.CacheScope}'"
            : null;
        ReferenceResolver references;
        if (Flow.Target.VerifyReferences == ReferenceVerification.Storage)
        {
            var client = await _target.ClientAsync(ct).ConfigureAwait(false);
            references = new ReferenceCheck(ledger, client, Flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath)
                .Resolver(Flow.Id, cache, label);
        }
        else
        {
            references = new ReferenceResolver(cache, label, (ids, token) => ledger.HeldIdsAsync(Flow.Id, ids, token), osdu: null);
        }

        return new ValidationGate(Flow, SchemaOfAsync, references, _context.Time);
    }

    /// <summary>
    /// The schema of <paramref name="kind"/> at <paramref name="version"/>: the runtime's own template when it is that one,
    /// else that version as the template folders on the mapping's path hold it. A version is the hash of its content, so
    /// every file of it is the same schema: a record rendered before its template's file changed is still checked against
    /// the version it was rendered for while a folder on the mapping's path holds it, and is unverified once none does.
    /// Without a version, the runtime's own template of that kind. Null when nothing holds it.
    /// </summary>
    private async Task<SchemaSnapshot?> SchemaOfAsync(string kind, string? version, CancellationToken ct)
    {
        if (_mapping?.Schema is { } own && string.Equals(own.Kind, kind, StringComparison.Ordinal)
            && (version is null || string.Equals(own.Version, version, StringComparison.Ordinal)))
        {
            return own;
        }

        if (version is null)
        {
            return null;
        }

        var document = _mapping?.Mapping.TemplateDocument ?? Flow.SourcePath;
        var resolved = await _context.DocumentTemplates.ResolveAsync(document, kind, version, ct).ConfigureAwait(false);
        return resolved.Schema;
    }

    /// <summary>
    /// Which records this interface's records wait for (osdu/docs/reference/flow/interfaces.md, Records that wait for
    /// records): every record of the ledger still to land, except those of the interfaces of the same source that the
    /// source's order does not wait for, because the two refer to each other and the order says which goes first. Worked
    /// out from every interface of the source, whichever the run selected, so a run of one interface, and a fan-out member,
    /// wait exactly as a run of the source does.
    /// </summary>
    public async Task<WaitRules> WaitRulesAsync(CancellationToken ct = default)
    {
        if (_waits is not null)
        {
            return _waits;
        }

        var source = Flow.Interface is null
            ? null
            : SourceDocument ?? (Flow.SourcePath is { } path ? _context.Documents.LoadSource(path) : null);
        if (source is null || source.Interfaces.Count < 2)
        {
            return _waits = WaitRules.WaitForAll;
        }

        var schemas = new List<InterfaceSchema>(source.Interfaces.Count);
        foreach (var sibling in source.Interfaces)
        {
            if (string.Equals(sibling.Interface, Flow.Interface, StringComparison.OrdinalIgnoreCase))
            {
                schemas.Add(await SchemaAsync(ct).ConfigureAwait(false));
                continue;
            }

            using var other = ForTarget(_context, sibling);
            schemas.Add(await other.SchemaAsync(ct).ConfigureAwait(false));
        }

        var names = source.Interfaces.Select(i => i.Interface!).ToList();
        var order = InterfaceOrder.Plan(names, InterfaceOrder.Declared(source), schemas);
        var notWaitedFor = order.NotWaitedFor
            .Where(d => string.Equals(d.Interface, Flow.Interface, StringComparison.OrdinalIgnoreCase))
            .Select(d => source.Interface(d.DependsOn).Id)
            .ToHashSet();
        return _waits = new WaitRules { NotWaitedFor = notWaitedFor };
    }

    /// <summary>
    /// Sends back to pending the flow's waiting records whose wait ended while nothing released them (the record they
    /// wait for was found landed by a verify, or left the ledger), so this run takes them with the rest.
    /// </summary>
    private async Task ReleaseEndedWaitsAsync(CancellationToken ct)
    {
        var released = await RequireLedger().ReleaseResolvedWaitsAsync(Flow.Id, null, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (released > 0)
        {
            _log.LogInformation("{Count} waiting record(s) have nothing left to wait for, and go out with this run.", released);
        }
    }

    public async Task<Verifier> VerifierAsync(CancellationToken ct = default)
        => new(RequireLedger(), await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<Verifier>())
        {
            Trace = _context.Trace,
        };

    /// <summary>
    /// Plan and send, fanned out across the fleet when the flow asks for it: the <c>deliver</c> operation. A run that names
    /// key slices (<see cref="Slices"/>) is a part of a split delivery, and delivers the rows of those slices of the delivery's
    /// submission alone.
    /// </summary>
    public Task<RunResult> RunAsync(bool force, CancellationToken ct = default)
        => TrackAsync("deliver", new { force, source = Flow.Source.Record.Object, selection = Selection.Describe(), parameters = Parameters, nodes = Flow.Parallelism.Nodes, slices = Slices is null ? null : KeySlices.Describe(Slices) }, null, async () =>
        {
            if (Slices is { Count: > 0 } share)
            {
                // A part never closes the submission it works on, however it ends: the delivery's first run does.
                try
                {
                    return Tracked(await DeliverShareAsync(share, force, ct).ConfigureAwait(false));
                }
                finally
                {
                    await HeldSubmissions.ReleaseAllAsync().ConfigureAwait(false);
                }
            }

            var started = _context.Time.GetUtcNow().UtcDateTime;
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);

            // The cache the pipeline needs current is brought up to date before anything is read; when it cannot be, the run
            // stops here, with nothing sent.
            var before = await BringCacheUpToDateAsync(ct).ConfigureAwait(false);
            await ReleaseEndedWaitsAsync(ct).ConfigureAwait(false);
            FanOutHandle? handle = null;
            try
            {
                var (intake, fanned) = await PlanAndFanOutAsync(force, h => handle = h, ct).ConfigureAwait(false);
                handle = null;
                if (!intake.AlreadyProcessed && fanned is null)
                {
                    // What the planning could not render counts before anything is sent; a guard it trips cancels the run here.
                    Guard?.Planned(intake.Counts.Held, intake.Counts.Planned);
                    _context.Trace?.AddPlanned(intake.Counts.Planned);
                    ct.ThrowIfCancellationRequested();
                }

                if (intake.NothingToDo && fanned is null)
                {
                    // A plan with nothing new is not a run with nothing to send: a record released back to pending with
                    // its rendered document, or one a stopped run left due, still waits in this submission, and the plan
                    // skips it because the source row is exactly what it already queues. What is due now is sent; a record
                    // in backoff is not waited for, because a run that planned nothing must not sit out a retry's wait.
                    var worker = await WorkerAsync(ct).ConfigureAwait(false);
                    var sent = await PassUntilNothingClaimableAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                    sent = sent.Add(await SendOrphanedLeasesAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false));
                    var leftovers = await SendUnattendedAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                    var swept = await SweepAfterRunAsync(ct).ConfigureAwait(false);
                    if (sent.Processed == 0 && leftovers.Processed == 0 && sent.Waiting == 0 && leftovers.Waiting == 0)
                    {
                        return Tracked(new RunResult(intake, WorkerSummary.Empty, intake.Submission)
                        {
                            Undone = swept,
                            CacheBefore = before,
                            CacheAfter = swept.Idle ? null : await PutIntoCacheAsync(started, ct).ConfigureAwait(false),
                        });
                    }

                    var settled = sent.Processed > 0 || sent.Waiting > 0
                        ? await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false)
                        : intake.Submission;
                    var sentAfter = await PutIntoCacheAsync(started, ct).ConfigureAwait(false);
                    return Tracked(new RunResult(intake, sent.Add(leftovers), settled) { Undone = swept, CacheBefore = before, CacheAfter = sentAfter });
                }

                // A fanned-out run has sent every share by now; a run alone sends what it planned.
                var work = fanned?.Work ?? await SendShareAsync(intake.Submission.SubmissionId, null, null, ct).ConfigureAwait(false);
                var submission = await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false);

                // Its own records sent, the run takes what submissions no run works on any more still hold: records released
                // after their run settled, and submissions whose run ended before settling them.
                var unattended = await SendUnattendedAsync(await WorkerAsync(ct).ConfigureAwait(false), intake.Submission.SubmissionId, ct).ConfigureAwait(false);

                // Every delivery of the run has ended, its fan-out members' too: what unfinished deliveries left is undone now.
                var undone = await SweepAfterRunAsync(ct).ConfigureAwait(false);

                // What the run changed in OSDU, its members' too, goes into the cache types the pipeline names, so the pipelines
                // that run after it render against it.
                var after = await PutIntoCacheAsync(started, ct).ConfigureAwait(false);
                return Tracked(new RunResult(intake, work.Add(unattended), submission, fanned?.Members ?? 0)
                {
                    Undone = undone,
                    CacheBefore = before,
                    CacheAfter = after,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (handle is not null)
                {
                    // A cancelled root takes its members with it.
                    await CancelMembersAsync(handle).ConfigureAwait(false);
                }

                await SettleStoppedAsync().ConfigureAwait(false);
                throw;
            }
            catch when (handle is not null)
            {
                // A root that fails ends its members before its failure is reported, as a cancelled one does: the platform ends
                // them only once it writes the failure, and a member left working meanwhile works for a run that is gone.
                await CancelMembersAsync(handle).ConfigureAwait(false);
                throw;
            }
            finally
            {
                // Whatever ended the run, it works on no submission any more: a later run may take over what it left.
                await HeldSubmissions.ReleaseAllAsync().ConfigureAwait(false);
            }

            // What the audit trail records of the run: what it did itself, the submission it worked on, and whether it changed nothing.
            static (RunResult, string, Guid?, bool) Tracked(RunResult run) => (run, run.Own.ToString(), run.Submission.SubmissionId, run.Idle);
        }, ct);

    /// <summary>
    /// Closes the submission this run was delivering when it stopped (its failure guard tripped, or the run was cancelled),
    /// failed with why, while records of it are still pending, so the flow's next run sends them as it sends what any
    /// closed submission still holds. It is bookkeeping after the stop, so it runs whatever the run's token says; a ledger
    /// that cannot be reached leaves the submission open, and a drain of it sends the rest.
    /// </summary>
    private async Task SettleStoppedAsync()
    {
        if (SubmissionId is not { } submissionId || _context.Ledger is null)
        {
            return;
        }

        var reason = Guard?.Reason is { } tripped
            ? $"stopped: {tripped}"
            : "stopped: the run was cancelled before its records were all sent";
        try
        {
            await Intake.StopAsync(submissionId, Flow.Id, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            _log.LogWarning(
                "Submission {SubmissionId} stays open after the stop ({Message}); a drain of it sends the records it still holds.",
                submissionId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>Intake only: plan this run's records (or a member's share of the slices) into work batches.</summary>
    public Task<IntakeResult> IntakeAsync(bool force, CancellationToken ct = default)
        => TrackAsync("intake", new { force, source = Flow.Source.Record.Object, selection = Selection.Describe(), parameters = Parameters, slices = Slices is null ? null : KeySlices.Describe(Slices) }, null, async () =>
        {
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            var intake = await Intake.IntakeAsync(Flow, Mapping, Parameters, Request, force, ct).ConfigureAwait(false);
            return (intake, intake.AlreadyProcessed ? "the submission was already planned" : intake.Counts.Headline, intake.Submission.SubmissionId, intake.Counts.Idle);
        }, ct);

    /// <summary>Drains the pending work of the flow (one pass, or until nothing is due), optionally of one submission: the <c>drain</c> operation.</summary>
    public Task<DrainResult> WorkAsync(bool once, Guid? submissionId = null, CancellationToken ct = default)
        => TrackAsync("drain", new { once, submissionId }, null, async () =>
        {
            var started = _context.Time.GetUtcNow().UtcDateTime;
            if (submissionId is null)
            {
                // A drain of the whole flow ends with the after step, so a step the partition's cache cannot take is refused
                // before anything is sent.
                await CacheStepsAsync(ct).ConfigureAwait(false);
            }

            await ReleaseEndedWaitsAsync(ct).ConfigureAwait(false);
            var worker = await WorkerAsync(ct).ConfigureAwait(false);
            if (submissionId is { } s)
            {
                // A drain of one submission works on it as its run does (a fan-out member's drain, an operator's), so it
                // holds it for as long as it drains, and no later run takes it over meanwhile.
                var held = await HeldSubmissions.HoldAsync(s, ct).ConfigureAwait(false);
                try
                {
                    var drained = once ? await worker.PassAsync(s, ct).ConfigureAwait(false) : await worker.DrainAsync(s, ct).ConfigureAwait(false);
                    await Intake.CompleteAsync(s, Flow.Id, ct).ConfigureAwait(false);
                    return (new DrainResult(drained, UndoSummary.Empty), drained.Headline, submissionId, drained.Idle);
                }
                finally
                {
                    if (held)
                    {
                        await HeldSubmissions.ReleaseAsync(s).ConfigureAwait(false);
                    }
                }
            }

            var summary = once ? await worker.PassAsync(null, ct).ConfigureAwait(false) : await worker.DrainAsync(null, ct).ConfigureAwait(false);

            // A drain of the whole flow ends with the sweep a deliver run ends with. A drain of a submission does not: it is what a
            // fan-out member runs while its coordinator and other members deliver, and the coordinator sweeps once they are done.
            var undone = await SweepAfterRunAsync(ct).ConfigureAwait(false);
            var after = summary.Delivered > 0 || !undone.Idle ? await PutIntoCacheAsync(started, ct).ConfigureAwait(false) : null;
            var headline = undone.Idle ? summary.Headline : $"{summary.Headline}; {undone.Describe()}";
            return (new DrainResult(summary, undone) { CacheAfter = after }, headline, submissionId, summary.Idle && undone.Idle);
        }, ct);

    /// <summary>
    /// Undoes what unfinished deliveries of the flow left in OSDU (osdu/docs/reference/concepts/artifacts-and-undo.md, When
    /// the undo runs, and what it is given): the <c>undo</c> operation. Every artifact due, every one whose undo failed and
    /// is past its backoff (with <paramref name="exhausted"/>, also those whose undo failed as often as the sweep tries),
    /// and what a unit its record no longer carries created, a page of whole records at a time, up to
    /// <see cref="ArtifactLimits.SweepPerRun"/> records. Each record's undo is an attempt of phase undo on its history.
    /// </summary>
    public Task<UndoSummary> UndoUnfinishedAsync(bool exhausted, CancellationToken ct = default)
        => TrackAsync(DeliveryOperations.Undo, new { exhausted }, null, async () =>
        {
            var summary = await SweepAsync(exhausted, ct).ConfigureAwait(false);
            return (summary, summary.Describe(), (Guid?)null, summary.Idle);
        }, ct);

    /// <summary>
    /// How far before a run's start its after step looks for what it changed: the nodes a fanned-out run delivers on keep
    /// their own clocks. Reading a record a moment older again changes nothing.
    /// </summary>
    private static readonly TimeSpan CacheStepMargin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The pipeline's cache steps resolved against its partition's cache (osdu/docs/reference/flow/delivery.md, cache), once
    /// per runtime, or null when it names none. An entry naming nothing the cache holds, or a type a step cannot work on, is
    /// refused here, before the run reads or sends anything.
    /// </summary>
    /// <exception cref="DeliveryException">An entry cannot be resolved, or the host has no cache.</exception>
    private async Task<(string Scope, Snapshots.CacheStepPlan Plan)?> CacheStepsAsync(CancellationToken ct)
    {
        if (Flow.Cache.IsEmpty)
        {
            return null;
        }

        if (_cacheSteps is { } known)
        {
            return known;
        }

        var store = _context.Cache ?? throw new DeliveryException(
            $"Pipeline '{Flow.Label}' names cache steps, and caches live in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}");
        var scope = await LedgerRegistration.PartitionAsync(Flow, _context.Secrets, ct).ConfigureAwait(false);
        var declaration = await store.DeclarationAsync(scope, ct).ConfigureAwait(false);

        // A runtime opened for target work has no resolved mapping: the kind comes from the mapping's own header, and what the
        // chain reads matters only to the before step, which such a run does not take.
        var kind = _mapping?.Mapping.Kind ?? Mappings.Load(Flow.Render.Mapping).Kind;
        var reads = _mapping is { } resolved
            ? MappingCacheReads.OfChain(resolved.Mapping, resolved.Schema, resolved.Steps.SelectMany(ChainFragments))
            : new MappingCacheReads([], [], []);
        var plan = Snapshots.CacheStepPlan.Resolve(Flow.Label, Flow.Cache, declaration, kind, reads, EnrichRecords.KindsOf(Flow));
        _cacheSteps = (scope, plan);
        return _cacheSteps;
    }

    /// <summary>The enrichment mapping an enrichment renders, with the template it fills: one adding paths, or one rendering the records it adds.</summary>
    private static IEnumerable<(MappingDefinition Fragment, SchemaSnapshot? Schema)> ChainFragments(ResolvedStep step)
    {
        if (step.Mapping is { } fragment)
        {
            yield return (fragment, step.Schema);
        }

        if (step.Records is { Mapping: { } recordsMapping } records)
        {
            yield return (recordsMapping, records.Schema);
        }
    }

    /// <summary>
    /// The before step a deliver run starts with (osdu/docs/reference/flow/delivery.md, cache): the types the pipeline names
    /// brought up to date from OSDU, searched through its own connection to its partition. Then, whatever this step, an earlier
    /// pipeline or an earlier interface of the source wrote, the run renders against the newest version of the cache. The
    /// step is the run's to complete: its failure fails the run before anything is read or sent.
    /// </summary>
    private async Task<Snapshots.CacheStepOutcome?> BringCacheUpToDateAsync(CancellationToken ct)
    {
        Snapshots.CacheStepOutcome? outcome = null;
        if (await CacheStepsAsync(ct).ConfigureAwait(false) is { Plan.Before.Count: > 0 } steps)
        {
            outcome = await new Snapshots.CacheKeeper(_context, _context.Loggers.CreateLogger<Snapshots.CacheKeeper>()).BeforeAsync(
                steps.Scope,
                steps.Plan.Before,
                Flow.Label,
                token => Snapshots.OsduConnection.CreateAsync(
                    Flow.Target.Endpoint, Flow.Target.Auth, Flow.Target.Headers, Flow.Reliability, _context.Secrets,
                    allowLoopback: EngineContext.LoopbackAllowed, observer: _context.HttpObserver, ct: token),
                Flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath,
                RunId,
                Actor,
                ct).ConfigureAwait(false);
        }

        await RenderAgainstNewestCacheAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Resolves the render inputs again when the partition's cache moved past the version the runtime resolved, for a flow that
    /// renders against the current version: a source opens every interface before the first runs, so an interface renders
    /// against what the interfaces before it, and its own before step, put into the cache. A flow that pins a version keeps it.
    /// </summary>
    private async Task RenderAgainstNewestCacheAsync(CancellationToken ct)
    {
        if (_mapping is not { Context.CacheScope: { } scope } resolved || _context.Cache is not { } store
            || !Flow.Render.CacheVersion.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var current = await store.CurrentVersionAsync(scope, ct).ConfigureAwait(false);
        if (current is null || string.Equals(current, resolved.References.Version, StringComparison.Ordinal))
        {
            return;
        }

        var resolver = new RenderResolver(Mappings, _context.Cache, _context.DocumentTemplates, _context.Secrets, _search);
        _mapping = await resolver.ResolveAsync(Flow, ct).ConfigureAwait(false);
        _log.LogInformation(
            "The cache of partition {Scope} moved from version {Resolved} to {Current} since the run opened; it renders against {Current}.",
            scope, resolved.References.Version, current, current);
    }

    /// <summary>
    /// The after step a deliver run, a flow-wide drain, a removal and a reversal end with (osdu/docs/reference/flow/delivery.md,
    /// cache): what the pipeline changed in OSDU since <paramref name="startedUtc"/>, read back from OSDU's storage service into
    /// the types it names. Null when it names none. Its failure is not the run's: what the run did in OSDU stands, a refresh of
    /// the cache flows brings the records in, and the warning says so.
    /// </summary>
    private Task<Snapshots.CacheStepOutcome?> PutIntoCacheAsync(DateTime startedUtc, CancellationToken ct)
        => PutIntoCacheAsync(startedUtc, null, ct);

    /// <summary>
    /// The after step, told besides what the ledger names the OSDU ids <paramref name="alsoChanged"/> the run took out of
    /// OSDU and deleted from the ledger as it went, which no ledger row names any more.
    /// </summary>
    private async Task<Snapshots.CacheStepOutcome?> PutIntoCacheAsync(DateTime startedUtc, IReadOnlyCollection<string>? alsoChanged, CancellationToken ct)
    {
        if (Flow.Cache.After.IsEmpty || _context.Cache is null || _context.Ledger is null)
        {
            return null;
        }

        string? scope = null;
        try
        {
            if (await CacheStepsAsync(ct).ConfigureAwait(false) is not { Plan.After.Count: > 0 } steps)
            {
                return null;
            }

            scope = steps.Scope;
            return await new Snapshots.CacheKeeper(_context, _context.Loggers.CreateLogger<Snapshots.CacheKeeper>()).AfterAsync(
                steps.Scope,
                steps.Plan.After,
                Flow.Id,
                Flow.LedgerName,
                startedUtc - CacheStepMargin,
                _target.ClientAsync,
                Flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath,
                RunId,
                Actor,
                alsoChanged,
                steps.Plan.TakesStepDatasets,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or System.Data.Common.DbException or TimeoutException or InvalidOperationException or HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // A call that timed out surfaces as a cancellation the run did not ask for; the run's own cancellation still cancels.
            // A partition that did not resolve is named as the flow declares it.
            var reason = HeaderRedaction.RedactMessage(ex.Message);
            var partition = scope ?? Flow.Partition ?? string.Empty;
            _log.LogWarning(
                "The cache of partition {Scope} was not brought up to date with what the run changed in OSDU ({Message}); the records stand in OSDU, and a refresh of the cache flows brings them into the cache.",
                partition, reason);
            return new Snapshots.CacheStepOutcome(
                Snapshots.CacheStepOutcome.AfterStep, partition, [], 0, 0, 0, null, false, 0, $"{reason}. A refresh of the cache flows brings the records in.");
        }
    }

    /// <summary>
    /// The sweep a deliver run and a flow-wide drain end with. Its failure is not the run's: what the run delivered stands, the
    /// artifacts stay as they were, and the next run sweeps them.
    /// </summary>
    private async Task<UndoSummary> SweepAfterRunAsync(CancellationToken ct)
    {
        try
        {
            return await SweepAsync(exhausted: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Data.Common.DbException or TimeoutException or InvalidOperationException)
        {
            _log.LogWarning(
                "Undoing what unfinished deliveries left could not finish ({Message}); what is left stays recorded, and the next run undoes it.",
                HeaderRedaction.RedactMessage(ex.Message));
            return UndoSummary.Empty;
        }
    }

    private async Task<UndoSummary> SweepAsync(bool exhausted, CancellationToken ct)
    {
        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        if (!protocol.Undoes)
        {
            return UndoSummary.Empty;
        }

        var ledger = RequireLedger();

        // A lease a stopped worker left is applied first, so the records it held say which unit they carry before anything of
        // theirs is told abandoned.
        await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var runner = new UndoRunner(protocol, _context.Time, Truncate($"{Environment.MachineName}/{Environment.ProcessId}/undo", 200), RunId);
        var total = UndoSummary.Empty;
        DeliveryKey? after = null;
        while (total.Records < ArtifactLimits.SweepPerRun)
        {
            ct.ThrowIfCancellationRequested();
            var now = _context.Time.GetUtcNow().UtcDateTime;
            var page = await ledger.SweepArtifactsAsync(Flow.Id, now, after, Math.Min(ArtifactLimits.SweepPage, ArtifactLimits.SweepPerRun - total.Records), exhausted, ct).ConfigureAwait(false);
            if (page.Artifacts.Count == 0)
            {
                break;
            }

            after = page.Last;
            var records = await ledger.GetRecordsAsync(Flow.Id, page.Artifacts.Select(a => a.Key).Distinct().ToList(), ct).ConfigureAwait(false);
            var requests = page.Artifacts
                .GroupBy(a => a.Key)
                .Where(g => records.TryGetValue(g.Key, out var record) && record.LeaseOwner is null)
                .Select(g =>
                {
                    var record = records[g.Key];
                    var artifacts = g.ToList();

                    // A record with work queued that writes its metadata, as the final hash check will find it, keeps the
                    // record itself, which that work writes again; any other has everything its unfinished units left taken back.
                    return new UndoRequest(record, artifacts, DeliveryWorker.ReasonOf(record, artifacts), KeepRecord: record.HasPendingWork && ChangeDetector.AtPush(record, Flow.Change).Metadata);
                })
                .ToList();
            if (requests.Count == 0)
            {
                continue;
            }

            using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
            var undos = await runner.RunAsync(requests, correlation.Id, ct).ConfigureAwait(false);
            await ledger.SettleArtifactsAsync(Flow.Id, undos, ct: ct).ConfigureAwait(false);
            var swept = UndoSummary.Of(undos);
            total = total.Add(swept);
            if (swept.Failed > 0)
            {
                _log.LogWarning("{Summary}; the undos that failed are tried again later.", swept.Describe());
            }
        }

        if (!total.Idle)
        {
            _log.LogInformation(RunTrace.Bounded, "Undid what unfinished deliveries left: {Summary}.", total.Describe());
        }

        return total;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>
    /// The drift pass: the <c>verify</c> operation. With <paramref name="reconcile"/>, each record it marks for redelivery is
    /// named under the pass's activity, so the record's history shows which verify asked for it to be sent again.
    /// </summary>
    public Task<VerifySummary> VerifyAsync(int max, TimeSpan? notVerifiedWithin, bool reconcile, IReadOnlyList<DeliveryKey>? keys = null, CancellationToken ct = default)
        => TrackAsync("verify", new { max, notVerifiedWithin, reconcile, keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var verifier = await VerifierAsync(ct).ConfigureAwait(false);
            var summary = await verifier.RunAsync(max, notVerifiedWithin, reconcile, keys, activity, ct).ConfigureAwait(false);
            return (summary, summary.ToString(), (Guid?)null, summary.Idle);
        }, ct);

    /// <summary>
    /// Consolidates the ledger with the ingestion tables for <paramref name="keys"/>, or for every record of the flow when
    /// it is null (<see cref="SourceSync"/>): each row read by its stored key in the scope its record was planned under,
    /// its arrival recorded, a change the ledger never saw asked to be planned by the next run, and a row that is gone
    /// put on its record's history. Nothing is rendered and nothing reaches OSDU.
    /// </summary>
    public Task<SourceSyncSummary> SyncAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("sync", new { keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async () =>
        {
            var sync = new SourceSync(_context, Flow, RequireLedger(), Parameters, RunId, _context.Loggers.CreateLogger<SourceSync>());
            var summary = await sync.RunAsync(keys, ct).ConfigureAwait(false);
            return (summary, summary.ToString(), (Guid?)null, false);
        }, ct);

    /// <summary>Releases held, failed, deleted or reverted records (all of them when <paramref name="keys"/> is null).</summary>
    public Task<int> ReleaseAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("release", new { keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var selection = keys is null ? ReleaseSelection.EveryBlocked : ReleaseSelection.Named(keys);
            var released = await RequireLedger().ReleaseAsync(Flow.Id, selection, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.released", keys, $"released by {Actor}", "all blocked records", ct).ConfigureAwait(false);
            return (released, $"released {released} record(s)", (Guid?)null, false);
        }, ct);

    /// <summary>
    /// Deletes records already removed from OSDU from the ledger (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Deleting removed records from the ledger), asking nothing of OSDU: the named records, or with null keys every
    /// record the ledger marks deleted, a page at a time. Only a record the ledger marks deleted goes, none a lease
    /// holds, and none whose unfinished deliveries left something the undo has not taken back yet; any other named
    /// record is left as it is and counted as such. Each record deleted keeps one line and is named under this
    /// intervention's activity, the same way the removal's extra step deletes one
    /// (<see cref="ILedger.PurgeRecordsAsync"/>), and its line asks for its row to be read again, so the flow's next run
    /// delivers it as a new record.
    /// </summary>
    public Task<LedgerPurgeSummary> PurgeFromLedgerAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync(
            "purge",
            keys is null
                ? new { every = (string?)"deleted", records = (int?)null, keys = (List<string>?)null }
                : new { every = (string?)null, records = (int?)keys.Count, keys = (List<string>?)keys.Take(20).Select(k => k.ToString()).ToList() },
            keys is { Count: 1 } ? keys[0] : null,
            async activity =>
            {
                var ledger = RequireLedger();
                var now = _context.Time.GetUtcNow().UtcDateTime;
                LedgerPurgeSummary summary;
                if (keys is not null)
                {
                    var purged = await ledger.PurgeRecordsAsync(Flow.Id, keys.Distinct().ToList(), Actor, activity, now, ct).ConfigureAwait(false);
                    summary = new LedgerPurgeSummary(keys.Distinct().Count(), purged.Count);
                }
                else
                {
                    // Every record the ledger marks deleted, a selection's worth at a time. A record a page leaves (a lease holds it,
                    // or an undo has still to settle what its deliveries left) comes back on the next page, so the walk goes on while
                    // a page deletes something and ends on one that deletes nothing: it ends however the ledger moves underneath it,
                    // and each record is counted once.
                    var seen = new HashSet<DeliveryKey>();
                    var total = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = await ledger.ListKeysAsync(Flow.Id, new RecordQuery { Status = RecordStatus.Deleted }, RemovalLimits.MaxSelection, ct).ConfigureAwait(false);
                        if (page.Count == 0)
                        {
                            break;
                        }

                        var purged = await ledger.PurgeRecordsAsync(Flow.Id, page, Actor, activity, now, ct).ConfigureAwait(false);
                        seen.UnionWith(page);
                        total += purged.Count;
                        if (purged.Count == 0 || page.Count < RemovalLimits.MaxSelection)
                        {
                            break;
                        }
                    }

                    summary = new LedgerPurgeSummary(seen.Count, total);
                }

                return (summary, summary.Describe(), (Guid?)null, false);
            },
            ct);

    /// <summary>
    /// Releases every record one issue keeps blocked (osdu/docs/reference/concepts/record-lifecycle.md, Issues), after
    /// its cause was fixed: every record blocked by it, however many. The activity names the issue
    /// and its pattern, and every record it releases is named under the activity, so each record's history shows the
    /// release. <paramref name="pattern"/> is what the operator was shown, recorded as they saw it.
    /// </summary>
    public Task<int> ReleaseProblemAsync(long problem, string pattern, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var named = ProblemSignature.Format(problem);
        return TrackAsync("release", new { issue = named, pattern }, null, async activity =>
        {
            var released = await RequireLedger().ReleaseAsync(Flow.Id, ReleaseSelection.OfProblem(problem), activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.released", null, $"released by {Actor}", $"the records issue {named} kept blocked", ct).ConfigureAwait(false);
            return (released, $"released {released} record(s) issue {named} kept blocked: {pattern}", (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Marks records for redelivery (osdu/docs/reference/concepts/removal-and-reversal.md, Redelivering records: forget
    /// what OSDU holds so the next plan re-sends), and asks the flow's next run to plan them again. Null keys means every
    /// record the flow has delivered.
    /// </summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey>? keys, RedeliverScope scope, CancellationToken ct = default)
        => RedeliverAsync(keys, new RedeliverSelection(scope, []), ct);

    /// <summary>Marks records for redelivery of what <paramref name="selection"/> names, parts of a payload sent in parts included.</summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey>? keys, RedeliverSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        object extra = selection.Lost
            ? new { scope = selection.Scope.ToString(), parts = selection.Parts, lost = true }
            : new { scope = selection.Scope.ToString(), parts = selection.Parts };
        return TrackAsync("redeliver", Named(keys, extra), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var marked = await RequireLedger().ForceRedeliverAsync(Flow.Id, keys, selection, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.redeliver", keys, $"redelivery of {selection} requested by {Actor}", ct).ConfigureAwait(false);
            return (marked, keys is null
                ? $"marked every delivered record, {marked} in all, for redelivery of {selection}"
                : $"marked {marked} record(s) for redelivery of {selection}", (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Gives records OSDU lost a new OSDU id each, in place of the one they claimed (osdu/docs/reference/flow/inventory-removals.md,
    /// Repairing missing ids), so the next deliver run writes them under it: only a record the ledger holds as delivered
    /// and no lease holds, whose new id no other record claims; any other is left as it is. <paramref name="reason"/> says
    /// why, in each record's history. Returns the records given one, each with its lost and new id.
    /// </summary>
    public Task<IReadOnlyList<TargetIdReassignment>> ReassignTargetIdsAsync(IReadOnlyList<DeliveryKey> keys, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return TrackAsync("reassign-id", Named(keys, new { reason }), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var reassigned = await RequireLedger().ReassignTargetIdsAsync(Flow.Id, keys, Actor, reason, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.reassign-id", reassigned.Select(r => r.Key).ToList(), $"new OSDU ids given by {Actor}: {reason}", ct).ConfigureAwait(false);
            return (reassigned, string.Create(
                CultureInfo.InvariantCulture,
                $"gave {reassigned.Count} of {keys.Count} record(s) a new OSDU id in place of one OSDU no longer holds; a record not delivered now, or held by a lease, was left as it is"),
                (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Asks the named records held because OSDU already holds a record at the id each one's key makes to adopt that record
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds): the flow's next deliver run
    /// takes each over, at the version OSDU holds then, and writes its row there. A named record held for another reason, or
    /// claiming an id already, is left as it is. Returns the records asked.
    /// </summary>
    public Task<IReadOnlyList<DeliveryKey>> AdoptAsync(IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return TrackAsync("adopt", Named(keys, null), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var asked = await RequireLedger().RequestAdoptionAsync(Flow.Id, ReleaseSelection.Named(keys), activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.adoption-asked", [.. asked], $"adoption asked by {Actor}", ct).ConfigureAwait(false);
            return (asked, string.Create(
                CultureInfo.InvariantCulture,
                $"asked {asked.Count} of {keys.Count} record(s) to adopt the record OSDU holds at the id its key makes; a record not held for such an id, or claiming one already, was left as it is"),
                (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Asks every record one issue keeps held to adopt the record OSDU holds at the id its key makes
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Issues): the issue OSDU already holding a record at such an id, as many
    /// records as it keeps held. The activity names the issue and its pattern as the operator was shown it, and every record
    /// asked is named under it.
    /// </summary>
    public Task<IReadOnlyList<DeliveryKey>> AdoptProblemAsync(long problem, string pattern, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var named = ProblemSignature.Format(problem);
        return TrackAsync("adopt", new { issue = named, pattern }, null, async activity =>
        {
            var asked = await RequireLedger().RequestAdoptionAsync(Flow.Id, ReleaseSelection.OfProblem(problem), activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.adoption-asked", null, $"adoption asked by {Actor}", $"the records issue {named} kept held", ct).ConfigureAwait(false);
            return (asked, $"asked {asked.Count} record(s) issue {named} kept held to adopt the records OSDU holds at their ids: {pattern}", (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Asks every record of the ledger held because OSDU already holds a record at the id its key makes to adopt that record:
    /// the batch an operator runs after a run held them, so that the next run takes them all over and the ledger is in step
    /// with OSDU. The activity names every record asked.
    /// </summary>
    public Task<IReadOnlyList<DeliveryKey>> AdoptEveryAsync(CancellationToken ct = default)
        => TrackAsync("adopt", new { every = "held for an id OSDU holds" }, null, async activity =>
        {
            var asked = await RequireLedger().RequestAdoptionAsync(Flow.Id, ReleaseSelection.EveryBlocked, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.adoption-asked", null, $"adoption asked by {Actor}", "every record held for an id OSDU holds", ct).ConfigureAwait(false);
            return (asked, $"asked {asked.Count} record(s) held for an id OSDU holds to adopt the records OSDU holds at their ids", (Guid?)null, false);
        }, ct);

    /// <summary>
    /// Asks records OSDU holds to be brought up to date (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Redelivering records): rendered again by the next plan that meets them, whichever run that is, and sent only
    /// where they render differently, since their delivered hashes stay. Null keys means every record the flow has
    /// delivered; a named record OSDU does not hold is left as it is. Returns how many were marked.
    /// </summary>
    public Task<int> BringUpToDateAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("rerender", Named(keys, null), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var marked = await RequireLedger().RequestRenderAsync(Flow.Id, keys, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.rerender", keys, $"bringing up to date requested by {Actor}", ct).ConfigureAwait(false);
            return (marked, keys is null
                ? $"asked every delivered record, {marked} in all, to be brought up to date"
                : $"asked {marked} of {keys.Count} record(s) to be brought up to date; a record that is not delivered (held, failed, removed, reverted, or never delivered) is left as it is: release or redeliver it instead", (Guid?)null, false);
        }, ct);

    /// <summary>
    /// The most records an intervention names one by one, in its activity and in its events. Beyond it the activity says how
    /// many it asked for and one event speaks for the flow, while the ledger still names every record under the activity.
    /// </summary>
    internal const int NamedPerIntervention = 100;

    /// <summary>What an intervention on <paramref name="keys"/> records as its parameters: the keys, or how many when they are many, with <paramref name="extra"/> beside them.</summary>
    private static Dictionary<string, object?> Named(IReadOnlyList<DeliveryKey>? keys, object? extra)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (keys is null || keys.Count <= NamedPerIntervention)
        {
            parameters["keys"] = keys?.Select(k => k.ToString()).ToList();
        }
        else
        {
            parameters["count"] = keys.Count;
        }

        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
            {
                parameters[property.Name] = property.Value;
            }
        }

        return parameters;
    }

    /// <summary>Tells the listeners what an intervention on <paramref name="keys"/> asked for: an event per record while they are few, else one for the flow.</summary>
    private Task EmitNamedAsync(string kind, IReadOnlyList<DeliveryKey>? keys, string detail, CancellationToken ct)
        => keys is { Count: <= NamedPerIntervention }
            ? EmitAsync(kind, keys, detail, ct)
            : EmitAsync(kind, null, detail, keys is null ? "every delivered record" : $"{keys.Count} records", ct);

    /// <summary>
    /// How many of the records the ledger asked to be planned again one pass of a deliver run reads by key
    /// (<see cref="DeliveryExecutor.RequestedPerPass"/>); a run makes as many passes as there are such records. Tests lower it.
    /// </summary>
    internal int RequestedPerPass { get; set; } = DeliveryExecutor.RequestedPerPass;

    /// <summary>
    /// The next records the ledger asked to be planned again, after <paramref name="after"/> in the order they were asked
    /// for, at most <paramref name="max"/>: what a run turns into a
    /// key-scoped selection so a release, a redelivery, a removal and a cache rollout reach the records they marked, and the
    /// records deleted from the ledger whose rows wait to be read again. The page names the last record it read, which the
    /// next page starts after, so a run walking every page meets each record once, even one a pass could not plan (its row
    /// gone from the table, or out of the run's scope): that one keeps its request for the run that can. A record with no
    /// source key cannot be read by key and is counted rather than read; the first few are named.
    /// </summary>
    public async Task<PlanRequestedPage> PlanRequestedKeysAsync(int max, PlanRequestedRecord? after, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var ledger = RequireLedger();
        var keys = new List<KeyTuple>();
        var seen = new HashSet<KeyTuple>();
        var requests = new List<PlanRequestedRecord>();
        var purgedRequests = new List<PlanRequestedRecord>();
        var keylessRequests = new List<PlanRequestedRecord>();
        var keyless = 0;
        var read = 0;
        var last = after;
        while (read < max)
        {
            var page = await ledger.ListPlanRequestedAsync(Flow.Id, last, Math.Min(max - read, 500), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var record in page)
            {
                if (record.SourceKeyJson is { } json)
                {
                    // A record deleted from the ledger and made a record again before a run read its line asks for the
                    // same row twice; the pass reads it once.
                    var key = KeyTuple.FromJson(json);
                    if (seen.Add(key))
                    {
                        keys.Add(key);
                    }

                    (record.Purged ? purgedRequests : requests).Add(record);
                }
                else
                {
                    keyless++;
                    keylessRequests.Add(record);
                    if (_keylessNamed++ < KeylessNamed)
                    {
                        _log.LogInformation(
                            "Record {Key} waits to be planned again but the ledger holds no source key for it (it predates the ingestion source); this run reads the whole scope to plan it.",
                            record.DeliveryKey);
                    }
                }
            }

            read += page.Count;
            last = page[^1];
        }

        return new PlanRequestedPage(keys, read == 0 ? null : last, keyless) { Requests = requests, PurgedRequests = purgedRequests, KeylessRequests = keylessRequests };
    }

    /// <summary>
    /// Settles the requests of records that carry no source key (<paramref name="asked"/>, each as its record carried it)
    /// after a read of the whole scope: one it met is planned, answered, and keyed by that plan; one still standing, with no
    /// key yet, belongs to a row the read did not meet, which is gone from the ingestion table. Its request is answered with
    /// that on the record's history, and a released record is blocked again, so no run reads the whole scope for it again.
    /// Returns how many such rows were reported gone.
    /// </summary>
    internal async Task<int> SettleKeylessAsync(IReadOnlyList<PlanRequestedRecord> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0)
        {
            return 0;
        }

        var ledger = RequireLedger();
        var records = await ledger.GetRecordsAsync(Flow.Id, asked.Select(a => a.DeliveryKey).Distinct().ToList(), ct).ConfigureAwait(false);
        var findings = asked
            .Where(a => records.TryGetValue(a.DeliveryKey, out var record) && record.PlanRequestedUtc == a.RequestedUtc && record.SourceKeyJson is null)
            .GroupBy(a => a.DeliveryKey)
            .Select(g => records[g.Key])
            .Select(record => new SourceSyncFinding
            {
                DeliveryKey = record.DeliveryKey,
                NotFound = "a read of the whole scope did not meet the record's row, and the record carries no source key to read it by: the row was deleted from the ingestion table, or its key changed",
                Gone = true,
                Origin = record.PendingSourceUpdatedUtc is not null || record.PendingSourceFileName is not null ? record.PendingOrigin : record.Origin,
            })
            .ToList();
        if (findings.Count == 0)
        {
            return 0;
        }

        var applied = await ledger.ApplySourceSyncAsync(Flow.Id, findings, RunId, ct).ConfigureAwait(false);
        return applied.NotFound;
    }

    /// <summary>
    /// Settles the requests a pass read by key and left as they were (<paramref name="asked"/>, each as its record carried
    /// it): the pass could not plan those records. Each is read again by a sync (<see cref="SourceSync"/>) in the scope it was
    /// planned under: a row gone from the table answers its request, and a released record is blocked again, so the request
    /// is not read by every run that follows; a row in the scope of another run keeps its request for that run. Returns what
    /// the sync found, or null when every request was answered.
    /// </summary>
    internal async Task<SourceSyncSummary?> SettleUnansweredAsync(IReadOnlyList<PlanRequestedRecord> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0)
        {
            return null;
        }

        var ledger = RequireLedger();
        var records = await ledger.GetRecordsAsync(Flow.Id, asked.Select(a => a.DeliveryKey).Distinct().ToList(), ct).ConfigureAwait(false);
        var unanswered = asked
            .Where(a => records.TryGetValue(a.DeliveryKey, out var record) && record.PlanRequestedUtc == a.RequestedUtc)
            .Select(a => a.DeliveryKey)
            .Distinct()
            .ToList();
        if (unanswered.Count == 0)
        {
            return null;
        }

        var sync = new SourceSync(_context, Flow, ledger, Parameters, RunId, _context.Loggers.CreateLogger<SourceSync>());
        return await sync.RunAsync(unanswered, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles the requests of records deleted from the ledger that a pass read by key (<paramref name="asked"/>, as the
    /// listing gave them). A row the pass planned made a record again, which answers its request, whatever became of the
    /// record then (delivered, held, failed: its own history says); so does a row the ingestion table no longer holds, which
    /// no run can deliver. A row outside this run's scope keeps its request for the run of its own scope, and so does a row
    /// the pass read and planned nothing of, which the log names. Returns what became of them, or null when the pass read none.
    /// </summary>
    internal async Task<PurgedRequestsSettled?> SettlePurgedRequestsAsync(IReadOnlyList<PlanRequestedRecord> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0)
        {
            return null;
        }

        var ledger = RequireLedger();
        var records = await ledger.GetRecordsAsync(Flow.Id, asked.Select(a => a.DeliveryKey).Distinct().ToList(), ct).ConfigureAwait(false);
        var planned = asked.Where(a => records.ContainsKey(a.DeliveryKey)).ToList();
        var unplanned = asked
            .Where(a => !records.ContainsKey(a.DeliveryKey) && a.SourceKeyJson is not null)
            .Select(a => (Request: a, Key: KeyTuple.FromJson(a.SourceKeyJson!)))
            .ToList();
        var gone = new List<PlanRequestedRecord>();
        var elsewhere = 0;
        var unexplained = 0;
        if (unplanned.Count > 0)
        {
            // Read once more by key in this run's scope, which says why the pass planned nothing of them: not in the table at
            // all, or in it outside this scope. Nothing is rendered or written by the read.
            var source = _context.Sources.Open(Flow, Parameters, _context.Loggers);
            var header = await source.OpenAsync(SourceSelection.ForKeys([.. unplanned.Select(u => u.Key).Distinct()]), null, ct).ConfigureAwait(false);
            var missing = header.MissingKeys.ToHashSet();
            var outside = header.OutOfScopeKeys.ToHashSet();
            foreach (var (request, key) in unplanned)
            {
                if (missing.Contains(key))
                {
                    gone.Add(request);
                    if (gone.Count <= PurgedNamed)
                    {
                        _log.LogInformation(
                            "Record {Key} was deleted from the ledger and its row is gone from {Object}, so it cannot be delivered again; its request is answered.",
                            request.DeliveryKey, Flow.Source.Record.Object);
                    }
                }
                else if (outside.Contains(key))
                {
                    elsewhere++;
                }
                else
                {
                    unexplained++;
                    if (unexplained <= PurgedNamed)
                    {
                        _log.LogWarning(
                            "Record {Key} was deleted from the ledger and its row is in {Object} in this run's scope, but the pass planned nothing of it; its request stays for the next run.",
                            request.DeliveryKey, Flow.Source.Record.Object);
                    }
                }
            }
        }

        await ledger.AnswerPurgedRequestsAsync(Flow.Id, [.. planned, .. gone], ct).ConfigureAwait(false);
        return new PurgedRequestsSettled(planned.Count, gone.Count, elsewhere, unexplained);
    }

    /// <summary>
    /// Removes records from OSDU through the flow's protocol, to the extent the choice asks for, and records what
    /// happened to each one in the ledger. One record and ten thousand take the same path: the selection is
    /// resolved to keys, the records are removed in chunks (batched into one request where the protocol and the
    /// scope allow it), and every record gets its own ledger attempt naming the scope and the operator. A record
    /// the flow never delivered has nothing in OSDU to remove and is reported as skipped rather than failing the
    /// removal; a record OSDU has already lost settles the ledger just as a removal would, because the state the
    /// operator asked for is the state OSDU is in. <see cref="RemovalChoice.Previous"/> takes the same path to the same
    /// records and, in place of a delete, writes the version before the latest back
    /// (<see cref="Reversals.PreviousVersionRestore"/>), under an activity of its own, since it removes nothing. With
    /// <paramref name="purgeLedger"/>, a removal that takes the record out of OSDU (<see cref="RemovalChoice.Record"/> or
    /// <see cref="RemovalChoice.Everything"/>) also deletes each record OSDU answered for from the ledger, keeping one line
    /// of it (<see cref="ILedger.PurgeRecordsAsync"/>); a record whose removal failed stays in the ledger as it was. Either
    /// way each record removed is asked for again, on its record or on its line, so the flow's next run reads its row by key
    /// and delivers it again: what the source holds and OSDU does not comes back.
    /// </summary>
    public Task<RemovalSummary> RemoveAsync(RemovalSelection selection, RemovalChoice scope, bool purgeLedger = false, CancellationToken ct = default)
    {
        // A selection by filter is resolved here, when the removal runs: whoever queued it (the API, which counted it, or a run
        // triggered by hand, which did not), what it matches now is what it removes, and never a part of it.
        ArgumentNullException.ThrowIfNull(selection);
        if (purgeLedger && scope is not (RemovalChoice.Record or RemovalChoice.Everything))
        {
            throw new DeliveryException(
                $"A record is deleted from the ledger only with a removal that takes it out of OSDU (record or everything), not with {scope.ToString().ToLowerInvariant()}.");
        }

        var single = selection.Keys is { Count: 1 } only ? only[0] : (DeliveryKey?)null;
        var kind = scope == RemovalChoice.Previous ? AttemptPhases.RestorePrevious : "delete";
        return TrackAsync(kind, selection.Describe(scope, purgeLedger), single, async activity =>
        {
            var removalStarted = _context.Time.GetUtcNow().UtcDateTime;
            var ledger = RequireLedger();
            var keys = selection.Keys ?? await FilteredKeysAsync(ledger, selection.Filter!, ct).ConfigureAwait(false);
            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var results = new List<RemovalRecordResult>(keys.Count);

            // A lease that ran out is settled first, so the only records a worker holds below are ones it is sending now.
            await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var stepBack = scope == RemovalChoice.Previous
                ? new Reversals.PreviousVersionRestore(Flow, ledger, protocol, _context.Time, Actor, RunId)
                : null;
            var purged = 0;
            var undone = UndoSummary.Empty;

            foreach (var chunk in keys.Chunk(RemovalLimits.Chunk))
            {
                ct.ThrowIfCancellationRequested();

                // The chunk's records are held while they are taken out of OSDU, so no worker sends one meanwhile. A step back
                // holds none: it passes over a record with work queued or under a lease, and writes only a version OSDU held.
                purged += await UnderRemovalHoldAsync(ledger, stepBack is null ? chunk : [], async holder =>
                {
                    var records = await ledger.GetRecordsAsync(Flow.Id, chunk, ct).ConfigureAwait(false);

                    // What unfinished deliveries of these records left is undone first, through the one undo path, so the
                    // removal never leaves behind a dataset, session or version no record names.
                    var (chunkUndone, unsettled) = await UndoForRemovalAsync(ledger, protocol, records.Values.ToList(), open: true, minted: false, holder, ct).ConfigureAwait(false);
                    undone = undone.Add(chunkUndone);
                    return await RemoveChunkAsync(
                        ledger, protocol, chunk, records, scope, purgeLedger, deliverAgain: true, activity, stepBack, results, unsettled, holder, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }

            // What the removal took out of OSDU, or stepped back, leaves the cache types the pipeline puts its records into. A
            // record deleted from the ledger as well leaves no row to tell it, so its id is handed to the step.
            var purgedIds = purgeLedger
                ? results.Where(r => r.Outcome is "removed" or "already-gone" && r.TargetId is not null).Select(r => r.TargetId!).ToList()
                : null;
            var summary = RemovalSummary.Of(scope, keys.Count, results, purged) with
            {
                Undone = undone,
                CacheAfter = await PutIntoCacheAsync(removalStarted, purgedIds, ct).ConfigureAwait(false),
            };
            return (summary, summary.Describe(), results.Count == 1 ? results[0].SubmissionId : null, false);
        }, ct);
    }

    /// <summary>
    /// The records a removal's listing filter matches now, as the listing counts them. A filter that matches more than one
    /// removal takes is refused, as the API refuses it, so a removal never takes the part of what it matches that the listing's
    /// order happens to put first.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> FilteredKeysAsync(ILedger ledger, RecordQuery filter, CancellationToken ct)
    {
        var matched = await ledger.CountAsync(Flow.Id, filter, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
        if (!matched.Exact || matched.Count > RemovalLimits.MaxSelection)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The filter matches more than {RemovalLimits.MaxSelection} records as far as the listing counts; a removal takes at most {RemovalLimits.MaxSelection} at a time. Narrow the filter and remove in parts."));
        }

        return await ledger.ListKeysAsync(Flow.Id, filter, RemovalLimits.MaxSelection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One chunk of a removal (<see cref="RemoveAsync"/>, and the removal that opens <see cref="DeleteLedgerAsync"/>): the
    /// records of <paramref name="chunk"/> OSDU may hold are removed to the extent <paramref name="scope"/> asks, or with
    /// <paramref name="stepBack"/> given the version before the latest back, under one correlation id, and the ledger is settled
    /// for the records the target answered for. A record the flow never wrote to OSDU is reported as skipped. Each record's
    /// result is added to <paramref name="results"/>; returns how many records the extra step deleted from the ledger. A
    /// record in <paramref name="unsettled"/> still has artifacts its unfinished deliveries left that could not be undone, so the
    /// extra step keeps it in the ledger, where the sweep goes on undoing them. A record held under a lease other than the
    /// removal's own (<paramref name="holder"/>) is passed over: a worker is sending it, or another removal has it. With
    /// <paramref name="deliverAgain"/> (an operator's removal) each record removed is asked to be planned again, so the flow's
    /// next run delivers it again; the removal that opens deleting the ledger asks for nothing, since the run after it reads
    /// every row.
    /// </summary>
    private async Task<int> RemoveChunkAsync(
        ILedger ledger, IDeliveryProtocol protocol, IReadOnlyList<DeliveryKey> chunk, IReadOnlyDictionary<DeliveryKey, RecordState> records,
        RemovalChoice scope, bool purgeLedger, bool deliverAgain, long? activity, Reversals.PreviousVersionRestore? stepBack, List<RemovalRecordResult> results,
        IReadOnlySet<DeliveryKey> unsettled, string? holder, CancellationToken ct)
    {
        var removals = new List<RecordRemoval>(chunk.Count);
        foreach (var key in chunk)
        {
            if (!records.TryGetValue(key, out var record))
            {
                results.Add(RemovalRecordResult.Skipped(key, null, null, null, "the record is not in this flow's ledger", null));
            }
            else if (stepBack is null && record.LeaseOwner is not null && !string.Equals(record.LeaseOwner, holder, StringComparison.Ordinal))
            {
                // A worker is sending it now: removing it under that send would leave OSDU holding whichever call landed
                // last, and the ledger saying otherwise. A step back passes such a record over itself, saying why.
                results.Add(RemovalRecordResult.Skipped(key, record.SourceKey, record.Label, null, HeldElsewhere, record.LastSubmissionId));
            }
            else if (record.TargetId is null)
            {
                results.Add(RemovalRecordResult.Skipped(key, record.SourceKey, record.Label, null, "the record has no OSDU id: it was never delivered", record.LastSubmissionId));
            }
            else if (!string.Equals(record.ClaimedTargetId, record.TargetId, StringComparison.Ordinal))
            {
                // Only an id the flow claimed is the flow's to remove: a record that was only ever held never
                // wrote to OSDU, and the id it names can be another flow's record.
                results.Add(RemovalRecordResult.Skipped(
                    key, record.SourceKey, record.Label, null,
                    "the record never queued a document, so this flow wrote nothing to OSDU to remove", record.LastSubmissionId));
            }
            else
            {
                removals.Add(new RecordRemoval(key, record.TargetId, JsonMerge.ToValues(record.TargetStateJson)));
            }
        }

        if (removals.Count == 0)
        {
            return 0;
        }

        // The run's correlation id, which the chunk's calls carry and each removal attempt names, so a removal can be followed
        // into OSDU's own logs like a delivery.
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        if (stepBack is not null)
        {
            var stepped = await stepBack.RestoreAsync(removals.Select(r => records[r.Key]).ToList(), correlation.Id, ct).ConfigureAwait(false);
            for (var i = 0; i < removals.Count; i++)
            {
                results.Add(await AnnounceRestoreAsync(stepped[i], records[removals[i].Key], ct).ConfigureAwait(false));
            }

            return 0;
        }

        var outcomes = await protocol.DeleteBatchAsync(removals, scope.Scope(), ct).ConfigureAwait(false);

        // The ledger settles the whole chunk in one write, and only for the records the target actually
        // answered for: a record whose call failed keeps the state it had, so a retry of the removal is
        // still the removal of a record that is still there.
        var settled = outcomes.Where(o => o.Succeeded).Select(o => o.Removal.Key).ToList();
        await ledger.MarkRemovedAsync(Flow.Id, settled, scope.Scope(), Actor, _context.Time.GetUtcNow().UtcDateTime, correlation.Id, deliverAgain, ct: ct).ConfigureAwait(false);

        // The extra step: what OSDU no longer holds goes from the ledger too, one line of each kept; a record whose unfinished
        // deliveries left something the undo could not take back stays, so nothing in OSDU loses the record that names it.
        var purgeable = settled.Where(k => !unsettled.Contains(k)).ToList();
        var gone = purgeLedger && purgeable.Count > 0
            ? (await ledger.PurgeRecordsAsync(Flow.Id, purgeable, Actor, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        foreach (var outcome in outcomes)
        {
            var result = await AnnounceRemovalAsync(outcome, scope.Scope(), records[outcome.Removal.Key], ct).ConfigureAwait(false);
            results.Add(gone.Contains(outcome.Removal.Key) ? result with { Detail = result.Detail + "; deleted from the ledger" }
                : purgeLedger && outcome.Succeeded && unsettled.Contains(outcome.Removal.Key)
                    ? result with { Detail = result.Detail + "; kept in the ledger, since what its unfinished deliveries left could not all be undone yet" }
                    : result);
        }

        return gone.Count;
    }

    /// <summary>
    /// Runs <paramref name="remove"/> while the records of <paramref name="keys"/> no lease holds are held for a removal
    /// (<see cref="ILedger.HoldForRemovalAsync"/>), handing it the hold's token (null when it holds nothing): no worker claims
    /// one of them, so OSDU never ends up holding what a worker sent under the removal while the ledger says removed. The
    /// hold is renewed every half of the flow's lease while the removal goes on, and let go when it ends, whatever its
    /// outcome; a removal that stops lets its records go once that lease runs out.
    /// </summary>
    private async Task<T> UnderRemovalHoldAsync<T>(ILedger ledger, IReadOnlyCollection<DeliveryKey> keys, Func<string?, Task<T>> remove, CancellationToken ct)
    {
        var length = TimeSpan.FromSeconds(Math.Max(Flow.Reliability.LeaseSeconds, 1));
        var hold = await ledger.HoldForRemovalAsync(Flow.Id, keys, RemovalOwner, length, _context.Time.GetUtcNow().UtcDateTime, RunId, ct).ConfigureAwait(false);
        if (hold.Token is not { } token)
        {
            return await remove(null).ConfigureAwait(false);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keeping = KeepRemovalHoldAsync(ledger, token, length, stop.Token);
        try
        {
            return await remove(token).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await keeping.ConfigureAwait(false);
            try
            {
                await ledger.CloseLeaseAsync(token, new LeaseClosing { End = LeaseEnd.Done }, _context.Time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or DeliveryException)
            {
                _log.LogWarning(
                    "Could not let go of the records the removal held ({Message}); its lease runs out within {Seconds}s and the flow's next claim lets them go.",
                    ex.Message, (int)length.TotalSeconds);
            }
        }
    }

    /// <summary>Renews a removal's hold every half lease until <paramref name="ct"/> says the removal ended.</summary>
    private async Task KeepRemovalHoldAsync(ILedger ledger, string token, TimeSpan length, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(length.TotalMilliseconds / 2, 1000));
        try
        {
            while (true)
            {
                await Task.Delay(interval, _context.Time, ct).ConfigureAwait(false);
                try
                {
                    if (!await ledger.RenewLeaseAsync(token, length, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
                    {
                        _log.LogWarning(
                            "The removal's hold on its records ran out before it was renewed, so a worker may claim one of them while it is removed; each record's history shows what was done to it.");
                        return;
                    }
                }
                catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
                {
                    _log.LogWarning("Renewing the removal's hold on its records failed ({Message}); trying again in {Seconds}s.", ex.Message, (int)interval.TotalSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The removal ended, and its hold is let go by the caller: stopping here is how renewing ends.
            return;
        }
    }

    /// <summary>
    /// Undoes, before records are removed or their ledger deleted (osdu/docs/reference/concepts/artifacts-and-undo.md, When
    /// the undo runs, and what it is given), what deliveries of <paramref name="records"/> left in OSDU, through the one
    /// undo path, for each record no worker holds: with <paramref name="open"/>, every open artifact of their unfinished
    /// deliveries, the record itself included; with <paramref name="minted"/>, the ids their committed deliveries minted
    /// (datasets, content, outputs), removed reversibly, since nothing names them once the ledger is gone. Returns what the
    /// undo did, and the records left with an artifact it could not take back yet, which the sweep goes on undoing. A
    /// record the removal itself holds (<paramref name="holder"/>) counts as one no worker holds.
    /// </summary>
    private async Task<(UndoSummary Undone, IReadOnlySet<DeliveryKey> Unsettled)> UndoForRemovalAsync(
        ILedger ledger, IDeliveryProtocol protocol, IReadOnlyList<RecordState> records, bool open, bool minted, string? holder, CancellationToken ct)
    {
        var free = records.Where(r => r.LeaseOwner is null || string.Equals(r.LeaseOwner, holder, StringComparison.Ordinal)).ToDictionary(r => r.DeliveryKey);
        if (!protocol.Undoes || free.Count == 0)
        {
            return (UndoSummary.Empty, new HashSet<DeliveryKey>());
        }

        var artifacts = new List<LedgerArtifact>();
        if (open)
        {
            artifacts.AddRange(await ledger.OpenArtifactsAsync(Flow.Id, free.Keys, ct).ConfigureAwait(false));
        }

        if (minted)
        {
            artifacts.AddRange(await ledger.MintedArtifactsAsync(Flow.Id, free.Keys, ct).ConfigureAwait(false));
        }

        if (artifacts.Count == 0)
        {
            return (UndoSummary.Empty, new HashSet<DeliveryKey>());
        }

        var requests = artifacts
            .GroupBy(a => a.Key)
            .Select(g => new UndoRequest(free[g.Key], g.OrderBy(a => a.ArtifactId).ToList(), UndoReason.Removed, KeepRecord: false))
            .ToList();
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        var undos = await new UndoRunner(protocol, _context.Time, Truncate(Actor, 200), RunId).RunAsync(requests, correlation.Id, ct).ConfigureAwait(false);
        await ledger.SettleArtifactsAsync(Flow.Id, undos, minted, ct).ConfigureAwait(false);
        var unsettled = undos.Where(u => u.Settlements.Any(s => s.Status == ArtifactStatus.Failed)).Select(u => u.DeliveryKey).ToHashSet();
        var undone = UndoSummary.Of(undos);
        if (undone.Failed > 0)
        {
            _log.LogWarning("{Summary}; the undos that failed are tried again later.", undone.Describe());
        }

        return (undone, unsettled);
    }

    /// <summary>
    /// Deletes the ledger (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the ledger). Nothing happens
    /// unless <paramref name="confirm"/> names the partition the ledger is kept in, and no worker holds a lease on its
    /// records. Then every record OSDU may hold is removed from it first, reversibly (the record scope), through the
    /// removal a selection takes, a page of the ledger at a time in key order; a record the ledger already marks
    /// removed is not asked about again. Only when OSDU answered for every one is the ledger deleted whole
    /// (<see cref="ILedger.DeleteLedgerAsync"/>): its records, whatever their state, each kept as one line, and what it
    /// keeps of its runs, so the next run reads every row and delivers each as a new record. Before a page's records
    /// are removed, what their unfinished deliveries left is undone; once a record is out of OSDU, the datasets,
    /// content and outputs its committed deliveries minted are removed reversibly too, since nothing would name them
    /// afterwards. A removal OSDU refused, or an undo it could not take, leaves the ledger as it was, with the records
    /// it did remove marked removed, and fails naming them: OSDU never holds a record or an id the ledger forgot. A
    /// route with no reversible removal is refused outright.
    /// </summary>
    public Task<LedgerDeleteSummary> DeleteLedgerAsync(string confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirm);
        if (Flow.Target.Protocol is DeliveryProtocol.Dspdm or DeliveryProtocol.Etp)
        {
            throw new DeliveryException(
                $"'{Flow.Label}' is delivered by the {DeliveryProtocols.Name(Flow.Target.Protocol)} route, which keeps nothing it deletes, so its records have no reversible removal; deleting the ledger removes every record from OSDU reversibly first, and is refused here.");
        }

        return TrackAsync(DeliveryOperations.DeleteLedger, new { confirm }, null, async activity =>
        {
            var ledger = RequireLedger();
            var partition = _ledgerPartition ?? throw new InvalidOperationException("The ledger is registered before an intervention's action runs, which names its partition.");
            if (!string.Equals(confirm.Trim(), partition, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException(
                    $"The run names partition '{confirm.Trim()}' to confirm deleting the ledger of '{Flow.Label}', which is kept in partition '{partition}', so nothing was removed or deleted. Name '{partition}' to delete it.");
            }

            var now = _context.Time.GetUtcNow().UtcDateTime;
            await ledger.RecoverExpiredLeasesAsync(Flow.Id, now, ct).ConfigureAwait(false);
            if (await ledger.NextLeaseExpiryAsync(Flow.Id, null, ct).ConfigureAwait(false) is { } leased)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A worker holds a lease on records of '{Flow.Label}' until {leased:u}, so nothing was removed or deleted: work is in flight on this ledger. Delete it once that work has ended."));
            }

            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var tally = new RemovalTally();
            var undone = UndoSummary.Empty;
            DeliveryKey? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await ledger.ListRecordsAsync(Flow.Id, after, RemovalLimits.Chunk, ct).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                after = page[^1].DeliveryKey;

                // The page's records are held while they are taken out of OSDU, so no run that started since sends one meanwhile;
                // one a worker holds already is refused below, and the ledger is kept.
                var pageKeys = page.Select(r => r.DeliveryKey).ToList();
                await UnderRemovalHoldAsync(ledger, pageKeys, async holder =>
                {
                    var read = await ledger.GetRecordsAsync(Flow.Id, pageKeys, ct).ConfigureAwait(false);
                    var current = pageKeys.Where(read.ContainsKey).Select(k => read[k]).ToList();

                    // What the page's unfinished deliveries left, a removed record's included, is undone before anything is removed.
                    undone = undone.Add((await UndoForRemovalAsync(ledger, protocol, current, open: true, minted: false, holder, ct).ConfigureAwait(false)).Undone);
                    var held = current.Where(r => r.Status != RecordStatus.Deleted).ToList();
                    tally.AlreadyRemoved += current.Count - held.Count;
                    var results = new List<RemovalRecordResult>(held.Count);
                    if (held.Count > 0)
                    {
                        await RemoveChunkAsync(
                            ledger, protocol, held.Select(r => r.DeliveryKey).ToList(), held.ToDictionary(r => r.DeliveryKey), RemovalChoice.Record,
                            purgeLedger: false, deliverAgain: false, activity, stepBack: null, results, new HashSet<DeliveryKey>(), holder, ct).ConfigureAwait(false);
                        tally.Add(results);
                    }

                    // Once a record is out of OSDU, the ids its committed deliveries minted are removed reversibly with it.
                    var outOfOsdu = results.Where(r => r.Outcome is "removed" or "already-gone").Select(r => new DeliveryKey(r.DeliveryKey)).ToHashSet();
                    var retired = current.Where(r => r.Status == RecordStatus.Deleted || outOfOsdu.Contains(r.DeliveryKey)).ToList();
                    undone = undone.Add((await UndoForRemovalAsync(ledger, protocol, retired, open: false, minted: true, holder, ct).ConfigureAwait(false)).Undone);
                    return 0;
                }, ct).ConfigureAwait(false);
            }

            if (tally.Failed > 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tally.Failed} record(s) of '{Flow.Label}' could not be removed from OSDU, so the ledger was kept as it was and OSDU holds nothing it forgot: {tally.Failures}. The {tally.Removed + tally.AlreadyGone} record(s) OSDU answered for are marked removed; delete the ledger again once OSDU removes the rest."));
            }

            // An id the undo could not take back keeps the ledger too: the ledger is the one place that names it.
            var left = await ledger.ArtifactCountsAsync(Flow.Id, ct).ConfigureAwait(false);
            if (left.ToUndo > 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{left.ToUndo} item(s) the deliveries of '{Flow.Label}' left in OSDU could not be undone ({undone.Describe()}), so the ledger was kept as it was and OSDU holds nothing it forgot. Every record OSDU answered for is marked removed; the sweep goes on undoing the rest, and the ledger can be deleted again once it has."));
            }

            // Every record is marked removed now, and the ledger still names them: what OSDU no longer holds leaves the cache types
            // the pipeline puts its records into before the ledger is deleted.
            var cacheAfter = await PutIntoCacheAsync(now, ct).ConfigureAwait(false);
            var deleted = await ledger.DeleteLedgerAsync(Flow.Id, Actor, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var summary = new LedgerDeleteSummary(partition, tally.Removed, tally.AlreadyGone, tally.AlreadyRemoved, tally.Skipped, deleted)
            {
                Undone = undone,
                CacheAfter = cacheAfter,
            };
            return (summary, summary.Describe(), (Guid?)null, false);
        }, ct);
    }

    /// <summary>What the removal opening <see cref="DeleteLedgerAsync"/> did, counted as it walks the ledger, and its first refusals.</summary>
    private sealed class RemovalTally
    {
        private const int Named = 3;

        private readonly List<string> _failures = [];

        public int Removed { get; private set; }

        public int AlreadyGone { get; private set; }

        public int AlreadyRemoved { get; set; }

        public int Skipped { get; private set; }

        public int Failed { get; private set; }

        /// <summary>The first refusals, each with its record and why, and how many more there were.</summary>
        public string Failures => string.Join("; ", _failures) + (Failed > _failures.Count ? string.Create(CultureInfo.InvariantCulture, $"; and {Failed - _failures.Count} more") : string.Empty);

        public void Add(IEnumerable<RemovalRecordResult> results)
        {
            foreach (var result in results)
            {
                switch (result.Outcome)
                {
                    case "removed":
                        Removed++;
                        break;
                    case "already-gone":
                        AlreadyGone++;
                        break;
                    case "skipped" when !string.Equals(result.Detail, HeldElsewhere, StringComparison.Ordinal):
                        Skipped++;
                        break;
                    default:
                        Failed++;
                        if (_failures.Count < Named)
                        {
                            _failures.Add($"{result.TargetId ?? result.SourceKey ?? result.DeliveryKey.ToString()}: {result.Detail}");
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// Reverses what <paramref name="source"/> delivered (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a
    /// run or a submission): opens or resumes its reversal, lists every record it delivered, and puts each back as OSDU
    /// held it before, through the flow's protocol, every step written to the ledger under this run's activity. Resumable:
    /// a reversal stopped anywhere is finished by running it again.
    /// </summary>
    public Task<Reversals.ReversalSummary> ReverseAsync(ReversalSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return TrackAsync("reverse", new { source = source.Kind, sourceId = source.Id }, null, async activity =>
        {
            var reversalStarted = _context.Time.GetUtcNow().UtcDateTime;
            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var runner = new Reversals.ReversalRunner(Flow, RequireLedger(), protocol, _context.Time, _log, _context.Trace, Actor, RunId);
            var summary = await runner.RunAsync(source, activity, ct).ConfigureAwait(false);

            // What the reversal restored or removed in OSDU goes into the cache types the pipeline puts its records into.
            summary = summary with { CacheAfter = await PutIntoCacheAsync(reversalStarted, ct).ConfigureAwait(false) };
            return (summary, summary.Describe(source), source.IsRun ? (Guid?)null : source.Id, false);
        }, ct);
    }

    /// <summary>
    /// Sends what a stopped run left leased in a submission. The platform runs one execution of a flow at a time, so a
    /// record still leased when a run of that flow starts belongs to a worker that is gone: a control plane or node
    /// stopped mid-delivery, whose run was recovered and requeued. Each such lease is waited out, reclaimed (the ledger
    /// notes why) and the record sent, its completed steps resumed. A lease running out further ahead than the flow's
    /// own lease is not a stopped run's, so it is left alone with a warning, and records in backoff are not waited for.
    /// </summary>
    private async Task<WorkerSummary> SendOrphanedLeasesAsync(DeliveryWorker worker, Guid submissionId, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var longest = TimeSpan.FromSeconds(Flow.Reliability.LeaseSeconds) + LeaseExpiryMargin;
        var total = WorkerSummary.Empty;
        while (await ledger.NextLeaseExpiryAsync(Flow.Id, submissionId, ct).ConfigureAwait(false) is { } expiry)
        {
            ct.ThrowIfCancellationRequested();
            var now = _context.Time.GetUtcNow().UtcDateTime;
            if (expiry >= now)
            {
                var wait = expiry - now + LeaseExpiryMargin;
                if (wait > longest)
                {
                    _log.LogWarning(
                        "A record of submission {SubmissionId} is leased until {Expiry:o}, further ahead than this flow's lease of {Seconds}s, so a running worker holds it and it is left alone.",
                        submissionId, expiry, Flow.Reliability.LeaseSeconds);
                    break;
                }

                _log.LogInformation(
                    "A record of submission {SubmissionId} is still leased by a run that stopped; waiting {Seconds}s for the lease to run out.",
                    submissionId, (int)Math.Ceiling(wait.TotalSeconds));
                await Task.Delay(wait, _context.Time, ct).ConfigureAwait(false);
            }

            var reclaimed = await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var sent = await PassUntilNothingClaimableAsync(worker, submissionId, ct).ConfigureAwait(false);
            total = total.Add(sent);
            if (reclaimed == 0 && sent.Processed == 0)
            {
                // The wait ended without the lease running out, or the lease was reclaimed elsewhere and nothing of it is
                // left to send: either way there is nothing this run can take over.
                _log.LogWarning(
                    "The lease on a record of submission {SubmissionId} (due to run out at {Expiry:o}) could not be reclaimed, so the record is left for a later run.",
                    submissionId, expiry);
                break;
            }

            if (reclaimed > 0)
            {
                _log.LogInformation("Recovered the leases a stopped run left behind ({Count} record(s) settled or handed back), and sent {Sent}.", reclaimed, sent.Processed);
            }
        }

        return total;
    }

    /// <summary>Claim passes over one submission until nothing of it is claimable, without waiting for records in backoff.</summary>
    private static async Task<WorkerSummary> PassUntilNothingClaimableAsync(DeliveryWorker worker, Guid submissionId, CancellationToken ct)
    {
        var total = WorkerSummary.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pass = await worker.PassAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(pass);
            if (pass.Processed == 0 && pass.Batches == 0 && pass.Waiting == 0)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// Sends what submissions of the flow no run works on any more still hold, <see cref="UnattendedSubmissionsPerPage"/>
    /// submissions at a time (osdu/docs/reference/concepts/submissions.md, Recovering a stopped submission). Two kinds belong
    /// to no run: a settled submission holding records released back to pending with their rendered documents after its
    /// run was over, and a submission its run left received, planned or running when it stopped (its node was lost, its
    /// process ended, it ran out of attempts) with records still planned in its work batches. A newer plan skips those
    /// records, because their rows are what they already queue, so without this they would wait for a drain by hand.
    /// Each one is taken over only when no run holds it (<see cref="ILedger.TakeOverSubmissionAsync"/>), so a run still
    /// planning or sending it, a fan-out member included, is never raced, and only when no lease that has not run out holds
    /// its work; it is passed over until nothing of it is claimable (records in backoff are not waited for), and closed:
    /// a settled one has its totals counted again when anything of it was sent, and one its run left open is closed as
    /// its records stand, failed with why when records of it are still to be sent or its planning never finished. Every
    /// claim is the one a run of the submission makes, so a record is sent once, resuming after the steps a stopped try
    /// recorded. A submission is visited once per run: what is left of it is the next run's, so the run ends however many
    /// submissions it finds.
    /// </summary>
    private async Task<WorkerSummary> SendUnattendedAsync(DeliveryWorker worker, Guid current, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var visited = new HashSet<Guid> { current };
        var total = WorkerSummary.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListUnattendedSubmissionsAsync(Flow.Id, visited, _context.Time.GetUtcNow().UtcDateTime, UnattendedSubmissionsPerPage, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return total;
            }

            foreach (var submission in page)
            {
                visited.Add(submission.SubmissionId);
                total = total.Add(await SendUnattendedAsync(worker, submission, ct).ConfigureAwait(false));
            }
        }
    }

    /// <summary>Takes over one submission no run may work on any more, sends what it holds and closes it (see above).</summary>
    private async Task<WorkerSummary> SendUnattendedAsync(DeliveryWorker worker, SubmissionState submission, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var submissionId = submission.SubmissionId;
        var settled = submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed;
        await using var hold = await ledger.TakeOverSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (hold is null)
        {
            _log.LogInformation(
                "Submission {SubmissionId} ({Status}) is held by a run that still works on it, so it is left to that run.",
                submissionId, Status(submission.Status));
            return WorkerSummary.Empty;
        }

        if (!settled)
        {
            _log.LogWarning(
                "Submission {SubmissionId} was left {Status} by {Run}, which no longer works on it: it ended before the submission was settled. This run takes it over, sends what it still holds and closes it.",
                submissionId, Status(submission.Status), submission.RunId is { } stoppedRun ? $"run {stoppedRun:D}" : "its run");
        }

        var sent = await PassUntilNothingClaimableAsync(worker, submissionId, ct).ConfigureAwait(false);
        if (settled)
        {
            if (sent.Processed > 0 || sent.Waiting > 0)
            {
                await Intake.CompleteAsync(submissionId, Flow.Id, ct).ConfigureAwait(false);
                _log.LogInformation(
                    "Sent {Count} record(s) that submission {SubmissionId} still held after it settled (released back to pending with their rendered documents).",
                    sent.Processed, submissionId);
            }

            return sent;
        }

        if (await ledger.IsSubmissionLeasedAsync(Flow.Id, submissionId, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
        {
            // A worker took up some of it meanwhile (a drain of the whole flow claims any submission's batches): closing it now
            // would count records that worker is still sending, so it is left open for the next run to close.
            _log.LogInformation(
                "Sent {Count} record(s) of submission {SubmissionId}; a worker is sending the rest under a lease of its own, so the submission is left open for the flow's next run to close.",
                sent.Processed, submissionId);
            return sent;
        }

        var closed = await Intake.StopAsync(submissionId, Flow.Id, StoppedReason(submission), ct).ConfigureAwait(false);
        _log.LogInformation(
            "Took over submission {SubmissionId}: sent {Count} record(s) it still held, and closed it {Status}: {Summary}.",
            submissionId, sent.Processed, Status(closed.Status), SubmissionIntake.Summarize(closed));
        return sent;
    }

    /// <summary>
    /// Why a submission this run took over is closed failed, when it is: records of it are still to be sent (in backoff for a
    /// retry), or the run that registered it ended before its planning finished.
    /// </summary>
    private string StoppedReason(SubmissionState submission)
    {
        var stopped = submission.RunId is { } run ? $"run {run:D}" : "the run that registered it";
        var takenBy = RunId is { } own ? $"run {own:D}" : "a later run of the flow";
        return submission.Status == SubmissionStatus.Received
            ? $"stopped: {stopped} ended before its planning finished; {takenBy} took it over and sent what it had planned, and the rows it had not reached are left to the flow's plans, which read them again: it moved no watermark and answered no request to plan them"
            : $"stopped: {stopped} ended before its records were all sent; {takenBy} took it over and sent what was due, and the flow's next run sends the rest";
    }

    private static string Status(SubmissionStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>
    /// Asks the legal service about the mapping's legal tags before a run plans or sends anything. Every record the
    /// mapping renders carries the same tags, and storage refuses a record whose tag is unknown or expired, so a run
    /// that starts with a bad tag would fail each record it plans with the same error; refusing the run names the tag
    /// and the service's reason once, before anything reaches the ledger or OSDU. A target that does not ask (see
    /// <see cref="IDeliveryProtocol.InvalidLegalTagsAsync"/>) is logged as not checked, never taken as valid.
    /// </summary>
    public Task CheckLegalTagsAsync(CancellationToken ct = default) => EnsureLegalTagsAsync(ct);

    /// <summary>The legal tag check, asked once per runtime; see <see cref="CheckLegalTagsAsync"/>.</summary>
    private async Task EnsureLegalTagsAsync(CancellationToken ct)
    {
        if (_legalTagsChecked)
        {
            return;
        }

        if (Mapping.Mapping.Envelope.LegalTags.Count == 0)
        {
            // A mapping of DSPDM business object rows puts no legal tags on anything: its rows are no OSDU records.
            _legalTagsChecked = true;
            return;
        }

        var tags = Mapping.Mapping.Envelope.LegalTags.Select(tag => MappingEntry.ExpandParameters(tag, Mapping.Renderer.ParameterValue)).ToList();
        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        var invalid = await protocol.InvalidLegalTagsAsync(tags, ct).ConfigureAwait(false);
        if (invalid is null)
        {
            _log.LogInformation(
                "The mapping's legal tags ({Tags}) were not checked with the legal service before the run: the target does not ask it (validateLegalTags is off, or a well log endpoint names neither ddmsRoot nor legalValidatePath).",
                string.Join(", ", tags));
            _legalTagsChecked = true;
            return;
        }

        if (invalid.Count > 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The legal service refuses {invalid.Count} of the legal tag(s) mapping {Mapping.Mapping.Reference} puts on every record, so nothing was planned or sent: ")
                + string.Join("; ", invalid.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ": " + kv.Value)));
        }

        _log.LogInformation("The legal service accepts the legal tag(s) the mapping puts on every record ({Tags}).", string.Join(", ", tags));
        _legalTagsChecked = true;
    }

    /// <summary>Announces one record's removal, now that the ledger holds it, and says what it did.</summary>
    private async Task<RemovalRecordResult> AnnounceRemovalAsync(RemovalResult outcome, RemovalScope scope, RecordState record, CancellationToken ct)
    {
        var key = outcome.Removal.Key;
        if (outcome.Failure is { } failure)
        {
            return RemovalRecordResult.Failed(key, record.SourceKey, record.Label, record.TargetId, HeaderRedaction.RedactMessage(failure.Message), record.LastSubmissionId);
        }

        var result = outcome.Outcome!;
        var now = _context.Time.GetUtcNow().UtcDateTime;
        await _context.Listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = now,
            FlowId = Flow.Id,
            FlowName = Flow.Label,
            Interface = Flow.Interface,
            Kind = scope == RemovalScope.History ? "record.history-purged" : "record.deleted",
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = key,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = record.TargetVersion,
            Worker = Actor,
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            Detail = result.Detail,
        }, ct).ConfigureAwait(false);

        return result.AlreadyGone
            ? RemovalRecordResult.AlreadyGone(key, record.SourceKey, record.Label, record.TargetId, result.Detail, record.LastSubmissionId)
            : RemovalRecordResult.Removed(key, record.SourceKey, record.Label, record.TargetId, result.Detail, record.LastSubmissionId);
    }

    /// <summary>Announces one record written back at its version before the latest, now that the ledger holds it, and says what it did.</summary>
    private async Task<RemovalRecordResult> AnnounceRestoreAsync(Reversals.PreviousVersionStep step, RecordState record, CancellationToken ct)
    {
        if (step.Restored is not { } restored)
        {
            return step.Result;
        }

        await _context.Listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _context.Time.GetUtcNow().UtcDateTime,
            FlowId = Flow.Id,
            FlowName = Flow.Label,
            Interface = Flow.Interface,
            Kind = "record.restored",
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = record.DeliveryKey,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = restored.NewVersion,
            Worker = Actor,
            Phase = AttemptPhases.RestorePrevious,
            Detail = step.Result.Detail,
        }, ct).ConfigureAwait(false);
        return step.Result;
    }

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
        _holds?.Dispose();
        (_search as IDisposable)?.Dispose();
        _target.Dispose();
    }
}

/// <summary>
/// A page of the records the ledger asked to be planned again: the key tuples of those that can be read by key, the last
/// record the page read (null when there was none), which the next page starts after, and how many it read that carry no
/// source key.
/// </summary>
public sealed record PlanRequestedPage(IReadOnlyList<KeyTuple> Keys, PlanRequestedRecord? Last, int Keyless)
{
    /// <summary>The requests the page read by key, as each record carried it: what a pass that planned them answered.</summary>
    public IReadOnlyList<PlanRequestedRecord> Requests { get; init; } = [];

    /// <summary>
    /// The requests of records deleted from the ledger the page read by key, as their lines carried them: a pass plans each
    /// as a new record, and <see cref="FlowRuntime.SettlePurgedRequestsAsync"/> answers them.
    /// </summary>
    public IReadOnlyList<PlanRequestedRecord> PurgedRequests { get; init; } = [];

    /// <summary>The requests of records that carry no source key, which no pass reads: a read of the whole scope plans them.</summary>
    public IReadOnlyList<PlanRequestedRecord> KeylessRequests { get; init; } = [];
}

/// <summary>
/// What a pass did with the records deleted from the ledger whose rows it read (<see cref="FlowRuntime.SettlePurgedRequestsAsync"/>):
/// planned as records again, gone from the ingestion table (both answered), outside the run's scope, and read but not planned
/// (both left for a later run).
/// </summary>
public sealed record PurgedRequestsSettled(int Planned, int Gone, int Elsewhere, int Unplanned)
{
    public override string ToString()
    {
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{Planned} planned as records again") };
        if (Gone > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Gone} whose row is gone from the ingestion table, not read again"));
        }

        if (Elsewhere > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Elsewhere} outside this run's scope, left for the run of their own"));
        }

        if (Unplanned > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Unplanned} read but not planned, left for the next run"));
        }

        return string.Join(", ", parts);
    }
}
