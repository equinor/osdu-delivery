---
id: delivery-table-compute-usage-day
title: "osdu.ComputeUsageDay: the compute usage a cleanup folded, one row per day, team and flow"
type: table
summary: "One row per UTC day, team and flow: the count, wall-clock time and compute units of the work a cleanup removed from osdu.ComputeUsage."
keywords:
  - osdu.ComputeUsageDay
  - compute usage totals
  - daily totals
  - compute units
  - folded usage
related:
  - delivery-concept-compute-usage
  - delivery-concept-database
  - delivery-concept-storage-and-retention
  - delivery-table-compute-usage
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageStore.cs
---

# osdu.ComputeUsageDay: the compute usage a cleanup folded, one row per day, team and flow

The compute usage of one UTC day by one flow under one team, folded from the rows of
[`osdu.ComputeUsage`](compute-usage.md) a cleanup removed
([Compute usage](../concepts/compute-usage.md#keeping-the-totals)). A day's usage is this row plus whatever rows of
that day the work table still holds: a row of work is in exactly one of the two, since a fold removes it there and adds
it here in one transaction.

The table is empty until an admin sets how long the work stays listed piece by piece. It then grows by at most one row
per flow, team and day, and nothing removes it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `UsageDayId` | `int` identity | no | The row's number. |
| `Day` | `date` | no | The UTC day the work ended on. |
| `Team` | `nvarchar(64)` | no | The team the work was charged to; empty for work no team was named for. |
| `Repo` | `nvarchar(256)` | no | The repository's name; empty for work that named no repository. |
| `Project` | `nvarchar(100)` | no | The project folder; empty for a flow at the repository's root and for a compute task. |
| `Flow` | `nvarchar(400)` | no | The flow's name; for compute tasks, the operation. |
| `Kind` | `nvarchar(8)` | no | `run` or `task`. |
| `WorkCount` | `bigint` | no | How many pieces of work the row sums. |
| `WallClockMs` | `bigint` | no | Their wall-clock time, in milliseconds. |
| `ComputeUnits` | `float` | no | Their compute units. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_ComputeUsageDay` | `UsageDayId` | primary key | none | none | A row named by its number. |
| `IX_ComputeUsageDay_Day_Team_Repo_Project_Flow_Kind` | `Day`, `Team`, `Repo`, `Project`, `Flow`, `Kind` | unique | none | none | One row per day, team and flow, which a fold adds to; and a period's days, which every sum reads. |

## Written and read by

- **Written** only by the fold of the Maintenance cleanup of `osdu.compute-work` (`ComputeUsageStore.FoldAsync`), which
  adds to the row of a day, team and flow or makes it.
- **Read** by the Compute usage page's sums, beside the work still listed (`SummaryAsync`).
