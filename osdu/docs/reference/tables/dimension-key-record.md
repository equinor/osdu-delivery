---
id: delivery-table-dimension-key-record
title: "osdu.DimensionKeyRecord: each record a dimension key's label and attributes were read through"
type: table
summary: "One row per key and record its label and attributes were read through: the record's id hash and entity type, how an incremental load finds keys to read again."
keywords:
  - osdu.DimensionKeyRecord
  - dimension key record table
  - incremental load
  - read through
  - label records
  - changed records
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-value
  - delivery-table-dimension-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
---

# osdu.DimensionKeyRecord: each record a dimension key's label and attributes were read through

A record one key's label and attributes were read through: the record the key names and every record a step reached or
looked for, found or not. A build rewrites them for every key it reads; an incremental load finds the keys to read again
because a record changed by looking the record up here, and asks the search what changed in the entity types the table
names ([Full and incremental loads](../flow/dimension.md#full-and-incremental-loads)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `KeyRecordId` | `bigint` identity | no | The row's own number, which the table is stored in the order of. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `ValueId` | `bigint` | no | The original read through the record ([`osdu.DimensionValue`](dimension-value.md)). |
| `RecordHash` | `binary(32)` | no | SHA-256 of the record's id without its version, as UTF-8: what a changed record is looked up by. |
| `EntityType` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The entity type the id names (`master-data--Wellbore`): the kinds an incremental load asks the search what changed in. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionKeyRecord` | `PartitionId`, `KeyRecordId` | primary key | none | none | A partition's rows together, in the order they were written. |
| `IX_DimensionKeyRecord_KeyRecordId` | `KeyRecordId` | unique | none | none | A row named by its number. |
| `IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash` | `PartitionId`, `DimensionId`, `ValueId`, `RecordHash` | unique | none | none | A key's records, once each: what a write rewrites them by. |
| `IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash` | `PartitionId`, `DimensionId`, `RecordHash` | index | none | none | The keys read through a record that changed: one seek a record. |
| `IX_DimensionKeyRecord_PartitionId_DimensionId_EntityType` | `PartitionId`, `DimensionId`, `EntityType` | index | none | none | The entity types a dimension's keys were read through, which an incremental load asks the search what changed in. |

## Written and read by

- **Written** by a build for every key it reads (`SqlServerDimensionStore.cs`).
- **Read** by an incremental load (`DimensionRunner.Incremental.cs`).
