---
id: delivery-table-retrieval
title: "osdu.Retrieval: one run of a retrieval flow, the window it covered, where its files went and its outcome"
type: table
summary: "One row per retrieve run: the kinds and query it read, its window (the next run's start), where it wrote its files, and what it wrote."
keywords:
  - osdu.Retrieval
  - retrieval table
  - retrieval flow
  - retrieve run
  - modifytime watermark
  - JSON Lines files
  - incremental window
related:
  - delivery-concept-database
  - delivery-flow-retrieval
  - delivery-guide-retrieving-records
  - delivery-table-ledger
  - delivery-wiki-0008-retrieval-lands-raw-records
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Engine/RetrievalExecutor.cs
---

# osdu.Retrieval: one run of a retrieval flow, the window it covered, where its files went and its outcome

One run of a [retrieval flow](../flow/retrieval.md#what-a-run-records): the window it covered, where its files went, and
its outcome. A retrieve run registers the flow's ledger (kind `retrieval`) in the partition its header names, opens a row
when it starts and closes it with its counts. The watermark of an incremental retrieval lives here: `WindowTo` of the
latest `done` row is the next run's start, and a failed run does not advance it.

Not pruned by retention.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `RetrievalId` | `bigint` identity | no | The row's number, unique on its own. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the retrieval flow ([`osdu.Ledger`](ledger.md)). |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `RunId` | `uniqueidentifier` | yes | The platform run. |
| `Actor` | `nvarchar(200)` | no | Who asked for the run. |
| `Kinds` | `nvarchar(4000)` | no | The kinds the run covered, comma separated. |
| `Query` | `nvarchar(max)` | yes | The query as it ran, window included. |
| `WindowField` | `nvarchar(200)` | yes | The field the window is read on. |
| `WindowFrom` | `datetime2` | yes | Where the window started. |
| `WindowTo` | `datetime2` | yes | Where it ended; that of the latest `done` row is the next run's start. |
| `Location` | `nvarchar(2000)` | no | The run's directory. |
| `ManifestLocation` | `nvarchar(2000)` | yes | The run's manifest. |
| `Status` | `nvarchar(16)` | no | `running`, `done`, `failed` or `cancelled`. |
| `Records` | `bigint` | no | The records the run wrote. |
| `Files` | `int` | no | The files it wrote. |
| `Bytes` | `bigint` | no | The bytes it wrote, uncompressed. |
| `StartedUtc` | `datetime2` | no | When it started. |
| `CompletedUtc` | `datetime2` | yes | When it ended. The Retrievals tab's Took column is the time from `StartedUtc` to it. |
| `Error` | `nvarchar(4000)` | yes | The redacted error of a failed run. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Retrieval` | `PartitionId`, `RetrievalId` | primary key | none | none | A partition's retrievals together. |
| `IX_Retrieval_RetrievalId` | `RetrievalId` | unique | none | none | A retrieval named by its id: the run that completes it. |
| `IX_Retrieval_PartitionId_FlowId_StartedUtc` | `PartitionId`, `FlowId`, `StartedUtc` | index | none | none | A flow's retrievals, newest first. |
| `IX_Retrieval_PartitionId_FlowId_Status_StartedUtc` | `PartitionId`, `FlowId`, `Status`, `StartedUtc` | index | none | none | The watermark chain: a flow's last `done` run. |
| `IX_Retrieval_RunId` | `RunId` | index | none | none | The platform run's row. |

## Written and read by

- **Written** by the retrieve run as it opens and closes (`RetrievalExecutor.cs`, through `OsduLedger.cs`).
- **Read** by the next run of the flow (its window), the pipeline's **Retrievals** tab, and
  `GET /api/v1/delivery/flows/{pipelineId}/retrievals?max=<n>`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the identity of the
  flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
