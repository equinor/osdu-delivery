---
id: delivery-table-source-watermark
title: "osdu.SourceWatermark: how far the last whole-scope plan of a flow scope read"
type: table
summary: "One row per flow and scope (parameter set): the upper bound of the last completed whole-scope plan, the submission that wrote it, and its render context hash."
keywords:
  - osdu.SourceWatermark
  - source watermark table
  - incremental window
  - change window
  - UpdatedThroughUtc
  - flow scope
  - render context hash
related:
  - delivery-concept-database
  - delivery-concept-submissions
  - delivery-concept-change-detection
  - delivery-table-submission
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
---

# osdu.SourceWatermark: how far the last whole-scope plan of a flow scope read

The watermark of one flow scope: the upper bound of the last whole-scope plan that completed, so the next incremental
plan reads the rows the ingestion tables changed after it ([Submissions](../concepts/submissions.md),
[Change detection](../concepts/change-detection.md)). A scope is the set of parameter values a run supplies, so a flow
run once per slice of its table keeps one watermark per slice, and a run given several values of a scope parameter one for
the set ([several values of a scope parameter](../flow/delivery.md#several-values-of-a-scope-parameter)).

Not pruned: a watermark is state. Deleting a delivery flow's ledger deletes its watermarks.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow. |
| `Scope` | `nvarchar(400)` | no | The flow scope: the parameter set the plans of this watermark ran with. |
| `UpdatedThroughUtc` | `datetime2` | no | The upper bound of the change window the last completed whole-scope plan covered. |
| `SubmissionId` | `uniqueidentifier` | no | The submission whose completion wrote the watermark ([`osdu.Submission`](submission.md)). |
| `ContextHash` | `nvarchar(64)` | yes | The render context the last plan of this scope used. The whole-run gate compares it, so a cache, mapping or schema version that moved renders the scope again even when no source row changed. |
| `RecordedUtc` | `datetime2` | no | When the watermark was written. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_SourceWatermark` | `PartitionId`, `FlowId`, `Scope` | primary key | none | none | A flow scope's one watermark, read before each incremental plan of it. |

## Written and read by

- **Written** by the ledger when a whole-scope plan completes (`OsduLedger.cs`).
- **Read** by the intake, which starts an incremental plan's window from it.
