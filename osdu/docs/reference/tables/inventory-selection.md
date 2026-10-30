---
id: delivery-table-inventory-selection
title: "osdu.InventorySelection: the ids of an inventory that hold what an operator's conditions ask in OSDU"
type: table
summary: "One row per match of an operator's conditions against an inventory: the conditions as written and as searched, how OSDU was read, and its counts."
keywords:
  - osdu.InventorySelection
  - inventory selection table
  - conditions
  - match
  - select ids by value
  - search terms
  - removal by selection
related:
  - delivery-concept-database
  - delivery-flow-inventory
  - delivery-flow-inventory-selections
  - delivery-table-inventory-selection-item
  - delivery-table-inventory-removal
  - delivery-concept-search-terms
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261020222949_InventorySelections.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryDuplicates.cs
---

# osdu.InventorySelection: the ids of an inventory that hold what an operator's conditions ask in OSDU

The ids of an [inventory](inventory.md) that hold what an operator's conditions ask in OSDU: the conditions as the operator
wrote them and as the search was asked them, how the match read OSDU and what it found, and the build of the inventory it
was matched against. Every id it found is an [`osdu.InventorySelectionItem`](inventory-selection-item.md). A match runs as
a run of the inventory flow, and a removal may take its ids
([Selecting ids by what OSDU holds](../flow/inventory-selections.md)). Added by migration
`InventorySelections`.

The control plane opens a selection `queued`; the `match` run starts it against the inventory's last build, adds to its
counts with every page and closes it `completed` or `failed`; a read of it closes one whose run ended without finishing
it; and the next match of the partition, or the next selection made of a duplicate check's picks, retires those older
than a week: each is deleted with its ids, or kept `expired` without them when a removal took its ids.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `InventorySelectionId` | `bigint` identity | no | The selection's id within the partition. |
| `InventoryId` | `int` | no | The inventory it was matched against ([`osdu.Inventory`](inventory.md)). |
| `RunId` | `uniqueidentifier` | yes | The platform run the match ran as; null until it is queued. |
| `Actor` | `nvarchar(200)` | no | Who asked for the match. |
| `ConditionsJson` | `nvarchar(max)` | no | The conditions as the operator wrote them, a search term by its id, as JSON: what the page shows again. |
| `ConditionsKey` | `varchar(64)` | no | A SHA-256 of `ConditionsJson` in hex, by which the same conditions asked again of the same build find the match already made rather than reading OSDU again. |
| `FiltersJson` | `nvarchar(max)` | no | The conditions as the search is asked them: an explorer search of the inventory's kind, each search term carried through its mapping to the property its route fills, as JSON. |
| `Status` | `nvarchar(16)` | no | `queued`, `running`, `completed`, `failed` or `expired`. |
| `Query` | `nvarchar(max)` | yes | The Lucene query the conditions came to, as the match sent it; null until the match runs. |
| `Direction` | `nvarchar(16)` | yes | How the match read OSDU: `conditions` (every record the conditions find in the inventory's kind, joined with the inventory's ids) or `ids` (the inventory's ids in chunks, each asked with the conditions); null until it decides. |
| `NotesJson` | `nvarchar(max)` | yes | What the reader should know of how the conditions were read, as a JSON list: a source column read through other records, a value that found nothing. |
| `ColumnsJson` | `nvarchar(4000)` | yes | The properties whose values each id's row carries, those the conditions ask, as a JSON list. |
| `BuildRunId` | `bigint` | yes | The inventory's last build when the match read OSDU; a later build makes the match stale. |
| `Candidates` | `bigint` | no | The ids the inventory serves that the match judged. |
| `ToCheck` | `bigint` | no | How much the match has to read: the records the conditions find, or the inventory's ids. |
| `Checked` | `bigint` | no | How much of that it has read. |
| `Matched` | `bigint` | no | The ids of the inventory that hold what the conditions ask. |
| `Outside` | `bigint` | no | Records the conditions find in the inventory's kind that the inventory serves no row of: created since its last build, or outside its query. |
| `Changed` | `bigint` | no | Ids matched at another version than the inventory listed: changed since its last build, which a removal leaves for the next build. |
| `Requests` | `bigint` | no | The search requests the match sent. |
| `CreatedUtc` | `datetime2` | no | When the selection was opened. |
| `StartedUtc` | `datetime2` | yes | When the match started. |
| `CompletedUtc` | `datetime2` | yes | When it closed. |
| `Error` | `nvarchar(4000)` | yes | Why the match failed, redacted; null for one that did not. |
| `DuplicateCheckId` | `bigint` | yes | The [duplicate check](inventory-duplicate-check.md) whose picks the selection holds: the copies of each group it picked to remove, made a selection completed against the build the check read, with no conditions (`ConditionsJson` is `[]`, and its `ConditionsKey` names the check, so no lookup of conditions finds it) and no match. Null for a selection a match filled. |
| `DuplicateKeyJson` | `nvarchar(max)` | yes | The key the duplicate check's groups were formed by when the selection was made of its picks, as the check keeps it: the check may be grouped by another key since, and the selection still says what chose its ids. Null for a selection a match filled, or one made before selections kept it (its check's key is shown then). |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_InventorySelection` | `PartitionId`, `InventorySelectionId` | primary key | none | none | A partition's selections together. |
| `IX_InventorySelection_PartitionId_InventoryId_ConditionsKey_InventorySelectionId` | `PartitionId`, `InventoryId`, `ConditionsKey`, `InventorySelectionId` | index | none | none | The same conditions asked again of an inventory finding the newest match made of them. |
| `IX_InventorySelection_PartitionId_CreatedUtc` | `PartitionId`, `CreatedUtc` | index | none | none | The matches the sweep retires, oldest first. |

## Written and read by

- **Written** by the control plane when an operator asks for a match, or makes a selection of a duplicate check's picks
  (`SqlServerLedgerBulk.InventoryDuplicates.cs`, in one transaction with its ids), and by the `match` run of the inventory flow
  (`OsduLedger.InventorySelection.cs`, `SqlServerLedgerBulk.InventorySelection.cs`).
- **Read** by the inventory report's selection, `GET /api/v1/delivery/inventories/{partition}/{inventoryId}/selections/{selectionId}`,
  a removal by the selection, and the **Removals** tab.
