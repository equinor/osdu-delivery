---
id: delivery-flow-inventory-duplicates
title: "Finding duplicate records (operation duplicates): the records OSDU holds more than once, the copy kept and the copies removed"
type: flow-reference
summary: "An inventory's duplicate check: the key, the download grouped in SQL, the pick that reads the copies, an operator's choice, and the copies removed."
keywords:
  - duplicate records
  - duplicate check
  - find duplicates
  - deduplicate
  - copies of a record
  - duplicate key
  - equal values
  - download the key
  - group again by another key
  - pick the copies to keep
  - check table dup_
  - most complete copy
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
  - delivery-flow-dimension
  - delivery-table-inventory-duplicate-check
  - delivery-table-inventory-duplicate-group
  - delivery-table-inventory-duplicate-member
  - delivery-table-inventory-selection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicateTable.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RunTimeTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateKey.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateReferences.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateExport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261024100322_InventoryDuplicateChecks.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261024131842_InventoryDuplicateDownloads.cs
  - osdu/gui/src/features/delivery/inventories/duplicates/InventoryDuplicates.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateKeyParts.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateKeyBuilder.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateGroupsGrid.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/DuplicateCompareDialog.tsx
---

# Finding duplicate records (operation duplicates): the records OSDU holds more than once, the copy kept and the copies removed

OSDU can hold one record more than once: a load run twice under ids OSDU assigned, a record another tool wrote again, a
delivery that changed how it makes its ids. An inventory knows each id, not what the records hold, so two copies of one
well log are two ids to it. A duplicate check finds them. An operator names a key, the properties the copies of one record
hold equal; the check downloads those properties of every id the inventory serves into a table of its own and groups the
ids there, in SQL. The groups can be formed again by any key of what was downloaded, at once and without reading OSDU. When
the groups look right, a pick reads each group's copies whole from storage and keeps the most complete; the copies it picks
to remove are made a selection, which the inventory's removal takes as it takes any selection. The download and the pick
are runs of the flow (the `duplicates` operation), through the flow's own `source` and credentials, and change nothing in
OSDU.

## The key

A key is one to 100 properties, each named once, each a path from the record's root (`data.WellboreID`,
`data.Curves.Mnemonic`; a list is crossed without `[*]`) starting at `data`, `meta`, `tags`, `ancestry`, `acl`, `legal`,
`kind`, `createUser`, `createTime`, `modifyUser` or `modifyTime`. `id` and `version` are refused: every record holds its
own.

Two records are copies of one another when they hold equal values at every property of the key:

- text exactly as written, case and spaces included (a composed and a decomposed letter are two spellings);
- a number as a number, however it is written (`1520.50`, `1.5205e3` and `1520.5` are equal); text and a number are never
  equal, and empty text is a value;
- a property that crosses a list holds every value the list yields, equal in any order, each as often as it occurs;
- a property a record holds nothing at is equal only in a record that holds nothing there too.

A record holding no value at any property of the key is passed over (`unkeyed`). The key is kept in one spelling, so the
same key, however it is written, answers with the check already grouped by it.

## How a check finds the duplicates

