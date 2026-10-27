---
id: delivery-guide-data-quality-tests
title: "Testing what OSDU holds after a delivery: assertion flows end to end"
type: guide
summary: "Write tests of delivered records, try them with a plan, run them, read the report, run them after every delivery and publish the results from CI."
keywords:
  - data quality tests
  - test delivered records
  - check osdu after delivery
  - assertion flow
  - smoke test
  - references resolve
  - schema conformance
  - test report
  - junit
  - failed test
  - errored test
  - skipped test
  - schedule tests after delivery
related:
  - delivery-flow-assertion
  - delivery-flow-known-values
  - delivery-cli-assertions
  - delivery-flow-mapping-assertions
  - delivery-flow-delivery
  - delivery-concept-templates
  - delivery-guide-getting-started
  - delivery-cli-run
  - flow-schedule
  - guide-notifications
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/AssertionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/TestEvaluator.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/Evaluators.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/BulkEvaluators.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/IndexSettling.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryAssertionVerbs.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
  - sqlflow/src/SqlFlow.Yaml/YamlScheduleLibraryLoader.cs
---

# Testing what OSDU holds after a delivery: assertion flows end to end

This guide adds tests to a delivery: an [assertion flow](../flow/assertion.md) that checks, after every delivery, that
the well logs the well database delivered are in OSDU as they should be. It starts with a smoke test, tries it with a
plan, runs it, reads the report, adds checks of the records and their curve data, runs the tests after every delivery,
and publishes the results from CI.

The tests read what users of OSDU see: the search index, storage, the legal service and the Wellbore DDMS. They never
write to OSDU.

## Before you start

