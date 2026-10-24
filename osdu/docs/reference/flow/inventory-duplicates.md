---
id: delivery-flow-inventory-duplicates
title: "Finding duplicate records (operation duplicates): the records OSDU holds more than once, the copy kept and the copies removed"
type: flow-reference
summary: "An inventory's duplicate check: the key, how a duplicates run reads OSDU, which copy is kept, an operator's choice, and removing the copies picked."
keywords:
  - duplicate records
  - duplicate check
  - find duplicates
  - deduplicate
  - copies of a record
  - duplicate key
  - exact ignoreCase loose
  - most complete copy
  - attribute count
  - keep a copy
  - compare two copies
  - duplicates operation
  - duplicateCheck
  - InventoryDuplicateCheck
yamlPath: "(run operation duplicates of a flowType: inventory)"
related:
  - delivery-flow-inventory
  - delivery-flow-inventory-selections
  - delivery-flow-inventory-removals
  - delivery-concept-explorer
  - delivery-flow-assertion
  - delivery-table-inventory-duplicate-check
  - delivery-table-inventory-duplicate-group
  - delivery-table-inventory-duplicate-member
  - delivery-table-inventory-selection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateKey.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateExport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261024100322_InventoryDuplicateChecks.cs
  - osdu/gui/src/features/delivery/inventories/duplicates/InventoryDuplicates.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateGroupsGrid.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateKeyEditor.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateCompareDialog.tsx
---

# Finding duplicate records (operation duplicates): the records OSDU holds more than once, the copy kept and the copies removed

OSDU can hold one record more than once: a load run twice under ids OSDU assigned, a record another tool wrote again, a
delivery that changed how it makes its ids. An inventory knows each id, not what the records hold, so two copies of one
well log are two ids to it. A duplicate check finds them. An operator names a key, the properties the copies of one record
hold alike, and the check reads the key of every id the inventory serves, groups the ids that share it, reads each group's
copies whole and keeps the most complete; the copies it picks to remove are made a selection, which the inventory's removal
takes as it takes any selection. A check is a run of the flow (the `duplicates` operation), through the flow's own `source`
and credentials, and changes nothing in OSDU.

## The key

A key is one to 100 properties, each named once, each a path from the record's root (`data.WellboreID`,
`data.Curves.Mnemonic`; a list is crossed without `[*]`) starting at `data`, `meta`, `tags`, `ancestry`, `acl`, `legal`,
`kind`, `createUser`, `createTime`, `modifyUser` or `modifyTime`. `id` and `version` are refused: every record holds its
own. Each property compares one way:

| Comparison | Two values are alike when |
| --- | --- |
| `exact` (the default) | They are the same as the records hold them: text exactly, case and spaces included; a number as a number however it is written (`1520.50`, `1.5205e3` and `1520.5` alike). Text and a number are never alike. |
| `ignoreCase` | Text is the same ignoring case, in Unicode's composed form (`Wellbore A-1` and `WELLBORE A-1`); spaces still count. |
| `loose` | Text is the same ignoring case, the spaces at either end and how many spaces stand between words; empty text is no value; text that is a number is that number, so `"1520.50"` and `1520.5` are alike. |

Two records are copies of one another when they are alike at every property of the key. A property that crosses a list
holds every value the list yields, compared in any order, each as often as it occurs; a property a record holds nothing at
is alike only in a record that holds nothing there too. A record holding no value at any property of the key is passed
over (`unkeyed`). The key is kept in one spelling, so the same key, however it is written, asked again of the same build
answers with the check already made of it (200); `refresh` reads OSDU again (202).

## How a check reads OSDU