1. **The download.** Every record of the inventory's kind, narrowed by its `query`, is read through the search a page of
   1,000 at a time (paged as the flow's `source.paging` says), returning only `id`, `version` and the properties the check
   downloads. Each page is kept as it is read, matched with the ids the inventory serves by the OSDU id: a row for each id
   the inventory serves, holding each property's values and their hash ([The check's table](#the-checks-table)); a record
   the inventory serves no row of is counted (`outside`: written since its last build, or outside its query), and so is an
   id at another version than the inventory listed (`changed`). The search returns only what its index keeps of a record,
   so a property no record held a value at is named in the check's notes, and so are the ids the inventory serves that the
   search did not return.
2. **The groups.** The table is grouped by the key in SQL: each row's key is the hash of its key properties' hashes, the
   rows whose key another shares are numbered into groups, the group of the most copies first, and every id whose key no
   other holds is let go. A group of more than 100 copies is too broad to be one record written more than once: it is left
   for review, a pick does not read it, and the check's notes say to narrow the key. Each copy is given what it holds at
   the key, then at the check's other properties as room allows, so a group shows how its copies differ outside the key.

A download costs a search per 1,000 records of the kind, however many duplicates the key finds, and reads no copy. A
download that stops (the search refusing or failing, the run cancelled, its process stopped) keeps no group and no table,
and says why; a run tried again downloads afresh. A check is a run of the flow, so one queued while another run of the flow
is under way waits for it.

## Grouping again by another key

A key whose every property the check downloaded groups the check's table again, in SQL, at once: no run is queued and OSDU
is not read. The groups and their outcomes are formed afresh, so the picks and operator choices of the groups before are
let go, and the check says who set its key and when. A key naming a property the check did not download opens a new check,
which downloads that property with the key's others and every property the check before it downloaded, so its key can come
back to them. Only the inventory's current check (its newest, against its last build) is grouped again; a check under way
is not, and a check the inventory was built again since downloads afresh.

## The check's table

Each check that downloaded holds a table of its own in the module's database, `[osdu].[dup_<check>]`: one row for each id
of the inventory the check downloaded, keyed by the inventory's row of it.

| Column | Type | What it holds |
| --- | --- | --- |
| `inventory_record_id` | `bigint`, the key | The id's row of the inventory ([`osdu.InventoryRecord`](../tables/inventory-record.md)). |
| `target_id` | `nvarchar(500)` | The OSDU id. |
| `version` | `bigint` | The version the search index held of the record. |
| `<property>` | `nvarchar(max)` | For each property downloaded, its values as the record holds them, a JSON list of text, readable as a sheet; null when it holds none. |
| `<property>__hash` | `binary(32)` | The SHA-256 of those values as a key compares them: in one order, each written with its length and what it is. Two ids hold equal values exactly when their hashes are equal. |

A property's column is its path without the `data` root, each dot an underscore (`data.Curves.Mnemonic` is
`Curves_Mnemonic`, `tags.Source` is `tags_Source`), cut to 100 characters and made unique ignoring case with a number after
it. The check's row names each property's column (`AttributesJson`) and its table (`TableName`).

The table is data the module makes at run time, as a dimension's table is: the check's run creates it, and widens it, through
SQLFlow's schema evolution; no migration creates or alters it, and no entity maps it. It is dropped when its download fails,
when a later download of the same inventory completes (its properties carried into the newer check, the older check's groups
kept as they were found), and when the sweep retires the check. The database user the module connects as needs
`CREATE TABLE` in the database and `ALTER` on the `osdu` schema; without them the check fails saying so, before it reads
OSDU.

## Picking the copy to keep

A pick is asked for on its own, once the groups look right (`POST .../duplicates/{checkId}/pick`), and runs as a run of
the flow. A round of 200 groups waiting to be picked at a time:

1. each copy is read whole from storage (`POST /query/records`, 100 ids a request, as many requests at once as the flow's
   `parallelism.recordsAtOnce`), and the values it holds under `data` counted;
2. the records that name each copy storage holds are counted through the search over every kind (`*:*:*:*`), 40 copies a
   search, one search after another: one query names every copy of the batch and leaves out the batch's own records, so a
   batch no other record names answers in one request; a batch whose query finds a record, or that the service refuses, is
   split in two and each half asked again, until each named copy is counted on its own; a copy named by another copy of the
   batch is counted from the copies' own records;
