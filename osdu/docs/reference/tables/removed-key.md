---
id: delivery-table-removed-key
title: "osdu.RemovedKey: a delivery ledger's deletions, the keys its source no longer holds, and how far taking each out has come"
type: table
summary: "One row per key a removal flow found its source removed: why and as of when, its state, what taking it out came to, and who found and approved it."
keywords:
  - osdu.RemovedKey
  - ledger deletions
  - removed keys
  - keys the source no longer holds
  - pending deletions
  - awaiting approval
  - incremental removal
  - kept out
related:
  - delivery-concept-database
  - delivery-flow-removal
  - delivery-table-removal-run
  - delivery-table-record
  - delivery-concept-removal-and-reversal
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022223524_RemovalFlows.cs
---

# osdu.RemovedKey: a delivery ledger's deletions, the keys its source no longer holds, and how far taking each out has come

A delivery ledger's deletions ([The ledger's deletions](../flow/removal.md#the-ledgers-deletions)): one row per key a
[removal flow](../flow/removal.md) found its source no longer holds, under the delivery flow's own ledger identity, whichever
removal flow found it. A removal run adds the row once, `pending`, or `awaiting-approval` when the run's
[guard](../flow/removal.md#the-guard) held what it found; taking deletions out of OSDU works through the `pending` rows
alone and makes each `removed` once its try finishes it, so a key taken out is never asked of OSDU again. While the row is
`pending` or `removed`, the delivery flow's plan delivers no row of the key that is not newer than `RemovedUtc` (it skips
it as removed by its source); a row `awaiting-approval` keeps nothing out. The row goes when the source holds the key again,
when the record is delivered again from a newer row, or when an operator lifts it
([Delivering a record again](../flow/removal.md#delivering-a-record-again)), and the record is then delivered as any other.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition of the ledger, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the delivery flow (or interface) the key's records belong to ([`osdu.Ledger`](ledger.md)). |
| `DeliveryKey` | `uniqueidentifier` | no | The key's delivery key: the record's, when the ledger holds one ([`osdu.Record`](record.md)). |
| `SourceKeyJson` | `nvarchar(2000)` | yes | The key tuple, a JSON array of the key columns' values as text, in the record key's order. |
| `Reason` | `nvarchar(16)` | no | `absent` (the source's current keys, read whole, no longer held it) or `listed` (the source listed it as removed). |
| `RemovedUtc` | `datetime2` | no | The moment the removal stands for: when the read that lacked the key began, or the moment the source's list gives. A row of the key not newer than it is not delivered while the deletion stands. |
| `FileName` | `nvarchar(800)` | yes | The file (or table) of the row that listed the key as removed. |
| `RowNumber` | `bigint` | yes | That row's number in its file, or its position in the table's read. |
| `State` | `nvarchar(24)` | no | `awaiting-approval` (the guard held it), `pending` (to be taken out) or `removed` (taken out, settled or kept out). |
| `Outcome` | `nvarchar(24)` | yes | What the last try came to: `removed`, `already-gone`, `settled` or `kept-out`, which finish it, or `referenced`, `shared`, `busy` or `failed`, which leave it pending; null before the first try. |
| `Detail` | `nvarchar(1000)` | yes | Why it came to that outcome, redacted: the records still naming it, what OSDU answered. |
| `Tries` | `int` | no | How many times a removal run tried to take it out. |
| `RemovalFlow` | `nvarchar(200)` | no | The removal flow that found it, by name. |
| `Entry` | `nvarchar(300)` | no | The removal of that flow that found it. |
| `DetectedRunId` | `bigint` | no | The removal run that found it ([`osdu.RemovalRun`](removal-run.md)). |
| `DoneRunId` | `bigint` | yes | The removal run that finished it; null while it is not `removed`. |
| `RunId` | `uniqueidentifier` | yes | The platform run of the removal run that last acted on it. |
| `ApprovedBy` | `nvarchar(200)` | yes | Who approved it, for a key the guard held. |
| `ApprovedUtc` | `datetime2` | yes | When it was approved. |
| `CreatedUtc` | `datetime2` | no | When the removal run found it. |
| `DoneUtc` | `datetime2` | yes | When it was finished; null while it is not `removed`. |
| `UpdatedUtc` | `datetime2` | no | When the row last changed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RemovedKey` | `PartitionId`, `FlowId`, `DeliveryKey` | primary key | none | none | One deletion per key of a ledger; the plan's look-up of a page of keys, a record's page, a lift, and the comparison's joins. |
| `IX_RemovedKey_PartitionId_FlowId_State_DeliveryKey` | `PartitionId`, `FlowId`, `State`, `DeliveryKey` | index | none | none | A ledger's deletions of one state in key order: a removal takes the pending ones in turn, an approval the ones waiting for it, the grid pages either, and the counts by state. |
| `IX_RemovedKey_PartitionId_DetectedRunId` | `PartitionId`, `DetectedRunId` | index | none | none | What one removal run found. |

## Written and read by

- **Written** by a removal run's comparison, which adds, lifts and moves deletions in one transaction with its counts
  (`SqlServerLedgerBulk.Removal.cs`); by an approval, which makes the deletions waiting for it pending; by taking them out,
  which records each try's outcome (`FlowRuntime.SourceRemoval.cs`); and by an operator's lift (`OsduLedger.Removal.cs`),
  each in the delivery ledger it belongs to.
- **Read** by the delivery flow's plan, a page of keys at a time (`Planner.cs`), the deletions grid of the Removals page and
  of the delivery flow's Deletions tab, a record's page, `GET /api/v1/delivery/removals/ledgers/{ledgerFlowId}/keys`, and
  `sqlflow records removed`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of a renamed pipeline's old ledger under
  its new identity, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
