---
id: delivery-wiki-0009-searched-references
title: "Decision 0009: references to business data are searched for, not cached"
type: decision
summary: "Why wellbores and other business data are found by a search per record while closed vocabularies stay in the cache."
keywords:
  - "search"
  - "findby"
  - "business data"
  - "wellbore reference"
  - "cache size"
  - "reference data"
referenceRefs:
  - delivery-flow-mapping-lookups
  - delivery-flow-cache
related:
  - delivery-wiki-0003-rendering-location
  - delivery-wiki-0018-template-folders
updated: 2026-10-21
---
# 0009: References to business data are searched for, not cached

Status: proposed. Documented in [mapping lookups](../../reference/flow/mapping-lookups.md). Amends [0003](0003-rendering-location.md).

> **Amended by [0018](0018-template-folders.md).** A search's `schema` is read from the template folders on the
> mapping's path, not from the saved templates, and may name the kind alone to take the one version the nearest folder
> holding it holds.

## Context

The cache captures every record of each declared type into the catalog, so a render resolves references out of it
without reaching the platform. That suits closed vocabularies: a partition's units and type codes number in the
hundreds or thousands and change rarely. It does not suit business data. In the `dev` partition the cache held 165,381
records, and 163,818 of them (99.1%) were wellbores: every capture of them costs more as the partition grows, a wellbore
delivered today is missing from the cache until the next capture, and a lookup by name is only as current as that
capture.

## Decision

- A mapping declares a search (`searches:`) for a kind whose records it refers to by value, pinning the saved template
  whose schema says how that kind is indexed. An entry reads `search.<name>.id`, found by `findBy` lines tried in order.
- A lookup asks the platform's search service for the one record whose property is exactly the value, one value at a
  time. Values are never batched into one query with the matching record picked out of the answer afterwards.
- The query follows how the schema has the platform index the property: the `keyword` sub-field of text, the property
  itself for a link or a value inside a flattened array, the service's `nested(...)` form inside a nested array. The
  rules are read from the OSDU indexer and search service and kept in one library, `SqlFlow.Delivery.Search`, which also
  refuses every value the service would misread or could never match.
- The render stays synchronous and free of I/O. It asks what the run already knows and names what it does not; the plan
  asks a batch's questions, each distinct one once, and renders the waiting rows again.
- Exactly one record is the answer. No record is a miss, as a cache miss is. Several records, a query the service
  refuses, or a value that could not be asked for when nothing was found, hold the record whatever `required` says. A
  platform that cannot be asked fails the run: a missing answer is never taken for "no record".
- Where the partition's indexer keeps a lowercased copy of text (`featureFlag.keywordLower.enabled`), a value no record
  holds exactly is asked once more on the `keywordLower` sub-field, and that answer is taken only when it is one record.
  An exact answer always wins, and several records that match once case is ignored hold the record: codes that differ
  only by case are different records. A partition not known to keep the copy is asked exact questions alone.
- Whether a partition keeps it is one of the partition's system properties, which every capture of its cache reads from
  the indexer's and the search service's `GET /info` (`featureFlagStates`) and keeps with the version, apart from the
  cached records. A service that cannot be asked fails nothing and changes nothing the cache knew of it; a property the
  engine relies on that no service reports is recorded as unknown, with the reason.
- Fixtures declare the answers they assume and never search.
- The sample estate's WellLog and WellboreTrajectory mappings search for wellbores by name and then by alias, and the
  sample cache flow no longer captures wellbores.

## Consequences

- Planning a mapping that searches needs the platform the flow delivers to: its search service, under the flow's own
  target, credentials and partition. The offline planning [0003](0003-rendering-location.md) describes holds for a
  mapping that searches nothing.
- What a search answered is not part of the render context. The mapping version pins the searches and their schemas, so
  a record is rendered again when its row or its mapping changes, not when the platform's wellbores do.
- The render context of a mapping that searches pins the state of the system properties its lookups rely on. A mapping
  that only searches pins them in place of a cache version: a capture that changes reference data renders none of its
  records again and asks none of their questions again, while one that finds keywordLower turned on or off renders all
  of them again under the new rule.
- The cache holds vocabularies only; in `dev` a capture shrinks from 165,381 records to about 1,563.
- In lineage a flow that searches reads the searched kind, so the flow delivering those records to the partition is
  ordered before it.
- The end-to-end suite runs a stand-in for the platform that answers the sample wellbores, since its plans now search.

## Open

- A wellbore delivered by an earlier interface of the same run is found once the platform's indexer has picked it up. A
  log planned before then is held as not found, and like any held record stays held until it is released or its row
  changes. Whether a record held for a reference the platform did not hold should be planned again by the next run on
  its own is a policy question: the reference can appear later with nothing in the source changing.
