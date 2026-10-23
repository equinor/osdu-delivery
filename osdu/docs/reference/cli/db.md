---
id: delivery-cli-db
title: "sqlflow db in OSDU Delivery: migrating and checking the osdu module database"
type: cli-command
summary: "What sqlflow db migrate, status and sync do for the osdu schema: its migrations, its SchemaVersion row, --module osdu, and what a sync writes."
keywords:
  - osdu schema
  - module database
  - db migrate
  - db status
  - "--module"
  - schemaversion
  - ef migrations
  - sqlflow_osdu_db
  - migration drift
  - pending migrations
  - db sync mappings
  - osdu:database:connection
related:
  - cli-db
  - delivery-concept-architecture
  - delivery-cli-worker
  - delivery-concept-control-plane
  - delivery-guide-deployment
  - concept-shadow-catalog
sourceRefs:
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/ICliModule.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabase.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabases.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseStatus.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseConnections.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - sqlflow/src/SqlFlow.ControlPlane/Infrastructure/ModuleDatabaseVerification.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContextDesignTimeFactory.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/tests/SqlFlow.Delivery.Tests/MigrationScriptTests.cs
---

# sqlflow db in OSDU Delivery

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `db` is SQLFlow's verb, and what it
does for SQLFlow's catalog (`migrate` and `--create`, `status`, `sync`, run write-back, the guards on provisioning) is
documented in [sqlflow db](../../../../sqlflow/docs/reference/cli/db.md). This page covers what OSDU Delivery adds: the
`osdu` module database, which `migrate` and `status` cover after the catalog, and the module's documents, which `sync`
writes beside the pipelines.

## Synopsis

```bash
sqlflow db migrate [--db <conn-ref>] [--create] [--module osdu]
sqlflow db status  [--db <conn-ref>] [--module osdu]
sqlflow db sync    [path] [--db <conn-ref>] [--repo <name>] [--repo-url <url>] [--connect]
```

## The module database

Every table, view and index of OSDU Delivery (the ledger, mappings, templates, caches, the partition registry, the
central configuration) lives in the SQL Server schema `osdu`, owned by the module's own EF Core context with its own
migrations and its own version. SQLFlow's catalog model holds no OSDU table, and OSDU never changes SQLFlow's tables.

| Property | Value |
| --- | --- |
| Module name | `osdu`: the name `--module` takes and every message uses. |
| Schema | `osdu` |
| Migration history | `[osdu].[__EFMigrationsHistory]` |
| Version row | `[osdu].[SchemaVersion]`: one row (a check constraint allows only `Id = 1`) with the module version, the last migration applied, when and by whom, and the oldest SQLFlow catalog migration the schema needs. |
| Model and migrations | `osdu/src/SqlFlow.Delivery.Data` (`OsduDbContext`, `Migrations/`). |

The one exception to "every table has a migration": a dimension flow materialises each dimension as
`[osdu].[dim_<dimension>]` at run time and widens it through schema evolution. Those tables are data the module
publishes; no migration creates them and `status` does not count them (see [Dimension flow](../flow/dimension.md)).

### Where it lives

The `osdu` schema lives in the catalog's database, beside SQLFlow's `catalog` schema, and every host reaches it through
the catalog's one connection:

| Host | The module database is |
| --- | --- |
| The CLI (`sqlflow db`, `config`, `partition`, `template`, `check`, `run` and the other commands) | The catalog database: `--db`, else `${env:SQLFLOW_CATALOG_DB}`. |
| The control plane | The catalog database, on the connection it opens the catalog on (`ControlPlane:Catalog:ConnectionReference`, by default `${env:SQLFLOW_CATALOG_DB}`). |
| A worker node | `${env:SQLFLOW_CATALOG_DB}`: SQLFlow gives a node no catalog connection, so the module opens the catalog's database through that variable itself (see [sqlflow worker](worker.md)). |

