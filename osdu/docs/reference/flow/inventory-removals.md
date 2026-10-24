---
id: delivery-flow-inventory-removals
title: "Inventory removals (operation remove): taking what an inventory found out of OSDU, repairing missing ids, and removing gone ids"
type: flow-reference
summary: "An inventory's remove run: what it checks and records id by id, delivering missing ids again, and removing gone ids from it."
keywords:
  - removal
  - soft delete
  - purge
  - remove gone ids
  - obsolete inventory rows
  - repair missing records
  - deliver again
  - restore soft deleted record
  - new osdu id
related:
  - delivery-flow-inventory
  - delivery-flow-inventory-selections
  - delivery-flow-inventory-duplicates
  - delivery-concept-removal-and-reversal
  - delivery-guide-finding-orphans
  - delivery-table-inventory-removal
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRemovals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRepairs.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261008132311_InventoryRemovals.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
---

# Inventory removals (operation remove): taking what an inventory found out of OSDU, repairing missing ids, and removing gone ids

An [inventory flow](inventory.md) that declares `removal` lets an operator take out of OSDU the orphan, stale and forgotten
ids its inventories found, every one of a finding, those picked, or those a [selection](inventory-selections.md) holds. The
missing ids are delivered again through the flows that delivered them, and the gone ids, which OSDU no longer serves, are
removed from the inventory alone, whatever the flow declares. Each is asked for from the inventory's page or the API, and
each is a run of the flow or an activity of its ledger, recorded id by id.

## What a removal does

