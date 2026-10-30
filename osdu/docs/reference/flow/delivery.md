---
id: delivery-flow-delivery
title: "Delivery flow (flowType: delivery): deliver ingestion table rows to OSDU through a pinned mapping"
type: flow-reference
summary: "The flowType: delivery document: source tables and keys, the pinned mapping, target, partitions, cache steps, reliability, parallelism, stop rules, schedules."
keywords:
  - delivery flow
  - "flowtype delivery"
  - ingestion table
  - silver table
  - source.record
  - primarykey
  - maxrecords
  - smoke test a flow
  - first records of the scope
  - render.mapping
  - render.parameters
  - target.endpoint
  - oauth2clientcredentials
  - reliability
  - parallelism
  - concurrency
  - fan-out
  - schedule values
  - several values of a parameter
  - scope parameter list
  - deliver to osdu
  - literal secret refused
  - credential header
  - provenance columns
  - cache.before
  - cache.after
  - update the cache before a pipeline
  - update the cache after a pipeline
  - cache steps
yamlPath: "(root, flowType: delivery)"
related:
  - delivery-flow-delivery-record-table
  - delivery-flow-interfaces
  - delivery-flow-enrich
  - delivery-flow-routes
  - delivery-flow-mapping
  - delivery-concept-change-detection
  - delivery-concept-partitions
  - delivery-concept-preflight
  - flow-ing
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/WorkflowMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLayout.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowTelemetry.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowCacheSteps.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheSteps.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.FanOut.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionConnection.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceBindings.cs
  - osdu/src/SqlFlow.Delivery/Validation/PayloadLocations.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/FailureGuard.cs
  - osdu/src/SqlFlow.Delivery/Http/AuthResolver.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpClientBuilder.cs
  - osdu/src/SqlFlow.Delivery/Http/UrlGuard.cs
  - sqlflow/src/SqlFlow.Core/Engine/SourceLocations.cs
  - sqlflow/src/SqlFlow.Yaml/YamlDocumentLoader.cs
---

# Delivery flow (flowType: delivery): deliver ingestion table rows to OSDU through a pinned mapping

A `flowType: delivery` document delivers records into an OSDU partition. It reads the keyed ingestion tables that
SQLFlow's own pre-ingestion and ingestion flows load ([ing flows](../../../../sqlflow/docs/reference/flow/ing.md)),
renders each record row with a pinned [mapping](mapping.md) into an OSDU record of the mapping's kind, sends what
rendered differently from what OSDU holds, and records every step in the ledger. Use it once the source's rows sit in a
keyed table: one row per record, a key that identifies it, and SQLFlow's system columns saying when it last changed.

A delivery document comes in two forms. The single form, described here, delivers one OSDU kind. The interface form
lists several kinds of one source under `interfaces`, sharing the connection, target and settings
([a source with interfaces](interfaces.md)). Both load into one model and run on one engine; every key on this page
means the same in both, with the differences named where they apply.

The loader is strict: an unknown key, a misspelled key or a key written twice is a load error naming the line, unlike
SQLFlow's own flow kinds, which ignore unknown keys. Run `sqlflow validate` after every edit; it loads the document
offline, with no catalog, database or OSDU ([cli-validate](../../../../sqlflow/docs/reference/cli/validate.md)).

## Minimal working example

The wellbore flow, `flows/welldb-wellbore-03-delivery.yaml`, as
[getting started](../guides/getting-started.md) builds it:

```yaml
flowType: delivery
name: welldb-wellbore-03-delivery
batch: welldb

partitions: [dev]

source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.Wellbore
    key: [wellbore_id]
    primaryKey: RecId
  lastModified: update_date
  work: ../.work/wellbore

render:
  mapping: Wellbore@1.0.0

target:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}
  protocol: storage
```

```bash
sqlflow validate flows/welldb-wellbore-03-delivery.yaml
sqlflow check flows/welldb-wellbore-03-delivery.yaml
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation plan
sqlflow run flows/welldb-wellbore-03-delivery.yaml
```

`validate` prints `OK  'welldb-wellbore-03-delivery' is valid (delivery: OsduData.silver.Wellbore -> ${env:OSDU_URL}).`
[`sqlflow check`](../cli/check.md) then resolves the mapping against the template its template folders hold and the partition's cache, and a
`plan` run reports what a delivery would send without sending it ([running an OSDU flow](../cli/run.md)).

## A fuller example

The well log flow, `flows/welldb-welllog-03-delivery.yaml`: a child table, a flow parameter that scopes the run, a
business version column, a fan-out, a stop rule and a schedule:

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
batch: welldb
description: Well logs from the well database, one WellLog record per log, written through the storage service.

partitions: [dev, test]

parameters:
  logSource:
    default: WIRELINE
    description: The log source a run delivers (the log_source column).

source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [log_id]
    primaryKey: RecId
    scope:
      log_source: logSource
  datasets:
    curves:
      object: OsduData.silver.WellLogCurve
      join:
        log_id: log_id
      orderBy: [curve_id]
  lastModified: update_date
  work: ../.work/welllog/{logSource}

render:
  mapping: WellLog@1.0.0

target:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}
  protocol: storage
  validation:
    mode: enforce

reliability:
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }

parallelism:
  recordsAtOnce: 8
  nodes: 4

failWhen:
  failedPercent: 20

schedule:
  cron: "0 * * * *"
  timezone: UTC
  values:
    partition: dev
    logSource: WIRELINE
