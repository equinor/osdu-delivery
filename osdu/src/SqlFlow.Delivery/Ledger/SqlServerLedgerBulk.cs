using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The writes that carry the volume of a submission, as bulk copies and set-based statements on the module's SQL Server
/// database (osdu/docs/reference/concepts/ledger.md, Writes at scale): staging the pending records, appending what
/// workers learn under their leases (attempts and record events), and applying those events to the records. SQL Server
/// is the only provider the module runs on, so these are the ledger's only write path for them. A record is its
/// partition, its flow and its delivery key together, the record table's key, and every statement that writes one
/// matches on all three.
/// <para>Many nodes write these tables at once, so no write takes a lock on a range of keys, and no statement touches more
/// rows than the caller's slice: SQL Server turns the row locks of a statement that takes 5,000 of them on one index into a
/// lock on the whole table, which would stop every other node while it ran. Staging copies the whole batch once and
/// writes it a slice at a time, each slice in a transaction of its own, so a record is staged whole or not at all and a
/// failure keeps the slices before it. An append only adds rows, at the end of two tables clustered on ever-increasing
/// ids. An application takes a slice of a lease's events, settles their records and deletes them in one transaction.</para>
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    private const int BulkBatchSize = 5000;

    private const int BulkTimeoutSeconds = 600;

    /// <summary>The unique index through which the database refuses a second flow's claim on an OSDU id.</summary>
    private const string ClaimIndex = "IX_Record_ClaimedTargetId";

    /// <summary>The record table's key, through which the database refuses a record another staging inserted first.</summary>
    private const string RecordKey = "PK_Record";

    /// <summary>The event table's key: an application reads and deletes exactly its slice through it, never a range of the table.</summary>
    private const string EventKey = "PK_RecordEvent";

    /// <summary>How many times one slice of staging is tried when a concurrent writer got there first.</summary>
    private const int ContentionAttempts = 5;

    // The batch is copied once, sorted by key and numbered into slices, so each slice is one contiguous key range and a
    // statement reads its slice by the stage's clustered key. Existing marks the records the slice found, and locked.
    private const string PendingStageSql = """
        CREATE TABLE #PendingStage (
            [Slice] int NOT NULL,
            [Existing] bit NOT NULL DEFAULT 0,
            [FlowId] uniqueidentifier NOT NULL,
            [DeliveryKey] uniqueidentifier NOT NULL,
            [SourceKey] nvarchar(400) NOT NULL,
            [SourceKeyJson] nvarchar(2000) NULL,
            [Label] nvarchar(400) NULL,
            [MappingName] nvarchar(200) NOT NULL,
            [TargetId] nvarchar(500) NULL,
            [TargetVersion] bigint NULL,
            [LastSubmissionId] uniqueidentifier NULL,
            [PendingDocumentRef] nvarchar(64) NULL,
            [WorkBatch] int NULL,
            [PendingRenderContext] nvarchar(max) NULL,
            [PendingSourceFingerprint] nvarchar(200) NULL,
            [PendingSourceModifiedUtc] datetime2 NULL,
            [PendingSourceFileName] nvarchar(800) NULL,
            [PendingSourceRowNumber] bigint NULL,
            [PendingSourceUpdatedUtc] datetime2 NULL,
            [SourceInsertedUtc] datetime2 NULL,
            [PendingMetadataHash] nvarchar(64) NULL,
            [PendingPayloadHash] nvarchar(64) NULL,
            [PendingPayloadModifiedUtc] datetime2 NULL,
            [PendingPayloadLocation] nvarchar(2000) NULL,
            [PendingMetadata] bit NOT NULL,
            [PendingPayload] bit NOT NULL,
            [PendingReferences] nvarchar(max) NULL,
            [CacheSetId] bigint NULL,
            [ParentId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NULL,
            [PendingParentVersion] bigint NULL,
            [SeenPlanRequestedUtc] datetime2 NULL,
            [AnswersAnyRequest] bit NOT NULL,
            PRIMARY KEY ([Slice], [FlowId], [DeliveryKey]),
            UNIQUE ([FlowId], [DeliveryKey]));
        """;

    // The slice's records that exist are found by key and update-locked to the end of the transaction, so nothing else
    // writes them between the tests below and the update. Only rows that exist are locked, never a range of keys: a
    // record another staging inserts meanwhile is not held off here, and the insert below refuses it instead. A record
    // keeps the OSDU id it claimed, so that id, not the one this work was rendered with, is the one the work is delivered
    // to and the one the claim check compares (the plan holds a record whose render gives another, so the two agree). A
    // record that claimed none takes the id this work gives: an id a held render left on it was never written to. A
    // staging is one ledger's, so its partition is a parameter.
    private const string LockExistingSql = $$"""
        UPDATE s SET [Existing] = 1, [TargetId] = COALESCE(t.[ClaimedTargetId], s.[TargetId] COLLATE Latin1_General_100_BIN2)
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON t.[PartitionId] = @partitionId AND t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        WHERE s.[Slice] = @slice;
        """;

    // Work older than what the record already holds, delivered or queued (due, being delivered, or waiting for a record it
    // refers to), is taken out of the stage and named before the write. A comparison with a NULL column is unknown, and
    // refuses nothing.
    private const string RefuseOlderSql = $$"""
        DELETE s
        OUTPUT deleted.[DeliveryKey]
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON t.[PartitionId] = @partitionId AND t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE WHEN t.[PendingDocumentRef] IS NOT NULL AND t.[Status] IN (N'pending', N'delivering', N'waiting') THEN 1 ELSE 0 END AS [Queued]) AS q
        WHERE s.[Slice] = @slice
          AND ((s.[PendingSourceModifiedUtc] IS NOT NULL
                AND (s.[PendingSourceModifiedUtc] < t.[SourceModifiedUtc]
                     OR (q.[Queued] = 1 AND s.[PendingSourceModifiedUtc] < t.[PendingSourceModifiedUtc])))
           OR (s.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] IS NOT NULL
                AND (s.[PendingPayloadModifiedUtc] < t.[PayloadModifiedUtc]
                     OR (q.[Queued] = 1 AND t.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] < t.[PendingPayloadModifiedUtc]))));
        """;

    // Work for an OSDU id another record has claimed is taken out of the stage and named: a record of another flow, with
    // the owning flow and its name as its last submission recorded it, or another record of the same flow, whose key
    // gives the same id (an id made from the key's values, under a key or system the mapping no longer has), named by its
    // key. Ids compare exactly (the claim column's binary collation). The claim index is unique, so a stage row meets at
    // most one record, and each is one seek of that index, named here: the filtered index applies because the query
    // repeats its predicate, and a plan that went looking for the first match any other way would read the whole table
    // for every id nobody has claimed. The unique index is also what settles a race between two flows' intakes; this read
    // names the owner, from the owner's last submission, which its own ledger keeps under the owner's partition: one seek
    // of the submission table's key.
    private const string RefuseClaimedSql = $$"""
        DELETE s
        OUTPUT deleted.[DeliveryKey], deleted.[TargetId], t.[FlowId], sub.[FlowName],
            CASE WHEN t.[FlowId] = deleted.[FlowId] THEN t.[DeliveryKey] END, CASE WHEN t.[FlowId] = deleted.[FlowId] THEN t.[SourceKey] END
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (FORCESEEK ({{ClaimIndex}} ([ClaimedTargetId])))
            ON t.[ClaimedTargetId] = s.[TargetId] COLLATE Latin1_General_100_BIN2
            AND t.[ClaimedTargetId] IS NOT NULL
            AND (t.[FlowId] <> s.[FlowId] OR t.[DeliveryKey] <> s.[DeliveryKey])
        LEFT JOIN [osdu].[Submission] AS sub ON sub.[PartitionId] = t.[PartitionId] AND sub.[SubmissionId] = t.[LastSubmissionId]
        WHERE s.[Slice] = @slice AND s.[TargetId] IS NOT NULL;
        """;

    // Two records of the slice that give one OSDU id nobody has claimed (keys whose values make the same id): the one with
    // the lowest key is staged and claims it, and the others are taken out of the stage and named, each with the record
    // that keeps the id. Run after the claim check, so an id a record already claimed stays with that record. A record of
    // another slice that gives the same id finds this slice's claim when its own slice is checked.
    private const string RefuseRepeatedSql = """
        DELETE s
        OUTPUT deleted.[DeliveryKey], deleted.[TargetId], k.[FlowId], k.[DeliveryKey], k.[SourceKey]
        FROM #PendingStage AS s
        CROSS APPLY (
            SELECT TOP (1) f.[FlowId], f.[DeliveryKey], f.[SourceKey]
            FROM #PendingStage AS f
            WHERE f.[Slice] = @slice
              AND f.[TargetId] COLLATE Latin1_General_100_BIN2 = s.[TargetId] COLLATE Latin1_General_100_BIN2
              AND f.[DeliveryKey] < s.[DeliveryKey]
            ORDER BY f.[DeliveryKey]) AS k
        WHERE s.[Slice] = @slice AND s.[TargetId] IS NOT NULL;
        """;

    // The records the slice found are updated, and the others inserted. A record being delivered right now (its lease is
    // alive) keeps its status, lease, retry count and last error: the new work queues behind the delivery, whose
    // completion leaves it pending. A record a removal holds (its lease alive) takes the new work and stays held, so no
    // worker claims it while the removal takes it out of OSDU; removed, it drops the work, and let go, it is sent. Every right-hand side reads the row as it was. Staging answers the request to plan the
    // record again that the plan read (SeenPlanRequestedUtc), so that request is cleared; one made after the plan read the
    // record (an operator's release or redelivery, a cache rollout, while the run planned) stays for the next run, and with
    // no request read, nothing is cleared. Work that is not a plan's (AnswersAnyRequest) answers whatever request the record
    // carries. It claims the record's OSDU id for its flow the first time it queues a document, and the id OSDU held a
    // record at that an earlier hold named for adoption is no longer the record's to adopt, nor is an adoption asked of it
    // still to do. A record a run adopts carries the version OSDU holds of it (TargetVersion), which a record that never
    // delivered takes, so the write that follows is an update of that record. A record
    // another staging inserted after the slice looked is never overwritten here: the insert fails on the table's key,
    // and the slice runs again and finds it.
    private const string PendingWriteSql = $$"""
        DECLARE @updated int;
        UPDATE t SET
                [SourceKey] = s.[SourceKey], [SourceKeyJson] = COALESCE(s.[SourceKeyJson], t.[SourceKeyJson]), [Label] = s.[Label], [MappingName] = s.[MappingName],
                [TargetId] = COALESCE(s.[TargetId], t.[TargetId]),
                [ClaimedTargetId] = COALESCE(t.[ClaimedTargetId], s.[TargetId] COLLATE Latin1_General_100_BIN2),
                [OccupiedTargetId] = NULL, [OccupiedTargetVersion] = NULL, [AdoptRequestedUtc] = NULL,
                [TargetVersion] = COALESCE(t.[TargetVersion], s.[TargetVersion]),
                [LastSubmissionId] = s.[LastSubmissionId], [NextAttemptUtc] = NULL,
                [Status] = CASE WHEN f.[InFlight] = 1 THEN t.[Status] ELSE N'pending' END,
                [AttemptCount] = CASE WHEN f.[InFlight] = 1 THEN t.[AttemptCount] ELSE 0 END,
                [LastError] = CASE WHEN f.[InFlight] = 1 THEN t.[LastError] ELSE NULL END,
                [LeaseOwner] = CASE WHEN f.[InFlight] = 1 OR f.[Removing] = 1 THEN t.[LeaseOwner] ELSE NULL END,
                [PendingDocumentRef] = s.[PendingDocumentRef], [WorkBatch] = s.[WorkBatch], [PendingStepJson] = NULL,
                [PendingRenderContext] = s.[PendingRenderContext], [PendingSourceFingerprint] = s.[PendingSourceFingerprint],
                [PendingSourceModifiedUtc] = s.[PendingSourceModifiedUtc],
                [PendingSourceFileName] = s.[PendingSourceFileName], [PendingSourceRowNumber] = s.[PendingSourceRowNumber],
                [PendingSourceUpdatedUtc] = s.[PendingSourceUpdatedUtc],
                [SourceInsertedUtc] = COALESCE(s.[SourceInsertedUtc], t.[SourceInsertedUtc]),
                [PendingMetadataHash] = s.[PendingMetadataHash], [PendingPayloadHash] = s.[PendingPayloadHash],
                [PendingPayloadModifiedUtc] = s.[PendingPayloadModifiedUtc],
                [PendingPayloadLocation] = s.[PendingPayloadLocation], [PendingMetadata] = s.[PendingMetadata], [PendingPayload] = s.[PendingPayload],
                [PendingReferences] = s.[PendingReferences], [WaitingFor] = NULL,
                [CacheSetId] = s.[CacheSetId], [ParentId] = s.[ParentId], [PendingParentVersion] = s.[PendingParentVersion], [Blocked] = 0, [ProblemHash] = NULL,
                [PlanRequestedUtc] = CASE WHEN s.[AnswersAnyRequest] = 1 OR t.[PlanRequestedUtc] = s.[SeenPlanRequestedUtc] THEN NULL ELSE t.[PlanRequestedUtc] END, [UpdatedUtc] = @now
        FROM [osdu].[Record] AS t WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
        INNER JOIN #PendingStage AS s ON t.[PartitionId] = @partitionId AND t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT
            CASE WHEN t.[Status] = N'delivering' AND EXISTS (
                SELECT 1 FROM [osdu].[Lease] AS l WHERE l.[PartitionId] = t.[PartitionId] AND l.[Token] = t.[LeaseOwner] AND l.[ExpiresUtc] > @now AND l.[ForRemoval] = 0)
                THEN 1 ELSE 0 END AS [InFlight],
            CASE WHEN EXISTS (
                SELECT 1 FROM [osdu].[Lease] AS l WHERE l.[PartitionId] = t.[PartitionId] AND l.[Token] = t.[LeaseOwner] AND l.[ExpiresUtc] > @now AND l.[ForRemoval] = 1)
                THEN 1 ELSE 0 END AS [Removing]) AS f
        WHERE s.[Slice] = @slice AND s.[Existing] = 1;
        SET @updated = @@ROWCOUNT;
        INSERT INTO [osdu].[Record] ([PartitionId], [DeliveryKey], [FlowId], [SourceKey], [SourceKeyJson], [Label], [MappingName], [TargetId], [ClaimedTargetId], [Status], [LastSubmissionId], [AttemptCount],
                [PendingDocumentRef], [WorkBatch], [PendingRenderContext], [PendingSourceFingerprint], [PendingSourceModifiedUtc],
                [PendingSourceFileName], [PendingSourceRowNumber], [PendingSourceUpdatedUtc], [SourceInsertedUtc],
                [PendingMetadataHash], [PendingPayloadHash], [PendingPayloadModifiedUtc],
                [PendingPayloadLocation], [PendingMetadata], [PendingPayload], [PendingReferences], [CacheSetId], [ParentId], [PendingParentVersion], [Blocked], [CreatedUtc], [UpdatedUtc])
        SELECT @partitionId, s.[DeliveryKey], s.[FlowId], s.[SourceKey], s.[SourceKeyJson], s.[Label], s.[MappingName], s.[TargetId], s.[TargetId] COLLATE Latin1_General_100_BIN2, N'pending', s.[LastSubmissionId], 0,
                s.[PendingDocumentRef], s.[WorkBatch], s.[PendingRenderContext], s.[PendingSourceFingerprint], s.[PendingSourceModifiedUtc],
                s.[PendingSourceFileName], s.[PendingSourceRowNumber], s.[PendingSourceUpdatedUtc], s.[SourceInsertedUtc],
                s.[PendingMetadataHash], s.[PendingPayloadHash], s.[PendingPayloadModifiedUtc],
                s.[PendingPayloadLocation], s.[PendingMetadata], s.[PendingPayload], s.[PendingReferences], s.[CacheSetId], s.[ParentId], s.[PendingParentVersion], 0, @now, @now
        FROM #PendingStage AS s
        WHERE s.[Slice] = @slice AND s.[Existing] = 0;
        SELECT @updated + @@ROWCOUNT;
        """;

    // The next slice of a lease's events, in record order, and the latest of each record among them. A record's events
    // are applied in the order they were appended, a slice at a time, so a slice may end part way through a record: its
    // latest event there is older than the ones the next slice applies, and a step carries every step before it. A try's
    // completion is its record's last event. A record another lease holds now is that lease's to settle. The events go in
    // the same transaction as their application, so each is applied once. A lease is one ledger's, so its events, and the
    // records they settle, are one partition's, which the statement names back.
    private const string ApplyEventsSql = $$"""
        CREATE TABLE #Events ([PartitionId] smallint NOT NULL, [EventId] bigint NOT NULL, PRIMARY KEY ([PartitionId], [EventId]));
        INSERT INTO #Events ([PartitionId], [EventId])
        SELECT TOP (@slice) e.[PartitionId], e.[EventId]
        FROM [osdu].[RecordEvent] AS e
        WHERE e.[LeaseToken] = @token
        ORDER BY e.[FlowId], e.[DeliveryKey], e.[EventId];
        DECLARE @events int = @@ROWCOUNT;
        DECLARE @partition smallint = (SELECT TOP (1) [PartitionId] FROM #Events);

        SELECT l.* INTO #Latest
        FROM (
            SELECT e.*, ROW_NUMBER() OVER (PARTITION BY e.[PartitionId], e.[FlowId], e.[DeliveryKey] ORDER BY e.[EventId] DESC) AS [Rank]
            FROM #Events AS n
            INNER JOIN [osdu].[RecordEvent] AS e WITH (FORCESEEK ({{EventKey}} ([PartitionId], [EventId]))) ON e.[PartitionId] = n.[PartitionId] AND e.[EventId] = n.[EventId]) AS l
        WHERE l.[Rank] = 1;

        {{CompletionUpdateSql}}
        DECLARE @applied int = @@ROWCOUNT;

        -- The ids of the records a completion just promoted: what they delivered is in OSDU now, and the records waiting
        -- for those ids go back to pending once this transaction commits. A record another lease holds was not applied.
        CREATE TABLE #Landed ([TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);
        INSERT INTO #Landed ([TargetId])
        SELECT DISTINCT r.[TargetId]
        FROM #Latest AS s
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = s.[PartitionId] AND r.[FlowId] = s.[FlowId] AND r.[DeliveryKey] = s.[DeliveryKey]
        WHERE s.[Kind] = N'completion' AND s.[Promote] = 1 AND r.[TargetId] IS NOT NULL AND r.[LeaseOwner] IS NULL;

        UPDATE r SET [PendingStepJson] = s.[StepJson]
        FROM [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
        INNER JOIN #Latest AS s ON r.[PartitionId] = s.[PartitionId] AND r.[FlowId] = s.[FlowId] AND r.[DeliveryKey] = s.[DeliveryKey]
        WHERE s.[Kind] = N'step'
          AND (r.[LeaseOwner] IS NULL OR r.[LeaseOwner] = @token)
          AND r.[PendingDocumentRef] = s.[ClaimDocumentRef]
          AND (r.[LastSubmissionId] = s.[ClaimSubmissionId] OR (r.[LastSubmissionId] IS NULL AND s.[ClaimSubmissionId] IS NULL));

        DELETE e FROM [osdu].[RecordEvent] AS e WITH (FORCESEEK ({{EventKey}} ([PartitionId], [EventId])))
        INNER JOIN #Events AS n ON n.[PartitionId] = e.[PartitionId] AND n.[EventId] = e.[EventId];
        DROP TABLE #Latest;
        DROP TABLE #Events;
        SELECT @events, @applied, @partition;
        SELECT [TargetId] FROM #Landed;
        DROP TABLE #Landed;
        """;

    // How a try's completion settles its record. A record now carrying other pending work than the try claimed
    // (newer work queued behind it) promotes what the try delivered from the claim and goes back to pending, keeping
    // the newer work and its step progress; any other record settles as the completion says, promoting its own
    // pending columns. A promotion carries the origin of the version it delivered: the claim's when superseded, the
    // pending origin otherwise, and only when the work names one. The delivery time is when the try ended. A promotion
    // ends an operator's acceptance of the pending document: what is queued later is judged again, even the same document.
    // A try whose pending document could not be read (Replan) drops it, and the record, unblocked and with no problem, is
    // asked to be planned again from its row: a release would only send the missing document again. A completion that
    // reaches a record no lease holds (the try's own lease ran out, the record was handed back, and the worker finished
    // after all) is applied only while the record stands where the hand-back left it: pending, or failed once its tries
    // were used up, with the try count the try's claim left. A later claim, release, removal, delivery or newly queued
    // work changed the count, and a plan that held the record changed its status; what they did stands, and the try's
    // attempt, written when it was appended, is on the record's history either way.
    private const string CompletionUpdateSql = $$"""
        UPDATE r SET
            [Status] = CASE WHEN x.[Superseded] = 1 THEN N'pending' ELSE s.[Status] END,
            [Blocked] = CASE WHEN x.[Superseded] = 1 OR s.[Replan] = 1 THEN CAST(0 AS bit) WHEN s.[Status] IN (N'held', N'failed') THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
            [ProblemHash] = CASE WHEN x.[Superseded] = 0 AND s.[Replan] = 0 AND s.[Status] IN (N'held', N'failed') THEN s.[ProblemHash] ELSE NULL END,
            [PlanRequestedUtc] = CASE WHEN s.[Replan] = 1 AND x.[Superseded] = 0 THEN COALESCE(r.[PlanRequestedUtc], @now) ELSE r.[PlanRequestedUtc] END,
            [ValidationOutcome] = COALESCE(s.[ValidationOutcome], r.[ValidationOutcome]),
            [ValidationProblems] = CASE WHEN s.[ValidationOutcome] IS NULL THEN r.[ValidationProblems] ELSE s.[ValidationProblems] END,
            [ValidatedUtc] = CASE WHEN s.[ValidationOutcome] IS NULL THEN r.[ValidatedUtc] ELSE s.[ValidatedUtc] END,
            [AssertionFailures] = CASE WHEN s.[ValidationOutcome] IS NULL THEN r.[AssertionFailures] ELSE s.[AssertionFailures] END,
            [LeaseOwner] = NULL, [UpdatedUtc] = @now,
            [NextAttemptUtc] = CASE WHEN x.[Superseded] = 1 THEN NULL ELSE s.[NextAttemptUtc] END,
            [LastError] = CASE WHEN x.[Superseded] = 1 THEN NULL ELSE s.[Error] END,
            [TargetId] = COALESCE(s.[TargetId], r.[TargetId]), [TargetVersion] = COALESCE(s.[TargetVersion], r.[TargetVersion]),
            [TargetStateJson] = COALESCE(s.[TargetStateJson], r.[TargetStateJson]),
            [PendingStepJson] = CASE WHEN x.[Superseded] = 1 THEN r.[PendingStepJson] ELSE s.[PendingStepJson] END,
            [RenderContext] = CASE WHEN s.[Promote] = 0 THEN r.[RenderContext]
                WHEN x.[Superseded] = 1 THEN COALESCE(s.[ClaimRenderContext], r.[RenderContext])
                ELSE COALESCE(r.[PendingRenderContext], r.[RenderContext]) END,
            [SourceFingerprint] = CASE WHEN s.[Promote] = 0 THEN r.[SourceFingerprint]
                WHEN x.[Superseded] = 1 THEN COALESCE(s.[ClaimSourceFingerprint], r.[SourceFingerprint])
                ELSE COALESCE(r.[PendingSourceFingerprint], r.[SourceFingerprint]) END,
            [SourceModifiedUtc] = CASE WHEN s.[Promote] = 0 THEN r.[SourceModifiedUtc]
                WHEN x.[Superseded] = 1 THEN COALESCE(s.[ClaimSourceModifiedUtc], r.[SourceModifiedUtc])
                ELSE COALESCE(r.[PendingSourceModifiedUtc], r.[SourceModifiedUtc]) END,
            [SourceFileName] = CASE WHEN s.[Promote] = 0 THEN r.[SourceFileName]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimSourceUpdatedUtc] IS NULL AND s.[ClaimSourceFileName] IS NULL THEN r.[SourceFileName] ELSE s.[ClaimSourceFileName] END
                WHEN r.[PendingSourceUpdatedUtc] IS NULL AND r.[PendingSourceFileName] IS NULL THEN r.[SourceFileName]
                ELSE r.[PendingSourceFileName] END,
            [SourceRowNumber] = CASE WHEN s.[Promote] = 0 THEN r.[SourceRowNumber]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimSourceUpdatedUtc] IS NULL AND s.[ClaimSourceFileName] IS NULL THEN r.[SourceRowNumber] ELSE s.[ClaimSourceRowNumber] END
                WHEN r.[PendingSourceUpdatedUtc] IS NULL AND r.[PendingSourceFileName] IS NULL THEN r.[SourceRowNumber]
                ELSE r.[PendingSourceRowNumber] END,
            [SourceUpdatedUtc] = CASE WHEN s.[Promote] = 0 THEN r.[SourceUpdatedUtc]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimSourceUpdatedUtc] IS NULL AND s.[ClaimSourceFileName] IS NULL THEN r.[SourceUpdatedUtc] ELSE s.[ClaimSourceUpdatedUtc] END
                WHEN r.[PendingSourceUpdatedUtc] IS NULL AND r.[PendingSourceFileName] IS NULL THEN r.[SourceUpdatedUtc]
                ELSE r.[PendingSourceUpdatedUtc] END,
            [MetadataHash] = CASE WHEN s.[Promote] = 0 THEN r.[MetadataHash]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimMetadata] = 1 THEN s.[ClaimMetadataHash] ELSE r.[MetadataHash] END
                WHEN r.[PendingMetadata] = 1 THEN r.[PendingMetadataHash] ELSE r.[MetadataHash] END,
            [ParentVersion] = CASE WHEN s.[Promote] = 0 THEN r.[ParentVersion]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimMetadata] = 1 THEN s.[ClaimParentVersion] ELSE r.[ParentVersion] END
                WHEN r.[PendingMetadata] = 1 THEN r.[PendingParentVersion] ELSE r.[ParentVersion] END,
            [PayloadHash] = CASE WHEN s.[Promote] = 0 THEN r.[PayloadHash]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimPayload] = 1 THEN s.[ClaimPayloadHash] ELSE r.[PayloadHash] END
                WHEN r.[PendingPayload] = 1 THEN r.[PendingPayloadHash] ELSE r.[PayloadHash] END,
            [PayloadModifiedUtc] = CASE WHEN s.[Promote] = 0 THEN r.[PayloadModifiedUtc]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimPayload] = 1 THEN COALESCE(s.[ClaimPayloadModifiedUtc], r.[PayloadModifiedUtc]) ELSE r.[PayloadModifiedUtc] END
                WHEN r.[PendingPayload] = 1 THEN COALESCE(r.[PendingPayloadModifiedUtc], r.[PayloadModifiedUtc]) ELSE r.[PayloadModifiedUtc] END,
            [LastDeliveredUtc] = CASE WHEN s.[Promote] = 1 AND s.[NothingSent] = 0 THEN s.[AtUtc] ELSE r.[LastDeliveredUtc] END,
            [LastVerifiedUtc] = CASE WHEN s.[Promote] = 1 AND s.[NothingSent] = 0 THEN NULL ELSE r.[LastVerifiedUtc] END,
            [LastVerifyOutcome] = CASE WHEN s.[Promote] = 1 AND s.[NothingSent] = 0 THEN NULL ELSE r.[LastVerifyOutcome] END,
            [PendingDocumentRef] = CASE WHEN (s.[Promote] = 1 OR s.[Replan] = 1) AND x.[Superseded] = 0 THEN NULL ELSE r.[PendingDocumentRef] END,
            [WorkBatch] = CASE WHEN (s.[Promote] = 1 OR s.[Replan] = 1) AND x.[Superseded] = 0 THEN NULL ELSE r.[WorkBatch] END,
            [PendingMetadata] = CASE WHEN (s.[Promote] = 1 OR s.[Replan] = 1) AND x.[Superseded] = 0 THEN CAST(0 AS bit) ELSE r.[PendingMetadata] END,
            [PendingPayload] = CASE WHEN (s.[Promote] = 1 OR s.[Replan] = 1) AND x.[Superseded] = 0 THEN CAST(0 AS bit) ELSE r.[PendingPayload] END,
            [PendingPayloadLocation] = CASE WHEN (s.[Promote] = 1 OR s.[Replan] = 1) AND x.[Superseded] = 0 THEN NULL ELSE r.[PendingPayloadLocation] END,
            [PendingReferences] = CASE WHEN (s.[Promote] = 1 OR s.[Replan] = 1) AND x.[Superseded] = 0 THEN NULL ELSE r.[PendingReferences] END,
            [AcceptedMetadataHash] = CASE WHEN s.[Promote] = 1 AND x.[Superseded] = 0 THEN NULL ELSE r.[AcceptedMetadataHash] END,
            [AttemptCount] = CASE WHEN s.[Promote] = 1 OR x.[Superseded] = 1 THEN 0
                WHEN s.[NothingSent] = 1 AND r.[AttemptCount] > 0 THEN r.[AttemptCount] - 1
                ELSE r.[AttemptCount] END
        FROM [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
        INNER JOIN #Latest AS s ON r.[PartitionId] = s.[PartitionId] AND r.[FlowId] = s.[FlowId] AND r.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE
            WHEN s.[ClaimDocumentRef] IS NOT NULL AND r.[PendingDocumentRef] IS NOT NULL
                 AND (r.[PendingDocumentRef] <> s.[ClaimDocumentRef]
                      OR r.[LastSubmissionId] <> s.[ClaimSubmissionId]
                      OR (r.[LastSubmissionId] IS NULL AND s.[ClaimSubmissionId] IS NOT NULL)
                      OR (r.[LastSubmissionId] IS NOT NULL AND s.[ClaimSubmissionId] IS NULL))
            THEN 1 ELSE 0 END AS [Superseded]) AS x
        WHERE s.[Kind] = N'completion'
          AND (r.[LeaseOwner] = @token OR (r.[LeaseOwner] IS NULL AND (s.[ClaimAttemptCount] IS NULL
                OR (r.[AttemptCount] = s.[ClaimAttemptCount] AND r.[Status] IN (N'pending', N'failed')))));
        """;

    /// <summary>
    /// Stages one flow's pending work, <paramref name="slice"/> records to a transaction. A slice that lost a race is
    /// rolled back and run again, and then reads what the winner left: another flow's intake claimed one of the same OSDU
    /// ids between the slice's claim check and its write (the claim's unique index refuses the write, and the check now
    /// names the owner), another staging of this flow inserted one of the slice's records after the slice looked (the
    /// table's key refuses the insert, and the record is now found and compared), or the database ended a deadlock by
    /// rolling the slice back.
    /// </summary>
    public static async Task<PendingStaging> UpsertPendingAsync(OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<RecordState> records, int slice, DateTime now, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slice, 1);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var connection = (SqlConnection)db.Database.GetDbConnection();
                await ExecuteAsync(connection, null, PendingStageSql, ct).ConfigureAwait(false);
                using (var table = PendingTable(flowId, records, slice))
                {
                    await BulkCopyAsync(connection, null, "#PendingStage", table, ct).ConfigureAwait(false);
                }

                var staged = 0;
                var refused = new List<DeliveryKey>();
                var conflicts = new List<TargetIdConflict>();
                var slices = (records.Count + slice - 1) / slice;
                for (var index = 0; index < slices; index++)
                {
                    var written = await StageSliceAsync(db, connection, partitionId, flowId, index, now, ct).ConfigureAwait(false);
                    staged += written.Staged;
                    refused.AddRange(written.Refused);
                    conflicts.AddRange(written.Conflicts);
                }

                await ExecuteAsync(connection, null, "DROP TABLE #PendingStage;", ct).ConfigureAwait(false);
                return new PendingStaging(staged, refused, conflicts);
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static async Task<PendingStaging> StageSliceAsync(OsduDbContext db, SqlConnection connection, short partitionId, Guid flowId, int slice, DateTime now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Disposing a transaction that was not committed rolls it back, the stage's changes included, so a
                // slice that runs again starts from the rows it was copied with.
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var transaction = (SqlTransaction)tx.GetDbTransaction();
                await SliceAsync(connection, transaction, LockExistingSql, slice, partitionId, ct).ConfigureAwait(false);
                var refused = await KeysAsync(connection, transaction, RefuseOlderSql, slice, partitionId, ct).ConfigureAwait(false);
                IReadOnlyList<TargetIdConflict> conflicts = [
                    .. await ConflictsAsync(connection, transaction, slice, ct).ConfigureAwait(false),
                    .. await RepeatsAsync(connection, transaction, slice, ct).ConfigureAwait(false),
                ];
                var staged = await ScalarAsync(connection, transaction, PendingWriteSql, now, partitionId, flowId, slice, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return new PendingStaging(staged, refused, conflicts);
            }
            catch (Exception ex) when (attempt < ContentionAttempts && IsContention(ex))
            {
                // Another writer got there first. Waiting a moment, longer each time and never the same for two
                // stagings, keeps the two from meeting in the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a write failed only because another writer got there first: the claim's unique index refused an id another
    /// flow's record claimed meanwhile, the record table's key refused a record another staging inserted meanwhile (2601,
    /// 2627), or the database ended a deadlock by rolling this write back (1205). The context's execution strategy reports
    /// a deadlock wrapped as a transient failure, so the causes are searched too.
    /// </summary>
    internal static bool IsContention(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql
                && sql.Errors.Cast<SqlError>().Any(e => e.Number == DeadlockVictim || (e.Number is 2601 or 2627 && IsRaceIndex(e.Message))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a statement failed because the database chose it as the victim of a deadlock, and rolled it back.</summary>
    internal static bool IsDeadlock(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number == DeadlockVictim))
            {
                return true;
            }
        }

        return false;
    }

    private const int DeadlockVictim = 1205;

    // SQL Server quotes the index or constraint name in the message of a duplicate key error.
    private static bool IsRaceIndex(string message)
        => message.Contains($"'{ClaimIndex}'", StringComparison.Ordinal) || message.Contains($"'{RecordKey}'", StringComparison.Ordinal);

    /// <summary>
    /// Appends a lease's attempts and record events in one transaction: two bulk copies, and nothing updated. A deadlock
    /// rolls both back, and the caller writes them again.
    /// </summary>
    public static Task<int> AppendAsync(OsduDbContext db, IReadOnlyList<DeliveryAttempt> attempts, IReadOnlyList<DeliveryRecordEvent> events, ArtifactWrites? artifacts, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            if (attempts.Count > 0)
            {
                using var table = AttemptTable(attempts);
                await BulkCopyAsync(connection, transaction, "[osdu].[Attempt]", table, ct).ConfigureAwait(false);
            }

            if (events.Count > 0)
            {
                using var table = EventTable(events);
                await BulkCopyAsync(connection, transaction, "[osdu].[RecordEvent]", table, ct).ConfigureAwait(false);
            }

            // What the steps created, and what the tries and undos settled of it, in the transaction that writes them: an id a
            // step minted is in the ledger exactly when the step is.
            if (artifacts is not null)
            {
                await WriteArtifactsAsync(connection, transaction, artifacts, ct).ConfigureAwait(false);
            }

            return attempts.Count + events.Count;
        }, ct);

    /// <summary>What applying a slice of a lease's events did.</summary>
    /// <param name="Records">How many events the slice took: fewer than the slice when none are left after it.</param>
    /// <param name="Applied">How many tries it settled.</param>
    /// <param name="PartitionId">The partition the lease's records are kept under, or null when it held no event.</param>
    /// <param name="Landed">The OSDU ids of the records those tries landed, all in that partition.</param>
    public sealed record AppliedEvents(int Records, int Applied, short? PartitionId, IReadOnlyList<string> Landed);

    /// <summary>
    /// Applies the next <paramref name="slice"/> events of a lease to their records and deletes them, in one transaction.
    /// </summary>
    public static Task<AppliedEvents> ApplyEventsAsync(OsduDbContext db, string token, int slice, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, ApplyEventsSql, slice);
            command.Parameters.Add(new SqlParameter("@token", SqlDbType.NVarChar, DeliveryModel.MaxLeaseTokenLength) { Value = token });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException($"Applying the events of lease {token} returned no counts.");
            }

            var records = reader.GetInt32(0);
            var applied = reader.GetInt32(1);
            short? partition = reader.IsDBNull(2) ? null : reader.GetInt16(2);
            if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException($"Applying the events of lease {token} did not name the records that landed.");
            }

            var landed = new List<string>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                landed.Add(reader.GetString(0));
            }

            return new AppliedEvents(records, applied, partition, landed);
        }, ct);

    // Takes the lock every decision to wait is made under, for the rest of the caller's transaction.
    private const string WaitLockSql = """
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
        SELECT @granted;
        """;

    /// <summary>
    /// Takes the application lock every decision to wait is made under, held until the caller's transaction ends. Throws
    /// when another decision held it for longer than <paramref name="timeoutMs"/>, which a decision of a few reads never does.
    /// </summary>
    public static async Task TakeWaitLockAsync(OsduDbContext db, string resource, int timeoutMs, CancellationToken ct)
    {
        var (connection, transaction) = Current(db);
        await using var command = Command(connection, transaction, WaitLockSql, slice: null);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMs });
        var granted = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            throw new DeliveryException(
                $"The ledger could not take the lock its decisions to wait are made under ({resource}) within {timeoutMs / 1000} seconds (sp_getapplock answered {granted}); another claim held it that long, which a decision never needs. The claim is tried again on the worker's next pass.");
        }
    }

    // The decided records, each still pending with the document the decision read and held by no lease, left waiting.
    // The records are one ledger's, so the partition and the flow are parameters and each is found by the table's key.
    private const string MarkWaitingSql = $$"""
        UPDATE r SET r.[Status] = N'waiting', r.[WaitingFor] = w.[WaitingFor], r.[LastError] = w.[Reason], r.[UpdatedUtc] = @now
        OUTPUT inserted.[DeliveryKey]
        FROM OPENJSON(@waits) WITH (
            [DeliveryKey] uniqueidentifier '$.key',
            [DocumentRef] nvarchar(64) '$.ref',
            [WaitingFor] nvarchar(500) '$.id',
            [Reason] nvarchar(2000) '$.reason') AS w
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = w.[DeliveryKey]
        WHERE r.[Status] = N'pending' AND r.[LeaseOwner] IS NULL AND r.[PendingDocumentRef] = w.[DocumentRef];
        """;

    /// <summary>Leaves the decided records waiting in the caller's transaction, and returns the ones it did.</summary>
    public static async Task<IReadOnlyList<Guid>> MarkWaitingAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<(Guid Key, string DocumentRef, string WaitingFor, string Reason)> waits, DateTime now, CancellationToken ct)
    {
        var (connection, transaction) = Current(db);
        var payload = System.Text.Json.JsonSerializer.Serialize(waits.Select(w => new { key = w.Key, @ref = w.DocumentRef, id = w.WaitingFor, reason = w.Reason }));
        await using var command = Command(connection, transaction, MarkWaitingSql, slice: null);
        command.Parameters.Add(new SqlParameter("@waits", SqlDbType.NVarChar, -1) { Value = payload });
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        return await GuidsAsync(command, ct).ConfigureAwait(false);
    }

    // A slice of the flow's waiting records (the named ones, when a key list is given) whose wait is over: the record
    // holding the id they wait for landed, or no record holds it any more but ones removed from OSDU. A holder is found
    // through the partition's id index, whose collation folds case, and compared exactly as OSDU compares ids: an id is
    // referred to within its partition, so its holders are the waiting record's partition's records.
    private const string ReleaseResolvedSql = """
        UPDATE TOP (@slice) w SET w.[Status] = N'pending', w.[WaitingFor] = NULL, w.[NextAttemptUtc] = NULL, w.[LastError] = NULL, w.[UpdatedUtc] = @now
        OUTPUT inserted.[DeliveryKey]
        FROM [osdu].[Record] AS w
        WHERE w.[PartitionId] = @partitionId AND w.[FlowId] = @flowId AND w.[Status] = N'waiting'
          AND (@keys IS NULL OR w.[DeliveryKey] IN (SELECT CAST(k.[value] AS uniqueidentifier) FROM OPENJSON(@keys) AS k))
          AND (w.[WaitingFor] IS NULL
               OR EXISTS (
                    SELECT 1 FROM [osdu].[Record] AS p
                    WHERE p.[PartitionId] = w.[PartitionId]
                      AND p.[TargetId] = w.[WaitingFor] COLLATE DATABASE_DEFAULT
                      AND p.[TargetId] COLLATE Latin1_General_100_BIN2 = w.[WaitingFor]
                      AND (p.[Status] = N'delivered' OR p.[TargetVersion] IS NOT NULL))
               OR NOT EXISTS (
                    SELECT 1 FROM [osdu].[Record] AS p
                    WHERE p.[PartitionId] = w.[PartitionId]
                      AND p.[TargetId] = w.[WaitingFor] COLLATE DATABASE_DEFAULT
                      AND p.[TargetId] COLLATE Latin1_General_100_BIN2 = w.[WaitingFor]
                      AND p.[Status] <> N'deleted'
                      AND NOT (p.[FlowId] = w.[FlowId] AND p.[DeliveryKey] = w.[DeliveryKey])));
        """;

    /// <summary>
    /// Sends back to pending up to <paramref name="slice"/> of the flow's waiting records whose wait is over, of the ones
    /// <paramref name="keys"/> names when given, in one statement of its own. Returns their keys.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ReleaseResolvedWaitsAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<Guid>? keys, int slice, DateTime now, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, ReleaseResolvedSql, slice);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1)
            {
                Value = keys is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(keys),
            });
            return await GuidsAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    // Which of the listed ledger identities hold a record at all: an EXISTS per identity, a seek on the key the records
    // lead with (the ledger's partition, from the directory, and its identity), so it costs the same whatever the ledger
    // holds. One statement for any number of identities; a UNION of one query per identity nests as deep as the list is
    // long, which the provider's query translation cannot walk.
    private const string FlowsWithRecordsSql = """
        SELECT l.[FlowId]
        FROM (SELECT DISTINCT CAST(j.[value] AS uniqueidentifier) AS [FlowId] FROM OPENJSON(@flows) AS j) AS f
        INNER JOIN [osdu].[Ledger] AS l ON l.[FlowId] = f.[FlowId]
        WHERE EXISTS (SELECT 1 FROM [osdu].[Record] AS r WHERE r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId]);
        """;

    // Which of the listed ledger identities have an activity on the audit trail: the same EXISTS per identity, a seek on
    // the trail's [PartitionId, FlowId, StartedUtc] index.
    private const string FlowsWithActivitiesSql = """
        SELECT l.[FlowId]
        FROM (SELECT DISTINCT CAST(j.[value] AS uniqueidentifier) AS [FlowId] FROM OPENJSON(@flows) AS j) AS f
        INNER JOIN [osdu].[Ledger] AS l ON l.[FlowId] = f.[FlowId]
        WHERE EXISTS (SELECT 1 FROM [osdu].[Activity] AS a WHERE a.[PartitionId] = l.[PartitionId] AND a.[FlowId] = l.[FlowId]);
        """;

    /// <summary>The ledger identities among <paramref name="flowIds"/> that hold at least one record, in any state.</summary>
    public static Task<IReadOnlyList<Guid>> FlowsWithRecordsAsync(OsduDbContext db, IReadOnlyCollection<Guid> flowIds, CancellationToken ct)
        => FlowsHoldingAsync(db, FlowsWithRecordsSql, flowIds, ct);

    /// <summary>The ledger identities among <paramref name="flowIds"/> with at least one activity, of any kind or outcome.</summary>
    public static Task<IReadOnlyList<Guid>> FlowsWithActivitiesAsync(OsduDbContext db, IReadOnlyCollection<Guid> flowIds, CancellationToken ct)
        => FlowsHoldingAsync(db, FlowsWithActivitiesSql, flowIds, ct);

    /// <summary>Runs one of the statements that answer which of <paramref name="flowIds"/> hold a row, given as JSON in <c>@flows</c>.</summary>
    private static async Task<IReadOnlyList<Guid>> FlowsHoldingAsync(OsduDbContext db, string sql, IReadOnlyCollection<Guid> flowIds, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, sql, slice: null);
            command.Parameters.Add(new SqlParameter("@flows", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(flowIds) });
            return await GuidsAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The context's open connection and the transaction it is in, for a statement that belongs to that transaction.</summary>
    private static (SqlConnection Connection, SqlTransaction? Transaction) Current(OsduDbContext db)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction;
        return (connection, transaction);
    }

    /// <summary>Runs a statement whose result set is one key per row, and reads the keys.</summary>
    private static async Task<IReadOnlyList<Guid>> GuidsAsync(SqlCommand command, CancellationToken ct)
    {
        var keys = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetGuid(0));
        }

        return keys;
    }

    private static async Task<T> InTransactionAsync<T>(OsduDbContext db, Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var connection = (SqlConnection)db.Database.GetDbConnection();
                var transaction = (SqlTransaction)tx.GetDbTransaction();
                var result = await work(connection, transaction).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static async Task BulkCopyAsync(SqlConnection connection, SqlTransaction? transaction, string destination, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destination,
            BatchSize = BulkBatchSize,
            BulkCopyTimeout = BulkTimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice: null);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs a statement over one slice of the stage, in the partition of the ledger it stages.</summary>
    private static async Task SliceAsync(SqlConnection connection, SqlTransaction transaction, string sql, int slice, short partitionId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice);
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, SqlTransaction transaction, string sql, DateTime now, short partitionId, Guid flowId, int? slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice);
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, int? slice)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = BulkTimeoutSeconds;
        if (slice is { } value)
        {
            command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = value });
        }

        return command;
    }

    /// <summary>
    /// Runs the claim check over one slice and reads back the records it took out of the stage, with the flow that owns each
    /// id and, when it is the same flow, the record.
    /// </summary>
    private static async Task<IReadOnlyList<TargetIdConflict>> ConflictsAsync(SqlConnection connection, SqlTransaction transaction, int slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, RefuseClaimedSql, slice);
        var conflicts = new List<TargetIdConflict>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            conflicts.Add(new TargetIdConflict(
                new DeliveryKey(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : new DeliveryKey(reader.GetGuid(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return conflicts;
    }

    /// <summary>
    /// Runs the check for one id given by several records of the slice, and reads back the records it took out of the
    /// stage, each with the record of the same flow that keeps the id.
    /// </summary>
    private static async Task<IReadOnlyList<TargetIdConflict>> RepeatsAsync(SqlConnection connection, SqlTransaction transaction, int slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, RefuseRepeatedSql, slice);
        var conflicts = new List<TargetIdConflict>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            conflicts.Add(new TargetIdConflict(
                new DeliveryKey(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetGuid(2),
                null,
                new DeliveryKey(reader.GetGuid(3)),
                reader.GetString(4)));
        }

        return conflicts;
    }

    /// <summary>Runs a statement over one slice whose result set is one delivery key per row, and reads the keys back.</summary>
    private static async Task<IReadOnlyList<DeliveryKey>> KeysAsync(SqlConnection connection, SqlTransaction transaction, string sql, int slice, short partitionId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice);
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        var keys = new List<DeliveryKey>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(new DeliveryKey(reader.GetGuid(0)));
        }

        return keys;
    }

    /// <summary>
    /// The batch as the stage's rows, in the order SQL Server sorts the keys and numbered <paramref name="slice"/> to a
    /// slice, so each slice is one contiguous range of the record table's key and concurrent stagings lock in one order.
    /// </summary>
    private static DataTable PendingTable(Guid flowId, IReadOnlyList<RecordState> records, int slice)
    {
        var table = new DataTable();
        table.Columns.Add("Slice", typeof(int));
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("FlowId", typeof(Guid));
        table.Columns.Add("SourceKey", typeof(string));
        table.Columns.Add("SourceKeyJson", typeof(string));
        table.Columns.Add("Label", typeof(string));
        table.Columns.Add("MappingName", typeof(string));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("TargetVersion", typeof(long));
        table.Columns.Add("LastSubmissionId", typeof(Guid));
        table.Columns.Add("PendingDocumentRef", typeof(string));
        table.Columns.Add("WorkBatch", typeof(int));
        table.Columns.Add("PendingRenderContext", typeof(string));
        table.Columns.Add("PendingSourceFingerprint", typeof(string));
        table.Columns.Add("PendingSourceModifiedUtc", typeof(DateTime));
        table.Columns.Add("PendingSourceFileName", typeof(string));
        table.Columns.Add("PendingSourceRowNumber", typeof(long));
        table.Columns.Add("PendingSourceUpdatedUtc", typeof(DateTime));
        table.Columns.Add("SourceInsertedUtc", typeof(DateTime));
        table.Columns.Add("PendingMetadataHash", typeof(string));
        table.Columns.Add("PendingPayloadHash", typeof(string));
        table.Columns.Add("PendingPayloadModifiedUtc", typeof(DateTime));
        table.Columns.Add("PendingPayloadLocation", typeof(string));
        table.Columns.Add("PendingMetadata", typeof(bool));
        table.Columns.Add("PendingPayload", typeof(bool));
        table.Columns.Add("PendingReferences", typeof(string));
        table.Columns.Add("CacheSetId", typeof(long));
        table.Columns.Add("ParentId", typeof(string));
        table.Columns.Add("PendingParentVersion", typeof(long));
        table.Columns.Add("SeenPlanRequestedUtc", typeof(DateTime));
        table.Columns.Add("AnswersAnyRequest", typeof(bool));
        var position = 0;
        foreach (var r in records.OrderBy(r => new SqlGuid(r.DeliveryKey.Value)))
        {
            table.Rows.Add(
                position++ / slice, r.DeliveryKey.Value, flowId, Truncate(r.SourceKey, 400), Value(Truncate(r.SourceKeyJson, 2000)), Value(Truncate(r.Label, 400)), r.MappingName, Value(r.TargetId), Value(r.TargetVersion),
                Value(r.LastSubmissionId), Value(r.PendingDocumentRef), Value(r.WorkBatch), Value(r.PendingRenderContext),
                Value(r.PendingSourceFingerprint), Value(r.PendingSourceModifiedUtc),
                Value(Truncate(r.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength)), Value(r.PendingSourceRowNumber), Value(r.PendingSourceUpdatedUtc),
                Value(r.SourceInsertedUtc), Value(r.PendingMetadataHash), Value(r.PendingPayloadHash),
                Value(r.PendingPayloadModifiedUtc), Value(r.PendingPayloadLocation), r.PendingMetadata, r.PendingPayload,
                Value(RecordReferences.Encode(r.PendingReferences)), Value(r.CacheSetId), Value(r.ParentId), Value(r.PendingParentVersion),
                Value(r.Basis?.PlanRequestedUtc), r.Basis is null);
        }

        return table;
    }

    private static DataTable AttemptTable(IReadOnlyList<DeliveryAttempt> attempts)
    {
        var table = new DataTable();
        table.Columns.Add("PartitionId", typeof(short));
        table.Columns.Add("FlowId", typeof(Guid));
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("SubmissionId", typeof(Guid));
        table.Columns.Add("RunId", typeof(Guid));
        table.Columns.Add("Worker", typeof(string));
        table.Columns.Add("StartedUtc", typeof(DateTime));
        table.Columns.Add("CompletedUtc", typeof(DateTime));
        table.Columns.Add("Outcome", typeof(string));
        table.Columns.Add("Phase", typeof(string));
        table.Columns.Add("MetadataHash", typeof(string));
        table.Columns.Add("PayloadHash", typeof(string));
        table.Columns.Add("TargetVersion", typeof(long));
        table.Columns.Add("Error", typeof(string));
        table.Columns.Add("ResultJson", typeof(string));
        table.Columns.Add("WorkBatch", typeof(int));
        table.Columns.Add("SourceFileName", typeof(string));
        table.Columns.Add("SourceRowNumber", typeof(long));
        table.Columns.Add("SourceUpdatedUtc", typeof(DateTime));
        table.Columns.Add("SourceDeletedUtc", typeof(DateTime));
        table.Columns.Add("ParentVersion", typeof(long));
        foreach (var a in attempts)
        {
            table.Rows.Add(
                a.PartitionId, a.FlowId, a.DeliveryKey, Value(a.SubmissionId), Value(a.RunId), a.Worker, a.StartedUtc, a.CompletedUtc,
                a.Outcome, a.Phase, Value(a.MetadataHash), Value(a.PayloadHash), Value(a.TargetVersion),
                Value(a.Error), Value(a.ResultJson), Value(a.WorkBatch),
                Value(a.SourceFileName), Value(a.SourceRowNumber), Value(a.SourceUpdatedUtc), Value(a.SourceDeletedUtc), Value(a.ParentVersion));
        }

        return table;
    }

    private static DataTable EventTable(IReadOnlyList<DeliveryRecordEvent> events)
    {
        var table = new DataTable();
        table.Columns.Add("PartitionId", typeof(short));
        table.Columns.Add("LeaseToken", typeof(string));
        table.Columns.Add("FlowId", typeof(Guid));
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("Kind", typeof(string));
        table.Columns.Add("AtUtc", typeof(DateTime));
        table.Columns.Add("StepJson", typeof(string));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("Promote", typeof(bool));
        table.Columns.Add("NothingSent", typeof(bool));
        table.Columns.Add("Replan", typeof(bool));
        table.Columns.Add("NextAttemptUtc", typeof(DateTime));
        table.Columns.Add("Error", typeof(string));
        table.Columns.Add("ProblemHash", typeof(long));
        table.Columns.Add("ValidationOutcome", typeof(string));
        table.Columns.Add("ValidationProblems", typeof(long));
        table.Columns.Add("ValidatedUtc", typeof(DateTime));
        table.Columns.Add("AssertionFailures", typeof(long));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("TargetVersion", typeof(long));
        table.Columns.Add("TargetStateJson", typeof(string));
        table.Columns.Add("PendingStepJson", typeof(string));
        table.Columns.Add("ClaimSubmissionId", typeof(Guid));
        table.Columns.Add("ClaimDocumentRef", typeof(string));
        table.Columns.Add("ClaimAttemptCount", typeof(int));
        table.Columns.Add("ClaimRenderContext", typeof(string));
        table.Columns.Add("ClaimSourceFingerprint", typeof(string));
        table.Columns.Add("ClaimSourceModifiedUtc", typeof(DateTime));
        table.Columns.Add("ClaimSourceFileName", typeof(string));
        table.Columns.Add("ClaimSourceRowNumber", typeof(long));
        table.Columns.Add("ClaimSourceUpdatedUtc", typeof(DateTime));
        table.Columns.Add("ClaimMetadataHash", typeof(string));
        table.Columns.Add("ClaimParentVersion", typeof(long));
        table.Columns.Add("ClaimPayloadHash", typeof(string));
        table.Columns.Add("ClaimPayloadModifiedUtc", typeof(DateTime));
        table.Columns.Add("ClaimMetadata", typeof(bool));
        table.Columns.Add("ClaimPayload", typeof(bool));
        foreach (var e in events)
        {
            table.Rows.Add(
                e.PartitionId, e.LeaseToken, e.FlowId, e.DeliveryKey, e.Kind, e.AtUtc, Value(e.StepJson),
                Value(e.Status), e.Promote, e.NothingSent, e.Replan, Value(e.NextAttemptUtc), Value(e.Error), Value(e.ProblemHash),
                Value(e.ValidationOutcome), Value(e.ValidationProblems), Value(e.ValidatedUtc), Value(e.AssertionFailures),
                Value(e.TargetId), Value(e.TargetVersion), Value(e.TargetStateJson), Value(e.PendingStepJson),
                Value(e.ClaimSubmissionId), Value(e.ClaimDocumentRef), Value(e.ClaimAttemptCount), Value(e.ClaimRenderContext), Value(e.ClaimSourceFingerprint),
                Value(e.ClaimSourceModifiedUtc), Value(e.ClaimSourceFileName), Value(e.ClaimSourceRowNumber), Value(e.ClaimSourceUpdatedUtc),
                Value(e.ClaimMetadataHash), Value(e.ClaimParentVersion), Value(e.ClaimPayloadHash), Value(e.ClaimPayloadModifiedUtc), e.ClaimMetadata, e.ClaimPayload);
        }

        return table;
    }

    private static object Value<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object Value(string? value) => value is null ? DBNull.Value : value;

    private static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];
}
