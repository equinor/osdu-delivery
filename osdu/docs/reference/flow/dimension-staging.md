---
id: delivery-flow-dimension-staging
title: "A dimension build's staging: the tables of its own a build stages what it reads in, and when they go"
type: flow-reference
summary: "The dimstage_ heaps a dimension build stages in, loaded in bulk and dropped when it ends: their columns, when one is indexed, and a build finding them gone."
keywords:
  - dimstage_
  - dimension staging
  - staging tables
  - heap
  - bulk load
  - TABLOCK
  - dimension build memory
  - staging tables are gone
related:
  - delivery-flow-dimension-build
  - delivery-concept-database
  - delivery-concept-storage-and-retention
  - delivery-table-dimension-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261026192825_DimensionStagePerBuild.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionStageTests.cs
---

# A dimension build's staging: the tables of its own a build stages what it reads in, and when they go

A [dimension build](dimension-build.md) stages what it reads in tables of its own in the `osdu` schema, holding in
memory only the batch it is working on ([What a build holds](dimension-build.md#what-a-build-holds)). Each build makes
seven, named `[osdu].[dimstage_<dimension>_<build>_<table>]` after the dimension's id
([`osdu.Dimension`](../tables/dimension.md)), the build's id ([`osdu.DimensionRun`](../tables/dimension-run.md)) and the
table, for example `[osdu].[dimstage_12_4810_key]`. No other table of the schema starts `dimstage_`, and no migration
makes them: they are objects outside the model ([The osdu schema](../concepts/database.md#objects-outside-the-model)).

## Loaded as heaps, indexed once read in order

A build makes its tables empty, with no index, when it starts. Rows are copied in batches of at most 5,000 rows (or about
two million characters) with one bulk copy each, under a bulk update lock that the build's other copies into the same
table take beside it, so copies from several threads load one table at once and log only the pages they allocate. Two
tables are read back in the order staged, a batch at a time: `key`, to label and clean the keys, and `member`, to count
the records of the values laid out. Each is given a clustered index on `Seq` once staged whole, the first time it is
read, since a table loaded first and indexed after loads faster than one kept in order as it fills. The other five stay heaps: the write reads
each of them once, whole.

A build reads and locks its own tables alone, never another build's, so builds of different dimensions, and a build
beside one whose process stopped, stage without waiting on each other.

## The tables

Every text compared exactly has the collation `Latin1_General_100_BIN2`, and each column is the size of the dimension's
column it is written to. `Seq` is a row's place in the order the build staged its table's rows, from 0.

| Table | One row is | Columns |
| --- | --- | --- |
| `key` | A key the build read, as each range of keys completes; for an incremental load also a key it read again and found held by no record | `Seq`, `KeyHash` (SHA-256 of the key as UTF-8), `Key` (`nvarchar(1024)`, exactly as the index holds it), `Count` (the records or objects holding it), `Gone` (held by no record now: the write removes it) |
| `value` | A key as the build cleaned it | `Seq` (the key's place, which new keys take their ids in), `OriginalHash`, `Original`, `CleanValue` (null when it belongs to no value), `LeftOut` (`empty`, `tooLong`, `dropped` or `failed`), `Note`, `Label`, `LabelFrom` (the record the label was read from), `Filter`, `Count`, `Filterable` |
| `member` | A value laid out from the keys | `Seq`, `Value`, `Records`, `RecordsExact` (set once a search of its filter counted them), `Originals`, `Unfilterable`, `Filter` (`nvarchar(max)`, when it fits one query), `FilterParts` |
| `attribute` | An attribute value of a key, read through the record the key names or collected from records holding it; the write adds up the rows of one key, attribute and value | `Seq`, `KeyHash`, `Name`, `Value`, `ValueFrom` (the record read, or the collected text), `Records` (for a collected value) |
| `text` | A text collected, with the value it is shown as; the write adds up the rows of one text | `Seq`, `Name`, `TextHash`, `Text`, `Value`, `Records` |
| `element` | A field of an object of a nested array a key's records hold; an object holding none of the fields is one row with no field | `Seq`, `KeyHash`, `Record` (the record holding the object), `Ordinal` (its place among the record's objects), `Name`, `Value` (`nvarchar(4000)`) |
| `record` | A record met: `Kind` 1 a record a key's label or attributes were read through, 2 a record read by its unique key, 3 a key such a record holds | `Seq`, `Kind`, `RecordHash`, `KeyHash` (null for kind 2), `EntityType` (for kind 1) |

## When they go

A build drops its tables when it ends, written or not, letting go of the rows it still held in memory without copying
them. Dropping deallocates what a table holds rather than deleting it row by row, so ending a build of millions of rows
takes as long as ending one of ten. Removing a dimension drops every staging table of its builds.

What a build whose worker stopped left (a crash, a restart, a machine taken away) is dropped by the next build of the
same dimension, once the stopped build is closed, has run for 48 hours, or is one the ledger no longer holds. A build
drops another's tables only under the dimension's write lock, taken without waiting: while another build is writing the
dimension, what was left waits for a later build. A table of a build's own name that is already there when the build
starts (a database restored to before the build's id was taken) is dropped first, so a build never reads rows it did not
stage.

A build whose tables were dropped under it, by a later build that took it for one its process left or by the removal of
its dimension, stops at its next step, its write included, writes nothing and fails:
`The staging tables of dimension build <id> are gone: another build of the dimension dropped them, taking this build,
running for more than 48 hours, for one its process left, or the dimension was removed while it ran. Nothing was
written; build the dimension again.` A write finding fewer rows than its build staged fails the same way:
`The staging of dimension build <id> holds <n> of the <n> key(s) and <n> of the <n> value(s) it staged, so it is not what
the build read. Nothing was written; build the dimension again.`

## Rights

A worker makes, indexes, empties and drops its builds' tables with the rights it already has to make each dimension's
`dim_` table (`db_ddladmin` in a deployed estate), and copies into them with the rights it writes the ledger with. The
storage page ([Storage and retention](../concepts/storage-and-retention.md)) lists them as one area, every
`osdu.dimstage_*` table, safe to drop.