A removal is a `remove` run, queued from the inventory's page in the GUI (`POST
/api/v1/delivery/inventories/{partition}/{inventoryId}/removals`) or run with `sqlflow run --operation remove`. Before it
reads anything it is refused unless the flow allows the finding and the scope, `confirm` names the run's partition, the
inventory has been reconciled, and the inventory holds as many ids of the finding as `expected` (or a row of every id
named; or, for a removal by a selection, the selection holds as many, matched against the inventory's last build). The page
and the API also refuse while a run of the flow is queued or running.

It then reads the ids 500 at a time, in order. Each id is checked again just before it goes, and left in OSDU, skipped
with why, when the inventory no longer finds it so, the ledgers give it another finding now, storage serves another
version than the inventory listed, (an orphan) storage says no owner created it, or (a copy a duplicate check picked)
storage no longer holds the copy its group keeps, read in the same pass; an id storage no longer holds is `gone`. The
rest are removed through the flow's `source`: a soft delete 500 ids a request, or a purge one id at a time. A
soft delete answered 207 has deleted every id it does not name, and only the ids it names are sent again one at a time, so
each has its own outcome: one storage no longer holds is `gone`, one it refuses is `failed`. Storage refuses a record with
403 when the flow's credentials are no owner of it (its `acl.owners`), so such a record fails every removal until its ACL
names them or one of its owners removes it, and its outcome says so. A 207 that names no id it was sent leaves every id of
the chunk `failed` with that answer, since asking again could not tell an id it deleted from one that was gone before.

Every chunk is recorded before the next is read: each id's outcome (`removed`, `gone`, `skipped`, `failed`) with its
version and why, the inventory's rows marked gone at once, and, for a stale record, its own ledger's removal (an attempt
naming who asked). A removal stopped part way keeps what it did; when its process stopped before it finished, the next
removal of the inventory closes it as failed (`the removal ended without finishing: its process stopped before it did;
what it recorded stands`) before it starts. OSDU refusing every id of a chunk with 401 or 403 stops
the removal there: `OSDU refused the removal (403) of every id of a chunk: the flow's source credentials must be an owner
of the records (users.datalake.editors, and in each record's owners ACL). Nothing more was asked of it.` The removal is
an `inventory-remove` activity of the audit trail.

## Repairing missing ids

A `missing` id is a record a ledger delivered that OSDU no longer holds: a cleanup or an accident soft deleted or purged
it. On the **missing** tab, pick ids or **Select all**, and **Deliver again**: each record is delivered again through the
flow that delivered it, and a deliver run of each such flow is queued
([decision 0015](../../wiki/decisions/0015-lost-records.md)).

- **Under their own ids** (the default). Each record is marked as OSDU having lost it and asked to be planned again, as
  a verify with `reconcile` marks one. The storage route brings a soft-deleted record back before writing it (`PATCH
  /records/{id}` with `{"deleted": false}`), so the keys the write keeps of it (`preserveDataKeys`) are kept, and writes a
  purged one again under the same id. Records that name the id are whole again once the run lands.
- **Under new OSDU ids**, with the partition typed back. Each record claims a new id in the same partition and entity
  type, made from its key and the id it replaces, and is written under it from then on whatever its mapping makes from its
  key. Its history keeps both ids, and it is found by the new one. Records that name the old id keep naming it: a new id
  is for the case where the old one must not be used again.

Only a record its ledger holds as delivered is reached. A dataset a delivery minted, a record held, failed, removed or
reverted since the reconcile, and one whose flow is no longer in the catalog are left, and the answer says why. The API is
`POST /api/v1/delivery/inventories/{partition}/{inventoryId}/repairs` with `expected`, `ids` (at most 1,000), `newIds`
and `confirm`; the repair is an `inventory-repair` activity of the inventory flow, and each flow's marking an activity of
its own naming every record. No `removal` is needed in the inventory flow's document: the records are written by their
own flows, with their own credentials.

## Removing gone ids

A gone id stays in the inventory, with when OSDU stopped serving it, so the report says what disappeared and when. When
that history is no longer wanted, a removal with `finding: gone` and `scope: inventory` takes the rows out of the
inventory, with the versions kept of them (`versions: all`). It asks nothing of OSDU, so any inventory flow allows it,
whatever its `removal` declares, and the flow's credentials play no part.

```json
{
  "inventories": ["WellLogs"],
  "removal": { "finding": "gone", "scope": "inventory", "expected": 60955 },
  "confirm": "dev"
}
```

It is asked for and checked as any removal is: the partition typed back, the inventory reconciled and holding exactly
`expected` gone ids (or a row of every id named), and no run of the flow queued or running. It then takes 2,000 ids a
round, each round in one transaction under the lock a build's merge holds, so a round and a merge go one after the
other. An id stays, `skipped` with why, when the inventory no longer finds it gone, OSDU serves it again (the last build
listed it), or a ledger expects it (a later build asks storage whether it is missing). The rest are removed.

A removal from the inventory keeps how many ids it removed, who asked and when, but not the ids themselves: a row of
each would keep what it removes. Its items are the ids it kept. A lookup by OSDU id no longer finds a removed id in the
inventory, no ledger changes, and a later build lists again, as a new id, any id OSDU serves again.

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Inventory flow 'welldb-06-inventory' allows no removal: its document declares no 'removal', so it only reads OSDU. ...` | `remove` on a flow without `removal`. |
| `Inventory flow 'welldb-06-inventory' allows soft deletes only; removal.purge: true lets it purge, which destroys every version for good. Nothing was removed.` | `scope: everything` without `purge: true`. |
| `The removal confirms partition 'test', and inventory flow 'welldb-06-inventory' reads 'dev'. Nothing was removed.` | `confirm` names another partition. |
| `Inventory 'WellLogs' holds 41 orphan id(s) now, and the removal was asked for 37: what it holds changed since it was shown. Nothing was removed; look at it again and ask again.` | The inventory changed since the operator looked. |
| `payload removal asks for gone ids with scope record: a gone id is one OSDU no longer serves, so there is nothing of it in OSDU to remove; its removal takes the inventory's own row (inventory).` | A gone removal with another scope than `inventory`. |
| `payload removal asks for orphan ids with scope inventory: OSDU serves an id the inventory finds orphan, and the next build would list it again; ...` | `scope: inventory` for an id OSDU serves. |
