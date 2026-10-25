---
id: delivery-flow-removal
title: "Removal flow (flowType: removal): take out of OSDU the records whose keys the source no longer holds"
type: flow-reference
summary: "The flowType: removal document: read a source's keys whole, and its removed keys, compare them with a delivery ledger, and take out what the source dropped."
keywords:
  - removal flow
  - deletion detection
  - detect deleted rows
  - keys the source no longer holds
  - removed keys list
  - full key read
  - source key comparison
  - ledger deletions
  - RemovedKey
  - guard
  - maxPercent
  - maxRecords
  - approve removals
  - keepReferenced
  - lift removal
  - deliver again
  - incremental removal
  - matchKeys
  - soft delete
  - removedAt
related:
  - delivery-cli-removal
  - delivery-cli-records
  - delivery-concept-removal-and-reversal
  - delivery-concept-record-lifecycle
  - delivery-concept-change-detection
  - delivery-concept-ledger
  - delivery-flow-delivery
  - delivery-flow-inventory
  - delivery-flow-inventory-removals
  - delivery-table-removed-key
  - delivery-table-removal-run
  - delivery-table-removal-key
  - delivery-wiki-0022-removal-flows
  - flow-source
  - flow-schedule
yamlPath: "(root, flowType: removal)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/RemovalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RemovalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/RemovalFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RemovalExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalKeySources.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalTargets.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SourceRemovals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryRemovalEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRemovalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022223524_RemovalFlows.cs
  - osdu/gui/src/features/delivery/removals/DeliveryRemovalsPage.tsx
  - osdu/gui/src/features/delivery/removals/RemovedKeysGrid.tsx
  - osdu/docs/census/keys.removal.json
  - osdu/samples/welldb/flows/welldb-wellbore-05-removal.yaml
---

# Removal flow (flowType: removal): take out of OSDU the records whose keys the source no longer holds

