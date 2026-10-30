---
id: delivery-flow-inventory-selections
title: "Selecting ids by what OSDU holds (operation match): an inventory's ids chosen by conditions on their records, and removed by the selection"
type: flow-reference
summary: "An inventory's match: conditions on its records asked of OSDU's search, the selection it fills, and a removal of what it holds."
keywords:
  - select ids by attribute
  - filter inventory by facility name
  - match operation
  - selection
  - remove by selection
  - InventorySelection
  - conditions
related:
  - delivery-flow-inventory
  - delivery-flow-inventory-removals
  - delivery-concept-explorer
  - delivery-concept-search-terms
  - delivery-table-inventory-selection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Match.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/SearchRequestCounter.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventorySelections.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Selections.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261020222949_InventorySelections.cs
  - osdu/gui/src/features/delivery/inventories/inventorySelection.ts
  - osdu/gui/src/features/delivery/inventories/InventorySelectionParts.tsx
---

# Selecting ids by what OSDU holds (operation match): an inventory's ids chosen by conditions on their records, and removed by the selection

An inventory knows each id by its finding; what the record holds (its name, the well it belongs to, the system it came
from) is OSDU's. To act on part of a finding, say the orphans of one wellbore, an operator selects the ids by what OSDU
holds of them: conditions on the records' properties, matched with the inventory's own ids by the OSDU id. The ids that
hold them are a selection, which the report lists and a removal can take whole.

The conditions are the explorer's ([conditions and properties](../concepts/explorer.md#conditions-and-properties)), at
most 12, all holding together: a property of the inventory's kind, asked the way the platform indexes it (`contains`,
`is`, `is one of`, `starts with`, a range, `has a value`, and their negations), or a column of a source system the flows
read ([search terms](../concepts/search-terms.md)), carried through its mapping before OSDU is asked. A selection is
opened with them (`POST /api/v1/delivery/inventories/{partition}/{inventoryId}/selections`, body `filters`), and filled
by a `match` run of the inventory flow, through the flow's own `source` and credentials, as its build reads.

## How a match reads OSDU

