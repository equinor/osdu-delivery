using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements behind reversals (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a
/// submission): the pages of what a source delivered, read in the order of the attempt indexes that hold them; the
/// listing of each record of a page with what OSDU held before; and the settlement of a slice of records, which changes
/// each record, writes its attempt, names it under the run's activity and settles its item in one transaction. No
/// statement reads or writes more than a slice of records.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // The phases a submission's delivered attempts were written under: what its pages are read by, one phase at a time,
    // since the submission's index orders its attempts by outcome, then phase, then id.
    private const string SubmissionPhasesSql = """
        SELECT DISTINCT a.[Phase]
        FROM [osdu].[Attempt] AS a
        WHERE a.[SubmissionId] = @submissionId AND a.[Outcome] = N'delivered';
        """;

    // A page of one submission's delivered attempts in one phase, after the last one read, in the order of the
    // submission's index: a seek however many attempts the submission wrote.
    private const string SubmissionDeliveredPageSql = """
        SELECT TOP (@slice) a.[AttemptId], a.[DeliveryKey]
        FROM [osdu].[Attempt] AS a
        WHERE a.[SubmissionId] = @submissionId AND a.[Outcome] = N'delivered' AND a.[Phase] = @phase
          AND a.[PartitionId] = @partitionId AND a.[AttemptId] > @after
        ORDER BY a.[PartitionId], a.[AttemptId];
        """;

    // A page of the records one run delivered itself in one ledger, after the last one read (the empty key before the
    // first, which no record has), in the order of the run's index: a seek past the last page however many it delivered.
    private const string RunDeliveredPageSql = """
        SELECT TOP (@slice) a.[DeliveryKey]
        FROM [osdu].[Attempt] AS a
        WHERE a.[RunId] = @runId AND a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId
          AND a.[DeliveryKey] > @after AND a.[Outcome] = N'delivered'
        GROUP BY a.[DeliveryKey]
        ORDER BY a.[DeliveryKey];
        """;

    // Whether a submission of the ledger, or a submission a run planned in it, delivered a record: a seek of the submission
    // (by its id, or the run's submissions by the run's index), then a seek of each one's delivered attempts in the
    // submission's index, which stops at the first.
    private const string SubmissionDeliveredSql = """
        SELECT TOP (1) 1
        FROM [osdu].[Submission] AS s
        WHERE s.[SubmissionId] = @submissionId AND s.[PartitionId] = @partitionId AND s.[FlowId] = @flowId
          AND EXISTS (SELECT 1 FROM [osdu].[Attempt] AS a WHERE a.[SubmissionId] = s.[SubmissionId] AND a.[Outcome] = N'delivered');
        """;

    private const string RunSubmissionsDeliveredSql = """
        SELECT TOP (1) 1
        FROM [osdu].[Submission] AS s
        WHERE s.[RunId] = @runId AND s.[PartitionId] = @partitionId AND s.[FlowId] = @flowId
          AND EXISTS (SELECT 1 FROM [osdu].[Attempt] AS a WHERE a.[SubmissionId] = s.[SubmissionId] AND a.[Outcome] = N'delivered');
        """;

    // Whether a run delivered a record of the ledger itself, read from at most @probe of its attempts there: the run's index
    // names them without their outcome, so the bound is what keeps a run that held a million records quick to answer.
    private const string RunDeliveredSql = """
        SELECT TOP (1) 1
        FROM (
            SELECT TOP (@probe) a.[PartitionId], a.[AttemptId]
            FROM [osdu].[Attempt] AS a
            WHERE a.[RunId] = @runId AND a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId
        ) AS t
        INNER JOIN [osdu].[Attempt] AS d ON d.[PartitionId] = t.[PartitionId] AND d.[AttemptId] = t.[AttemptId]
        WHERE d.[Outcome] = N'delivered';
        """;

    // Whether a run delivered anything, or tried to, in one ledger.
    private const string RunTouchedSql = """
        SELECT TOP (1) 1 FROM [osdu].[Attempt] AS a WHERE a.[RunId] = @runId AND a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId;
        """;

    // The records of a page as a reversal lists them, each seeking its own attempts: the source's first delivered attempt
    // (what it wrote first, and the version it recorded it replaced), the source's last one (the version the source left),
    // and the record's last attempt before the source that left OSDU holding a version or nothing. An attempt is the
    // source's when its submission is one of the source's, or it was the source run's own. What OSDU held before is the
    // version the first attempt recorded it replaced, when it recorded one; else what the attempt before says; else
    // unknown, which OSDU's version list decides. The first version the source wrote is the lowest any step of its first
    // attempt returned: one try can write two versions (a Wellbore DDMS record, then its bulk data, which the try records
    // as the version it left).
    private const string ResolveSql = """
        WITH k AS (SELECT DISTINCT CAST(j.[value] AS uniqueidentifier) AS [DeliveryKey] FROM OPENJSON(@keys) AS j),
             s AS (SELECT CAST(j.[value] AS uniqueidentifier) AS [SubmissionId] FROM OPENJSON(@submissions) AS j),
             x AS (
                SELECT k.[DeliveryKey], r.[ClaimedTargetId] AS [TargetId], f.[AttemptId] AS [FirstAttemptId],
                    CASE WHEN f.[FirstWritten] IS NOT NULL AND (f.[TargetVersion] IS NULL OR f.[FirstWritten] < f.[TargetVersion]) THEN f.[FirstWritten] ELSE f.[TargetVersion] END AS [FirstVersion],
                    l.[TargetVersion] AS [RunVersion],
                    CASE
                        WHEN f.[Recorded] = 1 AND f.[Replaced] IS NULL THEN N'none'
                        WHEN f.[Recorded] = 1 THEN N'version'
                        WHEN p.[Outcome] = N'deleted' THEN N'none'
                        WHEN p.[Outcome] IN (N'delivered', N'restored') AND p.[TargetVersion] IS NOT NULL THEN N'version'
                        ELSE N'unknown'
                    END AS [Prior],
                    CASE
                        WHEN f.[Recorded] = 1 THEN f.[Replaced]
                        WHEN p.[Outcome] IN (N'delivered', N'restored') THEN p.[TargetVersion]
                    END AS [PriorVersion],
                    CASE
                        WHEN p.[Outcome] IN (N'delivered', N'restored') AND p.[TargetVersion] IS NOT NULL AND (f.[Recorded] = 0 OR p.[TargetVersion] = f.[Replaced])
                            THEN p.[AttemptId]
                    END AS [PriorAttemptId]
                FROM k
                LEFT JOIN [osdu].[Record] AS r
                    ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = k.[DeliveryKey]
                CROSS APPLY (
                    SELECT TOP (1) a.[AttemptId], a.[TargetVersion],
                        CASE WHEN ISJSON(a.[ResultJson]) = 1 AND JSON_QUERY(a.[ResultJson], '$.replaced') IS NOT NULL THEN 1 ELSE 0 END AS [Recorded],
                        CASE WHEN ISJSON(a.[ResultJson]) = 1 THEN TRY_CAST(JSON_VALUE(a.[ResultJson], '$.replaced.version') AS bigint) END AS [Replaced],
                        CASE WHEN ISJSON(a.[ResultJson]) = 1 THEN (
                            SELECT MIN(TRY_CAST(JSON_VALUE(st.[value], '$.returned.version') AS bigint))
                            FROM OPENJSON(a.[ResultJson], '$.steps') AS st) END AS [FirstWritten]
                    FROM [osdu].[Attempt] AS a
                    WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = k.[DeliveryKey] AND a.[Outcome] = N'delivered'
                      AND (a.[SubmissionId] IN (SELECT s.[SubmissionId] FROM s) OR (@runId IS NOT NULL AND a.[RunId] = @runId))
                    ORDER BY a.[AttemptId]) AS f
                CROSS APPLY (
                    SELECT TOP (1) a.[TargetVersion]
                    FROM [osdu].[Attempt] AS a
                    WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = k.[DeliveryKey] AND a.[Outcome] = N'delivered'
                      AND (a.[SubmissionId] IN (SELECT s.[SubmissionId] FROM s) OR (@runId IS NOT NULL AND a.[RunId] = @runId))
                    ORDER BY a.[AttemptId] DESC) AS l
                OUTER APPLY (
                    SELECT TOP (1) a.[AttemptId], a.[Outcome], a.[TargetVersion]
                    FROM [osdu].[Attempt] AS a
                    WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = k.[DeliveryKey]
                      AND a.[AttemptId] < f.[AttemptId] AND a.[Outcome] IN (N'delivered', N'deleted', N'restored')
                    ORDER BY a.[AttemptId] DESC) AS p)
        """;

    // What OSDU held of each record before the write that left the version the ledger holds now: the attempt that delivered
    // or restored that version says, by the version a delivery recorded it replaced or a step back recorded it replaced;
    // else the attempt before it does (a removal leaves nothing, a delivery or a restore its version); else nobody does. The
    // first version the write left is the lowest any of its steps returned, since one try can write two.
    private const string PriorVersionsSql = """
        WITH k AS (
            SELECT j.[k] AS [DeliveryKey], j.[v] AS [Version]
            FROM OPENJSON(@items) WITH ([k] uniqueidentifier '$.k', [v] bigint '$.v') AS j)
        SELECT k.[DeliveryKey],
            CASE
                WHEN w.[AttemptId] IS NULL THEN N'unknown'
                WHEN w.[Recorded] = 1 AND w.[Replaced] IS NULL THEN N'none'
                WHEN w.[Recorded] = 1 THEN N'version'
                WHEN p.[Outcome] = N'deleted' THEN N'none'
                WHEN p.[Outcome] IN (N'delivered', N'restored') AND p.[TargetVersion] IS NOT NULL THEN N'version'
                ELSE N'unknown'
            END,
            CASE
                WHEN w.[Recorded] = 1 THEN w.[Replaced]
                WHEN p.[Outcome] IN (N'delivered', N'restored') THEN p.[TargetVersion]
            END,
            CASE
                WHEN w.[AttemptId] IS NULL THEN NULL
                WHEN w.[FirstWritten] IS NOT NULL AND w.[FirstWritten] < k.[Version] THEN w.[FirstWritten]
                ELSE k.[Version]
            END
        FROM k
        OUTER APPLY (
            SELECT TOP (1) a.[AttemptId],
                CASE WHEN ISJSON(a.[ResultJson]) = 1
                    AND (JSON_QUERY(a.[ResultJson], '$.replaced') IS NOT NULL OR JSON_VALUE(a.[ResultJson], '$.previous.replacedVersion') IS NOT NULL)
                    THEN 1 ELSE 0 END AS [Recorded],
                CASE WHEN ISJSON(a.[ResultJson]) = 1 THEN COALESCE(
                    TRY_CAST(JSON_VALUE(a.[ResultJson], '$.previous.replacedVersion') AS bigint),
                    TRY_CAST(JSON_VALUE(a.[ResultJson], '$.replaced.version') AS bigint)) END AS [Replaced],
                CASE WHEN ISJSON(a.[ResultJson]) = 1 THEN (
                    SELECT MIN(TRY_CAST(JSON_VALUE(st.[value], '$.returned.version') AS bigint))
                    FROM OPENJSON(a.[ResultJson], '$.steps') AS st) END AS [FirstWritten]
            FROM [osdu].[Attempt] AS a
            WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = k.[DeliveryKey]
              AND a.[Outcome] IN (N'delivered', N'restored') AND a.[TargetVersion] = k.[Version]
            ORDER BY a.[AttemptId] DESC) AS w
        OUTER APPLY (
            SELECT TOP (1) a.[Outcome], a.[TargetVersion]
            FROM [osdu].[Attempt] AS a
            WHERE w.[AttemptId] IS NOT NULL AND a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = k.[DeliveryKey]
              AND a.[AttemptId] < w.[AttemptId] AND a.[Outcome] IN (N'delivered', N'deleted', N'restored')
            ORDER BY a.[AttemptId] DESC) AS p;
        """;

    // A page of records added to a reversal once each: a record a page of another phase or submission listed already is
    // left as it is, so a listing stopped half way and started again adds only what it had not.
    private const string CaptureSql = ResolveSql + """

        INSERT INTO [osdu].[ReversalItem] ([PartitionId], [ReversalId], [DeliveryKey], [TargetId], [FirstAttemptId], [FirstVersion], [RunVersion],
            [Prior], [PriorVersion], [PriorAttemptId], [State], [UpdatedUtc])
        SELECT @partitionId, @reversalId, x.[DeliveryKey], x.[TargetId], x.[FirstAttemptId], x.[FirstVersion], x.[RunVersion],
            x.[Prior], x.[PriorVersion], x.[PriorAttemptId], N'pending', @now
        FROM x
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[ReversalItem] AS i
            WHERE i.[PartitionId] = @partitionId AND i.[ReversalId] = @reversalId AND i.[DeliveryKey] = x.[DeliveryKey]);
        SELECT @@ROWCOUNT;
        """;

    // The records of a page as a reversal would list them, for a preview; nothing is written.
    private const string ResolvePreviewSql = ResolveSql + """

        SELECT x.[DeliveryKey], x.[TargetId], x.[FirstAttemptId], x.[FirstVersion], x.[RunVersion], x.[Prior], x.[PriorVersion], x.[PriorAttemptId]
        FROM x;
        """;

    // The records a source delivered: its submissions' delivered attempts and, for a run, its own, each record once.
    private const string SourceRecordsSql = """
        WITH s AS (SELECT CAST(j.[value] AS uniqueidentifier) AS [SubmissionId] FROM OPENJSON(@submissions) AS j),
             d AS (
                SELECT a.[DeliveryKey]
                FROM s
                INNER JOIN [osdu].[Attempt] AS a ON a.[SubmissionId] = s.[SubmissionId] AND a.[Outcome] = N'delivered' AND a.[PartitionId] = @partitionId
                UNION
                SELECT a.[DeliveryKey]
                FROM [osdu].[Attempt] AS a
                WHERE @runId IS NOT NULL AND a.[RunId] = @runId AND a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[Outcome] = N'delivered')
        SELECT COUNT_BIG(*) FROM d;
        """;

    // The first records a source delivered in key order, for a preview's sample. Keys are derived from source data by a
    // hash, so the first in key order are spread across the source like a sample.
    private const string SourceSampleSql = """
        WITH s AS (SELECT CAST(j.[value] AS uniqueidentifier) AS [SubmissionId] FROM OPENJSON(@submissions) AS j),
             d AS (
                SELECT a.[DeliveryKey]
                FROM s
                INNER JOIN [osdu].[Attempt] AS a ON a.[SubmissionId] = s.[SubmissionId] AND a.[Outcome] = N'delivered' AND a.[PartitionId] = @partitionId
                UNION
                SELECT a.[DeliveryKey]
                FROM [osdu].[Attempt] AS a
                WHERE @runId IS NOT NULL AND a.[RunId] = @runId AND a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[Outcome] = N'delivered')
        SELECT TOP (@slice) d.[DeliveryKey] FROM d ORDER BY d.[DeliveryKey];
        """;

    // Whether the record r is held or failed at a row of its own: its pending columns name the version it was held at, the
    // latest row the ledger read for it, which a reversal leaves it blocked at (as OsduLedger.HeldAtItsRow says it).
    private const string HeldAtRow =
        "CASE WHEN r.[Status] IN (N'held', N'failed') AND (r.[PendingSourceFingerprint] IS NOT NULL OR r.[PendingSourceModifiedUtc] IS NOT NULL) THEN 1 ELSE 0 END";

    // One slice of a reversal's settlement, in one transaction. Each restore or removal is settled with its record only
    // while the record is still the one the source left (the version it left, no lease holding it), and the record is
    // locked until the transaction ends; one that moved on meanwhile is settled failed, saying what OSDU now holds. Every
    // item gets its attempt, phase reverse: restored (with the hashes and origin of the version put back), deleted, skipped
    // or failed. A restored record becomes reverted at the new version, its source version and origin left where the
    // reversed delivery had them, or for a record held or failed at a later row, at that row, so the planner keeps it
    // blocked until the source moves; a removed one becomes deleted, as a removal leaves it. Every record changed is named
    // under the run's activity, and every item is settled.
    private const string SettleSql = $$"""
        DECLARE @rows TABLE (
            [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
            [State] nvarchar(16) NOT NULL,
            [Outcome] nvarchar(24) NOT NULL,
            [Detail] nvarchar(2000) NULL,
            [RestoredVersion] bigint NULL,
            [NewVersion] bigint NULL,
            [TargetStateJson] nvarchar(max) NULL,
            [ResultJson] nvarchar(max) NULL,
            [Note] nvarchar(2000) NULL,
            [Change] nvarchar(8) NOT NULL);
        INSERT INTO @rows ([DeliveryKey], [State], [Outcome], [Detail], [RestoredVersion], [NewVersion], [TargetStateJson], [ResultJson], [Note], [Change])
        SELECT j.[k], j.[s], j.[o], j.[d], j.[rv], j.[nv], j.[ts], j.[rj], j.[n], j.[c]
        FROM OPENJSON(@settled) WITH (
            [k] uniqueidentifier '$.k', [s] nvarchar(16) '$.s', [o] nvarchar(24) '$.o', [d] nvarchar(2000) '$.d', [rv] bigint '$.rv', [nv] bigint '$.nv',
            [ts] nvarchar(max) '$.ts', [rj] nvarchar(max) '$.rj', [n] nvarchar(2000) '$.n', [c] nvarchar(8) '$.c') AS j;

        DECLARE @apply TABLE ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY);
        INSERT INTO @apply ([DeliveryKey])
        SELECT x.[DeliveryKey]
        FROM @rows AS x
        INNER JOIN [osdu].[ReversalItem] AS i
            ON i.[PartitionId] = @partitionId AND i.[ReversalId] = @reversalId AND i.[DeliveryKey] = x.[DeliveryKey]
        INNER JOIN [osdu].[Record] AS r WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = x.[DeliveryKey]
        WHERE x.[Change] <> N'none' AND r.[LeaseOwner] IS NULL
          AND (r.[TargetVersion] = i.[RunVersion] OR (r.[TargetVersion] IS NULL AND i.[RunVersion] IS NULL));

        DECLARE @moved TABLE ([Outcome] nvarchar(24) NOT NULL);
        UPDATE x SET
            x.[State] = N'failed',
            x.[Outcome] = N'failed',
            x.[Detail] = LEFT(CONCAT(
                N'the record changed in the ledger while the reversal wrote to OSDU (the ledger now holds ',
                CASE WHEN r.[DeliveryKey] IS NULL THEN N'no record' ELSE CONCAT(r.[Status], N', ', COALESCE(N'version ' + CAST(r.[TargetVersion] AS nvarchar(20)), N'no version')) END,
                N'), so it was left as it is; OSDU ',
                CASE WHEN x.[Change] = N'restore' THEN CONCAT(N'holds version ', CAST(x.[NewVersion] AS nvarchar(20)), N', which the reversal wrote') ELSE N'no longer holds the record' END,
                N'. Compare the record with OSDU before reversing again.'), 2000),
            x.[Change] = N'none'
        OUTPUT deleted.[Outcome] INTO @moved ([Outcome])
        FROM @rows AS x
        LEFT JOIN [osdu].[Record] AS r
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = x.[DeliveryKey]
        WHERE x.[Change] <> N'none' AND NOT EXISTS (SELECT 1 FROM @apply AS a WHERE a.[DeliveryKey] = x.[DeliveryKey]);

        INSERT INTO [osdu].[Attempt] ([PartitionId], [FlowId], [DeliveryKey], [SubmissionId], [RunId], [Worker], [StartedUtc], [CompletedUtc],
            [Outcome], [Phase], [MetadataHash], [PayloadHash], [TargetVersion], [Error], [ResultJson], [WorkBatch],
            [SourceFileName], [SourceRowNumber], [SourceUpdatedUtc], [SourceDeletedUtc])
        SELECT @partitionId, @flowId, x.[DeliveryKey], r.[LastSubmissionId], @runId, @actor, @now, @now,
            CASE x.[Outcome]
                WHEN N'restored' THEN N'restored'
                WHEN N'removed' THEN N'deleted'
                WHEN N'already-gone' THEN N'deleted'
                WHEN N'failed' THEN N'failed'
                ELSE N'skipped'
            END,
            N'reverse',
            CASE WHEN x.[Outcome] = N'restored' THEN pa.[MetadataHash] END,
            CASE WHEN x.[Outcome] = N'restored' THEN pa.[PayloadHash] END,
            CASE WHEN x.[Outcome] = N'restored' THEN x.[NewVersion] ELSE r.[TargetVersion] END,
            CASE WHEN x.[State] = N'failed' THEN x.[Detail] END,
            x.[ResultJson],
            NULL,
            CASE WHEN x.[Outcome] = N'restored' THEN pa.[SourceFileName] END,
            CASE WHEN x.[Outcome] = N'restored' THEN pa.[SourceRowNumber] END,
            CASE WHEN x.[Outcome] = N'restored' THEN pa.[SourceUpdatedUtc] END,
            NULL
        FROM @rows AS x
        INNER JOIN [osdu].[ReversalItem] AS i
            ON i.[PartitionId] = @partitionId AND i.[ReversalId] = @reversalId AND i.[DeliveryKey] = x.[DeliveryKey]
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = x.[DeliveryKey]
        LEFT JOIN [osdu].[Attempt] AS pa
            ON pa.[PartitionId] = @partitionId AND pa.[AttemptId] = i.[PriorAttemptId];

        UPDATE r SET
            r.[Status] = N'reverted',
            r.[Blocked] = 1,
            r.[ProblemHash] = NULL,
            r.[PendingSourceFingerprint] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceFingerprint] ELSE r.[SourceFingerprint] END,
            r.[PendingSourceModifiedUtc] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceModifiedUtc] ELSE COALESCE(r.[SourceModifiedUtc], @now) END,
            r.[PendingSourceFileName] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceFileName] ELSE r.[SourceFileName] END,
            r.[PendingSourceRowNumber] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceRowNumber] ELSE r.[SourceRowNumber] END,
            r.[PendingSourceUpdatedUtc] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceUpdatedUtc] ELSE r.[SourceUpdatedUtc] END,
            r.[SourceFileName] = pa.[SourceFileName],
            r.[SourceRowNumber] = pa.[SourceRowNumber],
            r.[SourceUpdatedUtc] = pa.[SourceUpdatedUtc],
            r.[MetadataHash] = pa.[MetadataHash],
            r.[PayloadHash] = pa.[PayloadHash],
            r.[PayloadModifiedUtc] = NULL,
            r.[SourceFingerprint] = NULL,
            r.[SourceModifiedUtc] = NULL,
            r.[RenderContext] = NULL,
            r.[TargetVersion] = x.[NewVersion],
            r.[TargetStateJson] = x.[TargetStateJson],
            r.[LastVerifiedUtc] = NULL,
            r.[LastVerifyOutcome] = NULL,
            r.[NextAttemptUtc] = NULL,
            r.[AttemptCount] = 0,
            r.[PendingDocumentRef] = NULL,
            r.[WorkBatch] = NULL,
            r.[PendingStepJson] = NULL,
            r.[PendingReferences] = NULL,
            r.[WaitingFor] = NULL,
            r.[PendingMetadata] = 0,
            r.[PendingPayload] = 0,
            r.[PendingPayloadLocation] = NULL,
            r.[PendingPayloadModifiedUtc] = NULL,
            r.[PendingRenderContext] = NULL,
            r.[PendingMetadataHash] = NULL,
            r.[PendingPayloadHash] = NULL,
            r.[PendingParentVersion] = NULL,
            r.[AcceptedMetadataHash] = NULL,
            r.[PlanRequestedUtc] = NULL,
            r.[LastError] = x.[Note],
            r.[UpdatedUtc] = @now
        FROM @rows AS x
        INNER JOIN @apply AS a ON a.[DeliveryKey] = x.[DeliveryKey]
        INNER JOIN [osdu].[ReversalItem] AS i
            ON i.[PartitionId] = @partitionId AND i.[ReversalId] = @reversalId AND i.[DeliveryKey] = x.[DeliveryKey]
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = x.[DeliveryKey]
        LEFT JOIN [osdu].[Attempt] AS pa
            ON pa.[PartitionId] = @partitionId AND pa.[AttemptId] = i.[PriorAttemptId]
        CROSS APPLY (SELECT {{HeldAtRow}} AS [HeldAtRow]) AS h
        WHERE x.[Change] = N'restore';

        UPDATE r SET
            r.[Status] = N'deleted',
            r.[Blocked] = 1,
            r.[ProblemHash] = NULL,
            r.[PendingSourceFingerprint] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceFingerprint] ELSE r.[SourceFingerprint] END,
            r.[PendingSourceModifiedUtc] = CASE WHEN h.[HeldAtRow] = 1 THEN r.[PendingSourceModifiedUtc] ELSE COALESCE(r.[SourceModifiedUtc], @now) END,
            r.[TargetVersion] = NULL,
            r.[TargetStateJson] = NULL,
            r.[MetadataHash] = NULL,
            r.[PayloadHash] = NULL,
            r.[PayloadModifiedUtc] = NULL,
            r.[SourceFingerprint] = NULL,
            r.[SourceModifiedUtc] = NULL,
            r.[LastVerifiedUtc] = NULL,
            r.[LastVerifyOutcome] = NULL,
            r.[LeaseOwner] = NULL,
            r.[NextAttemptUtc] = NULL,
            r.[AttemptCount] = 0,
            r.[PendingDocumentRef] = NULL,
            r.[WorkBatch] = NULL,
            r.[PendingStepJson] = NULL,
            r.[PendingReferences] = NULL,
            r.[WaitingFor] = NULL,
            r.[PendingMetadata] = 0,
            r.[PendingPayload] = 0,
            r.[PendingPayloadLocation] = NULL,
            r.[PendingPayloadModifiedUtc] = NULL,
            r.[PlanRequestedUtc] = NULL,
            r.[LastError] = x.[Note],
            r.[UpdatedUtc] = @now
        FROM @rows AS x
        INNER JOIN @apply AS a ON a.[DeliveryKey] = x.[DeliveryKey]
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = x.[DeliveryKey]
        CROSS APPLY (SELECT {{HeldAtRow}} AS [HeldAtRow]) AS h
        WHERE x.[Change] = N'remove';

        IF @activityId IS NOT NULL
            INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
            SELECT @partitionId, @flowId, a.[DeliveryKey], @activityId
            FROM @apply AS a
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[ActivityRecord] AS e
                WHERE e.[PartitionId] = @partitionId AND e.[FlowId] = @flowId AND e.[DeliveryKey] = a.[DeliveryKey] AND e.[ActivityId] = @activityId);

        UPDATE i SET
            i.[State] = x.[State],
            i.[Outcome] = x.[Outcome],
            i.[Detail] = x.[Detail],
            i.[RestoredVersion] = x.[RestoredVersion],
            i.[NewVersion] = x.[NewVersion],
            i.[RunId] = @runId,
            i.[UpdatedUtc] = @now
        FROM @rows AS x
        INNER JOIN [osdu].[ReversalItem] AS i
            ON i.[PartitionId] = @partitionId AND i.[ReversalId] = @reversalId AND i.[DeliveryKey] = x.[DeliveryKey];

        SELECT (SELECT COUNT(*) FROM @rows), (SELECT COUNT(*) FROM @apply),
            (SELECT COUNT(*) FROM @moved WHERE [Outcome] = N'restored'), (SELECT COUNT(*) FROM @moved WHERE [Outcome] <> N'restored');
        """;

    /// <summary>One record of a source as a reversal lists it.</summary>
    public sealed record ResolvedReversalItem(
        Guid DeliveryKey, string? TargetId, long FirstAttemptId, long? FirstVersion, long? RunVersion, string Prior, long? PriorVersion, long? PriorAttemptId);

    /// <summary>One settlement as the settle statement reads it.</summary>
    public sealed record SettleRow(
        Guid DeliveryKey, string State, string Outcome, string? Detail, long? RestoredVersion, long? NewVersion, string? TargetStateJson, string? ResultJson,
        string? Note, string Change);

    /// <summary>The phases a submission's delivered attempts were written under.</summary>
    public static async Task<IReadOnlyList<string>> SubmissionPhasesAsync(OsduDbContext db, Guid submissionId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SubmissionPhasesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@submissionId", SqlDbType.UniqueIdentifier) { Value = submissionId });
            var phases = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                phases.Add(reader.GetString(0));
            }

            return phases;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The next page of a submission's delivered attempts in <paramref name="phase"/>, after attempt <paramref name="after"/>.</summary>
    public static async Task<IReadOnlyList<(long AttemptId, Guid DeliveryKey)>> SubmissionDeliveredPageAsync(
        OsduDbContext db, short partitionId, Guid submissionId, string phase, long after, int slice, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SubmissionDeliveredPageSql, slice);
            command.Parameters.Add(new SqlParameter("@submissionId", SqlDbType.UniqueIdentifier) { Value = submissionId });
            command.Parameters.Add(new SqlParameter("@phase", SqlDbType.NVarChar, 32) { Value = phase });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            var page = new List<(long, Guid)>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                page.Add((reader.GetInt64(0), reader.GetGuid(1)));
            }

            return page;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The next page of the records a run delivered itself in one ledger, after <paramref name="after"/> (<see cref="Guid.Empty"/> for the first).</summary>
    public static async Task<IReadOnlyList<Guid>> RunDeliveredPageAsync(
        OsduDbContext db, short partitionId, Guid flowId, Guid runId, Guid after, int slice, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, RunDeliveredPageSql, slice);
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.UniqueIdentifier) { Value = after });
            return await GuidsAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether <paramref name="source"/> delivered a record of one ledger: under the submission, under a submission the run
    /// planned, or by the run itself among the first <paramref name="probe"/> of its attempts there.
    /// </summary>
    public static async Task<bool> SourceDeliveredAsync(OsduDbContext db, short partitionId, Guid flowId, ReversalSource source, int probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(probe, 1);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            if (!source.IsRun)
            {
                return await ExistsAsync(connection, SubmissionDeliveredSql, "@submissionId", source.Id, partitionId, flowId, null, ct).ConfigureAwait(false);
            }

            return await ExistsAsync(connection, RunSubmissionsDeliveredSql, "@runId", source.Id, partitionId, flowId, null, ct).ConfigureAwait(false)
                || await ExistsAsync(connection, RunDeliveredSql, "@runId", source.Id, partitionId, flowId, probe, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> ExistsAsync(
        SqlConnection connection, string sql, string idParameter, Guid id, short partitionId, Guid flowId, int? probe, CancellationToken ct)
    {
        await using var command = Command(connection, null, sql, slice: null);
        command.Parameters.Add(new SqlParameter(idParameter, SqlDbType.UniqueIdentifier) { Value = id });
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
        if (probe is { } bound)
        {
            command.Parameters.Add(new SqlParameter("@probe", SqlDbType.Int) { Value = bound });
        }

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <summary>Whether a run delivered anything, or tried to, in one ledger.</summary>
    public static async Task<bool> RunTouchedAsync(OsduDbContext db, short partitionId, Guid flowId, Guid runId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, RunTouchedSql, slice: null);
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Lists the records <paramref name="keys"/> names in a reversal, each once; returns how many it added.</summary>
    public static async Task<int> CaptureSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, long reversalId, IReadOnlyCollection<Guid> keys, string submissionsJson, Guid? runId, DateTime now,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, CaptureSql, slice: null);
            AddSourceParameters(command, partitionId, flowId, keys, submissionsJson, runId);
            command.Parameters.Add(new SqlParameter("@reversalId", SqlDbType.BigInt) { Value = reversalId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The records <paramref name="keys"/> names as a reversal would list them, writing nothing.</summary>
    public static async Task<IReadOnlyList<ResolvedReversalItem>> ResolveAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyCollection<Guid> keys, string submissionsJson, Guid? runId, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, ResolvePreviewSql, slice: null);
            AddSourceParameters(command, partitionId, flowId, keys, submissionsJson, runId);
            var items = new List<ResolvedReversalItem>(keys.Count);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new ResolvedReversalItem(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7)));
            }

            return items;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>What OSDU held of each record before the write that left the version <paramref name="current"/> names for it.</summary>
    public static async Task<IReadOnlyList<KeyValuePair<DeliveryKey, PriorVersion>>> PriorVersionsAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyCollection<KeyValuePair<DeliveryKey, long>> current, CancellationToken ct)
    {
        if (current.Count == 0)
        {
            return [];
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, PriorVersionsSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@items", SqlDbType.NVarChar, -1)
            {
                Value = System.Text.Json.JsonSerializer.Serialize(current.Select(c => new { k = c.Key.Value, v = c.Value })),
            });
            var priors = new List<KeyValuePair<DeliveryKey, PriorVersion>>(current.Count);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                priors.Add(new(
                    new DeliveryKey(reader.GetGuid(0)),
                    new PriorVersion(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3))));
            }

            return priors;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How many records a source delivered.</summary>
    public static async Task<long> SourceRecordsAsync(OsduDbContext db, short partitionId, Guid flowId, string submissionsJson, Guid? runId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SourceRecordsSql, slice: null);
            AddSourceParameters(command, partitionId, flowId, null, submissionsJson, runId);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The first <paramref name="count"/> records a source delivered, in key order.</summary>
    public static async Task<IReadOnlyList<Guid>> SourceSampleAsync(
        OsduDbContext db, short partitionId, Guid flowId, string submissionsJson, Guid? runId, int count, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SourceSampleSql, count);
            AddSourceParameters(command, partitionId, flowId, null, submissionsJson, runId);
            return await GuidsAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Settles one slice of a reversal in one transaction (see the statement); returns what it settled and changed.</summary>
    public static Task<ReversalSettled> SettleReversalSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, long reversalId, IReadOnlyList<SettleRow> rows, string actor, long? activityId, Guid? runId,
        DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new
            {
                k = r.DeliveryKey,
                s = r.State,
                o = r.Outcome,
                d = r.Detail,
                rv = r.RestoredVersion,
                nv = r.NewVersion,
                ts = r.TargetStateJson,
                rj = r.ResultJson,
                n = r.Note,
                c = r.Change,
            }));
            await using var command = Command(connection, transaction, SettleSql, slice: null);
            command.Parameters.Add(new SqlParameter("@settled", SqlDbType.NVarChar, -1) { Value = payload });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@reversalId", SqlDbType.BigInt) { Value = reversalId });
            command.Parameters.Add(new SqlParameter("@actor", SqlDbType.NVarChar, 200) { Value = actor });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId is { } run ? run : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("The settlement of a reversal's slice answered nothing.");
            }

            return new ReversalSettled(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }, ct);

    /// <summary>The parameters every statement over a source takes: its ledger, its submissions, its run, and the records of the page.</summary>
    private static void AddSourceParameters(SqlCommand command, short partitionId, Guid flowId, IReadOnlyCollection<Guid>? keys, string submissionsJson, Guid? runId)
    {
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
        command.Parameters.Add(new SqlParameter("@submissions", SqlDbType.NVarChar, -1) { Value = submissionsJson });
        command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId is { } run ? run : DBNull.Value });
        if (keys is not null)
        {
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(keys) });
        }
    }
}
