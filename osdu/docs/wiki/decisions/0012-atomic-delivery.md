---
id: delivery-wiki-0012-atomic-delivery
title: "Decision 0012: every delivery completes or is undone, and the ledger names every id it minted"
type: decision
summary: "Why a delivery of many OSDU calls is a unit of work whose minted ids the ledger keeps, so an unfinished one can be undone."
keywords:
  - "atomic delivery"
  - "unit of work"
  - "undo"
  - "minted ids"
  - "artifacts"
  - "partial failure"
referenceRefs:
  - delivery-concept-removal-and-reversal
  - delivery-concept-protocols
  - delivery-concept-ledger
related:
  - delivery-wiki-0013-inventory-flows
updated: 2026-10-16
---
# 0012: Every delivery is a unit of work that completes or is undone, and the ledger names every id it minted

Status: proposed. Documented in [protocols](../../reference/concepts/protocols.md) and [removal and reversal](../../reference/concepts/removal-and-reversal.md).

## Context

A delivery of one record can take many calls that each change OSDU: files uploaded and registered, the record written,
bulk data sent through a DDMS session, rows posted, a workflow run triggered. The DDMSs call storage, file and dataset in
turn. OSDU has no transaction across any of them, so when a call fails part way, or its answer is lost after the service
acted, OSDU keeps what the earlier calls wrote. The worker kept a record's completed steps only while the record went
back to `pending`, and dropped them on six other paths (held, failed, new work staged, a superseded completion, a plan
hold, a removal), and with them the only working copy of what the unfinished delivery had created. What was left (a
dataset nothing names, a record with metadata and no bulk data, a session never closed, rows posted twice, a version
written before a refusal) looks complete in OSDU, is named nowhere in the ledger, and is the kind of orphan nobody finds.

## Decision

- A delivery of one record's pending work is a **unit of work**: it begins with the first call that can change OSDU, spans
  every try while the record stays pending, and ends committed or aborted (held, failed, abandoned by newer work, or
  removed).
- Every object a unit creates in OSDU, or sets out to create, is an **artifact** in `osdu.Artifact`, written in the
  transaction that writes the step that made it. A call whose id the service mints, and whose answer can be lost, is
  preceded by an **intent** holding what finds the object without its id. An id a committed unit minted stays in the table
  for good, whatever later happens to the record or its ledger.
- An aborted unit is **undone**: what it created is removed at the route's reversible scope, the version it replaced is
  written back where the route can, and what no call removes is kept with why. The worker undoes at once on held and
  failed, the claim undoes an abandoned unit before newer work goes, and the sweep at the end of every deliver and drain
  run (and the `undo` operation) finishes the rest. A removal and deleting the ledger undo first.
- A record is removed only when storage's `createTime` says the unit created it; an update is put back, never deleted. The
  record goes back before the datasets it names, and what a DDMS made beside it goes before the record.
- Newer work does not go while an earlier unit's undo is unfinished: the try is skipped without charge, and the record is
  held once the undo has used its tries.
- Each route declares what it creates and implements its own undo against the services' contracts and source; a route
  that makes one atomic write declares nothing.

## Consequences

- Every id a delivery minted is in an indexed table keyed by the ledger partition and the OSDU id, so an inventory
  ([0013](0013-inventory-flows.md)) can say which ids OSDU serves that no delivery accounts for.
- A soft delete is reversible by design, so nothing an undo removes is destroyed; what OSDU cannot remove (historian
  points, Seismic Store objects on gc, files behind a soft-deleted dataset, bulk blobs under a logical delete, ETP arrays)
  is kept and listed, never silently left.
- An undo that cannot reach OSDU holds newer work of the record back, which can delay a record's next delivery; the
  alternative was newer work writing ids the late undo would then take back.
- A route's undo is only as good as its contract: a service that acts after its answer is lost and leaves nothing to find
  the object by (a DSPDM row whose key is too long to record) is kept and reported, and the inventory is what finds it.
