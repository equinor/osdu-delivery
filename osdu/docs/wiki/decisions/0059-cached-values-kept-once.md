---
id: delivery-wiki-0059-cached-values-kept-once
title: "Decision 0059: a cached value a render read is kept once, and a set names it by its number"
type: decision
summary: "Why the render context keeps each cached value once and a set names it by number, and why the cache's own tables stay clustered by partition."
keywords:
  - render context
  - cache set
  - CacheSetValue
  - CacheSetEntry
  - identity clustered key
  - storage
  - application lock
  - migration
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261115192942_CacheSetValues.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260916125004_ClusterCacheByPartition.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.CacheSets.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/CacheSetKeeping.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/tests/SqlFlow.Delivery.Tests/CacheSetSweepTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/CacheSetValuesScaleTests.cs
referenceRefs:
  - delivery-concept-storage-and-retention
  - delivery-table-cache-set
  - delivery-table-cache-set-entry
  - delivery-table-cache-set-value
  - delivery-concept-database
related:
  - delivery-wiki-0053-render-context-keeps-what-records-point-at
  - delivery-wiki-0030-dimension-rows-one-range
  - delivery-wiki-0058-dimension-keys-by-their-text
updated: 2026-11-15
---
# 0059: A cached value a render read is kept once, and a set names it by its number

Status: accepted. Builds on [0053](0053-render-context-keeps-what-records-point-at.md), which removes the sets nothing
reads; this one shrinks the sets that stay. Documented in
[The render context](../../reference/concepts/storage-and-retention.md#the-render-context).

## Context

Every row of `osdu.CacheSetEntry` repeated, as text, what its value was read from (partition, cached type, record id,
path, kind), with the value's hash as 64 characters and the value's text. The table was clustered on a key of six of
those columns, up to 2,664 bytes wide, and its second index carried that key again. On the production estate of
[0053](0053-render-context-keeps-what-records-point-at.md) it held 11.6 GB in 15.3 million rows, 799 bytes a row, and
its second index 4.5 GB more.

The owner, having directed that a dimension's table be clustered on its identity
([0030](0030-dimension-rows-one-range.md), amended), asked whether the cache could take the same simplification: an
identity as the clustered key, and what a row is about held once. He added that the cache may be rebuilt and needs no
backward compatibility, but that it feeds the mapping document, so the change had to be fail-safe.

Measured on a development catalog before choosing a shape: 4,603 rows of 28 sets named 178 cached records at 370 places
(a record's path, as a kind), and held only 373 distinct values of them. The value repeats as much as the place it was
read from: a set differs from its neighbours in a few values and shares the rest, since every record that reads a unit
reads the same name of it.

## Decision

- **A value is kept once.** `osdu.CacheSetValue` holds what was read (partition, type, item, path, kind) and what it held
  (the hash of the whole value and its text), under an identity, unique on the place and the hash. A cached value that
  changes is another row; the row of what it held before stays while a set a record points at holds it.
- **A set's entry is two numbers.** `osdu.CacheSetEntry` is the set's number beside the value's, clustered on an
  identity of its own, unique on the pair, with an index by value for the impact query. A set's identity is unchanged:
  its hash is computed from the same texts as before, so no record points anywhere new.
- **Both tables are clustered on their identity**, eight bytes that only grow, which is all their other indexes carry.
  Nothing reads them a partition at a time, and every statement that writes them seeks the rows it names (`FORCESEEK`).
- **A set is written by one statement** (`SqlServerLedgerBulk.MintCacheSetAsync`): the values that are new, then the set
  and its entries in one transaction. Two application locks order the writers. `osdu.CacheSetValue.add`, taken alone
  around the insert of new values, keeps two sets that share a new value from inserting it twice or waiting on each
  other's rows in two orders. `osdu.CacheSetValue.use` is shared by sets being written and taken alone by the sweep's
  delete of unheld values, so an entry never names a value that is gone. A mint never holds both, and the sweep takes
  them in one order.
- **The sweep removes the values no set holds**, after the sets, 2,000 a round: those only the removed sets held, and
  those of a set that was never written whole. A value the sweep removed between a mint's two steps is noticed (the
  entries written come out fewer than the values handed over), nothing of the set is kept, and the mint starts again.
- **The migration carries the render context over; it does not empty it.** Delivered records point at their sets, and a
  cache change finds the records to update through them, so an emptied context would leave every earlier delivery deaf to
  cache changes without a word. The new table is built beside the one it replaces, in the migration's transaction, the
  former table read and then copied in sixteen statements each (a statement of a migration has thirty minutes), and the
  count written is checked against the count read before the former table goes. A migration that fails leaves
  everything as it was.
- **A text the database could read otherwise stops the write.** The values travel as JSON; the statement writes nothing
  unless it read as many values as it was handed, and the ledger refuses a type or partition name longer than its
  column, which the database would cut without a word.

## Measured

`CacheSetValuesScaleTests` builds a render context in the former shape, of the shape an estate's has (seven values in
ten of records every set reads, the rest of records four sets share, one record in ten changed halfway), migrates it,
and compares every row before and after. Four million rows in 12,500 sets, on a workstation:

| | Before | After |
| --- | --- | --- |
| `osdu.CacheSetEntry` | 4,000,000 rows, 2,290 MB with its index (600 bytes a row) | 4,000,000 rows, 330 MB with its indexes (86 bytes a row) |
| `osdu.CacheSetValue` | none | 300,768 rows, 153 MB with its index (532 bytes a row) |
| Together | 2,290 MB | 482 MB, 21% |

The migration took 31 seconds. What an estate saves depends on how much its sets share: a value held by one set
costs a row here and an entry, which is more than the row it had; one held by thirteen, as above, costs a thirteenth.

## Not done: the cache's own tables stay clustered by partition

The same rule was weighed for `osdu.CacheItem`, `osdu.CacheVersion` and `osdu.CacheMember` and not applied. They are
clustered by partition on purpose (migration `ClusterCacheByPartition`): clustered on an id, a statement a refresh
filters by its partition could read, and lock, every partition's rows, and two partitions refreshing at once deadlocked.
Their statements are LINQ queries, which carry no seek hint, and the snapshot a render loads pages through the clustered
key (`OsduCacheStore.ReadAsync`, `HoldersAsync`). An identity clustered key there would save nothing (most of
`CacheItem` is the captured values) and would give that deadlock back whenever a plan chose a scan, which a small table
invites. Moving them means rewriting the merge's statements with forced seeks and a test of their plans first, as the
dimension tables have; it is a change of its own.

## Rejected

- **A key table for the place, with the value left on each entry.** Built first: it halved the table, but the value's
  hash and text were then most of every row, and the measurement above shows they repeat as much as the place.
- **A hash of the place as the value's key.** A place is found by its text under the unique index, as a dimension's key
  is ([0058](0058-dimension-keys-by-their-text.md)); the value's hash is content, what tells two values of one place
  apart when they read alike to their first 400 characters.
- **A foreign key from entry to value.** The module's schema has none, and its test database is emptied table by table;
  the locks give the same guarantee without an error on either side as the usual course.
- **One exclusive lock around every mint.** Sets number about as many as records, so mints would run one at a time
  across the estate; only the insert of new values is ordered.
- **Emptying the render context in the migration.** Simpler and quick, and wrong without a sign: see above.
- **One statement for the whole copy.** A statement of a migration has thirty minutes, and the production tier ran
  dimension writes at fifty to seventy times a workstation's pace.

## Consequences

- The space the former table held goes back to the database for its tables to reuse; the data file keeps its size.
- The unique index of `osdu.CacheSetValue` can be asked for a key longer than the 1,700 bytes an index holds, which SQL
  Server says when it is made. The former table refused a key past 900 bytes, so nothing kept before is refused now.
- The sweep's result and its log line count three things: sets, entries and values.
- The migration's `Down` gives every row its texts back, in one statement.
