---
id: delivery-table-inventory-duplicate-member
title: "osdu.InventoryDuplicateMember: each copy in a duplicate check's groups, with its values, completeness and role"
type: table
summary: "One row per copy in a duplicate check's groups: its key's hash and values, what storage held of it, how many values it holds, and its role."
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

# osdu.InventoryDuplicateMember: each copy in a duplicate check's groups, with its values, completeness and role

One id of an [inventory](inventory.md) in a group of a [duplicate check](inventory-duplicate-check.md): the hash of its
key, what it holds, and the version the index held when the check downloaded it. The check's grouping writes the rows from
the check's own table ([The check's table](../flow/inventory-duplicates.md#the-checks-table)), one for each id whose key
another shares, numbered with its group, then gives each what it holds, a chunk at a time; grouping the check again by
another key deletes them and writes them afresh. A pick fills in what storage held of each, how many values it holds and
how many records name it, and its role. Rows are deleted with a failed check and when the check retires.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryDuplicateCheckId` | `bigint` | no | The check ([`osdu.InventoryDuplicateCheck`](inventory-duplicate-check.md)). |
| `InventoryRecordId` | `bigint` | no | The inventory row of the id ([`osdu.InventoryRecord`](inventory-record.md)). |
| `KeyHash` | `binary(32)` | no | A SHA-256 of its key: the hashes of what it holds at each property of the key, in the key's order, a property it holds nothing at standing as zeros. |
| `GroupNumber` | `int` | yes | Its group ([`osdu.InventoryDuplicateGroup`](inventory-duplicate-group.md)), set by the grouping that writes it. |
| `IndexedVersion` | `bigint` | yes | The version the search index held of it when the check downloaded it. |
| `ValuesJson` | `nvarchar(4000)` | yes | What it holds, as it holds them, property to values, as JSON within 4,000 characters: the key's properties first, cut to fit, then the check's other downloaded properties whole while they fit. |
| `StorageState` | `nvarchar(16)` | yes | What storage answered for it when its group's copies were read: `held`, `missing` or `unanswered`; null before, and for a group too broad to read. |
| `StoredVersion` | `bigint` | yes | The version storage held when its group's copies were read. |
| `Attributes` | `int` | yes | How many values it holds under `data`, each item of a list counted on its own: how complete the copy is. |
| `MentionedBy` | `bigint` | yes | How many records name it, as OSDU's search counts them; null where the search could not count them. |
| `Role` | `nvarchar(16)` | yes | `keep`, `remove`, `leave`, `review` or `gone`; null before its group's copies are read. |
| `Reason` | `nvarchar(1000)` | yes | Why it has its role, in a line. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryDuplicateMember` | `PartitionId`, `InventoryDuplicateCheckId`, `InventoryRecordId` | primary key | none | none | A check's copies in the inventory's own order: the grouping gives them their values a chunk at a time, and grouping again, a failed check and a sweep delete them a batch at a time. |
| `IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_GroupNumber_InventoryRecordId` | `PartitionId`, `InventoryDuplicateCheckId`, `GroupNumber`, `InventoryRecordId` | index | none | none | A group's copies in order: the list shows them, a pick reads them, and a selection is made of those picked to remove. |

## Written and read by

- **Written** by a check's grouping, whenever its key is set, and by its picks (`SqlServerLedgerBulk.InventoryDuplicates.cs`),
  and by an operator's choice of the copy a group keeps, which rewrites the roles of its copies.
- **Read** by the Duplicates tab, the API and the CSV export of a check, and by the making of a selection of its picks.
