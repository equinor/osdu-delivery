---
id: delivery-table-inventory-selection-item
title: "osdu.InventorySelectionItem: each id of an inventory a match found holding what its conditions ask"
type: table
summary: "One row per id a match of an inventory selection found: the version the search index held, and the values it held at the conditions' properties."
keywords:
  - osdu.InventorySelectionItem
  - inventory selection item table
  - matched ids
  - matched version
  - selection grid
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-selections
  - delivery-table-inventory-selection
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
---

# osdu.InventorySelectionItem: each id of an inventory a match found holding what its conditions ask

One id of an [inventory](inventory.md) a [match](inventory-selection.md) found holding what its conditions ask: the
version the search index holds and the values it holds there. Each page or chunk of a match writes its rows, joined with
[`osdu.InventoryRecord`](inventory-record.md) by the OSDU id in one transaction. Rows are deleted with a failed match and
when the selection retires.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventorySelectionId` | `bigint` | no | The selection ([`osdu.InventorySelection`](inventory-selection.md)). |
| `InventoryRecordId` | `bigint` | no | The inventory row of the id ([`osdu.InventoryRecord`](inventory-record.md)). |
| `MatchedVersion` | `bigint` | yes | The version the search index held of it when the match read it. |
| `ValuesJson` | `nvarchar(4000)` | yes | What it held at each property the conditions ask, property to values, as JSON cut to 4,000 characters; for a copy a duplicate check picked, what it held at the check's key. |
| `KeptInventoryRecordId` | `bigint` | yes | For an id of a selection a [duplicate check](inventory-duplicate-check.md) made: the inventory row of the copy its group keeps, which a removal by the selection checks storage still holds before it removes this one. Null for an id a match found. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventorySelectionItem` | `PartitionId`, `InventorySelectionId`, `InventoryRecordId` | primary key | none | none | A selection's ids in the inventory's own order: the grid pages through them, and a removal joins them by row. |

## Written and read by

- **Written** by each page or chunk of a match (`SqlServerLedgerBulk.InventorySelection.cs`), and with a selection made of a
  duplicate check's picks, every copy picked to remove beside the copy its group keeps (`SqlServerLedgerBulk.InventoryDuplicates.cs`).
- **Read** by a selection's grid and counts, its CSV export, and a removal by the selection, which for a duplicate check's
  picks reads each copy's kept copy too (`InventoryRunner.Removal.cs`).
