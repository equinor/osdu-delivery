---
id: delivery-cli-run
title: "Running an OSDU flow with sqlflow run and sqlflow trigger: operations, values, payload and result"
type: cli-command
summary: "What OSDU flows add to sqlflow run and trigger: each kind's operations, --set values and the partition, the --payload fields, the result and exit codes."
keywords:
  - run a delivery flow
  - "--operation"
  - "--payload"
  - "--set"
  - partition
  - deliver
  - redeliver
  - recordKeys
  - verify
  - reverse
  - undo
  - remove records
  - delete-ledger
  - refresh
  - trigger
  - "--full refused"
  - payload property refused
related:
  - cli-run
  - cli-control-plane
  - delivery-flow-delivery
  - delivery-concept-submissions
  - delivery-concept-partitions
  - delivery-concept-removal-and-reversal
  - delivery-cli-records
  - delivery-cli-config
sourceRefs:
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunParameters.cs
  - sqlflow/src/SqlFlow.Execution/DocumentExecutor.cs
  - sqlflow/src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/RetrievalExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/AssertionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DimensionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/InventoryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/CacheDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
---

# Running an OSDU flow with sqlflow run and sqlflow trigger: operations, values, payload and result

An OSDU flow runs like any SQLFlow flow: `sqlflow run <flow.yaml>` runs it on this machine, `sqlflow trigger` queues it
on the fleet, and a schedule fires it. The verbs, their other options, the run artifacts (`run.json`, `run.log`) and the
catalog write-back are SQLFlow's, documented in [sqlflow run](../../../../sqlflow/docs/reference/cli/run.md) and
[the control-plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md). This page covers what the OSDU flow
kinds (`delivery`, `retrieval`, `cache`, `assertion`, `dimension`, `inventory`) add: the operations each kind runs, the
values a run takes (the partition among them), the payload, the result, and the exit codes.

The command line is `sqlflow`: OSDU Delivery's CLI is SQLFlow's CLI with the OSDU verbs added. The options every
command shares (`--db`, `--json`, `-v`, the `.sqlflow/env` file) are described in
[CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md).

## Synopsis

```bash
sqlflow run <flow.yaml> [--operation <name>] [--set name=value]... [--payload <json> | --payload @<file>]
            [--db <conn-ref>] [--json] [--log-level info|debug|trace] [...sqlflow run's other options]

sqlflow trigger --repo <name|id> --flow <flow> [--operation <name>] [--set name=value]... [--payload <json> | @<file>]
            [...sqlflow trigger's other options]
```

## The kind arguments

`--operation`, `--set` and `--payload` are SQLFlow's generic arguments for a flow of a registered kind. They are parsed
and validated the same way by `run`, by `trigger` (on the control plane, before anything is queued) and by a schedule,
and they are recorded on the run, so its history says exactly what was asked.

