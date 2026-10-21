---
id: delivery-table-dimension-attribute-name
title: "osdu.DimensionAttributeName: each attribute of a dimension, under the number its values are joined by"
type: table
summary: "One row per attribute or element field of a dimension: its number, its name, whether it is collected, and its place among the attributes declared now."
keywords:
  - osdu.DimensionAttributeName
  - dimension attribute name table
  - attribute number
  - attribute order
  - collected attribute
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-attribute
  - delivery-table-dimension-element
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
---

# osdu.DimensionAttributeName: each attribute of a dimension, under the number its values are joined by

An attribute (or an element's field) of a [dimension](dimension.md), under the number its values are kept and joined by:
its name as the dimension declares it, whether it is collected from the dimension's own records, and its place among the
attributes the dimension declares now, which is the order of its columns as the dimension's table is read. An attribute
the dimension declares no more keeps its number, with no place ([The tables a dimension keeps](../concepts/dimension-tables.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `AttributeId` | `int` identity | no | The attribute's number, unique on its own: what [`osdu.DimensionAttribute`](dimension-attribute.md), [`osdu.DimensionCollectedText`](dimension-collected-text.md) and [`osdu.DimensionElement`](dimension-element.md) name it by. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `Name` | `nvarchar(64)` | no | Collation `Latin1_General_100_BIN2`. The attribute's name, as the dimension declares it, compared exactly. |
| `Ordinal` | `smallint` | yes | Its place among the attributes the dimension declares now, from 1; null for an attribute the dimension no longer declares. |
| `Collected` | `bit` | no | Whether its values are collected from the dimension's own records, so a key holds several. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionAttributeName` | `PartitionId`, `AttributeId` | primary key | none | none | A partition's attributes together. |
| `IX_DimensionAttributeName_AttributeId` | `AttributeId` | unique | none | none | An attribute named by its number. |
| `IX_DimensionAttributeName_PartitionId_DimensionId_Name` | `PartitionId`, `DimensionId`, `Name` | unique | none | none | A dimension's attribute by its name, once: what a build finds the number by. |

## Written and read by

- **Written** by a build, which gives each attribute it reads its number and place (`SqlServerDimensionStore.cs`).
- **Read** by the build that lays out the dimension's table, and by the pages and searches that show attributes by name.
