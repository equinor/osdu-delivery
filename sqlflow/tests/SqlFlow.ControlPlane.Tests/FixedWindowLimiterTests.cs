using System.Threading.RateLimiting;
using SqlFlow.ControlPlane.Infrastructure;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The control plane's rate limit window (<see cref="FixedWindowLimiter"/>): it takes its permits, refuses the next call with
/// exactly the time its window has left, rolls when the window has ended, and reports itself idle only once nothing it
/// counted can still refuse a call. Read on a clock the tests move by hand.
/// </summary>
public sealed class FixedWindowLimiterTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    /// <summary>A clock moved by hand, whose timestamps are ticks.</summary>
    private sealed class HandClock : TimeProvider
    {
        private long _ticks = TimeSpan.FromDays(1).Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public void A_window_takes_its_permits_and_refuses_the_next_call_with_the_time_left_in_it()
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(3, Minute, clock);

        for (var call = 0; call < 3; call++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }

        clock.Advance(TimeSpan.FromSeconds(15));
        using var refused = limiter.AttemptAcquire();

        Assert.False(refused.IsAcquired);
        Assert.True(refused.TryGetMetadata(MetadataName.RetryAfter, out var wait));
        Assert.Equal(TimeSpan.FromSeconds(45), wait);
        Assert.Equal(45, RateLimitRejection.RetryAfterSeconds(refused));
        Assert.Equal([MetadataName.RetryAfter.Name], refused.MetadataNames);
    }

    [Fact]
    public void Once_its_window_has_ended_the_next_call_opens_a_fresh_one()
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(2, Minute, clock);
        limiter.AttemptAcquire(2).Dispose();
        clock.Advance(TimeSpan.FromSeconds(50));
        using (var refused = limiter.AttemptAcquire())
        {
            Assert.False(refused.IsAcquired);
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        using (var fresh = limiter.AttemptAcquire())
        {
            Assert.True(fresh.IsAcquired);
        }

        // The fresh window opened with that call: it holds one permit more, and refuses with a whole window left.
        Assert.Equal(1, limiter.GetStatistics()!.CurrentAvailablePermits);
        limiter.AttemptAcquire().Dispose();
        using var again = limiter.AttemptAcquire();
        Assert.False(again.IsAcquired);
        Assert.Equal(60, RateLimitRejection.RetryAfterSeconds(again));
    }

    [Theory]
    [InlineData(59.5, 1)]
    [InlineData(59.999, 1)]
    [InlineData(15.8, 45)]
    [InlineData(0.0, 60)]
    public void The_wait_a_refusal_states_is_rounded_up_to_whole_seconds_and_is_never_zero(double secondsIn, int stated)
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(1, Minute, clock);
        limiter.AttemptAcquire().Dispose();
        clock.Advance(TimeSpan.FromSeconds(secondsIn));

        using var refused = limiter.AttemptAcquire();

        Assert.Equal(stated, RateLimitRejection.RetryAfterSeconds(refused));
    }

    [Fact]
    public void It_is_idle_only_once_the_window_its_last_permit_was_taken_in_has_ended()
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(5, Minute, clock);
        limiter.AttemptAcquire().Dispose();

        // Retired now, the caller would get a fresh window early: still counting, so not idle.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(limiter.IdleDuration);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.Zero, limiter.IdleDuration);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(15), limiter.IdleDuration);

        limiter.AttemptAcquire().Dispose();
        Assert.Null(limiter.IdleDuration);
    }

    [Fact]
    public void Asking_for_no_permits_asks_whether_one_is_left_and_takes_nothing()
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(1, Minute, clock);

        using (var probe = limiter.AttemptAcquire(0))
        {
            Assert.True(probe.IsAcquired);
        }

        Assert.Equal(1, limiter.GetStatistics()!.CurrentAvailablePermits);
        limiter.AttemptAcquire().Dispose();
        using var none = limiter.AttemptAcquire(0);
        Assert.False(none.IsAcquired);
    }

    [Fact]
    public void A_call_for_more_permits_than_a_window_holds_is_refused_as_a_mistake()
    {
        using var limiter = new FixedWindowLimiter(3, Minute, new HandClock());

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedWindowLimiter(0, Minute, new HandClock()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedWindowLimiter(1, TimeSpan.Zero, new HandClock()));
    }

    [Fact]
    public async Task Waiting_for_a_permit_waits_for_nothing_and_a_cancelled_wait_is_cancelled()
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(1, Minute, clock);

        using (var granted = await limiter.AcquireAsync())
        {
            Assert.True(granted.IsAcquired);
        }

        using (var refused = await limiter.AcquireAsync())
        {
            Assert.False(refused.IsAcquired);
            Assert.Equal(60, RateLimitRejection.RetryAfterSeconds(refused));
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await limiter.AcquireAsync(1, cancelled.Token));
    }

    [Fact]
    public async Task Calls_made_at_once_from_many_threads_never_take_more_than_a_window_holds()
    {
        var clock = new HandClock();
        using var limiter = new FixedWindowLimiter(100, Minute, clock);
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var call = 0; call < 50; call++)
            {
                using var lease = limiter.AttemptAcquire();
                if (lease.IsAcquired)
                {
                    Interlocked.Increment(ref granted);
                }
            }
        })));

        Assert.Equal(100, granted);
        var statistics = limiter.GetStatistics()!;
        Assert.Equal((0L, 100L, 300L), (statistics.CurrentAvailablePermits, statistics.TotalSuccessfulLeases, statistics.TotalFailedLeases));
    }

    [Fact]
    public void Each_caller_of_a_partitioned_limiter_has_a_window_of_its_own()
    {
        var clock = new HandClock();
        using var limiter = PartitionedRateLimiter.Create<string, string>(caller =>
            RateLimitPartition.Get(caller, _ => new FixedWindowLimiter(1, Minute, clock)));

        using (var first = limiter.AttemptAcquire("ada"))
        {
            Assert.True(first.IsAcquired);
        }

        using (var other = limiter.AttemptAcquire("grace"))
        {
            Assert.True(other.IsAcquired);
        }

        using var refused = limiter.AttemptAcquire("ada");
        Assert.False(refused.IsAcquired);
        Assert.Equal(60, RateLimitRejection.RetryAfterSeconds(refused));
    }

    [Fact]
    public void A_refusal_that_states_no_wait_carries_no_retry_after()
    {
        using var lease = new FailedWithoutWait();

        Assert.Null(RateLimitRejection.RetryAfterSeconds(lease));
    }

    private sealed class FailedWithoutWait : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
