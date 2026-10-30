---
id: delivery-flow-inventory-builds
title: "Inventory builds (operations build and reconcile): what a build reads and merges, the finding of each id, and the tables kept"
type: flow-reference
summary: "What an inventory build and reconcile do step by step, what each finding of an id means, and the tables an inventory keeps."
keywords:
  - findings
  - orphan records
  - missing records
  - unlisted
  - drift between osdu and ledger
  - missing checks in turn
  - inventory tables
  - InventoryRecord
  - InventoryScan staging
  - reconcile
  - build
  - merge
related:
  - delivery-flow-inventory
  - delivery-flow-inventory-removals
  - delivery-concept-ledger
  - delivery-guide-finding-orphans
  - delivery-table-inventory-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReaders.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReport.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Inventories.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Inventory.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261008024435_InventoryFlows.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009094531_InventoryCheckedUtc.cs
---

# Inventory builds (operations build and reconcile): what a build reads and merges, the finding of each id, and the tables kept

A build of an [inventory flow](inventory.md) reads every id each inventory's kind holds, merges what it read into the inventory, and
reconciles it: every id gets a finding, what OSDU serves of it set against what the ledgers of the partition hold of it. A
reconcile compares the inventory, as its last build left it, with the ledgers as they stand now. This page says what each
finding means, what a build does step by step, and the tables an inventory keeps.

## The findings

Each id gets one finding. "Live" artifacts are the datasets and other ids a delivery minted.

| Finding | What OSDU serves | What the ledgers hold | Raised |
| --- | --- | --- | --- |
| `orphan` | the record | nothing; an owner created it | yes |
| `missing` | nothing | a delivered record, or a live minted id, that storage does not hold | yes |
| `undoing` | the id | a minted id whose undo is due or failed | yes |
| `forgotten` | the record | a record purged from its ledger, or an id minted by a delivery of one | yes |
| `stale` | the record | a record marked removed, or a minted id removed, gone or kept by an undo | yes |
| `unconfirmed` | the record | a record that never confirmed a delivery (pending, held or failed, no version), or a minted id still in intent or pending | yes |
| `drifted` | the record, at another version | a delivered record at another version | yes |
| `unlisted` | not in the read | a delivered record, or a live minted id, that storage holds: the index has not caught up, or it is outside the query | yes |
| `foreign` | the record | nothing; another identity created it | no |
| `superseded` | the dataset | a minted id a later delivery replaced, kept live on purpose | no |
| `tracked` | the record | a delivered record at this version, or a live minted id | no |
| `gone` | nothing | nothing: listed before, and no ledger expects it, or a ledger expects it and storage has not been asked for it yet (its reason says which) | no |
| `unreconciled` | the record | not compared yet: a build listed it and no reconcile has run since | no |

