---
id: delivery-wiki-0005-ledger-retention
title: "Decision 0005: attempts are pruned by age, keeping the latest per record"
type: decision
summary: "Why the ledger's attempt history is pruned by age while every record keeps its latest attempt."
keywords:
  - "retention"
  - "attempts"
  - "pruning"
  - "ledger size"
  - "history"
referenceRefs:
  - delivery-concept-availability-and-retention
  - delivery-concept-ledger
updated: 2026-10-16
---
# 0005: Attempts are pruned by age, keeping the latest per record

Status: proposed. Documented in [availability and retention](../../reference/concepts/availability-and-retention.md).

## Context

The attempt table is append-only and record-grained: one row per delivery try, which on a quiet estate is a
few rows per record per year and on a noisy one is bounded by the retry budget per submission. Getting
retention wrong turns the ledger into the new bottleneck.

## Decision

- `Attempt` is indexed on `(DeliveryKey, StartedUtc)` and on `StartedUtc`.
- `POST /api/v1/delivery/ledger/prune` (admin scope, `olderThanDays`) deletes attempts older than the cut-off while keeping the
  latest attempt per record, so every record's last outcome remains explainable from the ledger alone.
- The suggested cadence is weekly with a 90-day window, run from the same schedule as the verify pass.
- The record and submission tables are never pruned by this tool; they are the state.

## Consequences

- Attempt volume is bounded by the window and the retry budget.
- Analytical history beyond the window belongs in the periodic Delta snapshot of the ledger, not in the live
  store.
- If volume still demands it, the next step is partitioning `Attempt` by `StartedUtc` in a migration; the
  prune becomes a partition switch and nothing above the ledger changes.
