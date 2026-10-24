---
id: delivery-table-reversal
title: "osdu.Reversal: one reversal of a run or a submission, putting back what it put into OSDU"
type: table
summary: "One row per run or submission reversed in a ledger: what it covers, its status, who asked and when, and the run that last worked on it."
keywords:
  - osdu.Reversal
  - reversal table
  - reverse a run
  - reverse a submission
  - put back
  - restore
  - removal and reversal
related:
  - delivery-concept-database
  - delivery-concept-removal-and-reversal
  - delivery-table-reversal-item
  - delivery-table-submission
  - delivery-table-activity-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Reversals.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryReversalEndpoints.cs
---

# osdu.Reversal: one reversal of a run or a submission, putting back what it put into OSDU

One reversal: what one run or one submission put into OSDU, put back record by record as OSDU held it before
([Removal and reversal](../concepts/removal-and-reversal.md#reversing-a-run-or-a-submission)). There is one per source of
a ledger; asking again resumes it. Every record it reaches is an [`osdu.ReversalItem`](reversal-item.md), and its counts
are read from its items, never kept here.

Never pruned by retention: a reversal is what a record's custody was put back by. Deleting the ledger deletes it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ReversalId` | `bigint` identity | no | The reversal's id, unique on its own. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow whose run or submission it reverses. |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `SourceKind` | `nvarchar(16)` | no | `run` or `submission`. |
| `SourceId` | `uniqueidentifier` | no | The run or the submission reversed. |
| `SubmissionsJson` | `nvarchar(max)` | no | The submissions the source covers, as a JSON array of ids, fixed when the reversal was opened. |
| `Status` | `nvarchar(16)` | no | `capturing`, `reversing`, `completed`, `failed` or `cancelled`. |
| `RequestedBy` | `nvarchar(200)` | no | Who asked for the reversal first. |
| `RequestedUtc` | `datetime2` | no | When it was first asked for. |
| `CapturedUtc` | `datetime2` | yes | When every record the source delivered was listed as an item; null while the listing is not finished. |
| `StartedUtc` | `datetime2` | yes | When the latest run working on it started. |
| `CompletedUtc` | `datetime2` | yes | When the latest run working on it ended. |
| `LastRunId` | `uniqueidentifier` | yes | The latest run that worked on it. |
| `Error` | `nvarchar(2000)` | yes | Why the latest run stopped, redacted; null when it did not fail. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Reversal` | `PartitionId`, `ReversalId` | primary key | none | none | A partition's reversals together. |
| `IX_Reversal_ReversalId` | `ReversalId` | unique | none | none | A reversal named by its id alone: a run's payload, a link. |
| `IX_Reversal_PartitionId_FlowId_SourceKind_SourceId` | `PartitionId`, `FlowId`, `SourceKind`, `SourceId` | unique | none | none | One reversal per source of a ledger: asking again resumes it rather than opening a second one. |
| `IX_Reversal_PartitionId_FlowId_RequestedUtc` | `PartitionId`, `FlowId`, `RequestedUtc` | index | none | none | A ledger's reversals, newest first. |

## Written and read by

- **Written** by `sqlflow records reverse` and the reverse operation of a run, through the ledger
  (`OsduLedger.Reversals.cs`, `SqlServerLedgerBulk.Reversals.cs`).
- **Read** by the reverse runs that resume it, and by the delivery API's `GET /flows/{pipelineId}/reversals` and
  `GET /reversals/{reversalId}` (`DeliveryReversalEndpoints.cs`), which the GUI shows.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the
  identity of the flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
