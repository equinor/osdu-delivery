---
id: concept-reports
title: "Reports: read-only queries run on demand on a worker, never scheduled and never stored as results"
type: concept
summary: "How a report (flowType rpt) runs: a pipeline row that is not a flow, a statement only the synced document supplies, typed values, a worker, who may run one."
keywords:
  - report
  - reports page
  - flowtype rpt
  - report query
  - reportQuery
  - run on demand
  - why a report cannot be scheduled
  - not scheduled
  - read-only
  - compute task
  - worker
  - typed parameters
  - no sql from a client
  - assistant run
  - column allow-list
  - api reports
related:
  - flow-rpt
  - flow-rpt-views
  - guide-write-a-report
  - concept-control-plane
  - concept-data-operations
  - concept-connections-and-secrets
  - concept-maintenance
  - concept-authentication-and-identity
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/ReportEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DescribedLineage.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - src/SqlFlow.ControlPlane/Background/RunDispatcher.cs
  - src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - src/SqlFlow.Catalog/ComputeTaskStore.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/FlowLoadProfiles.cs
  - src/SqlFlow.Core/Compute/ComputeTaskModels.cs
  - src/SqlFlow.Core/Reports/ReportFlow.cs
  - src/SqlFlow.Core/Query/QueryModels.cs
  - src/SqlFlow.Core/Query/QueryParameter.cs
  - src/SqlFlow.Execution/ComputeTaskExecutor.cs
  - src/SqlFlow.SqlServer/Query/ReadOnlyQueryGuard.cs
  - src/SqlFlow.SqlServer/Query/ReportStatements.cs
  - src/SqlFlow.SqlServer/Query/SqlServerQueryRunner.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/RunArtifactCollector.cs
  - src/SqlFlow.Yaml/YamlReportFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
---

# Reports

A [report](../flow/rpt.md) (`flowType: rpt`) is a YAML document of read-only SQL queries, kept in a repository beside the flows. A person opens it on the **Reports** page, and each query runs then, on a worker, and is shown as figures, a chart or a table.

A report is stored where a flow is stored and is not a flow. This page says what that means, how one query runs from the request to the answer, who may run one, and what is recorded.

## A pipeline row that is not a flow

A catalog sync stores a report as a pipeline row of kind `rpt`, so its document has one home: the text, its definition, its place in a repository. Everything else a pipeline row is used for leaves it out:

- It has mode `disabled` and belongs to no wave (`-1`). The execution plan (`GET /repos/{id}/waves`) does not list it, and the lineage graph holds no node, edge or wave for it.
- Every run of it is refused before anything is queued: a trigger, the anchor of a node-scoped run, and a schedule that names it as a member all answer 400 ([never run as a flow](../flow/rpt.md#never-run-as-a-flow)).
- The listings and counts of pipelines (`GET /pipelines`, its batches and projects, the dashboard's pipeline counts, the lineage graph's scope picker, the flows that went quiet) leave it out. Asking for the kind lists it: `GET /pipelines?kind=rpt`. The reports have a listing of their own, `GET /reports`.
- It never stands in a flow's place. A pipeline is identified by its name, so a report that has the name of a flow that runs, or of a report read before it, is left out of the catalog with a warning: the flow keeps its pipeline, its wave, its schedule and its run history. A flow's document rewritten as a report under the same name leaves the plan: its wave becomes `-1`.
- It has no run history. A run folder is attributed to the flow of its name, never to a report.

The report's `connection` is recorded as the pipeline row's source, so the report counts among what reads that datasource (`GET /datasources`).

## How one query runs

No SQL ever arrives from a client. A run names a report and one of its queries, and gives values for the report's parameters:

```text
POST /api/v1/reports/{reportId}/queries/per_day/run
{ "values": { "source": "web", "days": "7" } }

202 Accepted
Location: /api/v1/datasources/tasks/{taskId}
{ "taskId": "...", "status": "queued", "query": "per_day",
  "parameters": [ { "name": "days", "type": "integer", "value": "7" },
                  { "name": "source", "type": "text", "value": "web" } ] }
```

| Step | Where | What happens |
| --- | --- | --- |
| 1 | Control plane | The report is read from its synced text with the reader that accepted it at sync, so what a page shows and what a run executes are the document as it is stored. |
| 2 | Control plane | The values are bound: each parameter the statement refers to takes the value given, its default, or NULL ([parameters](../flow/rpt.md#parameters)). A value that names no parameter, is not a value of its type or is not one of its choices, or a required parameter with no value, answers `400 The query cannot run`. |
| 3 | Control plane | The statement is parsed as one read-only `SELECT`. One that is not answers `400`, and nothing is queued. |
| 4 | Control plane | A compute task of the operation `reportQuery` is queued with the connection reference, the database, the statement, the row cap, the timeout and the bound values, for the pool the report names. The request is answered with the task's id and the values bound, defaults included. |
| 5 | Worker | A worker claims the task and resolves the connection reference. A connection that is not SQL Server or Azure SQL is refused. |
| 6 | Worker | The statement is parsed once more, since the queue row is data read from a database. |
| 7 | Worker | The statement runs inside a READ COMMITTED transaction, each value sent as a typed parameter. The transaction is always rolled back, on every path, cancelled or not. |
| 8 | Worker | Rows are read up to `maxRows`. At the cap the read stops and the answer is marked `truncated`. The command carries `timeoutSeconds` as its timeout. |
| 9 | Client | The answer is read from the task: `GET /api/v1/datasources/tasks/{taskId}?waitMs=20000` holds the request until the task ends or the wait is over. |

To run a query the control plane opens no connection to a datasource. A report's query travels as every other read of live data on request does: a reference on the compute queue, resolved by the worker that runs it ([how compute tasks execute](data-operations.md#how-it-executes)).

The operation cannot be queued any other way. `POST /datasources/tasks` refuses `reportQuery`, and names the report route.

### The answer

The task's `result`, once it has succeeded:

| Member | Holds |
| --- | --- |
| `columns` | Each column's `name` and the database's type name for it (`dataType`). |
| `rows` | The rows, each cell as text, null for NULL ([limits](../flow/rpt.md#limits)). |
| `rowCount` | How many rows the answer holds. |
| `truncated` | `true` when the row cap stopped the read before the rows ran out. |
| `database` | The database the statement ran in, as the connection reported it. |
| `sql` | The statement that ran. |

A task that failed carries `error` in place of `result`, with anything that looks like a credential removed. A task no worker claims within `Dispatch:TaskQueuedExpiryMinutes` (15) is failed, which is what a `pool` no worker serves comes to.

### Where the connection comes from

The task belongs to the report's repository. It is queued carrying the values the control plane supplies for that repository's `${env:NAME}` references, so a report resolves its connection exactly as a run of a flow of the same repository does ([values the control plane supplies with a run](connections-and-secrets.md#values-the-control-plane-supplies-with-a-run)). With no such values, the worker resolves the reference from its own environment.

## Where a statement is checked

Whether a statement is one read-only `SELECT` is decided by the T-SQL parser, in one place every check asks ([what a statement may be](../flow/rpt.md#what-a-statement-may-be)):

| Where | A statement that is refused |
| --- | --- |
| `sqlflow validate <file>` | Makes the document invalid: exit code 1, each refused query with the reason. |
| A catalog sync, and `sqlflow lineage` | Is a warning that names the report and the query. The report is kept. |
| `GET /reports/{reportId}` | Sets the query's `problem` to the reason, which the page shows in place of an answer. |
| `POST /reports/{reportId}/queries/{query}/run` | Answers `400 The query cannot run`. |
| The worker | Fails the task with the reason. |

A report with one refused statement still shows its other queries.

## Who may run one

| Caller | List and read reports | Run a query | Read a query's answer |
| --- | --- | --- | --- |
| A signed-in person | yes | yes | yes |
| An assistant run | yes | no: `403 Not available to an assistant run` | no: `403` |

Any signed-in person may run any report: the control plane has no finer permission than signed in for reading and operating ([authentication and scopes](control-plane.md#authentication-and-scopes)). What a report can read is set by its `connection` and by who may merge a document into the repository.

A report's statement is not held to the column allow-list that governs what an assistant may read ([column policy](data-operations.md#column-policy-what-the-assistant-may-read-at-all)): it is a document its repository reviewed for people to read. For that reason an assistant run is kept out of the data. It may read a report as a document (its heading, parameters and statements), it cannot queue a query, and it may not read the answer of a query a person queued. What an assistant reads from the data stays what an approved AI context opens, through a prepared query ([assistant run tokens](authentication-and-identity.md#assistant-run-tokens)).

## What is recorded

A report stores nothing of its own, and nothing caches an answer: every run of a query asks the database again. What each run leaves is its compute task, the row every ad-hoc read of a datasource leaves (`catalog.ComputeTask`):

| Recorded | Holds |
| --- | --- |
| Who asked | `requestedBy`: the signed-in account, by its subject. |
| What ran | The operation (`reportQuery`), the connection reference, and the task's arguments: the report's name and the query's, the statement, the database, the values bound, the row cap and the timeout. The task list names the two as its target, `<report>: <query>`. |
| When, and where | When the task was queued, started and ended, the node that ran it, and its status. |
| The outcome | The answer, rows included, or the error. |

The row is kept as the **Compute tasks** storage area keeps it (`sqlflow.compute-tasks`): nothing is removed until an admin sets how long the area's data stays, and from then on a finished task older than that goes with its answer ([Maintenance](maintenance.md#the-assistants-conversations-and-query-results)).

## On the page

- The **Reports** page (`/reports`) lists the estate's reports by their `batch`, with a search. Opening one opens the report's own page (`/reports/{id}`).
- Every shown query runs when the page opens, each on its own, so one slow query does not hold the others.
- Parameters are controls above the tiles. Their values are kept in the page's address (`?p.<name>=<value>`), so a link reproduces the view. A parameter left at its default is not in the address; one that was cleared is written with nothing after it.
- Changing a parameter runs again only the queries that bind it. While the new answer is read, the tile holds the answer before it, dimmed.
- Each view is a tile. Its menu shows the answer as a table, downloads it as CSV (the values as the query answered them), shows how it was computed (the statement, the values bound, where it ran, the rows and the time it took), and runs the query again.
- **Refresh** runs every query again. A running query can be stopped, from its tile or its menu, which cancels it on the worker.
- An answer cut at `maxRows` says so on its tile, since a total over a cut answer is not the whole.
- A tile whose query cannot run, failed or waits for a required parameter says which and why, in place of an answer.
- A click on a mark of a view with `selects` sets that parameter to the mark's `by` value, and a second click clears it ([views](../flow/rpt-views.md)).

## API

All under `/api/v1`. The two `GET /reports` routes are on the read surface, the run and the cancel on the operate surface.

| Request | Answer |
| --- | --- |
| `GET /reports` | The estate's reports: every active pipeline of kind `rpt`, by batch and title, at most 500. Each with its `id`, repository, `name`, `title`, `description`, `batch`, path, `connection`, `database`, and how many queries, views and parameters it has. `repoId` narrows to one repository. A synced document that no longer reads as a report is listed with `problem`, the reason. |
| `GET /reports/{reportId}` | One report as its page reads it: its heading, its `parameters` (each with `usedBy`, the queries that bind it) and its `queries` in the document's order (each with the connection, database, timeout and row cap it runs with, its `sql`, the parameters it binds, its `views` and `columns`, and `problem` when the statement cannot run). `404` for an id that is no report. `422 The report does not load` when the synced text no longer reads. |
| `POST /reports/{reportId}/queries/{query}/run` | Queues one query. The body is `{ "values": { "<parameter>": "<value>" } }`, every value text as its type writes it. A parameter left out takes its default, and one given null or empty was cleared and binds NULL. `202` with the task's id, its status, the query's name and the values bound. `404` for an unknown report or query (the answer lists the report's queries), `422` when the report does not load, `400 The query cannot run` otherwise. |
| `GET /datasources/tasks/{taskId}` | The task, with `result` once it has succeeded. `waitMs` (at most 20000) holds the request until the task ends. `403` for an assistant run when the task is a report's query. |
| `POST /datasources/tasks/{taskId}/cancel` | Cancels the query: `200` when it was still queued, `202` when it is running and its worker is told to stop, `409` when it has already ended. |

## Known limits

- **SQL Server and Azure SQL only.** A report's statement is T-SQL, and a worker refuses a connection of any other kind.
- **One connection per query.** A query runs on the report's connection, or on the one it names itself.
- **No stored results.** A report shows what the database answers now. There is no earlier answer to compare with, and nothing to schedule.
- **An answer is bounded.** At most 5000 rows, 600 seconds and 8,000,000 characters, whatever the document asks for ([limits](../flow/rpt.md#limits)).

## See also

- [Report (flowType: rpt)](../flow/rpt.md): the document and its keys.
- [Report views](../flow/rpt-views.md): how an answer is shown.
- [Write a report](../guides/write-a-report.md)
- [Data operations](data-operations.md): the compute queue and the read-only parser a report shares with a question's query.
