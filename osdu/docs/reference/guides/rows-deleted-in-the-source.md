---
id: delivery-guide-rows-deleted-in-the-source
title: "Rows deleted in the source: never delivered, taken out of OSDU with the ledger saying so, and delivered again as new when they come back"
type: guide
summary: "Set up the ingestion, removal and delivery flows so a row the source deletes is never sent, its record leaves OSDU, and a row that comes back is new."
keywords:
  - deleted rows
  - rows deleted in the source
  - never push deleted rows
  - delete from osdu when the source deletes
  - DeletedDate_DW
  - matchKeys
  - key match
  - removal flow
  - filter deleted rows
  - source.record.filter
  - mark deleted
  - row comes back
  - data comes back
  - re-emergence
  - deleted then restored
  - held as source-deleted
related:
  - delivery-flow-removal-marks
  - delivery-flow-delivery-filter
  - delivery-flow-removal
  - delivery-flow-delivery
  - delivery-concept-removal-and-reversal
  - delivery-concept-record-lifecycle
  - delivery-concept-change-detection
  - delivery-cli-removal
  - delivery-cli-records
  - delivery-guide-operations-runbook
  - delivery-wiki-0043-removal-marks-the-ingestion-table
  - flow-ing-load
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceSync.cs
  - sqlflow/src/SqlFlow.SqlServer/Schema/MatchKeyGenerator.cs
---

# Rows deleted in the source

When a source deletes a row, three things should follow, and one more when it comes back:

1. the row is never delivered again;
2. its record is taken out of OSDU, reversibly;
3. the ledger says so, with who, when and why;
4. a row that comes back is delivered again, as a new record under the id it had, and the record's history runs on.

Three flows do it together, each with one job:

| Flow | Its job here |
| --- | --- |
| The ingestion flow (`flowType: ing`) | Loads the rows into the record table, which carries the soft-delete column `DeletedDate_DW`. It may mark rows itself with SQLFlow's key match. |
| The [removal flow](../flow/removal.md) (`flowType: removal`) | Finds the keys the source removed, marks their rows in the record table, takes their records out of OSDU and writes each deletion to the delivery ledger; unmarks a row whose key comes back. |
| The delivery flow (`flowType: delivery`) | Reads only the rows not marked, with a [filter](../flow/delivery-filter.md), so a deleted row is never read, held or sent. |

The mark in the record table and the deletion in the ledger then say the same thing, and the [ingestion table's
marks](../flow/removal-marks.md) page says exactly how a removal keeps them in step.

## Pick how removals are found

How the removal flow learns that a key is gone depends on how the ingestion flow loads:

| The ingestion flow | Removals are found by | The removal flow declares |
| --- | --- | --- |
| Loads the source whole every run | SQLFlow's key match in the ingestion flow, which marks the rows whose key left the source and unmarks them when it comes back | Neither `keys` nor `removed`: it reads the marks |
| Loads files incrementally (a lake, new files only) | A full read of the source's keys, which only the removal flow can make (an incremental load cannot tell a dropped key from one not in this load) | `keys`, the source's complete key set; or `removed`, when the source keeps a list of what it deleted |

