---
id: delivery-cli-assertions
title: "sqlflow assertions: read an assertion flow's test reports in a terminal or a CI job"
type: cli-command
summary: "List an assertion flow's test runs, show where each test stands, and write a run's report as JSON, Markdown, HTML or JUnit XML for a CI server."
keywords:
  - sqlflow assertions
  - assertions list
  - assertions status
  - assertions report
  - test report
  - junit xml
  - ci pipeline
  - test results
  - "--format"
  - "--run"
  - "--partition"
  - why a test run failed
cliCommand: assertions
related:
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
  - delivery-cli-run
  - delivery-cli-db
  - cli-run
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryAssertionVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionReport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/TestResult.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Json/CanonicalJson.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerbContext.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliModuleSet.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
---

# sqlflow assertions: read an assertion flow's test reports in a terminal or a CI job

## Synopsis

```bash
sqlflow assertions list   <flow.yaml> [--partition <id>] [--max <n>] [--json] [--db <conn-ref>]
sqlflow assertions status <flow.yaml> [--partition <id>] [--json] [--db <conn-ref>]
sqlflow assertions report <flow.yaml> [--partition <id>] [--run <n>] [--format json|md|html|junit] [--out <file>] [--db <conn-ref>]
```

## Description

`sqlflow assertions` reads the reports an [assertion flow](../flow/assertion.md)'s runs keep in the module's database:
which runs there were, where each test stands, and the full report of one run. It is one of the verbs OSDU Delivery adds
to SQLFlow's command line (the `sqlflow` binary, built from `SqlFlow.Delivery.Cli.Host`); every other verb, `run` among
them, is SQLFlow's.

It reads the module's database only: it does not reach OSDU or a control plane, so it works where an operator already
is, at a terminal or in a CI job. The flow document names the tests and the partitions; the reports come from the
ledger tables `osdu.AssertionRun` and `osdu.AssertionResult`.

Running the tests is a run like any other, through SQLFlow's [sqlflow run](../../../../sqlflow/docs/reference/cli/run.md)
or `sqlflow trigger` ([Running OSDU flows](run.md)):

```bash
sqlflow run welldb-welllog-04-assertion.yaml --set partition=dev --payload '{"tests":["log-headers"]}'
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `list`, `status` or `report` | yes | What to show. Anything else prints `ERROR  say what to show: list, status or report.` and the usage. |
| `<flow.yaml>` | yes | The assertion flow document. Without it: `ERROR  name the assertion flow document whose report this is.` and the usage. A path that does not exist fails with `Assertion flow file not found: '<path>'.` |

## Options

| Option | Applies to | Default | Description |
| --- | --- | --- | --- |
| `--partition <id>` | all | see below | The partition whose reports to read. |
| `--max <n>` | `list` | 20 | How many runs to list, newest first: 1 to 1000. Anything else fails with `--max '<value>' is not a count between 1 and 1000.` |
| `--run <n>` | `report` | the latest run | The report number (`assertionRunId`) to render. |
| `--format <f>` | `report` | `json` | `json`, `md` (or `markdown`), `html`, or `junit` (or `xml`). Anything else fails with `--format '<value>' is not one of json, md, html, junit.` |
| `--out <file>` | `report` | stdout | Write the report to this file (its folder is created) instead of the console. |
| `--json` | `list`, `status` | off | Print one JSON document instead of text. |
| `--db <conn-ref>` | all | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema the module's tables are read from. See [The module database](db.md). |

Without `--db` and `SQLFLOW_CATALOG_DB` every form fails with
`The osdu schema is read through the catalog's connection, which did not resolve: Environment variable 'SQLFLOW_CATALOG_DB' is not set. Give the command the catalog's connection with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.`

### Which partition is read

A flow that tests partitions keeps a report per partition (its ledger is `name@partition`):

- With `--partition`, that partition is read as named, even one no longer registered, since its reports stay. For a flow
  that names its partitions it has to be one of them: `Assertion flow '<name>' does not test partition '<id>'; it names dev, test.`
- Without it, the partition a run would test: the registry's default when the flow tests it, else the flow's only
  partition. When neither settles it: `Assertion flow '<name>' tests dev, test, and no partition is the default; name the partition this run tests.`
- A flow whose `source.headers` name its partition is read as it is; `--partition` on it is refused with
  `Assertion flow '<name>' names no partitions; it tests the partition its source.headers name, so a run cannot target '<id>'. ...`

## list

The flow's runs in the partition, newest first: the report number, status (`running`, `passed`, `failed`, `errored`,
`cancelled`), when it started, how its tests came out, and who ran it.

```text
$ sqlflow assertions list welldb-welllog-04-assertion.yaml --partition dev --max 2
welldb-welllog-04-assertion@dev: 2 run(s)
       418  failed     2026-10-09 03:05:12Z  3 passed, 1 failed, 0 warned, 0 errored, 0 skipped  by cli:ci@build-agent
            4 of 4 test(s) evaluated, 3 passed; 1 failed on an assertion of severity error (log-headers)
       412  passed     2026-10-08 03:05:09Z  4 passed, 0 failed, 0 warned, 0 errored, 0 skipped  by cli:ci@build-agent
