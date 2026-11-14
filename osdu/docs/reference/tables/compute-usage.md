---
id: delivery-table-compute-usage
title: "osdu.ComputeUsage: the work the nodes did, one row per run attempt or compute task, with its wall-clock time and compute units"
type: table
summary: "One row per piece of work a node did: how long it held it, the node's cores and slots, the compute units, and the team, repository and flow it is charged to."
keywords:
  - osdu.ComputeUsage
  - compute usage table
  - compute units
  - wall clock
  - node work
  - metering
  - team
related:
  - delivery-concept-compute-usage
  - delivery-concept-database
  - delivery-concept-storage-and-retention
  - delivery-table-compute-usage-day
  - delivery-table-repository-team
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageStore.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageMeter.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageRecorder.cs
---

# osdu.ComputeUsage: the work the nodes did, one row per run attempt or compute task

One row per piece of work a node did: one attempt of a platform run, or one compute task
([Compute usage](../concepts/compute-usage.md)). The node writes the row once the work has ended, whatever way it
ended, with the wall-clock time it held the work, its cores and slots at the time, the compute units that comes to,
and who the work is charged to.

Every value the compute units are worked out from is on the row, so a figure can be worked out again from the row
alone: `WallClockMs / 3,600,000 * Cores * Slots / NodeSlots`.

Nothing removes a row until an admin sets a retention for it on the Maintenance page. A cleanup then adds the row to
the total of its day, team and flow in [`osdu.ComputeUsageDay`](compute-usage-day.md) and removes it, in one
transaction, so a piece of work is in exactly one of the two tables.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `EndedUtc` | `datetime2` | no | When the work ended on its node: the first column of the key, so a period's rows sit together. The work is counted on this UTC day. |
| `UsageId` | `bigint` identity | no | The row's number. |
| `MeasurementId` | `uniqueidentifier` | no | The id the node gave the measurement when it took it, so a batch a node writes twice lands once. |
| `Node` | `nvarchar(128)` | no | The node that did the work, by the name it polls the control plane under. |
| `Kind` | `nvarchar(8)` | no | `run` for an attempt of a platform run, `task` for a compute task. |
| `WorkId` | `uniqueidentifier` | no | The platform run, or the compute task. |
| `Attempt` | `int` | no | Which attempt of the run this was; `0` for a compute task. |
| `Team` | `nvarchar(64)` | no | The team the work is charged to: the `team` of the repository document nearest the flow's file, as [`osdu.RepositoryTeam`](repository-team.md) held it when the row was written; empty when no document above the flow names one. |
| `RepoId` | `uniqueidentifier` | yes | The repository of the work's flow, when the work names one. |
| `Repo` | `nvarchar(256)` | no | The repository's name as it was when the work ran; empty for work that names no repository, and for a compute task. |
| `Project` | `nvarchar(100)` | no | The first folder of the flow's path in its repository, cut to 100 characters; empty for a flow at the repository's root and for a compute task. |
| `Flow` | `nvarchar(400)` | no | The flow's name; for a compute task, its operation. |
| `RequestedBy` | `nvarchar(256)` | yes | Who asked for the run, as the platform recorded it (a user, `schedule:<name>`, `trigger:<name>`); null when the work names nobody. |
| `StartedUtc` | `datetime2` | no | When the node was handed the work. |
| `WallClockMs` | `bigint` | no | How long the node held the work, in milliseconds, by the node's own monotonic clock. |
| `Cores` | `int` | no | The cores the node had. |
| `Slots` | `int` | no | The slots of the node the work held: one, or every run slot for a run that takes a whole node. |
| `NodeSlots` | `int` | no | The slots the node had in all: its run slots and its compute task slots. |
| `ComputeUnits` | `float` | no | The compute units the work used: its hours times the cores its slots stand for. |
| `Outcome` | `nvarchar(16)` | no | How the work ended: `completed`, `failed`, `cancelled`, or `interrupted` for work the node gave up without an outcome. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_ComputeUsage` | `EndedUtc`, `UsageId` | primary key | none | none | A period's rows as one range of the table: what every sum reads, the work list pages through, and a cleanup folds from the old end. |
| `IX_ComputeUsage_MeasurementId` | `MeasurementId` | unique | none | none | A measurement named by its id: the check that keeps a batch written twice from landing twice. |

## Written and read by

- **Written** by each node's recorder, in batches, as its work ends (`ComputeUsageStore.RecordAsync`).
- **Read** by the Compute usage page's sums and its work list (`SummaryAsync`, `WorkAsync`).
- **Removed** only by the Maintenance cleanup of `osdu.compute-work`, which folds each row into
  [`osdu.ComputeUsageDay`](compute-usage-day.md) first (`FoldAsync`).
