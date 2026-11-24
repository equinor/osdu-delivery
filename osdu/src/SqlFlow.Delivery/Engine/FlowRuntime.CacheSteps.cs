using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine;

// The pipeline's cache steps (osdu/docs/reference/flow/delivery.md, cache): resolved once against the partition's
// cache; the before step a deliver run starts with, after which the run renders against the newest version of the
// cache; and the after step a deliver run, a flow-wide drain, a removal and a reversal end with.
public sealed partial class FlowRuntime
{
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

        var resolver = new RenderResolver(Mappings, _context.Cache, _context.DocumentTemplates, _context.Secrets, _search, RenderResolver.RestrictionsOf(_context.Ledger));
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
}
