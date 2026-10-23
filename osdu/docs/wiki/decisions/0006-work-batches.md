---
id: delivery-wiki-0006-work-batches
title: "Decision 0006: rendered documents live in work batch files, not in the ledger"
type: decision
summary: "Why rendered documents are written to work batch files the ledger points at, and how large submissions fan out."
keywords:
  - "work batch"
  - "rendered documents"
  - "key slice"
  - "fan out"
  - "submission"
referenceRefs:
  - delivery-concept-submissions
related:
  - delivery-wiki-0025-parallelism-and-default-fan-out
updated: 2026-10-23
---
# 0006: Rendered documents live in work batch files, not in the ledger

Status: proposed. Documented in [submissions](../../reference/concepts/submissions.md).

> **Partly superseded.** The decision itself stands: rendered documents live in work batch files and the ledger holds
> pointers, not documents. What has been replaced is the input it describes. There is no drop and no drop reader any
> more: the intake streams the flow's **ingestion tables**, and a large submission fans out over **key slices** of the
> record key rather than over a drop's partitions. Read "the drop" below as "the rows the ingestion tables changed",
> and "drop partition" as "key slice". See [concepts/architecture.md](../../reference/concepts/architecture.md).

## Context

A source can hold millions of rows. Holding the rendered documents on the ledger's record rows made every
intake a write of the whole set into SQL Server and every drain a read of it back, and it put the intake's
memory at the mercy of the source's size.

## Decision

- The intake streams the source through a bounded rendering pipeline and writes the rendered documents to JSON Lines
  work batch files under the flow's work location (`source.work`), `parallelism.batchRecords` documents per file.
- The record row carries the batch number and the document's byte range (`WorkBatch`, `PendingDocumentRef`);
  `osdu.WorkBatch` carries one row per file with its status, lease and counts.
- A drain leases a whole batch, reads the documents by range, and hands the protocol up to `batchSize`
  records per request. Completion is bulk (a table-valued merge on SQL Server).
- A submission of at least `parallelism.fanOutMinRecords` records spreads its intake (by key slice) and its drains (by
  batch) over member runs across the fleet, as one run family; since [0025](0025-parallelism-and-default-fan-out.md)
  it does so by default, and the settings that say how much runs at once are the flow's `parallelism`.

## Consequences

- Intake memory is bounded by the pipeline's capacity, not the source; the ledger holds pointers, not documents.
- The work location needs write access from every node that runs the flow, and its files are part of the
  submission's evidence until the submission completes.
- A record's pending document is read from storage by the nodes; the API and the GUI show its reference and
  batch, not its bytes.
