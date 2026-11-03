---
id: delivery-flow-assertion-sets
title: "Assertion sets (set): where a group of assertion flows stands now, and its report"
type: flow-reference
summary: "Assertion flows naming one set read as one: a line on the board, a dashboard of where each assertion stands now, and a report in HTML, Markdown, JSON or JUnit."
keywords:
  - set
  - assertion set
  - group assertion flows
  - set report
  - status of a set
  - board meeting report
  - latest status
  - last status of the assertions
  - disabled assertions and why
  - not yet checked
  - changed since its last result
  - run a set
  - assertion-sets route
  - only the latest result is kept
  - assertion run retention
yamlPath: "(root, flowType: assertion) set"
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
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionSetReport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/CheckView.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAssertionEndpoints.cs
  - osdu/gui/src/features/delivery/assertions/AssertionSetPage.tsx
  - osdu/gui/src/features/delivery/assertions/assertionBoardModel.ts
---

# Assertion sets (set): where a group of assertion flows stands now, and its report

An estate that tests what OSDU holds often has many [assertion flows](assertion.md): one per wellbore of a sheet of proof
values, one for the counts per project, one per kind. A set gathers them: the flows that name one `set` are read as one,
so its status is one line on the board, one page in the GUI, and one report to put in front of people.

```yaml
flowType: assertion
name: welldb-welllog-assertion-well-a
set: welldb-welllog-proof-values
```

| Key | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `set` | string | no | the flow's `batch` | The set the flow's tests are grouped under: a letter or digit followed by letters, digits, `.`, `_` and `-`, at most 100 characters, since a link, a route and the command line name it. Left out, the flow is in its `batch`'s set; a flow naming neither is in none. |

A `set` that is not a name is refused when the flow is read:
`set 'proof values' is not a set name: a letter or digit followed by letters, digits, '.', '_' and '-', at most 100 characters. Name the set the flow's tests are grouped under, such as set: welllog-proof-values, or take the key out to group them under the flow's batch.`

A set is only a grouping: each flow still runs, keeps its runs and reports, and tests its partitions as its own document
says. A set holds as many flows as the board shows (500).

## Where an assertion stands

Every assertion of a set is reported by where it stands now, as its document states it:

| Standing | When |
| --- | --- |
| Passed, Failed, Warning, Could not be checked | Its latest result says so, and that result judged the assertion as it is now. |
| Disabled | Its document disables it (the test, or every check of it), whatever an earlier run found. It is listed with the reason the document gives. |
| Not yet checked | No run has judged it, or its latest result judged an earlier version of it (its definition has changed since), which says nothing of this one. |

A result is the assertion's latest one that judged anything: a run that skipped it (the index had not settled, or it does
not run in the partition) leaves the result before standing. The pass rate is the share of the assertions checked that
pass; disabled ones and those not yet checked are counted beside it, never in it.

## The board and the set's page

The GUI's **OSDU**, **Assertions** board lists sets first: each set one line with how its assertions stand, when it was
last checked, its report and **Run set** (one run of each of its flows that can run in the partition, each with its
document's default parameters). A set opens on its flows, and a flow on its assertions. The flows that name no set are
listed last, under **Flows in no set**.

A set's page (`/delivery/assertions/sets/{set}`) is a dashboard that fits the window: the set's verdict in one sentence
(`Needs attention: 4 of 65 assertions fail, in 4 of 19 flows; 6 disabled, 38 not yet checked.`), its figures (each the
filter to what it counts), its flows worst first with each one's first issue, and the flow picked opening beside them on
its assertions. An assertion that failed, could not be checked or warned opens on its checks, each read once: what it
expected (with what qualifies that: a warning band, values left out, the day the values were stated), what it found, why
it came out as it did, and the records that fail it. Disabled assertions, and those not yet checked, are gathered by
their reason. The page runs the set, a flow, or the assertions not passing.

## The report

The set's report renders, from the same reading, as:

| Format | What it is |
| --- | --- |
| `html` | A self-contained page to present: the verdict, the figures, then one row per flow, worst first, that opens on its assertions and what each check expected and found. It needs no script, and prints on A4 as the overview with its rows closed. |
| `md` | The same in Markdown: the figures, the flows at a glance, each assertion that needs attention with its checks, and the disabled, not yet checked and passing ones grouped. |
| `json` | Every flow and test with its standing, its reason and its latest result whole. |
| `junit` | A suite per flow and a case per assertion: a disabled one, or one not yet checked, is skipped with its reason. |

The API serves a set, every route a `GET` under `/api/v1/delivery`, in the partition `?partition=` names, else the
workbench's:

| Route | Answers |
| --- | --- |
| `/assertions?set={set}` | The board, narrowed to the set's flows. |
| `/assertion-sets/{set}` | The set: its status and verdict, its flows as the board shows them, and each assertion's latest result whole. |
| `/assertion-sets/{set}/report?format=json\|md\|html\|junit` | The set's report, downloaded as `{set}-{partition}-assertion-report.{extension}`. |

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
