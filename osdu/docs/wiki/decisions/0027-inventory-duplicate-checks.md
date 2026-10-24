---
id: delivery-wiki-0027-inventory-duplicate-checks
title: "Decision 0027: an inventory finds the records OSDU holds more than once, keeps the most complete copy, and removes the rest through a selection"
type: decision
summary: "Why duplicates are found by downloading a key's properties and grouping them in SQL, why the fullest copy is kept, and how removal stays safe."
keywords:
  - "duplicate records"
  - "duplicate check"
  - "inventory"
  - "most complete copy"
  - "equal values"
  - "download and group in SQL"
  - "selection"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateKey.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateReferences.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicateTable.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RunTimeTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/Evaluators.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/gui/src/features/delivery/inventories/duplicates/InventoryDuplicates.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateKeyParts.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateKeyBuilder.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/duplicateDocument.ts
referenceRefs:
  - delivery-flow-inventory
  - delivery-flow-inventory-duplicates
  - delivery-flow-dimension
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

The first version read every id's key through the search, then each group's copies from storage, and counted the records
naming each copy with a search per copy over every kind. Against a real partition that search per copy was answered 429
(too many requests) at once, and the human pointed out the deeper fault: a poorly chosen key makes thousands of copies,
and the work grew with them. They asked for the inventory's ids to be extended with the key's properties and the
duplicates found on the SQL side, with the schema evolution the module already uses for dimensions, so that the analysis
of what was downloaded is flexible and live reads of OSDU come after it, for the groups an operator settles on.

## Decision

- **A check of an inventory, by a key of equal values.** An operator names a key, one to 100 properties of the record
  (paths from its root). Two records are copies when they hold equal values at every property: text exactly as written, a
  number as a number however it is written, a list as its values in any order. The human asked for equality alone; a
  key's properties carry no comparison of their own.
- **Download once, group in SQL.** The check downloads the key's properties of every record of the inventory's kind
  through the search (a request per 1,000 records), returning only those properties, into a table of its own in the
  module's schema, `dup_<check>`, made and widened at run time through SQLFlow's schema evolution as a dimension's table is
  ([dimension flows](../../reference/flow/dimension.md)): a row for each id the inventory serves, and for each property its
  values, readable, and their hash. The groups are formed in SQL from the hashes. A download costs the same whatever the
  key finds.
- **Any key of what was downloaded groups again at once.** Adding a downloaded property to the key, or taking one out,
  groups the table again without a run and without reading OSDU; a new property downloads again, carrying the properties
  downloaded before, so the operator can move between keys freely. Each copy keeps its values at the other downloaded
  properties beside the key's, so a group shows how its copies differ outside the key. The groups' sizes are shown, and a
  group of more than 100 copies is left for review unread: a key that broad is not one record written more than once.
- **Live reads only for the groups an operator settles on.** Reading the copies whole from storage (a request per 100) and
  counting the records naming them is a separate pick the operator asks for, its cost said first. The records naming
  copies are counted 40 copies to a search: one query names the whole batch and leaves the batch's own records out, so a
  batch no other record names, which is most, answers in one request; a batch that finds a record is split until each
  named copy is counted on its own. The searches run one after another, since a search over every kind is what OSDU limits
  hardest. A pick that stops keeps what it read and is asked again for the rest.
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
  the group with who made it and when, and can be taken back. Grouping again lets go of picks and choices, and asks first.
- **The key is picked in a record.** One of the inventory's records is shown as its JSON beside the key, and a click
  on a value adds its property: the property a reader sees holding what the copies of that record would repeat. A value
  inside the items of a list adds its property across every item, as the check reads it. A property the check in view did
  not download is marked, since a key holding it downloads again.
- **Removal goes through the selection the inventory already has.** The copies picked to remove are made a selection
  (with the copy each group keeps beside each id, and the key the groups were formed by), and the inventory's existing
  removal by selection takes it: one finding at a time, the count the operator was shown, the partition typed back,
  against the build the check read, each id checked again before it goes. A copy goes only while storage holds the copy
  its group keeps, read in the same pass, so no removal takes the last copy of a record.

## Rejected

- **Reading OSDU per copy.** The first version's search per copy for the records naming it, and its reading of copies for
  every group a key found, made the cost grow with what a key found: a poor key cost thousands of requests and the
  partition answered 429. Downloading the key once and reading copies only on a pick bounds what an exploration costs.
- **Comparisons per property** (exact, ignoring case, loose). The first version offered them; the human asked for equal
  values alone. Spellings that differ are seen in the grid, beside the copies, where a property outside the key shows
  each copy's value.
- **A live check in the explorer.** The explorer answers within a request and keeps nothing, pages only the first 10,000
  records of a query, and its Group by is OSDU's `aggregateBy`, one field whose groups the service caps. Finding every copy
  of a kind needs every record read and kept, and each id's finding, which the inventory has.
- **Picking the key from a list of the kind's properties.** The first key editor did so. The human turned it down: an
  OSDU record holds hundreds of properties, and a list of names shows none of their values. The list stays behind
  **By name**, for a property the record shown holds no value at.
- **The assertion flow's `unique`.** It holds a test's records to no two sharing the values of some paths, in memory, and
  names a few examples: a pass or a fail, not a list to act on.
- **Reading each copy's bulk data from the DDMS.** The attribute count already tells a copy with bulk data from one
  without, since the DDMS writes its link onto the record.
- **Keeping the documents.** An inventory keeps ids and system properties. The check downloads only the properties its
  keys name, and the comparison of two copies reads them live.
- **Removing without an operator.** A check only proposes: the copies picked are selected and removed by an operator
  through the removal every other inventory finding goes through, with its counts, checks and audit trail.

## Consequences

- A download of an inventory of N ids costs about N/1,000 searches, whatever the key finds; grouping again costs nothing
  in OSDU. A pick of C copies costs about C/100 storage reads and C/40 searches, more where copies are named.
- The search returns only what its index keeps of a record: a property no record holds a value at is named in the
  check's notes, and ids the index does not hold are not checked.
- Each download keeps a table in the module's database until a later download of the inventory completes, the download
  fails, or the sweep retires the check a week on; the module's user needs `CREATE TABLE` and `ALTER` on the schema. It is
  the second kind of table no migration makes, beside a dimension's, and the project's instructions name both.
- A check is a snapshot of the inventory's build: a later build makes it, and a selection made of it, stale, and both
  are refused for removal until the key is downloaded again.
- A removal by a check's selection removes only the findings the flow's `removal.findings` allows, as any removal does;
  a foreign copy stays, and its group says so.
