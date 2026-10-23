using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Removals;

/// <summary>What one removal of a run came to, as the run's result and the flow's page show it.</summary>
public sealed record RemovalEntryOutcome
{
    public required string Entry { get; init; }

    /// <summary>The delivery flow (and interface) whose ledger it compared and wrote to.</summary>
    public required string DeliveryFlow { get; init; }

    /// <summary>The removal run's number in the partition; null when the removal stopped before it opened one.</summary>
    public long? RemovalRunId { get; init; }

    /// <summary>completed, guarded or failed.</summary>
    public required string Status { get; init; }

    public required RemovalTally Tally { get; init; }

    /// <summary>The ledger's deletions still pending once the run was done: what the next run takes.</summary>
    public long Pending { get; init; }

    /// <summary>The ledger's deletions this removal found that wait for an operator's approval.</summary>
    public long AwaitingApproval { get; init; }

    /// <summary>For a plan: whether the guard would hold what the removal found.</summary>
    public bool WouldHold { get; init; }

    /// <summary>What the undo did with what unfinished deliveries of the records left.</summary>
    public UndoSummary? Undone { get; init; }

    /// <summary>What the pipeline's cache after-step did with what the removal took out.</summary>
    public CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>Why the removal stopped, or what its guard held; null for one that completed.</summary>
    public string? Error { get; init; }

    public string Describe()
    {
        var t = Tally;
        var found = string.Create(CultureInfo.InvariantCulture,
            $"{Entry} ({DeliveryFlow}): {t.SourceKeys} source key(s), {t.ListedKeys} listed removed, {t.Compared} compared, {t.Detected} found removed");
        var done = string.Create(CultureInfo.InvariantCulture,
            $"{t.Removed} removed from OSDU, {t.Settled} settled, {t.KeptOut} kept out, {t.AlreadyGone} already gone, {t.Referenced + t.Shared + t.Busy + t.Failed} left for the next run, {t.Lifted} lifted, {t.CameBack} came back");
        return Status switch
        {
            SourceRemovals.Failure => $"{found}; failed: {Error}",
            SourceRemovals.Guarded => $"{found}; {done}; {AwaitingApproval} wait for approval: {Error}",
            _ => $"{found}; {done}",
        };
    }
}

/// <summary>What a removal flow's run came to: each removal's outcome, in document order.</summary>
public sealed record RemovalRunOutcome
{
    /// <summary>remove or plan.</summary>
    public required string Operation { get; init; }

    public required string Partition { get; init; }

    public required IReadOnlyList<RemovalEntryOutcome> Removals { get; init; }

    /// <summary>The records the run took out of OSDU, across its removals.</summary>
    public long Removed => Removals.Sum(r => r.Tally.Removed);

    public string Describe() => string.Join(Environment.NewLine, Removals.Select(r => r.Describe()));
}

/// <summary>A removal run in which a removal failed, or held what it found for approval: the run ends failed, with its outcome.</summary>
public sealed class RemovalRunFailedException : DeliveryException
{
    public RemovalRunFailedException(RemovalRunOutcome outcome)
        : base(Summary(outcome))
    {
        Outcome = outcome;
    }

    public RemovalRunOutcome Outcome { get; }

    private static string Summary(RemovalRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var failed = outcome.Removals.Where(r => r.Status == SourceRemovals.Failure).Select(r => r.Entry).ToList();
        var held = outcome.Removals.Where(r => r.Status == SourceRemovals.Guarded).Select(r => r.Entry).ToList();
        var parts = new List<string>();
        if (failed.Count > 0)
        {
            parts.Add($"removal {string.Join(", ", failed)} failed");
        }

        if (held.Count > 0)
        {
            parts.Add($"removal {string.Join(", ", held)} found more removed keys than its guard allows, which wait for an operator's approval on the removal flow's page");
        }

        return string.Join("; ", parts) + ". " + outcome.Describe();
    }
}

