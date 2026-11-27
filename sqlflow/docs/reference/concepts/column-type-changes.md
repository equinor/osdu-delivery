---
id: concept-column-type-changes
title: "Changing the type of a column that already exists"
type: concept
summary: How a declared type change reaches a view and an ingestion target, in three tiers, with a data check, a safe rewrite keeping every row, and a report per run.
keywords:
  - type change
  - retype
  - convertTypes
  - allowTableRewrite
  - checked conversion
  - safe rewrite
  - shadow column
  - widening
  - narrowing
  - key column
  - typechange
related:
  - concept-schema-evolution
  - flow-ing-schema-incremental
  - flow-transform
  - concept-type-inference
  - cli-plan
sourceRefs:
  - src/SqlFlow.SqlServer/Schema/TypeChangeClassifier.cs
  - src/SqlFlow.SqlServer/Schema/TypeChanges.cs
  - src/SqlFlow.SqlServer/Schema/LosslessConversion.cs
  - src/SqlFlow.SqlServer/Schema/ColumnRetyper.cs
  - src/SqlFlow.SqlServer/Schema/TypeChangeApplier.cs
  - src/SqlFlow.SqlServer/Schema/StagingWriteGuard.cs
  - src/SqlFlow.SqlServer/Schema/TableDependencies.cs
  - src/SqlFlow.SqlServer/Schema/ChangeFootprintClassifier.cs
  - src/SqlFlow.Core/Model/TypeChangeReport.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Engine/TypeInferencer.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
---

# Changing the type of a column that already exists

A new source column lands as text and stays text. When an author decides later that it holds a date, a number or a
shorter string, they declare that type, and the engine takes it to every layer on the next run, with no hand-written
SQL: it works out what the change means, checks the data, applies it safely or refuses it, and says so in the run.

## Where a type is declared

Only a declared type changes the type of a column that already exists:

- in a file flow, `transform.columns[].type` (with or without an `expr`) types the column in the typed view
  `[schema].[v_<table>]` ([transform](../flow/transform.md));
- in an `ing` flow, the source's type: the typed view it reads, or the source table's own declared type.

