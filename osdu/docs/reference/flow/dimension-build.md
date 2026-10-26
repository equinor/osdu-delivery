---
id: delivery-flow-dimension-build
title: "Dimension builds: the operations a run takes, what a build does, and what a run returns"
type: flow-reference
summary: "A dimension flow's runs: build and plan, the payload, what a build does step by step, and the result each run returns."
keywords:
  - build
  - plan
  - run payload
  - what a build does
  - aggregationsize
  - maxvalues
  - run.json
  - dimension build memory
  - out of memory
  - staging
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-incremental
  - delivery-flow-dimension-errors
  - delivery-cli-dimensions
  - delivery-flow-dimension-staging
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/DimensionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DistinctValues.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionFilters.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
---

# Dimension builds: the operations a run takes, what a build does, and what a run returns

A [dimension flow](dimension.md) is run like any other flow: to build its dimensions, or to plan a build.

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Loads each dimension, in full or incrementally as the flow's `incremental` block says ([Full and incremental loads](dimension-incremental.md)): reads its keys, their labels and attributes, cleans keys into values, and keeps what changed. |
| `plan` | Settles each dimension's field from the templates, counts the records it would read, says how a build would load it (and, loading incrementally, the records that changed in its window), and gives each view's statement and what would stop a build writing it; reads no value and keeps nothing. |

The run payload takes one field, `dimensions`, the names of the dimensions to build; a run naming none builds every
one. A name the flow does not declare fails the run, listing the flow's dimensions. Anything else in the payload is
refused before the run starts, for example
`payload tests does not apply to a dimension flow: only an assertion flow's runs select tests; a dimension flow's payload names only dimensions.`
SQLFlow's `fullLoad` (`--full`) makes a build load every dimension in full, and its backfill window (`--from`, `--to`)
names the window a flow with an `incremental` block reads; its other flags (`--files`, `--filter`, ...) are refused.

```bash
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --payload '{"dimensions":["Wellbore"]}'
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --operation plan
```

Both operations read the templates from the template folders on the flow's path, and a build keeps the version of each
it read. See [running an OSDU flow](../cli/run.md) for the run options.

## What a build does

For each dimension, in the run's partition:

1. **Kinds and the field.** For a `data` path, one aggregation by `kind` lists the concrete kinds the pattern and query
   match, and the template of each, the one version the nearest template folder holding that kind holds, says how the
   index stores the path. A kind no folder on the flow's path holds fails the dimension. A partition holding no record of
   the kind leaves the dimension with no value and says so.
2. **Every distinct key.** The search aggregates the field over the whole query. A range answered with fewer groups
   than `aggregationSize` is complete; one answered with that many is split at a key it returned and each half is asked
   again, until every range answers whole. A range that cannot be split is read record by record through the search
   cursor. Ranges are asked `recordsAtOnce` at a time, and each range's keys are staged as it completes. A text value
   over 256 characters is not in the `keyword` field and is counted, never a key; a key over 1,024 characters is counted
   and left out.
3. **Labels, attributes and collected values**, read side by side when the dimension has both.
4. **Values and filters.** Each key is cleaned into its value. A key's filter is written the way the index holds the
   field; a value's filter holds at most 500 keys a query, and a value with more has its filter in parts. A key no query
   can carry (a control character, `nested(`, or inside a nested array a value the service rewrites) stays under its
   value, marked unfilterable.
