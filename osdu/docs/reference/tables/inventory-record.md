---
id: delivery-table-inventory-record
title: "osdu.InventoryRecord: each id an inventory holds or a ledger expects, and what the ledgers hold of it"
type: table
summary: "One row per OSDU id an inventory holds, or a ledger of its partition expects: its system properties, when it was seen, changed or gone, and its finding."
keywords:
  - osdu.InventoryRecord
  - inventory record table
  - finding
  - orphan
  - missing
  - stale
  - drifted
  - OSDU id
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-guide-finding-orphans
  - delivery-table-inventory
  - delivery-table-artifact
  - delivery-table-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Inventory.cs
---

# osdu.InventoryRecord: each id an inventory holds or a ledger expects, and what the ledgers hold of it

One id an [inventory](inventory.md) holds, or a ledger of its partition expects: its kind, version and system properties
as OSDU served them, when it was first listed, last changed and found gone, and what the ledgers hold of it (the
finding). The reconcile sets every row's finding from the delivery ledgers' [`osdu.Record`](record.md),
[`osdu.Artifact`](artifact.md) and [`osdu.PurgedRecord`](purged-record.md), and never writes them
([The findings](../flow/inventory.md#the-findings)).

Rows leave the table only through a removal of gone ids; a removal from OSDU marks its ids gone and keeps them.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventoryRecordId` | `bigint` identity | no | The row's id within the partition. |
| `InventoryId` | `int` | no | The inventory ([`osdu.Inventory`](inventory.md)). |
| `TargetId` | `nvarchar(500)` | no | Collation `Latin1_General_100_BIN2`. The OSDU id, compared exactly. |
| `Kind` | `nvarchar(300)` | yes | Its kind as OSDU last served it (or, for an id never listed, as storage answered when asked), cut to 300 characters. |
| `Version` | `bigint` | yes | Its version. |
| `CreateUser` | `nvarchar(256)` | yes | Who created it, cut to 256 characters. |
| `CreateTime` | `datetime2` | yes | When it was created. |
| `ModifyUser` | `nvarchar(256)` | yes | Who last modified it, cut to 256 characters. |
| `ModifyTime` | `datetime2` | yes | When it was last modified. |
| `FirstSeenUtc` | `datetime2` | yes | When a build first listed it; null for an id a ledger expects that OSDU never served while the inventory read it. |
| `ChangedUtc` | `datetime2` | yes | When a build last saw its version or system properties change, or listed it again. |
| `GoneUtc` | `datetime2` | yes | When a complete build first did not list it; null while OSDU serves it. |
| `VersionsAt` | `bigint` | yes | The latest version its versions were read at (`versions: all`), so a rebuild reads versions only for what moved ([`osdu.InventoryVersion`](inventory-version.md)). |
| `CheckedUtc` | `datetime2` | yes | When a reconcile last asked storage for it while a ledger expected it and the read did not list it; null when none has. The ids a ledger expects are asked for least recently asked first, so builds bounded by `maxMissingChecks` take turns through them. |
| `Finding` | `nvarchar(16)` | no | What the ledgers hold of it: `tracked`, `drifted`, `unconfirmed`, `stale`, `superseded`, `undoing`, `forgotten`, `orphan`, `foreign`, `missing`, `unlisted`, `gone` or `unreconciled` ([The findings](../flow/inventory.md#the-findings)). |
| `FindingUtc` | `datetime2` | no | When its finding became what it is; rewriting the same finding keeps the time. |
| `LedgerFlowId` | `uniqueidentifier` | yes | The ledger that claims it, when one does. |
| `DeliveryKey` | `uniqueidentifier` | yes | The record of that ledger that claims it. |
| `LedgerStatus` | `nvarchar(16)` | yes | That record's status. |
| `LedgerVersion` | `bigint` | yes | The version that record holds. |
| `ArtifactId` | `bigint` | yes | The artifact a delivery recorded for it, when the ledgers know it as one ([`osdu.Artifact`](artifact.md)). |
| `ArtifactState` | `nvarchar(16)` | yes | That artifact's state. |
| `Detail` | `nvarchar(1000)` | yes | What the finding rests on, in a line of at most 1,000 characters (`the ledger holds version 3, OSDU serves version 4`). |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventoryRecord` | `PartitionId`, `InventoryRecordId` | primary key | none | none | A partition's inventory rows together. |
| `IX_InventoryRecord_PartitionId_InventoryId_TargetId` | `PartitionId`, `InventoryId`, `TargetId` | unique | none | none | One row per id of an inventory: the merge seeks it by id. |
| `IX_InventoryRecord_PartitionId_InventoryId_Finding_InventoryRecordId` | `PartitionId`, `InventoryId`, `Finding`, `InventoryRecordId` | index | none | none | An inventory's rows of one finding, in order: the report's grid pages through them, and its counts are read here. |
| `IX_InventoryRecord_PartitionId_InventoryId_InventoryRecordId` | `PartitionId`, `InventoryId`, `InventoryRecordId` | index | none | none | An inventory's rows in order, whatever their finding: the grid of every id and the export page through them, seeking the inventory rather than walking every inventory of the partition. |
| `IX_InventoryRecord_PartitionId_TargetId` | `PartitionId`, `TargetId` | index | none | `InventoryId`, `Finding`, `GoneUtc` | An OSDU id across every inventory of the partition: a lookup by id answers what each inventory found of it. |

## Written and read by

- **Written** by a build's merge, the reconcile, the storage checks, a removal (which marks ids gone) and a removal of
  gone ids (which deletes rows) (`SqlServerLedgerBulk.Inventory.cs`, `SqlServerLedgerBulk.InventoryRemoval.cs`).
- **Read** by the counts by finding, the **Ids** grid and its tabs, the CSV export, the lookup by OSDU id (`GET /api/v1/delivery/inventories/lookup`), a removal, a
  selection's match and a repair.
