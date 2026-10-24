---
id: delivery-concept-architecture
title: "Architecture: the OSDU module on SQLFlow, its hosts, its databases and the osdu schema"
type: concept
summary: "How OSDU Delivery composes SQLFlow with its module: the hosts, the extension points it registers through, what runs where, the databases and the osdu schema."
keywords:
  - architecture
  - osdu module
  - extension points
  - hosts
  - control plane host
  - worker host
  - osdu schema
  - module database
  - SQLFLOW_CATALOG_DB
  - ingestion database
  - catalog database
  - adddeliverykind
  - schemaversion
related:
  - delivery-concept-overview
  - delivery-concept-control-plane
  - delivery-cli-db
  - delivery-cli-worker
  - delivery-concept-ledger
  - concept-architecture-and-execution
  - concept-control-plane
  - cli-db
sourceRefs:
  - osdu/hosts/SqlFlow.Delivery.ControlPlane.Host/Program.cs
  - osdu/hosts/SqlFlow.Delivery.Worker.Host/Program.cs
  - osdu/hosts/SqlFlow.Delivery.Cli.Host/Program.cs
  - osdu/hosts/osdu-delivery-mcp/src/lib.rs
  - osdu/hosts/osdu-delivery-mcp/Cargo.toml
  - osdu/src/SqlFlow.Delivery/Hosting/OsduDeliveryBranding.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/gui/src/module.tsx
  - osdu/deploy/docker/control-plane.Dockerfile
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabase.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseStatus.cs
  - docs/sqlflow-changes.md
---

# Architecture: the OSDU module on SQLFlow, its hosts, its databases and the osdu schema

OSDU Delivery is a module on SQLFlow, not a fork of it. SQLFlow's control plane, catalog, run queue, compute nodes,
schedules, lineage, GUI shell and CLI run unchanged, and the module plugs into them through generic extension points.
Everything OSDU lives in the module: the six flow kinds and their executors, the ledger and the rest of the `osdu`
schema, the delivery API, the OSDU verbs of the CLI and the OSDU pages of the GUI. This page shows how the two are
composed, what each host runs, and where the data lives. SQLFlow's own architecture (two modes, one engine, the pull
model) is on [SQLFlow's architecture page](../../../../sqlflow/docs/reference/concepts/architecture-and-execution.md).

## One composition in every host

The module's composition root is `AddDeliveryKind`, which every host calls after SQLFlow's `AddSqlFlowEngine`, so a
flow runs the same way on the control plane, on a node and from the command line:

| Registered | What it is |
| --- | --- |
| The flow kinds | `delivery`, `retrieval`, `cache`, `assertion`, `dimension`, `inventory`, `removal`, each an `IFlowDocumentKind` |
| The companion documents | `mapping` (owned by the delivery kind) and `dictionary`, each an `ICompanionDocumentKind` |
| The executors | One `IFlowDocumentExecutor` per flow kind, behind SQLFlow's `DocumentExecutor` |
| The node tasks | Two `IComputeOperation`s a node runs for the control plane: a value check across a scope, and a removal |
| The direct operations | What a person asks and waits for, run in the process asked: a target probe, a record read back, a record's source rows, a preview, a scope's values, the explorer's reads |
| The repository sync extension | Mapping documents, cache definitions and delivery interfaces projected as rows of the `osdu` schema on every sync |
| The engine's services | The ingestion table reader, the payload file stores, the route implementations, the record search on the platform |

A host that also has the module's database calls `AddDeliveryLedger`, which adds the ledger, the template store, the
cache store, the central configuration and the partition registry over the `osdu` schema. A host without it can
validate and plan against the documents, and says so when asked for more:

```text
This host has no osdu database connection; the delivery ledger and caches are unavailable. Without them only validation
and planning against the flow document are available, since a render reads any cache it reads from that database. The
osdu schema lives in the catalog's database: start the host with the catalog's connection (SQLFLOW_CATALOG_DB, or --db on
the command line).
```

## The hosts

Each host is a short program in `osdu/hosts` that passes SQLFlow's entry point the module and the product's branding,
"OSDU Delivery, powered by SQLFlow":

| Host | What it is | Image |
| --- | --- | --- |
| `SqlFlow.Delivery.ControlPlane.Host` | SQLFlow's whole control plane (`ControlPlaneHost.RunAsync`) with `DeliveryControlPlaneModule`: the catalog, the run queue and dispatcher, schedules, the managed git sync, identity, notifications and the node protocol, plus the delivery endpoints and background services | `osdu-delivery-control-plane` |
| `SqlFlow.Delivery.Worker.Host` | A compute node: the CLI's `worker` verb with the module installed, and nothing else on its command line | `osdu-delivery-worker` |
| `SqlFlow.Delivery.Cli.Host` | The `sqlflow` command line (`CliHost.RunAsync` with `DeliveryCliModule`): every SQLFlow verb plus the OSDU verbs | (the binary) |
| `osdu-delivery-mcp` | SQLFlow's MCP server library (`sqlflow-mcp`) composed with the delivery module: its `delivery_*` tools, its documentation and its key census | `osdu-delivery-mcp` |

The GUI is SQLFlow's workbench built with the module's pages registered through the GUI module contract, shipped as
`osdu-delivery-gui`. The deployment page has the images and how they fit together
([deployment](../guides/deployment.md)).

The worker host implies its verb. Passing `worker` yourself is refused:

```text
ERROR  this host is the OSDU Delivery worker; pass the node's options only (the 'worker' verb is implied).
```

The MCP host leaves out SQLFlow's tools that read rows or reach a datasource (`run_query`, `prepare_query`,
`detect_unique_key`, `discover_source` and the rest), because the product's assistant answers from metadata alone
([MCP server](../guides/mcp.md)).