1. **The keys.** Every record of the inventory's kind, narrowed by its `query`, is read through the search a page of 1,000
   at a time (paged as the flow's `source.paging` says), returning only `id`, `version` and the key's properties. Each page
   is kept as it is read, matched with the ids the inventory serves by the OSDU id: an id that holds the key, or holds
   none of it; a record the inventory serves no row of (`outside`: written since its last build, or outside its query);
   an id at another version than the inventory listed (`changed`). The search returns only what its index keeps of a
   record, so a property of the key no record held a value at is named in the check's notes, and so are the ids the
   inventory serves that the search did not return (an inventory read through storage lists records the index does not
   hold).
2. **The groups.** The ids that share a key are numbered into groups, the group of the most copies first; every id whose
   key no other holds is let go. A group of more than 100 copies is too broad to be one record written more than once: it
   is left for review, and its copies are not read.
3. **The copies.** A round of 200 groups at a time, each group's copies are read whole from storage (`POST
   /query/records`, 100 ids a request) and the values each holds under `data` counted; for each copy storage holds, the
   records that name it are counted through the search (`"<id>" AND NOT id:"<id>"` over every kind, as the explorer's
   Mentioned by asks it); as many requests at once as the flow's `parallelism.recordsAtOnce`. Each group is judged
   ([Which copy is kept](#which-copy-is-kept)), and each round is written before the next is read.

A check that stops (the search or storage refusing or failing, the run cancelled, its process stopped) keeps no group, and
says why; a run tried again checks afresh. A check is a run of the flow, so one queued while another run of the flow is
under way waits for it.

## Which copy is kept

A copy's completeness is how many values it holds under `data`: every number, `true` and `false`, and text holding more
than spaces, each item of a list counted on its own and each value inside an object. A copy whose bulk data a Wellbore DDMS
keeps holds the link the DDMS writes to it (`data.ExtensionProperties.wdms.bulkURI`, and its entry in
`data.DDMSDatasets`), and a copy naming its files names them under `data.Datasets`, so the count tells a copy with them from
a copy without.

The findings a ledger holds live (`tracked`, `drifted`, `unconfirmed`, `undoing`, `superseded`) are a ledger's copies: a
delivery writes them, so the check never picks one to remove. A group comes out as the first rule that fits says:

| The group | Outcome |
| --- | --- |
| storage did not say whether it holds a copy | `review` |
| storage holds one copy or none now | `settled`; the copy held is kept |
| two or more copies are a ledger's | `review`: a delivery writes one record under more than one id, so the source or the key is to look at first |
| one copy is a ledger's, and another holds more values | `review`: the delivery leaves out what the other copy holds |
| one copy is a ledger's, and none holds more values | `picked`: the ledger's copy is kept |
| no copy is a ledger's, and one holds the most values | `picked`: that copy is kept |
| no copy is a ledger's, and two or more hold the most values | `review` |
| a copy picked to remove is named by another record, or the records naming it could not be counted | `review`: removing it would leave them pointing at nothing |

In a picked group every other copy is `remove` when the flow's `removal.findings` names its finding, and `leave`
otherwise, with why: another identity created it (`foreign`), only its owner can remove it; a ledger holds it, it is
removed through its delivery flow; the flow does not remove its finding, or declares no removal. A copy storage no longer
holds is `gone`. In a group for review every copy is `review`: nothing of it is removed until an operator chooses.

## Choosing the copy to keep

An operator may choose the copy any group keeps, among the copies storage held when the group was read. The group is
judged again with that copy kept (`chosen`), and the choice is recorded with who made it and when: the other copies the
flow may remove are picked to remove, a copy other records name among them with how many name it. A choice taken back
leaves the group to the check's own rules again. Choices are written only of a check that completed against the
inventory's last build, and not of a group too broad to have its copies read; the check's counts move with each.

## Removing the copies a check picked

The copies picked to remove are made a selection of the inventory (`POST .../duplicates/{checkId}/selection`): every copy
picked to remove of every group whose copy to keep is decided, each with the version the index held, what it holds at the
key, and the copy its group keeps. It is a completed selection against the build the check read, naming the check; asked
again with no choice written since, the same selection answers (200), and a new one is made otherwise (201). It is refused
for a check that has not completed, one the inventory was built again since, and one that picked nothing.

The selection is removed as any selection is ([Removing what a selection holds](inventory-selections.md#removing-what-a-selection-holds)): one
finding at a time, as many ids as the operator was shown, the partition typed back, against the inventory's last build.
Every copy is checked again just before it goes, as any removal checks it ([What a removal does](inventory-removals.md#what-a-removal-does)), and
goes only while storage still holds the copy its group keeps: the removal reads both in one pass, and leaves a copy whose
kept copy storage no longer holds (`skipped`, with why), so a removal never takes the last copy of a record.

## What a check says

| Status | Meaning |
| --- | --- |
| `queued` | Opened, its run queued. |
| `running` | Reading the keys (`keys`, with the records read of the ids the inventory serves) or the copies (`copies`, with the groups judged of the groups found). |
| `completed` | Read OSDU whole: its groups are every set of the inventory's ids that share the key, with how many it decided, left for review and picked to remove. |
| `failed` | Stopped, holding no group; `error` says why. |
| `expired` | Older than a week and kept for the removal that took a selection made of its picks: its key and counts stay, its groups were retired. |

A completed check made against an earlier build of the inventory is `stale`. A check is kept a week; the next check of the
partition retires those older, as a selection is retired.

## In the GUI

The inventory's **Duplicates** tab. Its first line builds the key: a chip for each property, its comparison picked from a
menu that says what each does, its cross to leave it out; **Property** lists the kind's properties, as the explorer's
Filter does, a text property added compared `loose` and anything else `exact`; and a copy open in the bottom panel offers
each of its values' properties to the key (**key**). **Find duplicates** asks for the check, **Check again** reads OSDU
again for the key of the check in view, and the history lists the inventory's checks. The second line says where the check
stands: how far it got and its run, or what it found (the whole of it behind the mark), its notes, **Download** (every copy
of every group as CSV) and **Select the copies to remove**, which makes the selection and opens it on the Ids tab, where a
removal takes it. The outcomes are tabs over the groups, those for review first.

Each group is a node of the grid: how it came out (why on hover), what its copies share, how many there are, the copy it
keeps and who chose it. Under it, each copy: its role (why on hover), its id, its values at the key's properties compared
other than exactly (those compared exactly start hidden, since every copy holds the same; View shows them), its finding,
how many values it holds against the fullest copy, how many records name it, and who created it. **Keep** stands on every
copy of a group for review and on a decided group's copies on hover; **Use the check's pick** takes a choice back;
**Compare** puts the two copies ticked, or the copy kept and the fullest beside it, side by side as OSDU holds them now,
their ids and the fields OSDU stamps left out. A group too broad to pick from lists its copies on asking. The check in view
(`dc`), the key being built (`dk`) and the outcome (`do`) live in the page's address, and a selection made of a check's
picks is listed by its number (`s`).

## What a run returns

A check's result is the check it filled, its status, the ids the inventory served, how many held the key and how many held
none of it, the records outside the inventory, the groups and the ids in them, how many groups it decided and left for
review, the copies it picked to remove, the requests it sent, its notes, and why it stopped. A check that stops ends the
run failed, carrying the same result.

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation duplicates --payload '{"inventories":["WellLogs"],"duplicateCheck":12}'
```

The run fills a check the control plane opened (`POST /api/v1/delivery/inventories/{partition}/{inventoryId}/duplicates`,
body `key`); the inventory's page and the API open them, and a run naming no check, or one of another inventory, is refused.

## The API

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `POST /inventories/{partition}/{inventoryId}/duplicates` | operate | body `key` (each `path` and `compare`), `refresh`, `pool` | 200 with the check already made of the same key against the inventory's last build, else 202 with a new one queued as a `duplicates` run. 400 for a key that is not one; 409 for an inventory never built or no longer declared. |
| `GET /inventories/{partition}/{inventoryId}/duplicates` | read | `limit` (20, at most 100) | Its checks, newest first. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}` | read | | One check as it stands, its groups counted by outcome once it completed. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups` | read | `outcome`, `after`, `limit` (50, at most 500) | A page of its groups, each with its copies when it holds at most 100. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/copies` | read | `after`, `limit` | A page of one group's copies. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/keep` | operate | body `inventoryRecordId` | Keeps that copy as the caller's choice; null takes a choice back. 200 with the group. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/selection` | operate | | The copies picked to remove, made a selection: 200 with the one already made when no choice was written since, else 201. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/export` | read | | Every copy of every group as CSV: the group, how it came out and why, the copy kept and who chose it; each copy's id, kind, finding, versions, storage's answer, values held, records naming it, role and why, who created and changed it and when; and a column for each property of the key. |

