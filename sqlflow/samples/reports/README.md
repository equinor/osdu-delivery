# A report: read-only queries shown as figures, charts and tables

One report (`flowType: rpt`) over a staging table, `stg.Orders`: how many rows it holds, by source, and what arrived
lately.

| Document | Kind | What it is |
| --- | --- | --- |
| `staging-overview.yaml` | rpt | Four queries over one connection, two parameters, and four views in two sections. |

What the document shows:

| Part | Shows |
| --- | --- |
| `parameters.source` with `choicesFrom: sources` | A parameter whose choices are read from a query of the report. |
| `parameters.days` with `type: integer` and `default: 30` | A typed parameter with a value from the start. |
| `queries.sources` with `show: none` | A query that feeds the choices and has no tile. |
| `queries.totals` with `show: figure` and `columns` | One row of numbers as large figures, with labels and formats. |
| `queries.by_source` with two views and `selects: source` | One answer as bars and as a table. A click on a bar sets the parameter `source`. |
| `queries.per_day` with `type: column` | Columns over days, narrowed by both parameters. |

A report is never run as a flow. It takes no schedule, is in no wave and has no run history. Its queries run when a
person opens the report's page, each as one read-only `SELECT` on a worker.

```text
$ sqlflow validate staging-overview.yaml
OK  'staging-overview-rpt' is valid (report: 4 queries shown as 4 view(s), 2 parameter(s), on ${env:SQLFLOW_DW}; read-only, run on demand from its page).

$ sqlflow run staging-overview.yaml
ERROR  a report (flowType: rpt) is a set of read-only queries a person runs on demand from the report's page; it is not a flow, so there is nothing to run, schedule or trigger.
```

Validation reads the document and parses every statement. It connects to nothing, so `SQLFLOW_DW` need not be set and
`stg.Orders` need not exist.

To open the report, point `connection` and `database` at a database that holds a table with the columns the statements
name (`Source`, `LoadedUtc`), sync the repository, and open it from the **Reports** page.

See `docs/reference/flow/rpt.md`, `docs/reference/flow/rpt-views.md` and `docs/reference/guides/write-a-report.md`.
