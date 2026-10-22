---
id: delivery-cli-records
title: "sqlflow records: a delivery flow's records in the ledger, their history, issues, release and reversal"
type: cli-command
summary: "Read and act on a delivery flow's ledger from a terminal: list records, one record's attempts, blocking issues, release, reverse a run, artifacts and undos."
keywords:
  - records
  - record history
  - attempts
  - failed records
  - held records
  - blocked records
  - issues
  - release
  - reverse a run
  - reversal
  - artifacts
  - undo
  - ledger lookup
  - source key
cliCommand: records
related:
  - delivery-concept-ledger
  - delivery-concept-record-lifecycle
  - delivery-concept-removal-and-reversal
  - delivery-guide-operations-runbook
  - delivery-cli-run
  - delivery-cli-preview
  - delivery-flow-mapping-assertions
  - delivery-cli-db
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/FindingLines.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryReversalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryArtifactVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ProblemSignature.cs
  - osdu/src/SqlFlow.Delivery/Protocols/TargetArtifacts.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/ReversalRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
---

# sqlflow records: a delivery flow's records in the ledger, their history, issues, release and reversal

`sqlflow records` reads and acts on a delivery flow's ledger where an operator already is, at a terminal, on a node or
in a script, without a control plane to reach. It lists an interface's records, shows one record with every attempt it
took, groups the blocked records into issues, releases blocked records once their cause is fixed, reverses what a run
or a submission delivered, and shows what deliveries created in OSDU and what unfinished deliveries left behind. It is
the same history the GUI's Records, Issues and record pages show, read from the same ledger
([The ledger](../concepts/ledger.md)).

The command line is `sqlflow`: OSDU Delivery's CLI is SQLFlow's CLI with the OSDU verbs added. The options every
command shares (`--db`, `--json`, `-v`, the `.sqlflow/env` file) are described in
[CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md).

## Synopsis

```bash
sqlflow records list      <flow.yaml> [--search <term>] [--contains] [--status <status>] [--issue <id>] [--max <n>]
sqlflow records show      <flow.yaml> --key <delivery key | source key> [--attempts <n>]
sqlflow records issues    <flow.yaml> [--issue <id>] [--max <n>]
sqlflow records release   <flow.yaml> [--key <delivery key>]... | [--issue <id>]
sqlflow records adopt     <flow.yaml> [--key <delivery key>... | --issue <id>] [--confirm <partition>]
sqlflow records reverse   <flow.yaml> (--run <id> | --submission <id>) [--preview]
sqlflow records reversals <flow.yaml> [--run <id> | --submission <id>] [--outcome <outcome>] [--max <n>]
sqlflow records artifacts <flow.yaml> --key <delivery key | source key> [--max <n>]
sqlflow records undos     <flow.yaml> [--max <n>]

every form: [--interface <name>] [--partition <id>] [--db <conn-ref>] [--json] [-v|--verbose]
```

## Arguments and shared options

