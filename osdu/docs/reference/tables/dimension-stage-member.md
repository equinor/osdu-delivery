---
id: delivery-table-dimension-stage-member
title: "osdu.DimensionStageMember: the values a running dimension build laid out from its keys"
type: table
summary: "One row per value a running dimension build laid out from its keys: its records, keys and filter. What the build's write holds of its values."
keywords:
  - osdu.DimensionStageMember
  - dimension staging
  - staged values
  - members laid out
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-table-dimension-stage-value
  - delivery-table-dimension-member
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
---

# osdu.DimensionStageMember: the values a running dimension build laid out from its keys

A value a running build of a [dimension](dimension.md) laid out from all its keys
([`osdu.DimensionStageValue`](dimension-stage-value.md)): how many records and keys it has, and the search filter
finding its records. The build's write copies these into [`osdu.DimensionMember`](dimension-member.md)
([What a build holds](../flow/dimension-build.md#what-a-build-holds)). An incremental load lays out only the values its
keys were and are under, each from all its keys. A build deletes its rows when it ends; what a build whose worker
stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The value's place in the order the build laid its values out: what the exact count of its records is set by. |
| `Filter` | `nvarchar(max)` | yes | The search filter finding every record holding a filterable key of the value, when it fits one query; null otherwise. |
| `FilterParts` | `int` | no | How many queries the filter takes: `1` when `Filter` holds it, more when its keys pass one query's clauses, `0` with none filterable. |
| `Originals` | `int` | no | The keys under the value. |
| `Records` | `bigint` | no | The records holding any of its keys, or the sum of its keys' counts when `RecordsExact` is clear. |
| `RecordsExact` | `bit` | no | Whether `Records` is an exact count, set once a search of the value's filter counted its records. |
| `Unfilterable` | `int` | no | Its keys no query can carry, which the filter leaves out. |
| `Value` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The value, compared exactly. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageMember` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's values together, in the order it laid them out: the build reads the values to count a batch at a time from the last it read, and the write copies them in one statement. |

## Written and read by

- **Written** by a build as it lays its values out, and set exact a batch at a time as their records are counted
  (`DimensionRunner.cs`, `DimensionStage.cs`).
- **Read** by the build for the values whose records one search counts, and by its write, which checks it holds every
  value the build staged before it copies them (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
