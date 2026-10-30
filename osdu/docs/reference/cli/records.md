---
id: delivery-cli-records
title: "sqlflow records: a delivery flow's records in the ledger, their history, issues, release and reversal"
type: cli-command
summary: "A delivery flow's ledger from a terminal: list or export records to Excel, one record's attempts, blocking issues, release, reverse a run, artifacts, undos."
keywords:
  - records
  - record history
  - export records
  - excel
  - xlsx
  - working list
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
  - removed keys
  - ledger deletions
  - lift removal
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
  - delivery-flow-removal
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/FindingLines.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryReversalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryArtifactVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRemovalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/RecordExport.cs
  - osdu/src/SqlFlow.Delivery/Storage/XlsxWorkbook.cs
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
in a script, without a control plane to reach. It lists an interface's records, exports them as an Excel workbook, shows
one record with every attempt it took, groups the blocked records into issues, releases blocked records once their cause is fixed, reverses what a run
or a submission delivered, and shows what deliveries created in OSDU and what unfinished deliveries left behind. It is
the same history the GUI's Records, Issues and record pages show, read from the same ledger
([The ledger](../concepts/ledger.md)).

The command line is `sqlflow`: OSDU Delivery's CLI is SQLFlow's CLI with the OSDU verbs added. The options every
command shares (`--db`, `--json`, `-v`, the `.sqlflow/env` file) are described in
[CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md).

## Synopsis

```bash
sqlflow records list      <flow.yaml> [--search <term>] [--contains] [--status <status>] [--issue <id>] [--max <n>]
sqlflow records export    <flow.yaml> --out <file.xlsx> [--search <term>] [--contains] [--status <status>] [--issue <id>]
sqlflow records show      <flow.yaml> --key <delivery key | source key> [--attempts <n>]
sqlflow records issues    <flow.yaml> [--issue <id>] [--max <n>]
sqlflow records release   <flow.yaml> [--key <delivery key>]... | [--issue <id>]
sqlflow records adopt     <flow.yaml> [--key <delivery key>... | --issue <id>] [--confirm <partition>]
sqlflow records reverse   <flow.yaml> (--run <id> | --submission <id>) [--preview]
sqlflow records reversals <flow.yaml> [--run <id> | --submission <id>] [--outcome <outcome>] [--max <n>]
sqlflow records artifacts <flow.yaml> --key <delivery key | source key> [--max <n>]
sqlflow records enrichments <flow.yaml> --key <delivery key | source key>
sqlflow records undos     <flow.yaml> [--max <n>]
sqlflow records removed   <flow.yaml> [--state <state>] [--after <delivery key>] [--max <n>]
sqlflow records lift-removal <flow.yaml> --key <delivery key>...

every form: [--interface <name>] [--partition <id>] [--db <conn-ref>] [--json] [-v|--verbose]
```

## Arguments and shared options

| Argument or option | Description |
| --- | --- |
| subcommand | `list`, `export`, `show`, `issues`, `release`, `adopt`, `reverse`, `reversals`, `artifacts`, `enrichments`, `undos`, `removed` or `lift-removal`. Anything else prints `ERROR  say what to do with the records: list, export, show, issues, release, adopt, reverse, reversals, artifacts, enrichments, undos, removed or lift-removal.` and the usage, exit 1. |
| `<flow.yaml>` | The delivery flow whose ledger is read. Without it: `ERROR  name the flow document whose records these are.` |
| `--interface <name>` | The interface whose ledger is read. A source with several interfaces has to name one: `Flow '<name>' delivers <n> interfaces (<names>); name the one this operation is for.` |
| `--partition <id>` | The partition whose ledger is read, for a flow that works in partitions (each partition keeps a ledger of its own). A partition named here is read as it is, even one taken out of the registry, since its records stay readable. Without it, the partition a run would take. |
| `--db <conn-ref>` | The catalog database, which holds the `osdu` schema. Without it and without `SQLFLOW_CATALOG_DB`, every form fails with `The osdu schema is read through the catalog's connection, which did not resolve: Environment variable 'SQLFLOW_CATALOG_DB' is not set. Give the command the catalog's connection with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.` See [The osdu module database in sqlflow db](db.md). |
| `--json` | One JSON document on stdout instead of the text form. Keys are sorted and properties without a value are left out. |

