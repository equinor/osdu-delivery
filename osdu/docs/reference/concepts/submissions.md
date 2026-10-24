---
id: delivery-concept-submissions
title: "Submissions, work batches and leases: how a delivery run plans and sends its records"
type: concept
summary: "How a delivery run plans records into a submission and work batches, how workers claim them under leases across nodes, and how one source feeds several flows."
keywords:
  - submission
  - work batch
  - intake
  - drain
  - lease
  - claim
  - fan-out
  - many nodes
  - autoscaling
  - parallelism.nodes
  - rate limit across a fan-out
  - watermark
  - submission status
  - record-scoped run
  - one source several flows
  - osdu id claim
  - reliability.leaseSeconds
  - stopped submission
  - recover a stopped run
  - submission stuck planned
  - bounded memory
  - render parallelism
  - backpressure
related:
  - delivery-concept-ledger
  - delivery-concept-record-lifecycle
  - delivery-concept-change-detection
  - delivery-cli-run
  - delivery-flow-delivery
  - delivery-flow-interfaces
  - delivery-concept-partitions
  - cli-worker
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/FanOut/IFanOutDispatcher.cs
  - osdu/src/SqlFlow.Delivery/Engine/TargetConnection.cs
  - osdu/src/SqlFlow.Delivery/Http/RateLimiter.cs
  - osdu/src/SqlFlow.Delivery/Storage/WorkBatchFile.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionHolds.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SubmissionHold.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Holds.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Holds.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/LeaseJournal.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
---

# Submissions, work batches and leases: how a delivery run plans and sends its records

A deliver run of a [delivery flow](../flow/delivery.md) does two things: it **plans** (the intake reads the ingestion
tables, renders each record and decides what to send) and it **drains** (workers claim the planned records and send them to
OSDU). One plan is a **submission**; its rendered documents are written to **work batch** files; workers claim batches and
records under **leases**, so any number of nodes can deliver one flow at once. Everything is recorded in the
[ledger](ledger.md) as it happens. This page explains each of them and what to expect when you run, re-run or stop a flow.

## The operations of a delivery flow

A run of a delivery flow performs one operation, the one it names (`sqlflow run <flow.yaml> --operation <name>`) or
`deliver`. See [Running an OSDU flow](../cli/run.md) for the options and payload.

