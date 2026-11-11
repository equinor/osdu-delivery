---
id: delivery-flow-assertion-sets
title: "Assertion sets, titles and dimensions: the assertion dashboard and a set's report"
type: flow-reference
summary: "Group assertion flows by set and by dimensions (title, dimensions): one dashboard of where each stands now, mapped by two dimensions, that downloads as a page."
keywords:
  - set
  - assertion set
  - group assertion flows
  - dimensions
  - categorize assertions
  - title
  - assertion dashboard
  - where the data stands
  - heat map
  - board meeting
  - present the assertions
  - download the dashboard
  - set report
  - status of a set
  - latest status
  - disabled assertions and why
  - include disabled
  - not yet checked
  - open an assertion
  - expected and found
  - how far a count is off
  - largest gaps
  - worst first
  - short by
  - off by
  - failing records of a check
  - as it was written
  - changed since its last result
  - run a set
  - assertion-results route
  - only the latest result is kept
  - assertion run retention
yamlPath: "(root, flowType: assertion) set, title, dimensions; tests[].title, tests[].dimensions"
related:
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
  - delivery-cli-assertions
  - delivery-concept-gui
  - delivery-concept-storage-and-retention
  - flow-batch
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionDimensions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionSetReport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAssertionEndpoints.cs
  - osdu/gui/src/features/delivery/assertions/dashboard/dashboardModel.ts
  - osdu/gui/src/features/delivery/assertions/dashboard/AssertionDashboard.tsx
  - osdu/gui/src/features/delivery/assertions/dashboard/AssertionBrief.tsx
  - osdu/gui/src/features/delivery/assertions/dashboard/DashboardParts.tsx
  - osdu/gui/src/features/delivery/assertions/dashboard/checkBrief.ts
  - osdu/gui/src/features/delivery/assertions/dashboard/exportDashboard.ts
  - osdu/gui/vite.config.ts
---

# Assertion sets, titles and dimensions: the assertion dashboard and a set's report

An estate that tests what OSDU holds often has many [assertion flows](assertion.md): one per wellbore of a sheet of proof
values, one for the counts per area, one per kind. Three keys say what they are about, so a dashboard can lay out
hundreds of assertions as a few lines anyone can read: `set` gathers flows, `title` names a flow or a test for people,
and `dimensions` say what its assertions are about (an area, a source system, a wellbore).

```yaml
flowType: assertion
name: welllog-counts-south
set: well-logs
title: Well log counts in the South area
dimensions:
  Area: South
  Source: System A
tests:
  - name: logs
    title: Well logs in the South area
    dimensions: { Source: System B }
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    assert:
      - count: { atLeast: 1 }
```

| Key | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `set` | string | no | the flow's `batch` | The set the flow's tests are grouped under: a letter or digit followed by letters, digits, `.`, `_` and `-`, at most 100 characters, since a link, a route and the command line name it. Left out, the flow is in its `batch`'s set; a flow naming neither is in none. |
| `title` | string | no | the flow's `name` | What people read the flow as: one line, at most 200 characters. |
| `dimensions` | map | no | none | What the flow's assertions are about, name to value. Every test takes them. At most 20. |
| `tests[].title` | string | no | the test's `name` | What people read the test as on the dashboard: one line, at most 200 characters. |
| `tests[].dimensions` | map | no | none | The test's own dimensions over its flow's: a name the flow names takes the test's value in its place (above, `Source: System B`), a new name adds to them. |

A dimension's name is a letter followed by letters, digits, spaces, `_` and `-`, at most 40 characters; its value is one
line of at most 200 characters. Names are compared ignoring case. A test's `title` and `dimensions` are not part of its
definition, so changing them leaves its latest result standing (a changed query or check does not).

A key the flow cannot take is refused when it is read:

| Message | Cause |
| --- | --- |
| `<source>: set 'proof values' is not a set name: a letter or digit followed by letters, digits, '.', '_' and '-', at most 100 characters. Name the set the flow's tests are grouped under, such as set: welllog-proof-values, or take the key out to group them under the flow's batch.` | A `set` that is not a name. |
| `<source>: dimensions names '1st area', which is not a dimension name: a letter followed by letters, digits, spaces, '_' and '-', at most 40 characters, such as Country.` | A dimension name that is not one. |
| `<source>: dimensions names Set, which is the flow's set; name it with set: instead.` | `Set` among the dimensions. |
| `<source>: test 'logs': dimensions names Area twice, ignoring case; name each dimension once.` | The same name twice. |
| `<source>: dimensions.Area is empty; give the value the assertions have along it, or take the dimension out.` | A dimension without a value. |
| `<source>: dimensions.Area is not a value on one line of at most 200 characters.` | A value too long, or on several lines. |
| `<source>: dimensions names 21 dimensions; at most 20.` | Too many. |
| `<source>: title is not a title on one line of at most 200 characters.` | A title too long, or on several lines. |

A set and a dimension are only groupings: each flow still runs, keeps its runs and reports, and tests its partitions as
its own document says. A set holds as many flows as the board shows (500).

## Dimensions every assertion has

Three dimensions come without a word of YAML, so even an estate that names none can be mapped:

| Dimension | Value |
| --- | --- |
| `Set` | The flow's set; absent for a flow in none. |
| `Type` | The kind the test reads, as people name it: its entity type and version (`WellLog 1.4.0`). |
| `Check` | What the test checks, read off the checks it judges: `Proof values` (known values, a record set), `Counts` (a count, an aggregate of the records, groups, row counts), `Schema` (conformance, a mapping's rules), `References` (a field that resolves), `Legal tags`, `Delivery` (delivered, indexed), `Bulk data` (columns, monotonic, an aggregate of a column) or `Values` (any other field check). A test making several is named by the first in that order. |

A flow or a test that names `Type` or `Check` itself takes the place of the one read off the test. The board answers
each test's dimensions in order: `Set`, `Type`, `Check`, then the ones its documents name.

## Where an assertion stands

Every assertion is shown by where it stands now, as its document states it:

| Standing | When |
| --- | --- |
| Passing, Failing, Warning, Could not be checked | Its latest result says so, and that result judged the assertion as it is now. A pass whose proof values are past their `validUntil` is a warning. |
| Disabled | Its document disables it (the test, or every check of it), whatever an earlier run found, with the reason the document gives. |
| Not yet checked | No run has judged it, or its latest result judged an earlier version of it (its definition has changed since), which says nothing of this one. |

A result is the assertion's latest one that judged anything: a run that skipped it (the index had not settled, or it does
not run in the partition) leaves the result before standing. The health figure is the share of the assertions checked
that pass; disabled ones and those not yet checked are counted beside it, never in it.

## The dashboard

The GUI's **OSDU**, **Assertions** page is one dashboard of every assertion in the workbench's partition, and an
assertion flow's **Assertions** tab is the same dashboard of its own assertions. It reads from the top:

1. **The heading**, one row: the title with one pill per set beside it, each with its worst standing and how many of its
   checked assertions pass, and the controls at the right. A set picked narrows everything below to it, and the address
   keeps it (`?set=well-logs`), so a link names a set. **Run the set** runs each of its flows that can run in the
   partition, each with its document's default parameters.
2. **The summary**, one panel read left to right:
   - a ring of how the assertions stand, an arc a standing, with the health figure (the share of the checked that pass)
     in its middle;
   - the verdict in one sentence (`Needs attention: 7 assertions fail.`), which says where they are when at least half
     of them are in one square of the map (`..., 4 of them in South · Proof values.`), and under it what was checked and
     when (`20 of 32 checked pass · 9 not yet checked · last checked 11 Nov, 15:56`);
   - **Largest gaps**: the three failing assertions with the most that does not hold, each in words
     (`Well logs in the South area`, `Nothing found, 62 expected`) with its measure drawn. A click opens the assertion
     in the list, wherever the list is narrowed to;
   - the count of each standing: Failing, Could not be checked, Warnings, Not yet checked and Passing. A count is the
     filter to what it counts, and its hover says what the standing means; the counts are the legend of the ring and of
     every glyph below them.
