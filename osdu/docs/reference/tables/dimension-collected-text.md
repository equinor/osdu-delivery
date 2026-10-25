---
id: delivery-table-dimension-collected-text
title: "osdu.DimensionCollectedText: each text a dimension's records hold at a collected attribute's path"
type: table
summary: "One row per collected attribute and text: the text exactly as the index holds it, the value it is shown as, and the records holding it."
keywords:
  - osdu.DimensionCollectedText
  - collected text table
  - collected attribute
  - composed search
  - dimension search
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-cli-dimensions
  - delivery-table-dimension-attribute-name
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
---

# osdu.DimensionCollectedText: each text a dimension's records hold at a collected attribute's path

One text a [dimension](dimension.md)'s records hold at a collected attribute's path, as the last build that settled the
field read it: the text exactly as the index holds it, the value it is shown as (several texts shown alike are one value),
and the records holding it. A [composed search](../cli/dimensions.md#search) picking a collected value asks for every text
shown as it ([The tables a dimension keeps](../concepts/dimension-tables.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `TextId` | `bigint` identity | no | The row's own number, which the table is stored in the order of. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `AttributeId` | `int` | no | The collected attribute, by its number ([`osdu.DimensionAttributeName`](dimension-attribute-name.md)). |
| `TextHash` | `binary(32)` | no | SHA-256 of the text's UTF-8 bytes, which the row is unique by, since a text may be longer than a key holds. |
| `Text` | `nvarchar(1024)` | no | Collation `Latin1_General_100_BIN2`. The text exactly as the index holds it, compared exactly, at most 1,024 characters. |
| `Value` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The value it is shown as, compared exactly. |
| `Records` | `bigint` | no | The records holding it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionCollectedText` | `PartitionId`, `DimensionId`, `TextId` | primary key | none | none | A dimension's collected texts together, in the order they were written: every statement over one dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it. |
| `IX_DimensionCollectedText_TextId` | `TextId` | unique | none | none | A text named by its number. |
| `IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash` | `PartitionId`, `DimensionId`, `AttributeId`, `TextHash` | unique | none | none | A text of a collected attribute, once, by its hash, since a text may be longer than a key holds. |
| `IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value` | `PartitionId`, `DimensionId`, `AttributeId`, `Value` | index | none | none | The texts of the values a search picks: one seek. |

## Written and read by

- **Written** by the build that settles the collected field (`SqlServerDimensionStore.cs`).
- **Read** by composed searches (`sqlflow dimensions search` and the dimension's search panel).