## The extension points it registers through

Nothing in the vendored `sqlflow/` names OSDU. Each point below is generic, and the OSDU side of each lives in `osdu/`
(the full list, with the reason for each, is `docs/sqlflow-changes.md`):

| SQLFlow extension point | What the module registers there |
| --- | --- |
| Host modules (`IControlPlaneModule`, `ICliModule`, `McpModule`) and branding (`ProductBranding`) | The module itself in each host, and the product's name |
| Flow kinds, companion documents and executors | The eight document kinds and six executors above |
| Run parameters (operation, values, payload, requester) | Each kind's operations and payload, carried by a run or a schedule fire |
| Fan-out (`IRunFanOut`) | A large delivery split into parts, each a run of the same flow on a node of its own (`wholeNode`) |
| Compute operations (`IComputeOperation`) | The value check and the removal a node runs |
| Module database (`ModuleDatabase`, `IsReachableOn`) | `OsduDbContext` over the `osdu` schema, migrated, reported and verified with the catalog |
| Catalog sync extension (`ICatalogSyncExtension`) | Mappings, cache definitions and interfaces as rows, and the question whether lineage must be computed again |
| Lineage descriptions (`DescribeLineage`, `DeclaredDataset`, `DeclaredDerivation`, the estate) | OSDU types, cache types, mappings and dimensions as lineage nodes ([lineage](lineage.md)) |
| Search contributor (`ISearchContributor`) | The delivery records category of the control plane's search |
| GUI module contract | Routes, the OSDU navigation group and its sections, per-kind tabs, run panels and trigger fields, the partition picker in the title bar and the `X-Osdu-Partition` header it adds to every call, the key census for the YAML editor |

## What runs where

**The control plane** holds the delivery API under `/api/v1/delivery` on SQLFlow's authorization groups: reads in
`read`, running something (a redelivery, a removal) in `operate`, refining a search term in `author`. All three admit any signed-in user; only the `admin` routes (partitions, central configuration, the ledger's
retention pass, removing a dimension) check a scope ([authentication and identity](authentication-and-identity.md)).
It runs the module's background services: the cache update rollout
(`Osdu:CacheRollout`, on by default), the scheduled target probe (`Osdu:TargetProbe`, off by default), the OSDU data
definitions copy the Templates page browses (`Osdu:SchemaRepository`), and, at start, backfills of the interface and
search term read models and of the ledger's record identity and problem indexes. It also
answers the direct operations above while the request waits. Every delivery, retrieval, cache, assertion, dimension
and inventory run it queues carries the central configuration (`sqlflow config`) when one is set, so a flow's
`${env:NAME}` resolves from one place before it falls back to the node ([control plane](control-plane.md)).

**A node** pulls runs and node tasks over SQLFlow's node protocol, and SQLFlow gives it no catalog connection. The
delivery engine reads and writes the ledger per record while it plans and delivers, so the module opens the catalog's
database, which holds the `osdu` schema, through `SQLFLOW_CATALOG_DB` itself, ideally with a login whose rights cover the
`osdu` schema alone. A node without it refuses to start
(`ERROR  the worker refuses to start: Environment variable 'SQLFLOW_CATALOG_DB' is not set.`), so it never takes a run
([worker](../cli/worker.md)). Every other credential a flow names (the ingestion database, the OSDU endpoint, payload
storage) also resolves on the node.

**The command line** runs a flow locally through the same executors. Its module database is the catalog the command
names (`--db`, else `${env:SQLFLOW_CATALOG_DB}`), which holds the `osdu` schema.

