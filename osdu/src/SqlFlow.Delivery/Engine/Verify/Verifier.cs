using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Verify;

public sealed record VerifySummary(int Checked, int Matched, int Drifted, int Missing, int Errors)
{
    /// <summary>Whether the pass changed nothing: it found no record to check.</summary>
    public bool Idle => Checked == 0;

    public override string ToString() => $"{Checked} checked: {Matched} match, {Drifted} drifted, {Missing} missing, {Errors} errors";
}

/// <summary>
/// The drift pass (osdu/docs/reference/flow/delivery.md, verify): compares OSDU's current version of each delivered
/// record with the ledger's <c>targetVersion</c>. A mismatch is either drift to correct or a signal that this system is
/// not the authority for the record. With <c>reconcile</c> each drifted or missing record is marked for redelivery as a
/// redelivery marks it (<see cref="ILedger.RecordVerifyAsync"/>), named under the pass's activity, so the flow's next
/// deliver run sends it again whatever its incremental window reads. Every drifted record and the pass itself go
/// through the completion callback.
/// </summary>
public sealed class Verifier
{
    private readonly ILedger _ledger;
    private readonly IDeliveryProtocol _protocol;
    private readonly FlowDefinition _flow;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<Verifier> _logger;

    /// <summary>The trace of the run the pass is part of, which paces its progress lines; null outside a run.</summary>
    public RunTrace? Trace { get; init; }

    public Verifier(ILedger ledger, IDeliveryProtocol protocol, FlowDefinition flow, TimeProvider time, IDeliveryListener listener, ILogger<Verifier> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _protocol = protocol;
        _flow = flow;
        _time = time;
        _listener = listener;
        _logger = logger;
    }

    /// <summary>
    /// The target state a verify of <paramref name="record"/> takes: all of it for a protocol that finds records by it, and
    /// otherwise only what a hash of the owned content needs, which spares parsing it for every record of a large pass.
    /// </summary>
    private IReadOnlyDictionary<string, string>? StateFor(RecordState record)
        => _protocol.VerifiesWithTargetState ? JsonMerge.ToValues(record.TargetStateJson) : Protocols.OwnedContent.StateOf(record.TargetStateJson);

