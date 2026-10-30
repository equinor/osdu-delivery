---
id: delivery-table-ledger
title: "osdu.Ledger: the ledger directory, one row per ledger identity and the partition it belongs to"
type: table
summary: "One row per ledger: the identity every ledger row carries, the partition it belongs to, its kind, flow, interface and display name."
keywords:
  - osdu.Ledger
  - ledger directory
  - ledger table
  - FlowId
  - ledger identity
  - ledger kind
  - LedgerName
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-partitions
  - delivery-table-ledger-partition
  - delivery-table-record
  - delivery-flow-interfaces
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Directory.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
---

# osdu.Ledger: the ledger directory, one row per ledger identity and the partition it belongs to

A ledger is the rows of one flow (or one interface of a source) in one OSDU partition, under one ledger identity: the
`FlowId` every ledger row carries ([The delivery ledger](../concepts/ledger.md#ledgers-flows-and-partitions)). This table
is the directory of those identities. A run registers its ledger here, with the partition it delivers to, before it
writes a row of it; from then on the ledger belongs to that partition, and a registration that names another partition
is refused, naming both. So which partition a record, a submission or an audit entry belongs to is known from the ledger
alone, and the flow id in the delivery API's record routes is this identity, not the pipeline id, which keeps a record's
history reachable after its flow leaves the repository.

Rows are never deleted by retention. A ledger moved to the flow that replaced the one that kept it, or that took it over,
loses its row once its rows are under the new identity ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)). A row
no declared flow keeps, whose ledger holds anything, is an orphaned ledger, which the Orphaned ledgers page lists.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the ledger belongs to ([`osdu.LedgerPartition`](ledger-partition.md)), or `0` for a ledger the upgrade to partition keys could not place, which its next run adopts. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity. A flow in the single form derives it from its name, an interface of a source from `<flow>/<interface>`, either from the ledger its `ledger:` names instead, and a flow that works in partitions from that name and the partition (except in the partition marked `keepLedger`). |
| `Kind` | `nvarchar(16)` | no | The kind of flow whose ledger it is: `delivery`, `retrieval`, `assertion`, `dimension`, `inventory` or `download`. |
| `FlowName` | `nvarchar(200)` | no | The flow, or for a source with interfaces the source, the ledger belongs to. |
| `Interface` | `nvarchar(64)` | no | The interface of a source, or empty for a flow in the single form. |
| `LedgerName` | `nvarchar(200)` | no | The ledger's name as pages show it; `<ledger>@<partition>` for a partition keeping a ledger of its own. |
| `RegisteredUtc` | `datetime2` | no | When the ledger was registered, in UTC. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Ledger` | `PartitionId`, `FlowId` | primary key | none | none | A partition's ledgers together, as every ledger table keeps its rows. |
| `IX_Ledger_FlowId` | `FlowId` | unique | none | none | A ledger belongs to one partition: its identity is unique on its own, and every read of a ledger starts by looking it up here. |

## Written and read by

- **Written** by the ledger directory (`OsduLedger.Directory.cs`) when a delivery, retrieval, assertion, dimension,
  inventory or download run binds its flow and registers its ledger; by a ledger move asked by the repository sync or a takeover
  (`LedgerMoves.cs`), which registers the ledger of the flow it moves to in the old one's partition; and by the move
  (`OsduLedger.Moves.cs`), which deletes the old ledger's row.
- **Read** by the Orphaned ledgers page and a takeover (`LedgerBindings.cs`), which find the ledgers no declared flow
  keeps, by every ledger operation that resolves a ledger to its partition, by the record search across flows, by
  the pipeline and partition endpoints of the control plane, and by the intake's claim check (`SqlServerLedgerBulk.cs`),
  which names the ledger of the record that claimed an id so a held record can say how a renamed flow keeps it.