| Argument or option | Description |
| --- | --- |
| subcommand | `list`, `show`, `issues`, `release`, `reverse`, `reversals`, `artifacts` or `undos`. Anything else prints `ERROR  say what to do with the records: list, show, issues, release, reverse, reversals, artifacts or undos.` and the usage, exit 1. |
| `<flow.yaml>` | The delivery flow whose ledger is read. Without it: `ERROR  name the flow document whose records these are.` |
| `--interface <name>` | The interface whose ledger is read. A source with several interfaces has to name one: `Flow '<name>' delivers <n> interfaces (<names>); name the one this operation is for.` |
| `--partition <id>` | The partition whose ledger is read, for a flow that works in partitions (each partition keeps a ledger of its own). A partition named here is read as it is, even one taken out of the registry, since its records stay readable. Without it, the partition a run would take. |
| `--db <conn-ref>` | The module database when the `osdu` schema lives in the catalog's database; `SQLFLOW_OSDU_DB` names it otherwise. Without a module database every form fails with `Records live in the module's database. Run 'sqlflow records' with --db <conn-ref>, or set the catalog variable.` See [The osdu module database in sqlflow db](db.md). |
| `--json` | One JSON document on stdout instead of the text form. Keys are sorted and properties without a value are left out. |

Records are named the way the product shows them: a **delivery key** (a UUID, which `records list` prints as 32
hexadecimal characters; either UUID form is accepted) or a **source key** (`welldb:WB-0001`, the source system and the key's parts joined by `/`).
Where a source key is accepted, it is looked up as a ledger search (a prefix of the source key, the label, the OSDU id or
the file name) and has to match exactly one record, so write it whole, with its system (`welldb:WB-0001`).

## records list

The interface's records, the most recently changed first, 50 unless `--max` says otherwise (at most 1,000).

| Option | Description |
| --- | --- |
| `--search <term>` | A delivery key (exact), or a prefix of the source key, the label, the OSDU id or the name of the file the row was landed from. A prefix search is indexed. |
| `--contains` | Match `--search` anywhere in the source key, the label or the OSDU id instead. It has no index, so it is refused when the rest of the filter leaves more than 100,000 records: `A contains search reads every record the rest of the filter leaves, and this filter leaves more than 100000. Use a prefix search, which is indexed, or narrow by status, submission or run first.` |
| `--status <status>` | One status, any case: `pending`, `delivering`, `delivered`, `held`, `failed`, `deleted`, `reverted` or `waiting` ([Record lifecycle](../concepts/record-lifecycle.md)). Anything else fails naming them. |
| `--issue <id>` | Only the records one issue keeps blocked: the 16 characters `records issues` prints for it. |
| `--max <n>` | Records listed; a number above 1,000 is taken as 1,000, and one that is not a whole number above zero is refused (`--max '<value>' is not a whole number of records above zero.`). |

```text
welldb-wellbore-03-delivery: 2 record(s)
  <delivery key>  Delivered   welldb:WB-0001
      <OSDU id> v<version>
  <delivery key>  Failed      welldb:WB-0002
      <OSDU id> (blocked)
      <the last error, on one line, cut at 240 characters>
```

A waiting record adds `(waiting for <what>)` after its OSDU id. A record says what moves it next where its status does
not: `(blocked)` until its row changes or it is released, `(asked to be planned again)` by the next run (a release, a
redelivery, a sync that found its row moved), or, held or failed with no document to send, `(nothing will move it:
release it)`. An empty ledger says `none. A flow's records appear
once a submission has staged them: an intake or a deliver run does that, while a plan run reports what it would do and
stages nothing.`

`--json`: `flow`, `flowId` and `records`, each with `deliveryKey`, `sourceKey`, `label`, `status`, `blocked`,
`planRequestedUtc` (when it was asked to be planned again, or null), `targetId`,
`targetVersion`, `waitingFor`, `mapping`, `attemptCount`, `lastDeliveredUtc`, `lastVerifiedUtc`,
`lastVerifyOutcome`, `lastError`, `sourceFileName`, `sourceRowNumber`, `issue`, and what the last check before sending
found: `validationOutcome`, `validatedUtc` and `assertionFailures` (how many judgements of the mapping's assertions
failed; null when the mapping states none).

## records show

One record and every attempt it took, newest first: what each did, how long it took, the steps it ran with what the
target answered, and the error that stopped it. `--key` is required (`name the record with --key <delivery key or
source key>.`); `--attempts` caps the attempts shown (20 by default, at most 1,000).

```text
welldb-wellbore-03-delivery: welldb:WB-0002
  key        <delivery key>
  status     Failed (blocked)
  osdu id    <OSDU id>
  mapping    Wellbore@1.0.0
  from       <file the row was landed from> row <n>
  delivered  <time>
  verified   <time> <Match | Drifted | Missing | Error>
  error      <the last error, on one line>
  checked    <valid | invalid | unverified>[, <n> assertion failure(s)] at <time>
  <n> attempt(s) shown, <n> on the work pending now; newest first:
    <completed>  <outcome>  <phase>  <seconds>s  <worker>
        version <n>
        <step> <HTTP status> <returned values>
        checked <the verdict of the check before sending>
          <where> fails "<assertion>" (<stage>, <onFail>): <why>
        <the attempt's error>
