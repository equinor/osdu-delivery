---
id: delivery-concept-record-lifecycle
title: "Record lifecycle: record states, attempts, retries, held and failed records, issues and releases"
type: concept
summary: "Record states, what each attempt records, when a record is retried, held or failed, how issues group blocked records, and what a release does."
keywords:
  - record status
  - record states
  - held record
  - failed record
  - retry
  - backoff
  - blocked
  - release
  - issues
  - waiting record
  - attempt outcome
  - attempt phase
  - reliability.retry
  - skipStatusCodes
  - adopt
  - adopt a record osdu already holds
  - adopt in a batch
  - adoption asked
  - occupied id
  - business key already in osdu
  - another instance
  - linked instances
related:
  - delivery-concept-ledger
  - delivery-concept-submissions
  - delivery-concept-change-detection
  - delivery-concept-protocols
  - delivery-concept-removal-and-reversal
  - delivery-guide-operations-runbook
  - delivery-cli-records
  - delivery-flow-delivery
  - delivery-flow-mapping-assertions
  - delivery-flow-mapping-parent
  - delivery-flow-mapping-child
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Waits.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Problems.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OccupiedHolds.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Problems.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ProblemSignature.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordWaits.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Planning/ChangeDetector.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/ValidationGate.cs
  - osdu/src/SqlFlow.Delivery/Validation/AssertionFindings.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/FailureGuard.cs
  - osdu/src/SqlFlow.Delivery/Http/RetryPolicy.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RecordProblemBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryIssueEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
---

# Record lifecycle: record states, attempts, retries, held and failed records, issues and releases

Every record a delivery flow plans has one row in the [ledger](ledger.md) (`osdu.Record`) holding its custody state, and an
append-only history of attempts (`osdu.Attempt`). This page says which states a record can be in, how it moves between
them, what each attempt records, when a failed send is retried, held or failed for good, how the ledger groups blocked
records into issues, and what releasing a record does. For removals and reversals, which also move records, see
[Removal and reversal](removal-and-reversal.md).

## Record states

| Status | Meaning | Blocked | How it moves on |
| --- | --- | --- | --- |
| `pending` | A rendered document (and payload) is queued for the record, due now or at `NextAttemptUtc`. | No | A worker claims it: `delivering`. |
| `delivering` | A worker holds it under a lease and is sending it. | No | The try settles it when the lease applies; a lease that ran out is recovered. |
| `delivered` | OSDU holds what the ledger says: `TargetId`, `TargetVersion` and the hashes. | No | A plan finds a change: `pending`. |
| `held` | The record was refused: a data problem, a status no retry can fix, the check before sending, or a render problem. | Yes | Its source row changes, or an operator releases it. |
| `failed` | Retryable failures used up the retry budget. | Yes | As `held`. |
| `waiting` | Its document refers to a record another record of the ledger holds and has not delivered. | No | That record lands: `pending`. Nothing is charged. |
| `deleted` | Removed from OSDU by an operator, by a reversal of the run that created it, or by a removal flow because its source no longer holds the key. | Not after a removal; yes after a reversal | The flow's next run reads its row by key and delivers it again, since the removal asked for it (removal), or a release (reversal). A record a removal flow took out is kept out instead: only a row newer than its removal, the key back in the source, or **Deliver again** brings it back ([Removal flow](../flow/removal.md#delivering-a-record-again)). |
| `reverted` | A reversal, or a step back to the previous version, wrote an earlier version back. | Yes | Its source row changes, or a release makes it `delivered` again. |

A **blocked** record is skipped by every later plan as `blocked`, with the reason
`<status> since <time>: <last error>; release the record or change the source to plan it again`, until either its source
row moves on from the version it was left at or an operator releases it. The row moves on when the ingestion table
rewrites it (its fingerprint changes) or, when the flow orders rows by `source.lastModified`, when its moment moves past
the one the record was left at. Under that order a row rewritten at the same moment moves the record too, since a
correction need not touch the column (a business date names the day the data describes, not the day it was fixed), while
a row older than that moment is a late row and moves nothing. A record left at no fingerprint cannot be seen to move, and
waits for an operator. A re-run of the same data never re-attempts a known problem, while a corrected source row flows
through on its own.

