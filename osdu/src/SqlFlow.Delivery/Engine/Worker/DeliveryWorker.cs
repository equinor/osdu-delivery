using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Diagnostics;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>
/// What a drain did. <c>Unchanged</c> counts the records the final hash check found OSDU already holding, settled
/// without sending anything. <c>Waiting</c> counts the records its claims left waiting for a record they refer to; they
/// were not tried, so they are not among the processed.
/// </summary>
public sealed record WorkerSummary(long Processed, long Delivered, long Retried, long Held, long Failed, int Batches = 0, long Unchanged = 0, long Waiting = 0)
{
    public static WorkerSummary Empty { get; } = new(0, 0, 0, 0, 0);

    public WorkerSummary Add(WorkerSummary other) => new(
        Processed + other.Processed, Delivered + other.Delivered, Retried + other.Retried, Held + other.Held, Failed + other.Failed, Batches + other.Batches,
        Unchanged + other.Unchanged, Waiting + other.Waiting);

    /// <summary>Whether the drain changed nothing: it settled no record and left none waiting for a record it refers to.</summary>
    public bool Idle => Processed == 0 && Waiting == 0;

    /// <summary>The counts that are not zero, as the audit trail shows a drain run: "3 delivered, 1 failed".</summary>
    public string Headline => CountLine.Of(
        "nothing due", (Delivered, "delivered"), (Unchanged, "unchanged"), (Retried, "retrying"), (Held, "held"), (Failed, "failed"), (Waiting, "waiting"));

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Processed} processed in {Batches} batch(es): {Delivered} delivered, {Unchanged} already held (nothing sent), {Retried} retrying later, {Held} held, {Failed} failed, {Waiting} waiting for a record they refer to");
}

/// <summary>
/// The lease-and-retry worker (osdu/docs/reference/concepts/submissions.md, Leases and claims): the fan-out. Claims
/// whole work batches (a file of rendered documents and every record of it that is due) and, when no batch is claimable,
/// a group of records that came due for a retry, each under one lease; processes them with bounded concurrency, in
/// protocol batches when the protocol accepts arrays. While it delivers it writes nothing to the records: it renews its
/// lease's one row and appends what it learns (every step the target answered, before the delivery goes on, and each
/// try's attempt and outcome) through a <see cref="LeaseJournal"/>, which writes what its concurrent deliveries hand
/// over together. At every renewal the
/// lease applies what was appended so far and the worker reports the batch's progress to the listeners; closing the
/// lease applies the rest and settles the batch. The completion callback (<see cref="IDeliveryListener"/>) fires after
/// every outcome is written. Any number of workers, on any number of nodes, share the ledger safely: a crashed worker's
/// lease runs out and the next claim of its flow recovers it, a stopping worker closes its lease at once, and a worker
/// whose lease was taken over stops sending under it. The run trace carries batch lines, the progress of each lease, the
/// first problems of each batch, and, for the records the run's <see cref="RunTrace"/> describes, a line per record, step
/// and call; never a line per record for the rest.
/// </summary>
public sealed class DeliveryWorker
{
    /// <summary>The longest one wait for records in backoff lasts before the queue is looked at again.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(5);

    private const int FailuresLoggedPerBatch = 10;

    /// <summary>How often a lease asks the run's trace whether it is time for a progress line.</summary>
    private static readonly TimeSpan ProgressCheck = TimeSpan.FromSeconds(5);

    private readonly ILedger _ledger;
    private readonly IPayloadFiles _payloads;
    private readonly FileStoreRegistry _stores;
    private readonly IDeliveryProtocol _protocol;
    private readonly FlowDefinition _flow;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly string _workerId;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Lazy<Task<SubmissionState>>> _submissions = new();

    /// <summary>What the gate found during the current drain, said once when the drain ends.</summary>
    private ValidationTally _drained = new();

    /// <summary>The platform run the worker drains for, stamped on every attempt it writes.</summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// How long a drain waits, at most in one go, for records in backoff before looking at the queue again; null
    /// returns as soon as nothing is due (a single pass of what is claimable now).
    /// </summary>
    public TimeSpan? MaxWait { get; init; } = DefaultMaxWait;

    /// <summary>
    /// What judges the outcomes of this worker's records for the interface as a whole
    /// (osdu/docs/reference/flow/interfaces.md, When an interface stops), or null when nothing does. The worker reports
    /// every settled or retried record to it; stopping the work when it trips is up to whoever cancels the token the worker
    /// was handed.
    /// </summary>
    public FailureGuard? Guard { get; init; }

    /// <summary>How often a lease is renewed and checkpointed while the worker delivers: half the lease unless set (tests shorten it).</summary>
    internal TimeSpan? KeepInterval { get; init; }

    /// <summary>
    /// The trace of the run the worker delivers for, or null outside a run. The records it describes get a line when they
    /// are sent, one for each step the target answered, and one for how they ended, and their calls to OSDU are named; each
    /// lease says how far it has got every <see cref="RunTrace.ProgressInterval"/>. Without a trace the worker writes its
    /// batch lines and the first problems of each batch, and nothing per record.
    /// </summary>
    public RunTrace? Trace { get; init; }

    /// <summary>
    /// Which records the flow's records wait for (osdu/docs/reference/flow/interfaces.md, Records that wait for records):
    /// every record of the ledger still to land, unless the source's order says a reference points back.
    /// </summary>
    public WaitRules Waits { get; init; } = WaitRules.WaitForAll;

    /// <summary>
    /// The work batches this worker claims and waits for, or null for every batch of what it drains: a part of a split
    /// delivery works on the batches it planned its own slices into, whose files are on its node, and on no other part's
    /// (osdu/docs/reference/concepts/submissions.md, Many nodes).
    /// </summary>
    public BatchRange? Batches { get; init; }

    /// <summary>
    /// Where this worker reads the documents of the batches it claims, or null for the work location the submission
    /// records: a part reads its batches back from the folder it wrote them to on its own node.
    /// </summary>
    public string? WorkRoot { get; init; }

    /// <summary>
    /// The gate every document goes through immediately before it is sent (<see cref="ValidationGate"/>): its verdict, the
    /// records it holds under the flow's <c>target.validation</c>, and under <c>target.verifyReferences: storage</c> the
    /// records whose references storage does not hold. Null sends every record unchecked, which only a worker built for a
    /// test does; a flow's runtime always gives one.
    /// </summary>
    public ValidationGate? Gate { get; init; }

    /// <summary>
    /// The data-partition-id the worker's ledger is kept under, which tags what it counts: the partition the ledger was
    /// registered in, or the flow's own when it is bound to one and nothing else was said.
    /// </summary>
    public string? Partition
    {
        get => _partition ?? _flow.Partition;
        init => _partition = value;
    }

    private readonly string? _partition;

    public DeliveryWorker(
        ILedger ledger,
        IPayloadFiles payloads,
        FileStoreRegistry stores,
        IDeliveryProtocol protocol,
        FlowDefinition flow,
        TimeProvider time,
        IDeliveryListener listener,
        ILogger<DeliveryWorker> logger,
        string? workerId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _payloads = payloads;
        _stores = stores;
        _protocol = protocol;
        _flow = flow;
        _time = time;
        _listener = listener;
        _logger = logger;
        _workerId = workerId ?? $"{Environment.MachineName}/{Environment.ProcessId}";
    }

    public string WorkerId => _workerId;

    private TimeSpan Lease => TimeSpan.FromSeconds(Math.Max(_flow.Reliability.LeaseSeconds, 30));

    /// <summary>
    /// Processes until nothing of the submission (or flow) is pending for this worker: batches first, then records
    /// that came due for a retry, waiting for records in backoff when that is all that is left. Records another
    /// worker holds are not waited for.
    /// </summary>
    public async Task<WorkerSummary> DrainAsync(Guid? submissionId, CancellationToken ct = default)
    {
        _drained = new ValidationTally();
        try
        {
            return await DrainAllAsync(submissionId, ct).ConfigureAwait(false);
        }
        finally
        {
            // What the gate found is said once, whatever ended the drain: a line per template, never a line per record.
            foreach (var line in _drained.Lines())
            {
                _logger.LogInformation(RunTrace.Bounded, "{Validated}", line);
            }
        }
    }