5. **The write**, in one transaction under a lock per dimension: the build's staging is copied where the database holds
   it, then new keys and values are added, changed ones rewritten, what the build no longer found marked removed (keeping
   its id should a later build find it again), every key that arrived, left, moved or came back logged, and the
   dimension's table brought to the same rows: every row by a full load, the rows of the keys it read again by an
   incremental one ([The dimension's table](dimension-table.md)). A full load's write grows with the dimension (one of
   well log curves stages a row for every field of every curve), an incremental load's with what it read again; neither
   has a time limit, so each takes as long as its rows do, and cancelling the run stops it with nothing written.
   Writes of different dimensions run side by side: each locks only its own rows, every table holding a dimension's rows
   keeping them together and taking no page lock, so neither waits for the other. A statement writing 5,000 rows or
   more of one table (a large dimension's first load) can have SQL Server lock that table whole until its build commits,
   which makes the writes of other dimensions to it wait meanwhile.

Dimensions build `recordsAtOnce` at a time, and the build asks the search at most `recordsAtOnce` × `recordsAtOnce`
requests at once, its dimensions together: each of a dimension's reads (value ranges, cursors, counts) asks up to
`recordsAtOnce` at a time, and its labels and attributes as many as the build has free, so the last dimension building
asks as much as the whole build may rather than its own share. A dimension that fails keeps what the build before it
wrote, and the run ends failed with every other dimension built. A second build of the same dimension waits for the first's lock and fails after
two minutes: `Another build of dimension <id> held its write lock for more than 120 seconds (sp_getapplock answered ...),
so this build wrote nothing. Build it again when the other has finished.`

A build reads the index as it stands: a record written seconds before may not be in it yet. When the counts read do not
add up to the records holding a value, the build keeps what it read and says so in its notes.

## What a build holds

A build stages what it reads in the module's database as it reads it, and holds in memory only the batch it is working
on, so the memory a build takes does not grow with its dimension: one of 60,000 wellbores with nine attributes holds
about as much as one of 20,000.

- **Keys** are staged as each range completes and read back in the order they were read, a batch at a time, to be
  labelled and cleaned. A batch holds at most 50,000 references between its keys from one step of a label or an
  attribute to the next ([Reading through steps](dimension-attributes.md#reading-through-steps)), and at most 4,000 keys. The first batch
  is as many keys as the declaration allows at that bound, each label or attribute of several steps counted at its
  `maxReferences`; every later batch as many as the references the keys before it followed say fit, with a quarter to
  spare, since a wellbore names one well however many its attributes may follow. A batch whose keys reach more is let go
  the moment they do and read again in batches half its size, so no batch holds more, whatever its records. Two batches
  are read side by side, so the search is asked for one while the other's answers are read: a build holds at most
  100,000 references. Each batch is staged as cleaned, in the order of its keys, with its attributes and the records
  they were read through, while the next batches' labels are read.
- **Collected values, a nested array's objects and the records an incremental load reads** are staged a page at a time
  as the search answers.
- **Values** are laid out from the staged keys in one ordered read, and staged; those whose records a search counts
  exactly are counted 1,000 at a time.
- **The write** checks that the staging holds every key and value the build staged, then copies it into the dimension
  where the database holds it: no row passes through the worker on the way.

Each build stages in tables of its own, heaps loaded in bulk and indexed only where they are read back in order, and
drops them when it ends, written or not; what a build whose worker stopped left, the next build of the dimension drops
once the stopped build is closed or has run for 48 hours ([A dimension build's staging](dimension-staging.md)). A build
whose staging was dropped under it writes nothing and fails:
`The staging tables of dimension build <id> are gone: another build of the dimension dropped them, taking this build,
running for more than 48 hours, for one its process left, or the dimension was removed while it ran. Nothing was
written; build the dimension again.`

## What a run returns

A build's result (`run.json`, the run page) is the flow, the partition, how many dimensions were built, failed and
skipped (not built in this partition), and per dimension its status (`completed`, `failed` or `skipped`), build number,
values, keys, keys of no value, unfilterable keys, labelled keys, what it changed (values and keys added, removed,
restored, keys moved), requests, the field it aggregated, how it loaded (`load`: `full` or `incremental`, the window
`windowFrom` and `windowTo`, `changedRecords` and `touchedKeys`), its error, and its first 5 notes. Every note is on the build in
the ledger (`sqlflow dimensions history`). A plan's result is, per dimension, the query, the field it would read,
whether a record holds it more than once, the records it would read, the kinds with their records and templates, and
what stops it, and how a build would load it (`load`, `fullBecause`, `windowFrom`, `windowTo`, `changedRecords`). Both say, per view, what the build did or would do: a build's `views` give each view's status
(`written`, `unchanged` or `failed`), its check (`passed` or `failed`), its rows and its first notes, and `viewsDropped`
the views it dropped; a plan's `views` give each view's tables, its statement, what stops it and what a build will do. A
view that cannot be written or read fails the run, every dimension still built.