```

## Top-level keys

| Key | Type | Required | Meaning |
| --- | --- | --- | --- |
| `flowType` | string | yes | `delivery`. |
| `name` | string | yes | The flow's pipeline in the catalog, its schedules and its run history. In the single form the ledger keeps the flow under this name, at most 200 characters, unless `ledger` names another ([ledger](../concepts/ledger.md)). |
| `batch` | string | no | The platform batch the flow belongs to, read by SQLFlow ([flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). |
| `description` | string | no | Free text. |
| `telemetry.operation` | string | no | The flow's operation code in the correlation id every OSDU request of its runs carries, `osdu-delivery-<repository>-<operation>-<run id>`: a lower-case letter, then lower-case letters and digits, at most 16, no hyphen; `dlv` when left out. See [request correlation](../concepts/request-correlation.md). |
| `schedule`, `mode`, `lifecycle` | | no | SQLFlow's envelope keys, read by the platform ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). See [Schedules](#schedules) for what a delivery flow adds. |
| `parameters` | map | no | The flow's own run parameters. See [Parameters](#parameters). |
| `partitions` | list | no | The OSDU data partitions the flow may deliver to. See [Partitions](#partitions). |
| `keepLedger` | string | no | For a flow that serves every registered partition: the partition that keeps the ledger the flow kept before. See [Partitions](#partitions). |
| `ledger` | string | no | In the single form, the name of an existing ledger the flow keeps instead of its own, for a flow renamed and changed at once, which the repository sync does not take for the same document, as it does a plain rename. See [Renaming a pipeline](../concepts/renaming-a-pipeline.md). |
| `source` | map | yes | Where the records come from. See [source](#source). |
| `render` | map | yes | The pinned mapping and what it renders with. See [render](#render). |
| `change` | map | no | How a change is decided. See [change](#change). |
| `target` | map | yes | The OSDU endpoint, its credentials and the route. See [target](#target). |
| `reliability` | map | no | What makes a request hold: retries, timeouts, the rate limit, TLS, the URL guard, ceilings and leases. See [reliability](#reliability). |
| `parallelism` | map | no | What runs at once: the records a run sends at once, the renderers, the work batches, and the fan-out across the fleet. See [parallelism](#parallelism). |
| `verify` | map | no | What a verify run does with drift. See [verify](#verify). |
| `failWhen` | map | no | When record failures stop the run. See [failWhen](#failwhen). |
| `cache` | map | no | The types of the partition's cache the pipeline brings up to date before it runs and after it. See [cache](#cache). |
| `workflows` | map | no | The OSDU workflows run over the records of the enrichments of the flow's mapping once each record landed, by the enrichment's name. See [an enrichment's workflow](enrich-workflow.md). What a record is enriched with is its mapping's `enrich:` ([enrichments](enrich.md)); a flow `enrich:` key is refused. |
| `interfaces` | map | no | The interface form. See [a source with interfaces](interfaces.md). |

## source

The flow reads the keyed ingestion tables SQLFlow loads, and nothing else: there is no drop folder, no manifest and no
replica, and the flow never extracts from a database of its own. `source.connection` names the database; `record` is the
table holding one row per record; each `datasets` entry is a child table joined to it by the record key; `payloads` say
where each record's files are.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `connection` | string | required | The database holding the ingestion tables, as a `${env:NAME}` or `${keyvault:vault/secret}` reference, or a SQL Server connection string whose password is such a reference. Resolved on the node that runs the flow ([connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md)); an `${env:...}` reference in it takes the values set for no partition, then the node's environment, and on the control plane, for a preview or a source read, from its environment alone ([a flow's source connection](../concepts/environment-variables.md#a-flows-source-connection)). |
| `record.object` | string | required | The record table's three-part name, `[database].[schema].[table]` (`OsduData.silver.WellLog`). |
| `record.key` | list | required | The key columns, in order: the ing flow's `load.keyColumns`. They must name the same columns, in the same order, as the mapping's `dataset.key`. With `record.primaryKey` set, the table needs a unique index on them ([the record table](delivery-record-table.md#the-unique-key-index)). |
| `record.primaryKey` | string | none | The table's identity primary key (the ing flow's `target.identityColumn`, such as `RecId`). A delivery is split over nodes on it ([parallelism](#parallelism)); without it a run is not split, and one node plans and sends it all. See [The identity primary key](#the-identity-primary-key). |
| `record.scope` | map | none | Column to parameter: each entry becomes a typed `[column] = @parameter` predicate, so a run reads only the rows of its parameter values. A parameter the run gives several values reads the rows holding any of them, `[column] IN (@p0, @p1, ...)` ([Several values of a scope parameter](#several-values-of-a-scope-parameter)). A value is bound, never substituted into SQL. |
| `record.maxRecords` | integer | none | The first records of the scope a run reads, 1 to 1000, by key, on every run that gives no `maxRecords` of its own: a smoke test before the whole scope goes; leave it out to read the scope whole. A run's own stands in for it. See [The first records of the scope](#the-first-records-of-the-scope). |
| `datasets.<name>.object` | string | required | A child table's three-part name. The mapping reads it under `<name>` (a `$forEach` over it, or `$dataset.<column>`). |
| `datasets.<name>.join` | map | required | Child column to record key column. Every record key column must be joined. |
| `datasets.<name>.orderBy` | list | none | The order of a record's child rows. |
| `datasets.<name>.maxRowsPerRecord` | integer | 100000 | The most child rows one record may carry, 1 to 1000000. A record over it is held. |
| `datasets.<name>.files` | map | none | The files each child row names (`root`, `locationColumn`, `pattern`, `hashColumn`, `chunkCountColumn`, `contentType`), for the enrichment of the mapping named like the dataset: registered with the datasets it adds, or sent as the record's bulk data ([records an enrichment adds](enrich-records.md#the-keys), [an enrichment's bulk data](enrich-bulk.md)). Refused on a dataset no enrichment reads. |
| `payloads.<name>.root` | string | required | The folder or storage prefix a record's files must sit under. Relative to the flow file; `{parameter}` tokens allowed, each taking one value. A `${...}` reference is refused: a location is not resolved from one. |
| `payloads.<name>.locationColumn` | string | none | The record column holding the record's folder, relative to `root` or absolute under it. Required when the route sends the payload. |
| `payloads.<name>.pattern` | string | `*` | A file name glob under the record's folder, without a path. |
| `payloads.<name>.hashColumn` | string | none | The record column holding the payload's content hash. Required unless `change.payloadDetect` is `lastModified`. |
| `payloads.<name>.chunkCountColumn` | string | none | The record column holding how many files the payload has, which spares a storage listing when planning. |
| `lastModified` | string | none | A business version column: a `datetime`, or RFC 3339 / ISO 8601 text (read as UTC without an offset). A row older than the version the ledger holds is never sent ([change detection](../concepts/change-detection.md)). |
| `systemColumns.updated` | string | `UpdatedDate_DW` | The column an incremental read windows on. Cannot be opted out of. |
| `systemColumns.deleted` | string | `DeletedDate_DW` | The soft-delete stamp, used when the table carries it. A row marked deleted is held, never delivered. |
| `systemColumns.inserted` | string | `InsertedDate_DW` | When the row first reached the table, used when the table carries it; dates the record's arrival on its history. |
| `systemColumns.fileName` | string | `FileName_DW` | The file the row was landed from, traced on every attempt; a column wider than 800 characters is refused. |
| `systemColumns.rowNumber` | string | `RowNumber_DW` | The row's position in that file: a whole-number column (an integer type, or `decimal`/`numeric` with scale 0 and at most 18 digits). A column of another type is refused; opt out with `~`. |
| `incremental.overlapSeconds` | integer | 900 | How far below the last watermark the next run reads again, 0 to 86400. |
| `incremental.pageSize` | integer | 1000 | Records per page, 1 to 100000. |
| `incremental.isolation` | `snapshot` \| `readCommitted` | `snapshot` | How a page's record rows and child rows see the database. `snapshot` reads them as one moment and needs `ALLOW_SNAPSHOT_ISOLATION ON`. |
| `incremental.commandTimeoutSeconds` | integer | 0 | Seconds a read may run; 0 is bounded by the run's cancellation alone. |
| `work` | string | required | Where the intake writes its work batches (the rendered documents the drains read back): a folder or storage prefix every node can write, relative to the flow file, with `{parameter}` tokens. A relative folder is in each node's own copy of the repository, which is where each run of a [fan-out](../concepts/submissions.md#many-nodes) writes and reads its own share's batches. A parameter the run gives several values fills its token with them joined by `+` (`../.work/COMPOSITE+WIRELINE`). It may open with a `${...}` reference, which the run resolves with its own values as it opens, as SQLFlow resolves a file flow's location; a reference anywhere else in it is refused. |

`updated` has to exist, named or left at its default. `deleted`, `inserted`, `fileName` and `rowNumber` named explicitly
have to exist: a run (and `sqlflow check --connect`) is refused naming the key, the column and the table, so a misspelt
provenance column never loses every record's origin without a word. Left at their defaults they are used only when the
table carries them. `~` opts a column out (`fileName: ~`), except `updated`. The
[provenance columns](../../../../sqlflow/docs/reference/concepts/provenance-and-row-keys.md) are SQLFlow's; a delivery
reads them as they are.

A payload `root` is substituted from the run's parameters and read as written, so a `${...}` reference in it is refused
when the flow loads:
`<file>: source.payloads.las.root '<location>' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.`
`work` alone may open with a reference, so the work batches go to the storage the deployment configures, which every
node of a fan-out reaches, and the flow moves between deployments unchanged:

```yaml
source:
  work: ${env:LANDING_URL}/.work/welllog/{logSource}
