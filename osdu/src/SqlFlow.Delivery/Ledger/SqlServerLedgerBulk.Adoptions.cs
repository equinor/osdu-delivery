using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statement behind an adoption asked of a ledger's records (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a
/// record OSDU already holds): a slice of records at a time, naming each record asked under the intervention.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // One slice of an adoption, in one transaction with the rows that name what it asked. The named records of one ledger held
    // because OSDU held a record at the id their key makes (the id the hold kept, or a hold whose message begins as such holds
    // do, @prefix), claiming no id, under no lease and not asked already, each found by the table's key, are marked as asked
    // to adopt, unblocked, with no problem, and asked to be planned again, saying so: the test OccupiedHolds.Adoptable makes of
    // one record. The test is the statement's own, so a record a run claimed, held for another reason or leased since the page
    // was read is passed over. With an activity, each record asked is named under it.
    private const string AdoptSliceSql = $$"""
        DECLARE @asked TABLE ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY);
        UPDATE r SET
            r.[AdoptRequestedUtc] = @now,
            r.[Blocked] = 0,
            r.[ProblemHash] = NULL,
            r.[PlanRequestedUtc] = @now,
            r.[LastError] = @note,
            r.[UpdatedUtc] = @now
        OUTPUT inserted.[DeliveryKey] INTO @asked ([DeliveryKey])
        FROM (SELECT DISTINCT CAST(k.[value] AS uniqueidentifier) AS [DeliveryKey] FROM OPENJSON(@keys) AS k) AS n
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = n.[DeliveryKey]
        WHERE r.[Status] = N'held' AND r.[ClaimedTargetId] IS NULL AND r.[LeaseOwner] IS NULL AND r.[AdoptRequestedUtc] IS NULL
          AND (r.[OccupiedTargetId] IS NOT NULL OR r.[LastError] LIKE @prefix + N'%');
        IF @activityId IS NOT NULL
            INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
            SELECT @partitionId, @flowId, x.[DeliveryKey], @activityId
            FROM @asked AS x
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[ActivityRecord] AS a
                WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = x.[DeliveryKey] AND a.[ActivityId] = @activityId);
        SELECT [DeliveryKey] FROM @asked;
        """;

    /// <summary>
    /// Asks the named records of one ledger held for an id OSDU holds to adopt it, in one transaction (see the statement),
    /// naming each one asked under <paramref name="activityId"/> when given. <paramref name="prefix"/> holds no wildcard of
    /// LIKE, so it matches as written. Returns the records asked.
    /// </summary>
    public static Task<IReadOnlyList<Identity.DeliveryKey>> AdoptSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<Guid> keys, long? activityId, string note, string prefix, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            if (prefix.IndexOfAny(['%', '_', '[']) >= 0)
            {
                throw new ArgumentException($"The prefix '{prefix}' holds a wildcard of LIKE, so it would not match as written.", nameof(prefix));
            }

            await using var command = Command(connection, transaction, AdoptSliceSql, slice: null);
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(keys) });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@note", SqlDbType.NVarChar, 2000) { Value = note });
            command.Parameters.Add(new SqlParameter("@prefix", SqlDbType.NVarChar, 200) { Value = prefix });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            var asked = new List<Identity.DeliveryKey>(keys.Count);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                asked.Add(new Identity.DeliveryKey(reader.GetGuid(0)));
            }

            return (IReadOnlyList<Identity.DeliveryKey>)asked;
        }, ct);
}
