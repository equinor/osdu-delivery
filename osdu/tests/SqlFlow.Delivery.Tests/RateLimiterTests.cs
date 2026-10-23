using SqlFlow.Delivery.Http;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The rate a flow declares (reliability.rateLimitRps) and how the runs of a fan-out divide it: each run's limiter lets
/// through its share, so the fan-out as a whole sends no more than the flow declares, and a run alone again takes the
/// whole rate back.
/// </summary>
public sealed class RateLimiterTests
{
    [Fact]
    public void A_shared_limiter_lets_through_its_share_of_the_declared_rate_and_takes_the_whole_rate_back()
    {
        var limiter = new RateLimiter(10, new TestClock());
        Assert.Equal(10, limiter.Rate);

        limiter.Share(4);
        Assert.Equal(2.5, limiter.Rate);

        limiter.Share(1);
        Assert.Equal(10, limiter.Rate);
    }

    [Fact]
    public async Task A_share_of_the_rate_spaces_requests_as_the_share_says()
    {
        var clock = new TestClock();
        var limiter = new RateLimiter(10, clock);
        limiter.Share(4);

        // The bucket is the share's: two and a half tokens, two of them taken at once.
        await limiter.AcquireAsync();
        await limiter.AcquireAsync();
        var third = limiter.AcquireAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(third.IsCompleted);

        // Half a token was left; at two and a half a second the other half takes a fifth of a second.
        clock.Advance(TimeSpan.FromSeconds(0.25));
        await third.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_unlimited_rate_stays_unlimited_however_many_runs_share_it()
    {
        var limiter = new RateLimiter(0, new TestClock());
        limiter.Share(9);

        Assert.Equal(0, limiter.Rate);
        for (var i = 0; i < 100; i++)
        {
            var acquired = limiter.AcquireAsync();
            Assert.True(acquired.IsCompletedSuccessfully);
            await acquired;
        }
    }

    [Fact]
    public void No_fewer_than_one_run_shares_a_rate()
    {
        var limiter = new RateLimiter(10, new TestClock());
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.Share(0));
        Assert.Equal(10, limiter.Rate);
    }
}
