---
id: delivery-concept-ledger
title: "The delivery ledger: what OSDU Delivery records about every record, run and intervention"
type: concept
summary: "What the ledger in the osdu schema records per delivered record, how to reconstruct a record from it, and how its counts and record search work."
keywords:
  - delivery ledger
  - osdu schema
  - osdu.record
  - osdu.attempt
  - attempt history
  - audit trail
  - traceability
  - record history
  - reconstruct a record
  - record search
  - identity index
  - statistics
  - purged record
  - activity
related:
  - delivery-concept-artifacts-and-undo
  - delivery-concept-record-lifecycle
  - delivery-concept-submissions
  - delivery-concept-removal-and-reversal
  - delivery-concept-availability-and-retention
  - delivery-concept-partitions
  - delivery-cli-records
  - delivery-concept-architecture
  - delivery-flow-mapping-assertions
  - concept-provenance-and-row-keys
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260928064320_LedgerPartitions.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261010205917_RecordAssertions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs
  - osdu/src/SqlFlow.Delivery/Ledger/AttemptResult.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery/Protocols/IDeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordChain.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
---

# The delivery ledger: what OSDU Delivery records about every record, run and intervention

The ledger is the set of tables in the `osdu` schema that says, for every record a delivery flow handles, what OSDU
holds, what is waiting to be sent, and everything that ever happened to it. Traceability is the product: a delivered
record can be reconstructed from the ledger alone, from the source file and row it came from, through the mapping,
template and cache version that rendered it and every try to send it, to the OSDU id and version it landed as and every
operator action on it. Every delivery path, retry, removal, reversal and release goes through the ledger; there is no
repair script or side door that changes what OSDU holds without writing it here.

You read the ledger in the GUI (OSDU, Ledger: **Delivery**, **Records**, **Audit trail**, and a flow's Records, Issues and
Submissions tabs), with [`sqlflow records`](../cli/records.md), and through the [delivery API](api.md). This page says what
the ledger records and how to read it back. [Record lifecycle](record-lifecycle.md) says how a record moves between states,
and [Submissions](submissions.md) how a run plans and sends.

## Where the ledger lives

The `osdu` schema belongs to the OSDU Delivery module: its own EF Core context (`OsduDbContext`), its own migration history
(`[osdu].[__EFMigrationsHistory]`) and its own schema version (`[osdu].[SchemaVersion]`, one row: the module version, the
last migration, when and by whom, and the oldest SQLFlow catalog migration it needs). No table of the schema has a foreign
key into SQLFlow's catalog: a ledger row names a run, a pipeline or a repository by its id alone. See
[Architecture](architecture.md) and [`sqlflow db`](../cli/db.md).

The schema lives in the catalog's database. SQLFlow gives a worker node no catalog connection, so the module reaches the
ledger there through `SQLFLOW_CATALOG_DB` itself, and a node started without it refuses to start.

## Ledgers, flows and partitions

A **ledger** is the records of one flow in one OSDU partition, under one ledger identity (the `FlowId` every ledger row
carries). A flow in the single form has one identity, derived from its name; each interface of a source keeps its own,
derived from `<flow>/<interface>` (see [Interfaces](../flow/interfaces.md)). A flow that works in partitions derives one
identity per partition from that name and the partition, except in the partition marked `keepLedger`. The same source row read by two flows is two
records, each with its own state and history.

`osdu.Ledger` is the directory: one row per ledger identity, naming its partition, its kind (`delivery`, `retrieval`,
`assertion`, `dimension` or `inventory`), its flow and interface. `osdu.LedgerPartition` numbers each partition once, and every ledger table's key
starts with that two-byte number, so one partition's rows are one range of every clustered index. A run registers its
ledger in the partition it delivers to before it writes a row; a registration that names another partition than the
directory holds is refused, naming both. See [Partitions](partitions.md).

## The tables

