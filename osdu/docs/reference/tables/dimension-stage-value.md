---
id: delivery-table-dimension-stage-value
title: "osdu.DimensionStageValue: the keys of a running dimension build, as it cleaned them"
type: table
summary: "One row per key a running dimension build cleaned: its value or why it has none, its label, count and filter. What the build's write holds."
keywords:
  - osdu.DimensionStageValue
  - dimension staging
  - cleaned keys
  - staged values
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-table-dimension-stage-key
  - delivery-table-dimension-value
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
---

# osdu.DimensionStageValue: the keys of a running dimension build, as it cleaned them

A key of a running build of a [dimension](dimension.md) as the build cleaned it: the value it belongs to, or why it
belongs to none, the label read for it, how many records hold it and the search filter finding them. These are the keys
the build's write holds: it copies them into [`osdu.DimensionValue`](dimension-value.md), adding, rewriting and removing
there ([What a build holds](../flow/dimension-build.md#what-a-build-holds)). The build lays its values out from them. A
build deletes its rows when it ends; what a build whose worker stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The key's place in the order the build read its keys ([`osdu.DimensionStageKey`](dimension-stage-key.md)): the order the keys a build finds first take their ids in. |
| `CleanValue` | `nvarchar(256)` | yes | Collation `Latin1_General_100_BIN2`. The value the key belongs to, compared exactly; null when it belongs to none. |
| `Count` | `bigint` | no | The records (or objects of a nested array) holding the key. |
| `Filter` | `nvarchar(4000)` | yes | The search filter finding the records holding the key, exactly as the index holds it; null when no query can carry it, or the filter is longer than 4,000 characters. |
| `Filterable` | `bit` | no | Whether a query can carry the key, so its value's filter finds its records. |
| `Label` | `nvarchar(1024)` | yes | The label read for the key, before cleaning, cut to 1,024 characters; null for a dimension that reads no label, or when the build could not read one. |
| `LabelFrom` | `nvarchar(1024)` | yes | The id of the record the label was read from. |
| `LeftOut` | `nvarchar(16)` | yes | Why the key belongs to no value: `empty`, `tooLong`, `dropped` or `failed`; null when it belongs to one. |
| `Note` | `nvarchar(400)` | yes | What cleaning had to say about the key, cut to 400 characters. |
| `Original` | `nvarchar(1024)` | no | Collation `Latin1_General_100_BIN2`. The key exactly as the index holds it, at most 1,024 characters. |
| `OriginalHash` | `binary(32)` | no | SHA-256 of the key's UTF-8 bytes: what the write matches it to the dimension's key by. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageValue` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's cleaned keys together: the values are laid out from them in one read, and the write copies them in one statement, both seeking one build's rows. |

## Written and read by

- **Written** by a build a batch of keys at a time as it cleans them (`DimensionRunner.cs`, `DimensionStage.cs`).
- **Read** by the build when it lays its values out, and by its write, which checks it holds every key the build staged
  before it copies them (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
