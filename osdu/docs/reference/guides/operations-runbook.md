---
id: delivery-guide-operations-runbook
title: "Operations runbook: what to do about held, failed, waiting or stuck records, drift, and a run that went wrong"
type: guide
summary: "Task by task: find and fix held and failed records, release them, redeliver, check drift, sync with the source, recover stuck submissions, reverse a bad run."
keywords:
  - runbook
  - held records
  - failed records
  - release records
  - stuck delivering
  - stuck submission
  - drift
  - redeliver
  - bring up to date
  - sync timeline
  - reverse a run
  - waiting records
  - troubleshooting
  - issues tab
  - stopped submission
  - recover a stopped run
  - osdu id moved
  - boolean is not a number
  - column inferred as bit
related:
  - delivery-concept-record-lifecycle
  - delivery-concept-removal-and-reversal
  - delivery-concept-ledger
  - delivery-concept-submissions
  - delivery-cli-records
  - delivery-cli-run
  - delivery-concept-run-trace-and-metrics
  - delivery-concept-availability-and-retention
  - delivery-concept-storage-and-retention
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryReversalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryArtifactVerbs.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/FailureGuard.cs
  - osdu/src/SqlFlow.Delivery/Engine/Verify/Verifier.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/gui/src/module.tsx
  - osdu/gui/src/features/delivery/DeliveryFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordPage.tsx
---

# Operations runbook: what to do about held, failed, waiting or stuck records, drift, and a run that went wrong

This runbook takes the situations an operator meets with a running [delivery flow](../flow/delivery.md), one task at a
time: what you see, where to look in the GUI and on the command line, and what to do. Every action here goes through the
[ledger](../concepts/ledger.md), so it is recorded with who asked and when; none of them edits the ledger by hand, and
none is needed.

The examples use the flow `flows/welldb-wellbore-03-delivery.yaml`, delivering wellbores to the partitions `dev` and
`test`. `sqlflow` is SQLFlow's command line with the OSDU verbs added; the `records` verbs read the module database, so
give them `--db <conn-ref>` or set the catalog variable, `--partition` for a flow that serves several partitions, and
`--interface` for a source of several interfaces.

## Where to look

| Place | What it shows |
| --- | --- |
| A delivery flow's **Delivery** tab | Counts by status, the last submission, **Probe target**, **Sync timelines**, **Redeliver**, **Delete ledger**, **Unfinished deliveries to undo**. |
| The flow's **Records** tab | The records, filtered by status, search, **Drifted only**, issue, submission or run; the selection bar's **Release**, **Redeliver**, **Remove**; **Export**, the filtered records as an Excel workbook. |
| The flow's **Issues** tab | Blocked records grouped by the problem that keeps them blocked. |
| The flow's **Submissions** tab, a submission's page | Each plan, its counts, its batches, its attempts. |
| A record's page | **Timeline** (every attempt and activity), **Source**, **Render** (renders it now, sends nothing), **OSDU**, **Artifacts**. |
| OSDU, Ledger: **Records** and **Audit trail** | The lookup across flows by any value a record is known by; every run and intervention. |
| The run's trace | What a run is doing, live ([Run trace and metrics](../concepts/run-trace-and-metrics.md)). |

```bash
sqlflow records list   flows/welldb-wellbore-03-delivery.yaml --partition dev --status held --max 100
sqlflow records show   flows/welldb-wellbore-03-delivery.yaml --partition dev --key welldb:WB-0123
sqlflow records issues flows/welldb-wellbore-03-delivery.yaml --partition dev
```

## Records are held

**You see** records with status `held` on the Delivery tab, or a run summary with "held".

