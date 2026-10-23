---
id: delivery-cli-inventory
title: "sqlflow inventory: read inventories, their findings, runs and removals from a terminal"
type: cli-command
summary: "sqlflow inventory list, show, records, lookup, runs, export, removals and removal: what inventory flows found of the ids OSDU serves."
keywords:
  - sqlflow inventory
  - inventory findings
  - orphan ids
  - lookup osdu id
  - inventory records
  - inventory runs
  - inventory export
  - inventory removals
  - removal outcome
  - "--finding"
cliCommand: inventory
related:
  - delivery-flow-inventory
  - delivery-guide-finding-orphans
  - delivery-cli-run
  - delivery-concept-ledger
  - cli-run
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryInventoryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryExport.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Inventories.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRemovals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerbContext.cs
---

# sqlflow inventory: read inventories, their findings, runs and removals from a terminal

`sqlflow inventory` reads what [inventory flows](../flow/inventory.md) found: the inventories of a partition, one
inventory's ids by finding, a page of its ids, what every inventory holds of one OSDU id, its builds and reconciles, a
CSV of its ids, and the removals asked of it with what each did to each id. It is one of the verbs OSDU Delivery adds to
SQLFlow's command line (`SqlFlow.Delivery.Cli.Host`, published as `sqlflow`). It reads the module's database only: it needs no flow file, no control plane, and
sends nothing to OSDU.

Building, reconciling and removing are runs of the flow, not this verb:

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --payload '{"inventories":["WellLogs"]}'
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation reconcile
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation remove --payload @removal.json
```

## Synopsis

```bash
sqlflow inventory list     [--partition <id>]
sqlflow inventory show     <partition> <id>
sqlflow inventory records  <partition> <id> [--finding <finding>] [--after <n>] [--limit <n>]
sqlflow inventory lookup   <partition> <osdu-id>
sqlflow inventory runs     <partition> <id> [--limit <n>]
sqlflow inventory export   <partition> <id> [--finding <finding>] [--out <file.csv>]
sqlflow inventory removals <partition> <id> [--limit <n>]
sqlflow inventory removal  <partition> <removal> [--outcome <outcome>] [--after <n>] [--limit <n>]
```

Every form also takes `--db <conn-ref>`, `--json` and `-v`. An inventory is named by its partition and its number, as
`list` shows them; a removal by its partition and its number, as `removals` shows them. A partition is a
`data-partition-id` (letters, digits, underscore, hyphen and dot).

| Option | Meaning |
| --- | --- |
| `--db <conn-ref>` | The database the module's tables live in: the catalog's (`--db`, else `${env:SQLFLOW_CATALOG_DB}`). |
| `--json` | Writes exactly one JSON document to the console; notes go to the error stream. |
| `--finding <finding>` | One of `orphan`, `missing`, `undoing`, `forgotten`, `stale`, `unconfirmed`, `drifted`, `unlisted`, `foreign`, `superseded`, `tracked`, `gone`, `unreconciled`. |

## list

The inventories of the partition `--partition` names, or of every partition: the number, partition, flow and inventory,
the kind it reads, and how many ids its last reconcile raised. A newer run that failed or is under way is named under
it.

```text
3 inventory(ies) in dev
       4  dev  welldb-06-inventory / Wellbores  *:*:master-data--Wellbore:*  2 raised, reconciled 2026-10-09 04:01:12Z
       5  dev  welldb-06-inventory / WellLogs  *:*:work-product-component--WellLog:*  39 raised, reconciled 2026-10-09 04:03:40Z
       6  dev  welldb-06-inventory / LogFiles  osdu:wks:dataset--File.Generic:*  not reconciled yet
          newest build 31 failed at 2026-10-09 04:03:41Z: ...
```

With `--json`: `partition` and `inventories`, each with `inventoryId`, `partition`, `flowName`, `ledgerId`, `name`, `kind`,
`query`, `read`, `versions`, `lastBuildRunId`, `lastBuiltUtc`, `lastReconcileRunId`, `lastReconciledUtc`, `reconciled`
(counts by finding), `raised` and `latest`.

## show

One inventory: what it reads and how, its last build and reconcile (what each listed, changed and raised), the owners its
last reconcile used and how it knew them (`declared`, `inferred` or `none`), and its ids by finding as its rows hold them
now, the raised findings marked.

```text
WellLogs: inventory 5 of welldb-06-inventory in dev
  reads *:*:work-product-component--WellLog:* through search, every version
  last build      27 completed 2026-10-09 04:03:40Z by cli:analyst@workstation: listed 18204 (12 new, 40 changed, 3 gone, 0 served again), 39 raised
  last reconcile  27 completed 2026-10-09 04:03:40Z by cli:analyst@workstation: listed 18204 (12 new, 40 changed, 3 gone, 0 served again), 39 raised
  owners          welldb-delivery@example.com (17950 claimed) (declared)
  18209 id(s), 39 raised
    orphan                37  raised
    missing                2  raised
    tracked            18167
    gone                   3
  See them with: sqlflow inventory records dev 5 --finding orphan