    /// <summary>
    /// Verifies up to <paramref name="max"/> delivered records (those not verified within <paramref name="notVerifiedWithin"/>,
    /// or the named <paramref name="keys"/>); with <paramref name="reconcile"/> each one found drifted or missing is marked
    /// for redelivery and named under <paramref name="activityId"/>, the activity that records the pass, when given.
    /// </summary>
    public async Task<VerifySummary> RunAsync(
        int max, TimeSpan? notVerifiedWithin, bool reconcile, IReadOnlyList<DeliveryKey>? keys = null, long? activityId = null, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var before = notVerifiedWithin is { } window ? now - window : (DateTime?)null;
        var records = await _ledger.ListForVerifyAsync(_flow.Id, before, max, ct).ConfigureAwait(false);
        if (keys is { Count: > 0 })
        {
            // A scoped verify (one record from its detail page, a handful from a listing) reads exactly those.
            var scoped = await _ledger.GetRecordsAsync(_flow.Id, keys, ct).ConfigureAwait(false);
            records = scoped.Values.Where(r => r.Status == RecordStatus.Delivered && r.TargetId is not null).ToList();
        }
        var matched = 0;
        var drifted = 0;
        var missing = 0;
        var errors = 0;
        var settled = 0;
        var progressGate = new Lock();
        var lastProgress = _time.GetUtcNow();

        using var gate = new SemaphoreSlim(Math.Max(1, _flow.Parallelism.Concurrency));

        // The protocol says how many records one read of the target covers; a storage target answers a hundred at a
        // time, so a pass over a large estate costs a handful of requests rather than one per record. A protocol
        // that cannot batch reports one and this chunks to one, which is the same walk as before.
        var chunks = records.Chunk(Math.Max(1, _protocol.MaxVerifyBatch)).ToList();
        _logger.LogInformation(
            "Verifying {Count} delivered record(s) against OSDU in {Reads} read(s) of up to {Batch}, {Concurrency} at a time{Reconcile}.",
            records.Count, chunks.Count, Math.Max(1, _protocol.MaxVerifyBatch), Math.Max(1, _flow.Parallelism.Concurrency),
            reconcile ? ", reconciling what drifted" : string.Empty);
        var tasks = chunks.Select(async chunk =>
        {
            // The gate covers the read and the ledger writes that follow it, so concurrency bounds what this pass
            // asks of both the target and the catalog, exactly as it did when the read was per record.
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyList<VerifyResult> results;
                try
                {
                    results = await _protocol.VerifyBatchAsync(
                        chunk.Select(r => new VerifyRequest(r.TargetId!, r.TargetVersion, StateFor(r))).ToList(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
                {
                    _logger.LogWarning(
                        "Verify of {Count} record(s) failed, the first being {SourceKey}: {Message}",
                        chunk.Length, chunk[0].SourceKey, HeaderRedaction.RedactMessage(ex.Message));
                    var failure = new VerifyResult(VerifyOutcome.Error, null, ex.Message);
                    results = Enumerable.Repeat(failure, chunk.Length).ToList();
                }

                for (var i = 0; i < chunk.Length; i++)
                {
                    await SettleAsync(chunk[i], results[i], reconcile).ConfigureAwait(false);
                }

                ReportProgress(Interlocked.Add(ref settled, chunk.Length));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var summary = new VerifySummary(records.Count, matched, drifted, missing, errors);
        _logger.LogInformation("Verify: {Summary}.", summary);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _time.GetUtcNow().UtcDateTime,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
            Kind = "verify.completed",
            Worker = "verify",
            Detail = summary.ToString() + (reconcile ? " (reconcile on)" : string.Empty),
        }, CancellationToken.None).ConfigureAwait(false);
        return summary;

        // How far the pass has got, at the pace of the run's trace (every progress interval outside a run), so a long
        // pass shows it is at work.
        void ReportProgress(int done)
        {
            var at = _time.GetUtcNow();
            if (Trace is { } trace)
            {
                if (!trace.ProgressDue("verify", at))
                {
                    return;
                }
            }
            else
            {
                lock (progressGate)
                {
                    if (at - lastProgress < RunTrace.ProgressInterval)
                    {
                        return;
                    }

                    lastProgress = at;
                }
            }

            _logger.LogInformation(
                RunTrace.Bounded,
                "Verify progress: {Done} of {Count} record(s) checked: {Matched} match, {Drifted} drifted, {Missing} missing, {Errors} could not be read.",
                done, records.Count, Volatile.Read(ref matched), Volatile.Read(ref drifted), Volatile.Read(ref missing), Volatile.Read(ref errors));
        }

        // What one record's result means for the counters, the ledger and the listener.
        async Task SettleAsync(RecordState record, VerifyResult result, bool reconciling)
        {
            switch (result.Outcome)
            {
                case VerifyOutcome.Match:
                    Interlocked.Increment(ref matched);
                    break;
                case VerifyOutcome.Drifted:
                    Interlocked.Increment(ref drifted);
                    _logger.LogWarning("Drift on {SourceKey} ({TargetId}): {Detail}", record.SourceKey, record.TargetId, result.Detail);
                    break;
                case VerifyOutcome.Missing:
                    Interlocked.Increment(ref missing);
                    _logger.LogWarning("Missing in OSDU: {SourceKey} ({TargetId})", record.SourceKey, record.TargetId);
                    break;
                default:
                    Interlocked.Increment(ref errors);
                    break;
            }

            if (result.Outcome != VerifyOutcome.Error)
            {
                await _ledger.RecordVerifyAsync(
                    _flow.Id, record.DeliveryKey, result.Outcome, result.ObservedVersion, _time.GetUtcNow().UtcDateTime, reconciling, activityId, CancellationToken.None).ConfigureAwait(false);
            }

            if (result.Outcome is VerifyOutcome.Drifted or VerifyOutcome.Missing)
            {
                await _listener.OnEventAsync(new DeliveryEvent
                {
                    AtUtc = _time.GetUtcNow().UtcDateTime,
                    FlowId = _flow.Id,
                    FlowName = _flow.Label,
                    Interface = _flow.Interface,
                    Kind = "verify.drifted",
                    SubmissionId = record.LastSubmissionId,
                    DeliveryKey = record.DeliveryKey,
                    SourceKey = record.SourceKey,
                    Label = record.Label,
                    TargetId = record.TargetId,
                    TargetVersion = result.ObservedVersion,
                    Worker = "verify",
                    Detail = (result.Outcome == VerifyOutcome.Missing ? "missing in OSDU" : result.Detail) + (reconciling ? "; redelivery requested for the next deliver run" : string.Empty),
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
