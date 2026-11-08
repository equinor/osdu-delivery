---
id: delivery-flow-dimension-nodes
title: "Dimension builds over nodes: parts, how dimensions are dealt, and what the splitting run does"
type: flow-reference
summary: "How a dimension build is spread over nodes: nodes and minRecordsPerNode, how its dimensions are dealt into parts, and what each run does."
keywords:
  - nodes
  - minrecordspernode
  - spread over nodes
  - dimension build nodes
  - one node
  - fan-out
  - parts
  - rateshare
  - scale out
yamlPath: "parallelism"
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-build
  - delivery-flow-dimension-incremental
  - delivery-concept-submissions
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Spread.cs
  - osdu/src/SqlFlow.Delivery/Engine/FanOut/FanOutWait.cs
  - osdu/src/SqlFlow.Delivery/Engine/DimensionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
---

# Dimension builds over nodes: parts, how dimensions are dealt, and what the splitting run does

A [dimension flow](dimension.md)'s build reads many dimensions, each through its own searches. A large build is split into
parts, and each part builds its share of the dimensions on a node of its own, so a build with many large dimensions brings
up more nodes. A dimension is never split: one run builds it, so a build takes at least as long as its largest dimension.

```yaml
parallelism:
  recordsAtOnce: 4        # on each node: the dimensions built at once, and each one's searches at once
  nodes: 8                # the most nodes a build is spread over
  minRecordsPerNode: 50000
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `nodes` | integer | 8 | The most nodes a build is spread over, 1 to 64, the node of the run that splits it among them. 1 keeps every build on one node. |
| `minRecordsPerNode` | integer | 50000 | The fewest records a part reads, so a node is started only for work enough to be worth it. At least 1. |

## When a build is split

The run counts the records each dimension it builds will read, as a [plan](dimension-build.md#operations) counts them:
the records its query matches for a full load, those that changed in its window for an incremental one
([full and incremental loads](dimension-incremental.md)). The build is split into a part per `minRecordsPerNode` records,
at most `nodes` parts and never more than the dimensions it builds. One part is no split: the run builds every dimension
itself, as before.

| The build | Parts |
| --- | --- |
| Fewer than 100,000 records to read (twice `minRecordsPerNode`) | 1: one run builds them |
| 100,000 to 149,999 | 2 |
| 400,000 or more, over 8 dimensions or more | 8, the most `nodes` allows |
| Any number, over 3 dimensions | 3 at most |

A build is not split, and says why in the run log, when the flow sets `nodes: 1`, it builds one dimension, the run
cannot queue runs (`sqlflow run` on a laptop, outside the fleet), or counting the records failed. An incremental build
that found little changed reads few records, so a nightly build usually stays on one node, and its weekly full load spreads.

## How the dimensions are dealt

The dimension reading the most records goes first, each to the part reading the fewest so far, and no part is left empty;
ties go by the order the flow declares them. The first part, holding the dimension that reads the most, is built by the
run that split the build, which starts at once; every other part is queued as a run of the same flow, asking for a node to
itself, so the autoscaler starts a node for each part no idle node takes. The run log names which part builds which
dimensions:

```text
Spread the build of 12 dimension(s), 640000 record(s) to read, over 8 parts (group ...), each on a node of its own.
This run builds Wellbore; part 2 (run ...) builds Well; part 3 (run ...) builds LogCurve; ...
```

## What a part does

A part is a `build` run of the flow whose payload names its dimensions and `rateShare`, the runs the build is spread over:

```json
{ "dimensions": ["Well"], "rateShare": 8 }
```

It builds those dimensions in the partition the splitting run builds in, `recordsAtOnce` at a time as any build does,
keeps each in the flow's ledger under its own run, and writes no view. It is never split again. Every run of the build
sends under its share of `reliability.rateLimitRps`, so the build as a whole keeps to the rate. Each node asks the search
as much as a build alone does (`recordsAtOnce` × `recordsAtOnce` at once), so a build spread over eight nodes can ask
eight times as much of OSDU at once; set `reliability.rateLimitRps`, or fewer `nodes`, where OSDU must be spared.
`rateShare` is refused on a plan, on a payload naming no dimensions, and outside 2 to 64.

## What the splitting run does

Once its own part is built, the run waits for the others and reads from the ledger what each kept. A dimension a part
completed or failed is that part's; one it never built, or stopped building, the run builds itself. A part no node has
started three minutes after the last change, while none is running, is stopped and its dimensions built here: every node
of the pool can be busy, and a pool at its most nodes starts no more. Then the run writes the flow's
[views](dimension-views.md), over every table the parts wrote, and returns how every dimension came out, whoever built it.
Cancelling the run cancels its parts.

Each build is kept under the run that made it (`osdu.DimensionRun.RunId`), so a part's builds show on that part's run,
and the splitting run's result lists every dimension of the build.