| Option | Shape | Meaning |
| --- | --- | --- |
| `--operation <name>` | 1 to 32 characters: lower-case letters, digits and `-`, starting with a letter | Which of the kind's operations the run performs. Left out, the kind's default. |
| `--set name=value` | repeatable; at most 32 names; a name is an identifier of at most 64 characters, a value at most 1,000 characters without control characters | A run value: one of the flow's own `parameters`, or `partition` (see [The partition](#the-partition)). A name set more than once is given the list of its values, at most 100 of them, which only a parameter a delivery flow's `source.record.scope` binds takes ([several values of a scope parameter](../flow/delivery.md#several-values-of-a-scope-parameter)); every other parameter refuses a list. |
| `--payload <json>` or `--payload @<file>` | one JSON object, at most 64,000 characters, inline or read from a file | The kind's own arguments (see [The payload](#the-payload)). |

What SQLFlow refuses before the run starts, each as one `ERROR  <message>` line and exit 1:

```text
operation must be 1 to 32 characters of lowercase letters, digits and '-', starting with a letter.
Parameter '<assignment>' must be written as name=value.
At most 32 parameter values can be supplied.
payload must be at most 64000 characters of JSON.
--payload is not valid JSON: <parser message>
operation must be one of deliver, plan, intake, drain, verify, replan, sync, reverse, undo, remove, delete-ledger for 'delivery' flows; '<name>' is not.
```

SQLFlow's backfill flags (`--full`, `--from`/`--to`, `--file-pattern`, `--source-filter`) have no meaning for an OSDU
flow: each kind reads its source its own way. They are refused whether or not the run carries a kind argument, because
the kind's own check runs for every run of the kind: `sqlflow run <flow.yaml> --full` alone fails with the kind's
message and runs nothing, for example
`fullLoad do(es) not apply to 'delivery' flows; the replan operation reads every row of the scope again, and recordKeys in the payload scope a run to chosen records.`
A trigger from the API or the GUI answers 400 with the same message, and so does a node-scoped trigger whose backfill
window is anchored on an OSDU flow. `--assertions-only` is refused for every kind but `ing`.

## What a run needs

| Kind | Needs the module database for |
| --- | --- |
| `delivery` | Every operation: the ledger, the templates and the partition's cache live there. Without it a run fails, for example with `This host has no osdu database connection; the delivery ledger and caches are unavailable. ...` |
| `cache` | `refresh`: it writes the version into the partition's cache. |
| `assertion` | `test`: it keeps the report there. |
| `dimension` | `build`: it keeps the builds there, and reads the templates of the kinds the dimensions read. |
| `inventory` | `build`, `reconcile` and `remove`: it keeps the inventories there. |
| `removal` | Every operation: it compares with the delivery ledgers kept there, and keeps its runs and the deletions it finds in them. |
| `retrieval` | Nothing; when it has one, each `retrieve` is recorded in the flow's ledger. |

The module database is the catalog's: on a workstation `--db`, else `${env:SQLFLOW_CATALOG_DB}`, and on a node
`${env:SQLFLOW_CATALOG_DB}` alone ([The osdu module database in sqlflow db](db.md)). The flow's own references (its ingestion connection, the OSDU endpoint and credentials) resolve
on the machine that runs it, from the central configuration a run carries first ([sqlflow config](config.md)) and the
environment after. The control plane attaches that configuration to the runs it queues of every OSDU kind (an
inventory's `remove` run included); a `sqlflow run` on a workstation carries none and resolves from the environment
alone.

## The partition

A flow that works in partitions (it names them under `partitions`, or names neither them nor a
`data-partition-id` header and so serves every partition registered with the catalog) runs in the partition the run
value `partition` names: `--set partition=test`, a schedule's `values: { partition: test }`, or the partition picked in
the GUI's title bar when a run is started there. The value never reaches the flow's own parameters: it binds the flow,
so the ids, the `data-partition-id` header, the cache, the ledger and the central configuration of the run are that
partition's ([Partitions](../concepts/partitions.md)).

| Kind | No partition named | `partition=*` |
| --- | --- | --- |
| `delivery` | The registry's default when the flow serves it, else the flow's only listed partition; otherwise refused (`Flow '<name>' serves dev, test and no partition is the default; name the one this run or request targets, or make one the default.`) | Refused: `A run or request of delivery flow '<name>' acts in one partition; name it rather than '*'.` |
| `cache` | As for a delivery flow. | Refreshes every partition the flow serves, one after another. One that fails fails the run, naming it; the others stay refreshed. |
| `assertion`, `dimension`, `inventory` | As for a delivery flow. | Refused: one partition per run (a report, a build, an inventory describes one partition). |
| `retrieval` | It reads the partition its `source.headers` name. | Any partition is refused: `Retrieval flow '<name>' names no partitions: it retrieves from the partition its source.headers name, so a run cannot target '<id>'. Leave the partition out of its run.` |

A flow whose partition is its `data-partition-id` header refuses a partition too. A delivery, cache or retrieval flow of
that kind that declares a parameter named `partition` (one written before partitions existed) takes the value as that
parameter instead. A partition named for a flow that follows the registry has to be registered
([sqlflow partition](partition.md)).

## Operations

| Kind | Operation | What it does | Writes to OSDU |
| --- | --- | --- | --- |
| `delivery` | `deliver` (default) | Plan the rows the ingestion tables changed and deliver what renders differently. | yes |
| | `plan` | Plan the changed rows and report what a delivery would send, changing nothing. | no |
| | `intake` | Plan the rows into work batches without delivering them: a fan-out member's share of a plan. The flow's next deliver run sends what it planned unless a drain of it comes first. | no |
| | `drain` | Deliver the work batches a submission already planned. | yes |
| | `verify` | Compare what OSDU holds with what the ledger recorded (5,000 records a run, passing over records verified in the last 24 hours unless forced). With the flow's `verify.reconcile`, mark each drifted or missing record for redelivery, named under the verify's activity; the flow's next deliver run sends it again, whatever its incremental window reads. | no |
| | `replan` | Read every row of the scope again and deliver what renders differently now. | yes |
| | `sync` | Read the ledger's records from the ingestion tables and consolidate the ledger: record what it lacks, flag rows that changed unseen for the next run, report rows that are gone. Sends nothing. | no |
| | `reverse` | Put OSDU back as it was before one run or submission: remove what it created, restore the version it replaced, and block those records until their source changes or they are released. | yes |
| | `undo` | Undo what deliveries that did not complete left in OSDU. Deliver runs, and drains of the whole flow, end with the same sweep. | yes |
| | `remove` | Remove the records the payload names, by key or by a listing filter, from OSDU to the extent its `scope` asks, and with `purgeLedger` delete each one OSDU answered for from the ledger too. **Remove** on the Records tab and on a record's page queues it ([Removing records from OSDU](../concepts/removal-and-reversal.md#removing-records-from-osdu)). | yes |
| | `delete-ledger` | Remove every record of the ledger from OSDU (reversibly), then delete the whole ledger, so the next run reads every row and delivers each as a new record. | yes |
| `retrieval` | `retrieve` (default) | Retrieve the records the query matches into files on the lake, from where the last run stopped. | no |
| | `plan` | Count what the query matches and say where a retrieve would write. | no |
| `cache` | `refresh` (default) | Capture every declared type (from OSDU, a dictionary or an ingestion table) and merge it into the partition's cache, writing a version when the content moved. | no |
| | `plan` | Count what each declared type would hold, writing nothing. | no |
| `assertion` | `test` (default) | Run the tests (all, or those the payload names) against what OSDU holds, and record the report. | no |
| | `plan` | Check every test against the template of its kind and count what each would read, recording nothing. | no |
| `dimension` | `build` (default) | Read every distinct key of each dimension from the search index, read labels, clean them into values, and keep what changed. | no |
| | `plan` | Check every dimension against the templates and count the records each would read, keeping nothing. | no |
| `inventory` | `build` (default) | Read every id each inventory's kind holds, keep what changed, and compare every id with the ledgers of the partition. | no |
| | `reconcile` | Compare each inventory, as its last build left it, with the ledgers as they stand now. | no |
| | `plan` | Count the records each inventory would read, and say what would stop it. | no |
| | `remove` | Remove from OSDU the ids of one finding (`orphan`, `stale` or `forgotten`), for a flow whose document allows it. | yes |
| `removal` | `remove` (default) | Read every removal's key sources whole, compare them with its delivery flow's ledger, and take out of OSDU (reversibly) the records whose keys the source no longer holds and no run took out before, unless the guard holds what it found for an approval ([Removal flow](../flow/removal.md#a-run)). | yes |
| | `plan` | Read and compare the same way and say what a remove run would find and whether the guard would hold it, writing nothing. | no |

A source with interfaces runs the interfaces its payload selects, every one when it selects none: every selected
interface is checked first, then they run in waves of their dependencies, and an interface that stops takes only the
interfaces waiting for it along ([Interfaces](../flow/interfaces.md)). What each operation does to the ledger is on
[Submissions](../concepts/submissions.md) and [Removal and reversal](../concepts/removal-and-reversal.md).

## The payload

Everything that scopes or forces a run beyond its operation travels in the `--payload` JSON object, because SQLFlow's
own flags are kind-agnostic. The kind parses it strictly: an unknown property, a wrong type or a value out of range is
refused naming it (`payload property '<name>' is not one of ...`), at every boundary a run is validated at, so a payload
no engine path could honour is refused before anything is queued.

### A delivery flow's payload

| Field | Type | Meaning |
| --- | --- | --- |
| `force` | `true`/`false` | Look at every record past the whole-run gates (the tier 0 skip and an already completed submission). Each record's own hashes still decide what is sent; to send records again, use `redeliver`. A `verify` with `force` checks records verified in the last 24 hours too; an `undo` with `force` also takes the undos that failed as often as the sweep tries. |
| `submissionId` | UUID | The submission the run works on: a re-run, a fan-out member's share, or the submission a `reverse` run reverses. |
| `runId` | UUID | The run a `reverse` run reverses. Only `reverse` takes it. |
| `recordKeys` | array of UUIDs | The delivery keys the run is scoped to: at most 1,000, each once, or the 25,000 one removal takes on a `remove` run. A `deliver` run sends the named records again (everything of them, unless `redeliver` names a part); a `remove` run removes them. |
| `redeliver` | text | What a `deliver` run sends again, changed or not: `all`, `record` (the document; its datasets and bulk data keep what OSDU holds), `files`, `bulk`, `workflow`, or the older names `metadata` (the record) and `payload` (the files or the bulk data). Without `recordKeys`, every record the flow has delivered in the partition. A part the route does not send is refused, naming the parts it does. |
| `rerender` | `true`/`false` | Bring records up to date: render the named records, or every delivered record, again under today's rules and let each record's hashes decide, so only what renders differently is sent. A `plan` with `rerender` renders every record it reads and says what that would send. |
| `interface` | text | The one interface of a source the run works on. A run on records or slices, a `remove` run, and a `reverse` of a run, of a source with several interfaces has to name it; a run on a submission finds its interface from the submission. |
| `interfaces` | array of text | The interfaces a run of a source runs, each once (at most 200); every interface when left out. |
| `slices` | array of whole numbers | The key slices of `submissionId` a part of a split delivery delivers (`deliver`) or an `intake` plans: 0 to 1023, each once, neighbours on a `deliver`. Written by a split delivery, not by hand. |
| `rateShare` | whole number | How many parts of a split delivery share the flow's `reliability.rateLimitRps`, 2 to 65: a part sends under that share of it ([submissions](../concepts/submissions.md#many-nodes)). Written by a split delivery, not by hand. |
| `confirm` | text | The partition a `delete-ledger` run acts in, as the person who asked typed it. Required by `delete-ledger` and taken by no other operation of a delivery flow (an inventory flow's `remove` takes its own). |
| `scope` | text | How much of each record a `remove` run takes: `record` (reversibly), `previous` (the version before the latest written back), `history` (the earlier versions) or `everything` ([the scopes](../concepts/removal-and-reversal.md#removing-records-from-osdu)). Required by `remove`, exactly as written. |
| `filter` | object | The listing a `remove` run empties instead of naming `recordKeys`: `status`, `search` with `mode` (`prefix` or `contains`), `submissionId`, `deliveredBySubmissionId`, `runId`, `drifted` and `issue`, as the records list names them. The run resolves it when it removes the records and refuses a filter that matches more than 25,000. Any other property is refused. |
| `purgeLedger` | `true`/`false` | With `scope` `record` or `everything`, a `remove` run also deletes from the ledger each record OSDU answered for, keeping one line of it. |
| `references`, `partitionReferences` | objects | The central configuration the control plane supplies with every delivery, cache, retrieval, assertion, dimension and inventory run it queues ([sqlflow config](config.md)). Not written by hand. |

Which fields each operation takes:

| Operation | Takes | Refuses |
| --- | --- | --- |
| `deliver` | `force`, `submissionId`, `recordKeys`, `redeliver`, `rerender`, `slices` and `rateShare` (with `submissionId`), `interface`, `interfaces` | `slices` with `redeliver` or `rerender`; `rateShare` without `slices`; `redeliver` or `rerender` with `submissionId`; `rerender` with `redeliver` |
| `plan` | `force`, `submissionId`, `recordKeys`, `rerender`, `interface`, `interfaces` | `redeliver`, `slices`, `rateShare`; `rerender` with `submissionId` |
| `intake` | `force`, `submissionId`, `recordKeys`, `slices` and `rateShare` (with `submissionId`), `interface`, `interfaces` | `redeliver`, `rerender` |
| `drain` | `submissionId`, `rateShare` (with `submissionId`), `interface`, `interfaces` | `force`, `recordKeys`, `redeliver`, `rerender`, `slices` |
| `verify` | `force`, `recordKeys`, `interface`, `interfaces` | `submissionId`, `redeliver`, `rerender`, `slices`, `rateShare` |
| `replan` | `force`, `interface`, `interfaces` | `submissionId`, `recordKeys`, `redeliver`, `rerender`, `slices`, `rateShare` |
| `sync` | `recordKeys`, `interface`, `interfaces` | `force`, `submissionId`, `redeliver`, `rerender`, `slices`, `rateShare` |
| `reverse` | `submissionId` or `runId` (exactly one), `interface` | `force`, `recordKeys`, `redeliver`, `rerender`, `slices`, `rateShare`, `interfaces` |
| `undo` | `force`, `interface`, `interfaces` | `submissionId`, `recordKeys`, `redeliver`, `rerender`, `slices`, `rateShare` |
| `remove` | `scope` (required), `recordKeys` or `filter` (exactly one), `purgeLedger` (with `scope` `record` or `everything`), `interface` | `force`, `submissionId`, `redeliver`, `rerender`, `slices`, `rateShare`, `interfaces` |
| `delete-ledger` | `confirm` (required), `interface`, `interfaces` | `force`, `submissionId`, `recordKeys`, `redeliver`, `rerender`, `slices`, `rateShare` |

Across every operation: `submissionId` and `recordKeys` never together; `interface` and `interfaces` never together;
`interfaces` never with `submissionId`, `recordKeys` or `slices`; `runId` only on `reverse`; `confirm` only on
`delete-ledger`; `scope`, `filter` and `purgeLedger` only on `remove`; and `tests`, `tags`, `dimensions`, `inventories`, `removal`, `entries` and `approve`, which belong to the other kinds, never.
A refusal names the field and why, for example
`payload redeliver does not apply to the plan operation: a plan sends nothing.`, and a property of another kind is
refused in the shape every kind uses (below):
`payload tests does not apply to a delivery flow: only an assertion flow's runs select tests; a delivery flow's payload names only force, submissionId, runId, recordKeys, redeliver, rerender, slices, rateShare, interface, interfaces, confirm, scope, filter and purgeLedger.`

A redelivery or a release of many records is planned by the next deliver run whole: the records the ledger was asked
to plan again are read by key, 5,000 to a pass and each pass in a submission of its own, before the run's own pass.

### Other kinds' payloads

Every kind refuses each payload property it does not take in one shape:
`payload <property> does not apply to a|an <kind> flow: <the runs that take it>; a|an <kind> flow's payload names only <what it takes>.`
A property counts when it asks for something (`force` set to `true`, a list that names something); one written as
`false` or empty asks for nothing and is not refused.

| Kind | Takes | Refused with, for example |
| --- | --- | --- |
| `cache` | `types`: the types to refresh or plan, at most 500 names, each once; every one when left out ([cache flow](../flow/cache.md#operations-and-the-run-payload)). | `payload force does not apply to a cache flow: only delivery and retrieval runs force; a cache flow's payload names only types.` |
| `retrieval` | `force`: restart an incremental retrieval at its declared start. | `payload submissionId does not apply to a retrieval flow: only a delivery flow's runs name a submission; a retrieval flow's payload names only force.` |
| `assertion` | `tests` (test names) and `tags` (every test carrying one of them), each at most 500 names, each once. With neither, every test runs. | `payload recordKeys does not apply to an assertion flow: only a delivery flow's runs are scoped to records; an assertion flow's payload names only tests and tags.` |
| `dimension` | `dimensions`: the dimensions to build; every one when left out. | `payload tests does not apply to a dimension flow: only an assertion flow's runs select tests; a dimension flow's payload names only dimensions.` |
| `inventory` | `inventories`: the inventories to build or reconcile. A `remove` run names exactly one inventory, `removal` (`finding`, `scope` `record` or `everything`, `expected`, and optionally `ids`) and `confirm`, the partition. | `payload runId does not apply to an inventory flow: only a delivery flow's reverse run names the run it reverses; an inventory flow's payload names only inventories, removal and confirm.` |
| `removal` | `entries`: the removals to carry out; every one when left out. An approval of what a guard held is `approve` (`expected`, the count the operator was shown waiting) with `confirm`, the partition, on a `remove` run naming exactly one removal; the Removals page sends it ([The guard](../flow/removal.md#the-guard)). | `payload tests does not apply to a removal flow: only an assertion flow's runs select tests; a removal flow's payload names only entries, approve and confirm.` |

A name that is not a test, tag, dimension, inventory, removal or type of the flow fails the run naming what the flow has, for example
`Assertion flow '<name>' has no test named '<x>'; its tests are <names>.`

## The result

Without `--json`, `sqlflow run` prints SQLFlow's shared line for a registered kind and the run's folder:

```text
OK  delivery 'welldb-wellbore-03-delivery' completed in <seconds>s
  run log: <flow folder>/.sqlflow/runs/welldb-wellbore-03-delivery/<yyyyMMdd-HHmmss>_<run id prefix>
```

or `FAILED  <error>` in place of the `OK` line. The run log says what the run did, step by step. With `--json`, stdout
carries the run's result object (camelCase, enum values as names, `operation` first), which is also the `result` of its
`run.json` and what the run page and the run list show:

| Run | Result carries |
| --- | --- |
| `deliver`, `replan` | `operation`, `submissionId`, `source`, `selection`, `status`, `recordCount`, this run's `planned`, `skippedUnchanged`, `awaitingApproval`, `skippedStale`, `unchangedAtPush`, `blocked`, `delivered`, `held`, `failed`, `retried`, `waiting` and `batches`, the parts it was split into (`members`), what a part planned of its rows (`share`, as an `intake` result), the passes over records asked to be planned again (`requestedPasses`, `requestedSubmissionId`, `requestedRecords`), `nothingToDo`, `error`, the submission's totals across every run (`submission`), what the closing sweep undid (`undone`) and `rowsLoaded` (the records delivered). |
| `plan` | `records`, `deliveries` (of them `metadata` and `payload`), `skips`, `awaitingApproval`, `stale`, `holds`, `blocked`, `untracked`, `slices`, `skippedWholeRun` and `skipReason`, `issues`, `renderedEvery`, of the deliveries those whose document fails an assertion of the mapping (`failingAssertions`) and those the check before sending would hold for one (`heldByAssertions`; [mapping assertions](../flow/mapping-assertions.md)), and `sample`: the first 20 records a delivery would send, each with its key, label, source key, action and reason. |
| `intake` | `submissionId`, `slices`, `records`, `planned`, `skippedUnchanged`, `awaitingApproval`, `skippedStale`, `held`, `blocked`, `untracked`, `batches`, `alreadyProcessed`. |
| `drain` | `submissionId`, `processed`, `delivered`, `unchanged`, `retried`, `held`, `failed`, `batches`, `waiting`, `undone`. |
| `verify` | `checked`, `matched`, `drifted`, `missing`, `errors`, `reconcile`. |
| `sync` | `checked`, `inAgreement`, `arrivalsRecorded`, `changedUnseen`, `deletedUnseen`, `plansRequested`, `restored`, `notFound`, `notFoundRecorded`, `foundAgain`, `withoutKey`, `notInLedger`, `notFoundSample`. |
| `reverse` | `reversalId`, `source`, `sourceId`, `records`, `taken`, `restored`, `removed`, `skipped`, `failed`, `open`, and `outcomes` across every run of the reversal. |
| `undo` | `records`, `removed`, `restored`, `gone`, `kept`, `superseded`, `failed`. |
| `remove` (delivery) | `scope`, `selected`, `removed`, `restored`, `alreadyGone`, `skipped`, `failed`, `purged` (of those removed or already gone, the ones deleted from the ledger), `records` (at most 200, failures first, each with its `deliveryKey`, `sourceKey`, `label`, `targetId`, `outcome`, `detail` and `submissionId`), `truncated`, `undone`, `cacheAfter`, `summary` and `rowsLoaded` (the records it changed in OSDU). A run OSDU refused any record of fails and still carries all of it. |
| `delete-ledger` | `partition`, `removed`, `alreadyGone`, `alreadyRemoved`, `neverInOsdu`, and what was deleted: `records`, `submissions`, `workBatches`, `watermarks`, `reversals`; `undone`. |
| A source with interfaces | `operation`, `source` and `interfaces`, each with its `interface`, `flowId`, `ledger`, `route`, `routeReason`, `waitsFor`, `waitReasons`, `wave`, `state` (`completed`, `stopped` or `skipped`), `reason`, `startedUtc`, `completedUtc` and `result` (what a run of it alone returns), with totals added up. |
| `refresh` | `scope` (the partition), `flow`, `version`, `previousVersion`, `written`, `capturedUtc`, `types` (each with its origin, records, change and the delivered records its changes reach) and `systemProperties`. A run over several partitions gives `partitions`, each with its `outcome` or `error`. |
| cache `plan` | `scope`, `flow`, `currentVersion`, `types` (each with what it would hold), `records`, `systemProperties`. |
| `plan` of a retrieval, assertion, dimension or inventory flow | What the plan counted: a retrieval's `kinds`, `total` and the `location` a retrieve would write to; an assertion flow's `tests` (each with what it matches and would read, the template it fits and its problems) and `testsLeftOut`; a dimension flow's `dimensions`; an inventory flow's `inventories`. |
| `retrieve` | `retrievalId`, `location`, `manifest`, the window (`windowField`, `windowFrom`, `windowTo`), `records`, `files`, `bytes`, `nothingToDo`, `kinds`. |
| `test` | `assertionRunId`, `flow`, `partition`, `status`, `tests`, `passed`, `failed`, `warned`, `errored`, `skipped`, `results` (the tests that did not pass, the first 50) and `resultsLeftOut`. |
| `build` (dimension) | `flow`, `partition`, `built`, `failed`, `skipped`, `dimensions`. |
| `build`, `reconcile` (inventory) | `flow`, `partition`, `completed`, `failed`, `inventories`. |
| `remove` (inventory) | `inventoryRemovalId`, `inventory`, `finding`, `scope`, `requested`, `removed`, `gone`, `skipped`, `failed`, `error`. |
| `remove`, `plan` (removal) | `operation`, `partition`, `removed` (across its removals) and `removals`, each with its `entry`, `deliveryFlow`, `removalRunId`, `status` (`completed`, `guarded` or `failed`), `tally` (the counts of [`osdu.RemovalRun`](../tables/removal-run.md)), `pending` and `awaitingApproval` after the run, `wouldHold` (a plan), `undone`, `cacheAfter` and `error`. |
| A run that failed before it produced an outcome | `operation` and `error`. |

## Examples

Plan the wellbores of the `test` partition, forcing past the change gates:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation plan --set partition=test --payload '{"force":true}'
```

Deliver the `dev` partition, then send two wellbores again, the record documents only:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev \
  --payload '{"recordKeys":["<delivery key>","<delivery key>"],"redeliver":"record"}'
```

Run only the well log interface of a source:

```bash
sqlflow run flows/welldb-03-delivery.yaml --set partition=dev --payload '{"interfaces":["welllogs"]}'
```

Put OSDU back as it was before one run, queued on the fleet:

```bash
sqlflow trigger --repo welldb --flow welldb-wellbore-03-delivery --set partition=dev \
  --operation reverse --payload '{"runId":"<run id>"}'
```

Undo what unfinished deliveries left, retrying the undos that used every try:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --operation undo --payload '{"force":true}'
```

Refresh the lookup cache of every partition the cache flow serves, and run the smoke tests of an assertion flow:

```bash
sqlflow run flows/welldb-lookups-00-cache.yaml --set 'partition=*'
sqlflow run flows/welldb-welllog-04-assertion.yaml --set partition=dev --payload '{"tags":["smoke"]}'
```

Read a long payload from a file:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --payload @keys.json
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | The run succeeded. |
| 1 | The run failed: its operation failed; an interface of a source stopped or was skipped; a cache partition of a run over several failed; a dimension build or an inventory failed; a removal stopped part way; a removal flow's removal failed or its guard held what it found for an approval; or an assertion flow's tests came out as its `failRunOn` says fails the run (a failed or errored test by default). The result still says what was done. Also exit 1, with one `ERROR  <message>` line and no run, for kind arguments refused before the run starts. |
| 130 | Interrupted with Ctrl+C. |

A catalog write-back failure never changes the exit code (SQLFlow's rule). `sqlflow trigger` exits as SQLFlow's
control-plane verbs do: 0 once queued, or with `--follow` by the outcome of the run.

## See also

- [sqlflow run](../../../../sqlflow/docs/reference/cli/run.md): the verb, its other options, run artifacts and catalog write-back.
- [The control-plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md): `trigger`, `runs`, `--follow`.
- [Delivery flow](../flow/delivery.md): the document a delivery run reads.
- [Submissions](../concepts/submissions.md): planning, work batches, fan-out and draining.
- [Removal and reversal](../concepts/removal-and-reversal.md): `reverse`, `undo` and `delete-ledger`.
- [sqlflow records](records.md): the ledger a run writes, record by record.
