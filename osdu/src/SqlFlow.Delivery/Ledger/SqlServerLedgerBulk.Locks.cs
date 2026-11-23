using System.Data;
using System.Data.SqlTypes;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The read a write that decides record by record starts with (osdu/docs/reference/concepts/ledger.md, Writes at scale): the
/// records it is about to decide, found by the table's key and held against every other writer until its transaction ends.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // The named records of one ledger that exist, each found by one seek of the table's key and update-locked to the end of
    // the caller's transaction. One statement takes every lock, in the order of the key (InKeyOrder), and the seek names
    // the key's three columns, so no plan reads a range of the table. Only records that exist are locked, never a range of
    // keys: a record another writer inserts meanwhile is refused by the table's key when this one inserts it.
    private const string LockRecordsSql = $$"""
        SELECT r.*
        FROM {{NamedKeys}}
        INNER JOIN [osdu].[Record] AS r WITH (UPDLOCK, ROWLOCK, FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = n.[DeliveryKey]
        {{InKeyOrder}}
        """;

    /// <summary>
    /// The named records of a statement that writes records by key, as <c>n</c>: the keys of <c>@keys</c>, which
    /// <see cref="KeysInOrder"/> wrote distinct and in the order of the table's key.
    /// </summary>
    internal const string NamedKeys = "OPENJSON(@keys) WITH ([DeliveryKey] uniqueidentifier '$') AS n";

    /// <summary>
    /// The hint a statement joining <see cref="NamedKeys"/> to the record table ends with: the keys are read first and as
    /// written, and each record is found by a seek of its own, so the statement takes its records' locks in the order of
    /// the table's key. Every write of named records takes them so, and two of them that share records never each hold one
    /// the other waits for: the one that reaches their first shared record second waits there holding none of the rest.
    /// </summary>
    internal const string InKeyOrder = "OPTION (FORCE ORDER, LOOP JOIN)";

    /// <summary>
    /// <paramref name="keys"/> as a statement over <see cref="NamedKeys"/> reads them: each once, in the order the database
    /// sorts a uniqueidentifier, which is the record table's key within a ledger.
    /// </summary>
    internal static string KeysInOrder(IEnumerable<Guid> keys)
        => JsonSerializer.Serialize(InKeyOrderOf(keys));

    /// <summary><paramref name="keys"/>, each once, in the order the database sorts a uniqueidentifier.</summary>
    internal static List<Guid> InKeyOrderOf(IEnumerable<Guid> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return [.. keys.Distinct().OrderBy(k => new SqlGuid(k))];
    }

    /// <summary>
    /// The records of <paramref name="keys"/> one ledger holds, tracked by <paramref name="db"/> and update-locked until the
    /// transaction <paramref name="db"/> is in ends, so what the caller decides from them is decided of the rows as they
    /// stand, and written before any other writer reaches them. The locks are taken in the order of the table's key,
    /// whatever order the keys are given in.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="db"/> is in no transaction, so the locks would end with the read.</exception>
    public static async Task<Dictionary<Guid, DeliveryRecord>> LockRecordsAsync(
        OsduDbContext db, short partitionId, Guid flowId, IEnumerable<Guid> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keys);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Records are locked for a write inside the transaction that writes them; the context is in none.");
        }

        var sorted = InKeyOrderOf(keys);
        if (sorted.Count == 0)
        {
            return [];
        }

        var rows = await db.DeliveryRecords
            .FromSqlRaw(
                LockRecordsSql,
                new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = KeysInOrder(sorted) },
                new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId },
                new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId })
            .AsTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.DeliveryKey);
    }
}
