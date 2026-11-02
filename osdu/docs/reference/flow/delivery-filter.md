---
id: delivery-flow-delivery-filter
title: "Narrowing the rows a delivery reads (source.record.filter, source.datasets.<name>.filter): rows marked deleted, drafts"
type: flow-reference
summary: "A filter is one SQL predicate a delivery appends to every read of its record table or a child dataset: rows it leaves out are never read, planned or sent."
keywords:
  - filter
  - source.record.filter
  - source.datasets.filter
  - record.filter
  - narrow the rows read
  - leave out deleted rows
  - DeletedDate_DW IS NULL
  - rows marked deleted
  - never push deleted rows
  - skip draft rows
  - where clause on the record table
  - filter on a child dataset
  - a filter is one predicate
  - leading AND
yamlPath: "source.record.filter, source.datasets.<name>.filter (flowType: delivery)"
related:
  - delivery-flow-delivery
  - delivery-flow-delivery-record-table
  - delivery-flow-removal
  - delivery-flow-removal-marks
  - delivery-concept-change-detection
  - delivery-concept-removal-and-reversal
  - delivery-wiki-0043-removal-marks-the-ingestion-table
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Source/SourceFilter.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/ScopeReads.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceSync.cs
  - osdu/docs/census/keys.delivery.json
---

# Narrowing the rows a delivery reads

A delivery flow reads every row of its record table in its scope, and every child row of each record. A `filter` narrows
that: one SQL predicate the flow appends to every read of the record table (`source.record.filter`), or of one child
dataset (`source.datasets.<name>.filter`). A row the filter leaves out is read by no run of the flow: it is never planned,
held or sent, as a row of another scope is not.

The usual one leaves out the rows the ingestion table marks deleted. SQLFlow's key match marks them
(`matchKeys`, [SQLFlow's deleted-row detection](../../../../sqlflow/docs/reference/flow/ing-load.md)), and so does a
[removal flow](removal-marks.md), which takes their records out of OSDU and records each in the ledger:

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
  datasets:
    curves:
      object: OsduData.arc.WellLogCurve
      join:
        source_project: source_project
        log_id: log_id
      orderBy: [curve_ordinal]
      filter: AND [curve_ordinal] > 0
  lastModified: update_date
  work: ../.work/welllog/{logSource}
```

Without it, a row marked deleted is still read and held, never sent ([change detection](../concepts/change-detection.md#what-is-decided-for-each-record-read)),
and a child row marked deleted is always left out of its record's document, filter or not.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `record.filter` | string | none | A predicate over the record table's columns, appended to the read's WHERE as written, so it carries its own leading `AND`: `AND DeletedDate_DW IS NULL`, `AND [status] <> 'draft'`, `AND EXISTS (SELECT 1 FROM OsduData.arc.Region g WHERE g.region_id = region_id)`. |
| `datasets.<name>.filter` | string | none | A predicate over the child table's columns, the same way: a child row it leaves out is in no record's document. |

In a source with interfaces each interface's `record` and `datasets` take their own (`interfaces.<name>.record.filter`,
`interfaces.<name>.datasets.<name>.filter`).

## What a filter may be

A filter is one predicate. The flow is refused when it loads, naming the key and why, for a filter that:

- does not open with `AND`:
  `flow.yaml: source.record.filter is 'DeletedDate_DW IS NULL'; it is appended to the read's WHERE as written, so it carries its own leading AND (AND status = 'active').`
- holds a `;`, which would end the read, or a comment (`--`, `/*`), which would hide the rest of it;
- closes a parenthesis it did not open, or opens one, a string or a quoted name (`[...]`, `"..."`) it never closes;
- is longer than 4000 characters (read a view of the table instead).

A `;` or `--` inside a string (`AND note <> 'a;b'`) is part of the string and is taken. The same rules hold for a removal
flow's [table source](removal.md#top-level-keys) `filter`.

## How it is read

Every read of the flow takes the rows the filter keeps, the table's rows as a derived table of their own
(`FROM (SELECT * FROM <table> WHERE 1 = 1 <filter>) r`), so its column names are the table's own whatever the read joins
to it, and SQL Server still seeks the table's indexes:

- a run's reads, of the whole scope, of the window since the watermark, and of the records it names by key;
- the first records of the scope (`record.maxRecords`), the values a scope parameter offers, the null key check, and the
  staged rows and queries a person runs on the Source tab;
- the child rows of each record, under the dataset's own filter.

A read by key that names a row the filter leaves out finds it outside the run's scope, never gone from the table: the run
log names it, and a [sync](../concepts/removal-and-reversal.md#syncing-the-ledger-with-the-source) puts it on the
record's history once as found outside its scope. A record out of OSDU (`deleted`) whose row the filter leaves out agrees
with its source, so a sync says nothing of it.

A [removal flow](removal-marks.md) reads the record table whole in the removal's scope, whatever the filter keeps, since it
reads the rows' marks and keeps them in step.

## Changing a filter

A filter enters what a scope's watermark is kept against, so the first run after it changed (added, edited or taken out)
reads the scope whole once, as a run after the mapping moved does
([which rows a run reads](../concepts/change-detection.md#which-rows-a-run-reads)): a filter widened meets the rows it now
keeps. The records already delivered whose rows a narrowed filter leaves out stay as they are in OSDU and in the ledger;
taking them out is a removal ([removal flow](removal.md), [removal and reversal](../concepts/removal-and-reversal.md)). A
flow without a filter keeps the watermark it had.
