---
id: delivery-cli-worker
title: "sqlflow worker in OSDU Delivery: what a delivery node needs beyond a SQLFlow node"
type: cli-command
summary: "The OSDU Delivery worker host and what a node needs to deliver: SQLFLOW_OSDU_DB, the flows' credentials, the network guard and the start-up check."
keywords:
  - worker node
  - compute node
  - delivery node
  - sqlflow_osdu_db
  - worker host
  - node credentials
  - module database on a node
  - worker refuses to start
  - private networks
  - worker container image
  - delivery-check-values
  - positional argument refused
  - hosted service failed to start
related:
  - cli-worker
  - delivery-cli-db
  - delivery-concept-environment-variables
  - delivery-cli-config
  - delivery-cli-auth
  - delivery-guide-deployment
  - delivery-concept-run-trace-and-metrics
sourceRefs:
  - osdu/hosts/SqlFlow.Delivery.Worker.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Http/NetworkPolicy.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpClientBuilder.cs
  - osdu/src/SqlFlow.Delivery/Storage/FileStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/CheckValuesOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/src/SqlFlow.Delivery.Telemetry/TelemetryOptions.cs
  - osdu/src/SqlFlow.Delivery.Telemetry/DeliveryTelemetry.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheWarmupService.cs
  - osdu/deploy/docker/worker.Dockerfile
  - osdu/deploy/docker/worker-entrypoint.sh
  - osdu/deploy/k8s/worker-pool.yaml
  - osdu/deploy/bicep/worker.bicep
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseConnections.cs
  - sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseStatus.cs
---

# sqlflow worker in OSDU Delivery

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `worker` is SQLFlow's verb. The
node runtime (the poll, leases and the attempt budget, version pinning and git materialization, the live trace, failure
handling, the drain on shutdown) and every option (`--url`, `--token`, `--pool`, `--poll-seconds`, `--drain-seconds`)
are documented in [sqlflow worker](../../../../sqlflow/docs/reference/cli/worker.md). This page covers what an OSDU
Delivery node needs that a plain SQLFlow node does not.

## Synopsis

```bash
sqlflow worker --url <control-plane> [--token <ref>] [--pool a,b] [--poll-seconds N] [--drain-seconds N] [-v]
SqlFlow.Delivery.Worker.Host [--url <control-plane>] [--token <ref>] [--pool a,b] [--poll-seconds N] [--drain-seconds N] [-v]
```

## The worker host

In a deployment the node is `osdu/hosts/SqlFlow.Delivery.Worker.Host`: the CLI's `worker` verb with the OSDU module
installed and nothing else on the command line. It takes the node's options only; the verb is implied, and any
positional argument is refused, the verb itself or anything else, so the image can never behave as another program or
ignore an argument silently. An option's value is never taken for one: `--pool worker` serves the pool named `worker`.

```text
ERROR  this host is the OSDU Delivery worker; pass the node's options only (the 'worker' verb is implied).
ERROR  this host is the OSDU Delivery worker and takes the node's options only (--url, --token, --pool, --poll-seconds, --drain-seconds, -v); 'run' is not one of them.
```

