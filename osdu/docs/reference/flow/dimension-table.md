---
id: delivery-flow-dimension-table
title: "The dimension's table: osdu.dim_<dimension>, its columns and names, and how it follows the document"
type: flow-reference
summary: "A dimension's table, osdu.dim_<dimension>: its columns and their names, each row's InsertedDate_DW and UpdatedDate_DW, schema evolution, and how rows are kept."
keywords:
  - "osdu.dim_"
  - dimension table
  - column names
  - IX_key_text
  - schema evolution
  - IX_key
  - IX_value
  - clustered on id
  - InsertedDate_DW
  - UpdatedDate_DW
  - data freshness
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-target
  - delivery-concept-dimension-tables
  - delivery-flow-dimension-views
  - concept-schema-evolution
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionColumnNames.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261001163440_DimensionTables.cs
---

# The dimension's table: osdu.dim_<dimension>, its columns and names, and how it follows the document

Each [dimension](dimension.md) is one table, in the module's database unless its flow's [target](dimension-target.md)
names another, which its builds make and keep.

Each dimension is one table, `dim_<dimension>` (`osdu.dim_Wellbore` in the module's database, or in the schema and
database the flow's target names): the dimension's
name with anything not a letter, digit or underscore made an underscore. It is what a report, a query, a cascade of
drop-downs or a table of facts reads. A build makes it and keeps it; no line of the document asks for it, and no
migration creates it.

| Column | Holds |
| --- | --- |
| `id` | The row's number: an identity, the primary key (nonclustered, see below), and what a table of facts joins on. It stays the same for as long as the dimension holds the row. |
| `partition` | The partition the row was read in. A flow building in several partitions writes them all to this table. |
| `key_id` | The key's number, the same in every row of the key. |
| the key's column | The key exactly as the index holds it. |
| the value's column | The key's value. |
| `records` | The records of the row: those holding the collected value, or every record of the key. |
| `filter` | The search filter finding the key's records; null when no query can carry the key. |
| `InsertedDate_DW` | When a build first wrote the row, in UTC (`datetime`). It stays for as long as the row keeps its `id`. |
| `UpdatedDate_DW` | When a build last changed the row, in UTC (`datetime`): the time it was first written, or the time a later build rewrote its key, value, records, filter or an attribute. |
| one per attribute | The attribute's value for the row, under the attribute's name; null where the key has none. |
| `element` | Only for a dimension that reads elements: the element's place among the key's, from 1, in the order the key's record holds them; one column per element field follows the attributes. |

A row is a key and the value it collects: a dimension with a collected attribute has a row for each value a key holds,
any other a row a key. A key left out by cleaning, and a key no build finds any more, is no row.

**Column names.** The key's column is named after the property `path` ends with (`data.WellboreID` is `WellboreID`);
the value's after the property the last `label` path ends with (`FacilityName`), or after the dimension's own name when
it reads no label (`CurveMnemonic` beside `Mnemonic`). Where the two would be the same ignoring case (a dimension
`Source` reading `data.Source` with no label), the value's column keeps the name and the key's takes `Key` at its end:
`Source` and `SourceKey`. `columns: { key, value }` names either one; it has to where the name would be one of `id`,
`partition`, `key_id`, `records`, `filter`, `InsertedDate_DW` or `UpdatedDate_DW`, an attribute's name, or no column
name at all. A name is a letter, then
letters, digits and underscores, at most 64; the key's column cannot be named `value` nor the value's `key`, the words
that ask for the two columns whatever they are named.

**The schema follows the document.** SQLFlow's [schema evolution](../../../../sqlflow/docs/reference/concepts/schema-evolution.md)
brings the table to the declaration before each write: an attribute added gets its column on the next build, and
nothing is dropped or narrowed. An attribute taken out keeps its column, emptied; a column somebody added is left alone.
When the key's or value's column is named otherwise (the path or label changed, or `columns` says so), the next build
renames it where it is, keeping every row and its `id`; a query naming the old column has to change with the flow, and a
name given under `columns` stays through a change of path or label. A build writes only the rows that differ, in the
transaction that writes the rest of the dimension. A write lays out and compares the rows of the keys it changed alone,
the only rows it can have changed, so it takes as long as what changed rather than as the dimension (a log's curves read
again rewrite that log's rows, not the table's): an incremental load's, the keys it read again and those it removed; a
full load's, the keys that arrived, left, moved or came back and those whose own row, attribute values or objects it
wrote, which a key's hashes tell ([decision 0058](../../wiki/decisions/0058-dimension-keys-by-their-text.md)). A table
behind its dimension (the write before did not reach it, or it moved) is laid out whole. When the collected attribute is added, taken out or replaced, the
partition's rows are written again under new numbers, each first written then. A write that changes a row raises the table's version
([`osdu.Dimension`](../tables/dimension.md)'s `TableVersion`), which tells a view's check whether the rows it read changed
([A check kept](dimension-views.md)).

**When a row was written.** `InsertedDate_DW` and `UpdatedDate_DW` are SQLFlow's own audit columns, under the same names,
type and rule as on SQLFlow's ingestion tables, so a reader that knows those reads these. Every row a build adds takes the
time of the build's write in both; a row it rewrites takes it in `UpdatedDate_DW` alone; a row it finds as it was keeps
both. The time is the one the dimension records as its last build (`LastBuiltUtc` on
[`osdu.Dimension`](../tables/dimension.md)), so a row changed by the last build has that time. `UpdatedDate_DW` says how
fresh a row's data is, and a reader takes what changed since a time it read by it (`WHERE [UpdatedDate_DW] >= @since`).
A row a build deletes leaves the table and carries no time; the dimension's
[change log](dimension.md#what-a-build-keeps) says which keys left, and when. A table made before tables kept these columns is given them by its next
build, not null, every row it holds taking that build's time in both; on SQL Server's editions that add such a column
online (Enterprise, Developer, Azure SQL) that changes the table's metadata alone, not its rows.

The table keeps its rows in the order of `id`, its key and identity, the clustered index: a small key that grows as rows
are added, so every other index carries a row's eight-byte `id` alone. A partition's rows, and a key's rows within it, are
found through `IX_key` (`partition`, `key_id`, `id`, unique). Every statement of a build's write seeks the partition it
writes through it, then each row by its `id`, so builds of the dimension in two partitions at once read and lock their own
partition's rows alone. The table is read through two more indexes the build makes: `IX_value` (`partition`, the value's
column, `id`), which a page reads in value order, and `IX_key_text` (`partition`, the key's column), which a view's join
seeks; a key is at most 800 characters, which the index holds. A table made before keys were found by their text loses
its `key_hash` column and the index over it on its next build. A table a build laid out before its rows were kept by `id`
(`IX_key` its clustered index, `id` a nonclustered key) is laid out once, on its next build, keeping every row and its
`id`: its indexes are built again on the new order, which takes time and log space in proportion to its rows, and a
build of the dimension in another partition waits for it.
