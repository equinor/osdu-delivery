---
id: delivery-wiki-0054-dimensions-keep-only-their-latest-state
title: "Decision 0054: a dimension keeps only its latest state, and its change log keeps what left"
type: decision
summary: "Why the next build deletes what a build removed, a few keys at a time before it merges, and why each change keeps its key's and values' text."
keywords:
  - dimension
  - removed keys
  - latest state
  - purge
  - change log
  - storage
  - DimensionElement
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.Purge.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Maintenance/OsduStorageContributor.cs
referenceRefs:
  - delivery-flow-dimension
  - delivery-concept-dimension-tables
  - delivery-table-dimension-change
  - delivery-concept-storage-and-retention
related:
  - delivery-wiki-0011-dimension-flows
  - delivery-wiki-0030-dimension-rows-one-range
  - delivery-wiki-0055-dimension-tables-kept-where-the-flow-says
updated: 2026-11-12
---
# 0054: A dimension keeps only its latest state, and its change log keeps what left

Status: accepted. Amends [0011](0011-dimension-flows.md), whose builds kept every key they ever found. Documented in
[What a build keeps](../../reference/flow/dimension.md#what-a-build-keeps).

## Context

Decision 0011 marked a key no build finds as removed and kept it, with its id, should it come back. Its attributes,
elements and the records it was read through stayed with it, since a build rewrites those only for the keys it found.

A production estate's catalog database (Azure SQL, 32 GB at most) reached 24.5 GB in four weeks from about 5 GB of source
data. Measured on that estate: the well log dimensions held 44,610 keys, 34,008 of them (76%) removed, as replaced well log
ids left the index; those removed keys held 5.5 million of the 7.4 million rows of `osdu.DimensionElement` (3.8 GB) and
1.15 million attribute rows. The dimension tables carried three times what they publish.

The owner said a dimension needs no versioning: keep only its latest version, and, of what changed, a summary rather than
the data.

## Decision

- **A dimension keeps what its last build found.** A key or value a build no longer finds is still marked removed by
  that build, since its write takes the key out of the dimension's table in the same transaction and the incremental
  load reads the mark; it stays listed as removed until the next build.
- **The next build's write deletes it first.** Before it merges, a write deletes every removed key with its attributes,
  elements, the records it was read through and the records that held it, then every removed value no key is under,
  25 keys a round, each round its own short transaction under the dimension's write lock (the lock a merge takes). A
  round deletes a key and everything naming it together, so a write that stops leaves nothing half deleted, and the next
  write takes up the rest. Its notes say how many keys and values it deleted.
- **The change log is the summary, and it keeps its text.** Each change keeps the key as the index held it and the values
  it left and went to, as they read when the change was made, so it still says what it was once its key is gone. The
  migration that adds those columns gave every earlier change the text of the rows it names.
- **A key that comes back is a new arrival.** It is `added`, with a new id. `restored` is told only of a key back before
  the delete, which two builds of a dimension writing one after the other can see.

## Rejected

- **Keeping removed keys for a number of builds.** The owner chose the latest state only; a count of builds would keep
  the bulk this decision removes, and the change log already says what left and when.
- **Deleting inside the merge's transaction.** The first build after the upgrade deletes tens of thousands of keys with
  millions of elements; one transaction of that size holds the dimension tables for its whole length and fills the log.
  Rounds of a few keys keep each transaction short.
- **Dropping the removed mark.** The write takes a removed key out of the dimension's table in the same transaction as the
  merge, and an incremental load reads which keys are removed; the mark is what both read, and it lasts one build.
- **Deleting a removed key's changes with it.** The change log is a dimension's history and is small (one row a change);
  only the rows a key's data took go.

## Consequences

- A key that leaves and comes back gets a new id, and so a new `key_id` in the dimension's table and in its views.
- The first build of each dimension after the upgrade deletes what earlier builds removed: on the estate above, about 34,000
  keys with 5.6 million element and 1.15 million attribute rows, in rounds, before its merge. The space goes back to the
  database for its tables to reuse; the data file keeps its size.
- `DimensionRun` keeps each build's counts, removed and restored included, and the change log keeps every change, so a
  dimension's history reads as before; only its earlier rows are gone.
