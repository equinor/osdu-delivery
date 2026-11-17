---
id: flow-rpt
title: "Report (flowType: rpt): read-only queries, their parameters and the keys reference"
type: flow-reference
summary: "flowType: rpt declares read-only SQL queries a person runs on demand from the report's page, shown as figures, charts and tables. Never run, never scheduled."
keywords:
  - report
  - flowtype rpt
  - rpt keys
  - read-only query
  - select
  - parameters
  - filter a report by a parameter
  - "@name"
  - choices
  - choicesFrom
  - maxRows
  - timeoutSeconds
  - on demand
  - never scheduled
  - cannot be scheduled
  - how many rows
  - dashboard
yamlPath: "(root, flowType: rpt)"
related:
  - flow-rpt-views
  - concept-reports
  - guide-write-a-report
  - flow-overview
  - cli-validate
  - concept-connections-and-secrets
  - concept-data-operations
sourceRefs:
  - src/SqlFlow.Yaml/YamlReportFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Core/Reports/ReportFlow.cs
  - src/SqlFlow.Core/Reports/ReportSql.cs
  - src/SqlFlow.Core/Query/QueryParameter.cs
  - src/SqlFlow.Core/Query/QueryModels.cs
  - src/SqlFlow.SqlServer/Query/ReadOnlyQueryGuard.cs
  - src/SqlFlow.SqlServer/Query/ReportStatements.cs
  - src/SqlFlow.SqlServer/Query/SqlServerQueryRunner.cs
  - src/SqlFlow.Execution/ComputeTaskExecutor.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - src/SqlFlow.Cli/Program.cs
  - docs/reference/flow/keys.rpt.json
  - samples/reports/staging-overview.yaml
---

# Report (flowType: rpt)

A `flowType: rpt` document is a report: a set of read-only SQL queries over one connection that a person runs on demand from the report's page in the GUI. Each query's answer is shown as figures, a chart or a table. The document says what to ask and how to show it. Nothing in it runs until someone opens the page.

It is a document of queries, and only that:

- **It is never run as a flow.** It takes no schedule and no mode, is in no wave and has no run history. `sqlflow run`, a trigger and a schedule all refuse it with the same sentence, and a batch run leaves it out.
- **It only reads.** Every statement is one read-only `SELECT`. The T-SQL parser proves that before the statement runs, and the statement runs inside a transaction that is always rolled back.
- **It takes no SQL from a client.** A run names the report and the query, and gives values for the parameters the report declares. The statement is the one the synced document holds.
- **It keeps no result of its own.** Each run of a query is a compute task, recorded and removed as every compute task is ([Reports](../concepts/reports.md)).

## Minimal example

```yaml
flowType: rpt
name: staging-overview-rpt
title: Staging overview
connection: ${env:SQLFLOW_DW}
database: Staging

parameters:
  source:
    label: Source

queries:
  by_source:
    title: Rows by source
    sql: |
      SELECT Source, COUNT(*) AS Staged
      FROM stg.Orders
      WHERE (@source IS NULL OR Source = @source)
      GROUP BY Source
      ORDER BY Staged DESC
    show: bar
```

This says: the report **Staging overview** asks the `Staging` database how many rows `stg.Orders` holds per source, and draws the answer as bars. A person may narrow it to one source.

## What each line does

| Line | Effect |
| --- | --- |
| `flowType: rpt` | The document is a report. A sync stores it as a pipeline row of kind `rpt`, with mode `disabled`, in no wave, and left out of the listings and counts of pipelines unless asked for by its kind. It is listed on the **Reports** page. |
| `name:` | The report's identity: the name of its pipeline row. |
| `title:` | The heading a person reads, in the list of reports and on the report's page. Left out, the name is shown. |
| `connection:` | Where every query runs. The worker that runs a query resolves the reference. The document never holds a connection string. |
| `database:` | The database every query runs in. Left out, a query runs in the connection's own database. |
| `parameters:` and `source:` | One control on the report's page, and one value a statement binds as `@source`. With no `type` it is text, and with no `default` it is empty until a person sets it. |
| `label:` | What is written beside the control. Left out, the parameter's name. |
| `queries:` and `by_source:` | One query, named `by_source`: the name a run request, a parameter's `choicesFrom` and the page's address use. |
| `title:` on a query | The heading over the query's answer. Left out, the query's name. |
| `sql:` | The statement. It runs as written when the page opens, and again when a parameter it binds changes. |
| `@source` in the statement | Binds the parameter `source` as a typed value. While nobody has set it the value is NULL, so `(@source IS NULL OR Source = @source)` keeps every row. Once set, only that source's rows are counted. |
| `show: bar` | The answer is drawn as horizontal bars, one per row, in the order the statement answers them. Left out, the view is chosen from the answer's shape ([views](rpt-views.md)). |

