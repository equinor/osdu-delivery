---
id: delivery-wiki-0025-parallelism-and-default-fan-out
title: "Decision 0025: What runs at once is a flow's parallelism, and a delivery fans out by default"
type: decision
summary: "Why a delivery is spread over nodes by default, a part per node worked out from its rows, why its keys say what they are, and what was rejected."
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
  - nodes
  - parts on nodes of their own
  - whole-node runs
referenceRefs:
  - delivery-flow-delivery
  - delivery-concept-submissions
  - delivery-cli-run
  - delivery-guide-deployment
  - delivery-flow-assertion
  - delivery-flow-dimension
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
  - sqlflow/src/SqlFlow.Dispatch/DispatchState.cs
  - sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs
  - osdu/src/SqlFlow.Delivery/Source/KeySlices.cs
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
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
related:
  - delivery-wiki-0006-work-batches
updated: 2026-10-30
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
  retries and TLS are not parallelism either. `concurrency` kept its name inside the block, because on the other kinds it
  counts tests, dimensions, version lists or kinds, not records (since reversed: every kind writes `recordsAtOnce`,
  below).
- **The old keys are refused, naming the new.** A document that still writes one under `reliability` is refused with
  where it goes now, rather than accepted under both names, which a document could set to disagree. The refusal is the
  loader's own, not YamlDotNet's unknown property, so it can name the new key; `render.cache` was retired the same way.
- **Each kind takes what it uses.** An assertion, dimension, inventory or retrieval flow takes `parallelism.concurrency`
  alone (written `recordsAtOnce` since, below), and a cache flow no parallelism block. They used to accept every delivery setting under `reliability` and
  ignore it.
- **A delivery fans out by default, up to eight members.** The human chose the default over keeping fan-out opt-in: the
  pool and its autoscaler exist to spread work, and a delivery that never asks for it never scales. Eight was proposed
  and accepted. `fanOutMinRecords` (1,000) keeps a small delivery on one run, and 0 turns fan-out off (since renamed:
  `minRowsPerNode`, and `nodes: 1` keeps a delivery on one node, below).
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
  however high `fanOut` is (since replaced: a part per `minRowsPerNode` rows, at most `nodes`, below). Fanning out needs
  the record table's identity primary key, the only thing slices are cut on.
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
- **Slice runs are queued before the coordinating run delivers its own share,** so the nodes deliver beside it (since
  replaced: the first run of a split delivery delivers no range of its own, below).
- **A flow says how many nodes; a run works out the rest, and each part has a node to itself.** With slice runs a
  delivery of 5,000 records spread over nine runs, the coordinating run and eight members, but the pool's workers took
  four runs each, so the nine shared three workers. `fanOut` counted runs, a worker's slots were fixed inside SQLFlow's
  worker, and how many workers a delivery used followed from both, which the human found hard to understand and harder to
  steer. In their words: say how many rows there are and how many nodes the user wants, and compute the rest. A flow
  writes `parallelism.nodes`, the most nodes a delivery is spread over; a run splits into a part per `minRowsPerNode` rows
  it reads (1,000), at most `nodes`, so an incremental run of a few rows stays on one node and a full delivery is spread.
  1,000 rows is about four minutes of work at the rate the human's pool sent, longer than the autoscaler takes to start a
  node, so each node is worth starting. Each part asks SQLFlow for a node to itself (`IRunFanOut.EnqueueAsync(members,
  wholeNode: true, ct)`, a generic extension point recorded in `docs/sqlflow-changes.md`): the dispatcher hands it only to a
  node executing nothing else, and the replica target counts it as a node, so the parts and the nodes are one number while
  every other run still shares a node four at a time. Setting a worker's slots to one for the whole pool was the other way,
  and was rejected because every ingestion and cache run would then take a node too. When a delivery is split, the first
  run delivers no range itself and only coordinates, so every range is on a node of its own; it delivers a range only when
  the part that had it never started or failed.
- **The keys say what they are.** The human found `concurrency`, `fanOut` and `batchSize` side by side confusing:
  `batchSize` sounded like the main batch and only governed retries. The delivery's keys are now `nodes`,
  `minRowsPerNode`, `recordsAtOnce`, `renderThreads`, `recordsPerBatch` and `interfacesAtOnce`, `batchSize` is gone (a
  worker claims retries `recordsAtOnce` at a time, at least 50), and an old name is refused naming the new one, as the
  move out of `reliability` was. The other kinds kept `concurrency`, which counts what each of them runs at once, until the next amendment.
- **Every kind writes `recordsAtOnce`.** Once the delivery's keys were renamed, the estate's assertion and dimension
  flows were written with `parallelism.recordsAtOnce` as well, following the delivery's table, and were refused: the
  other kinds had kept `concurrency`. The human chose to change the code, not the documents: every kind takes
  `parallelism.recordsAtOnce`, so a flow's settings read alike whatever its kind and an author need not know which kind
  spells the setting how. Keeping `concurrency` on the other kinds, which this record first decided because there the
  setting counts tests, dimensions, version lists or kinds rather than records, was rejected for that reason, and so was
  a name for each kind (`testsAtOnce`, `dimensionsAtOnce`), which would have changed the estate's documents again. What
  the key counts on each kind is on that kind's reference page. `parallelism.concurrency` is refused on those kinds,
  naming `recordsAtOnce`, as the delivery's old names are.
- **The split is fail safe.** The ranges are contiguous and exhaustive by construction (the first has no lower bound,
  the last no upper bound, each starts where the one before ends) and are recorded on the submission before any part
  starts, so a part executed again, or taken over, reads exactly its range. A range a part did not deliver is delivered
  by the first run, and a record whose document was on a lost node is held to be planned again, never dropped and never
  sent from a document that is not its own.
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
- A large delivery is split into up to eight parts, each on a node of its own and wherever its `source.work` is, and an
  autoscaled pool brings up a node for each; a flow wanting one node sets `parallelism.nodes: 1`. Each part keeps its own `parallelism.recordsAtOnce`, so the records in flight against OSDU
  multiply by the runs; the rate limit is what bounds them as a whole.
- Every run's trace says why it did not fan out, which is the first thing to read when a pool does not scale.
- On a pool with no place free, a fanned-out intake takes up to three minutes longer than one run planning alone; a
  drain takes nothing longer.
- The engine suite proves both paths with a fan-out no node ever starts, and the work batch rewrite with a share planned
  again (`osdu/tests/SqlFlow.Delivery.Tests/ScaleEngineTests.cs`).
