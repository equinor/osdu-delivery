---
id: delivery-table-attempt
title: "osdu.Attempt: every try of a record, and every decision written as one, append-only"
type: table
summary: "One row per delivery try or decision about a record: who made it and when, what came of it, what was sent, the error, the steps, and the source row."
keywords:
  - osdu.Attempt
  - attempt table
  - attempt history
  - record history
  - delivery try
  - outcome
  - phase
  - correlation id
  - retention
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-record-lifecycle
  - delivery-concept-availability-and-retention
  - delivery-table-record
  - delivery-table-submission
  - delivery-table-record-event
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Purges.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260928064320_LedgerPartitions.cs
---

# osdu.Attempt: every try of a record, and every decision written as one, append-only

Append-only. One row per delivery try, and one per decision the engine or an operator made about a record even when
nothing was sent: a hold at render time, a stale or identical row, a removal, a restore, an undo, a row a sync no longer
found. A record's history is its attempts, newest first ([The delivery ledger](../concepts/ledger.md)). An attempt keeps
the ingestion row its document was built from, because the record's own origin columns move on with later versions.

Pruned by age, and only where a later attempt of the same record exists, so every record's last outcome stays
explainable ([Availability and retention](../concepts/availability-and-retention.md#retention)). Its primary key and its
`StartedUtc` index grow at their end and are set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` by migration `LedgerPartitions` where
the server has it (SQL Server 2019 and later, Azure SQL), since every drain appends at their end.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `AttemptId` | `bigint` identity | no | The attempt's id, ever increasing within the partition. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the record's flow; with the delivery key, the record's identity ([`osdu.Record`](record.md)). |
| `DeliveryKey` | `uniqueidentifier` | no | The record's delivery key. |
| `SubmissionId` | `uniqueidentifier` | yes | The submission the try or decision belonged to, when it belonged to one. |
| `RunId` | `uniqueidentifier` | yes | The platform run the attempt happened in; null for an operator action. |
| `Worker` | `nvarchar(200)` | no | Who made it: a worker's name, `intake` or `sync` for a decision the plan or a sync wrote, or the operator who asked for a removal. |
| `StartedUtc` | `datetime2` | no | When the try or decision started. |
| `CompletedUtc` | `datetime2` | no | When it ended. |
| `Outcome` | `nvarchar(16)` | no | What came of it: `delivered`, `skipped`, `held`, `failed`, `deleted`, `historypurged`, `restored` or `undone` ([Record lifecycle](../concepts/record-lifecycle.md#attempts)). |
| `Phase` | `nvarchar(32)` | no | What was sent or decided: `metadata`, `payload`, `metadata+payload`, `none`, or a decision such as `render`, `source-deleted`, `identical`, `stale`, `unchanged`, `undo-wait`, `source-missing`, `delete`, `purge-history`, `restore-previous`, `reassign-id`, `adopt`, `reverse` or `undo` ([Record lifecycle](../concepts/record-lifecycle.md#attempts)). |
| `MetadataHash` | `nvarchar(64)` | yes | The record document hash the try established. |
| `PayloadHash` | `nvarchar(64)` | yes | The payload hash the try established. |
| `TargetVersion` | `bigint` | yes | The OSDU version the try returned. |
| `Error` | `nvarchar(2000)` | yes | The redacted error of a held or failed try, at most 2,000 characters. |
| `ResultJson` | `nvarchar(max)` | yes | What the try did and what OSDU answered, as JSON: a `steps` array (name, start, duration, status, the values returned, whether an earlier try completed it), the `correlationId` every OSDU request of the try carried, the version OSDU held before (`replaced`), the check before sending (`validation`, with the mapping's `assertions`), the unit of work (`unit`) and a note (`detail`). |
| `WorkBatch` | `int` | yes | The work batch the try was drained from, when it ran from one. |
| `SourceFileName` | `nvarchar(800)` | yes | The ingestion file the attempt's document was built from. |
| `SourceRowNumber` | `bigint` | yes | The row of that file. |
| `SourceUpdatedUtc` | `datetime2` | yes | When the ingestion table last updated that row. |
| `SourceDeletedUtc` | `datetime2` | yes | When the ingestion table marked that row deleted, for the hold of a deleted row; null otherwise. |
| `ParentVersion` | `bigint` | yes | The version of the record's parent the attempt's document was rendered from, kept here because the record's own moves on with later versions; null when its mapping reads no parent, or OSDU held none ([Parent records](../flow/mapping-parent.md)). |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Attempt` | `PartitionId`, `AttemptId` | primary key | none | none | Appends at the end of each partition's range: every drain writes here. Set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it. |
| `IX_Attempt_PartitionId_FlowId_DeliveryKey_StartedUtc` | `PartitionId`, `FlowId`, `DeliveryKey`, `StartedUtc` | index | none | none | A record's timeline, and the later attempt that pruning looks for beside each one it removes. |
| `IX_Attempt_StartedUtc` | `StartedUtc` | index | none | none | Pruning by age, across every partition: retention is the estate's, not a partition's. Set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it. |
| `IX_Attempt_SubmissionId_Outcome_Phase` | `SubmissionId`, `Outcome`, `Phase` | index | none | `DeliveryKey` | A submission's attempts, and the records they settled by outcome, counted when the submission closes from this index alone, however many attempts a full plan wrote. |
| `IX_Attempt_RunId_PartitionId_FlowId_DeliveryKey` | `RunId`, `PartitionId`, `FlowId`, `DeliveryKey` | index | none | none | A run's records: the record listing's run filter seeks the run and joins on the record's key without reading the attempt. |

## Written and read by

- **Written** by the lease applying a worker's completions, by the intake for the decisions a plan makes, by syncs,
  removals, restores, reversals and undos, always in the transaction that changes the record it explains
  (`OsduLedger.cs`, `SqlServerLedgerBulk.cs`).
- **Read** by a record's Timeline, `sqlflow records show --attempts`, the delivery API's `/attempts` route, a
  submission's counts when it closes, a run's record listing, and the prune (`POST /api/v1/delivery/ledger/prune`).