3. **Where the data stands**: a map with a row per value of one dimension and a column per value of another. A square
   draws a mark for each of its assertions, in the glyph of where it stands, the ones that need a look first, with the
   worst that is there in words (`1 failing`, `all pass`); past 6 assertions it draws the first 5, counts the rest and
   says passing of checked (`18/20`). A square where all pass stays plain, so the ones that do not are what the eye
   finds. The map is drawn along the first dimension the documents name and along `Check`, and the reader picks any
   two. A dimension's values are laid out worst first, at most 12 rows and 7 columns, the rest gathered as **Other**. A
   square lists its assertions.
4. **The assertions**, grouped by what they need: **Needs attention** (failing, could not be checked, warnings) open,
   **Not yet checked** and **Passing** closed with their reasons gathered, 10 rows at a time. What needs attention is
   listed by where it stands, then the most that does not hold first: nothing of what was expected found before a near
   miss. A row reads as a line: the assertion, what does not hold of it (`172 short of 812 (21.2%)`,
   `2 of 5 rows do not hold: 1 differs, 1 is not found`, `Nothing found, 62 expected`) over the check that is of as its
   document names it (`812 wellbores`), how many more of its checks do not hold, and the measure, drawn. The check a row
   names is the one with the most that does not hold. One opens at a time, on [its brief](#an-open-assertion).

The dashboard reads the latest result of every assertion needing attention to rank them, up to 200 of them; past that
they are listed by title, each result is read as its row comes into view, and no largest gaps are named.

Disabled assertions are left out of all of it until **Include disabled** is switched on; then they are counted, mapped and
listed in a **Disabled** group, each with the reason its document gives. **Present** shows the dashboard full screen.
The page scrolls as one; nothing in it scrolls on its own. A flow whose document does not parse is named above the
verdict with its problem, since its assertions cannot be shown.

### An open assertion

An open assertion is written for someone who did not write it. It reads from the top, in a column kept to a width a
line reads at, with what it is about beside it:

- **What it checks**: its `description`; why it stands where it does when no result says so (disabled, not yet checked,
  could not be checked); and what its result notes, such as that a sample of the records was evaluated.
- **How many of its checks hold**, with one square per check in the order the document states them.
- **A card for each check that does not hold**: its label, and under it its `description`, else the question its type
  asks (`Does OSDU hold exactly what was delivered?`); its figures in a row, as wide as each other, the one that is the
  news in its tone under a label that says what it is; one bar that draws them; in one line what is left to say (what
  was measured, that a miss within a band only warns, the day the number was stated); and why, where that adds to them.
- **Where it does not hold**, a line under the card's figures that opens the records or rows that fail it. It is open
  when the assertion has one card, and a click away when it has several. The records are gathered by why: a reason many share
  is said once, with how many records share it and the first 5 of them under it, and the first 3 reasons show. A record
  whose reason lists the proof values it differs in has a table of them, one value a line: **Expected**
  (`60 ± 0.0001`), **Found** (`53.1875`, with the row it was read from) and **Off by** (`-6.8125`).
- **The checks that hold**, a line each: the label, what it expected and what it found. With more than 3 of them under
  cards they are folded behind one line.
- **Beside them**: the assertion's dimensions, the records read, when it was checked, the day its values were stated
  and the day they hold until, and its flow; under **How it was checked** its name, kind, query, template and report;
  then **Run again**, its report and its flow, and a step to the assertion before and after it in its group.

Where a check compares numbers, they are its figures and one bar draws them:

| Check | Its figures | The bar |
| --- | --- | --- |
| A `count`, or an `aggregate` of the records, against `equals`, `atLeast`, `atMost`, `greaterThan`, `lessThan` or `between` | **Expected** (`812`, `at least 5`, `172 to 458`), **Found**, and by how much it missed with its share of what was expected: **Short by** `172` `21.2%` or **Over by** for a count, **Below by** or **Above by** for any other measure. | What was found, then what it is short by, hatched as what is not there, up to what was expected; or, past what was expected, what it is over by. A tick stands where an end of the range is inside the bar. |
| A check judged record by record, a reference looked up, `delivered`, `indexed`, `legal` | How many were checked, how many **Pass**, and how many **Do not pass** with their share. | The part that passes, and the part that does not. |
| `knownValues` | **Rows checked**, how many **Hold**, and each part of the rest with its share: **In the warning band**, **Differ**, **Not found**, **Found more than once**. | The rows that hold, are within the warning band, differ, are not found (hatched) and are found more than once. |

A share of one thing is said in a line (`The row is not found.`). Any other check shows what it expected beside what it
found as text, as wide as each other. A check that is only noted (one of `info` severity that does not hold) is held
against nothing, so its figures and its bar are drawn plain.

The texts a run recorded are shown in plain words: `5 record(s)` reads `5 records`, `1 row(s) find no record` reads
`1 row finds no record`, and `atLeast 5` reads `at least 5`; a quoted value is left as it is. An answer of an OSDU
service that kept a check or an assertion from being made is said by the service and what it answered
(`The search service answered 503 Service Unavailable: ...`), on the row and in the brief, with the text as the run
recorded it (the address and the correlation id) under **As it was written**. A check that accounts for a delivery
flow's staged rows also offers **Show the rows in SQL** under its failing records: the T-SQL that reads them from the flow's own tables
([the rows in the source](staged-accounting.md#the-rows-in-the-source)).

### Download the dashboard

**Download**, **Dashboard** saves the dashboard as it is being read (the set picked, disabled assertions in or out) as one
HTML file, `{set}-{partition}-assertion-dashboard.html` (`all-sets-{partition}-...` for every set). It is the same
dashboard, built from the same sources into one script with the data and every assertion's latest result inside, so it
opens offline, in a board meeting or from an email, with the sets, the map, the filters and each assertion's checks, in a
light or a dark theme: the [design](../concepts/gui-designs.md) worn when it was downloaded, in its mode. It reads
nothing from the control plane once saved, so it shows no SQL of staged rows, which the control plane writes when asked.

## A set's report

With a set picked, **Download** also offers the set's report, rendered by the control plane from the same reading:

| Format | What it is |
| --- | --- |
| `md` | The figures, the flows at a glance, each assertion that needs attention with its checks, and the disabled, not yet checked and passing ones grouped: for a pull request or a wiki. |
| `json` | Every flow and test with its standing, its reason, its title, its dimensions and its latest result whole. |
| `junit` | A suite per flow and a case per assertion: a disabled one, or one not yet checked, is skipped with its reason. For a CI dashboard. |

The page to present is the dashboard; a set's report has no HTML format.

## The routes

Every route is a `GET` under `/api/v1/delivery`, in the partition `?partition=` names, else the workbench's:

| Route | Answers |
| --- | --- |
| `/assertions?set={set}` | The board, narrowed to the set's flows when `set` is given: each flow with its `title`, each test with its `title`, its `dimensions` and its latest result without its detail. |
| `/assertion-results?ids={id},{id}` | Tests' results whole, by the result ids the board gives (1 to 100 a read): `resultId`, `assertionRunId` and `result`. An id that names no result is left out; one that is not a number answers 400. |
| `/assertion-sets/{set}/report?format=md\|json\|junit` | The set's report, downloaded as `{set}-{partition}-assertion-report.{extension}`. Another format answers 400. |

A name that is not a set's answers 400; a set no active flow names answers 404:
`No assertion flow is in set '<set>': a flow names its set with set:, and one that names none is in its batch's.`

## Only the latest status is kept

A run that completes removes its flow's earlier runs in the partition that its latest status no longer needs, each whole
with its results: every finished run but the latest, unless it holds an assertion's latest result of the assertion as its
document states it now. So a flow keeps its newest run and the runs holding a result a later run did not replace, and no
history piles up behind them. A cleanup that fails is logged and left to the next run. The Maintenance page's limit for
assertion runs still removes what this does not reach, such as the runs of a flow that never runs again
([What the osdu schema stores](../concepts/storage-and-retention.md)).

## See also

- [Assertion flow](assertion.md): the tests, their assertions, and a run's report.
- [sqlflow assertions](../cli/assertions.md): where each test stands, from a terminal or a CI job.
- [Testing what OSDU holds](../guides/data-quality-tests.md): writing, running and scheduling tests.
