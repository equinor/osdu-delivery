---
id: delivery-table-dimension-run
title: "osdu.DimensionRun: one build of one dimension, how it read, what it found and what it changed"
type: table
summary: "One row per build of a dimension: how it loaded (full or incremental), how it read the index, what it found and how complete that is, and what it changed."
keywords:
  - osdu.DimensionRun
  - dimension run table
  - dimension build
  - full load
  - incremental load
  - aggregations
  - ranges
  - scans
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension
  - delivery-table-dimension-change
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DistinctValues.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
---

# osdu.DimensionRun: one build of one dimension, how it read, what it found and what it changed

One build of one [dimension](dimension.md): the declaration and the field it read with, how it read (aggregations, ranges,
scans), what it found and how complete that is, and what it changed. A build is registered when it starts and closed with
its counts; a failed build writes nothing but this row ([The tables a dimension keeps](../concepts/dimension-tables.md)).
How a build reads (one aggregation per range, a range answered whole or split, a range that cannot be split scanned
through the search cursor) is in [the dimension flow](../flow/dimension.md).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionRunId` | `bigint` identity | no | The build's id, unique on its own. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the dimension flow in the partition. |
| `RunId` | `uniqueidentifier` | yes | The platform run the build ran in. |
| `Actor` | `nvarchar(200)` | no | Who started it. |
| `Status` | `nvarchar(16)` | no | `running`, `completed`, `failed` or `cancelled`. |
| `Mode` | `nvarchar(16)` | no | How the build read its dimension: `full`, every key the index holds, or `incremental`, the keys of the records that changed in its window and of the records their labels and attributes were read through. |
| `WindowFrom` | `datetime2` | yes | Where an incremental load's window began; null for a full load, and for a window open below. |
| `WindowTo` | `datetime2` | yes | Up to when a completed build has read what changed (its start less the flow's lag): the next incremental load reads on from the latest of them. Null for a build that has not completed. |
| `ChangedRecords` | `bigint` | yes | The records an incremental load found changed in its window; null for a full load. |
| `TouchedKeys` | `bigint` | yes | The keys an incremental load read again; null for a full load. |
| `RecordKey` | `nvarchar(1600)` | yes | The unique key a load kept each record's keys by ([`osdu.DimensionRecord`](dimension-record.md)): the flow's `incremental.keyColumns`, comma separated. Null for a load that kept none. |
| `DefinitionHash` | `nvarchar(16)` | no | The hash of the declaration the build read with. |
| `Query` | `nvarchar(4000)` | yes | The query as the build ran it, its tokens substituted. |
| `AggregateBy` | `nvarchar(1100)` | yes | The field as the build's aggregations named it. |
| `Templates` | `nvarchar(max)` | yes | The kinds the pattern matched and the template each was read against, as JSON. |
| `Records` | `bigint` | yes | The records the query matched; null when that could not be counted. |
| `WithValue` | `bigint` | yes | The records the exact field holds a value for; null when that could not be counted. |
| `Nulls` | `bigint` | no | The records (or objects) whose value is null. |
| `TooLong` | `bigint` | yes | The records holding only text values longer than the exact field keeps; null when not counted. |
| `Unreadable` | `bigint` | no | Values the index holds that are not of the field's type. |
| `Members` | `bigint` | no | The members the dimension held when the build closed. |
| `Originals` | `bigint` | no | The originals the dimension held when the build closed. |
| `LeftOut` | `bigint` | no | Originals under no member: cleaned to nothing, to a clean value too long, dropped by a map, or on which a clean step could not run. |
| `Unfilterable` | `bigint` | no | Originals no query can carry, which their members' filters leave out. |
| `MembersAdded` | `bigint` | no | Members the build found that the dimension did not hold. |
| `MembersRemoved` | `bigint` | no | Members the build no longer found. |
| `MembersRestored` | `bigint` | no | Members a build had removed that this one found again. |
| `OriginalsAdded` | `bigint` | no | Originals the build found that the dimension did not hold. |
| `OriginalsRemoved` | `bigint` | no | Originals the build no longer found. |
| `OriginalsMoved` | `bigint` | no | Originals now under another member than before, or under none where they had one. |
| `OriginalsRestored` | `bigint` | no | Originals a build had removed that this one found again. |
| `Aggregations` | `int` | no | The aggregations the build asked. |
| `Slices` | `int` | no | The ranges an aggregation answered whole. |
| `Splits` | `int` | no | The ranges split because their aggregation was cut off. |
| `ScannedSlices` | `int` | no | The ranges read through the search cursor instead: those whose records fit a cursor page, and those that could not be split. |
| `ScanPages` | `int` | no | The cursor pages read by scanning. |
| `ScannedUnits` | `bigint` | no | The records (or objects of a nested array) read by scanning. |
| `CountQueries` | `int` | no | Counts of members' records the build asked the search for. |
| `Labelled` | `bigint` | no | Keys whose label the build read from the record they name. |
| `Unlabelled` | `bigint` | no | Keys the build could not label: a key that names no record, a record the search does not hold, or one without the path. |
| `LabelQueries` | `int` | no | Searches the build asked to read labels and attributes. |
| `Notes` | `nvarchar(max)` | yes | What the build had to say, as a JSON array of lines, at most 100. |
| `StartedUtc` | `datetime2` | no | When the build started. |
| `CompletedUtc` | `datetime2` | yes | When it ended. |
| `Error` | `nvarchar(4000)` | yes | Why it failed, redacted. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionRun` | `PartitionId`, `DimensionId`, `DimensionRunId` | primary key | none | none | A dimension's builds together, in the order they were written: every statement over one dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it. |
| `IX_DimensionRun_DimensionRunId` | `DimensionRunId` | unique | none | none | A build named by its id. |
| `IX_DimensionRun_PartitionId_DimensionId_StartedUtc` | `PartitionId`, `DimensionId`, `StartedUtc` | index | none | none | A dimension's builds, newest first. |
| `IX_DimensionRun_RunId` | `RunId` | index | none | none | The platform run's builds. |

## Written and read by

- **Written** by the dimension runner as a build opens and closes (`DimensionRunner.cs`, through
  `OsduLedger.Dimensions.cs`).
- **Read** by the dimension's builds page, `sqlflow dimensions`, the next incremental load, and the blueprint, which reads
  the last build's kinds.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the identity of the
  flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