| Table | One row is | Grows with |
| --- | --- | --- |
| [`osdu.Record`](../tables/record.md) | The current state of one deliverable of one flow | Deliverables |
| [`osdu.Attempt`](../tables/attempt.md) | One try of a record, append-only, or one decision written as an attempt | Tries |
| [`osdu.Submission`](../tables/submission.md) | One plan of a flow over its ingestion tables | Plans |
| [`osdu.WorkBatch`](../tables/work-batch.md) | One file of rendered documents of a submission | Batches |
| [`osdu.Lease`](../tables/lease.md), [`osdu.RecordEvent`](../tables/record-event.md) | A worker's hold on work, and what it learned and has not applied yet | In-flight work only |
| [`osdu.SourceWatermark`](../tables/source-watermark.md) | How far the last whole-scope plan of a flow scope read | Flow scopes |
| [`osdu.Activity`](../tables/activity.md) | One run or operator intervention: the audit trail | Runs and interventions |
| [`osdu.ActivityRecord`](../tables/activity-record.md) | One record an intervention or a reversal reached | Records reached |
| [`osdu.RecordIdentity`](../tables/record-identity.md) | One value a record can be found by | Records and their values |
| [`osdu.PurgedRecord`](../tables/purged-record.md) | The one line kept of a record deleted from the ledger | Records deleted |
| [`osdu.Artifact`](../tables/artifact.md) | One object a delivery created in OSDU, or set out to create | Objects delivered |
| [`osdu.Reversal`](../tables/reversal.md), [`osdu.ReversalItem`](../tables/reversal-item.md) | One reversal of a run or submission, and each record it reached | Reversals |
| [`osdu.Ledger`](../tables/ledger.md), [`osdu.LedgerPartition`](../tables/ledger-partition.md) | The ledger directory and the partition numbers | Ledgers |

Each table has a page of its own with every column, key and index ([The osdu schema](database.md) lists them all). The
other tables of the schema are explained with what they serve:

- `osdu.CacheVersion`, `osdu.CacheItem`, `osdu.CacheMember`, `osdu.CacheSet`, `osdu.CacheSetEntry`, `osdu.UpdateTag`,
  `osdu.CacheDefinition`: the partition cache and its changes, in [The partition cache](partition-cache.md).
