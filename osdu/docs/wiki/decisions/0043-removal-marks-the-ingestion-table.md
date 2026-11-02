---
id: delivery-wiki-0043-removal-marks-the-ingestion-table
title: "Decision 0043: a removal flow extends SQLFlow's key match, and a delivery leaves marked rows out with a filter"
type: decision
summary: "Why a removal reads the ingestion table's deleted marks as removed keys and marks what it takes out, and why a delivery narrows its reads with a filter."
keywords:
  - removal marks the ingestion table
  - DeletedDate_DW
  - matchKeys results
  - source.record.filter
  - never push deleted rows
  - deleted row comes back
  - re-emergence
updated: 2026-11-02
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceFilter.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - sqlflow/src/SqlFlow.SqlServer/Schema/MatchKeyGenerator.cs
referenceRefs:
  - delivery-flow-removal-marks
  - delivery-flow-delivery-filter
  - delivery-flow-removal
  - delivery-concept-change-detection
  - flow-ing-load
related:
  - delivery-wiki-0022-removal-flows
---
# 0043: A removal flow extends SQLFlow's key match, and a delivery leaves marked rows out with a filter

Status: accepted. Documented in [the ingestion table's marks](../../reference/flow/removal-marks.md) and
[narrowing the rows a delivery reads](../../reference/flow/delivery-filter.md). Amends
[0022](0022-removal-flows.md).

## Context

An estate's ingestion flow marks the rows whose key left its source (SQLFlow's key match stamps `DeletedDate_DW`), and
the human wanted those rows never delivered, their records deleted from OSDU, the ledger saying so, and a row that comes
back delivered again as a new record. What existed did each part apart, and none of them together:

- A delivery read every marked row and held it, blocked, as an issue for an operator, while OSDU kept the record live.
- A removal flow took records out of OSDU and kept each deletion in the delivery ledger (0022), but only from the
  source's keys or its list of removed keys. The ingestion table's marks meant nothing to it, and nothing it did reached
  the table, so the delivery went on reading and holding the rows it had removed.
- SQLFlow's key match takes a mark off a key that reappears without stamping the row, so no incremental read met the
  row again, and the record stayed held until an operator released it.
- A delivery had no way to leave rows out of its reads: `source.record` and `source.datasets` took no filter.

The human set the direction: a delivery flow takes a filter, so marked rows are never selected; a removal flow is the
key match extended, which uses the marks to remove, compares the source's keys with the ingestion table and marks what
it removes; a row already removed is never removed again, by cross-matching with the ledger; and the ledger, keyed on the
business key, carries the record on when its row comes back.

## Decision

- **A delivery takes a filter.** `source.record.filter` and `source.datasets.<name>.filter` are one SQL predicate each,
  with its own leading `AND`, appended to every read of the table as a derived table of its own, so its columns are the
  table's whatever the read joins. It is checked when the document loads (no `;`, no comment, no parenthesis or quote
  it does not close), and a changed filter moves the scope's watermark, so the next run reads the scope whole once.
- **The marks are removed keys.** A removal reads the record table whole in its scope, whatever the filter keeps, with
  each row's mark, and a marked row is a removed key (`marked`, as of the mark) unless the source's keys, read after the
  mark, hold it. `keys` and `removed` become optional: a removal of the marks alone uses the key match's results.
- **The removal keeps the marks in step.** After comparing, and before taking anything out, it marks the rows whose key
  holds a pending or removed deletion, and unmarks a marked row whose key holds no deletion and is back, stamping its
  update column. Only rows that disagree are written.
- **A key that comes back lifts its deletion.** Besides the source holding the key again and the record delivered again,
  a deletion goes when its row was loaded again after its moment and not marked since, when a deletion found from a mark
  no longer has one, or when an operator lifts it, which unmarks the row too. The record keeps its delivery key and OSDU
  id; the removal forgot what OSDU held of it, so its row is sent whole, as a new record under the same id.
- **A row loaded after its mark is back.** The plan holds a marked row only when it was not loaded again after its mark,
  since the key match and a removal both unmark a key that comes back.
- **The ledger's counts say what was written.** A removal run keeps `Marked` and `Unmarked`, and a sync counts a
  record out of OSDU whose row is gone or filtered out as agreeing with its source.

## Rejected

- **A delivery that removes what it reads marked.** It would take records out of OSDU as a side effect of a read, with
  no guard and no run of its own; 0022's reasons for a flow of its own stand.
- **A filter for deleted rows alone (`skipDeleted: true`).** The filter is general, and the deleted case is one line of it;
  a second switch for one predicate would be two ways to say the same thing.
- **A filter written into each read's WHERE beside the scope.** Its unqualified columns would be ambiguous in the reads
  that join a child table or the named keys; a derived table keeps them the table's own.
- **Leaving the marks to the ingestion flow.** An incremental lake load cannot mark what it did not load (0022), and a
  filter that leaves marked rows out would then never see a removal flow's removals in the table.
- **Changing SQLFlow's key match to stamp the update column when it unmarks.** The removal lifts a deletion whose mark is
  gone and asks the record to be planned, which reads it by key whatever its stamp; the engine stays as it is.

## Consequences

- A removal writes to the ingestion table, through the delivery flow's source connection, which needs `UPDATE` on it; a
  refused write fails the removal before anything is taken out.
- An operator's lift unmarks the row from the control plane, which may not reach the record table. The lift stands
  whatever the write comes to, as it did before marks existed, and its answer says when the row keeps its mark: failing it
  would take away a lift that worked before, for a mark the removal flow's next run would find anyway.
- The key match and a removal flow both write the mark. They agree while their sources agree; where they do not, each
  undoes the other until they do, so a record table wants one source of truth.
- A record table without the soft-delete column is read and removed from as before, and gives a removal of the marks
  alone nothing to read, which is refused.
- A flow without a filter keeps its watermark, its hashes and its reads exactly as they were.
