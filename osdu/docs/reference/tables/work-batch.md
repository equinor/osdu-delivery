---
id: delivery-table-work-batch
title: "osdu.WorkBatch: one file of rendered documents of a submission, claimed and drained as one unit"
type: table
summary: "One row per work batch a submission's intake wrote: where its file is, how many records it holds, its status, the lease draining it, and its counts."
keywords:
  - osdu.WorkBatch
  - work batch table
  - work batch file
  - drain
  - batch claim
  - rendered documents
  - submission batches
related:
  - delivery-concept-database
  - delivery-concept-submissions
  - delivery-table-submission
  - delivery-table-lease
  - delivery-table-record
  - delivery-wiki-0006-work-batches
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
---

# osdu.WorkBatch: one file of rendered documents of a submission, claimed and drained as one unit

One row per work batch: a file of rendered documents a submission's intake wrote under its work location, claimed and
drained by a worker as one unit ([Submissions](../concepts/submissions.md)). Rendered documents live in these files, not
in the database; a record's `PendingDocumentRef` points at its document's place in one
([decision 0006](../../wiki/decisions/0006-work-batches.md)).

Not pruned by retention: an attempt names its batch. Deleting a delivery flow's ledger deletes its batches.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `SubmissionId` | `uniqueidentifier` | no | The submission whose intake wrote the batch ([`osdu.Submission`](submission.md)). |
| `Index` | `int` | no | The batch's place among the submission's batches. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow. |
| `Location` | `nvarchar(2000)` | no | Where the batch's file was written. |
| `RecordCount` | `int` | no | How many records the intake staged into the batch. |
| `Status` | `nvarchar(16)` | no | `queued`, `running`, `done` or `failed`. |
| `LeaseOwner` | `nvarchar(200)` | yes | The token of the lease a worker drains the batch under ([`osdu.Lease`](lease.md)), while it runs. |
| `RunId` | `uniqueidentifier` | yes | The platform run of the worker that claimed it. |
| `CreatedUtc` | `datetime2` | no | When the intake wrote it. |
| `StartedUtc` | `datetime2` | yes | When a worker first claimed it. |
| `CompletedUtc` | `datetime2` | yes | When its drain ended. |
| `Delivered` | `bigint` | no | Records of the batch its drain delivered. |
| `Held` | `bigint` | no | Records of the batch its drain held. |
| `Failed` | `bigint` | no | Records of the batch whose tries failed for good. |
| `Retrying` | `bigint` | no | Records of the batch whose try failed and were left due for a retry when the batch closed. |
| `Waiting` | `bigint` | no | Records of the batch its claim found waiting for a record they refer to, and did not send. |
| `Error` | `nvarchar(2000)` | yes | Why the batch failed, redacted. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_WorkBatch` | `PartitionId`, `SubmissionId`, `Index` | primary key | none | none | A batch by its partition, submission and place. |
| `IX_WorkBatch_PartitionId_FlowId_Status_CreatedUtc` | `PartitionId`, `FlowId`, `Status`, `CreatedUtc` | index | none | none | The claim: the oldest queued batch of a flow, or of a submission. |
| `IX_WorkBatch_SubmissionId_Index` | `SubmissionId`, `Index` | unique | none | `Status` | A batch named by its submission and place, and a submission's batches in order, counted by status from the index: the batch's identity is unique on its own. |

## Written and read by

- **Written** by the intake when it writes a batch file (`SubmissionIntake.cs`), and by the worker's claim and the
  lease's close, which set its status, lease and counts (`OsduLedger.Leases.cs`, `DeliveryWorker.cs`).
- **Read** by the claims of the workers draining a submission, by a submission's progress, and by the lease recovery that
  hands an abandoned batch back.
