---
id: delivery-table-record
title: "osdu.Record: the current state of one deliverable of one flow, and the work waiting to be sent"
type: table
summary: "One row per record a flow delivers: what OSDU holds of it, where it came from, the work queued for it, its status and why it is blocked."
keywords:
  - osdu.Record
  - record table
  - delivery ledger
  - record state
  - delivery key
  - OSDU id
  - record status
  - pending work
  - blocked records
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-record-lifecycle
  - delivery-concept-change-detection
  - delivery-table-attempt
  - delivery-table-record-identity
  - delivery-table-submission
  - delivery-table-lease
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
---

# osdu.Record: the current state of one deliverable of one flow, and the work waiting to be sent

One row per deliverable of one flow in one partition: the same source row read by two flows is two records, and so is
one flow delivering it to two partitions, under the same delivery key. A record holds what OSDU holds of it (its id,
version and hashes), where that version came from, the work queued to be sent (`Pending*`), and its custody state
([Record lifecycle](../concepts/record-lifecycle.md#record-states)). Its history is its attempts in
[`osdu.Attempt`](attempt.md), and the values it can be found by are in [`osdu.RecordIdentity`](record-identity.md). The
rendered document itself is never stored in the database: `PendingDocumentRef` points into a work batch file.

Never pruned: a record leaves only when an operator deletes it from the ledger after removing it from OSDU, or deletes
the whole ledger, and one line of it stays in [`osdu.PurgedRecord`](purged-record.md)
([Availability and retention](../concepts/availability-and-retention.md#retention)). A flow's counts are counted from
this table through its status indexes on every call; nothing keeps a running total beside it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow or interface that delivers the record ([`osdu.Ledger`](ledger.md)). |
| `DeliveryKey` | `uniqueidentifier` | no | The record's deterministic key: derived from the source system's name and the ordered natural-key values, never assigned by a run. The OSDU id and the idempotency token derive from it. |
| `SourceKey` | `nvarchar(400)` | no | The source key as text, at most 400 characters. |
| `SourceKeyJson` | `nvarchar(2000)` | yes | The key tuple as a JSON array of strings, in the flow's `source.record.key` order: what a key-scoped read uses. |
| `Label` | `nvarchar(400)` | yes | The mapping's `dataset.label` rendered for the row, at most 400 characters: for search and display only. |
| `MappingName` | `nvarchar(200)` | no | The mapping the record is rendered with. |
| `RenderContext` | `nvarchar(max)` | yes | The render context of the delivered document, as canonical JSON (see [`osdu.Submission`](submission.md)). |
| `SourceFingerprint` | `nvarchar(200)` | yes | The ingestion fingerprint of the rows the delivered document was built from. |
| `SourceModifiedUtc` | `datetime2` | yes | The business version of the row the delivered document was built from: the flow's `source.lastModified`. |
| `SourceFileName` | `nvarchar(800)` | yes | The ingestion file the version OSDU holds came from (the row's `FileName_DW`, at most 800 characters). |
| `SourceRowNumber` | `bigint` | yes | The row of that file (`RowNumber_DW`). |
| `SourceUpdatedUtc` | `datetime2` | yes | When the ingestion table last updated that row (`UpdatedDate_DW`). |
| `SourceInsertedUtc` | `datetime2` | yes | When the ingestion table first inserted the record's row (`InsertedDate_DW`), as the last plan that read it saw it: the row's arrival, which later changes never move. Null when the table does not carry it. |
| `MetadataHash` | `nvarchar(64)` | yes | The hash of the record document OSDU holds: a change gate for the next plan ([Change detection](../concepts/change-detection.md)). |
| `PayloadHash` | `nvarchar(64)` | yes | The hash of the payload OSDU holds. |
| `PayloadModifiedUtc` | `datetime2` | yes | The newest modified time among the payload files the delivered payload was sent from. |
| `TargetId` | `nvarchar(500)` | yes | The OSDU id the record is delivered under. |
| `ClaimedTargetId` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. The OSDU id the record claimed for its flow when it first queued a document, or when a run took over the record OSDU held there as an operator asked, kept for good afterwards; null for a record that was only ever held. Unique across the ledger: one OSDU record belongs to one record of one flow. |
| `TargetIdAssignedUtc` | `datetime2` | yes | When an operator gave the record a new OSDU id because OSDU lost the one it claimed; from then on the record is written under `ClaimedTargetId` rather than the id its mapping makes. Null for a record whose id is its mapping's. Added by migration `RecordAssignedTargetId` (module version 1.45.0). |
| `OccupiedTargetId` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. While the record is held because OSDU already holds a record at the OSDU id its mapping makes from its key, and no record of any ledger claimed that id: the id, which an adoption makes the record claim ([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)). Null for any other record, and cleared when the record claims an id or is held for another reason. Added by migration `RecordOccupiedTargetId` (module version 1.56.0). |
| `OccupiedTargetVersion` | `bigint` | yes | The version OSDU held at `OccupiedTargetId` when the record was held for it; null with it. An adoption takes the version OSDU holds when the run takes the record over, not this one. Added by migration `RecordOccupiedTargetId` (module version 1.56.0). |
| `AdoptRequestedUtc` | `datetime2` | yes | When an operator asked the record, held for an id OSDU holds, to adopt the record OSDU holds there: the flow's next run takes it over ([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)). Null for a record not asked, and cleared when a run writes the record; kept while a run holds the record for another reason, so the run after a release takes it over. Added by migration `RecordAdoptRequested` (module version 1.57.0). |
| `TargetVersion` | `bigint` | yes | The last OSDU version the ledger knows of. |
| `Status` | `nvarchar(16)` | no | The custody state: `pending`, `delivering`, `delivered`, `held`, `failed`, `waiting`, `deleted` or `reverted` ([Record lifecycle](../concepts/record-lifecycle.md#record-states)). |
| `LastDeliveredUtc` | `datetime2` | yes | When a try last delivered the record. |
| `LastVerifiedUtc` | `datetime2` | yes | When a verify run last compared the record with what OSDU holds. |
| `LastVerifyOutcome` | `nvarchar(16)` | yes | That comparison's outcome: `match`, `drifted`, `missing` or `error`. |
| `LeaseOwner` | `nvarchar(200)` | yes | The token of the lease the record is being delivered under ([`osdu.Lease`](lease.md)), set when a worker claims it and cleared when the lease applies its outcome or hands it back. The lease holds the expiry. |
| `LastSubmissionId` | `uniqueidentifier` | yes | The submission that last staged work for the record ([`osdu.Submission`](submission.md)). |
| `AttemptCount` | `int` | no | The tries made for the pending work; reset when new work is queued. |
| `NextAttemptUtc` | `datetime2` | yes | When the pending work is next due. |
| `LastError` | `nvarchar(2000)` | yes | The redacted message of the last failure or the hold reason, at most 2,000 characters. |
| `PendingDocumentRef` | `nvarchar(64)` | yes | Where the pending rendered document sits in the submission's work batches, as `batch:offset:length`. |
| `WorkBatch` | `int` | yes | The work batch the pending document was written in ([`osdu.WorkBatch`](work-batch.md)). |
| `TargetStateJson` | `nvarchar(max)` | yes | Every identifier the target returned for what it holds now (record id and version, dataset ids, file sources, a workflow run id), as a JSON object merged step by step. |
| `PendingStepJson` | `nvarchar(max)` | yes | The steps of the pending delivery that already completed and what they returned, keyed by step, so a retry resumes after them. |
| `CacheSetId` | `bigint` | yes | The cached values the delivered document was built from, as the id of the set it shares with every record that read the same values ([`osdu.CacheSet`](cache-set.md)). |
| `HeldCacheSetId` | `bigint` | yes | While the record is held: what its held render read out of the cache and did not find, as a set id. A cache version that brings in one of those ids releases the record to be planned again ([The partition cache](../concepts/partition-cache.md)). |
| `ParentId` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. The record's parent: the OSDU id, without a version, of the record of the kind its template is linked to whose sections its render reads, or would have read had OSDU held it; null for a record whose mapping reads no parent. Written when work is queued or the record is held, so the parent landing plans the record again ([Parent records](../flow/mapping-parent.md)). Added by migration `LinkedTemplateParents` (module version 1.55.0). |
| `ParentVersion` | `bigint` | yes | The version of the parent the document OSDU holds was rendered from; null when it read none. |
| `PendingParentVersion` | `bigint` | yes | The version of the parent the pending work was rendered from; null when it read none. |
| `PendingRenderContext` | `nvarchar(max)` | yes | The render context of the pending work. |
| `PendingSourceFingerprint` | `nvarchar(200)` | yes | The ingestion fingerprint of the pending work, or of the state a held, failed or deleted record was left in. |
| `PendingSourceModifiedUtc` | `datetime2` | yes | The business version of the pending work, or of the state a held, failed or deleted record was left in. |
| `PendingSourceFileName` | `nvarchar(800)` | yes | The ingestion file the queued version came from, or the one a held record was left at. |
| `PendingSourceRowNumber` | `bigint` | yes | The row of that file. |
| `PendingSourceUpdatedUtc` | `datetime2` | yes | When the ingestion table last updated that row. |
| `PendingMetadataHash` | `nvarchar(64)` | yes | The hash the pending record document will establish. |
| `PendingPayloadHash` | `nvarchar(64)` | yes | The hash the pending payload will establish. |
| `PendingPayloadModifiedUtc` | `datetime2` | yes | The payload watermark of the pending work, when it carries a payload. |
| `PendingPayloadLocation` | `nvarchar(2000)` | yes | Where the pending payload's files are listed from (folder and pattern); null when no payload is pending. |
| `PendingMetadata` | `bit` | no | Whether the pending work sends the record document. |
| `PendingPayload` | `bit` | no | Whether the pending work sends the payload. |
| `PendingReferences` | `nvarchar(max)` | yes | The OSDU ids the pending document refers to through the properties its template declares relationships for, each with the property that holds it, as a JSON array. Written and cleared with the pending document ([Interfaces](../flow/interfaces.md)). |
| `Blocked` | `bit` | no | Set when the record was held, failed, deleted or reverted and not released since: it is planned again only when its source changes or an operator releases it. |
| `ProblemHash` | `bigint` | yes | While the record is blocked, held or failed: the problem that keeps it so, as the hash of its last error with every part naming the record replaced, which every record refused for the same reason shares. Null for any other record ([Record lifecycle](../concepts/record-lifecycle.md#issues)). |
| `ValidationOutcome` | `nvarchar(16)` | yes | What the last check of its document against the template came to: `valid`, `invalid` or `unverified`; null until a document was checked ([Preflight](../concepts/preflight.md)). |
| `ValidationProblems` | `bigint` | yes | How many problems that check found. |
| `ValidatedUtc` | `datetime2` | yes | When that check was made. |
| `AssertionFailures` | `bigint` | yes | How many judgements of the mapping's assertions failed on the document that check read; `0` when every one was met, null when the mapping states none or no check has reached the record; a try that sends the payload alone keeps the last count. Added by migration `RecordAssertions` (module version 1.39.0), with the same column on [`osdu.RecordEvent`](record-event.md) ([Mapping assertions](../flow/mapping-assertions.md#what-the-ledger-keeps)). |
| `AcceptedMetadataHash` | `nvarchar(64)` | yes | The metadata hash of the pending document an operator's release accepted as it is: that document is sent whatever its verdict, and one rendered differently is judged again. |
| `WaitingFor` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. While the record is `waiting`: the OSDU id of the record it waits for, which another record of the ledger holds and has not delivered. |
| `PlanRequestedUtc` | `datetime2` | yes | When the ledger asked for the record to be planned again (a redelivery, a release, a cache rollout); null once a plan took it. |
| `CreatedUtc` | `datetime2` | no | When the row was created. |
| `UpdatedUtc` | `datetime2` | no | When the row last changed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Record` | `PartitionId`, `FlowId`, `DeliveryKey` | primary key | none | none | One record; a flow's key-ordered walks within the partition (a removal's key list, a key-scoped plan, deleting a ledger). |
| `IX_Record_PartitionId_FlowId_Status_NextAttemptUtc` | `PartitionId`, `FlowId`, `Status`, `NextAttemptUtc` | index | none | `LastSubmissionId`, `UpdatedUtc`, `PendingDocumentRef` | The worker's claim and what is due next, for a flow, answered from the index alone. |
| `IX_Record_PartitionId_FlowId_LastSubmissionId_Status_NextAttemptUtc` | `PartitionId`, `FlowId`, `LastSubmissionId`, `Status`, `NextAttemptUtc` | index | none | `UpdatedUtc` | The same reads for one submission of the flow, and the settled submissions with due work. |
| `IX_Record_CacheSetId_DeliveryKey_FlowId` | `CacheSetId`, `DeliveryKey`, `FlowId` | index | `[CacheSetId] IS NOT NULL` | none | A cache rollout walking one set's records in key order, then flow order; the filter keeps untagged records out. |
| `IX_Record_HeldCacheSetId_Status` | `HeldCacheSetId`, `Status` | index | `[HeldCacheSetId] IS NOT NULL` | none | The held records a new cache version releases, found by the set their held render read. |
| `IX_Record_PartitionId_ParentId` | `PartitionId`, `ParentId` | index | `[ParentId] IS NOT NULL` | none | The records of a partition, of every flow, that read a parent that just landed, which are planned again ([Parent records](../flow/mapping-parent.md#when-the-parent-changes)). |
| `IX_Record_ClaimedTargetId` | `ClaimedTargetId` | unique | `[ClaimedTargetId] IS NOT NULL` | none | One OSDU record, one flow: the database refuses a second claim on an id, whatever races the intakes run. An id names its partition, so it stays unique across every partition. |
| `IX_Record_LastSubmissionId_WorkBatch` | `LastSubmissionId`, `WorkBatch` | index | none | none | A work batch's claim marking the batch's due records, a slice at a time however large the batch. |
| `IX_Record_LeaseOwner` | `LeaseOwner` | index | none | none | The records a lease holds: the ones a checkpoint or a close hands back, and the ones a submission waits on. |
| `IX_Record_PartitionId_FlowId_LastSubmissionId_UpdatedUtc` | `PartitionId`, `FlowId`, `LastSubmissionId`, `UpdatedUtc` | index | none | none | A submission's records, most recent first, read in index order however many it holds. |
| `IX_Record_PartitionId_FlowId_LastVerifiedUtc` | `PartitionId`, `FlowId`, `LastVerifiedUtc` | index | none | none | A flow's records by when they were last verified. |
| `IX_Record_PartitionId_FlowId_Label` | `PartitionId`, `FlowId`, `Label` | index | none | none | Prefix search by label within a flow. |
| `IX_Record_PartitionId_FlowId_SourceKey` | `PartitionId`, `FlowId`, `SourceKey` | index | none | none | Prefix search by source key within a flow. |
| `IX_Record_PartitionId_FlowId_TargetId` | `PartitionId`, `FlowId`, `TargetId` | index | none | none | Prefix search by OSDU id within a flow. |
| `IX_Record_PartitionId_FlowId_UpdatedUtc` | `PartitionId`, `FlowId`, `UpdatedUtc` | index | none | none | A flow's records, most recently updated first. |
| `IX_Record_PartitionId_FlowId_Status_UpdatedUtc` | `PartitionId`, `FlowId`, `Status`, `UpdatedUtc` | index | none | none | A flow's records of one status, most recently updated first. |
| `IX_Record_Parked` | `PartitionId`, `FlowId`, `Blocked`, `Status` | index | `[Status] IN (N'held', N'failed', N'deleted', N'reverted')` | `PlanRequestedUtc`, `PendingDocumentRef` | The parked records (held, failed, deleted or reverted) by whether they are blocked: what a flow's statistics count as blocked, and as held with nothing to move them, each a seek of a range that holds the parked records alone. |
| `IX_Record_PartitionId_FlowId_LastDeliveredUtc` | `PartitionId`, `FlowId`, `LastDeliveredUtc` | index | none | none | A flow's records by when they were last delivered. |
| `IX_Record_PartitionId_FlowId_LastVerifyOutcome` | `PartitionId`, `FlowId`, `LastVerifyOutcome` | index | none | none | A flow's records by their last verify outcome: the drifted and missing counts. |
| `IX_Record_PartitionId_DeliveryKey` | `PartitionId`, `DeliveryKey` | index | none | none | The lookup across the flows of a partition by delivery key: every flow's record of one source row. |
| `IX_Record_PartitionId_TargetId` | `PartitionId`, `TargetId` | index | none | none | The lookup across the flows of a partition by OSDU id. |
| `IX_Record_PartitionId_SourceKey` | `PartitionId`, `SourceKey` | index | none | none | The lookup across the flows of a partition by source key. |
| `IX_Record_PartitionId_Label` | `PartitionId`, `Label` | index | none | none | The lookup across the flows of a partition by label prefix. |
| `IX_Record_PartitionId_SourceFileName` | `PartitionId`, `SourceFileName` | index | none | none | The lookup across the flows of a partition by ingestion file name. |
| `IX_Record_PartitionId_UpdatedUtc` | `PartitionId`, `UpdatedUtc` | index | none | none | The Records page with nothing typed: the most recently updated records of a partition, read from the end of the index. |
| `IX_Record_PartitionId_Status_UpdatedUtc` | `PartitionId`, `Status`, `UpdatedUtc` | index | none | none | The most recent records of one status across the flows of a partition. |
| `IX_Record_PartitionId_FlowId_SourceFileName_SourceRowNumber` | `PartitionId`, `FlowId`, `SourceFileName`, `SourceRowNumber` | index | none | none | Which records of a flow came from one file, in milliseconds. |
| `IX_Record_PartitionId_FlowId_PlanRequestedUtc` | `PartitionId`, `FlowId`, `PlanRequestedUtc` | index | `[PlanRequestedUtc] IS NOT NULL` | none | The records the ledger asked to be planned again, which the planner pages every run; the filter keeps the index as small as the backlog. |
| `IX_Record_PartitionId_WaitingFor` | `PartitionId`, `WaitingFor` | index | `[WaitingFor] IS NOT NULL` | none | The waiting records released when the record holding the id they wait for lands; the filter keeps the index as small as what is waiting, so the release every settle runs is a seek. |
| `IX_Record_PartitionId_FlowId_ProblemHash_UpdatedUtc` | `PartitionId`, `FlowId`, `ProblemHash`, `UpdatedUtc` | index | `[ProblemHash] IS NOT NULL` | `Status`, `PendingSourceFileName` | The blocked records by the problem that keeps them so: a flow's issues counted with their records, newest and oldest and files, and an issue's records listed newest first and released. |
| `IX_Record_Unsorted` | `PartitionId`, `FlowId` | index | `[ProblemHash] IS NULL AND [Blocked]=(1) AND ([Status] IN (N'held', N'failed'))` | none | The blocked records not sorted into a problem yet, which the control plane's backfill reads; empty but for that backlog. A query reaches it only by repeating its filter. |
| `IX_Record_PartitionId_FlowId_ValidationOutcome_UpdatedUtc` | `PartitionId`, `FlowId`, `ValidationOutcome`, `UpdatedUtc` | index | `[ValidationOutcome] IS NOT NULL` | none | A flow's records by what the last check of their documents came to, counted for the flow's page; the invalid and unverified ones listed. |
| `IX_Record_PartitionId_FlowId_AssertionFailures_UpdatedUtc` | `PartitionId`, `FlowId`, `AssertionFailures`, `UpdatedUtc` | index | `[AssertionFailures]>(0)` | none | A flow's records whose last checked document failed an assertion of its mapping, counted and listed. |

## Written and read by

- **Written** by the intake, which stages and queues records a slice of at most 1,000 at a time, and by the lease
  applying what a worker appended ([`osdu.RecordEvent`](record-event.md)): a worker does not write this table while it
  delivers. Releases, adoptions, redeliveries, removals, reversals, syncs, cache rollouts and parents that land write it
  through the ledger (`OsduLedger.cs`, `OsduLedger.Adoptions.cs`, `OsduLedger.Parents.cs`, `SqlServerLedgerBulk.cs`,
  `SqlServerLedgerBulk.Adoptions.cs`), each
  writing an attempt or an activity beside the change.
- **Read** by the worker's claims, every plan's change gates, a flow's statistics and Records and Issues tabs, the record
  search across flows, a record's page, `sqlflow records` and the delivery API's record routes
  ([The delivery ledger](../concepts/ledger.md#finding-records)).
