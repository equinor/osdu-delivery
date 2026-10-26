---
id: delivery-table-dimension-view-check
title: "osdu.DimensionViewCheck: what a build's check of a dimension view found in a partition"
type: table
summary: "One row per check of a dimension view in a partition: passed or failed, its rows, what each join found, and what each converted column could not read."
keywords:
  - osdu.DimensionViewCheck
  - dimension view check table
  - view check
  - joins
  - conversions
  - v_dim_ views
related:
  - delivery-concept-database
  - delivery-flow-dimension-views
  - delivery-table-dimension-view
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
---

# osdu.DimensionViewCheck: what a build's check of a dimension view found in a partition

What a build's check of a [view](dimension-view.md) found in one partition: the rows, what each join found, and the values
each conversion could not read, with examples; or why the view could not be read
([Dimension views](../flow/dimension-views.md)). A view keeps its 50 newest checks of each partition: a build prunes past
them.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `CheckId` | `bigint` identity | no | The check's id, unique on its own. |
| `ViewId` | `int` | no | The view checked ([`osdu.DimensionView`](dimension-view.md)). |
| `RunId` | `uniqueidentifier` | yes | The platform run whose build checked it. |
| `Status` | `nvarchar(16)` | no | `passed`, or `failed` when the view could not be read. |
| `Rows` | `bigint` | no | The rows the view holds of the partition. |
| `JoinsJson` | `nvarchar(max)` | yes | What each join found: the rows that found their row, those whose value found none, and some of those values, as JSON. |
| `ColumnsJson` | `nvarchar(max)` | yes | The values each column holds, and the values each converted column could not read with examples, as JSON. |
| `Error` | `nvarchar(4000)` | yes | Why the view could not be read, redacted. |
| `CheckedUtc` | `datetime2` | no | When the check was made. |
| `DurationMs` | `int` | no | How long the check read the view, in milliseconds. |
| `ReadVersion` | `varchar(64)` | yes | What the check read, as one text: SHA-256, hex, of the view's statement hash and the version of every dimension table it reads, and every table a view it joins reads, in the partition ([`osdu.Dimension`](dimension.md)'s `TableVersion`). Two checks of the same text read the same rows. Null for a check made before checks kept it. |
| `ReusedCheckId` | `bigint` | yes | The check whose findings this one keeps, when nothing the view reads had changed since it (the same `ReadVersion`) and the view was not read again ([A check kept](../flow/dimension-views.md)); null for a check that read the view. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionViewCheck` | `PartitionId`, `CheckId` | primary key | none | none | A partition's checks together. |
| `IX_DimensionViewCheck_CheckId` | `CheckId` | unique | none | none | A check named by its id. |
| `IX_DimensionViewCheck_ViewId_PartitionId_CheckedUtc` | `ViewId`, `PartitionId`, `CheckedUtc` | index | none | none | A view's checks of a partition, newest first: its page, and what a build prunes past the newest it keeps. |

## Written and read by

- **Written** and pruned by a build after it writes the views (`SqlServerDimensionViewStore.cs`); a check whose
  `ReadVersion` is that of the view's last passed check of the partition is written with that check's findings.
- **Read** by the dimension flow's views page, and by a build, which compares its `ReadVersion` with the last check's.