```

The record's `checked` line is what the last check before sending found: the outcome against the template and, when its
mapping states assertions, how many judgements of them failed ([mapping assertions](../flow/mapping-assertions.md)).
An attempt's `checked` line is its verdict
(`valid against osdu:wks:work-product-component--WellLog:1.4.0: 12 rule(s) met; 1 of 12 assertion judgement(s) of WellLog@1.4.0 failed (1 holding), first data.VerticalMeasurement.VerticalMeasurement "reference-elevation"`),
followed by each assertion failure it lists, at most ten, and `and <n> more failure(s)`; a try that checked nothing
prints none. A record held for an assertion has the hold as its error, starting `assertion:`.
An undo's attempt also lists every artifact it took and what became of it. A key that names no record fails with
`<flow> has no record for '<key>'.`; a source key matching several records fails with
`'<key>' matches <n> records of <flow> (<source keys>); name one by its delivery key.`

`--json`: `flow`, `flowId` and `record` (the keys `records list` gives, and `attempts`, each with `attemptId`,
`startedUtc`, `completedUtc`, `outcome`, `phase`, `worker`, `submissionId`, `runId`, `targetVersion`, `error` and
`result`, the attempt's recorded result as JSON, whose `validation.assertions` holds the findings).

## records issues

The issues keeping the interface's records blocked, the most records first (50 unless `--max` says otherwise, at most
1,000). An issue groups the held and failed records whose errors share a pattern ([Record lifecycle](../concepts/record-lifecycle.md)).

```text
welldb-wellbore-03-delivery: <n> blocked record(s) in <n> issue(s)
  <issue id>  <row errors | set error>, <n> record(s): <n> held, <n> failed; last changed <time> to <time>
      <the pattern the records' errors share>
      for example <delivery key>  <source key>  (<file> row <n>)
        <that record's own error>
```

`set error` is the same error in every record looked at, made once for the whole set (a dataset, a cache entry, a
mapping, a legal tag) and fixed once; `row errors` name different values, each its own row's, fixed in the rows
themselves.
Records blocked before the ledger kept issues are counted on an `and <n> blocked record(s) not sorted into an issue yet`
line; the control plane sorts them a page at a time. With no blocked record it says `none. A record is blocked when it is
held or fails, and stays so until its source changes or it is released.`

`--issue <id>` shows one issue: the files its records came from (the 20 with the most records) and five records spread
across it, with the values their errors name. `records list --issue <id>` lists all its records. An id the flow has no
blocked record under fails with `<flow> has no record blocked by issue <id>; 'records issues' lists the issues it has.`

`--json`: `flow`, `flowId`, `totalIssues`, `totalRecords`, `unsorted` and `issues`, each with `issue`, `shape` (`rows` or
`set`), `values`, `pattern`, `records`, `held`, `failed`, `oldestUtc`, `newestUtc` and `example`. With `--issue`:
`flow`, `flowId` and `issue`, which adds `files` (`fileName`, `records`) and `samples` (each record with its `values`).

## records release

Releases blocked records (held, failed, reverted, or removed by a reversal) once their cause is fixed, and held or failed
records nothing moves (not blocked, not asked to be planned again, no document to send, which an earlier version could
leave): every one of the interface, the ones `--key` names (repeat it; delivery keys only), or every one `--issue` keeps
blocked. A record that still holds a rendered document goes back to pending and is sent as it is; the others are planned
again from their rows by the flow's next run, which holds a record again if its row still calls for it, sends what renders
differently, and delivers again with nothing sent a record whose row renders what OSDU already holds.
The release is recorded on the audit trail as `cli:<user>@<machine>`'s, with every record it released named under it.

```text
welldb-wellbore-03-delivery: <n> record(s) released of the <n> named. The ones that still hold a rendered document are queued now; the rest are planned again by the next run.
```

`--key` and `--issue` together are refused (`release the records --key names or the ones --issue keeps blocked, not
both.`). A key that is not a UUID fails with `--key '<value>' is not a delivery key; 'records list' prints them.`

`--json`: `flow`, `flowId`, `issue` (when `--issue` named one) and `released`.

## records adopt

Asks held records to adopt the record OSDU already holds at the OSDU id each one's key makes, written by another system or
another instance of the delivery whose ledger this database does not hold ([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)):
every record of the interface held so, every one `--issue` keeps held, or the ones `--key` names (repeat it; delivery keys
only). Every record or an issue's takes the partition the ledger is kept in typed back as `--confirm`. Nothing is claimed
yet: the flow's next run takes each record over, at the version OSDU holds then, and writes its row there. A record held
for anything else, claiming an id, held by a delivery or asked already is left as it is. The adoption is recorded on the
audit trail as `cli:<user>@<machine>`'s, with every record it asked named under it.

```text
welldb-namespaces-03-delivery: <n> record(s) held for an id OSDU holds asked to adopt the records OSDU holds at their ids. The next run takes each over, at the version OSDU holds then, and writes its row there; a record held for anything else was left as it is.
```

`--key` and `--issue` together are refused (`adopt the records --key names or the ones --issue keeps held, not both; name
neither to adopt every record held for an id OSDU holds.`), and every record or an issue's without the partition typed back
is refused (`... type the partition back with --confirm dev.`).

`--json`: `flow`, `flowId`, `issue` (when `--issue` named one) and `asked`, the delivery keys asked.

## records reverse

Puts OSDU back as it was before one run or one submission of the interface: what it created is removed again,
reversibly, and what it updated gets back the version OSDU held before, record by record, every step written to the
ledger. Those records stay blocked until their source changes or they are released. It runs in this process, through
the flow's own route and credentials, so the flow's OSDU references have to resolve on this machine; it is recorded as
`cli:<user>@<machine>`'s. Asking again resumes a reversal that stopped. What a reversal can and cannot put back is on
[Removal and reversal](../concepts/removal-and-reversal.md).

Name exactly one source: `--run <run id>` or `--submission <submission id>` (`name what to reverse: --run <run id> or
--submission <submission id>, one of them.`).

`--preview` says what the reversal would reach and do, and writes nothing. It classifies the first 1,000 records in key
order:

```text
welldb-wellbore-03-delivery: run <run id> delivered <n> record(s) under <n> submission(s).
  Of them: <n> would be restored to the version OSDU held before, <n> removed (it created them), <n> decided by OSDU's version list.
  <n> would be passed over: <outcome>
  restore: <how the route restores a version>
  remove:  <how the route removes a record>
  Nothing was written. Run without --preview to reverse.
