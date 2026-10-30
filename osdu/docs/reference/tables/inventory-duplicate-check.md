---
id: delivery-table-inventory-duplicate-check
title: "osdu.InventoryDuplicateCheck: a check of an inventory's ids for duplicate records, by an operator's key"
type: table
summary: "One row per duplicate check of an inventory: what it downloaded and its table, its key, where it stands, what it found, and its last selection."
keywords:
  - osdu.InventoryDuplicateCheck
  - inventory duplicate check table
  - duplicate records
  - duplicate key
  - check table dup_
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
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicateTable.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InventoryDuplicateSweepService.cs
---

# osdu.InventoryDuplicateCheck: a check of an inventory's ids for duplicate records, by an operator's key

One check of an [inventory](inventory.md)'s ids for duplicate records
([Finding duplicate records](../flow/inventory-duplicates.md)): the properties it downloaded and the table it keeps them in,
the key its groups are formed by and who set it, where the check stands, what it found, and the build of the inventory it
read. The control plane writes the row when an operator asks for a check or a pick, with the run it queued, and when an
operator groups it again by another key; the inventory flow's `duplicates` run downloads into the check's own table
([The check's table](../flow/inventory-duplicates.md#the-checks-table)) and picks, its groups in
[`osdu.InventoryDuplicateGroup`](inventory-duplicate-group.md) and their copies in
[`osdu.InventoryDuplicateMember`](inventory-duplicate-member.md). A check is kept a week, as a selection is: the next check
of the partition, or the control plane's hourly sweep, deletes those older with their groups and tables, or keeps one
`expired` (its groups deleted) when a removal took the picks of a selection made of it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryDuplicateCheckId` | `bigint` identity | no | The check's number within the partition. |
| `InventoryId` | `int` | no | The inventory it checks ([`osdu.Inventory`](inventory.md)). |
| `RunId` | `uniqueidentifier` | yes | The platform run the check ran as; null until it is queued. |
| `Actor` | `nvarchar(200)` | no | Who asked for the check. |
| `KeyJson` | `nvarchar(max)` | no | The key the groups are formed by, in the one spelling a check keeps: each property's path, in order, as JSON. A key written when keys named a comparison is read for its paths alone. |
| `KeyHash` | `varchar(64)` | no | A SHA-256 of `KeyJson` in hex, by which the same key asked again finds the check grouped by it. |
| `AttributesJson` | `nvarchar(max)` | yes | The properties the check downloads, each with its column of the check's table, as JSON: the key's, and those the check before it downloaded. A key of any of them groups the check again without reading OSDU. Null for a check opened before checks downloaded. |
| `TableName` | `nvarchar(128)` | yes | The check's table in the `osdu` schema (`dup_<check>`), once its download made it; null before, and after its download failed, a later download of the inventory completed, or the sweep retired the check. |
| `KeyedBy` | `nvarchar(200)` | yes | Who set the key the groups are formed by: who asked for the check, or who grouped it again since. |
| `KeyedUtc` | `datetime2` | yes | When the key the groups are formed by was set. |
| `PickedUtc` | `datetime2` | yes | When a pick last read every group waiting to be picked; null while groups wait, after a pick stopped, and after the check was grouped again. |
| `Status` | `nvarchar(16)` | no | `queued` or `running` (its download, or a pick of it), `completed` (downloaded), `failed` (its download stopped) or `expired`. |
| `Phase` | `nvarchar(16)` | yes | What a queued or running check does: `keys` (downloading every id's properties) or `copies` (a pick reading each group's copies); null otherwise. |
| `NotesJson` | `nvarchar(max)` | yes | What the reader should know of how the check read OSDU, a JSON list: a key property no record held a value at, ids the search did not return, groups too broad to pick from. |
| `BuildRunId` | `bigint` | yes | The inventory's last build when the check read OSDU; a later build makes the check stale. |
| `Candidates` | `bigint` | no | The ids the inventory served when the check started. |
| `Checked` | `bigint` | no | The records the search returned while the check downloaded. |
| `Keyed` | `bigint` | no | The inventory's ids holding a value at one of the key's properties at least. |
| `Unkeyed` | `bigint` | no | The inventory's ids holding no value at any of the key's properties, passed over. |
| `Outside` | `bigint` | no | Records the search returned that the inventory serves no row of: written since its last build, or outside its query. |
| `Changed` | `bigint` | no | Ids the search returned at another version than the inventory listed: changed since its last build. |
| `Groups` | `bigint` | no | The groups of ids holding equal values at the key. |
| `Copies` | `bigint` | no | The ids in those groups. |
| `GroupsJudged` | `bigint` | no | The groups judged: those too broad to read, at once when the check is grouped, and those whose copies a pick has read, which a page following it counts against those waiting. Grouping again sets it back to the broad ones. |
| `Decided` | `bigint` | no | The groups whose copy to keep is decided: picked by the check, or chosen by an operator. |
| `Review` | `bigint` | no | The groups left for an operator to look at. |
| `ToRemove` | `bigint` | no | The copies picked to remove. |
| `Requests` | `bigint` | no | The requests the check's download and picks sent: searches and storage reads. |
| `SelectionId` | `bigint` | yes | The selection last made of the copies picked to remove ([`osdu.InventorySelection`](inventory-selection.md)); null until one is made. |
| `ChosenUtc` | `datetime2` | yes | When an operator last chose the copy a group keeps, or took a choice back: a selection made before then no longer says what the check picks. |
| `CreatedUtc` | `datetime2` | no | When the check was opened. |
| `StartedUtc` | `datetime2` | yes | When its run started. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `Error` | `nvarchar(4000)` | yes | Why its download failed, or why its last pick stopped, redacted; null for one that did neither. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryDuplicateCheck` | `PartitionId`, `InventoryDuplicateCheckId` | primary key | none | none | A partition's checks together, read by number. |
| `IX_InventoryDuplicateCheck_PartitionId_InventoryId_InventoryDuplicateCheckId` | `PartitionId`, `InventoryId`, `InventoryDuplicateCheckId` | index | none | none | An inventory's checks, newest first: the Duplicates tab's history, the current check a key asked again groups, and the earlier checks whose tables a completed download lets go. |
| `IX_InventoryDuplicateCheck_PartitionId_CreatedUtc` | `PartitionId`, `CreatedUtc` | index | none | none | The checks a sweep retires, oldest first. |

## Written and read by

- **Written** by the control plane when an operator asks for a check or a pick, names its run, groups the check again by
  another key (`KeyJson`, `KeyedBy`, `KeyedUtc` and the counts), or closes one whose run ended without finishing it
  (`DeliveryInventoryEndpoints.Duplicates.cs`); by the `duplicates` run as it downloads, groups and picks
  (`InventoryRunner.Duplicates.cs`, through `OsduLedger.InventoryDuplicates.cs` and `SqlServerLedgerBulk.InventoryDuplicates.cs`),
  which also clears `TableName` of the inventory's earlier checks when a download completes; by an operator's choice of the
  copy a group keeps, which moves its counts; by the making of a selection of its picks (`SelectionId`); and by the sweep,
  when a check is asked for and every hour in the control plane (`InventoryDuplicateSweepService.cs`).
- **Read** by the Duplicates tab, the API, and the removals of the inventory, which name the check that made the selection
  they took their ids from.
