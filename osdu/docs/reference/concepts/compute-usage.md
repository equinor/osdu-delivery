---
id: delivery-concept-compute-usage
title: "Compute usage: how long the nodes worked, in compute units, and which team each piece of work is charged to"
type: concept
summary: "Every run attempt and compute task a node does is measured in wall-clock time and compute units, charged to its team, repository and flow, and summed per day."
keywords:
  - compute usage
  - compute unit
  - compute units
  - wall clock
  - wall time
  - metering
  - meter
  - usage
  - core hours
  - cost division
  - chargeback
  - team
  - team tag
  - licence unit
  - license unit
  - node work
  - osdu.ComputeUsage
  - osdu.ComputeUsageDay
  - osdu.RepositoryTeam
  - compute-usage api
related:
  - delivery-flow-repository
  - delivery-table-compute-usage
  - delivery-table-compute-usage-day
  - delivery-table-repository-team
  - delivery-concept-storage-and-retention
  - delivery-concept-control-plane
  - delivery-concept-api
  - delivery-concept-menu
  - delivery-concept-run-trace-and-metrics
sourceRefs:
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeWork.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageMeter.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/MeteredNodeTransport.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageRecorder.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Model/RepositoryDefinition.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryComputeUsageEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Maintenance/OsduStorageContributor.cs
  - osdu/gui/src/features/delivery/compute/ComputeUsagePage.tsx
  - osdu/gui/src/features/delivery/compute/computeUsageModel.ts
  - osdu/gui/src/api/computeUsage.ts
---

# Compute usage: how long the nodes worked, and which team each piece of work is charged to

Every piece of work a node does is measured: how long the node held it by the wall clock, on how many of the node's
cores, and for whom. The measure is the **compute unit**, one core held for one hour. It is summed per day and divided
over teams, repositories, project folders and flows, so a deployment several teams share can say how much of its
compute each of them used.

It is a measurement and nothing more: no price, no limit and no licence is attached to it, and nothing a flow does
depends on it.

## The compute unit

One compute unit is one core held for one hour.

```text
compute units = wall-clock hours x the node's cores x slots held / the node's slots
```

| Term | What it is |
| --- | --- |
| Wall-clock hours | How long the node held the work, from the moment it was handed it until it reported the outcome, on the node's own monotonic clock. |
| The node's cores | The processors the node's process has, as it counts them itself. In a container that is the CPU limit the container was given. |
| The node's slots | Its run slots plus its compute task slots: how much work it takes at once. |
| Slots held | One for a run and for a compute task; every run slot for a run that takes a whole node (a part of a delivery spread over nodes, for one). |

A node's cores are shared evenly over its slots, so work is charged the cores its slots stand for, and a node never
yields more compute units in an hour than it has cores. A run of 90 minutes on a node of 8 cores and 6 slots (4 run
slots, 2 compute task slots) is `1.5 x 8 x 1 / 6 = 2` compute units; the same run taking the whole node is
`1.5 x 8 x 4 / 6 = 8`.

Every value of the formula is kept on the row of the work
([`osdu.ComputeUsage`](../tables/compute-usage.md): `WallClockMs`, `Cores`, `Slots`, `NodeSlots`), so a figure can be
worked out again from the row alone, and another rule can be applied to the same rows later.

## How work is measured

A node is handed work in the answer to a poll of the control plane and reports its outcome on the same line. The meter
listens on that line, on a standalone node and on the control plane's own node alike, so it sees every run of every
flow kind, SQLFlow's and this module's, and every compute task, and it changes nothing the node sends or receives.

| What happens | What is measured |
| --- | --- |
| The node is handed a run or a compute task | The clock starts. A run handed out again after a failed attempt is a piece of work of its own, with its attempt's number. |
| The node reports the outcome | The clock stops, whatever the control plane makes of the report: `completed`, `failed` or `cancelled`. A run that completed is one the node carried to its end; whether its flow succeeded is the run's own result. |
| The control plane revokes the lease, or the node no longer holds the work at its next poll | The clock stops there, and the work is `interrupted`. |
| The node stops while it still holds work its drain did not finish | The clock stops when the node has stopped, and the work is `interrupted`. |
| The node dies outright | Nothing: no process is left to say how long the work ran. The work is measured again from the start when another node is handed it. |

Measuring never waits for the database and never fails the work: an ended piece of work goes on a queue in the node's
memory, and a background service writes the queue to [`osdu.ComputeUsage`](../tables/compute-usage.md) in batches. A
write that fails is tried again, after 5 seconds and then twice as long each time up to a minute, and each measurement
carries an id of its own, so a batch sent twice lands once. The queue holds 10,000 measurements; one that does not fit
is dropped and the node's log says how many were. A node started without the module's database measures nothing and
says so once in its log.

