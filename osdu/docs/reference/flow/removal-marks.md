---
id: delivery-flow-removal-marks
title: "The ingestion table's marks: a removal flow reads the rows marked deleted as removed keys, and marks and unmarks them"
type: flow-reference
summary: "How a removal flow extends SQLFlow's matchKeys: rows marked deleted are removed keys, the rows it takes out are marked, and a key that comes back is unmarked."
keywords:
  - DeletedDate_DW
  - deleted mark
  - soft delete mark
  - mark rows deleted
  - unmark
  - tag deleted rows
  - matchKeys results
  - key match
  - removal from the ingestion table
  - marked
  - removal reads no source
  - row comes back
  - data comes back
  - re-emerged record
  - delivered again as a new record
  - Marked
  - Unmarked
yamlPath: "removals[] (flowType: removal)"
related:
  - delivery-flow-removal
  - delivery-flow-delivery-filter
  - delivery-flow-delivery
  - delivery-concept-removal-and-reversal
  - delivery-concept-change-detection
  - delivery-table-removed-key
  - delivery-table-removal-key
  - delivery-table-removal-run
  - delivery-wiki-0022-removal-flows
  - delivery-wiki-0043-removal-marks-the-ingestion-table
  - flow-ing-load
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SourceRemovals.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Documents/RemovalDocumentMapper.cs
  - sqlflow/src/SqlFlow.SqlServer/Schema/MatchKeyGenerator.cs
---

# The ingestion table's marks

