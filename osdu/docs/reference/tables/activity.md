---
id: delivery-table-activity
title: "osdu.Activity: the audit trail, one row per run and per operator intervention"
type: table
summary: "One row per run or intervention: what it was, who asked and when, its inputs and outcome, what it concerned, a summary of its counts and the run log."
keywords:
  - osdu.Activity
  - activity table
  - audit trail
  - intervention
  - actor
  - run log
  - idle runs
  - release
  - redeliver
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-authentication-and-identity
  - delivery-table-activity-record
  - delivery-table-record
  - delivery-concept-availability-and-retention
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
---

# osdu.Activity: the audit trail, one row per run and per operator intervention

One row per run and per operator intervention, never deleted: who did what, when, with which inputs, and the outcome
([The delivery ledger](../concepts/ledger.md#osduactivity-and-osduactivityrecord-the-audit-trail)). An intervention made
for one record names it on its row; one that reaches many records names each in
[`osdu.ActivityRecord`](activity-record.md), so a record's history holds every request that reached it.

The row is never removed. Its captured run log is cleared once the activity has finished and an admin has set a limit
for the activity logs on the Maintenance page ([What the osdu schema stores](../concepts/storage-and-retention.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ActivityId` | `bigint` identity | no | The activity's id, unique on its own: what the audit trail numbers an entry by. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow the activity concerned. |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `Kind` | `nvarchar(32)` | no | What it was. Runs: `deliver`, `intake`, `drain`, `verify`, `sync`, `undo`, `reverse`, `delete-ledger` and `probe`. Interventions: `release`, `adopt`, `redeliver`, `rerender`, `delete`, `restore-previous`, `purge`, `remove-dimension`, `inventory-remove`, `inventory-repair`, `reassign-id`, `source-remove` (a removal flow's run taking out, in this delivery ledger, what the source no longer holds) and `lift-removal`; and `ledger-move`, a ledger moved to another flow's identity, by the repository sync for a renamed flow or by a person taking over an orphaned ledger ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)), `running` until the move is made. |
| `Actor` | `nvarchar(200)` | no | Who asked: for a run, who asked for it or `schedule:<name>`; `user:<name>` for an intervention from the GUI or the API, `cli:<user>@<machine>` from a workstation, `service:schedule` for a scheduled probe, `service:repository-sync` for the move of a renamed flow's ledger ([Authentication and identity](../concepts/authentication-and-identity.md)). |
| `StartedUtc` | `datetime2` | no | When it started. |
| `CompletedUtc` | `datetime2` | yes | When it ended; null while it runs. |
| `Outcome` | `nvarchar(16)` | no | `running`, `completed`, `failed` or `cancelled`. |
| `ParametersJson` | `nvarchar(max)` | yes | The inputs it was given, as JSON. |
| `SubmissionId` | `uniqueidentifier` | yes | The submission it concerned, when it concerned one ([`osdu.Submission`](submission.md)). |
| `DeliveryKey` | `uniqueidentifier` | yes | The record an intervention made for one record concerned. |
| `RunId` | `uniqueidentifier` | yes | The platform run it ran in: every operation of a run, an intervention a run's payload asked for, an inventory removal. Null for an intervention through the control plane, the GUI or the command line. A plan run records no activity. |
| `Summary` | `nvarchar(2000)` | yes | A summary of its counts that are not zero, at most 2,000 characters. |
| `Log` | `nvarchar(max)` | yes | For a run, the captured run log, at most 200,000 characters; cleared by retention once the activity has finished. |
| `Idle` | `bit` | no | Set for a run that completed having changed nothing: it planned, held, blocked and sent no record, and left none waiting. The audit trail leaves these out unless asked for them; an intervention is never idle. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Activity` | `PartitionId`, `ActivityId` | primary key | none | none | A partition's activities together, in the order the trail numbered them. |
| `IX_Activity_ActivityId` | `ActivityId` | unique | none | none | An entry named by its id: the audit trail's detail, and the run that completes it. |
| `IX_Activity_PartitionId_FlowId_StartedUtc` | `PartitionId`, `FlowId`, `StartedUtc` | index | none | none | A flow's activities, newest first. |
| `IX_Activity_PartitionId_FlowId_DeliveryKey_StartedUtc` | `PartitionId`, `FlowId`, `DeliveryKey`, `StartedUtc` | index | none | none | The activities a record's own row names, newest first. |
| `IX_Activity_SubmissionId` | `SubmissionId` | index | none | none | The activities of a submission. |
| `IX_Activity_RunId` | `RunId` | index | none | none | The activities of a platform run. |
| `IX_Activity_PartitionId_Kind_StartedUtc` | `PartitionId`, `Kind`, `StartedUtc` | index | none | none | The audit trail of a partition filtered by action, newest first. |
| `IX_Activity_PartitionId_Actor_StartedUtc` | `PartitionId`, `Actor`, `StartedUtc` | index | none | none | The audit trail of a partition filtered by actor, newest first. |
| `IX_Activity_PartitionId_StartedUtc` | `PartitionId`, `StartedUtc` | index | none | none | The audit trail of a partition, newest first. |
| `IX_Activity_PartitionId_Idle_StartedUtc` | `PartitionId`, `Idle`, `StartedUtc` | index | none | none | The trail as it opens, without the idle runs, and the count of those it leaves out, each a seek: a partition whose schedules fire every hour holds mostly idle runs. |

## Written and read by

- **Written** by the ledger when a run starts and ends and when an intervention is made (`OsduLedger.cs`), in the
  transaction of the change it records.
- **Read** by the GUI's **Audit trail** and a record's Timeline, `GET /activities` (which takes `idle=false` or
  `idle=true`), and `GET /records/{flowId}/{key}/activities` (`DeliveryEndpoints.cs`).
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the
  identity of the flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