```

With `--json`: the inventory's fields as in `list`, `counts` (every finding, with `count` and `raised`), `ids`, `raised`,
`owners` (`source` and `identities`), `latest`, `lastBuild` and `lastReconcile`.

## records

A page of the inventory's ids, of the finding `--finding` names or every one, in the order the inventory took them in:
each with its number, finding, OSDU id and version, the ledger, record, status and version or the minted id it rests on,
and why it has its finding. `--limit` is 1 to 1,000 (50 when left out); the line under a full page names the `--after`
of the next.

```text
WellLogs in dev: 2 id(s) missing
         118  missing       dev:work-product-component--WellLog:6f1c2a9e3b7d4c1e9a550d4e2b8c7f10  v1712301122334455  ledger welldb-welllog-03-delivery@dev record 6f1c2a9e-3b7d-4c1e-9a55-0d4e2b8c7f10 delivered v1712301122334455
              a ledger expects it, and storage does not hold it
         341  missing       dev:work-product-component--WellLog:0b9e7d612c4a4f8eb1d35a6c7e8f9012  v1712301199887766  ledger welldb-welllog-03-delivery@dev record 0b9e7d61-2c4a-4f8e-b1d3-5a6c7e8f9012 delivered v1712301199887766
              a ledger expects it, and storage does not hold it
```

With `--json`: `partition`, `inventoryId`, `inventory`, `finding`, `next` and `records`, each with `inventoryRecordId`,
`targetId`, `kind`, `version`, `createUser`, `createTime`, `modifyUser`, `modifyTime`, `firstSeenUtc`, `changedUtc`,
`goneUtc`, `finding`, `findingUtc`, `detail`, `ledgerFlowId`, `ledger`, `deliveryKey`, `ledgerStatus`, `ledgerVersion`,
`artifactId` and `artifactState`.

## lookup

What every inventory of the partition holds of one OSDU id: each inventory listing it or expecting it from a ledger, with
its finding there, and what removals did to it.

```bash
sqlflow inventory lookup dev dev:work-product-component--WellLog:9a41c7e2d05b4f6a8c3e1b7d2f9064ab
```

```text
dev:work-product-component--WellLog:9a41c7e2d05b4f6a8c3e1b7d2f9064ab in dev: 1 inventory(ies)
  welldb-06-inventory / WellLogs (inventory 5): gone, v1712309988776655
      removed by analyst@example.com in removal 9 (run 0199a3c2-5e4f-7b21-9c3d-8e1f2a3b4c5d): soft deleted (reversible)
  removal 9 at 2026-10-09 09:12:30Z: removed (orphan): removed from OSDU (reversible, in bulk)
```

With `--json`: `partition`, `id`, `hits` (each `inventory` and `record`) and `removals` (each removal item, as in
`removal`).

## runs

The inventory's builds and reconciles, newest first (20 unless `--limit` says otherwise, at most 200): status, what each
listed (a build) or checked in storage (a reconcile), what it raised, who ran it and why one failed.

```text
WellLogs in dev: 2 run(s)
        27  build      completed  2026-10-09 04:01:02Z  listed 18204 (12 new, 40 changed, 3 gone, 0 served again), 39 raised  by cli:analyst@workstation
        24  reconcile  completed  2026-10-08 16:20:11Z  checked 5 expected id(s) in storage, 41 raised  by cli:analyst@workstation