**Look** at the Issues tab, or `sqlflow records issues`. Each issue is one reason, with how many records it holds, the
error as they share it (a placeholder such as `n` or `value` where each record says something of its own), and whether it
is a **set error** (every record names the same values: one cause upstream) or **row errors** (each record names its own
row's values). Click an issue, or `--issue <id>`, for the files the records came from and five samples spread across the
issue; on the tab it opens in the bottom panel, where Up and Down step through the issues.

```bash
sqlflow records issues flows/welldb-wellbore-03-delivery.yaml --partition dev --issue 3fa1c09b5d2e7a44
sqlflow records list   flows/welldb-wellbore-03-delivery.yaml --partition dev --issue 3fa1c09b5d2e7a44
```

**Do:**

- **Set error** (a cache entry, the mapping, a dictionary, a legal tag, a prepared file): fix the cause once. Check a few
  samples: their Render tab, or `sqlflow preview <flow.yaml> --key <key>`, renders the record as it would be sent now and
  sends nothing. Then release the issue's records together, from the Issues tab or
  `sqlflow records release <flow.yaml> --issue <id>`, and let the next deliver run send them (the GUI can queue it with the
  release).
- **Row errors**: fix the rows in the source system and let the pre and ingestion flows load them. A corrected row is
  planned again on its own, because its source version moved; no release is needed. The list to hand to the source's
  owners is **Export** on the Records tab narrowed to the issue (**Records** on the issue), or
  `sqlflow records export <flow.yaml> --issue <id> --out <file.xlsx>`: a workbook with each record's key, file and row,
  and the problem to fix ([records export](../cli/records.md#records-export)).
- **Held for a deleted row** (`the ingestion table marked the record row deleted ...`): the row is gone in the source. Restore
  it there, or take the record out of OSDU with a removal if it should go.

A held record is skipped by every run as `blocked` until its row changes or it is released, so re-running the flow does not
retry it. See [Record lifecycle](../concepts/record-lifecycle.md#issues).

## Records are failed

**You see** status `failed`: the record was retried `reliability.retry.attempts` times (4 by default) and every try failed
with something a retry could fix, such as a 5xx, a 429 or a timeout. Its error reads `failed after <n> attempt(s): <message>`.

**Look** at the record's Timeline: each failed try with its status, the steps it ran and the correlation id; or
`sqlflow records show <flow.yaml> --key <key> --attempts 20`.

**Do:** find why the service kept failing (it was down, it throttled, a gateway timed out). Once it answers, try one record
(release it by key), then release the issue's records. A run that keeps failing many records in a row may have been stopped
by `failWhen` (below).

## Records are held by the check before sending

**You see** errors starting `validation:`, such as `validation: the record breaks the schema of ...` or
`validation: the record could not be fully checked`, or one naming references neither the ledger nor OSDU's storage service
holds.

**Do:** the flow declares `target.validation` or `target.verifyReferences: storage` ([Preflight](../concepts/preflight.md)).
Correct the source rows or the mapping (the next plan renders and judges the new documents), put the file of the template
version the records were rendered for back in a template folder on the mapping's path when the check could not find it,
or deliver the records the references name. When the documents are right as
they are, release the issue's records: a released record holding its rendered document is sent as it is, and the release
is recorded.

## Records are waiting

**You see** status `waiting`, and on the record's page the OSDU id it waits for.

**Do:** usually nothing. The record refers to a record of the same ledger that has not landed yet; it goes out as soon as
that one is delivered, and nothing is charged. When the record it waits for is held or failed, fix and release that one. To
send a waiting record now, with its reference pointing at nothing until the other lands, use **Send without waiting** on
its page, or release it by key.

## Records are stuck in delivering

**You see** status `delivering` on records whose lease, on the record's page, is in the past.

**Do:** nothing, usually. A worker stopped mid-delivery (a node killed, a control plane restarted). The flow's next claim
recovers the lease, applies what the worker had already sent, and hands the rest back to `pending` with the note
`the lease expired mid-attempt (the worker stopped) and the record was requeued`; the next deliver run or `drain` sends them,
resuming after the steps the stopped try had recorded. A record whose every try stops its worker (a document that exhausts
its memory, say) is `failed` once its tries are used up, saying
`failed after <n> attempt(s): the lease expired mid-attempt every time (the worker stopped), so the record is not claimed again until it is released`:
find why the worker stopped from the host's log at its attempts' times, then release it. If no run is due, start one:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation drain --set partition=dev
```

Records shown as `delivering` while the run's trace already says they were sent are expected: a worker applies what it
sent at each renewal of its lease, so the record table trails the trace by at most one renewal.

## A submission stays planned or running

**You see** a submission whose status stays `planned` or `running` after its run ended, with batches still `queued`.

**Do:** nothing, usually. Its run stopped part way (a node went away, a part of a split delivery failed, a command-line run was
killed): a run the platform executes again resumes it; otherwise the flow's next deliver run takes it over once no run
holds it and its leases have run out, sends what it holds and closes it, its log naming the submission
([Recovering a stopped submission](../concepts/submissions.md#recovering-a-stopped-submission)). A submission a run
still works on is left to that run. To act now, re-run or drain it from the run dialog (**Re-run submission**,
**Submission to drain**) or the command line:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --payload '{"submissionId":"0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f"}'
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation drain --set partition=dev --payload '{"submissionId":"0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f"}'
```

A record still leased by the stopped run is waited for until its lease runs out, then recovered and sent. Nothing is sent
twice. A run that asks for a submission another run is taking over in that moment fails after 30 seconds with
`Submission <id> could not be held for this run within 30 seconds ...`; run it again once that run has ended.

## A run fails before it plans anything

| Message | What to do |
| --- | --- |
| `The legal service refuses <n> of the legal tag(s) mapping <mapping> puts on every record, so nothing was planned or sent: ...` | Fix or renew the legal tags the flow's parameters name. Nothing reached the ledger or OSDU. |
| `The preflight of '<source>' found <n> problem(s), so nothing was planned or sent: ...` | Fix each finding (a template no template folder on the mapping's path holds, a record table without its identity key, a service refusing the credentials) and run again. |
| `Flow '<name>' names no partition, and its target.headers no 'data-partition-id', so the partition its ledger belongs to is unknown.` | Name the partition the flow delivers to. |
| A registration naming another partition than the ledger is kept in | The flow's `data-partition-id` resolved differently on this node; nothing ran. Make the reference resolve the same everywhere ([Partitions](../concepts/partitions.md)). |

## A run of a source stops part way

**You see** `<operation> of '<source>': <n> of <m> interface(s) completed (...)`, with each interface's state and reason in
the run's result.

**Do:** what completed is in the ledger. An interface stopped by `an outage: <n> records in a row ... with none delivered in between (failWhen.outageFailures 25)`
met a service that was down or refused its credentials; its records are pending, and the next run sends them once the
service answers (**Probe target** on the Delivery tab checks it under the flow's own credentials). One stopped by
`failWhen.failedPercent` holds or fails too many records at once: fix the data or mapping, release, and run again. Run
only some interfaces with `--payload '{"interfaces":["wellbores"]}'`.

## Every record is held: a number is 'false'

**You see** every record of a flow held with `... value 'false' is not a valid number (a boolean is not a number)`.

**Do:** the pre flow inferred a numeric column as `bit` from a file whose values were all 0 or 1 (SQLFlow's inference
reads such a column as a boolean). Declare the column's type in the pre flow's `transform.columns`
(`- { name: top_depth, expr: "NULLIF(@ColName, '')", type: "decimal(38,18)" }`) and run the pre and ingestion
flows; a column the ingestion table already holds as `bit` is a cross-family change SQLFlow does not make, so it needs
a one-time `ALTER` ([schema evolution](../../../../sqlflow/docs/reference/concepts/schema-evolution.md)). Then
release the records; the next run renders them again.

## A record-scoped run plans nothing

**You see** the warning `The record table OsduData.silver.Wellbore holds no record with key <key>; it cannot be planned.`

**Do:** the ingestion table has no row for the key yet. Look at the pre and ingestion runs that load the table; once they
have loaded the row, the run scoped to the record reads it by key.

## Every record is held after a flow was renamed

**You see** every record of a flow whose `name` changed held with
`OSDU id <id> is already claimed by flow '<old name>' (<id>), which no repository declares any more, ...`.

**Do:** a flow keeps its ledger under its name, so the renamed flow ran on a new, empty ledger while the old name's records
stayed in the old one with the OSDU ids they claimed. Nothing was sent, and nothing needs deleting.

- When only the name changed, sync the repository: the sync finds the old one's document under a new name, moves that
  ledger to it once it committed, and says so in its result (`Partition '<partition>': moved <n> record(s) of '<old>' to
  '<new>', ...`, and a `ledger-move` entry by `service:repository-sync` in the audit trail).
- Otherwise (the flow was also changed, its file was deleted and added back in separate syncs, or the rename was synced
  before documents' hashes were recorded), open **Orphaned ledgers** in the menu's Ledger group, find the old
  name, press **Take over** and pick the renamed flow, which is suggested when it pins the same mapping. Each ledger moves
  at once, as you.

Then run the flow: the records it held give way to the old ones, and it sends only what changed, as updates of the ids
they claimed ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)). When the sync warns that the two ledgers are in
different partitions, keep the old ledger with `ledger: <old name>` in the flow instead.

## A record is held because another flow owns its OSDU id

**You see** `OSDU id <id> is already claimed by flow '<name>' (<id>), and one OSDU record belongs to one flow. ...`, or
`OSDU already holds a record at <id>, the OSDU id made from the key, and no record of the ledger claimed it: ...`.

**Do:** two flows, or two keys of one flow, give the same OSDU id, or the id belongs to a record another system wrote.
Deliver the flow to another partition, or give its mapping a `dataset.system` or key that yields other ids. Nothing was
sent. When the flow that claimed the id is this one under its old name, keep its ledger
([Every record is held after a flow was renamed](#every-record-is-held-after-a-flow-was-renamed)).

For ids another system wrote that are these rows' records (an earlier deployment of the delivery, or another instance
delivering to the same partition, wrote them from the same business keys), adopt them all at once: **Adopt (N)** on the
flow's Delivery tab, with the partition typed back and a deliver run queued straight after, or `sqlflow records adopt
<flow.yaml> --confirm <partition>` for one interface. The run takes each record over at the version OSDU holds then and
writes its row there, and the ledger maintains the records from then on; **Adopt N** on the Issues tab, **Adopt** in the
Records tab's selection bar and on a record's page ask fewer
([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)). For a
record that is not this row's, remove or rename it in OSDU first.

## A record is held because its OSDU id moved

**You see** `the mapping now gives this record the OSDU id <new id>, and the record claimed <old id> when it first queued a
document; a record keeps the OSDU id it claimed, so nothing is sent. ...`

**Do:** something moved the id after the record claimed it: the mapping's `dataset.idFrom`, the entity type of the kind
it renders, or the partition the flow mints ids in (`dataPartition`). Put it back, or move the records on purpose: remove
them from OSDU at the `record` scope, then deliver them under a ledger of their own or delete them from the ledger with the
removal ([change detection](../concepts/change-detection.md#a-record-keeps-the-osdu-id-it-claimed)). A flow bound by its
`data-partition-id` header now mints its ids in its header's partition; one whose header and `OSDU_DATA_PARTITION` once
disagreed has records whose ids were claimed in the environment's partition, and their next render meets this hold.

## OSDU holds something other than what was delivered

**You see** drift: a `verify` run reported `drifted` or `missing` records, the Delivery tab counts them, and the Records tab
lists them with **Drifted only**.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation verify --set partition=dev --payload '{"force":true}'
```

**Do:** decide whether the change in OSDU was legitimate. A newer version that changed only data keys another system owns
is not drift. To put the delivered version back, use **Redeliver**, **Send again** on the records. With
`verify.reconcile: true` a verify marks each drifted or missing record for redelivery as Redeliver does (its note reads
`verify: drifted (observed version <v>, expected <v>); redelivery requested, the flow's next deliver run sends it again`)
and names it under the verify's activity; the flow's next deliver run sends it whole although its row did not change.
Send again sends it at once.

## Records need rendering again after a change

**You see** records OSDU holds that an older engine rendered (an upgrade writes a value another way), or the explorer's
validation finds the same problem on every record of a kind. A mapping, template, parameter or cache change does not need
this: the next run reaches the records it affects.

**Do:** **Redeliver**, **Bring up to date**. Its check says how many records would change and which part; only those are sent,
and a record that renders the same writes no new version.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --payload '{"rerender":true}'
```

## A record OSDU lost, or someone changed there

**Do:** **Redeliver**, **Send again** on the record (or the selection), naming the part: everything, the record, or on a
route that sends them its files or bulk data. OSDU keeps a new version.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --payload '{"recordKeys":["6f1c2a9e-4b7d-4c1e-9a3f-2d8e5b7c1a40"],"redeliver":"all"}'
```

## The ledger disagrees with the ingestion tables

**You see** records without an arrival, a row changed or deleted in the source that no run planned, or a row gone from the
table.

**Do:** **Sync timeline** on the record, or **Sync timelines** for a selection or the whole interface. It sends nothing:
it records what the ledger lacks, asks the next run to plan the rows that changed unseen, and notes rows that are gone
(`source-missing`) without removing anything from OSDU.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation sync --set partition=dev
```

## Records must leave OSDU

**Do:** **Remove** on the record's page or the Records tab selection. Pick **Remove the record** (reversible),
**Restore the previous version**, **Purge earlier versions** or **Purge everything**, and, with a choice that leaves them
out of OSDU, **Delete from the ledger** if their history should go too. The dialog names the exact call for the flow's
route and asks for the partition to be typed back for anything permanent. The removal is a `remove` run of the flow:
follow it under **Runs** or with **Open run** on the notice. It waits for a run of the pipeline in progress to end, and a
removal OSDU refused records of ends failed, naming them; ask again for those once what refused them is settled. A removed
record is delivered again by the next run that reads its row; a row that should stay out of OSDU is taken out of the source.
See [Removal and reversal](../concepts/removal-and-reversal.md#removing-records-from-osdu).

## A run delivered the wrong thing

**You see** a run that loaded a wrong file, rendered with a wrong mapping or cache, or reached the wrong partition.

**Do:** reverse it. **Reverse this run** on its audit trail entry or run page, **Reverse this submission** on a submission's
page, or:

```bash
sqlflow records reverse flows/welldb-wellbore-03-delivery.yaml --partition dev --run 0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f --preview
sqlflow records reverse flows/welldb-wellbore-03-delivery.yaml --partition dev --run 0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f
```

Records it updated get the version OSDU held before; records it created are removed again, reversibly. Reverse later runs
first: a record a later run delivered again is passed over as `superseded`. Fix the source or the mapping before the next
run; reversed records stay blocked while their rows are unchanged.

## A flow has to start over from nothing

**Do:** **Delete ledger** on the Delivery tab, typing the partition back. It removes every record of the pipeline from OSDU
reversibly, then deletes the pipeline's ledgers, and the next run reads every row and delivers each again. It is refused
while work is in flight, and keeps the ledger whole when OSDU refuses a removal.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation delete-ledger --set partition=dev --payload '{"confirm":"dev"}'
```

## Something an unfinished delivery left is not undone

**You see** **Unfinished deliveries to undo** on the Delivery tab, records skipped with phase `undo-wait`, or records held
saying an earlier delivery left items its undo could not take back; or `Delete ledger` refused for the same reason.

```bash
sqlflow records undos     flows/welldb-welllog-03-delivery.yaml --partition dev
sqlflow records artifacts flows/welldb-welllog-03-delivery.yaml --partition dev --key welldb:LOG-0042
```

**Do:** each artifact's note says what OSDU answered. Fix what stops the undo (the identity's entitlements, a service that
is down), then run the undo; `force` retries the items whose undo used all ten tries. A held record goes on once released,
which tries the undo first. An item `kept` has no call that removes it (historian points, files behind a soft-deleted
dataset); it is listed so it is known.

```bash
sqlflow run flows/welldb-welllog-03-delivery.yaml --operation undo --set partition=dev --payload '{"force":true}'
```

## A failure has to be followed into OSDU's own logs

**Do:** open the try on the record's Timeline (or `sqlflow records show`): it names the `correlationId` every request of
the try carried, and a refused request's error quotes the id the service answered with. Give the OSDU operators that id.

## Related pages

- [Record lifecycle](../concepts/record-lifecycle.md): states, retries, issues and releases.
- [Removal and reversal](../concepts/removal-and-reversal.md): every action that changes what OSDU holds after delivery.
- [Submissions](../concepts/submissions.md): plans, batches and leases.
- [Availability and retention](../concepts/availability-and-retention.md): outages, recovery and backup.
- [What the osdu schema stores](../concepts/storage-and-retention.md): what takes the space, what is kept, and the three cleanups an admin may set on the Maintenance page.
- [`sqlflow records`](../cli/records.md) and [running an OSDU flow](../cli/run.md).