- A delivery flow that delivers the kind, here `welldb-welllog-03-delivery` delivering
  `osdu:wks:work-product-component--WellLog:1.4.0` (its mapping's `template.kind`) to the partitions `dev` and `test`.
  See [the delivery flow](../flow/delivery.md).
- The template of that kind in a template folder on the assertion flow's path: the one the mapping fills. A test that
  reads fields is checked against it. See [template folders](../concepts/templates.md#template-folders).
- The module's database for `sqlflow` commands (`--db <conn-ref>` or `SQLFLOW_CATALOG_DB`), and the OSDU references the
  flow uses (`OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET`, `OSDU_SCOPE`).

A test finds its records with a search query. Give the records something to be found by: here the well log mapping,
`WellLog@1.0.0`, writes the source system's name, a literal, under `data`. An excerpt of `mappings/WellLog@1.0.0.yaml`
(the whole mapping is on [one source, several kinds](multi-kind-source.md#3-point-the-well-log-at-its-wellbore)):

```yaml
record:
  data:
    Source: welldb
```

## 1. Write a smoke test

Start with what proves the delivery reached users: the records are there, every record the delivery flow's ledger says
it delivered is found by search, and the index took each one whole. This is the start of the estate's
`flows/welldb-welllog-04-assertion.yaml`; steps 5 and 6 add two more tests, and the whole file, with a fourth
(`log-names`), is the [assertion flow's example](../flow/assertion.md#example).

```yaml
# flows/welldb-welllog-04-assertion.yaml, its top and first test: steps 5 and 6 add two more
flowType: assertion
name: welldb-welllog-04-assertion
batch: welldb
description: What the well logs of the well database look like in OSDU once they are delivered.

partitions: [dev, test]

parameters:
  system:
    default: welldb
    description: The data.Source value the delivered records carry.

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}
  ddmsRoot: /api/os-wellbore-ddms

defaults:
  maxRecords: 10000
  examples: 20

failRunOn: error

reliability:
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }

parallelism:
  recordsAtOnce: 4

tests:
  - name: logs-delivered
    description: Every log the delivery flow holds as delivered is in the search index, indexed cleanly.
    tags: [smoke]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    read: index
    assert:
      - count: { atLeast: 1 }
      - delivered: welldb-welllog-03-delivery
      - indexed: true
```

- `kind` is one type in one version, exactly as the mapping's `template.kind`; a wildcard is refused.
- `partitions` lists the partitions the flow tests; a run tests one of them and keeps its report under it.
- `{system}` is the flow's parameter; `{partition}` is always the partition the run tests.
- `read: index` reads the search hits instead of storage: enough for ids, and cheaper.

Check it offline:

```bash
sqlflow validate flows/welldb-welllog-04-assertion.yaml
```

```text
OK  'welldb-welllog-04-assertion' is valid (assertion: ${env:OSDU_URL} -> report).
```

`validate` checks the document's shape: one subject per assertion, the keys each subject takes, the operands, the
tokens. Whether the paths are properties of the kind is checked when a plan or a run reads the template.

## 2. Try it with a plan

A plan selects the tests, checks each against its template and counts what it matches and would read, without
evaluating anything or recording a report:

```bash
sqlflow run flows/welldb-welllog-04-assertion.yaml --set partition=dev --operation plan
```

The run log has a line per test, such as `plan logs-delivered (osdu:wks:work-product-component--WellLog:1.4.0): 1250 matching record(s)`,
and the result (`run.json`) lists for each test what it `matched`, what it `wouldRead`, the `template` version it was
checked against and its `problems`. A test whose query matches more records than it reads (10,000 unless `maxRecords`
says otherwise) is flagged there before you run it.

## 3. Run it

```bash
sqlflow run flows/welldb-welllog-04-assertion.yaml --set partition=dev
```

The run reads what this module's delivery flows changed lately, so a test of a type changed within its settle window is
skipped rather than judged (see [When a test is skipped](#when-a-test-is-skipped)), then runs the tests and records each
result the moment it is known. The run log names the partition and the report,
`assertion flow 'welldb-welllog-04-assertion' in partition 'dev': 1 test(s) selected; report 412`, and has a line per
test: `test logs-delivered: passed in 840 ms (1250 matched, 1250 read)`.

The run fails when a test failed or errored (`failRunOn: error`, the default), so `sqlflow run` exits 1, and a run on
the control plane fires the failure notifications. Its error names the tests:
`assertion flow 'welldb-welllog-04-assertion' in partition 'dev': 1 of 1 test(s) evaluated, 0 passed; 1 failed on an assertion of severity error (logs-delivered).`
Set `failRunOn: warning` to fail on warnings too, or `never` to only report.

To run some tests, name them or their tags in the payload:

```bash
sqlflow run flows/welldb-welllog-04-assertion.yaml --set partition=dev --payload '{"tags":["smoke"]}'
```

## 4. Read the report

At a terminal ([sqlflow assertions](../cli/assertions.md)):

```bash
sqlflow assertions status flows/welldb-welllog-04-assertion.yaml --partition dev
sqlflow assertions report flows/welldb-welllog-04-assertion.yaml --partition dev --format md
```

In the GUI, **OSDU**, **Assertions** is the board of every assertion flow in the partition picked in the
title bar, where each test is called an assertion and each of its assertions a check; the flow's pipeline has
**Assertions**, **History** and **Reports** tabs, and a report page runs the assertions that did not pass again.

A failed assertion says what it expected, what it found, how many records it checked and how many failed it, and names
the first failing records (20 unless `defaults.examples` says otherwise), each with its id, the value it held and why it
failed.

## 5. Check the records themselves

Add a test that reads each record as storage holds it (`read: storage`, the default) and checks it:

```yaml
  - name: log-headers
    description: Each log header meets the WellLog schema, and its references resolve.
    tags: [smoke]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    assert:
      - conforms: true
      - field: data.WellboreID
        resolves: master-data--Wellbore
      - field: data.Curves.CurveUnit
        resolves: reference-data--UnitOfMeasure
        severity: warning
      - name: a depth curve is among the curves
        field: data.Curves.Mnemonic
        in: [MD, DEPT]
        values: any
      - field: acl.viewers
        length: { atLeast: 1 }
      - legal: valid
      - unique: [data.WellboreID, data.Name]
        severity: warning
      - aggregate: missing
        field: data.SamplingInterval
        equals: 0
        severity: info
```

| Assertion | Checks |
| --- | --- |
| `conforms: true` | Each record meets the schema of its kind, as the test's template states it. |
| `resolves: master-data--Wellbore` | Each `WellboreID` names a wellbore storage holds. |
| `resolves: reference-data--UnitOfMeasure` | Each curve's unit names a unit of measure storage holds; only a warning. |
| `in` with `values: any` | Each log has a depth curve (`MD` or `DEPT`) among its curves; the assertion's `name` is what reports call it. |
| `length` | Each record names at least one viewer group (`acl.viewers` is the list itself, so its length is its count of groups). |
| `legal: valid` | Each record carries a legal tag, and every tag is valid now. |
| `unique` | No two logs share a wellbore and a name; only a warning, since the test is then `warned`, not `failed`. |
| `aggregate: missing` | How many logs carry no `SamplingInterval`; `info`, so it is reported and changes nothing. |

Every condition and subject is listed in [the assertion flow reference](../flow/assertion.md#assertions). A field
assertion holds for every record unless `for` says otherwise (`for: 95%`, `for: any`, `for: none`), and `where`
narrows the records it looks at.

A test reads at most `maxRecords` records (10,000 by default, at most 1,000,000). When the query matches more, the
assertions over the records are not evaluated and the test errors, saying so; narrow the query, raise `maxRecords`, or
set `sample: true` to judge the first ones and mark the result as a sample.

### A rule in the mapping, or a test

A rule about one record's own values can be stated in two places, and each has its use:

| | A mapping's `$assert` | An assertion flow's test |
| --- | --- | --- |
| Judges | Each record as the delivery renders it, before it is sent, and the value the row gives as well as the value the record carries | What OSDU holds after the delivery, as search, storage, legal and the DDMS answer |
| A record that breaks it | Held with its document kept, sent and recorded, or sent without the value, as the rule says | Reported; nothing is held or changed |
| Records it reaches | The records a run renders and sends; a record whose document does not change is not judged again | Every record the test's query finds, whoever delivered it and when |
| Rules across records | No | Counts, `unique`, aggregates, groups, the ledger (`delivered`), references (`resolves`), legal tags, bulk data |

State the rule in the mapping when a record that breaks it must not reach OSDU, or must not reach it with that value
([mapping assertions](../flow/mapping-assertions.md)). Test it in an assertion flow when it is about the set of records,
about what other systems wrote, or about records already delivered. One rule can serve both: once the delivery's mapping
states assertions, a test's `mapping` subject holds every record it reads to the mapping's record-stage assertions
([assertion flow](../flow/assertion.md#holding-records-to-a-mappings-assertions)):

```yaml
  - name: log-rules
    description: Each delivered log meets what its mapping asserts.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    assert:
      - mapping: WellLog@1.0.0
```

The mapping is read from the mappings the repository sync keeps, so the test judges records delivered before the
mapping stated its rules too.

### Values a sheet states about records it does not name

A QC sheet lists what a delivery should have produced, by the names people use: wellbore A/1-F-11, its COMPOSITE log,
the DT curve in `us/ft`. It names no OSDU id, and a log does not even hold its wellbore's name. A `knownValues` assertion
finds each record by full paths in the OSDU record and the values the sheet gives, reading another record through a
reference with a list of paths, as a dimension's label does, then holds the record to the sheet's proof values, each a
full path and its value:

```yaml
  - name: composite-log
    kind: osdu:wks:work-product-component--WellLog:1.5.0
    assert:
      - knownValues:
          locateBy:
            '[data.WellboreID, data.FacilityName]': A/1-F-11
            data.Name: COMPOSITE
          rows:
            - locateBy: { data.Curves.CurveID: DT }
              check:
                data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:us%2Fft:"
                data.Curves.TopDepth: { equals: 1023.0612, tolerance: 0.00005 }
```

Each row of the sheet becomes a row of proof values, and the result says, row by row, which hold, which differ and in
what, which are not found and by which path, and which find more than one record. Keep one flow per wellbore and one test
per log, so a report reads as the sheet does. See [Known values](../flow/known-values.md).

A number the sheet states as of a day can drift as more data lands. `warnWithin: 10%` beside a count, an aggregate or a
proof value lets it miss by that much as a warning, so the test is `warned` (amber on the board) rather than failed, and
the result says by how much it missed ([A warning band](../flow/assertion.md#a-warning-band)).

## 6. Check the curve data in the Wellbore DDMS

A test with a `bulk` block reads each record's bulk data from the Wellbore DDMS (under `source.ddmsRoot`, by default
`/api/os-wellbore-ddms`):

```yaml
  - name: log-curves
    description: Each log's curve data is in the Wellbore DDMS, indexed by a depth that only increases.
    tags: [bulk]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    maxRecords: 200
    sample: true
    bulk: { columns: [MD], maxRows: 2000000 }
    assert:
      - rowCount: { atLeast: 1 }
      - columns: { includes: [MD] }
      - column: MD
        monotonic: strictlyIncreasing
      - column: MD
        exists: true
        for: 99%
```

Each record's curve data is held to each assertion on its own; a record with no bulk data fails. `sample: true` with a
small `maxRecords` keeps the test to a sample of the logs, since a partition's curves run to millions of rows, and
`maxRows` bounds the rows read of one log. List in
`bulk.columns` every column the assertions name, or leave `columns` out to read exactly those; a list that leaves one out
is refused when the flow loads.

## 7. Run the tests after every delivery

Put the tests on the schedule the delivery runs on. A named schedule both flows join fires them as one group, ordered by
lineage: the assertion flow reads the type the delivery writes, so it runs after it.

```yaml
# schedules.yaml
schedules:
  welldb-welllog-hourly:
    cron: "0 * * * *"
    timezone: UTC
    values:
      partition: dev
      logSource: WIRELINE
```

Both flows then join it: the delivery flow's inline `schedule:` block becomes `schedule: welldb-welllog-hourly`, and the
assertion flow gains the same line. The schedule's `values` reach every flow that joins it, so the assertion flow
declares the delivery's `logSource` parameter too; a run given a value for a parameter it does not declare fails with
`<file>: parameter 'logSource' is not declared under parameters.` Its `parameters` then read:

```yaml
parameters:
  system:
    default: welldb
    description: The data.Source value the delivered records carry.
  logSource:
    description: Set by the schedule this flow shares with the delivery flow; the tests do not use it.
```

A scheduled run runs every test; the payload's `tests` and `tags` are for runs you start. See
[schedule](../../../../sqlflow/docs/reference/flow/schedule.md) for named schedules and their member sets.

### When a test is skipped

OSDU's search index takes a change from a queue, so for a while after a delivery it still lists the records as they
were. A run therefore reads from the ledger what this module's delivery flows wrote, removed or put back in the
partition within each test's `indexSettleSeconds` (300 by default), and skips a test whose entity type changed then.
The skipped result says which flow changed what and from when a run judges it. A skipped test does not fail the run.
Within the same hourly fire the tests after a delivery that changed logs are skipped, and the next fire judges them.
Set `indexSettleSeconds: 0` on a test to judge it whatever changed.

A test is also skipped, saying why, when it is `disabled: <why>`. Values counted on a day change as the source does: a
test that says when its values were stated (`asOf`) and how long they hold (`validFor: 90d`) is not judged past that, and
is flagged instead, a warning that it needs new proof values, so the board shows which tests to restate; `asOfField:
createTime` compares only the records OSDU had on that day
([disabling a test](../flow/assertion.md#disabling-a-test-and-the-day-its-values-hold-until)).

## 8. Publish the results from CI

```bash
sqlflow run flows/welldb-welllog-04-assertion.yaml --set partition=dev
status=$?
sqlflow assertions report flows/welldb-welllog-04-assertion.yaml --partition dev --format junit --out reports/welllog-tests.xml
exit $status
```

The JUnit file has a suite per kind and a case per test: failed is a failure, errored an error, skipped skipped, and
warned passes with its warnings in its output. See [sqlflow assertions](../cli/assertions.md#a-ci-job).

## When a test does not pass

| The result says | What it means and what to do |
| --- | --- |
| `failed` | An `error` assertion did not hold: the data is not as the test expects. Open the assertion's examples. |
| `warned` | Only `warning` assertions did not hold. The run passes unless `failRunOn: warning`. |
| `errored`, `The test's fields are checked against the template of <kind>, and no template folder on the path of ... holds template <kind>. ...` | The test reads fields and no template folder on the flow's path holds the kind's template. Write it into one (`sqlflow template capture --kind <kind> --out <folder>/<file>.json`, or download it on the Templates page) and run again. |
| `errored`, `'<label>': '<path>' is not a property of <kind> (template <version>); did you mean '<nearest>'?` | A path the schema does not have. Fix the path; nothing was read. |
| `errored`, `The query matches <n> records, more than the <m> this test reads, ...` | Narrow the query, raise `maxRecords`, or set `sample: true`. |
| `errored`, `Delivery flow '<flow>' keeps no ledger in partition '<p>': it has not delivered there, or is named differently.` | `delivered` names a flow that has no ledger in the partition. Check the name and the partition. |
| `errored`, `Delivery flow '<flow>' delivers 2 interfaces in partition '<p>' (...); name the one whose records this test reads with interface.` | Add `interface:` to the `delivered` assertion. |
| `failed`, `<n> record(s) differ between the ledger of <flow> and what OSDU holds.` | Each example says whether a delivered record is not found by the query, or (with `exact: true`) a found record was not delivered. A query too narrow, a record removed outside the ledger, or the index behind. |
| `failed`, a reference `names no record storage holds (or one this identity may not read)` | A `resolves` reference points nowhere, or the flow's identity may not read the record. |
| `failed`, `the DDMS holds no bulk data for the record` | The record has no curve data in the Wellbore DDMS. |
| `failed`, `its bulk data holds <n> rows, more than the test's bulk.maxRows <m>; ...` | Raise `bulk.maxRows`, or set `sample: true` to read the first rows. |
| `errored`, `Reading OSDU failed: <error>` | A search, storage, legal or DDMS request failed. The error says which, redacted. |
| `skipped`, `The search index may not list every change to <type> yet: ...` | The index has not settled after a delivery. The next run after the time it names judges the test. |
| `skipped`, `The test runs in <partitions>, not in '<p>'.` | The test's own `partitions` leave this partition out. |
| `errored`, `'<label>': no mapping <Name@version> is synced; ...` | A `mapping` assertion names a mapping no repository syncs. Check the reference, and sync the repository that declares it. |
| `errored`, `'<label>': mapping <Name@version> is synced from <n> repositories whose documents differ (...)` | Two repositories declare the reference with different documents. Give each its own version. |
| `failed`, `<path> fails "<assertion>": <why>` | A record OSDU holds breaks a rule its mapping states; each example names the record's first failure. |

## See also

- [Assertion flow](../flow/assertion.md): every key, subject, condition and outcome.
- [Mapping assertions](../flow/mapping-assertions.md): rules a mapping states and a delivery judges before it sends.
- [sqlflow assertions](../cli/assertions.md): the reports from a terminal or a CI job.
- [Running OSDU flows](../cli/run.md): operations, values and payloads.
- [Failure notifications](notifications.md): being told when a run fails.