| Operation | What it does |
| --- | --- |
| `deliver` | Plan the selection into a submission, then drain it; send what submissions no run works on any more still hold (released records of settled submissions, and submissions a stopped run left open, which it then closes; see [Recovering a stopped submission](#recovering-a-stopped-submission)); undo what unfinished deliveries left. |
| `plan` | Plan and report what would be sent, with a sample of the records it would send. Stages nothing in the ledger and sends nothing. |
| `intake` | Plan into work batches without sending: a fan-out member's share, or a plan to drain later. |
| `drain` | Send the work batches already planned, of the flow or of one submission. |
| `replan` | Read every row of the scope again and deliver what renders differently. |
| `verify` | Read back delivered records from OSDU and record `match`, `drifted` or `missing`: up to 5,000 records a run, those not verified in the last 24 hours unless forced. |
| `sync` | Consolidate the ledger with the ingestion tables; renders and sends nothing ([Removal and reversal](removal-and-reversal.md#syncing-the-ledger-with-the-source)). |
| `reverse`, `undo`, `remove`, `delete-ledger` | Put back what a run delivered, undo unfinished deliveries, remove records, delete the ledger ([Removal and reversal](removal-and-reversal.md)). |

Every operation of a source with interfaces runs each interface it selects in order ([Interfaces](../flow/interfaces.md)),
each under its own ledger identity, as a run of that interface alone would.

## What a run reads

The intake reads one **selection** of the ingestion tables, recorded on the submission as its kind:

| Kind | When | What it reads |
| --- | --- | --- |
| `incremental` | An ordinary run of a scope that has a watermark | The rows changed after the scope's watermark, less `source.incremental.overlapSeconds`. |
| `full` | The scope has no watermark yet, a `replan`, or the mapping, template or parameters changed since the watermark was written | Every row of the scope. |
| `keys` | A run scoped to records (`recordKeys`, at most 1,000), or the records the ledger asked to be planned again | Those records' rows, by their stored key tuples. |

The **scope** is the flow's parameter set: one watermark per distinct set (`osdu.SourceWatermark`). A whole-scope plan
(`incremental` or `full`) moves the watermark to the upper bound it read when its planning finishes, with every fan-out
member successful; a `keys` plan never moves it. When no row changed in the window and no record waits to be planned again,
the run is skipped whole (the tier-0 gate) unless it is forced; the watermark still moves. See
[Change detection](change-detection.md).

Before its own read, a deliver run of a whole scope reads the records the ledger asked to be planned again (a release
without a rendered document, a redelivery, bringing records up to date, a cache rollout, a sync that found a change): 5,000
records a pass, each pass a `keys` submission of its own, as many passes as there are such records. A record a pass cannot
plan (its row gone, or outside the run's scope) keeps its request for a run that can.

## The submission

`osdu.Submission` holds one row per plan. Its id is the idempotency key: a run that names a submission (`submissionId` in
its payload) works on that submission, reading exactly the window or keys it recorded. A run the platform executes again
after an interruption (its node stopped and its lease lapsed) resumes the submission its interrupted attempt left
unsettled for the same read (a whole-scope plan resumes the whole-scope plan, a key-scoped plan only one made for
exactly the same keys) instead of registering another: it plans it again, keeps its key slices, and gets back its
fan-out members.

A submission records what produced it (the ledger, the mapping, the render context and the parameter values), what it
selected and read (its kind, the connection reference exactly as the flow declares it, the record table, the
`UpdatedDate_DW` window and what else bounded the read), where its work batches went, its status and timeline, and what
its planning passes and its drain counted. Every column, key and index: [`osdu.Submission`](../tables/submission.md).

**Status.** A submission is `received` while it plans. It is `planned` when the planning finished with records to send,
and `completed` at once when it planned none. A plan that finds nothing new while records an earlier pass queued are
still pending leaves the submission `planned`, not `completed`. When the drain ends it is closed from the ledger:
`running` while records of it are still pending, else `failed` when any of its records failed, else `completed`. A run
stopped part way (cancelled, or its interface stopped by `failWhen`) closes its submission as `failed` with the reason,
`stopped: ...`, while records of it are still pending, or when its planning had not finished; the flow's next run sends
them. A submission whose run ended without closing it is taken over by the flow's next deliver run
([Recovering a stopped submission](#recovering-a-stopped-submission)).

**Counts.** The closing counts are counted from the ledger each time the submission closes, never tallied while records
are sent: `Delivered` and `UnchangedAtPush` count the records the submission's own attempts settled so, `Held`, `Failed`
and `Waiting` count records now. A re-run that sends one record of three
already delivered therefore shows 1 planned and 3 delivered; what that run itself did is on the run and its activity.

**Re-running.** Running a submission again re-plans its window against the ledger as it stands now, unless it is completed
and not forced, in which case nothing is done. A `drain` with `submissionId` sends what it still holds.

## Work batches

The intake streams the rendered documents into JSON Lines files under the flow's work location (`source.work`),
`parallelism.recordsPerBatch` documents a file (500 by default). `osdu.WorkBatch` holds one row per file: its submission and
index, location, record count, status (`queued`, `running`, `done`, `failed`), the lease draining it, and how its drain ended
(delivered, held, failed, retrying, waiting). A record points at its batch and its `batch:offset:length` range; the document
itself is never stored in the database. The work location needs write access from every node that runs the flow, and its
files are part of the submission's evidence until the submission completes.

Each share of a [fan-out](#many-nodes) numbers its batches in a range of its own, so members never collide. A share planned
again (a run resuming its submission, a member the platform executes again, a coordinating run planning the share of a
member it stopped) passes over the records it already queued, which keep reading their documents from the files they were
written to, and writes its batches after those, so no file a queued record reads from is written again. A record whose
document is read back by its range checks that the line there is its own; one that is not is held, and the flow's next run
plans it again from its row.

## Planning in bounded memory

An ingestion table can hold millions of rows, and the plan never holds the table, a child dataset or the run's rendered
documents in memory. The intake is a pipeline of three stages joined by bounded channels, and each stage waits when the
next one is full:

| Stage | What it holds at a time |
| --- | --- |
| **The read.** The candidate keys are read a page at a time in key order (the identity primary key when the flow declares `source.record.primaryKey`, else the record key), `source.incremental.pageSize` keys a page (1,000 by default, at most 100,000). One command per page returns the page's rows with their child datasets, under `source.incremental.isolation` (snapshot by default); a page the database ends as a deadlock victim is read again, up to four reads in all. The records are cut into render batches of 200. | One page of records, and the render batch being filled |
| **The renderers.** `parallelism.renderThreads` renderers (half the node's processors when 0, at least one, at most 256) each take a render batch, look its 200 records up in the ledger in one lookup, and decide and render each ([What is decided for each record read](change-detection.md#what-is-decided-for-each-record-read)). | Between the read and the renderers, at most twice the render parallelism of render batches; one batch per renderer in hand |
| **The intake.** One consumer takes each batch of plan entries, writes every document to send into the open [work batch](#work-batches) file, and writes the ledger: skipped and held records 200 at a time, records to send when the work batch reaches `parallelism.recordsPerBatch`. | Between the renderers and the intake, at most twice the render parallelism of batches of plan entries; one work batch's ledger rows |

**When a stage falls behind.** When the renderers fall behind the read, the channel between them fills and the read waits
on its next batch, so no further page is read until a renderer takes one. When the intake falls behind (a slow work
location, a busy ledger), the channel before it fills, the renderers wait to hand on their entries, and the read stops in
turn. Nothing is dropped and nothing spills to disk: the slowest stage sets the pace. What a node holds for a plan is set
by the page size, the renderers and `parallelism.recordsPerBatch`, whatever the size of the table.

**Order and failure.** The renderers finish batches in any order, so entries reach the intake in no particular order; each
record is decided on its own, and a whole-scope plan moves the watermark only when its planning has finished. A failure
in the read or in any renderer stops the read and every renderer, and the run fails with that error; what the intake
had already staged stays with the submission ([Recovering a stopped submission](#recovering-a-stopped-submission)).

The same pipeline runs every plan: the intake of a deliver or `intake` run, each fan-out member over its key slice
([Many nodes](#many-nodes)), and a `plan` run, which keeps only its counts, the first entries for its trace and a sample of
the records it would send.

## Leases and claims

A worker takes work under a **lease**: one `osdu.Lease` row per claim, with a token (the worker's name and a fresh id), the
flow, the submission, the batch, the owner, and an expiry.

- **Claim.** A drain claims the oldest queued batch of the flow (or of one submission): the lease row and the batch's move
  to `running` commit together, so two workers never hold one batch. Records due for a retry outside a running batch are
  claimed `parallelism.recordsAtOnce` at a time, at least 50. Either way a record moves from `pending` to
  `delivering` by a compare-and-swap that also adds one to its tries, so two leases never hold one record.
- **Renew.** The worker renews its one lease row every half lease (at least a second apart). The lease lasts
  `reliability.leaseSeconds` (300 by default). A renewal that finds the lease taken over stops the worker.
- **Append.** While it sends, the worker does not write `osdu.Record`. It appends each completed step and each finished try
  to `osdu.RecordEvent`, with the try's attempt in the same transaction, at most 500 entries a write. A step is in the ledger
  before the next step starts, so a later try resumes after it.
- **Apply.** At each renewal and when the batch closes, the lease's events are applied to their records, 1,000 records a
  transaction, and deleted as they are applied. The record table therefore trails what workers have sent by at most one
  renewal; the attempts are there at once.
- **Close.** A worker that stops on its own terms hands back the records it did not reach, without charging their try, and
  the batch is `done`, `failed`, or `queued` again.
- **Recover.** Every claim first recovers the flow's leases that ran out: it takes each over by a compare-and-swap, applies
  what the stopped worker appended, hands the rest back to `pending` with the try charged, an attempt and the note
  `the lease expired mid-attempt (the worker stopped) and the record was requeued`, and queues the batch again. A record
  that has used its tries (`reliability.retry.attempts`, which the lease keeps) that way is `failed` and blocked instead,
  so a record that stops its worker every time is not claimed for ever. A recovery holds the lease it took for five
  minutes, after which a recovery that itself stopped is recovered too. A worker that finishes after its lease was
  recovered writes its attempt; its outcome reaches the record only while nothing has moved the record since the hand-back
  ([Record lifecycle](record-lifecycle.md#retry-hold-fail)).
- **Removal.** A removal holds the records it takes out of OSDU under a lease of its own, renewed the same way. A claim
  passes over any record a lease holds, and staging keeps a removal's hold while it queues newer work behind it. Closed or
  run out, a removal's lease lets its records go as they are, charging nothing
  ([Removal and reversal](removal-and-reversal.md#removing-records-from-osdu)).

A run the platform executes again resumes its submission, waits out leases a gone worker still holds, recovers them and
sends the records, resuming after the steps the stopped try had recorded. A submission whose run does not come back
(attempts ran out, a command-line run killed) is taken over by the flow's next deliver run
([Recovering a stopped submission](#recovering-a-stopped-submission)). A lease running out further ahead than the flow's
own `reliability.leaseSeconds` belongs to a live worker and is left alone with a warning.

## Many nodes

A large delivery is split into parts, each delivered from a node of its own. The flow says the most nodes it may use
(`parallelism.nodes`, 8 by default); the run works out the rest from the rows it reads:

**parts = rows the run reads ÷ `minRowsPerNode` (1,000 by default), at least 1 and at most `nodes`**

So an incremental run that reads a few changed rows is delivered by one node, and a first or full delivery of thousands of
rows is spread over several. One part is no split at all.

**Cutting the rows.** The run cuts the rows it reads into as many contiguous ranges of the record table's identity
primary key (`source.record.primaryKey`) as it has parts, of about equal count, and records the cut on the submission.
The first range has no lower bound and the last no upper bound, and each starts where the one before it ends, so every
row the run reads falls in exactly one range. A flow without a primary key, and a run outside the fleet, are not split.

**One part, one node.** The run queues the flow again once per range: a deliver run of the same flow filtered to its
range (`submissionId` and `slices` in its payload, [running an OSDU flow](../cli/run.md)), which asks for a node to itself.
SQLFlow's dispatcher hands such a run only to a node executing nothing else and puts nothing beside it, and the
autoscaler starts a node for each one no idle node takes, up to the pool's most workers
([SQLFlow's dispatcher](../../../../sqlflow/docs/reference/concepts/control-plane.md#the-dispatcher)). Each part plans
its range into [work batches](#work-batches) on its own node, sends them, and keeps at them until every record of its range
is settled, waiting out retries. It claims only its own range's batches and records, and reads them back from the folder
it wrote them to, so no part reads another's batches: `source.work` stays a folder next to the flow file, in each node's
own copy of the repository. The parts appear as a run family on the run's page.

**The first run coordinates.** It delivers no range itself: it waits for the parts, finalises the submission with every
part's totals, sends what the submission still holds (a record a part left in backoff, a record released from a wait),
and closes it. A part never closes the submission, moves the watermark or brings the cache up to date: the first run does
that once for the whole delivery.

**Nothing is lost when a part does not finish.**

- A part no node starts waits in the queue until a node is free. Once no part is running and none has started or ended for
  3 minutes, long enough for an autoscaler to bring up a node, the first run stops the ones still queued and delivers
  their ranges itself.
- A part that fails has its range delivered by the first run, with a line on its trace:
  `Part <slot> (run <id>) failed: <error>; this run delivers its rows (slices <slices>).`
- Delivering a range again is safe: the records the part sent are unchanged and passed over, and the rows it never
  reached are planned now. A record it had queued and not sent has its document in a batch file on that part's node; the
  first run finds the document missing, and the record is held, to be planned again by the flow's next run
  ([Work batches](#work-batches)). It is never sent from a document that is not its own.
- A first run that fails or is cancelled stops its parts with it, and a run the platform executes again gets its parts
  back and delivers the same cut.

**The rate limit.** `reliability.rateLimitRps` holds for the delivery as a whole. Each part sends under its share of the
rate (`rateShare` in its payload), and the first run takes the whole rate back once its parts have ended.
`parallelism.recordsAtOnce` is each part's own, so a delivery of eight parts has up to eight times as many records in
flight.

Why a run was not split is on its trace: `The delivery is not split: it reads 400 row(s), fewer than two parts of parallelism.minRowsPerNode (1000), so one node delivers them.`,
or the other reason (no primary key to split the rows on, `parallelism.nodes` is 1, a run outside the fleet).

Records of one flow written by many nodes never wait on each other for long: no statement writes more than 1,000 records,
a worker writes only its lease row and the append-only tables while it sends, staging locks only records that exist, and a
statement the database ends as a deadlock victim is run again, up to five runs in all.

## Recovering a stopped submission

A submission whose run stops before it settled (its node was lost, its process ended, it ran out of attempts, a
command-line run was killed) is not left for a drain by hand: the flow's next deliver run takes it over.

**Every run that works on a submission holds it.** The deliver run that plans it, each part of a split delivery, and a
drain that names a submission take a shared, session-owned application lock on the module's database,
`SqlFlow.Delivery.Submission:<id>` (`sp_getapplock`), on a connection of their own, from before the submission is
registered or reopened until they are done with it. Any number of runs hold one submission at once, as a fan-out's
coordinator and its members do. A process that stops frees its holds with its connections, whatever stopped it, which is
how a later run tells a submission somebody still works on from one whose run has ended. A hold reads and writes nothing
of the ledger, so there is no table to look at; the submission's status and leases say the rest.

**Every deliver run looks for submissions no run works on any more**, after it has sent its own records: first the
flow's submissions left `received`, `planned` or `running` that no lease which has not run out holds work of, oldest
first, 10 a page; then settled submissions holding records that are due again (released back to pending with their
rendered documents after their run was over). It takes each over only when no run holds it (an exclusive lock, asked
without waiting, then held shared), so a run still planning or sending it, a fan-out member included, is never raced; a
submission a run still holds is left to that run (`Submission <id> (<status>) is held by a run that still works on it, so
it is left to that run.`). It passes over the submission with the claims a run of it makes, so each record is sent once,
resuming after the steps a stopped try recorded, without waiting for records in backoff, and closes it. Before closing,
it checks again: a submission a whole-flow drain took work of meanwhile is left open for the flow's next run to close.
Each submission is visited once per run.

A submission its run left open is closed `failed`, its reason saying what happened:

```text
stopped: run <id> ended before its records were all sent; run <id> took it over and sent what was due, and the flow's next run sends the rest
stopped: run <id> ended before its planning finished; run <id> took it over and sent what it had planned, and the rows it had not reached are left to the flow's plans, which read them again: it moved no watermark and answered no request to plan them
```

The attempts the recovering run writes carry its own run id and the stopped submission's id, so the record's history
says which run sent it for which plan. A submission an `intake` run planned is sent and closed the same way by the flow's
next deliver run, unless a drain of it comes first.

A run that wants a submission another run is taking over in that moment waits up to 30 seconds for it, then fails:
`Submission <id> could not be held for this run within 30 seconds (sp_getapplock answered <n>): another run of the flow was taking it over, as a deliver run does with a submission no run works on any more, and that run sends what it holds. Run this one again once that run has ended.`

## One source, several flows

An ingestion table can feed several delivery flows, each rendering the rows with its own mapping. The rows are loaded once;
each flow keeps its own ledger over them.

- **A record is one flow's.** The ledger keys a record by flow and delivery key, so a row read by two flows is two records,
  each with its own state, attempts, activities, submissions and watermark, and its own counts.
- **One OSDU record, one record of the ledger.** A record claims its OSDU id the first time it queues a document
  (`ClaimedTargetId`, unique across the ledger). Work whose id another record has claimed is held and nothing is sent:
  `OSDU id <id> is already claimed by flow '<name>' (<id>), and one OSDU record belongs to one flow. Deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.`
  Within one flow, two keys that give one id (possible with `dataset.idFrom: key`, or after a mapping's `dataset.system` or
  key changed) are refused the same way, naming the record that holds it.
- **A record keeps the id it claimed.** A render that gives another id than the one claimed is held, naming both, and
  nothing is sent.
- **An id made from a key is claimed only when it is free.** Before a record first claims such an id, the intake asks OSDU;
  when OSDU already holds a record there that no record of the ledger claimed, or cannot say, the record is held unclaimed.
  An operator who knows the records OSDU holds are the source's adopts them, and the next run claims each id
  ([Adopting a record OSDU already holds](record-lifecycle.md#adopting-a-record-osdu-already-holds)).
- **A flow acts only on ids it claimed.** Sends, read backs and removals reach only claimed ids, so a record that was only
  ever held never touches another flow's OSDU record.

## Ledger registration

A run registers its ledger in the partition it delivers to before it writes any row: the partition the flow is bound to, or
the one its `data-partition-id` header resolves to. A flow with neither is refused:
`Flow '<name>' names no partition, and its target.headers no 'data-partition-id', so the partition its ledger belongs to is unknown.`
See [Partitions](partitions.md).

## Related pages

- [Record lifecycle](record-lifecycle.md): what a try does to a record.
- [Change detection](change-detection.md): what a plan sends and skips.
- [Running an OSDU flow](../cli/run.md): operations, payload and exit codes.
- [Operations runbook](../guides/operations-runbook.md): a submission that stays planned or running, records stuck
  delivering.
- SQLFlow's [`sqlflow worker`](../../../../sqlflow/docs/reference/cli/worker.md) for the nodes that run these drains.
