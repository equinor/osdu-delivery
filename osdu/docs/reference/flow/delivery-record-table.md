---
id: delivery-flow-delivery-record-table
title: "The record table a delivery reads (source.record): one row per record, a unique key index and an identity primary key, made by its ing flow"
type: flow-reference
summary: "What a delivery's record table must hold and the ing flow keys that make it: a unique unfiltered key index with record.primaryKey, no SCD2; each refusal's fix."
keywords:
  - record table
  - source.record
  - source.record.key
  - source.record.primaryKey
  - unique index without a filter
  - has no unique index without a filter
  - NCI_KeyColumn
  - load.keyColumns
  - target.identityColumn
  - unique key index
  - record held by two rows
  - duplicate record key
  - SCD2 record table
  - versioning.scd2
  - write an ing flow for a delivery
  - ing flow and delivery flow together
yamlPath: "source.record (flowType: delivery)"
related:
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-concept-preflight
  - delivery-concept-submissions
  - delivery-guide-getting-started
  - flow-ing
  - flow-ing-versioning
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - sqlflow/src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs
  - sqlflow/src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
---

# The record table a delivery reads

A delivery flow reads one table holding one row per record, `source.record.object`, which an ingestion flow
(`flowType: ing`) loads. A run checks the table against the flow as it opens it, and stops before it reads a row when the
table does not hold what the flow declares; `sqlflow check --connect`, `sqlflow preview`, `sqlflow values` and the
mapping builder's data check make the same checks ([preflight](../concepts/preflight.md#the-source-tables)).

Write the ing flow and the delivery flow together. The ing flow creates the key index and the identity primary key the
delivery relies on, and it creates them only on the run that creates the table.

## What the table needs

