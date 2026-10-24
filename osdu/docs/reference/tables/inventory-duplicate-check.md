---
id: delivery-table-inventory-duplicate-check
title: "osdu.InventoryDuplicateCheck: a check of an inventory's ids for duplicate records, by an operator's key"
type: table
summary: "One row per duplicate check of an inventory: its key, where it stands, what it read and found, and the selection last made of its picks."
keywords:
  - osdu.InventoryDuplicateCheck
  - inventory duplicate check table
  - duplicate records
  - duplicate key
  - copies to remove
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-duplicates
  - delivery-table-inventory-duplicate-group
  - delivery-table-inventory-duplicate-member
  - delivery-table-inventory-selection
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
---

# osdu.InventoryDuplicateCheck: a check of an inventory's ids for duplicate records, by an operator's key

One check of an [inventory](inventory.md)'s ids for duplicate records
([Finding duplicate records](../flow/inventory-duplicates.md)): the key an operator compares the records
by, where the check stands, what it read and found, and the build of the inventory it read. The control plane writes the
row when an operator asks for a check, with the run it queued; the inventory flow's `duplicates` run fills it, its groups
in [`osdu.InventoryDuplicateGroup`](inventory-duplicate-group.md) and its ids in
[`osdu.InventoryDuplicateMember`](inventory-duplicate-member.md). A check is kept a week, as a selection is: the next check
of the partition deletes those older, or keeps one `expired` (its groups deleted) when a removal took the picks of a
selection made of it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryDuplicateCheckId` | `bigint` identity | no | The check's number within the partition. |
| `InventoryId` | `int` | no | The inventory it checks ([`osdu.Inventory`](inventory.md)). |
| `RunId` | `uniqueidentifier` | yes | The platform run the check ran as; null until it is queued. |
| `Actor` | `nvarchar(200)` | no | Who asked for the check. |
| `KeyJson` | `nvarchar(max)` | no | The key in the one spelling a check keeps: each property's path and comparison (`exact`, `ignoreCase` or `loose`), in order, as JSON. |
| `KeyHash` | `varchar(64)` | no | A SHA-256 of `KeyJson` in hex, by which the same key asked again of the same build finds the check already made. |
| `Status` | `nvarchar(16)` | no | `queued`, `running`, `completed`, `failed` or `expired`. |
| `Phase` | `nvarchar(16)` | yes | What a running check is doing: `keys` (reading every id's key) or `copies` (reading each group's copies); null otherwise. |
| `NotesJson` | `nvarchar(max)` | yes | What the reader should know of how the check read OSDU, a JSON list: a key property no record held a value at, ids the search did not return, groups too broad to pick from. |
| `BuildRunId` | `bigint` | yes | The inventory's last build when the check read OSDU; a later build makes the check stale. |
| `Candidates` | `bigint` | no | The ids the inventory served when the check started. |
| `Checked` | `bigint` | no | The records the search returned while the check read the keys. |
| `Keyed` | `bigint` | no | The inventory's ids holding a value at one of the key's properties at least. |
| `Unkeyed` | `bigint` | no | The inventory's ids holding no value at any of the key's properties, passed over. |
| `Outside` | `bigint` | no | Records the search returned that the inventory serves no row of: written since its last build, or outside its query. |
| `Changed` | `bigint` | no | Ids the search returned at another version than the inventory listed: changed since its last build. |
| `Groups` | `bigint` | no | The groups of ids that share a key. |
| `Copies` | `bigint` | no | The ids in those groups. |
| `GroupsJudged` | `bigint` | no | The groups whose copies the check has read and judged, which a page following it counts against `Groups`; a group too broad to pick from is judged at once. |
| `Decided` | `bigint` | no | The groups whose copy to keep is decided: picked by the check, or chosen by an operator. |
| `Review` | `bigint` | no | The groups left for an operator to look at. |
| `ToRemove` | `bigint` | no | The copies picked to remove. |
| `Requests` | `bigint` | no | The requests the check sent: searches and storage reads. |
| `SelectionId` | `bigint` | yes | The selection last made of the copies picked to remove ([`osdu.InventorySelection`](inventory-selection.md)); null until one is made. |
| `ChosenUtc` | `datetime2` | yes | When an operator last chose the copy a group keeps, or took a choice back: a selection made before then no longer says what the check picks. |
| `CreatedUtc` | `datetime2` | no | When the check was opened. |
| `StartedUtc` | `datetime2` | yes | When its run started. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `Error` | `nvarchar(4000)` | yes | Why the check failed, redacted; null for one that did not. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryDuplicateCheck` | `PartitionId`, `InventoryDuplicateCheckId` | primary key | none | none | A partition's checks together, read by number. |
| `IX_InventoryDuplicateCheck_PartitionId_InventoryId_KeyHash_InventoryDuplicateCheckId` | `PartitionId`, `InventoryId`, `KeyHash`, `InventoryDuplicateCheckId` | index | none | none | The same key asked again of an inventory finding the newest check made of it. |
| `IX_InventoryDuplicateCheck_PartitionId_InventoryId_InventoryDuplicateCheckId` | `PartitionId`, `InventoryId`, `InventoryDuplicateCheckId` | index | none | none | An inventory's checks, newest first: the Duplicates tab's history. |
| `IX_InventoryDuplicateCheck_PartitionId_CreatedUtc` | `PartitionId`, `CreatedUtc` | index | none | none | The checks a sweep retires, oldest first. |

## Written and read by

- **Written** by the control plane when an operator asks for a check, names its run, or closes one whose run ended
  without finishing it (`DeliveryInventoryEndpoints.Duplicates.cs`); by the `duplicates` run as it reads and judges
  (`InventoryRunner.Duplicates.cs`, through `OsduLedger.InventoryDuplicates.cs` and `SqlServerLedgerBulk.InventoryDuplicates.cs`);
  by an operator's choice of the copy a group keeps, which moves its counts; by the making of a selection of its picks
  (`SelectionId`); and by the sweep.
- **Read** by the Duplicates tab, the API, and the removals of the inventory, which name the check that made the selection
  they took their ids from.
