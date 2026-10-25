---
id: delivery-wiki-0005-ledger-retention
title: "Decision 0005: attempts are pruned by age, keeping the latest per record"
type: decision
summary: "Why the ledger's attempt history may be pruned by age while every record keeps its latest attempt."
keywords:
  - "retention"
  - "attempts"
  - "pruning"
  - "ledger size"
  - "history"
referenceRefs:
  - delivery-concept-storage-and-retention
  - delivery-concept-availability-and-retention
  - delivery-concept-ledger
related:
  - delivery-wiki-0029-storage-maintenance
updated: 2026-10-25
---
# 0005: Attempts are pruned by age, keeping the latest per record

Status: proposed. Documented in [What the osdu schema stores](../../reference/concepts/storage-and-retention.md).
Amended by [0029](0029-storage-maintenance.md): the prune is an optional area of the Maintenance page, off until an admin
sets a limit, rather than an admin route.

## Context

The attempt table is append-only and record-grained: one row per delivery try, which on a quiet estate is a
few rows per record per year and on a noisy one is bounded by the retry budget per submission. Getting
retention wrong turns the ledger into the new bottleneck.

## Decision

- `Attempt` is indexed on `(DeliveryKey, StartedUtc)` and on `StartedUtc`.
- A prune deletes attempts older than the cut-off while keeping the latest attempt per record, so every record's last
  outcome remains explainable from the ledger alone.
- The record and submission tables are never pruned; they are the state.

## Amended by 0029

The route this decision first proposed, `POST /api/v1/delivery/ledger/prune` with one `olderThanDays` for attempts,
activity logs and assertion runs together, had no caller in the product: an estate had to schedule it itself. The prune
is now the area `osdu.attempts` on the Maintenance page, with the activity logs and the assertion runs as areas of their
own, each with its own limit, preview, cleanup and log, and none applied until an admin sets one. The 90-day weekly
cadence this decision suggested is gone with the route: a limit is a choice of years as readily as of months, since the
delivery statistics and a record's history read further back than an incident does.

## Consequences

- Attempt volume is bounded by the limit, once one is set, and the retry budget.
- Analytical history beyond the limit belongs in the periodic Delta snapshot of the ledger, not in the live
  store.
- If volume still demands it, the next step is partitioning `Attempt` by `StartedUtc` in a migration; the
  prune becomes a partition switch and nothing above the ledger changes.