SQLFlow's key match marks an ingestion table's row deleted when its key leaves the source: it stamps the soft-delete
column (`DeletedDate_DW`, and `RowStatus_DW` `'D'` when the table carries it), and takes the mark off when the key
reappears, stamping `UpdatedDate_DW` as it stamps a row it updates
([SQLFlow's matchKeys](../../../../sqlflow/docs/reference/flow/ing-load.md)). A removal flow extends it to OSDU.
On the delivery flow's record table, when the table carries the soft-delete column (`source.systemColumns.deleted`,
`DeletedDate_DW` unless the delivery flow names another or opts out with `~`), a removal:

1. **reads the marks as removed keys**: a row marked deleted is a key the source removed, as of the moment of its mark,
   whoever marked it;
2. **marks the rows it removes**: a row whose key holds a `pending` or `removed` deletion and carries no mark is marked,
   before anything is taken out of OSDU;
3. **unmarks a row that came back**, and stamps its update column, so the delivery flow's next incremental read meets it.

The marks and the ledger's deletions then say the same thing, so a delivery flow can leave marked rows out of every read
with a [filter](delivery-filter.md), `filter: AND DeletedDate_DW IS NULL`, and never read, hold or send a deleted row.

## A removal of the marks alone

`keys` and `removed` are optional. A removal that declares neither takes its removed keys from the marks, which suits an
ingestion flow that runs SQLFlow's key match itself:

```yaml
flowType: removal
name: welldb-welllog-05-removal
partitions: [dev]

removals:
  - flow: welldb-welllog-03-header-delivery
    values: { logSource: WIRELINE }
```

A delivery flow whose record table carries no soft-delete column gives such a removal nothing to read, and the removal
fails before it compares anything:
`removals[0] declares neither keys nor removed, so the record table's deleted marks are its only source of removed keys, and <table> carries no soft-delete column (source.systemColumns.deleted of '<flow>'). Declare the keys the source holds now (keys), the keys it lists as removed (removed), or mark the table's rows deleted with SQLFlow's key match (matchKeys in its ingestion flow).`

## A mark as a removed key

The removal reads the record table whole in its scope, whatever the delivery flow's filter keeps, each row with when it
last changed and when it was marked. A marked row is found removed (reason `marked`, its deletion's file the record table)
as of its mark, unless:

- `keys` is read after the mark and holds the key (the source holds it again), or
- the row was loaded again after its mark (it came back, counted as such).

A removed list and the current keys say first: a key `removed` lists is `listed`, one `keys` lacks is `absent`, and only
a key neither says is gone is `marked`. A key the ledger already holds a deletion of is not found again, and one taken out
is never asked of OSDU again ([What a run finds](removal.md#what-a-run-finds)): the run cross-matches the marks with the
ledger, so marks written by many runs of the key match are each taken out once. The [guard](removal.md#the-guard) counts
what the marks found with the rest.

## Marking and unmarking

A `remove` run, once it has compared and before it takes anything out, pages through the rows whose mark disagrees with
the ledger's deletions, 5,000 a statement, and writes only those:

| The row | What the run writes |
| --- | --- |
| Not marked, and its key holds a `pending` or `removed` deletion | `DeletedDate_DW` set to the database's UTC clock, as the key match stamps it; a row-status column is left as it is |
| Marked, its key holds no deletion, and the key is back: `keys` read after the mark holds it, or the row was loaded after its mark | `DeletedDate_DW` cleared and `UpdatedDate_DW` stamped |
| Anything else | Nothing |

A deletion waiting for approval marks nothing until it is approved. A mark no deletion explains and nothing says is back
(the key match marked a key the removal's sources do not read) is left as it is. A `plan` run marks nothing. The run counts
what it marked and unmarked (`Marked`, `Unmarked` on [the removal run](../tables/removal-run.md)), shown under **Marks** on
the Removals page and by `sqlflow removal runs` ([removal runs](../cli/removal.md#removal-runs)).

The marks are written through the delivery flow's own source connection, which then needs `UPDATE` on the record table. A
write the database refuses fails the removal before anything is taken out, naming the table and the SQL error; the
deletions it found stay `pending` for the next run.

## A key that comes back

A deletion is lifted, its key no longer kept out and a record taken out of OSDU asked to be planned again, when:

- the source holds the key again (`keys`, read after the deletion's moment);
- the record table's row was loaded again after the deletion's moment and has not been marked since;
- the deletion was found from a mark, and the table no longer marks the row (the key match took the mark off);
- the record was delivered again since the deletion was made; or
- an operator lifts it (**Deliver again**), which also unmarks the row ([Delivering a record again](removal.md#delivering-a-record-again)).
  When the record table cannot be reached or written from where the lift runs (the control plane, for **Deliver again**), the lift stands and its answer says the rows keep their mark and why (`marksKept`), as its activity does: the delivery flow's filter then leaves the row out, and the removal flow's next run finds the mark and takes the record out again.

The same run unmarks a row still marked and stamps it, so the delivery flow's filter lets it through, and its next run
reads it by key. A row SQLFlow's key match took the mark off is back for the delivery flow before any removal run: the
key match stamps the row too, so the delivery flow's next run reads it, finds it newer than its deletion's moment and
delivers it again, and the removal flow's next run lifts the deletion the record outlived. In the order lineage runs
them (the ingestion flow, the delivery, the removal), a key the key match finds back is delivered again in the same run
of the chain, and a key the removal finds back (its `keys`, or a row loaded again while still marked) by the delivery's
run after it.

Either way the record keeps its delivery key and its OSDU id, since both come from the business key, and its history
runs on: delivered, `deleted` by the removal (attempt `source-removed`), then delivered again. The removal forgot what OSDU held
of it, so the row is sent whole, as a new record under the same id, as a record removed at the record scope is when its
row is delivered again ([removal and reversal](../concepts/removal-and-reversal.md)).

A delivery flow without a filter reads a marked row and holds it, unless the row was loaded again after its mark: a row
loaded after its mark is back, and is decided as any other ([change detection](../concepts/change-detection.md#what-is-decided-for-each-record-read)).

## Checking that a record is gone

A verify run of the delivery flow checks each record taken out to be gone from OSDU (`gone`, or **still in OSDU** when OSDU
holds a record at its id), and a removed record's **OSDU** tab asks OSDU for its id
([Check that OSDU no longer holds a record](../guides/rows-deleted-in-the-source.md#check-that-osdu-no-longer-holds-a-record)).

## SQLFlow's key match beside a removal

Both write the same column, and they agree as long as their sources do: a key the ingestion flow's source lacks is one
the removal's sources lack. Where they disagree, the last to run wins until the other runs again: a removal whose `keys`
holds a key unmarks the row the key match marked, and the key match marks it again. Give a record table one source of
truth: the removal's `keys`, the ingestion flow's key match read by a removal of the marks alone, or both reading the same
source.
