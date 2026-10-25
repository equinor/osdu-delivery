---
id: delivery-concept-api
title: "The delivery API (/api/v1/delivery): every route, its scope, parameters and answer"
type: concept
summary: "Every HTTP route OSDU Delivery adds under /api/v1/delivery, by area: method, scope, query and body parameters, and what each answers or queues."
keywords:
  - api/v1/delivery
  - rest api
  - endpoints
  - routes
  - http api
  - scopes
  - x-osdu-partition
  - "?interface="
  - "?partition="
  - compute task
  - "202 accepted"
  - problem details
  - openapi
related:
  - concept-control-plane
  - delivery-concept-control-plane
  - delivery-concept-authentication-and-identity
  - delivery-concept-ledger
  - delivery-concept-explorer
  - delivery-cli-run
  - delivery-cli-records
  - delivery-concept-gui
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DirectOperationRunner.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/WorkbenchPartition.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryArtifactEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAssertionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheStreams.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Dimension.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Element.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.References.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Validate.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryIssueEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryLedgerEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryReversalEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliverySearchTermEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryValueCheckEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlanePolicies.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/Contracts.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryOrderEndpoints.cs
---

# The delivery API: every route under /api/v1/delivery

OSDU Delivery adds one route group to SQLFlow's control plane, `/api/v1/delivery`. The GUI's OSDU pages, the OSDU verbs
of `sqlflow` that talk to a control plane, and the `delivery_*` tools of the MCP server all call it. Everything else the
control plane serves (runs, pipelines, repositories, schedules, lineage, search, users, nodes, the OpenAPI document at
`/openapi/v1.json`) is SQLFlow's API, documented in
[the control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md). This page lists every route the
module registers, grouped by area, with the policy it is mapped under, its parameters and what it answers.

## How the routes behave

**Policies.** The module registers no policy of its own: it maps its routes into SQLFlow's policy groups. `read`,
`operate` and `author` are names only, each resolving to "a signed-in user", so any valid token, whatever scopes it
carries, reaches every route of those three groups. Only `admin` checks the token, for the `admin` scope, on eight routes
([authentication and identity](authentication-and-identity.md)). The Policy column says which group a route is in.

**Addressing.** A flow's routes take the catalog's pipeline id (`/flows/{pipelineId}/...`). A record's routes take the
ledger's flow id and the record's delivery key (`/records/{flowId}/{key}/...`), because the same row read by several
flows is one record per flow; the flow id is on every record a listing returns.

