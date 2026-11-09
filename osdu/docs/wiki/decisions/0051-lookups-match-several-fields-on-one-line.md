---
id: delivery-wiki-0051-lookups-match-several-fields-on-one-line
title: "Decision 0051: a lookup matches several fields on one findBy line, and a cached type keeps one key"
type: decision
summary: "Why a record is found by several fields joined by and on one $findBy line, not by a composite key, and how its misses and its index are kept."
keywords:
  - "composite lookup"
  - "multi-column join"
  - "identity key"
  - "findBy and"
  - "composite miss"
  - "composite index"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheUsage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
referenceRefs:
  - delivery-flow-mapping-lookups-composite
  - delivery-flow-mapping-lookups
  - delivery-guide-lookup-table-cache
  - delivery-table-cache-set-entry
  - delivery-concept-partition-cache
related:
  - delivery-wiki-0009-searched-references
  - delivery-wiki-0044-findings-say-why-and-prove-it
updated: 2026-11-09
---
# 0051: A lookup matches several fields on one findBy line, and a cached type keeps one key

Status: accepted. Documented in [a line of several comparisons](../../reference/flow/mapping-lookups-composite.md).

## Context

A lookup table is kept under one key column, and a `$findBy` line compared one field. Several lines were alternatives,
tried in order, never conditions a record had to meet together. A table whose rows only several columns tell apart (a
curve type per well and mnemonic) could be read only by building one key column out of them on both sides: in the
ingestion SQL of the table and again in the delivery's own SQL, or through an `$expr` and a `replace`. The owner asked
for a lookup on several columns at once.

## Decision

- **A cached type keeps one key.** A lookup table is kept under one unique column, as before; a table no one column
  tells apart is keyed by an identity column. A row's identity is its key, and the ledger's dependencies, change tags
  and refresh comparisons rest on that identity, so none of them changes.
- **A `$findBy` line may compare several fields, joined by `and`.** The record found holds every value, matched by the
  steps a single value is (exactly, then ignoring case, then folding separators when asked), applied to the values
  together. The lines stay alternatives tried in order. The word `and` mirrors the mapping's expressions and reads as
  the SQL join it stands for; inside quotes it is text.
- **A line of several reads the cache only.** A search asks the platform one property a query, and a `$findAll` already
  takes each condition as a line of its own, so both refuse a line joined by `and` rather than reading it some other way.
- **What a line found is recorded by each value; what it did not find, by all of them.** A found record gets one match
  per value, so a later version in which it stops holding any one of them tags the records `unmatched`. Values no record
  met together are kept as one ledger entry whose item is a marker, then the values folded as a JSON list. A refresh
  reads a type's entries of that kind by the marker (a seek of the existing index on the item) and asks both versions
  for the values together, for a lookup table as for OSDU records, so only a record meeting all of them tags the
  records `listed`.
- **The index is in memory, per combination, per version.** Each combination of fields a line compares is indexed the
  first time a render asks for it, as a single field already was, and dropped with the version a run loads. A
  combination one record holds more than 64 sets of values under is not indexed and is matched by reading candidates,
  with the same result.

## Rejected

- **A composite key on the cache flow** (`key: [well, mnemonic]`). Every row identity, ledger entry and change tag
  would hold a tuple, and every type's refresh comparison with it. An identity column gives the table one key, and a
  line of several comparisons gives the lookup every combination, including ones the table's key was not built for.
- **Recording a miss as one entry per value.** The entries a set keeps are told apart by item and path, so two misses
  sharing a value (two curves of one well) would collapse into one and lose the other's values, and a refresh would
  tag records whenever a row shared any one value with what they looked for. One entry per miss, judged by all its
  values, tags exactly the records a new row reaches.
- **Finding composite misses by a value's key**, as single misses are found. A row arriving holds every value of the
  miss, but which of the type's combinations the ledger holds cannot be known from the row; reading the type's
  composite misses by their marker is one indexed range and needs no list of combinations.
- **A database index for the lookup.** A render never asks the database for a cached record: it loads the version it is
  pinned to and matches in memory, so a database index would serve nothing there. An index on the source table would
  change a flow's source, which this project never does on its own, and a capture reads the whole table either way.

## Consequences

- An estate with a table read by several columns adds an identity column to it and a line joined by `and` to the
  mapping; the delivery's own SQL no longer builds a key for it.
- A miss of a lookup table found by a line of one value is still not recorded, as before this decision; a line of
  several is recorded whichever type it reads.
- Values too long to be kept whole as one ledger item (512 characters, as a JSON list) are not recorded, as a key no
  table could hold is not.