Either way the record table needs the soft-delete column: SQLFlow adds `DeletedDate_DW` with the ingestion flow's
`systemColumns.deletedDate: true`, and turns it on by itself for `matchKeys` with `action: tag`
([SQLFlow's ingestion flow](../../../../sqlflow/docs/reference/flow/ing-load.md)).

## Set up a source loaded whole

The ingestion flow marks rows with the key match:

```yaml
flowType: ing
name: welldb-welllog-02-header-ing
connections:
  osduData: ${env:OSDU_DATA_DB}
source:
  server: osduData
  object: OsduData.pre.v_WellLog
target:
  server: osduData
  object: OsduData.arc.WellLog
  identityColumn: RecId
load:
  keyColumns: [source_project, log_id]
  matchKeysInSourceAndTarget: true
matchKeys:
  action: tag
  thresholdPercent: 20
```

The removal flow reads the marks alone:

```yaml
flowType: removal
name: welldb-welllog-05-removal
partitions: [dev]
removals:
  - flow: welldb-welllog-03-header-delivery
    values: { logSource: WIRELINE }
schedule: welldb-removal
```

## Set up a source loaded incrementally

The ingestion flow only carries the column (`systemColumns.deletedDate: true`), and the removal flow reads the source's
keys whole every run, from the same files the pre flow lands:

```yaml
flowType: removal
name: welldb-welllog-05-removal
partitions: [dev]
removals:
  - flow: welldb-welllog-03-header-delivery
    values: { logSource: WIRELINE }
    keys:
      type: csv
      location: ../data/welllog
      options:
        delimiter: ","
        header: true
        srcFile: "*.csv"
schedule: welldb-removal
```

The marks are read too, so a row someone else marked is also found; the [removal flow](../flow/removal.md) page has the
table source form, `removed` lists and the `values` a scoped delivery flow needs.

## Leave marked rows out of the delivery

In both set-ups the delivery flow adds one line to its record table:

```yaml
source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.arc.WellLog
    key: [source_project, log_id]
    primaryKey: RecId
    scope:
      log_source: logSource
    filter: AND DeletedDate_DW IS NULL
```

Without the filter, a marked row is still read and held as `source-deleted`, blocked, until the removal flow takes its
record out; with it, the row is never read. A child row marked deleted is left out of its record's document either way.
The first run after the filter is added reads the scope whole once
([changing a filter](../flow/delivery-filter.md#changing-a-filter)).

Schedule the removal flow after the deliveries it compares, as lineage orders it, and give the delivery flow's source
connection `UPDATE` on the record table: the removal writes the marks through it.

## What happens when a row is deleted

| When | The record table | The ledger | OSDU |
| --- | --- | --- | --- |
| The source drops the key | The key match marks the row (source loaded whole); or nothing yet (incremental) | The record stays `delivered` | The record is live |
| The removal flow runs | Marks the row, when the key match has not | A deletion of the key, `pending`, reason `marked`, `absent` or `listed`, with its moment | |
| The same run takes it out | | The record `deleted`, an attempt `source-removed` naming the removal flow, its run, why and as of when; the deletion `removed` | Soft deleted (the record scope), reversible |
| The delivery runs | The filter leaves the row out | Nothing is planned, held or sent | |
| A sync runs | | The record agrees with its source: nothing is written | |
| The removal flow runs again | | The key holds a deletion: it is not found again, and OSDU is asked nothing | |

A run that finds more removed than its [guard](../flow/removal.md#the-guard) allows (20% of what it compares by default)
marks and takes out nothing of them, and waits for an operator to approve the count on the Removals page.

## What happens when a row comes back

| When | The record table | The ledger | OSDU |
| --- | --- | --- | --- |
| The key is back in the source | The key match unmarks the row (source loaded whole), or the ingestion flow loads the row again | | |
| The removal flow runs | Unmarks the row, when still marked, and stamps `UpdatedDate_DW` | The deletion is lifted, and the record asked to be planned again | |
| The delivery runs | The filter lets the row through | The record is planned by key and delivered: its earlier hashes were forgotten at the removal, so it is sent whole | Written again under the same id, as a record removed at the record scope is when its row is delivered again ([removal and reversal](../concepts/removal-and-reversal.md)) |

The record keeps its delivery key and its OSDU id, both derived from the business key, so the ledger carries one record
whose history runs: delivered, `deleted` by the removal flow, delivered again. An operator can bring a record back before
its key returns with **Deliver again** on its page, which lifts the deletion and unmarks the row; while the source still
lacks the key, the removal flow's next run finds it removed again. The control plane unmarks the row through the delivery
flow's source connection: when it cannot reach the record table, the lift still stands and says the row keeps its mark.

## Where to see it

- The **Removals** page: each removal's deletions by state, and its runs, with what each read, found, took out, saw come
  back and marked or unmarked (**Marks**). `sqlflow removal runs <removal.yaml>` prints the same
  ([removal runs](../cli/removal.md#removal-runs)).
- The delivery flow's **Deletions** tab, and `sqlflow records removed <flow.yaml>` ([records removed](../cli/records.md#records-removed)).
- A record's page: when its key was removed, by which removal flow and why, its `source-removed` attempt on the timeline,
  and **Deliver again**.

## When something is off

| What you see | Why, and what to do |
| --- | --- |
| Records held as `source-deleted` | The delivery reads marked rows: add `filter: AND DeletedDate_DW IS NULL`, and run a removal flow, which reads the marks and takes their records out of OSDU. |
| `removals[0] declares neither keys nor removed, so the record table's deleted marks are its only source of removed keys, and ... carries no soft-delete column ...` | The record table has no `DeletedDate_DW`: turn on the ingestion flow's `systemColumns.deletedDate` or its key match, or declare `keys` or `removed`. |
| A removal fails with `the source database refused to mark rows deleted for ...` | The delivery flow's source connection may not update the record table. Grant `UPDATE`; the deletions it found wait, `pending`, for the next run. |
| A row is marked, unmarked and marked again, run after run | The ingestion flow's key match and the removal's `keys` read different sources and disagree about the key. Keep one source of truth ([SQLFlow's key match beside a removal](../flow/removal-marks.md#sqlflows-key-match-beside-a-removal)). |
| A run waits for approval | More was found removed than the guard allows: check the source read (a truncated export, a wrong folder), then approve the count on the Removals page, or correct the source. |
