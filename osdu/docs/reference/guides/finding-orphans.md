---
id: delivery-guide-finding-orphans
title: "Finding records in OSDU that no ledger holds, and removing the orphans safely"
type: guide
summary: "Use an inventory flow to list what OSDU serves, compare it with the ledgers, act on orphan, missing and stale records, and remove orphans safely."
keywords:
  - find orphan records
  - records not in the ledger
  - missing records in osdu
  - compare osdu with ledger
  - inventory flow
  - stale records
  - remove orphans
  - soft delete orphans
  - remove gone ids
  - repair missing records
  - reconcile
  - clean up osdu partition
related:
  - delivery-flow-inventory
  - delivery-cli-inventory
  - delivery-concept-removal-and-reversal
  - delivery-concept-ledger
  - delivery-guide-operations-runbook
  - delivery-concept-record-lifecycle
  - cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/InventoryDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Inventories.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryInventoryVerbs.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - osdu/gui/src/features/delivery/RemovalDialog.tsx
  - osdu/gui/src/features/delivery/inventories/InventoryRecordsGrid.tsx
  - osdu/specs/core/storage/openapi.yaml
---

# Finding records in OSDU that no ledger holds, and removing the orphans safely

Every record OSDU Delivery sends is in a ledger. What OSDU serves can still drift from the ledgers: a write that landed
after its answer was lost, a workflow that created more than it reported, a record written by hand, a ledger deleted or
a record purged from it. This guide sets up an [inventory flow](../flow/inventory.md) that lists every wellbore and well
log the `dev` partition serves, compares them with every ledger of the partition, and works through what it finds:
orphans no ledger knows, records a ledger expects that OSDU no longer holds, and records a ledger removed that OSDU still
serves. It ends by removing the orphans, which the flow allows only when its document says so.

## 1. Write the inventory flow

```yaml
flowType: inventory
name: welldb-06-inventory
description: Every wellbore and well log OSDU serves in the partition, set against the ledgers of the welldb flows.
batch: welldb
partitions: [dev, test]

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body: { scope: "${env:OSDU_SCOPE}" }
  read: search

owners: [welldb-delivery@example.com]
maxMissingChecks: 100000
removal: { findings: [orphan, stale], purge: false }
parallelism: { concurrency: 4 }

inventories:
  - name: Wellbores
    description: Every wellbore record, whatever its authority, source and schema version.
    kind: "*:*:master-data--Wellbore:*"
  - name: WellLogs
    kind: "*:*:work-product-component--WellLog:*"
    versions: all
  - name: LogFiles
    kind: "osdu:wks:dataset--File.Generic:*"
    query: 'tags.DeliveredBy:"welldb"'
```

The estate's `flows/welldb-06-inventory.yaml` keeps three inventories. `Wellbores` and `WellLogs` name their entity type
and leave the authority, source and version as `*`, with no query. That makes each **cover the type whole**: besides the ids it lists, it reads from storage the records the ledgers delivered of that type
and it did not list, so it can say one is `missing`. An inventory narrowed by a query or a version reports only the ids
it listed before and no longer lists, as `LogFiles` does: it lists the `dataset--File.Generic` records the welldb flows
registered. `maxMissingChecks` bounds how many ids a build reads from storage; `owners` and `removal` come into play
later (steps 3 and 5).

The flow reads through the search index (`source.read: search`, the default), which a viewer's entitlements allow. The
index lags writes and leaves out records it failed to index; `source.read: storage` lists every active record from
storage itself, unindexed ones included, and needs the `service.storage.admin` role.

```bash
sqlflow validate flows/welldb-06-inventory.yaml
```

```text
OK  'welldb-06-inventory' is valid (inventory: ${env:OSDU_URL} -> inventories).
```

## 2. Plan, then build

A plan counts the records each inventory would read, and for a storage read the kinds a wildcard expands to; it keeps
nothing:

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation plan
```

A build reads every id of each inventory whole (a read that fails part way changes nothing), merges it into the module's
database, and compares every id with every ledger of the partition:

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev
```

