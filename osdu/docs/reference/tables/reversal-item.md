---
id: delivery-table-reversal-item
title: "osdu.ReversalItem: each record a reversal reaches, what OSDU held before and what the reversal did"
type: table
summary: "One row per record a reversed run or submission delivered: the versions it wrote, what OSDU held before, and the item's state and outcome."
keywords:
  - osdu.ReversalItem
  - reversal item table
  - reversal
  - prior version
  - restored version
  - removed
  - already gone
related:
  - delivery-concept-database
  - delivery-concept-removal-and-reversal
  - delivery-table-reversal
  - delivery-table-attempt
  - delivery-table-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Reversals.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryReversalEndpoints.cs
---

# osdu.ReversalItem: each record a reversal reaches, what OSDU held before and what the reversal did

One record a [reversal](reversal.md) reaches: what its source delivered of it, what OSDU held before, and what the
reversal did. Written when the reversal lists its source, and settled in the transaction that changes the record
([Removal and reversal](../concepts/removal-and-reversal.md#reversing-a-run-or-a-submission)).

Never pruned by retention. Deleting the ledger deletes it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ReversalId` | `bigint` | no | The reversal ([`osdu.Reversal`](reversal.md)). |
| `DeliveryKey` | `uniqueidentifier` | no | The record's delivery key. |
| `TargetId` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. The OSDU id the record claimed when the reversal listed it; null for a record that claimed none. |
| `FirstAttemptId` | `bigint` | no | The source's first delivered attempt of the record ([`osdu.Attempt`](attempt.md)). |
| `FirstVersion` | `bigint` | yes | The version that attempt wrote. |
| `RunVersion` | `bigint` | yes | The version the source's last delivered attempt of the record left. |
| `Prior` | `nvarchar(16)` | no | What OSDU held before the source: `version`, `none` or `unknown`. |
| `PriorVersion` | `bigint` | yes | With `Prior` `version`: the version OSDU held. |
| `PriorAttemptId` | `bigint` | yes | The attempt that delivered or restored the version OSDU held before, when the ledger still has it: its hashes and origin are what the record gets back. |
| `State` | `nvarchar(16)` | no | `pending`, `sending`, `done`, `skipped` or `failed`. |
| `Outcome` | `nvarchar(24)` | yes | What came of it once settled: `restored`, `removed`, `already-gone`, or why it was passed over or failed; at most 24 characters. |
| `Detail` | `nvarchar(2000)` | yes | The reason, redacted. |
| `RestoredVersion` | `bigint` | yes | The version a restore put back. |
| `NewVersion` | `bigint` | yes | The version OSDU gave the restored record. |
| `RunId` | `uniqueidentifier` | yes | The reverse run that settled it, or marked it sending. |
| `UpdatedUtc` | `datetime2` | no | When the item last changed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_ReversalItem` | `PartitionId`, `ReversalId`, `DeliveryKey` | primary key | none | none | A reversal's records together, in key order: what its listing adds to and its settlement walks. |
| `IX_ReversalItem_PartitionId_ReversalId_State_DeliveryKey` | `PartitionId`, `ReversalId`, `State`, `DeliveryKey` | index | none | `Outcome` | The next page of what is still to do, in key order, and the counts by state and outcome, read from this alone. |
| `IX_ReversalItem_PartitionId_ReversalId_Outcome_DeliveryKey` | `PartitionId`, `ReversalId`, `Outcome`, `DeliveryKey` | index | none | none | A reversal's records by what came of them, a page at a time. |

## Written and read by

- **Written** by the reversal's listing of its source and by the reverse runs that settle each item
  (`SqlServerLedgerBulk.Reversals.cs`).
- **Read** by the reverse runs, the reversal's counts, and the delivery API's
  `GET /reversals/{reversalId}/records` (`DeliveryReversalEndpoints.cs`), which the GUI shows.
