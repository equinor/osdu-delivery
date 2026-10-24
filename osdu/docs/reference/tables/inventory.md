---
id: delivery-table-inventory
title: "osdu.Inventory: one inventory of an inventory flow in a partition, what it reads and its last build and reconcile"
type: table
summary: "One row per inventory a flow declares in a partition: the kind and query it reads, how it reads, the owners its last reconcile used, and its last runs."
keywords:
  - osdu.Inventory
  - inventory table
  - inventory flow
  - orphans
  - owners
  - build
  - reconcile
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-builds
  - delivery-guide-finding-orphans
  - delivery-table-inventory-run
  - delivery-table-inventory-record
  - delivery-wiki-0013-inventory-flows
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.cs
---

# osdu.Inventory: one inventory of an inventory flow in a partition, what it reads and its last build and reconcile

One inventory of an [inventory flow](../flow/inventory.md) in a partition: what it reads, the owners its last reconcile
used and how it knew them, and its last build and reconcile. Its counts by finding are read from its records in
[`osdu.InventoryRecord`](inventory-record.md), never kept here. An inventory flow keeps its tables under its own ledger
in the partition (kind `inventory`), and no table of an inventory has a foreign key into another
([The tables an inventory keeps](../flow/inventory-builds.md#the-tables-an-inventory-keeps)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryId` | `int` identity | no | The inventory's id within the partition. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the inventory flow in the partition ([`osdu.Ledger`](ledger.md)). |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `Name` | `nvarchar(200)` | no | The inventory's name in its flow. |
| `Kind` | `nvarchar(300)` | no | The kind it reads, as the flow declares it (wildcards allowed), at most 300 characters. |
| `Query` | `nvarchar(4000)` | yes | The query that narrows a search read, at most 4,000 characters; null for every record of the kind. |
| `ReadMode` | `nvarchar(16)` | no | `search` or `storage`. |
| `Versions` | `nvarchar(16)` | no | `latest` or `all`. |
| `OwnersJson` | `nvarchar(max)` | yes | The identities the last reconcile took as this estate's, each with how many records it created that a ledger claims, as JSON. |
| `OwnersSource` | `nvarchar(16)` | yes | How it knew them: `declared` (the flow names them), `inferred` (from the records a ledger claims) or `none`. |
| `CreatedUtc` | `datetime2` | no | When the inventory was first registered. |
| `UpdatedUtc` | `datetime2` | no | When the row last changed. |
| `LastBuildRunId` | `bigint` | yes | The last build that completed ([`osdu.InventoryRun`](inventory-run.md)). |
| `LastBuiltUtc` | `datetime2` | yes | When it completed. |
| `LastReconcileRunId` | `bigint` | yes | The last reconcile that completed, a build's or one of its own. |
| `LastReconciledUtc` | `datetime2` | yes | When it completed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Inventory` | `PartitionId`, `InventoryId` | primary key | none | none | A partition's inventories together. |
| `IX_Inventory_PartitionId_FlowId_Name` | `PartitionId`, `FlowId`, `Name` | unique | none | none | One row per inventory of a flow's ledger in the partition: a build finds its own by flow and name. |

## Written and read by

- **Written** by every build and reconcile, which registers the inventory and rewrites its kind, query, read mode and
  versions from the document, and by a completed run, which sets its last build, last reconcile and owners
  (`OsduLedger.Inventory.cs`).
- **Read** by the inventories listing and an inventory's page, `GET /api/v1/delivery/inventories` and `sqlflow inventory`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which gives the inventories of an old ledger the identity and the
  name of the flow that replaced it or took it over, all at once, and refuses a flow that holds an inventory of one of their
  names ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
