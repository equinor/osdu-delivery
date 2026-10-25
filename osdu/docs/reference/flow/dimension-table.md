---
id: delivery-flow-dimension-table
title: "The dimension's table: osdu.dim_<dimension>, its columns and names, and how it follows the document"
type: flow-reference
summary: "A dimension's table, osdu.dim_<dimension>: its columns, how the key's and value's columns are named, schema evolution, and how its rows are kept."
keywords:
  - "osdu.dim_"
  - dimension table
  - column names
  - key_hash
  - schema evolution
  - IX_key
  - IX_value
  - IX_key_hash
  - clustered by partition
related:
  - delivery-flow-dimension
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

Each [dimension](dimension.md) is one table in the module's database, which its builds make and keep.

Each dimension is one table in the module's database, `osdu.dim_<dimension>` (`osdu.dim_Wellbore`): the dimension's
name with anything not a letter, digit or underscore made an underscore. It is what a report, a query, a cascade of
drop-downs or a table of facts reads. A build makes it and keeps it; no line of the document asks for it, and no
migration creates it.

| Column | Holds |
| --- | --- |
| `id` | The row's number: an identity, the clustered primary key, and what a table of facts joins on. It stays the same for as long as the dimension holds the row. |
| `partition` | The partition the row was read in. A flow building in several partitions writes them all to this table. |
| `key_id` | The key's number, the same in every row of the key. |
| the key's column | The key exactly as the index holds it. |
| `key_hash` | The key's SHA-256 (`HASHBYTES('SHA2_256', ...)`, 32 bytes), what a build finds a key's rows by through `IX_key_hash` (`partition`, `key_hash`). |
| the value's column | The key's value. |
| `records` | The records of the row: those holding the collected value, or every record of the key. |
| `filter` | The search filter finding the key's records; null when no query can carry the key. |
| one per attribute | The attribute's value for the row, under the attribute's name; null where the key has none. |
| `element` | Only for a dimension that reads elements: the element's place among the key's, from 1, in the order of the records' ids and then of the objects in each record; one column per element field follows the attributes. |

A row is a key and the value it collects: a dimension with a collected attribute has a row for each value a key holds,
any other a row a key. A key left out by cleaning, and a key no build finds any more, is no row.

**Column names.** The key's column is named after the property `path` ends with (`data.WellboreID` is `WellboreID`);
the value's after the property the last `label` path ends with (`FacilityName`), or after the dimension's own name when
it reads no label (`CurveMnemonic` beside `Mnemonic`). Where the two would be the same ignoring case (a dimension
`Source` reading `data.Source` with no label), the value's column keeps the name and the key's takes `Key` at its end:
`Source` and `SourceKey`. `columns: { key, value }` names either one; it has to where the name would be one of `id`,
`partition`, `key_id`, `records` or `filter`, an attribute's name, or no column name at all. A name is a letter, then
letters, digits and underscores, at most 64; the key's column cannot be named `value` nor the value's `key`, the words
that ask for the two columns whatever they are named.

**The schema follows the document.** SQLFlow's [schema evolution](../../../../sqlflow/docs/reference/concepts/schema-evolution.md)
brings the table to the declaration before each write: an attribute added gets its column on the next build, and
nothing is dropped or narrowed. An attribute taken out keeps its column, emptied; a column somebody added is left alone.
When the key's or value's column is named otherwise (the path or label changed, or `columns` says so), the next build
renames it where it is, keeping every row and its `id`; a query naming the old column has to change with the flow, and a
name given under `columns` stays through a change of path or label. A build writes only the rows that differ, in the
transaction that writes the rest of the dimension. When the collected attribute is added, taken out or replaced, the
partition's rows are written again under new numbers.

The table keeps a partition's rows together, and a key's rows together within it: `IX_key` (`partition`, `key_id`, `id`)
is its clustered index, and `id` its key, a nonclustered one. Every statement of a build's write seeks the partition it
writes, so builds of the dimension in two partitions at once read and lock their own partition's rows alone. The table is
read through two more indexes the build makes: `IX_value` (`partition`, the value's column, `id`), which a page reads in
value order, and `IX_key_hash` (`partition`, `key_hash`), which a view's join seeks. A table a build made before its rows
were kept by partition (clustered on `id`) is laid out once, on its next build, keeping every row and its `id`: its
secondary indexes are built again on the new order, which takes time and log space in proportion to its rows, and a build
of the dimension in another partition waits for it.
