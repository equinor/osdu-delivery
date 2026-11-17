---
id: wiki-reports-are-documents-run-on-demand
title: "A report is a flow type that is never run: a document of read-only queries a person runs on demand"
type: decision
summary: "Why flowType rpt is a pipeline row, takes no schedule and keeps no result, why a worker runs its queries, and why it is closed to assistant runs."
keywords:
  - report
  - flowType rpt
  - why a report cannot be scheduled
  - run on demand
  - read-only query
  - compute queue
  - typed parameters
  - no sql from a client
  - server catalog
  - column allow-list
  - assistant run
  - no stored results
  - health check
  - rejected alternatives
sourceRefs:
  - src/SqlFlow.Yaml/YamlReportFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Core/Reports/ReportFlow.cs
  - src/SqlFlow.Core/Reports/ReportSql.cs
  - src/SqlFlow.Core/Query/QueryParameter.cs
  - src/SqlFlow.Core/Query/QueryModels.cs
  - src/SqlFlow.Core/Compute/ComputeTaskModels.cs
  - src/SqlFlow.SqlServer/Query/ReadOnlyQueryGuard.cs
  - src/SqlFlow.SqlServer/Query/ReportStatements.cs
  - src/SqlFlow.SqlServer/Query/SqlServerQueryRunner.cs
  - src/SqlFlow.Execution/ComputeTaskExecutor.cs
  - src/SqlFlow.ControlPlane/Api/ReportEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DescribedLineage.cs
  - src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - src/SqlFlow.ControlPlane/Background/RunDispatcher.cs
  - src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - src/SqlFlow.Catalog/ComputeTaskStore.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.HealthCheck/HealthCheckSqlBuilder.cs
  - src/SqlFlow.Cli/Program.cs
referenceRefs:
  - flow-rpt
  - flow-rpt-views
  - concept-reports
  - guide-write-a-report
  - concept-data-operations
  - concept-connections-and-secrets
  - concept-maintenance
  - concept-authentication-and-identity
related:
  - wiki-lineage-descriptions-beside-the-graph
  - wiki-semantic-layer-is-the-allow-list
  - wiki-ai-contexts-are-files-an-admin-approves
  - wiki-dispatch-in-control-plane
updated: 2026-11-17
---

# A report is a flow type that is never run: a document of read-only queries a person runs on demand

A `flowType: rpt` document declares read-only queries that a person runs from the report's page. What the document
holds, how a query runs and what the API answers are in [Report](../../reference/flow/rpt.md),
[Report views](../../reference/flow/rpt-views.md) and [Reports](../../reference/concepts/reports.md). This page records
why it has that shape.

## What it is for

A report is a fixed set of questions about a database, written once and looked at whenever someone wants to know:
how many rows the staging area holds per source, what arrived lately. Two surfaces already read live data, and neither
is that. A question's query is composed for one question and approved by a person each time it runs. A health check
(`hc`) is a flow: it runs on a schedule and scores a metric over time. A report is a document a team keeps, and nothing
about it runs until a person opens it.

## A flow type, stored as a pipeline row

A report is a YAML document in a repository, read by the same loader as every flow document. That gives its text one
home, and it gives the report everything a flow document already has: it is reviewed in git like a flow, the editor
opens it, `sqlflow validate` checks it, a sync stores it with its definition. It took the path a lineage description
had just taken ([the decision](lineage-descriptions-beside-the-graph.md)): a kind every loader reads and no host
registers, because it has no operation a client could offer.

Two other homes were weighed against that one:

- **A table of its own for reports.** As for a lineage description, it would have needed a second sync path, a second
  document page and a second search surface for what is one more YAML file in the repository.
- **An object authored in the GUI.** A report's statements read live data over the estate's own connection. Written in
  a repository, a statement is seen by whoever reviews the change before anyone can run it. Written in a page, it is
  SQL arriving from a client, which is what the rest of this design exists to rule out.

The price is the one a lineage description paid. A pipeline row is read by everything that lists or counts pipelines,
so the rule that left descriptions out of those was widened to every kind that is not a flow
(`PipelineListings.ThatAreFlows`). And a pipeline is identified by its name, so the estate scan takes a report out of
the flow set when it has the name of a flow that runs: a report read first would otherwise become that flow's
pipeline, and the flow would never run again.

## Never run as a flow, and no schedule

A report's rows are read when a person looks. A run of it as a flow would have nothing to do: it moves no data, and
it keeps no result. So the kind declares no operation and refuses every run with one sentence
(`ReportFlowKind.NeverRuns`), through the rule a lineage description already used (`YamlDocumentLoader.NeverRunReason`):
a trigger, a schedule, the command line and the executor all ask it, before anything is queued.

Its reader refuses `schedule`, `mode` and `lifecycle` with the reason, where the envelope every other kind shares
would have accepted them. A schedule on a report would be a promise that something happens at that time, and nothing
would.

## A worker runs it, through the queue and the runner that were already there

To read live data for a client, the control plane opens no connection to a datasource. That is the product's
credential model: a connection travels as a reference, and the node that does the work resolves it
([connections and secrets](../../reference/concepts/connections-and-secrets.md)). A report's `connection` is such a
reference, and the reader refuses a connection string outright, since a worker is given a reference to resolve and
nothing else.

