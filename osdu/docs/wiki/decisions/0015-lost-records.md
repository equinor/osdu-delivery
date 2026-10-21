---
id: delivery-wiki-0015-lost-records
title: "Decision 0015: a record OSDU lost is delivered again under its own id, and under a new one only when an operator asks"
type: decision
summary: "Why a record OSDU lost is delivered again under the id it claimed, restored first when soft-deleted, and given a new id only on request."
keywords:
  - "missing records"
  - "lost records"
  - "accidental delete"
  - "restore soft delete"
  - "undelete"
  - "redeliver"
  - "new osdu id"
  - "reassign id"
  - "repair"
referenceRefs:
  - delivery-flow-inventory
  - delivery-concept-removal-and-reversal
related:
  - delivery-wiki-0002-client-supplied-ids
  - delivery-wiki-0004-preserved-keys
  - delivery-wiki-0013-inventory-flows
updated: 2026-10-16
---
# 0015: A record OSDU lost is delivered again under its own id, and under a new one only when an operator asks

Status: proposed. Reference: [inventory flow, Repairing missing ids](../../reference/flow/inventory.md#repairing-missing-ids). Amends
[0002](0002-client-supplied-ids.md), whose records keep the id they first claimed.

## Context

A cleanup in OSDU, or an accident, can soft delete or purge records a ledger holds as delivered. An inventory finds them
`missing` and a verify finds them `missing`, but nothing brought them back: a plain run passes them over, since the ledger
says OSDU holds them, and a redelivery wrote them under their id with the keys OSDU keeps of a record (decision 0004)
lost, since the read that copies them answers 404 for a record OSDU no longer serves. Operators asked for a way to bring
them back, and to give a record a new id where its old one must not be used again.

OSDU storage brings a soft-deleted record back with a merge patch of `{"deleted": false}` (`PATCH /records/{id}`), at the
version it had and with no new version, answers 404 for an id it holds no record of, and 400 "Record State already
updated" for a record already active (storage source, `RecordServiceImpl.patchRecord`). A write of an id it holds
soft-deleted makes it active again as a new version, and a write of a purged id creates it (osdu/specs/core/INTEGRATION.md
section 5.4).

## Decision

- A record a ledger delivered that OSDU no longer holds is delivered again through its own flow, under the id it claimed:
  the id is made from the record's key, other records name it by that id, and OSDU takes it back whether it soft-deleted
  or purged the record. An inventory's **Deliver again** on its missing ids, a verify with `reconcile`, and a redelivery
  that says OSDU lost the record (`RedeliverSelection.Lost`) all mark it the same way: the record's verify outcome is
  `missing` and it is asked to be planned again.
- The storage route brings a record so marked back from its soft delete before it writes it (step `restore`), so the
  keys it keeps of the record are read from it; a purged one is written again without being read. The mark goes with the
  first write that lands. Other routes write the record again under its id as they write any record.
- A new id is given only when an operator asks for it, with the partition typed back: the record claims an id made from
  its key and the id it replaces, marked assigned (`osdu.Record.TargetIdAssignedUtc`, migration
  `RecordAssignedTargetId`, module 1.45.0), and from then on the planner writes it under that id whatever its mapping
  makes from its key. Its history keeps both ids (phase `reassign-id`), and it is found by its new id.
- Only a record the ledger holds as delivered is reached. A dataset a delivery minted, or a record held, failed, removed or
  reverted since the inventory looked, is left and the answer says why.

## Consequences

- Records that name a record's old id keep naming it after it is given a new one: a new id is for good, and for an id
  that must not be used again.
- The restore step needs the flow's credentials to be an owner of the record (403 otherwise, which fails the record and
  writes nothing).
- A reversal of a run that wrote the old id passes the record over, since the version it would put back is not the
  record's any more; a step back to the previous version after the first write under the new id finds that write created
  the record.
