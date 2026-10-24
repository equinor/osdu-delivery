---
id: delivery-table-removal-run
title: "osdu.RemovalRun: one run of one removal of a removal flow, what it read, found and took out"
type: table
summary: "One row per run of a removal: the removal flow and removal, the delivery ledger it wrote to, what it read and compared, found removed and took out."
keywords:
  - osdu.RemovalRun
  - removal run table
  - removal flow runs
  - keys found removed
  - guard held
  - source keys read
  - removal tallies
related:
  - delivery-concept-database
  - delivery-flow-removal
  - delivery-table-removed-key
  - delivery-table-removal-key
  - delivery-cli-removal
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022223524_RemovalFlows.cs
---

# osdu.RemovalRun: one run of one removal of a removal flow, what it read, found and took out

One run of one removal of a [removal flow](../flow/removal.md): the removal flow and removal, the delivery flow's ledger it
compared and wrote to, the platform run it ran as and who asked, what its sources held, what it found removed, and what
taking the removed keys out came to. A removal flow keeps no ledger of its own, so its runs are kept under the ledger of the
delivery flow whose records they are ([A run](../flow/removal.md#a-run)). A run is opened `running` when the removal's turn
comes, adds to its counts as it goes, and is closed `completed`, `guarded` (its [guard](../flow/removal.md#the-guard) held
what it found for an operator's approval) or `failed`. A run of the same removal still `running` when the next one opens is
closed `failed` (`the run ended without finishing: its process stopped before it did; what it recorded stands, and the next
run decides again`), and the keys it staged are deleted.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition of the delivery ledger the run wrote to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `RemovalRunId` | `bigint` identity | no | The run's number within the partition: what `sqlflow removal run <partition> <number>` and the pages name it by. |
| `FlowName` | `nvarchar(200)` | no | The removal flow, by name. |
| `Entry` | `nvarchar(300)` | no | The removal of the flow, by name. |
| `LedgerFlowId` | `uniqueidentifier` | no | The ledger identity of the delivery flow (or interface) whose records the removal compared and took out ([`osdu.Ledger`](ledger.md)). |
| `DeliveryFlow` | `nvarchar(300)` | no | The delivery flow as pages name it: its name, with its interface after a slash for a source with interfaces. |
| `RunId` | `uniqueidentifier` | yes | The platform run the removal ran as. |
| `Operation` | `nvarchar(16)` | no | `remove` or `plan`. |
| `Actor` | `nvarchar(200)` | no | Who asked for the run: an operator, a schedule, or the CLI's local account. |
| `Status` | `nvarchar(16)` | no | `running`, `completed`, `guarded` or `failed`. |
| `StartedUtc` | `datetime2` | no | When it opened. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `SourceReadUtc` | `datetime2` | yes | When the read of the source's current keys began: the moment the keys it did not find are removed as of. Null for a removal that reads only removed keys. |
| `RowsRead` | `bigint` | no | Rows its key sources returned. |
| `SourceKeys` | `bigint` | no | Distinct keys the source's current key set held. |
| `ListedKeys` | `bigint` | no | Distinct keys the source of removed keys listed. |
| `IngestionKeys` | `bigint` | no | Distinct keys the delivery flow's record table held in the removal's scope. |
| `Compared` | `bigint` | no | Distinct keys compared: the ledger's records in the scope that are not removed, and the record table's keys besides. The guard's share is of this. |
| `Detected` | `bigint` | no | Keys the run found removed that the ledger held no deletion of yet: the rows it added to [`osdu.RemovedKey`](removed-key.md) (or, for a plan, would add). |
| `AwaitingApproval` | `bigint` | no | Of them, the keys the guard held for an operator's approval. |
| `Approved` | `bigint` | no | Deletions waiting for approval that an approval this run carried out made pending. |
| `Removed` | `bigint` | no | Records taken out of OSDU (the reversible record scope) and marked removed in the ledger. |
| `AlreadyGone` | `bigint` | no | Records OSDU no longer held when the removal reached them, settled in the ledger as removals. |
| `Settled` | `bigint` | no | Records the ledger held that OSDU held nothing of for the flow: settled in the ledger, nothing asked of OSDU. |
| `KeptOut` | `bigint` | no | Keys only the record table held: kept out, so no run delivers them. |
| `Referenced` | `bigint` | no | Records left in OSDU because other records still name them. |
| `Shared` | `bigint` | no | Records left in OSDU because another ledger of the partition holds the same id live. |
| `Busy` | `bigint` | no | Records a worker or another removal held, taken by the next run. |
| `Failed` | `bigint` | no | Records OSDU refused or failed to remove, or whose reference check or undo could not be done; the next run tries again. |
| `CameBack` | `bigint` | no | Keys whose row arrived after the moment of their removal: the key came back, and nothing was found removed of it. |
| `Lifted` | `bigint` | no | Deletions lifted: the source holds the key again, or the record was delivered again since. |
| `GuardPercent` | `int` | no | The guard's share, 0 to 100, the run was held to. |
| `GuardRecords` | `bigint` | yes | The guard's count the run was held to; null for none. |
| `ApprovedBy` | `nvarchar(200)` | yes | Who approved the deletions this run made pending, for a run that carried out an approval. |
| `ActivityId` | `bigint` | yes | The activity on the delivery ledger the run's removals are recorded under ([`osdu.Activity`](activity.md), kind `source-remove`). |
| `Error` | `nvarchar(4000)` | yes | Why the run stopped, or what its guard held, redacted; null for one that completed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RemovalRun` | `PartitionId`, `RemovalRunId` | primary key | none | none | A partition's removal runs together, and one by its number. |
| `IX_RemovalRun_PartitionId_FlowName_Entry_RemovalRunId` | `PartitionId`, `FlowName`, `Entry`, `RemovalRunId` | index | none | none | A removal's runs, newest first: the removal flow's page and `sqlflow removal runs`, and a run closing the earlier ones its process left open. |
| `IX_RemovalRun_PartitionId_LedgerFlowId_RemovalRunId` | `PartitionId`, `LedgerFlowId`, `RemovalRunId` | index | none | none | A delivery ledger's removal runs, newest first, whichever removal flow ran them: the delivery flow's Deletions tab. |

## Written and read by

- **Written** by the removal runner as a removal opens, reads, compares, takes out and closes (`RemovalRunner.cs`, through
  `OsduLedger.Removal.cs` and `SqlServerLedgerBulk.Removal.cs`).
- **Read** by the Removals page and a removal flow's Removals tab, `GET /api/v1/delivery/removals/flows/{pipelineId}/runs`,
  `GET /api/v1/delivery/removals/{partition}/runs/{removalRunId}`, `GET /api/v1/delivery/removals/ledgers/{ledgerFlowId}/runs`,
  and `sqlflow removal runs` and `sqlflow removal run`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the
  identity of the flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
