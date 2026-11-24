using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

// What a run's records go through as they are sent: the worker, with the records it sets aside; the validation gate
// every document passes before it is sent, with the template versions it checks documents against; and which records
// this interface's records wait for, from the order of its source's interfaces (osdu/docs/reference/flow/interfaces.md).
public sealed partial class FlowRuntime
{
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

    /// <summary>
    /// The records this run's workers could not send because the documents queued for them were not there to read, which
    /// the run reads by key and sends before it ends (<see cref="DeliveryExecutor.DeliverSetAsideAsync"/>).
    /// </summary>
    internal SetAsideRecords SetAside { get; } = new();

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
            SetAside = SetAside,
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
}
