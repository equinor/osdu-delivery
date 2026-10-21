---
id: delivery-table-inventory-run
title: "osdu.InventoryRun: one build or reconcile of an inventory, how it read OSDU and what it found and changed"
type: table
summary: "One row per build or reconcile of an inventory: the run and who asked, how it read OSDU, what the merge changed, and its counts by finding."
keywords:
  - osdu.InventoryRun
  - inventory run table
  - inventory build
  - reconcile
  - listed ids
  - findings
  - owners
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-table-inventory
  - delivery-table-inventory-scan
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.cs
---

# osdu.InventoryRun: one build or reconcile of an inventory, how it read OSDU and what it found and changed

One build or reconcile of an [inventory](inventory.md): the platform run and who asked, how it read OSDU, what it found
and changed, and its counts by finding as it left them. A run is opened `running` when the inventory's turn comes and
closed `completed` or `failed` with its counts; a run of the same inventory still `running` when the next one opens is
closed `failed` (`the run ended without finishing: its process stopped before it did`)
([How a build moves rows through the scan](../flow/inventory.md#how-a-build-moves-rows-through-the-scan)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryRunId` | `bigint` identity | no | The run's id within the partition. |
| `InventoryId` | `int` | no | The inventory ([`osdu.Inventory`](inventory.md)). |
| `RunId` | `uniqueidentifier` | yes | The platform run. |
| `Operation` | `nvarchar(16)` | no | `build` or `reconcile`. |
| `Actor` | `nvarchar(200)` | no | Who asked for it. |
| `Status` | `nvarchar(16)` | no | `running`, `completed` or `failed`. |
| `StartedUtc` | `datetime2` | no | When it opened. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `ReadMode` | `nvarchar(16)` | no | `search` or `storage`. |
| `Listed` | `bigint` | no | The ids the read listed, repeats left out. |
| `Pages` | `int` | no | The pages the read took. |
| `Requests` | `bigint` | no | The requests the read sent. |
| `Added` | `bigint` | no | Ids new to the inventory. |
| `Changed` | `bigint` | no | Ids whose version or system properties moved. |
| `Gone` | `bigint` | no | Ids no longer served. |
| `Returned` | `bigint` | no | Ids served again. |
| `MissingChecked` | `bigint` | no | The ids a ledger expects that the reconcile asked storage for, to tell missing from merely unlisted. |
| `FindingsJson` | `nvarchar(max)` | yes | The counts by finding the reconcile left, as JSON. |
| `OwnersJson` | `nvarchar(max)` | yes | The owners the reconcile used, each with its count, and how it knew them, as JSON. |
| `Error` | `nvarchar(4000)` | yes | Why the run failed, redacted. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryRun` | `PartitionId`, `InventoryRunId` | primary key | none | none | A partition's inventory runs together. |
| `IX_InventoryRun_PartitionId_InventoryId_InventoryRunId` | `PartitionId`, `InventoryId`, `InventoryRunId` | index | none | none | An inventory's runs, newest first: its page, and its next build's look at the last ones. |

## Written and read by

- **Written** by the inventory runner as a build or reconcile opens and closes (`InventoryRunner.cs`, through
  `OsduLedger.Inventory.cs`).
- **Read** by an inventory's **Runs** tab and `GET /api/v1/delivery/inventories/{partition}/{inventoryId}/runs`.
