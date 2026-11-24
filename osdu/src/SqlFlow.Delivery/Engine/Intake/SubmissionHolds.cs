using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Intake;

/// <summary>
/// The submissions one runtime works on, each held while it does
/// (<see cref="ILedgerSubmissions.HoldSubmissionAsync"/>), so a later run of the flow can tell a submission somebody
/// works on from one whose run has ended. A submission is held once however often the runtime asks for it. The
/// operation that took a hold releases it when it ends; the runtime releases whatever is left when it is disposed, and
/// a process that stops frees every hold it had with its connections.
/// </summary>
public sealed class SubmissionHolds : IAsyncDisposable, IDisposable
{
    private readonly ILedger _ledger;
    private readonly Dictionary<Guid, SubmissionHold> _held = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SubmissionHolds(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <summary>
    /// Holds <paramref name="submissionId"/> for this runtime. Returns true when this call took the hold, false when the
    /// runtime already held it.
    /// </summary>
    public async Task<bool> HoldAsync(Guid submissionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_held.ContainsKey(submissionId))
            {
                return false;
            }

            _held[submissionId] = await _ledger.HoldSubmissionAsync(submissionId, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lets go of <paramref name="submissionId"/>, when the runtime holds it.</summary>
    public async Task ReleaseAsync(Guid submissionId)
    {
        SubmissionHold? hold;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_held.Remove(submissionId, out hold))
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        await hold.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Lets go of every submission the runtime holds.</summary>
    public async Task ReleaseAllAsync()
    {
        List<SubmissionHold> holds;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            holds = [.. _held.Values];
            _held.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var hold in holds)
        {
            await hold.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await ReleaseAllAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var hold in _held.Values)
        {
            hold.Dispose();
        }

        _held.Clear();
        _gate.Dispose();
    }
}