## Keys reference

### The report

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | | Must be `rpt`. |
| `name` | string | yes | | The report's name. One line, at most 400 characters, and a name no flow and no other report of the repository has. |
| `title` | string | no | the name | The heading a person reads. One line, at most 200 characters. |
| `description` | string | no | | What the report is for, in a sentence or two. At most 1000 characters. |
| `batch` | string | no | | A grouping label, as on every flow. Reports are listed by it. At most 250 characters. |
| `connection` | string | yes | | The connection the queries run on: one whole reference (`${env:NAME}`, `${keyvault:vault/secret}`) or an `@alias`. Never a connection string. At most 512 characters. |
| `database` | string | no | the connection's own | The database the queries run in. At most 128 characters. |
| `pool` | string | no | any worker | The worker pool the queries are routed to, for an estate where only some workers reach the connection. At most 128 characters. |
| `timeoutSeconds` | integer | no | `60` | How long a query may run, in seconds. 1 to 600. |
| `maxRows` | integer | no | `1000` | The most rows a query answers. 1 to 5000. |
| `parameters` | map | no | | Parameter name to what the parameter is. At most 20. |
| `queries` | map | yes | | Query name to the query. 1 to 40, at least one of them shown. |

### parameters

A parameter's name is the `@name` a statement refers to, written without the `@`: a letter or underscore, then letters, digits or underscores, at most 64 characters. A parameter listed with nothing after its name is a text value with no default and no fixed choices.

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `parameters.<parameter>.label` | string | no | the name | What a person reads beside the control. At most 100 characters. |
| `parameters.<parameter>.description` | string | no | | What the parameter narrows, in a sentence. At most 1000 characters. |
| `parameters.<parameter>.type` | string | no | `text` | `text`, `integer`, `number`, `date`, `datetime` or `flag`. |
| `parameters.<parameter>.default` | value | no | NULL | The value a query binds when a person sets none, written as its type writes a value. |
| `parameters.<parameter>.required` | bool | no | `false` | `true` when a query that binds the parameter does not run until it has a value. |
| `parameters.<parameter>.choices` | list | no | | The values a person picks from. Each entry is a value, or a mapping with `value` and `label`. At most 200, none repeated. Not with `choicesFrom`, and not on a `flag`. A `default` must be one of them. |
| `parameters.<parameter>.choicesFrom` | string | no | | The query of this report whose rows are the choices: its first column the value, its second (when it has one) the label. That query may not bind the parameter itself. Not on a `flag`. |

### queries

A query's name is a letter or underscore, then letters, digits, underscores or hyphens, at most 64 characters. Its heading is written under `title`. Queries are shown in the document's order.

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `queries.<query>.title` | string | no | the name | The heading a person reads. One line, at most 200 characters. |
| `queries.<query>.description` | string | no | | What the answer says, in a sentence. At most 1000 characters. |
| `queries.<query>.section` | string | no | | The heading the query is shown under. Consecutive queries naming one section share it. Left out, the query goes under the section before it, or under none. At most 200 characters. |
| `queries.<query>.connection` | string | no | the report's | The connection this query runs on, when it is not the report's. The same form as the report's. |
| `queries.<query>.database` | string | no | the report's | The database this query runs in. |
| `queries.<query>.timeoutSeconds` | integer | no | the report's | How long this query may run. 1 to 600. |
| `queries.<query>.maxRows` | integer | no | the report's | The most rows this query answers. 1 to 5000. |
| `queries.<query>.sql` | string | yes | | The statement: one read-only `SELECT`. At most 20000 characters. |
| `queries.<query>.show` | string, map or list | no | `auto` | How the answer is shown: [views](rpt-views.md). At most 6 views. |
| `queries.<query>.columns` | map | no | | How the answer's columns are labelled and written: [columns](rpt-views.md#columns). At most 100. |

Keys are case-sensitive. Parameter names and query names are compared without regard to case, so `Source` and `source` are one name declared twice.

`docs/reference/flow/keys.rpt.json` is the full census with the exact validation, and is what the editor reads.

## Parameters

A statement refers to a parameter as `@name`. The name is compared without regard to case, as T-SQL compares a variable's name. A statement may refer only to parameters the report declares, and every declared parameter must be referred to by at least one query.

