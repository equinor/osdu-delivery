using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The steps of a dimension build's write, in the order it takes them (osdu/docs/reference/flow/dimension-build.md, The
/// write): what earlier builds removed is deleted, what the build staged is merged into the ledger, and the dimension's table
/// is brought to it.
/// </summary>
public enum DimensionWriteStep
{
    /// <summary>The keys and values earlier builds removed are deleted, a few keys a transaction.</summary>
    Purge,

    /// <summary>What the build staged is merged into the ledger in one transaction.</summary>
    Merge,

    /// <summary>The dimension's table is brought to what the merge kept, where the flow's target keeps it.</summary>
    Table,
}

/// <summary>
/// Hears a dimension's write as it goes, so a build's trace tells a write that is working from one waiting for another
/// build's lock and from one that is stuck: each step's start and end with its counts, a wait for the dimension's write
/// lock, and, while a step runs long, what its statement is doing.
/// </summary>
public interface IDimensionWriteListener
{
    /// <summary>The step began; <paramref name="what"/> says what it works on.</summary>
    void Started(DimensionWriteStep writeStep, string what);

    /// <summary>Another build of the dimension holds its write lock, and the step waits for it.</summary>
    void Waiting(DimensionWriteStep writeStep);

    /// <summary>The step took the write lock after waiting <paramref name="waited"/> for it.</summary>
    void Locked(DimensionWriteStep writeStep, TimeSpan waited);

    /// <summary>
    /// The step is still running after <paramref name="elapsed"/>; <paramref name="state"/> says what its statement is doing,
    /// as the database reports it, or is null when the database does not say.
    /// </summary>
    void Running(DimensionWriteStep writeStep, TimeSpan elapsed, string? state);

    /// <summary>The step ended after <paramref name="took"/>; <paramref name="what"/> says what it did.</summary>
    void Ended(DimensionWriteStep writeStep, TimeSpan took, string what);
}

/// <summary>
/// Times the steps of one dimension write, tells its listener of each, and keeps what each took for the build's notes. While
/// a step runs it asks the database, at first after a minute and then at doubling intervals up to ten minutes, what the
/// write's session is doing, so a long step leaves a few lines rather than one a minute.
/// </summary>
internal sealed class DimensionWriteClock
{
    /// <summary>When a running step is first reported, unless the clock is given another time.</summary>
    internal static readonly TimeSpan FirstReport = TimeSpan.FromMinutes(1);

    /// <summary>The longest interval between two reports of a running step.</summary>
    private static readonly TimeSpan LastInterval = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _firstReport;

    private readonly IDimensionWriteListener? _listener;
    private readonly Func<int, CancellationToken, Task<string?>>? _probe;
    private readonly List<string> _taken = [];
    private readonly Stopwatch _whole = Stopwatch.StartNew();

    /// <param name="listener">Who hears the write; null hears nothing, and the clock still keeps the notes.</param>
    /// <param name="probe">Asks the database what a session is doing; null asks nothing.</param>
    /// <param name="firstReport">When a running step is first reported (<see cref="FirstReport"/> when null).</param>
    public DimensionWriteClock(IDimensionWriteListener? listener, Func<int, CancellationToken, Task<string?>>? probe, TimeSpan? firstReport = null)
    {
        _listener = listener;
        _probe = probe;
        _firstReport = firstReport ?? FirstReport;
    }

    /// <summary>The server process id of the session the write's statements run on, once the write has opened it; 0 before.</summary>
    public int Session
    {
        get => Volatile.Read(ref _session);
        set => Volatile.Write(ref _session, value);
    }

    private int _session;

    /// <summary>
    /// Runs <paramref name="step"/>'s work, telling the listener it began and ended and, while it runs long, what the write's
    /// session (<see cref="Session"/>) is doing; <paramref name="ended"/> says what the work did once it has done it.
    /// </summary>
    public async Task<T> TimeAsync<T>(DimensionWriteStep step, string what, Func<Task<T>> work, Func<T, string> ended, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(ended);
        _listener?.Started(step, what);
        var watch = Stopwatch.StartNew();
        using var reporting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reports = _listener is null ? Task.CompletedTask : ReportAsync(step, watch, reporting.Token);
        T result;
        try
        {
            result = await work().ConfigureAwait(false);
        }
        finally
        {
            await reporting.CancelAsync().ConfigureAwait(false);
            await reports.ConfigureAwait(false);
        }

        var took = watch.Elapsed;
        var done = ended(result);
        lock (_taken)
        {
            _taken.Add(string.Create(CultureInfo.InvariantCulture, $"{Named(step)} {Seconds(took)} ({done})"));
        }

        _listener?.Ended(step, took, done);
        return result;
    }

