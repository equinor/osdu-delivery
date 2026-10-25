---
id: delivery-concept-dimension-tables
title: "The tables a dimension keeps: the osdu model behind a dimension flow, and its dim_ tables and v_dim_ views"
type: concept
summary: "Every osdu table of a dimension: its grain, keys and contents, what writes and reads it, and how the dim_ tables and v_dim_ views are laid out from them."
keywords:
  - dimension tables
  - DimensionMember
  - DimensionValue
  - DimensionAttribute
  - DimensionRun
  - DimensionChange
  - DimensionView
  - DimensionKeyRecord
  - "osdu.dim_"
  - "osdu.v_dim_"
related:
  - delivery-flow-dimension
  - delivery-concept-dimension-blueprint
  - delivery-cli-dimensions
  - delivery-concept-architecture
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRemoval.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260930103543_DimensionFlows.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261001163440_DimensionTables.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009204033_DimensionViews.cs
---

# The tables a dimension keeps: the osdu model behind a dimension flow, and its dim_ tables and v_dim_ views

A dimension flow's build keeps what it read in tables of the module's `osdu` schema, owned by the module's model and
created by its migrations, and from them makes the two kinds of object no migration creates: each dimension's own table,
`[osdu].[dim_<dimension>]` ([The dimension's table](../flow/dimension-table.md)), and each view, `[osdu].[v_dim_<view>]`
([Views](../flow/dimension.md#views)). The model's tables are the ledger of the dimension; the `dim_` tables and `v_dim_` views are what a
report, a pipeline or a person reads.

Every table but the views' record belongs to a ledger of kind `dimension`, one per flow and partition, and is keyed by
the ledger partition's number (`PartitionId`) and an identity of its own, which is how the others name its rows: they
are joined on numbers, never on text, with no foreign keys. Text a filter or a key compares (a value, a key, an attribute
value, a collected text) is held in a binary collation and compared exactly. The tables keep the names they were made
with: a **member** (`DimensionMember`) is a dimension's value, and an **original** (`DimensionValue`) is a key.

| Table | One row per | Unique by |
| --- | --- | --- |
| [`Dimension`](../tables/dimension.md) | dimension of a flow in a partition | `DimensionId`; `PartitionId`, `FlowId`, `Name` |
| [`DimensionRun`](../tables/dimension-run.md) | build | `DimensionRunId` |
| [`DimensionMember`](../tables/dimension-member.md) | value | `PartitionId`, `DimensionId`, `Value` |
| [`DimensionValue`](../tables/dimension-value.md) | key | `PartitionId`, `DimensionId`, `OriginalHash` (SHA-256 of the key) |
| [`DimensionAttributeName`](../tables/dimension-attribute-name.md) | attribute or element field of a dimension | `PartitionId`, `DimensionId`, `Name` |
| [`DimensionAttribute`](../tables/dimension-attribute.md) | key, attribute and value | `PartitionId`, `DimensionId`, `ValueId`, `AttributeId`, `Value` |
| [`DimensionCollectedText`](../tables/dimension-collected-text.md) | collected attribute and text | `PartitionId`, `DimensionId`, `AttributeId`, `TextHash` |
| [`DimensionElement`](../tables/dimension-element.md) | field of an object of a key's elements | `PartitionId`, `DimensionId`, `ValueId`, `Seq`, `AttributeId` |
| [`DimensionKeyRecord`](../tables/dimension-key-record.md) | key and record its label and attributes were read through | `PartitionId`, `DimensionId`, `ValueId`, `RecordHash` |
| [`DimensionRecord`](../tables/dimension-record.md) | record and key it held | `PartitionId`, `DimensionId`, `RecordHash`, `ValueId` |
| [`DimensionChange`](../tables/dimension-change.md) | change to a key | `ChangeId` |
| [`DimensionView`](../tables/dimension-view.md) | view in the database | `Name`; `ViewName` |
| [`DimensionViewCheck`](../tables/dimension-view-check.md) | check of a view in a partition | `CheckId` |

What every column holds, and what each index serves, is on each table's page.

**What writes them.** A build registers its dimension and its build when it starts, and writes the rest when it has read
everything: in one transaction, under an application lock per dimension (`osdu-dimension:<partition number>:<dimension
id>`), its keys and values are staged and merged by set-based statements that add, rewrite, mark removed and bring back,
give each attribute its number, rewrite the attributes, elements and records read through of the keys it found, log every
change, and bring the dimension's own table to the same rows, then close the build with its counts. The tables keep each
dimension's rows together ([How the tables are keyed](database.md#how-the-tables-are-keyed)), so every statement of the
write reads and locks its own dimension's rows alone, and a build of another dimension writes beside it. Nothing is deleted:
a value or key no build finds is marked removed and keeps its number should it come back. A failed build writes nothing
but its build row. The views and their checks are written after the dimensions, each view in a transaction of its own.
[Removing a dimension](../flow/dimension.md#removing-a-dimension) deletes every row of it in its partition, in batches under the same lock,
and drops its table with the last partition writing it; removing a view deletes its record and its checks.

**What reads them.** The dimension's pages, [`sqlflow dimensions`](../cli/dimensions.md) and the API read the values,
keys, attributes, changes and builds; a [composed search](../cli/dimensions.md#search) reads values, keys and collected
texts; a [cache flow](../flow/cache.md) capturing a dimension reads its values, keys and attributes; an incremental load reads its builds,
records read through and records; [the blueprint](dimension-blueprint.md) reads the last build's kinds and the attributes' coverage.

**How the `dim_` table is laid out from them.** A row of `[osdu].[dim_<dimension>]` is a key the dimension holds now
(`DimensionValue`, not removed) under its value (`DimensionMember`): `key_id` is the key's `ValueId`, the key's column
its text, the value's column its value's text, `records` its records (or the collected value's, or 1 for an element),
`filter` its filter, and each attribute's column the value of the attribute in that place (`DimensionAttributeName`
ordering `DimensionAttribute`). A collected attribute makes a row for each value of the key, and elements a row for each
object (`DimensionElement`), numbered in `element`. A build writes only the rows that differ, so a row keeps its `id`.
A view reads only `dim_` tables, of its own flow; `DimensionView` records each view a build made, so a build writes over
and drops only views it recorded.
