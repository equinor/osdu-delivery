using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What the ledger and each store beside it stand on (osdu/docs/reference/concepts/ledger.md): one module database
/// (<see cref="LedgerDatabase"/>) and one directory of partitions (<see cref="LedgerDirectory"/>), shared by
/// <see cref="OsduLedger"/> and every store it makes, so they read and write as one ledger. A store keeps tables of its own
/// that hold no delivered record (downloads, assertion runs, inventories, dimensions), so a change to it changes that
/// store alone and not <see cref="ILedger"/>.
/// </summary>
/// <remarks>
/// The directory is shared rather than each store resolving partitions itself: it caches each ledger's partition, and a
/// move forgets the identity it moved from (<see cref="LedgerDirectory.Forget"/>). A store with a directory of its own would
/// go on writing under that identity after the move removed it.
/// </remarks>
public abstract class LedgerStore
{
    /// <summary>Item ids or set ids looked up per statement, well inside the parameter ceiling of one command.</summary>
    private protected const int LookupChunk = 500;

    private protected LedgerStore(LedgerDatabase database, LedgerDirectory partitions)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(partitions);
        Database = database;
        Partitions = partitions;
    }

    /// <summary>The module's database as the ledger and its stores reach it.</summary>
    private protected LedgerDatabase Database { get; }

    /// <summary>The ledger's directory of partitions, the one every store shares.</summary>
    private protected LedgerDirectory Partitions { get; }

    /// <summary>Now, in UTC, by the ledger's clock.</summary>
    private protected DateTime Now => Database.Now;

    /// <summary>A tracking context (<see cref="LedgerDatabase.Open"/>).</summary>
    private protected OsduDbContext Open() => Database.Open();

    /// <summary>A read through a context of its own, untracked and read again past a deadlock (<see cref="LedgerDatabase.ReadAsync{T}"/>).</summary>
    private protected Task<T> ReadAsync<T>(Func<OsduDbContext, Task<T>> read, CancellationToken ct) => Database.ReadAsync(read, ct);

    /// <summary>Runs <paramref name="work"/>, and again when the database ended a deadlock by rolling it back (<see cref="LedgerDatabase.RetryDeadlockAsync{T}"/>).</summary>
    private protected static Task<T> RetryDeadlockAsync<T>(Func<Task<T>> work, CancellationToken ct) => LedgerDatabase.RetryDeadlockAsync(work, ct);

    /// <summary>The partition number <paramref name="flowId"/>'s rows are kept under (<see cref="LedgerDirectory.PartitionOfAsync"/>).</summary>
    private protected ValueTask<short?> PartitionOfAsync(Guid flowId, CancellationToken ct) => Partitions.PartitionOfAsync(flowId, ct);

    /// <summary>The partition number every row a write adds to <paramref name="flowId"/>'s ledger is keyed by (<see cref="LedgerDirectory.WritePartitionAsync"/>).</summary>
    private protected ValueTask<short> WritePartitionAsync(Guid flowId, CancellationToken ct) => Partitions.WritePartitionAsync(flowId, ct);

    /// <summary>The number of the partition named <paramref name="partition"/> (<see cref="LedgerDirectory.PartitionIdOfAsync"/>).</summary>
    private protected ValueTask<short?> PartitionIdOfAsync(string partition, CancellationToken ct) => Partitions.PartitionIdOfAsync(partition, ct);

    /// <summary>The data-partition-id of partition number <paramref name="partitionId"/> (<see cref="LedgerDirectory.PartitionNameAsync"/>).</summary>
    private protected ValueTask<string?> PartitionNameAsync(short partitionId, CancellationToken ct) => Partitions.PartitionNameAsync(partitionId, ct);

    /// <summary>A run's error as the ledger keeps it (<see cref="LedgerRuns.Error"/>).</summary>
    private protected static string? RunError(string? error) => LedgerRuns.Error(error);

    /// <summary><paramref name="text"/> cut to <paramref name="max"/> characters, as the column it is written to holds it; null stays null.</summary>
    internal static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];

    /// <summary>Whether a write failed on a unique key or index: another writer inserted the same row first.</summary>
    private protected static bool IsDuplicate(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627))
            {
                return true;
            }
        }

        return false;
    }
}
