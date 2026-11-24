using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The module's database as the ledger and the stores it holds reach it: a tracking context for a write, a read retried when
/// the database chose it as a deadlock's victim, a statement retried the same way, and the clock the rows are stamped with.
/// <see cref="OsduLedger"/> makes one and hands it to each store it holds, so they read and write as one ledger.
/// </summary>
internal sealed class LedgerDatabase
{
    /// <summary>How many times a claim or lease statement runs when the database ends a deadlock by rolling it back.</summary>
    private const int DeadlockAttempts = 5;

    /// <summary>How many times a read the database chose as a deadlock victim is made before the deadlock is reported.</summary>
    private const int ReadDeadlockAttempts = 5;

    private readonly Func<OsduDbContext> _factory;
    private readonly TimeProvider _time;

    /// <param name="factory">Makes a context on the module's database.</param>
    /// <param name="time">The clock.</param>
    public LedgerDatabase(Func<OsduDbContext> factory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _time = time;
    }

    /// <summary>Now, in UTC, by the ledger's clock.</summary>
    public DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A tracking context: the host may pool no-tracking contexts (the control plane does), and the ledger's read-modify-write
    /// operations rely on tracking.
    /// </summary>
    public OsduDbContext Open()
    {
        var db = _factory();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return db;
    }

    /// <summary>
    /// Opens a context for one read, untracked. A read takes no transaction of its own: while a worker delivers it
    /// writes its lease row and the append-only event and attempt tables, never the record table, which changes only
    /// when a lease is claimed, checkpointed, closed or recovered, a thousand rows to a short transaction. A read that
    /// needs a record and its lease together asks for both in one statement (the ledger's leased reads), so nothing
    /// here depends on the database allowing snapshot isolation.
    /// </summary>
    /// <remarks>
    /// Without snapshot isolation a read holds shared locks while its statement runs, so a writer taking the same rows in
    /// another order (a claim or a recovery on another node) can make it the victim of a deadlock. A read changes nothing,
    /// so it is read again, as a write the database rolled back is written again (<see cref="SqlServerLedgerBulk.IsContention"/>).
    /// </remarks>
    public async Task<T> ReadAsync<T>(Func<OsduDbContext, Task<T>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = Open();
                db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                return await read(db).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < ReadDeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                // Waiting a moment, longer each time and never the same for two readers, keeps the read from meeting the
                // same writer the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/>, and again when the database ended a deadlock by rolling it back. Only for a read, or
    /// for one statement whose second run does what the first would have: a claim, a lease, a release.
    /// </summary>
    public static async Task<T> RetryDeadlockAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                // Waiting a moment, longer each time and never the same for two writers, keeps the two from meeting in
                // the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