The module's database is the catalog's (`SQLFLOW_CATALOG_DB`, or `--db <conn-ref>`). Between builds, `--operation reconcile` compares the inventory as its last build left it with the ledgers as
they stand now, reading from storage only the ids a ledger expects; it is the quick check after a delivery or a cleanup.

## 3. Read the report

```bash
sqlflow inventory list --partition dev
sqlflow inventory show dev 5
sqlflow inventory records dev 5 --finding orphan
sqlflow inventory lookup dev dev:work-product-component--WellLog:9a41c7e2d05b4f6a8c3e1b7d2f9064ab
```

`show` gives the ids by finding, with the raised ones marked, and the owners the reconcile used. `records` pages through
one finding, each id with what OSDU serves of it, the ledger and record or minted id it rests on, and why it has its
finding. `lookup` answers what every inventory of the partition holds of one id. In the GUI, **OSDU**, **In OSDU**,
**Inventories** lists the inventories; an inventory's report shows the findings as tabs over a grid of ids, and a row
opens the id as OSDU holds it now. `sqlflow inventory export dev 5 --finding orphan --out orphans.csv` writes a finding
as CSV.

**Check the owners first.** An id no ledger knows is an `orphan` when an identity this estate writes as created it, and
`foreign` otherwise. The flow names its identities (`owners: [welldb-delivery@example.com]`), so orphans and foreign
records are told apart the same way every time, and `show` prints them as `declared`. Without `owners`, a reconcile
infers them: every identity that created at least 1% of the ids a ledger claims, which `show` prints as `inferred`.

## 4. Act on each finding