    private async Task<WorkerSummary> DrainAllAsync(Guid? submissionId, CancellationToken ct)
    {
        var total = WorkerSummary.Empty;
        while (!ct.IsCancellationRequested)
        {
            var summary = await PassAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(summary);
            if (summary.Processed > 0 || summary.Batches > 0 || summary.Waiting > 0)
            {
                continue;
            }

            if (MaxWait is not { } maxWait)
            {
                break;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var nextDue = await _ledger.NextDueAsync(_flow.Id, submissionId, now, Batches, ct).ConfigureAwait(false);
            if (nextDue is null)
            {
                break;
            }

            var wait = nextDue.Value - now;
            if (wait > maxWait)
            {
                wait = maxWait;
            }

            if (wait < TimeSpan.FromSeconds(1))
            {
                wait = TimeSpan.FromSeconds(1);
            }

            // Waits come as often as records come due, so a run says so at the pace of its progress lines.
            if (Trace is not { } trace || trace.ProgressDue("backoff", _time.GetUtcNow()))
            {
                _logger.LogInformation(RunTrace.Bounded, "Nothing is due; waiting {Seconds}s for records in backoff (next at {Next:u}).", (int)wait.TotalSeconds, nextDue.Value);
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>One claim: a work batch when one is claimable, else one group of due records.</summary>
    public async Task<WorkerSummary> PassAsync(Guid? submissionId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var claimed = await _ledger.ClaimWorkBatchAsync(_flow.Id, submissionId, _workerId, Lease, now, RunId, Waits, _flow.Reliability.Retry.Attempts, Batches, ct).ConfigureAwait(false);
        if (claimed is not null)
        {
            _logger.LogInformation(
                "Claimed batch {Batch} of submission {SubmissionId}: {Records} record(s) due, {Waiting} left waiting.",
                claimed.Batch.Index, claimed.Batch.SubmissionId, claimed.Records.Count, claimed.Waiting.Count);
            var waited = await ReportWaitingAsync(claimed.Waiting, claimed.Batch.SubmissionId, ct).ConfigureAwait(false);
            var summary = await ProcessLeaseAsync(claimed.Lease, claimed.Batch, claimed.Records, ct).ConfigureAwait(false);
            return summary with { Waiting = summary.Waiting + waited };
        }

        var due = await _ledger.ClaimAsync(_flow.Id, submissionId, _workerId, _flow.Parallelism.RetryClaim, Lease, now, RunId, Waits, _flow.Reliability.Retry.Attempts, Batches, ct).ConfigureAwait(false);
        var waitedDue = await ReportWaitingAsync(due.Waiting, submissionId, ct).ConfigureAwait(false);
        if (due.Lease is not { } lease)
        {
            return WorkerSummary.Empty with { Waiting = waitedDue };
        }

        _logger.LogInformation("Claimed {Count} record(s) due for a retry.", due.Records.Count);
        var retried = await ProcessLeaseAsync(lease, batch: null, due.Records, ct).ConfigureAwait(false);
        return retried with { Waiting = retried.Waiting + waitedDue };
    }

    /// <summary>
    /// Puts the records a claim left waiting on the run's trace, one line each saying what the record waits for, and
    /// counts them. Waiting is not a try, so nothing is written to the ledger here: the claim already did.
    /// </summary>
    private async Task<long> ReportWaitingAsync(IReadOnlyList<WaitingRecord> waiting, Guid? submissionId, CancellationToken ct)
    {
        if (waiting.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "{Count} record(s) wait for a record they refer to that has not landed yet; the first {Key} {Reason}.",
            waiting.Count, waiting[0].DeliveryKey, waiting[0].Reason);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var record in waiting)
        {
            DeliveryMetrics.RecordWaiting(_flow.Label, Partition, RouteOf(_protocol.Kind));
            await _listener.OnEventAsync(new DeliveryEvent
            {
                AtUtc = now,
                FlowId = _flow.Id,
                FlowName = _flow.Label,
                Interface = _flow.Interface,
                Kind = "record.waiting",
                SubmissionId = submissionId,
                DeliveryKey = record.DeliveryKey,
                Worker = _workerId,
                Detail = record.Reason,
            }, ct).ConfigureAwait(false);
        }

        return waiting.Count;
    }

    /// <summary>
    /// Delivers what one lease holds and closes it: done with the batch's counts, stopped when the run is cancelled,
    /// failed when the work cannot be read. A lease another worker took over is closed for what this worker sent, and the
    /// rest is left to that worker.
    /// </summary>
    private async Task<WorkerSummary> ProcessLeaseAsync(LeaseState lease, WorkBatchState? batch, IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var started = _time.GetUtcNow().UtcDateTime;
        var journal = new LeaseJournal(_ledger, _flow.Id, lease.Token, _listener);
        using var lost = new CancellationTokenSource();
        using var keeping = new CancellationTokenSource();
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(ct, lost.Token);
        var keeper = Task.WhenAll(
            KeepLeaseAsync(lease, journal, records.Count, batch, lost, keeping.Token),
            TraceProgressAsync(lease, batch, keeping.Token));
        WorkerSummary summary;
        try
        {
            var loaded = batch is null
                ? await LoadRecordsAsync(records, sending.Token).ConfigureAwait(false)
                : await LoadBatchAsync(batch, records, sending.Token).ConfigureAwait(false);
            summary = await ProcessRecordsAsync(loaded, batch, journal, sending.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
            var sent = Summarize(journal) with { Batches = batch is null ? 0 : 1 };
            _logger.LogWarning(
                "The lease on {What} is no longer this worker's (it ran out and another worker took it over), so it stopped sending: {Summary}. The rest is left to that worker.",
                Describe(lease, batch), sent);
            await _ledger.CloseLeaseAsync(lease.Token, new LeaseClosing { End = LeaseEnd.Stopped }, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop, not a failure: hand the work back now rather than letting the lease run out, and do not charge the
            // interrupted tries to the retry budget.
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
            await CloseOnStopAsync(lease, batch).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or InvalidOperationException or JsonException)
        {
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
            var message = HeaderRedaction.RedactMessage(ex.Message);
            _logger.LogError("{What} could not be processed: {Message}", Describe(lease, batch), message);
            await _ledger.CloseLeaseAsync(lease.Token, new LeaseClosing { End = LeaseEnd.Failed, Failure = message }, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
        }

        var completed = _time.GetUtcNow().UtcDateTime;
        await _ledger.CloseLeaseAsync(
            lease.Token,
            new LeaseClosing { End = LeaseEnd.Done, Delivered = summary.Delivered, Held = summary.Held, Failed = summary.Failed, Retrying = summary.Retried },
            completed,
            CancellationToken.None).ConfigureAwait(false);
        if (batch is null)
        {
            return summary;
        }

        _logger.LogInformation("Batch {Batch} done in {Seconds:0.#}s: {Summary}", batch.Index, (completed - started).TotalSeconds, summary);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
            Kind = "batch.completed",
            SubmissionId = batch.SubmissionId,
            Worker = _workerId,
            Phase = batch.Index.ToString(CultureInfo.InvariantCulture),
            Duration = completed - started,
            Detail = summary.ToString(),
        }, CancellationToken.None).ConfigureAwait(false);
        return summary with { Batches = 1 };
    }

    /// <summary>The batch's documents for the records its lease holds, read once from its file.</summary>
    private async Task<IReadOnlyList<(RecordState Record, WorkItem? Item)>> LoadBatchAsync(WorkBatchState batch, IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var loaded = new List<(RecordState Record, WorkItem? Item)>(records.Count);
        if (records.Count == 0)
        {
            return loaded;
        }

        var submission = await SubmissionAsync(batch.SubmissionId, ct).ConfigureAwait(false);
        var wanted = records.ToDictionary(r => r.DeliveryKey.Value);
        try
        {
            await foreach (var (item, _) in WorkBatchFile.ReadAllAsync(_stores, WorkRoot ?? submission.WorkLocation ?? throw MissingWorkLocation(submission), batch.SubmissionId, batch.Index, ct).ConfigureAwait(false))
            {
                if (wanted.Remove(item.Key, out var record))
                {
                    loaded.Add((record, item));
                }
            }
        }
        catch (WorkDocumentMissingException ex)
        {
            // The batch is not there to read, or not as it was written: its records are the flow's to plan again, as a record
            // whose own document is missing is, while a store that did not answer fails the lease as it came.
            _logger.LogWarning(
                "Batch {Batch} of submission {SubmissionId} could not be read for {Records} record(s): {Message}",
                batch.Index, batch.SubmissionId, wanted.Count, HeaderRedaction.RedactMessage(ex.Message));
        }

        // Leased records the file does not hold (the work location was pruned) are planned again by the flow's next run, not
        // silently dropped.
        foreach (var missing in wanted.Values)
        {
            loaded.Add((missing, null));
        }

        return loaded;
    }

    /// <summary>The documents of records due for a retry, each read from its own batch.</summary>
    private async Task<IReadOnlyList<(RecordState Record, WorkItem? Item)>> LoadRecordsAsync(IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var loaded = new List<(RecordState Record, WorkItem? Item)>(records.Count);
        foreach (var record in records)
        {
            loaded.Add((record, await LoadItemAsync(record, ct).ConfigureAwait(false)));
        }

        return loaded;
    }

    private async Task CloseOnStopAsync(LeaseState lease, WorkBatchState? batch)
    {
        try
        {
            await _ledger.CloseLeaseAsync(lease.Token, new LeaseClosing { End = LeaseEnd.Stopped }, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Handed {What} back on shutdown; the next pass picks it up.", Describe(lease, batch));
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or DbException or TimeoutException)
        {
            // The lease runs out on its own and the next claim of the flow recovers it, charging the interrupted tries.
            _logger.LogWarning("Could not hand {What} back on shutdown ({Message}); its lease runs out and the next claim recovers it.", Describe(lease, batch), ex.Message);
        }
    }

    /// <summary>
    /// Keeps a lease while the worker delivers under it: renews its one row every half lease and, at each renewal, applies
    /// what the worker appended so far, so the records show their outcomes while the batch runs, and reports the progress
    /// on the run's trace. A lease that is no longer this worker's, or that could not be renewed before it ran out, is
    /// lost: <paramref name="lost"/> is cancelled and the worker stops sending under it.
    /// </summary>
    private async Task KeepLeaseAsync(LeaseState lease, LeaseJournal journal, int records, WorkBatchState? batch, CancellationTokenSource lost, CancellationToken ct)
    {
        var length = Lease;
        var interval = KeepInterval ?? TimeSpan.FromMilliseconds(Math.Max(length.TotalMilliseconds / 2, 1000));
        var retry = KeepInterval ?? TimeSpan.FromMilliseconds(Math.Max(length.TotalMilliseconds / 10, 500));
        var expires = lease.ExpiresUtc;
        var wait = interval;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow().UtcDateTime;
            bool renewed;
            try
            {
                renewed = await _ledger.RenewLeaseAsync(lease.Token, length, now, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            {
                if (now + retry >= expires)
                {
                    _logger.LogWarning("The lease on {What} could not be renewed before it ran out ({Message}); the worker stops sending under it.", Describe(lease, batch), ex.Message);
                    await lost.CancelAsync().ConfigureAwait(false);
                    return;
                }

                _logger.LogWarning("Renewing the lease on {What} failed ({Message}); trying again in {Seconds}s.", Describe(lease, batch), ex.Message, (int)retry.TotalSeconds);
                wait = retry;
                continue;
            }

            if (!renewed)
            {
                await lost.CancelAsync().ConfigureAwait(false);
                return;
            }

            expires = now + length;
            wait = interval;
            try
            {
                await _ledger.CheckpointLeaseAsync(lease.Token, now, ct).ConfigureAwait(false);
                await ReportProgressAsync(lease, journal, records, batch, now).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or DeliveryException)
            {
                _logger.LogWarning("The progress of {What} could not be applied yet ({Message}); closing its lease applies it.", Describe(lease, batch), ex.Message);
            }
        }
    }

    private async Task ReportProgressAsync(LeaseState lease, LeaseJournal journal, int records, WorkBatchState? batch, DateTime now)
    {
        var sent = Summarize(journal);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = now,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
            Kind = "batch.progress",
            SubmissionId = batch?.SubmissionId ?? lease.SubmissionId,
            Worker = _workerId,
            Phase = batch?.Index.ToString(CultureInfo.InvariantCulture) ?? "retries",
            Duration = now - lease.AcquiredUtc,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{sent.Processed} of {records} record(s) settled so far: {sent}"),
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Says on the run's trace how far the run's deliveries have got, at the pace the trace sets (<see cref="RunTrace.ProgressDue"/>),
    /// from the run's totals rather than the lease's: a run of thousands of batches says so a bounded number of times, and a
    /// batch of slow records (a large bulk upload, a throttled service) still shows the run is at work. Nothing outside a run.
    /// </summary>
    private async Task TraceProgressAsync(LeaseState lease, WorkBatchState? batch, CancellationToken ct)
    {
        if (Trace is not { } trace)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(ProgressCheck, _time, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            if (trace.ProgressDue("deliver", now))
            {
                _logger.LogInformation(RunTrace.Bounded, "Delivering: {Progress}; working on {What}.", trace.DeliveryProgress(now), Describe(lease, batch));
            }
        }
    }

    /// <summary>What a lease's journal has written so far, as a summary.</summary>
    private static WorkerSummary Summarize(LeaseJournal journal)
    {
        var (byStatus, unchanged) = journal.Written;
        var delivered = byStatus.GetValueOrDefault(RecordStatus.Delivered);
        var retried = byStatus.GetValueOrDefault(RecordStatus.Pending);
        var held = byStatus.GetValueOrDefault(RecordStatus.Held);
        var failed = byStatus.GetValueOrDefault(RecordStatus.Failed);
        return new WorkerSummary(delivered + retried + held + failed, delivered - unchanged, retried, held, failed, Unchanged: unchanged);
    }

    private static string Describe(LeaseState lease, WorkBatchState? batch)
        => batch is null
            ? $"the records due for a retry (lease {lease.Token})"
            : string.Create(CultureInfo.InvariantCulture, $"batch {batch.Index} of submission {batch.SubmissionId}");

    private static async Task StopKeepingAsync(CancellationTokenSource keeping, Task keeper)
    {
        if (!keeping.IsCancellationRequested)
        {
            await keeping.CancelAsync().ConfigureAwait(false);
        }

        await AwaitQuietlyAsync(keeper).ConfigureAwait(false);
    }

    /// <summary>Delivers a set of leased records: protocol batches when the protocol takes arrays, bounded concurrency otherwise.</summary>
    private async Task<WorkerSummary> ProcessRecordsAsync(IReadOnlyList<(RecordState Record, WorkItem? Item)> loaded, WorkBatchState? batch, LeaseJournal journal, CancellationToken ct)
    {
        var results = new WorkerSummary[loaded.Count];
        var failuresLogged = 0;
        var failuresLeftOut = 0;

        async Task RecordAsync(int index, RecordCompletion completion, DeliveryEvent evt, WorkerSummary summary, Exception? failure, IReadOnlyList<DeliveryStep> steps)
        {
            // Written whatever happens to the run meanwhile: the try happened, and the ledger says so.
            await journal.OutcomeAsync(completion, evt).ConfigureAwait(false);
            results[index] = summary;
            Observe(completion.Status, evt.Detail, failure);
            Trace?.Settled(completion.Status, completion.NothingSent);
            var described = Describes(completion.DeliveryKey);
            if (summary.Held > 0 || summary.Failed > 0 || summary.Retried > 0)
            {
                // A record the trace describes always says how it ended; of the rest, the first few of each batch do.
                if (described || Interlocked.Increment(ref failuresLogged) <= FailuresLoggedPerBatch)
                {
                    TraceProblem(completion, evt);
                }
                else
                {
                    Interlocked.Increment(ref failuresLeftOut);
                }
            }
            else if (described)
            {
                TraceSettled(completion, evt, steps);
            }
        }

        var groups = Group(loaded, Math.Max(1, _protocol.MaxBatch));
        using var gate = new SemaphoreSlim(Math.Max(1, _flow.Parallelism.RecordsAtOnce));
        var tasks = groups.Select(async group =>
        {
            void NeverStarted()
            {
                // Claimed but never started: closing the lease hands them back.
                foreach (var (index, _, _) in group)
                {
                    results[index] = WorkerSummary.Empty;
                }
            }

            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                NeverStarted();
                throw;
            }

            // A slot the delivery before gave up can be handed to this group just as that delivery stops the work (a failure
            // guard tripping, the run being cancelled): the semaphore then reports the slot taken although the stop came
            // first. The stop wins, so nothing is sent after it.
            if (ct.IsCancellationRequested)
            {
                gate.Release();
                NeverStarted();
                ct.ThrowIfCancellationRequested();
            }

            try
            {
                await DeliverGroupAsync(group, batch, journal, RecordAsync, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        var total = results.Aggregate(WorkerSummary.Empty, (acc, r) => acc.Add(r ?? WorkerSummary.Empty));
        if (failuresLeftOut > 0)
        {
            _logger.LogWarning("{Count} more record(s) were held, failed or scheduled for retry; see the submission's records.", failuresLeftOut);
        }

        return total;
    }

    private static List<List<(int Index, RecordState Record, WorkItem? Item)>> Group(IReadOnlyList<(RecordState Record, WorkItem? Item)> loaded, int size)
    {
        var groups = new List<List<(int, RecordState, WorkItem?)>>();
        var current = new List<(int, RecordState, WorkItem?)>(size);
        for (var i = 0; i < loaded.Count; i++)
        {
            var (record, item) = loaded[i];
            // Records without a document are settled alone: they never reach the protocol.
            if (item is null)
            {
                groups.Add([(i, record, null)]);
                continue;
            }

            current.Add((i, record, item));
            if (current.Count == size)
            {
                groups.Add(current);
                current = new List<(int, RecordState, WorkItem?)>(size);
            }
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    private async Task DeliverGroupAsync(
        List<(int Index, RecordState Record, WorkItem? Item)> group,
        WorkBatchState? batch,
        LeaseJournal journal,
        Func<int, RecordCompletion, DeliveryEvent, WorkerSummary, Exception?, IReadOnlyList<DeliveryStep>, Task> record,
        CancellationToken ct)
    {
        var started = _time.GetUtcNow().UtcDateTime;
        var works = new List<(int Index, RecordState Record, DeliveryWork Work)>(group.Count);
        // The step progress each record reported during this try, so the completion keeps it for the next try.
        var reportedSteps = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();

        // What these records' aborted or abandoned units left in OSDU, and what the units they resume created
        // (osdu/docs/reference/concepts/protocols.md, When a delivery does not complete); read only on a route that can
        // create anything before its last call.
        var open = await OpenArtifactsAsync(group.Select(g => g.Record.DeliveryKey).ToList(), ct).ConfigureAwait(false);
        var settledEarly = new List<RecordState>();
        foreach (var (index, state, item) in group)
        {
            if (item is null || state.TargetId is null)
            {
                // Nothing about the record's data is wrong: its queued document is gone (or it names no OSDU id to send it
                // to, which only a write outside the ledger leaves). It drops what it had queued and is planned again from
                // its row by the next run, rather than held for a release that would only send the missing document again.
                var (completion, evt, summary) = Settle(
                    state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null,
                    "the rendered document queued for the record could not be read (its work batch was pruned or rewritten); the flow's next run plans it again from its row",
                    null, null);
                await record(index, completion with { Replan = true }, evt, summary, null, []).ConfigureAwait(false);
                settledEarly.Add(state);
                continue;
            }

            // A flow writes only the OSDU ids its records claimed when their documents were queued; staging claims them,
            // so an unclaimed id here means the record's state was changed outside the ledger, and nothing is sent.
            if (!string.Equals(state.ClaimedTargetId, state.TargetId, StringComparison.Ordinal))
            {
                var (completion, evt, summary) = Settle(
                    state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null,
                    $"the record's OSDU id {state.TargetId} is not claimed by this flow (claimed: {state.ClaimedTargetId ?? "none"}), so nothing was sent; redeliver it to plan it again",
                    null, null);
                await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
                settledEarly.Add(state);
                continue;
            }

            // The final check before anything is sent: the queued document and payload against what OSDU holds at the
            // moment this worker has the record. What was planned against an earlier state can already have landed.
            var (sendMetadata, sendPayload) = ChangeDetector.AtPush(state, _flow.Change);
            if (!sendMetadata && !sendPayload)
            {
                var held = $"the final hash check found OSDU already holding this version (metadata hash {state.PendingMetadataHash ?? "none"}"
                    + (state.PendingPayload ? $", payload hash {state.PendingPayloadHash ?? "none"}" : string.Empty) + "); nothing was sent";
                var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Delivered, AttemptOutcome.Skipped, AttemptPhases.Unchanged, null, held, null, null, null, promote: true, nothingSent: true);
                await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
                settledEarly.Add(state);
                continue;
            }

            JsonObject document;
            try
            {
                document = JsonNode.Parse(item.Document) as JsonObject ?? throw new DeliveryException("the pending document is not a JSON object");
            }
            catch (JsonException ex)
            {
                var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, $"the pending document is not valid JSON: {ex.Message}", null, null);
                await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
                settledEarly.Add(state);
                continue;
            }

            // The document is written to the id it carries, and the ledger records the record's: the two must be one id, or
            // OSDU would gain a record the ledger does not know while the one it names stays as it was.
            if (document["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var documentId) && !string.Equals(documentId, state.TargetId, StringComparison.Ordinal))
            {
                var (completion, evt, summary) = Settle(
                    state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null,
                    $"the queued document is written to {documentId}, and the record's OSDU id is {state.TargetId}, so nothing was sent; redeliver it to plan it again",
                    null, null);
                await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
                settledEarly.Add(state);
                continue;
            }

            // A payload sent in parts lists each part with its files and hash; one payload set is its folder.
            IPayloadSource? single = null;
            IReadOnlyList<WorkPayloadPart> parts = [];
            IReadOnlySet<string> forced = new HashSet<string>(StringComparer.Ordinal);
            if (state.PendingPayloadLocation is { } location)
            {
                if (CompositePayload.IsComposite(location))
                {
                    CompositePayload composite;
                    try
                    {
                        composite = CompositePayload.Decode(location);
                    }
                    catch (DeliveryException ex)
                    {
                        var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, ex.Message, null, null);
                        await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
                        settledEarly.Add(state);
                        continue;
                    }

                    parts = composite.Parts
                        .Select(p => new WorkPayloadPart(p.Role, p.Payload, p.Location is null ? null : new StoragePayloadSource(_payloads, PayloadLocation.Parse(p.Location)), p.Hash))
                        .ToList();
                    forced = composite.Forced;
                }
                else
                {
                    single = new StoragePayloadSource(_payloads, PayloadLocation.Parse(location));
                }
            }

            // The records the record's enrichments add, a dataset's files streaming from where its row said they are, and what the
            // plan decided a delivery does with them.
            EnrichWork stepWork;
            IReadOnlyList<WorkStepPart> stepParts;
            try
            {
                stepWork = EnrichParts.Decode(item.Enrich);
                stepParts = stepWork.Parts
                    .Select(p => new WorkStepPart(p, p.RegistersFiles ? new StoragePayloadSource(_payloads, PayloadLocation.Parse(p.Location)) : null))
                    .ToList();
            }
            catch (DeliveryException ex)
            {
                var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, ex.Message, null, null);
                await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
                settledEarly.Add(state);
                continue;
            }

            // The unit of work this try belongs to: the one an earlier try of the same work began, kept with its steps, or a new
            // one. The route is given the steps without it.
            var kept = ParseSteps(state.PendingStepJson);
            var unit = DeliveryUnit.FromSteps(kept) ?? new DeliveryUnit(Guid.CreateVersion7(), started);
            var completedSteps = kept.Where(kv => kv.Key != DeliveryUnit.StepName).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var key = state.DeliveryKey;
            works.Add((index, state, new DeliveryWork
            {
                Key = key,
                TargetId = state.TargetId,
                Document = document,
                Assertions = AssertionFindings.FromText(item.Assertions),
                DeliverMetadata = sendMetadata,
                DeliverPayload = sendPayload,
                Payload = single,
                Parts = parts,
                ForcedParts = forced,
                StepParts = stepParts,
                StepDocumentUnchanged = stepWork.DocumentUnchanged,
                StepPartsForced = stepWork.Forced,
                StepKeptRecords = stepWork.KeptRecords,
                StepKeep = stepWork.Keep.ToDictionary(k => KeptStep.StateKey(k.Key), k => k.Value, StringComparer.Ordinal),
                StepTrace = stepWork.Trace,
                ExistingVersion = state.TargetVersion,
                // A record OSDU no longer held when it was last looked for (a verify, or an inventory's repair) is brought back
                // under its id by a route that can, before it is written; the mark goes with the first write that lands.
                RestoreFirst = state.LastVerifyOutcome == VerifyOutcome.Missing,
                SourceKey = state.SourceKey,
                Label = state.Label,
                CompletedSteps = completedSteps,
                TargetState = JsonMerge.ToValues(state.TargetStateJson),
                Unit = unit,
                StepCompleted = (report, _) => SaveStepAsync(state, unit, completedSteps, report, reportedSteps, journal),
            }));
        }

        // Before anything is sent: what an aborted or abandoned unit of these records left is undone, under the lease. A record
        // the route is about to write keeps the record itself when the newer work writes its metadata again; one settled
        // without reaching the route has everything its units left taken back.
        var waiting = await UndoBeforeSendingAsync(open, settledEarly, works.Select(w => (w.Record, w.Work.Unit!, w.Work.DeliverMetadata)).ToList(), journal, ct).ConfigureAwait(false);

        // A record whose earlier unit could not be undone yet is not sent: the newer work would write the ids the undo still has
        // to take back, and the undo, when it lands, would take back the newer work's. The try is not charged: nothing was sent.
        foreach (var (index, state, _) in works.Where(w => waiting.ContainsKey(w.Record.DeliveryKey)))
        {
            var wait = waiting[state.DeliveryKey];
            var (completion, evt, summary) = wait.RetryAtUtc is { } retryAt
                ? Settle(state, batch, started, RecordStatus.Pending, AttemptOutcome.Skipped, AttemptPhases.UndoWait, null, wait.Detail, null, null, null, keepSteps: true, nextAttempt: retryAt, nothingSent: true)
                : Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, AttemptPhases.UndoWait, null, null, wait.Detail, null, null);
            await record(index, completion, evt, summary, null, []).ConfigureAwait(false);
        }

        works.RemoveAll(w => waiting.ContainsKey(w.Record.DeliveryKey));
        if (works.Count == 0)
        {
            return;
        }

        // The records the run's trace describes say what is being sent before anything is, and what is said and sent for
        // them is on the trace; a group that holds one of them is described whole, since it sends its records together.
        // Of any other group, only its problems reach the trace, within the run's allowance.
        var described = works.Where(w => Describes(w.Record.DeliveryKey)).ToList();
        using var about = RunTrace.AboutRecords(described.Count > 0);
        foreach (var (_, state, work) in described)
        {
            TraceSending(state, work);
        }

        // The run's correlation id: every OSDU request the protocol sends for these records carries it, and each attempt
        // names it, so what happened can be followed into the services' own logs.
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        var outcomes = new DeliveryOutcome?[works.Count];
        var verdicts = new ValidationVerdict?[works.Count];
        try
        {
            await GateAsync(works, outcomes, verdicts, ct).ConfigureAwait(false);
            var sending = Enumerable.Range(0, works.Count).Where(i => outcomes[i] is null).ToList();
            if (sending.Count > 0)
            {
                var sent = sending.Count == 1
                    ? [await DeliverOneAsync(works[sending[0]].Work, ct).ConfigureAwait(false)]
                    : await _protocol.DeliverBatchAsync(sending.Select(i => works[i].Work).ToList(), ct).ConfigureAwait(false);
                if (sent.Count != sending.Count)
                {
                    throw new DeliveryException($"The {_protocol.Kind} protocol answered {sent.Count} outcome(s) for {sending.Count} record(s).");
                }

                for (var i = 0; i < sending.Count; i++)
                {
                    outcomes[sending[i]] = sent[i];
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop, not a failure: closing the lease hands the records back without charging the interrupted try.
            throw;
        }

        var settled = new List<(int Index, RecordState State, DeliveryWork Work, DeliveryOutcome Outcome, RecordCompletion Completion, DeliveryEvent Event, WorkerSummary Summary)>(works.Count);
        for (var i = 0; i < works.Count; i++)
        {
            var (index, state, work) = works[i];
            var latestSteps = reportedSteps.TryGetValue(state.DeliveryKey.Value, out var reported) ? reported : state.PendingStepJson;
            var outcome = outcomes[i]!;
            if (outcome.Succeeded && (work.StepKeep.Count > 0 || work.StepTrace.Count > 0))
            {
                // What the enrichments set aside on their holds composed into the document that landed: kept with the record, so
                // a later render one of them holds in goes ahead with it (osdu/docs/reference/flow/enrich-holds.md). And where
                // the document's enrichments came from, kept with the record it belongs to (osdu/docs/reference/flow/enrich.md).
                var returned = new Dictionary<string, string>(outcome.Returned, StringComparer.Ordinal);
                foreach (var (key, value) in work.StepKeep.Concat(work.StepTrace))
                {
                    returned[key] = value;
                }

                outcome = outcome with { Returned = returned };
            }

            var (completion, evt, summary) = Classify(state, batch, started, work, outcome, latestSteps, correlation.Id, verdicts[i]);
            completion = completion with { UnitId = work.Unit?.Id, Superseded = outcome.Succeeded ? outcome.Superseded : [] };
            settled.Add((index, state, work, outcome, completion, evt, summary));
        }

        // A try that ended held or failed aborts its unit: what the unit created is undone now, under the lease, and written
        // after the try's completion, which makes the unit's artifacts due.
        var aborted = settled
            .Where(s => s.Completion.Status is RecordStatus.Held or RecordStatus.Failed)
            .Select(s => (s.State, s.Work, s.Completion.Status))
            .ToList();
        var undos = await UndoAbortedAsync(aborted, correlation.Id, ct).ConfigureAwait(false);
        foreach (var (index, state, _, outcome, completion, evt, summary) in settled)
        {
            await record(index, completion, evt, summary, outcome.Failure, outcome.Steps).ConfigureAwait(false);
            if (undos.TryGetValue(state.DeliveryKey, out var undo))
            {
                await journal.UndoAsync(undo).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The open artifacts of <paramref name="keys"/>, by record, on a route that can create anything before its last call; none
    /// on any other, which reads nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<DeliveryKey, List<LedgerArtifact>>> OpenArtifactsAsync(IReadOnlyList<DeliveryKey> keys, CancellationToken ct)
    {
        if (!_protocol.Undoes || keys.Count == 0)
        {
            return new Dictionary<DeliveryKey, List<LedgerArtifact>>();
        }

        var open = await _ledger.OpenArtifactsAsync(_flow.Id, keys, ct).ConfigureAwait(false);
        return open.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Undoes, before anything is sent, what aborted or abandoned units of the group's records left: for a record about to be
    /// written, every open artifact of another unit than the one it resumes, the record itself kept when the work about to go
    /// writes its metadata again; for a record settled without reaching the route, every open artifact. Each undo is
    /// journaled under the lease. Returns the records about to be written whose earlier units are not undone yet, which wait.
    /// </summary>
    private async Task<IReadOnlyDictionary<DeliveryKey, UndoWait>> UndoBeforeSendingAsync(
        IReadOnlyDictionary<DeliveryKey, List<LedgerArtifact>> open,
        IReadOnlyList<RecordState> settledEarly,
        IReadOnlyList<(RecordState Record, DeliveryUnit Unit, bool WritesMetadata)> sending,
        LeaseJournal journal,
        CancellationToken ct)
    {
        var waiting = new Dictionary<DeliveryKey, UndoWait>();
        if (open.Count == 0)
        {
            return waiting;
        }

        var requests = new List<UndoRequest>();
        foreach (var record in settledEarly)
        {
            if (open.TryGetValue(record.DeliveryKey, out var artifacts) && artifacts.Count > 0)
            {
                requests.Add(new UndoRequest(record, artifacts, ReasonOf(record, artifacts), KeepRecord: false));
            }
        }

        foreach (var (record, unit, writesMetadata) in sending)
        {
            if (open.TryGetValue(record.DeliveryKey, out var artifacts) && artifacts.Where(a => a.UnitId != unit.Id).ToList() is { Count: > 0 } left)
            {
                requests.Add(new UndoRequest(record, left, ReasonOf(record, left), KeepRecord: writesMetadata));
            }
        }

        if (requests.Count == 0)
        {
            return waiting;
        }

        var writing = sending.Select(s => s.Record.DeliveryKey).ToHashSet();
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        var undos = await new UndoRunner(_protocol, _time, _workerId, RunId).RunAsync(requests, correlation.Id, ct).ConfigureAwait(false);
        foreach (var undo in undos)
        {
            TraceUndo(undo);
            await journal.UndoAsync(undo).ConfigureAwait(false);
            if (writing.Contains(undo.DeliveryKey) && UndoWait.Of(undo) is { } wait)
            {
                waiting[undo.DeliveryKey] = wait;
            }
        }

        return waiting;
    }

    /// <summary>
    /// Why a record's newer work waits: what an earlier unit left that its undo could not take back, and when the undo is tried
    /// again; no retry once the undo has used its tries, and the record is held until an operator releases it.
    /// </summary>
    private sealed record UndoWait(string Detail, DateTime? RetryAtUtc)
    {
        /// <summary>The wait <paramref name="undo"/> leaves its record in; null when every artifact was settled.</summary>
        public static UndoWait? Of(RecordUndo undo)
        {
            var failed = undo.Settlements.Where(s => s.Status == ArtifactStatus.Failed).ToList();
            if (failed.Count == 0)
            {
                return null;
            }

            var count = failed.Count.ToString(CultureInfo.InvariantCulture);
            var notes = ArtifactLimits.Note(string.Join("; ", failed.Select(s => s.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal)));
            return failed.Any(s => s.RetryAtUtc is null)
                ? new UndoWait(
                    string.Create(CultureInfo.InvariantCulture, $"an earlier delivery of this record left {count} item(s) in OSDU that {ArtifactLimits.MaxUndoAttempts} undo tries could not take back ({notes}), so the newer work was not sent; once what stops the undo is fixed, release the record and the undo is tried again before it"),
                    null)
                : new UndoWait(
                    $"an earlier delivery of this record left {count} item(s) in OSDU that its undo could not take back yet ({notes}), so the newer work waits for the undo's next try",
                    failed.Min(s => s.RetryAtUtc));
        }
    }

    /// <summary>
    /// Undoes the units of the tries that ended held or failed: every open artifact each unit created, read from the ledger, where
    /// the unit's steps wrote them before the try went on. Returns each record's undo, to be journaled after its completion.
    /// </summary>
    private async Task<IReadOnlyDictionary<DeliveryKey, RecordUndo>> UndoAbortedAsync(
        IReadOnlyList<(RecordState State, DeliveryWork Work, RecordStatus Status)> aborted, string correlationId, CancellationToken ct)
    {
        if (!_protocol.Undoes || aborted.Count == 0)
        {
            return new Dictionary<DeliveryKey, RecordUndo>();
        }

        var open = await _ledger.OpenArtifactsAsync(_flow.Id, aborted.Select(a => a.State.DeliveryKey).ToList(), ct).ConfigureAwait(false);
        var byKey = open.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.ToList());
        var requests = new List<UndoRequest>();
        foreach (var (state, work, status) in aborted)
        {
            if (work.Unit is not { } unit || !byKey.TryGetValue(state.DeliveryKey, out var artifacts))
            {
                continue;
            }

            var mine = artifacts.Where(a => a.UnitId == unit.Id).ToList();
            if (mine.Count > 0)
            {
                requests.Add(new UndoRequest(state, mine, status == RecordStatus.Held ? UndoReason.Held : UndoReason.Failed, KeepRecord: false));
            }
        }

        var undos = await new UndoRunner(_protocol, _time, _workerId, RunId).RunAsync(requests, correlationId, ct).ConfigureAwait(false);
        foreach (var undo in undos)
        {
            TraceUndo(undo);
        }

        return undos.ToDictionary(u => u.DeliveryKey);
    }

    /// <summary>Why a record's open artifacts are undone, from what the record is now and what is left of its units.</summary>
    internal static UndoReason ReasonOf(RecordState record, IReadOnlyList<LedgerArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Any(a => a.Status is ArtifactStatus.Intent or ArtifactStatus.Pending))
        {
            return UndoReason.Abandoned;
        }

        return record.Status switch
        {
            RecordStatus.Held => UndoReason.Held,
            RecordStatus.Failed => UndoReason.Failed,
            RecordStatus.Deleted => UndoReason.Removed,
            _ => UndoReason.Abandoned,
        };
    }

    /// <summary>An undo on the run's trace: what it took back for the record, and what is still to undo.</summary>
    private void TraceUndo(RecordUndo undo)
    {
        var outcomes = undo.Settlements.Select(s => s.Status).ToList();
        if (undo.Attempt.Error is { } error)
        {
            _logger.LogWarning(
                "Undoing what an unfinished delivery of {Record} left in OSDU: {Summary}; {Error}", undo.DeliveryKey, ArtifactLimits.Describe(outcomes), error);
        }
        else if (Describes(undo.DeliveryKey))
        {
            _logger.LogInformation("Undid what an unfinished delivery of {Record} left in OSDU: {Summary}.", undo.DeliveryKey, ArtifactLimits.Describe(outcomes));
        }
    }

    /// <summary>
    /// The gate before anything is sent (<see cref="ValidationGate"/>): each record's verdict, kept for its attempt, and the
    /// records the gate holds, held with their documents kept so a release can send them as they are. When the records a
    /// group refers to cannot be looked up, every record of the group is tried again later, as if its delivery had failed.
    /// </summary>
    private async Task GateAsync(List<(int Index, RecordState Record, DeliveryWork Work)> works, DeliveryOutcome?[] outcomes, ValidationVerdict?[] verdicts, CancellationToken ct)
    {
        if (Gate is not { } gate)
        {
            return;
        }

        IReadOnlyList<GateDecision> decisions;
        try
        {
            decisions = await gate.DecideAsync(works.Select(w => (w.Record, w.Work.Document, w.Work.DeliverMetadata, w.Work.Assertions)).ToList(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = new DeliveryException($"the records this one refers to could not be looked up before it was sent: {ex.Message}", ex);
            for (var i = 0; i < works.Count; i++)
            {
                outcomes[i] = DeliveryOutcome.Failed(failure);
            }

            return;
        }

        for (var i = 0; i < works.Count; i++)
        {
            var (decision, record) = (decisions[i], works[i].Record);
            verdicts[i] = decision.Verdict;
            var held = decision.Hold is not null;
            if (decision.Hold is { } hold)
            {
                outcomes[i] = DeliveryOutcome.Failed(new RecordHeldException(hold));
            }

            Trace?.Validation.Add(decision.Verdict, held);
            _drained.Add(decision.Verdict, held);
            if (decision.Verdict.Outcome != ValidationOutcome.NotValidated && Describes(record.DeliveryKey))
            {
                _logger.LogInformation("{Record} validated: {Verdict}.", RunTrace.Record(record.SourceKey, record.Label, record.DeliveryKey), decision.Verdict.Summary());
            }
        }
    }

    /// <summary>Whether the run's trace describes the record; outside a run no record is described.</summary>
    private bool Describes(DeliveryKey key) => Trace?.Describes(key, _logger) == true;

    /// <summary>A described record, as it is handed to the protocol: what is sent, to which id, and what an earlier try already did.</summary>
    private void TraceSending(RecordState record, DeliveryWork work)
    {
        var what = (work.DeliverMetadata, work.DeliverPayload) switch
        {
            (true, true) => "the record and its payload",
            (true, false) => "the record",
            _ => "its payload",
        };
        if (work.Parts.Count > 0 && work.DeliverPayload)
        {
            var parts = work.Parts.Where(work.Sends).Select(p => p.Role).ToList();
            what += parts.Count == 0 ? " (no part of it changed)" : $" ({string.Join(", ", parts)})";
        }

        var attempt = record.AttemptCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $", attempt {record.AttemptCount} of {_flow.Reliability.Retry.Attempts}")
            : string.Empty;
        var resuming = work.CompletedSteps.Count > 0
            ? $", resuming after {string.Join(", ", work.CompletedSteps.Keys)}"
            : string.Empty;
        _logger.LogInformation(
            "Sending {Record} to {TargetId}: {What}{Attempt}{Resuming}.",
            RunTrace.Record(record.SourceKey, record.Label, record.DeliveryKey), work.TargetId, what, attempt, resuming);
    }

    /// <summary>How a described record that landed, or that OSDU already held, ended: its id, version and the steps it took.</summary>
    private void TraceSettled(RecordCompletion completion, DeliveryEvent evt, IReadOnlyList<DeliveryStep> steps)
    {
        var record = RunTrace.Record(evt.SourceKey, evt.Label, completion.DeliveryKey);
        if (evt.Kind == "record.unchanged")
        {
            _logger.LogInformation("{Record}: {Detail}.", record, evt.Detail);
            return;
        }

        var version = evt.TargetVersion is { } v ? string.Create(CultureInfo.InvariantCulture, $" version {v}") : string.Empty;
        var took = steps.Count == 0 ? string.Empty : "; " + string.Join("; ", steps.Select(DescribeStep));
        _logger.LogInformation(
            "Delivered {Record} as {TargetId}{Version} ({Phase}) in {Elapsed}{Steps}.",
            record, evt.TargetId, version, evt.Phase, RunTrace.Elapsed(evt.Duration ?? TimeSpan.Zero), took);
    }

    /// <summary>How a record that was held, failed or put back for a retry ended, with the reason the ledger records.</summary>
    private void TraceProblem(RecordCompletion completion, DeliveryEvent evt)
    {
        var record = RunTrace.Record(evt.SourceKey, evt.Label, completion.DeliveryKey);
        switch (completion.Status)
        {
            case RecordStatus.Pending when completion.NextAttemptUtc is { } next:
                _logger.LogWarning("{Record} is tried again at {Next:u}: {Detail}", record, next, evt.Detail);
                break;
            case RecordStatus.Held:
                _logger.LogWarning("{Record} is held: {Detail}", record, evt.Detail);
                break;
            default:
                _logger.LogWarning("{Record} failed: {Detail}", record, evt.Detail);
                break;
        }
    }

    /// <summary>One step of an attempt as the trace names it: the status the target answered, how long it took, what it returned.</summary>
    private static string DescribeStep(DeliveryStep step)
    {
        if (step.Resumed)
        {
            return $"{step.Name} done by an earlier try";
        }

        var status = step.Status is { } s ? string.Create(CultureInfo.InvariantCulture, $" HTTP {s}") : string.Empty;
        var took = RunTrace.Elapsed(step.CompletedUtc - step.StartedUtc);
        var returned = step.Returned
            .Where(kv => kv.Key is not ("recordId" or "version"))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} {kv.Value}")
            .ToList();
        var values = returned.Count == 0 ? string.Empty : $", {string.Join(", ", returned)}";
        var error = step.Error is { } e ? $", {e}" : string.Empty;
        return HeaderRedaction.RedactMessage($"{step.Name}{status} in {took}{values}{error}");
    }

    /// <summary>Reports a record's outcome to the guard: a delivery ends every run of failures, a failure counts in its class.</summary>
    private void Observe(RecordStatus status, string? detail, Exception? failure)
    {
        if (Guard is not { } guard)
        {
            return;
        }

        switch (status)
        {
            case RecordStatus.Delivered:
                guard.Succeeded();
                break;
            case RecordStatus.Pending:
                guard.Failed(FailureGuard.Classify(failure), settled: false, detail);
                break;
            default:
                guard.Failed(FailureGuard.Classify(failure), settled: true, detail);
                break;
        }
    }

    private async Task<DeliveryOutcome> DeliverOneAsync(DeliveryWork work, CancellationToken ct)
    {
        try
        {
            return await _protocol.DeliverAsync(work, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DeliveryOutcome.Failed(ex);
        }
    }

    /// <summary>Turns a protocol outcome into the record's next state, its attempt and its event.</summary>
    private (RecordCompletion Completion, DeliveryEvent Event, WorkerSummary Summary) Classify(
        RecordState record, WorkBatchState? batch, DateTime started, DeliveryWork work, DeliveryOutcome outcome, string? latestSteps, string correlationId, ValidationVerdict? verdict)
    {
        var resultJson = AttemptResult.WithValidation(ResultJson(outcome, work.CompletedSteps, latestSteps, correlationId, work.Unit), verdict);
        record = record with { PendingStepJson = latestSteps };
        if (outcome.Succeeded)
        {
            var phase = (outcome.MetadataDelivered, outcome.PayloadDelivered) switch
            {
                (true, true) => "metadata+payload",
                (true, false) => "metadata",
                (false, true) => "payload",
                _ => "none",
            };
            var targetState = JsonMerge.Merge(record.TargetStateJson, JsonMerge.FromValues(outcome.Returned));

            // The version this delivery replaced, as the claim knew it: what a reversal of it puts back.
            var replaced = AttemptResult.WithReplaced(resultJson, work.ExistingVersion);
            return Settle(record, batch, started, RecordStatus.Delivered, AttemptOutcome.Delivered, phase, outcome.TargetVersion, outcome.Detail, null, replaced, targetState, promote: true);
        }

        var failure = outcome.Failure!;
        switch (failure)
        {
            case RecordHeldException held:
                return Settle(record, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, held.Message, resultJson, null, keepSteps: true);
            case OsduStatusException http when IsTerminal(http.StatusCode):
                return Settle(record, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, $"HTTP {http.StatusCode} is not retryable: {http.Message}", resultJson, null, keepSteps: true);
            case SqlFlowException or HttpRequestException or IOException or TimeoutException or JsonException or InvalidOperationException:
                {
                    var attempts = record.AttemptCount;
                    if (attempts >= _flow.Reliability.Retry.Attempts)
                    {
                        return Settle(record, batch, started, RecordStatus.Failed, AttemptOutcome.Failed, "none", null, null, $"failed after {attempts} attempt(s): {failure.Message}", resultJson, null, keepSteps: true);
                    }

                    var backoff = RetryPolicy.RecordBackoff(_flow.Reliability.Retry, attempts);

                    // A service that said how long to wait is not asked again sooner: the transport hands a wait
                    // longer than it will sit through inline up here, and the record's next attempt honours it.
                    if (failure is OsduStatusException { RetryAfter: { } asked } && asked > backoff)
                    {
                        backoff = asked;
                    }

                    var next = _time.GetUtcNow().UtcDateTime + backoff;
                    return Settle(record, batch, started, RecordStatus.Pending, AttemptOutcome.Failed, "none", null, null, failure.Message, resultJson, null, keepSteps: true, nextAttempt: next);
                }

            default:
                return Settle(record, batch, started, RecordStatus.Failed, AttemptOutcome.Failed, "none", null, null, $"unexpected failure: {failure.GetType().Name}: {failure.Message}", resultJson, null, keepSteps: true);
        }
    }

    private bool IsTerminal(int status)
    {
        if (_flow.Reliability.SkipStatusCodes.Contains(status))
        {
            return true;
        }

        // Client errors that a retry cannot fix. 401/408/425/429 are transient (token refresh, throttling).
        return status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.NotFound
            or (int)HttpStatusCode.MethodNotAllowed or (int)HttpStatusCode.Conflict or (int)HttpStatusCode.RequestEntityTooLarge
            or (int)HttpStatusCode.UnsupportedMediaType or (int)HttpStatusCode.UnprocessableEntity;
    }

    private (RecordCompletion Completion, DeliveryEvent Event, WorkerSummary Summary) Settle(
        RecordState record,
        WorkBatchState? batch,
        DateTime started,
        RecordStatus status,
        AttemptOutcome outcome,
        string phase,
        long? version,
        string? detail,
        string? error,
        string? resultJson,
        string? targetStateJson,
        bool promote = false,
        bool keepSteps = false,
        DateTime? nextAttempt = null,
        bool nothingSent = false)
    {
        var completed = _time.GetUtcNow().UtcDateTime;
        var redacted = error is null ? null : HeaderRedaction.RedactMessage(error);

        // The record keeps what the check of the try's document came to; a try that checked none leaves its last as it was.
        var verdict = AttemptResult.Validation(resultJson);
        var completion = new RecordCompletion
        {
            Validation = verdict is { Outcome: not ValidationOutcome.NotValidated }
                ? new RecordValidation(ValidationOutcomes.Name(verdict.Outcome), verdict.ProblemCount, verdict.CheckedUtc, verdict.Assertions?.Failed)
                : null,
            DeliveryKey = record.DeliveryKey,
            Status = status,
            Promote = promote,
            NothingSent = nothingSent,
            Claimed = ClaimedWork.Of(record),
            ClaimedAttemptCount = record.AttemptCount,
            TargetVersion = version,
            TargetId = record.TargetId,
            NextAttemptUtc = nextAttempt,
            Error = redacted,
            TargetStateJson = targetStateJson,
            // A retry resumes after the steps that completed; a settled record starts afresh next time.
            PendingStepJson = keepSteps && status == RecordStatus.Pending ? record.PendingStepJson : null,
            Attempt = new AttemptRecord
            {
                DeliveryKey = record.DeliveryKey,
                SubmissionId = record.LastSubmissionId,
                RunId = RunId,
                Worker = _workerId,
                StartedUtc = started,
                CompletedUtc = completed,
                Outcome = outcome,
                Phase = phase,
                // A half the work carries is the one it tried; a half it does not carry is the one OSDU holds, so a version
                // written back later (a reversal, a step back) takes the hashes OSDU really held at this version.
                MetadataHash = record.PendingMetadata ? record.PendingMetadataHash : record.MetadataHash,
                PayloadHash = record.PendingPayload ? record.PendingPayloadHash : record.PayloadHash,
                TargetVersion = version,
                // The error column holds errors; what an attempt that did not fail has to say goes with its result.
                Error = redacted,
                ResultJson = redacted is null ? AttemptResult.WithDetail(resultJson, detail) : resultJson,
                WorkBatch = batch?.Index ?? record.WorkBatch,
                // The ingestion file and row the document being delivered was built from: what ties this try to the
                // exact line of the exact file, whatever the record later moves on to.
                SourceFileName = record.PendingSourceFileName,
                SourceRowNumber = record.PendingSourceRowNumber,
                SourceUpdatedUtc = record.PendingSourceUpdatedUtc,
                // The version of the parent the document was rendered from, as the hashes above take the half the work carries.
                ParentVersion = record.PendingMetadata ? record.PendingParentVersion : record.ParentVersion,
            },
        };

        var evt = new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
            Kind = status switch
            {
                RecordStatus.Delivered when nothingSent => "record.unchanged",
                RecordStatus.Delivered => "record.delivered",
                RecordStatus.Pending => "record.retry",
                RecordStatus.Held => "record.held",
                _ => "record.failed",
            },
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = record.DeliveryKey,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = version,
            Worker = _workerId,
            Phase = phase,
            Duration = completed - started,
            Detail = redacted ?? detail ?? (nextAttempt is { } n ? $"next attempt at {n:u}" : null),
            Returned = resultJson,
        };

        var summary = status switch
        {
            RecordStatus.Delivered when nothingSent => new WorkerSummary(1, 0, 0, 0, 0, Unchanged: 1),
            RecordStatus.Delivered => new WorkerSummary(1, 1, 0, 0, 0),
            RecordStatus.Pending => new WorkerSummary(1, 0, 1, 0, 0),
            RecordStatus.Held => new WorkerSummary(1, 0, 0, 1, 0),
            _ => new WorkerSummary(1, 0, 0, 0, 1),
        };

        // Telemetry for watching the fleet; the counts the product shows are read from the ledger.
        DeliveryMetrics.RecordSettled(_flow.Label, Partition, RouteOf(_protocol.Kind), evt.Kind["record.".Length..], completed - started);
        return (completion, evt, summary);
    }

    /// <summary>The route a protocol is, as flows name it, or the protocol's own name for one no flow can name.</summary>
    private static string RouteOf(DeliveryProtocol kind) => Enum.IsDefined(kind) ? DeliveryProtocols.Name(kind) : kind.ToString();

    /// <summary>
    /// The attempt's result: the correlation id its OSDU requests carried, every step (including the ones resumed from an
    /// earlier try) and what came back.
    /// </summary>
    internal static string? ResultJson(
        DeliveryOutcome outcome,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> completedBefore,
        string? reportedDuringTry = null,
        string? correlationId = null,
        DeliveryUnit? unit = null)
    {
        var steps = new JsonArray();
        foreach (var (name, returned) in completedBefore)
        {
            if (name != DeliveryUnit.StepName && !outcome.Steps.Any(s => s.Name == name))
            {
                steps.Add(new JsonObject { ["name"] = name, ["resumed"] = true, ["returned"] = ToNode(returned) });
            }
        }

        // Steps the protocol reported before the try failed: they completed against the target, and the next try
        // resumes after them, so the attempt says so even though the protocol produced no outcome.
        foreach (var (name, returned) in ParseSteps(reportedDuringTry))
        {
            if (name != DeliveryUnit.StepName && !completedBefore.ContainsKey(name) && !outcome.Steps.Any(s => s.Name == name))
            {
                steps.Add(new JsonObject { ["name"] = name, ["completed"] = true, ["returned"] = ToNode(returned) });
            }
        }

        foreach (var step in outcome.Steps)
        {
            var node = new JsonObject
            {
                ["name"] = step.Name,
                ["startedUtc"] = step.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["ms"] = (long)(step.CompletedUtc - step.StartedUtc).TotalMilliseconds,
            };
            if (step.Status is { } status)
            {
                node["status"] = status;
            }

            if (step.Resumed)
            {
                node["resumed"] = true;
            }

            if (step.Error is { } error)
            {
                node["error"] = HeaderRedaction.RedactMessage(error);
            }

            if (step.Returned.Count > 0)
            {
                node["returned"] = ToNode(step.Returned);
            }

            steps.Add(node);
        }

        if (steps.Count == 0 && outcome.Returned.Count == 0 && correlationId is null)
        {
            return null;
        }

        var result = new JsonObject();
        if (correlationId is not null)
        {
            result["correlationId"] = correlationId;
        }

        // The unit of work the try belonged to, which the artifacts it created carry
        // (osdu/docs/reference/concepts/protocols.md, When a delivery does not complete).
        if (unit is not null && (steps.Count > 0 || outcome.Returned.Count > 0))
        {
            result["unit"] = unit.Id.ToString("D");
        }

        result["steps"] = steps;
        if (outcome.Returned.Count > 0)
        {
            result["returned"] = ToNode(outcome.Returned);
        }

        return result.ToJsonString();
    }

    private static JsonObject ToNode(IReadOnlyDictionary<string, string> values)
    {
        var node = new JsonObject();
        foreach (var (name, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            node[name] = value;
        }

        return node;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseSteps(string? json)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (name, value) in JsonMerge.Parse(json))
        {
            if (value is JsonObject values)
            {
                result[name] = JsonMerge.ToValues(values.ToJsonString());
            }
        }

        return result;
    }

    /// <summary>
    /// Appends a completed step before the protocol moves on, so a crash never repeats it. The step belongs to the document
    /// the record was claimed with; newer work queued behind the try never inherits it. It is written whatever happens to
    /// the run meanwhile, since what it records has already happened at the target.
    /// </summary>
    private Task SaveStepAsync(
        RecordState claimed,
        DeliveryUnit unit,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> completed,
        StepReport report,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, string> reported,
        LeaseJournal journal)
    {
        var key = claimed.DeliveryKey;
        var reference = claimed.PendingDocumentRef
            ?? throw new DeliveryException($"Record {key} is being delivered without a pending document reference, so its step progress cannot be tied to the document it belongs to.");
        string json;

        // A route reports the steps of a request's records at once, each building on the record's own progress so far, so the
        // progress of one record is read and written under one lock.
        lock (reported)
        {
            var node = new JsonObject
            {
                // The unit goes with the steps, so it is dropped exactly when they are, and a later try resumes the same unit.
                [DeliveryUnit.StepName] = ToNode(unit.ToValues()),
            };
            foreach (var (name, values) in completed)
            {
                node[name] = ToNode(values);
            }

            if (reported.TryGetValue(key.Value, out var earlier))
            {
                foreach (var (name, value) in JsonMerge.Parse(earlier))
                {
                    node[name] = value?.DeepClone();
                }
            }

            node[report.Step] = ToNode(report.Returned);
            json = node.ToJsonString();
            reported[key.Value] = json;
        }

        if (Describes(key))
        {
            var values = string.Join(", ", report.Returned.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
            var created = report.Artifacts.Count == 0
                ? string.Empty
                : "; " + string.Join(", ", report.Artifacts.Select(a => $"{ArtifactStatuses.Name(a.Status)} {a.Role} {a.TargetId ?? a.Locator}"));
            _logger.LogDebug(
                "{Record}: {Step} done{Values}{Created}.",
                RunTrace.Record(claimed.SourceKey, claimed.Label, key),
                report.Step,
                HeaderRedaction.RedactMessage(values.Length == 0 ? string.Empty : $" ({values})"),
                HeaderRedaction.RedactMessage(created));
        }

        return journal.StepAsync(new RecordStep(key, claimed.LastSubmissionId, reference, json, _time.GetUtcNow().UtcDateTime)
        {
            Unit = unit,
            Artifacts = report.Artifacts,
            RunId = RunId,
        });
    }

    private async Task<WorkItem?> LoadItemAsync(RecordState record, CancellationToken ct)
    {
        if (record.LastSubmissionId is not { } submissionId || !DocumentRef.TryParse(record.PendingDocumentRef, out var reference))
        {
            return null;
        }

        var submission = await SubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if ((WorkRoot ?? submission.WorkLocation) is not { } workRoot)
        {
            return null;
        }

        // A document that is not there is the record's to plan again; a store that did not answer fails the try as it came,
        // so the lease hands the record back and a later try reads again.
        WorkItem item;
        try
        {
            item = await WorkBatchFile.ReadOneAsync(_stores, workRoot, submissionId, reference, ct).ConfigureAwait(false);
        }
        catch (WorkDocumentMissingException ex)
        {
            _logger.LogWarning("The pending document of {SourceKey} could not be read from batch {Batch}: {Message}", record.SourceKey, reference.Batch, HeaderRedaction.RedactMessage(ex.Message));
            return null;
        }

        // The line a reference reads is the record's only while the file is the one the reference was taken from. A line that
        // names another record is never sent under this one: the record's document is missing, and the record is planned
        // again.
        if (item.Key != record.DeliveryKey.Value)
        {
            _logger.LogWarning(
                "The pending document of {SourceKey} could not be read from batch {Batch}: document {Reference} belongs to record {Other}, so the batch file was written again since the record was queued.",
                record.SourceKey, reference.Batch, reference, item.Key);
            return null;
        }

        return item;
    }

    private Task<SubmissionState> SubmissionAsync(Guid submissionId, CancellationToken ct)
    {
        var lazy = _submissions.GetOrAdd(submissionId, id => new Lazy<Task<SubmissionState>>(() => LoadSubmissionAsync(id, ct)));
        if (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _submissions.TryRemove(submissionId, out _);
        }

        return lazy.Value;
    }

    private async Task<SubmissionState> LoadSubmissionAsync(Guid submissionId, CancellationToken ct)
        => await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");

    private static DeliveryException MissingWorkLocation(SubmissionState submission)
        => new($"Submission {submission.SubmissionId} records no work location; its batches cannot be read.");

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected: the renewal loop was told to stop
        }
    }
}
