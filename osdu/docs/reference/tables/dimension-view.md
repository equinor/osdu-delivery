---
id: delivery-table-dimension-view
title: "osdu.DimensionView: each view a dimension flow declares, as its builds last wrote it in the schema"
type: table
summary: "One row per view a dimension flow declares: its v_dim_ name, its declaration, the statement it was written with, its tables and columns, and who wrote it."
keywords:
  - osdu.DimensionView
  - dimension view table
  - v_dim_ views
  - dimension views
  - view statement
  - view record
related:
  - delivery-concept-database
  - delivery-flow-dimension-views
  - delivery-concept-dimension-tables
  - delivery-table-dimension-view-check
  - delivery-table-dimension
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009204033_DimensionViews.cs
---

# osdu.DimensionView: each view a dimension flow declares, as its builds last wrote it in the schema

A view a dimension flow declares, as its builds last wrote it: what the module made in its schema, so a build writes only
views it recorded and drops only those its flow no longer declares ([Dimension views](../flow/dimension-views.md)). The
view itself, `[osdu].[v_dim_<view>]`, is written from the flow's document alone and is no object of the model: no entity
maps it and no migration creates it. A view spans every partition its tables hold, so this table belongs to no ledger
partition and has no `PartitionId`. Removing a view deletes its record and its checks
([`osdu.DimensionViewCheck`](dimension-view-check.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `ViewId` | `int` identity | no | The record's id. |
| `Name` | `nvarchar(64)` | no | The view's name as the document gives it, unique ignoring case among the views of the database. |
| `ViewName` | `nvarchar(128)` | no | The view's name in the module's schema (`v_dim_<name>`). |
| `FlowName` | `nvarchar(200)` | no | The dimension flow that declares the view, by its name. |
| `LedgerId` | `uniqueidentifier` | no | The ledger of the build that last wrote the view, which a removal of the view is recorded under ([`osdu.Ledger`](ledger.md)). |
| `Description` | `nvarchar(1000)` | yes | The view's description, as its document gives it. |
| `DeclarationJson` | `nvarchar(max)` | no | The view as its document declares it: its `from` dimension, its joins and its columns, as JSON. |
| `Sql` | `nvarchar(max)` | no | The statement the view was last written with. |
| `SqlHash` | `nvarchar(64)` | yes | SHA-256 of `Sql`, in hex: a build that would write the same statement writes nothing. Null when the view is to be written again: a build renamed a column of a table it reads and dropped it. |
| `TablesJson` | `nvarchar(max)` | no | The dimension tables the view reads, its `from` table first, as a JSON list of names. |
| `ColumnsJson` | `nvarchar(max)` | no | The view's columns with their types, expressions and descriptions, as JSON. |
| `Note` | `nvarchar(1000)` | yes | Why the view is not in the database now, when it is not: what dropped it, and what writes it again. |
| `WrittenRunId` | `uniqueidentifier` | yes | The platform run whose build last wrote the view. |
| `WrittenBy` | `nvarchar(200)` | no | Who started that run. |
| `WrittenUtc` | `datetime2` | no | When the view was last written. |
| `CreatedUtc` | `datetime2` | no | When the record was created. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionView` | `ViewId` | primary key | none | none | A view's record by its id. |
| `IX_DimensionView_Name` | `Name` | unique | none | none | A view by its name, once in the database: what a build finds its record by, and what refuses a second flow's view of the same name. |
| `IX_DimensionView_ViewName` | `ViewName` | unique | none | none | A view by its name in the schema, once. |
| `IX_DimensionView_FlowName` | `FlowName` | index | none | none | A flow's views: what a build drops the undeclared ones of. |

## Written and read by

- **Written** by a build after its dimensions, each view in a transaction of its own (`SqlServerDimensionViewStore.cs`).
- **Read** by the next build, the dimension flow's views page, and removal of a view.