```

Without `--preview`:

```text
welldb-wellbore-03-delivery: reversal <id> of run <run id>: this run took <n> of <n> record(s): <n> restored, <n> removed, <n> passed over, <n> failed.
  across every run: <n> restored, <n> removed, ...
```

The failed records are taken again when the reversal is asked again; the output names the `records reversals` command
that lists them. The command exits 1 when a record failed.

`--json`: with `--preview`, `flow`, `source`, `sourceId`, `submissions`, `records`, `sampled`, `restore`, `remove`,
`resolvedFromOsdu`, `passedOver`, `restores`, `removes` and `reversalId` (an existing reversal of the source). Without
it, `flow`, `flowId`, `reversalId`, `source`, `sourceId`, `records`, `taken`, `restored`, `removed`, `skipped`, `failed`
and `outcomes`.

## records reversals

The interface's reversals, newest first (20 unless `--max` says otherwise; the ledger returns at most 200): each with its
id, state (`capturing`, `reversing`, `completed`, `failed` or `cancelled`), source, who asked and when.

With `--run` or `--submission`, that source's reversal: its state, who asked, its records counted by outcome, and the
error that stopped it. A source with no reversal prints `<flow>: no reversal of run <id> was asked for.` and exits 1.
`--outcome` adds the records the reversal settled that way (20 unless `--max` says otherwise, at most 1,000), each with
its delivery key, OSDU id, outcome and why. The outcomes are `restored`, `removed`, `already-gone`, `superseded`,
`unchanged`, `changed-in-osdu`, `missing-in-osdu`, `version-missing`, `busy`, `not-claimed`, `not-in-ledger`,
`not-reversible` and `failed`, and `pending` for the records not settled yet.

`--json`: an array of `reversalId`, `source`, `sourceId`, `status`, `requestedBy`, `requestedUtc`, `completedUtc` and
`error`; for one reversal, the same object with `recordCount`, `outcomes` and, with `--outcome`, `records` (each
`deliveryKey`, `targetId`, `outcome`, `detail`, `restoredVersion`, `newVersion`).

## records artifacts

What deliveries of one record created in OSDU, newest first (100 unless `--max` says otherwise, at most 1,000): each
dataset, session, version and the like, with its state, role and slot, its OSDU id or what finds it, its versions, the
unit of work that wrote it, who settled it and when, how often its undo was tried, and its note. `--key` takes a
delivery key or a source key; a record deleted from the ledger keeps its artifacts and is found by its delivery key.

The states are `intent`, `pending`, `live`, `superseded`, `due`, `removed`, `restored`, `gone`, `kept` and `failed`. An
artifact that is `intent`, `pending`, `due` or `failed` is open: an undo may still take it. An undo that failed 10 times
is no longer tried by the sweep: `no more tries: an undo run with force takes it`.

```text
welldb-wellbore-03-delivery: <record>: <n> artifact(s) shown, newest first; <n> open
  <state>  <role>  <slot>  <OSDU id or locator> v<version> (replaced v<version>)
      unit <unit id> began <time>; written <time>
      settled <time> by <who> in run <run id>
