---
id: delivery-table-lease
title: "osdu.Lease: a worker's hold on work, one row per claim, renewed while it delivers"
type: table
summary: "One row per worker claim on a work batch or a group of records due for a retry: its token, owner, expiry, and the tries its flow allows."
keywords:
  - osdu.Lease
  - lease table
  - worker lease
  - claim
  - lease token
  - expiry
  - lease recovery
  - removal hold
related:
  - delivery-concept-database
  - delivery-concept-submissions
  - delivery-table-record-event
  - delivery-table-work-batch
  - delivery-table-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Holds.cs
---

# osdu.Lease: a worker's hold on work, one row per claim, renewed while it delivers

One row per claim a worker makes: a work batch, or a group of records due for a retry. The worker renews this one row
while it delivers, however many records the claim holds, and the records carry its token in `LeaseOwner`
([Submissions](../concepts/submissions.md#leases-and-claims)). What the worker learns meanwhile is appended to
[`osdu.RecordEvent`](record-event.md), not written to the records. A lease that ran out belongs to nobody alive: the next
claim of its flow recovers it, applies what the worker appended, and hands the rest of the work back.

Self-clearing: closing or recovering a lease deletes it, so the table holds in-flight work only.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `Token` | `nvarchar(200)` | no | The claim's token: the worker's name and a fresh id, at most 200 characters. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow whose work the lease holds. |
| `SubmissionId` | `uniqueidentifier` | yes | The submission the claim was made for: the batch's, or the one a retry claim named. |
| `WorkBatch` | `int` | yes | The work batch the lease drains, or null for a group of records due for a retry. |
| `Owner` | `nvarchar(200)` | no | Who holds the lease: the worker that claimed it, or the one recovering it. |
| `RunId` | `uniqueidentifier` | yes | The platform run the claim was made in. |
| `AcquiredUtc` | `datetime2` | no | When the lease was claimed. |
| `ExpiresUtc` | `datetime2` | no | When the lease runs out unless its worker renews it. |
| `MaxAttempts` | `int` | no | The tries the claiming worker's flow gives a record (`reliability.retry.attempts`), so whoever recovers the lease fails a record that used them up with its worker stopping mid-attempt every time, rather than handing it back for ever; `0` for a lease claimed without one. |
| `ForRemoval` | `bit` | no | Set for a lease a removal holds records under while it takes them out of OSDU, so no worker claims one meanwhile. Nothing is sent under it: when it ends or runs out, its records are let go as they are, with no try charged and no attempt written. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Lease` | `PartitionId`, `Token` | primary key | none | none | A partition's leases together. |
| `IX_Lease_Token` | `Token` | unique | none | none | A lease named by its token: a renewal, a checkpoint, a close, a record's holder. |
| `IX_Lease_PartitionId_FlowId_ExpiresUtc` | `PartitionId`, `FlowId`, `ExpiresUtc` | index | none | none | The sweep: a flow's leases that ran out. |
| `IX_Lease_SubmissionId_ExpiresUtc` | `SubmissionId`, `ExpiresUtc` | index | none | none | A submission's leases: what its run waits on. |

## Written and read by

- **Written** by the worker's claims, renewals, checkpoints and closes, by the recovery of a lease that ran out, and by a
  removal holding records (`OsduLedger.Leases.cs`, `OsduLedger.Holds.cs`).
- **Read** by the sweep that finds leases that ran out, by a run waiting on its submission's leases, and by a record's
  page, which names the lease delivering it.
