---
id: delivery-wiki-0011-dimension-flows
title: "Decision 0011: dimension flows read every distinct value by ranges and keep each key"
type: decision
summary: "Why dimensions page through every distinct value of a field by ranges instead of the search aggregation."
keywords:
  - "dimension"
  - "distinct values"
  - "aggregation limit"
  - "ranges"
  - "keys"
referenceRefs:
  - delivery-flow-dimension
  - delivery-guide-dimensions
related:
  - delivery-wiki-0018-template-folders
updated: 2026-10-21
---
# 0011: Dimension flows read every distinct value by ranges, and keep each key beside the value a person picks

Status: proposed. Documented in [dimension flow](../../reference/flow/dimension.md).

> **Amended by [0018](0018-template-folders.md).** How the index stores a path is read from the template of each kind
> that the template folders on the flow's path hold, not from the saved templates.

## Context

OSDU answers "which values does this field hold across a kind" only through the search service's aggregation, which
returns at most `AGGREGATION_SIZE` (1,000 by default) distinct values, ordered by count, with no paging, and no cursor
query takes an aggregation. A field with more values, a wildcard kind, or a property inside a nested array, has no API
that lists all of it. The values it does return are the index's exact values, spelled as each source spelled them, so a
dimension a person can filter by needs them cleaned, and the spelling each record holds still has to be what a search
compares. A reference is worse: the index holds the id of the record it names (`dev:master-data--Wellbore:WB-0001:`),
and the name a person filters by (`Wellbore A-1`, or the wellbore's country) is in another record, which a search cannot join.

## Decision

- A dimension flow (`flowType: dimension`) is a flow kind of its own, holding dimensions: a kind (wildcards allowed), an
  optional query, one path of the record or its data, and the steps that clean each value. It only reads OSDU; it is not
  a cache or delivery option, so what it reads cannot change what a delivery sends.
- Every distinct value is read, however many: an aggregation per value range, a range the aggregation cuts off split at
  a value it returned until each answers whole, and a range that cannot be split read record by record through the
  search cursor. How the index stores the path (text through its keyword sub-field, keyword, number, boolean, date, and
  the nested array it sits in) is read from the saved template of each kind, never guessed from the path.
- Each distinct value is kept exactly as the index holds it (a **key**), beside the human-friendly **value** a person
  picks. A dimension may name a **label**: a path of the record a key names, or up to three paths, each but the last
  reading the reference to the next record. A build finds those records by id through the search service and reads the
  label there, and the label, cleaned, is the key's value; a key with no label is its own value, cleaned. A key's search
  filter and a value's are written from the keys, so a filter finds exactly the records the index holds under them,
  whatever the label and the cleaning did, and values picked across dimensions compose one search. A key keeps
  attributes read the same way, indexed, which values, keys and searches are picked by; a path segment holding objects
  can filter them (the country among a wellbore's political contexts). A value is ready for a drop-down: a key naming a
  record and no label read is valued by its id's code, its escapes decoded.
- The dimensions live in the module's database, in tables of their own keyed by the ledger partition, written per build
  in one transaction under a lock per dimension. What a build no longer finds is marked removed and keeps its id; every
  change to a key is logged. Nothing is counted separately from those rows.
- A cache flow can hold a dimension's values as a lookup table, so a mapping turns a raw key into its value as the
  dimension does, and lineage orders it after the dimension flow.

## Consequences

- A build costs requests in proportion to the values, not the records: a field of a million values over a hundred
  million records is read in about a thousand aggregations, with scans only where the index cannot be split.
- A dimension over a field with more distinct values than `maxValues` fails rather than filling the database.
- A value longer than the keyword sub-field keeps (256 characters) is not aggregated by OSDU; a build counts the records
  holding only such values rather than pretending they are absent.
- A dimension reads the index as it stands: a record written seconds before a build may not be in it yet.
- A label costs a search per 500 keys per step, so a dimension of a hundred thousand wellbore ids labelled through their
  country takes a few hundred searches; a label is read again on every build, so a renamed wellbore or country shows at
  the next build, and the change log says which keys moved to another value.
- A value standing for many keys (a country's wellbores) filters by all of them, so a composed search is bounded by the
  clauses one query holds (1,000 kept of the service's 1,024), and says so rather than truncating.
