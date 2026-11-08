---
id: delivery-wiki-0050-dimension-builds-over-nodes
title: "Decision 0050: a large dimension build is spread over nodes, a share of its dimensions on each"
type: decision
summary: "Why a dimension build is split by its dimensions, sized by the records they read, with the splitting run building the largest part and the views."
keywords:
  - "dimension build"
  - "nodes"
  - "minrecordspernode"
  - "scale out"
  - "fan-out"
  - "parts"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Spread.cs
  - osdu/src/SqlFlow.Delivery/Engine/FanOut/FanOutWait.cs
  - osdu/src/SqlFlow.Delivery/Engine/DimensionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
referenceRefs:
  - delivery-flow-dimension-nodes
  - delivery-flow-dimension-build
  - delivery-flow-dimension
related:
  - delivery-wiki-0025-parallelism-and-default-fan-out
  - delivery-wiki-0049-schedules-as-wide-as-their-waves
updated: 2026-11-08
---
# 0050: A large dimension build is spread over nodes, a share of its dimensions on each

Status: accepted. Extends [0025](0025-parallelism-and-default-fan-out.md) from a delivery to a dimension build, through
the same run fan-out. Documented in [builds over nodes](../../reference/flow/dimension-nodes.md).

## Context

An estate's metadata dimension flow builds sixteen dimensions, several of them over more than a hundred thousand records,
and every build ran on one node however large it was. The owner wants the dimensions built concurrently, more nodes
coming up when there is more to build. A build ran its dimensions `recordsAtOnce` at a time in one run, and the executor
never handed the run the fan-out the platform gives every run, so a build had no way to ask for a node.

Two things bound what a split can give. A dimension is one read of the search, staged and written in one transaction, so
one run builds it, and a build takes at least as long as its largest dimension. And a full build of that flow on a
development partition took 206 to 317 seconds, its largest dimension 183 to 278 of them, while its incremental builds took
seconds: a node takes minutes to start, so a split pays only on a build reading a lot.

## Decision

- **A build is split by its dimensions.** The run deals the dimensions it builds into parts and queues a run of the flow
  for each part but its own, naming the part's dimensions in the payload a run already takes, each asking for a node of
  its own as a delivery's parts do. No new run shape, no hand-off between nodes: a part is a build of fewer dimensions.
- **Parts come from the records read, at most `nodes`.** The run counts what each dimension will read, as a plan counts
  it (the query's records for a full load, the window's changes for an incremental one), and takes a part per
  `minRecordsPerNode` (50,000) of them, at most `nodes` (8) and never more than the dimensions. The settings read as a
  delivery's do (`nodes`, `minRowsPerNode`), so a flow's settings read alike whatever its kind.
- **The largest dimension first, to the lightest part.** Dimensions are dealt by the records they read, so the parts end
  near one another, and the first part, which holds the largest dimension, is built by the splitting run, which starts at
  once while the other parts wait for their nodes.
- **The splitting run writes the views and takes over what a part did not build.** A view joins tables several parts
  write, so it is written once all are built. What a part kept is read from the ledger, which holds every build under the
  run that made it; a part no node starts within the delivery's grace is stopped and built here, with the same wait as a
  delivery's (`FanOutWait`, shared by both).

## Rejected

- **Split the flow into several files.** A view joins only the dimensions of its own flow, so the views would have to go,
  and every estate would size its own files where the run can size the parts from what it reads.
- **One run per dimension, sharing nodes.** Runs packed four to a node would not follow `nodes`, and a part of one small
  reference dimension would cost a run's start for a second's work.
- **Split one dimension over nodes.** Its read pages through ranges the search answers one after another, and its write
  is one transaction; splitting it would mean passing staged keys between nodes, which 0025 ruled out for deliveries.

## Consequences

- A weekly full build of a large flow uses as many nodes as its records call for; a nightly incremental build that read
  little stays on one node.
- Each node asks the search as much as a build alone did, so a build over eight nodes can ask eight times as much of OSDU
  at once. `reliability.rateLimitRps` is shared between the parts, and `nodes` bounds the rest.
- A build still takes as long as its largest dimension: the split shortens a build of many large dimensions, not one.