**Interfaces and partitions.** A route under `/flows/{pipelineId}` works on one interface of the flow. A flow in the single
form, or a source of one interface, needs no name; for a source of several, `?interface=<name>` names it, and without it
the answer is 400 `Interface required`, listing the interfaces (a name the flow does not declare is 404
`No such interface`). A flow that names its partitions keeps a ledger per partition, so the same routes take
`?partition=<name>`: 400 `Partition required` without it for a flow of several partitions, 400 `No such partition` for one
it does not name. The counts (`stats`) and the interface listing cover every partition when none is named. Reads across
flows (the record lookup, the audit trail, the boards) read the partition the request names, else the one the GUI sends
in the `X-Osdu-Partition` header (the title bar's partition), else every partition.

**What runs where.** Reads are answered from the ledger in the `osdu` schema. What a person asks of OSDU or the source and
waits on (a probe, a read-back, a source read, a preview, a scope's values, every explorer read) runs in the control
plane with the flow's own credentials and answers in the response; a failure is a problem saying which side failed: 502
(OSDU or the ingestion tables refused or failed), 504 (no answer in time) or 422 (the flow cannot serve the request),
every resolved secret redacted. A value check is a compute task for a node: 202 with `/api/v1/compute/tasks/{taskId}` to
poll. A redelivery, a sync, a verify, a removal, a reversal and a ledger deletion are runs: 202 with `/api/v1/runs/{runId}`
to follow. The control plane never delivers.

**Paging.** `page`/`pageSize` lists use the platform's pages (50 by default, at most 200). Cursor lists take `after` and
`limit` and answer `next`, the value to pass as `after`.

## Flows and records

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /flows/{pipelineId}/stats` | read | `interface`, `partition` | Record counts by custody state, drift, the last 24 hours and the last submission; every interface (and partition) added up unless one is named. Counted from the ledger. |
| `GET /flows/{pipelineId}/interfaces` | read | `partition` | The flow's interfaces in document order: ledger identity, route and why, mapping and kind, record table, `after`, the order a run takes them in, parameters with the scope column each binds, key columns, counts. |
| `GET /flows/{pipelineId}/records` | read | `search`, `mode` (`contains`; prefix otherwise), `status`, `submissionId`, `deliveredBy`, `runId`, `drifted`, `issue`, `page`, `pageSize`, `interface`, `partition` | A filtered page of the interface's records. `status` is one of `pending`, `delivering`, `delivered`, `held`, `failed`, `deleted`, `reverted`, `waiting`. A page past the listing's count limit is 400 rather than a scan. |
| `GET /records` | read | `search`, `status`, `flowId`, `partition`, `page`, `pageSize` | The record lookup across every flow: a delivery key, or the start of an OSDU id, source key, label, identity value or ingestion file name. Each hit names its flow, interface, partition, the values that matched and the file and row it came from. Without `search`, the newest records. |
| `GET /records/flows` | read | `partition` | The ledger identities the lookup can be narrowed to (`flowId` for `GET /records`), one per interface that holds a record. |
| `GET /flows/{pipelineId}/target` | read | `interface`, `partition` | Where the records live: endpoint as written, data partition, route, auth type, the path each removal scope calls, and on a DDMS route the collection serving the mapping's kind. No header other than the partition is reported. |
| `GET /flows/{pipelineId}/submissions` | read | `max` (100, at most 1,000), `interface`, `partition` | The interface's submissions, newest first. |
| `GET /flows/{pipelineId}/retrievals` | read | `max` (100, at most 1,000) | A retrieval flow's runs, newest first; 409 for another kind. |
| `GET /records/{flowId}/{key}` | read | | One record: custody state, OSDU id and version, hashes, source key and label, last error, its pipeline, interface and partition, the record it waits for and those waiting for it. A record deleted from the ledger is 404 `Deleted from the ledger`, naming who deleted it and when. |
| `GET /records/{flowId}/{key}/attempts` | read | `max` (100, at most 500) | Every delivery attempt with its outcome, steps and error. |
| `GET /records/{flowId}/{key}/activities` | read | `max` (100, at most 500) | The interventions and runs that named the record. |
| `GET /records/{flowId}/{key}/chain` | read | | The record's row through its ingestion table: when it arrived and every recorded change, each with the ingestion run and the landing of its file. |
| `GET /records/{flowId}/{key}/cache` | read | | What the record read out of the partition cache when it was rendered. |
| `GET /records/{flowId}/{key}/artifacts` | read | `max` (200, at most 1,000) | What deliveries of the record created in OSDU, with each artifact's state and undo. Answers for a record deleted from the ledger too; 404 for a key the ledger never held. |
| `GET /flows/{pipelineId}/undos` | read | `page`, `pageSize`, `interface`, `partition` | Open undos counted for the source and each interface, with a page of the records that hold artifacts an undo may still take. |

## Submissions and the audit trail

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /submissions/{submissionId}` | read | | One submission with its counts and the runs that worked on it. |
| `GET /submissions/{submissionId}/attempts` | read | `max` (500, at most 5,000) | The submission's attempts. |
| `GET /submissions/{submissionId}/batches` | read | `status`, `page`, `pageSize` | Its work batches. |
| `GET /activities` | read | `pipelineId` or `flowId` (not both), `submissionId`, `runId`, `kind`, `actor`, `outcome`, `idle`, `since`, `until`, `page`, `pageSize`, `interface`, `partition` | The audit trail, newest first. `idle=false` leaves out runs that changed nothing, `idle=true` lists only them. |
| `GET /activities/flows` | read | `partition` | The ledger identities the trail can be narrowed to, including ledgers whose flow is no longer synced. |
| `GET /activities/{activityId}` | read | | One activity with its captured log, and the pipeline, interface and partition it is reached by. |

## Interventions

An intervention is recorded in the ledger under the caller's name: what the control plane marks itself (a release, the records a redelivery names) as `user:<subject>`, and what a queued run does under the run's requester.

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `POST /flows/{pipelineId}/release` | operate | body `keys`, `run` (false), `pool`; `interface`, `partition` | Releases the interface's blocked records (all, or `keys`). 200 with the count, or 202 with a deliver run under the last submission's parameter values when `run` is true. |
| `POST /records/{flowId}/{key}/release` | operate | body `run` (false), `pool` | The same for one record. |
| `POST /flows/{pipelineId}/issues/{issue}/release` | operate | body `run` (false), `pool`; `interface`, `partition` | Releases every record one issue keeps blocked, recorded with the issue and its pattern. |
| `POST /flows/{pipelineId}/adopt` | operate | body `confirm` (the partition the ledgers are kept in, typed back), `keys` or `filter` with `expected` (neither: every record held for an id OSDU holds), `run` (false), `pool`; `interface`, `partition` | Asks records held because OSDU already holds a record at the id each one's key makes to adopt it; the flow's next run takes each over at the version OSDU holds then ([Adopting a record OSDU already holds](record-lifecycle.md#adopting-a-record-osdu-already-holds)). Naming no record and no interface asks every such record of every interface of the source, and its run is the whole source's. 200 with `selected` and `asked`, or 202 with a deliver run when `run` is true and a record was asked; 400 without `confirm` or with another partition, 409 when the ledger keeps no records in a partition yet. |
| `POST /records/{flowId}/{key}/adopt` | operate | body `run` (false), `pool` | The same for one record, without `confirm`. 200 with `selected` and `asked`, or 202 with a deliver run scoped to the record when `run` is true; 409 when the record is not held for such an id, claims an id, is held by a delivery or is asked already, saying which. |
| `POST /flows/{pipelineId}/issues/{issue}/adopt` | operate | body `confirm` (the partition the ledger is kept in, typed back), `run` (false), `pool`; `interface`, `partition` | Asks every record one issue keeps held for an id OSDU holds to adopt it, recorded with the issue and its pattern. 200 with `asked`, or 202 with a deliver run; 400 without `confirm` or with another partition, 404 for an issue that keeps no record blocked. |
| `POST /flows/{pipelineId}/redeliver` | operate | body `scope` (the part: `all`, `record`, `files`, `bulk`, `workflow` as the route sends them), `keys` or `filter` with `expected`, `run` (true), `pool`; `interface`, `partition` | Sends records again whatever their hashes say: the named ones are marked here, every delivered record when none is named (marked by the run itself). 202 with the run, or 200 with the count when `run` is false. |
| `POST /records/{flowId}/{key}/redeliver` | operate | body `scope`, `run` (true), `pool` | The same for one record. |
| `POST /flows/{pipelineId}/rerender` | operate | body `keys` or `filter` with `expected`, `run` (true), `pool`; `interface`, `partition` | Brings records up to date: rendered again under today's rules and sent only where a hash moved. |
| `POST /flows/{pipelineId}/rerender/preview` | operate | as `rerender` | 202 with a `plan` run that says what bringing the selection up to date would send, sending nothing. |
| `POST /flows/{pipelineId}/sync` | operate | body `keys` or `filter` with `expected` (neither: every record); `interface`, `partition` | 202 with a sync run that reconciles the ledger with the ingestion tables. Sends nothing to OSDU. |
| `POST /records/{flowId}/{key}/sync` | operate | | The same for one record. |
| `POST /records/{flowId}/{key}/verify` | operate | | 202 with a verify run that compares what OSDU holds with the ledger and records drift. |
| `POST /cache/tags/decide` | operate | body `tagIds`, `approve` | Approves or rejects cache changes. An approved change is carried to its records by the rollout ([the control plane](control-plane.md)). |
| `POST /flows/{pipelineId}/lift-removal` | operate | body `keys` (1 to 1,000 delivery keys), `run` (false), `pool`; `interface`, `partition` | Lifts the deletions a [removal flow](../flow/removal.md) found of the records named, in one `lift-removal` activity: their keys are no longer kept out, and a record taken out is planned again. 200 with `lifted` (a key the ledger keeps out for no removal is passed over), or 202 with a deliver run when `run` is true and a record was lifted; 400 for no key or too many. |
| `POST /records/{flowId}/{key}/lift-removal` | operate | body `run` (false), `pool` | The same for one record, its deliver run scoped to it. 409 when the ledger keeps its key out for no removal. |

A `filter` is the record listing's filter (`status`, `search`, `mode`, `submissionId`, `runId`, `drifted`,
`deliveredBy`, `issue`). `expected` is the count the operator was shown; when the filter no longer resolves to it the
request is refused with 409, so a set that changed underneath is never acted on.

## Reading OSDU and the source

Each runs in the control plane with the flow's credentials and answers JSON; nothing is written.

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `POST /flows/{pipelineId}/probe` | operate | `interface`, `partition` | Whether the target answers under the flow's credentials: reachable, status, the path asked. A refusal is an answer. Recorded as a `probe` activity under the caller and counted on the probe metric; 422 `The probe could not be recorded` for a flow whose ledger cannot be placed, which is then not probed. |
| `POST /records/{flowId}/{key}/read` | operate | body `version` | The OSDU record the ledger's record claimed, at its latest or at `version`, with its version list; a record that never queued a document has none to read. |
| `POST /flows/{pipelineId}/osdu/read` | operate | body `targetId`; `interface`, `partition` | Any OSDU record by id through the flow's route, at its latest version. |
| `POST /flows/{pipelineId}/osdu/validate` | operate | body `targetId`, `version`, `schema` (`osdu` or `template`), `templateVersion`, `mapping`; `interface`, `partition` | One record checked against the schema of its kind, and against the assertions of the flow's own mapping, or of the synced mapping whose id `mapping` names. 409 for a route that keeps no record in storage (`dspdm`, `etp`). |
| `POST /records/{flowId}/{key}/source` | operate | | The record's rows as the ingestion tables hold them now: the record row with its system columns, child datasets, origin file and row. |
| `POST /records/{flowId}/{key}/preview` | operate | | The record rendered now from its current row, in the scope it was last planned under. |
| `POST /flows/{pipelineId}/preview` | operate | body `key`, `values`; `interface`, `partition` | One record rendered as a delivery would render it: the scope's first, or the one `key` names. A key that names no row is an answer (`found` false). |
| `POST /flows/{pipelineId}/scope-values` | operate | `interface`, `partition` | The values each scope parameter can take, read from the record table. A run form looks one parameter's values up, narrowed to what is typed, through SQLFlow's `POST /pipelines/{id}/parameters/{name}/values`, which the module answers with the same read ([parameters](../flow/parameters.md#lookups)). |

## Removing records and deleting the ledger

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `POST /flows/{pipelineId}/records/remove/preview` | operate | body as `remove`; `interface`, `partition` | What a removal would take away, and from which target. |
| `POST /flows/{pipelineId}/records/remove` | operate | body `scope` (`record`, `previous`, `history`, `everything`), `keys` or `filter` with `expected`, `purgeLedger`, `pool`; `interface`, `partition` | 202 with `runId`, `status`, `scope`, `records` and `purgeLedger`: the `remove` run of the flow that removes the records from OSDU. `purgeLedger` (with `record` or `everything` only) also deletes from the ledger each record OSDU answered for. |
| `POST /records/{flowId}/{key}/delete` | operate | body `scope`, `purgeLedger`, `pool` | The same removal for one record. |
| `POST /flows/{pipelineId}/records/purge` | operate | body `keys` or `filter` with `expected`, or neither; `interface`, `partition` | Deletes records already removed from OSDU from the ledger, asking nothing of OSDU; a record OSDU may still hold is counted as left. |
| `POST /records/{flowId}/{key}/purge` | operate | | The same for one record; 409 for a record OSDU may still hold. |
| `POST /flows/{pipelineId}/ledger/delete` | operate | body `confirm` (the partition the ledger is kept in), `pool`; `partition` | 202 with the run that removes every record of every interface from OSDU, then deletes each ledger. 400 without a matching `confirm`; 409 when no ledger exists or a deletion is already queued or running. |

See [removal and reversal](removal-and-reversal.md) for what each scope does.

## Missing documents

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /ledgers/orphaned` | read | `partition` (else the workbench's, else every partition) | The missing documents: each flow no repository declares any more whose ledgers hold anything, with `kind`, `flowName`, `relativePath` and `leftUtc` (where and when a sync last found it), `rows` and `claimed` (what it left behind), `ledgers` (one per interface and partition) and `candidates` (the declared flows that can take it over, the likeliest first, each with what it `shares`, how many ledgers it `takes`, and whether it is `suggested`). |
| `POST /ledgers/orphaned/takeover` | operate | body `kind`, `from` (the missing flow), `to` (the declared flow), `partition` (else the workbench's, else every partition) | 200 with each ledger's move (`outcome`: `completed`, `running` for one that waits for work in flight, `failed`, or `skipped` for one the flow keeps no counterpart of, which stays orphaned) and how many ended each way. 400 without `kind`, `from` or `to`; 409 when the document is not missing, or no repository (or more than one) declares the flow. |

A takeover moves each ledger as the caller, as [Renaming a pipeline](renaming-a-pipeline.md#missing-documents-and-taking-them-over) describes.

## Issues and reversals

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /flows/{pipelineId}/issues` | read | `max` (100, at most 500), `interface`, `partition` | What keeps the interface's records blocked, grouped by issue, the most records first. |
| `GET /flows/{pipelineId}/issues/{issue}` | read | `samples` (5, at most 20), `interface`, `partition` | One issue with the files its records came from and samples spread across it. 400 for an id that is not sixteen hexadecimal characters. |
| `GET /flows/{pipelineId}/reversible` | read | `runId` or `submissionId`, `interface`, `partition` | Whether the run or submission has anything to reverse now, and why not. |
| `GET /flows/{pipelineId}/reversals` | read | `max` (50, at most 200), `runId`, `submissionId`, `interface`, `partition` | The interface's reversals, newest first; with a run or submission, that source's reversal, counted. |
| `GET /reversals/{reversalId}` | read | | One reversal, counted, with its pipeline, submissions and runs. |
| `GET /reversals/{reversalId}/records` | read | `outcome` (`pending` for unsettled), `after`, `limit` (100, at most 1,000) | A page of its records in key order. |
| `POST /flows/{pipelineId}/reverse/preview` | operate | body `runId` or `submissionId`; `interface`, `partition` | What reversing it would reach, deciding a sample as the run would; nothing is written. |
| `POST /flows/{pipelineId}/reverse` | operate | body `runId` or `submissionId`, `expected`, `pool`; `interface`, `partition` | 202 with the reverse run. Asking again resumes a reversal that stopped. |

## Mappings, templates and the mapping builder

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /mappings` | read | `repoId`, `status` | The mapping documents the repositories hold, as the last sync read them, each with the template version it fills (`templateVersion`) and the template folder file it was settled from (`templatePath`, empty when no folder on its path holds it). |
| `GET /mappings/{mappingId}` | read | | One mapping with its YAML. |
| `GET /mappings/{mappingId}/flows` | read | | The interfaces of delivery flows that render with the mapping, in every partition. |
| `POST /flows/{pipelineId}/check-values` | operate | body `targets` (template paths, at most 200), `values`, `maxRows` (10,000; 0 for the whole scope), `samples` (20), `skipSamples`, `mapping`; `interface`, `partition` | 202 with a compute task that finds the rows whose values the mapping's template will not take. |
| `GET /templates` | read | | Every file of every synced repository's template folders, as its last sync recorded it: `repoId`, `repository`, `path`, `folder`, the `kind` and `version` it holds or the `problem` why it holds none, `seenUtc`, and the kind its template is linked to as its `parent`, when it is ([what the sync records](templates.md#what-the-sync-records), [linked templates](templates.md#linked-templates)). |
| `GET /templates/detail` | read | `repoId`, `path`, `scope` | The template one repository's file holds, read from the file and laid out variable by variable, with the file it was read from (`file`), the kind it is linked to (`parent`), and the kinds of the repository's templates linked to it, which a mapping of it can link as children with `$child` (`children`, [mapping $child](../flow/mapping-child.md)); with `scope`, the cached types each variable can read. 404 for a file the sync did not record; 422 for one that holds no template, has changed since the sync, or whose repository's folder cannot be had on this host. |
| `GET /templates/schema` | read | `repoId`, `path` | The template one repository's file holds, as a template file to save under the file's own name ([a template as a file](templates.md#a-template-as-a-file)); 404 and 422 as for `detail`. |
| `GET /templates/resolve` | read | `repoId`, `path`, `kind`, `version` | The template a document at `path` in a synced repository reads for `kind` (exactly `version` when given), as the template folders on its path held them at the last sync ([template folders](templates.md#template-folders)): `found`, `kind`, `version`, the file (`file`) or why there is none (`problem`), and `suggestedPath`, where the nearest folder would keep the template's file. |
| `POST /templates/preview` | read | body `kind`, `schema`, `scope`, `release` | A schema laid out without keeping it, bundled with the shared schemas it refers to from `release`. |
| `GET /templates/osdu/releases` | read | | The releases of the OSDU data definitions, newest first, and which are in the local copy. |
| `GET /templates/osdu/schemas` | read | `release` (newest when left out) | Every record kind a release publishes. |
| `GET /templates/osdu/schema` | read | `release`, `kind` | One kind's schema from a release, bundled, with the template version its file holds. |
| `GET /templates/osdu/file` | read | `release`, `kind` | One kind's schema from a release as the template file a template folder keeps, bundled and naming its kind, to save under the kind's file name ([the OSDU data definitions](templates.md#the-osdu-data-definitions)). |
| `GET /templates/osdu/compare` | read | `fromRelease`, `fromKind`, `toRelease`, `toKind` | Two versions of one kind compared: breaking, additive and wording changes, variable by variable. |
| `POST /templates/osdu/sync` | operate | body `release` | Reads the release list again and downloads the release when it is not on disk. |
| `GET /mapping-builder/repos` | read | | The repositories a mapping can be written for, with their delivery flows and the parameters each renders with. |
| `GET /mapping-builder/caches` | read | | Every partition cache, its flows, current version and types. |
| `POST /mapping-builder/draft` | read | body `scope`, `repoId`, `templatePath`, `name`, `mappingVersion`, `system` | A new mapping draft for the template a synced repository's file holds. |
| `POST /mapping-builder/compose` | read | body `scope`, `draft`, `parameters`, `repoId`, `path` | The draft as YAML, its issues, and whether it loads and passes the preflight. |
| `POST /mapping-builder/parse` | read | body `yaml`, `path`, `repoId` | A mapping document as a draft the builder edits. |
| `POST /mapping-builder/shape` | read | body `yaml`, `path`, `parameters`, `repoId` | The shape of the records the mapping renders, drawn against its template without a row or a cache. |
| `POST /mapping-builder/coverage` | read | body `yaml`, `path`, `repoId` | What the mapping fills of its template and what it leaves required and empty. |

With `repoId`, a document of that synced repository reads its template from the template folders on its `path`, as the
last sync recorded them, each read from its file in the repository's synced folder; without it, from the templates every
synced repository holds: the version it names, or for a kind named alone the one the repository synced most recently
holds. A document that does not load, or whose template cannot be read, is an answer with its issue on `shape` and
`coverage`, not an HTTP error. See [templates](templates.md) and [preflight](preflight.md).

## Caches

Every cache route but `GET /caches` names one partition's cache with `scope` (or `partition` on `/cache/streams`).

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /caches` | read | `repoId` | Every partition cache: the cache flows filling it (each with the `retentionDays` it declares), the types they declare, the current version, and the partition's `retentionDays`. |
| `GET /cache/streams` | read | `partition` (required) | Where each cached type comes from (the flows upstream of its cache flow), which delivery flows read it, and its state. |
| `GET /cache/items` | read | `scope`, `type`, `search`, `version`, `page`, `pageSize` | The cached records of one version (the current one by default). |
| `GET /cache/versions` | read | `scope` | The versions, newest first, with the flow and run that wrote each, and `prunedUtc` once the retention pruned a version's records. |
| `GET /cache/history` | read | `scope`, `type` | Each version with how many records it changed, added and removed, and `beforePruned` when the version before it is pruned. |
| `GET /cache/diff` | read | `scope`, `from`, `to`, `type`, `change`, `search`, `page`, `pageSize` | What changed between two versions (`to` defaults to the current one). |
| `GET /cache/tags` | read | `status` (`pending`, `approved`, `rolling`, `rejected`, `applied`), `scope`, `page`, `pageSize` | The cache changes delivered records were built from, and what each reaches. |
| `GET /cache/gaps` | read | `scope`, `type`, `empty`, `page`, `pageSize` | What delivered records were built without, most records first. |
| `POST /cache/prune/preview` | operate | body `scope`, `keepDays` | What purging the cache's history, keeping `keepDays` (0 to 36500; the partition's retention when left out), would prune: `kept`, `pruned`, `rows`, `summary`. Changes nothing. |
| `POST /cache/prune` | admin | body `scope`, `keepDays` | Purges the cache's history as the preview says, and records the admin and the time on every version it prunes. |

A version whose records the partition's [retention](partition-cache.md#retention) pruned stays listed by
`/cache/versions` and `/cache/history`, its counts unchanged. `GET /cache/items` naming it, and `GET /cache/diff` naming
it on either side, answer `410 Gone` (`Version <v> of the cache of partition '<p>' was pruned at <time> by the cache's
retention: ...`), which a client tells apart from `404` for a version the cache never held. A purge answers `400` for days
out of range and `404` for a partition whose cache holds no version.

See [the partition cache](partition-cache.md).

## The OSDU order

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /order/releases` | read | | The built releases of OSDU's lineage, newest built first, each with its commit, who built it and when, its kinds and relationships, and the kinds it could not read. |
| `POST /order/releases` | operate | body `release` | Builds the release (the newest when null) from the OSDU data definitions, downloading it when the control plane does not have it, and replaces an earlier build of it. `404` for a release the data definitions do not have, `502` when they cannot be reached. |
| `GET /order/releases/{release}/entities` | read | | Every entity type of a built release with its kinds and how many types it refers to and is referred to by. `404` for a release not built. |
| `GET /order/releases/{release}/graph` | read | `entity` (required), `depth` (1 to 4), `direction` (`both`, `out`, `in`) | The part of the release around the entity type: the types and the references between them, with the properties behind each, cut at 150 types (`truncated`). |
| `GET /order/partitions` | read | | Each partition's order with every repository's deliveries: levels, deliveries, references kept and cut, warnings, and what the order could not read. |
| `GET /order/schedules` | read | | Every schedule running a delivery, the schedules it runs best after with the timing (`chained`, `firesFirst`, `firesAfter`, `notFiring`, `unknown`) and the reasons, the deliveries it refers to that no schedule runs, and the schedules that refer to each other in a circle. |

See [the OSDU order](osdu-order.md).

## Partitions and the central configuration

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /partitions` | read | | Every registered partition, and every partition something is still kept under, each with `configured`: true for one the control plane's configuration names. |
| `POST /partitions` | admin | body `name`, `description`, `isDefault` | 201 with the partition; the first one registered becomes the default. 409 `Set by configuration` for `isDefault` while a configured partition is the default. |
| `PUT /partitions/{name}` | admin | body `description` | Sets what a partition is for. 409 `Set by configuration` for a configured partition. |
| `POST /partitions/{name}/default` | admin | | Makes it the partition a run that names none runs in. 409 `Set by configuration` while a configured partition is the default. |
| `DELETE /partitions/{name}` | admin | | 204; nothing kept under it is deleted. The default is refused while others are registered, and a configured partition with 409 `Set by configuration`. |
| `GET /config` | read | `repoId`, `partition` | Every property of every scope, values shown (a property never holds a secret), ordered by name, repository and partition, each with `configured`: true for a value the control plane's configuration sets; `repoId` narrows it to one repository's, `partition` to one partition's, both to that repository's in that partition. 400 for a `partition` that is not a data-partition-id. |
| `PUT /config/{name}` | admin | body `value`, `description`; `repoId`, `partition` | Sets a property for the control plane, a repository or a partition, through the store checks `sqlflow config set` makes (value and description trimmed). 400 `Configuration property refused` on a refusal, 404 `Repository not found` for a `repoId` the catalog does not hold, 409 `Set by configuration` for a value the configuration sets. |
| `DELETE /config/{name}` | admin | `repoId`, `partition` | 204, or 404 when it was not set there; 409 `Set by configuration` for a value the configuration sets. The repository is not looked up, so a deleted repository's properties can be removed. |
| `GET /config/effective/{repoId}` | read | `partition` | What a run of the repository's flows is given, flattened. |

The control plane's configuration names the partitions and their values (`Osdu:Partitions`), and registers them when it
starts. What it sets is changed only there: each `409 Set by configuration` carries a message naming the setting to
change, for example
`Partition 'dev' is set by the control plane's configuration, which describes it: change Osdu:Partitions:dev:Description there.`

See [partitions](partitions.md), [configured partitions](partitions.md#configured-partitions),
[sqlflow partition](../cli/partition.md) and [sqlflow config](../cli/config.md).

## The explorer and search terms

The explorer reads OSDU through the partition's own connection, the platform and client its configuration names, and
needs no flow ([how it reaches OSDU](explorer.md#how-it-reaches-osdu)); the GUI names the partition with `partition` or
the workbench header.

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /explorer/connection` | read | `partition` | Whether the partition's connection can be made: `partition`, `available`, the `endpoint` it reaches, or the `reason` it cannot be. |
| `POST /explorer/types` | operate | body as `search`; `partition` | The kinds a search finds, each with its count. |
| `POST /explorer/search` | operate | body `text`, `lucene`, `mentions`, `kind`, `filters`, `sort` (`relevance`, `modified`, `created`), `offset`, `limit` (100), `facet`, `columns`; `partition` | One page of the records a search finds. |
| `POST /explorer/fields` | operate | body `kind`; `partition` | The properties a kind's records hold. |
| `POST /explorer/read` | operate | body `targetId`, `version`; `partition` | One record from the storage service, with its version list. |
| `POST /explorer/validate` | operate | body `targetId`, `version`, `schema`, `templateVersion`, `mapping`; `partition` | One record checked against its kind's schema, and with `mapping` (a synced mapping's id, as `GET /mappings` lists it) against that mapping's record-stage assertions, under the verdict's `assertions`. |
| `POST /explorer/validate-list` | operate | body `search`, `max` (1 to 1,000), `schema`, `mapping`; `partition` | The records a search finds checked and counted by the rules they break, and with `mapping` by the assertions they fail (`mapping`, `asserted`, `failingAssertions`, `assertions`, and each record's `assertionFailures`). |
| `POST /explorer/element-queries` | operate | body `kind`, `path`, `section`, `value`, `values` | The Lucene query finding records that hold an element, from the template of the kind a synced repository holds alone. |
| `POST /explorer/referenced-by` | operate | body `type`, `refresh`; `partition` | The types whose records name records of a type, from the partition's schemas. |
| `GET /explorer/dimension/candidates` | operate | `kind` | The keys the template of a kind a synced repository holds suggests for a dimension. |
| `POST /explorer/dimension/keys` | operate | body `kind`, `query`, `path`; `partition` | The commonest keys of a drafted dimension's path. |
| `POST /explorer/dimension/compose` | operate | body `draft`, `example`; `partition` | A drafted dimension as YAML, checked; nothing is saved. |
| `GET /search-terms` | read | `entityType`, `kind`, `orphans` | The search terms of an entity type, or of every one. |
| `GET /search-terms/entity-types` | read | | The entity types with how many terms each has. |
| `GET /search-terms/{termId}` | read | `entityType` | One term; 404 when unknown. |
| `PUT /search-terms/{termId}` | author | `entityType`; body `name`, `excluded`, `route`, `note` | Refines a term. |
| `DELETE /search-terms/{termId}/refinement` | author | | Removes what people made of the term. |
| `POST /search-terms/delete` | author | body `terms`, `entityType` | Deletes terms from the search in one save. |
| `POST /search-terms/restore` | author | body `terms`, `entityType` | Offers deleted terms again. |

See [the explorer](explorer.md) and [search terms](search-terms.md).

## Assertions, dimensions and inventories

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /assertions` | read | `partition` | The board of every active assertion flow. |
| `GET /flows/{pipelineId}/assertions` | read | `partition` | One assertion flow's board; 409 for another kind. |
| `GET /flows/{pipelineId}/assertion-runs` | read | `partition`, `max` (50, at most 500) | Its runs, newest first. |
| `GET /flows/{pipelineId}/assertion-matrix` | read | `partition`, `runs` (30, at most 200) | Recent runs against every test. |
| `GET /flows/{pipelineId}/assertions/{test}/history` | read | `partition`, `runs` (30, at most 200) | One test's results over its recent runs. |
| `GET /assertion-runs/{assertionRunId}` | read | | One run with every test's result. |
| `GET /assertion-runs/{assertionRunId}/report` | read | `format` (`json`, `md`, `html`, `junit`) | The run's report as a file; 400 for another format. |
| `GET /dimensions` | read | `partition` | Every active dimension flow's dimensions. |
| `GET /flows/{pipelineId}/dimensions` | read | `partition` | One dimension flow's dimensions; 409 for another kind. |
| `GET /flows/{pipelineId}/dimensions/{name}/blueprint` | read | `partition` | How a dimension is built: its YAML beside what each line reads and makes. |
| `GET /dimensions/{dimensionId}` | read | | One dimension, its pipeline and parameters. |
| `GET /dimensions/{dimensionId}/table` | read | `search`, `attr`, `order`, `dir`, `offset`, `limit` (100, at most 1,000) | A page of the dimension's own table. |
| `GET /dimensions/{dimensionId}/values` | read | `search`, `order`, `removed`, `attr`, `after`, `limit` | A page of its values. |
| `GET /dimensions/{dimensionId}/values/{valueId}` | read | | One value with its keys, filter and history. |
| `GET /dimensions/{dimensionId}/keys` | read | `search`, `value`, `leftOut`, `removed`, `attr`, `order`, `after`, `limit` | A page of its keys. |
| `GET /dimensions/{dimensionId}/attributes/{name}` | read | `search`, `limit`, `attr`, `value` | The values an attribute holds among its keys. |
| `GET /dimensions/{dimensionId}/builds` | read | `max` (30, at most 500) | Its builds, newest first. |
| `GET /dimensions/{dimensionId}/changes` | read | `build`, `key`, `value`, `change`, `before`, `limit` | A page of its change log. |
| `GET /dimensions/{dimensionId}/export` | read | `set` (`values`, `keys`, `table`), `format` (`csv`, `jsonl`) | The dimension as a file, written a page at a time. |
| `GET /runs/{runId}/dimension-builds` | read | | The builds a platform run made. |
| `POST /dimensions/{dimensionId}/filter` | read | body `valueIds`, `values` | The search filter of the values named. Reads only. |
| `POST /dimensions/search` | read | body `picks`, `kind`, `within` | The OSDU search finding records that hold the picked values. Nothing is sent to OSDU. |
| `DELETE /dimensions/{dimensionId}` | admin | | Removes a dimension its flow no longer declares, and everything kept of it. 409 for one the flow declares. |
| `GET /dimensions/views` | read | | Every view: those the active dimension flows declare, and those builds wrote that no flow declares any more. |
| `GET /flows/{pipelineId}/dimensions/views` | read | | One dimension flow's views; 409 for another kind. |
| `GET /dimensions/views/{name}` | read | | One view: what its flow declares and what a build recorded, its newest checks, its SQL as written and as declared, and its YAML. |
| `DELETE /dimensions/views/{name}` | admin | | Removes a view no flow declares any more: the view and its record. 409 for one its flow declares. |
| `GET /inventories` | read | `partition` | The inventories of the partition, or of every partition. |
| `GET /inventories/lookup` | read | `id`, `partition` | What every inventory of the partition holds of one OSDU id; 400 when no partition is named or sent. |
| `GET /flows/{pipelineId}/inventories` | read | `partition` | An inventory flow's inventories; 409 for another kind. |
| `GET /inventories/{partition}/{inventoryId}` | read | | One inventory with its counts by finding, owners and last runs. |
| `GET /inventories/{partition}/{inventoryId}/records` | read | `finding`, `selection`, `after`, `limit` (100, at most 1,000) | A page of its ids; with `selection`, only those the selection holds, each with the version and values its match read. |
| `GET /inventories/{partition}/{inventoryId}/runs` | read | `limit` (30, at most 200) | Its builds and reconciles. |
| `GET /inventories/{partition}/{inventoryId}/export` | read | `finding`, `selection` | Its ids as CSV; with `selection`, only those the selection holds, with a column for each property its conditions ask. |
| `GET /inventories/{partition}/{inventoryId}/selections/{selectionId}` | read | | One selection: where its match stands, whether it is stale, what it read and found, and its ids counted by finding; for one a duplicate check made, the check and its key. 404 for one of another inventory. |
| `GET /inventories/{partition}/{inventoryId}/duplicates` | read | `limit` (20, at most 100) | Its duplicate checks, newest first, each as it stands. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}` | read | | One check: its key, the properties it downloaded, its table and who set its key, where it stands (downloading or picking, while it runs), whether it is stale, what it found, its groups by outcome and by size, the groups waiting to be picked with their copies, and whether the selection last made of its picks still stands. 404 for one of another inventory. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups` | read | `outcome` (`review`, `picked`, `chosen`, `settled`, `pending`), `after`, `limit` (50, at most 500) | A page of its groups, the largest first, each with its copies (every copy beside the inventory's row of it) when it holds at most 100. 400 for another outcome. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/copies` | read | `after`, `limit` (100, at most 1,000) | A page of one group's copies: how a group too large to list with its page is read. 404 for a group the check does not hold. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/export` | read | | Every copy of every group as CSV, a column for each property of the key. |
| `POST /inventories/{partition}/{inventoryId}/duplicates` | operate | body `key` (each `path`), `refresh`, `pool` | Finds the ids that hold equal values at the key: 200 with the inventory's current check grouped by the key in SQL when it downloaded every property of it, else 202 with a new check downloading them, queued as a `duplicates` run of the flow; `refresh` always downloads. 400 for a key that is not one; 409 for an inventory never built or no longer declared, or a current check downloading or picking with another key. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/pick` | operate | body `pool` | Asks for a pick: a `duplicates` run reading the copies of the groups waiting to be picked from storage, and the copy each keeps picked. 202 with the check. 409 for a check not completed, picking, stale, holding no group to pick, or a flow no longer declared. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/keep` | operate | body `inventoryRecordId` | Keeps that copy of the group, recorded as the caller's choice, the group judged again with it kept; null takes a choice back. 200 with the group. 400 for a copy not of the group or not held by storage; 409 for a check not completed or picking, stale, a group waiting to be picked or too broad to read, or a flow no longer declared. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/selection` | operate | | Makes the copies the check picked to remove a selection a removal takes: 200 with the selection already made when no choice was written since, else 201. 409 for a check not completed, stale, or one that picked nothing. |
| `GET /inventories/{partition}/{inventoryId}/removals` | read | `limit` | Its removals, newest first. |
| `GET /inventories/{partition}/removals/{removalId}` | read | | One removal. |
| `GET /inventories/{partition}/removals/{removalId}/items` | read | `outcome`, `after`, `limit` | What the removal did to each id. |
| `POST /inventories/{partition}/{inventoryId}/removals` | operate | body `finding`, `scope`, `expected`, `ids` or `selection`, `confirm`, `pool` | 202 with the run that removes the ids of one finding from OSDU (every one, those named, or those a selection holds), or (`gone`, scope `inventory`) from the inventory. |
| `POST /inventories/{partition}/{inventoryId}/selections` | operate | body `filters`, `refresh`, `pool` | Selects the ids that hold what the conditions ask in OSDU: 200 with the match already made of the same conditions against the inventory's last build, else 202 with a new one queued as a `match` run of the flow. 400 for conditions that cannot be asked; 409 for an inventory never built or no longer declared. |
| `POST /inventories/{partition}/{inventoryId}/repairs` | operate | body `expected`, `ids`, `newIds`, `confirm`, `pool` | Delivers again the records behind missing ids through their flows, under their own ids or new ones: 202 with each flow's marked records and queued run, and the ids left with why (200 when it queued none). |

See [assertion flows](../flow/assertion.md), [dimension flows](../flow/dimension.md) and
[inventory flows](../flow/inventory.md).

## Removal flows

A removal flow keeps its runs, and the deletions it finds, under the ledgers of the delivery flows it names
([removal flow](../flow/removal.md)), so its runs are read by its partition and the deletions by their ledger. The partition
is the one the query names, else the workbench's (`X-Osdu-Partition`).

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /removals` | read | `partition` | Every active removal flow that removes in the partition (at most 200), each as the next route answers it; one whose catalog copy does not load is listed with its problem. |
| `GET /removals/flows/{pipelineId}` | read | `partition` | One removal flow: its guard and parameters, and each removal with the delivery flow (and pipeline) it writes to, what it reads, that ledger's identity in the partition, its deletions by state, the count waiting for this removal's approval and its last run. A partition the flow does not remove in, or a copy that does not load, is answered with the flow as declared and `problem`; 404 for another kind. |
| `GET /removals/flows/{pipelineId}/runs` | read | `partition`, `entry`, `limit` (50, at most 1,000) | Its runs in the partition, newest first, of one removal with `entry`; 404 for a removal it does not declare. |
| `GET /removals/{partition}/runs/{removalRunId}` | read | | One run with every count it kept. |
| `GET /removals/ledgers/{ledgerFlowId}/keys` | read | `state` (`pending`, `awaiting-approval`, `removed`), `run` (the removal run that found them), `after` (a delivery key), `limit` (200, at most 1,000) | A page of a delivery ledger's deletions in key order, with `next` (the `after` of the next page, when there is one) and `counts` by state. 400 for another state. |
| `GET /removals/ledgers/{ledgerFlowId}/runs` | read | `limit` (50, at most 1,000) | The removal runs that wrote to a delivery ledger, newest first, whichever removal flow ran them. |
| `POST /removals/flows/{pipelineId}/approve` | operate | body `entry`, `expected` (the count the operator was shown waiting for approval), `confirm` (the partition, typed back), `pool` | 202 with the `remove` run that approves what the removal's guard held and takes it out ([The guard](../flow/removal.md#the-guard)); the run checks the count again on the node. 400 without a removal, a count or a partition; 404 for a removal the flow does not declare; 409 when the delivery flow keeps no ledger in the partition, another number waits than the caller was shown, or a run of the flow is queued or running. |

The deletions of a record are also on its detail (`GET /records/{flowId}/{key}`, `removedKey`), and delivering records
again is an intervention (`POST /flows/{pipelineId}/lift-removal`, [Interventions](#interventions)).

## Retention

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /api/v1/maintenance/storage` and the routes under `/api/v1/maintenance/areas/{areaId}` | read, operate, admin | SQLFlow's [Maintenance](../../../../sqlflow/docs/reference/concepts/maintenance.md) routes | The `osdu` schema's storage areas beside SQLFlow's; the three that take a limit are `osdu.attempts`, `osdu.activity-logs` and `osdu.assertion-runs` ([What the osdu schema stores](storage-and-retention.md)). The module's own retention pass, `POST /ledger/prune`, is gone. |

## Running an OSDU flow, and search

There is no delivery route for starting a run: an OSDU flow is run with SQLFlow's `POST /api/v1/runs`, whose body carries
`operation`, `values` and `payload` as the kind defines them ([running an OSDU flow](../cli/run.md)). The module also adds
a `records` category to SQLFlow's `GET /api/v1/search/all`, answered from the same ledger lookup as `GET /records`.

## See also

- [The control plane: what OSDU Delivery adds](control-plane.md)
- [Authentication and identity](authentication-and-identity.md)
- [The ledger](ledger.md)
- [SQLFlow's control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md)
