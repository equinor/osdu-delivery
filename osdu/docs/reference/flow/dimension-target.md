---
id: delivery-flow-dimension-target
title: "Dimension target (target): the connection, database and schema a dimension flow keeps its tables and views in"
type: flow-reference
summary: "Where a dimension flow keeps its dim_ tables and v_dim_ views: another connection, database or schema, written after the merge; dimensions stay in the catalog."
keywords:
  - target
  - target.connection
  - target.database
  - target.schema
  - dimension tables in another database
  - alternate database
  - catalog size
  - table step
  - table behind
  - moving a target
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-table
  - delivery-flow-dimension-views
  - delivery-concept-dimension-tables
  - delivery-concept-lineage
  - delivery-concept-storage-and-retention
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionViewMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionLineage.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTableTarget.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.DimensionViews.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261112223514_DimensionTableTargets.cs
---

# Dimension target (target): the connection, database and schema a dimension flow keeps its tables and views in

A dimension is kept in two parts. Its record (its keys, values, attributes, elements, the records it was read from, its
changes and its builds) is always in the module's database: it is what a build merges into, what tells one build's
values from the next, and what the GUI, the CLI and the cache read a dimension from. Its table, `dim_<dimension>`
([the dimension's table](dimension-table.md)), and the flow's [views](dimension-views.md), `v_dim_<view>`, are what
reports and pipelines read, and `target` says where those are kept. A flow naming no target keeps them in the module's
database, in schema `osdu`, as before.

```yaml
target:
  connection: ${env:WAREHOUSE_DB}   # the server; left out, the module's own connection
  database: Dimensions              # the database on it; left out, the connection's own
  schema: dim                       # the schema; left out, osdu
```

| Key | Default | Meaning |
| --- | --- | --- |
| `target` | the module's database, schema `osdu` | Where the flow's tables and views are kept. It names at least one of the three keys below; `target: {}` is refused. |
| `target.connection` | the module's own connection | The server, as a `${env:...}` or `${keyvault:...}` reference, or a SQL Server connection string whose password is one, written exactly as the pipelines reading the tables and views write it. A literal password is refused when the document is read. |
| `target.database` | the connection's own database | The database on that server, at most 128 characters. Named without `connection`, it is a database on the module's own server, reached on the module's own connection. |
| `target.schema` | `osdu` | A letter or an underscore, then letters, digits and underscores, at most 128. The build makes it when the database does not hold it. |

A database beside the module's, on its own server, needs no reference of its own:

```yaml
target:
  database: OsduDimensions          # dim_ tables and v_dim_ views here; the dimensions stay in the module's database
```

## What a build does

1. **It settles the target before it builds anything.** The build opens the target with the run's references (the
   partition's supplied values over the node's environment) and asks the server which database it reached. A target
   it cannot open stops the build before anything is built, naming the reference and never what it resolves to, and a
   plan says the same of each view. A target that reaches the module's own database, however it is spelled, is the
   module's database; there a table is kept in schema `osdu` only, so another schema is refused before anything is built.
2. **The merge.** Each dimension is merged into its record in one transaction of the module's database, as without a
   target, and the rows its table is to hold are laid out there. The record says where the table is to be kept.
3. **The table step.** Once the merge commits, the table is made ready where the target keeps it (made, given a new
   attribute's column, a renamed key's or value's column, its indexes) and brought to the laid-out rows. In the module's
   database this is a transaction on its own connection. Elsewhere, the laid-out rows are copied to the target's
   connection, applied in a transaction of the target's database, and recorded in the ledger once that commits.
4. **A table step that fails** leaves the dimension written and the table behind it. The build is failed with why
   (`Dimension <id> was written, but its table <name> was not: ...`), and the ledger keeps which build the table holds,
   so the next build lays the table out whole rather than only what it changed.
5. **The views** are written once every dimension is built, beside the tables they read, in the target's database and
   schema; each view's record and its checks stay in the module's database. The check reads the view where it is.

## Moving a target

Changing `target` moves the flow's tables and views at its next build. Before a dimension's merge, the build takes the
dimension's table from where it was kept: the table itself when no other partition keeps the dimension there, else the
partition's rows of it, a batch at a time. The table is then laid out whole where the target keeps it now. Each view is
written at the new place and dropped from the old. A place the build can no longer reach stops it with nothing changed,
and the next build tries again, so nothing is left behind where the tables were.

## Reading, removing and lineage

- **Reading.** The GUI, the CLI and the API read a dimension's table and the flow's views where the ledger records them,
  and name them there (`Dimensions.dim.dim_Wellbore`; `osdu.dim_Wellbore` in the module's database). They open the target
  with the host's own references: a target whose reference only a partition's central configuration supplies is
  reached by builds, and read by the GUI only where the control plane's host resolves the same reference.
- **Removing.** Removing a dimension removes its partition's rows of the table where it is kept, and the table with the
  last partition keeping it; removing a view drops it where it is kept.
- **Lineage.** With a target, the flow declares that it writes each table and view on the target's connection
  (`${env:SQLFLOW_CATALOG_DB}`, the module's, when it names a database or schema alone), in its database and schema, so
  a pipeline reading them through the same reference is ordered after the flow ([lineage](../concepts/lineage.md)). A
  flow naming no target declares none.

## Permissions

The database user a build writes the target as (the target connection's, or the module's for a database alone) needs
CREATE TABLE and CREATE VIEW in the target's database and ALTER on its schema, to make tables, add and rename columns
and make indexes; and CREATE SCHEMA there when the schema is not there yet. In the module's database the module's own
grants cover it.

**Write the flow file**, in the explorer's dimension builder, writes a new flow keeping its tables where the estate's
flows write their data ([Writing a dimension flow file](../guides/dimension-flow-file.md)).

Why the record stays and only the tables move is [decision 0055](../../wiki/decisions/0055-dimension-tables-kept-where-the-flow-says.md).
