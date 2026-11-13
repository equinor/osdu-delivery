---
id: delivery-wiki-0055-dimension-tables-kept-where-the-flow-says
title: "Decision 0055: a dimension flow keeps its tables and views where its target says, written after the merge"
type: decision
summary: "Why dim_ tables and v_dim_ views may live in another database while the dimension stays in the catalog, and why the table is written after the merge."
keywords:
  - dimension
  - target
  - target.database
  - target.schema
  - alternate database
  - catalog size
  - table step
  - storage
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTableTarget.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.DimensionViews.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionViewMapper.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
referenceRefs:
  - delivery-flow-dimension-target
  - delivery-flow-dimension-table
  - delivery-flow-dimension-views
  - delivery-concept-storage-and-retention
related:
  - delivery-wiki-0011-dimension-flows
  - delivery-wiki-0026-one-database-connection
  - delivery-wiki-0054-dimensions-keep-only-their-latest-state
updated: 2026-11-13
---
# 0055: A dimension flow keeps its tables and views where its target says, written after the merge

Status: accepted. Documented in [Dimension target](../../reference/flow/dimension-target.md). Leaves
[0026](0026-one-database-connection.md) as it is: the module's own tables stay in the catalog's database on its one
connection.

## Context

A production estate's catalog database (Azure SQL, 32 GB at most) reached 24.5 GB in four weeks. Of its 9.1 GB of
dimension storage, 7.2 GB was the dimensions' record in the `osdu` schema (keys, values, attributes, elements) and 1.7 GB
the `dim_` tables built from it; [0054](0054-dimensions-keep-only-their-latest-state.md) takes the removed part of the
record out. The tables are what reports and pipelines read, and they need not share the catalog's size limit, backups or
load.

A flow could already declare `target.connection`, but only for lineage: a build refused any connection that did not reach
the module's own database, and a flow declaring views had to name one.

The owner asked for the materialized table to go optionally to another database of their choice, named with its
connection and database, as a second step after the dimension is built, and to stay in the catalog when nothing is named.
The dimensions themselves stay in the catalog.

## Decision

- **The record stays; the tables and views move.** A dimension's record is what a build merges into, what tells one
  build from the next, and what the ledger answers from, so it stays in the module's database. Only `dim_<dimension>`
  and the flow's `v_dim_<view>` views are kept where `target` says: `connection`, `database` and `schema`, each optional,
  the module's connection, its database and `osdu` when left out.
- **The table is a second step.** The merge commits in the module's database and lays out the rows the table is to hold;
  the table step then writes them where the target keeps them, in a transaction of that database, and the ledger records
  which build the table holds. A table step that fails fails the build, leaves the dimension written, and the next build
  lays the table out whole. There is one way to write a table: the module's database takes the same two steps on its own
  connection.
- **A target is settled before anything is built.** A build opens the target with the run's references and asks the
  server which database it reached. One it cannot open stops the build; one reaching the module's own database is the
  module's database, where a table is kept in `osdu` only.
- **A moved target leaves nothing behind.** Before a merge, the build takes the table from where it was kept (the table,
  or the partition's rows of a table other partitions still keep there), and lays it out whole at the new place; a view
  is written at the new place and dropped from the old.
- **Views no longer need a target.** Without one they are kept beside their tables in the module's database, as the
  tables always were.

## Rejected

- **Moving the whole dimension out of the catalog.** The record is joined with the ledger's runs and changes, read by the
  cache and the GUI, and written in the ledger's transactions; a second database for it is the second module connection
  0026 removed.
- **Writing the table in the merge's transaction across databases.** That needs a distributed transaction (MSDTC on SQL
  Server, elastic transactions on Azure SQL, each with its own setup and limits on which servers take part), and it would
  hold the record's locks for as long as the other database takes. The record is right without the table; the table
  only has to catch up, which the next build does.
- **Moving the tables by a job of their own.** A second path to the same tables, run on its own schedule, would leave them
  behind the record between runs; the build already knows what changed.
- **Storing what a reference resolves to.** The ledger keeps the reference as written, as every flow does, and resolves
  it when it opens the target.

## Consequences

- A flow naming another database moves its tables and views there at its next build; the catalog keeps the record, the
  views' records and their checks.
- The GUI, the CLI and the API read a table where the ledger says it is, with the host's references: a reference only a
  partition's central configuration supplies is reached by builds but not by those readers.
- The database user a target is written as needs CREATE TABLE, CREATE VIEW and ALTER on the schema there, and CREATE
  SCHEMA when the schema is new.
- A flow declaring `target.connection` to the module's database, as views required before, builds as it did.
