---
id: delivery-wiki-0017-pipelines-update-the-cache
title: "Decision 0017: A pipeline names the cache it needs current before it runs and the cache it updates after"
type: decision
summary: "Why the cache steps belong to the pipeline, why before is mandatory and after is not, why after reads storage, and how held records wait for what they miss."
keywords:
  - cache.before
  - cache.after
  - update the cache before a pipeline
  - update the cache after a pipeline
  - cache update during a schedule
  - held record waits for a reference
referenceRefs:
  - delivery-concept-partition-cache
  - delivery-flow-delivery
  - delivery-flow-cache
  - delivery-concept-lineage
related:
  - delivery-wiki-0016-osdu-order
updated: 2026-10-20
---
# 0017: A pipeline names the cache it needs current before it runs and the cache it updates after

Status: proposed. Documented in [the partition cache](../../reference/concepts/partition-cache.md#pipelines-bring-the-cache-up-to-date)
and [the delivery flow](../../reference/flow/delivery.md#cache). Answers what [0016](0016-osdu-order.md) left open.

## Context

A schedule runs one repository's flows in its waves: reference data first, then the records that refer to it. A mapping
resolves its references through the partition's cache, which a cache flow's refresh fills. A schedule that delivered
units and then the wells referring to them rendered the wells against a cache that did not hold the new units yet: the
reference went out unverified, or the record was held, and nothing brought it back once the unit was there.

Running the cache flow between the two deliveries does not close the gap well. A refresh searches every record of each
type it declares, a full sweep to add the few records one run landed, and the search index holds a record only a moment
after it is written, so a refresh straight after the delivery can miss what the delivery just sent. A partition is also
filled by several repositories, so what a pipeline needs current may be delivered by another repository, another
schedule, or by hand.

## Decision

- **The pipeline declares its cache steps,** in a `cache` block of the delivery flow (or of one interface): `before`, the
  types it needs current before it renders, and `after`, the types it brings up to date as it ends. Each is `auto` or a
  list of entries. An entry is a cache type by name, or an OSDU entity type, which stands for every type of the cache
  holding it whichever repository's cache flow declares it: the steps work on the OSDU dataset's own terms. The cache flow
  says what the cache holds; the pipeline says when it must be current.
- **Entries are resolved before anything is read,** against what the partition's synced cache flows declare. An entry
  naming nothing the cache holds, in `before` a dictionary or a dimension, in `after` a lookup table or a type holding
  another kind or narrowed by a query in every declaration, stops the run with nothing sent.
- **The before step is mandatory.** It searches each OSDU type as each declaring cache flow would, through the pipeline's
  own connection to its partition, and merges the capture as that flow's own; a lookup table from an ingestion table is
  read over its cache flow's connection. A type it cannot read whole fails the run before anything is sent: the pipeline
  said it must not render without it.
- **The after step is not the run's failure.** It takes what the run delivered, removed or reverted from the ledger and
  reads those records back from OSDU's storage service by id, which holds a record once its write returns. A record
  storage returns goes in, held by the delivery; one storage no longer holds is taken out whoever held it. What the run did
  in OSDU stands whatever the step does, and the next refresh settles a step that failed. It is bounded: a run changing
  more than 50,000 records reads none and says to run the refresh.
- **The run renders against the newest cache when it starts,** after its before step, so the interfaces of one source see
  what the interfaces before them put in.
- **A held record waits for what it misses.** A render held because a reference names an id the cache lacks records that
  id, apart from what a delivered document was built from (`[osdu].[Record].[HeldCacheSetId]`); a version that brings it
  in releases the held record under an activity naming the version. Each step carries the approved changes it wrote out
  at once, up to 100,000 records, so the pipelines after it in the schedule find them due.
- **Lineage orders the steps:** a pipeline writes the types its steps name, and reads the kinds its before step searches.
  Nothing waits across schedules ([0016](0016-osdu-order.md)).
- A cache run may name the types it refreshes (payload `types`), so one type is refreshed without sweeping the others.

## Consequences

- A schedule that delivers reference data and then the records referring to it renders the second against the first
  with no refresh in between, once the second names the reference types in `cache.before`, or the first names them in
  `cache.after`.
- A record held for a missing reference comes back without an operator's release once the reference is cached.
- A delivery run's result carries `cacheBefore` and `cacheAfter`, and its page shows them under **Cache**.
- Each step that changes the cache writes a version. It is a capture like any other: a version that changes nothing is
  not written, and retention prunes the rest.
- A removal that deletes records from the ledger hands the after step their ids, and a deletion of the ledger takes the
  step before the ledger is gone, so neither leaves records in the cache that OSDU no longer holds.