3. each group is judged ([Which copy is kept](#which-copy-is-kept)), and the round is written before the next is read.

A pick reads about a storage request per 100 copies and a search per 40, more where a copy is named. A pick that stops
keeps the groups it judged and leaves the check completed with why it stopped; a pick asked again reads the groups left. A
group's copies, and how complete each is, are not known until a pick reads them.

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
holds is `gone`. In a group for review every copy is `review`: nothing of it is removed until an operator chooses. A group
not read yet is `pending`.

## Choosing the copy to keep

An operator may choose the copy any group a pick read keeps, among the copies storage held then. The group is judged again
with that copy kept (`chosen`), and the choice is recorded with who made it and when: the other copies the flow may remove
are picked to remove, a copy other records name among them with how many name it. A choice taken back leaves the group to
the check's own rules again. Choices are written only of a check that completed against the inventory's last build and is
not picking, and not of a group too broad to read or waiting to be picked; the check's counts move with each.

## Removing the copies a check picked

The copies picked to remove are made a selection of the inventory (`POST .../duplicates/{checkId}/selection`): every copy
picked to remove of every group whose copy to keep is decided, each with the version the index held, what it holds at the
key, and the copy its group keeps; the selection keeps the key the groups were formed by. It is a completed selection
against the build the check read, naming the check; asked again with no choice written since, the same selection answers
(200), and a new one is made otherwise (201). It is refused for a check that has not completed, one picking, one the
inventory was built again since, and one that picked nothing.

The selection is removed as any selection is ([Removing what a selection holds](inventory-selections.md#removing-what-a-selection-holds)): one
finding at a time, as many ids as the operator was shown, the partition typed back, against the inventory's last build.
Every copy is checked again just before it goes, as any removal checks it ([What a removal does](inventory-removals.md#what-a-removal-does)), and
goes only while storage still holds the copy its group keeps: the removal reads both in one pass, and leaves a copy whose
kept copy storage no longer holds (`skipped`, with why), so a removal never takes the last copy of a record.

## What a check says

| Status | Meaning |
| --- | --- |
| `queued` | Its download, or a pick of it (`phase` `copies`), waits for its run. |
| `running` | Downloading (`keys`, with the records read of the ids the inventory serves) or picking (`copies`, with the groups judged). |
| `completed` | Downloaded whole: its groups are every set of the inventory's ids that hold equal values at the key, those waiting to be picked counted with their copies, and its groups counted by size (pairs, 3 to 10, 11 to 100, more). A pick that stopped leaves it completed with its `error`. |
| `failed` | Its download stopped, holding no group and no table; `error` says why. |
| `expired` | Older than a week and kept for the removal that took a selection made of its picks: its key and counts stay, its groups and table were retired. |

A completed check made against an earlier build of the inventory is `stale`. A check is kept a week; the next check of the
partition retires those older, as a selection is retired.

## In the GUI

The inventory's **Duplicates** tab. While the inventory has no check, and after **Edit the key**, it builds the key in a
record: one of the inventory's records as OSDU holds it, read through the partition's connection and shown as its JSON,
beside the key. A click on a value adds its property to the key and a second click takes it out; a value of a list of
values adds the list, and a value inside the items of a list adds its property across every item (a click on one curve's
`Mnemonic` adds `data.Curves.Mnemonic`). The key lists its properties beside the record with the values the record shown
holds there; a property the check in view did not download is marked **to download**, and one of `data` the explorer does
not list for the kind is marked, since the search may return no value of it. **By name** adds a property the record shown
holds no value at. The record's line steps through the ids OSDU serves or opens any id, searches it, and folds it.

The key's action says what it reads before it is asked: **Group by this key** when the check in view downloaded every
property of it (no OSDU read), **Find duplicates** with the searches its download takes, **Download again** for the key the
check already has. **Back to check** leaves the key as the check has it.

With a check in view, the key line shows a chip for each property the check downloaded, the key's first: a click adds one to
the key or takes it out, and the groups form again at once, asking first when that lets go of a pick or an operator's
choice. **Edit the key** opens the record to pick other properties, **Download again** reads OSDU afresh, and the history
lists the inventory's checks. The status line says how far a download or a pick got and its run, or what the check found
(the whole of it behind the mark): its groups and ids, by size, a size too broad to pick from marked; the groups to pick;
those for review; its notes. **Pick the copies to keep** asks for a pick, its storage requests and searches said on hover;
**Pick the rest** continues one that stopped. **Select the copies to remove** makes the selection and opens it on the Ids
tab, where a removal takes it; **Download** writes every copy of every group as CSV.

The outcomes are tabs over the groups, those for review first once a pick has read them. Each group is a node of the grid:
how it came out (why on hover), what its copies share, how many there are, the copy it keeps and who chose it. Under it,
each copy: its role (why on hover), its id, its values at each property the check downloaded (the key's start hidden, since
every copy of a group holds the same; View shows them), its finding, how many values it holds against the fullest copy,
how many records name it, and who created it. A group waiting to be picked lists its copies as downloaded, and its grid is
read again while a pick writes it. **Keep** stands on every copy of a group for review and on a decided group's copies on
hover; **Use the check's pick** takes a choice back; **Compare** puts two copies side by side as OSDU holds them now. A group
too broad to pick from lists its copies on asking. The check in view (`dc`), the key being built (`dk`), the record it is
built in (`dr`) and the outcome (`do`) live in the page's address.

## What a run returns

A check's result is the check it filled, its status, the ids the inventory served, how many held the key and how many held
none of it, the records outside the inventory, the groups and the ids in them, how many groups it decided and left for
review, the copies it picked to remove, the requests it sent, its notes, and why it stopped. A download or a pick that stops
ends the run failed, carrying the same result.

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation duplicates --payload '{"inventories":["WellLogs"],"duplicateCheck":12}'
```

The run downloads the check the control plane opened (`POST .../duplicates`, body `key`), or picks it when a pick was asked
(`POST .../duplicates/{checkId}/pick`); a run naming no check, or one of another inventory, is refused.

## The API

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `POST /inventories/{partition}/{inventoryId}/duplicates` | operate | body `key` (each `path`), `refresh`, `pool` | 200 with the current check grouped by the key when it downloaded every property of it (or is grouped by it already); else 202 with a new check downloading them, queued as a `duplicates` run. `refresh` always downloads. 400 for a key that is not one; 409 for an inventory never built or no longer declared, or a current check downloading or picking with another key. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/pick` | operate | body `pool` | 202 with the check, a pick of its groups waiting queued as a `duplicates` run. 409 for a check not completed, picking, stale, holding no group to pick, or a flow no longer declared. |
| `GET /inventories/{partition}/{inventoryId}/duplicates` | read | `limit` (20, at most 100) | Its checks, newest first. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}` | read | | One check as it stands, with what it downloaded, its groups by outcome and size, and those waiting to be picked. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups` | read | `outcome`, `after`, `limit` (50, at most 500) | A page of its groups, each with its copies when it holds at most 100. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/copies` | read | `after`, `limit` | A page of one group's copies. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/keep` | operate | body `inventoryRecordId` | Keeps that copy as the caller's choice; null takes a choice back. 200 with the group. 409 for a group waiting to be picked or too broad to read. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/selection` | operate | | The copies picked to remove, made a selection: 200 with the one already made when no choice was written since, else 201. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/export` | read | | Every copy of every group as CSV: how its group came out, the copy kept, its role and why, and a column for each property of the key. |

Every route is under `/api/v1/delivery`; the [API page](../concepts/api.md) lists them with the rest.

## The tables a check keeps

| Table | One row per |
| --- | --- |
| [`osdu.InventoryDuplicateCheck`](../tables/inventory-duplicate-check.md) | duplicate check an operator's key opened |
| `osdu.dup_<check>` | id the check downloaded ([The check's table](#the-checks-table)) |
| [`osdu.InventoryDuplicateGroup`](../tables/inventory-duplicate-group.md) | group of ids holding equal values at the key |
| [`osdu.InventoryDuplicateMember`](../tables/inventory-duplicate-member.md) | copy in a group: what it holds, its completeness and role |
| [`osdu.InventorySelection`](../tables/inventory-selection.md) | selection made of a check's picks (`DuplicateCheckId`, `DuplicateKeyJson`) |
| [`osdu.InventorySelectionItem`](../tables/inventory-selection-item.md) | copy picked to remove, beside the copy its group keeps (`KeptInventoryRecordId`) |

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Selection 33 holds the picks of duplicate check 12, made against an earlier build of inventory 'WellLogs', and the inventory was built again since: check for duplicates again before removing what it holds. Nothing was removed.` | A removal by a stale selection a duplicate check made. |
| `Inventory 'WellLogs' has not been built in 'dev', so it holds no id to check; build it first. Nothing was checked.` | `duplicates` before a build; the check is closed with it. |
| `Inventory 'WellLogs' in 'dev' holds no duplicate check 12: it was opened for another inventory, or retired since. Nothing was checked.` | `duplicates` naming a check the inventory does not hold, or one that expired. |
| `Duplicate check 12 is downloading these properties: set another key once its download ends.` | Another key of the current check's properties while it downloads (409); `... is picking the copies of its groups: set another key once the pick ends.` while it picks. |
| `Duplicate check 12 holds no group waiting to be picked: every group's copies were read, or its groups are too broad to pick from.` | A pick asked of a check with nothing to read (409). |
| `The copies of group 3 are not read yet: pick the copies of the check's groups first, which reads how complete each copy is.` | A choice in a group waiting to be picked (409). |
| `Group 1 holds 412 copies, and no pick reads them: a group of more than 100 is too broad to pick from. Add properties to the key.` | A choice in a group too broad to read (409). |
| `Duplicate check 12 of inventory 'WellLogs' is queued: only a check that downloaded every id says which ids are copies of one another.` | A choice, a pick or a selection asked of a check that has not completed (409). |
| `Inventory 'WellLogs' was built again since duplicate check 12 read it: check for duplicates again, and act on what the new check finds.` | A choice, a pick or a selection asked of a stale check (409). |
| `Duplicate check 12 picked no copy to remove: every group keeps its copies, is left for review, or holds copies the flow may not remove. Choose the copy to keep in the groups for review first.` | A selection asked of a check that picked nothing (409). |
| `The duplicate check's table osdu.dup_12 could not be made, so nothing was downloaded: ... The database user the module connects as needs CREATE TABLE in the database and ALTER on the osdu schema.` | The module's database user cannot create the check's table; the check fails. |
| `key[0] 'id': every record holds its own id, so no two records share it; a key names what the copies of a record hold equal.` | A key naming `id` or `version` (400). |
