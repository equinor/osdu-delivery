using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension write tells its listener each step as it takes it (osdu/docs/reference/flow/dimension-build.md, What the trace
/// says of a write): the step's start and end with what it did, a wait for another build's write lock, and, while a step runs
/// long, what its statement is doing; and it keeps what each step took for the build's notes.
/// </summary>
public sealed class DimensionWriteClockTests
{
    [Fact]
    public async Task Each_step_is_told_as_it_starts_and_ends_and_the_note_keeps_what_it_did()
    {
        var heard = new Heard();
        var clock = new DimensionWriteClock(heard, null);

        var deleted = await clock.TimeAsync(DimensionWriteStep.Purge, "the keys and values earlier builds removed", () => Task.FromResult(3), n => $"{n} key(s) deleted", CancellationToken.None);

        Assert.Equal(3, deleted);
        Assert.Equal(
            ["started deleting what earlier builds removed: the keys and values earlier builds removed", "ended deleting what earlier builds removed: 3 key(s) deleted"],
            heard.Lines);
        Assert.Matches(@"^Written in \d+\.\d s: deleting what earlier builds removed \d+\.\d s \(3 key\(s\) deleted\)\.$", clock.Note());
    }

    [Fact]
    public async Task A_lock_taken_at_once_is_not_told_and_one_waited_for_is_told_and_kept()
    {
        var heard = new Heard();
        var clock = new DimensionWriteClock(heard, null);

        Assert.Equal(0, await clock.LockAsync(DimensionWriteStep.Merge, wait => Task.FromResult(0)));
        Assert.Empty(heard.Lines);

        var asked = new List<bool>();
        Assert.Equal(1, await clock.LockAsync(DimensionWriteStep.Table, wait =>
        {
            asked.Add(wait);
            return Task.FromResult(wait ? 1 : -1);
        }));

        // It tried without waiting, then waited, telling the listener both.
        Assert.Equal([false, true], asked);
        Assert.Equal(["waiting writing its table", "locked writing its table"], heard.Lines);
        Assert.Contains("waited ", clock.Note(), StringComparison.Ordinal);
        Assert.Contains(" for the write lock before writing its table", clock.Note(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_running_long_is_told_with_what_its_statement_is_doing()
    {
        var heard = new Heard();
        var asked = new List<int>();
        var clock = new DimensionWriteClock(heard, (session, _) =>
        {
            lock (asked)
            {
                asked.Add(session);
            }

            return Task.FromResult<string?>("its INSERT is working, waiting on PAGEIOLATCH_SH");
        }, TimeSpan.FromMilliseconds(20))
        {
            Session = 57,
        };

        await clock.TimeAsync(DimensionWriteStep.Merge, "10 key(s)", async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            return 0;
        }, _ => "done", CancellationToken.None);

        // At least the first report, at 20 ms, and the next at 60 ms, came before the step ended at 150 ms; none after.
        var running = heard.Lines.Where(l => l.StartsWith("running merging", StringComparison.Ordinal)).ToList();
        Assert.True(running.Count >= 2, string.Join(" / ", heard.Lines));
        Assert.All(running, l => Assert.EndsWith(": its INSERT is working, waiting on PAGEIOLATCH_SH", l, StringComparison.Ordinal));
        Assert.All(asked, s => Assert.Equal(57, s));
        Assert.Equal("ended merging: done", heard.Lines[^1]);
    }

    [Fact]
    public async Task A_step_that_fails_is_not_told_as_ended_and_its_reports_stop()
    {
        var heard = new Heard();
        var clock = new DimensionWriteClock(heard, null, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<InvalidOperationException>(() => clock.TimeAsync<int>(
            DimensionWriteStep.Table, "dim_X", () => throw new InvalidOperationException("the table is gone"), _ => "done", CancellationToken.None));
        var told = heard.Lines.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(80));

        // A machine busy enough to stall the step for the first report's 20 ms may hear that the step runs before it
        // fails; what never comes is its end, or a report once it has failed.
        Assert.Equal("started writing its table: dim_X", heard.Lines[0]);
        Assert.All(heard.Lines.Skip(1), l => Assert.StartsWith("running writing its table", l, StringComparison.Ordinal));
        Assert.DoesNotContain(heard.Lines, l => l.StartsWith("ended", StringComparison.Ordinal));
        Assert.Equal(told, heard.Lines.Count);
    }

    /// <summary>What a listener was told, a line a call.</summary>
    private sealed class Heard : IDimensionWriteListener
    {
        private readonly Lock _gate = new();

        public List<string> Lines { get; } = [];

        public void Started(DimensionWriteStep writeStep, string what) => Add($"started {DimensionWriteClock.Named(writeStep)}: {what}");

        public void Waiting(DimensionWriteStep writeStep) => Add($"waiting {DimensionWriteClock.Named(writeStep)}");

        public void Locked(DimensionWriteStep writeStep, TimeSpan waited) => Add($"locked {DimensionWriteClock.Named(writeStep)}");

        public void Running(DimensionWriteStep writeStep, TimeSpan elapsed, string? state) => Add($"running {DimensionWriteClock.Named(writeStep)}: {state}");

        public void Ended(DimensionWriteStep writeStep, TimeSpan took, string what) => Add($"ended {DimensionWriteClock.Named(writeStep)}: {what}");

        private void Add(string line)
        {
            lock (_gate)
            {
                Lines.Add(line);
            }
        }
    }
}
