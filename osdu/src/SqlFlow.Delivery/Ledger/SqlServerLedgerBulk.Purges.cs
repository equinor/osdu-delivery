using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements behind deleting records from the ledger (osdu/docs/reference/concepts/removal-and-reversal.md,
/// Deleting removed records from the ledger, and Deleting the ledger): one slice of records in one transaction, each
/// kept as one line of <c>osdu.PurgedRecord</c> and named under the intervention that deleted it, its attempts, search
/// entries and row deleted; and, once a ledger has no record left, everything else it keeps of its runs.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // One slice of records deleted from the ledger. No record whose access in OSDU is restricted is ever deleted from the
    // ledger, which would forget the restriction (osdu/docs/reference/concepts/access-restriction.md). A removal's extra step
    // and the ledger-only deletion take only a record OSDU no longer holds, one the ledger marks deleted; deleting the whole ledger (@everyState) takes every record, after
    // the run that deletes it removed from OSDU every one OSDU held. Either way only a record no lease holds goes, so no work
    // is in flight on it and no lease event waits to be applied to it, and only one with no artifact an undo has still to
    // settle (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger), since the
    // sweep reaches an artifact through its record and an id the ledger has
    // still to take back would otherwise be stranded; the record is locked from the check to the end of the transaction. Each goes with one line saying what it was (its key, source key, label, OSDU id, the last version an
    // attempt of it named, how many attempts went with it), who deleted it and under which intervention; the intervention
    // names it too, so the audit trail reaches it however many records it reached. The rows of the audit trail that name it
    // (the activities and their links) and a reversal's item of it stay: they are another record's, or the trail's.
    // The line keeps the record's key tuple and asks for its row to be read again: the flow's next run reads it by key, as it
    // reads every record asked to be planned again, and delivers it as a record the ledger never held. A record a reversal
    // took out stays out (it is blocked, and its line asks for nothing), a record with no key tuple cannot be read by key,
    // and deleting the whole ledger asks for nothing, since its next run reads every row. A record deleted again takes the
    // request over from its earlier line, so a run reads its row once. Deleting a record says nothing about the ledger's
    // watermarks.
    private const string PurgeSql = $$"""
        DECLARE @purged TABLE ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY);
        INSERT INTO @purged ([DeliveryKey])
        SELECT r.[DeliveryKey]
        FROM (SELECT DISTINCT CAST(j.[value] AS uniqueidentifier) AS [DeliveryKey] FROM OPENJSON(@keys) AS j) AS k
        INNER JOIN [osdu].[Record] AS r WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = k.[DeliveryKey]
        WHERE (@everyState = 1 OR r.[Status] = N'deleted') AND r.[LeaseOwner] IS NULL AND r.[RestrictionId] IS NULL
            AND NOT EXISTS (
                SELECT 1 FROM [osdu].[Artifact] AS o
                WHERE o.[PartitionId] = @partitionId AND o.[FlowId] = @flowId AND o.[DeliveryKey] = r.[DeliveryKey]
                    AND o.[State] IN (N'intent', N'pending', N'due', N'failed'));

        UPDATE l SET l.[PlanRequestedUtc] = NULL
        FROM @purged AS p
        INNER JOIN [osdu].[PurgedRecord] AS l
            ON l.[PartitionId] = @partitionId AND l.[FlowId] = @flowId AND l.[DeliveryKey] = p.[DeliveryKey]
        WHERE l.[PlanRequestedUtc] IS NOT NULL;

        INSERT INTO [osdu].[PurgedRecord] ([PartitionId], [FlowId], [DeliveryKey], [SourceKey], [SourceKeyJson], [Label], [TargetId], [LastVersion], [Attempts],
            [ActivityId], [PurgedBy], [PurgedUtc], [PlanRequestedUtc])
        SELECT @partitionId, @flowId, r.[DeliveryKey], r.[SourceKey], r.[SourceKeyJson], r.[Label], COALESCE(r.[TargetId] COLLATE Latin1_General_100_BIN2, r.[ClaimedTargetId]),
            (SELECT TOP (1) a.[TargetVersion] FROM [osdu].[Attempt] AS a
             WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = r.[DeliveryKey] AND a.[TargetVersion] IS NOT NULL
             ORDER BY a.[AttemptId] DESC),
            (SELECT COUNT(*) FROM [osdu].[Attempt] AS a
             WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = r.[DeliveryKey]),
            @activityId, @actor, @now,
            CASE WHEN @everyState = 0 AND r.[Blocked] = 0 AND r.[SourceKeyJson] IS NOT NULL THEN @now END
        FROM @purged AS p
        INNER JOIN [osdu].[Record] AS r
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = p.[DeliveryKey];

        IF @activityId IS NOT NULL
            INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
            SELECT @partitionId, @flowId, p.[DeliveryKey], @activityId
            FROM @purged AS p
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[ActivityRecord] AS e
                WHERE e.[PartitionId] = @partitionId AND e.[FlowId] = @flowId AND e.[DeliveryKey] = p.[DeliveryKey] AND e.[ActivityId] = @activityId);

        DELETE a
        FROM [osdu].[Attempt] AS a
        INNER JOIN @purged AS p ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = p.[DeliveryKey];

        DELETE i
        FROM [osdu].[RecordIdentity] AS i
        INNER JOIN @purged AS p ON i.[PartitionId] = @partitionId AND i.[FlowId] = @flowId AND i.[DeliveryKey] = p.[DeliveryKey];

        DELETE r
        FROM [osdu].[Record] AS r
        INNER JOIN @purged AS p ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = p.[DeliveryKey];

        SELECT p.[DeliveryKey] FROM @purged AS p;
        """;

    // What a ledger keeps of its runs, deleted once it holds no record: its submissions and their work batches, its leases
    // and the lease events no lease applied, its watermarks, and its reversals with their items. Without a watermark the
    // ledger's next run reads every row, and without its records it delivers each as a record the ledger never held; the
    // requests earlier deletions of single records left on their lines are answered, since that run reads their rows too. A live
    // lease (one a worker still renews) or a record still in the ledger leaves everything as it was and says so: the work in
    // flight, or the record a lease held through the deletion of the rest, belongs to a ledger that is not empty. The
    // activities, their links and the lines of the deleted records stay, as the whole audit trail does, and so does the
    // ledger's own entry in the directory. Every delete seeks the partition and the ledger through an index of its table.
    private const string DeleteRunStateSql = """
        DECLARE @leased bit = CASE WHEN EXISTS (
            SELECT 1 FROM [osdu].[Lease] WITH (UPDLOCK, HOLDLOCK)
            WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId AND [ExpiresUtc] > @now) THEN 1 ELSE 0 END;
        DECLARE @recordsLeft int = (
            SELECT COUNT(*) FROM [osdu].[Record] WITH (UPDLOCK, HOLDLOCK)
            WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId);
        DECLARE @submissions int = 0, @batches int = 0, @leases int = 0, @events int = 0, @watermarks int = 0, @reversals int = 0;

        IF @leased = 0 AND @recordsLeft = 0
        BEGIN
            DELETE FROM [osdu].[RecordEvent] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;
            SET @events = @@ROWCOUNT;
            DELETE FROM [osdu].[Lease] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;
            SET @leases = @@ROWCOUNT;
            DELETE FROM [osdu].[WorkBatch] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;
            SET @batches = @@ROWCOUNT;
            DELETE FROM [osdu].[SourceWatermark] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;
            SET @watermarks = @@ROWCOUNT;
            UPDATE [osdu].[PurgedRecord] SET [PlanRequestedUtc] = NULL
            WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId AND [PlanRequestedUtc] IS NOT NULL;
            DELETE i
            FROM [osdu].[ReversalItem] AS i
            INNER JOIN [osdu].[Reversal] AS v ON v.[PartitionId] = i.[PartitionId] AND v.[ReversalId] = i.[ReversalId]
            WHERE v.[PartitionId] = @partitionId AND v.[FlowId] = @flowId;
            DELETE FROM [osdu].[Reversal] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;
            SET @reversals = @@ROWCOUNT;
            DELETE FROM [osdu].[Submission] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;
            SET @submissions = @@ROWCOUNT;
        END

        SELECT @leased, @recordsLeft, @submissions, @batches, @leases, @events, @watermarks, @reversals;
        """;

    /// <summary>
    /// Deletes one slice of records from the ledger in one transaction; returns the records it deleted. Without
    /// <paramref name="everyState"/> only a record the ledger marks deleted goes; with it, every record no lease holds.
    /// </summary>
    public static Task<IReadOnlyList<DeliveryKey>> PurgeSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, long? activityId, DateTime now, bool everyState, CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<DeliveryKey>>(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, PurgeSql, slice: null);
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(keys.Select(k => k.Value)) });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@actor", SqlDbType.NVarChar, 200) { Value = actor });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            command.Parameters.Add(new SqlParameter("@everyState", SqlDbType.Bit) { Value = everyState });
            var purged = new List<DeliveryKey>(keys.Count);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                purged.Add(new DeliveryKey(reader.GetGuid(0)));
            }

            return purged;
        }, ct);

    /// <summary>
    /// Deletes in one transaction what an empty ledger keeps of its runs (<see cref="DeleteRunStateSql"/>), and says what it
    /// deleted, or why it deleted nothing: a live lease, or records still in the ledger.
    /// </summary>
    public static Task<RunStateDeletion> DeleteRunStateAsync(OsduDbContext db, short partitionId, Guid flowId, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, DeleteRunStateSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Deleting a ledger's run state answered no row; the statement always selects one.");
            }

            return new RunStateDeletion(
                Leased: reader.GetBoolean(0),
                RecordsLeft: reader.GetInt32(1),
                Submissions: reader.GetInt32(2),
                WorkBatches: reader.GetInt32(3),
                Leases: reader.GetInt32(4),
                Events: reader.GetInt32(5),
                Watermarks: reader.GetInt32(6),
                Reversals: reader.GetInt32(7));
        }, ct);
}

/// <summary>What <see cref="SqlServerLedgerBulk.DeleteRunStateAsync"/> found and deleted.</summary>
internal sealed record RunStateDeletion(
    bool Leased, int RecordsLeft, int Submissions, int WorkBatches, int Leases, int Events, int Watermarks, int Reversals);
