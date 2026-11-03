---
id: delivery-concept-gui-ledger
title: "The Ledger pages of the workbench: Delivery, Records, Removals, the audit trail, orphaned ledgers and a record's page"
type: concept
summary: "The Ledger pages of the workbench: Delivery, Records, Removals, Audit trail, Orphaned ledgers, a record's page, a submission's page and their dialogs."
keywords:
  - delivery page
  - sync a repository from the delivery page
  - records page
  - export records to excel
  - audit trail
  - record page
  - removal dialog
  - removals page
  - orphaned ledgers
  - submission page
  - redeliver dialog
related:
  - delivery-concept-gui
  - delivery-concept-gui-build
  - delivery-concept-ledger
  - delivery-concept-record-lifecycle
  - delivery-concept-removal-and-reversal
  - delivery-concept-renaming-a-pipeline
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/gui/src/features/delivery/RecordParentChips.tsx
  - osdu/gui/src/features/delivery/DeliveryOverviewPage.tsx
  - osdu/gui/src/features/delivery/RepositorySyncControl.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordsPage.tsx
  - osdu/gui/src/features/delivery/DeliveryActivityPage.tsx
  - osdu/gui/src/features/delivery/OrphanedLedgersPage.tsx
  - osdu/gui/src/features/delivery/DeliveryActivitySheet.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordPage.tsx
  - osdu/gui/src/features/delivery/RecordJourney.tsx
  - osdu/gui/src/features/delivery/RecordSourceTab.tsx
  - osdu/gui/src/features/delivery/RecordRenderTab.tsx
  - osdu/gui/src/features/delivery/RecordOsduView.tsx
  - osdu/gui/src/features/delivery/RecordArtifacts.tsx
  - osdu/gui/src/features/delivery/DeliverySubmissionPage.tsx
  - osdu/gui/src/features/delivery/RemovalDialog.tsx
  - osdu/gui/src/features/delivery/DeleteLedgerDialog.tsx
  - osdu/gui/src/features/delivery/RedeliverDialog.tsx
  - osdu/gui/src/features/delivery/ReverseDialog.tsx
  - osdu/gui/src/features/delivery/removals/DeliveryRemovalsPage.tsx
  - osdu/gui/src/features/delivery/removals/RemovedKeysGrid.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
---

# The Ledger pages of the workbench

The **Ledger** group of the menu, and the record, submission and dialog pages it leads to, in the title bar's
partition. The rest of what the module adds to the workbench is on [the OSDU pages of the workbench](gui.md).

## Delivery

Every delivery flow as a card, counted in the title bar's partition: a delivered bar, **Records**, **Delivered**,
**Pending**, **Held**, **Failed** and **Last 24h**, with the last delivery, the last verify and the last submission. A
flow that delivers only to other partitions says where, without counts. The header line totals what the flows hold
(delivered of total, pending, held, failed, drifted). **Find a record** with **Look up** opens Records looked up for
what is typed. A card opens the flow's Delivery tab. **Sync now** syncs the repository picked in the list beside it, so a commit reaches the
flows without a trip to the Repos page: it is the Repos page's own sync, with its message, and its trace followed by the
title bar's trace indicator, which opens it in the bottom panel when clicked. The list shows how each repository last synced, a paused one cannot be picked, the pick is remembered, and a mark
beside a repository whose last sync failed shows the error and opens that trace. When any sync ends, whoever started it,
the page reads its flows and counts again. A card shows what a flow's run did within seconds of the run
ending: the page checks each flow's newest run every five seconds (every three while one is queued or running), and reads
a flow's counts again when its newest run starts, moves on or ends, and at every check while it runs. The counts are also
read every 15 seconds, for what no run does (a release).

## Records

