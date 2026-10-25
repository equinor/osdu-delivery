---
id: delivery-table-dimension-record
title: "osdu.DimensionRecord: the keys each record of a dimension held when a load last read it"
type: table
summary: "One row per record and key it held, the record named by a hash of its unique key (incremental.keyColumns): what an incremental load reads again."
keywords:
  - osdu.DimensionRecord
  - dimension record table
  - incremental load
  - keyColumns
  - record keys
  - changed records
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-key-record
  - delivery-table-dimension-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
---

# osdu.DimensionRecord: the keys each record of a dimension held when a load last read it

A key one record of a [dimension](dimension.md) held when a load last read the record, the record named by its unique key
(the flow's `incremental.keyColumns`): what an incremental load reads again when the record changes, besides the keys it
holds now. Only a flow with an `incremental` block keeps them: a full load of such a flow rewrites them all, and an
incremental load rewrites those of the records it read
([Full and incremental loads](../flow/dimension-incremental.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionRecordId` | `bigint` identity | no | The row's own number, which the table is stored in the order of. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `RecordHash` | `binary(32)` | no | SHA-256 of the record's unique key, its columns' values joined, as UTF-8. |
| `ValueId` | `bigint` | no | The original the record held ([`osdu.DimensionValue`](dimension-value.md)). |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionRecord` | `PartitionId`, `DimensionId`, `DimensionRecordId` | primary key | none | none | A dimension's records' keys together, in the order they were written: every statement over one dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it. |
| `IX_DimensionRecord_DimensionRecordId` | `DimensionRecordId` | unique | none | none | A row named by its number. |
| `IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId` | `PartitionId`, `DimensionId`, `RecordHash`, `ValueId` | unique | none | none | A record's keys, once each: what a load rewrites them by and an incremental load reads them by. |
| `IX_DimensionRecord_PartitionId_DimensionId_ValueId` | `PartitionId`, `DimensionId`, `ValueId` | index | none | none | The records of a key: what goes with a key a load removes. |

## Written and read by

- **Written** by the loads of a flow with an `incremental` block (`SqlServerDimensionStore.cs`).
- **Read** by an incremental load (`DimensionRunner.Incremental.cs`).
