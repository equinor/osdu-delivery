---
id: delivery-table-dimension-stage-element
title: "osdu.DimensionStageElement: the objects of a nested array a running dimension build read"
type: table
summary: "One row per field of an object of a nested array a running dimension build read, with the record holding the object and its place there."
keywords:
  - osdu.DimensionStageElement
  - dimension staging
  - nested array
  - elements
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-flow-dimension-elements
  - delivery-table-dimension-element
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionElementReader.cs
---

# osdu.DimensionStageElement: the objects of a nested array a running dimension build read

A field of one object of the nested array a key's records hold, as a running build of a [dimension](dimension.md) read
it: the record holding the object and the object's place among that record's objects, and the field's value. The write
numbers each key's objects in the order of their records' ids, then of the objects in each, and copies the objects of the
keys it holds into [`osdu.DimensionElement`](dimension-element.md)
([Elements](../flow/dimension-elements.md), [What a build holds](../flow/dimension-build.md#what-a-build-holds)). A build
deletes its rows when it ends; what a build whose worker stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The row's place in the order the build staged its rows. |
| `KeyHash` | `binary(32)` | no | SHA-256 of the key's UTF-8 bytes ([`osdu.DimensionStageKey`](dimension-stage-key.md)). |
| `Name` | `nvarchar(64)` | yes | Collation `Latin1_General_100_BIN2`. The field's name; null for the one row of an object holding none of the fields. |
| `Ordinal` | `int` | no | The object's place among the record's objects, from 0. |
| `Record` | `nvarchar(1024)` | no | Collation `Latin1_General_100_BIN2`. The id of the record holding the object. |
| `Value` | `nvarchar(4000)` | yes | Collation `Latin1_General_100_BIN2`. The value read, as the field keeps it, cut to 4,000 characters; null for an object holding none of the fields. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageElement` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's objects together: the write reads them in one statement, numbering each key's objects. |

## Written and read by

- **Written** by a build a page at a time as it reads the nested array (`DimensionElementReader.cs`, `DimensionStage.cs`).
- **Read** by the build to count the keys holding an object, and by its write (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
