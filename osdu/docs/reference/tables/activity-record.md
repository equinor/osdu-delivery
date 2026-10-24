---
id: delivery-table-activity-record
title: "osdu.ActivityRecord: each record an intervention for many records changed, named once"
type: table
summary: "One row per record a release, redelivery, reversal, verify reconcile or ledger deletion reached, written by the statement that changed it."
keywords:
  - osdu.ActivityRecord
  - activity record table
  - audit trail
  - record history
  - bulk release
  - intervention records
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-table-activity
  - delivery-table-record
  - delivery-table-purged-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Problems.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
---

# osdu.ActivityRecord: each record an intervention for many records changed, named once

One record an intervention changed, written in the statement that changed it. An intervention made for one record names
it on its [`osdu.Activity`](activity.md) row; one made for many (a release of a whole flow or of every record a problem
keeps blocked, a redelivery of every delivered record, a reversal, a verify run that marks drifted or missing records for
redelivery, the deletion of removed records from the ledger) names each record here. A record's activities are the ones
naming it on their row and the ones naming it here, so its history shows every request that reached it, with who asked
and when, however many records the request reached ([The delivery ledger](../concepts/ledger.md)).

Never pruned: these rows are records' histories.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the record's flow. |
| `DeliveryKey` | `uniqueidentifier` | no | The record's delivery key ([`osdu.Record`](record.md)). |
| `ActivityId` | `bigint` | no | The intervention, as the audit trail numbers it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_ActivityRecord` | `PartitionId`, `FlowId`, `DeliveryKey`, `ActivityId` | primary key | none | none | A record's requests: one seek of its key, in the order the trail numbered them. |

## Written and read by

- **Written** by the ledger's bulk statements that release, redeliver, reverse, reconcile or delete records
  (`SqlServerLedgerBulk.Problems.cs`, `SqlServerLedgerBulk.Reversals.cs`, `SqlServerLedgerBulk.Purges.cs`).
- **Read** by a record's Timeline and `GET /records/{flowId}/{key}/activities`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of a renamed pipeline's old ledger under
  its new identity, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
