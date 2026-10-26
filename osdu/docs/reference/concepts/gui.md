---
id: delivery-concept-gui
title: "The OSDU pages of the workbench: what each page and tab shows, and its actions"
type: concept
summary: "The OSDU menu the SQLFlow workbench gains (Ledger, In OSDU, Build, Setup), each page's tabs and buttons, the flow and run page additions, and who may use them."
keywords:
  - gui
  - workbench
  - osdu menu
  - delivery page
  - records page
  - export records to excel
  - audit trail
  - record page
  - flow page tabs
  - removal dialog
  - partition picker
  - flow does not deliver to this partition
  - cache page
  - mapping builder
  - templates page
  - tests board
  - partitions page
related:
  - concept-control-plane
  - concept-authentication-and-identity
  - delivery-concept-explorer
  - delivery-concept-search-terms
  - delivery-concept-ledger
  - delivery-concept-removal-and-reversal
  - delivery-concept-partition-cache
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/src/features/delivery/RecordParentChips.tsx
  - osdu/gui/src/features/delivery/PartitionSwitcher.tsx
  - osdu/gui/src/features/delivery/DeliveryOverviewPage.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordsPage.tsx
  - osdu/gui/src/features/delivery/DeliveryActivityPage.tsx
  - osdu/gui/src/features/delivery/OrphanedLedgersPage.tsx
  - osdu/gui/src/features/delivery/useLinkedPartition.ts
  - osdu/gui/src/features/delivery/DeliveryActivitySheet.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordPage.tsx
  - osdu/gui/src/features/delivery/RecordJourney.tsx
  - osdu/gui/src/features/delivery/RecordSourceTab.tsx
  - osdu/gui/src/features/delivery/RecordRenderTab.tsx
  - osdu/gui/src/features/delivery/RecordOsduView.tsx
  - osdu/gui/src/features/delivery/RecordArtifacts.tsx
  - osdu/gui/src/features/delivery/DeliverySubmissionPage.tsx
  - osdu/gui/src/features/delivery/DeliveryFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryIssuesPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryPreviewPanel.tsx
  - osdu/gui/src/features/delivery/PartitionNotice.tsx
  - osdu/gui/src/features/delivery/useInterfaceChoice.ts
  - osdu/gui/src/features/delivery/DeliveryOpenUndos.tsx
  - osdu/gui/src/features/delivery/RemovalDialog.tsx
  - osdu/gui/src/features/delivery/DeleteLedgerDialog.tsx
  - osdu/gui/src/features/delivery/RedeliverDialog.tsx
  - osdu/gui/src/features/delivery/ReverseDialog.tsx
  - osdu/gui/src/features/delivery/DeliveryRunHeader.tsx
  - osdu/gui/src/features/delivery/DeliveryTriggerFields.tsx
  - osdu/gui/src/features/delivery/RetrievalFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryCacheFlowVersions.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionBoard.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionBoardGrid.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionTestSheet.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionReportPage.tsx
  - osdu/gui/src/features/delivery/inventories/InventoryReport.tsx
  - osdu/gui/src/features/delivery/removals/DeliveryRemovalsPage.tsx
  - osdu/gui/src/features/delivery/removals/RemovedKeysGrid.tsx
  - osdu/gui/src/features/delivery/removals/DeliveryDeletionsPanel.tsx
  - osdu/gui/src/features/delivery/dimensions/DeliveryDimensionsPage.tsx
  - osdu/gui/src/features/delivery/dimensions/DimensionWorkspace.tsx
  - osdu/gui/src/features/delivery/DeliveryDocumentsPage.tsx
  - osdu/gui/src/features/delivery/TemplateVariableExplorer.tsx
  - osdu/gui/src/features/delivery/MappingBuilderPage.tsx
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
  - osdu/gui/src/features/delivery/DeliveryCachePage.tsx
  - osdu/gui/src/features/delivery/DeliveryCacheApprovals.tsx
  - osdu/gui/src/features/delivery/DeliveryPartitionsPage.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - sqlflow/gui/src/auth/AuthContext.tsx
  - osdu/gui/src/features/delivery/order/OrderPage.tsx
---

# The OSDU pages of the workbench

OSDU Delivery's GUI is SQLFlow's workbench with the OSDU Delivery module loaded. The workbench itself (Pipelines, runs,
schedules, lineage, the catalog, the trigger dialog, the assistant) is SQLFlow's and is documented there: the
[control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md) behind it, [sign-in and
scopes](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md), and the
[assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md). This page covers what the module adds:

- the product name, "OSDU Delivery, powered by SQLFlow", on the login page and the title bar;
- an **OSDU** menu group of its own, straight after Workspace, with the pages below;
- the side bar closed the first time the workbench opens, so it opens on the page with only the activity bar beside
  it; a click on any activity bar icon opens the side bar, and from then on it starts as the person last left it in
  their browser;
- the side bar's menu folded the first time it opens, every group and section showing only its name; an activity bar
  icon unfolds the group it names, and from then on each group and section starts as the person last left it in their
  browser;
- a **Partition** picker in the title bar, which every OSDU page reads in;
- tabs and header actions on the pipeline and run pages of the seven kinds it adds (delivery, retrieval, cache, assertion,
  dimension, inventory, removal), and their fields in the trigger dialog;
- a **records** category in the workbench's search.

No flow is configured in the OSDU group. Flows of every kind, these included, are defined, synced and scheduled in
Workspace, and a flow's own page is reached through **Pipelines** like any other. On these kinds' pipeline pages,
SQLFlow's Transforms, Files and Definition tabs are hidden, and on their run pages its Files, Statements, Surrogate keys,
Assertions and Health metrics tabs.

## Who may use what

The control plane puts every OSDU endpoint in one of SQLFlow's authorization groups:

| Group | What it covers |
| --- | --- |
| read | every page's reads: the ledger, records, submissions, the audit trail, mappings, templates, caches, partitions, search terms, boards and reports |
| operate | anything that runs or reaches OSDU: the trigger dialog, release, redeliver, verify, sync, probe, read back, render, preview, value checks, removals, Delete ledger, reversals, cache approvals, inventory removals, every explorer read, syncing the data definitions |
| author | saving and deleting templates, refining, deleting and restoring search terms, proposing a mapping from the builder (SQLFlow's proposal endpoint) |
| admin | describing, making default and removing the partitions the control plane's configuration does not name (registering is the configuration's, or the API's and `sqlflow partition add`'s, never a page's); setting and removing central configuration; removing a dimension its flow no longer declares; pruning the ledger |

SQLFlow resolves `read`, `operate` and `author` to "signed in", and only `admin` consults the token's scopes, in the
control plane and in the GUI alike. So **any signed-in user can use every page and action here except the admin ones**,
which the GUI shows only to an admin (see [authentication and identity](authentication-and-identity.md)). Every
intervention is recorded in the ledger under the name of who asked.

## The partition picker

The title bar's **Partition** chip names the OSDU partition every OSDU page is read in, with a count beside it when
cache changes wait for a decision there. Its menu lists each partition: **default**, **not registered** (a flow
hard-codes it, or something is kept under it from before it was removed), the cache changes waiting, its cache's
current version, and how many delivery flows deliver there; **Partitions** opens the registry. Every OSDU request
carries the choice (as the `X-Osdu-Partition` header), so a flow that names its partitions is read in the picked one,
and a link that names a partition (`?partition=`) makes it the title bar's. See [partitions](partitions.md).

## The home page

The workbench opens on **Home**: SQLFlow's home page, with what this product adds to it. At the top, on the brand's
navy, a greeting, the product's line and **Ask the assistant**: a question typed there, or one of the three offered,
opens the **Assistant** asking it in a fresh conversation
([the chat assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md#asking-from-the-home-page)). The questions
offered are about deliveries, and the assistant's delivery tools answer them: which delivery flows have held or failed
records and why, what changed in the partition's cache since yesterday, and which mappings deliver well logs and what
they read from the source. When the assistant is not configured, the box says so.

Under SQLFlow's headline counts, **Delivery** says what the delivery flows hold in the title bar's partition: how much of
it is delivered, a bar of the records by where they stand, and their counts (delivered, pending, held, failed, drifted,
delivered in the last 24 hours), added up exactly as the [Delivery](#delivery) page adds them. **Find a record** looks a
record up by any of its keys, and **Open Delivery** opens the page. The newest runs and the runs by state follow.

## The OSDU menu

| Section | Entry | Page |
| --- | --- | --- |
| Ledger | Delivery | `/delivery` |
| | Records | `/delivery/records` |
| | Removals | `/delivery/removals` |
| | Audit trail | `/delivery/activity` |
| | Orphaned ledgers | `/delivery/ledgers/orphaned` |
| In OSDU | Explorer | `/delivery/explorer` |
| | Assertions | `/delivery/assertions` |
| | Inventories | `/delivery/inventories` |
| | Dimensions | `/delivery/dimensions` |
| Build | Mappings | `/delivery/documents` (and the Mapping builder, `/delivery/mappings/build`) |
| | Templates | `/delivery/templates` |
| | Cache | `/delivery/cache` |
| | Order | `/delivery/order` |
| Setup | Partitions | `/delivery/partitions` |
| | Search terms | `/delivery/search-terms` |

**Ledger** is what the delivery system sent and keeps; **In OSDU** is what OSDU holds, read live by the explorer or by
the assertion, inventory and dimension flows; **Build** is what a delivered document is built from; **Setup** is what is
kept in the GUI rather than in a flow. The side bar's menu is a tree, like the catalog's: **OSDU** is a branch beside
SQLFlow's own groups, each section a branch under it and each entry a leaf under its section, every level indented
under the one above with a guide line down its side. A click on a section's name folds its entries away or brings them
back, as a click on **OSDU** does for the whole group, and the browser keeps each section as it was left. The menu is one
stop for Tab: it lands on the page shown, the arrow keys move through it (left and right fold and unfold a branch), and
Enter opens an entry's page. The command palette lists the same entries, headed `OSDU · Ledger`, `OSDU · In OSDU` and so
on, whatever is folded.

## Ledger

### Delivery

Every delivery flow as a card, counted in the title bar's partition: a delivered bar, **Records**, **Delivered**,
**Pending**, **Held**, **Failed** and **Last 24h**, with the last delivery, the last verify and the last submission. A
flow that delivers only to other partitions says where, without counts. The header line totals what the flows hold
(delivered of total, pending, held, failed, drifted). **Find a record** with **Look up** opens Records looked up for
what is typed. A card opens the flow's Delivery tab. A card shows what a flow's run did within seconds of the run
ending: the page checks each flow's newest run every five seconds (every three while one is queued or running), and reads
a flow's counts again when its newest run starts, moves on or ends, and at every check while it runs. The counts are also
read every 15 seconds, for what no run does (a release).

### Records

Where an operator starts from what they hold rather than from a flow. With nothing typed, the page lists the latest
records the delivery system took in or sent, newest first, refreshing every ten seconds. Typed, it lists the records any
of whose values start with the term, across every flow: an identity the mapping declares (`dataset.identity`), the
source key or one of its key columns, a word of the label, the OSDU id, the delivery key (which lands on one record),
or the ingestion file. Each hit says which value matched. A status picker (`pending`, `waiting`, `delivering`,
`delivered`, `held`, `failed`, `deleted`, `reverted`) and a flow picker narrow both. A row shows the status, the record
(label, source key, ingestion file and row), the flow, when it last changed and was last delivered, and its OSDU type,
a link to the record's OSDU tab; a row opens the record's page. The term (`q`), status (`status`) and flow (`flow`, the
ledger's flow id) are in the address, so a lookup is a link; a link that names a partition (`partition`) makes it the
title bar's, so the lookup is read in the partition it is about. **Export**, in the header, downloads every record the
lookup reaches (at most 1,000) as an Excel workbook, each with its flow, interface and partition and the values the term
matched; when more match, its About sheet says so, and a flow's Records tab exports up to 25,000 records of the flow
([records export](../cli/records.md#records-export)).

### Removals

Every removal of the partition, from every removal flow: its delivery flow, that ledger's deletions by state (pending,
waiting for approval, removed) and its last run, the ones waiting for approval marked. One opens under a compact heading
(**Removals** › its name, a switcher to the others, **Run pipeline**): what it reads and compares under, its guard, its
last run, and, when its guard held what it found, one line saying how many wait, why on hover, **Review** and **Approve**,
whose dialog says what the approval does in the ledger and in OSDU and takes the partition typed back. Under it, a grid
that fills the window and reads 200 deletions at a time as it is scrolled: one line of state tabs with their counts, each
deletion's key (opening its record), why it counts as removed and as of when (the whole of it on hover, to copy), what
taking it out came to, when it was found and done. Picked deletions are delivered again through **Deliver again**. **Runs**
lists the removal's runs with what each read, found, took out and left. See [removal flows](../flow/removal.md).

### Audit trail

Every run and intervention across flows, newest first: when, **Took** (how long it ran, counting up while it runs), flow,
partition, **Action**, **By** (a schedule, a person, the command line), **Result** and **Target** (the record, else the
submission, else the run). It narrows by actor, flow,
action (`deliver`, `intake`, `drain`, `verify`, `probe`, `sync`, `release`, `adopt`, `redeliver`, `rerender`, `delete`,
`restore-previous`, `purge`, `reverse`, `undo`, `delete-ledger`, `remove-dimension`, `inventory-remove`, `inventory-repair`,
`reassign-id`, `source-remove`, `lift-removal`, `ledger-move`) and outcome. Runs
that changed nothing are left out until **Show idle runs**. An entry opens on its ids, **Parameters**, its captured
**Log** (downloadable), and for a `deliver`, `replan`, `drain` or `intake` run that has ended, **Reverse this run**; a
`reverse` entry shows its **Reversal**. The flow is in the address (`flow`, the ledger's flow id), so one flow's trail is
a link; a link that names a partition (`partition`) makes it the title bar's, so the trail is read where that ledger is
kept.

### Orphaned ledgers

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

### A record's page

`/delivery/records/{flowId}/{key}`, opened from any records list.

**The header** names the record (its label, status and a blocked mark), its **osdu** id, **flow**, **partition** and
**submission** as chips, and for a record linked to another through a linked template
([mapping $parent](../flow/mapping-parent.md)) its **parent** (the record of the canonical kind with the same minted id,
opening on that record's page, or in the explorer when no flow of the partition delivers it) or the records it is
**read by**, each named by its type; each try on the timeline gives the **Parent version** its document was rendered
from. Then the one situation its state calls for (held or failed with the error and next try, waiting
for another record, under a lease, blocked, released and waiting for its run, held with nothing to move it, removed,
records waiting on this one). Its buttons:

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

**The tabs:**

- **Timeline**: what happened to the record, newest first, in chapters (each started by a row arriving, changing or
  going, or by someone's request), on three lanes (**Source**, **Ledger**, **OSDU**); every try with what OSDU answered,
  and every request with who made it. It narrows to one lane or to the requests people made.
- **Source**: where the row came from (ingestion file and row, when received, source key, delivery key, mapping, key
  columns with a copy of the key as the ledger holds it), a newer row a waiting document is built from, and a read of the
  rows as the ingestion tables hold them now, with the flow's own connection.
- **Render**: **Render** builds the record from its current row with the mapping and the cache as they are now, and reads
  what OSDU holds, at once; nothing is sent. It shows the mapping and cache versions used beside those that rendered what
  OSDU holds, the route, what the next run would do, why a delivery would hold the record, the preflight's warnings, and
  the document **Beside OSDU** (the differences) or **Whole document**.
- **OSDU**: **Read** reads the record as OSDU holds it through the flow's route, in the record inspector the explorer uses
  (outline, fields or JSON, versions and **Compare**, linked records, Validation). Icons open it in the explorer, or in a
  window of its own (`/delivery/records/{flowId}/{key}/osdu`).
- **Artifacts**: what deliveries of the record created in OSDU, with each one's state, and the ones an undo still has to
  take; the header says so while an unfinished delivery left something. Once a delivery completes, only the datasets,
  content, outputs and dataspaces it made stay listed; the record's own write, a version or a session leaves the list, so
  a record delivered through a DDMS session, or in one call, shows none.

### A submission's page

`/delivery/submissions/{id}`: what the submission read (the ingestion table, connection, change window or the whole
scope), its counts, when it was received, started and completed and its **Duration** (from its start to its completion,
counting up while it is planned or running, with the wait after it was received in the hover), the runs that carried
it, and tabs **Attempts**, **Batches** (each work batch with its own duration), **Parameters** and **Render context**.
**Records it delivered** and **Records it last planned** open the flow's Records tab narrowed to each set.
**Reverse this submission** opens the reverse dialog while the submission has anything to reverse; once asked, the
reversal's card shows its state, its records by outcome and its runs.

### The dialogs

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

## A delivery flow's page

A delivery flow's pipeline page (Pipelines) gains five tabs, Delivery first. A source that delivers several interfaces
is read one interface at a time, through an interface picker whose choice is in the address; a flow that names its
partitions is read in the title bar's partition.

- **Delivery**: the source's counts and status bar, with **Probe target**, **Release blocked (N)** (N counts the blocked
  records and the held ones nothing moves, what the release reaches), **Adopt (N)** while records are held because OSDU
  already holds a record at the ids their keys make (N counts them over every interface; it asks them all to adopt those
  records once the partition is typed back, and can queue a deliver run of the source straight after,
  [Adopting a record OSDU already holds](record-lifecycle.md#adopting-a-record-osdu-already-holds)), **Redeliver**,
  **Sync timelines**, **Undo unfinished** (runs the `undo` operation now rather than at the end of the next deliver run)
  and **Delete ledger**; the last submission; for a source, the **Interfaces** in the order a run takes them (each with
  its mapping, wave, route, kind, what it waits for and its counts, in a table that fits its panel rather than scrolling
  sideways: why an interface goes by its route is on the route's hover, the kind clips from the left and keeps its
  entity and version in view, a long mapping name or list of interfaces waited for clips with the whole on hover, and
  the kind and the route's reason each have a copy beside them); the target probe's answer; and unfinished deliveries
  left to undo. The tab follows the
  flow's newest run as the Delivery page does: its counts and submissions move with a run, and every view of the flow is
  read again within seconds of a run ending.
- **Records**: the ledger's records, searched by delivery key, label, source key or OSDU id, with a status picker,
  **Drifted only** and **Match anywhere**; links from elsewhere narrow it to one run (`?run=`), what a submission last
  planned (`?submission=`) or delivered (`?delivered=`), or one issue (`?issue=`). **Export** downloads every record the
  filter matches, not the page in view, as an Excel workbook to work through where the records come from
  ([records export](../cli/records.md#records-export)); it says on hover why it cannot when nothing matches or more than
  25,000 records do. Rows tick; the selection bar offers
  **Select all N matching**, **Redeliver**, **Adopt** (when the selection holds a record held for an id OSDU holds; the
  others selected are left), **Sync timelines** and **Remove**.
- **Issues**: the blocked records grouped by the issue that keeps them blocked, each a **Set error** or **Row errors**,
  with its records and when it last changed. The issue picked shows samples, each with **Check** (opens it on its Render
  tab) and **Try** (releases it alone and queues a deliver run), the files its records came from, **Records** (the
  Records tab narrowed to it) and **Release N**, which can queue a deliver run straight after. An issue of records held
  because OSDU already holds a record at the ids their keys make also offers **Adopt N**, which asks every record of it
  to adopt the record OSDU holds once the partition is typed back, and can queue a deliver run straight after.
- **Submissions**: every submission with what it read and its counts; a row opens its page.
- **Preview**: **Preview a record** renders one record exactly as a delivery would and sends nothing: the first record of
  the scope, or the one a key names, with a field for each parameter the flow declares. The answer shows what the next run
  would do, the document, the route's requests, the payload files, the records it refers to and its source rows, and
  **Download JSON**.

A parameter the flow's `source.record.scope` binds to a column lists the values that column holds, with their row counts,
wherever a scope is given values (the Preview tab, the value check, the trigger dialog), and takes several: a value picked
from the list, or typed and added with Enter, joins the field, picking it again drops it, and Backspace in an empty field
drops the last. The value being typed counts as one of them as it stands. A form shows every value picked; the value
check's line shows the first and how many more, each named on hover. A value the column holds no row of is marked once
the list closes. The run reads the rows holding any of the values
([several values of a scope parameter](../flow/delivery.md#several-values-of-a-scope-parameter)).

### When the title bar's partition is not the flow's

The title bar decides which partition every page is about, so a flow's own tabs never show another partition's ledger
under the picked one's name. When the flow does not deliver to the partition picked, the **Delivery**, **Records**,
**Issues**, **Submissions** and **Preview** tabs read nothing (no counts, records, issues, submissions or preview) and
show a notice in their place:

| The flow | The title bar | The notice |
| --- | --- | --- |
| names its partitions, or follows the registry | a partition the flow does not serve | `<flow> does not deliver to <partition>, the partition picked in the title bar.` and `It delivers to <its partitions>. Pick one of them in the title bar to see this flow there and act on it.` (`Pick it` when it serves one) |
| names its partitions, or follows the registry | no partition picked | `No partition is picked in the title bar.`, then where it delivers, as above |
| delivers through its `data-partition-id` header, and has run | a partition other than the one its ledger is kept under | `<flow> does not deliver to <partition>, ...` and `It delivers to <partition>, the partition its data-partition-id header names. Pick it in the title bar ...` |

The partitions a flow serves are the ones its interfaces are listed in: those it names, in its order, or every registered
partition for a flow that follows the registry ([three ways a flow names its partitions](partitions.md#three-ways-a-flow-names-its-partitions)).
A header flow's partition is known from its ledger once it has run; until then, and while no partition is picked, its
tabs show its ledger as they always do. The notice changes nothing: the switch is the title bar's. A link that names a
partition the flow serves (`?partition=`) makes it the title bar's, so a link lands on the ledger it names.

## Runs and the trigger dialog

The trigger dialog of these kinds shows the flow's **Flow parameters** and **Run settings** as its document declares
them ([parameters](../flow/parameters.md#in-the-gui-and-on-a-schedule)): each parameter with its description and
default, a list to pick from for its choices or the values its column holds, and for a deliver, plan, intake or replan
run **Records at most** (the first 1 to 1,000 records of the scope, or 0 for the whole, `maxRecords`: a smoke test before
the whole scope goes; the document's `source.record.maxRecords` is its default, and the run's page shows it as a chip).
The control plane checks the run as the fields are filled in. The kind adds the **Partition** a run writes to (the title
bar's), and per operation: **Force** and **Send again** (Nothing, What renders differently, or a part) for a deliver run,
**Redeliver these records** (delivery keys), a submission to work on or drain, and the **Interfaces** of a source a run
takes. See [running an OSDU flow](../cli/run.md) for the
operations.

A run page gains, by kind:

| Kind | Header actions | Counts |
| --- | --- | --- |
| delivery | **Records of this run**; **Reverse this run** once a `deliver`, `replan`, `drain` or `intake` run has ended | Planned, Delivered, Held, Failed, Unchanged, Waiting; the submission as a chip. A `remove` run: Selected, Removed (Restored for a step back), Already gone, Skipped, Failed, and Deleted from the ledger when it was asked to, with what it was asked to take and its summary line |
| cache | **Open the cache** | |
| assertion | **Open the report** | Tests, Passed, Failed, Warned, Errored, Skipped |
| dimension | **Open dimensions** | Built, Failed, Skipped, Values, Keys |
| inventory | **Open inventories** | Inventories, Would read, Completed, Failed, Listed, Raised |
| removal | **Open removals** | Found removed, Removed, Held, Left (a plan: Would find, Would hold) |

The other kinds' pipeline pages gain one tab each, which they open on: a retrieval flow **Retrievals** (every run with
how long it took, its window, location, counts and outcome); a cache flow **Cache versions** (the partition it fills and every version of
its cache, with a **Cache page** link); an assertion flow **Assertions**, **History** (its assertions against recent
runs, each with its pass rate and how often it flipped) and **Reports**; a dimension flow **Dimensions**; an inventory flow
**Inventories**; a removal flow **Removals** (each removal with its delivery flow, that ledger's deletions by state and its
last run, **Approve** for what a guard held, and **Open** to the Removals page). A delivery flow also gains **Deletions**:
the keys removal flows found its source no longer holds, as the Removals page's grid shows them, with **Deliver again** for
picked records, and the removal runs that wrote to its ledger.

## In OSDU

- **Explorer**: what the partition holds, read live from OSDU. See [the explorer](explorer.md).
- **Assertions**: every assertion flow's assertions in the partition, one line per flow, what needs a look first. The
  GUI calls each entry of a flow's `tests:` an assertion, and each item of its `assert:` a check. A strip counts
  **Failing**, **Errored**, **Warned**, **Not run**, **Passing**, **Unfit** and **Changed**, each a filter; a search and
  **Types** and **Tags** pickers narrow further. A flow runs every assertion, or **Run N** picked ones, through the
  trigger dialog, whose operation **Check** runs them. An assertion says on its line what its document says now: a
  disabled one shows `disabled:` and its reason, and one whose proof values are past their day shows
  `needs new proof values` and that day. It opens in a sheet with **Latest report** and **Run**, and tabs **Checks**
  and **Definition** (the schema it is checked against and where that comes from: the newest captured from OSDU, the
  version it names, or the version a mapping pins; and when its proof values were stated and how long they hold). A
  check of known values lists its rows that do not hold: the values a row is located by, the record it found (none
  when it found none), and what it came to. A report (`/delivery/assertions/runs/{n}`) shows one run's assertions by
  outcome, with **Run the N assertions not passing again**, **Run again**, and the report as HTML, Markdown, JSON or
  JUnit XML. See [assertion flows](../flow/assertion.md).
- **Inventories**: every inventory flow's inventories in the partition (kind, findings raised, when reconciled). One
  opens its report: the findings (Orphan, Foreign, Stale, Forgotten and the rest), tabs **Ids**, **Runs** and
  **Removals**, **Run pipeline**, **Export the ids as CSV**, and **Remove** for picked ids where the flow allows it,
  through the removal dialog. An id opens as OSDU holds it in the workbench's bottom panel. **Filter** selects the ids
  by what OSDU holds, as the explorer's conditions: the tabs then count the matching ids of each finding, the grid lists
  them with the values the conditions ask, and every matching id of a removable finding can be removed at once. The
  **Duplicates** tab finds the records OSDU holds more than once: a key of the properties their copies hold equal, picked
  by clicking the values of one of its records shown as JSON; **Find duplicates**, which downloads them for every id and
  groups them; a chip for each property downloaded, which groups again at once by a click; the groups by size and outcome;
  **Pick the copies to keep**, which reads each group's copies; **Keep** and **Compare** on them; and **Select the copies to
  remove**, which opens them on the Ids tab for a removal. See [inventory flows](../flow/inventory.md),
  [selecting ids by what OSDU holds](../flow/inventory-selections.md) and
  [finding duplicate records](../flow/inventory-duplicates.md).
- **Dimensions**: a strip of facts (dimensions, values, what needs a look, when last built), then each dimension flow's
  dimensions as cards, with **Run pipeline** per flow and **Build a search**. One dimension opens on the page's width
  with **Export** (the table, values or keys as CSV or JSON Lines), **Run pipeline**, and tabs **Table**, **Values**,
  **Keys**, **Changes**, **Builds** and **Definition**. A dimension its flow no longer declares can be removed for good
  by an admin with **Remove**. See [dimension flows](../flow/dimension.md).

## Build

### Mappings

The mapping documents the repositories hold, as the last sync found them, filtered by repository, with **New mapping**
(opens the builder). A mapping opens in a sheet with the template version it fills (a link to Templates, its tooltip
naming the template folder file the version was read from, or saying no folder on the mapping's path holds it), **Open in
builder**, and three tabs:

- **Properties**: the template the mapping fills as the record's tree, beside the selected attribute. The views
  **Filled**, **Missing**, **Unfilled**, **Failing** (after a data check) and **All** each carry a count; **View** adds
  **Required**, **Minted** and **Nested**. The selected attribute reads in **Filled by**, **Data** and **Schema**. The
  data check line picks a flow that renders with the mapping, its scope's values and how many rows, and **Check all
  attributes**; an attribute's **Check values** checks it alone. See [writing a mapping](../guides/writing-a-mapping.md).
- **YAML**: the document as written.
- **Record shape**: the record the mapping renders, placeholders in angle brackets.

### Mapping builder

Opened from Mappings (`/delivery/mappings/build`, or `?mappingId=` for a synced mapping). **Setup** picks the
**Repository**, the **Partition cache** the mapping is checked against, the **Template** (a file of the repository's
template folders, by its kind and path), and the mapping's
**Name**, **Mapping version**, **Source system** and **Description**, then **Start mapping**. **Dataset** sets the **Key
columns**, how the **OSDU id** is made (from the delivery key, or the key's values) and the **Label**. Every template
variable is listed, each filled as a **Dataset column**, **Repeat child rows**, **Cache**, **Lookup**, **Platform
search**, **Expression**, **Static value**, **First value of**, **List of values** or **Object**, or left **Not
filled**; for a template linked to a parent, also as **Parent record**, a section of the record's parent by its path,
offered at the variable's own path ([mapping $parent](../flow/mapping-parent.md)); for a template other templates of the
repository are linked to, also as **Child record**, the link to the record's child of one of those kinds, never required
and never an alternative of **First value of** ([mapping $child](../flow/mapping-child.md)); the YAML and its check against the template and the cache's current version follow every edit. **Copy YAML**
takes it; **Propose to repository** opens a pull request through SQLFlow's proposal endpoint (**Open pull request**)
that writes the mapping into the `mappings` folder beside the template folder holding its template, so that folder is
on its path ([template folders](templates.md#template-folders)). A repository that holds no template yet says how to
get one. **Start over** discards the draft. A mapping that cannot be opened says why.

### Templates

The OSDU record schemas the synced repositories hold in their template folders, each read from its file; the database
keeps none of them. A repository adds and removes its templates by committing and deleting their files, and its sync
records them. Two tabs:

- **Repositories**: every file of every synced repository's template folders, filtered by kind, version, repository or
  file, each with the kind and version it holds (or why it holds none) and when its repository's sync last saw it; a
  template linked to a canonical kind shows a link beside its kind, naming the parent on hover
  ([linked templates](templates.md#linked-templates)). A file opens laid out as a template, with its repository and
  path, the kind it is linked to, and each kind of the repository linked to it, as a **Child**; **Download** saves it as a template file under its
  own name, to keep in another repository's template folder. A link naming a kind and version opens the file holding
  it, and says so when no repository holds it.
- **Browse OSDU**: the OSDU data definitions, the Open Group's public repository of OSDU schemas, by **Release**,
  searched by kind, with **Every version**, **View**, **Compare versions**, **Download** (on a kind's row and in its
  sheet: the kind's schema as the file a template folder keeps), and **Sync with the repository**, which reads the
  release list again and downloads the release in view.

See [templates](templates.md).

### Cache

The cache of the title bar's partition. The header names the cache flow files that fill it, with **Cache files** (opens
Pipelines filtered to them) and **Refresh now** (the trigger dialog on a cache flow's refresh; a menu when several flows
fill the partition). A summary row gives the **Current version** with the flow that wrote it, its records and types, and
whether changes wait for approval; a banner with **Review changes** shows while any do. Four tabs:

- **Records**: the cached records at the version being read, by type, searched over every value, with a version picker.
- **History**: every version and what it changed; a version raises its changes in the bottom panel.
- **Deliveries**: **Updated by the cache**, by state (**Missing from cache**, **Waiting for approval**, **Approved**,
  **Rolling out**, **Rolled out**, **Rejected**, **All changes**); a waiting change has **Approve** and **Reject**, alone
  or for the selection.
- **Setup**: how the cache is filled, as a tree of the partition, its cache flows and their types, the partition's OSDU
  feature flags, and how a mapping reads the cache. Read-only: the setup lives in the cache flow files.

See [the partition cache](partition-cache.md).

### Order

The order records reach OSDU in, from what each kind refers to. Three tabs:

- **Schedules**: every schedule running a delivery, drawn left to right with an arrow to the schedules whose flows refer
  to what it delivers; an arrow is dashed where the timing does not keep the order, and a schedule out of order or in a
  circle carries a warning mark. A click slides the schedule in from the left, over part of the graph: when it fires,
  each schedule it runs best after with the timing and what to do about it, the flows, types and partitions behind it,
  and the schedules that run best after it. A schedule named there selects it in the graph; the close button, Escape or
  a click on the empty graph closes it.
- **Partition**: a partition's entity types level by level, each with the deliveries writing it and the types it waits
  for, and the references the order cut and its warnings behind marks on the facts line.
- **Releases**: OSDU's lineage of a built release drawn around an entity type, by **Depth** and **Direction**; a click
  slides a type's kinds and references in from the left the same way, a double click or **Draw around it** draws around
  it, and **Build** builds a release.

See [the OSDU order](osdu-order.md).

## Setup

- **Partitions**: the registry, as the control plane's configuration names it. Each partition with its description,
  whether it is the default, its cache, the flows that serve it, and when and by whom it was registered. The page
  registers nothing: the configuration names the partitions, and the control plane registers them when it starts. A
  configured partition carries a **configured** badge, whose tooltip names its setting (`Osdu:Partitions:<name>`), and has
  no actions. A registered partition the configuration does not name keeps, for an admin, **Make default** (hidden while
  the configuration names the default), **Describe** and **Remove**; removing deletes nothing kept under the partition
  (caches, ledgers and runs stay). The **Actions** column shows only when some row has one. With no partition at all
  the page says **No partition to work against** and how to name one in the configuration. See
  [configured partitions](partitions.md#configured-partitions).
- **Search terms**: the source columns the explorer searches by. See [search terms](search-terms.md).

## Records in the workbench's search

The module adds a **records** category to the workbench's search: a delivery key lands on its record, and an OSDU id, a
source key, a label or an ingestion file name lists the records that start with it, across every flow of the title bar's
partition, from the ledger's indexed lookup, the same one the Records page reads.
