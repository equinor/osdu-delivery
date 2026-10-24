---
id: delivery-wiki-0027-inventory-duplicate-checks
title: "Decision 0027: an inventory finds the records OSDU holds more than once, keeps the most complete copy, and removes the rest through a selection"
type: decision
summary: "Why duplicate records are found on an inventory by an operator's key, why the fullest copy is kept, and how removing the others stays safe."
keywords:
  - "duplicate records"
  - "duplicate check"
  - "inventory"
  - "most complete copy"
  - "attribute count"
  - "selection"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateKey.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/Evaluators.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/gui/src/features/delivery/inventories/duplicates/InventoryDuplicates.tsx
referenceRefs:
  - delivery-flow-inventory
  - delivery-flow-inventory-duplicates
  - delivery-concept-explorer
  - delivery-flow-assertion
  - delivery-table-inventory-duplicate-check
related:
  - delivery-wiki-0013-inventory-flows
  - delivery-wiki-0014-inventory-removals
  - delivery-wiki-0002-client-supplied-ids
updated: 2026-10-24
---
# 0027: An inventory finds the records OSDU holds more than once, keeps the most complete copy, and removes the rest through a selection

Status: proposed. Documented in [Finding duplicate records](../../reference/flow/inventory-duplicates.md). Builds on
[0013](0013-inventory-flows.md) and [0014](0014-inventory-removals.md).

## Context

OSDU can hold one record more than once. The ids of a record written under ids OSDU assigned are not computed from its
key, which is where the duplicate defects of the earlier design came from ([0002](0002-client-supplied-ids.md)); a load
run twice, another tool writing the same well log, or a delivery that changed how it makes ids leaves copies too. OSDU
says nothing of it: each copy is a record of its own. The human asked for a way to find them by any part of the document,
attached to one type of record, and to end with a set of ids that can be deleted, deleting the least complete copy, since
one copy may carry its bulk data in a DDMS where another does not. They asked for it to be production grade, its interface
built with care, and for as many properties in the key as are needed.

## Decision

- **A check of an inventory, by a key.** An operator names a key, one to 100 properties of the record (paths from its
  root), each compared exactly, ignoring case, or loose (case, spacing and empty text ignored, text that is a number read
  as the number). Two records are copies when they are alike at every property; a list compares as its values in any
  order. The check is a run of the inventory flow (operation `duplicates`), through the flow's own source and
  credentials, as a match is; it reads and writes the module's database, never OSDU.
- **Read the key cheaply, the copies whole.** The key of every record of the inventory's kind is read through the search,
  returning only the key's properties (a request per 1,000 records); the ids that share a key are grouped in SQL; then
  only the copies are read from storage (a request per 100) and the records naming each are counted through the search.
  A group of more than 100 copies is a key too broad to be one record written more than once, left for review unread.
- **The most complete copy is kept.** Completeness is the count of values a copy holds under `data`, each item of a list
  counted. The Wellbore DDMS writes the link to a record's bulk data onto the record itself
  (`data.ExtensionProperties.wdms.bulkURI`, and its entry in `data.DDMSDatasets`), and a record names its files under
  `data.Datasets`, so the count tells a copy with bulk data from one without, without reading a DDMS.
- **A ledger's copy is never picked to remove.** A copy a ledger holds live is the one a delivery writes; removing it
  through the inventory would only see it written again, and would bypass the ledger. It is kept when no copy holds more
  values; when one does, or two copies are a ledger's, the group is left for review, since the delivery or the key is what
  is wrong. A copy other records name is never picked to remove either: the group is left for review.
- **What the rules cannot decide, an operator does.** A tie, the cases above, and a copy storage did not answer for leave
  the group for review, and nothing of it is removed until an operator chooses the copy to keep. A choice is recorded on
  the group with who made it and when, and can be taken back.
- **Removal goes through the selection the inventory already has.** The copies picked to remove are made a selection
  (with the copy each group keeps beside each id), and the inventory's existing removal by selection takes it: one finding
  at a time, the count the operator was shown, the partition typed back, against the build the check read, each id checked
  again before it goes. The removal adds one check for such a selection: a copy goes only while storage holds the copy its
  group keeps, read in the same pass, so no removal takes the last copy of a record.

## Rejected

- **A live check in the explorer.** The explorer's property list is what the human liked, and the check's key editor
  reuses it; but the explorer answers within a request and keeps nothing, pages only the first 10,000 records of a query,
  and its Group by is OSDU's `aggregateBy`, one field whose groups the service caps. Finding every copy of a kind needs
  every record read, a run that keeps what it found, and each id's finding, which the inventory has and the explorer, which
  shows only what OSDU holds, does not.
- **The assertion flow's `unique`.** It holds a test's records to no two sharing the values of some paths, in memory, and
  names a few examples: a pass or a fail of one kind in one version, not a list to act on.
- **Reading each copy's bulk data from the DDMS.** The human pointed out that the attribute count already tells a copy with
  bulk data from one without, since the DDMS writes its link onto the record; a describe call per copy would add a DDMS
  dependency and a request per copy for what the count says.
- **Keeping the documents.** An inventory keeps ids and system properties, not documents. Reading every document at each
  build would cost a request per 100 records for every record, not only for copies; the check keeps the counts it needs
  and the comparison of two copies reads them live.
- **Fuzzy matching** (near names, coordinates within a distance). It would need a different index and different
  guarantees; a key compared loose covers the spellings copies written by different hands most often differ in.
- **Removing without an operator.** A check only proposes: the copies picked are selected and removed by an operator
  through the removal every other inventory finding goes through, with its counts, checks and audit trail.

## Consequences

- A check of an inventory of N ids costs about N/1,000 searches for the keys, then, for C copies in groups, C/100 storage
  reads and C searches for the records that name them, run as many at once as the flow's `parallelism.recordsAtOnce`.
- The search returns only what its index keeps of a record: a key property no record holds a value at is named in the
  check's notes, and ids the index does not hold are not checked.
- A check is a snapshot of the inventory's build: a later build makes it, and a selection made of it, stale, and both
  are refused for removal until the key is checked again.
- A removal by a check's selection removes only the findings the flow's `removal.findings` allows, as any removal does;
  a foreign copy stays, and its group says so.
