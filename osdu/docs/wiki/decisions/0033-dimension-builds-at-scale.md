---
id: delivery-wiki-0033-dimension-builds-at-scale
title: "Decision 0033: a dimension build sizes its batches by what it reads, keeps what did not change, stages in heaps, and locks only its own rows"
type: decision
summary: "Why a dimension build sizes label batches by what keys reach, keeps unchanged checks, writes only touched rows, stages in heaps and locks its own rows."
keywords:
  - "dimension build time"
  - "label batches"
  - "request budget"
  - "view check kept"
  - "heap staging"
  - "dimstage_"
  - "deadlock"
  - "row locks"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionLabeler.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261026181652_DimensionViewCheckReuse.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261026192825_DimensionStagePerBuild.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261026202905_DimensionRowLocks.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionBuildScaleTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionRowLockTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DimensionStageTests.cs
referenceRefs:
  - delivery-flow-dimension-build
  - delivery-flow-dimension-staging
  - delivery-flow-dimension-views
  - delivery-flow-dimension-table
  - delivery-flow-dimension
related:
  - delivery-wiki-0032-dimension-builds-stage-in-the-database
  - delivery-wiki-0030-dimension-rows-one-range
  - delivery-wiki-0025-parallelism-and-default-fan-out
updated: 2026-10-26
---
# 0033: A dimension build sizes its batches by what it reads, keeps what did not change, stages in heaps, and locks only its own rows

Status: accepted. Documented in [What a build does](../../reference/flow/dimension-build.md#what-a-build-does),
[What a build holds](../../reference/flow/dimension-build.md#what-a-build-holds),
[A dimension build's staging](../../reference/flow/dimension-staging.md), [Dimension views](../../reference/flow/dimension-views.md)
and [The dimension's table](../../reference/flow/dimension-table.md).

## Context

A production estate's well log dimension flow took 30 minutes a build. Its trace put 16 minutes in one wellbore
dimension's labels and attributes, read in 20,919 searches; 4.8 minutes in that dimension's write; and 7 minutes in
checking the flow's views, which ran after every build, whatever it changed.

Reading the code behind the trace: a batch of keys was sized for the most references every label and attribute of
several steps could reach (`maxReferences`), so a wellbore that names one well was budgeted as if it named hundreds, and
batches of a few dozen keys followed one another. The labeler had concurrency of its own that the build never gave it
room to use. A view's check hashed every row it read, and ran again on a build that changed nothing it read. An
incremental load pivoted and compared every row of the dimension's table, however few keys it read again. The staging of
[0032](0032-dimension-builds-stage-in-the-database.md) loaded seven tables shared by every build, keyed and ordered as
they filled, and deleted a build's rows one by one when it ended.

Measuring at the estate's size then showed builds of two dimensions deadlocking, one failing as the victim, and
waiting for each other otherwise. SQL Server's deadlock graphs named page locks on the page where one dimension's rows
meet the next, in the clustered index and in an index of the same table, and a check before each write that scanned the
dimensions rather than seeking the one it read.

## Decision

- **A label batch is sized by what the keys before it reached.** The first batch is sized as before, from the
  declaration. Every later one holds as many keys as the references the keys before them followed say fit in 50,000,
  with a quarter to spare, at most 4,000. A batch whose keys reach more is let go the moment they do and read again in
  halves, so no batch holds more than before. Two batches are read side by side.
- **The build shares one request budget.** A build asks the search at most `recordsAtOnce` x `recordsAtOnce` requests
  at once, its dimensions together, and a dimension's labels take as many as are free, so the last dimension building
  asks as much as the whole build may.
- **A view's check reads each row once, and is kept when nothing it reads changed.** The check no longer hashes a row to
  count it. Each check records a version of everything its view reads, the dimension tables of the views it joins
  included; a build that changed none of it keeps the last check and says so (the Checks tab's **kept**).
- **An incremental load writes only the rows it read again.** The dimension's table is pivoted and compared for the
  keys the load read again alone; a full load, or one that rewrites the table, still writes every row. Each dimension's
  table carries a version a view's check reads.
- **Each build stages in heaps of its own, dropped when it ends.** A build makes seven tables named after its dimension
  and its id (`dimstage_<dimension>_<build>_<table>`), heaps a bulk copy fills under a bulk update lock. Only the two
  read back in order (the keys and the values laid out) are given a clustered index, once staged whole. A build drops
  its tables when it ends, rather than deleting its rows, and a later build drops what a stopped one left. A build whose
  tables were dropped under it fails saying so. The human asked for this: large inserts go to tables without indexes,
  and an index built after the load costs less than one kept as the rows arrive.
- **A build locks only its own rows.** Every index of the ten tables holding a dimension's rows as one range of their
  clustered key takes no page lock, so two builds' locks never meet on a page their ranges share; a test that holds one
  dimension's rows while the other builds fails when page locks are allowed. The check of a dimension's table name
  before a write seeks an index holding what it reads, so it reads no other dimension's row.

## Held back

- **Spreading a build over nodes.** Built and then set aside with the human: after sizing the batches, the largest
  dimension's labels are a few hundred searches, about as long as an autoscaled node takes to start, so a build split
  over nodes added a coordinator, a payload and two settings for little time. Revisit when a run on a real platform
  shows a node, not the search service or the database, is what a build waits on.
- **Writing every statement in batches of under 5,000 rows.** SQL Server locks a table whole once one statement holds
  5,000 locks on it, which makes other dimensions' writes to that table wait until the build commits. Batches of fewer
  rows in the one transaction avoid it (tested on this server: twenty statements of 2,000 rows held 120,000 row locks
  and no table lock). Set aside with the human: it rewrites every statement of the write, and only a large dimension's
  first load writes that many rows at once, since a later full load writes what changed.

## Rejected

- **Downloading every record and searching it locally.** A second copy of a partition beside the database and the
  search service, which [0032](0032-dimension-builds-stage-in-the-database.md) rejected for a build's own reads. Sized
  batches already take the wellbore dimension's labels from about 20,900 searches to a few hundred.
- **Committing the write a batch at a time.** It releases locks as it goes, but a build failing or cancelled part way
  would leave its dimension part old and part new, and views would read it so; a write is whole or nothing.
- **Keeping the shared staging tables and their keys.** Every build's copies wrote into the same clustered keys, and a
  build's end deleted its rows one by one, each delete logged.
- **Hashing every row to check a view.** The hash was the check's cost and told nothing the row count and the checks'
  own reads did not.

## What it costs

On the suites' stand-in search at a production estate's size (35,067 well logs with 350,667 curves, 161,308
wellbores, 139,058 wells, 506,968 geo contexts), with 20 ms a search, before the heap staging: the wellbore dimension's
labels and attributes took 323 searches; a build that changed nothing took 5.9 s, its views kept; one with a sixth of the
logs changed took 98.8 s. The stand-in search answers in process, so a full load's time there is mostly its own, and its
writes took about six and a half minutes, the largest the curves' at 157 s. A build's staging is seven tables made and
dropped per build, which the worker's rights already allowed (it makes each dimension's `dim_` table). Row locks hold
more locks than page locks for a large write, which SQL Server keeps in memory until the build commits.
