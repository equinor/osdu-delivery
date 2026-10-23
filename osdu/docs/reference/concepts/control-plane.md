---
id: delivery-concept-control-plane
title: "The control plane in OSDU Delivery: the module's background services, Osdu configuration and in-process reads"
type: concept
summary: "What the OSDU module adds to SQLFlow's control plane: its database, configured partitions, background services, Osdu:* settings and in-process reads."
keywords:
  - control plane
  - background services
  - cache rollout
  - target probe
  - osdu data definitions
  - osdu:database:connection
  - "osdu:partitions"
  - configured partitions
  - osdu-partitions check
  - osdu:cacherollout
  - osdu:targetprobe
  - osdu:schemarepository
  - central configuration
  - in-process reads
  - module database
  - controlplane host
related:
  - concept-control-plane
  - delivery-concept-api
  - delivery-concept-environment-variables
  - delivery-concept-run-trace-and-metrics
  - delivery-concept-architecture
  - delivery-guide-deployment
  - delivery-cli-control-plane
sourceRefs:
  - osdu/hosts/SqlFlow.Delivery.ControlPlane.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantAccess.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheUpdateRolloutService.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheWarmupService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ScheduledTargetProbeService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/DataDefinitionsWarmupService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/SearchTermCatalogRefreshService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RecordIdentityBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RecordProblemBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CentralConfigurationStep.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/PartitionReadiness.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RepositoryResync.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DirectOperationRunner.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/TargetClients.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneModuleServices.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabase.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseStatus.cs
---

# The control plane: what OSDU Delivery adds

