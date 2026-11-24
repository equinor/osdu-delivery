using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
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

    /// <summary>The schemas read from a partition's Schema service, kept for a few minutes, so going through a mapping's records asks for each once.</summary>
    public Checks.SchemaServiceReads SchemaReads { get; init; } = new();

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
