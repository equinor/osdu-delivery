---
id: delivery-wiki-0058-dimension-keys-by-their-text
title: "Decision 0058: a dimension's keys are found by their text, and a build writes only the keys whose content changed"
type: decision
summary: "Why dimension keys are unique by their text, not a SHA-256 of it, and why a key keeps its content's hash so a build writes only what changed."
keywords:
  - dimension
  - OriginalHash
  - key_hash
  - content hash
  - ElementsHash
  - AttributesHash
  - full load
  - weekly full load
  - write steps
  - elements path id
  - spread by cost
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionWriteSteps.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionElementReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Spread.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
referenceRefs:
  - delivery-flow-dimension-build
  - delivery-flow-dimension-elements
  - delivery-flow-dimension-staging
  - delivery-concept-dimension-tables
  - delivery-table-dimension-value
related:
  - delivery-wiki-0030-dimension-rows-one-range
  - delivery-wiki-0033-dimension-builds-at-scale
  - delivery-wiki-0054-dimensions-keep-only-their-latest-state
  - delivery-wiki-0055-dimension-tables-kept-where-the-flow-says
updated: 2026-11-15
---
# 0058: A dimension's keys are found by their text, and a build writes only the keys whose content changed

Status: accepted. Documented in [The build](../../reference/flow/dimension-build.md),
[Staging](../../reference/flow/dimension-staging.md) and [Elements](../../reference/flow/dimension-elements.md).
Keeps [0030](0030-dimension-rows-one-range.md): every table is still clustered by partition, dimension and row.

## Context

A well log estate's weekly full load of 16 dimensions took 88 minutes, 81 of them one dimension: the curves of 10,602
logs, about 112,000 curves of 17 fields each, 1.9 million element rows. Reading from OSDU took minutes; the write took
most of an hour and logged nothing between "records read" and "loaded in full". A first attempt was cancelled after 21
minutes and requeued, which a node stopping under its run does; the scaler then counted busy nodes from heartbeats
alone, which could read none under running work (fixed on its own), and the build started again from zero.

A local reproduction of the dimension's shape put the full load's write at 40 seconds of statements, of which:

| Step | Share |
| --- | --- |
| Copy the staged elements, numbering each key's objects by a sort of all 2M rows on (key hash, record id, ordinal) | 48% |
| Join those rows to the keys on the key's SHA-256 to find each key's number | 19% |
| Lay out and compare the dimension's table | 18% |
| Compare the element rows with those kept (update, insert, delete) | 13% |

The write changed nothing: a full load whose records had not changed still did work proportional to the dimension. Every
row staged carried the key's SHA-256, which the merge joined on to find the key's number, because a key of up to 1,024
characters was too long for an index key.

The owner pointed out that a dimension's key is an OSDU id, unique within its kind, and that a key's unique constraint is
on the key itself; the binary key held no value.

## Decision

- **A key is unique by its text.** `DimensionValue` is unique on (partition, dimension, `Original`, `OriginalBytes`),
  in the exact collation, and a key is at most 800 characters, which a nonclustered index key of 1,700 bytes holds; an
  OSDU id is about a hundred. SQL Server compares text as though the shorter were padded with spaces, which the hash
  hid: `OriginalBytes`, the key's length computed by the database, keeps `GR` and `GR ` two keys, and every statement
  matching a key compares it. A collected text (`Text`, `TextBytes`) is unique the same way. The records a key was read
  through (`RecordId`) and the records that held it (`RecordKey`) are found by their text; an id or a unique key with
  white space at an end names no record (an OSDU id holds none) and is left out. `OriginalHash`, `RecordHash`,
  `TextHash` and the `dim_` tables' `key_hash` are gone; a view joins a table on its key's text and length, which
  `IX_key_text` finds.
- **A key's content travels with its hash.** The build stages a key's objects as one JSON text, numbered in the order its
  record holds them, and the attribute values read through its record as another, each with its SHA-256. The key keeps
  both hashes (`ElementsHash`, `AttributesHash`). The merge compares a key's staged hash with the one it kept and writes
  only the keys whose hash moved, expanding their text into rows there. A collected attribute's values are still
  compared row by row: they count records, which change without the key's record.
- **A full load lays out only the keys it changed.** The table step of a full load, as an incremental load's always did,
  lays out and compares the rows of the keys that arrived, left, moved or came back and of those whose own row, values or
  objects the merge wrote. A table behind its dimension is laid out whole, as before.
- **A dimension reading elements is keyed by id.** A key's objects are its own record's, so they are numbered while they
  are read and hashed as one. A dimension keyed by a property, or by a referenced record's id, reads that property as an
  attribute instead.
- **A write says what it is doing.** Each step (deleting what earlier builds removed, merging, writing the table) logs
  when it starts and ends with its counts and duration; a wait for another build's write lock is logged; a step still
  running after a minute, then at doubling intervals up to ten, logs what its statement is doing as the database reports
  it, a lock it waits for among it. The build's notes keep the same timings.
- **Parts are dealt by expected time.** A spread build weighs a dimension by the records it reads times the seconds a
  record took in its last full build, from the ledger, so a dimension that writes many rows a record has a part of its own.

## Rejected

- **Resolving each key's number before staging.** Staging rows under the key's id would need the keys written first and
  a lookup per row while reading; the JSON per key needs neither, and is one row however many objects the key holds.
- **Per-row content hashes.** A hash per element row keeps the 2M-row compare; a hash per key skips it.
- **Disabling lock escalation on the dimension tables.** Nothing measured showed a write waiting on another dimension's
  table lock; the trace now says when a statement waits for a lock, and the setting follows that evidence if it comes.
- **Resuming a cancelled build from its staging.** The merge's transaction rolls back whole, so a resumed build would save
  only the read, minutes of an hour; a write that changes little is the remedy.
- **Clustering the shared tables on their identity alone.** 0030 is why: a dimension's statements must seek their own rows.

## Consequences

- The migration empties `DimensionKeyRecord` and `DimensionRecord`, which held hashes alone, and every dimension's next
  build loads in full and lays its table out whole; keys over 800 characters, of which none was known, are left out.
- A weekly full load in which nothing changed writes the keys' rows it must (counts, labels) and no element or read-through
  attribute row; its table step compares no row.
- A dimension declaring `elements` on a path other than `id` is refused when its document loads.
- The staged JSON of a dimension whose keys hold many objects is held by `nvarchar(max)` rows of the staging; a key's
  objects are bounded by what a build reads (`MaxElements`).
