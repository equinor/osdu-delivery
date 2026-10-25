---
id: delivery-table-assertion-result
title: "osdu.AssertionResult: one test's result in one run of an assertion flow, with everything its assertions found"
type: table
summary: "One row per test of an assertion run: its outcome and severity, what it matched and evaluated, its definition hash, and the whole result as JSON."
keywords:
  - osdu.AssertionResult
  - assertion result table
  - test result
  - outcome
  - severity
  - sampled
  - definition hash
related:
  - delivery-concept-database
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
  - delivery-table-assertion-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
---

# osdu.AssertionResult: one test's result in one run of an assertion flow, with everything its assertions found

One test's result in one [run](assertion-run.md) of an assertion flow: its outcome, what it read and matched, and every
assertion with what it expected, what it found and the records that failed it. Written the moment the test finishes. A
result keeps the whole of what each assertion found, up to 1,000,000 characters; past that it keeps fewer examples,
marked as cut back. Each result carries its test's definition hash, so a result that answers an earlier definition of the
test shows as changed ([The report](../flow/assertion.md#the-report)).

Removed with its run, by the Maintenance page's cleanup of the assertion runs, which takes a run only once later results
of each of its tests superseded it ([What the osdu schema stores](../concepts/storage-and-retention.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ResultId` | `bigint` identity | no | The result's id, unique on its own. |
| `AssertionRunId` | `bigint` | no | The run ([`osdu.AssertionRun`](assertion-run.md)). |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the flow in the partition. |
| `TestName` | `nvarchar(100)` | no | The test, as the flow names it. |
| `Kind` | `nvarchar(400)` | no | The OSDU kind the test reads. |
| `Outcome` | `nvarchar(16)` | no | `passed`, `failed`, `warned`, `errored` or `skipped`. |
| `Severity` | `nvarchar(16)` | yes | The heaviest severity among the assertions that failed: `error`, `warning` or `info`; null when none did. |
| `Matched` | `bigint` | yes | The records the test matched (the index's count, or the ids storage holds); null when it did not get as far. |
| `Evaluated` | `bigint` | yes | The records the test read and held its assertions to. |
| `Sampled` | `bit` | no | Set when more records matched than the test reads, and it evaluated the first ones as a sample. |
| `Assertions` | `int` | no | The assertions the test holds. |
| `FailedAssertions` | `int` | no | The assertions that failed. |
| `DefinitionHash` | `nvarchar(32)` | no | The hash of the test's definition when it ran, so a result says whether the test has changed since. |
| `DurationMs` | `bigint` | no | How long the test took, in milliseconds. |
| `Error` | `nvarchar(4000)` | yes | Why the test errored, redacted. |
| `Detail` | `nvarchar(max)` | no | The whole result as JSON: every assertion, what it expected and found, and its failing examples, up to 1,000,000 characters. |
| `StartedUtc` | `datetime2` | no | When the test started. |
| `CompletedUtc` | `datetime2` | no | When it finished. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_AssertionResult` | `PartitionId`, `ResultId` | primary key | none | none | A partition's results together. |
| `IX_AssertionResult_ResultId` | `ResultId` | unique | none | none | A result named by its id. |
| `IX_AssertionResult_PartitionId_AssertionRunId` | `PartitionId`, `AssertionRunId` | index | none | none | A run's report: its results in the order they were recorded. |
| `IX_AssertionResult_PartitionId_FlowId_TestName_AssertionRunId` | `PartitionId`, `FlowId`, `TestName`, `AssertionRunId` | index | none | none | One test's history, newest run first, and the latest result of each test of a flow: one seek per test. |

## Written and read by

- **Written** by the assertion runner as each test finishes (`AssertionRunner.cs`, through `OsduLedger.Assertions.cs`).
- **Read** by a run's report (JSON, Markdown, HTML or JUnit), an assertion flow's **Assertions** and **History** tabs,
  `sqlflow assertions status` and `report`, and the delivery API's `/assertions`, `/flows/{pipelineId}/assertion-matrix`
  and `/flows/{pipelineId}/assertions/{test}/history`.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the identity of the
  flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