Every finding keeps the ledger and record, or the minted id, it rests on, and why in a line (`the ledger holds version
3, OSDU serves version 4`). A raised finding is one the report counts as a disagreement. What to do about each is in
the [orphans guide](../guides/finding-orphans.md#4-act-on-each-finding).

## What a build does

1. **Read.** Each inventory is read whole into a stage, 10,000 ids a write. A read that fails part way merges nothing:
   an id missing from a complete read is marked gone, so a partial read would mark live records gone. A run whose
   process stopped is closed failed by the next run of the inventory, which discards its stage.
2. **Merge**, in one transaction under a lock per inventory: new ids are added (`unreconciled` until compared), changed
   ones rewritten, ids served again brought back, and ids the read did not list marked gone, never deleted. A second
   build of the same inventory waits for the lock and, after ten minutes, fails with `Another build of the inventory held
   it for ten minutes; this build merged nothing.`
3. **Versions**, for `versions: all`: the version list of every record that is new or moved, `recordsAtOnce` at a time.
4. **Reconcile**: the owners, the finding of every id served, then up to `maxMissingChecks` ids a ledger expects that the
   read did not list, read by id from storage (`missing` when storage does not hold it, `unlisted` when it does). The ids
   are asked for least recently asked first (never asked first, by the row's `CheckedUtc`), so builds that each reach the bound take turns through
   all of them. An id a build did not ask for keeps what storage last answered; one never asked is `gone`, its detail
   saying a ledger expects it and a later build asks. With `maxMissingChecks: 0` nothing is read from storage and every
   id no longer listed is `gone`. A build that reaches the bound logs that more may be missing.

Inventories run one after another; one that fails leaves the others to complete, and the run ends failed naming it.

## The tables an inventory keeps

An inventory flow keeps what it reads and finds in the module's database, in the `osdu` schema, under its own ledger in
the partition: every build and reconcile registers that ledger (kind `inventory`, named `name@partition` for a flow that
works in partitions) before it reads anything. Every table is keyed by the partition first, so each partition's inventories are
apart, and no table has a foreign key into another. The comparison with the ledgers reads the delivery ledgers'
`osdu.Record` (by the id each record claims), `osdu.Artifact` and `osdu.PurgedRecord` (by OSDU id), and never writes them.

| Table | One row per |
| --- | --- |
| [`osdu.Inventory`](../tables/inventory.md) | inventory of a flow in a partition |
| [`osdu.InventoryRun`](../tables/inventory-run.md) | build or reconcile of one inventory |
| [`osdu.InventoryScan`](../tables/inventory-scan.md) | id one build's read listed, while the build runs |
| [`osdu.InventoryRecord`](../tables/inventory-record.md) | id an inventory holds, or a ledger of the partition expects it to hold |
| [`osdu.InventoryVersion`](../tables/inventory-version.md) | version storage keeps of a record (`versions: all`) |
| [`osdu.InventoryRemoval`](../tables/inventory-removal.md) | removal an operator asked for |
| [`osdu.InventoryRemovalItem`](../tables/inventory-removal-item.md) | id a removal reached; for a removal of gone ids, only each id it kept |
| [`osdu.InventorySelection`](../tables/inventory-selection.md) | selection an operator's conditions opened |
| [`osdu.InventorySelectionItem`](../tables/inventory-selection-item.md) | id of an inventory a match found, or copy a duplicate check picked to remove |
| [`osdu.InventoryDuplicateCheck`](../tables/inventory-duplicate-check.md) | duplicate check an operator's key opened |
| [`osdu.InventoryDuplicateGroup`](../tables/inventory-duplicate-group.md) | group of ids a check found sharing a key |
| [`osdu.InventoryDuplicateMember`](../tables/inventory-duplicate-member.md) | id a check read: its key, completeness and role |

Each table's page says what every column holds, what each index serves, and what writes and reads it.

A repair keeps no table of its own: it reads the inventory's `missing` rows (each with the ledger and record that claims
it) and is an `inventory-repair` activity of the inventory flow's ledger, while each delivery ledger records the marking
of its own records ([Repairing missing ids](inventory-removals.md#repairing-missing-ids)). A removal's `ActivityId` names its
`inventory-remove` activity. OSDU ids (`TargetId`) are kept in a binary collation, so they are compared exactly, as OSDU
compares them.

### What a row of an inventory holds

An id's system properties as OSDU last served them, when a build first listed it, last saw it change and first found it
gone, when a reconcile last asked storage for it, its finding with when it last changed and what it rests on, and the
ledger, record and artifact that claim it. Every column: [`osdu.InventoryRecord`](../tables/inventory-record.md).

### How a build moves rows through the scan

1. **Open.** The run of an inventory first closes any run of the same inventory still `running` as `failed` (`the run
   ended without finishing: its process stopped before it did`), deletes every row its earlier runs left in
   `osdu.InventoryScan`, and opens its own `osdu.InventoryRun`.
2. **Stage.** The read appends what it lists to `osdu.InventoryScan` under the run's id, 10,000 rows a bulk copy, each in
   its own transaction. Nothing of `osdu.InventoryRecord` changes while the read runs, so a read that fails part way
   leaves the inventory as the last complete build left it.
3. **Merge**, in one transaction holding the inventory's lock (`osdu-inventory-<PartitionId>-<InventoryId>`, the lock a
   removal of gone ids takes too). The run's staged rows are cut to one per id: the highest version a repeat listed, and
   of equal versions the last listed. Then, in this order, each counted:

   | Count | What changes in `osdu.InventoryRecord` |
   | --- | --- |
   | served again | A row gone, or never listed, that the read lists: its properties are written, `GoneUtc` cleared, `FirstSeenUtc` set when empty. |
   | changed | A row the read lists whose kind, version or system properties differ: they are rewritten and `ChangedUtc` set. |
   | added | An id with no row: inserted, `unreconciled`, first seen now. |
   | gone | A row listed before that the read did not list: `GoneUtc` set. The row stays. |

   The run's staged rows are then deleted in the same transaction, so `osdu.InventoryScan` holds nothing of a merged run.
4. **Versions** (`versions: all`): the records served whose `VersionsAt` is not their version, 500 at a time, have their
   versions read and their rows in `osdu.InventoryVersion` replaced.
5. **Reconcile.** The finding of every row OSDU serves is set from the ledgers, written only where the finding, the
   ledger record or the detail changed. Then the ids a ledger expects and the read did not list are asked of storage, up
   to `maxMissingChecks`, and in one transaction: the rows not asked become `gone` (or keep what storage last answered),
   the rows asked become `missing` or `unlisted` with `CheckedUtc` set, and an id a ledger expects that has no row is
   inserted with no `FirstSeenUtc`.
6. **Close.** A completed run writes its counts and findings to `osdu.InventoryRun` and its last build, last reconcile
   and owners to `osdu.Inventory`. A failed run's staged rows are deleted when it closes; those of a run whose process
   stopped are deleted by the next run of the inventory (step 1).

A `reconcile` runs steps 1, 5 and 6 and stages nothing. Rows leave `osdu.InventoryRecord` only through a removal of gone
ids ([Removing gone ids](inventory-removals.md#removing-gone-ids)); a removal from OSDU marks its ids gone and keeps them.

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Inventory 'WellLogs' has never been built in 'dev', so there is nothing to reconcile yet; build it first.` | `reconcile` before a build. |
| `Storage no longer knows the cursor of its listing of <kind> part way through, so the listing cannot be read whole; the next build reads it again from the start.` | A storage listing lost its cursor. |
| `Storage handed back a cursor of its listing of <kind> it had handed back before, so the listing goes in circles and cannot be read whole.` | A storage listing repeated a cursor. |
