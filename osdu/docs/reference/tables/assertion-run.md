---
id: delivery-table-assertion-run
title: "osdu.AssertionRun: one run of an assertion flow's tests in a partition, and how they came out"
type: table
summary: "One row per run of an assertion flow: the tests it was asked for, its status and counts by outcome, a hash of the definitions it ran, and who asked."
keywords:
  - osdu.AssertionRun
  - assertion run table
  - assertion flow
  - data quality tests
  - test report
  - junit
related:
  - delivery-concept-database
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
  - delivery-cli-assertions
  - delivery-table-assertion-result
  - delivery-wiki-0010-assertion-flows-read-only
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
---

# osdu.AssertionRun: one run of an assertion flow's tests in a partition, and how they came out

One run of an [assertion flow](../flow/assertion.md#the-report)'s tests in one partition: the tests it ran, how they came
out, and who asked. Each test's result is a row of [`osdu.AssertionResult`](assertion-result.md), written the moment the
test finishes, so a long run shows its progress and a stopped one keeps what it found. An assertion flow keeps its ledger
in the partition (kind `assertion`).

Pruned by age, a run whole, and only once later results of the same tests superseded every one of its results, so every
test's latest result stays ([Availability and retention](../concepts/availability-and-retention.md#retention)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `AssertionRunId` | `bigint` identity | no | The run's id, unique on its own. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow in the partition ([`osdu.Ledger`](ledger.md)). |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `RunId` | `uniqueidentifier` | yes | The platform run the tests ran in. |
| `Actor` | `nvarchar(200)` | no | Who asked for the run. |
| `Selection` | `nvarchar(max)` | yes | The tests the run was asked for, as JSON (an object of tests and tags); null when it ran every test. |
| `Status` | `nvarchar(16)` | no | `running`, `passed`, `failed`, `errored` or `cancelled`. |
| `Tests` | `int` | no | The tests the run took up, those skipped included. |
| `Passed` | `int` | no | Tests that passed. |
| `Failed` | `int` | no | Tests that failed. |
| `Warned` | `int` | no | Tests that passed with warnings: an assertion failed at severity `warning`. |
| `Errored` | `int` | no | Tests that could not be evaluated. |
| `Skipped` | `int` | no | Tests skipped. |
| `DefinitionsHash` | `nvarchar(64)` | yes | A hash of the tests the run ran, as their definitions stood: two runs of the same tests hash the same. |
| `StartedUtc` | `datetime2` | no | When the run started. |
| `CompletedUtc` | `datetime2` | yes | When it ended. |
| `Error` | `nvarchar(4000)` | yes | The redacted error of a run that errored. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_AssertionRun` | `PartitionId`, `AssertionRunId` | primary key | none | none | A partition's runs together. |
| `IX_AssertionRun_AssertionRunId` | `AssertionRunId` | unique | none | none | A run named by its id: its report, and the results that close it. |
| `IX_AssertionRun_PartitionId_FlowId_StartedUtc` | `PartitionId`, `FlowId`, `StartedUtc` | index | none | none | A flow's runs in a partition, newest first. |
| `IX_AssertionRun_RunId` | `RunId` | index | none | none | The platform run's row. |

## Written and read by

- **Written** by the assertion runner as a run opens and as its results close it (`AssertionRunner.cs`, through
  `OsduLedger.Assertions.cs`), and pruned by `POST /api/v1/delivery/ledger/prune`.
- **Read** by the GUI's **Assertions** board and an assertion flow's **Reports** tab, `sqlflow assertions`, and the
  delivery API's `/flows/{pipelineId}/assertion-runs`, `/assertion-runs/{id}` and `/assertion-runs/{id}/report`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the identity of the
  flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
