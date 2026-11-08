---
id: delivery-wiki-0049-schedules-as-wide-as-their-waves
title: "Decision 0049: a schedule runs every flow of a wave at once, in waves of the flows it runs"
type: decision
summary: "Why a schedule that sets no maxConcurrency is unbounded in OSDU Delivery, and why a fire's waves count only the flows that fire runs."
keywords:
  - "schedule"
  - "maxconcurrency"
  - "fire width"
  - "waves"
  - "nodes"
  - "scale out"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - sqlflow/src/SqlFlow.Core/ScheduleFireDefaults.cs
  - sqlflow/src/SqlFlow.Catalog/RunScopeExpander.cs
  - sqlflow/src/SqlFlow.Lineage/Graph/MemberWaves.cs
  - sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs
referenceRefs:
  - delivery-guide-deployment
  - delivery-concept-lineage
  - delivery-concept-osdu-order
  - flow-schedule
related:
  - delivery-wiki-0016-osdu-order
  - delivery-wiki-0025-parallelism-and-default-fan-out
updated: 2026-11-08
---
# 0049: A schedule runs every flow of a wave at once, in waves of the flows it runs

Status: accepted. Refines [0016](0016-osdu-order.md), whose order within a repository stands, and extends
[0025](0025-parallelism-and-default-fan-out.md) from one delivery to a schedule's fire. Documented in
[the deployment guide](../../reference/guides/deployment.md#the-images), [lineage](../../reference/concepts/lineage.md#how-the-nodes-order-flows)
and SQLFlow's [schedule page](../../../../sqlflow/docs/reference/flow/schedule.md#fire-width-maxconcurrency).

## Context

A refresh of an estate's sixteen OSDU cache flows, one schedule, ran on one node however it was started. The owner
expected several nodes to share the work. Two things held it on one node.

- **The width.** A schedule that sets no `maxConcurrency` ran four of its flows at a time, SQLFlow's default. A node also
  runs four flows at once, and the replica target the autoscaler and the managed fleet follow counts only the queued
  runs no gate holds back (`ScaleTargetStore`): with four running, the rest waited on the schedule's cap and asked for
  no node. An untuned schedule could never use a second node.
- **The order.** A fire put its flows in the repository's lineage waves. Those count every flow of the repository, and
  most of the cache flows follow a delivery of the `reference-data` schedule, each a different one. So the fire ran as
  four waves of 9, 5, 1 and 1 flows, though none of the sixteen reads what another writes, and the deliveries between
  them do not run in that fire.

The owner's requirement: when there is more work and some flows take time, more nodes start. Ordering applies within a
schedule; schedules handle their dependencies on each other themselves ([0016](0016-osdu-order.md)).

## Decision

- **A fire's waves count only the dependencies among the flows it runs.** A flow waits for a flow of the same fire it
  depends on, and for nothing that fire does not run, directly or through a flow outside it. This is SQLFlow's rule now
  (`RunScopeExpander.InWaves`), with the algorithm batch flows already used (`MemberWaves`), so a flow run with its
  descendants is ordered the same way. The sync still stamps the repository's waves and checks the OSDU order against
  them.
- **OSDU Delivery's schedules are unbounded unless they say otherwise.** The delivery module registers
  `ScheduleFireDefaults.Unbounded`, an extension point SQLFlow added for a product to choose its own default. Every flow
  of a wave is queued at once, and the replica target asks for a node per four of them. A schedule that should spare
  something its flows share sets `maxConcurrency`.

## Rejected

- **Change SQLFlow's default to unbounded.** SQLFlow's reason for four holds for SQLFlow: the members of a wave usually
  read one SQL Server, and an untuned schedule should not open every connection it can against it. OSDU Delivery's flows
  bound their own load on OSDU (their `parallelism`), so the product chooses differently, and SQLFlow keeps its default.
- **Leave the default and set `maxConcurrency` in each estate.** Every schedule of every estate would need it, and one
  that forgot it would stay on one node with no sign why, which is how this was found.
- **Split one cache flow over nodes, as a delivery is.** A cache refresh reads OSDU's search page by page, behind one
  cursor, and the sixteen took 29 seconds together; a node takes longer than that to start. More nodes help a fire with
  more flows, which this decision gives, not a single refresh.

## Consequences

- A fire with more flows in a wave than one node runs brings up more nodes, up to the pool's ceiling (10 by default), so
  OSDU can see up to 40 flows at once from one estate.
- A schedule whose flows share something fragile, such as a source database, sets `maxConcurrency`, as in SQLFlow.
- A fire that is over in seconds still finishes on the nodes already running: a node started for it finds nothing left
  and leaves.
- A flow that waited in its schedule's fire for another schedule's deliveries no longer does. When it must run after
  them, its schedule is chained behind theirs (`after:`), as 0016 has it.
