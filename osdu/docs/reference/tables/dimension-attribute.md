---
id: delivery-table-dimension-attribute
title: "osdu.DimensionAttribute: each value of each attribute of a dimension's keys"
type: table
summary: "One row per key, attribute and value: the value read for the key, where it was read from, and for a collected attribute how many of the key's records hold it."
keywords:
  - osdu.DimensionAttribute
  - dimension attribute table
  - attribute values
  - collected attribute
  - dimension columns
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-attribute-name
  - delivery-table-dimension-value
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
---

# osdu.DimensionAttribute: each value of each attribute of a dimension's keys

One value of one attribute of one original (a key, [`osdu.DimensionValue`](dimension-value.md)). An attribute read
through the record the key names holds one value a key, with the record it was read from; a collected attribute holds
every value the key's own records hold, each with how many of them hold it. A row is rewritten when a build reads it
otherwise. The attribute is named by its number ([`osdu.DimensionAttributeName`](dimension-attribute-name.md)), and each
attribute is a column of the dimension's table ([The tables a dimension keeps](../concepts/dimension-tables.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `AttributeValueId` | `bigint` identity | no | The row's own number, which the table is stored in the order of. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `ValueId` | `bigint` | no | The original the attribute is of. |
| `AttributeId` | `int` | no | The attribute, by its number. |
| `Value` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The value read, trimmed, compared exactly, at most 256 characters. |
| `ValueFrom` | `nvarchar(1024)` | yes | Where it was read: the id of the record the original names, or of the last one the attribute's steps reached; for a collected attribute, the text the key's records hold, as the index holds it. Null for the value a key holding none is given. |
| `Records` | `bigint` | yes | For a collected attribute, how many of the key's records hold the value; null for an attribute read through the record the key names, which is every record of the key's. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionAttribute` | `PartitionId`, `AttributeValueId` | primary key | none | none | A partition's attribute values together, in the order they were written. |
| `IX_DimensionAttribute_AttributeValueId` | `AttributeValueId` | unique | none | none | A row named by its number. |
| `IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value` | `PartitionId`, `DimensionId`, `ValueId`, `AttributeId`, `Value` | unique | none | none | A key's attributes, read with it a page at a time: one row per value, so a collected attribute holds several, and a key holds a value of an attribute once. |
| `IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value` | `PartitionId`, `DimensionId`, `AttributeId`, `Value` | index | none | none | The keys an attribute value holds (`Country` is `Norway`), and an attribute's values: one seek either way. |

## Written and read by

- **Written** by a build, which rewrites the attributes of the keys it found in its merge (`SqlServerDimensionStore.cs`).
- **Read** by the dimension's keys page, a cache flow capturing the dimension, the blueprint (the attributes' coverage),
  and the build that lays out the dimension's table.