    /// <summary>
    /// Takes the dimension's write lock for <paramref name="step"/> through <paramref name="take"/> (given false, it tries
    /// without waiting; given true, it waits up to the lock's timeout): at once when no other build holds it, else telling
    /// the listener the step waits, and then how long it waited. Answers what the database answered the last try.
    /// </summary>
    public async Task<int> LockAsync(DimensionWriteStep step, Func<bool, Task<int>> take)
    {
        ArgumentNullException.ThrowIfNull(take);
        var granted = await take(false).ConfigureAwait(false);
        if (granted >= 0)
        {
            return granted;
        }

        _listener?.Waiting(step);
        var watch = Stopwatch.StartNew();
        granted = await take(true).ConfigureAwait(false);
        var waited = watch.Elapsed;
        lock (_taken)
        {
            _taken.Add(string.Create(CultureInfo.InvariantCulture, $"waited {Seconds(waited)} for the write lock before {Named(step)}"));
        }

        if (granted >= 0)
        {
            _listener?.Locked(step, waited);
        }

        return granted;
    }

    /// <summary>The note the build keeps of its write: how long it took in all, and each step with what it did.</summary>
    public string Note()
    {
        lock (_taken)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Written in {Seconds(_whole.Elapsed)}: {string.Join("; ", _taken)}.");
        }
    }

    private async Task ReportAsync(DimensionWriteStep step, Stopwatch watch, CancellationToken ct)
    {
        var interval = _firstReport;
        try
        {
            while (true)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                string? state = null;
                if (_probe is not null && Session is > 0 and var session)
                {
                    try
                    {
                        state = await _probe(session, ct).ConfigureAwait(false);
                    }
                    catch (SqlException)
                    {
                        // A database that does not let the module see its own sessions says nothing of what the step does;
                        // the report still says the step runs, and for how long.
                        state = null;
                    }
                }

                _listener!.Running(step, watch.Elapsed, state);
                interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, LastInterval.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The step ended, or the write was cancelled: nothing more to report.
        }
    }

    /// <summary>What a step is called in a note: the words an operator reads in the trace.</summary>
    internal static string Named(DimensionWriteStep step) => step switch
    {
        DimensionWriteStep.Purge => "deleting what earlier builds removed",
        DimensionWriteStep.Merge => "merging",
        DimensionWriteStep.Table => "writing its table",
        _ => step.ToString(),
    };

    internal static string Seconds(TimeSpan span) => string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.0} s");

    /// <summary>
    /// What session <paramref name="session"/>'s statement is doing, as the database reports it: waiting for a lock another
    /// session holds, or working, with the wait it is in and the pages it has read and written; null when no statement of the
    /// session runs, or the database shows none to the module.
    /// </summary>
    internal static async Task<string?> SessionStateAsync(SqlConnection connection, int session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) r.[command], r.[wait_type], r.[wait_time], r.[blocking_session_id], r.[logical_reads], r.[writes]
            FROM sys.dm_exec_requests AS r
            WHERE r.[session_id] = @session;
            """,
            connection) { CommandTimeout = 30 };
        command.Parameters.Add(new SqlParameter("@session", System.Data.SqlDbType.Int) { Value = session });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var statement = reader.GetString(0).Trim();
        var wait = await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1);
        var waited = TimeSpan.FromMilliseconds(reader.GetInt32(2));
        var blocker = reader.GetInt16(3);
        var reads = reader.GetInt64(4);
        var writes = reader.GetInt64(5);
        if (blocker > 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"its {statement} waits for a lock session {blocker} holds ({wait}, {Seconds(waited)} so far)");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"its {statement} is working{(wait is null ? " on the processor" : $", waiting on {wait}")}, {reads:N0} page(s) read and {writes:N0} written so far");
    }
}
