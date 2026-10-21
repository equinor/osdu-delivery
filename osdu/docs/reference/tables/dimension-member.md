---
id: delivery-table-dimension-member
title: "osdu.DimensionMember: one value of a dimension, a clean value with its records and search filter"
type: table
summary: "One row per member (clean value) of a dimension: the value, the records holding any of its keys, its search filter, and the builds that found and removed it."
keywords:
  - osdu.DimensionMember
  - dimension member table
  - clean value
  - dimension values
  - search filter
  - member counts
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension
  - delivery-table-dimension-value
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
---

# osdu.DimensionMember: one value of a dimension, a clean value with its records and search filter

One member of a [dimension](dimension.md): a clean value, with how many records hold any of its originals (keys,
[`osdu.DimensionValue`](dimension-value.md)), and the search filter that finds them. A member a build no longer finds is
marked removed and kept, so its id stays its own if it comes back. A build writes a row only when something of it changed
([The tables a dimension keeps](../concepts/dimension-tables.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `MemberId` | `bigint` identity | no | The member's id, unique on its own. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `Value` | `nvarchar(256)` | no | Collation `Latin1_General_100_BIN2`. The clean value, trimmed, compared exactly, at most 256 characters. |
| `Records` | `bigint` | no | The records holding any of its originals, or the sum of its originals' counts when `RecordsExact` is clear. |
| `RecordsExact` | `bit` | no | Whether `Records` is an exact count of records rather than the sum of its originals' counts. |
| `Originals` | `int` | no | The originals under it. |
| `Unfilterable` | `int` | no | Its originals no query can carry, which the filter leaves out. |
| `Filter` | `nvarchar(max)` | yes | The search filter finding every record holding a filterable original, when it fits one query; null otherwise. |
| `FilterParts` | `int` | no | How many queries the filter takes: `1` when `Filter` holds it, more when its originals pass one query's clauses, `0` with none filterable. |
| `FirstSeenRunId` | `bigint` | no | The build that first found it ([`osdu.DimensionRun`](dimension-run.md)). |
| `FirstSeenUtc` | `datetime2` | no | When that build ran. |
| `RemovedRunId` | `bigint` | yes | The build that no longer found it, or null while it is there. |
| `RemovedUtc` | `datetime2` | yes | When that build ran. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionMember` | `PartitionId`, `MemberId` | primary key | none | none | A partition's members together. |
| `IX_DimensionMember_MemberId` | `MemberId` | unique | none | none | A member named by its id: its originals and changes name it so. |
| `IX_DimensionMember_PartitionId_DimensionId_Value` | `PartitionId`, `DimensionId`, `Value` | unique | none | none | A member is a clean value of one dimension, once: what a build matches its members by. |
| `IX_DimensionMember_PartitionId_DimensionId_RemovedRunId` | `PartitionId`, `DimensionId`, `RemovedRunId` | index | none | none | The members a dimension holds now, and those it held, each read as one range. |
| `IX_DimensionMember_PartitionId_DimensionId_Records_Value` | `PartitionId`, `DimensionId`, `Records` DESC, `Value` | index | none | none | The members most records hold first, a page at a time. |

## Written and read by

- **Written** by a build's merge, which adds, rewrites, marks removed and brings back members in one transaction under the
  dimension's lock (`SqlServerDimensionStore.cs`).
- **Read** by the dimension's values page, `sqlflow dimensions`, a composed search, a cache flow capturing the dimension,
  and the build that lays out the dimension's table.
