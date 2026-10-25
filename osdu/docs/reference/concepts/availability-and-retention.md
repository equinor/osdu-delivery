---
id: delivery-concept-availability-and-retention
title: "Availability, recovery, retention and backup of the delivery ledger, and the size ceilings to set"
type: concept
summary: "What keeps delivering when the control plane or a node stops, how deliveries recover, what a backup must hold, and the size limits to set."
keywords:
  - availability
  - recovery
  - outage
  - retention
  - maintenance
  - backup
  - restore
  - size ceiling
  - maxRequestBodyBytes
  - lease recovery
  - schema version
related:
  - delivery-concept-storage-and-retention
  - delivery-concept-ledger
  - delivery-concept-submissions
  - delivery-concept-control-plane
  - delivery-cli-db
  - delivery-guide-deployment
  - delivery-guide-operations-runbook
  - concept-control-plane
  - cli-worker
sourceRefs:
  - osdu/deploy/bicep/control-plane.bicep
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/bicep/worker.bicep
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Identity/TargetId.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheUpdateRolloutService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RecordIdentityBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RecordProblemBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/SearchTermCatalogRefreshService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CentralConfigurationStep.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/PartitionReadiness.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ScheduledTargetProbeService.cs
  - sqlflow/src/SqlFlow.ControlPlane/Infrastructure/ModuleDatabaseVerification.cs
  - sqlflow/src/SqlFlow.Dispatch/DispatchOptions.cs
  - sqlflow/src/SqlFlow.Catalog/ComputeTaskStore.cs
---

# Availability, recovery, retention and backup of the delivery ledger, and the size ceilings to set

This page is for the people who run an OSDU Delivery estate: what keeps working when the control plane or a node stops,
how deliveries recover without anyone editing the ledger, where what may be aged out of the `osdu` schema is decided,
what a backup must hold to keep every delivered record reconstructible, and which size limits to set on purpose. The control plane, the
run queue and the node fleet are SQLFlow's; see SQLFlow's [control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md)
and [`sqlflow worker`](../../../../sqlflow/docs/reference/cli/worker.md) for dispatch, leases on runs, requeues and drains.
This page says what OSDU Delivery adds.

## Availability

The shipped Azure deployment runs the control plane as one replica (`minReplicas` and `maxReplicas` both 1 in
`control-plane.bicep`), because only the replica that holds SQLFlow's dispatch lease hands out work and a second replica
adds no dispatch capacity. Worker nodes scale from zero on the control plane's scale target.

**While the control plane is down:**

- The API and the GUI's OSDU pages answer nothing, and the CLI's remote verbs fail. No run is queued or handed out, no
  schedule fires, no repository sync runs, no approved cache change rolls out, and the module's background passes (below)
  pause.
- A worker node already delivering keeps delivering. It reaches OSDU with the flow's own credentials and the ledger with its
  own connection to the catalog's database (`SQLFLOW_CATALOG_DB`), neither through the control plane, so records go on being claimed,
  sent and settled, and every attempt is written. Leave the nodes alone: a stopped node costs the runs it holds their
  progress.
- `sqlflow run` on a workstation with the module database connection still delivers: it takes the ledger's own leases,
  which any number of runs share safely.

**While a node is down:** the records it was sending stay `delivering` under its leases until they run out
(`reliability.leaseSeconds`, 300 by default). Nothing is sent twice: every step a try completed is in the ledger, and the
next try resumes after it.

## Recovery

Most of it needs nobody.