- `osdu.RepositoryTemplate`: the files of the repositories' template folders, in [Templates](templates.md). `osdu.Mapping`: the synced mappings, in
  [The mapping document](../flow/mapping.md). `osdu.Interface`: the read model of sources and interfaces, in
  [The interface read model](../flow/interfaces.md#the-interface-read-model).
- `osdu.Partition` and `osdu.ConfigProperty`: the partition registry and central configuration, in
  [Partitions](partitions.md).
- `osdu.Retrieval`: in [Retrieval flow](../flow/retrieval.md). `osdu.AssertionRun`, `osdu.AssertionResult`: in
  [Assertion flow](../flow/assertion.md).
- `osdu.Dimension` and its tables, and each `osdu.dim_<dimension>`: in [The tables a dimension keeps](dimension-tables.md).
- `osdu.DimensionView` (each view a dimension flow made, with the statement it was written by), `osdu.DimensionViewCheck`
  (what each build's check of a view found in a partition, the newest 50 of each kept) and each `osdu.v_dim_<view>`: in
  [Dimension views](../flow/dimension-views.md).
- `osdu.Inventory` and its tables: in
  [The tables an inventory keeps](../flow/inventory-builds.md#the-tables-an-inventory-keeps).
- `osdu.RemovedKey` (a ledger's deletions: the keys a removal flow found its source no longer holds), `osdu.RemovalRun` and
  `osdu.RemovalKey`: in [Removal flow](../flow/removal.md#the-ledgers-deletions). A removal flow keeps no ledger of its
  own; its runs and deletions are kept under the delivery flow's ledger identity.
- `osdu.SearchTerm`, `osdu.SearchTermRefinement`: in [Search terms](search-terms.md).

### `osdu.Record`: the current state of one deliverable

Keyed by `(PartitionId, FlowId, DeliveryKey)`. The delivery key is a deterministic UUID derived from the source key, so the
same row always lands on the same record.

It holds what OSDU holds of the record (its id, version and hashes), where that version came from (the ingestion
row's file, row number and update time, and its arrival), the work waiting to be sent (`Pending*`: its hashes, render
context, source version and origin, payload location, the OSDU ids it refers to, and where its rendered document sits in
a work batch file; the document itself is never stored in the database), its custody state and why it is blocked, the
last check of its document, and the lease delivering it now. Every column, key and index:
[`osdu.Record`](../tables/record.md).

### `osdu.Attempt`: every try, and every decision that changed the record

Append-only. One row per delivery try, and one per decision the engine or an operator made about the record even when
nothing was sent: a hold at render time, a stale or identical row, a removal, a restore, an undo, a row a sync no longer
found. The record's history is its attempts.

Each attempt names the record, the submission, run and batch it belonged to, who made it and when, what came of it
and what was sent, the hashes it established and the version OSDU returned, the ingestion row its document was built
from, the redacted error, and every step of the route with what OSDU answered. Every column, key and index:
[`osdu.Attempt`](../tables/attempt.md).

The `correlationId` is the id each OSDU request of the try sent in its `correlation-id` header, so an attempt can be followed
into the OSDU services' own logs.

### Runs, submissions and leases

`osdu.Submission`, `osdu.WorkBatch`, `osdu.Lease`, `osdu.RecordEvent` and `osdu.SourceWatermark` hold how a run planned and
sent its records: one row per plan with its window and counts, one per work batch file, one per worker claim, and what a
worker appended under its lease. A run holds each submission it works on with an application lock on the module's
database (`SqlFlow.Delivery.Submission:<id>`), not with a table, so a later run can tell a submission somebody works on
from one whose run has ended. [Submissions](submissions.md) describes them
([Recovering a stopped submission](submissions.md#recovering-a-stopped-submission)).

### `osdu.Activity` and `osdu.ActivityRecord`: the audit trail

One `osdu.Activity` row per run and per operator intervention, never deleted:

- runs: `deliver`, `intake`, `drain`, `verify`, `sync`, `undo`, `reverse`, `delete-ledger`, and `probe` (a target probe,
  by button, the API, the MCP tool or the schedule);
- interventions: `release`, `adopt` (a record taking over the OSDU record at the id its key makes), `redeliver`, `rerender` (bring up to date), `delete` (a removal from OSDU),
  `restore-previous`, `purge` (deleting removed records from the ledger), `remove-dimension`, `inventory-remove`,
  `inventory-repair`, `reassign-id`, `source-remove` (a removal flow taking out what the source no longer holds, in the
  delivery flow's ledger) and `lift-removal` (an operator delivering such records again).

Each row carries the actor (for a run, who asked for it or `schedule:<name>` for a run a schedule fired; `user:<name>` for
an intervention from the GUI or the API and for a probe asked for, `cli:<user>@<machine>` for one from a workstation,
`service:schedule` for a scheduled probe;
see [Authentication and identity](authentication-and-identity.md)),
start and end, outcome (`running`, `completed`, `failed` or `cancelled`), parameters, the submission, record and platform
run it concerned, a summary of the counts that are not zero, and for runs the captured run log (at most 200,000
characters). `Idle` marks a run that completed having changed nothing (a scheduled run that found nothing new). The GUI's
audit trail leaves idle runs out unless **Show idle runs** is on, and says how many it left out; `GET /activities` takes
`idle=false` to leave them out and `idle=true` to list only them. An intervention is never idle.

An intervention made for one record names it on its row. One that reaches many (a release of every blocked record, a
redelivery of every delivered record, a reversal) names each record it changed in `osdu.ActivityRecord`, written by the
statement that changed the record; so does a verify run that reconciles drift, for each drifted or missing record it
marked for redelivery. A record's activities (`GET /records/{flowId}/{key}/activities`) are those that name it
on their row and those that name it there, so its history shows every request that reached it, however many records the
request reached.

### `osdu.PurgedRecord`: what is kept of a record deleted from the ledger

A record removed from OSDU can be deleted from the ledger ([Removal and reversal](removal-and-reversal.md)). Its attempts,
search entries and row go; one line stays: its key, source key and key tuple (`SourceKeyJson`), label, the OSDU id it was
delivered and removed under, the last version an attempt named, how many attempts went with it, who deleted it, when, and
under which activity. Asked for afterwards, the record's API route answers 404 titled `Deleted from the ledger` with that
line under `purged`.

`PlanRequestedUtc` is the line's request to read the record's row again: each run pages these requests beside the records
asked to be planned again, reads the rows by key, and delivers each as a new record. A run that planned the row, or found
it gone from the ingestion table, clears it; a later deletion of the same record moves it to the new line. Lines written
by deleting the whole ledger, by a record a reversal keeps out, or for a record with no key tuple carry none. Lines are
otherwise only ever added. Added by migration `PurgedRecordReadAgain` (module version 1.46.0).

### `osdu.Artifact`: every id a delivery minted

Every object a delivery created in OSDU, or set out to create, written in the transaction that writes the step that made it:
a dataset, a bulk session, a version written before a later call failed, rows, a workflow run. An id a committed delivery
minted stays for good, whatever later happens to the record or its ledger, which is what lets an
[inventory flow](../flow/inventory.md) say which ids OSDU serves that no delivery accounts for. A delivery that does not
complete is undone from these rows; see [Removal and reversal](removal-and-reversal.md#unfinished-deliveries-and-their-undo).

Each artifact names its unit of work, the route's name for it and what it is, its OSDU id or what finds it, the version
it wrote and the one it replaced, its state, and how its undo settled it. Every column, key and index:
[`osdu.Artifact`](../tables/artifact.md).

### `osdu.Reversal` and `osdu.ReversalItem`

One reversal per run or submission reversed in a ledger, and one item per record its source delivered, with what OSDU held
before and what the reversal did. Never pruned; deleting the ledger deletes them. See [Removal and reversal](removal-and-reversal.md#reversing-a-run-or-a-submission).

## Reconstructing a delivered record

Every question about a delivered record is answered from the ledger:

| Question | Where the ledger answers it |
| --- | --- |
| Which source file and row did it come from? | `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc` on the record for the version OSDU holds, and on each attempt for the version that try sent. `GET /records/{flowId}/{key}/chain` follows every change of the row back to the ingestion run that stamped it and the landing run that brought its file in, naming a run only when the catalog proves it. |
| Which mapping, template and cache rendered it? | `RenderContext`: the mapping reference and fingerprint, the template version, the cache partition and version, and the mapping parameters. `CacheSetId` names the cached values the render read (`GET /records/{flowId}/{key}/cache`). |
| Every try and its outcome | The record's attempts, newest first: outcome, phase, error, steps, correlation id, the ingestion row it was built from. |
| Did it meet its schema and its mapping's assertions? | Each attempt's `validation`: the verdict against the template, and under `assertions` every assertion its document failed, with the value and why. The record keeps the last outcome and `AssertionFailures`. |
| Which OSDU id and version did it land as? | `TargetId`, `TargetVersion`, `TargetStateJson`; each delivered attempt's `TargetVersion` and the version it replaced. |
| Who released, redelivered, removed or reversed it, and when? | The record's activities: its own and those naming it in `osdu.ActivityRecord`. |
| What did its deliveries create in OSDU? | Its artifacts. |

In the GUI a record's page has the tabs **Timeline** (attempts and activities), **Source**, **Render**, **OSDU** and
**Artifacts**. On the command line:

```bash
sqlflow records show flows/welldb-wellbore-03-delivery.yaml --key welldb:WB-0123 --partition dev --attempts 50
sqlflow records artifacts flows/welldb-wellbore-03-delivery.yaml --key welldb:WB-0123 --partition dev
```

`--key` takes the delivery key or the source key. The API routes are `GET /api/v1/delivery/records/{flowId}/{key}` and its
`/attempts`, `/activities`, `/chain`, `/cache` and `/artifacts`. The flow id in these routes is the ledger identity, not the
pipeline id, so a record's history stays reachable after its flow leaves the repository.

## Statistics are counted from the records

A flow's counts (`GET /api/v1/delivery/flows/{pipelineId}/stats`: total, pending, delivering, delivered, held, failed,
deleted, reverted, waiting, drifted, delivered in the last 24 hours, the last delivery and verification, the submissions)
are counted from `osdu.Record` through its status indexes on every call. Nothing keeps a running total beside the records,
so the counts are exact and cannot drift from the ledger. Drifted counts records whose last verify outcome is `drifted` or
`missing`. A source's counts are its interfaces' added up, and a flow that serves several partitions is counted per
partition or added up across them.

A submission's counts are counted the same way when it closes ([Submissions](submissions.md)), and a flow's issues are
counted from the blocked records ([Record lifecycle](record-lifecycle.md#issues)). The metrics the engine exports
([Run trace and metrics](run-trace-and-metrics.md)) are rates to watch and alert on; the delivered, pending, held and
failed counts the GUI and CLI show are always read from the ledger.

## Finding records

Every listing reads a bounded, index-backed part of the ledger, however many records a flow holds.

**The lookup across flows** (the search box, `GET /api/v1/delivery/records?search=`) reads the partition the request names,
else the workbench's (the `X-Osdu-Partition` header the GUI sends), else every partition, each through its own range of a
partition-first index. A term that parses as a UUID is a delivery key and finds every flow's record of that row. Anything else is a prefix over
`osdu.RecordIdentity`, which holds, folded to upper case, every value a record is known by:

| Kind | Values |
| --- | --- |
| `identity` | The columns the mapping declares in `dataset.identity` (a wellbore name, a UWI). |
| `key` | The source key whole, and each of its key values. |
| `label` | Up to 8 words of the rendered label. |
| `osdu` | The OSDU id, and its part after the last colon. |
| `file` | The ingestion file the current version came from. |

A record contributes at most 24 values, each at most 200 characters; values shorter than 3 characters are not stored. A
record's values are rewritten as a set whenever it is staged. The lookup takes at most 1,000 candidates from the index and
reports a larger match as a floor.

**A flow's listing** (a flow's Records tab, `GET /flows/{pipelineId}/records`, `sqlflow records list`) filters by status,
submission, run, the submission that delivered it, issue or drift, and searches by a UUID (exact) or a prefix over the
source key, label, OSDU id and origin file name. It counts no further than 25,000 matches, pages through the first 25,000
in update order (newest first, ties broken by key), and refuses a page past them, saying to narrow the filter. A contains
search has no index: it runs only when the rest of the filter leaves at most 100,000 records, and is refused otherwise with
`A contains search reads every record the rest of the filter leaves, and this filter leaves more than 100000. Use a prefix search, which is indexed, or narrow by status, submission or run first.`

### The indexes behind it

Every key and every index that serves a listing leads with `PartitionId`.

Each table's page lists every index with what it serves: [`osdu.Record`](../tables/record.md#keys-and-indexes)
(the worker's claims, the listings newest first, prefix search in a flow, the lookups across a partition's flows, the
issues, the records asked to be planned again, the waiting records, and the unique claim on an OSDU id),
[`osdu.RecordIdentity`](../tables/record-identity.md#keys-and-indexes) (the lookup by any identifier),
[`osdu.Attempt`](../tables/attempt.md#keys-and-indexes) and [`osdu.PurgedRecord`](../tables/purged-record.md#keys-and-indexes).

`osdu.Attempt`'s primary key and `StartedUtc` index, and `osdu.RecordEvent`'s primary key, grow at their end and are set
to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it. OSDU ids are compared in a binary collation
(`Latin1_General_100_BIN2`), since ids that differ only in case are different records.

## Writes at scale

Many nodes write one record table at once, so no statement writes more than 1,000 records: staging, releases, redeliveries
and lease applications all work a slice at a time, and a statement the database picks as a deadlock victim is tried up to
five times in all, each statement written so a second run does what the first would have. A worker does not write `osdu.Record` while it delivers: it renews
one lease row and appends events and attempts, and the records change when the lease applies them
([Submissions](submissions.md#leases-and-claims)). Nothing else is maintained inside a record write.

## Related pages

- [Record lifecycle](record-lifecycle.md): states, attempts, retries, issues, releases.
- [Submissions](submissions.md): plans, work batches, leases, many nodes.
- [Removal and reversal](removal-and-reversal.md): taking records out of OSDU, deleting the ledger, reversing a run.
- [Availability and retention](availability-and-retention.md): pruning, backup, recovery.
- [`sqlflow records`](../cli/records.md) and the [operations runbook](../guides/operations-runbook.md).