| Finding | What it means | What to do |
| --- | --- | --- |
| `orphan` | OSDU serves an id no ledger knows, created by an owner. | Find where it came from (its kind, `createUser`, `createTime`, a `lookup`). Remove it (step 5), or deliver it again through a flow so a ledger holds it. |
| `missing` | A ledger delivered it, or minted it, and storage does not hold it. | **Deliver again** from the missing tab: under its own id (a soft-deleted record restored, a purged one written again) or under a new one ([repairing missing ids](../flow/inventory.md#repairing-missing-ids)); or remove it from its ledger if it should be gone ([removal and reversal](../concepts/removal-and-reversal.md)). |
| `undoing` | An unfinished delivery left it and its undo is due or failed. | Let the undo run, or run the delivery flow's `undo` operation ([removal and reversal](../concepts/removal-and-reversal.md)). |
| `forgotten` | Its record was purged from its ledger, or a delivery of such a record minted it, and OSDU still serves it. | Remove it (a flow allowing `forgotten`), or deliver it again. |
| `stale` | A ledger marks it removed, or an undo removed or kept it, and OSDU still serves it. | Look at the record's history; remove it again from its ledger, or from the inventory (a flow allowing `stale`), which records the removal on the record. |
| `unconfirmed` | A ledger never confirmed the delivery (pending, held or failed): a write that landed without its answer. | The record's next delivery, or its release, settles it ([record lifecycle](../concepts/record-lifecycle.md)). |
| `drifted` | OSDU serves another version than the ledger delivered. | Run the delivery flow's `verify` to see whether the change was legitimate ([operations runbook](operations-runbook.md)). |
| `unlisted` | Storage holds it and the read did not list it. | Wait for the index and build again, or read through storage. |

`foreign`, `superseded`, `tracked`, `gone` and `unreconciled` need nothing; a `gone` whose reason says a ledger expects
it waits for a later build to ask storage. Acting on a finding goes through the
ledger's own actions; the inventory flow removes from OSDU only the ids no ledger holds live, and only when its document
allows it.

Gone ids stay in the inventory, each with when OSDU stopped serving it. To drop that history, open the **gone** tab, pick
ids or **Select all**, and **Remove**: the dialog leaves OSDU as it is and removes the rows from the inventory once the
partition is typed back. From the command line the payload is
`{"inventories": ["WellLogs"], "removal": {"finding": "gone", "scope": "inventory", "expected": 60955}, "confirm": "dev"}`.
It needs no `removal` in the document, asks nothing of OSDU, and keeps a gone id a ledger still expects
([removing gone ids](../flow/inventory.md#removing-gone-ids)).

## 5. Allow removal, and remove the orphans

Removing from OSDU is a line of the flow's document, reviewed where the document is. The file of step 1 has it, beside
the identities it writes as:

```yaml
# flows/welldb-06-inventory.yaml: the lines that allow removal
owners: [welldb-delivery@example.com]
maxMissingChecks: 100000
removal: { findings: [orphan, stale], purge: false }
```

`removal.findings` names what may be removed, of `orphan`, `stale` and `forgotten`. Removals are soft deletes (reversible
in OSDU) unless `purge: true` also allows purging, which destroys every version for good. They go through the flow's
own `source` credentials, which must be an owner of the records: a soft delete needs `users.datalake.editors` and the
identity in each record's owners ACL; a purge needs `service.storage.admin` and owner. Read-only viewer credentials are
refused by storage, and the removal stops at its first chunk saying so.

With the document synced to the catalog and reconciled, remove:

- **From the GUI.** On the inventory's report, open the **orphan** tab: each row has a box. Pick ids, or **Select all**
  to take every orphan however many, and **Remove**. The dialog offers **Remove the record** (a soft delete) or **Purge
  everything** where the flow allows it, says what is checked again of each id, and queues nothing until the partition
  is typed back. The removal is a run of the flow.
- **From the command line.** Write the payload the page would send, with the count `show` gave you:

  ```json
  {
    "inventories": ["WellLogs"],
    "removal": { "finding": "orphan", "scope": "record", "expected": 37 },
    "confirm": "dev"
  }
  ```

  ```bash
  sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation remove --payload @removal.json
  ```

  `scope` is `record` (soft delete) or `everything` (purge). To remove ids you picked, add `"ids": [...]` (at most 1,000)
  and set `expected` to their number.

Nothing is removed unless the inventory still holds exactly `expected` ids of the finding (or a row of every id named),
`confirm` is the run's partition, and the flow allows the finding and the scope. The page and the API also refuse while
a run of the flow is queued or running. Then every id is checked again just before it goes, 500 at a time: an id the
ledgers now hold, one whose version moved since the build, or (an orphan) one storage says no owner created, is skipped
and stays in OSDU.

## 6. Check what the removal did

```bash
sqlflow inventory removals dev 5
sqlflow inventory removal dev 9 --outcome skipped
```

Each id has an outcome: `removed`, `gone` (OSDU no longer served it), `skipped` (left in OSDU, with why) or `failed`
(with what OSDU answered). Removed ids are marked gone in the inventory at once, and a stale record's ledger records the
removal on the record with who asked. A removal stopped part way keeps what it did; the inventory then holds what is
left. Look at skipped ids, build again so they are listed as they are now, and remove again if they still should go. A
soft-deleted record can be brought back in OSDU; a purge cannot.

## 7. Keep it running

Schedule the inventory flow after the delivery flows that write the kinds it reads
([schedules](../../../../sqlflow/docs/reference/flow/schedule.md)); lineage orders it after them. A build each night and a
`reconcile` after a cleanup keep the report current. For an inventory keeping every version (`versions: all`), only
records that are new or moved cost a version read.

## When something goes wrong

| Symptom | Cause and fix |
| --- | --- |
| Many `foreign` ids you wrote yourself | The owners are wrong or inferred from too little: name them in `owners`. |
| `missing` ids that are really there | Storage answers an id the flow's credentials may not see as not found: check the source credentials' entitlements to the records' ACLs. |
| `Inventory 'WellLogs' has never been built in 'dev', so there is nothing to reconcile yet; build it first.` | Run a build before a reconcile. |
| `Inventory 'WellLogs' holds 41 orphan id(s) now, and the removal was asked for 37: ...` | The inventory changed since you looked; read `show` again and send the new count. |
| `OSDU refused the removal (403) of every id of a chunk: ...` | The flow's credentials are not an owner of the records, or lack the role. |
| `Storage no longer knows the cursor of its listing of <kind> part way through ...` | A storage read lost its cursor; build again. |
