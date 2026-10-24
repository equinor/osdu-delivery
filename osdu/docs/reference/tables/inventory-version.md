---
id: delivery-table-inventory-version
title: "osdu.InventoryVersion: each version storage keeps of a record an inventory holds"
type: table
summary: "One row per version storage keeps of a record an inventory holds, for an inventory that reads every version (versions: all)."
keywords:
  - osdu.InventoryVersion
  - inventory version table
  - record versions
  - versions all
  - inventory build
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-builds
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryRemoval.cs
---

# osdu.InventoryVersion: each version storage keeps of a record an inventory holds

One version storage keeps of a record an [inventory](inventory.md) holds, written for an inventory that declares
`versions: all`. The versions step of a build reads the versions of each served record whose
[`osdu.InventoryRecord`](inventory-record.md) `VersionsAt` is not its version, 500 at a time, and replaces its rows here;
a removal of gone ids deletes them with their row. No page, verb or route reads the table: the record's `VersionsAt`
says which version its list was read at.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryRecordId` | `bigint` | no | The inventory row whose record keeps the version ([`osdu.InventoryRecord`](inventory-record.md)). |
| `Version` | `bigint` | no | The version storage keeps. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryVersion` | `PartitionId`, `InventoryRecordId`, `Version` | primary key | none | none | A record's versions together, in order. |

## Written and read by

- **Written** by the versions step of a build, and deleted by a removal of gone ids (`SqlServerLedgerBulk.Inventory.cs`,
  `SqlServerLedgerBulk.InventoryRemoval.cs`).
- **Read** by the build's own versions step only.