A removal flow finds the records a source no longer holds and takes them out of OSDU. It reads the keys the source holds
now, whole, every run, and optionally a list the source keeps of the keys it removed; compares them with the ledger of each
delivery flow it names; and takes out of OSDU, through that delivery flow's own removal path and into that flow's ledger,
the records whose keys are gone. It keeps no ledger of its own: what it finds is the delivery ledger's **deletions**, one
per key ([`osdu.RemovedKey`](../tables/removed-key.md)), worked through incrementally, so a record taken out is never asked
of OSDU again, and the delivery flow delivers no row of the key that is not newer than its removal. A run that finds more
removed than its [guard](#the-guard) allows takes nothing out and waits for an operator's approval.

It is a flow of its own because a full key read does not belong in the ingestion chain. An ingestion flow that loads its
files incrementally sees only the new ones, so its `matchKeys` comparison (SQLFlow's
[deleted-row detection](../../../../sqlflow/docs/reference/flow/ing-load.md)) cannot tell a key that was dropped from one
that was simply not in this load. A removal flow reads the source itself, on a
schedule of its own, and leaves the pre, ingestion and delivery flows exactly as they are
([decision 0022](../../wiki/decisions/0022-removal-flows.md)).

## Example

The sample estate's removal flow, `flows/welldb-wellbore-05-removal.yaml`. The wellbore folder holds the well database's
current cut, one file written whole and replaced each time, so it is the source's complete key set:

```yaml
flowType: removal
name: welldb-wellbore-05-removal
description: The wellbores the well database no longer holds, taken out of OSDU.
batch: welldb
partitions: [dev]

guard:
  maxPercent: 20

removals:
  - flow: welldb-wellbore-03-delivery
    keys:
      type: csv
      location: ../data/wellbore
      options:
        delimiter: ","
        header: true
        srcFile: "*.csv"

schedule: welldb-removal
```

The delivery flow keys its records by `wellbore_name`; the source's files hold a column of that name, so `columns` is left
out. A source that also keeps a table of the keys it deleted, each with when, reads both, and a key either says is gone is
removed:

```yaml
removals:
  - name: wellbores
    flow: welldb-wellbore-03-delivery
    keys:
      connection: ${env:WELLDB_SOURCE_DB}
      object: WellDB.dbo.Wellbore
      filter: AND Status <> 'draft'
      columns:
        - { name: WellboreName, as: wellbore_name, expr: "LTRIM(RTRIM(@ColName))" }
    removed:
      connection: ${env:WELLDB_SOURCE_DB}
      object: WellDB.dbo.DeletedWellbore
      columns:
        - { name: WellboreName, as: wellbore_name }
      removedAt: { name: DeletedAt }
```

## Top-level keys

| Key | Required | What it is |
| --- | --- | --- |
| `flowType` | yes | `removal`. |
| `name` | yes | The flow's name: its pipeline identity, and the name its runs and the deletions it finds are kept under in each delivery ledger. At most 200 characters. |
| `description` | no | What the removals are for. |
| `batch` | no | The platform batch label. |
| `parameters` | no | The flow's parameters, by name, as every flow kind declares them: `{name}` tokens of a removal's `values` and a key source's `location` take them. `partition` is the run's and may not be declared. A parameter may list its `choices` ([parameters](parameters.md)). |
| `partitions` | no | The partitions the flow removes in, each a literal `data-partition-id`, unique ignoring case. Left out, every partition registered with the catalog. A run removes in one partition, since the ledgers it writes to are a partition's; `*` is refused. |
| `guard` | no | When a removal waits for an approval ([The guard](#the-guard)): `maxPercent` (0 to 100, default 20) and `maxRecords` (0 or more, no bound by default). |
| `removals` | yes | The removals, at least one and at most 50, names unique ignoring case. |
| `schedule`, `mode`, `lifecycle` | no | The platform envelope, as SQLFlow's flows take it ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md)). |

Each removal:

| Key | Required | What it is |
| --- | --- | --- |
| `flow` | yes | The delivery flow whose records it takes out, by name ([The delivery flow](#the-delivery-flow)). |
| `interface` | no | The interface of that flow, for a source with interfaces. |
| `name` | no | The removal's name, at most 300 characters: what its runs, approvals and deletions are kept under. Left out, the flow's name, with `/<interface>` for an interface. |
| `values` | no | The delivery flow's parameter values the comparison is scoped by, each a literal or a `{name}` token, or a sequence of them for a parameter its scope reads several values of (`logSource: [COMPOSITE, WIRELINE]`). They bind its `source.record.scope`, so only the records planned under them, and the record table's rows in that scope, are compared; a scoped flow needs a value for every parameter its scope binds. |
| `keepReferenced` | no | `true` (the default): a record other records in OSDU still name stays there until they are gone. `false` takes it out whatever names it. |
| `keys` | one of the two | The keys the source holds now, its complete key set, read whole every run. |
| `removed` | one of the two | A list of the keys the source removed, each with the moment it was removed. |

A key source (`keys` or `removed`) is either a **reader source**, read by one of SQLFlow's file readers, or a **table
source**, a table or view of a source database, never both:

| Key | Applies to | What it is |
| --- | --- | --- |
| `type` | reader | The reader, as a SQLFlow file flow names it: `csv`, `xls`, `xlsx`, `json`, `jsonl`, `ndjson`, `xml`, `parquet`, `prq`, `duckdb` or `delta`. |
| `location` | reader | The folder, file, glob or URI, relative to the flow file unless rooted, a URI or a `${env:...}` / `${keyvault:...}` reference; `{name}` tokens and `{partition}` are filled first, and a value holding a path separator or `..` is refused. Required unless `options.srcPath` names it. |
| `options` | reader | The reader's options, exactly as a SQLFlow file flow's source takes them. The file lifecycle (`copyToPath`, `zipToPath`, `srcDeleteIngested`, `srcDeleteAtPath`) is refused: reading keys never moves or deletes a source's files; so is `settledFilesOnly`, the engine's to set. A date window (`initFromFileDate`, `initToFileDate`, `incrementalAfterDate`) is refused for `keys`, whose read must be whole, and taken for `removed`, which only removes less. |
| `connection` | table | The source database's connection, a `${env:NAME}` or `${keyvault:vault/secret}` reference (a SQL Server one may be a connection string holding no secret). |
| `provider` | table | The source system: `mssql` (the default; `sqlserver` is the same), `azdb`, `mysql`, `postgres` (`postgresql`) or `oracle`. |
| `object` | table | The table or view, a three-part `[database].[schema].[object]` name, as SQLFlow's ingestion flows name one. |
| `filter` | table | A predicate narrowing the rows read, appended to the WHERE as written, so it carries its own leading `AND`. For `keys`, a key it leaves out looks removed. |
| `columns` | both | The columns the record key is read from, in SQLFlow's transform notation: `name` (the source column), `as` (the record key column it fills, the same name when left out) and `expr` (an expression over the value, written `@ColName` whatever the column is called, as `UPPER(@ColName)`; any other `@` variable is refused). Each record key column is filled once. Left out, each record key column is read from a column of the same name. |
| `removedAt` | `removed` | The column holding the moment each key was removed (`name`, and `expr` over it). Required for a table source; a reader source without one takes each file's date (`FileDate_DW`). Refused on `keys`, and `as` is refused. |

Every key is checked when the document loads, and every literal secret is refused, as in every flow of the module. The
editor documents and checks the keys from the key census, `osdu/docs/census/keys.removal.json`.

## The delivery flow

A removal compares and writes to the ledger of a delivery flow (`flowType: delivery`) in the removal flow's repository,
named by `flow`. The run finds its document where the catalog's read model of the repository's interfaces says it is, under
one of the removal flow's own folders; in a checkout the catalog has not synced, it searches from the removal flow's folder
up, a folder at a time, for the one delivery document of that name, and refuses a name two documents declare. The delivery
flow is bound to the run's partition, which it has to serve, and to the removal's `interface`, which it has to declare.

The records are then taken out under the delivery flow's ledger identity, with its credentials and route, through its own
removal path: the removal flow never writes a record of its own. A delivery flow on the `dspdm` or `etp` route is refused,
since those routes keep nothing they delete and a removal flow takes records out reversibly only.

## How a key is read

Each key source is read in full through SQLFlow's own readers and nothing of the removal's: a reader source through the
registered source reader of its `type`, with its schema found as it is read (columns unioned across files) and the
provenance columns it adds (`FileName_DW`, `RowNumber_DW`, `FileDate_DW`); a table source through SQLFlow's connection
resolution, connection factory and the source system's SQL dialect, `SELECT <columns> FROM <object> WHERE 1 = 1 <filter>`.
A column the source does not hold stops the removal, naming the columns it does hold.

The record key is the delivery flow's (`source.record.key`), in its order, so a key is always read whole. Each value is read
as text in invariant form (a number in invariant digits, a moment in ISO 8601 with every digit of its fraction, a GUID in
its 36-character form), and SQL Server, where the ledger is kept, converts it: `expr` is evaluated over it first, then the
value is converted to the type the key column holds in the record table, exactly as the delivery flow reads that column. The
converted values are made text as a delivery makes them and the delivery key is derived from them as a delivery derives it,
so a key read from a Parquet file, a CSV file or a source table lands on the same delivery key as the record table's row.

A removal moment is converted to an instant in UTC (an offset it carries is honoured). A key whose value is blank or does not
convert, or a removed key whose moment is blank or does not convert, stops the removal before it compares anything, naming up
to 20 such rows by file and row: a key that cannot be read would make its record look removed, or hide a removal. Keys are
converted and staged 20,000 at a time in [`osdu.RemovalKey`](../tables/removal-key.md); a key read twice is staged once,
with the latest removal moment.

## A run

A removal flow runs as a platform run, from its schedule, the Trigger run dialog, the Removals page, or
`sqlflow run <removal.yaml>`. Its operations:

| Operation | What it does |
| --- | --- |
| `remove` (the default) | Reads, compares and writes what it finds, then takes out of OSDU the ledger's pending deletions. |
| `plan` | Reads and compares the same way, reports what it would find and whether the guard would hold it, and writes nothing to OSDU or a ledger. |

The run's partition is the `partition` run value, settled as every run of the module settles it. Its payload takes
`entries` (the removals to carry out, every one when it names none) and, for an approval, `approve` with `confirm`
([The guard](#the-guard)); the built-in run overrides (a full load, a backfill window, a file pattern, a source filter) are
refused, since a removal reads every key its sources hold.

For each removal, in document order, the run:

1. finds and opens the delivery flow, binds its values and loads its mapping and record key;
2. registers the delivery ledger in the partition and opens a [removal run](../tables/removal-run.md) under it, closing any
   run of the same removal its process left `running`;
3. carries out an approval, when the payload gives one;
4. reads `keys` whole, noting when the read began, then `removed`, then the delivery flow's record table in the removal's
   scope (each key with when its row last changed), and stages all three;
5. compares them with the ledger ([What a run finds](#what-a-run-finds)) in one transaction;
6. takes out the ledger's pending deletions ([Taking out what the source removed](#taking-out-what-the-source-removed));
7. closes the run `completed`, or `guarded` when its guard held what it found, and deletes what it staged.

A removal that fails is closed `failed` with why, and the next removal runs. A run in which a removal failed or was held
ends `failed`, so the run-failed notification goes out, and its result still says how every removal came out. A run that is
cancelled closes its removal `failed`; what it recorded stands and the next run takes the rest. The run's log names each
step with its counts, and its result lists every removal with its tally.

## What a run finds

The comparison covers the delivery ledger's records whose last submission carried the removal's scope values (every record
of a flow whose records carry no scope), and the record table's keys in that scope that the ledger holds no record of. A
submission given several values of a parameter ([several values of a scope
parameter](delivery.md#several-values-of-a-scope-parameter)) carried the removal's scope when every one of them is among
the removal's values: a removal of `[COMPOSITE, WIRELINE]` compares the records planned under `COMPOSITE`, under
`WIRELINE` and under both, and a removal of `COMPOSITE` alone compares no record planned under both, since such a record may
hold either value and the removal reads the rows of one. Of those, a key is **found removed** when:

- `keys` is read and does not hold it (`absent`, as of when the read began), or
- `removed` lists it with a moment, and `keys`, when read, does not hold it after that moment (`listed`, as of the moment
  the list gives).

A found key is set aside as **come back** when its row in the record table (or the ledger's record of it) changed after the
moment: for an absent key, after five minutes before the read began (the margin covers the clocks of the node and the
database); for a listed key, after the moment it lists. The key's source holds it again, or its row was loaded again.

A key the ledger already holds a deletion of is not found again; a later moment for it moves its deletion's moment. A
deletion is **lifted**, and its key no longer kept out, when the source holds the key again (`keys` holds it, and its
deletion is older than the read) or the record was delivered again since the deletion was made (a newer row arrived and the
delivery flow delivered it). A lifted record the removal took out is asked to be planned again, so the next run of the
delivery flow delivers it under its id.

What the run found becomes the ledger's deletions, `pending`, or `awaiting-approval` when the guard holds it. `Compared`,
`Detected`, `CameBack` and `Lifted` are kept on the removal run.

## The guard

A source that suddenly holds far fewer keys is more often a broken read (a truncated export, a wrong folder, an expired
share) than a real mass removal. The guard holds a run's findings when:

- they are more than `guard.maxPercent` of the compared keys (default 20, SQLFlow's own `matchKeys` default),
- they are more than `guard.maxRecords`, when set, or
- `keys` was read and held no key at all, while the comparison found keys removed.

Held deletions are written `awaiting-approval`: nothing is done with them, and no row of them is kept out, until an operator
approves the count. The run is closed `guarded` and ends failed, naming the removal, so the run-failed notification goes
out; the deletions already pending, found within the guard or approved earlier, it takes out as any run does.

An approval is given on the Removals page (or the removal flow's Removals tab) for the count shown there, with the partition
typed back. It queues a `remove` run of the removal alone whose payload carries the approval:

```json
{ "entries": ["wellbores"], "approve": { "expected": 640 }, "confirm": "dev" }
```

The control plane refuses it with 409 while a run of the flow is queued or running, or when another number waits than the
operator was shown; the run checks the count again on the node, in a serializable transaction, and approves nothing when it
changed. Approved deletions become `pending` under the approver's name (`ApprovedBy`, `ApprovedUtc`), and the same run reads
the sources again and takes them out with the rest. What it finds besides meets the guard on its own.

## Taking out what the source removed

A remove run takes out the delivery ledger's `pending` deletions alone, whichever removal flow found them, 500 at a time in
key order, so a deletion taken out is never asked of OSDU again. Each chunk's records are held under the removal's lease, as
an operator's removal holds them, and each key comes to one outcome:

| Outcome | What happened | Finishes it |
| --- | --- | --- |
| `removed` | Taken out of OSDU with a soft delete (the record scope, which OSDU can undo) and marked removed in the ledger. | yes |
| `already-gone` | OSDU no longer held it when the removal reached it; the ledger was settled as for a removal. | yes |
| `settled` | The ledger held it and OSDU held nothing of it for the flow (never delivered, or already removed): its queued work was dropped and it was marked removed, nothing asked of OSDU. | yes |
| `kept-out` | Only the record table held it: no record, nothing in OSDU; it is kept out, so no run delivers it. | yes |
| `referenced` | Left in OSDU: other records there still name it (`keepReferenced`), found by OSDU's search for records mentioning its id, less the ones this run takes out. | no |
| `shared` | Left in OSDU: another ledger of the partition holds the same id live, so it is that ledger's to keep. | no |
| `busy` | A worker was sending it, or another removal held it. | no |
| `failed` | OSDU refused or failed the removal, its reference check could not be asked, or what an unfinished delivery of it left could not be undone yet; the detail says which. | no |

A finishing outcome makes the deletion `removed`, with the run that finished it; any other leaves it `pending`, with the
outcome and why, for the next run. Every try is counted (`Tries`). What unfinished deliveries of the records left in OSDU is
undone first, through the one undo path, and once the records are out, what their deliveries minted (datasets, content,
outputs) goes reversibly too, as when the ledger is deleted. Each record taken out or settled gets an attempt of phase
`source-removed` naming the removal flow, the run, why its source no longer holds it and the file and row that said so, and
the removal's records are named under one `source-remove` activity on the delivery ledger's audit trail. The delivery pipeline's
`cache.after` step runs once records were taken out.

## The ledger's deletions

[`osdu.RemovedKey`](../tables/removed-key.md) holds one row per key, under the delivery flow's ledger identity:

| State | What it means |
| --- | --- |
| `awaiting-approval` | Found by a run its guard held: nothing is done with it, and nothing is kept out, until it is approved. |
| `pending` | To be taken out: the next remove run takes it. |
| `removed` | Taken out, settled or kept out: never asked of OSDU again. |

While a deletion is `pending` or `removed`, the delivery flow's plan delivers no row of the key that is not newer than its
moment (`UpdatedDate_DW` at or before `RemovedUtc`, or no change stamp at all); it skips the row as removed by its source
([Change detection](../concepts/change-detection.md)). A row newer than the moment is delivered as any other, and the next
removal run lifts the deletion. The deletions are read, a page at a time in key order, on the Removals page, on the delivery
flow's **Deletions** tab, through `GET /api/v1/delivery/removals/ledgers/{ledgerFlowId}/keys` and with
`sqlflow records removed`; a record's page says when its key is removed, by which flow, and why.

## Delivering a record again

A deletion is lifted on its own when the source holds the key again or a newer row of it was delivered
([What a run finds](#what-a-run-finds)). An operator lifts one at any time: **Deliver again** on the record's page, a
selection of up to 1,000 on a deletions grid, `POST /api/v1/delivery/flows/{pipelineId}/lift-removal` or
`sqlflow records lift-removal`. The deletion goes; a record the removal took out is asked to be planned again, so the
delivery flow's next run (or the deliver run the lift queues, when asked) reads its row and delivers it under its id; a
record not taken out yet stays as it is. Every record lifted is named under one `lift-removal` activity. While the source
still does not hold the key, the removal flow's next run finds it removed again: bring the key back in the source to keep
the record.

## In the GUI

- The **Removals** page (OSDU, Ledger) lists every removal of the partition picked in the title bar, with its delivery
  flow, its ledger's deletions by state and its last run. A removal opens under a compact heading: what it reads and
  compares under, its last run, and a line saying what waits for approval with **Review** and **Approve**; under it, the
  ledger's deletions as a grid that fills the window and reads more as it is scrolled, by state, or the removal's runs.
- A removal flow's **Removals** tab lists its removals with an **Approve** button for the held ones and **Open**.
- A delivery flow's **Deletions** tab shows its ledger's deletions and the removal runs that wrote to it.
- A record's page says when its key was removed, by which removal flow and why, with **Deliver again**, and its timeline
  shows the `source-removed` attempt.

## Lineage

A removal flow reads its key sources (the files a reader reads, the tables of a source database) and the OSDU records each
delivery flow it names writes, so lineage runs it after the flows that land the keys and after those deliveries. It writes
nothing another flow reads.
