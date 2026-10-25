---
id: delivery-table-dimension-change
title: "osdu.DimensionChange: each change a build made to one key of a dimension"
type: table
summary: "One row per change a build made to a key: it arrived, left, came back, or moved from one value to another, with the values it moved from and to."
keywords:
  - osdu.DimensionChange
  - dimension change table
  - change log
  - added
  - removed
  - moved
  - restored
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-run
  - delivery-table-dimension-value
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
---

# osdu.DimensionChange: each change a build made to one key of a dimension

A change a [build](dimension-run.md) made to one original (key): it arrived (after the dimension's first build), left,
came back, or moved from one member to another. The first build's originals are its arrivals, told by their first build,
not by a row here ([The tables a dimension keeps](../concepts/dimension-tables.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ChangeId` | `bigint` identity | no | The change's id, unique on its own. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that made it ([`osdu.DimensionRun`](dimension-run.md)). |
| `ValueId` | `bigint` | no | The original it changed ([`osdu.DimensionValue`](dimension-value.md)). |
| `Change` | `nvarchar(16)` | no | `added`, `removed`, `moved` or `restored`. |
| `FromMemberId` | `bigint` | yes | The member it was under before; null when it was under none. |
| `ToMemberId` | `bigint` | yes | The member it is under after; null when it is under none. |
| `ChangedUtc` | `datetime2` | no | When the build made it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionChange` | `PartitionId`, `DimensionId`, `ChangeId` | primary key | none | none | A dimension's changes together, in the order they were written: every statement over one dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it. |
| `IX_DimensionChange_ChangeId` | `ChangeId` | unique | none | none | A change named by its id. |
| `IX_DimensionChange_PartitionId_DimensionId_ValueId_ChangeId` | `PartitionId`, `DimensionId`, `ValueId`, `ChangeId` | index | none | none | An original's history. |
| `IX_DimensionChange_PartitionId_DimensionRunId` | `PartitionId`, `DimensionRunId` | index | none | none | A build's changes. |

## Written and read by

- **Written** by a build's merge, which logs every change it makes (`SqlServerDimensionStore.cs`).
- **Read** by the dimension's changes page, `sqlflow dimensions` and the delivery API.