```

A run that did not pass has its reason on the next line. For a run that completed, it is how many tests failed (on an
assertion of severity error) or errored, and which, then the tests that warned or were skipped:
`4 of 4 test(s) evaluated, 3 passed; 1 errored (log-names)`. For a run that stopped (`cancelled`, or `errored` because
the run itself could not go on), it is why it stopped. A completed run records a result for every test it selected
(passed + failed + warned + errored + skipped = tests); a stopped one records fewer. A run with no test to judge in its
partition asks OSDU nothing and completes `passed`, its tests skipped.
A flow with no run yet says `none yet. Run the tests with: sqlflow run <flow.yaml> (a plan checks them without running: --operation plan)`.

With `--json`: `flow`, `ledger` and `runs[]`, each run with `assertionRunId`, `runId`, `partition`, `status`, `tests`,
`passed`, `failed`, `warned`, `errored`, `skipped`, `actor`, `startedUtc`, `completedUtc` and `error`: the same reason,
set for every run that did not pass (how many tests failed or errored and which, or why the run stopped).

## status

Where each test of the flow stands, in document order: its latest result that was not skipped, or `not run`; what it
matched, how many of its assertions failed or errored, the run it came from, and `(changed since)` when the test's
definition has changed since that result.

```text
$ sqlflow assertions status welldb-welllog-04-assertion.yaml --partition dev
welldb-welllog-04-assertion@dev: 4 test(s)
  passed     logs-delivered  (osdu:wks:work-product-component--WellLog:1.4.0)  matched 1250, 0 of 3 assertion(s) failed, run 418 at 2026-10-09 03:05:20Z
  failed     log-headers  (osdu:wks:work-product-component--WellLog:1.4.0)  matched 1250, 1 of 8 assertion(s) failed, run 418 at 2026-10-09 03:05:41Z
  passed     log-curves  (osdu:wks:work-product-component--WellLog:1.4.0)  matched 1250, 0 of 4 assertion(s) failed, run 418 at 2026-10-09 03:06:02Z  (changed since)
  not run    log-names  (osdu:wks:work-product-component--WellLog:1.4.0)
```

A test that errored has its reason on the next line. With `--json`: `flow`, `ledger` and `tests[]`, each with `test`,
`kind`, `outcome`, `assertionRunId`, `matched`, `failedAssertions`, `completedUtc` and `changed`. JSON output sorts keys
and leaves out fields that have no value, so a test that has not run carries only `test`, `kind` and `changed`.

## report

The full report of one run, the latest unless `--run` names one: the run, and every test with each assertion, what it
expected, what it found, how many records it checked and failed, and the first records that failed it. It is rendered
by the same code as the control plane's report download, so it reads the same from either:

| Format | What it is for |
| --- | --- |
| `json` | Tools: the run and every result whole. |
| `md` | A pull request or a wiki: a summary, then each test with its assertions. |
| `html` | One self-contained page (no script, no external reference) to read, print or send. |
| `junit` | A CI server's test view: a suite per kind and a case per test. A failed test is a failure, an errored one an error, a skipped one skipped, and a warned one passes with its warnings in its output. |

```bash
sqlflow assertions report welldb-welllog-04-assertion.yaml --partition dev --format junit --out reports/welllog-tests.xml
```

With `--out`, the console's error stream says `Wrote the report of run 418 (failed) to <full path>`. The report is
written whatever the tests found. A `--run` from another flow, or the same flow in another partition, is refused:
`Assertion run <n> is a run of <flow> in <partition>, not of <flow>@<partition>.` Other failures:
`No assertion run <n>.`, `<flow>@<partition> has no run to report yet. Run its tests with: sqlflow run <flow.yaml>`, and
`--run '<value>' is not a report number.` (with the usage).

## Exit codes

| Code | When |
| --- | --- |
| 0 | The list, status or report was written, whatever the tests found. |
| 1 | A usage error, or a failure, which prints one `ERROR  <message>` line naming it. |
| 130 | Interrupted with Ctrl+C. |

To fail a CI job on the tests, rely on the run: `sqlflow run` exits 1 when the flow's `failRunOn` says its tests failed
it.

## A CI job

Run the tests, then publish the report whatever they found. The job's environment sets `SQLFLOW_CATALOG_DB` (or each
command takes `--db <conn-ref>`), and the OSDU references the flow uses (`OSDU_URL` and the credentials):

```bash
sqlflow run welldb-welllog-04-assertion.yaml --set partition=dev
status=$?
sqlflow assertions report welldb-welllog-04-assertion.yaml --partition dev --format junit --out reports/welllog-tests.xml
sqlflow assertions report welldb-welllog-04-assertion.yaml --partition dev --format html --out reports/welllog-tests.html
exit $status
```

The JUnit file feeds the CI server's test view; the HTML page is an artifact to read.

## See also

- [Assertion flow](../flow/assertion.md): the tests, their assertions and outcomes.
- [Testing what OSDU holds](../guides/data-quality-tests.md): from a first test to a scheduled, reported run.
- [Running OSDU flows](run.md): operations, payloads and results of every OSDU kind.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): how SQLFlow's CLI parses arguments.