Records are named the way the product shows them: a **delivery key** (a UUID, which `records list` prints as 32
hexadecimal characters; either UUID form is accepted) or a **source key** (`welldb:WB-0001`, the source system and the key's parts joined by `/`).
Where a source key is accepted, it is looked up as a ledger search (a prefix of the source key, the label, the OSDU id,
the file name, or a key value or word of the label) and has to match exactly one record, so write it whole, with its system (`welldb:WB-0001`).

## records list

The interface's records, the most recently changed first, 50 unless `--max` says otherwise (at most 1,000).

| Option | Description |
| --- | --- |
| `--search <term>` | A delivery key (exact), or a prefix of the source key, the label, the OSDU id or the name of the file the row was landed from, or of one of the record's key values or a word of its label (`LOG-0002` finds `Wellbore B-1 / COMPOSITE / LOG-0002/1`). A prefix search is indexed. |
| `--contains` | Match `--search` anywhere in the source key, the label, the OSDU id or the ingestion file instead. It has no index, so it is refused when the rest of the filter leaves more than 100,000 records: `A contains search reads every record the rest of the filter leaves, and this filter leaves more than 100000. Use a prefix search, which is indexed, or narrow by status, submission or run first.` |
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
`planRequestedUtc` (when it was asked to be planned again; left out otherwise), `targetId`,
`targetVersion`, `waitingFor`, `mapping`, `attemptCount`, `lastDeliveredUtc`, `lastVerifiedUtc`,
`lastVerifyOutcome`, `lastError`, `sourceFileName`, `sourceRowNumber`, `issue`, and what the last check before sending
found: `validationOutcome`, `validatedUtc` and `assertionFailures` (how many judgements of the mapping's assertions
failed; left out when the mapping states none).

## records export

The interface's records a filter matches, every one rather than a page, as the Excel workbook the GUI's **Export** gives: a
working list of what to fix where the records come from. It takes the filter `records list` takes (`--search`,
`--contains`, `--status`, `--issue`) and writes at most 25,000 records, the listing's count limit. A filter matching more is
refused, and nothing is written: `The filter matches more than 25000 records as far as the listing counts, and an export
writes at most 25000 at once. Narrow it (a status, an issue, a submission, a run or a search) and export each part.` The
file is written beside its name and moved into place, so a failure never leaves half a workbook under it.

| Option | Description |
| --- | --- |
| `--out <file.xlsx>` | The workbook to write, required (`name the workbook to write with --out <file.xlsx>.`). A name not ending in `.xlsx` is refused (`--out '<path>' names no workbook: the export is an Excel workbook, so name a file ending in .xlsx.`). |

```text
welldb-wellbore-03-delivery: wrote 312 record(s) to /work/held-wellbores.xlsx
```

The **Records** sheet has a row for each record, sorted by record (its label, or its source key when its mapping gives
none) and then by source key, under a bold header row that stays in view and carries a filter: **Record**, **Source key**,
a column for each key column the flow declares (`source.record.key`, named as it names them) holding that part of the
record's key, **Status**, **Blocked**, **Issue** (the 16 characters `records issues` prints), **Problem** (the last error,
or why the record is held), **Source file** and **Source row** (where its newest version came from: the queued version's
while work waits), **Identities**, **Attempts**, **Next attempt (UTC)**, **Waiting for**, **Document waiting**,
**Delivered (UTC)**, **Verified (UTC)**, **Verify**, **OSDU id**, **OSDU version** (text, since its sixteen digits are more
than a spreadsheet number keeps), **Mapping**, **Updated (UTC)** and **Delivery key**. Times are UTC date-time cells and
counts are numbers; no text is read as a formula. The **About** sheet names the flow, interface, ledger and partition, the
filter in words, how many records were written, how they are sorted and when, and what each column holds.