At run time a node's login needs to reach the `osdu` schema only. The module has no connection of its own:
`SQLFLOW_OSDU_DB` and the control plane's `Osdu:Database:Connection` are no longer read, and a host still given either ignores it, so a
deployment whose settings cannot change starts as before. An estate that kept the `osdu` schema in a database of its own
brings that schema into the catalog's database before it upgrades: the hosts read the ledger in the catalog's database
alone.

Work that has to commit with the catalog (a repository sync writing mappings, a run queued with its submission) joins
the catalog's connection and transaction.

## migrate

`migrate` applies SQLFlow's catalog migrations first and then the module's, because the module needs a minimum catalog
migration. The module database follows the same `--create` rule as the catalog: without it, a missing database, or an
`osdu` schema that holds tables but no migration history of the module, is refused rather than provisioned. A database
ahead of this build is refused either way: an older build does not migrate a newer schema. Each migration runs in its
own transaction under EF Core's migration lock, and the version row is written on the same connection once they are
applied, recording `sqlflow db migrate on <machine> by <user>` as who applied them.

On success each database prints one line:

```text
OK   catalog database current at '<last-migration>' (N migration(s) applied, 0 pending).
OK   module 'osdu' (schema 'osdu', catalog connection): current at '<last-migration>' (N migration(s) applied, 0 pending; build version <version>, recorded version <version>, catalog has '<catalog-migration>').
```

A worker node's start-up check describes it as `own connection` instead: the node opens the catalog's database
through `SQLFLOW_CATALOG_DB` itself, since SQLFlow gives a node no catalog connection.

## status

`status` reports the catalog and then the module database, without changing either. The module's line names its state,
its counts and its versions, then one indented line per migration that is pending or that this build does not know:

```text
catalog: N migration(s) applied, 0 pending.
module 'osdu' (schema 'osdu', catalog connection): behind this build (N migration(s) applied, 2 pending; build version <version>, recorded version <version>, catalog has '<catalog-migration>').
  pending: <migration>
  pending: <migration>
```

| State | Meaning |
| --- | --- |
| current | Every migration this build knows is applied, none it does not know, and the catalog has the migration the module needs. |
| the database does not exist | The catalog database the connection names is missing. `migrate --create` provisions it. |
| behind this build | Migrations this build knows are not applied. `migrate` applies them. |
| ahead of this build | The database has migrations this build does not know (`unknown to this build:` lines): a newer build migrated it. Run that build. |
| diverged from this build | Both of the above. |
| current, but the catalog has not applied migration '...' | The module database is current and the catalog is older than the module needs. |

`status` exits 0 when every database is current and 2 when any is not, so it works as a deployment or CI drift gate for
both schemas at once:

```bash
if ! sqlflow db status; then
  echo "a database is behind or ahead of this build; run: sqlflow db migrate"
  exit 1
fi
```

## --module osdu

`--module osdu` limits `migrate` or `status` to the module database. The catalog is read to check the catalog migration
the module needs, but is not migrated. `--module` with `sync`, or a name no module registers, is refused:

```text
ERROR  --module applies to 'db migrate' and 'db status' only.
ERROR  there is no module database 'osdx'; this host registers: osdu.
```

## sync

`sync` projects a repository into the catalog as SQLFlow describes, and the OSDU module extends the same pass: every
`documentType: mapping` document becomes a mapping row keyed by its `Name@version` (one that does not parse is recorded
as invalid, with its error, rather than left out), every cache flow's declared types become cache definition rows, one
per type and partition served, and every delivery flow's interfaces become interface rows, one per partition served. A
flow that follows the partition registry is described in the partitions registered when the sync runs. The search terms
are extracted from the same documents. The summary line gains one fragment:

```text
OK   synced '<dir>': pipelines ...; lineage ...; documents +A added, U updated, N unchanged, D removed, I invalid.
```

