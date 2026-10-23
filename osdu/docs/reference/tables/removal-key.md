---
id: delivery-table-removal-key
title: "osdu.RemovalKey: the keys a running removal staged from its sources and the delivery flow's record table"
type: table
summary: "Staging for one removal run: each distinct key read from the source's current keys, its removed keys or the record table, derived to its delivery key."
keywords:
  - osdu.RemovalKey
  - removal staging
  - staged keys
  - source keys
  - removed keys list
  - record table keys
  - full key read
related:
  - delivery-concept-database
  - delivery-flow-removal
  - delivery-table-removal-run
  - delivery-table-removed-key
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022223524_RemovalFlows.cs
---

# osdu.RemovalKey: the keys a running removal staged from its sources and the delivery flow's record table

The staging a [removal run](removal-run.md) compares from: every distinct key it read, from one of three sides, converted to
the record table's key types and derived to the delivery key the ledger keys the record by
([How a key is read](../flow/removal.md#how-a-key-is-read)). Side `1` is the source's current keys, read whole every run;
side `2` its removed keys, each with the moment it was removed; side `3` the delivery flow's record table in the removal's
scope, each key with when its row last changed. A key read twice on one side is staged once, the later removal moment kept.
The comparison ([What a run finds](../flow/removal.md#what-a-run-finds)) reads the three sides against the ledger, and the
run deletes its rows when it closes; a run its process stopped leaves them for the next run of the same removal to delete.
Nothing outside a running removal reads this table.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition of the run, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `RemovalRunId` | `bigint` | no | The run that staged it ([`osdu.RemovalRun`](removal-run.md)). |
| `Side` | `tinyint` | no | `1`, the source's current keys; `2`, its removed keys; `3`, the delivery flow's record table. |
| `DeliveryKey` | `uniqueidentifier` | no | The delivery key derived from the key, as a delivery derives it. |
| `SourceKeyJson` | `nvarchar(2000)` | yes | The key tuple, a JSON array of the key columns' values as text, in the record key's order. |
| `UpdatedUtc` | `datetime2` | yes | For side `3`: when the key's row last changed in the record table (`UpdatedDate_DW`). |
| `RemovedUtc` | `datetime2` | yes | For side `2`: when the source removed the key, in UTC. |
| `FileName` | `nvarchar(800)` | yes | The file the key was read from, or the table, for a key a source listed. |
| `RowNumber` | `bigint` | yes | The key's row in that file, or its position in the table's read. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RemovalKey` | `PartitionId`, `RemovalRunId`, `Side`, `DeliveryKey` | primary key | none | none | One key once per side of a run, and each side of a run joined with another and with the ledger by key. |

## Written and read by

- **Written** by the removal runner, a batch of up to 20,000 keys at a time, as it reads each source and the record table
  (`RemovalRunner.cs`, through `SqlServerLedgerBulk.Removal.cs`), and **deleted** when the run closes, 50,000 rows at a time.
- **Read** by the run's comparison (`SqlServerLedgerBulk.Removal.cs`), in one transaction with what it writes to
  [`osdu.RemovedKey`](removed-key.md).
