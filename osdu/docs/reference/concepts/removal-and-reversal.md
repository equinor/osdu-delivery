---
id: delivery-concept-removal-and-reversal
title: "Removing records from OSDU, deleting the ledger, reversing a run and redelivering records"
type: concept
summary: "Every way to change what OSDU holds after delivery: remove records, step back a version, delete the ledger, reverse a run, undo, redeliver, sync."
keywords:
  - remove records from osdu
  - soft delete
  - purge
  - delete ledger
  - delete from the ledger
  - reverse a run
  - reversal
  - restore previous version
  - undo
  - unfinished delivery
  - redeliver
  - bring up to date
  - sync timeline
  - rollback
  - unit of work
  - artifacts
  - undo unfinished deliveries
  - records the source no longer holds
  - removal flow
related:
  - delivery-concept-ledger
  - delivery-concept-artifacts-and-undo
  - delivery-concept-record-lifecycle
  - delivery-flow-routes
  - delivery-guide-operations-runbook
  - delivery-cli-records
  - delivery-concept-api
  - delivery-guide-finding-orphans
  - delivery-concept-protocols
  - delivery-flow-removal
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/RecordRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/ReversalRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/ReversalRoute.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/ReversalAvailability.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/PreviousVersionRestore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/VersionWriteBack.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/RecordRestores.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/UndoRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Problems.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Reversals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Protocols/IDeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryLedgerEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryReversalEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryReversalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryArtifactVerbs.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Removal.cs
---

# Removing records from OSDU, deleting the ledger, reversing a run and redelivering records

After a record is delivered, everything that changes what OSDU holds of it, or what the ledger says about it, goes through
the [ledger](ledger.md): each record gets an attempt saying what was done, and each request is an activity naming who asked
and when. This page covers every such action, what it calls in OSDU, and what it leaves in the ledger. Removals are checked
against the storage service's OpenAPI description; route-specific calls are in [Routes](../flow/routes.md).

| You want to | Use |
| --- | --- |
| Take records out of OSDU, reversibly | A removal at the `record` scope. |
| Take out of OSDU what the source no longer holds, on a schedule | A [removal flow](../flow/removal.md). |
| Make the version before the latest current again | A removal at the `previous` scope. |
| Destroy a record's earlier versions, or the record for good | A removal at the `history` or `everything` scope. |
| Forget records already removed from OSDU | Delete them from the ledger. |
| Start a flow from nothing: out of OSDU and out of the ledger | Delete the ledger. |
| Put OSDU back as it was before one run or one submission | A reversal. |
| Take back what a delivery that did not complete left in OSDU | The undo (mostly automatic). |
| Send records again | Bring up to date, or Send again. |
| Make the ledger agree with the ingestion tables | A timeline sync. |

## Removing records from OSDU

OSDU's storage service offers three removals, and they are not degrees of one thing. OSDU Delivery names them, and a
fourth that removes nothing, the same way in the API, the run, the GUI and the ledger:

| Scope | Storage call | What goes | Reversible | The ledger afterwards |
| --- | --- | --- | --- | --- |
| `record` | `POST /records/{id}:delete`, or `POST /records/delete` for several ids (500 a request: the route's chunk size, not a service limit) | The record stops resolving; nothing is destroyed. | Yes | `deleted`, not blocked, asked to be planned again; hashes and version forgotten. Attempt `deleted`, phase `delete`. |
| `previous` | `GET /records/{id}/{version}`, then `PUT /records` | Nothing: the version before the latest is written back as a new version. | Yes | `reverted`, blocked. Attempt `restored`, phase `restore-previous`. |
| `history` | `DELETE /records/{id}/versions` | Every version but the latest; the latest stays live. | No | Unchanged (OSDU still holds what the ledger says). Attempt `historypurged`, phase `purge-history`. |
| `everything` | `DELETE /records/{id}` | The record and every version. | No | As `record`. |

**A removed record is delivered again by the next run.** Removing is a clean-up, and the record follows its source
again: the removal asks for it to be planned again (`PlanRequestedUtc`), so the flow's next run, a scheduled one
included, reads its row by key whether or not the row changed, finds nothing delivered and creates it again, under the
same OSDU id. What the source holds and OSDU does not is delivered again without a `replan`, and the run reads only the
rows asked for, never the whole scope. A row the ingestion table marks deleted is never sent, so a record that should
stay out of OSDU while its row stays is taken out of the source. `previous` does block, since the next run would
otherwise write the version it took back again. The removal that opens [deleting the ledger](#deleting-the-ledger) asks
for nothing, since the run after it reads every row, and neither does an inventory's removal of an id the ledger already
holds as removed ([Inventory flows](../flow/inventory.md)), which only makes OSDU agree with the ledger.

**Which records.** A removal names its records by key, or by a listing filter that is resolved on the node when the
removal runs, so "every record this submission delivered" travels as a filter rather than as thousands of keys. The filter
has two ways of naming a submission's records: `submissionId` is the records the submission last planned, and
`deliveredBy` is the records it delivered, resolved through its delivered attempts, which stay its own however many
submissions touch them later. The filter carries every part of the listing it was taken from, the issue a listing is
narrowed to included, so the run removes the records the operator counted and no others. One removal takes at most 25,000
records; a filter that matches more is refused, when the removal is asked for and again when the run resolves it, with
`The filter matches more than 25000 records as far as the listing counts; a removal takes at most 25000 at a time. Narrow the filter and remove in parts.`
A filter that matches another count than the operator was shown is refused too (`The selection changed`).

**How it runs.** A removal is a run of the pipeline with the operation `remove`, listed under **Runs** and on the flow's
runs with its trace, its run log and its outcome. Its payload names the interface, `scope`, `purgeLedger`, and the records
by `recordKeys` or by `filter` ([Running an OSDU flow](../cli/run.md)). As a run of the pipeline it never runs beside
another of them: one asked for while a delivery of the pipeline runs starts once that run ends, and the next delivery
waits for it. Records are removed in chunks of 500, batched into one request where the route and the scope allow it
(only the reversible scope has a bulk call), each record with its own attempt and the chunk's correlation id. A record
OSDU has already lost is reported as `already-gone` and settled as removed; a record with no OSDU id, or one that never
queued a document (so this flow wrote nothing to OSDU under that id), is skipped. While a chunk is removed, its records
are held under a lease of the removal's own, renewed every half of `reliability.leaseSeconds` as a worker renews its
lease: no worker claims one of them, and a plan that queues newer work for one leaves it held, so OSDU never ends up holding
what a worker sent under the removal while the ledger says removed. A record another lease holds (a worker is sending it,
or another removal has it) is skipped:
`the record is held under a lease right now (a worker is delivering it, or another removal is taking it out); remove it once that work settles`.
A removal first recovers the flow's leases that ran out, so a record a stopped worker held is removed. The hold is let go
when the chunk ends, whatever its outcome, and a removal that stops lets its records go once its lease runs out; either way
nothing is charged to a record and no attempt is written for the hold. A step back to the previous version holds nothing:
it passes over a record with work queued or under a lease itself. What an unfinished delivery of a record
left is undone before the record is removed ([below](#unfinished-deliveries-and-their-undo)). The removal is an activity
of kind `delete` (or `restore-previous`) naming the run, and the run's result carries the counts and up to 200 per-record
outcomes, failures first. A removal OSDU refused any record of ends failed, saying how many and why the first ones failed:
`<n> of <m> record(s) of '<flow>' could not be removed from OSDU: <record>: <why>; ...`. Every other record was removed,
and the ones refused stay in the ledger as they were, to be removed again once what refused them is settled.

**Routes.** The `ddms` route calls what the DDMS serving the records offers (a Wellbore DDMS, Well Delivery or RAFS record
is removed with a `DELETE` of its own), the `dspdm` and `etp` routes write rows and objects that keep no deleted copies or
versions, so only `everything` applies there, and Seismic Store on a gc deployment refuses `everything`. The GUI's removal
dialog shows the exact call each scope makes for the flow before anything is queued. See [Routes](../flow/routes.md).

**Where.** **Remove** on a record's page and on a flow's Records tab selection opens the one removal dialog: an **In OSDU**
part (**Remove the record**, **Restore the previous version**, **Purge earlier versions**, **Purge everything**, and
**Leave as it is** for records removed already) and an **In the ledger** part (**Delete from the ledger**, offered only with
a choice that leaves the records out of OSDU). Anything permanent asks for the partition to be typed back. Once the
removal is queued, **Open run** on the notice opens its run. The API is
`POST /api/v1/delivery/records/{flowId}/{key}/delete` and `POST /api/v1/delivery/flows/{pipelineId}/records/remove` (with
`/remove/preview`), each queuing the `remove` run and answering its `runId`. From a terminal, the run itself (a source of
several interfaces names the one it removes in, with `"interface"`):

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation remove --set partition=dev   --payload '{"recordKeys":["<delivery key>"],"scope":"record","purgeLedger":true}'
```

### Restoring the previous version

OSDU has no call that removes only the latest version, so `previous` reads the version OSDU held before the write that left
the latest and writes it back as a new version, as it was. Only the data keys another system writes on records of that
type are carried over from the latest version. "Before the latest" is the ledger's answer, not the version just below in
OSDU's list, since one delivery can write two versions (a Wellbore DDMS record, then its bulk data). The version replaced
stays in the record's history, so asking again brings it back.

A record is passed over, saying why, when OSDU holds another version than the ledger says this flow left, when the write
that left the latest created the record (`OSDU held nothing before it; remove the record instead`), when the attempts that
would say which version came before were pruned, when OSDU no longer keeps that version, or when work is queued or in
flight for it. A route that cannot write a version back (every route but `storage` and the Wellbore DDMS on `ddms` under a
platform endpoint) does not offer it.

## Deleting removed records from the ledger

A record removed from OSDU can be deleted from the ledger: as the removal's extra step (`purgeLedger`, **Delete from the
ledger** in the dialog), or later on its own, asking nothing of OSDU (**Leave as it is** with the ledger step,
`POST /flows/{pipelineId}/records/purge` for keys, a filter or every removed record, `POST /records/{flowId}/{key}/purge`).
It is an activity of kind `purge`.

Only a record the ledger marks `deleted` goes, none a lease holds, and none with something an unfinished delivery left that
its undo has not taken back. Each goes with its attempts, search entries and row, one slice of records to a transaction, and
leaves one line in `osdu.PurgedRecord` ([The delivery ledger](ledger.md#osdupurgedrecord-what-is-kept-of-a-record-deleted-from-the-ledger)).
The record's own route refuses with 409: `Not removed from OSDU` for a record OSDU may still hold, `Undo unfinished`
for one an undo has still to finish, and `Record busy` for one work started on after it was read. History purges and
`previous` never delete from the ledger:
`A record is deleted from the ledger only with a removal that takes it out of OSDU (record or everything), not with <scope>.`

What goes is the record's history, and nothing else: the ledger's watermarks stay. The line kept of the record keeps its
key tuple and asks for its row to be read again, so the flow's next run reads that row by key, beside the records asked
to be planned again, and delivers it as a record the ledger never held. Once a run has planned the row, or found it gone
from the ingestion table, the request is answered; a row outside the run's scope keeps it for the run of its own scope.
Deleting the record again later moves the request to the new line. No request is left by a record a reversal took out
(it is blocked, and stays out), by one the ledger holds no key tuple for (its row is read again when it changes), or by
[deleting the ledger](#deleting-the-ledger), whose next run reads every row. The record's page, asked for a record deleted
from the ledger, says so while the request stands. To have every row read and delivered again, delete the ledger, or run
`replan`.

Records deleted from the ledger before the line kept their key tuple have no request: their rows are read again when they
change, or by a `replan`.

## Deleting the ledger

**Delete ledger** on a flow's Delivery tab deletes the ledger of every interface of the pipeline in the partition in view,
for a flow to be delivered again from nothing. It removes the pipeline's records from OSDU first, then deletes everything
of the pipeline from the ledger, and the next run reads every row. It queues a run of the pipeline with the operation
`delete-ledger` (`POST /api/v1/delivery/flows/{pipelineId}/ledger/delete` with `confirm`), so no delivery of the pipeline
runs beside it. Nothing is queued unless `confirm` names the partition the ledger is kept in, and the run checks it again
on the node before it touches anything. For each interface, in reverse delivery order, the run:

1. Refuses while a worker holds a lease on the ledger's records:
   `A worker holds a lease on records of '<flow>' until <time>, so nothing was removed or deleted: work is in flight on this ledger. Delete it once that work has ended.`
2. Removes from OSDU, at the `record` scope, every record OSDU may hold, a page of the ledger at a time in key order,
   each page held as a removal holds its records; a record already marked removed is not asked about again. Before a record
   is removed, what an unfinished delivery of it left is undone; once it is out of OSDU, the datasets, content and outputs
   its deliveries minted are removed reversibly too.
3. Only when OSDU answered for every record, deletes the ledger whole: every record whatever its state (one line of each
   kept in `osdu.PurgedRecord`), then its submissions, work batches, leases, lease events, watermarks and reversals.

A record OSDU refused to remove keeps the whole ledger as it was, with the records it did remove marked removed, and the run
fails: `<n> record(s) of '<flow>' could not be removed from OSDU, so the ledger was kept as it was and OSDU holds nothing it forgot: ...`.
So does a record a worker claimed after the walk began (a run started beside it), named with why it was passed over.
An item an undo could not take back keeps it the same way. Deleting the ledger again does the rest. The `dspdm` and `etp`
routes have no reversible removal, so deleting their ledger is refused before anything is asked.

What stays: the activities and their links, the lines of the deleted records, the artifacts the deliveries recorded
(`osdu.Artifact`, the ids they minted), and the ledger's entry in the directory. No line asks for its row to be read
again, and the requests earlier deletions of single records left are answered. The
next run of the flow, a scheduled one included, finds no watermark, reads every row and delivers each as a new record under
the OSDU id its mapping gives, which is the id the record had.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation delete-ledger --set partition=dev --payload '{"confirm":"dev"}'
```

## Reversing a run or a submission

A run that went wrong (a wrong file loaded, a wrong mapping or cache, the wrong partition) is undone by reversing it: OSDU is
put back, record by record, as it was before. The source of a reversal is a **submission** (every record it delivered,
whichever run drained it) or a **run** (every record delivered under the submissions it planned, and every record it
delivered itself). For each record the source delivered, the ledger reads from the record's own attempts what OSDU held
before the source's first delivery of it:

- a record the source **updated** gets that version back: read from storage and written again as a new version (only the
  data keys another system writes are carried over from the latest). The record becomes `reverted` and blocked, with the
  hashes and origin of the attempt that delivered that version;
- a record the source **created** is removed again at the `record` scope, and becomes `deleted` and blocked.

Where the attempts before the source were pruned, OSDU's version list decides: the newest version older than the source's
first is put back, and with none the record is removed. A reversal never purges.

| Route | Gives a version back | Removes what the source created |
| --- | --- | --- |
| `storage` | Yes | Yes, in bulk |
| `ddms`, a Wellbore DDMS record under a platform endpoint | Yes, through storage; the version carries its own bulk link | Yes, the DDMS's own delete |
| Other routes | No: files, datasets or rows beside the record are not brought back by a version, so such a record is passed over as `not-reversible` | Where their `record` scope removes reversibly |

A record is reversed only while it is still the record the source left. Each one passed over gets a `skipped` attempt
(phase `reverse`) saying why:

| Outcome | When |
| --- | --- |
| `superseded` | A later run delivered the record again, or it was restored or removed since. Reverse the later run first. |
| `changed-in-osdu` | OSDU's latest version is not the one the source left: something outside this flow wrote it. |
| `missing-in-osdu` | OSDU no longer holds a record the source updated. |
| `unchanged` | The version before the source is the one the source left. |
| `version-missing` | The version to put back can no longer be read from OSDU. |
| `busy` | Work is queued or in flight for the record; taken again when the reversal is asked again. |
| `not-claimed`, `not-in-ledger` | The flow never claimed the record's OSDU id, or the ledger no longer holds the record. |
| `not-reversible` | The route cannot do what the record needs. |

A reversal is a run of the flow with the operation `reverse` and the payload `runId` or `submissionId` (with `interface`
for a source of several interfaces), recorded as a `reverse` activity. It lists what the source delivered, then reverses
500 records at a time in key order; each record's attempt, its custody change, its line under the activity and its
reversal item are written in one transaction, and only while the record still stands at the version the source left. A
reversal is kept per source (`osdu.Reversal`, one `osdu.ReversalItem` per record), so a run stopped anywhere loses nothing:
asking for the same source again resumes it, taking what is pending, what failed and what was `busy`, and checking an item
left mid-write against OSDU before writing again. A source that delivered nothing, and one whose reversal settled every
record, are refused before a reversal is opened, for example
`Submission <id> delivered nothing to OSDU in this ledger, so there is nothing to reverse.`

After a reversal, `reverted` and `deleted` records stay blocked while their source rows are unchanged, so a scheduled run
does not send the same rows again; a corrected row flows through on its own, and a release makes a `reverted` record
`delivered` again ([Record lifecycle](record-lifecycle.md#releasing-a-record)). A record that was held or failed at a
later row than the reversed delivery (its row moved on after that run, and was refused) stays blocked at that later row,
as `previous` leaves it too, so reading the row again moves nothing. A cache change never marks a `reverted` or `deleted`
record: what the reversal put back stays until the row moves or someone releases the record.

**Where.** **Reverse this run** on a `deliver`, `drain` or `intake` entry of the audit trail and on the page of a
`deliver`, `replan`, `drain` or `intake` run once it has ended,
**Reverse this submission** on a submission's page, **Resume the reversal** when one stopped. The API is
`POST /flows/{pipelineId}/reverse` after `POST /flows/{pipelineId}/reverse/preview`; `GET /flows/{pipelineId}/reversible`
says whether a source can be reversed. On the command line:

```bash
sqlflow records reverse flows/welldb-wellbore-03-delivery.yaml --partition dev --run 0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f --preview
sqlflow records reverse flows/welldb-wellbore-03-delivery.yaml --partition dev --run 0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f
sqlflow records reversals flows/welldb-wellbore-03-delivery.yaml --partition dev --run 0193f2a4-7c1e-7b2d-9e4f-1a2b3c4d5e6f --outcome changed-in-osdu
```

## Unfinished deliveries and their undo

A delivery of one record can take many calls that each change OSDU (files uploaded and registered, the record written, bulk
data sent through a session, rows posted), and OSDU has no transaction across them. So a delivery of one record's pending
work is a **unit of work**: it begins with the first call that can change OSDU, spans every try while the record stays
pending, and ends committed or aborted. Every object a unit creates in OSDU, or sets out to create, is an artifact in
`osdu.Artifact`, written in the same transaction as the step that made it. A call whose id the service mints, and whose
answer can be lost, is preceded by an **intent**: the artifact is written before the call with what finds the object
without its id (a file's landing-zone path, the record a session belongs to), and completed with the id when the answer
comes. A route that makes one atomic write (`storage`) creates no artifacts. When a unit commits, the ids it minted
that stay in OSDU (datasets, content, outputs, dataspaces) become `live`, and `superseded` once a later delivery
replaces them; of the artifacts that only stood for its progress (the record's own write, a version, a session, a lock,
rows, points, objects, a run), those still `pending` and those the route removed itself are deleted, since the record's
state names what they stood for, while one a route settled as `kept` or `gone` stays as it is. An intent whose call never
answered becomes `due` even though the unit committed, so the undo looks for what that call may have made
([Artifacts and undo](artifacts-and-undo.md#how-the-states-move)).

An aborted unit is undone:

| When | What undoes the unit |
| --- | --- |
| A try ends held or failed | The worker, at once, under the record's lease. |
| Newer work is planned or staged for a record while a unit is unfinished, or a plan holds the record | The claim that next takes the record, before the newer work is sent. |
| Anything left: an undo that failed and is due again, a unit nothing claimed again | The sweep at the end of every deliver run and flow-wide drain (up to 10,000 records a run), and the `undo` operation (`sqlflow run <flow.yaml> --operation undo`). |
| A removal, and deleting the ledger | The removal, before it takes the record out of OSDU. |

An undo removes what the unit created at the route's reversible scope (a soft delete, a logical DDMS delete, an abandoned
session, a released lock), writes back the version the unit replaced where the route can, and keeps, with why, what no
call removes. A record is removed only when storage's `createTime` says the unit created it (no earlier than the unit's
start, less five minutes for clocks); an update is given back its earlier version, never deleted. What a DDMS made beside
the record goes back before the record, and the record before the datasets it names; when one of them cannot be undone
yet, the others wait with it, so OSDU never serves a record naming what the undo already removed. Each undo is an attempt
(`undone`, phase `undo`) naming every artifact and what became of it.

An undo is idempotent. One that cannot reach OSDU, or that OSDU refuses, is tried again after 1, 2, 4 ... minutes (at most
six hours apart), up to ten times, and then left for an operator: `sqlflow records undos <flow.yaml>` lists what is left;
fix what stops it, then run `undo` with `force`, which tries the exhausted ones again. Meanwhile newer work of the record
waits without being charged, and the record is held once the undo has used its tries
([Record lifecycle](record-lifecycle.md#waiting-for-an-undo)).

Some things no call removes, and an undo keeps them, named: the series versions the production historian accepted, a
Seismic Store dataset registered on gc (where one dataset's delete takes the files of every dataset in its subproject),
RAFS content kept in its own blob store, what a Wellbore DDMS session aggregated, bulk data under a DDMS's logical delete,
the files behind a soft-deleted dataset, and the files a registration left in a landing zone or staging area.
`sqlflow records artifacts <flow.yaml> --key <key>` lists every artifact of a record with where it stands.

```bash
sqlflow records undos flows/welldb-welllog-03-delivery.yaml --partition dev
sqlflow records artifacts flows/welldb-welllog-03-delivery.yaml --partition dev --key welldb:LOG-0042
sqlflow run flows/welldb-welllog-03-delivery.yaml --operation undo --set partition=dev --payload '{"force":true}'
```

In the GUI: a record's **Artifacts** tab, and **Unfinished deliveries to undo** on a flow's Delivery tab with
**Undo unfinished**.

## Redelivering records

A record is sent when what it renders to, or its payload, differs from what the ledger says OSDU holds. A row change, a
mapping, template or parameter change, or a cache change reaches the records it affects on its own. What none of those
reach is a change in how the engine renders (an upgrade that writes a value another way), a record OSDU lost, or one
someone changed in OSDU. A redelivery is how an operator reaches them, in one of two ways:

| Way | What it does to each record | Sends |
| --- | --- | --- |
| **Bring up to date** (`rerender`) | Keeps the hashes of what OSDU holds and the business date of the row it stands at, forgets the ingestion fingerprint it was planned under, and asks for it to be planned again; a record asked for is rendered whatever version it was planned under. Only delivered records with an OSDU id are reached. A record brought up to date and then reversed before a run planned it is blocked at its row's business date, as any reversed record is. | Only a part that renders differently; a record that renders the same writes no new version. |
| **Send again** (`redeliver`) | Forgets the hashes of the part named and asks for it to be planned again. | That part, changed or not: OSDU keeps a new version. |

`redeliver` names a part: `all` (the default), `record` (or `metadata`) for the document alone, or of the payload `files`,
`bulk` or `workflow` where the route sends that part, or `payload` for all of it; a part the route does not send is
refused, naming the parts it does (the `storage` route sends the record alone). Either way the record's own
hashes and the pinned mapping decide what is rendered: a redelivery never sends a document the mapping does not produce
from the current source rows.

A record OSDU lost (a verify with `reconcile`, or an inventory's **Deliver again**, found it missing) is sent again under
its own id, its verify outcome `missing` telling the storage route to bring a soft-deleted record back first (`PATCH
/records/{id}` with `{"deleted": false}`) so the keys the write keeps of it are kept; a purged one is written again under
the same id. Asked for, an inventory gives such a record a new OSDU id instead, written under it from then on
([repairing missing ids](../flow/inventory-removals.md#repairing-missing-ids)).

The request is the ledger's (`PlanRequestedUtc`), so whichever run meets the record next, a scheduled one included, plans
it, 5,000 records a pass. In the GUI, **Redeliver** (a flow's Records tab selection, a flow's Delivery tab for every
delivered record, a record's page) offers both ways, first asking a `plan` run what bringing the records up to date would
send (the selection's delivered records when they are 1,000 or fewer, else the first 1,000 as a sample), and queues a
deliver run once confirmed. Each request is an activity (`rerender` or `redeliver`) naming every record it reached.

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --payload '{"rerender":true}'
sqlflow run flows/welldb-wellbore-03-delivery.yaml --set partition=dev --payload '{"recordKeys":["6f1c2a9e-4b7d-4c1e-9a3f-2d8e5b7c1a40"],"redeliver":"record"}'
```

Without `recordKeys` a payload's `rerender` or `redeliver` reaches every delivered record of the flow; a run scoped to
`recordKeys` with neither sends those records again whole.

## Records the source no longer holds

A [removal flow](../flow/removal.md) reads the keys a source holds now, whole, and the keys it lists as removed, compares
them with a delivery flow's ledger, and takes out of OSDU the records whose keys are gone. What it finds is the ledger's
own record of each deletion ([`osdu.RemovedKey`](../tables/removed-key.md)), kept under the delivery flow's ledger identity
whichever pipeline runs the removal. Taking them out goes through the removal above, at the `record` scope (reversible),
under the removal's lease, with what unfinished deliveries left undone first, and differs from an operator's removal in two
ways:

- **It is incremental.** Only the `pending` deletions are taken; one taken out is `removed` and never asked of OSDU again.
- **The record is kept out, not delivered again.** The plan delivers no row of the key that is not newer than its removal,
  and a record's attempt (phase `source-removed`) says which removal flow took it out and why. A newer row, the key coming
  back in the source, or an operator's **Deliver again** lifts the deletion, and the record follows its source again.

A run that finds more removed than its guard allows takes nothing out and waits for an operator's approval
([The guard](../flow/removal.md#the-guard)).

## Syncing the ledger with the source

A timeline sync consolidates the ledger with the ingestion tables, for one record (**Sync timeline** on its page), a
selection or filter, or every record of an interface (**Sync timelines**). It is a run with the operation `sync`, recorded
as an activity, and it renders and sends nothing. Each record's row is read by its stored key, in the scope its last
submission ran with, 1,000 records a page:

| What the sync finds | What it does |
| --- | --- |
| The row's arrival (`InsertedDate_DW`) is missing from the ledger, or differs | Writes it to the record. |
| The row changed past both versions the ledger holds, and no plan saw it | Asks for the record to be planned; the next run reads it by key and decides it. |
| The ingestion table marked the row deleted without the ledger knowing | Asks for it to be planned; the next run holds it as a deleted row. |
| The row is gone from the table, or outside the record's scope | Writes a `skipped` attempt, phase `source-missing`, once. The record keeps its status: removing it from OSDU stays a removal someone asks for, or a [removal flow](../flow/removal.md) finds. |
| A record held because its row was deleted, whose row is no longer deleted | Counts it, for an operator to release. |
| Everything agrees | Nothing. |

Rows the ledger holds no record of are not a sync's to find: a row's delivery key comes from its mapping, so a plan finds
them (`plan` to see them, the next run or `replan` to deliver them).

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation sync --set partition=dev
```

## Related pages

- [The delivery ledger](ledger.md) and [Record lifecycle](record-lifecycle.md).
- [Routes](../flow/routes.md): what each route calls to remove a record.
- [Finding orphans](../guides/finding-orphans.md): ids OSDU serves that no ledger accounts for.
- [Removal flow](../flow/removal.md): taking out what a source no longer holds, on a schedule, with a guard.
- [Operations runbook](../guides/operations-runbook.md): these actions, task by task.