```

`--json`: `flow`, `flowId`, `deliveryKey`, `record` and `artifacts`, each with `artifactId`, `unitId`,
`unitStartedUtc`, `slot`, `role`, `targetId`, `locator`, `version`, `priorVersion`, `state`, `open`, `exhausted`,
`note`, `undoAttempts`, `nextUndoUtc`, `submissionId`, `createdRunId`, `createdUtc`, `updatedUtc`, `settledUtc`,
`settledRunId` and `settledBy`.

## records undos

What unfinished deliveries left in the interface's OSDU that an undo may still take, counted by state, and the records
holding it (50 unless `--max` says otherwise, at most 1,000), those whose undo used every try first. The undo itself is
a run of the flow: `sqlflow run <flow.yaml> --operation undo`, with `--payload '{"force":true}'` to retry the undos
that used every try ([Running an OSDU flow](run.md)).

```text
welldb-wellbore-03-delivery: <n> artifact(s) to undo (<n> due, <n> failed); <n> more of deliveries under way or abandoned (<n> intent, <n> pending)
  <n> failed 10 times and are no longer tried by the sweep: once what stops them is fixed, run 'sqlflow run <flow.yaml> --operation undo --payload {"force":true}'.
  <n> record(s) hold them, those whose undo has used its tries first:
  <delivery key>  <status>  <label or source key>
      <n> used every try, <n> due; the oldest written <time>; next try at <time>
```

With nothing left it says `no record holds anything an undo may still take.`

`--json`: `flow`, `flowId`, `intent`, `pending`, `due`, `failed`, `failedExhausted`, `toUndo`, `totalRecords` and
`records` (each `deliveryKey`, `sourceKey`, `label`, `status`, `targetId`, `intent`, `pending`, `due`, `failed`,
`failedExhausted`, `oldestUtc`, `nextUndoUtc`).

## Examples

The failed wellbores of the `dev` partition:

```bash
sqlflow records list flows/welldb-wellbore-03-delivery.yaml --partition dev --status failed
```

One wellbore's history, by its source key:

```bash
sqlflow records show flows/welldb-wellbore-03-delivery.yaml --partition dev --key welldb:WB-0002
```

Every name record of a source an earlier deployment wrote to OSDU, adopted at once, then taken over by the next run:

```bash
sqlflow records adopt flows/welldb-namespaces-03-delivery.yaml --partition dev --confirm dev
sqlflow run flows/welldb-namespaces-03-delivery.yaml --partition dev
```

What blocks the well logs of a source, then release one issue after fixing its cause:

```bash
sqlflow records issues flows/welldb-03-delivery.yaml --partition dev --interface welllogs
sqlflow records release flows/welldb-03-delivery.yaml --partition dev --interface welllogs --issue <issue id>
```

See what reversing a run would do, then reverse it:

```bash
sqlflow records reverse flows/welldb-wellbore-03-delivery.yaml --partition dev --run <run id> --preview
sqlflow records reverse flows/welldb-wellbore-03-delivery.yaml --partition dev --run <run id>
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | The answer was printed, or the release or reversal completed with no failed record. |
| 1 | A usage error; the flow, its partition or interface could not be settled; no module database; a record, issue or reversal that does not exist; a reversal with a failed record; or `records reversals` naming a source with no reversal. Errors print one `ERROR  <message>` line on stderr, credentials redacted. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [The ledger](../concepts/ledger.md): what the ledger records and how a record is reconstructed from it.
- [Record lifecycle](../concepts/record-lifecycle.md): statuses, attempts, issues, holding and failing.
- [Removal and reversal](../concepts/removal-and-reversal.md): reversing a run, unfinished deliveries and their undo.
- [Operations runbook](../guides/operations-runbook.md): failed and held records, releasing, redelivering.
- [Running an OSDU flow](run.md): the `undo`, `reverse` and `deliver` operations as runs.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
