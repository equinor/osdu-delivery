---
id: delivery-wiki-0014-inventory-removals
title: "Decision 0014: an inventory flow may remove the orphan, stale and forgotten ids it found, and restore its gone ids"
type: decision
summary: "Why an inventory flow may remove what it found when its document allows it, and how that stays traceable."
keywords:
  - "inventory"
  - "removal"
  - "orphans"
  - "cleanup"
  - "soft delete"
  - "restore"
referenceRefs:
  - delivery-flow-inventory
  - delivery-flow-inventory-removals
  - delivery-flow-inventory-selections
  - delivery-concept-removal-and-reversal
related:
  - delivery-wiki-0013-inventory-flows
  - delivery-wiki-0022-removal-flows
  - delivery-wiki-0027-inventory-duplicate-checks
updated: 2026-11-02
---
# 0014: An inventory flow may remove the orphan, stale and forgotten ids it found, when its document allows it

Status: proposed. Documented in [inventory removals](../../reference/flow/inventory-removals.md#what-a-removal-does). Amends
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

## Addendum (2026-11-02): a soft-deleted gone id is restored in OSDU

The gone tab's one **Remove** read as a removal from OSDU, and a gone id a removal soft deleted could not be brought back
from the inventory, though storage keeps it with every version and undeletes it with `PATCH /records/{id}` and
`{"deleted": false}` (osdu/specs/core/INTEGRATION.md, D3). So the gone tab now offers **Restore in OSDU** and **Remove from
inventory**, each saying where it acts, a removable finding's tab **Remove from OSDU**, and a soft-delete removal
**Restore what it removed**.

- A restore is a run of the flow of its own (operation `restore`), so its runs read as restores; it is kept with the
  removals (`osdu.InventoryRemoval`, scope `restore`, a `Restored` tally, and `RestoresRemovalId` for one that undoes a
  removal) rather than in tables of its own, since it is the same thing kept the same way: an operator's change to an
  inventory's ids, a run of the flow, checked id by id and recorded id by id. A lookup by OSDU id then answers removals and
  restores in one list.
- Only a flow that declares `removal` restores: a restore writes to OSDU as a removal does, through the same credentials,
  and a flow without it only reads OSDU.
- An id a ledger knows (a record claims it, a delivery recorded it, or its record was purged from a ledger) is left: what
  a ledger holds comes back through its own flow, which records it, so no restore writes past a ledger. A gone id picked
  on the tab is restored only when an identity this estate writes as created it, as an orphan's removal checks; a removal's
  own ids were checked so when it removed them.
- What one removal removed is restored from the removal's items, whether or not the inventory still holds a row of them,
  so removing gone ids from the inventory does not lose the way back.
- The restored rows are served again in the inventory at once with the finding the reconcile's rule gives them, as a
  removal marks its ids gone at once; storage publishes the undelete to be indexed, so a later build lists them.
