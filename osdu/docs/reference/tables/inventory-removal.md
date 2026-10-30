---
id: delivery-table-inventory-removal
title: "osdu.InventoryRemoval: one removal of an inventory's ids an operator asked for, and what it came to"
type: table
summary: "One row per removal of an inventory's ids: the run, who asked, the finding and scope, how many ids the operator was shown, and its tallies."
keywords:
  - osdu.InventoryRemoval
  - inventory removal table
  - remove orphans
  - soft delete
  - purge
  - removal tallies
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-removals
  - delivery-table-inventory-removal-item
  - delivery-table-inventory-selection
  - delivery-table-activity
  - delivery-wiki-0014-inventory-removals
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRemovals.cs
---

# osdu.InventoryRemoval: one removal of an inventory's ids an operator asked for, and what it came to

One removal of an [inventory](inventory.md)'s ids an operator asked for: the run it was, who asked, the finding and how
much of each record it removes, how many ids the operator was shown, and what it came to. Every id it reached is an
[`osdu.InventoryRemovalItem`](inventory-removal-item.md). A `remove` run opens it `running`, adds to its tallies with
every chunk or round, and closes it `completed` or `failed`
([What a removal does](../flow/inventory-removals.md#what-a-removal-does), [decision 0014](../../wiki/decisions/0014-inventory-removals.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryRemovalId` | `bigint` identity | no | The removal's id within the partition. |
| `InventoryId` | `int` | no | The inventory whose ids it removes. |
| `RunId` | `uniqueidentifier` | yes | The platform run the removal ran as. |
| `Actor` | `nvarchar(200)` | no | Who asked for it. |
| `Finding` | `nvarchar(16)` | no | The finding whose ids it removes. |
| `Scope` | `nvarchar(16)` | no | `record` (a soft delete), `everything` (a purge) or `inventory` (the inventory's own rows of gone ids, nothing asked of OSDU; [Removing gone ids](../flow/inventory-removals.md#removing-gone-ids)). |
| `NamesIds` | `bit` | no | Set when the removal named its ids; clear when it took every id of the finding. |
| `Requested` | `bigint` | no | How many ids the operator was shown, which the removal held the inventory to before it removed anything. |
| `Status` | `nvarchar(16)` | no | `running`, `completed` or `failed`. |
| `StartedUtc` | `datetime2` | no | When it opened. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `Removed` | `bigint` | no | Ids it removed. |
| `Gone` | `bigint` | no | Ids OSDU no longer held. |
| `Skipped` | `bigint` | no | Ids it passed over. |
| `Failed` | `bigint` | no | Ids whose removal failed. |
| `Error` | `nvarchar(4000)` | yes | Why the removal stopped, redacted; null for one that completed. |
| `ActivityId` | `bigint` | yes | The audit trail's `inventory-remove` activity of the removal ([`osdu.Activity`](activity.md)). |
| `InventorySelectionId` | `bigint` | yes | The selection the removal took its ids from ([`osdu.InventorySelection`](inventory-selection.md)); null for a removal that named its ids or took every id of the finding. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryRemoval` | `PartitionId`, `InventoryRemovalId` | primary key | none | none | A partition's removals together. |
| `IX_InventoryRemoval_PartitionId_InventoryId_InventoryRemovalId` | `PartitionId`, `InventoryId`, `InventoryRemovalId` | index | none | none | An inventory's removals, newest first: its page lists them, and a removal closes the earlier ones its process left open. |

## Written and read by

- **Written** by a `remove` run of the inventory flow (`OsduLedger.InventoryRemoval.cs`,
  `SqlServerLedgerBulk.InventoryRemoval.cs`).
- **Read** by an inventory's **Removals** tab, `GET /api/v1/delivery/inventories/{partition}/{inventoryId}/removals` and
  `GET /api/v1/delivery/inventories/{partition}/removals/{removalId}`.
