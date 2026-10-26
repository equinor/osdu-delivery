---
id: delivery-table-dimension-stage-record
title: "osdu.DimensionStageRecord: the records a running dimension build read through, read, or found holding a key"
type: table
summary: "One row per record a running dimension build met: one a key's label or attributes were read through, one read by its unique key, or a key such a record holds."
keywords:
  - osdu.DimensionStageRecord
  - dimension staging
  - records read through
  - incremental load
  - keyColumns
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-flow-dimension-incremental
  - delivery-table-dimension-key-record
  - delivery-table-dimension-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRecordReader.cs
---

# osdu.DimensionStageRecord: the records a running dimension build read through, read, or found holding a key

A record a running build of a [dimension](dimension.md) met, by its kind: a record a key's label or attributes were read
through, which the write copies into [`osdu.DimensionKeyRecord`](dimension-key-record.md); a record a flow with an
`incremental` block read by its unique key; or a key such a record holds, which the write copies into
[`osdu.DimensionRecord`](dimension-record.md) ([Full and incremental loads](../flow/dimension-incremental.md),
[What a build holds](../flow/dimension-build.md#what-a-build-holds)). A build deletes its rows when it ends; what a
build whose worker stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The row's place in the order the build staged its rows. |
| `EntityType` | `nvarchar(256)` | yes | Collation `Latin1_General_100_BIN2`. For a record read through, the entity type its id names (`master-data--Wellbore`); null for the other kinds. |
| `KeyHash` | `binary(32)` | yes | SHA-256 of the key's UTF-8 bytes: the key read through the record, or the key the record holds; null for a record read. |
| `Kind` | `tinyint` | no | `1` for a record a key's label or attributes were read through, `2` for a record read by its unique key, `3` for a key such a record holds. |
| `RecordHash` | `binary(32)` | no | SHA-256 of the record's id without its version, as UTF-8, for a record read through; of its unique key (the flow's `incremental.keyColumns`, its columns' values joined) for the other kinds. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageRecord` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's records together: the write reads each kind of them in one statement. |

## Written and read by

- **Written** by a build a batch of keys at a time as it reads their labels and attributes, and a page at a time as it
  reads its records by their unique key (`DimensionRunner.cs`, `DimensionRecordReader.cs`, `DimensionStage.cs`).
- **Read** by the build's write (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
