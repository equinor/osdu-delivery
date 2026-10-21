---
id: delivery-wiki-0013-inventory-flows
title: "Decision 0013: inventory flows compare what OSDU serves with every ledger of the partition"
type: decision
summary: "Why finding orphans needs a read of what OSDU serves set against every ledger, and what an inventory compares."
keywords:
  - "inventory"
  - "orphans"
  - "stale ids"
  - "forgotten ids"
  - "ledger drift"
referenceRefs:
  - delivery-flow-inventory
  - delivery-guide-finding-orphans
related:
  - delivery-wiki-0012-atomic-delivery
  - delivery-wiki-0014-inventory-removals
updated: 2026-10-16
---
# 0013: Inventory flows read what OSDU serves and compare it with every ledger of the partition

Status: proposed. Documented in [inventory flow](../../reference/flow/inventory.md).

## Context

A ledger can come out of step with OSDU for reasons no delivery controls: a service that acted after its answer was lost,
an ingestion workflow that wrote more than it reported, a record written outside the flows, a ledger deleted or a record
purged from it, a restore of OSDU or of the database. None of these is visible from the ledger alone, and an undo
([0012](0012-atomic-delivery.md)) can only take back what the ledger names. Finding the rest needs what OSDU actually
serves, set against everything the ledgers say they delivered, minted, removed or forgot.

## Decision

- An inventory flow (`flowType: inventory`) is a flow kind of its own, holding inventories: each a kind (wildcards
  allowed), optionally narrowed by a search query, and whether it keeps every version. It only reads OSDU, as assertion
  and dimension flows do.
- A build reads every id of the kind whole, through the search index (a viewer's entitlements suffice) or through storage
  itself (every active record, unindexed ones included, which needs storage's admin role), with the system properties
  that say who created and last changed it. A read that fails part way is never merged: an id not in a complete read is
  marked gone, so a partial read would mark live records gone.
- Every id is compared with every ledger of the partition, read and never written: the records' claimed ids, the
  artifacts deliveries recorded, and the records purged from a ledger. Each id gets one finding (orphan, missing, undoing,
  forgotten, stale, unconfirmed, drifted, unlisted, foreign, superseded, tracked, gone).
- An id no ledger knows is an orphan when an identity this estate writes as created it, else foreign. The identities are
  declared, or inferred from the creators of the ids a ledger claims, and the run says which and how.
- The ids a ledger expects that the read did not list are read by id from storage, within a bound per build, to tell
  missing (storage does not hold it) from unlisted (the index has not caught up, or the read was narrowed).
- The inventories live in the module's database, keyed by the ledger partition, merged in one transaction under a lock
  per inventory; counts are read from the rows.

## Consequences

- The orphan report is a query over the module's database, by finding, by OSDU id across inventories, or by the ledger
  that claims an id, at production volume.
- A search read sees the index as it stands: a record written seconds before a build, or one the index failed to index,
  is not listed, and is reported unlisted rather than missing when a ledger expects it. A storage read sees everything,
  at the cost of the admin role and a request per thousand ids.
- An inventory narrowed by a query, or a concrete kind version, reports as missing only the ids it listed before; only an
  inventory that covers its entity type whole can say a ledger's record of the type is missing.
- Nothing is written to OSDU by a build, a reconcile or a plan: acting on a finding is an operator's decision. A missing,
  drifted or unconfirmed record is acted on through its ledger's own actions; the ids no ledger holds live (orphan, stale,
  forgotten) are removed by the inventory flow itself, when its document allows it ([0014](0014-inventory-removals.md)).
