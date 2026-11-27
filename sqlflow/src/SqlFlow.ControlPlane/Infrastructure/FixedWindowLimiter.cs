using System.Threading.RateLimiting;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// A fixed window of permits that knows when its window ends, so a refused call is told how long until the next one is
/// taken. The framework's <see cref="FixedWindowRateLimiter"/> reports a whole window (or several) as its retry-after,
/// however much of the window is left, and keeps the window's start to itself; a client told to wait that long sits idle
/// for up to a window after the limit has lifted, and one told less (or guessing) is refused again.
/// </summary>
/// <remarks>
/// <para>A window opens with the first call after the previous one ended, and holds <c>permitLimit</c> permits until
/// <c>window</c> has passed since it opened; a call it cannot take is refused at once with the time left in the window
/// (<see cref="MetadataName.RetryAfter"/>). Calls are never queued, which is how every partition of the control plane's
/// limiter is configured (a queued call would hold a request open on a person's page).</para>
/// <para>Windows roll when a call arrives rather than on a timer, so the limiter holds no timer and needs no replenishing
/// from the partitioned limiter that owns it. It reports itself idle (<see cref="IdleDuration"/>) only once the window
/// its last permit was taken in has ended, from that end: the partitioned limiter retires a limiter idle for a while, and
/// one retired while its window was still open would hand its caller a fresh window early.</para>
/// <para>It holds nothing to release, so a call that reaches it after the partitioned limiter has retired and disposed it
/// is still answered (and counted in a window nobody else reads) rather than failed.</para>
/// </remarks>
public sealed class FixedWindowLimiter : RateLimiter
{
    private static readonly RateLimitLease Granted = new Lease(acquired: true, retryAfter: null);

    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <summary>When the current window opened, as a timestamp of <see cref="_clock"/>.</summary>
    private long _windowStart;
    private int _available;
    private long _successful;
    private long _failed;

    /// <summary>A limiter of <paramref name="permitLimit"/> calls per <paramref name="window"/>, read on <paramref name="clock"/>.</summary>
    public FixedWindowLimiter(int permitLimit, TimeSpan window, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(clock);
        _permitLimit = permitLimit;
        _window = window;
        _clock = clock;
        _windowStart = clock.GetTimestamp();
        _available = permitLimit;
    }

    /// <inheritdoc />
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_gate)
            {
                var sinceEnd = _clock.GetElapsedTime(_windowStart) - _window;
                return sinceEnd >= TimeSpan.Zero ? sinceEnd : null;
            }
        }
    }

    /// <inheritdoc />
    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_gate)
        {
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _clock.GetElapsedTime(_windowStart) >= _window ? _permitLimit : _available,
                CurrentQueuedCount = 0,
                TotalFailedLeases = _failed,
                TotalSuccessfulLeases = _successful,
            };
        }
    }

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            var elapsed = _clock.GetElapsedTime(_windowStart, now);
            if (elapsed >= _window)
            {
                _windowStart = now;
                _available = _permitLimit;
                elapsed = TimeSpan.Zero;
            }

            // Asking for no permits asks whether one is left, and takes nothing (RateLimiter's contract).
            if (permitCount == 0 ? _available > 0 : _available >= permitCount)
            {
                _available -= permitCount;
                _successful++;
                return Granted;
            }

            _failed++;
            return new Lease(acquired: false, retryAfter: _window - elapsed);
        }
    }

    /// <inheritdoc />
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        // Nothing waits: a call the window cannot take is refused at once, as AttemptAcquire refuses it.
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<RateLimitLease>(cancellationToken)
            : ValueTask.FromResult(AttemptAcquireCore(permitCount));
    }

    /// <summary>A permit, or a refusal with how long until the window that refused it ends.</summary>
    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired { get; } = acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } wait && string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = wait;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
