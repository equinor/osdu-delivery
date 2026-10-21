---
id: delivery-wiki-0008-retrieval-lands-raw-records
title: "Decision 0008: retrieval lands records as OSDU holds them"
type: decision
summary: "Why a retrieval flow brings OSDU records back unchanged instead of reversing a mapping."
keywords:
  - "retrieval"
  - "raw records"
  - "reverse mapping"
  - "osdu to lake"
  - "landing"
referenceRefs:
  - delivery-flow-retrieval
  - delivery-guide-retrieving-records
updated: 2026-10-16
---
# 0008: The retrieval kind lands records as OSDU holds them

Status: proposed. Documented in [retrieval](../../reference/flow/retrieval.md).

## Context

The lake needs OSDU's records back. A mapping is not invertible (an `equals` modifier collapses a string to
a boolean, a `split` discards everything but one part, static values have no source), so a reverse rendering
would be a second mapping language with none of the guarantees of the first.

## Decision

- A retrieval flow (`flowType: retrieval`) pages the search index per kind into JSON Lines files on the lake,
  rolled by record count, optionally reading every hit's full record back from storage. No rendering, no
  schema: each line is the record as OSDU holds it.
- Incremental flows carry a watermark on a record timestamp field between runs, with a lag for the indexer;
  only a completed run advances it.
- Every run has a manifest on the lake and a row in `osdu.Retrieval`, with the same counts.

## Consequences

- Projecting, joining and reshaping the landed records is the lake's job (a view or a notebook over the
  files), where it can be validated against the files themselves.
- The search index's projection is enough for most kinds; `fetchRecords` costs one storage request per
  hundred records and is opt-in.
