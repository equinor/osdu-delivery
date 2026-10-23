---
id: delivery-wiki-0026-one-database-connection
title: "Decision 0026: The osdu schema lives in the catalog's database, reached through its one connection"
type: decision
summary: "Why the module has no database connection of its own, why a host ignores the retired SQLFLOW_OSDU_DB, and why the control plane owns the osdu schema."
keywords:
  - one database connection
  - SQLFLOW_OSDU_DB retired
  - SQLFLOW_CATALOG_DB
  - module database
  - osdu schema in the catalog database
  - node connection
  - control plane owns the osdu schema
referenceRefs:
  - delivery-cli-db
  - delivery-cli-worker
  - delivery-concept-architecture
  - delivery-concept-control-plane
  - delivery-concept-environment-variables
  - delivery-guide-deployment
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/ManagedWorkers/ProcessWorkerLauncher.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/deploy/terraform/main.tf
  - osdu/deploy/terraform/variables.tf
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/k8s/worker-pool.yaml
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseConnections.cs
updated: 2026-10-23
---
# 0026: The osdu schema lives in the catalog's database, reached through its one connection

Status: accepted. Documented in [`sqlflow db`](../../reference/cli/db.md#where-it-lives), [the worker](../../reference/cli/worker.md)
and [environment variables](../../reference/concepts/environment-variables.md).

## Context

An estate has one metadata database, `OsduDelivery`, holding SQLFlow's catalog schema and the module's `osdu` schema
beside it. Every shipped deployment kept the two there, yet each was given two connections to it:

- The control plane read the catalog through `SQLFLOW_CATALOG_DB` and could be given a second reference for the module,
  `Osdu:Database:Connection` or `SQLFLOW_OSDU_DB`. The Terraform and Bicep deployments wrote a second Key Vault secret,
  `osdu-delivery-osdu-db`, holding the same connection string as `osdu-delivery-catalog-db`, and mounted both.
- A worker node was given `SQLFLOW_OSDU_DB` for the ledger. The compose, Kubernetes and Radix layouts also gave it
  `SQLFLOW_CATALOG_DB`, for the flows that read the dimension views, with the same connection string.
- The deployments could put the `osdu` schema in a database of its own (`osdu_database_name`, `osduDatabaseName`), for
  an Azure SQL estate that wants the two apart, since no statement there reaches across two databases. No shipped
  environment did. The repository sync carried a second path for that shape, committing the module's rows on their own
  connection instead of in the sync's transaction.

The second connection had two roots. One was that option of a separate database. The other was SQLFlow's node: SQLFlow
gives a worker no catalog connection (`ModuleDatabaseConnections.WithoutCatalog`), so a module database a node opens
needs a reference of its own, and the module took a new name for it rather than the catalog's.

The human, looking at a colleague's deployment of three database connections: there is one catalog database, so there
should be no duplicate connections, and every hard-coded lookup of the second one should go.

## Decision

- **The `osdu` schema lives in the catalog's database, and every host reaches it through the catalog's connection.** The
  control plane and a command open it on the connection they open the catalog on. A node, which SQLFlow still gives no
  catalog connection, opens it through `${env:SQLFLOW_CATALOG_DB}` itself; the managed-worker launcher hands a local
  worker that variable as the control plane resolved it. A node's login keeps its grants on the `osdu` schema alone.
- **The settings of a second connection are not read, and a host still given one ignores it.** `SQLFLOW_OSDU_DB` and
  `Osdu:Database:Connection` neither change where the module's database is nor stop a host from starting, and the
  Terraform's `osdu_database_name` and the Bicep's `osduDatabaseName` are accepted and ignored, so an environment file
  or a parameters file that still sets them deploys unchanged. The human, on the first refusal: a deployed application's
  environment cannot always be changed, so an unused variable has to be handled gracefully.
- **A node given `SQLFLOW_OSDU_DB` alone reads the catalog's connection from it, as a temporary workaround.** A node
  given `SQLFLOW_CATALOG_DB` reads that and ignores the old variable. This was first rejected, the human having asked
  that the old variable be ignored rather than used, until the dev workers deployed from the fork crash-looped: their
  image rolled forward to this decision while their settings, which an administrator deployed and the human cannot
  change, still gave `SQLFLOW_OSDU_DB` alone. Every such deployment set it to the catalog's connection, so the node reads
  the same database either way, and it warns as it starts. The Terraform is left as it is: its next apply gives the
  nodes `SQLFLOW_CATALOG_DB`.
- **The control plane owns the `osdu` schema.** It migrates and verifies the catalog and then the module at startup,
  serves the ledger, the mappings, the templates and the caches to the GUI and the API, and reads each data database for
  the reads a person waits on. That access is the module's requirement, not SQLFlow's, and the one connection carries it.
- **One path for the repository sync.** The module's rows always join the sync's transaction on the catalog's connection.
- **The message a host without the database gives is one sentence**, `OsduModuleDatabase.StartWithCatalogConnection`,
  which every operation that needs the database completes its message with, in place of the copies that named the
  retired settings.

## Alternatives rejected

- **Refusing the retired settings at startup**, which this decision first did, in the words SQLFlow uses for its own
  retired settings, so that an estate whose `osdu` schema really sat in a database of its own would not start on an empty
  ledger in the catalog's. Every shipped deployment pointed the variable at the catalog's own database, so the refusal
  guarded nothing there and stopped deployments whose settings their operators cannot change; the human chose to ignore
  the settings instead. The case it guarded is left to the upgrade: such an estate brings its `osdu` schema into the
  catalog's database first.
- **Keeping `SQLFLOW_OSDU_DB` as an optional override**: it was already optional, and every deployment still wrote and
  mounted it, pointing at the catalog's database. Two names for one database is the duplicate the human asked to remove.
- **Keeping a database of the module's own as an option for Azure SQL**: no estate used it, and it alone kept the second
  connection and the sync's second path. The two schemas still keep their own contexts, migration histories and
  versions, with no foreign key between them, so the boundary between SQLFlow and the module holds in one database.

## Consequences

- An estate has one connection string for its metadata database: one Key Vault secret, one variable on every tier.
- A Terraform state holding a separate `osdu` database forgets it rather than destroying it (a `removed` block); that
  database's `osdu` schema is brought into the catalog's database by hand before the hosts start again.
- A node's start-up check describes the module as `own connection`, SQLFlow's word for a module database opened through
  a reference, though the reference is the catalog's.
- A developer's `.sqlflow/env` still naming `SQLFLOW_OSDU_DB` starts as before; `dev.bat check` lists it among the names
  the template does not set.