So a report's query is a compute task, as a browse of a datasource and a question's query are. It is one more
operation (`reportQuery`) on the existing executor arm and the existing runner (`SqlServerQueryRunner.RunAsync`), which
is where the guarantees live: the statement parsed again on the node, the transaction that is always rolled back, the
row cap, the timeout, the trimmed cell. A second way to run a statement would have had to earn each of them again.

The task belongs to the report's repository (`ComputeTaskEnqueueRequest.RepoId`), so it resolves its connection with
the values that repository's runs are supplied with. Before this a compute task was work of no repository. A report is
a document of one, and its page has to reach the database a flow of that repository reaches, on any worker.

The alternative was a connection opened by the control plane for the page: no queue between a click and an answer. It
was rejected because every surface that reads live data on request (a browse, a check, a question's query) goes
through the queue, and a page that did not would be the one place where the control plane resolved a datasource's
credential for a client. And not every host reaches every database: `pool` exists on a report for the estate where
only some workers can.

## The statement comes only from the synced document

A run names a report and a query and gives values. It carries no statement, and the one other way to queue a compute
task refuses the operation. The statement is read from the report's synced text, with the reader that accepted it at
sync, so what the page shows as the statement and what runs are one text.

This is what lets any signed-in person run a report without the approval step a question's query has. That step exists
because the statement is composed at the time of asking, so a person has to see it before it runs
([ad-hoc business queries](../../reference/concepts/data-operations.md#ad-hoc-business-queries)). A report's statement
was seen when it was merged.

The same statement is still parsed wherever it is about to matter (`ReportStatements`, one place every check asks):
`validate` refuses the document, a sync warns and keeps the report so its other queries still show, the page is given
the reason for that query, a run is refused, and the worker parses once more because a queue row is data read from a
database.

## Values are typed parameters

A value a person sets is the one input a run takes from outside the repository. It is always sent to the engine as a
parameter and never written into the statement's text, so no value can change what the statement is. It is sent in its
declared type and not as text, so the engine compares it as that type and can use an index on the column it is compared
with.

The types are a closed set of six, each with one written form, so a value is the same text in a page's address, in a
request and on a queue row. A parameter nobody set is NULL, and one a person cleared is NULL whatever its default:
a default is where a page starts, and a person must be able to take it away.

The reader holds the two sides together. A statement may refer only to a declared parameter, and a declared parameter
must be bound by some query, since one that is not would be a control that changes nothing.

## A declared statement may read the server's catalog

The read-only guard refuses `sys`, `INFORMATION_SCHEMA` and the metadata functions to a statement composed for a
question. The reason is the column allow-list: it knows a restricted column by the estate's own objects only, so a
query over `sys.columns` would hand back the very column names the allow-list withholds
([the semantic layer is the allow-list](semantic-layer-is-the-allow-list.md)).

That reason is about what an assistant may read. A report's statement is declared by its repository, so it may read
the catalog as its author may. The guard therefore takes one flag, `declared`, that lifts that one refusal and keeps
every rule that makes a statement read-only. The flag is not something a payload sets: the worker derives it from the
operation, and the operation cannot be queued by a client.

## Closed to assistant runs, not held to the allow-list

Holding a report's statements to the allow-list would have kept an assistant from learning anything through a report
that an approved AI context does not open. It would also have limited a report to what those contexts open, and they
are written for what a model may read, not for what a team may look at
([AI contexts are files an admin approves](ai-contexts-are-files-an-admin-approves.md)).

A report's statement is a document its repository reviewed for people to read. So the allow-list is left out of it,
and the assistant is kept out of the data instead, at two places. The run route is not opened to an assistant run. And
the route a task's answer is read from, which is open to an assistant run because a prepared query's answer is read
there, refuses one for a report's task: without that, an assistant could read the answer of a query a person queued,
by its id. An assistant still reads a report as a document, statements included, as it reads any flow's YAML.

## Nothing the feature keeps

No answer is cached and none is stored by the report. Each run is a compute task, and the answer rests on the task's
row and nowhere else. That row also says who asked, which statement ran and with which values, and it leaves under the
limit an admin sets for the Compute tasks storage area
([Maintenance](../../reference/concepts/maintenance.md)), so a report adds no table and no retention rule of its own.

What one run may cost is bounded where it runs instead: the row cap stops the read, and the timeout is on the command.

## What was rejected

- **A connection opened by the control plane.** Against the credential model, as above.
- **Reusing the health check flow (`hc`).** It also reads a table through SQL its document declares. But it is a flow
  that runs and scores, and it composes its statements by concatenating the document's expressions and filter into the
  text (`HealthCheckSqlBuilder`). That is sound for fragments a repository wrote and no request supplies. A report
  takes values from a person, so its statements are whole and its values are parameters.
- **A schedule that materialises results.** It would make a report a flow after all, with a table of answers to keep,
  age and secure.
- **A separate reports table.** A second home for one YAML file, as above.

## What it leaves open

- A compute task records the statement and the values, not the report or the query they came from.
- A report runs on SQL Server and Azure SQL only: the guarantee that a statement is read-only is the T-SQL parser's.
- The reader cannot tell which columns a statement answers, so a column a view names is not checked against it.
