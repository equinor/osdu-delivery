---
id: guide-write-a-report
title: "Write a report: one query that shows how many rows each source holds, then a parameter, a bar that sets it, and a list of choices"
type: guide
summary: "Write a report (flowType rpt) step by step: one query, validate, sync, add a parameter, make a chart filter the page, and read the reader's messages."
keywords:
  - write a report
  - report
  - flowtype rpt
  - how do I see how many rows a table holds
  - rows per source
  - staging area
  - first report
  - add a parameter
  - filter a report by a parameter
  - click a bar to filter
  - choices from a query
  - validate a report
  - common mistakes
  - dashboard
related:
  - flow-rpt
  - flow-rpt-views
  - concept-reports
  - cli-validate
sourceRefs:
  - src/SqlFlow.Yaml/YamlReportFlowLoader.cs
  - src/SqlFlow.Core/Reports/ReportFlow.cs
  - src/SqlFlow.SqlServer/Query/ReadOnlyQueryGuard.cs
  - src/SqlFlow.Cli/Program.cs
  - samples/reports/staging-overview.yaml
---

# Write a report

A [report](../flow/rpt.md) is the short way to look at what a database holds: a YAML document of `SELECT` statements, kept in the repository, that anyone signed in opens as a page of figures, charts and tables. This guide builds one in four steps. Each step is a document that validates, so stop wherever the report says enough.

The example asks a staging table, `stg.Orders`, how many rows it holds per source and what arrived lately. The finished document is `samples/reports/staging-overview.yaml`.

## 1. Start from one query

Put a file anywhere in the repository, `reports/staging-overview.yaml` say:

```yaml
flowType: rpt
name: staging-overview-rpt
title: Staging overview
batch: staging
connection: ${env:SQLFLOW_DW}
database: Staging

queries:
  by_source:
    title: Rows by source
    sql: |
      SELECT Source, COUNT(*) AS Staged
      FROM stg.Orders
      GROUP BY Source
      ORDER BY Staged DESC
```

| Line | Effect |
| --- | --- |
| `name:` | The report's identity. Give it a name no flow of the repository has: a report named like a flow is left out at sync. |
| `batch: staging` | The group the report is listed under on the **Reports** page. |
| `connection:` | A reference, the same one the estate's flows use for that database. A connection string is refused. |
| `sql:` | One `SELECT`. Aggregate in the statement, so the answer is the few rows worth looking at. |
| No `show` | The view is chosen from the answer's shape: figures for one row of numbers, a line over dates, bars by one name, otherwise a table. |

Validate it:

```text
$ sqlflow validate reports/staging-overview.yaml
OK  'staging-overview-rpt' is valid (report: 1 query shown as 1 view(s), on ${env:SQLFLOW_DW}; read-only, run on demand from its page).
```

Validation reads the document and parses every statement. It connects to nothing, so it does not know whether `stg.Orders` exists: a misspelt table is found when the query runs, and the query then fails with the database's own message.

Commit the file and sync the repository. The report is then listed on the **Reports** page under **staging**, and opening it runs the query.

## 2. Add a parameter

A parameter is a value a person sets on the page and a statement binds as `@name`:

```yaml
parameters:
  days:
    label: Days back
    type: integer
    default: 30

queries:
  by_source:
    title: Rows by source
    sql: |
      SELECT Source, COUNT(*) AS Staged
      FROM stg.Orders
      WHERE LoadedUtc >= DATEADD(day, -@days, SYSUTCDATETIME())
      GROUP BY Source
      ORDER BY Staged DESC
```

| Line | Effect |
| --- | --- |
| `days:` | A control labelled **Days back**. One query binds it here, so it is drawn on that query's tile; a parameter several queries bind is drawn in the page's header. |
| `type: integer` | Only a whole number is accepted, and the database is sent a `bigint`, never text. |
| `default: 30` | The page opens on the last 30 days. |
| `@days` | The statement takes the value. Changing it on the page runs this query again, and no query that does not bind `@days`. |

A parameter with no `default` is NULL until someone sets it, and a person can clear one that has a default. Write a filter that is optional so that NULL keeps every row:

```sql
WHERE (@source IS NULL OR Source = @source)
```

A filter that cannot be optional, as the one on `@days` here, keeps no row when the value is NULL. `required: true` on the parameter says so: a query that binds it does not run until it has a value.

The value is kept in the page's address, so a link to the page opens the same view.

## 3. Make a bar set it

Add a second parameter and a second query, and let a click on a bar of the first query narrow the second:

```yaml
parameters:
  days:
    label: Days back
    type: integer
    default: 30
  source:
    label: Source

queries:
  by_source:
    title: Rows by source
    sql: |
      SELECT Source, COUNT(*) AS Staged
      FROM stg.Orders
      WHERE LoadedUtc >= DATEADD(day, -@days, SYSUTCDATETIME())
      GROUP BY Source
      ORDER BY Staged DESC
    show: { type: bar, by: Source, values: Staged, selects: source }

  per_day:
    title: Rows loaded per day
    sql: |
      SELECT CAST(LoadedUtc AS date) AS Day, COUNT(*) AS Staged
      FROM stg.Orders
      WHERE LoadedUtc >= DATEADD(day, -@days, SYSUTCDATETIME())
        AND (@source IS NULL OR Source = @source)
      GROUP BY CAST(LoadedUtc AS date)
      ORDER BY Day
    show: { type: column, by: Day, values: Staged }
```

| Line | Effect |
| --- | --- |
| `selects: source` | A click on a bar sets `source` to that bar's `Source`. A second click clears it. |
| `by: Source` | Names the column the clicked value is read from. `selects` needs it. |
| `by_source` does not bind `@source` | Its bars stay whole, so another source can be clicked. Its heading names the filter its bars set, and shows the chosen source with a cross that clears it. |
| `per_day` binds `@source` | It runs again on every click, and counts the chosen source alone. It is the one query that binds `source`, so the **Source** control is drawn on its tile. |
| Both bind `@days` | **Days back** is now a filter of the page, drawn in its header. |

## 4. Add a choices query

A text parameter takes anything a person types. To offer the values there are, read them from a query of the same report:

```yaml
parameters:
  source:
    label: Source
    choicesFrom: sources

queries:
  sources:
    sql: SELECT DISTINCT Source FROM stg.Orders ORDER BY Source
    show: none
```

| Line | Effect |
| --- | --- |
| `choicesFrom: sources` | The rows of the query `sources` are what a person picks from: the first column is the value, a second column would be its label. |
| `show: none` | The query has no tile. It only feeds the choices. |

For a short list that never changes, write the values in the document under `choices` in place of a query.

```text
$ sqlflow validate reports/staging-overview.yaml
OK  'staging-overview-rpt' is valid (report: 3 queries shown as 2 view(s), 2 parameter(s), on ${env:SQLFLOW_DW}; read-only, run on demand from its page).
```

From here: `section` groups queries under headings, a list under `show` draws one answer several ways, and `columns` labels a column and writes it as a count, a size, a percentage or a date ([views](../flow/rpt-views.md)).

## Common mistakes

Every message names the file and the line, and all the problems of a document are reported together.

| Mistake | The message |
| --- | --- |
| A misspelt key | `query 'totals' has an unknown key 'tittle'; it may have: title, description, section, connection, database, timeoutSeconds, maxRows, sql, show, columns.` |
| A key in another case | `query 'totals' has key 'Title'; keys are case-sensitive, so write 'title'.` |
| A `schedule` on the report | `the report has 'schedule', which a flow that runs takes; a report is never run as a flow, so remove it. Its queries run when a person opens the report.` |
| `@name` that no parameter declares | `'sql' of query 'totals' refers to @sourse, which the report does not declare; its parameters are: source, days.` |
| A parameter no statement refers to | `parameter 'source' is bound by no query, so setting it would change nothing; refer to it in a statement as @source, or remove it.` |
| `selects` without `by` | `'selects' of 'show' of query 'by_source' sets a parameter to the value clicked, so the view names the column that value is read from under 'by'.` |
| A choices query that binds its own parameter | `'choicesFrom' of parameter 'source' names the query 'sources', whose statement binds @source itself; the choices of a parameter cannot depend on its own value.` |
| A connection string | `'connection' of query 'totals' must be one whole reference (${env:NAME}, ${keyvault:vault/secret}) or an @alias. A report's queries run on a worker, which resolves a reference and is never handed a connection string.` |
| A comma inside `{ }` | `column 'Staged' of query 'totals' reads 'staged' as a key with no value; inside { } a comma starts the next key, so put a value that holds a comma in quotes.` |
| A statement that writes | `query 'totals': Only a SELECT may be run here; this is an INSERT. The query surface is read-only, and nothing that changes data or schema is accepted.` |
| Two statements in one `sql` | `query 'totals': A query must be exactly ONE statement; this parses as 2 statement(s) in 1 batch(es). Run one SELECT at a time.` |
| A statement that does not parse | `query 'totals': The query does not parse as T-SQL (line 1): Incorrect syntax near '*'.` |

Three things the reader cannot catch, and what to do about each:

- **A wrong table or column name.** Validation connects to nothing. Open the report after the sync: a query the database refuses fails with the database's message.
- **A total over a cut answer.** An answer stops at `maxRows` and says so. Count and sum in the statement, never on the page.
- **A slow statement.** A query is cut at `timeoutSeconds` (60 unless the document says more, 600 at most). Narrow it with a parameter that has a `default`, so the page opens on a small slice.

## See also

- [Report (flowType: rpt)](../flow/rpt.md): every key, the parameter types, and what a statement may be.
- [Report views](../flow/rpt-views.md): the view types and `columns`.
- [Reports](../concepts/reports.md): how a query runs, who may run one, and what is recorded.
- `samples/reports`: the finished report of this guide.