Where an operator starts from what they hold rather than from a flow. With nothing typed, the page lists the latest
records the delivery system took in or sent, newest first, refreshing every ten seconds. Typed, it lists the records any
of whose values start with the term, across every flow: an identity the mapping declares (`dataset.identity`), the
source key or one of its key columns, a word of the label, the OSDU id, the delivery key (which lands on one record),
or the ingestion file. **Match anywhere** finds the term inside the source key, the label, the OSDU id or the ingestion
file instead (`-0002` finds `LOG-0002/1`); it reads every record the other filters leave, so with more than 100,000 of
them it asks for a flow or a status first ([finding records](ledger.md#finding-records)). Each hit says which value
matched. The filters a flow's Records tab has narrow both the listing and a lookup: a status picker (`pending`,
`waiting`, `delivering`, `delivered`, `held`, `failed`, `deleted`, `reverted`), **Drifted only** (the records whose last
verify found OSDU holding something else, or nothing, and the removed records OSDU still holds) and **Step held** ([an enrichment set aside on its
hold](../flow/enrich-holds.md)), with a flow picker beside them. A row shows the status, the record (label, source key,
ingestion file and row), the flow, when it last changed and was last delivered, and its OSDU type, a link to the
record's OSDU tab; a row opens the record's page. The term (`q`), status (`status`), flow (`flow`, the ledger's flow
id), **Match anywhere** (`mode=contains`), **Drifted only** (`drifted=true`) and **Step held** (`stepHeld=true`) are in
the address, so a lookup is a link; a link that names a partition (`partition`) makes it the title bar's, so the lookup
is read in the partition it is about. **Export**, in the header, downloads every record the lookup reaches (at most
1,000) as an Excel workbook, each with its flow, interface and partition and the values the term matched; its About
sheet says how the term matched and what narrowed it, and when more match, so does it, and a flow's Records tab exports
up to 25,000 records of the flow ([records export](../cli/records.md#records-export)). Selecting records and acting on
them (**Redeliver**, **Adopt**, **Sync timelines**, **Remove**) is a flow's Records tab's, since each acts in one ledger.

## Removals

Every removal of the partition, from every removal flow: its delivery flow, that ledger's deletions by state (pending,
waiting for approval, removed) and its last run, the ones waiting for approval marked. One opens under a compact heading
(**Removals** › its name, a switcher to the others, **Run pipeline**): what it reads and compares under, its guard, its
last run, and, when its guard held what it found, one line saying how many wait, why on hover, **Review** and **Approve**,
whose dialog says what the approval does in the ledger and in OSDU and takes the partition typed back. Under it, a grid
that fills the window and reads 200 deletions at a time as it is scrolled: one line of state tabs with their counts, each
deletion's key (opening its record), why it counts as removed and as of when (the whole of it on hover, to copy), what
taking it out came to, when it was found and done. Picked deletions are delivered again through **Deliver again**. **Runs**
lists the removal's runs with what each read, found, took out and left. See [removal flows](../flow/removal.md).

## Audit trail

Every run and intervention across flows, newest first: when, **Took** (how long it ran, counting up while it runs), flow
(with its partition when none is picked in the title bar), **Action**, **By** (a schedule, a person, the command line), **Result** and **Target** (the record, else the
submission, else the run). It narrows by actor, flow,
action (`deliver`, `intake`, `drain`, `verify`, `probe`, `sync`, `release`, `adopt`, `redeliver`, `rerender`, `delete`,
`restore-previous`, `purge`, `reverse`, `undo`, `delete-ledger`, `remove-dimension`, `inventory-remove`, `inventory-restore`, `inventory-repair`,
`reassign-id`, `source-remove`, `lift-removal`, `ledger-move`) and outcome. Runs
that changed nothing are left out until **Show idle runs**. An entry opens on its ids, **Parameters**, its captured
**Log** (downloadable), and for a `deliver`, `replan`, `drain` or `intake` run that has ended, **Reverse this run**; a
`reverse` entry shows its **Reversal**. The flow is in the address (`flow`, the ledger's flow id), so one flow's trail is
a link; a link that names a partition (`partition`) makes it the title bar's, so the trail is read where that ledger is
kept.

## Orphaned ledgers

The missing documents in the workbench's partition: the flows no repository declares any more whose ledgers still hold
their records and history, those that left the most behind first. A strip heads the page with how many there are, the
records they left, how many of those claimed an OSDU id (held back from any flow delivering them until taken over), and
how many have a suggested flow. A search narrows them by flow or file, and a kind by kind when more than one is listed.

Each row names the missing flow (its kind, for any kind but delivery) with the file it was last declared in under it,
**Left** (when a sync last found it), **Left behind** (its records, runs, dimensions or inventories, each ledger and its
claimed ids on hover) and **Taken over by** (the suggested flow, or how many could). **Take over** opens a dialog in
three parts: **What it left behind**, each ledger with links to its records and its audit trail; **Taken over by**, the
declared flows that can take it over, each with what it shares with the missing one (same mapping, record table,
interface or folder) and how many of its ledgers it takes, the suggested one picked to start with; and **What goes
where**, each ledger with the ledger it goes to under the flow picked, or that it stays orphaned. The button says what
moves (**Take over 5,431 records**). Once taken over, the dialog says how each ledger ended, with a link to the trail it
moved to.

The rows with a suggested flow can be selected and taken over together: **Take over with suggested flows** lists each
pair and what it holds, then takes them over one after another and says how each ended. While any missing document
exists, the Delivery page says so and links here. See
[Renaming a pipeline](renaming-a-pipeline.md#missing-documents-and-taking-them-over).

## A record's page

`/delivery/records/{flowId}/{key}`, opened from any records list.

**The header** names the record (its label, status and a blocked mark), its **osdu** id, **flow**, **partition** and
**submission** as chips, and for a record linked to another through a linked template
([mapping $parent](../flow/mapping-parent.md)) its **parent** (the record of the canonical kind with the same minted id,
opening on that record's page, or in the explorer when no flow of the partition delivers it) or the records it is
**read by**, each named by its type, a record taken out of OSDU no longer among them; each try on the timeline gives the
**Parent version** its document was rendered
from. Then the one situation its state calls for (held or failed with the error and next try, and blocked in the same
line, waiting for another record, under a lease, blocked, released and waiting for its run, held with nothing to move it,
removed, records waiting on this one). An error is said as the Issues tab says one: the property it names and how far
through a list it reaches, over its reason on one line; the whole of it is on hover, each reason under its property and a
`$coalesce`'s alternatives numbered, with a copy. Its buttons:

| Button | What it does |
| --- | --- |
| **Sync timeline** | reads the record's row from its ingestion table and consolidates the ledger with it; nothing is sent |
| **Verify** | queues a verify run for this record, comparing what OSDU holds with the ledger |
| **Redeliver** | brings the record up to date from its current row, or sends it again (the Redeliver dialog, opening on **Send again** for a record that is not delivered) |
| **Release** | while blocked, or held with nothing to move it: a kept document is sent as it is, otherwise the next run plans the record from its row |
| **Adopt** | while held because OSDU already holds a record at the id the record's key makes, written by another system or another instance of the delivery: asks first, then asks the flow's next run to take that OSDU record over at the version OSDU holds then and write the row there; it can queue a deliver run scoped to the record at once ([Adopting a record OSDU already holds](record-lifecycle.md#adopting-a-record-osdu-already-holds)). The record then stands as **Adoption asked: waiting for the next run**, and the timeline shows the request, and the run's adoption as its own step naming the id and the version |
| **Send without waiting** | while waiting: sends it on its next claim without waiting for the record it refers to |
| **Remove** | opens the removal dialog for this record |

**The milestones** under it: **From file**, **Loaded**, **Last change**, **Delivered**, **Verified**, and **Removed**
for a record that was.

**The tabs**, each named for the question it answers and carrying no count (a tab's own first line counts what it holds):

- **Timeline**: what happened to the record, newest first, in chapters (each started by a row arriving, changing or
  going, or by someone's request), on three lanes (**Source**, **Ledger**, **OSDU**); every try with what OSDU answered,
  and every request with who made it. It narrows to one lane or to the requests people made.
- **Source**: where the row came from (ingestion file and row, when received, source key, delivery key, mapping, key
  columns with a copy of the key as the ledger holds it), a newer row a waiting document is built from, and a read of the
  rows as the ingestion tables hold them now, with the flow's own connection.
- **Render**: **Render** builds the record from its current row with the mapping and the cache as they are now, and reads
  what OSDU holds, at once; nothing is sent. It shows the mapping and cache versions used beside those that rendered what
  OSDU holds, the route, what the next run would do (its reason on one line, the whole of it on hover; a record the run
  would skip without rendering, though it renders now to another document than the one last delivered because something
  it reads from OSDU changed, says so and that **Redeliver** sends what it renders now), **Held for**:
  why a delivery would hold the record (each reason under the property it names, a `$coalesce`'s alternatives numbered),
  the preflight's warnings, and the **Rendered record** **Beside OSDU** (the differences: **Rendered now** on the left,
  **In OSDU** and the version read on the right) or **Whole document**. A record
  its flow never wrote to OSDU is rendered without a read, and the document's line says OSDU holds nothing of it to set
  it beside.
- **OSDU**: **Read** reads the record as OSDU holds it through the flow's route, in the record inspector the explorer uses
  (outline, fields or JSON, versions and **Compare**, linked records, Validation). Icons open it in the explorer, or in a
  window of its own (`/delivery/records/{flowId}/{key}/osdu`). Only a record that claimed its OSDU id (it queued a document
  under it, or took over the record OSDU held there) is read: one that was only ever held names the id its key makes, but
  its flow wrote nothing there, so the tab says **Not sent yet**, and **Read** and **Verify** wait until it is sent.
- **Enrichments**: shown for a record its mapping enriches ([enrichments](../flow/enrich.md#what-the-ledger-keeps)), read from
  the ledger alone. A line of counts (enrichments, records added, linked, no longer linked, set aside), each enrichment in
  the order it renders with what it adds, its mapping and where its rows came from (**set aside** under its name when it was
  on the last render), then **Records the enrichments added**: each with whether the record links it, its type and the row
  key that tells it from the others (the whole OSDU id on hover and a copy away), the version OSDU holds, the row it came
  from and its writes, each listed on hover with when it went, the version it landed as and the one it replaced, read from
  the record's attempts.
- **Artifacts**: what deliveries of the record created in OSDU, with each one's state, and the ones an undo still has to
  take; the header says so while an unfinished delivery left something. Once a delivery completes, only the datasets,
  content, outputs and dataspaces it made stay listed; the record's own write, a version or a session leaves the list, so
  a record delivered through a DDMS session, or in one call, shows none.

## A submission's page

`/delivery/submissions/{id}`: what the submission read (the ingestion table, connection, change window or the whole
scope), its counts, when it was received, started and completed and its **Duration** (from its start to its completion,
counting up while it is planned or running, with the wait after it was received in the hover), the runs that carried
it, and tabs **Attempts**, **Batches** (each work batch with its own duration), **Parameters** and **Render context**.
**Records it delivered** and **Records it last planned** open the flow's Records tab narrowed to each set.
**Reverse this submission** opens the reverse dialog while the submission has anything to reverse; once asked, the
reversal's card shows its state, its records by outcome and its runs.

## The dialogs

- **Remove** (the removal dialog): names the target first (flow, endpoint, route, data partition), then **In OSDU**, one
  choice of **Remove the record** (reversible: it stops resolving, every version kept), **Restore the previous version**,
  **Purge earlier versions**, **Purge everything**, or **Leave as it is** (for records already removed), each with the
  call it makes and whether it can be undone; then **In the ledger**, **Delete from the ledger**, offered with a choice
  that leaves the records out of OSDU. A permanent choice, or deleting from the ledger, asks for the data partition to be
  typed back. Confirmed, it queues a `remove` run of the flow, listed under **Runs** with the flow's other runs: **Open
  run** on the notice opens it, and the flow's views read again within seconds of it ending. See
  [removal and reversal](removal-and-reversal.md).
- **Delete ledger** (a flow's Delivery tab): removes every record of every interface from OSDU, reversibly, then deletes
  the ledgers whole; the next run reads every row and delivers each as a new record. The partition is typed back first.
- **Redeliver**: **Bring up to date** renders the records again and sends only what renders differently, showing first
  what that would send; **Send again** sends them whatever their hashes say, **Everything** or one part (the record,
  files, bulk data, the workflow run, or every part of the payload). With **Queue a deliver run now** ticked, a run takes
  the records at once; otherwise the flow's next run does.
- **Reverse this run / submission**: says how many records it delivered and what reversing would do with each (what it
  created is removed reversibly, what it updated gets back the version OSDU held before, nothing is purged), then queues
  the reverse run. Every record put back is blocked until its source changes or it is released.
