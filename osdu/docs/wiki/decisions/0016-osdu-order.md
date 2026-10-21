---
id: delivery-wiki-0016-osdu-order
title: "Decision 0016: OSDU's relationships order a repository's waves, and dependencies between schedules are shown, not enforced"
type: decision
summary: "Why lineage orders delivery flows by the relationships OSDU's schemas declare, and why schedules that depend on each other are shown rather than chained."
keywords:
  - "osdu order"
  - "referential order"
  - "relationship"
  - "x-osdu-relationship"
  - "waves"
  - "schedule dependencies"
  - "cross schedule"
  - "osdu lineage by release"
referenceRefs:
  - delivery-concept-osdu-order
  - delivery-concept-lineage
  - delivery-flow-interfaces
  - concept-lineage-graph-and-plan
related:
  - delivery-wiki-0009-searched-references
  - delivery-wiki-0017-pipelines-update-the-cache
  - delivery-wiki-0018-template-folders
updated: 2026-10-22
---
# 0016: OSDU's relationships order a repository's waves, and dependencies between schedules are shown, not enforced

Status: proposed. Documented in [the OSDU order](../../reference/concepts/osdu-order.md) and [lineage](../../reference/concepts/lineage.md).
Answers the ordering question [0009](0009-searched-references.md) left to lineage.

> **Amended by [0018](0018-template-folders.md).** "A kind whose template is not saved" below reads "a kind no template
> folder on the mapping's path holds": lineage reads a kind's relationships from the template the folders give the
> mapping, and from a built release of OSDU's lineage when they give none. The order the catalog keeps reads that
> template from the file the sync recorded, in the repository's synced folder, and a release only when the file cannot
> be read, since a mapping whose template no folder holds does not load.

## Context

OSDU's storage service does not check references: a well log naming a wellbore that does not exist yet is accepted, and
points at nothing. So the order records reach a partition in is the delivery's to keep, and a partition is filled from
nothing in that order: reference data, then wells, then wellbores, then the work product components that refer to them.

Lineage ordered flows by what their mappings read: the ingestion tables, the cache types and the kinds a search looks
in. A reference written any other way (an id built from a column, a literal, a `ref` checked against a cache flow that
also captures what the delivery writes) gave no order. A delivery checking its records against a cache, and the cache
flow capturing what that delivery writes, each read what the other writes, and lineage ordered neither: the capture
could run first and miss what the delivery was about to send.

A partition is often filled by several repositories, each with its own schedules. A schedule runs the flows of one
repository in that repository's waves; schedules fire on their own clocks, or behind schedules of the same repository
they name in `after:`.

## Decision

### The OSDU order

- The order records must reach a partition in is derived from OSDU's own schemas: a mapping that fills a property its
  template marks with `x-osdu-relationship` refers to that entity type, however it fills it (a column, a literal, the
  cache, a search). It is an order of entity types: `WellLog` after `Wellbore` after `Well`.
- A reference to a type nothing in the partition delivers adds nothing; neither does a reference to the record's own
  type (a parent wellbore), which record waits order.
- Cycles are cut by fixed rules, so the order is acyclic and the same whoever asks: a reference from an earlier OSDU group
  to a later one gives way (reference data, master data, datasets, work product components, work products); among types
  of one group, a reference no search backs gives way to one a search backs; what is still circular is left unordered,
  with a warning naming the types. Every cut is reported with its rule.
- A kind whose template is not saved is read from a built release of OSDU's lineage: the relationship graph of a release
  of the OSDU data definitions, built on demand and kept in the module database, any release, newest built first.

### Lineage takes the order, within a repository

- A delivery flow's mapping node reads each entity type the order says its records wait for, as the pattern
  `*:*:<group>--<Entity>:*` on its platform and partition. Lineage binds the pattern to the exact kinds the repository's
  other flows write, so the flow delivering wells runs before the one delivering wellbores even when the wellbore mapping
  writes the well's id from a column. Only references the order keeps are read, so the reads add no cycle.
- A delivery no longer reads a cache type whose every holder also captures what the delivery writes: the cache flow then
  runs after the delivery, instead of the two being ordered in neither direction.
- The order lineage takes is the order of the repository's own deliveries. A schedule runs flows of one repository, so
  its flows run in that order; another repository's deliveries play no part in this repository's waves, and a change to
  them never computes this repository's lineage again.
- The repository sync checks the waves it stamped against the order: a reference between two of the repository's flows
  whose provider does not run in an earlier wave is a warning on the sync, naming both flows and why.

### Between schedules: shown, not enforced

- Nothing waits across schedules. Each schedule fires on its own clock, or behind the schedules of its repository it
  names in `after:`.
- The order of each partition with every repository's deliveries together tells which schedules run best before which:
  a schedule whose flows refer to records another schedule's flows deliver. The control plane serves it
  (`GET /api/v1/delivery/order/schedules`), and the Order page draws it, with how each pair's next fires fall (chained,
  the earlier fires first, the later fires first, the earlier does not fire) and the schedules that refer to each other in
  a circle. Fixing a wrong timing is the operator's: chain the schedules with `after:` when they are one repository's, or
  move a clock.

## Consequences

- An estate whose flows already ran in a safe order keeps it; the new reads explain it. Flows that referred to each other
  only by ids written from columns or literals now run in the order of what they refer to.
- The order needs the templates the mappings pin, or a built release holding the kind. A delivery of a kind neither
  holds orders nothing, and lineage warns on it.
- Interface rows record the partition each interface's records reach (`[osdu].[Interface].[TargetPartition]`), and the
  module keeps OSDU's lineage by release (`[osdu].[SchemaLineage]`, `[osdu].[SchemaLineageKind]`).
- A release built after a repository's mappings were synced, holding a kind one of them fills with no saved template,
  computes that repository's lineage again at its next sync.

## Open

- A record referring to a record another schedule has not delivered yet goes out referring to nothing, unless
  `target.verifyReferences: storage` holds it or another record of the ledger holds the id and has not landed it (record
  waits). Keeping the partition's cache current from the deliveries themselves, so the flows that follow in a schedule
  render against what was just delivered, is [0017](0017-pipelines-update-the-cache.md).