Every route is under `/api/v1/delivery`; the [API page](../concepts/api.md) lists them with the rest.

## The tables a check keeps

| Table | One row per |
| --- | --- |
| [`osdu.InventoryDuplicateCheck`](../tables/inventory-duplicate-check.md) | duplicate check an operator's key opened |
| [`osdu.InventoryDuplicateGroup`](../tables/inventory-duplicate-group.md) | group of ids a check found sharing a key |
| [`osdu.InventoryDuplicateMember`](../tables/inventory-duplicate-member.md) | id a check read: its key, completeness and role |
| [`osdu.InventorySelection`](../tables/inventory-selection.md) | selection made of a check's picks (`DuplicateCheckId`) |
| [`osdu.InventorySelectionItem`](../tables/inventory-selection-item.md) | copy picked to remove, beside the copy its group keeps (`KeptInventoryRecordId`) |

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Selection 33 holds the picks of duplicate check 12, made against an earlier build of inventory 'WellLogs', and the inventory was built again since: check for duplicates again before removing what it holds. Nothing was removed.` | A removal by a stale selection a duplicate check made. |
| `Inventory 'WellLogs' has not been built in 'dev', so it holds no id to check; build it first. Nothing was checked.` | `duplicates` before a build; the check is closed with it. |
| `Inventory 'WellLogs' in 'dev' holds no duplicate check 12: it was opened for another inventory, or retired since. Nothing was checked.` | `duplicates` naming a check the inventory does not hold, or one that expired. |
| `Duplicate check 12 of inventory 'WellLogs' is queued: only a check that read OSDU whole says which ids are copies of one another.` | A choice or a selection asked of a check that has not completed (409). |
| `Inventory 'WellLogs' was built again since duplicate check 12 read it: check for duplicates again, and act on what the new check finds.` | A choice or a selection asked of a stale check (409). |
| `Duplicate check 12 picked no copy to remove: every group keeps its copies, is left for review, or holds copies the flow may not remove. Choose the copy to keep in the groups for review first.` | A selection asked of a check that picked nothing (409). |
| `key[0] 'id': every record holds its own id, so no two records share it; a key names what the copies of a record hold alike.` | A key naming `id` or `version` (400). |