/// <summary>
/// Runs a removal flow's removals in one partition (osdu/docs/reference/flow/removal.md, A run): for each, the delivery flow it
/// names is found and opened for its target; the source's current keys are read whole (a full load, every run), its removed keys
/// with them, and the delivery flow's record table in the removal's scope; each is converted and derived to the delivery key the
/// ledger keys the record by, and staged; the staged keys are compared with the ledger's records and its deletions, which gain
/// what the source removed; and then the ledger's pending deletions are taken out of OSDU through the delivery flow's own
/// removal path, a deletion already taken out never asked of OSDU again. A plan does all but write and take out.
/// </summary>
public sealed class RemovalRunner
{
    /// <summary>How many keys of the record table one statement reads.</summary>
    private const int IngestionPage = 5000;

    private readonly EngineContext _context;
    private readonly RemovalKeySources _sources;
    private readonly RemovalFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly Guid _runId;
    private readonly string _actor;
    private readonly ILogger _log;

    public RemovalRunner(
        EngineContext context, RemovalKeySources sources, RemovalFlowDefinition flow, IReadOnlyDictionary<string, string> values, Guid runId, string actor, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentNullException.ThrowIfNull(log);
        if (flow.Partition is null)
        {
            throw new ArgumentException("A removal flow is bound to the partition a run removes in before it runs.", nameof(flow));
        }

        _context = context;
        _sources = sources;
        _flow = flow;
        _values = values;
        _runId = runId;
        _actor = actor;
        _log = log;
    }

    private string Partition => _flow.Partition!;

    /// <summary>
    /// Runs <paramref name="operation"/> (remove or plan) over the removals <paramref name="names"/> names (every one when none).
    /// A removal that fails is recorded so and the next one runs; a run in which one failed, or (a remove run) one's guard held
    /// what it found, ends failed with the whole outcome.
    /// </summary>
    /// <exception cref="RemovalRunFailedException">A removal failed, or held what it found for approval.</exception>
    public async Task<RemovalRunOutcome> RunAsync(string operation, IReadOnlyCollection<string> names, RemovalApproval? approval, string? confirm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (operation is not (DeliveryOperations.Remove or DeliveryOperations.Plan))
        {
            throw new DeliveryException($"A removal flow runs the {DeliveryOperations.Remove} and {DeliveryOperations.Plan} operations, not '{operation}'.");
        }

        if (_context.Ledger is null)
        {
            throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        }

        var entries = _flow.Select(names);
        if (approval is not null)
        {
            if (entries.Count != 1 || names.Count != 1)
            {
                throw new DeliveryException("An approval names the one removal whose held keys it approves (entries).");
            }

            if (!string.Equals(confirm?.Trim(), Partition, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException(
                    $"The approval confirms partition '{confirm?.Trim()}', and removal flow '{_flow.Name}' runs in partition '{Partition}' here, so nothing was approved. Name '{Partition}' to approve.");
            }
        }

        var outcomes = new List<RemovalEntryOutcome>(entries.Count);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            outcomes.Add(await RunEntryAsync(operation, entry, approval, removed, ct).ConfigureAwait(false));
        }

        var outcome = new RemovalRunOutcome { Operation = operation, Partition = Partition, Removals = outcomes };
        if (outcomes.Any(o => o.Status is SourceRemovals.Failure or SourceRemovals.Guarded))
        {
            throw new RemovalRunFailedException(outcome);
        }

        return outcome;
    }