```

With `--json`: `partition`, `inventoryId`, `inventory` and `runs`, each with `inventoryRunId`, `runId`, `operation`,
`status`, `actor`, `read`, `startedUtc`, `completedUtc`, `listed`, `pages`, `requests`, `added`, `changed`, `gone`,
`returned`, `missingChecked`, `findings`, `raised`, `owners` and `error`.

## export

The inventory's ids, of one finding or every one, as CSV with a header row, to `--out` or the console. A file is written
beside its name and moved into place, so a failure never leaves half a file; `Wrote 37 id(s) orphan of WellLogs to
<path>` goes to the error stream. It is the same file the GUI's export gives. The columns are `inventory_record_id`,
`id`, `kind`, `version`, `finding`, `finding_utc`, `detail`, `create_user`, `create_time`, `modify_user`, `modify_time`,
`first_seen_utc`, `changed_utc`, `gone_utc`, `ledger`, `ledger_flow_id`, `delivery_key`, `ledger_status`,
`ledger_version`, `artifact_id` and `artifact_state`. A cell a spreadsheet would run as a formula is written with a
leading apostrophe.

```bash
sqlflow inventory export dev 5 --finding orphan --out welllog-orphans.csv
```

## removals

The removals asked of the inventory, newest first (20 unless `--limit` says otherwise, at most 200): what each removed
(the finding, ids picked, those of a selection or every one), soft deleted or purged, where it stands, its tallies, who
asked and why one stopped. A removal that took the ids of a selection reads `of selection <n>`
([Selecting ids by what OSDU holds](../flow/inventory.md#selecting-ids-by-what-osdu-holds)); `GET
/api/v1/delivery/inventories/{partition}/{inventoryId}/selections/{n}` says which conditions made it.

```text
WellLogs in dev: 1 removal(s)
         9  completed  2026-10-09 09:12:02Z  37 orphan (every one), soft deleted (reversible): 35 removed, 1 already gone, 1 skipped, 0 failed  by analyst@example.com
```

A removal of gone ids reads `removed from the inventory alone (nothing asked of OSDU)` in place of soft deleted or
purged.

With `--json`: `partition`, `inventoryId`, `inventory` and `removals`, each with `removal`, `inventoryId`, `runId`,
`actor`, `finding`, `scope`, `namesIds`, `selection`, `requested`, `status`, `startedUtc`, `completedUtc`, `removed`,
`gone`, `skipped`, `failed`, `error` and `activityId`.

## removal

What one removal did to each id, of the outcome `--outcome` names (`removed`, `gone`, `skipped`, `failed`) or every one:
the id, its version and why. `--limit` is 1 to 1,000 (50 when left out); the line under a full page names the `--after`
of the next.

```text
removal 9 in dev (completed): 37 orphan, soft deleted (reversible), by analyst@example.com at 2026-10-09 09:12:02Z
       301  skipped   dev:work-product-component--WellLog:3c7e9b10a2d54e8f96b1c4a07d5e2f38 v1712305566778899: OSDU serves version 1712309911223344, and the inventory listed version 1712305566778899: it changed since, so it is left for the next build to look at
```

A removal of gone ids keeps the ids it kept, not those it removed from the inventory; its first line is followed by
`<n> id(s) removed from the inventory: the removal keeps how many, not which; the ids listed are those it kept.`

With `--json`: `partition`, `removal`, `outcome`, `next` and `items`, each with `item`, `removal`, `id`, `version`,
`finding`, `outcome`, `reason`, `ledgerFlowId`, `deliveryKey` and `recordedUtc`.

## Exit codes and errors

Every form exits 0 on success. A usage mistake prints `ERROR  <reason>` and the verb's usage lines and exits 1; any other
failure prints `ERROR  <message>` and exits 1. Ctrl+C exits 130.

| Message | Cause |
| --- | --- |
| `Inventories live in the module's database. Run 'sqlflow inventory' with --db <conn-ref>, or set the catalog variable.` | No module database. |
| `name the inventory by its number, as 'sqlflow inventory list' shows it: sqlflow inventory show <partition> <id>.` | The number is missing. |
| `No inventory 7 in partition 'dev'; 'sqlflow inventory list --partition dev' shows its inventories.` | No such inventory in the partition. |
| `--finding 'orphans' is not a finding; an inventory finds orphan, missing, undoing, forgotten, stale, unconfirmed, drifted, unlisted, foreign, superseded, tracked, gone, unreconciled.` | An unknown finding. |
| `--outcome 'done' is not what a removal comes to for an id; it is one of removed, gone, skipped, failed.` | An unknown outcome. |
| `--limit '5000' is not a count between 1 and 1000.` | Out of range. |

The control plane answers the same reads under `/api/v1/delivery/inventories` ([API](../concepts/api.md)), and the GUI's
**Inventories** page shows them.
