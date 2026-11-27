---
id: cli-plan
title: sqlflow plan
type: cli-command
summary: "Preview what a run would do, changing nothing: the exact DDL of a file flow, or an ingestion flow's schema and type changes against its live source and target."
keywords:
  - plan
  - dry run
  - ddl preview
  - schema diff
  - schema delta
  - file flows
  - ingestion flows
  - type changes
  - convertTypes
cliCommand: plan
related:
  - cli-run
  - cli-validate
  - concept-schema-evolution
  - flow-schema
  - flow-ing
  - flow-ing-schema-incremental
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Engine/SchemaDiffer.cs
  - src/SqlFlow.Core/Model/Results.cs
  - src/SqlFlow.SqlServer/SqlServerDdlGenerator.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionPlan.cs
  - src/SqlFlow.SqlServer/Schema/SchemaSyncService.cs
  - src/SqlFlow.SqlServer/Schema/TypeChangeApplier.cs
  - src/SqlFlow.SqlServer/Schema/ColumnRetyper.cs
  - src/SqlFlow.Core/Model/TypeChangeReport.cs
  - src/SqlFlow.Core/SqlFlowException.cs
  - samples/quickstart/orders.flow.yaml
---

# sqlflow plan

Show what a run of a file flow or an ingestion flow would do, without changing anything.

## Synopsis

```bash
sqlflow plan <pipeline.flow.yaml> [-v|--verbose]
```

## Description

`plan` answers the question "what will `sqlflow run` do to my target table?" before you run it. It plans two document kinds; every other kind is refused (see [Exit behavior](#exit-behavior)).

The document loads through the same loader as `validate` and `run` (src/SqlFlow.Execution/DocumentLoader.cs): a relative `source.location` resolves against the flow file's own directory, and a connection value that looks like an embedded credential produces a secret-hygiene warning on standard error. The git-ignored `.sqlflow/env` file (searched from the flow file's directory upward) is applied before anything resolves; the process environment always wins.

### File flows

A file flow (the default document kind: CSV, JSON, XML, XLS, or Parquet into SQL Server) is planned by `FlowRunner.PlanAsync` (src/SqlFlow.Core/Engine/FlowRunner.cs), which:

1. Resolves the target connection reference through the secret resolver.
2. Reads the source schema from the actual files (`source.columns` stage).
3. Builds the desired table schema by mapping every source column through the type mapper, applying `schema.defaultColumnType` and `schema.overrides`.
4. Introspects the live target table (`target.introspect` stage). This is a real database round trip, so connectivity to the target is required.
5. Diffs desired against actual under the flow's `schema.evolve` policy and generates the DDL that `run` would execute.

Nothing is executed against the database: no DDL is applied, no rows are loaded, no state is saved, and no run artifacts are written under `.sqlflow/runs/`. The result is a `FlowPlan` record (src/SqlFlow.Core/Model/Results.cs) carrying `Flow`, `SourceColumns`, `Desired`, `Actual`, `Delta`, `DdlStatements`, and `Trace`; the CLI prints a summary of it.

### Ingestion flows

A `flowType: ing` document is planned against its live source and target by `IngestionFlowRunner.PlanAsync` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs), through the runner its run builds (`DocumentExecutor.PlanIngestionAsync`, src/SqlFlow.Execution/DocumentExecutor.cs), so the document's `connections:` and their secrets resolve exactly as they do for `run`. The plan:

1. Resolves the source and target connections and refuses the flag combinations a run refuses before it reads anything (temporal with `truncateBeforeLoad`, temporal with SCD2, `insertUnknownDimensionRow`, a misconfigured `load.truncateSourceWhenConsolidated`).
2. Introspects the source object and builds the target schema the run wants, through the run's own steps (ignored columns dropped, virtual, system, hash and identity columns added, names cleaned).
3. Introspects the live target and plans schema sync against it (`SchemaSyncService.PlanAsync`, src/SqlFlow.SqlServer/Schema/SchemaSyncService.cs) with the flow's key columns: whether the target exists, the columns a run would add (every column when it would create the target), the in-place `ALTER COLUMN`s its DDL would carry (one that rewrites the table only with `schema.allowTableRewrite`), the critical mismatches that would stop the load, and the target columns the source no longer has, which a run keeps.
4. Says what a run with this flow's `schema.convertTypes` and `schema.allowTableRewrite` would do about every change to the type of an existing column, and about a rewrite an earlier run left unfinished (`TypeChangeApplier.PlanAsync`, src/SqlFlow.SqlServer/Schema/TypeChangeApplier.cs), with the same decisions and the same messages a run reports.

Only reads run: no DDL, no trigger, no shadow column, no staging table, no pre- or post-process hook, no invoke, no run log, and no run artifacts. The reads are the introspections, the table's facts (indexes, references, row count), the unfinished rewrites, each checked conversion's data check over the stored values (`ColumnRetyper.CheckAsync`, src/SqlFlow.SqlServer/Schema/ColumnRetyper.cs), and, for a key column, the count of stored keys that would merge. The result is an `IngestionPlan` record (src/SqlFlow.SqlServer/Ingestion/IngestionPlan.cs) with `Source`, `Target`, `TargetExists`, `SchemaSync`, `Policy`, `ColumnsToAdd`, `ColumnsToAlter`, `CriticalMismatches`, `ExtraColumns`, and one `TypeChangeReport` (layer `target`, src/SqlFlow.Core/Model/TypeChangeReport.cs) per type change.

Each type change carries the status a run would give it:

| Status | Printed | What a run does |
| --- | --- | --- |
| `Planned` | `PLANNED` | Applies it: in place when SQL Server records it in metadata, or by the safe rewrite (a shadow column filled in batches, then swapped in). A checked conversion is planned only once its data check finds every stored value converts without loss. An unfinished rewrite the run still wants and may make is planned too: the run finishes it first. |
| `Pending` | `PENDING` | Leaves it, the table untouched: the change needs `schema.convertTypes` or `schema.allowTableRewrite`, which the flow does not set. |
| `Refused` | `REFUSED` | Refuses a checked conversion whose stored values do not all convert: the count, of how many, and a sample. |
| `Manual` | `MANUAL` | Never applies a tier 3 change (a key column, a lossy change, a table the safe rewrite cannot carry); the manual steps say what to do. A key column's change also counts the stored keys that would merge. |
| `KeptWider` | `KEPT` | Keeps a wider type, which holds every value of the source. |
| `RolledBack` | `ROLLBACK` | Rolls back a rewrite an earlier run left unfinished, because the source no longer asks for it or the flow no longer allows it; the column keeps its type and every value. |

A text column that becomes typed is converted by a run after its load, which first writes the source's values into the rows it reads. The plan checks the values the table holds now, so a `REFUSED` conversion of this kind says so: a run converts the column when its load rewrites every failing row (a full load after the source renders them as the new type).

When the plan is blocked by a critical mismatch, a run fails at schema sync, before it loads the target; the statuses of the other changes say what a run does about them once the block is resolved.

With `schema.sync: false` a run changes no column of the target, so the plan lists none. It checks that the target takes every column the run writes, as the run does, and fails with the run's message when it does not; when a `preProcessOnTarget` hook is set the check is left to the run, since the hook runs first and may add the column, and a plan runs no hook.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<pipeline.flow.yaml>` | yes | Path to a file-flow or ingestion-flow (`flowType: ing`) document. Any other flow kind is rejected with exit code 1. |

## Options

