---
id: delivery-table-inventory-removal-item
title: "osdu.InventoryRemovalItem: what one removal did to one id, and why"
type: table
summary: "One row per id an inventory removal reached: the version it found, the finding it had, the outcome (removed, gone, skipped or failed) and why."
keywords:
  - osdu.InventoryRemovalItem
  - inventory removal item table
  - removal outcome
  - removed
  - skipped
  - failed
  - OSDU id
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-removals
  - delivery-table-inventory-removal
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryRemoval.cs
---

# osdu.InventoryRemovalItem: what one removal did to one id, and why

What one [removal](inventory-removal.md) did to one id: removed, already gone, skipped or failed, at the version it found,
and why. Written by each chunk or round of the removal, in the transaction that acts on the inventory's rows. A removal of
gone ids writes a row only for each id it kept ([Removing gone ids](../flow/inventory-removals.md#removing-gone-ids)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryRemovalItemId` | `bigint` identity | no | The row's id within the partition. |
| `InventoryRemovalId` | `bigint` | no | The removal ([`osdu.InventoryRemoval`](inventory-removal.md)). |
| `InventoryRecordId` | `bigint` | no | The inventory row of the id ([`osdu.InventoryRecord`](inventory-record.md)). |
| `TargetId` | `nvarchar(500)` | no | Collation `Latin1_General_100_BIN2`. The OSDU id, compared exactly. |
| `Version` | `bigint` | yes | The version the inventory listed it at. |
| `Finding` | `nvarchar(16)` | no | The finding it had when the removal reached it. |
| `Outcome` | `nvarchar(16)` | no | `removed`, `gone`, `skipped` or `failed`. |
| `Reason` | `nvarchar(1000)` | yes | Why, in a line: what was asked of OSDU, what moved, or what OSDU answered, redacted. |
| `LedgerFlowId` | `uniqueidentifier` | yes | The ledger that held it, for a stale or forgotten id. |
| `DeliveryKey` | `uniqueidentifier` | yes | The record of that ledger that held it. |
| `RecordedUtc` | `datetime2` | no | When the outcome was written. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryRemovalItem` | `PartitionId`, `InventoryRemovalItemId` | primary key | none | none | A partition's removal outcomes together. |
| `IX_InventoryRemovalItem_PartitionId_InventoryRemovalId_InventoryRemovalItemId` | `PartitionId`, `InventoryRemovalId`, `InventoryRemovalItemId` | index | none | none | A removal's ids in order: its page pages through them. |
| `IX_InventoryRemovalItem_PartitionId_InventoryRemovalId_Outcome_InventoryRemovalItemId` | `PartitionId`, `InventoryRemovalId`, `Outcome`, `InventoryRemovalItemId` | index | none | none | A removal's ids of one outcome in order. |
| `IX_InventoryRemovalItem_PartitionId_TargetId` | `PartitionId`, `TargetId` | index | none | `InventoryRemovalId`, `Outcome` | An OSDU id across every removal of the partition: a lookup by id answers what was removed of it, by whom and when (the latest 100 outcomes of an id). |

## Written and read by

- **Written** by each chunk or round of a removal (`SqlServerLedgerBulk.InventoryRemoval.cs`).
- **Read** by a removal's items, `GET /api/v1/delivery/inventories/{partition}/removals/{removalId}/items`, and the
  lookup by OSDU id (`GET /api/v1/delivery/inventories/lookup`).
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the
  identity of the flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
