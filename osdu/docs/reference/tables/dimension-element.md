---
id: delivery-table-dimension-element
title: "osdu.DimensionElement: each field of each object of a nested array a dimension's keys hold"
type: table
summary: "One row per field of an object of a nested array a key's records hold (a curve of a well log): the key, the object's place, the field and its value."
keywords:
  - osdu.DimensionElement
  - dimension element table
  - nested array
  - elements
  - curves
  - element rows
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-value
  - delivery-table-dimension-attribute-name
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
---

# osdu.DimensionElement: each field of each object of a nested array a dimension's keys hold

One field of one object of a nested array a key's records hold (a curve of a well log): the key, the object's place among
the key's (from 1, in the order of the records' ids and then of the objects in each record), the field, and the value read.
An object holding none of its fields is one row of its first field with no value. Each object is a row of the dimension's
table, numbered in its `element` column ([The tables a dimension keeps](../concepts/dimension-tables.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ElementValueId` | `bigint` identity | no | The row's own number, which the table is stored in the order of. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `ValueId` | `bigint` | no | The original the object belongs to ([`osdu.DimensionValue`](dimension-value.md)). |
| `Seq` | `int` | no | The object's place among the original's, from 1. |
| `AttributeId` | `int` | no | The field, by its number, as an attribute is named ([`osdu.DimensionAttributeName`](dimension-attribute-name.md)). |
| `Value` | `nvarchar(4000)` | yes | Collation `Latin1_General_100_BIN2`. The value read, as the field keeps it, compared exactly, at most 4,000 characters; null for an object holding none of its fields. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionElement` | `PartitionId`, `ElementValueId` | primary key | none | none | A partition's element fields together, in the order they were written. |
| `IX_DimensionElement_ElementValueId` | `ElementValueId` | unique | none | none | A row named by its number. |
| `IX_DimensionElement_PartitionId_DimensionId_ValueId_Seq_AttributeId` | `PartitionId`, `DimensionId`, `ValueId`, `Seq`, `AttributeId` | unique | none | none | A key's objects in their order, each field once: what a build matches its rows by and its table is laid out from. |

## Written and read by

- **Written** by a build, which rewrites the elements of the keys it found (`SqlServerDimensionStore.cs`).
- **Read** by the build that lays out the dimension's table.