An `@name` inside a string, a quoted or bracketed name or a comment is not a parameter, and neither is a system function such as `@@ROWCOUNT`.

A value is always sent to the database as a typed parameter. It is never written into the statement's text, so no value a person gives can change what the statement is.

| Type | A value is written | Sent as |
| --- | --- | --- |
| `text` | Any text, at most 4000 characters | `nvarchar` |
| `integer` | Digits with an optional leading minus: `30`, `-7` | `bigint` |
| `number` | With a period and no grouping: `0.25`, `-1200.5` | `decimal(38, 10)` |
| `date` | `yyyy-MM-dd`: `2026-01-31` | `date` |
| `datetime` | `yyyy-MM-ddTHH:mm:ss` with no zone: `2026-01-31T06:00:00`. A space in place of the `T`, no seconds, or fractional seconds are read too. | `datetime2` |
| `flag` | `true` or `false` | `bit` |

What a run binds for each parameter its statement refers to:

| The person | The statement is given |
| --- | --- |
| has not set the parameter | Its `default`, or NULL when it has none. |
| set a value | The value, checked as a value of the parameter's type and, with `choices`, as one of them. |
| cleared it (an empty value) | NULL, whatever the default. |

NULL is sent as NULL of the parameter's type. An empty text value is a cleared parameter, so a statement is never given an empty string.

Because a parameter nobody set is NULL, write an optional filter so that NULL keeps every row:

```sql
WHERE (@source IS NULL OR Source = @source)
```