The module's rows commit with the catalog's when the `osdu` schema is in the catalog's database, and on their own
connection otherwise. `sync` migrates the catalog only: run `migrate` first so the `osdu` tables exist. The sync
records which file of the repository's template folders holds which template, and keeps none of them: a template is read
from its file ([templates](../concepts/templates.md#what-the-sync-records)). Cache contents are not in a repository, so
the sync writes none: a partition's cache versions are written by the runs of its cache flows.

## The hosts refuse a mismatch

The same checks `status` makes stop a host before it serves anything:

- **The control plane** migrates the module database at startup after the catalog, under the same switches as the
  catalog (`ControlPlane:Bootstrap:ApplyMigrations`, and `ControlPlane:Bootstrap:AllowCreate` for a missing database or a
  populated schema), then verifies it. A module database that is missing, behind, ahead or diverged, or a catalog older
  than the module needs, stops the host with `The control plane refuses to run: <reason>` in its log. Until the database
  is verified, the readiness probe (`/health/ready`, through its `module-databases` check) answers unhealthy.
- **A worker node** verifies the module database before it polls for work, without a catalog check (it has no catalog),
  and exits with `ERROR  the worker refuses to start: <reason>`.

That turns "this database predates the build" into one message at startup naming the migration or version, instead of an
`Invalid object name` on whichever request first touches a missing table.

## Errors

Failures print `ERROR  <phase> '<subcommand>' failed: <message>`, where the phase is `catalog` or
`module database 'osdu'`, with the message passed through the credential redactor. The module's refusals, verbatim:

| Message | Cause |
| --- | --- |
| `The database of module 'osdu' (schema 'osdu', catalog connection) does not exist (<target>). Refusing to create it automatically. Provision it explicitly with 'sqlflow db migrate --create', or point the module's connection at an existing database.` | `migrate --module osdu` without `--create` against a missing catalog database. |
| `The schema 'osdu' in <target> holds <n> table(s) but no migration history of module 'osdu'. Refusing to create the module's tables beside objects it did not create. If the schema really belongs to the module, migrate it explicitly with 'sqlflow db migrate --create'.` | An `osdu` schema created by something else. |
| `The database of module 'osdu' (...) is ahead of this build: it has <n> migration(s) this build does not know (<names>; build version <v>, recorded version <v>). Run a build of module 'osdu' that includes them.` | A newer build migrated the database. |
| `Module 'osdu' needs the catalog migration '<migration>', which the catalog has not applied. Migrate the catalog first ('sqlflow db migrate').` | `migrate --module osdu` on the catalog connection while the catalog is behind. |

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | `migrate` or `sync` succeeded; `status` found every database current. |
| 1 | A refusal or failure above, an unresolvable `--db` or `SQLFLOW_CATALOG_DB`, or a misused `--module`. |
| 2 | `status` found a database that is not current (the catalog's pending migrations, or the module's state other than current). |

## For contributors: adding a migration

Every change to the module's model (an entity, a column, a length, an index) ships with its migration, the migration's
designer file and the refreshed model snapshot, in the same commit; nothing re-mints a database to make up for a missing
one. The EF tooling is pinned in the repository's tool manifest (`.config/dotnet-tools.json`):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project osdu/src/SqlFlow.Delivery.Data
```

The design-time factory builds the context against a local placeholder server only to pick the provider; migrations are
applied through `sqlflow db migrate` or the control plane, never through the factory. The test suite checks that every
module migration names the `osdu` schema alone, that its SQL script writes nowhere else, and that no SQLFlow catalog
migration touches `osdu`.

## See also

- [sqlflow db](../../../../sqlflow/docs/reference/cli/db.md): the verb, the catalog and its guards.
- [Architecture](../concepts/architecture.md): the module on SQLFlow, the hosts and the databases.
- [sqlflow worker](worker.md): why a node needs `SQLFLOW_CATALOG_DB`.
- [Control plane](../concepts/control-plane.md): the module's configuration on the control plane.
- [Deploying OSDU Delivery](../guides/deployment.md): when the databases are created.
- [The shadow catalog](../../../../sqlflow/docs/reference/concepts/shadow-catalog.md): what a sync projects for SQLFlow.