`plan` has no command-specific flags. The global CLI flags apply:

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-v`, `--verbose` | switch | off | Debug-level console logging. Also reports on standard error when a `.sqlflow/env` file was applied. |
| `-h`, `--help` | switch | off | Print usage. Exits 0 when the file argument is also present; without it the missing-argument check wins and the exit code is 1. |

## Behavior and output

### A file flow's plan

The printed plan has a fixed shape (`PrintPlan` in src/SqlFlow.Cli/Program.cs):

```text
Plan for '<name>' -> [<schema>].[<table>]
  target : exists | does not exist (will create)
  columns to add : <N>
  columns to widen : <N>
  load mode      : Append | TruncateLoad
  generated DDL  : (none)   (or each statement, verbatim, indented)
  trace:
    ok    <operation>  <elapsed> ms   (one line per stage; FAIL on a failed stage)
```

- The header prints the flow's `name` and the target's qualified name (`[schema].[table]`).
- `target : does not exist (will create)` means the introspection found no table; the whole desired schema becomes the `CREATE TABLE` statement.
- `columns to add` is the diff's `ColumnsToAdd` count. For a new table that is every column; for an existing table under `evolve: widen` it is only the columns the target lacks.
- `columns to widen` is the diff's `ColumnsToAlter` count: the existing columns under `evolve: widen` whose type is too narrow for the source's, each grown to the merged type with one `ALTER TABLE ... ALTER COLUMN` that keeps the column's nullability.
- `load mode` prints the parsed `LoadMode` enum value: `Append` (YAML `append`) or `TruncateLoad` (YAML `truncate-load`).
- `generated DDL` prints each statement exactly as `run` would execute it, indented four spaces, or `(none)` when the schema already matches. Generation rules (src/SqlFlow.SqlServer/SqlServerDdlGenerator.cs): a missing table gets one `CREATE TABLE` statement; an existing table gets one `ALTER TABLE ... ADD ... NULL;` per new column (columns added to an existing table are always nullable, since there is no default to backfill existing rows with), then one `ALTER TABLE ... ALTER COLUMN` per widened column.
- `trace` lists the plan's stages with per-operation timing: `source.columns` then `target.introspect`. Unlike `run` output, there is no `TOTAL` line.

Evolve-policy interaction (src/SqlFlow.Core/Engine/SchemaDiffer.cs):

- `create`: an existing table produces an empty delta, so `generated DDL : (none)` even when the source has new columns.
- `widen`: new source columns become `ALTER TABLE ... ADD` statements, and a column too narrow for the source's type is widened.
- `strict`: when the target lacks source columns, the diff throws and `plan` fails with `Schema drift on [<schema>].[<table>]: target is missing N column(s) (...) and the evolve policy is 'strict'.`

An unknown `source.type` fails with `No source reader is registered for source type '<type>'.` (src/SqlFlow.Core/Engine/FlowRunner.cs).

Each file-flow `plan` invocation mints a fresh time-ordered run id (`Guid.CreateVersion7()`) and publishes its stage events to the configured flow event sink, but it records nothing durable.

### An ingestion flow's plan

The printed plan has a fixed shape (`PrintIngestionPlan` in src/SqlFlow.Cli/Program.cs):

```text
Plan for '<name>' -> [<database>].[<schema>].[<table>]
  source : [<database>].[<schema>].[<object>]
  target : exists | does not exist (will create)
  schema sync : on (schema.convertTypes on|off, schema.allowTableRewrite on|off) | off (a run changes no column of the target)
  columns to add : <N>
    [<column>] <type>                       (one line per column)
  columns to alter in place : <N>
    [<column>] <from> -> <to> (metadata only | rewrites the table)
  columns kept : <N> in the target and not in the source, never dropped: [<column>], ...   (only when there are some)
  type changes : <N>
    <STATUS>  [<column>] <from> -> <to>
             <facts>
             <explanation>
             manual steps:                  (a MANUAL change only)
               - <step>
  critical mismatches : <N> ([<column>], ...), each a type change above   (only when blocked)
  verdict : nothing needs attention | <N> type change(s) need attention; a run loads and ends with a warning | BLOCKED, a run stops at schema sync, before it loads the target