Where a parameter's control is drawn follows from which queries bind it: in the page's header when several do, on the tile of the one query that does otherwise ([on the page](../concepts/reports.md#on-the-page)).

A `default` narrows the answer from the start, and a person may still clear it:

```yaml
parameters:
  days: { label: Days back, type: integer, default: 30 }
```

`required: true` is for a statement that makes no sense without the value. A query that binds a required parameter with no value does not run:

```text
Parameter 'days' is required by query 'per_day' and has no value.
```

The choices of a parameter are written in the document or read from a query:

```yaml
parameters:
  status:
    choices:
      - open
      - { value: closed, label: Closed orders }
    default: open
  source:
    choicesFrom: sources          # the rows of the query 'sources'

queries:
  sources:
    sql: SELECT DISTINCT Source FROM stg.Orders ORDER BY Source
    show: none                    # feeds the choices, and is not shown
```

A value given for a parameter with `choices` must be one of them. With `choicesFrom` a run checks the value's type only: the rows of the query are what a person picks from, not a rule a value is held to.

## What a statement may be

One read-only `SELECT`, written in T-SQL. Reports run on SQL Server and Azure SQL connections only: a worker refuses a connection of any other kind.

A `WITH ... AS (...) SELECT` is one `SELECT`. A report's statement may read the server's own catalog (`sys`, `INFORMATION_SCHEMA`) and call the metadata functions (`OBJECT_DEFINITION`, `COL_NAME` and their kind), which a query composed for an assistant's question may not.

| Refused | The reason given |
| --- | --- |
| More than one statement, or a `GO` | `A query must be exactly ONE statement` |
| Anything that is not a `SELECT`: `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `EXEC`, `CREATE`, `DROP`, `DECLARE`, `SET`, `USE` and the rest | `Only a SELECT may be run here` |
| `SELECT ... INTO` | `SELECT ... INTO creates a table, so it is not read-only.` |
| A stored procedure call | `A query may not call a stored procedure.` |
| `OPENQUERY`, `OPENROWSET` in every form, `OPENXML`, an ad-hoc remote table (`OPENDATASOURCE`) | It reaches outside the connection, or runs what cannot be inspected. |
| `NEXT VALUE FOR` | `NEXT VALUE FOR advances a sequence, so it is not read-only.` |
| A function named `xp_...`, `sp_...` or `fn_trace...` | `The function <name> is not allowed in a query here.` |
| Text the parser cannot read | `The query does not parse as T-SQL (line <n>): <the parser's message>` |

The parser reads a double-quoted run as a name, so write a string in single quotes.

The statement is checked at five places. `sqlflow validate <file>` refuses the document. A catalog sync and `sqlflow lineage` warn and keep the report. The report's page is given the reason in place of an answer. A run request is refused. The worker parses the statement once more before it reaches the database. [Reports](../concepts/reports.md#where-a-statement-is-checked) has the detail.

## Limits

| Limit | Value | What happens at it |
| --- | --- | --- |
| Rows of one answer | `maxRows`: default 1000, at most 5000 | The read stops at the cap, and the answer is marked `truncated`. The rows beyond it are never fetched. |
| Time one query may run | `timeoutSeconds`: default 60, at most 600 | The database command times out and the query fails with the database's message. |
| One cell | 4000 characters | The value is cut there and ends with `... [<n> more characters]`. |
| One answer | 8,000,000 characters as JSON | The query fails and says the result is over the limit. |
| One statement | 20000 characters | The reader refuses the document. |

A truncated answer is a page of the rows, not all of them. Aggregate in the statement (`COUNT`, `SUM`, `GROUP BY`) so that the answer is small, and keep `maxRows` for lists.

Every cell of an answer is text, written the same way wherever the worker runs: a number with a period, a date or a date and time in ISO 8601 form (`2026-01-31T06:00:00.0000000`), and binary as `0x` followed by hexadecimal digits. A NULL stays null.

## The strict reader

An unknown key, a value of the wrong shape, a value over its limit, a statement that refers to a parameter the report does not declare, or a view key its view type does not take keeps the document from loading. Every problem is reported with its line, all of them together:

```text
$ sqlflow validate reports/staging-overview.yaml
ERROR  reports/staging-overview.yaml: 4 problems in the report:
  reports/staging-overview.yaml:4: the report has 'schedule', which a flow that runs takes; a report is never run as a flow, so remove it. Its queries run when a person opens the report.
  reports/staging-overview.yaml:9: query 'totals' has an unknown key 'tittle'; it may have: title, description, section, connection, database, timeoutSeconds, maxRows, sql, show, columns.
  reports/staging-overview.yaml:10: 'sql' of query 'totals' refers to @sourse, which the report does not declare; its parameters are: source.
  reports/staging-overview.yaml:13: 'selects' of 'show' of query 'by_source' sets a parameter to the value clicked, so the view names the column that value is read from under 'by'.
```

A document that reads is then held to the statement rules. Each query whose statement is refused is named with the reason:

```text
$ sqlflow validate reports/staging-overview.yaml
ERROR  reports/staging-overview.yaml: 2 queries of the report cannot run:
  query 'archive': Only a SELECT may be run here; this is an INSERT. The query surface is read-only, and nothing that changes data or schema is accepted.
  query 'copy': SELECT ... INTO creates a table, so it is not read-only. Remove the INTO clause.
```

The folder form, `sqlflow validate <folder>`, holds every report to the same statement rules and lists a refused report among the documents that are not valid. `sqlflow lineage <folder>` and a catalog sync report a refused statement as a warning and keep the report, so its page says the same on that query's tile and its other queries still run.

A key a running flow takes is refused with the reason:

| Key | Answer |
| --- | --- |
| `schedule`, `mode`, `lifecycle`, `onErrorResume` | A report is never run as a flow, so remove it. |
| `connections`, `server` | A report names the one connection its queries run on under `connection`. |
| `source`, `target`, `load` | A report only reads, through the statements under `queries`. |

Three rules hold for values, because a report is stored and shown as written:

| Rule | Applies to | Message says |
| --- | --- | --- |
| No credential | Every text value: a title, a description, a label, a default, a choice, a statement | `appears to carry a credential`. The value is never echoed. In a statement, a comparison with a column named like a credential keyword is written with spaces around the `=`. |
| No credential anywhere else | The rest of the document: a comment, say | `carries what reads as a credential outside its values`. A report is stored with such text taken out, and it is the stored text that is shown and run, so a document is refused rather than stored differently from how it was written. |
| No `${...}` reference | Every value that names something: `name`, `batch`, `database`, `pool`, `choicesFrom`, `selects` | `holds a reference`. A report resolves its connection and nothing else, so write the value plainly. |
| One whole reference | `connection` | `must be one whole reference`. A connection string is refused, and never echoed. |

Inside `{ }` a comma starts the next key, so a value that holds a comma goes in quotes. The reader says so when it meets one:

```text
reports/staging-overview.yaml:8: column 'Staged' of query 'totals' reads 'staged' as a key with no value; inside { } a comma starts the next key, so put a value that holds a comma in quotes.
```

Two more rules look at the report as a whole: a parameter no query binds is a problem (setting it would change nothing), and so is a report whose every query has `show: none`.

## Never run as a flow

```text
$ sqlflow validate reports/staging-overview.yaml
OK  'staging-overview-rpt' is valid (report: 4 queries shown as 4 view(s), 2 parameter(s), on ${env:SQLFLOW_DW}; read-only, run on demand from its page).

$ sqlflow run reports/staging-overview.yaml
ERROR  a report (flowType: rpt) is a set of read-only queries a person runs on demand from the report's page; it is not a flow, so there is nothing to run, schedule or trigger.
```

| Asked | Answer |
| --- | --- |
| `sqlflow run <report>` | The `ERROR` line above, exit code 1. |
| A run of the report through the API (`POST /runs`) | `400 Invalid run parameters`, with the same sentence. Nothing is queued. |
| A node-scoped run that names the report as its anchor | `400 Invalid run parameters`: the report's name in quotes, then the sentence. |
| A schedule made through the API that names the report as a member | `400 Invalid schedule`: the report's name in quotes, then the sentence. |
| A batch run | Leaves the report out: a batch runs the flows whose mode is `auto`, and a report's is `disabled`. |
| `schedule`, `mode` or `lifecycle` in the document | The reader refuses the key. |

A report is in no wave: the execution plan and `sqlflow lineage` do not list it, and it adds no node or edge to the lineage graph.

## What a sync reports

A report that cannot be used in full is said so, as a warning of the sync and of `sqlflow lineage`:

| Warning | Cause |
| --- | --- |
| `report '<name>' has the name of the <kind> flow in <file>` | A pipeline is identified by its name. The report is left out until it has a name of its own. The flow keeps its pipeline, its wave and its run history, whichever of the two documents is read first. |
| `report '<name>' has the name of the report in <file>` | The first report in path order is kept, and this one is left out. |
| `report '<name>': query '<query>' cannot run: <reason>` | The statement is not one read-only `SELECT`. The report is kept, and its other queries run. |

## Full example

`samples/reports/staging-overview.yaml` is a whole report: two parameters, one of them with its choices read from a query, two sections of tiles, and a bar that sets a parameter when clicked.

```yaml
flowType: rpt
name: staging-overview-rpt
title: Staging overview
description: What the staging area holds, by source, and what arrived lately.
batch: staging
connection: ${env:SQLFLOW_DW}
database: Staging
timeoutSeconds: 60
maxRows: 1000

parameters:
  source:
    label: Source
    description: Only rows of this source.
    choicesFrom: sources
  days:
    label: Days back
    type: integer
    default: 30
    required: true

queries:
  sources:
    sql: SELECT DISTINCT Source FROM stg.Orders ORDER BY Source
    show: none

  totals:
    title: The staging area
    section: Overview
    sql: |
      SELECT COUNT(*) AS Staged, COUNT(DISTINCT Source) AS Sources, MAX(LoadedUtc) AS LastLoaded
      FROM stg.Orders
      WHERE (@source IS NULL OR Source = @source)
    show: figure
    columns:
      Staged: { label: Rows staged, format: integer }
      LastLoaded: { label: Last load, format: datetime }

  by_source:
    title: Rows by source
    section: Overview
    sql: |
      SELECT Source, COUNT(*) AS Staged
      FROM stg.Orders
      GROUP BY Source
      ORDER BY Staged DESC
    show:
      - { type: bar, by: Source, values: Staged, selects: source }
      - table

  per_day:
    title: Rows loaded per day
    section: Arrivals
    sql: |
      SELECT CAST(LoadedUtc AS date) AS Day, COUNT(*) AS Staged
      FROM stg.Orders
      WHERE LoadedUtc >= DATEADD(day, -@days, SYSUTCDATETIME())
        AND (@source IS NULL OR Source = @source)
      GROUP BY CAST(LoadedUtc AS date)
      ORDER BY Day
    show: { type: column, by: Day, values: Staged }
```

| Part | Effect |
| --- | --- |
| `sources` with `show: none` | Runs to fill the choices of `source`, and has no tile. |
| `totals` with `show: figure` | A strip of figures, one per column of its one row. `columns` labels two of them and writes the count with digit grouping. |
| `by_source` with two views | One answer shown twice: as bars and as a table. Its statement does not bind `source`, so the bars stay whole while a source is chosen. A click on a bar sets `source` to that bar's `Source`. |
| `per_day` | Binds both parameters, so it runs again when either changes. |
| `section:` | `totals` and `by_source` share the heading **Overview**, and `per_day` starts **Arrivals**. |

## See also

- [Report views](rpt-views.md): `show`, the view types and their keys, and `columns`.
- [Reports](../concepts/reports.md): how a query runs, who may run one, and what is recorded.
- [Write a report](../guides/write-a-report.md): from one query to a filtered page.
- [sqlflow validate](../cli/validate.md)
