---
id: delivery-wiki-0007-manifest-dataset-ids
title: "Decision 0007: manifest datasets take ids derived from their record's id"
type: decision
summary: "Why the datasets a manifest delivery registers get ids derived from the record, so a redelivery can replace them."
keywords:
  - "manifest"
  - "dataset id"
  - "ingestion workflow"
  - "redelivery"
  - "file generic"
referenceRefs:
  - delivery-flow-routes
  - delivery-concept-protocols
updated: 2026-10-16
---
# 0007: Manifest datasets take ids derived from their record's id

Status: proposed. Documented in [routes](../../reference/flow/routes.md).

## Context

The ingestion workflow registers the datasets a manifest carries. Left to it, the ids are minted by the
platform and never come back through the workflow API, so a redelivery could neither reference nor replace
the datasets of the previous delivery, and the ledger could not name what a record owns.

## Decision

- The `manifest` protocol gives every dataset entry an id derived from the record id and the dataset
  kind: `{partition}:{datasetType}:{recordSuffix}-{chunkIndex}`.
- The record's dataset list is written with those ids before the workflow runs, and the ids are recorded on
  the record's target state, so purge can delete them and a redelivery overwrites them.
- The `file` protocol, which registers datasets itself through the file service, lets the service assign
  the id and records what came back; the ids of every delivery stay on the attempts.

## Consequences

- A record's datasets are reconstructible from the ledger alone for both protocols.
- Client-supplied dataset ids depend on the partition accepting them, as decision 0002 already assumes for
  the records themselves.