`sqlflow worker` run from the OSDU Delivery CLI is the same node. Either way the module registers on the node's
services, so a run of a delivery, retrieval, cache, assertion, dimension or inventory flow executes exactly as it does
with `sqlflow run`, a removal of records from OSDU included, which is a `remove` run of the delivery flow. One compute
task of the module also drains on nodes: a value check of a mapping across a scope (`delivery-check-values`). What a
person asks and waits on (a probe of a target, a record read back, a preview, a scope's values, the explorer) is not a
node task: the control plane runs it while the request waits.

## The module database: SQLFLOW_OSDU_DB

The delivery engine reads and writes the `osdu` schema record by record while it plans and delivers: that is what makes
every attempt and outcome traceable. A node opens no catalog connection, so on a node the module database is always the
reference `${env:SQLFLOW_OSDU_DB}`, whatever the catalog database is. The variable holds the connection string of the
database that holds the `osdu` schema: the catalog's own database in the default estate, or the module's own.

Before it polls for work, the node verifies that database against its build, as the control plane does at startup (the
catalog migration the module needs is not checked here, since a node has no catalog):

```text
OK   module 'osdu' (schema 'osdu', own connection): current at '<last-migration>' (N migration(s) applied, 0 pending; build version <version>, recorded version <version>, catalog migration '<catalog-migration>' not checked).
```

A node without the variable, or over a module database that is missing, behind, ahead or diverged, does not start:

```text
ERROR  the worker refuses to start: Environment variable 'SQLFLOW_OSDU_DB' is not set.
ERROR  the worker refuses to start: The database of module 'osdu' (schema 'osdu', own connection) is behind this build: 2 migration(s) are not applied (...). Apply them with 'sqlflow db migrate'.
```

Migrating is the control plane's or `sqlflow db migrate`'s job, never a node's (see [sqlflow db](db.md)).

## What else a node resolves

Credentials are resolved on the node that runs the flow. Every `${env:...}` reference a pool's flows name has to resolve
there, unless the central configuration supplies it with the run ([sqlflow config](config.md)), as it supplies a
partition's values from the control plane's configuration
([configured partitions](../concepts/partitions.md#configured-partitions)):

| What | Typical reference | Used for |
| --- | --- | --- |
| The data database | `${env:OSDU_DATA_DB}` | Reading the keyed ingestion tables the OSDU flow delivers from, and a cache flow's lookup tables. |
| The metadata database | `${env:SQLFLOW_CATALOG_DB}` | Only a flow that reads the `osdu` schema's dimension views; the node itself never opens it. |
| The OSDU endpoint and its credentials | `${env:OSDU_URL}`, `${env:OSDU_TOKEN_URL}`, `${env:OSDU_CLIENT_ID}`, `${env:OSDU_CLIENT_SECRET}`, `${env:OSDU_SCOPE}` | The flow's `target` (or a cache, retrieval or assertion flow's `source`) and its token request. |
| The destination parameters | `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}`, `${env:OSDU_LEGAL_TAG}` | The mapping parameters the OSDU flow kind supplies when a flow does not set them. A flow bound to a partition takes `dataPartition` from its partition, or from its `data-partition-id` header, instead. |
| Azure storage | `SQLFLOW_AZURE_AUTH` and the `AZURE_*` family | Reading payload files and writing work batches and retrieval output on `abfss://`, `wasbs://` or blob and dfs `https://` locations, and resolving `${keyvault:...}` references. See [sqlflow auth](auth.md). |

The names in the middle column are the generic estate's; a flow can name any variable. A `${keyvault:...}` reference is
resolved with the node's own Azure identity.

## The network guard

The engine opens a connection only to an address its network policy reaches, checked for the address a URL names, every
address a host name resolves to when the connection opens, and every redirect. These settings are read from the node's
environment (and from the CLI's and control plane's, which run the engine too):

| Variable | Effect |
| --- | --- |
| `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` | The private ranges the node may reach, as CIDR ranges or single addresses separated by commas, semicolons or white space (`10.20.0.0/16,fd12:3456::/48`): an OSDU, a storage account or a proxy behind a private endpoint. Empty by default, which reaches public addresses only. |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | `true` lets a flow target a loopback address (a local OSDU stub, tests). Off by default. |
| `SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS` | `true` lets a flow that declares `reliability.verifyTls: false` run on this node. Off by default, so a repository document cannot take a node off TLS on its own. |

Link-local and cloud metadata addresses and the Azure wireserver address are never reachable, whatever the list says.
The variables are described with the rest in [Environment variables](../concepts/environment-variables.md).

## Metrics

A node takes its metrics export settings from its environment: `OSDU_TELEMETRY_EXPORTER` (`none`, `otlp`,
`azuremonitor` or `console`) and `OSDU_TELEMETRY_OTLP_ENDPOINT`, `_OTLP_PROTOCOL`, `_OTLP_HEADERS`,
`_AZURE_MONITOR_CONNECTION`, `_EXPORT_SECONDS`, `_SERVICE_NAME` and `_SERVICE_INSTANCE`. They are read and checked when the
node starts; a value the setting cannot take (an exporter name that does not exist, a non-numeric export interval, a
literal where a reference belongs) stops the node with
`ERROR  CLI module 'osdu' failed to configure its services: <reason>`.

The export is a hosted service of the node's services: it starts with the node, before the node takes any work, and
stops and flushes after the drain, so the last measurements leave the process. The node logs where its metrics go
(`Metrics on the meter SqlFlow.Delivery are exported to <Exporter> every <n>s as service <name>.`, or that they are
exported nowhere). A hosted service that fails to start stops the node before it takes work:
`ERROR  the worker refuses to start: hosted service <type> failed to start: <reason>`. What the meters publish is in
[Run trace and metrics](../concepts/run-trace-and-metrics.md).

## Cache warm-up

A run renders against the current version of its partition's cache, which it needs whole in memory. A node reads the
current version of every partition's cache when it starts, and every version a refresh makes current after that, so the
first run after a start or a refresh does not wait while the version is read
([the partition cache](../concepts/partition-cache.md#versions)). It looks for a newer version every
`OSDU_CACHE_WARMUP_SECONDS` (default `60`, 5 to 3600); `OSDU_CACHE_WARMUP=false` turns it off, for a node whose pool runs
no delivery and should not hold a cache. A value the setting cannot take stops the node with
`ERROR  CLI module 'osdu' failed to configure its services: <reason>`. The warm-up does not hold up the node's start: the
node takes work while the version is read, and a run that needs it meanwhile waits on the same read.

## Container image

`osdu/deploy/docker/worker.Dockerfile` packages the worker host; build it from the repository root, since the context
spans `osdu/` and `sqlflow/`:

```bash
docker build -f osdu/deploy/docker/worker.Dockerfile -t osdu-delivery-worker:latest .
docker run -d \
  -e SQLFLOW_URL="https://sqlflow.example.com" \
  -e SQLFLOW_TOKEN="$NODE_TOKEN" \
  -e SQLFLOW_OSDU_DB="$OSDU_MODULE_CONNECTION" \
  -e OSDU_DATA_DB="$INGESTION_CONNECTION" \
  -e SQLFLOW_WORKER_POOL="osdu" \
  osdu-delivery-worker:latest
```

The entrypoint composes the node's options from `SQLFLOW_WORKER_POOL`, `SQLFLOW_WORKER_POLL_SECONDS` and
`SQLFLOW_WORKER_DRAIN_SECONDS` (all optional), so the control plane URL, the token and the database connections never
appear on the command line or in `ps` output. The container exposes no ports. The shipped Kubernetes manifest
(`osdu/deploy/k8s/worker-pool.yaml`) mounts `SQLFLOW_OSDU_DB` from a secret and, like the Azure template
(`osdu/deploy/bicep/worker.bicep`), sets the termination grace period to 600 seconds, above the default 540-second
drain. The Azure template also sets `SQLFLOW_AZURE_AUTH=mi` with the app's managed identity, so `${keyvault:...}`
references and storage resolve as that identity.

## Exit behavior

Beyond SQLFlow's (0 after a clean drain, 1 when the URL or token is missing):

| Condition | Exit code | Output |
| --- | --- | --- |
| `SQLFLOW_OSDU_DB` unset, or the module database is not current | 1 | `ERROR  the worker refuses to start: <reason>` before the node polls |
| An `OSDU_TELEMETRY_*` value the setting cannot take | 1 | `ERROR  CLI module 'osdu' failed to configure its services: <reason>` |
| The worker host given the `worker` verb, or any other positional argument | 1 | `ERROR  this host is the OSDU Delivery worker; ...` |
| A hosted service of the node (the metrics export) fails to start | 1 | `ERROR  the worker refuses to start: hosted service <type> failed to start: <reason>` before the node polls |

## See also

- [sqlflow worker](../../../../sqlflow/docs/reference/cli/worker.md): the node runtime, its options and the drain.
- [sqlflow db](db.md): the module database and its migrations.
- [sqlflow config](config.md): values the control plane hands to runs instead of node variables.
- [sqlflow auth](auth.md): checking the node's Azure identity.
- [Environment variables](../concepts/environment-variables.md): every variable a node reads.
- [Deploying OSDU Delivery](../guides/deployment.md): the images, the tiers and the node token.
