---
id: delivery-wiki-0032-dimension-builds-stage-in-the-database
title: "Decision 0032: a dimension build stages what it reads in the database and holds one batch in memory"
type: decision
summary: "Why a dimension build stages its reads in tables of the osdu schema, reads labels in bounded batches, and why more memory, temp tables and files were rejected."
keywords:
  - "out of memory"
  - "OutOfMemoryException"
  - "dimension build memory"
  - "staging tables"
  - "bounded batches"
  - "parity suite"
  - "incremental load caps"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionLabeler.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/BoundedCache.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DistinctValues.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261026103651_DimensionBuildStaging.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionBuildParityTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionStageTests.cs
referenceRefs:
  - delivery-flow-dimension-build
  - delivery-flow-dimension-attributes
  - delivery-flow-dimension-incremental
  - delivery-concept-database
  - delivery-table-dimension-stage-key
related:
  - delivery-wiki-0011-dimension-flows
  - delivery-wiki-0030-dimension-rows-one-range
updated: 2026-10-26
---
# 0032: A dimension build stages what it reads in the database and holds one batch in memory

Status: accepted. Documented in [What a build holds](../../reference/flow/dimension-build.md#what-a-build-holds) and on
the staging tables' pages.

## Context

A dimension build held everything it read in the worker's memory until its write: every key with its label, attributes
and filter, the records each key was read through, the texts and values it collected, the objects of a nested array,
and the values laid out from the keys; the write then built its rows in the worker and copied them to the database. The
labeler read every key's label and attributes at once, and kept each record a step read whole until the read was done.

A wellbore dimension of a production estate, keyed by id with nine attributes read through references, failed with an
`OutOfMemoryException` in a worker of 1 GB, and again after the worker was given 3 GB. Measured on the suites' stand-in
search, by the managed memory a full collection leaves, the build held about 6.8 KB a key: 140 MB at 20,000 wellbores,
410 MB at 60,000. What a build held grew with its dimension, and no setting bounded it.

## Decision

- **A build stages what it reads in seven tables of the `osdu` schema as it reads it**, each keyed on the partition, the
  dimension, the build and the row's place, and holds in memory only the batch it is working on. Keys are staged as each
  range of the aggregation completes; collected values, elements and the records an incremental load reads, a page at a
  time; values are laid out from one ordered read of the staged keys.
- **Labels and attributes are read a batch of keys at a time**: at most 4,000 keys, fewer when a chain reads through
  several steps, so a batch follows at most 100,000 references. Of each record a step reads, only what its paths read is
  kept; what a later step read of a record is kept between batches for up to 50,000 records, so a record many keys reach
  is asked for once. The notes are added up over the batches exactly as one read of every key would say them.
- **The write copies the staging into the temporary tables its merge always read, inside the database**, under the
  dimension's lock, and the merge statement is unchanged. A build that hands its rows over in memory is staged the same
  way, so there is one write path.
- **The rebuild was held to the build before it**: a parity suite, written and run on the old engine first, keeps every
  row a build writes (keys, values, attributes, collected texts, elements, records, incremental loads) and fails on any
  difference. It found one defect of the old engine: a key holding a control character failed the whole dimension; it
  now has no label, and the notes say why.
- **A build deletes its staging when it ends.** What a build whose worker stopped left, the next build of the dimension
  deletes once the stopped build is closed or has run for 48 hours, taking the dimension's lock without waiting, so a
  build writing keeps what it staged. A write checks, under the lock, that its staging holds every key and value it
  staged, and writes nothing otherwise.
- **An incremental load past 250,000 changed records or keys to read again loads in full**: it holds those while it finds
  what to read, and a full load reads a range at a time.

## Rejected

- **More memory for the worker.** It failed at three times the memory, and any setting is outgrown by a larger
  dimension.
- **Temporary tables.** A temporary table belongs to one connection, and a build reads labels and collected values side
  by side, each copying on its own connection; a build that reads for hours would also lose everything with one dropped
  connection.
- **Files on the worker's disk.** A second store beside the database, on a disk a container may not keep, and the write
  would stream every row back through the worker; the database already holds the dimension the merge writes.
- **Writing a batch at a time without staging.** A full load removes what it no longer found, which only the whole read
  says, and a build is written whole or not at all.

## What it costs

On the stand-in search, the same builds, one after another on one machine: 17.4 s before and 18.6 s after at 20,000
wellbores, 57.1 s and 61.9 s at 60,000 (about 8% more); the managed memory a build holds is about 50 MB at either size. A
batch is staged while the next batch's labels are read, which about halved the time staging first added. The staging tables
hold rows only while builds run.
