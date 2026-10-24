using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>What one inventory's build, reconcile or plan came to.</summary>
public sealed record InventorySummary(
    string Inventory,
    string Kind,
    string Status,
    long? InventoryRunId,
    long Listed,
    long Added,
    long Changed,
    long Gone,
    long Returned,
    long VersionsRead,
    long MissingChecked,
    IReadOnlyDictionary<string, long> Findings,
    IReadOnlyList<InventoryOwner> Owners,
    string? OwnersSource,
    int Pages,
    long Requests,
    string? Error)
{
    /// <summary>The findings a report raises, in a line.</summary>
    public string Describe() => Status == InventoryRunStatus.Failed
        ? $"{Inventory}: failed: {Error}"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Inventory}: {Listed} listed ({Added} new, {Changed} changed, {Gone} gone, {Returned} served again); {new InventoryCounts(Findings).Describe()}");
}

/// <summary>What a run of an inventory flow came to, inventory by inventory.</summary>
public sealed record InventoryOutcome(string Operation, string Flow, string Partition, int Completed, int Failed, IReadOnlyList<InventorySummary> Inventories)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Operation} of {Completed + Failed} inventory(ies) of '{Flow}' in '{Partition}': {Completed} completed, {Failed} failed. {string.Join(" ", Inventories.Select(i => i.Describe()))}");
}

/// <summary>What a plan found of one inventory: how it would read, and how many records it would read.</summary>
public sealed record InventoryPlanSummary(string Inventory, string Kind, string? Query, string Read, string Versions, long? Records, IReadOnlyList<string> Kinds, IReadOnlyList<string> Problems);

/// <summary>What a plan of an inventory flow found, inventory by inventory; it reads no id and keeps nothing.</summary>
public sealed record InventoryPlanOutcome(string Operation, string Flow, string Partition, IReadOnlyList<InventoryPlanSummary> Inventories);

/// <summary>Every inventory of a run was taken up and at least one failed: the run ends failed, carrying how each came out.</summary>
public sealed class InventoryRunsFailedException : DeliveryException
{
    public InventoryRunsFailedException(InventoryOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public InventoryOutcome Outcome { get; }
}

/// <summary>
/// Runs an inventory flow (osdu/docs/reference/flow/inventory.md): a build reads every id each inventory's kind holds,
/// stages the read, merges it into the inventory once whole, reads the versions of what is new or moved when the
/// inventory keeps every version, and reconciles; a reconcile compares the inventory as its last build left it with the
/// ledgers as they stand now. The comparison reads every ledger of the partition and writes none of them; the ids a
/// ledger expects that the read did not list are read from storage, within the flow's bound, to tell missing from
/// merely unlisted. Nothing is written to OSDU, but by a removal an operator asks of a flow that allows it
/// (<see cref="RemoveAsync"/>).
/// </summary>
public sealed partial class InventoryRunner
{
    /// <summary>The ids a build stages in one write.</summary>
    public const int StageChunk = 10_000;

    /// <summary>The records whose versions one pass reads, and writes in one transaction.</summary>
    public const int VersionsChunk = 500;

    /// <summary>An identity is inferred as an owner when it created at least this share of the ids a ledger claims (and at least one).</summary>
    public const double InferredOwnerShare = 0.01;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly EngineContext _context;
    private readonly InventoryFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ILogger _log;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    /// <param name="context">The run's engine context.</param>
    /// <param name="flow">The flow bound to the partition the run reads (or a flow whose partition is its header's).</param>
    /// <param name="values">The flow's parameter values, resolved.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="transport">Replaces the built transport (null builds the configured one); it exists for tests.</param>
    /// <param name="allowLoopback">Lets the URL guard accept a loopback endpoint; it exists for tests.</param>
    public InventoryRunner(
        EngineContext context, InventoryFlowDefinition flow, IReadOnlyDictionary<string, string> values, ILogger log,
        HttpMessageHandler? transport = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _flow = flow;
        _values = values;
        _log = log;
        _transport = transport;
        _allowLoopback = allowLoopback || EngineContext.LoopbackAllowed;
    }

    private DateTime Now => _context.Time.GetUtcNow().UtcDateTime;

    /// <summary>Builds the inventories the run selects (every one when it names none): reads, merges and reconciles each.</summary>
    public Task<InventoryOutcome> BuildAsync(IReadOnlyCollection<string> names, Guid runId, string actor, CancellationToken ct)
        => RunAsync(InventoryRunStatus.Build, names, runId, actor, ct);

    /// <summary>Reconciles the inventories the run selects with the ledgers as they stand now, reading OSDU only for the ids a ledger expects.</summary>
    public Task<InventoryOutcome> ReconcileAsync(IReadOnlyCollection<string> names, Guid runId, string actor, CancellationToken ct)
        => RunAsync(InventoryRunStatus.Reconcile, names, runId, actor, ct);