Not measured, because no node does it: a run started on someone's own machine with `sqlflow run`, and what the control
plane does in its own process for a page (a probe, a record read back from OSDU, a preview).

## Teams

A flow belongs to the team its [repository document](../flow/repository.md) names:

```yaml
# osdu-delivery.yaml, at the root of the repository
documentType: repository
code: wl
team: Subsurface data
```

- **The nearest document wins.** A folder may hold a document of its own, and the flows under it take its team; a
  document that names no team leaves its flows with the team of the document above it.
- **Every flow in the folder, of every kind.** The team is found by where the flow's file is, so a pre-ingestion or an
  ingestion flow is charged to the same team as the OSDU flows beside it.
- **The sync records it.** Every sync of a repository writes the team of each of its documents to
  [`osdu.RepositoryTeam`](../tables/repository-team.md), and a piece of work is charged to the team recorded for its
  flow's folder when the node writes it.
- **It applies from then on.** Work already recorded keeps the team it was recorded with, so a flow that changes teams
  has a share under each for the period it changed in.
- **A compute task** names no flow file, so it takes the team of its repository's root document, when its arguments
  name a repository.
- **No team** is a share of its own: work whose flow sits under no document that names one. The page says how much of
  the period that is.

Beside its team, each piece of work keeps its repository, the first folder of its flow's path (its project), the flow
(for a compute task, its operation) and who asked for the run.

## The Compute usage page

**Admin > Compute usage** (`/delivery/compute-usage`) is an admin's page.

| Part | What it shows |
| --- | --- |
| The header | A search over the division, the period (the last 7, 30, 90 or 365 days, this month or last month, in UTC days), and **Download CSV**. |
| The four figures | The period's compute units, its wall-clock time, its pieces of work, and how much of its compute units is charged to a team. |
| Compute units a day | One bar per UTC day of the period. Work is counted on the day it ended. |
| Division | Teams, each with its repositories, their project folders and their flows, the largest first, each with its compute units, its share of the period, its wall-clock time and its pieces of work. The search narrows it by team, repository, folder or flow, by how a word of the name starts. |
| Work | The pieces of work, newest first: the flow, the team and repository it is charged to, the node, who started it, its wall-clock time, the cores it held, its compute units and how it ended. Every row of the division opens it for that row. A run's flow links to the run. |
| Team tags | Which folder of which repository names which team, as each repository's last sync recorded it. |

The download holds one line per flow and team with its work, wall-clock time, compute units and share: what the page
shows, to divide a cost by.

## Keeping the totals

The work is listed piece by piece for as long as an admin leaves it: nothing removes it until a retention is set for
**Node work, piece by piece** (`osdu.compute-work`) on the Maintenance page
([storage and retention](storage-and-retention.md#what-may-be-removed-and-what-it-costs)). A cleanup then adds each
older piece of work to the total of its day, team and flow in
[`osdu.ComputeUsageDay`](../tables/compute-usage-day.md) and removes its row, in one transaction, 5,000 rows at a time.

So no usage is ever lost with a cleanup. The figures, the chart and the division read both tables, and a piece of work
is in exactly one of them at any moment. What goes is the detail: which node did the work, who asked for it, how it
ended, and the values its units were worked out from. The Work tab says from when it lists.

## The API

All three are an admin's ([API](api.md)).

| Route | Answers |
| --- | --- |
| `GET /api/v1/delivery/compute-usage?from=&to=` | The period's total, each day that has usage and each flow's share under its team. `from` and `to` are UTC days written `yyyy-MM-dd`, both included; left out, the last 30 days. A period covers at most 1,100 days. |
| `GET /api/v1/delivery/compute-usage/work?from=&to=&team=&repo=&project=&flow=&after=&take=` | The pieces of work of the period, newest first, 100 a page unless `take` (1 to 500) says otherwise; `after` is the `next` of the page before. A filter left out is not applied; an empty one asks for the work that names none (`team=` is the work charged to no team). |
| `GET /api/v1/delivery/compute-usage/teams` | Every folder that names a team, by repository then folder. |

## Where it is built

| Part | Where |
| --- | --- |
| The unit and a measurement | `ComputeUnits`, `ComputeWork` (`osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeWork.cs`) |
| Measuring on the node's line | `MeteredNodeTransport`, `ComputeUsageMeter` |
| Writing in the background | `ComputeUsageRecorder`, a hosted service every host that runs a node registers (`AddDeliveryComputeUsage`) |
| The tables, the sums and the fold | `ComputeUsageStore` |
| The teams a sync records | `DeliveryCatalogSync.SyncTeamsAsync` |
| The routes | `DeliveryComputeUsageEndpoints` |
| The page | `osdu/gui/src/features/delivery/compute/` |

[Decision 0057](../../wiki/decisions/0057-compute-usage-measured-on-the-node.md) says why it is measured on the node,
why by slots, and what was left out.
