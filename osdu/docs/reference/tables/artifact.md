---
id: delivery-table-artifact
title: "osdu.Artifact: every object a delivery created in OSDU, or set out to create, and how its undo settled it"
type: table
summary: "One row per object a unit of work created or meant to create (a record, a dataset, a session, rows): its id or locator, version, state and undo."
keywords:
  - osdu.Artifact
  - artifact table
  - minted ids
  - unit of work
  - undo
  - intent
  - atomic delivery
  - orphans
related:
  - delivery-concept-database
  - delivery-concept-artifacts-and-undo
  - delivery-concept-removal-and-reversal
  - delivery-table-record
  - delivery-table-inventory-record
  - delivery-wiki-0012-atomic-delivery
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryArtifactEndpoints.cs
---

# osdu.Artifact: every object a delivery created in OSDU, or set out to create, and how its undo settled it

One thing a delivery created in OSDU, or set out to create: a dataset it registered, the record it wrote before a later
call, a bulk session it opened, rows it posted, a workflow run ([Artifacts and undo](../concepts/artifacts-and-undo.md#what-an-artifact-is)).
Written in the transaction that writes the step that created it, so the ledger names it before the delivery goes on; an
intent is written before a call whose id the service chooses, with what finds the object when the answer is lost, and
completed with the id. A unit of work that commits makes the ids it minted live and deletes what stood only for its
progress; one that aborts is undone, and each artifact says how: removed, restored, gone, or kept with why
([decision 0012](../../wiki/decisions/0012-atomic-delivery.md)).

An id a committed delivery minted stays here for good, whatever later happens to the record or its ledger, which is what
lets an [inventory flow](../flow/inventory.md) say which ids OSDU serves that no delivery accounts for. Never pruned.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ArtifactId` | `bigint` identity | no | The artifact's id. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the record's flow. |
| `DeliveryKey` | `uniqueidentifier` | no | The record whose delivery created it ([`osdu.Record`](record.md)). |
| `UnitId` | `uniqueidentifier` | no | The unit of work (one delivery of the record's pending work, across its tries) that created it. |
| `UnitStartedUtc` | `datetime2` | no | When that unit began: what an undo compares OSDU's creation time with before it removes a record. |
| `Slot` | `nvarchar(200)` | no | The route's name for it, unique within its unit: `record`, `dataset:0`, `content:Kr`, `session`. |
| `Role` | `nvarchar(16)` | no | What it is: `record`, `version`, `dataset`, `content`, `output`, `dataspace`, `session`, `lock`, `rows`, `points`, `objects` or `run` ([Roles](../concepts/artifacts-and-undo.md#roles)). |
| `TargetId` | `nvarchar(500)` | yes | Collation `Latin1_General_100_BIN2`. The OSDU id, or the key the target gave it; null for an intent whose call has not answered. |
| `Locator` | `nvarchar(1000)` | yes | What finds it when its id is not known, or what else names it (a file's landing-zone path, a session's record), at most 1,000 characters. |
| `Version` | `bigint` | yes | The version the unit wrote, when it wrote one. |
| `PriorVersion` | `bigint` | yes | The version the unit's write replaced, for a version of a record that existed: what an undo writes back. |
| `State` | `nvarchar(16)` | no | `intent`, `pending`, `live`, `superseded`, `due`, `removed`, `restored`, `gone`, `kept` or `failed` ([States](../concepts/artifacts-and-undo.md#states)). |
| `Note` | `nvarchar(1000)` | yes | Why it was kept, or why its undo failed, redacted, at most 1,000 characters. |
| `UndoAttempts` | `int` | no | How many undos of it were tried. |
| `NextUndoUtc` | `datetime2` | yes | When an undo that failed is tried again; null when none is due. |
| `SubmissionId` | `uniqueidentifier` | yes | The submission whose work the unit delivered. |
| `CreatedRunId` | `uniqueidentifier` | yes | The platform run the artifact was created in. |
| `CreatedUtc` | `datetime2` | no | When the row was written. |
| `UpdatedUtc` | `datetime2` | no | When the row last changed. |
| `SettledUtc` | `datetime2` | yes | When its unit's undo settled it (removed, restored, gone or kept); null while no undo has settled it. |
| `SettledRunId` | `uniqueidentifier` | yes | The platform run that settled it. |
| `SettledBy` | `nvarchar(200)` | yes | Who settled it: the worker, the sweep, or the operator whose removal undid it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Artifact` | `PartitionId`, `ArtifactId` | primary key | none | none | A partition's artifacts together, in the order they were written. |
| `IX_Artifact_PartitionId_FlowId_DeliveryKey_UnitId_Slot` | `PartitionId`, `FlowId`, `DeliveryKey`, `UnitId`, `Slot` | unique | none | none | One row per slot of a unit: an intent completed by its id, and a step reported again by a resumed try, update it. Its prefix is a record's artifacts, one seek for the record's page and for a claim. |
| `IX_Artifact_PartitionId_TargetId` | `PartitionId`, `TargetId` | index | `[TargetId] IS NOT NULL` | `State`, `Role`, `FlowId`, `DeliveryKey` | Every id a delivery minted, by OSDU id: what the inventory joins with what OSDU serves, and a lookup by id. |
| `IX_Artifact_Open` | `PartitionId`, `FlowId`, `State`, `NextUndoUtc` | index | `[State] IN (N'intent', N'pending', N'due', N'failed')` | `DeliveryKey`, `UnitId`, `UndoAttempts`, `CreatedUtc` | What is still to undo, and what an abandoned unit left: the sweep reads only these, and a flow's counts and its records with an undo to finish are read from it alone, however many an outage left open. |

## Written and read by

- **Written** by the routes' steps through the ledger, in the transaction of each step, and settled by the worker, the
  sweep or a removal's undo (`OsduLedger.Artifacts.cs`, `SqlServerLedgerBulk.Artifacts.cs`).
- **Read** by a record's **Artifacts** tab, `sqlflow records artifacts`, `GET /records/{flowId}/{key}/artifacts`
  (`DeliveryArtifactEndpoints.cs`), the sweep, and inventory reconciles (`SqlServerLedgerBulk.Inventory.cs`).
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the
  identity of the flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