OSDU Delivery's control plane is SQLFlow's control plane with the OSDU module composed in. The host
(`osdu/hosts/SqlFlow.Delivery.ControlPlane.Host`) is one line: SQLFlow's `ControlPlaneHost` run with the product's branding
and `DeliveryControlPlaneModule`. The API surface, authentication and policies, health probes, rate limiting, CORS and
proxy handling, the `ControlPlane` configuration section, first-run bootstrap, managed git sync, the scheduler, the
dispatcher and the node protocol are SQLFlow's, unchanged, and documented in
[SQLFlow's control plane page](../../../../sqlflow/docs/reference/concepts/control-plane.md). That includes running a
single replica, because the run queue has one owner.

This page covers what the module adds: its database, the partitions its configuration names, the reads it answers in
process, the configuration it attaches to runs, its background services and its `Osdu:*` settings. The routes
themselves are listed in [the delivery API](api.md).

## What the module registers

| Addition | What it does |
| --- | --- |
| The `osdu` module database | The ledger, mappings, templates, caches, partitions and the central configuration, in schema `osdu` with its own migrations and version. Bootstrap migrates it after SQLFlow's catalog, and readiness stays red until it verifies against this build. |
| The configured partitions | The OSDU partitions the deployment's configuration names (`Osdu:Partitions`), registered with the values their runs resolve once the module database verifies, and changed only in the configuration ([configured partitions](partitions.md#configured-partitions)). |
| The `osdu-partitions` check | Reported at `/health/startup`, beside the start-up pass, not by readiness: unhealthy until the configured partitions are written, and then while the registry holds no partition, since OSDU Delivery works in partitions and does nothing without one, while the API keeps serving so a person can read why and register one. The control plane logs why. A partition registered by hand completes it without a restart. |
| The OSDU flow kinds | The delivery, retrieval, cache, assertion, dimension and inventory kinds, their executors and their compute operations, so the repository sync projects them as pipelines and the dispatcher hands their runs to nodes. |
| The delivery routes | Every route under `/api/v1/delivery`, mapped into SQLFlow's `read`, `operate` and `author` groups, which all admit any signed-in user, with the admin routes on the `admin` policy, the only one that checks a scope ([authentication and identity](authentication-and-identity.md)). |
| The central configuration | The values set for no partition attached to every run of every kind and every compute task as it is queued; each partition's own values attached to every delivery, retrieval, cache, assertion, dimension and inventory run besides (below). |
| A `records` search category | The delivery records category of SQLFlow's `GET /api/v1/search/all`, answered from the ledger's indexed lookup. |
| The assistant's tools | The product's MCP server decides them: its instructions are the chat's rules, and a run's session lists the tools of the conversation's mode, SQLFlow's metadata tools and the `delivery_*` tools in estate mode, the semantic layer and the query tools in data mode ([the MCP server](../guides/mcp.md)). The module sets no list of its own; `ControlPlane:Assistant:Mcp:AllowedTools` narrows the tools when a deployment wants fewer. |
| The assistant's routes | A chat run calls the control plane with a token delegated for that run, and a call the MCP server marks as the assistant's is held the same way whatever its credential; SQLFlow refuses both on every route not opened to an assistant run. The module opens exactly the delivery routes its readers read, and the four mapping-builder previews (`DeliveryAssistantAccess`); every other delivery route, an operator action's included, stays closed to it ([authentication and identity](authentication-and-identity.md)). |
| The OSDU data definitions | A local copy of the Open Group's public schema repository, which the Templates page browses and downloads template files from. |
| Metrics export | Where the module's meter goes, when a deployment names an exporter ([run trace and metrics](run-trace-and-metrics.md)). |

### The module database and its connection

The module database is the catalog's: the control plane opens the `osdu` schema on the catalog's own connection
(`ControlPlane:Catalog:ConnectionReference`, by default `${env:SQLFLOW_CATALOG_DB}`), one metadata database with the
`osdu` schema beside SQLFlow's catalog schema. The control plane owns both: it migrates and verifies the catalog and then
the module at startup, and reads and writes the ledger, the mappings, the templates and the caches there.

The module has no connection of its own. A control plane started with `Osdu:Database:Connection` or `SQLFLOW_OSDU_DB`,
which once gave it one, stops, naming the setting:
`Osdu:Database:Connection is no longer a setting; the osdu schema lives in the catalog's database, which SQLFLOW_CATALOG_DB names. Remove it. ...`
A database that is missing, behind this build, ahead of it or diverged, or a catalog without the migration the module
needs, also stops it with a message naming the migrations, for example
`The database of ... is behind this build: 2 migration(s) are not applied (...). Apply them with 'sqlflow db migrate'.`
Which database holds the schema is chosen before the first migrate; see [sqlflow db](../cli/db.md).

## Reads a person waits on run here

The control plane never delivers: runs, value checks and removals go to nodes. What a person asks of OSDU or the ingestion
tables and waits on runs in the control plane instead, as the request:

- a probe of a flow's target (the one of these recorded in the audit trail, as a `probe` activity under the caller), a
  record read back, any record read by id, a record checked against its schema;
- a record's rows read from the ingestion tables, a preview, the values a scope parameter can take;
- every explorer read (types, search, fields, read, validate, referenced-by, dimension keys).

Each is given exactly what a node would be given (the flow file where the repository was synced, the interface, the
partition, who asked) and the central configuration of the flow's partition, so it reaches the same target with the same
credentials. That is why the control plane is given the flows' `${env:...}` references and
`SQLFLOW_DELIVERY_PRIVATE_NETWORKS` as a node is ([environment variables](environment-variables.md)).

The connection to a target is kept between reads: one per target (the flow and partition, endpoint, credentials,
headers, reliability and configuration together), at most 64 targets, each retired after 10 minutes unused or 30 minutes
in all, and at once on the 401 a rotated secret draws. A read is a person's, not a run's: at most two attempts, a backoff
of at most two seconds, no wait on a `Retry-After`, and an answer within 60 seconds. A failure answers 502 when OSDU or the
ingestion tables refused or failed, 504 when no answer came in time, and 422 when the flow cannot serve the request, with
every resolved secret redacted.

## The central configuration on every run

The values set for no partition reach every run of every kind and every compute task: the module registers the central
configuration as the control plane's reference source, so SQLFlow's dispatcher queues each one carrying the values its
repository resolves to, a pre-ingestion or ingestion flow's connections as much as a delivery's platform
([values the control plane supplies with a run](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md#values-the-control-plane-supplies-with-a-run)). A node therefore needs no
environment of its own for what the estate's flows read: it resolves a supplied `${keyvault:...}` value with its own
identity.

The module also wraps SQLFlow's run dispatcher. Every run of a delivery, retrieval, cache, assertion, dimension or
inventory flow (an inventory's remove run included) reaches the queue through it, whether an endpoint, a schedule fire, a
fan-out group or `sqlflow trigger` put it there, and is queued carrying, besides, each partition's own values. A run bound
to a partition resolves `${env:NAME}` from the repository's value for that partition, then the control plane's value for
it, then the repository's value, then the control plane's, and only then from the node's own environment. A compute task
of the module carries the values of the one partition it names over the values set for none. The run's trace names where
each reference took its value (the configuration the control plane supplied, or the node's environment), never the value.

A run whose payload already carries partition values keeps them. When nothing is configured, nothing is attached, and a
run resolves every reference from its node's environment. A configuration a run cannot carry (more than 64 values in one
scope, values for more than 64 partitions, more than a run's payload holds, or a credential written out) refuses the run
with a message naming what to put right; nothing is queued. A partition's values come from the control plane's
configuration (`Osdu:Partitions:<partition>:Properties`, below), and any property can be set with
[sqlflow config](../cli/config.md) or the API; a property holds a value or a reference, never a secret.

## Background services

| Service | Runs | What it does |
| --- | --- | --- |
| Central configuration (a step of the start-up pass, `osdu-configuration`) | At every start, right after the module database verifies | Writes the partitions `Osdu:Partitions` names into the registry, with their default and their values, and the values `Osdu:Properties` sets for no partition, in one transaction: registers the missing ones, takes over the named ones set by hand, and releases the ones the configuration no longer names; it removes nothing. A partition it registers makes every enabled repository due for a sync. Retried with the start-up pass's backoff until it completes; `/health/startup`, with its `osdu-partitions` check, stays red until then, while the API keeps serving ([configured partitions](partitions.md#configured-partitions)). |
| Cache update rollout | Unless `Osdu:CacheRollout:Enabled` is false | Carries approved cache changes to the records built from the old value: every `PollSeconds` it marks at most `BatchesPerPass` batches of `BatchSize` records for redelivery, in delivery-key order from each change's cursor. The marking is metadata only, a restart resumes where it stopped, and marking a record twice is the same as once. |
| Cache warm-up | Unless `Osdu:CacheWarmup:Enabled` is false | Reads the current version of every partition's cache into memory at start, then looks every `PollSeconds` for a version a refresh made current and reads that too, so a record's Render, a flow's Preview and the runs a single-node control plane runs itself find it there rather than waiting while it is read. A version that cannot be read is logged once and left until a newer one is current. Every node runs the same service ([worker](../cli/worker.md#cache-warm-up)). |
| Scheduled target probe | Only when `Osdu:TargetProbe:Enabled` is true | Probes every interface of every active delivery flow (or the `Pipelines` named) every `IntervalMinutes`, four at a time, through the same path as the GUI's Probe target, and records each as a `probe` activity by `service:schedule` ([watching the targets](run-trace-and-metrics.md#watching-the-targets)). The button's probe, the API's and the MCP tool's take the same path and are recorded the same way, under the caller (`user:<subject>`). |
| Data definitions warmup | When `Osdu:SchemaRepository:WarmOnStart` is true (the default) | At start, reads the release list and downloads the newest release when it is not on disk. A failure is not fatal; the Templates page asks again. |
| Interface catalog backfill | Once at start | Describes the interfaces of delivery pipelines a repository sync has not described yet, so a record's page finds its flow without waiting for the next sync. Retried every 30 seconds until a pass completes. |
| Search term refresh | Once at start | Writes the search terms of every repository with delivery flows again, keeping what people made of them; a repository with no delivery flow left loses its terms. Retried every 30 seconds until a pass completes. |
| Record identity backfill | At start, then every 6 hours | Fills the identity index for records the ledger held before the index existed, 500 records a page with a 250 ms pause between pages. |
| Record issue backfill | At start, then every 10 minutes | Sorts blocked records the ledger holds no issue for into their issues, 500 a page with a 250 ms pause. |

Every service runs on each replica that starts. The rollout and the backfills are written so two replicas at once cost
duplicated work and never a wrong result; the warm-up fills each replica's own memory.

## Configuration

The module binds its own sections beside SQLFlow's `ControlPlane` section. Environment variables use the usual
`Osdu__Section__Key` form. Each value is validated when the host composes and again on start, and a bad one stops the host
with a message naming it, for example
`Osdu:TargetProbe:IntervalMinutes must be between 5 and 1440 (a day): every pass costs a token exchange and a request against a live OSDU for each interface of each active delivery flow.`

| Key | Default | Notes |
| --- | --- | --- |
| `Osdu:Partitions:<data-partition-id>:Default` | `false` | `true` on the partition a run that names none runs in; exactly one of several, and one partition alone is the default without it. |
| `Osdu:Partitions:<data-partition-id>:Description` | none | What the partition is for; at most 400 characters. |
| `Osdu:Partitions:<data-partition-id>:Properties:<NAME>` | none | A value the runs bound to the partition resolve `${env:NAME}` with (`OSDU_URL`, `OSDU_CLIENT_ID`, ...); at most 64 partitions and 64 values each. A name ending in `SECRET`, `PASSWORD` or `TOKEN`, or naming an API key, takes only a `${keyvault:...}` or `${env:...}` reference, and a refusal never repeats the value ([configured partitions](partitions.md#configured-partitions)). |
| `Osdu:CacheRollout:Enabled` | `true` | `false` leaves approved changes waiting; the GUI still shows what is queued. |
| `Osdu:CacheRollout:BatchSize` | `5000` | Records marked per batch; positive. |
| `Osdu:CacheRollout:BatchesPerPass` | `2` | Batches per tick across all approved changes; positive. |
| `Osdu:CacheRollout:PollSeconds` | `60` | Seconds between ticks; positive. |
| `Osdu:CacheWarmup:Enabled` | `true` | `false` leaves each cache version to be read by the first render that needs it. |
| `Osdu:CacheWarmup:PollSeconds` | `60` | Seconds between the looks for a newer current version, 5 to 3600; each look is one query. |
| `Osdu:TargetProbe:Enabled` | `false` | Each pass costs a token exchange and a live request per interface. |
| `Osdu:TargetProbe:IntervalMinutes` | `15` | 5 to 1440. |
| `Osdu:TargetProbe:MaxPerPass` | `200` | Interfaces probed in one pass, 1 to 1000. |
| `Osdu:TargetProbe:Pipelines` | empty | Delivery pipeline names, comma separated; empty probes every active delivery pipeline. A name no active delivery pipeline carries is logged. |
| `Osdu:SchemaRepository:ApiUrl` | `https://community.opengroup.org/api/v4/projects/osdu%2Fdata%2Fdata-definitions/` | The GitLab API of the data definitions; point it and `WebUrl` at a mirror when the control plane cannot reach the Open Group. |
| `Osdu:SchemaRepository:WebUrl` | `https://community.opengroup.org/osdu/data/data-definitions/` | Where schema file links point. |
| `Osdu:SchemaRepository:CacheDirectory` | `<temp>/sqlflow/osdu-data-definitions` | The local copy. |
| `Osdu:SchemaRepository:RefreshMinutes` | `1440` | How old the release list may get before it is read again; 1 to 10080. A sync reads it at once. |
| `Osdu:SchemaRepository:DownloadTimeoutMinutes` | `15` | One release's download; 1 to 240. |
| `Osdu:SchemaRepository:WarmOnStart` | `true` | Runs the warmup above. |
| `Osdu:Telemetry:*` | exporter `None` | Where the metrics go ([run trace and metrics](run-trace-and-metrics.md#where-the-metrics-go)). |

```text
# The control plane's environment: the partition it delivers to, with the values its runs resolve.
Osdu__Partitions__dev__Description=Development platform
Osdu__Partitions__dev__Properties__OSDU_URL=https://osdu-dev.example.com
Osdu__Partitions__dev__Properties__OSDU_TOKEN_URL=https://login.example.com/oauth2/token
Osdu__Partitions__dev__Properties__OSDU_SCOPE=https://osdu-dev.example.com/.default
Osdu__Partitions__dev__Properties__OSDU_CLIENT_ID=welldb-delivery
Osdu__Partitions__dev__Properties__OSDU_CLIENT_SECRET=${keyvault:welldb-vault/osdu-dev-client-secret}

# The scheduled probe on for two flows, every 30 minutes.
Osdu__TargetProbe__Enabled=true
Osdu__TargetProbe__IntervalMinutes=30
Osdu__TargetProbe__Pipelines=welldb-wellbore-03-delivery,welldb-welllog-03-delivery
```

## See also

- [The delivery API](api.md): every route the module adds.
- [Configured partitions](partitions.md#configured-partitions): what `Osdu:Partitions` registers, and what it refuses elsewhere.
- [Environment variables](environment-variables.md): what each tier is given.
- [Deploying OSDU Delivery](../guides/deployment.md)
- [sqlflow control-plane verbs for OSDU flows](../cli/control-plane.md)
- [SQLFlow's control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md)
