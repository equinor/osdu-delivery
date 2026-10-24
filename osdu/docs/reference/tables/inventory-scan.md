---
id: delivery-table-inventory-scan
title: "osdu.InventoryScan: each id a build listed, staged until the read is whole and merged into the inventory"
type: table
summary: "One row per id an inventory build's read listed, with its system properties as OSDU served them, held only until the build merges or fails."
keywords:
  - osdu.InventoryScan
  - inventory scan table
  - staging
  - inventory build
  - merge
  - listed ids
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-builds
  - delivery-table-inventory-run
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Inventory.cs
---

# osdu.InventoryScan: each id a build listed, staged until the read is whole and merged into the inventory

One id a build listed, staged until the build's read is whole and merged into the inventory. The read appends what it
lists 10,000 rows a bulk copy, each in its own transaction, so nothing of [`osdu.InventoryRecord`](inventory-record.md)
changes while the read runs, and a read that fails part way leaves the inventory as the last complete build left it. The
merge deletes the run's rows in the transaction that merges them; a failed run's rows are deleted when it closes, and
those of a run whose process stopped are deleted by the next run of the inventory
([How a build moves rows through the scan](../flow/inventory-builds.md#how-a-build-moves-rows-through-the-scan)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ScanId` | `bigint` identity | no | The row's id within the partition. |
| `InventoryRunId` | `bigint` | no | The build that listed it ([`osdu.InventoryRun`](inventory-run.md)). |
| `TargetId` | `nvarchar(500)` | no | Collation `Latin1_General_100_BIN2`. The OSDU id, compared exactly. |
| `Kind` | `nvarchar(300)` | yes | Its kind, as OSDU served it, at most 300 characters. |
| `Version` | `bigint` | yes | Its version. |
| `CreateUser` | `nvarchar(256)` | yes | Who created it, at most 256 characters. |
| `CreateTime` | `datetime2` | yes | When it was created. |
| `ModifyUser` | `nvarchar(256)` | yes | Who last modified it, at most 256 characters. |
| `ModifyTime` | `datetime2` | yes | When it was last modified. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryScan` | `PartitionId`, `ScanId` | primary key | none | none | A partition's staged rows together, in the order the read appended them. |
| `IX_InventoryScan_PartitionId_InventoryRunId_TargetId` | `PartitionId`, `InventoryRunId`, `TargetId` | index | none | none | A build's ids by id: its merge reads them in this order, and a build's leftovers are deleted by run. |

## Written and read by

- **Written** by a build's read, a chunk at a time (`SqlServerLedgerBulk.Inventory.cs`).
- **Read** by the build's merge, and nothing else.
