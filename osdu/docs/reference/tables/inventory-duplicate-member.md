---
id: delivery-table-inventory-duplicate-member
title: "osdu.InventoryDuplicateMember: each id of an inventory a duplicate check read, with its key, completeness and role"
type: table
summary: "One row per id a duplicate check read and kept: its key's hash and values, what storage held of it, how many values it holds, and its role."
keywords:
  - osdu.InventoryDuplicateMember
  - inventory duplicate member table
  - attribute count
  - completeness
  - mentioned by
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-duplicates
  - delivery-table-inventory-duplicate-check
  - delivery-table-inventory-duplicate-group
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateKey.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs
---

# osdu.InventoryDuplicateMember: each id of an inventory a duplicate check read, with its key, completeness and role

One id of an [inventory](inventory.md) a [duplicate check](inventory-duplicate-check.md) read: the hash of its key, what
it holds at the key, and the version the index held. Each page of the key read writes its rows, joined with
[`osdu.InventoryRecord`](inventory-record.md) by the OSDU id in one transaction. The grouping then deletes, a batch at a
time, every id whose key no other holds, and numbers the rest with their group; so once a check completed, its rows are
its copies. The copies pass fills in what storage held of each, how many values it holds and how many records name it,
and its role. Rows are deleted with a failed check and when the check retires.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryDuplicateCheckId` | `bigint` | no | The check ([`osdu.InventoryDuplicateCheck`](inventory-duplicate-check.md)). |
| `InventoryRecordId` | `bigint` | no | The inventory row of the id ([`osdu.InventoryRecord`](inventory-record.md)). |
| `KeyHash` | `binary(32)` | no | A SHA-256 of what it holds at the key, each property's values compared as the key says. |
| `GroupNumber` | `int` | yes | Its group ([`osdu.InventoryDuplicateGroup`](inventory-duplicate-group.md)); null while the check reads the keys. |
| `IndexedVersion` | `bigint` | yes | The version the search index held of it when the check read its key. |
| `ValuesJson` | `nvarchar(4000)` | yes | What it held at each property of the key, as it holds them, property to values, as JSON cut to 4,000 characters. |
| `StorageState` | `nvarchar(16)` | yes | What storage answered for it when its group's copies were read: `held`, `missing` or `unanswered`; null before, and for a group too broad to read. |
| `StoredVersion` | `bigint` | yes | The version storage held when its group's copies were read. |
| `Attributes` | `int` | yes | How many values it holds under `data`, each item of a list counted on its own: how complete the copy is. |
| `MentionedBy` | `bigint` | yes | How many records name it, as OSDU's search counts them; null where the search could not count them. |
| `Role` | `nvarchar(16)` | yes | `keep`, `remove`, `leave`, `review` or `gone`; null before its group's copies are read. |
| `Reason` | `nvarchar(1000)` | yes | Why it has its role, in a line. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryDuplicateMember` | `PartitionId`, `InventoryDuplicateCheckId`, `InventoryRecordId` | primary key | none | none | A check's ids in the inventory's own order: a page of keys joins them by row, and the deletes of a failed or retired check take them a batch at a time. |
| `IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_KeyHash` | `PartitionId`, `InventoryDuplicateCheckId`, `KeyHash` | index | none | none | The ids that share a key: the grouping counts them, lets go of those no other shares, and numbers the rest with their group. |
| `IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_GroupNumber_InventoryRecordId` | `PartitionId`, `InventoryDuplicateCheckId`, `GroupNumber`, `InventoryRecordId` | index | none | none | A group's copies in order: the list shows them, the copies pass reads them, and a selection is made of those picked to remove. |

## Written and read by

- **Written** by each page of a check's key read, its grouping and its copies pass (`SqlServerLedgerBulk.InventoryDuplicates.cs`),
  and by an operator's choice of the copy a group keeps, which rewrites the roles of its copies.
- **Read** by the Duplicates tab, the API and the CSV export of a check, and by the making of a selection of its picks.