A blocked record the ledger was asked to plan again (a redelivery, a sync that found its row moved, a cache change of the
values it was built from) is planned whatever version it was left at. A cache change never reaches a `reverted` record or
a `deleted` one: what an operator's reversal, step back or removal left in OSDU stays until the row moves or someone
releases the record.

A held, failed or reverted record whose row renders the document OSDU already holds (the row went back to what was
delivered before it was held) is delivered again: nothing is sent, the record is unblocked with its error gone, and its
history gains an attempt (`skipped`, `identical`) marked as delivering it again. Not while what an earlier unfinished
delivery left in OSDU is still to be undone: until then OSDU holds more than the ledger says, so the record is blocked at
that row, with an attempt (`held`, `undo-wait`) saying `the <status> record renders what OSDU holds, but what an earlier
delivery of it left in OSDU is not undone yet; release it once the undo is done (the record's artifacts say what is left)`.

**A run and an operator at the same time.** A plan decides each record against the ledger row it read, and writes its
decision only while the row is still that one. A record released, redelivered, stepped back, reversed or delivered after
the plan read it is not overwritten: the plan writes nothing for it and asks for it to be planned again, and the next run
decides it from its row under what the operator did. A plan takes away only the request to plan a record again that it
read; one made while it planned stays for the next run. So a release made while a scheduled run is planning is never
lost.

```text
 new or changed row --plan--> pending --claim--> delivering --sent--> delivered
                                ^                    |
                                +-- retry later -----+
                                                     +--> held    (refused; blocked)
                                                     +--> failed  (retry budget spent; blocked)
 pending --claim, refers to a record not landed--> waiting --that record lands--> pending
 delivered --removal (record or everything)--> deleted --row read again--> pending
 delivered --removal flow (source no longer holds the key)--> deleted --newer row, key back, or Deliver again--> pending
 delivered --reversal or previous--> reverted --release--> delivered
```

## How a record reaches pending

A run plans the rows of its selection ([Submissions](submissions.md)) and decides, record by record, what to do
([Change detection](change-detection.md)). The plan writes an attempt for every decision that changes the record, even
when nothing is sent, so the record's history holds every change of its row:

| Decision | Status | Attempt (outcome, phase) |
| --- | --- | --- |
| New, or the document or payload renders differently from what OSDU holds | `pending`, its document in a work batch | none until a worker tries it |
| The render cannot be delivered (a missing value, an unresolved lookup, a refused OSDU id) | `held`, blocked | `held`, `render` |
| The ingestion table marked the row deleted | `held`, blocked | `held`, `source-deleted` |
| The row changed but renders the document OSDU holds or already queues | unchanged; the origin moves to the new row | `skipped`, `identical` |
| A held, failed or reverted record, unblocked by its row or by a release, renders the document OSDU holds | `delivered`, unblocked, its error gone; nothing is sent | `skipped`, `identical` (`settled` in its result) |
| The row is older than the version delivered or queued (a waiting record's queued document counts) | unchanged; a released held record is `delivered` again at the version OSDU holds | `skipped`, `stale`, once per row |
| The row and its render context are unchanged | unchanged | none |

Versions never go backwards: staging refuses work older than what the ledger holds, so two intakes racing for one record
leave the newer version standing. Work planned for a record a worker is sending right now is written behind that delivery
and sent after it. Before anything is sent, the worker compares the queued hashes with what the ledger says OSDU holds at
that moment; when neither half differs it settles the record with an attempt (`skipped`, `unchanged`) and sends nothing.

A row the ingestion table marks deleted is never delivered and never removed as a side effect: its hold says
`the ingestion table marked the record row deleted at <time>; a deleted row is never delivered. Remove the record from OSDU deliberately, or restore the row in the source.`

## Attempts

Each attempt names the record, the submission, the platform run, the worker, start and end, what was sent, the OSDU
version returned, the ingestion row the document was built from, the redacted error, and the steps of the try with what
OSDU answered and the correlation id every request carried ([The delivery ledger](ledger.md#osduattempt-every-try-and-every-decision-that-changed-the-record)).

| Outcome | Written when |
| --- | --- |
| `delivered` | A try sent the record and OSDU accepted every call. |
| `skipped` | Nothing was sent: unchanged at the final check, identical or stale at plan time, waiting on an undo, a row a sync no longer found, a record a reversal passed over, or a record a removal flow settled because OSDU held nothing of it. |
| `held` | A try or a plan refused the record. |
| `failed` | A try failed; the record is retried (status `pending`) or failed for good (status `failed`). |
| `deleted` | A removal took the record out of OSDU, a reversal removed a record its run created, or a removal flow took it out because its source no longer holds the key. |
| `historypurged` | A removal purged the record's earlier versions; the record itself is still live. |
| `restored` | An earlier version was written back, by a reversal or a step back to the previous version. |
| `undone` | What an unfinished delivery left in OSDU was undone. |

| Phase | Meaning |
| --- | --- |
| `metadata`, `payload`, `metadata+payload` | What a delivered try sent: the record document, the payload, or both. |
| `none` | A try that sent nothing: one that held, failed or will be retried. |
| `render`, `source-deleted`, `identical`, `stale` | Decisions the plan wrote (worker `intake`). |
| `unchanged` | The final hash check found OSDU already holding the queued work. |
| `undo-wait` | The newer work waited for an earlier delivery's undo ([below](#waiting-for-an-undo)). |
| `source-missing` | A sync found the row gone from the ingestion table, or outside the record's scope (worker `sync`). |
| `delete`, `purge-history` | A removal at the `record` or `everything` scope, or at the `history` scope. |
| `source-removed` | A removal flow took the record out of OSDU (`deleted`) or settled it in the ledger (`skipped`), because its source no longer holds the key; its result names the removal flow, the removal run, why and as of when, and the file and row that said so ([Removal flow](../flow/removal.md#taking-out-what-the-source-removed)). |
| `restore-previous` | An operator stepped the record back to the version before the latest. |
| `reassign-id` | An operator gave the record a new OSDU id because OSDU lost the one it claimed; its result names both (outcome `skipped`). |
| `adopt` | A run took over the record OSDU already held at the id the record's key makes, as an operator asked (worker `intake`, outcome `skipped`); its result names the id and the version OSDU held. See [Adopting a record OSDU already holds](#adopting-a-record-osdu-already-holds). |
| `reverse` | What a reversal did to the record. |
| `undo` | An undo of an unfinished delivery, naming each object it took back. |

A try that sent nothing (`unchanged`, `undo-wait`) is not charged to the record's retry budget, and neither is a try a
stopping worker handed back.

## Retry, hold, fail

A send goes through two loops. Inside one call, the HTTP executor repeats a request that is safe to repeat after a
transient status or a transport failure, waiting from `reliability.retry.baseDelayMs` up to
`reliability.retry.maxDelayMs`; which requests and statuses it repeats is on
[Requests every route makes](protocols.md#requests-every-route-makes). Whatever a call still fails with reaches the
worker, which settles the record:

| What the try ended with | Record | Attempt and error |
| --- | --- | --- |
| Every call answered 2xx | `delivered`; the pending work becomes what OSDU holds | `delivered` |
| HTTP 400, 403, 404, 405, 409, 413, 415 or 422, or a status listed in `reliability.skipStatusCodes` | `held`, blocked | `held`: `HTTP <status> is not retryable: <message>` |
| A hold the route or the check before sending raised (a payload over `reliability.maxRequestBodyBytes`, a document the template refuses under `target.validation`, a document that fails an assertion of its mapping whose `onFail` is `hold`, a reference storage does not hold under `target.verifyReferences: storage`) | `held`, blocked; the rendered document is kept | `held`, with the route's reason |
| Any other HTTP status (401, 408, 429, 5xx, ...), a transport failure, a timeout, a read or parse failure | `pending`, due again after the record backoff, completed steps kept for the resume | `failed` with the message |
| The queued document is not there to read (its work batch was pruned, truncated or rewritten) | `held`, not blocked, its document dropped, asked to be planned again: the next run renders it from its row | `held`: `the rendered document queued for the record could not be read ...` |
| The store holding the work batch did not answer | handed back to `pending` uncharged, with the rest of the lease; the next pass reads it again | none |
| The worker stopped mid-try (its node was lost, its process ended), so its lease ran out and the flow's next claim recovered it | `pending`, the try charged | `failed`: `the lease expired mid-attempt (the worker stopped) and the record was requeued` |
| The same, when the record has already been tried `reliability.retry.attempts` times | `failed`, blocked | `failed`: `failed after <n> attempt(s): the lease expired mid-attempt every time (the worker stopped), so the record is not claimed again until it is released` |
| The same, when the record has already been tried `reliability.retry.attempts` times | `failed`, blocked | `failed`: `failed after <n> attempt(s): <message>` |
| Anything unexpected | `failed`, blocked | `failed`: `unexpected failure: <type>: <message>` |

The record backoff is `recordBaseDelayMinutes` times 2 to the power of (tries made minus one), at most
`recordMaxDelayMinutes`; a `Retry-After` longer than `maxDelayMs` is not waited out inline, and the record's next try is no
sooner than the service asked. `reliability.retry.attempts` bounds both loops: the attempts one safe request is given inside
a call, and the tries a record is given before it fails. A record's try count starts again from zero when new work is
queued for it, when it is delivered, and when it is released with its rendered document.

A document that is gone holds the record unblocked rather than for a release, since a release would only send the
missing document again; the completion that says so is applied with the rest of its lease (`RecordEvent.Replan`, migration
`RecordEventReplan`, module version 1.42.0).

A record whose try stops its worker every time (a document that exhausts the worker's memory, say) is not claimed for
ever: each recovered lease charges the try and writes its attempt, and once the record has used its tries it is `failed`
and blocked until someone releases it, with fresh tries. A lease holds a whole work batch, and the ledger cannot tell the
record the worker stopped on from the ones it had not reached, so every record the lease held and had not settled is
charged, as a queue counts every message it handed out; a batch whose worker stops every time fails its unsettled records
together, under one issue. Releasing them a few at a time, by key, finds the record that stops the worker. A worker that finishes after its lease was recovered still writes
the try's attempt, since the try happened; its outcome reaches the record only while the record stands where the hand-back
left it (`pending`, or `failed` once its tries were used up, at the try count the claim left). A later try, a hold, a
release, a removal or newly queued work stands, and is never overwritten by the late outcome. The lease keeps the flow's
try budget and each completion the try count of its claim (`Lease.MaxAttempts`, `RecordEvent.ClaimAttemptCount`, migration
`LeaseRecovery`, module version 1.43.0). The same migration marks the leases a removal holds records under while it takes
them out of OSDU (`Lease.ForRemoval`), which no claim passes and which never charge a try
([Removal and reversal](removal-and-reversal.md#removing-records-from-osdu)).

| Key | Default | Meaning |
| --- | --- | --- |
| `reliability.retry.attempts` | `4` | Attempts of one safe request inside a call, and tries of a record before it fails. |
| `reliability.retry.backoff` | `exponential` | `exponential` or `fixed`, for the inline retries. |
| `reliability.retry.baseDelayMs` | `500` | The first inline wait. |
| `reliability.retry.maxDelayMs` | `30000` | The longest inline wait; a longer `Retry-After` moves to the record backoff. |
| `reliability.retry.honorRetryAfter` | `true` | Whether a service's `Retry-After` is honoured. |
| `reliability.retry.recordBaseDelayMinutes` | `1` | The first record backoff. |
| `reliability.retry.recordMaxDelayMinutes` | `60` | The longest record backoff. |
| `reliability.skipStatusCodes` | `[]` | More statuses that hold a record instead of retrying it. |

```yaml
reliability:
  leaseSeconds: 300
  skipStatusCodes: [412]
  retry:
    attempts: 4
    recordBaseDelayMinutes: 1
    recordMaxDelayMinutes: 60
```

A record with a data problem is held and its interface goes on. When so many records fail that the interface as a whole
has a problem, `failWhen` stops it: by default after 25 connection or permission failures in a row with none delivered
between (`failWhen.outageFailures`), and optionally on a share of held and failed records (`failWhen.failedPercent`,
judged once `failWhen.minRecords`, default 100, have settled) or on consecutive failures of one kind
(`failWhen.consecutiveFailures`). See [Delivery flow](../flow/delivery.md).

## Releasing a record

A release lets blocked records go again once their cause is fixed. It reaches the records named by key, every blocked
record of the flow, or every record one issue keeps blocked ([below](#issues)). It also reaches a held or failed record
nothing moves (not blocked, not asked to be planned again, no document to send), which a release answered by a plan that
left the record held could leave before module version 1.42.0; the GUI offers Release on such a record's page and counts
it with the flow's blocked records. What a release does to each record:

| The record | After the release |
| --- | --- |
| Blocked and still holding its rendered document (held or failed by a try, held by the check before sending) | `pending` again in its submission, its tries counted from zero; the document is accepted as it is, so the check before sending sends it whatever its verdict and its mapping's assertions say. |
| Blocked with no rendered document (held at render, deleted by a reversal) | Unblocked and asked to be planned again (`PlanRequestedUtc`), with the note `released; the flow's next run plans it again from its ingestion rows`. The run that plans it holds it again, queues what renders differently, or, when the row renders what OSDU holds or is older than that version, settles it as `delivered` with nothing sent. |
| `reverted` | `delivered` again, asked to be planned again; the next plan sends only what renders differently from what OSDU now holds. |
| `waiting`, named by key | `pending` without the references it waited for (ids a record of the ledger holds and has not delivered): sent without waiting ("Send without waiting" on the record's page). Its other references stay, so under `target.verifyReferences: storage` the check before sending still asks storage about them. A release of the whole flow leaves waiting records to their wait. |

A release changes the ledger only; the flow's next deliver run sends, whether the schedule starts it or the release
queues it at once (the GUI's release dialog does, unless "Queue a deliver run now" is cleared). A released record that
holds its document is sent by the next deliver run whatever that run plans, since a run takes the due records of every
settled submission after its own (ten submissions at a time). A record asked to be planned again is read by key at the
start of the next run, 5,000 records to a pass, as many passes as there are such records, and rendered whatever source
version it was last planned under. A record that carries no source key (one planned before the ledger kept them) cannot be
read by key: the run reads its whole scope instead of the window since its watermark, which meets the record's row, and a
request whose row that read did not meet is answered as a row gone from the table. A record a pass could not plan
is read again by a sync in the scope it was planned under: a row gone from the ingestion table answers the request there
(a `source-missing` attempt, once), and a released record is blocked again, saying
`the record's row is gone from the ingestion table (deleted from it, or its key changed), so no run can plan it; it is planned again when its row is back, or released`,
so no later run reads it again; a row in another run's scope keeps its request for that run. A removal answers any request
the record carried, so the next run does not send back what was just removed. A release records a `release` activity
under who asked, and names every record it released in `osdu.ActivityRecord`.

A flow's statistics count the records a release of every blocked record reaches: `blocked`, and `stranded` (held or
failed with nothing to move them), both from one index that holds the parked records alone (`IX_Record_Parked`, migration
`RecordParkedIndex`).

```bash
sqlflow records release flows/welldb-wellbore-03-delivery.yaml --partition dev --key 6f1c2a9e-4b7d-4c1e-9a3f-2d8e5b7c1a40
sqlflow records release flows/welldb-wellbore-03-delivery.yaml --partition dev --issue 3fa1c09b5d2e7a44
```

**A record held for an assertion of its mapping** (`onFail: hold`, [mapping assertions](../flow/mapping-assertions.md))
is held by the check before sending with its document kept, under an error that starts `assertion:` and names the
property, the assertion and the value. The records one assertion holds share one issue whatever values they hold, since
the error quotes the values. A release sends the kept document as it is: its verdict is marked accepted and still lists
what the assertions found. The acceptance ends when the document is delivered: the same document queued again later is
judged again. The record stays held while only the mapping changes; correcting the row plans it again, and
so does a redelivery, under the mapping the flow pins then.

**A record held because its parent is not in OSDU** (a record of a custom template reading sections of its canonical
parent with `$parent`) is held at its render, keeping no document, and its parent's id is on the record. It needs no
release: when the parent lands through the ledger, delivered by any flow of the partition, the record is released under
a `release` activity whose actor is `parent`, and its flow's next run renders it from the parent
([mapping $parent](../flow/mapping-parent.md#when-the-parent-changes)).

**A record held because an id it built names nothing the partition cache holds** needs no release either: when a later
cache version holds that record, the held record is released under a `release` activity whose actor is
`cache:<partition>@<version>`, and its flow's next run renders it against the new version
([A record held for a reference waits for it](partition-cache.md#a-record-held-for-a-reference-waits-for-it)).

A child is never a reason to hold its parent: a record linking its children with `$child` goes out without the link to a
child OSDU does not hold yet, and when the child lands through the ledger the record is asked to be planned again, with
no activity, so its flow's next run adds the link ([mapping $child](../flow/mapping-child.md#when-a-child-lands)).

## Adopting a record OSDU already holds

A mapping that makes its OSDU ids from the key's values (`dataset.idFrom: key`, [mapping](../flow/mapping.md)) makes the
same id from the same business key whoever renders it. Before a record claims such an id for the first time, the intake
asks OSDU whether a record is there already. When OSDU holds one and no record of the ledger claimed it, another system
wrote it, or another instance of the delivery whose ledger this database does not hold (an earlier deployment, a second
environment delivering to the same partition). The row's business key is in the ingestion table and its record is in OSDU,
but nothing is sent: writing to the id would make a new version of a record no ledger owns. The record is held, saying
`OSDU already holds a record at <id> at version <n>, the OSDU id made from the key, and no record of the ledger claimed it: ...`,
and the hold keeps that id and version on the record (`osdu.Record.OccupiedTargetId` and `OccupiedTargetVersion`,
migration `RecordOccupiedTargetId`, module version 1.56.0). A hold for any other reason, and one where OSDU could not say,
keeps neither.

**Adoption is a batch operation.** Once a run held records so, an operator who knows the records OSDU holds are this
source's asks them to adopt them, every one at once, and the flow's next run takes each over: from then on the ledger
maintains them as its own records, and the run after finds nothing to adopt. Asking claims nothing and asks nothing of
OSDU; it marks the records for the run.

| The record | When an adoption is asked |
| --- | --- |
| Held for an id OSDU holds, claiming none, no delivery holding it, not asked already | Asked: `osdu.Record.AdoptRequestedUtc` says when (migration `RecordAdoptRequested`, module version 1.57.0), and the record is unblocked and asked to be planned again, with the note `adoption asked: the next run takes over the record OSDU holds at the OSDU id this record's key makes, ...`. The adoption is an `adopt` activity naming every record it asked. |
| Held for anything else, claiming an id, held by a delivery, or asked already | Left as it is. A batch counts only the records it asked; the API answers 409 for a record named alone, saying why. |

A record is held for an id OSDU holds when its hold kept the id (`OccupiedTargetId`), or, for a record held before the
hold kept it, when its error begins `OSDU already holds a record at `, as every such hold's does. Records held either way
are adopted by what they were held for, and nothing stored is rewritten to make them adoptable.

**The next run takes the records over.** It makes each asked record's id from its key under the mapping it renders with
then, and asks OSDU, one batched read for a batch, as it asks before any first claim:

| OSDU at the id | What the run does |
| --- | --- |
| Holds a record no record of any ledger of the database claimed | Takes it over: the record claims the id (`ClaimedTargetId`) at the version OSDU holds then (`TargetVersion`), its history gets an attempt (worker `intake`, outcome `skipped`, phase `adopt`) naming the id and the version, and the run emits `record.adopted`. The run writes the row there as an update of that version. |
| Holds nothing | Delivers the row under the id as a new record, as any first claim. |
| Could not say | Holds the record, saying so, and the request stands: released once OSDU answers, the record is taken over by the run after. |
| Another record claimed the id since the record was asked | Holds the record, naming the record that claims the id; nothing is adopted. |

The request is spent when the run writes the record (`AdoptRequestedUtc` is cleared with the occupied id), so a record is
taken over once. The write carries forward the data keys OSDU keeps of a record (`preserveDataKeys`), since the ledger
knows the version it took over, and with `target.protocolOptions.skipDuplicates` OSDU writes no version when the row
renders what the record already holds, and the record lands at the version OSDU holds. From then on the record is one of
the ledger's: its later changes are written there, it is read back, verified and removed through its claim, and an
inventory finds the id `tracked`. The write needs the flow's credentials to be an owner of the record (its
`acl.owners`); storage refuses it with 403 otherwise, which holds the record with that reason.

**Asking.** A flow's counts include `adoptable`, the records an adoption may be asked of, summed over a source's
interfaces.

| Asked of | GUI | API | CLI |
| --- | --- | --- | --- |
| Every such record of the source, of every interface | **Adopt (N)** on the flow's Delivery tab | `POST /flows/{pipelineId}/adopt` naming no record and no interface | |
| Every such record of one interface | | the same route with `interface` | `sqlflow records adopt <flow.yaml> --confirm <partition>` |
| The records one issue keeps held | **Adopt N** on the flow's Issues tab | `POST /flows/{pipelineId}/issues/{issue}/adopt` ([Adopting an issue](#issues)) | `--issue <id> --confirm <partition>` |
| The records ticked, or every record a filter matches | **Adopt** in the Records tab's selection bar | `POST /flows/{pipelineId}/adopt` with `keys`, or `filter` with `expected` | `--key <delivery key>...` |
| One record | **Adopt** on a held record's page | `POST /records/{flowId}/{key}/adopt` | `--key <delivery key>` |

Every adoption through a flow's routes, and so every batch in the GUI, takes the partition the ledgers are kept in typed
back (`confirm`, case ignored): without it, or with another partition, nothing is asked (400). The CLI asks for it
(`--confirm`) for every record or an issue's. Each can queue a deliver run straight after (`run`): of the whole source
when every interface was asked, of the interface otherwise, and of the record for one record.

```bash
sqlflow records adopt flows/welldb-namespaces-03-delivery.yaml --partition dev --confirm dev
sqlflow records adopt flows/welldb-namespaces-03-delivery.yaml --partition dev --issue 3fa1c09b5d2e7a44 --confirm dev
```

**Two instances linked to one record.** Each instance keeps its own ledger, so each can adopt the same OSDU record, and
both deliver to it. Through the storage route each write records the hash of the content it wrote
([routes, storage](../flow/routes.md)), so a verify that finds a newer version reads the record and calls it `match` when
the other instance wrote the same content again, and `drifted` only when what this flow delivered changed. A write OSDU
skips as unchanged records the version OSDU holds, read after the write, rather than the one the ledger last knew.

## Issues

A blocked record's error names the record: the value it read, an OSDU id, a moment, a row, the correlation id of the
refused request. A million records held for one reason carry a million different errors. The ledger keeps beside each
blocked record the **issue** its error names: the error with every part that names the record replaced by a placeholder
(the pattern), hashed. Records refused for the same reason share an issue, so a flow with a million blocked records shows
the handful of issues they share. The code calls an issue a problem (`ProblemHash`); the GUI's Issues tab, the API's
`/issues` routes and `sqlflow records issues` call it an issue.

The pattern is made in one place from the redacted error, so the same error is the same issue on every path:

| Part of the error | Becomes |
| --- | --- |
| The correlation id a refused request names | `<id>` |
| An OSDU kind (`osdu:wks:master-data--Wellbore:1.3.0`) | kept: it names what was refused |
| A web address | its scheme, host and the words of its path; other segments `<id>`, the query `<query>` |
| Any other address, and a file path | `<path>` |
| A moment (ISO 8601, a cache version label, a time of day) | `<time>` |
| A UUID or 32 hexadecimal characters | `<id>` |
| An OSDU record id | `<osdu id>` |
| A run of 16 or more hexadecimal characters | `<hash>` |
| A value in single, back or typographic quotes | `'<value>'` in its own quotes |
| A number standing alone | `<n>`; a digit inside a word (`v2`) stays |

A double-quoted string is kept, since that is how a service spells its reason. An error with nothing in it is the issue
`no reason recorded`. An issue is named by the first eight bytes of the pattern's SHA-256, written as 16 hexadecimal
characters wherever it appears. Two errors that differ in a part the rules keep are two issues: the rules split an issue
rather than merge two, which is the safer way to be wrong when a whole issue is released at once.

**Set errors and row errors.** When an issue's records name no value, or all name the same values (the same bad unit in
every row of a file), the issue is a **set error**: fix its cause once (the cache, the mapping, a legal tag) and release
its records together. When they name different values, each its own row's, it is **row errors**: fix the rows in the
source, and each corrected row is planned again on its own. The listing reads the shape from each issue's newest and
oldest record; an issue's own page reads it from samples spread across it (five by default, up to 20), which can only turn
a set error into row errors, never the reverse.

**Reading issues.** `GET /flows/{pipelineId}/issues` and `sqlflow records issues` list a flow's issues, the most records
first (50 by default in the CLI): each with its records, held and failed, when they last changed, the pattern, an
example record, and `shared`, the error as the records looked at say it: the pattern with every part they all name
written in (a value, a number, a moment, an address) and the placeholder kept where they differ, which is what the GUI
and the CLI show. The listing reads `shared` and the shape from the newest and the oldest record.
`GET /flows/{pipelineId}/issues/{issue}` and `--issue` add the files its records came from and the samples, and read the
shape and `shared` from the samples; `sqlflow records list --issue <id>` lists its records. Everything is counted from the
blocked records through a filtered index that holds them alone, never kept beside them.

A reason the items of a `$forEach` array share is said once, with how many items say it
(`osdu.data.Curves[].CurveUnit, in 3 of 12 items: ...`), so records held for it are one issue however many of their items
it reaches ([mapping values](../flow/mapping-values.md#foreach-repeated-arrays-from-a-child-dataset)). A record held while such a
reason was said once an item (1.0.1 and before) keeps that error and its issue until it is planned again: released, it
falls into the one issue if it is held again.

**Releasing an issue** (`POST /flows/{pipelineId}/issues/{issue}/release`, `sqlflow records release --issue`) walks the
issue's records a page of 1,000 at a time and releases each page in its own statement; a record a run blocks with the same
issue while the walk goes is released too. The activity records the issue and the pattern the operator was shown.

**Adopting an issue** (`POST /flows/{pipelineId}/issues/{issue}/adopt` with `confirm`, the partition the flow's ledger is
kept in typed back, case ignored; `sqlflow records adopt --issue <id> --confirm <partition>`) walks the issue's records the
same way and asks each record held for an id OSDU holds to adopt it ([above](#adopting-a-record-osdu-already-holds)), a
slice of records to a transaction; a record of the issue held for anything else is left. Without `confirm`, or with
another partition, nothing is asked (400). The activity records the issue and its pattern.

**Records blocked before the ledger kept issues** have none. The control plane sorts them in the background, 500 records a
page a quarter of a second apart, looking again every ten minutes; until then a flow's issues say how many blocked records
are not sorted yet.

## Waiting records

A record whose rendered document refers, through a relationship its template declares, to an OSDU id another record of the
same ledger holds and has not delivered yet is left `waiting` when a worker comes to claim it, with `WaitingFor` naming that
id. Nothing is charged and no operator is needed: the record goes back to `pending` when the record holding the id lands.
An id no record of the ledger holds is not waited for (it is OSDU's or another system's), nor is one whose record was
removed from OSDU, nor one of an interface the source's order says this one does not wait for
([Interfaces](../flow/interfaces.md)). Every decision to wait is taken under one application lock, and a wait is followed
through at most 64 records: a longer chain is taken as one that might lead back to the record, which is then sent rather
than left waiting for good.

A waiting record's document is queued work like a due one: a plan compares a new row with it, a row that renders the same
document leaves it queued, and a row older than the version it carries is stale, never queued in its place.

With `target.verifyReferences: storage`, ids the ledger does not hold are asked of OSDU's storage service before the record
is sent, and a record naming one storage does not hold is held instead.

## Waiting for an undo

A delivery that does not complete is undone ([Removal and reversal](removal-and-reversal.md#unfinished-deliveries-and-their-undo)).
Newer work for a record waits while an earlier delivery's undo has not finished, since it would write ids the undo still
has to take back. The try is skipped (phase `undo-wait`), nothing is sent and nothing is charged. Once the undo has used its
ten tries the record is held, saying
`an earlier delivery of this record left <n> item(s) in OSDU that 10 undo tries could not take back (...)`, and releasing it
tries the undo again before the newer work.

## Related pages

- [The delivery ledger](ledger.md): every column a record and an attempt keep.
- [Submissions](submissions.md): how a run plans, claims and settles records.
- [Change detection](change-detection.md): what makes a record pending again.
- [Operations runbook](../guides/operations-runbook.md): held, failed, waiting and stuck records, task by task.
- [`sqlflow records`](../cli/records.md): list, show, issues and release from the command line.
