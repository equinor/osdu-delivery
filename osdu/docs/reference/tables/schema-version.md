---
id: delivery-table-schema-version
title: "osdu.SchemaVersion: the single row that says which version of the osdu schema a database holds"
type: table
summary: "The one row naming the osdu schema's module version, the last migration applied, when and by whom, and the oldest SQLFlow catalog migration it needs."
keywords:
  - osdu.SchemaVersion
  - schema version table
  - module version
  - migrations
  - sqlflow db status
  - catalog migration
related:
  - delivery-concept-database
  - delivery-cli-db
  - delivery-concept-architecture
  - delivery-concept-ledger
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
---

# osdu.SchemaVersion: the single row that says which version of the osdu schema a database holds

The single row that says which version of the `osdu` schema a database holds: the module version and the last migration
applied, when and by whom, and the oldest SQLFlow catalog migration this schema works with. Written by the module
database's migrate step on the same connection as the migrations, inserted on the first migrate and updated on every
later one; the check constraint `CK_SchemaVersion_SingleRow` (`[Id] = 1`) allows no second row. Every host reads it at
startup, and the hosts and `sqlflow db status` refuse to run against pending migrations, a database newer than the code,
or a SQLFlow catalog older than the minimum it names ([`sqlflow db`](../cli/db.md)).

The schema's migration history is `[osdu].[__EFMigrationsHistory]`, EF Core's own table, which the module's migrations
write and no entity maps ([The osdu schema](../concepts/database.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `int` | no | Always `1`: the table holds exactly one row. |
| `ModuleVersion` | `nvarchar(32)` | no | The module version the applied migrations produce. |
| `LastMigration` | `nvarchar(150)` | no | The newest migration applied. |
| `AppliedUtc` | `datetime2` | no | When the migrate ran. |
| `AppliedBy` | `nvarchar(200)` | no | The host and actor that ran the migrate, at most 200 characters. |
| `MinimumCatalogMigration` | `nvarchar(150)` | no | The oldest SQLFlow catalog migration the schema needs beside it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_SchemaVersion` | `Id` | primary key | none | none | The one row. |

## Written and read by

- **Written** by the module database's migrate step (`OsduSchema.RecordAsync`), which `sqlflow db migrate` runs.
- **Read** by every host at startup and by `sqlflow db status` (`OsduSchema.ReadAsync`).