```

- With `schema.sync: false` only the `source`, `target`, `schema sync` and `verdict` lines print.
- The facts line of a type change joins: the tier (`tier 1 widening`, `tier 2 checked conversion`, `tier 3 manual`), the footprint (`metadata only` or `rewrites the table`), `key column` for a key, hash, identity or primary-key column, `indexed by [<index>], ...`, `referenced by <object>, ...` (a computed column, a constraint, a foreign key or a schema-bound module that stops the change), the table's `<N> row(s)`, for a refusal `<N> value(s) do not convert`, and for a pending change the opt-in it needs (`needs schema.convertTypes: true` or `needs schema.allowTableRewrite: true`).
- The explanation is the message a run would log for the change: why the change is in its tier, what the run does, and what to do next. A refusal's sample is in it (`for example 'n/a' in 1 row(s)`), and so is a key column's count of keys that would merge. A tier 3 change's manual steps print on their own lines below it.
- A critical mismatch is a key column whose type family changes; each is also a `MANUAL` type change in the list above.

## Examples

Plan the quickstart flow before the target table exists (samples/quickstart/orders.flow.yaml: CSV source, `evolve: widen`, `OrderId` overridden to `BIGINT NOT NULL`, all other CSV columns falling back to `defaultColumnType: varchar(255)`, plus the provenance columns the CSV reader injects by default):

```bash
sqlflow plan samples/quickstart/orders.flow.yaml
```

Representative output (timings vary):

```text
Plan for 'orders' -> [dbo].[Orders]
  target : does not exist (will create)
  columns to add : 11
  columns to widen : 0
  load mode      : Append
  generated DDL  :
    CREATE TABLE [dbo].[Orders] (
        [OrderId] BIGINT NOT NULL,
        [Customer] varchar(255) NULL,
        [Amount] varchar(255) NULL,
        [OrderDate] varchar(255) NULL,
        [IsPaid] varchar(255) NULL,
        [FileName_DW] NVARCHAR(4000) NULL,
        [FileDate_DW] DATETIME2 NULL,
        [FileRowDate_DW] DATETIME2 NULL,
        [FileSize_DW] BIGINT NULL,
        [DataSet_DW] DATETIME2 NULL,
        [RowNumber_DW] BIGINT NULL
    );
  trace:
    ok    source.columns              3.1 ms
    ok    target.introspect          41.7 ms
```

Re-plan after the table exists and the source has grown one column (`Discount`), with `evolve: widen`:

```bash
sqlflow plan samples/quickstart/orders.flow.yaml
```

```text
Plan for 'orders' -> [dbo].[Orders]
  target : exists
  columns to add : 1
  columns to widen : 0
  load mode      : Append
  generated DDL  :
    ALTER TABLE [dbo].[Orders] ADD [Discount] varchar(255) NULL;
  trace:
    ok    source.columns              2.8 ms
    ok    target.introspect          39.2 ms
```

When source and target already agree, the same command prints `columns to add : 0` and `generated DDL  : (none)`.

Plan an ingestion flow (`target.identityColumn: RecId`, `load.keyColumns: [Id]`, `schema.convertTypes: true`) after its source declared two text columns a `date` and an `int` and grew a column, while the target still holds the text an earlier run wrote, one value of which (`n/a`) is no number:

```bash
sqlflow plan orders.flow.yaml
```

```text
Plan for 'orders' -> [Warehouse].[dbo].[Orders]
  source : [Landing].[dbo].[Orders]
  target : exists
  schema sync : on (schema.convertTypes on, schema.allowTableRewrite off)
  columns to add : 1
    [Note] nvarchar(10)
  columns to alter in place : 0
  type changes : 2
    PLANNED  [OrderDate] varchar(30) -> date
             tier 2 checked conversion; rewrites the table; 3 row(s)
             [Warehouse].[dbo].[Orders].[OrderDate] varchar(30) -> date (tier 2 checked conversion; rewrites the table): planned, a run applies it by the safe rewrite once the load has committed, which keeps every row, its identity, system dates, indexes and statistics. Text becomes date: every stored value must be one SQL Server reads the same under any language, without rounding or dropping a part. Every one of the 3 stored value(s) converts to date without loss; the run checks them again once the load has written the source's values.
    REFUSED  [Amount] varchar(20) -> int
             tier 2 checked conversion; rewrites the table; 3 row(s); 1 value(s) do not convert
             [Warehouse].[dbo].[Orders].[Amount] varchar(20) -> int (tier 2 checked conversion; rewrites the table): refused, the table is untouched. 1 of 3 stored value(s) do not convert to int without loss (for example 'n/a' in 1 row(s)). A run checks them once its load has written the source's values into the rows it reads, so it converts the column when the load rewrites every one of these rows. Render them in the source as values of the new type (an expression in its transform, such as NULLIF(@ColName, 'NA') or a CONVERT with the style the text is written in), then run this flow once with a full load: the load writes the source's values into the column first, and the conversion then checks those. Or correct them in the table.
  verdict : 1 type change(s) need attention; a run loads and ends with a warning
