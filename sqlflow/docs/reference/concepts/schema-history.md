---
id: concept-schema-history
title: "Schema history: what changed in a table or view, and when"
type: concept
summary: "The catalog's record of every table and view whose columns changed: written by each sync and schema registration that reads an object's columns differently, and by source-control snapshots."
keywords:
  - schema history
  - schema changes
  - column added
  - column dropped
  - type changed
  - what changed
  - SchemaChange
  - SchemaChangeColumn
  - database_schema_changes
  - database_object_compare
  - origin sync
  - origin snapshot
related:
  - concept-shadow-catalog
  - concept-column-type-history
  - flow-scm
  - concept-schema-evolution
  - concept-lineage-tiers
  - concept-maintenance
sourceRefs:
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/SchemaColumnDiff.cs
  - src/SqlFlow.Catalog/Migrations/20261127123211_SchemaHistoryFromSync.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/GitHistoryEndpoints.cs
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/catalog/SchemaChangesPage.tsx
---

# Schema history

The catalog keeps one history of what changed in the tables and views of the databases its flows reach, in
`catalog.SchemaChange`, one row per object per change. Two things write it, and each row says which in `Origin`:

| Origin | Written by | What the row carries |
| --- | --- | --- |
| `Sync` | every pass that reads an object's columns: a repository sync (its connected read of the database, or the `CREATE TABLE` a run's trace holds) and a schema registration run (`flowType: sch`) | the object's catalog key, and in `catalog.SchemaChangeColumn` every column that moved |
| `Snapshot` | a source-control run ([`flowType: scm`](../flow/scm.md#schema-history-in-the-catalog-and-the-gui)) | the commit whose diff holds the object's DDL |

So an estate has a schema history without a snapshot flow: every database a flow reaches is covered from its second
sync on. What a run did about a change to an existing column's type (applied, refused with the values that would not
convert, left for a person) is the [column type history](column-type-history.md) beside it: that is the run's decision,
this is what the catalog then reads the column to be.

## What a sync records

A pass compares the columns it read of an object with the columns the catalog held of it and records the difference
(`SchemaColumnDiff`):

- A column only the new read holds is **added**, one only the earlier read held is **dropped**, and one both hold is
  **changed** when its type, its nullability or its place moved. Each moved column keeps both sides: `FromType`,
  `ToType`, `FromNullable`, `ToNullable`, `FromOrdinal`, `ToOrdinal`.
- Columns are matched by name ignoring case, and types are compared ignoring case and spacing (`DECIMAL(18, 2)` is
  `decimal(18,2)`), so a read that only spells a name or a type differently changes nothing.
- A column's place is its rank among the columns both reads hold. Adding a column in the middle moves none of the
  ones after it; a table recreated with a column elsewhere does.
- An object the catalog sees for the first time is recorded as **added**, with every column it read, but only when
  the pass's repository has synced before (or the registering flow has registered before). A repository's first sync
  reads what was already there, which is the starting point, not a change.
- A read at another tier than the one the catalog held (a live read replacing columns parsed from a run's DDL) is a
  new starting point and records nothing: the two describe the same columns in their own words. An offline read
  never overwrites a live one ([lineage tiers](lineage-tiers.md)).
- Only database objects are recorded: an object with a database, never a file, a dataset or a subscriber.
- A pass that reads the same columns again writes nothing, neither a change nor the dictionary.

A change is dated to the pass that first saw it, not to when the DDL ran. The control plane's managed sync reads
the databases on every pass ([`ManagedSync:ConnectLineage`](control-plane.md), on by default), so a change is dated to the
first sync after it. A sync is no run, so its rows have no `RunId`; a registration run's rows carry the run and its
flow.

What a sync cannot see, it does not record. An object it can no longer read keeps the columns it last read, so a
dropped table shows no change until a pass forgets the object, and a view whose definition changed without changing
its columns is a snapshot's to record.

## Reading it

- `GET /api/v1/schema-changes` lists the changes newest first, each with its `origin`, `objectKey` and `columns`.
  It filters by `repoId`, `database`, `objectKey`, `changeType` (`Added`, `Changed`, `Deleted`) and `since`, and
  `search` matches the object's name, schema or category, or the name of a column a change moved: "when did
  `TripDuration` appear" is one query. `GET /api/v1/schema-changes/databases` returns the tally per database.
- `GET /api/v1/schema-changes/{id}/compare?since=<iso>` sets the object before the window against now. For a
  `Sync` change both sides are its columns, one line each (`[name] type NULL`): now as the catalog last read them,
  and before worked back from them through every change the catalog recorded for the object since `since`, in any
  repository. Omitting `since` works back through the whole history: the first read the catalog kept, or nothing
  for an object it saw added. For a `Snapshot` change the sides are the object's DDL from the snapshot repository.
- The MCP tools `database_schema_changes`, `database_schema_history_databases` and `database_object_compare` read
  the same routes. `database_object_ddl` reads one snapshot commit, so it is for `Snapshot` rows alone.
- **Explore > Schema changes** shows the tree of databases, schemas and objects that moved; an object's history
  lists each moved column under its change, and the panel below compares the object before and now.

## Storage

`SchemaChange` and `SchemaChangeColumn` are kept: a history that forgets is not a history. A change's columns go
with it, and both are deleted only with their repository ([maintenance](maintenance.md)).