| What | How it recovers |
| --- | --- |
| The `osdu` schema | The control plane applies pending migrations of SQLFlow's catalog and then of the module database when it starts, and stops when a database is missing, behind or ahead of the build. `/health/ready` carries the refusal (its `module-databases` check). |
| The configured partitions | Written into the registry once the module database verifies, and tried again every 30 seconds until a pass completes; writing them twice changes nothing. `/health/startup` stays red until then, and while no partition is registered (its `osdu-partitions` check; [configured partitions](partitions.md#configured-partitions)); the control plane stays ready and keeps serving the API. |
| Runs a stopped node held | SQLFlow requeues them once their run lease lapses, while they have execution attempts left. |
| Records a stopped worker was sending | Every claim of the flow first recovers its leases that ran out: it applies what the stopped worker had appended and hands the rest back to `pending` with the try charged and an attempt written, or `failed` and blocked once the record has used `reliability.retry.attempts` tries that way ([Submissions](submissions.md#leases-and-claims)). A run that finds records of its own submission still leased waits for the lease to run out, then recovers and sends them. |
| A submission whose run stopped mid-drain | A run executed again resumes it; otherwise the flow's next deliver run takes it over once no run holds it and its leases have run out, sends what it holds and closes it ([Recovering a stopped submission](submissions.md#recovering-a-stopped-submission)); a re-run (a deliver run with `submissionId`) or a `drain` of it still works at once. A released or recovered record of a submission that settled is sent by the flow's next deliver run. |
| What unfinished deliveries left in OSDU | The sweep at the end of the next deliver run or flow-wide drain undoes it ([Removal and reversal](removal-and-reversal.md#unfinished-deliveries-and-their-undo)). |
| Removals and value checks queued as tasks | SQLFlow fails a task no worker claimed within `Dispatch:TaskQueuedExpiryMinutes` (15) with `No worker claimed the task within <n> minutes...`; a running one is requeued. A removal that runs again reports what is already gone as `already-gone` and writes its own attempts. |
| Approved cache changes | Each resumes its rollout from its own cursor ([The partition cache](partition-cache.md)). |
| The identity and issue backfills | Resume where they stopped: each reads only what is left to do. |
| Interface descriptions and search terms | The interfaces of a repository not described yet, and every repository's search terms, are written once when the control plane starts, and again by each repository sync. |
| Target probes | A probe left open by a host that stopped, scheduled or asked for, is closed as unfinished 15 minutes after it started, by the next probe of that interface or the next scheduled pass. |
| Schedules | SQLFlow fires the next occurrence; a missed one fires only when the schedule declares `catchup: true`. A deliver run plans from its watermark, so one run after an outage covers every row that changed during it. |

Then the operator's own pass, in this order:

1. `sqlflow db status --db <ref>` (exit 2 means migrations are pending) and `sqlflow health`, or `/health/ready`: a red
   readiness naming the module database means the schema and the build disagree, and the message names the migration or
   version. Then `/health/startup` (`GET /api/v1/bootstrap` names the step): a red one the log explains with
   `OSDU Delivery has no partition to work against` means the configuration names no partition and none is registered
   by hand.
2. The fleet and the dispatcher (the GUI's Nodes page, `GET /api/v1/dispatch`): a pool with a backlog and no node online.
3. Runs that failed with an interrupted message, and the submissions they left `planned` or `running`: re-run or drain them.
4. Each flow's Records tab filtered to `delivering`: a record whose lease is in the past is recovered by the flow's next
   deliver run or drain; run `drain` if none is due.
5. Removals and value checks failed with `No worker claimed the task`: ask for them again.

No step needs a repair script or a hand edit of the ledger, and none exists: every recovery goes through the ledger, which
is what keeps a delivered record reconstructible.

## Retention

What may be removed from the `osdu` schema, and what never is, is set out area by area in
[What the osdu schema stores](storage-and-retention.md), and done from **Settings > Maintenance**, the page SQLFlow
keeps for the whole catalog database ([Maintenance](../../../../sqlflow/docs/reference/concepts/maintenance.md)). The
rule is the product's: every delivered record stays reconstructible from the ledger alone. So the records, their
submissions, their render context and every audit row are kept; work in progress and working sets clean themselves; and
three areas may be removed at a cost each states: the delivery attempts a later attempt of the same record follows
(each record's latest is kept), the free-text logs of finished activities (the activity rows stay), and the assertion
runs whose every result a later run superseded (each test's latest result stays). Nothing is removed until an admin sets
how long an area's data stays; the automatic cleanup then applies that limit every hour, on every replica, in bounded
statements that never hold a long lock on the tables the drains write to.

A limit is easy to set and hard to take back, so choose it in years rather than months: the delivery statistics, a
test's trend and a record's history read further back than any incident. If volume ever demands more than a limit
gives, partition the large tables by time in the module's model rather than delete more from them.

## Backup and restore

- **Back up the `osdu` schema with SQLFlow's catalog.** The schema lives in the catalog's database, so one backup covers
  both, and they restore to the same instant: a run row in the catalog and the submission, records and attempts it wrote
  are one unit of work.
- **The schema carries its version.** `[osdu].[__EFMigrationsHistory]` and `[osdu].[SchemaVersion]` come with the schema.
  A restore is usable only with a build that matches: a database behind the build is migrated on start, one ahead of it
  stops the host by name, and one paired with a SQLFlow catalog older than `SchemaVersion.MinimumCatalogMigration` is
  refused. Restore the image that goes with the backup, or migrate forward. Never edit the version row.
- **The ledger needs no database option of its own.** Its reads do not depend on snapshot isolation. The ingestion
  database a delivery flow reads does: a flow reads a record and its child rows as one instant unless it declares
  `source.incremental.isolation: readCommitted`, so a rebuilt source database needs `ALLOW_SNAPSHOT_ISOLATION ON` again.

What is not in the database and has to be restored beside it:

- **The repositories.** Flows, mappings and cache flows live in git; restore the commit the catalog records, and a sync
  rebuilds the read models.
- **The secrets.** Flows carry references only (`${env:...}`, `${keyvault:...}`), so the vault and the nodes' environment
  are their own backup.
- **The work locations.** A work batch names a file under the flow's `source.work`; without those files the batches still
  queued cannot be drained. Plan those scopes again (`replan`); records already delivered are skipped as unchanged.
- **The OSDU partition the ledger describes.** A ledger restored older than its partition holds records whose OSDU copy has
  moved on; a `verify` run finds them as drift, and a flow's Records tab lists them with **Drifted only**.

## Size ceilings

**Request bodies on the way to OSDU.** Set the request body ceiling of everything between the nodes and OSDU (the service,
the ingress, an API gateway) to one deliberate number and declare it on the flows as `reliability.maxRequestBodyBytes`
(0, the default, means not declared). On the routes that send payloads, a file or request above it then holds its record
before anything is sent, rather than failing after the record was written. A 413 from the service holds the record too.
`reliability.maxResponseBytes` (64 MiB by default) bounds what a node reads back; a bigger response fails the call with
`Response from <url> exceeds the <n> MB limit. Raise reliability.maxResponseBytes.`, the URL named without its query,
and a limit below 1 MiB stated in bytes (`exceeds the 500000 byte limit`).

**What one record costs in the ledger.** Each limit is a column width; what does not fit is refused or held with a message,
never stored cut short where it would mislead:

| Value | Limit | When it is exceeded |
| --- | --- | --- |
| Source key | 400 characters | Stored to that length. |
| Label | 400 characters | Stored to that length. |
| Origin file name (`FileName_DW`) | 800 characters | A file name column wider than that is refused when the source is opened, naming `source.systemColumns.fileName`. |
| OSDU id | 500 characters | A render that gives a longer id holds the record, naming the limit. |
| Last error, attempt error | 2,000 characters | Stored to that length, redacted. |
| Identity index | 24 values per record, each at most 200 characters; values under 3 characters are not stored | A record with more identifying values keeps the first. |
| Activity run log | 200,000 characters per run | Stored to that length; cleared once a limit is set for the activity logs on the Maintenance page. |

A mapping that declares many `dataset.identity` columns pays more of the identity index; one that declares none still
gets its key, label, OSDU id and file.

## Related pages

- [The delivery ledger](ledger.md) and [Submissions](submissions.md).
- [`sqlflow db`](../cli/db.md): the module database's migrations and version.
- [Control plane](control-plane.md): the module's background services and configuration.
- [Deploying](../guides/deployment.md) and the [operations runbook](../guides/operations-runbook.md).