The workbench's Records page exports what its lookup reaches the same way, at most the 1,000 records a lookup reaches,
with the flow, interface and partition of each record and, for a search, the values the term matched
([The OSDU pages](../concepts/gui.md#records)).

`--json`: `flow`, `flowId`, `file` (the full path written) and `records`.

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
      <the error as the records share it>
      for example <delivery key>  <source key>  (<file> row <n>)
        <that record's own error, when it says more than what they share>
```

What the records share is the pattern with every part they all name written in, and a placeholder (`<n>`, `<value>`)
where they differ; the listing reads it from each issue's newest and oldest record, `--issue` from its samples.

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
`set`), `values`, `pattern`, `shared` (the error as the records share it), `records`, `held`, `failed`, `oldestUtc`,
`newestUtc` and `example`. With `--issue`:
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

## records enrichments

What the mapping's enrichments added to one record, read from the ledger alone ([enrichments](../flow/enrich.md#what-the-ledger-keeps)):
each enrichment in the order it renders, with what it adds (`paths`, `records`, `bulk`, `workflow`), its enrichment
mapping, the kind of the records it adds and how they are written, whether it was set aside on the record's last render,
and where its rows came from; then every record it added, linked now or no longer linked, with the version OSDU holds, the
row it was rendered from and how many writes deliveries made of it. `--key` takes a delivery key or a source key.

```text
welldb-welllog-03-delivery: <record>: <n> enrichment(s), in the order they render
  lasfile: adds records (WellLogLasDataset@1.0.0), records of osdu:wks:dataset--File.Generic:1.0.0 written through dataset
      1 row(s) at las_files_20261028.csv#4
      linked      dev:dataset--File.Generic:<log>-enrich.lasfile v<version>, from las_files_20261028.csv#4, <n> write(s)
  quality: adds paths (WellLogGrade@1.0.0); set aside on its holds on the last render
      1 row(s) at log_quality_20261026.csv#118
```

A record its mapping enriches with nothing says `none. The record was rendered by a mapping that declares no enrich: block.`

`--json`: `flow`, `flowId`, `deliveryKey`, `record` and `enrichments`, each with `name`, `adds`, `mapping`,
`mappingFingerprint`, `recordKind`, `writtenThrough`, `setAside`, `rows` (`count`, `files`, `filesCounted`, `row`) and
`records`, each with `id`, `linked`, `version`, `hash`, `filesHash`, `source` and `writes`, each with `attemptId`, `atUtc`,
`runId`, `outcome` (how the record's try ended), `version` and `priorVersion` (left out for the write that created it).

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

## records removed

The ledger's deletions: the keys a [removal flow](../flow/removal.md) found the flow's source no longer holds, whichever
removal flow found them, in delivery key order, 50 unless `--max` says otherwise (1 to 1,000; any other count is refused). `--state` shows one state,
`pending`, `awaiting-approval` or `removed`; a value that is none of them is refused, naming them. The line under a page
names the `--after` of the next one.

```text
welldb-wellbore-03-delivery: 12 pending, 0 waiting for approval, 1040 removed
  0f1c...  removed            ["Wellbore B-2 B"]
      absent as of 2026-10-22T02:00:00Z; found by welldb-wellbore-05-removal (welldb-wellbore-03-delivery, run 13); removed after 1 try(ies)
  1a7e...  pending            ["Wellbore C-1"]
      listed as of 2026-10-20T08:15:00Z (removed/wellbores_2026-10-20.csv, row 9); found by welldb-wellbore-05-removal (welldb-wellbore-03-delivery, run 13); referenced after 2 try(ies)
      other records in OSDU still name it (...), so it stays until they are gone; the next run checks again
  more: --after 1a7e0000-0000-0000-0000-000000000000
```

Each deletion names its delivery key, its state and its key tuple; why its key counts as removed (`absent` from the
source's keys, or `listed` as removed) and as of when, the file and row that listed it, the removal flow, removal and run
that found it, what the last try came to and after how many, and why.

`--json`: `flow`, `flowId`, `counts` (by state), `next` and `removed` (each `deliveryKey`, `sourceKey`, `record` (the
ledger's record's status, left out when it holds none), `state`, `reason`, `removedUtc`, `fileName`, `rowNumber`,
`outcome`, `detail`, `tries`, `removalFlow`, `removal`, `detectedRunId`, `doneRunId`, `approvedBy`, `approvedUtc`,
`createdUtc`, `doneUtc`).

## records lift-removal

Lifts the deletions of the records `--key` names (at most 1,000), as `cli:<user>@<machine>`
([Delivering a record again](../flow/removal.md#delivering-a-record-again)): their keys are no longer kept out, and a
record the removal took out is asked to be planned again, so the flow's next run reads its row and delivers it under its
id. Every record lifted is named under one `lift-removal` activity. While the source still does not hold a key, the removal
flow's next run finds it removed again.

```text
welldb-wellbore-03-delivery: lifted the removal of 1 record(s); the flow's next run delivers them again.
  1a7e0000-0000-0000-0000-000000000000: the ledger keeps its key out for no removal
```

A key the ledger holds no deletion of is named and passed over, and the command exits 1. `--json`: `flow`, `flowId`,
`lifted` and `notRemoved`, the delivery keys of each.

## Examples

The failed wellbores of the `dev` partition:

```bash
sqlflow records list flows/welldb-wellbore-03-delivery.yaml --partition dev --status failed
```

The held wellbores of the `dev` partition, as a workbook for the people who fix them at the source:

```bash
sqlflow records export flows/welldb-wellbore-03-delivery.yaml --partition dev --status held --out held-wellbores.xlsx
```

One wellbore's history, by its source key:

```bash
sqlflow records show flows/welldb-wellbore-03-delivery.yaml --partition dev --key welldb:WB-0002
```

Every name record of a source an earlier deployment wrote to OSDU, adopted at once, then taken over by the next run:

```bash
sqlflow records adopt flows/welldb-namespaces-03-delivery.yaml --partition dev --confirm dev
sqlflow run flows/welldb-namespaces-03-delivery.yaml --set partition=dev
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
| 0 | The answer was printed, the release or reversal completed with no failed record, or every key a lift named was lifted. |
| 1 | A usage error; the flow, its partition or interface could not be settled; no module database; an export whose filter matches more records than it writes; a record, issue or reversal that does not exist; a reversal with a failed record; `records reversals` naming a source with no reversal; or a lift naming a key the ledger keeps out for no removal. Errors print one `ERROR  <message>` line on stderr, credentials redacted. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [The ledger](../concepts/ledger.md): what the ledger records and how a record is reconstructed from it.
- [Record lifecycle](../concepts/record-lifecycle.md): statuses, attempts, issues, holding and failing.
- [Removal and reversal](../concepts/removal-and-reversal.md): reversing a run, unfinished deliveries and their undo.
- [Operations runbook](../guides/operations-runbook.md): failed and held records, releasing, redelivering.
- [Running an OSDU flow](run.md): the `undo`, `reverse` and `deliver` operations as runs.
- [Removal flow](../flow/removal.md): how a removal flow finds the keys a source no longer holds, and the ledger's deletions.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
