---
id: delivery-table-dimension-stage-text
title: "osdu.DimensionStageText: the texts a running dimension build collected"
type: table
summary: "One row per text a running dimension build collected at a collected attribute's path, with the value it is shown as and the records holding it."
keywords:
  - osdu.DimensionStageText
  - dimension staging
  - collected texts
  - collected attribute
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-flow-dimension-attributes
  - delivery-table-dimension-collected-text
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCollector.cs
---

# osdu.DimensionStageText: the texts a running dimension build collected

A text a running build of a [dimension](dimension.md) collected at a collected attribute's path, with the value it is
shown as and the records holding it, where one text may be staged in several rows, each with the records it counted,
which the write adds up. The write copies them into [`osdu.DimensionCollectedText`](dimension-collected-text.md)
([What a build holds](../flow/dimension-build.md#what-a-build-holds)). A build deletes its rows when it ends; what a
build whose worker stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The row's place in the order the build staged its rows. |
| `Name` | `nvarchar(64)` | no | Collation `Latin1_General_100_BIN2`. The collected attribute's name. |
| `Records` | `bigint` | no | The records holding the text that this row counts. |
| `Text` | `nvarchar(1024)` | no | Collation `Latin1_General_100_BIN2`. The text exactly as the index holds it, at most 1,024 characters. |
| `TextHash` | `binary(32)` | no | SHA-256 of the text's UTF-8 bytes, which the write adds the rows of one text up by. |
| `Value` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The value the text is shown as, cut to 256 characters. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageText` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's texts together: the write reads them in one statement, adding up the rows of each text. |

## Written and read by

- **Written** by a build a page at a time as it collects (`DimensionCollector.cs`, `DimensionStage.cs`).
- **Read** by the build's write (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
