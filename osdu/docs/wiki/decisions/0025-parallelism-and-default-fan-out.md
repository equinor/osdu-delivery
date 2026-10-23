---
id: delivery-wiki-0025-parallelism-and-default-fan-out
title: "Decision 0025: What runs at once is a flow's parallelism, and a delivery fans out by default"
type: decision
summary: "Why parallelism left reliability, why a large delivery fans out by default into slice runs that each deliver their share, and what was rejected."
keywords:
  - parallelism
  - fan-out by default
  - reliability
  - autoscaling
  - replica target
  - members no node starts
  - rate limit across a fan-out
  - batch file written again
  - slice runs
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
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.FanOut.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - sqlflow/src/SqlFlow.Orchestration/IRunFanOut.cs
  - sqlflow/src/SqlFlow.SqlServer/Export/ExportSegmentPlanner.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/TargetConnection.cs
  - osdu/src/SqlFlow.Delivery/Http/RateLimiter.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs
  - sqlflow/src/SqlFlow.Catalog/RunQueueStore.cs
  - osdu/deploy/terraform/modules/worker/main.tf
  - sqlflow/src/SqlFlow.Core/Engine/SourceLocations.cs
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
- **A fan-out is the flow queued again once per share, each run delivering its share.** The fan-out first split a
  delivery into two stages of member runs: intake members planned key slices into work batches, and drain members claimed
  whatever batch came next and sent it. A batch file is written to `source.work`, which is a folder next to the flow file,
  in the copy of the repository each node runs the flow from: a disk of its own on every worker. A drain member on one
  node therefore asked for batch files another node had written, and a delivery spread over three workers kept to one.
  Moving the work to storage every node reaches was built first (relative work placed under `LANDING_URL`), and the human
  rejected it as the wrong design: one execution is split into sub-executions of the same pipeline, each with its filter,
  and nothing passes between them. Each slice run now plans its slices, sends their batches from its own node, and keeps
  at them until its share is settled; the worker claims only the batches and records of its share (a batch range, which
  the per-slice batch numbering already made one range) and reads them back from the folder it wrote them to. The fan-out
  itself is SQLFlow's run fan-out (`IRunFanOut`), which queues runs of the same pipeline with their parameters and
  reports their results, as before. SQLFlow's export splits a table into key or date segments
  (`sqlflow/src/SqlFlow.SqlServer/Export/ExportSegmentPlanner.cs`), and was looked at for the cut: it runs its segments as
  threads of one run on one node, its key buckets are of equal width over the key values rather than of equal count, and
  it is internal to SQLFlow's SQL Server project, so the delivery keeps its own cut on the candidate rows. A flow without
  a primary key no longer fans out its sending, since that was the drain members; it runs on one run.
- **The work location may name the deployment's storage.** `source.work` may open with a reference,
  `${env:LANDING_URL}/.work/welllog/{logSource}`, which the runtime resolves as the run opens by the rule SQLFlow already
  resolves a file flow's location with (`SourceLocations`, now public for one location). It was added for the two-stage
  fan-out and stays as a work location like any other; a fan-out no longer needs it. A reference anywhere but at the
  start of the location is refused, since the rest is a path under it; a payload root still refuses one.
- **Slice runs are bounded by the work.** A read is cut into about a work batch of records a slice, at most 1,024, and the
  slices are dealt into at most `fanOut` shares beside the coordinating run's own, so a small submission queues few runs
  however high `fanOut` is. Fanning out needs the record table's identity primary key, the only thing slices are cut on.
- **A coordinating run never waits for ever on slice runs no node starts, and takes over the share of one that failed.**
  A coordinating run holds a slot while it waits, and on a pool that cannot grow (the development host's built-in node, a
  pool of a fixed size, an autoscaled pool at its ceiling) every slot can hold a coordinating run whose slice runs queue
  behind it. Once none is running and none has started or ended for three minutes, long enough for an autoscaler to bring
  up a node, it stops the ones still queued and delivers their shares itself, as it does the share of a slice run that
  failed. A share planned again is safe: what its run sent is passed over as unchanged; what it queued and did not send
  has its documents on that run's node, is found missing, and is planned again by the flow's next run rather than failing
  this one. Stopping single runs, which would let the coordinating run take over a queued share at once, was not done:
  SQLFlow cancels a fan-out by group and gives a run executed again its members back by operation
  (`sqlflow/src/SqlFlow.Catalog/RunQueueStore.cs`), so it would have changed SQLFlow's node protocol, dispatcher and
  catalog, where the grace keeps the change in the module.
- **Slice runs are queued before the coordinating run delivers its own share,** so the nodes deliver beside it.
- **A rate limit holds for the fan-out as a whole.** Each run built its own limiter from `reliability.rateLimitRps`, so a
  fan-out sent the declared rate once per run; on by default, a flow that set a rate to spare a service would have sent
  nine times it. Each run now sends under its share, the rate divided between the coordinating run and its slice runs;
  a slice run's payload carries the share (`rateShare`), and no state is shared between the runs.
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
