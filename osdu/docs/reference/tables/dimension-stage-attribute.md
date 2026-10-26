---
id: delivery-table-dimension-stage-attribute
title: "osdu.DimensionStageAttribute: the attribute values a running dimension build read for its keys"
type: table
summary: "One row per attribute value a running dimension build read for a key, through the record it names or collected from its records."
keywords:
  - osdu.DimensionStageAttribute
  - dimension staging
  - staged attributes
  - collected attribute
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-flow-dimension-attributes
  - delivery-table-dimension-attribute
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCollector.cs
---

# osdu.DimensionStageAttribute: the attribute values a running dimension build read for its keys

An attribute value a running build of a [dimension](dimension.md) read for one of its keys: through the record the key
names, or collected from the records holding the key, where one value of one key may be staged in several rows, each
with the records it counted, which the write adds up. The write copies the values of the keys it holds into
[`osdu.DimensionAttribute`](dimension-attribute.md)
([What a build holds](../flow/dimension-build.md#what-a-build-holds)). A build deletes its rows when it ends; what a
build whose worker stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The row's place in the order the build staged its rows. |
| `KeyHash` | `binary(32)` | no | SHA-256 of the key's UTF-8 bytes ([`osdu.DimensionStageKey`](dimension-stage-key.md)). |
| `Name` | `nvarchar(64)` | no | Collation `Latin1_General_100_BIN2`. The attribute's name. |
| `Records` | `bigint` | yes | For a collected attribute, the records holding the key and the value that this row counts; null for an attribute read through the record the key names. |
| `Value` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The value read, cut to 256 characters. |
| `ValueFrom` | `nvarchar(1024)` | yes | Where it was read: the id of the record the key names, or of the last one the attribute's steps reached; for a collected attribute, the text the key's records hold. Null for the value a key holding none is given. Of the rows of one key, attribute and value, the write keeps the first of these in ordinal order. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageAttribute` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's attribute values together, in the order it staged them: the write reads them in one statement, adding up the rows of each key, attribute and value. |

## Written and read by

- **Written** by a build a batch of keys at a time as it reads their labels and attributes, and a page at a time as it
  collects (`DimensionRunner.cs`, `DimensionCollector.cs`, `DimensionStage.cs`).
- **Read** by the build's write (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