```

The command exits 2. Without `schema.convertTypes` both changes print as `PENDING` with `needs schema.convertTypes: true`, and the command still exits 2. When the source then declares its key column `Id` text, the plan is blocked and the command exits 1 (excerpt):

```text
  type changes : 3
    MANUAL   [Id] int -> varchar(20)
             tier 3 manual; rewrites the table; key column; indexed by [NCI_KeyColumn]; 3 row(s)
             [Warehouse].[dbo].[Orders].[Id] int -> varchar(20) (tier 3 manual; rewrites the table; key column; indexed by [NCI_KeyColumn]): refused, the engine does not apply a tier 3 change. [Id] is a key column: converting it can merge keys (for example '00123' and '123' both become 123), and a new key type changes the key every downstream record is identified by. No two stored keys would become one as varchar(20).
             manual steps:
               - Keep [Id]'s type in the source as the table has it, or change the key by reloading: write a migration that drops [Warehouse].[dbo].[Orders] (and the flow's staging table), then run the flow with a full load so every row is written again as varchar(20).
               - A reload gives every row a new identity and InsertedDate_DW, and anything downstream that identifies its records by this key sees every key as new: retire or re-key those records first.
  ...
  critical mismatches : 1 ([Id]), each a type change above
  verdict : BLOCKED, a run stops at schema sync, before it loads the target
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | A file flow's plan printed (including the no-op case with no DDL); an ingestion flow's plan in which nothing needs attention. Also returned by `-h`/`--help` when the file argument is present. |
| 1 | No file argument (usage is printed); the document is neither a file flow nor an ingestion flow; a `SqlFlowException` occurred (invalid YAML, unresolved connection reference, unknown source type, no matching source files, strict-policy schema drift, a source object that is not visible, a flag combination a run refuses, a `schema.sync: false` target that lacks a column the run writes); or an ingestion flow's plan is blocked by a critical mismatch, so a run would fail. Errors print `ERROR  <message>` to standard error with secret values redacted. |
| 2 | An ingestion flow's plan in which a run would load but a type change needs attention (`PENDING`, `REFUSED`, `MANUAL` or `ROLLBACK`): the run would end with a warning. |

A document of any other kind (`exp`, `sp`, `inv`, `hc`, `scm`, `batch`, and the rest) prints this to standard error and exits 1:

```text
ERROR  'plan' supports file flows and ingestion flows (flowType: ing); export and stored-procedure work is determined at run time against the live source.
```

## See also

- [sqlflow run](run.md): execute the same plan, then load.
- [sqlflow validate](validate.md): check the document offline, without touching source files or the target.
- [Schema evolution](../concepts/schema-evolution.md): the `create` / `widen` / `strict` policies the diff applies, and schema sync for ingestion flows.
- [schema](../flow/schema.md): the file flow document's `schema` section (`evolve`, `defaultColumnType`, `overrides`).
- [Ingestion flows](../flow/ing.md) and their [schema and incremental settings](../flow/ing-schema-incremental.md).
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
