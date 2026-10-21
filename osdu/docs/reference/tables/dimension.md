---
id: delivery-table-dimension
title: "osdu.Dimension: one dimension of a dimension flow in a partition, its declaration and what it holds now"
type: table
summary: "One row per dimension of a flow in a partition: the declaration its last build read with, the field it asked for, its dim_ table and columns, and its counts."
keywords:
  - osdu.Dimension
  - dimension table
  - dimension flow
  - dim_ table
  - members
  - originals
  - clean values
  - incremental load
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-guide-dimensions
  - delivery-table-dimension-run
  - delivery-table-dimension-member
  - delivery-table-dimension-value
  - delivery-wiki-0011-dimension-flows
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
---

# osdu.Dimension: one dimension of a dimension flow in a partition, its declaration and what it holds now

One dimension of a [dimension flow](../flow/dimension.md) in one partition: the declaration its last build read with, the
field it asked the index for, and how many members (clean values) and originals (keys) it holds now. Its members,
originals, builds and changes are rows of the other dimension tables, which name it by `DimensionId`
([The tables a dimension keeps](../concepts/dimension-tables.md)). A dimension flow keeps its ledger in the partition
(kind `dimension`).

The dimension's own table, `[osdu].[dim_<dimension>]`, is named here (`TableName`) but is no table of the model: builds
create and widen it through SQLFlow's schema evolution, and no migration touches it
([The dimension's table](../flow/dimension.md#the-dimensions-table)). Removing a dimension deletes its rows in the
partition and drops its table with the last partition writing it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DimensionId` | `int` identity | no | The dimension's id, unique on its own: every member, original, build and change of it names it so. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the dimension flow in the partition ([`osdu.Ledger`](ledger.md)). |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `Name` | `nvarchar(100)` | no | The dimension's name in its flow. |
| `Description` | `nvarchar(4000)` | yes | The dimension's description, as its document gives it. |
| `Kind` | `nvarchar(400)` | no | The kind the dimension reads, wildcards allowed per segment. |
| `Query` | `nvarchar(4000)` | yes | The query narrowing the records read, as the last build ran it; null for every record of the kind. |
| `Path` | `nvarchar(512)` | no | The path the dimension reads its keys at. |
| `FieldIndex` | `nvarchar(16)` | yes | How the index stores the field: `text`, `keyword`, `number`, `boolean` or `date`; null until a build has read the templates. |
| `NestedPath` | `nvarchar(512)` | yes | The nested array the field sits in, or null. |
| `AggregateBy` | `nvarchar(1100)` | yes | The field as the search's `aggregateBy` names it. |
| `Repeats` | `bit` | no | Whether one record can hold the field more than once, so members' counts need not be of records. |
| `CleanJson` | `nvarchar(max)` | no | The clean steps, as JSON. |
| `LabelJson` | `nvarchar(max)` | yes | Where each key's label is read, as a JSON array of paths: the record the key names, then each record a path before it refers to. Null when keys are their own values. |
| `AttributesJson` | `nvarchar(max)` | yes | The attributes each key is read with, as a JSON array of `{ "name", "steps", "collect" }`; null when the dimension reads none. |
| `ElementsJson` | `nvarchar(max)` | yes | The objects of a nested array each key's records hold, as JSON `{ "path", "fields": [...] }`: a row each in the dimension's table, their fields kept in [`osdu.DimensionElement`](dimension-element.md). Null when the dimension reads none. |
| `CollectedJson` | `nvarchar(max)` | yes | What the last build that settled the field read of each collected attribute, as a JSON array: its name and path, how the index stores its field, and the value records holding none of its values were given. |
| `DefinitionHash` | `nvarchar(16)` | no | The hash of the dimension's declaration as the last build read it. |
| `TableName` | `nvarchar(128)` | yes | The dimension's table in the schema (`dim_<dimension>`), as the last build wrote it; null before a build wrote one. |
| `KeyColumn` | `nvarchar(128)` | yes | The column of the dimension's table that holds each key, as the table has it now: named after the property the dimension's path ends with unless its document names it, and `key` in a table made before dimensions named their columns. |
| `ValueColumn` | `nvarchar(128)` | yes | The column of the dimension's table that holds each key's value, as the table has it now: named after the property the label ends with (or after the dimension, when it reads no label) unless its document names it, and `value` in a table made before dimensions named their columns. |
| `Members` | `bigint` | no | The members the dimension holds now. |
| `Originals` | `bigint` | no | The originals the dimension holds now, those under no member included. |
| `LastRunId` | `bigint` | yes | The build that last wrote or read the dimension ([`osdu.DimensionRun`](dimension-run.md)). |
| `LastBuiltUtc` | `datetime2` | yes | When that build completed. |
| `LastFullRunId` | `bigint` | yes | The last build that read the dimension in full and kept the records each key's label and attributes were read through: what an incremental load reads on from. Null until one has. |
| `LastFullBuiltUtc` | `datetime2` | yes | When `LastFullRunId` completed. |
| `CreatedUtc` | `datetime2` | no | When the dimension was first registered. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Dimension` | `PartitionId`, `DimensionId` | primary key | none | none | A partition's dimensions together. |
| `IX_Dimension_DimensionId` | `DimensionId` | unique | none | none | A dimension named by its id: every member, original, build and change of it names it so. |
| `IX_Dimension_PartitionId_FlowId_Name` | `PartitionId`, `FlowId`, `Name` | unique | none | none | A flow's dimension in a partition, by its name: what a build registers and a page finds. |
| `IX_Dimension_TableName` | `TableName` | index | none | none | The dimensions writing one table, one per partition: what a build asks before it writes and a removal before it drops. |

## Written and read by

- **Written** by a build, which registers the dimension when it starts and rewrites its declaration and counts when it has
  read everything, in one transaction under the dimension's application lock (`osdu-dimension:<partition number>:<dimension
  id>`) (`SqlServerDimensionStore.cs`), and by removing a dimension.
- **Read** by the dimension's pages, `sqlflow dimensions`, the delivery API, cache flows capturing the dimension, and the
  next build.
