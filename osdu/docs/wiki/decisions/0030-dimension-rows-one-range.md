---
id: delivery-wiki-0030-dimension-rows-one-range
title: "Decision 0030: a dimension's rows are one range of every table, and a build's statements seek them"
type: decision
summary: "Why dimension tables are keyed on partition, dimension and row, why a write's statements force seeks, and why retrying or serialising writes was rejected."
keywords:
  - "deadlock"
  - "dimension tables"
  - "clustered key"
  - "FORCESEEK"
  - "concurrent builds"
  - "recordsAtOnce"
  - "plan cache"
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261025112204_DimensionTablesKeyedByDimension.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionConcurrentWriteTests.cs
referenceRefs:
  - delivery-concept-database
  - delivery-flow-dimension-table
  - delivery-concept-dimension-tables
  - delivery-flow-dimension-build
related:
  - delivery-wiki-0011-dimension-flows
updated: 2026-10-25
---
# 0030: A dimension's rows are one range of every table, and a build's statements seek them

Status: accepted. Documented in [How the tables are keyed](../../reference/concepts/database.md#how-the-tables-are-keyed)
and on each dimension table's page.

## Context

A dimension flow builds `parallelism.recordsAtOnce` of its dimensions at once (eight unless it says otherwise), and every
build writes what it read in one transaction. A flow of sixteen dimensions building four at once, on a database whose
dimension tables were still small, failed one dimension as a deadlock victim in three builds of four. SQL Server's own
deadlock reports showed what: two writes of different dimensions, each waiting on key locks the other held in
`DimensionMember`, `DimensionAttributeName` and `DimensionValue`. The write's application lock is per dimension, so the
two never wrote the same rows; they read them.

Every table holding a dimension's rows was keyed, and so clustered, on `PartitionId` and the row's number, so the rows of
every dimension of a partition were interleaved in the order they were written. The statements of a write read their
own dimension by `PartitionId`, `DimensionId` and a column no index leading with those two held (`RemovedRunId`,
`Ordinal`, `Collected`, or most of a row's columns, which a write compares to tell a changed row). On small tables
SQL Server's plans read the partition's whole clustered index for them, so a write waited on, and locked, rows another
dimension's write had not committed. The write's three quick retries met the same writes again. The plans SQL Server had
cached for the write showed nearly every statement reading the whole partition.

## Decision

- **Every table holding a dimension's rows is keyed on `PartitionId`, `DimensionId` and the row's number**: members,
  keys, attribute names and values, elements, the records keys were read through, the records' keys, collected texts,
  builds and changes. A dimension's rows are one range of each table, in the order they were written, and no index of
  them reads a partition's rows of every dimension; a row's number keeps its own unique index for the lookups that name
  a row by it.
- **Every statement the dimension store sends names the partition and the dimension on each dimension table it reads**,
  directly rather than through a join, and reads it with `FORCESEEK`: the write and its table layout, the settling of a
  table's columns, a removal's batches and the lookups. A plan made while the tables are empty, as in a new estate's
  first build, costs a scan and a seek the same, and SQL Server picked scans; the hint makes it seek whatever the tables
  hold. The two conditions that matched a dimension's own row or the rows naming a table by one `OR` are two seeks.
- **The migration builds each index once**: a table's secondary indexes go first and come back last, on the key they end
  with, since changing a clustered key under them would rebuild each twice.
- **A test reads SQL Server's plans** for the store's statements after a dozen dimensions built at once, changed, removed
  and restored keys beside a removal, and fails naming any statement that read a dimension table past one dimension
  (`DimensionConcurrentWriteTests`).

## Rejected

- **Retrying the write more, or making writes take turns**, a partition's writes shared and a deadlocked one's retry
  alone. It would have hidden the cause: every write would still read every dimension's rows, waiting on each other's,
  and a flow's dimensions would lose writing beside each other. A deadlock that recurs is a missing index or a statement
  reading what it does not need, and both were.
- **A covering index for each statement.** The updates that tell a changed row compare most of its columns, so the
  indexes would have copied the tables, and every statement added later would need one of its own.
- **Clustering by dimension without the seeks**, the first form of this change. Builds stopped deadlocking, but the test
  still found plans made on empty tables that scanned, and a nonclustered key on the partition and the number alone
  offered a seek that reads every dimension of the partition.
- **Row locks for every statement.** A row lock where SQL Server takes a page lock would cost millions of locks on the
  first build of the largest dimensions.

## Amended: a dimension's own table keeps each partition's rows together

A dimension's own table (`osdu.dim_<dimension>`) holds every partition the dimension is built in, and SQLFlow's schema
evolution makes it clustered on its `id`, so builds of one dimension in two partitions at once, which runs of different
partitions are, read each other's rows of it as the dimensions' writes read each other's rows of the model's tables. The
build now keeps a partition's rows together and a key's within it (`IX_key`, `partition`, `key_id`, `id`, clustered; `id`
its key, nonclustered) and every statement writing a partition's rows seeks that partition. SQLFlow's evolution only
creates a table and adds or widens its columns, so the module lays the table out after it, under the table's lock, and
lays out once, on its next build, a table made before; `DimensionConcurrentWriteTests` builds the flow in two partitions
at once and checks the plans of those statements too. A removal's batches stay unhinted, since a removal can meet a table
no build has laid out since: it reads its own partition through the indexes every table has, and is run again if it
fails.

## What remains

A page holding the last rows of one dimension's range and the first of the next can carry a page lock, and a large
write's locks can escalate to its table, so two writes can still wait on each other there; the write's deadlock retry
covers that.
