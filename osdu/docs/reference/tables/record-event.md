---
id: delivery-table-record-event
title: "osdu.RecordEvent: what a worker appended under its lease and the lease has not applied to the record yet"
type: table
summary: "One row per step a worker completed or try it ended under a lease, with the claimed work it carried; applied to the record once, then deleted."
keywords:
  - osdu.RecordEvent
  - record event table
  - lease events
  - step
  - completion
  - lease apply
  - checkpoint
  - claimed work
related:
  - delivery-concept-database
  - delivery-concept-submissions
  - delivery-table-lease
  - delivery-table-record
  - delivery-table-attempt
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260928064320_LedgerPartitions.cs
---

# osdu.RecordEvent: what a worker appended under its lease and the lease has not applied to the record yet

What a worker appended while it delivered under a [lease](lease.md), not yet applied to the record: a step that
completed (`step`), or the outcome of a try (`completion`). Rows are only ever added. The lease applies them to their
records when the worker checkpoints or closes it, or when the lease is recovered, and deletes them in the same
transaction, so each is applied once ([Submissions](../concepts/submissions.md#leases-and-claims)). The try's permanent
history is its [`osdu.Attempt`](attempt.md), written with the completion.

A completion carries the pending work the try set out with (the `Claim*` columns), as it stood when the record was
claimed: newer work can be queued while the try is in flight, and the lease then makes current what the try actually
delivered and leaves the newer work pending.

Self-clearing: the table holds in-flight work only. Its primary key grows at its end and is set to
`OPTIMIZE_FOR_SEQUENTIAL_KEY` by migration `LedgerPartitions` where the server has it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `EventId` | `bigint` identity | no | The event's id, ever increasing within the partition. |
| `LeaseToken` | `nvarchar(200)` | no | The lease the event was appended under. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the record's flow. |
| `DeliveryKey` | `uniqueidentifier` | no | The record's delivery key. |
| `Kind` | `nvarchar(16)` | no | `step` or `completion`. |
| `AtUtc` | `datetime2` | no | When the step completed or the try ended. |
| `StepJson` | `nvarchar(max)` | yes | A step: every step of the pending work completed so far and what each returned, as a JSON object keyed by step. |
| `Status` | `nvarchar(16)` | yes | A completion: the status the try settles the record in. |
| `Promote` | `bit` | no | A completion that delivered the work it claimed, or found OSDU already holding it: the claimed state (render context, origin, hashes) becomes the record's current state. |
| `NothingSent` | `bit` | no | Nothing reached the target. With `Promote`, work the final hash check found OSDU already holding: the delivery and verification times stay as they were. Otherwise the try is handed back uncharged. |
| `Replan` | `bit` | no | A completion whose pending document could not be read: the record drops it and is asked to be planned again from its row. |
| `NextAttemptUtc` | `datetime2` | yes | A completion: when the record is next due, for a try that will be retried. |
| `Error` | `nvarchar(2000)` | yes | A completion: the redacted error of a held or failed try. |
| `ProblemHash` | `bigint` | yes | A completion that holds or fails the record: the problem its error names, which the record keeps while it is blocked. |
| `ValidationOutcome` | `nvarchar(16)` | yes | A completion of a try that checked its document: the outcome the record keeps; null leaves the record's as it was. |
| `ValidationProblems` | `bigint` | yes | How many problems that check found. |
| `ValidatedUtc` | `datetime2` | yes | When that check was made. |
| `AssertionFailures` | `bigint` | yes | How many judgements of the mapping's assertions failed on the document that check read; null when its mapping states none. |
| `TargetId` | `nvarchar(500)` | yes | The OSDU id the try delivered under. |
| `TargetVersion` | `bigint` | yes | The OSDU version the try returned. |
| `TargetStateJson` | `nvarchar(max)` | yes | The target state the record holds after the try, when the try changed it. |
| `PendingStepJson` | `nvarchar(max)` | yes | The step progress the record keeps for its next try; null clears it. |
| `ClaimSubmissionId` | `uniqueidentifier` | yes | The submission of the pending work the try carried. |
| `ClaimDocumentRef` | `nvarchar(64)` | yes | Where that work's document sits in its work batches; null when the try carried none. |
| `ClaimAttemptCount` | `int` | yes | A completion: the record's try count as the claim left it. A completion that reaches a record no lease holds is applied only while the count is still that one: a later claim, release, removal or delivery moved it, and is newer than the try. |
| `ClaimRenderContext` | `nvarchar(max)` | yes | The render context of the claimed work. |
| `ClaimSourceFingerprint` | `nvarchar(200)` | yes | The ingestion fingerprint of the claimed work. |
| `ClaimSourceModifiedUtc` | `datetime2` | yes | The business version of the claimed work. |
| `ClaimSourceFileName` | `nvarchar(800)` | yes | The ingestion file the claimed work came from. |
| `ClaimSourceRowNumber` | `bigint` | yes | The row of that file. |
| `ClaimSourceUpdatedUtc` | `datetime2` | yes | When the ingestion table last updated that row. |
| `ClaimMetadataHash` | `nvarchar(64)` | yes | The record document hash the claimed work establishes. |
| `ClaimParentVersion` | `bigint` | yes | The version of the record's parent the claimed work was rendered from; null when it read none. A completion that promotes the claim makes it the record's `ParentVersion` ([Parent records](../flow/mapping-parent.md)). |
| `ClaimPayloadHash` | `nvarchar(64)` | yes | The payload hash the claimed work establishes. |
| `ClaimPayloadModifiedUtc` | `datetime2` | yes | The payload watermark of the claimed work. |
| `ClaimMetadata` | `bit` | no | Whether the claimed work sends the record document. |
| `ClaimPayload` | `bit` | no | Whether the claimed work sends the payload. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RecordEvent` | `PartitionId`, `EventId` | primary key | none | none | Appends at the end of each partition's range: every worker only appends. Set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it. |
| `IX_RecordEvent_LeaseToken_FlowId_DeliveryKey_EventId` | `LeaseToken`, `FlowId`, `DeliveryKey`, `EventId` | index | none | none | What a lease applies: its events, record by record, the latest last. |
| `IX_RecordEvent_PartitionId_FlowId_AtUtc` | `PartitionId`, `FlowId`, `AtUtc` | index | none | `LeaseToken` | A flow's recovery: its events old enough that only a lease that is gone can have left them. |

## Written and read by

- **Written** by the worker as it delivers (`DeliveryWorker.cs`, through `OsduLedger.Leases.cs`).
- **Read and deleted** by the lease's apply on a checkpoint, a close or a recovery (`SqlServerLedgerBulk.cs`).
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of a renamed pipeline's old ledger under
  its new identity, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
