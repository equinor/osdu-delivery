---
id: delivery-wiki-0057-compute-usage-measured-on-the-node
title: "Decision 0057: compute usage is measured on the node, in compute units, and charged to the team a repository document names"
type: decision
summary: "Why a node's work is measured on the node's own line to the control plane, counted by cores and slots, charged by a team tag in YAML, and kept as daily totals."
keywords:
  - compute usage
  - compute unit
  - metering
  - wall clock
  - team tag
  - licence unit
  - cost division
  - node transport
sourceRefs:
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageMeter.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/MeteredNodeTransport.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageRecorder.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageStore.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeWork.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Maintenance/OsduStorageContributor.cs
referenceRefs:
  - delivery-concept-compute-usage
  - delivery-flow-repository
  - delivery-table-compute-usage
  - delivery-table-compute-usage-day
  - delivery-table-repository-team
  - delivery-concept-storage-and-retention
related:
  - delivery-wiki-0029-storage-maintenance
  - delivery-wiki-0037-request-correlation
  - delivery-wiki-0048-admin-holds-admin-pages
updated: 2026-11-14
---
# 0057: Compute usage is measured on the node, in compute units, and charged to the team a repository document names

Status: accepted. Documented in [Compute usage](../../reference/concepts/compute-usage.md).

## Context

The owner asked for a measure of how long compute runs, as a unit a licence policy could one day be written in, and a
way to divide the cost of one deployment over the teams that share it. The product is open source and nothing is
licensed by it today, so the measure had to be worth having on its own (who used how much) and cheap to carry if no
policy ever comes. The model named was a sibling product's: wall-clock time times cores per execution, every base
value kept so a figure can be worked out again, a list of executions, and a daily summary older executions are folded
into.

While it was being built the owner added that a team should be a tag in YAML, beside the code the repository document
already carries for the correlation id, and written with the wall-clock time so a team's total is a plain sum.

SQLFlow, which the product runs on, records a run's start and end but nothing that sums them: no cores, no share of a
node, no team, and the time of an attempt that was interrupted is erased when the run is queued again.

## Decision

- **Measured on the node, on its line to the control plane.** Every run of every flow kind and every compute task
  reaches a node in a poll's answer and leaves in an outcome report, on a standalone node and on the control plane's
  own node alike. A wrapper of that line (`MeteredNodeTransport`) starts a clock when work is handed out and stops it
  when the node reports, so the time is the node's own, on a monotonic clock, with the preparation a run's own duration
  leaves out. The node knows its cores; nothing else in the system does.
- **Nothing in the vendored SQLFlow changes.** The node's transport is an interface a module may wrap on both hosts.
  The control plane's dispatch ledger would have seen interrupted attempts too, but it knows no core count, and
  teaching it one meant a protocol and catalog change in SQLFlow for a module's feature.
- **One compute unit is one core held for one hour, and a node's cores are shared over its slots.** A node runs several
  runs at once, so charging each run every core would count a node's capacity four times over. Work is charged the cores
  its slots stand for (one slot, or every run slot for a run that takes the node), which keeps one invariant a reader
  can check: a node never yields more units in an hour than it has cores.
- **Every base value stays on the row** (wall-clock milliseconds, cores, slots held, the node's slots), so the units of
  any piece of work can be worked out again, and a different rule can be applied to the same rows if a policy asks for
  one.
- **A team is `team` in the repository document, found as the document is found.** The nearest document above a flow's
  file names its team, so one line tags a repository and a folder can differ. The repository sync records each
  document's team by folder, and a piece of work is stamped with the team as it is written, so a team's usage is a sum
  over one column and a flow that changes teams keeps what it used under the old one. It is found by the flow's path,
  not by its kind, so the ingestion flows beside an OSDU flow are charged to the same team.
- **The work is listed piece by piece until an admin says how long, and never lost.** The list is a storage area of the
  Maintenance page like the others an admin may limit ([0029](0029-storage-maintenance.md)); its cleanup adds each row
  to the total of its day, team and flow before removing it, in one transaction, so every sum reads the same before and
  after. Nothing is removed by default, as everywhere else in the product.
- **An admin's page**, in the Admin group ([0048](0048-admin-holds-admin-pages.md)): the figures are what a cost is
  divided by.

## Rejected

- **Summing the catalog's run rows.** It loses interrupted attempts, compute tasks have no duration there, the cores
  are unknown, and a run's recorded start moves to the engine's start on completion.
- **A team entity, or teams mapped to repositories in the GUI.** A tag in the repository is reviewed and versioned with
  the flows it tags, as the repository's code is; a registry would be a second place to keep in step.
- **A tamper-evident chain of records, a licence key, prices, limits and a service to report to.** They enforce a
  licence, and there is none. The customer runs the database and may run a fork, so a chain proves little until a
  licence issues the key it is signed with. The rows carry what such a policy would need to start from.
- **A switch to turn it off.** It costs one small row per piece of work, written in the background, and a measure that
  is sometimes off cannot divide a cost.

## Consequences

- A node that dies outright leaves no measurement of what it was doing: nothing is left to say how long it ran. Work a
  node gives up while it still lives (a revoked lease, a stop that outlasts its drain) is measured as interrupted.
- A run started with `sqlflow run` on a person's machine, and what the control plane does in its own process for a
  page, are not node work and are not measured.
- Idle nodes are charged to no one. The sum of a period's compute units is the compute that was used, not the compute
  that was paid for; dividing a bill by team shares spreads the idle time in proportion.
- A compute task has no flow file, so it takes the team of its repository's root document only.