    private async Task<RemovalEntryOutcome> RunEntryAsync(string operation, RemovalEntry entry, RemovalApproval? approval, HashSet<string> removed, CancellationToken ct)
    {
        FlowDefinition delivery;
        try
        {
            (delivery, _) = await RemovalTargets.ResolveAsync(_context, _flow, entry, Partition, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            _log.LogError("removal '{Entry}': {Error}", entry.Name, RunFailure.Describe(ex));
            return Failed(entry, entry.Flow, null, RemovalTally.Empty, RunFailure.Describe(ex));
        }

        var ledger = _context.Ledger!;
        using var runtime = FlowRuntime.ForTarget(_context, delivery);
        runtime.Actor = _actor;
        runtime.RunId = _runId;
        long? run = null;
        var tally = RemovalTally.Empty;
        try
        {
            if (delivery.Target.Protocol is DeliveryProtocol.Dspdm or DeliveryProtocol.Etp)
            {
                throw new DeliveryException(
                    $"{entry.Path}: '{delivery.Label}' is delivered by the {DeliveryProtocols.Name(delivery.Target.Protocol)} route, which keeps nothing it deletes, so its records have no reversible removal, and a removal flow takes records out reversibly only.");
            }

            var deliveryValues = DeliveryValues(entry, delivery);
            var scope = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (column, parameter) in delivery.Source.Record.Scope)
            {
                scope[parameter] = deliveryValues.TryGetValue(parameter, out var value)
                    ? value
                    : throw new DeliveryException(
                        $"{entry.Path}.values gives delivery flow '{delivery.Label}' no value for '{parameter}', which its record scope binds column '{column}' to; a removal compares the records of one scope, so name it.");
            }

            var mapping = runtime.Mappings.Load(delivery.Render.Mapping);
            var system = mapping.Dataset.System;
            var ingestion = _context.Sources.Open(delivery, deliveryValues, _context.Loggers);
            var recordKey = await ingestion.KeyColumnsAsync(ct).ConfigureAwait(false);

            await runtime.EnsureLedgerAsync(ct).ConfigureAwait(false);
            run = await ledger.StartRemovalRunAsync(delivery.Id, new RemovalRunStart
            {
                FlowName = _flow.Name,
                Entry = entry.Name,
                DeliveryFlow = delivery.Label,
                RunId = _runId,
                Operation = operation,
                Actor = _actor,
                StartedUtc = Now,
                GuardPercent = _flow.Guard.MaxPercent,
                GuardRecords = _flow.Guard.MaxRecords,
                ApprovedBy = approval is null ? null : _actor,
            }, ct).ConfigureAwait(false);
            _log.LogInformation("removal '{Entry}' of {Delivery} in {Partition}: run {Run}", entry.Name, delivery.Label, Partition, run);

            if (approval is not null && operation == DeliveryOperations.Remove)
            {
                var approved = await ledger.ApproveRemovalsAsync(delivery.Id, _flow.Name, entry.Name, approval.Expected, _actor, Now, ct).ConfigureAwait(false);
                tally = tally.Add(new RemovalTally { Approved = approved });
                _log.LogInformation("removal '{Entry}': {Approved} removed key(s) waiting for approval approved by {Actor}", entry.Name, approved, _actor);
            }

            DateTime? sourceRead = null;
            if (entry.Keys is { } keys)
            {
                sourceRead = Now;
                await ledger.MarkRemovalSourceReadAsync(delivery.Id, run.Value, sourceRead.Value, ct).ConfigureAwait(false);
                var (rows, staged) = await StageAsync(ledger, delivery, run.Value, keys, SourceRemovals.SourceSide, Resolve(keys, recordKey, delivery), system, ct).ConfigureAwait(false);
                tally = tally.Add(new RemovalTally { RowsRead = rows, SourceKeys = staged });
                _log.LogInformation("removal '{Entry}': the source's keys: {Rows} row(s), {Keys} distinct key(s)", entry.Name, rows, staged);
            }

            if (entry.Removed is { } listed)
            {
                var (rows, staged) = await StageAsync(ledger, delivery, run.Value, listed, SourceRemovals.RemovedSide, Resolve(listed, recordKey, delivery), system, ct).ConfigureAwait(false);
                tally = tally.Add(new RemovalTally { RowsRead = rows, ListedKeys = staged });
                _log.LogInformation("removal '{Entry}': the source's removed keys: {Rows} row(s), {Keys} distinct key(s)", entry.Name, rows, staged);
            }

            var ingestionKeys = await StageIngestionAsync(ledger, delivery, run.Value, ingestion, system, ct).ConfigureAwait(false);
            tally = tally.Add(new RemovalTally { IngestionKeys = ingestionKeys });

            var detection = await ledger.DetectRemovalsAsync(delivery.Id, run.Value, new RemovalComparison
            {
                ReadsSource = entry.Keys is not null,
                ReadsRemoved = entry.Removed is not null,
                SourceReadUtc = sourceRead,
                Scope = scope,
                Write = operation == DeliveryOperations.Remove,
                RemovalFlow = _flow.Name,
                Entry = entry.Name,
                RunId = _runId,
                GuardPercent = _flow.Guard.MaxPercent,
                GuardRecords = _flow.Guard.MaxRecords,
                NowUtc = Now,
            }, ct).ConfigureAwait(false);
            tally = tally.Add(new RemovalTally
            {
                Compared = detection.Compared,
                Detected = detection.Detected,
                AwaitingApproval = detection.Guarded ? detection.Detected : 0,
                CameBack = detection.CameBack,
                Lifted = detection.Lifted,
            });
            _log.LogInformation(
                "removal '{Entry}': {Compared} key(s) compared, {Detected} found removed{Held}, {CameBack} came back, {Lifted} lifted; {Pending} pending",
                entry.Name, detection.Compared, detection.Detected, detection.Guarded ? " (held for approval)" : string.Empty, detection.CameBack, detection.Lifted, detection.Pending);

            var held = detection.Guarded ? Held(detection, tally, entry.Keys is not null) : null;
            if (operation == DeliveryOperations.Plan)
            {
                await CloseAsync(ledger, delivery, run.Value, tally, SourceRemovals.Completed, null, null, ct).ConfigureAwait(false);
                return new RemovalEntryOutcome
                {
                    Entry = entry.Name,
                    DeliveryFlow = delivery.Label,
                    RemovalRunId = run,
                    Status = SourceRemovals.Completed,
                    Tally = tally,
                    Pending = detection.Pending,
                    AwaitingApproval = detection.AwaitingApproval,
                    WouldHold = detection.Guarded,
                    Error = held,
                };
            }

            // The pending deletions are taken out whatever the guard held: they were found within it, or approved, before.
            var summary = await runtime.RemoveForSourceAsync(new SourceRemovalWork
            {
                Note = new SourceRemovalNote
                {
                    RemovalFlow = _flow.Name,
                    Entry = entry.Name,
                    RemovalRunId = run.Value,
                    RunId = _runId,
                    Reason = SourceRemovals.Absent,
                    RemovedUtc = sourceRead ?? Now,
                },
                KeepReferenced = entry.KeepReferenced,
                Parameters = new { removalFlow = _flow.Name, removal = entry.Name, removalRunId = run.Value, approvedBy = approval is null ? null : _actor },
                RemovedBefore = removed,
            }, ct).ConfigureAwait(false);
            removed.UnionWith(summary.RemovedIds);
            tally = tally.Add(summary.Tally);
            var status = detection.Guarded ? SourceRemovals.Guarded : SourceRemovals.Completed;
            await CloseAsync(ledger, delivery, run.Value, tally, status, held, summary.ActivityId, ct).ConfigureAwait(false);
            var after = await ledger.RemovedKeyCountsAsync(delivery.Id, ct).ConfigureAwait(false);
            return new RemovalEntryOutcome
            {
                Entry = entry.Name,
                DeliveryFlow = delivery.Label,
                RemovalRunId = run,
                Status = status,
                Tally = tally,
                Pending = after.GetValueOrDefault(SourceRemovals.Pending),
                AwaitingApproval = after.GetValueOrDefault(SourceRemovals.AwaitingApproval),
                Undone = summary.Undone,
                CacheAfter = summary.CacheAfter,
                Error = held,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (run is { } stopped)
            {
                await CloseAsync(ledger, delivery, stopped, tally, SourceRemovals.Failure, "the run was cancelled; what it recorded stands, and the next run takes the rest", null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            var error = RunFailure.Describe(ex);
            _log.LogError(RunFailure.IsExpected(ex) ? null : ex, "removal '{Entry}' failed: {Error}", entry.Name, error);
            if (run is { } failed)
            {
                await CloseAsync(ledger, delivery, failed, tally, SourceRemovals.Failure, error, null, CancellationToken.None).ConfigureAwait(false);
            }

            return Failed(entry, delivery.Label, run, tally, error);
        }
    }

    /// <summary>
    /// The values of the delivery flow's parameters the removal compares under: its <c>values</c>, each <c>{name}</c> token filled
    /// from this flow's values and <c>{partition}</c> from the run's partition, then the delivery flow's defaults.
    /// </summary>
    private IReadOnlyDictionary<string, string> DeliveryValues(RemovalEntry entry, FlowDefinition delivery)
    {
        var tokens = new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = Partition };
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in entry.Values)
        {
            if (!delivery.Parameters.ContainsKey(name))
            {
                throw new DeliveryException(
                    $"{entry.Path}.values.{name} names no parameter of delivery flow '{delivery.Label}'; it declares {(delivery.Parameters.Count == 0 ? "none" : string.Join(", ", delivery.Parameters.Keys))}.");
            }

            supplied[name] = FlowParameters.Substitute(value, tokens);
        }

        try
        {
            return FlowParameters.Resolve(delivery, supplied);
        }
        catch (FlowValidationException ex)
        {
            throw new DeliveryException($"{entry.Path}.values: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The key columns a source is read through, in the record key's order: the columns the source names, each filling one record
    /// key column, or each record key column read from a column of the same name.
    /// </summary>
    private static IReadOnlyList<ResolvedKeyColumn> Resolve(RemovalKeySource source, IReadOnlyList<SourceKeyColumn> recordKey, FlowDefinition delivery)
    {
        if (source.Columns.Count == 0)
        {
            return recordKey.Select(k => new ResolvedKeyColumn(k.Name, k.Name, k.SqlType, null)).ToList();
        }

        var keyNames = string.Join(", ", recordKey.Select(k => k.Name));
        var byOutput = new Dictionary<string, RemovalKeyColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in source.Columns)
        {
            if (!recordKey.Any(k => string.Equals(k.Name, column.Output, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DeliveryException(
                    $"{source.Path}.columns fills '{column.Output}', which is not a key column of delivery flow '{delivery.Label}': its record key is {keyNames}.");
            }

            byOutput[column.Output] = column;
        }

        var missing = recordKey.Where(k => !byOutput.ContainsKey(k.Name)).Select(k => k.Name).ToList();
        if (missing.Count > 0)
        {
            throw new DeliveryException(
                $"{source.Path}.columns reads nothing for record key column{(missing.Count == 1 ? string.Empty : "s")} {string.Join(", ", missing)} of delivery flow '{delivery.Label}': a key is read whole ({keyNames}).");
        }

        return recordKey.Select(k =>
        {
            var column = byOutput[k.Name];
            return new ResolvedKeyColumn(column.Name, k.Name, k.SqlType, column.Expr);
        }).ToList();
    }

    /// <summary>
    /// Reads a key source whole, converts and derives each row's key a batch at a time, and stages them. A row whose key is blank,
    /// or does not convert to the record table's types, or (a removed key) whose moment does not, stops the removal before it
    /// compares anything: a key that cannot be read would make its record look removed, or hide a removal.
    /// </summary>
    private async Task<(long Rows, long Keys)> StageAsync(
        ILedger ledger, FlowDefinition delivery, long run, RemovalKeySource source, byte side, IReadOnlyList<ResolvedKeyColumn> columns, string system, CancellationToken ct)
    {
        var removedSide = side == SourceRemovals.RemovedSide;
        var conversion = new RemovalKeyConversion
        {
            Columns = columns.Select(c => new RemovalKeyTarget(c.Target, c.SqlType, c.Expression)).ToList(),
            Moment = removedSide ? new RemovalMomentTarget(source.RemovedAt?.Expr) : null,
        };
        var tokens = new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = Partition };
        long rows = 0, keys = 0;
        var batch = new List<RawRemovalKey>(SourceRemovals.StageBatch);
        await foreach (var raw in _sources.ReadAsync(source, columns, removedSide, _flow.SourcePath, tokens, _flow.Name, ct).ConfigureAwait(false))
        {
            batch.Add(raw);
            rows++;
            if (batch.Count == SourceRemovals.StageBatch)
            {
                keys += await FlushAsync(ledger, delivery, run, source, side, columns, conversion, system, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            keys += await FlushAsync(ledger, delivery, run, source, side, columns, conversion, system, batch, ct).ConfigureAwait(false);
        }

        return (rows, keys);
    }

    private static async Task<long> FlushAsync(
        ILedger ledger, FlowDefinition delivery, long run, RemovalKeySource source, byte side, IReadOnlyList<ResolvedKeyColumn> columns, RemovalKeyConversion conversion,
        string system, IReadOnlyList<RawRemovalKey> batch, CancellationToken ct)
    {
        var converted = await ledger.ConvertRemovalKeysAsync(conversion, batch, ct).ConfigureAwait(false);
        var staged = new List<StagedRemovalKey>(batch.Count);
        var problems = new List<string>();
        var bad = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var raw = batch[i];
            var values = new string[columns.Count];
            string? problem = null;
            for (var j = 0; j < columns.Count && problem is null; j++)
            {
                var typed = converted[i].Values[j];
                var text = typed is null ? null : SourceRow.Stringify(SourceValues.Normalize(typed));
                if (string.IsNullOrWhiteSpace(raw.Values[j]))
                {
                    problem = $"key column '{columns[j].Target}' (read from '{columns[j].Source}') is blank";
                }
                else if (string.IsNullOrWhiteSpace(text))
                {
                    problem = $"key column '{columns[j].Target}' holds '{Shown(raw.Values[j]!)}', which does not convert to {columns[j].SqlType}";
                }
                else
                {
                    values[j] = text;
                }
            }

            if (problem is null && side == SourceRemovals.RemovedSide && converted[i].Moment is null)
            {
                problem = raw.Moment is null
                    ? $"its removal moment ({source.RemovedAt?.Name ?? RemovalKeySources.FileDateColumn}) is blank"
                    : $"its removal moment '{Shown(raw.Moment)}' does not convert to a moment";
            }

            if (problem is not null)
            {
                bad++;
                if (problems.Count < SourceRemovals.MaxNamedRows)
                {
                    problems.Add($"{Where(raw)}: {problem}");
                }

                continue;
            }

            staged.Add(new StagedRemovalKey
            {
                Key = DeliveryKey.Derive(system, values),
                SourceKeyJson = new KeyTuple(values).Json,
                RemovedUtc = converted[i].Moment,
                FileName = raw.FileName,
                RowNumber = raw.RowNumber,
            });
        }

        if (bad > 0)
        {
            throw new DeliveryException(
                $"{source.Path}: {bad} row(s) of a batch hold a key that cannot be read, so nothing was compared or removed (a key that cannot be read would make its record look removed, or hide a removal). "
                + $"Correct the source, or convert the value with an expr: {string.Join("; ", problems)}{(bad > problems.Count ? "; ..." : string.Empty)}");
        }

        return await ledger.StageRemovalKeysAsync(delivery.Id, run, side, staged, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages the delivery flow's record table keys in the removal's scope, derived as the plan derives a row's delivery key: what
    /// the comparison covers besides the ledger's records, so a row of a removed key that no run delivered yet is kept out too.
    /// </summary>
    private static async Task<long> StageIngestionAsync(ILedger ledger, FlowDefinition delivery, long run, IIngestionSource ingestion, string system, CancellationToken ct)
    {
        long keys = 0;
        var batch = new List<StagedRemovalKey>(SourceRemovals.StageBatch);
        await foreach (var row in ingestion.ReadKeysAsync(IngestionPage, ct).ConfigureAwait(false))
        {
            // A row with a blank key is never delivered: the plan holds it, so there is nothing of it to compare.
            if (row.Key.Values.Any(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            batch.Add(new StagedRemovalKey { Key = DeliveryKey.Derive(system, row.Key.Values), SourceKeyJson = row.Key.Json, UpdatedUtc = row.UpdatedUtc });
            if (batch.Count == SourceRemovals.StageBatch)
            {
                keys += await ledger.StageRemovalKeysAsync(delivery.Id, run, SourceRemovals.IngestionSide, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            keys += await ledger.StageRemovalKeysAsync(delivery.Id, run, SourceRemovals.IngestionSide, batch, ct).ConfigureAwait(false);
        }

        return keys;
    }

    private async Task CloseAsync(ILedger ledger, FlowDefinition delivery, long run, RemovalTally tally, string status, string? why, long? activity, CancellationToken ct)
    {
        try
        {
            await ledger.AddRemovalTallyAsync(delivery.Id, run, tally, ct).ConfigureAwait(false);
            await ledger.CompleteRemovalRunAsync(delivery.Id, run, status, why, activity, Now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            // The run's row could not be closed: the next run of the removal closes it as one its process left open.
            _log.LogWarning("removal run {Run} could not be closed in the ledger: {Error}", run, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>What a guard that held a removal's finds says: how many, and why they wait.</summary>
    private string Held(RemovalDetection detection, RemovalTally tally, bool readsSource)
    {
        var bound = _flow.Guard.MaxRecords is { } most
            ? string.Create(CultureInfo.InvariantCulture, $"at most {most} key(s) and {_flow.Guard.MaxPercent}% of the {detection.Compared} compared")
            : string.Create(CultureInfo.InvariantCulture, $"at most {_flow.Guard.MaxPercent}% of the {detection.Compared} compared");
        var why = readsSource && tally.SourceKeys == 0 && detection.Detected > 0
            ? "the source's keys read none, which is more often a broken read than a source that removed everything"
            : $"the guard allows {bound}";
        return string.Create(CultureInfo.InvariantCulture,
            $"{detection.Detected} key(s) found removed wait for an operator's approval, since {why}; nothing of them was taken out or kept out. Approve them on the removal flow's page, or correct the source.");
    }

    private static RemovalEntryOutcome Failed(RemovalEntry entry, string delivery, long? run, RemovalTally tally, string error) => new()
    {
        Entry = entry.Name,
        DeliveryFlow = delivery,
        RemovalRunId = run,
        Status = SourceRemovals.Failure,
        Tally = tally,
        Error = error,
    };

    private static string Where(RawRemovalKey raw)
        => raw.FileName is null
            ? raw.RowNumber is { } row ? string.Create(CultureInfo.InvariantCulture, $"row {row}") : "a row"
            : raw.RowNumber is { } line ? string.Create(CultureInfo.InvariantCulture, $"{raw.FileName}, row {line}") : raw.FileName;

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;

    private DateTime Now => _context.Time.GetUtcNow().UtcDateTime;

    /// <summary>A failure of one removal that ends it and lets the next run; a cancellation the run asked for stops the run.</summary>
    private static bool Expected(Exception ex, CancellationToken ct) => !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