| The table holds | The delivery flow names it | The ing flow makes it |
| --- | --- | --- |
| One row per record | `source.record.object` | Loaded without `versioning.scd2`, which keeps a row per version ([a table that keeps history](#a-table-that-keeps-history)). |
| The key columns, each a comparable type (text with a length, a number, a date, a `uniqueidentifier`), null in no row of the run's scope | `source.record.key`: the same columns, in the same order, as the mapping's `dataset.key` | `load.keyColumns`, naming the same columns. |
| A unique index without a filter on exactly the key columns, when the flow sets `record.primaryKey` | Nothing: the run checks it ([the unique key index](#the-unique-key-index)) | `load.keyColumns` creates it, `NCI_KeyColumn`, on the run that creates the table. |
| An integer identity column that is the table's single-column primary key | `source.record.primaryKey` | `target.identityColumn: RecId`, on the run that creates the table ([the identity primary key](delivery.md#the-identity-primary-key)). |
| A date and time column stamped on every row inserted or changed | `source.systemColumns.updated` (default `UpdatedDate_DW`) | `systemColumns.updatedDate: true`. |

The welldb well log pair names one table and one key. The ing flow:

```yaml
flowType: ing
name: welldb-welllog-02-header-ing
batch: welldb

connections:
  osduData: ${env:OSDU_DATA_DB}

source:
  server: osduData
  object: OsduData.pre.v_WellLog

target:
  server: osduData
  object: OsduData.silver.WellLog
  identityColumn: RecId          # the identity primary key: record.primaryKey

load:
  keyColumns: [log_id]           # the key, and its unique index NCI_KeyColumn: record.key

systemColumns:
  insertedDate: true
  updatedDate: true              # UpdatedDate_DW: systemColumns.updated
```

and the delivery flow's `source.record`:

```yaml
source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [log_id]
    primaryKey: RecId
```

## The unique key index

A delivery split over nodes cuts the rows a run reads into contiguous ranges of the primary key, one per part
([submissions](../concepts/submissions.md#many-nodes)). A key that two rows hold could fall into two ranges, be planned
by two parts and be sent twice. A unique index on the key rules that out, so a flow that sets `record.primaryKey` needs one
on its record table: a unique index or unique constraint, with no filter (no `WHERE`), whose key columns are exactly the
columns of `record.key`, in any order. Its included columns do not count. Without one the run stops with:

```text
<flow file>: source.record.key (log_id) has no unique index without a filter on OsduData.silver.WellLog, so a record could be held by two rows and a fan-out could deal them to two runs. The ingestion flow's load.keyColumns creates one (NCI_KeyColumn); a table that keeps history (SCD2) filters it and cannot be read by ranges of its primary key.
```

The table lacks the index for one of three reasons:

- **The ing flow keys no columns, or other columns.** Set its `load.keyColumns` to the columns of `record.key`. A table
  the ing flow is yet to create gets the index on that run; one that exists already needs it created once, as below.
- **The table existed before the ing flow keyed it by these columns.** SQLFlow creates `NCI_KeyColumn` only on the run
  that creates the table ([canonical indexes](../../../../sqlflow/docs/reference/flow/ing.md#canonical-indexes-on-target-create)),
  so a table an earlier run created without `load.keyColumns`, or with other key columns, keeps what it was given.
  Create the index once, by hand, as below.
- **The ing flow keeps history with `versioning.scd2`.** No index fixes it ([a table that keeps history](#a-table-that-keeps-history)).

To create the index on an existing table, first look for a key that two rows hold, since the index cannot be created over
one. Each key this returns is a record held twice, settled in the source before the index goes on:

```sql
SELECT [log_id], COUNT(*) AS [rows]
FROM [OsduData].[silver].[WellLog]
GROUP BY [log_id]
HAVING COUNT(*) > 1;
```

Then, while nothing loads the table, create the index the ing flow creates with a new table, dropping first an
`NCI_KeyColumn` that names other columns:

```sql
DROP INDEX IF EXISTS [NCI_KeyColumn] ON [OsduData].[silver].[WellLog];
CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON [OsduData].[silver].[WellLog] ([log_id]);
```

## A table that keeps history

An SCD2 table holds a row for every version of a record, so its key is unique only among the current rows: its
`NCI_KeyColumn` is filtered (`WHERE [IsCurrent_DW] = 1`), and every SCD2 load drops an unfiltered unique index on the key,
so one made by hand does not last
([SCD2's key index](../../../../sqlflow/docs/reference/flow/ing-versioning.md#key-index-migration)). A delivery reads
each row of its record table as a record, so the table it reads is loaded without `versioning.scd2`. To keep the history,
use `versioning.temporal`: SQL Server keeps every version in a history table of its own, and the record table keeps one
row per record and its unfiltered key index
([versioning](../../../../sqlflow/docs/reference/flow/ing-versioning.md)).

## Every refusal of the record table

A run checks the table in this order and stops at the first it finds. Each message but the first opens with the flow
file's path.

| The table | The message | The fix |
| --- | --- | --- |
| Is not there, or the node cannot see it | `Flow '<flow>': the table OsduData.silver.WellLog, declared under source.record.object, was not found on the source database, or the identity this node connects with cannot see it. Check the ingestion flow that loads it has run, and that the node's login is granted SELECT on it.` | Run the ing flow, or grant the node's login `SELECT`. |
| Lacks a column the flow names | `source.record.key names column 'log_id', which the table OsduData.silver.WellLog does not hold. Columns: ...` | Name a column the table holds; the message lists them. |
| Has a key column that cannot be compared | `source.record.key names column 'log_id' of OsduData.silver.WellLog, which is nvarchar(max). A key column has to be a comparable type (text with a length, a number, a date, a uniqueidentifier).` | Key by a column of a comparable type. |
| Has a primary key that is not an integer | `source.record.primaryKey names column 'RecId' of OsduData.silver.WellLog, which is nvarchar(20). A read is paged and cut on an integer identity column (the ingestion flow's target.identityColumn).` | Name the identity column `target.identityColumn` created. |
| Has an updated column that is not a date | `source.systemColumns.updated names column 'UpdatedDate_DW' of OsduData.silver.WellLog, which is nvarchar(30). An incremental read windows on a date and time column (SQLFlow's UpdatedDate_DW).` | Name the column the ing flow stamps, `UpdatedDate_DW`. |
| Has a primary key that is not an identity column, or not the table's single-column primary key | `source.record.primaryKey names column 'RecId' of OsduData.silver.WellLog, which is not an identity column. The ingestion flow creates it with target.identityColumn: RecId when it creates the table. An existing table needs it added once, ...` | Add it once with the statement the message gives ([the identity primary key](delivery.md#the-identity-primary-key)). |
| Has no unique index without a filter on the key, with `record.primaryKey` set | `source.record.key (log_id) has no unique index without a filter on OsduData.silver.WellLog, ...` | [The unique key index](#the-unique-key-index). |
| Holds a row in the run's scope with a null key | `the record table OsduData.silver.WellLog holds a row whose key column 'log_id' is null, so that row has no identity to deliver under and could not be told apart from another like it. Give every row a key, or narrow source.record.scope to the rows that have one.` | Give every row a key, or narrow `record.scope`. |

## See also

- [Delivery flow](delivery.md#source): every `source` key.
- [Mapping](mapping.md#dataset-the-record-tables-key-label-and-identity): `dataset.key`, which names the same columns.
- [Ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md): `load.keyColumns`, `target.identityColumn` and the
  indexes a new table gets.
- [Getting started](../guides/getting-started.md): the welldb ing and delivery flows, written together.