## The databases

| Database | Holds | Reached by |
| --- | --- | --- |
| The catalog database | SQLFlow's catalog (pipelines, runs, schedules, lineage, users) and the `osdu` schema beside it | The control plane and the CLI (`SQLFLOW_CATALOG_DB`), which own both. A node reaches the `osdu` schema alone, through `SQLFLOW_CATALOG_DB` with a login of its own |
| The ingestion database (`OsduData` in this corpus's examples) | The `pre` tables the file flows land and the keyed ingestion tables the OSDU flows read | The flows' own connection references (`${env:OSDU_DATA_DB}`), resolved where each flow runs |

The `osdu` schema always lives in the catalog's database, reached through the catalog's one connection: the module has
no connection of its own. `SQLFLOW_OSDU_DB` and `Osdu:Database:Connection`, which once gave it a database apart, are no
longer read, and a host still given either ignores it ([`sqlflow db`](../cli/db.md#where-it-lives)). The one
place that writes module rows together with the catalog, the repository sync, joins the catalog's transaction, so its
rows commit or roll back with the sync.

The ingestion database a delivery flow reads must allow snapshot isolation (the default read isolation), so a record and
its child rows are read as one moment. A database that does not fails the run with
`Enable it with ALTER DATABASE [OsduData] SET ALLOW_SNAPSHOT_ISOLATION ON, or read each result set as it is committed
with source.incremental.isolation: readCommitted.`

## The osdu schema

Every OSDU table, view and index lives in the `osdu` schema, owned by the module's own EF Core context (`OsduDbContext`),
with its own migration history and its own version. SQLFlow's catalog model never contains an OSDU table, and nothing in
the `osdu` schema holds a foreign key into SQLFlow's tables: OSDU rows carry plain ids of pipelines, runs and repositories.

| Part | Where |
| --- | --- |
| Migration history | `[osdu].[__EFMigrationsHistory]` |
| Version | `[osdu].[SchemaVersion]`, one row: the module version, the last migration applied, when and by whom, and the oldest SQLFlow catalog migration the schema needs |
| The ledger | Submissions, records, attempts, work batches, leases, source watermarks, record identities, artifacts, reversals, purged records ([ledger](ledger.md)) |
| The audit trail | Activities and the records each touched |
| What flows render with | Templates, mapping and interface read models, cache definitions, cache versions and their items, update tags |
| What the other kinds keep | Retrievals, assertion runs and results, dimensions with their values and changes, inventories with their records and removals, removal runs and the deletions they find (kept under the delivery ledgers) |
| Setup | Partitions, the central configuration, search terms and their refinements |
| Dimension tables | `[osdu].[dim_<dimension>]`, created and widened at run time by a dimension flow's build, the one kind of table no migration creates |

`sqlflow db migrate` applies SQLFlow's catalog migrations and then the module's, and `sqlflow db status` reports both,
exiting 2 unless every database is current; `--module osdu` limits either to the module
([db](../cli/db.md), [SQLFlow's db](../../../../sqlflow/docs/reference/cli/db.md)). The hosts verify the schema before
they work: the control plane refuses to run over a module database that is missing, behind or ahead of the build, or
beside a catalog older than the module needs (its readiness carries the refusal, it logs
`The control plane refuses to run: ...` and stops); a node refuses to start over one that is missing, behind or ahead
(`ERROR  the worker refuses to start: ...`), leaving the catalog check to the control plane. The message names the
migrations, for example:

```text
The database of module 'osdu' (schema 'osdu', catalog connection) is behind this build: 1 migration(s) are not applied
('<migration>'). Apply them with 'sqlflow db migrate'.
```

## Principles the design keeps

1. **Traceability over everything.** If it happened to a record, the ledger says so; no path bypasses it.
2. **Secrets never live in documents.** References only, resolved where the flow runs and redacted from every log,
   error and ledger row.
3. **Control plane versus compute.** The control plane decides what runs and owns state; nodes run near the data and
   take their work outbound, so they can sit inside a customer's network.
4. **Generic extension points only.** The vendored SQLFlow never names OSDU; every OSDU behaviour is the module's.
5. **One code path.** The CLI, a node and the control plane run a flow through the same executors and the same engine.

## See also

- [What OSDU Delivery is](overview.md)
- [SQLFlow: architecture and the single execution pathway](../../../../sqlflow/docs/reference/concepts/architecture-and-execution.md)
- [The control plane's OSDU additions](control-plane.md) and [the node](../cli/worker.md)
- [The module database in sqlflow db](../cli/db.md)
- [The ledger](ledger.md)
- [Deploying OSDU Delivery](../guides/deployment.md)
