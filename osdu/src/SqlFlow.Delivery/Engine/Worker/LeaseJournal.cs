using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>
/// What a worker learns under one lease (steps that completed, tries that ended), appended to the ledger with group
/// commit: whatever the worker's concurrent deliveries hand over while one write is in flight goes into the next write,
/// so a node sending many records at once writes many of them together. Each caller's task completes once its entry is
/// written, so a step is in the ledger before the delivery that reported it goes on, and a completion's event reaches
/// the listener only after its attempt is stored.
/// </summary>
internal sealed class LeaseJournal
{
    /// <summary>The most entries one write carries, so the rows it adds stay well under a lock on the whole table.</summary>
    public const int MaxEntriesPerWrite = 500;

    private readonly ILedgerLeases _ledger;
    private readonly Guid _flowId;
    private readonly string _token;
    private readonly IDeliveryListener _listener;
    private readonly object _gate = new();
    private readonly Dictionary<RecordStatus, long> _written = [];
    private List<Entry> _queued = [];
    private bool _writing;
    private long _unchanged;

    public LeaseJournal(ILedgerLeases ledger, Guid flowId, string token, IDeliveryListener listener)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(listener);
        _ledger = ledger;
        _flowId = flowId;
        _token = token;
        _listener = listener;
    }

    /// <summary>How many tries the journal has written, by the status each settled its record in, and how many of the delivered sent nothing.</summary>
    public (IReadOnlyDictionary<RecordStatus, long> ByStatus, long Unchanged) Written
    {
        get
        {
            lock (_gate)
            {
                return (new Dictionary<RecordStatus, long>(_written), _unchanged);
            }
        }
    }

    /// <summary>Appends a completed step, with the artifacts it reports; the task completes once it is written.</summary>
    public Task StepAsync(RecordStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Enqueue(new Entry(step, null, null, null));
    }

    /// <summary>Appends how a try ended, with its event for the listener; the task completes once both are out.</summary>
    public Task OutcomeAsync(RecordCompletion completion, DeliveryEvent evt)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(evt);
        return Enqueue(new Entry(null, completion, evt, null));
    }

    /// <summary>
    /// Appends an undo of a record's aborted units (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished
    /// deliveries and their undo): its attempt and what it settled each artifact as, written after the completions of
    /// the same write, so a held try's completion makes its artifacts due before the undo settles them. The task
    /// completes once it is written.
    /// </summary>
    public Task UndoAsync(RecordUndo undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        return Enqueue(new Entry(null, null, null, undo));
    }

    private Task Enqueue(Entry entry)
    {
        bool start;
        lock (_gate)
        {
            _queued.Add(entry);
            start = !_writing;
            _writing = true;
        }

        if (start)
        {
            // The writer runs apart from the caller, so the caller never writes other callers' entries on its own stack.
            _ = Task.Run(WriteQueuedAsync, CancellationToken.None);
        }

        return entry.Written.Task;
    }

    /// <summary>Writes whatever is queued, again and again, until the queue is empty. Every failure goes to the entries it concerned.</summary>
    private async Task WriteQueuedAsync()
    {
        while (true)
        {
            List<Entry> batch;
            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    _writing = false;
                    return;
                }

                if (_queued.Count <= MaxEntriesPerWrite)
                {
                    batch = _queued;
                    _queued = [];
                }
                else
                {
                    batch = _queued.GetRange(0, MaxEntriesPerWrite);
                    _queued.RemoveRange(0, MaxEntriesPerWrite);
                }
            }

            try
            {
                var steps = batch.Where(e => e.Step is not null).Select(e => e.Step!).ToList();
                var completions = batch.Where(e => e.Completion is not null).Select(e => e.Completion!).ToList();
                var undos = batch.Where(e => e.Undo is not null).Select(e => e.Undo!).ToList();
                await _ledger.AppendAsync(_flowId, _token, new LeaseAppend(steps, completions) { Undos = undos }, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var completion in completions)
                    {
                        _written[completion.Status] = _written.GetValueOrDefault(completion.Status) + 1;
                        if (completion.NothingSent)
                        {
                            _unchanged++;
                        }
                    }
                }

                // The completion callback, after the rows are written, as a listener expects.
                foreach (var entry in batch)
                {
                    if (entry.Event is { } evt)
                    {
                        await _listener.OnEventAsync(evt, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                foreach (var entry in batch)
                {
                    entry.Written.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                foreach (var entry in batch)
                {
                    entry.Written.TrySetException(ex);
                }
            }
        }
    }

    private sealed record Entry(RecordStep? Step, RecordCompletion? Completion, DeliveryEvent? Event, RecordUndo? Undo)
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