Inference never re-types a column the view already has (see [Inference keeps a column's type](#inference-keeps-a-columns-type)),
so an ingestion target changes type only because someone declared it.

## The three tiers

Every change is classified into one tier (`TypeChangeClassifier` (src/SqlFlow.SqlServer/Schema/TypeChangeClassifier.cs)).
Anything not listed in tiers 1 and 2 is tier 3.

| Tier | Changes | What the engine does |
| --- | --- | --- |
| 1. Widening | Within a family, to a type that holds every value of the old one: a longer `varchar`/`nvarchar`/`varbinary`, `varchar` to `nvarchar`, `char` to `varchar`, `tinyint` to `smallint` to `int` to `bigint`, more `decimal` integer digits or scale, `real` to `float`, `smallmoney` to `money`, a higher `datetime2` scale, `date` to `datetime2`. | Applied automatically. In place when SQL Server records it in metadata; otherwise by the safe rewrite, only with `schema.allowTableRewrite: true`. |
| 2. Checked conversion | Text (`varchar`/`nvarchar`) to `tinyint`, `smallint`, `int`, `bigint`, `decimal(p,s)`, `date`, `datetime2`, `bit` or `uniqueidentifier`; one of those to `varchar`/`nvarchar`; a shorter `varchar`/`nvarchar`, or `nvarchar` to `varchar`; an integer to a `decimal` with room for every value of it. Only on a column that is not a key, identity, hash or primary-key column. | Only with `schema.convertTypes: true`. Every stored value is checked first; any that does not convert without loss refuses the change with a count and a sample. Otherwise the safe rewrite applies it. |
| 3. Manual | Every change to a key, identity, hash or primary-key column (other than an in-place length growth); a lossy change (`float` to `decimal`, a smaller scale, `datetime2` to `date`, `datetime` to `datetime2`, any narrowing other than of text); every change on a system-versioned (temporal), columnstore, CDC-tracked or partitioned table; a column a computed column, a check or default constraint, a foreign key, a schema-bound module, a full-text index, a clustered index, a primary key or a unique constraint references; a table with no identity column, primary key or unique index on NOT NULL columns. | Refused. The run, the plan and `validate` say why, which tier it is, and the manual steps. Nothing is applied in part. |

A narrowing (the target already holds a wider type than the source declares) is never forced: the column keeps its
wider type, which holds every value the source writes, and the report says `KeptWider`. A text narrowing is applied as
a checked conversion only with `schema.convertTypes`.

### What SQL Server records in metadata

The footprint of an `ALTER COLUMN` was measured, not assumed (the log an `ALTER COLUMN` writes over a populated table,
inside a transaction): only a variable-length column growing within its own base type (`varchar(n)` to `varchar(m)`,
`nvarchar`, `varbinary`, neither of them `max`) is recorded in metadata alone, and it is the one change SQL Server also
allows on an indexed column. Every other change touches every row under a schema-modification lock held for the whole
statement, including `varchar` to `nvarchar`, a `decimal` precision or scale change inside one storage-byte class, a
`datetime2` scale change inside one byte class, `real` to `float` and `smallmoney` to `money`. SQL Server refuses any
`ALTER COLUMN` (error 4922) on a column a computed column references, and anything but the length growth on an indexed
column (`ChangeFootprintClassifier` (src/SqlFlow.SqlServer/Schema/ChangeFootprintClassifier.cs)).

## The data check

A checked conversion runs only when every stored value converts without loss
(`LosslessConversion` (src/SqlFlow.SqlServer/Schema/LosslessConversion.cs)). `TRY_CONVERT(...) IS NOT NULL` is
not that test: SQL Server succeeds and changes the value when it converts `'2026-11-27 10:15'` to `date` (the time is
dropped), `'1.239'` to `decimal(10,2)` (rounded), `'5'` to `bit` (1), a GUID followed by other characters, a value with
an offset to `datetime2`, eight fractional digits to `datetime2(7)`, or a Unicode character to `varchar`, and it reads
`'03/04/2026'` by the session's language. So:

- text to a typed column accepts only a form whose meaning does not depend on the session: an integer exactly as SQL
  Server renders it (`'007'` and `'+7'` do not pass), a decimal with no more significant fractional digits than the
  scale, an ISO `yyyy-mm-dd` date (or date and time) with no offset and no more fractional digits than the type keeps,
  `0`, `1`, `true` or `false` for a bit, and a 36-character GUID; spaces around a value are trimmed;
- every other conversion passes when converting back gives the original value exactly (byte for byte for text).

A refusal reports the count of values that do not convert and up to five of them with their row counts (each cut to 100
characters), and leaves the table as it was.

## The safe rewrite

A tier 1 change that rewrites the table, and every tier 2 change, run through the safe rewrite
(`ColumnRetyper` (src/SqlFlow.SqlServer/Schema/ColumnRetyper.cs)), which blocks readers only for a final swap:

1. **Prepare**, in one short transaction: add a NULL shadow column of the new type, mark it with extended properties
   naming the column it replaces and the type, and create a trigger that keeps it in step with every `INSERT` and
   `UPDATE` of the column from then on (and refuses, with error 51000, a written value that does not convert without
   loss).
2. **Backfill** the shadow in committed batches (20,000 rows), keyed by the table's integer identity, primary key or unique key index, so
   every statement touches a bounded number of rows whatever the table's size.
3. **Verify** that no row holds a value whose shadow is empty.
4. **Rebuild**: make the shadow `NOT NULL` if the column is, then build a copy of every index and user statistics object
   on the column over the shadow (online on Enterprise, Azure SQL Database and Managed Instance). A unique index whose
   values collide under the new type fails here, and the change is rolled back and refused.
5. **Swap**, in one transaction: drop the trigger and the old indexes, rename the column away and the shadow into its
   place, drop the old column, give the copies their original names, drop the markers.

Afterwards the plain views over the table are refreshed with `sp_refreshview`; one that cannot be refreshed is named in
the report. Every row, its identity (`RecId`), its system dates (`InsertedDate_DW`, `UpdatedDate_DW`, `DeletedDate_DW`)
and its other columns are kept, and so are the column's name, nullability, indexes (including the key index and the
flow's `desiredIndexes`) and statistics. The column moves to the end of the table's column order. The table's schema app
lock is held throughout, so no other schema change of the table interleaves.

The upsert compares a staged row with the target's row by a checksum computed on both sides in the target's types, so
the run after a conversion does not see every row as changed: only a row whose value really differs is updated.

### An interrupted rewrite

The state of a rewrite lives in the table (the marked shadow column and its trigger), and the column itself is untouched
until the swap, which is atomic. A run that dies part way leaves a consistent table. The next run, before anything else
touches the table, finishes the rewrite when it still wants exactly that change and is allowed it (`Resumed`), and rolls
it back otherwise (`RolledBack`): the trigger, the index and statistics copies and the shadow column are removed. A
rewrite whose trigger was dropped or disabled meanwhile is rolled back and started again.

## When in a run

In an `ing` run ([the run pipeline](ingestion-run-pipeline.md)):

1. schema sync finishes or rolls back an earlier run's rewrite, then adds columns and applies the in-place changes;
2. it reports every type change, and applies the safe rewrites that belong before the load: a widening that rewrites,
   a typed column becoming text, a shorter text, an integer becoming a decimal;
3. a write guard checks the staged values of every column that kept a type its source no longer matches (pending,
   refused or manual), and stops the run before anything is written when SQL Server's implicit conversion would change
   one (a fraction into an `int`, a time into a `date`, a `float` into a `varchar`): it names the column, the count and
   a sample (`StagingWriteGuard` (src/SqlFlow.SqlServer/Schema/StagingWriteGuard.cs));
4. the load;
5. after the load commits, the checked conversions of a text column to a typed one. The load has written the source's
   current rendering of every row it read into the column, so when stored values do not convert because they were
   landed before the source typed the column (an `NA` the source now maps to NULL), a run with a full load
   (`sqlflow run --full`) re-renders them from the source and the conversion then applies in the same run.

In a file flow, the typed view is rebuilt on every run, a run that finds no new file included, so a declared column or
type reaches it at once.

## Inference keeps a column's type

When `transform.inferTypes` is on, inference profiles the landed table on every run, and the values change from export
to export. It types a column the first time the view sees it; after that the column keeps the type the view gives it
(`TypeInferencer` (src/SqlFlow.Core/Engine/TypeInferencer.cs)): text stays text whatever its values look like
now, and a typed column keeps its type, widening within its family when the values need it (an integer rank, a
decimal's digits, a date becoming a date and time). When the values no longer fit its family at all (a fraction in an
integer column, a word in a date column), the column still keeps its type, converted fail-loud, so the read of the view
stops on the value instead of the column silently becoming something else. Only a declaration changes it.

## A declared type in the view

When a type-only declaration (`type` with no `expr`) changes the type an existing view column has, the landed values
are checked first with the same lossless test. If any does not convert, the declaration is refused for that run: the
column keeps the type it had, and the run reports the count and a sample at warning level (`view.typechange`), until
the author maps the values with an expression (for example `expr: "NULLIF(@ColName, 'NA')"` beside the type, or a
`CONVERT` with the style the text is written in) or declares a type that holds them. A declared expression is the
author's own conversion; SQL Server raises its errors when the view is read.

## What a run reports

Every change a run considers is a `TypeChangeReport` in run.json (`result.typeChanges`, `TypeChangeReport` (src/SqlFlow.Core/Model/TypeChangeReport.cs)):
its layer (`target` or `view`), table, column, from and to types, tier, whether it rewrites the table, the key, index and
reference facts, the opt-in it needs, the row count, and for a refusal the failing count and sample. Its status is one of
`Applied`, `Pending` (an opt-in is missing), `Refused` (the data check failed), `Manual` (tier 3), `KeptWider`,
`Resumed` or `RolledBack`. A `Pending`, `Refused`, `Manual` or `RolledBack` change is logged at warning level
(`target.typechange`), so the run ends with a warning outcome on every run until it is resolved; the others at info.

```text
target.typechange  [arc].[WellLog].[date_logged] varchar(255) -> date (tier 2 checked conversion; rewrites the table): pending, the table is untouched. Text becomes date: every stored value must be one SQL Server reads the same under any language, without rounding or dropping a part. Set schema.convertTypes: true on the flow and the next run checks every stored value converts without loss, then converts the column by the safe rewrite.
```

## Downstream

A type change changes what a consumer reads: a date read as a date rather than as text renders differently in a
mapping, and a record a delivery renders from it gets a different rendered content hash and is delivered again when it
is next rendered. The rewrite keeps `UpdatedDate_DW`, so a consumer that selects rows by it does not re-read the
converted rows by itself: they carry the new rendering the next time their row changes, or when the consumer re-renders
its whole scope. A key column's type is never changed by the engine (tier 3) because converting it can merge keys
(`'00123'` and `'123'` both become `123`) and would change the key every downstream record is identified by; the
refusal counts the converted keys that would stand for several stored ones.

## Example

A pre flow landed `date_logged` as text holding `NA`. Once the export fills it, the author declares it, and lets the
ingestion flow convert its target:

```yaml
# pre flow
transform:
  inferTypes: true
  columns:
    - { name: date_logged, expr: "CONVERT(date, NULLIF(@ColName, 'NA'), 23)", type: date }
```

```yaml
# ing flow
schema:
  convertTypes: true
```

The next pre run rebuilds the view with `date_logged` as a `date`. The next ing run reports the conversion; if the target
still holds the `NA` it was given, the conversion is refused with that sample, and one `sqlflow run --full` of the
ingestion flow writes the source's NULLs over them and converts the column.

## See also

- [Schema evolution](schema-evolution.md)
- [Ingestion flow: schema sync](../flow/ing-schema-incremental.md)
- [Transform](../flow/transform.md)
- [plan](../cli/plan.md)
