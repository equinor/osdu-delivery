---
id: delivery-flow-delivery-lookups
title: "Lookup tables joined into a delivery's reads (source.record.lookups, source.datasets.<name>.lookups): a large lookup table without the cache"
type: flow-reference
summary: "Join a lookup table into a delivery's reads: a LEFT OUTER JOIN on its key gives each row its columns as <name>_<column>, with nothing held in memory."
keywords:
  - lookups
  - source.record.lookups
  - source.datasets.lookups
  - record.lookups
  - left outer join
  - join a lookup table
  - large lookup table
  - lookup table too large for the cache
  - out of memory cache
  - lookup columns as row columns
  - <name>_<column>
  - unique index on the lookup key
  - cross-database join
  - azure sql database cross database
  - lookup row changed redeliver
  - mapping overview lookup
yamlPath: "source.record.lookups, source.datasets.<name>.lookups (flowType: delivery)"
related:
  - delivery-flow-delivery
  - delivery-flow-delivery-filter
  - delivery-flow-delivery-record-table
  - delivery-flow-cache-origins
  - delivery-guide-lookup-table-cache
  - delivery-concept-change-detection
  - delivery-concept-mapping-overview
  - delivery-concept-lineage
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.Lookups.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionFingerprint.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryValueCheckEndpoints.cs
  - osdu/gui/src/features/delivery/overview/outlineModel.ts
  - osdu/docs/census/keys.delivery.json
---

# Lookup tables joined into a delivery's reads

A lookup table a mapping translates through (curve mnemonics to curve types, region codes to names) usually goes in the
[partition's cache](cache-origins.md#ingestion-tables-table), which every node holds whole in memory. A table of a few
million rows does not fit there. A delivery flow joins such a table into its own reads instead: `lookups` on the record
table or on a child dataset names the lookup table, its key and the column of the joining table the key matches, and every
read of the table becomes a LEFT OUTER JOIN on that key, inside the statement that already reads the rows. SQL Server does
the join where both tables are; nothing of the lookup table is copied, cached or held by a node, however large it is.

```yaml
source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [log_id]
  datasets:
    curves:
      object: OsduData.silver.WellLogCurve
      join: { log_id: log_id }
      orderBy: [curve_ordinal]
      lookups:
        CurveAlias:                              # prefixes the columns it gives: CurveAlias_curve_type, CurveAlias_unit
          object: OsduData.silver.CurveAlias
          key: source_mnemonic                   # the lookup table's key, kept unique by an index
          on: curve_mnemonic                     # the column of WellLogCurve the key matches
          columns: [curve_type, unit]
```

Each curve row is then read as if `WellLogCurve` held two more columns, and the mapping reads them as it reads the row's own:

```yaml
Curves:
  $forEach: curves
  CurveID:
    $from: curve_mnemonic
  CurveUnit:
    $from: CurveAlias_unit
    $required: false
```

A row whose value names no lookup row, or one the lookup table's ingestion flow marked deleted, has the lookup's columns
empty, and the mapping's `$required` decides whether that holds the record. Nothing is a cache lookup here: there are no
`$findBy` fallback lines, no second comparison ignoring case, and no change tags waiting for approval. The key is compared
as SQL Server compares it, under its own collation, which for text usually ignores case.

## The keys

| Key | Required | Meaning |
| --- | --- | --- |
| `lookups.<name>` | | One lookup table. The name is letters, digits and `_`, starting with a letter, and prefixes every column it gives a row (`<name>_<column>`). A table joins at most 16. |
| `lookups.<name>.object` | yes | The lookup table's three-part name, in the database of the table that joins it. |
| `lookups.<name>.key` | yes | The lookup table's key column. A unique index without a filter must keep it to one row per value. |
| `lookups.<name>.on` | yes | The column of the joining table whose value the key matches. |
| `lookups.<name>.columns` | yes | The lookup table's columns each row is given, at least one, each once. |

`source.record.lookups` joins into the record table's reads, so each record row is given the columns;
`source.datasets.<name>.lookups` joins into a child dataset's, so each child row is. An interface's `record` and `datasets`
take `lookups` the same way. Each row is also given the lookup table's update column, as `<name>_UpdatedDate_DW`: what a
run reads a moved lookup row by ([below](#when-a-lookup-row-changes)).

## What a run checks before it reads a row

When a run opens the source, every lookup is checked against the tables themselves; one the join cannot rely on stops the
run before anything is read or sent, naming what to change.

| Refusal | Why |
| --- | --- |
| `... names <table>, in database <db>, and <joining table> is in <other db>. ... no statement reads across two databases (Azure SQL Database runs none); load the lookup table into <db>.` | The join is one statement, over the flow's own connection. |
| `... names column '<key>' of <table>, which no unique index without a filter keeps to one row per value, ... CREATE UNIQUE INDEX ...` | Two lookup rows under one key would read the row twice. The ingestion flow's `load.keyColumns` creates the index; the message gives the statement for a table without one. |
| `... holds no UpdatedDate_DW date and time column: ...` | Without it, a corrected lookup value would never reach the records already delivered. A SQLFlow ingestion flow stamps it. |
| `... matches <table>'s column '<on>' (<type>) with the key '<key>' of <table> (<type>): the two are compared as they are, ...` | The key and `on` must be two text columns (compared under the key's collation), two whole numbers, or of one type, so the key's index is sought and no value fails a conversion. |
| `... gives each row column '<name>_<column>', which the row already has, ...` | The joining table, or another of its lookups, already has a column of that name; rename the lookup. |
| `... names column '<column>', which the table <table> does not hold. Columns: ...` | A misspelled `key`, `on` or column; the message lists the table's columns. |

What the document itself must say (a name of letters, digits and `_`, a three-part `object`, a `key`, an `on` and at
least one column, no name or column twice) is checked when the flow is loaded, by `sqlflow validate` and the repository
sync.

## When a lookup row changes

A run reads the rows that changed since its watermark ([which rows a run reads](../concepts/change-detection.md#which-rows-a-run-reads)).
A lookup table changes on its own schedule, so a run also reads every record joined to a lookup row that its ingestion
flow wrote or marked deleted in the window: through the record row for a record table's lookup, through any of its child
rows for a dataset's. The record's ingestion fingerprint takes in each joined lookup row's update moment, so such a record
reads as moved and renders again rather than being skipped; the document is sent only if it changed. A flow without
`lookups` keeps the fingerprint, and the document hash, it always had.

A lookup row deleted outright (rather than marked) leaves no trace a window can read; such a change reaches the records on
the scope's next whole read.

## How fast it is

The join is one statement with the read, so it costs no round trip and no memory, and the key's unique index makes each
row's lookup one seek. SQL Server can leave the join out of a read that takes none of a lookup's columns (a page of keys, a
count), which the unique index allows. Two indexes keep a large estate fast: one on `on` in the joining table, for the
window's search for records joined to a moved lookup row, and one on the lookup table's `UpdatedDate_DW`, for finding the
rows that moved.

## Where to see it

The [mapping overview](../concepts/mapping-overview.md) shows a column a lookup gives with a link mark in the table it is
read from, its tooltip naming the lookup table and the key it is found by, and the table's details list its lookups. A look
at the table's rows shows the columns with their values. `GET /api/v1/delivery/flows/{id}/source-tables` lists each table's
`lookups`, and the columns each gives after the table's own.

[Lineage](../concepts/lineage.md#what-each-kind-reads-and-writes) shows the flow reading each lookup table, so the
ingestion flow that loads one is ordered before the delivery that joins it, as the one loading the record table is.
