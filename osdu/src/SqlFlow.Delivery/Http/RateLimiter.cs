// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/RateLimiter.cs. Namespace changed; Share added, so the runs of a fan-out divide one rate.
namespace SqlFlow.Delivery.Http;

/// <summary>
/// A token-bucket rate limiter: capacity tokens refilled continuously at rate tokens/second, one token per request.
/// A non-positive rate is unlimited (a no-op). Uses the injected <see cref="TimeProvider"/> so tests run under a
/// paused clock. Thread-safe for the worker's bounded concurrency. The runs a delivery fans out to each hold a limiter of
/// their own, so each keeps to its <see cref="Share"/> of the flow's rate and together they keep to the rate itself.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim's wait handle is never accessed, so it holds no unmanaged resource to release.")]
public sealed class RateLimiter
{
    /// <summary>The rate the flow declares, which <see cref="Share"/> divides.</summary>
    private readonly double _declared;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private double _rate;
    private double _capacity;
    private double _tokens;
    private long _lastRefillTimestamp;

    public RateLimiter(double ratePerSecond, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _declared = ratePerSecond;
        _rate = ratePerSecond;
        _capacity = Math.Max(ratePerSecond, 1.0);
        _time = time;
        _tokens = _capacity;
        _lastRefillTimestamp = time.GetTimestamp();
    }

    /// <summary>The requests per second this limiter lets through now: its share of the declared rate; 0 is unlimited.</summary>
    public double Rate
    {
        get
        {
            _gate.Wait();
            try
            {
                return _rate;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Lets this limiter through <paramref name="runs"/>'s share of the declared rate from now on: the runs of one fan-out,
    /// each holding such a limiter, then send no more than the flow declares between them. 1 takes the whole rate back. An
    /// unlimited limiter stays unlimited, and the tokens already saved never exceed the smaller bucket.
    /// </summary>
    public void Share(int runs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);
        if (_declared <= 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            Refill();
            _rate = _declared / runs;
            _capacity = Math.Max(_rate, 1.0);
            _tokens = Math.Min(_tokens, _capacity);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Blocks until a token is available, then consumes it. Returns immediately when unlimited.</summary>
    public async Task AcquireAsync(CancellationToken ct = default)
    {
        // The declared rate decides whether the limiter limits at all: a share of a limit is a limit.
        if (_declared <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                wait = TimeSpan.FromSeconds((1.0 - _tokens) / _rate);
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefillTimestamp, now);
        _lastRefillTimestamp = now;
        _tokens = Math.Min(_capacity, _tokens + (elapsed.TotalSeconds * _rate));
    }
}