1. A source column reached through other records (the well's name, for a wellbore) is read first, as the explorer reads
   it, into a condition on the inventory's own records; every condition is then one clause of the query.
2. The records the conditions find in the inventory's kind, narrowed by its `query`, are counted: one request.
3. They are read the way that sends fewer searches:
   - **by conditions** (`conditions`): every record they find, 1,000 a page, paged as the flow's `source.paging` says;
     this is the way nearly always, since the conditions find no more records than the kind holds; or
   - **by ids** (`ids`): the ids the inventory serves, 500 to a search (fewer when the conditions compare many values,
     since the search takes 1,024 clauses in a query), each search asking the conditions of those ids alone. A kind that
     grew far past the inventory since its last build is read this way, so the match costs a search per 500 of the
     inventory's ids however many records of the kind the conditions find.
4. Each record found that the inventory serves a row of is kept, matched by its OSDU id, with the version the search index
   holds and what it holds at each property the conditions ask (the first 20 values of each, cut to 4,000 characters in
   all). A record the inventory serves no row of (written since its last build, or outside its query) is counted as
   outside the inventory, and not kept.

Only ids the inventory serves can be selected: OSDU's search does not serve a missing, unlisted or gone id, so no
condition reaches one. For an inventory read through storage, a record the search index does not hold is never selected
either. An id whose indexed version is not the one the inventory listed changed since its last build; the match counts
it, and a removal skips it.

A match keeps each page as it reads it, so the page following it shows how far it got. One that stops (the search refusing
or failing, the run cancelled, its process stopped) keeps no id, and its selection says why; a run tried again starts its
match afresh. A match is a run of the flow, so one queued while a build or a removal of the flow runs waits for it.

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation match --payload '{"inventories":["Wellbores"],"selection":41}'
```

The run fills a selection the control plane opened; the page and the API open them, and a run naming no selection, or one
of another inventory, is refused.

## What a selection says

| Status | Meaning |
| --- | --- |
| `queued` | Opened, its match queued as a run of the flow. |
| `running` | Reading OSDU: how much it has to read (`toCheck`, the records the conditions find or the inventory's ids) and has read (`checked`). |
| `completed` | Read OSDU whole: it holds the inventory's ids that hold what the conditions ask (`matched`), with the records found outside the inventory (`outside`), the ids at another version than the inventory listed (`changed`), and the search requests it sent. |
| `failed` | Stopped, holding no id; `error` says why. A selection whose run ended without finishing it, or was never queued, is closed so when it is read. |
| `expired` | Older than a week and kept for the removal that took its ids: its conditions and counts stay, its ids were retired. |

A completed selection made against an earlier build of the inventory is `stale`: its ids are still listed, and a removal
refuses to take them until the conditions are matched again. The same conditions, however they are spelled, asked again of
the same build answer with the match already made of them (200); `refresh` reads OSDU again (202). Each id of a selection
is counted under the finding the inventory gives it now, so its counts follow a reconcile or a removal.

A selection is kept a week. The next selection opened in the partition (by conditions, or of a duplicate check's picks)
retires those older: deleted whole, or, when a removal took
their ids, kept `expired` with their ids deleted, so the removal still says what it removed by.

## Removing what a selection holds

A removal names a `selection` in place of `ids`: every id of the finding the selection holds, as many as `expected`,
however many that is. Only `orphan`, `stale` and `forgotten` ids, which go from OSDU, are taken from a selection; a removal
names ids or a selection, not both. It is refused unless the selection is one of the inventory's, its match completed and
was made against the inventory's last build, and it holds exactly `expected` ids of the finding. Every id is then checked
again just before it goes, as any removal checks it ([What a removal does](inventory-removals.md#what-a-removal-does)). The removal keeps the
selection it took its ids from, and the **Removals** tab says which conditions chose them.

```json
{
  "inventories": ["Wellbores"],
  "removal": { "finding": "orphan", "scope": "record", "expected": 212, "selection": 41 },
  "confirm": "dev"
}
```

## In the GUI

**Filter**, at the end of the Ids tab's line, opens the explorer's editor on the inventory's kind: its properties, the
columns of the source systems its flows read, and the values the kind's records hold, the commonest first. Each condition
is a chip over the grid, changed by a click and dropped by its cross, and the conditions live in the page's address (`f`),
so a link opens the same selection. At the end of the chips' row the match says where it stands: asking, matching with how
far it got and its run, matched (when, and behind the mark what it found, the query and how it read OSDU), built again
since the match, or failed with why. While it holds, every tab counts its matching ids before its own (`212 / 60,000`),
the grid lists only them, with a column for each property the conditions ask, and **Select all** picks every matching id
of a removable finding for one removal by the selection. The CSV export adds the matching ids, with their values.

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Inventory 'Wellbores' has not been built in 'dev', so it holds no id to match; build it first. Nothing was matched.` | `match` before a build; the selection is closed with it. |
| `Inventory 'Wellbores' in 'dev' holds no selection 41: it was opened for another inventory, or retired since. Nothing was matched.` | `match` naming a selection the inventory does not hold. |
| `Selection 41 of inventory 'Wellbores' holds 209 orphan id(s) now, and the removal was asked for 212: what it holds changed since it was shown. Nothing was removed; look at it again and ask again.` | The selection's ids changed finding since the operator looked. |
| `Selection 41 was matched against an earlier build of inventory 'Wellbores', and the inventory was built again since: match its conditions again before removing what it holds. Nothing was removed.` | A removal by a stale selection. |
| `Selection 41 of inventory 'Wellbores' is running, not completed: only a match that read OSDU whole says which ids hold what its conditions ask. Nothing was removed.` | A removal by a selection whose match did not complete. |
| `Selection 33 of 'dev' holds the copies duplicate check 12 picked to remove; a match fills only a selection opened with conditions. Nothing was matched.` | `match` naming a selection a duplicate check made. |
