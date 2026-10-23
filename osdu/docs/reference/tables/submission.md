---
id: delivery-table-submission
title: "osdu.Submission: one plan of a delivery flow over its ingestion tables, with its window and counts"
type: table
summary: "One row per plan of a flow: what it read (window, full read or keys), what rendered it, its work batches, status and record counts."
keywords:
  - osdu.Submission
  - submission table
  - plan
  - change window
  - work batches
  - submission status
  - submission counts
related:
  - delivery-concept-database
  - delivery-concept-submissions
  - delivery-concept-change-detection
  - delivery-table-work-batch
  - delivery-table-record
  - delivery-table-source-watermark
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
---

# osdu.Submission: one plan of a delivery flow over its ingestion tables, with its window and counts

One row per plan of a delivery flow: an incremental window of changed rows, a full read, or a set of record keys. Its id
is the idempotency key: a run that names a submission works on exactly the window or keys it recorded, and a run the
platform executes again after an interruption resumes the submission its interrupted attempt left unsettled. Every
record a plan staged points back at it through `LastSubmissionId` ([Submissions](../concepts/submissions.md#the-submission)).

Not pruned by retention: a record names its submission. Deleting a delivery flow's ledger deletes its submissions.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `SubmissionId` | `uniqueidentifier` | no | The submission's id, unique on its own. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow (or interface) that planned it ([`osdu.Ledger`](ledger.md)). |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `MappingReference` | `nvarchar(200)` | no | The mapping that rendered the plan, as `<name>@<version>`. |
| `RenderContext` | `nvarchar(max)` | no | The render context, as canonical JSON: the mapping reference and fingerprint, the template version, the parameter values, the cache partition and version, and for a mapping that searches the system properties its searches were written under. |
| `ParametersJson` | `nvarchar(max)` | no | The parameter values the run supplied, as a JSON object: each a string, or an array of strings for a parameter given several values. A removal reads it to find the records planned under its scope. |
| `RecordCount` | `bigint` | no | How many candidate records the plan estimated when it opened the source. |
| `WorkLocation` | `nvarchar(2000)` | yes | The work location the intake wrote its batches under; null for a plan that wrote none. |
| `BatchCount` | `int` | no | How many work batches the intake wrote ([`osdu.WorkBatch`](work-batch.md)). |
| `Slices` | `int` | no | How many key slices the intake was cut into for its fan-out; `1` for a plan that ran on one node. |
| `Status` | `nvarchar(16)` | no | `received` while it plans; `planned` once the planning finished with records to send; `running` while records of it are still pending when its drain ends; `completed` or `failed` when it closes ([Submissions](../concepts/submissions.md#the-submission)). |
| `ReceivedUtc` | `datetime2` | no | When the submission was registered, in UTC. |
| `StartedUtc` | `datetime2` | yes | When its planning first started; a resumed plan keeps it. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `Planned` | `bigint` | no | Records the latest planning pass queued to send. |
| `SkippedUnchanged` | `bigint` | no | Records the latest planning pass found rendering as OSDU already holds them. |
| `AwaitingApproval` | `bigint` | no | Records a cache change waiting for approval held back: rendered and ready, not sent until the change is decided. |
| `SkippedStale` | `bigint` | no | Records the source carried in a version older than the one delivered or queued: skipped, never sent. |
| `UnchangedAtPush` | `bigint` | no | Records whose queued document was, when its turn came, what OSDU already held; nothing was sent. Counted across every pass. |
| `Blocked` | `bigint` | no | Records held, failed, deleted or reverted earlier whose source has not changed: the latest planning pass left them, since they need a release. |
| `Delivered` | `bigint` | no | Records its attempts delivered, across every pass. |
| `Held` | `bigint` | no | Records the latest planning pass held; when the submission closes, the records whose last submission it is that are held now. |
| `Failed` | `bigint` | no | When it closes: the records whose last submission it is that have failed. |
| `Waiting` | `bigint` | no | When it closes: the records of it still waiting for a record they refer to that has not landed. |
| `Untracked` | `bigint` | no | Rows with no derivable delivery key: not planned, not delivered. |
| `Error` | `nvarchar(4000)` | yes | Why it failed, redacted; a run stopped part way records `stopped: ...`. |
| `Kind` | `nvarchar(16)` | no | `incremental` (a window of changed rows), `full` (every row up to a bound) or `keys` (named record keys). |
| `SourceConnection` | `nvarchar(400)` | no | The source connection reference exactly as the flow declares it, never a resolved value. |
| `SourceObject` | `nvarchar(400)` | no | The record table's three-part name. |
| `WindowFromUtc` | `datetime2` | yes | The lower bound (exclusive) of the change window planned; null for a plan without a window. |
| `WindowToUtc` | `datetime2` | yes | The upper bound (inclusive) of the change window planned; set for incremental and full plans. |
| `SourceWindowJson` | `nvarchar(max)` | yes | What else bounded the read, as JSON: dataset objects, overlap, scope values, slice boundaries, the key count and, for a keys plan, the digest of the keys. |
| `RunId` | `uniqueidentifier` | yes | The platform run that coordinated the plan. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Submission` | `PartitionId`, `SubmissionId` | primary key | none | none | A partition's submissions together, as every ledger table keeps its rows. |
| `IX_Submission_RunId` | `RunId` | index | none | none | A run page linking the run to the plan it coordinated. |
| `IX_Submission_SubmissionId` | `SubmissionId` | unique | none | none | A submission named by its id alone: a run's payload, a link, a record's last submission. |
| `IX_Submission_PartitionId_ReceivedUtc` | `PartitionId`, `ReceivedUtc` | index | none | none | The submissions of a partition, most recent first, across its flows. |
| `IX_Submission_PartitionId_FlowId_ReceivedUtc` | `PartitionId`, `FlowId`, `ReceivedUtc` | index | none | none | A flow's submissions, most recent first. |
| `IX_Submission_PartitionId_FlowId_Status` | `PartitionId`, `FlowId`, `Status` | index | none | none | A flow's submissions in one status, such as the unsettled ones a deliver run takes over. |
| `IX_Submission_PartitionId_FlowId_Kind_ReceivedUtc` | `PartitionId`, `FlowId`, `Kind`, `ReceivedUtc` | index | none | none | A flow's submission listing filtered by kind. |

## Written and read by

- **Written** by the intake (`SubmissionIntake.cs`) through the ledger when a deliver or intake run plans, updated as
  the planning passes run, and closed from the ledger's records when the drain ends.
- **Read** by the drain and every later deliver run of the flow (to resume or take over a submission), by a flow's
  Submissions tab, the run page and the delivery API's submission routes.
