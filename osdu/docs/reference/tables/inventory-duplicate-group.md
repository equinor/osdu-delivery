---
id: delivery-table-inventory-duplicate-group
title: "osdu.InventoryDuplicateGroup: each group of an inventory's ids a duplicate check found sharing a key"
type: table
summary: "One row per group of ids a duplicate check found sharing a key: its copies, how it came out and why, the copy it keeps and who chose it."
keywords:
  - osdu.InventoryDuplicateGroup
  - inventory duplicate group table
  - copy kept
  - group for review
  - operator choice
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-duplicates
  - delivery-table-inventory-duplicate-check
  - delivery-table-inventory-duplicate-member
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs
---

# osdu.InventoryDuplicateGroup: each group of an inventory's ids a duplicate check found sharing a key

One group of ids a [duplicate check](inventory-duplicate-check.md) found sharing a key
([Which copy is kept](../flow/inventory-duplicates.md#which-copy-is-kept)): how many copies, what they share, how it came out and why,
the copy it keeps, and the operator who chose it. The check's grouping writes every group at once, the largest first,
from the check's own table, and writes them afresh when the check is grouped again by another key; a pick writes how each
came out, a round of groups at a time; an operator's choice rewrites one. Rows are deleted with a failed check and when
the check retires.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryDuplicateCheckId` | `bigint` | no | The check ([`osdu.InventoryDuplicateCheck`](inventory-duplicate-check.md)). |
| `GroupNumber` | `int` | no | The group's number within its check: the group of the most copies first, groups of as many copies in the order of their first id. |
| `KeyHash` | `binary(32)` | no | The key its copies share, as each copy's row keeps it. |
| `Copies` | `int` | no | How many copies share the key. |
| `ValuesJson` | `nvarchar(4000)` | yes | What its first copy holds, as that copy's row keeps it: the key's properties first, property to values, as JSON within 4,000 characters. |
| `Outcome` | `nvarchar(16)` | no | `pending` (waiting to be picked), `picked` (the check picked the copy to keep), `chosen` (an operator did), `review` (an operator has to look at it, or too broad to pick from) or `settled` (storage holds one copy or none). |
| `Reason` | `nvarchar(1000)` | yes | Why it came out so, in a line: the copy kept and what it holds, or what left it for review. |
| `KeptInventoryRecordId` | `bigint` | yes | The inventory row of the copy it keeps; null while none is decided. |
| `ChosenBy` | `nvarchar(200)` | yes | The operator who chose the copy kept, for a group an operator decided. |
| `ChosenUtc` | `datetime2` | yes | When they chose it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryDuplicateGroup` | `PartitionId`, `InventoryDuplicateCheckId`, `GroupNumber` | primary key | none | none | A check's groups in order, the largest first: the list pages through them, and a pick reads them so. |
| `IX_InventoryDuplicateGroup_PartitionId_InventoryDuplicateCheckId_Outcome_GroupNumber` | `PartitionId`, `InventoryDuplicateCheckId`, `Outcome`, `GroupNumber` | index | none | none | The groups of one outcome in order: the list's tabs page through them, a pick finds the pending ones, and the tabs count each outcome. |

## Written and read by

- **Written** by the check's grouping and its picks (`SqlServerLedgerBulk.InventoryDuplicates.cs`), and by an
  operator's choice of the copy a group keeps, or a choice taken back, through the same statement, under an update lock
  on the group.
- **Read** by the Duplicates tab and the API, the CSV export of a check, and the making of a selection of its picks,
  which takes the copies picked to remove of every decided group, each beside the copy its group keeps.