```

The run resolves it as it opens, with the values the control plane supplies, and stops before anything is read when it
cannot: `The source.work '${env:LANDING_URL}/.work/welllog/{logSource}' names a reference that does not resolve: ...`.
A reference anywhere but at its start is refused:
`<file>: source.work 'work/${env:WORK_ROOT}' names a reference other than at its start. A work location that names one is the reference's value with a path after it, such as ${env:LANDING_URL}/.work/welllog/{logSource}.`

```yaml
source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [log_id]
    primaryKey: RecId
  payloads:
    curves:
      root: ../data/curves/{logSource}
      locationColumn: curve_folder
      pattern: "*.parquet"
      hashColumn: payload_hash
      chunkCountColumn: chunk_count
  lastModified: update_date
  systemColumns:
    fileName: ~
  incremental:
    overlapSeconds: 900
    pageSize: 1000
    isolation: snapshot
  work: ../.work/welllog/{logSource}
```

Which payload a route sends, and the names `files` and `bulk` a route that sends both reads, are the route's
([routes](routes.md)). A record whose folder column is empty, climbs out with `..`, or points outside every root is held
with the reason.

### The identity primary key

`record.primaryKey` names an integer identity column that is the record table's own single-column primary key. SQLFlow's
ingestion creates it when the ing flow sets `target.identityColumn`. The record key still identifies a record, derives its
delivery key and OSDU id, and joins the child tables; the primary key is how rows are paged and dealt out:

- **Paging.** A read pages by the primary key; without one it pages by the record key.
- **Splitting over nodes.** The first run cuts the rows it reads into contiguous ranges of primary key values, one per
  part, and each part plans and sends its range from a node of its own
  ([submissions](../concepts/submissions.md#many-nodes)). Without a primary key the run is not split: one node plans and
  sends every row.

When a run opens the tables it refuses a declared column that is not an integer, not an identity column or not the
table's single-column primary key, and a record key with no unique index without a filter (a record held by two rows
could fall into two slices; [the unique key index](delivery-record-table.md#the-unique-key-index) says how to create
it). The message gives the statement that adds the column:

```text
... An existing table needs it added once, which rewrites the table (drop a plain column of that name first):
ALTER TABLE [OsduData].[silver].[WellLog] ADD [RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_WellLog] PRIMARY KEY CLUSTERED;
(NONCLUSTERED when the table already has a clustered index).
```

Run it while nothing loads the table. SQLFlow adds `target.identityColumn` only when it creates a table; set on an
existing one, it adds a plain nullable column, which a run refuses as not an identity column.

### The first records of the scope

`record.maxRecords` limits every run of the scope to its first records, for trying a flow, or a new mapping or template,
on a few records before every row goes:

```yaml
source:
  record:
    object: OsduData.arc.WellLog
    key: [source_project, log_id]
    primaryKey: RecId
    scope:
      log_source: logSource
    maxRecords: 100
```

- **Which records.** Every `deliver`, `plan`, `intake` and `replan` run reads the first `maxRecords` rows of its scope in
  the order a read pages in (the identity primary key, or the record key without one), the same ones run after run while
  the table's first rows stay. Their keys are read from the table, so the flow's first run takes them too, and each is
  planned as a run of every row would plan it: only what renders differently from what OSDU holds is sent.
- **Read by key.** The run reads them as a run scoped to records does ([submissions](../concepts/submissions.md#what-a-run-reads)),
  so the scope keeps no watermark while the key is set, and the records the ledger asks to plan again outside them (a
  release, a redelivery) wait for a run of the whole scope. A run naming records (`recordKeys`) or a submission reads
  what it names.
- **A default.** A run's payload `maxRecords` ([sqlflow run](../cli/run.md#a-delivery-flows-payload)) stands in for it on
  that run: more records, fewer, or 0 to read the scope whole. The log says which setting the run read by, and the run
  dialog's **Run settings** show the document's value as **Records at most** and give a run its own
  ([parameters](parameters.md#run-settings)).
- **Taking it out.** Remove the key to deliver the whole scope: the next run reads the scope as a run without it does,
  every row for a flow limited from its first run, or the rows changed since the watermark the scope kept from before
  the key was set. The records already delivered render as they were and are not sent again.

In a source with interfaces each interface's `record` takes its own (`interfaces.<name>.record.maxRecords`).
`sqlflow check --connect` reports the first records as what the next run reads, and why.

### What a run checks against the tables

Opening the source, a run (and `sqlflow check --connect`) reads the tables' columns and refuses, naming the key and the
table: a table the node cannot see, a column the flow names that the table does not hold (the message lists the
columns), a key column of a type that cannot be compared, an `updated` column that is not a date and time, a record row
whose key column is null in the run's scope, and the primary key rules above. A database that refuses snapshot isolation
fails the read with the statement that enables it, or `isolation: readCommitted`. Before it plans, a run also refuses a
`source.record.key` that differs from the mapping's `dataset.key`, and scope, `lastModified` and payload columns the
record table does not hold ([preflight](../concepts/preflight.md)).

## render

`render` is the only block that changes what a document is. Its values enter the render context recorded against every
record, so changing one renders the records again; everything else changes only how a document gets there
([change detection](../concepts/change-detection.md)).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `mapping` | string | required | The mapping, pinned as `Name@version` (`WellLog@1.0.0`). A floating reference is refused. |
| `cacheVersion` | string | `current` | Which version of the target partition's cache the mapping renders with: `current` takes the version current when the run starts; a label (`20261001T010000Z`) pins one, and the cache's [retention](../concepts/partition-cache.md#retention) keeps its records while a synced flow pins it. Refused for a flow that names several partitions or serves the registry. |
| `parameters` | map | none | Values for the parameters the mapping declares, as literals or `${env:...}` / `${keyvault:...}` references. |
| `mappings` | string | none | The folder the mappings are read from, relative to the flow file. Left out, the nearest `mappings` folder walking up from the flow file. |

A mapping `WellLog@1.0.0` is the file `WellLog@1.0.0.yaml` (or `WellLog/1.0.0.yaml`) in that folder, and the file must
declare the same name and version. A flow reads the cache of the partition it delivers to and names no cache: a
document that still declares `render.cache` is refused ([partition cache](../concepts/partition-cache.md)).

The four parameters every OSDU record needs belong to the kind rather than to a flow. When the mapping declares one and
the flow supplies no value, it takes a reference that resolves from the central configuration or the node's environment:

| Mapping parameter | Default when the flow leaves it out |
| --- | --- |
| `dataPartition` | the partition the run is bound to, for a flow that works in partitions; the flow's `target.headers.data-partition-id`, as written, for a flow bound by its header; `${env:OSDU_DATA_PARTITION}` only for a flow bound to no partition |
| `aclOwner` | `${env:OSDU_ACL_OWNER}` |
| `aclViewer` | `${env:OSDU_ACL_VIEWER}` |
| `legalTag` | `${env:OSDU_LEGAL_TAG}` |

A value the flow supplies always wins, and references are resolved before the render context is built, so the context
holds the value that reached the record. A flow that names its partitions, or serves the registry, may not set
`dataPartition`: every run mints its ids in the partition it targets.

## change

| Key | Values | Default | Meaning |
| --- | --- | --- | --- |
| `detect` | `renderedHash` \| `always` | `renderedHash` | A document is sent when the hash of what it renders to differs from what the ledger holds; `always` sends every record read. |
| `payloadDetect` | `contentHash` \| `lastModified` \| `always` | `contentHash` | A payload is sent when its hash column moves; `lastModified` takes its files' modified times as its watermark; `always` sends it every time. |
| `onUnchanged` | `skip` \| `deliver` | `skip` | `deliver` sends a record read whose document and payload are unchanged. |
| `useSourceVersions` | boolean | `true` | The tier-0 gate: an incremental run whose window holds no changed row, and no record waiting to be planned again, completes without reading a record. |

`detect: lastModified` and `detect: contentHash` are refused (the business version column is `source.lastModified`), so
is `payloadDetect: renderedHash`, and so is `payloadDetect: lastModified` on a route that sends no payload files. What
each decision reads is on [change detection](../concepts/change-detection.md).

## target

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | string | required | The OSDU platform root every service is reached under, usually `${env:OSDU_URL}` (a single-form flow on the ddms route may point at the DDMS itself; see [DDMSs](ddms.md#the-endpoint-the-platform-root-or-the-ddms-itself)). Changing it never redelivers a record by itself. A URL written literally whose user info or query carries a credential is refused. |
| `auth` | map | `type: none` | How every request authenticates. See below. |
| `headers` | map | none | Extra headers on every request. `data-partition-id` here hard-codes the flow's one partition. A header that carries a credential (`Authorization`, an API or subscription key, a token, any `x-api-*` header) holds a reference, after its scheme when it has one (`Bearer ${env:NAME}`); a literal is refused when the flow is read. `correlation-id` is refused: every request carries the one its run sets ([request correlation](../concepts/request-correlation.md)). |
| `protocol` | string | required in the single form | The route the records go by: `storage`, `file`, `dataset`, `manifest`, `ddms`, `fileAndDdms`, `manifestAndDdms`, `workflow`, `dspdm` or `etp` ([routes](routes.md)). |
| `protocolOptions` | map | none | The route's options ([routes](routes.md#options-every-route-reads)). |
| `ddms`, `eds`, `dspdm`, `etp` | map | none | The DDMSs and services some routes deliver to ([DDMSs](ddms.md), [DDMS shapes and services](ddms-services.md)). |
| `workflow`, `airflow` | map | none | The workflow route's declaration ([routes](routes.md)). |
| `verifyReferences` | `none` \| `storage` | `none` | `storage` asks OSDU's storage service, before a record is sent, about the ids it refers to that no record of the ledger holds, and holds a record naming one storage does not hold ([preflight](../concepts/preflight.md#references-checked-in-storage)). Refused on the `dspdm` route. |
| `validation.mode` | `report` \| `enforce` | `report` | What the gate before a record is sent does with a record that breaks its schema: send it and record the verdict, or hold it ([preflight](../concepts/preflight.md#validation-before-a-record-is-sent)). |
| `validation.unverified` | `send` \| `hold` | `send` | What it does with a record some part of which could not be checked. |

The names the routes carried before (`osduRecord`, `osduWellLog`, `osduFile`, `osduManifest`, `osduDataset`,
`osduFileAndDdms`, `osduManifestAndDdms`, `osduWorkflow`, `osduDspdm`, `osduEtp`) still load. In the interface form each
interface's route follows from what it declares, so `target.protocol` is refused at the source level.

### target.auth

| Key | Meaning |
| --- | --- |
| `type` | `none`, `bearer`, `apiKeyHeader`, `basic` or `oauth2ClientCredentials`. |
| `secretRef` | The token (`bearer`), the key (`apiKeyHeader`), the password (`basic`) or the client secret (`oauth2ClientCredentials`). Required for `bearer`, `apiKeyHeader` and `basic`. A reference only: a literal is refused when the flow is read. |
| `secondarySecretRef` | The user name (`basic`) or the client id (`oauth2ClientCredentials`): an identifier, so a literal is allowed. |
| `headerName` | The header an `apiKeyHeader` key goes in. Required for that type. |
| `valuePrefix` | Text written before the value: `Bearer` and a space by default for `bearer`, nothing for `apiKeyHeader`. |
| `token.url` | The token endpoint (`oauth2ClientCredentials`). Written literally, its user info and query may carry no credential. |
| `token.discoveryUrl` | An OIDC discovery document whose `token_endpoint` is used instead of `url`. The same rule as `url`. |
| `token.body` | Form fields of the token request (`scope`). `grant_type: client_credentials`, `client_id` and `client_secret` are added from the refs when the body leaves them out. A field named as a secret (`client_secret`, `password`, `refresh_token`, `client_assertion`, ...) holds a reference; `client_id`, `scope` and `audience` may be literals. |
| `token.basicAuthClient` | `true` sends the client id and secret as a Basic header instead of in the body. Default `false`. |
| `token.tokenPath` | Where the token is in the response. Default `access_token`. |
| `token.applyPrefix` | Text written before the token in `Authorization`. Default `"Bearer "` (with its space). |

`oauth2ClientCredentials` needs a `token` block. A token is reused until a minute before it expires (its `expires_in`, or
30 minutes when the response gives none). Every secret is a reference; a literal one is refused when the flow is read,
naming the key and never the value:

```text
<file>: target.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: write ${keyvault:vault/secret} or ${env:NAME}, and keep the value in the key vault or the environment of the nodes that run the flow (locally, the git-ignored .sqlflow/env file).
```

The same rule holds for `target.airflow.auth` and `target.airflow.headers`. The secret references the target declares
are what the catalog lists as the flow's credential references.

## Parameters

A flow parameter is a run value supplied with `--set name=value`, a trigger's values or a schedule's `values`. A
parameter `source.record.scope` binds may be given several values ([Several values of a scope
parameter](#several-values-of-a-scope-parameter)); every other parameter takes one.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `parameters.<name>.required` | boolean | `false` | A run without a value fails with `parameter '<name>' is required. Supply it with --set <name>=value or the run's values.` |
| `parameters.<name>.default` | string | none | The value a run without one takes. |
| `parameters.<name>.description` | string | none | Free text. |
| `parameters.<name>.choices` | list | none | The values the parameter may take; a run given another is refused, and the run form lists them. |
| `parameters.<name>.lookup.column` | string | none | The record table's column whose values the run form offers for the parameter; a scope parameter is looked up in the column it binds without one. |
| `parameters.<name>.lookup.interface` | string | the first | The interface whose record table holds that column. |

`{name}` tokens are substituted in `source.work`, every payload `root` and every `datasets.<name>.files.root`; a value carrying `/`, `\` or `..` is refused
there, since these locations bound what a run reads and writes. `source.record.scope` binds a parameter to a column
instead. A token or a scope naming an undeclared parameter is refused when the document loads, and a run supplying an
undeclared one is refused when it starts. Each distinct set of values is a scope of its own, with its own watermark.

How a run takes its values, which operations resolve them (`deliver`, `plan`, `intake` and `replan`), the choices and
lookups, and how the run form and a schedule show them are on [parameters](parameters.md). Mapping parameters are
different: they go under `render.parameters` ([render](#render)).

### Several values of a scope parameter

A run reads the rows of several values of a scope parameter at once when it is given a list of them: `--set` repeated
on the CLI (`--set logSource=COMPOSITE --set logSource=WIRELINE`), an array in a trigger's values
(`"values": { "logSource": ["COMPOSITE", "WIRELINE"] }`), a sequence in a schedule's `values`
(`logSource: [COMPOSITE, WIRELINE]`), or several picked in the parameter's field in the GUI. The read selects the rows
holding any of them, `[log_source] IN (@p0, @p1)`, each bound as a value of the column's own type, so one run plans and
delivers what a run of each value would.

- **The set is the scope.** The values are taken as a set, each once and in ordinal order, so the same values named in
  another order are the same scope. A set keeps one watermark of its own, beside the watermark each single value keeps, and
  its submissions record the set (`{"logSource":["COMPOSITE","WIRELINE"]}`), so a record's redelivery reads the scope it
  was planned under. A list of one value is that value.
- **Only a scope parameter takes several.** A list given to any other parameter is refused when the run starts:
  `<file>: parameter 'region' is given 2 values, and takes one: only a parameter source.record.scope binds (logSource) takes
  several.` So is a list for `partition` (`partition is given 2 values; a run acts in one partition.`), and a list of more
  than 100 values or over 16,000 characters.
- **Locations.** A list fills a `{parameter}` token in `source.work` with its values joined by `+`, since the work root
  only keeps one run's batches apart from another's. A payload `root` takes one value, since a record's files sit under one
  of them: `<file>: parameter 'logSource' is given 2 values, and source.payloads.curves.root is a location that takes one:
  run one value at a time.` Name the record's folder in its `locationColumn` instead, and the root needs no token.
- **The watermark's key.** A set whose scope key passes the 400 characters the ledger keeps a watermark under is refused
  before anything is planned: `The parameter values of this run make a scope key of <n> characters; the ledger keeps a
  watermark under at most 400. Shorten the flow's parameter values, or give a parameter fewer values at once.`

A removal compares the records planned under a set when its own `values` name every value of the set
([removal](removal.md#what-a-run-finds)).

## Partitions

A flow says which partitions it delivers to along one of three paths ([partitions](../concepts/partitions.md)):

| The flow declares | It delivers to |
| --- | --- |
| `partitions: [dev, test]` (or `{ name: dev, keepLedger: true }` entries) | the partitions it names; a run targets one of them |
| `target.headers.data-partition-id: dev` | that one partition |
| neither | every partition registered with the catalog (named in the control plane's configuration, `Osdu:Partitions`, or registered with `sqlflow partition add`), the run's choice or the registry's default |

A run names its partition with the run value `partition` (`--set partition=test`, a schedule's `values`); the
module's own verbs take `--partition`. Each partition keeps a ledger of its own, named `<ledger>@<partition>`;
`keepLedger` marks the one partition that keeps the ledger the flow kept before it served partitions (on the entry for a
flow that names them, at the top level for one that serves the registry). A flow that works in partitions declares no
`data-partition-id` header, no `render.parameters.dataPartition` and no parameter called `partition`.

## reliability

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `retry.attempts` | integer | 4 | The tries a request that is safe to repeat gets after a transport failure or a 408, 425, 429, 503 or 504; and the attempts a record gets before it is failed for good. At least 1. |
| `retry.backoff` | `exponential` \| `fixed` | `exponential` | The wait between tries of a request. |
| `retry.baseDelayMs`, `retry.maxDelayMs` | integer | 500, 30000 | The first and the longest wait between tries of a request. |
| `retry.honorRetryAfter` | boolean | `true` | Never ask again sooner than a `Retry-After` header says; a wait longer than `maxDelayMs` is left to the record's next try. |
| `retry.recordBaseDelayMinutes`, `retry.recordMaxDelayMinutes` | integer | 1, 60 | The wait before a record that failed gets another try, doubling per attempt up to the maximum. |
| `skipStatusCodes` | list | none | More statuses that hold a record instead of trying it again (`HTTP <status> is not retryable`). 400, 403, 404, 405, 409, 413, 415 and 422 hold it whatever the list says. |
| `timeoutSeconds` | integer | 100 | The timeout of one request. |
| `rateLimitRps` | number | 0 | Requests per second; 0 is unlimited. A fan-out keeps to it as a whole: each of its runs sends under its share ([submissions](../concepts/submissions.md#many-nodes)). |
| `verifyTls` | boolean | `true` | `false` accepts any certificate, and only on a node that sets `SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS=true`; elsewhere the run is refused. |
| `urlAllowlist` | list | none | Hosts requests may reach; `*.example.com` matches the domain and its subdomains. Checked on every redirect, and a redirect from https to http is refused. |
| `maxResponseBytes` | integer | 67108864 | The largest response read. |
| `maxRequestBodyBytes` | integer | 0 | The target's request body ceiling; a bigger chunk holds the record before anything is sent. 0 declares none ([DDMSs](ddms.md)). |
| `leaseSeconds` | integer | 300 | How long a worker's lease on a record lasts (at least 30 is used) before another node may take it. |

What happens to a record that fails, is held or waits is on [record lifecycle](../concepts/record-lifecycle.md).

## parallelism

How a delivery is spread over the fleet, and what runs at once on a node. A flow says the most nodes a delivery may use;
a run works out the rest from the rows it reads. In a source with interfaces the block applies to every interface, and an
interface lays its own keys over it, except `interfacesAtOnce`, which is the source's
([a source with interfaces](interfaces.md)).

```yaml
parallelism:
  nodes: 10
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `nodes` | integer | 8 | The most nodes a delivery is spread over, 1 to 64. A run reading at least twice `minRowsPerNode` rows is split into parts, one per `minRowsPerNode` rows and at most this many; each part delivers its range of the record table's `primaryKey` from a node of its own. 1 keeps every delivery on one node. |
| `minRowsPerNode` | integer | 1000 | The fewest rows a part takes, so a node is started only for work enough to be worth it. At least 1. |
| `recordsAtOnce` | integer | 8 | How many records each part, or a run alone, sends to OSDU at the same time; verify and reverse runs work this many at once too. At least 1. |
| `renderThreads` | integer | 0 | The threads that render records on one node, 0 to 256; 0 is half the node's processors, at least one. |
| `recordsPerBatch` | integer | 500 | The records one batch file holds, 1 to 100000: one file a part writes, one claim a worker takes, one unit of progress. |
| `interfacesAtOnce` | integer | 4 | The interface form only: how many interfaces of one wave deliver at the same time, 1 to 32 ([interfaces](interfaces.md#order)). |

`nodes` is the one setting most flows need, and the others have defaults no flow has to change. How many parts a run
uses comes from the rows it reads, at most `nodes`:

| The run reads | Parts, with the defaults |
| --- | --- |
| 50 changed rows (an incremental run) | 1: delivered by one node |
| 2,500 rows | 2 |
| 5,000 rows (a first, full delivery) | 5 |
| 20,000 rows or more | 8, the most `nodes` allows |

A worker claims the records due for a retry `recordsAtOnce` at a time, and at least 50. An assertion, dimension, inventory, retrieval or download flow writes what one of its runs works on at once with the same key, `recordsAtOnce`: its tests, dimensions, version lists, kinds or storage reads ([assertion](assertion.md), [dimension](dimension.md), [inventory](inventory.md), [retrieval](retrieval.md), [download](download.md)). How a run is cut into parts, how
each part is delivered, and what the first run does with a part that does not finish is on
[submissions](../concepts/submissions.md#many-nodes).

The keys were named by what they did not say before, and the settings were written under `reliability` before that. A
document that still writes an old name is refused, naming the key it is now:

```text
flows/welllog.yaml: parallelism.fanOut is now parallelism.nodes: the most nodes a delivery is spread over, each part on a node of its own; it counts every part, where fanOut counted the parts beside the first run. Rename it.
flows/welllog.yaml: reliability.concurrency is now parallelism.recordsAtOnce: what runs at once, and across the fleet, is the flow's parallelism, and reliability keeps what makes a request hold. Move it.
```

| Was | Is now |
| --- | --- |
| `fanOut` | `nodes` (it counts every part; `fanOut: 8` was nine runs) |
| `fanOutMinRecords` | `minRowsPerNode` |
| `concurrency` | `recordsAtOnce` |
| `renderers` (`reliability.renderParallelism`) | `renderThreads` |
| `batchRecords` | `recordsPerBatch` |
| `interfaces` (`reliability.parallelInterfaces`) | `interfacesAtOnce` |
| `batchSize` | removed: retries are claimed `recordsAtOnce` at a time, at least 50 |

## verify

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `verify.reconcile` | boolean | `false` | A verify run compares OSDU's version of each delivered record with the ledger's; with `true` it also marks each drifted or missing record for redelivery of all of it, as a redelivery does, named under the verify's activity; the flow's next deliver run reads it by key and sends it again, whatever its incremental window reads. Send again sends it at once. |

## failWhen

A record with a data problem is held and the run goes on. `failWhen` says when the records' failures add up to a failure
of the run itself, in a deliver, replan or drain run:

| Key | The run stops when | Default |
| --- | --- | --- |
| `outageFailures` | this many records in a row could not reach the service (transport failure, timeout, HTTP 408, 429 or 5xx) or were refused by it (401 or 403), none delivered in between; 0 turns it off | 25 |
| `consecutiveFailures` | this many records in a row failed with problems of one class (data, connection or permission) | not set |
| `failedPercent` | held and failed records reach this share (above 0, at most 100) of the records settled, once `minRecords` have settled | not set |
| `minRecords` | how many records settle before `failedPercent` is judged | 100 |

A stopped run hands back the records it had not sent without charging them a try, closes its submission as failed
(`stopped: <why>`) and ends failed; the next run sends what is left. In the interface form the same rules stop one
interface ([when an interface stops](interfaces.md#when-an-interface-stops)).

## cache

A pipeline says which entities of its partition's cache must be current before it runs, and which it brings up to date
after it runs, so the pipelines that follow it in a schedule render against what it delivered:

```yaml
cache:
  before: [UnitOfMeasure]            # brought up to date from OSDU before the run renders
  after: [master-data--Wellbore]     # what the run delivers goes in as it ends
```

| Key | Default | Meaning |
| --- | --- | --- |
| `cache.before` | none | The types brought up to date before the run renders. An OSDU type is searched with each declaring cache flow's kind and query through this pipeline's own connection to its partition, and the capture is merged as that flow's, so the run renders against what OSDU holds now, whoever delivered it. A lookup table from an ingestion table is read over its cache flow's connection, as that flow reads it. |
| `cache.after` | none | The types the records the run changes in OSDU go into as it ends: what it delivered, removed or reverted is read back from OSDU's storage service by id; a record storage returns goes in as it is now, into the types holding its kind, and one it no longer holds is taken out. With a type holding a kind the enrichments of the flow's mapping add, the records those records link are read back with them ([records an enrichment adds](enrich-records.md#the-cache)). |

Each step is `auto`, or a list of at most 64 entries, each once. An entry is a type of the partition's cache by its name
(`UnitOfMeasure`), or an OSDU entity type (`reference-data--UnitOfMeasure`), which stands for every type of the cache
holding records of it, whichever repository's cache flow declares it. `auto` works the entries out:

- **`before: auto`** takes the OSDU types the mapping and every enrichment mapping of its enrichments read, by name or by
  the entity type they check the ids they build against, and the lookup tables from ingestion tables they read by name.
- **`after: auto`** takes every type holding the kind the pipeline delivers, or a kind its enrichments add records of,
  that some cache flow declares without a query.

A run resolves the entries against what the partition's synced cache flows declare, and refuses before it reads or sends
anything an entry naming nothing the cache holds; in `before` a dictionary or a dimension type, which only its cache flow
reads, from the repository or the dimension's build, in the order lineage gives; and in `after` a lookup table, a type
holding neither the kind the pipeline delivers nor one its steps register, and a type every declaring flow narrows with a
query, since a delivered record the query does not match would stay in the cache.

The two steps differ in what their failure means. **The before step is the run's to complete:** when a type cannot be read
whole, the run fails before anything is sent, as a cache it must not render without demands. **The after step's failure is
not the run's:** what the run did in OSDU stands, the run's result says what the step could not do, and the next refresh of
the cache flows brings the records in. A run changing more than 50,000 records reads none back and says to run the
refresh, which reads them more cheaply.

Both steps tag what their changes reach, as a refresh does, and ask for those records to be planned again at once (up to
100,000; the control plane's rollout carries the rest), so a pipeline later in the schedule finds them due: a record held
because a reference named an id the cache did not hold is built again once the id is there. A deliver or replan run takes
both steps; a drain of the whole flow, a removal and a reversal take the after step. In a source with interfaces, `cache`
is every interface's unless the interface sets its own, which replaces it whole, and each interface renders against the
newest cache when it starts, so the logs see the wellbores the interface before them put in. The run page shows what each
step did under **Cache**, and the run's result carries it as `cacheBefore` and `cacheAfter`. [The partition
cache](../concepts/partition-cache.md#pipelines-bring-the-cache-up-to-date) describes the steps in full.

## Schedules

The `schedule:` key is SQLFlow's ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md)): a schedule name, a
list of names, or an inline cron or interval with its time zone. There is no scope: a fire runs every flow that joined
the schedule. A delivery flow uses two more keys of it, which SQLFlow passes to flows of a registered kind:

| Key | Meaning |
| --- | --- |
| `operation` | What every fire runs; `deliver` when left out. |
| `values` | Run values every fire supplies: the flow's parameters, and `partition`. A parameter `source.record.scope` binds may take a sequence (`logSource: [COMPOSITE, WIRELINE]`), which every fire reads as one scope. |

A fire supplies nothing on its own, so a flow with a required parameter gives the schedule a value for it, or every fired
run fails with `parameter '<name>' is required`. The schedule's operation is checked against the operations below when
the document loads; its values are checked by each run, as a trigger's are.

| Operation | What a run does |
| --- | --- |
| `deliver` | Plans the rows the ingestion tables changed and delivers what renders differently (the default). |
| `plan` | Reports what a delivery would send, changing nothing. |
| `intake` | Plans rows into work batches without delivering them: a plan to drain later, or the key slices of a submission it names. |
| `drain` | Delivers the work batches a submission already planned. |
| `verify` | Compares what OSDU holds with what the ledger recorded. |
| `replan` | Reads every row of the scope again and delivers what renders differently now. |
| `sync` | Reads the ledger's records from the ingestion tables and consolidates the ledger; sends nothing. |
| `reverse` | Puts OSDU back as it was before one run or submission. |
| `undo` | Undoes what deliveries that did not complete left in OSDU. |
| `remove` | Removes the records the payload names from OSDU to the extent its scope asks, and from the ledger with `purgeLedger`: what **Remove** on the Records tab queues. |
| `delete-ledger` | Removes every record of the ledger from OSDU (reversibly), then deletes the ledger; the payload names the partition to confirm. |

The payload each operation takes, the result and the exit codes are on [running an OSDU flow](../cli/run.md).

## Validation errors

Every message starts with the file. A sample of what `sqlflow validate` refuses:

| Cause | Message |
| --- | --- |
| A floating mapping | `render.mapping 'WellLog' must be pinned as 'Name@version'; floating references are not allowed.` |
| No route in the single form | `'target.protocol' is required.` |
| An unknown key | `invalid YAML at line 28, column 3 - Property 'replica' not found on type 'SqlFlow.Delivery.Documents.FlowSourceYaml'.` |
| A two-part table name | `source.record.object 'silver.WellLog' must be a three-part name [database].[schema].[table]: it has 2 part(s).` |
| A child join missing a key column | `source.datasets.curves.join does not cover key column 'log_source' of the record table; every key column must be joined, or a child row could belong to several records.` |
| A scope on an undeclared parameter | `source.record.scope binds column 'log_source' to parameter 'source', which is not declared under parameters.` |
| A limit out of range | `source.record.maxRecords is 5000; a run reads the first 1 to 1,000 records of its scope, or leave it out to read the scope whole.` |
| An undeclared token | `source.work uses '{source}', which is not declared under parameters.` |
| A reference in a payload root | `source.payloads.las.root '${env:LAS_ROOT}' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.` |
| A reference inside the work location | `source.work 'work/${env:WORK_ROOT}' names a reference other than at its start. A work location that names one is the reference's value with a path after it, such as ${env:LANDING_URL}/.work/welllog/{logSource}.` |
| A literal password | `source.connection carries a literal password. A flow document holds references only: put the connection string, or its password, behind ${keyvault:vault/secret} or ${env:NAME}, or connect with Azure AD (Authentication=Active Directory Default).` |
| A literal secret | `target.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: ...` |
| A literal credential header | `target.headers.Authorization holds a literal value, and the header carries a credential; it holds a reference, after its scheme when it has one (Bearer ${env:NAME}). ...` |
| `enrich:` on the flow | `enrich declares what is added to each record, which the mapping says now: move each enrichment to the enrich: block of render.mapping's mapping, with its rows, mapping, key, after and onHold; bind its rows here under source.datasets.<name> (their files under source.datasets.<name>.files), and its workflow under workflows.<name>.` |
| A pinned mapping that is not there | `render.mapping: Mapping 'Wellbore@1.0.0' was not found under '../mappings'. Expected one of: Wellbore@1.0.0.yaml, Wellbore@1.0.0.yml, 1.0.0.yaml, 1.0.0.yml.` |
| A setting that moved to `parallelism` | `reliability.renderParallelism is now parallelism.renderThreads: what runs at once, and across the fleet, is the flow's parallelism, and reliability keeps what makes a request hold. Move it.` |
| A `parallelism` key by its old name | `parallelism.batchRecords is now parallelism.recordsPerBatch: the records one batch file holds. Rename it.` |
| A header beside `partitions` | `target.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it targets. Remove the header.` |
| A pinned cache version whose records are pruned | `render.cacheVersion pins version 20261001T010000Z of the cache of partition 'dev', whose records the cache's retention pruned at <time>, so nothing can render against it. Pin a version the cache still holds ('sqlflow cache list dev' lists them), or remove render.cacheVersion to render against the current version. ...` |
| A pinned cache version with several partitions | `render.cacheVersion pins version 20261001T010000Z, which is a version of one partition's cache, and the flow names 2 partitions, each rendering against its own partition's cache. ...` |
| `render.cache` | `render.cache is not a setting any more: a flow reads the cache of the partition it delivers to (target.headers.data-partition-id), which every cache flow of that partition fills. Remove render.cache.` |
| A missing hash column | `the flow decides payload changes by content hash, so source.payloads.curves.hashColumn must name the record column holding it; or take the files' modified times instead with change.payloadDetect: lastModified.` |
| OAuth without a token block | `target.auth of type oauth2ClientCredentials needs a 'token' block with the token endpoint url and body.` |
| A cache step that is neither auto nor a list | `cache.before is 'everything': write auto, or list the types of the partition's cache by name (UnitOfMeasure) or by OSDU entity type (reference-data--UnitOfMeasure), for example [UnitOfMeasure].` |
| `auto` among a step's entries | `cache.after lists auto among its entries; auto stands alone: write cache.after: auto.` |
| An after step on the dspdm route | `cache.after is set, and the dspdm route writes DSPDM rows, which are no OSDU records a type of the partition's cache holds. Remove it.` |
| An entry the partition's cache does not hold (at run time) | `Pipeline '<name>' names in its cache steps what the cache of partition '<p>' cannot bring up to date, so nothing was read or sent: cache.after names '<entry>', which is neither a type of the partition's cache nor an entity type one holds; it holds ...` |

`validate` also finds and reads the mapping the flow pins, as a run finds it (`render.mappings`, else the nearest
`mappings` folder walking up), and refuses the flow when it is missing or does not load. What validate cannot check (the
mapping against its template, the cache, the tables, the order of interfaces) is checked by `sqlflow check` and by every
run before anything is sent ([preflight](../concepts/preflight.md)).

## See also

- [The record table a delivery reads](delivery-record-table.md): what the table must hold, the ing flow keys that make
  it, and each refusal with its fix.
- [A source with interfaces](interfaces.md): several kinds of one source in one document.
- [Routes](routes.md), [DDMSs](ddms.md) and [DDMS shapes and services](ddms-services.md): `target.protocol`, its
  options, and where DDMS records go.
- [Mapping](mapping.md): what `render.mapping` pins.
- [Change detection](../concepts/change-detection.md): what makes a record deliver again.
- [Preflight](../concepts/preflight.md): the checks before anything is sent.
- [Partitions](../concepts/partitions.md) and [submissions](../concepts/submissions.md).
- [Ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md): the flow that loads the tables a delivery reads.