    /// <summary>Counts what each inventory the run selects would read, and says what would stop it; reads no id and keeps nothing.</summary>
    public async Task<InventoryPlanOutcome> PlanAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);
        var summaries = new List<InventoryPlanSummary>(selected.Count);
        foreach (var inventory in selected)
        {
            var problems = new List<string>();
            long? records = null;
            IReadOnlyList<string> kinds = [];
            var query = Query(inventory, partition);
            try
            {
                records = await search.CountAsync(new OsduSearchQuery { Kind = inventory.Kind, Query = query, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
                if (_flow.Source.Read == InventoryRead.Storage && !OsduKind.IsExact(inventory.Kind))
                {
                    kinds = await StorageInventoryReader.KindsAsync(client, _context.Time, _flow.Source, inventory.Kind, new InventoryReadStats(), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (Expected(ex, ct))
            {
                problems.Add(HeaderRedaction.RedactMessage(ex.Message));
            }

            if (_flow.Source.Read == InventoryRead.Storage && records is > 100_000)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"storage lists {records} records a thousand at a time and reads their headers a thousand at a time: about {records / 500} requests"));
            }

            summaries.Add(new InventoryPlanSummary(
                inventory.Name, inventory.Kind, query, ReadName(_flow.Source.Read), inventory.Versions == InventoryVersions.All ? "all" : "latest", records, kinds, problems));
        }

        return new InventoryPlanOutcome(DeliveryOperations.Plan, _flow.Name, partition, summaries);
    }

    private async Task<InventoryOutcome> RunAsync(string operation, IReadOnlyCollection<string> names, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        await ledger.RegisterLedgerAsync(
            new LedgerEntry
            {
                FlowId = _flow.LedgerId,
                Partition = partition,
                Kind = LedgerKinds.Inventory,
                FlowName = _flow.Name,
                LedgerName = _flow.LedgerName,
            },
            ct).ConfigureAwait(false);
        _log.LogInformation("inventory flow '{Flow}' in partition '{Partition}': {Operation} of {Count} inventory(ies), reading through {Read}", _flow.Name, partition, operation, selected.Count, ReadName(_flow.Source.Read));

        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var summaries = new List<InventorySummary>(selected.Count);
        foreach (var inventory in selected)
        {
            summaries.Add(await RunOneAsync(ledger, client, operation, inventory, partition, runId, actor, ct).ConfigureAwait(false));
        }

        var outcome = new InventoryOutcome(
            operation, _flow.Name, partition,
            summaries.Count(s => s.Status == InventoryRunStatus.Completed), summaries.Count(s => s.Status == InventoryRunStatus.Failed), summaries);
        return outcome.Failed > 0 ? throw new InventoryRunsFailedException(outcome) : outcome;
    }

    private async Task<InventorySummary> RunOneAsync(ILedger ledger, OsduHttpClient client, string operation, InventorySpec inventory, string partition, Guid runId, string actor, CancellationToken ct)
    {
        var flowId = _flow.LedgerId;
        var state = await ledger.RegisterInventoryAsync(
            flowId, _flow.Name, inventory.Name, inventory.Kind, inventory.Query, ReadName(_flow.Source.Read), inventory.Versions == InventoryVersions.All ? "all" : "latest", ct).ConfigureAwait(false);
        var started = Now;
        var runRow = await ledger.StartInventoryRunAsync(flowId, state.InventoryId, operation, runId, actor, ReadName(_flow.Source.Read), started, ct).ConfigureAwait(false);
        var stats = new InventoryReadStats();
        var merge = new InventoryMerge(0, 0, 0, 0, 0);
        long versionsRead = 0;
        try
        {
            if (operation == InventoryRunStatus.Build)
            {
                merge = await ReadAndMergeAsync(ledger, client, inventory, state, runRow, partition, stats, ct).ConfigureAwait(false);
                if (inventory.Versions == InventoryVersions.All)
                {
                    versionsRead = await ReadVersionsAsync(ledger, client, state, stats, ct).ConfigureAwait(false);
                }
            }
            else if (state.LastBuildRunId is null)
            {
                throw new DeliveryException($"Inventory '{inventory.Name}' has never been built in '{partition}', so there is nothing to reconcile yet; build it first.");
            }

            var (owners, ownersSource, missingChecked) = await ReconcileAsync(ledger, client, inventory, state, partition, stats, ct).ConfigureAwait(false);
            var counts = await ledger.InventoryCountsAsync(flowId, state.InventoryId, ct).ConfigureAwait(false);
            var ownersJson = JsonSerializer.Serialize(new { source = ownersSource, owners }, Json);
            await ledger.CompleteInventoryRunAsync(
                flowId, runRow,
                new InventoryRunState
                {
                    InventoryRunId = runRow,
                    InventoryId = state.InventoryId,
                    Operation = operation,
                    Actor = actor,
                    Status = InventoryRunStatus.Completed,
                    StartedUtc = started,
                    ReadMode = ReadName(_flow.Source.Read),
                    Listed = merge.Listed,
                    Pages = stats.Pages,
                    Requests = stats.Requests,
                    Added = merge.Added,
                    Changed = merge.Changed,
                    Gone = merge.Gone,
                    Returned = merge.Returned,
                    MissingChecked = missingChecked,
                    FindingsJson = JsonSerializer.Serialize(counts.ByFinding),
                    OwnersJson = ownersJson,
                },
                ownersSource, Now, ct).ConfigureAwait(false);
            var summary = new InventorySummary(
                inventory.Name, inventory.Kind, InventoryRunStatus.Completed, runRow, merge.Listed, merge.Added, merge.Changed, merge.Gone, merge.Returned,
                versionsRead, missingChecked, counts.ByFinding, owners, ownersSource, stats.Pages, stats.Requests, null);
            _log.LogInformation("{Summary}", summary.Describe());
            return summary;
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            var error = HeaderRedaction.RedactMessage(ex.Message);
            _log.LogError(ex is DeliveryException or HttpRequestException ? null : ex, "inventory '{Inventory}' failed: {Error}", inventory.Name, error);
            await ledger.CompleteInventoryRunAsync(
                flowId, runRow,
                new InventoryRunState
                {
                    InventoryRunId = runRow,
                    InventoryId = state.InventoryId,
                    Operation = operation,
                    Actor = actor,
                    Status = InventoryRunStatus.Failed,
                    StartedUtc = started,
                    ReadMode = ReadName(_flow.Source.Read),
                    Listed = merge.Listed,
                    Pages = stats.Pages,
                    Requests = stats.Requests,
                    Added = merge.Added,
                    Changed = merge.Changed,
                    Gone = merge.Gone,
                    Returned = merge.Returned,
                    Error = error,
                },
                null, Now, CancellationToken.None).ConfigureAwait(false);
            return new InventorySummary(
                inventory.Name, inventory.Kind, InventoryRunStatus.Failed, runRow, merge.Listed, merge.Added, merge.Changed, merge.Gone, merge.Returned,
                versionsRead, 0, new Dictionary<string, long>(StringComparer.Ordinal), [], null, stats.Pages, stats.Requests, error);
        }
    }

    /// <summary>Reads the inventory whole into its stage, a chunk at a time, then merges it; a read that fails part way merges nothing.</summary>
    private async Task<InventoryMerge> ReadAndMergeAsync(
        ILedger ledger, OsduHttpClient client, InventorySpec inventory, InventoryState state, long runRow, string partition, InventoryReadStats stats, CancellationToken ct)
    {
        IInventoryReader reader = _flow.Source.Read == InventoryRead.Storage
            ? new StorageInventoryReader(client, _flow.Source, _context.Time)
            : new SearchInventoryReader(new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging));
        var buffer = new List<InventoryScanRow>(StageChunk);
        long listed = 0;
        await foreach (var page in reader.ReadAsync(inventory, Query(inventory, partition), stats, ct).ConfigureAwait(false))
        {
            buffer.AddRange(page);
            listed += page.Count;
            if (buffer.Count >= StageChunk)
            {
                await ledger.AppendInventoryScanAsync(_flow.LedgerId, runRow, buffer, ct).ConfigureAwait(false);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await ledger.AppendInventoryScanAsync(_flow.LedgerId, runRow, buffer, ct).ConfigureAwait(false);
        }

        _log.LogInformation("inventory '{Inventory}': {Listed} id(s) read in {Pages} page(s), {Requests} request(s); merging", inventory.Name, listed, stats.Pages, stats.Requests);
        return await ledger.MergeInventoryAsync(_flow.LedgerId, state.InventoryId, runRow, Now, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the versions of every record that is new or whose latest version moved since they were read, until none is left.</summary>
    private async Task<long> ReadVersionsAsync(ILedger ledger, OsduHttpClient client, InventoryState state, InventoryReadStats stats, CancellationToken ct)
    {
        var versions = new StorageVersions(client, _flow.Source);
        long read = 0;
        while (true)
        {
            var due = await ledger.InventoryVersionsDueAsync(_flow.LedgerId, state.InventoryId, VersionsChunk, ct).ConfigureAwait(false);
            if (due.Count == 0)
            {
                return read;
            }

            var reads = new InventoryVersionsRead[due.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, due.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Parallelism.RecordsAtOnce), CancellationToken = ct },
                async (i, token) =>
                {
                    // A record storage no longer holds keeps no versions, read at the version the build saw, so it is not read again.
                    var listed = await versions.ReadAsync(due[i].TargetId, stats, token).ConfigureAwait(false);
                    reads[i] = new InventoryVersionsRead(due[i].InventoryRecordId, due[i].Version, listed ?? []);
                }).ConfigureAwait(false);
            await ledger.WriteInventoryVersionsAsync(_flow.LedgerId, reads, ct).ConfigureAwait(false);
            read += reads.Length;
        }
    }

    /// <summary>
    /// Compares the inventory with every ledger of the partition: the owners (declared, or inferred from the ids a ledger
    /// claims), the finding of every id OSDU serves, and the ids a ledger expects that the read did not list, read from storage
    /// within the flow's bound.
    /// </summary>
    private async Task<(IReadOnlyList<InventoryOwner> Owners, string OwnersSource, long Checked)> ReconcileAsync(
        ILedger ledger, OsduHttpClient client, InventorySpec inventory, InventoryState state, string partition, InventoryReadStats stats, CancellationToken ct)
    {
        var flowId = _flow.LedgerId;
        IReadOnlyList<InventoryOwner> owners;
        string ownersSource;
        if (_flow.Owners.Count > 0)
        {
            var created = await ledger.InventoryOwnersAsync(flowId, state.InventoryId, ct).ConfigureAwait(false);
            owners = _flow.Owners
                .Select(o => new InventoryOwner(o, created.FirstOrDefault(c => string.Equals(c.Identity, o, StringComparison.OrdinalIgnoreCase))?.Records ?? 0))
                .ToList();
            ownersSource = "declared";
        }
        else
        {
            var created = await ledger.InventoryOwnersAsync(flowId, state.InventoryId, ct).ConfigureAwait(false);
            var claimed = created.Sum(c => c.Records);
            var floor = Math.Max(1, (long)Math.Ceiling(claimed * InferredOwnerShare));
            owners = created.Where(c => c.Records >= floor).ToList();
            ownersSource = owners.Count > 0 ? "inferred" : "none";
        }

        await ledger.ReconcileInventoryAsync(flowId, state.InventoryId, owners.Select(o => o.Identity).ToList(), Now, ct).ConfigureAwait(false);

        var prefix = inventory.CoversEntityType ? $"{partition}:{inventory.EntityType}:" : null;
        var candidates = await ledger.InventoryCandidatesAsync(flowId, state.InventoryId, prefix, _flow.MaxMissingChecks, ct).ConfigureAwait(false);
        var checks = new List<InventoryCheck>(candidates.Count);
        if (candidates.Count > 0)
        {
            var headers = await new StorageHeaders(client, _flow.Source).ReadAsync(candidates.Select(c => c.TargetId).ToList(), stats, ct).ConfigureAwait(false);
            checks.AddRange(candidates.Select(c => headers.TryGetValue(c.TargetId, out var held) ? new InventoryCheck(c, true, held) : new InventoryCheck(c, false, null)));
            if (candidates.Count >= _flow.MaxMissingChecks)
            {
                _log.LogWarning(
                    "inventory '{Inventory}': {Count} id(s) a ledger expects were read from storage, the flow's maxMissingChecks; more may be missing, and the next builds read the rest, the least recently read first",
                    inventory.Name, candidates.Count);
            }
        }

        await ledger.RecordInventoryChecksAsync(flowId, state.InventoryId, checks, Now, ct).ConfigureAwait(false);
        return (owners, ownersSource, checks.Count);
    }

    /// <summary>The inventory's query with its tokens filled: the flow's parameters, and <c>{partition}</c> the partition the run reads.</summary>
    private string? Query(InventorySpec inventory, string partition)
        => inventory.Query is { } query
            ? FlowParameters.Substitute(query, new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition })
            : null;

    /// <summary>
    /// The partition the run reads, by its data-partition-id: the one the flow is bound to, or for a flow whose partition is its
    /// header's, the header's resolved on this node.
    /// </summary>
    private async Task<string> PartitionAsync(CancellationToken ct)
    {
        if (_flow.Partition is { } bound)
        {
            return bound;
        }

        var declared = _flow.Source.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new DeliveryException(
                $"Inventory flow '{_flow.Name}' is bound to no partition and names no '{CacheScope.PartitionHeader}' in its source.headers, so the partition it reads is unknown.");
        }

        return CacheScope.Normalize(await _context.Secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{_flow.Name}: source.headers");
    }

    private static string ReadName(InventoryRead read) => read == InventoryRead.Storage ? "storage" : "search";

    /// <summary>What a failure of one inventory is: anything but the run itself being cancelled, which stops every inventory.</summary>
    private static bool Expected(Exception ex, CancellationToken ct)
        => (ex is not OperationCanceledException || !ct.IsCancellationRequested)
           && ex is SqlFlowException or HttpRequestException or IOException or TimeoutException or JsonException or InvalidOperationException
               or Microsoft.Data.SqlClient.SqlException or OperationCanceledException;
}
