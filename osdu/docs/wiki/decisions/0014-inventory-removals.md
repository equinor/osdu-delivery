---
id: delivery-wiki-0014-inventory-removals
title: "Decision 0014: an inventory flow may remove the orphan, stale and forgotten ids it found"
type: decision
summary: "Why an inventory flow may remove what it found when its document allows it, and how that stays traceable."
keywords:
  - "inventory"
  - "removal"
  - "orphans"
  - "cleanup"
  - "soft delete"
referenceRefs:
  - delivery-flow-inventory
  - delivery-concept-removal-and-reversal
related:
  - delivery-wiki-0013-inventory-flows
  - delivery-wiki-0022-removal-flows
updated: 2026-10-23
---
# 0014: An inventory flow may remove the orphan, stale and forgotten ids it found, when its document allows it

Status: proposed. Documented in [inventory flow](../../reference/flow/inventory.md) (What a removal does). Amends
[0013](0013-inventory-flows.md), whose inventories only read OSDU.

## Context

An inventory finds what OSDU serves that no ledger holds live: orphans (no ledger knows them, and the estate created them),
stale ids (a ledger marks their record removed, or an undo its id) and forgotten ids (their record was purged from its
ledger). 0013 left acting on them to "the ledger's own actions", but an orphan has no ledger record, and a forgotten id's
record is gone from its ledger, so there is no action of a ledger to take. A partition can hold thousands or millions of
them (a migration written outside the flows, an ingestion workflow that wrote more than it reported), so removing them one
record page at a time, or through a removal capped at 25,000 records, is no answer.

## Decision

- An inventory flow may declare `removal: { findings: [orphan, stale, forgotten], purge: false }`. Without it the flow
  only reads OSDU, as before. The power to delete is so a line of the flow's document, reviewed where the document is.
- A removal is a run of the inventory flow (operation `remove`) an operator asks for from the inventory's page or the
  API: the ids of one finding of one inventory, those picked (at most 1,000) or every one, however many. It goes through
  the flow's own `source` and credentials: a soft delete (`POST /records/delete`, 500 ids a request, which OSDU can revert)
  or, only where the flow allows `purge`, a purge (`DELETE /records/{id}`, one id at a time, every version destroyed).
- Nothing is queued unless the request names the partition the inventory is kept in and the inventory holds as many ids
  of the finding as the operator was shown (or every id they picked, with that finding), and no run of the flow is queued
  or running. The run checks all of it again on the node before it removes anything.
- Every id is checked again just before it goes, a chunk of 500 at a time, by the rule the reconcile sets findings by: the
  inventory still finds it so, the ledgers of the partition give it that finding now, storage still holds it at the version
  the inventory listed, and an orphan's creator is still an owner. An id that fails a check is left in OSDU and skipped
  with why.
- Every id the removal reached is kept with its outcome (removed, already gone, skipped, failed), the version it found and
  why, in `osdu.InventoryRemovalItem`, under a removal row (`osdu.InventoryRemoval`) that names the run, who asked, the
  finding, how much it removed, the count shown, and its tallies. The ids removed are marked gone in the inventory at once.
  A stale record's own ledger records the removal on the record (an attempt naming who asked), and the removal is an
  activity of the audit trail (`inventory-remove`). A lookup by OSDU id answers what removals did to the id.
- A chunk is recorded before the next is read, so a removal stopped part way keeps what it did, and the inventory then
  holds what is left for the next one. OSDU refusing a whole chunk for want of permission stops the removal there, rather
  than asking again for every id after it.

## Consequences

- The flow's source credentials must be an owner of the records it removes (`users.datalake.editors`, and in each record's
  owners ACL): a viewer's credentials, enough for a search read, are refused by storage, and the removal stops at its
  first chunk saying so.
- A removal never reaches an id a ledger holds live: a delivered, unconfirmed or drifted record, or a live or undoing
  artifact, is acted on through its ledger. Foreign ids (created by another identity) are never removable.
- A soft delete can be reverted in OSDU; a purge cannot, and the flow has to allow it in its document.
- A removal of millions of ids is about two thousand delete requests and as many header reads, and keeps a row per id.

## Addendum (2026-10-14): gone ids are removed from the inventory

A gone id (one OSDU stopped serving) stayed in the inventory for good, so an inventory of a partition that was cleaned
up kept far more gone rows than live ones. An operator may now remove gone ids from the inventory: a removal with
`finding: gone` and `scope: inventory`, the one scope of `gone` and of nothing else, since an id OSDU serves would be
listed again by the next build. It asks nothing of OSDU, so it needs no `removal` in the flow's document and does not use
the flow's credentials; the document cannot name `gone`. It is asked for, checked and audited as any removal is. An id a
ledger expects stays (a later build asks storage whether it is missing), as does one OSDU serves again. The removal keeps
its tallies and the ids it kept, not a row per id it removed, which would keep what it removes; no ledger changes.
