---
id: delivery-wiki-0025-parallelism-and-default-fan-out
title: "Decision 0025: What runs at once is a flow's parallelism, and a delivery fans out by default"
type: decision
summary: "Why parallelism left reliability, why a large delivery fans out by default, and how its coordinating run never waits on members no node starts."
keywords:
  - parallelism
  - fan-out by default
  - reliability
  - autoscaling
  - replica target
  - members no node starts
  - rate limit across a fan-out
  - batch file written again
referenceRefs:
  - delivery-flow-delivery
  - delivery-concept-submissions
  - delivery-cli-run
  - delivery-guide-deployment
  - delivery-table-work-batch
  - concept-control-plane
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/TargetConnection.cs
  - osdu/src/SqlFlow.Delivery/Http/RateLimiter.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs
  - sqlflow/src/SqlFlow.Catalog/RunQueueStore.cs
  - osdu/deploy/terraform/modules/worker/main.tf
  - osdu/tests/SqlFlow.Delivery.Tests/ScaleEngineTests.cs
related:
  - delivery-wiki-0006-work-batches
updated: 2026-10-23
---
# 0025: What runs at once is a flow's parallelism, and a delivery fans out by default

Status: proposed. Documented in [parallelism](../../reference/flow/delivery.md#parallelism) and
[many nodes](../../reference/concepts/submissions.md#many-nodes).

## Context

A well log delivery of about 5,000 records ran on one worker of an autoscaled pool for its whole length, at about four
records a second, while the pool's scale rule held one replica. The rule did what it was told. It reads the control
plane's replica target, which counts runs, not records: the eligible queued runs divided by the run slots a node offers,
rounded up, plus the nodes that are busy (`sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs`). The delivery was one run.
A delivery could already spread a submission over member runs, which the target counts like any other queued run, but
only when the flow set `reliability.fanOut`. That defaulted to 0, and no flow of the estate set it.

The setting sat in `reliability`, beside retries, timeouts and TLS, with every other setting that says how much runs at
once. The human's point: a delivery is not made more reliable by running more of it at once; fanning out is load
balancing, and filed under reliability it went unnoticed.

## Decision

- **Parallelism is a block of its own.** `concurrency`, `fanOut`, `fanOutMinRecords`, `batchRecords` and `batchSize`
  moved from `reliability` to `parallelism`, `renderParallelism` became `parallelism.renderers` and
  `parallelInterfaces` became `parallelism.interfaces`. `reliability` keeps what makes a request hold: retries,
  `skipStatusCodes`, timeouts, the rate limit, TLS, the URL allowlist, the response and request ceilings, and
  `leaseSeconds`, which is about recovering the records of a worker that stopped. Renaming the whole block was rejected:
  retries and TLS are not parallelism either. `concurrency` keeps its name inside the block, because on the other kinds it
  counts tests, dimensions, version lists or kinds, not records.
- **The old keys are refused, naming the new.** A document that still writes one under `reliability` is refused with
  where it goes now, rather than accepted under both names, which a document could set to disagree. The refusal is the
  loader's own, not YamlDotNet's unknown property, so it can name the new key; `render.cache` was retired the same way.
- **Each kind takes what it uses.** An assertion, dimension, inventory or retrieval flow takes `parallelism.concurrency`
  alone, and a cache flow no parallelism block. They used to accept every delivery setting under `reliability` and
  ignore it.
- **A delivery fans out by default, up to eight members.** The human chose the default over keeping fan-out opt-in: the
  pool and its autoscaler exist to spread work, and a delivery that never asks for it never scales. Eight was proposed
  and accepted. `fanOutMinRecords` (1,000) keeps a small delivery on one run, and 0 turns fan-out off.
- **A delivery fans out only over a work location every node reaches.** Every member reads and writes the
  submission's work batches on the node that runs it. A relative `source.work` resolves inside the copy of the
  repository the node runs the flow from, which is a disk of its own on every worker, so batches one node wrote are
  missing on the others; their records would be held as unreadable and planned again by the next run, which fans out
  again. Fan-out therefore needs a storage URI or an absolute path (a deployment's shared mount), and a relative one keeps
  the delivery on one run, its trace saying so. This was found when the human asked whether the estate's flows would
  scale out unchanged: they write their work to `../.work`, so they do not until it moves to storage. Rebasing relative
  work folders onto a storage root the deployment configures would let them fan out unchanged; it was not done here.
- **Members are bounded by the work.** The drain enqueued exactly `fanOut` members whatever the submission held, so a
  fan-out of 64 over ten batches started 64 runs and most found nothing. It now enqueues one per queued work batch beyond
  the one the coordinating run takes, up to `fanOut`, and decides on the records still to send rather than on what the
  last plan counted, which a submission planned again undercounts. The intake was already bounded by its slices. Planning
  fans out only when the record table names its identity primary key, the only thing it cuts slices on; without one the
  sending still fans out, where loading the flow used to refuse a fan-out without one.
- **A coordinating run never waits for ever on members no node starts.** A coordinating run holds a slot while it waits.
  With fan-out on by default every large delivery waits on members, and on a pool that cannot grow (the development
  host's built-in node, a pool of a fixed size, an autoscaled pool at its ceiling) every slot can hold a coordinating run
  whose members queue behind it. The drain stops the members still queued as soon as its own
  drain has left nothing to claim and none is running; they would find nothing. The intake, whose shares are dealt out
  in advance, stops them once none has started or ended for three minutes, long enough for an autoscaler to bring up a
  node, and plans their shares itself. Stopping single members, which would let the coordinating run take over a queued
  share at once, was not done: SQLFlow cancels a fan-out by group and gives a run executed again its members back by
  operation (`sqlflow/src/SqlFlow.Catalog/RunQueueStore.cs`), so it would have changed SQLFlow's node protocol, dispatcher
  and catalog, where the grace keeps the change in the module.
- **Intake members are enqueued before the coordinating run plans its own share,** where they used to be enqueued after
  it, so the nodes plan beside it.
- **A rate limit holds for the fan-out as a whole.** Each run built its own limiter from `reliability.rateLimitRps`, so a
  fan-out sent the declared rate once per run; on by default, a flow that set a rate to spare a service would have sent
  nine times it. Each run now sends under its share, the rate divided between the coordinating run and its members; the
  member's payload carries the share (`rateShare`), and no state is shared between the runs.
- **A share planned again never writes over a batch file a queued record reads from.** A plan of a share started its
  batch numbers at the share's base, and the records it passed over as already queued kept their references into files
  it then wrote again; a record retried by its reference could read another record's line. This was found while checking
  that taking over a stopped member's share is safe, and it applied before, to a member or a run the platform executes
  again. A share's batches now follow the ones it wrote, and a record that reads its document by its range checks that
  the line is its own (`osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs`).

## Consequences

- An estate's documents that write a moved key are refused until they are moved, each refusal naming its key; the
  documents move in one change with the upgrade, since the old version does not know `parallelism`.
- A large delivery whose `source.work` is on storage queues up to eight members, and an autoscaled pool brings up nodes
  for them; a flow wanting one run sets `parallelism.fanOut: 0`. A flow with a relative `source.work` is unchanged: one run. Each run keeps its own `parallelism.concurrency`, so the records in flight against OSDU
  multiply by the runs; the rate limit is what bounds them as a whole.
- Every run's trace says why it did not fan out, which is the first thing to read when a pool does not scale.
- On a pool with no place free, a fanned-out intake takes up to three minutes longer than one run planning alone; a
  drain takes nothing longer.
- The engine suite proves both paths with a fan-out no node ever starts, and the work batch rewrite with a share planned
  again (`osdu/tests/SqlFlow.Delivery.Tests/ScaleEngineTests.cs`).
