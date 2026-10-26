---
id: delivery-table-dimension-stage-key
title: "osdu.DimensionStageKey: the keys a running dimension build has read, in the order it read them"
type: table
summary: "One row per key a running dimension build read, with how many records hold it: what the build labels and cleans a batch at a time."
keywords:
  - osdu.DimensionStageKey
  - dimension staging
  - dimension build keys
  - staged keys
  - out of memory
  - dimension build memory
related:
  - delivery-concept-database
  - delivery-flow-dimension-build
  - delivery-table-dimension-stage-value
  - delivery-table-dimension-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
---

# osdu.DimensionStageKey: the keys a running dimension build has read, in the order it read them

A key a running build of a [dimension](dimension.md) read, staged as each range of its keys completes, with how many
records hold it. The build reads its keys back from here a batch at a time, in the order it read them, to label and clean
them ([What a build holds](../flow/dimension-build.md#what-a-build-holds)). An incremental load stages here too the keys it
read again and found held by no record, which its write removes. A build deletes its rows when it ends; what a build whose
worker stopped left, the next build of the dimension deletes.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the build runs in, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` | no | The dimension the build builds ([`osdu.Dimension`](dimension.md)). |
| `DimensionRunId` | `bigint` | no | The build that staged the row ([`osdu.DimensionRun`](dimension-run.md)). |
| `Seq` | `bigint` | no | The key's place in the order the build read its keys, from 0. A read that starts over (every value read again record by record) deletes the keys it staged, and the keys it stages after take places after theirs. |
| `Count` | `bigint` | no | The records (or objects of a nested array) holding the key. |
| `Gone` | `bit` | no | Set for a key an incremental load read again and found held by no record: its write removes it. A build reads back only the keys not set. |
| `Key` | `nvarchar(1024)` | no | Collation `Latin1_General_100_BIN2`. The key exactly as the index holds it, at most 1,024 characters. |
| `KeyHash` | `binary(32)` | no | SHA-256 of the key's UTF-8 bytes: what the build's other staging tables name the key by. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionStageKey` | `PartitionId`, `DimensionId`, `DimensionRunId`, `Seq` | primary key | none | none | A build's keys together, in the order it read them: the build reads the next batch by a seek from the last key it read, and deletes its rows a slice at a time, never touching another build's. |

## Written and read by

- **Written** by a build as each range of its keys completes, and by an incremental load for the keys it reads again
  (`DimensionRunner.cs`, `DimensionRunner.Incremental.cs`, `DimensionStage.cs`).
- **Read** by the build a batch at a time to label and clean its keys, and by its write for the keys it removes
  (`SqlServerDimensionStore.cs`).
- **Deleted** by the build when it ends, written or not, or by the next build of the dimension for a build whose worker
  stopped (`DimensionStage.cs`, `OsduLedger.Dimensions.cs`).
