---
id: delivery-table-purged-record
title: "osdu.PurgedRecord: the one line kept of a record deleted from the ledger after its removal from OSDU"
type: table
summary: "One row per record deleted from the ledger: its key, label, OSDU id and last version, who deleted it, and the request to read its row again."
keywords:
  - osdu.PurgedRecord
  - purged record table
  - deleted from the ledger
  - removal
  - read again
  - plan requested
  - purge
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-removal-and-reversal
  - delivery-table-record
  - delivery-table-activity-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Recency.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261014094057_PurgedRecordReadAgain.cs
---

# osdu.PurgedRecord: the one line kept of a record deleted from the ledger after its removal from OSDU

A record removed from OSDU can be deleted from the ledger: its attempts, search entries and row go, and this line is what
the ledger keeps of it, so who deleted which record, with which OSDU id and last version, is still answered from the
ledger ([Removal and reversal](../concepts/removal-and-reversal.md#deleting-removed-records-from-the-ledger)). Asked for
afterwards, the record's API route answers 404 titled `Deleted from the ledger`, with this line under `purged`. The
intervention that deleted it names the record in [`osdu.ActivityRecord`](activity-record.md) as well.

Lines are only ever added, and the one thing that changes on a line is its request to read the record's row again
(`PlanRequestedUtc`, added by migration `PurgedRecordReadAgain`, module version 1.46.0). Never pruned.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `PurgedRecordId` | `bigint` identity | no | The line's id. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the record's flow. |
| `DeliveryKey` | `uniqueidentifier` | no | The record's delivery key. |
| `SourceKey` | `nvarchar(400)` | no | The record's key in its source, as the ledger held it. |
| `SourceKeyJson` | `nvarchar(2000)` | yes | The record's key tuple as the ledger held it, a JSON array of strings in the flow's key order: what the run that reads its row again reads it by. Null for a record that carried none. |
| `PlanRequestedUtc` | `datetime2` | yes | When the deletion asked for the record's row to be read again: the next run of the flow reads it by `SourceKeyJson` and delivers it as a new record. Null once a run answered it (its row planned, or gone from the table), when a later deletion of the same record took it over, for a record a reversal keeps out, for one with no key tuple, and for every line deleting the whole ledger wrote. |
| `Label` | `nvarchar(400)` | yes | The record's label. |
| `TargetId` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. The OSDU id the record was delivered and removed under; null for a record that never had one. |
| `LastVersion` | `bigint` | yes | The last OSDU version an attempt of the record named, before it was removed. |
| `Attempts` | `int` | no | How many attempts were deleted with it. |
| `ActivityId` | `bigint` | yes | The intervention that deleted it, as the audit trail numbers it ([`osdu.Activity`](activity.md)). |
| `PurgedBy` | `nvarchar(200)` | no | Who deleted it. |
| `PurgedUtc` | `datetime2` | no | When it was deleted. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_PurgedRecord` | `PartitionId`, `PurgedRecordId` | primary key | none | none | A partition's lines together, in the order they were written. |
| `IX_PurgedRecord_PartitionId_FlowId_PlanRequestedUtc_DeliveryKey` | `PartitionId`, `FlowId`, `PlanRequestedUtc`, `DeliveryKey` | index | `[PlanRequestedUtc] IS NOT NULL` | `SourceKeyJson` | The deleted records whose rows wait to be read again, paged by each run beside the records asked to be planned again, in the same order; the filter keeps the index as small as what waits. |
| `IX_PurgedRecord_PartitionId_FlowId_DeliveryKey` | `PartitionId`, `FlowId`, `DeliveryKey` | index | none | none | A record's page asking by its key what became of a record the ledger no longer holds. |
| `IX_PurgedRecord_PartitionId_TargetId` | `PartitionId`, `TargetId` | index | `[TargetId] IS NOT NULL` | none | A lookup by OSDU id finding which record went under it. |
| `IX_PurgedRecord_PartitionId_FlowId_PurgedUtc` | `PartitionId`, `FlowId`, `PurgedUtc` | index | none | `TargetId` | What a ledger deleted lately, by when: an assertion run asks it before it judges what the search index lists, which can still list a record a removal took out of OSDU moments before. |

## Written and read by

- **Written** by the deletion of removed records from the ledger and by deleting a whole ledger
  (`SqlServerLedgerBulk.Purges.cs`, `OsduLedger.Purges.cs`); `PlanRequestedUtc` is cleared by the run that reads the row.
- **Read** by every deliver run of the flow (the rows to read again), a record's API route, the record lookup by OSDU id,
  and assertion runs (`SqlServerLedgerBulk.Recency.cs`).
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of a renamed pipeline's old ledger under
  its new identity, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
