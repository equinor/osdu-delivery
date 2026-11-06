---
id: delivery-concept-gui
title: "The OSDU pages of the workbench: what each page and tab shows, and its actions"
type: concept
summary: "The pages OSDU Delivery adds to the workbench (Ledger, OSDU, Build), their tabs and buttons, the flow and run page additions, and who may use them."
keywords:
  - gui
  - workbench
  - osdu pages
  - flow page tabs
  - partition picker
  - flow does not deliver to this partition
  - tests board
  - partitions page
related:
  - delivery-concept-gui-ledger
  - delivery-concept-gui-build
  - concept-control-plane
  - concept-authentication-and-identity
  - delivery-concept-explorer
  - delivery-concept-search-terms
  - delivery-concept-ledger
  - delivery-concept-removal-and-reversal
  - delivery-concept-partition-cache
  - delivery-concept-menu
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/src/features/delivery/PartitionSwitcher.tsx
  - osdu/gui/src/features/delivery/useLinkedPartition.ts
  - osdu/gui/src/features/delivery/DeliveryFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryIssuesPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryIssuePanel.tsx
  - osdu/gui/src/features/delivery/DeliveryPreviewPanel.tsx
  - osdu/gui/src/features/delivery/RecordPreviewView.tsx
  - osdu/gui/src/features/delivery/PartitionNotice.tsx
  - osdu/gui/src/features/delivery/useInterfaceChoice.ts
  - osdu/gui/src/features/delivery/DeliveryOpenUndos.tsx
  - osdu/gui/src/features/delivery/DeliveryRunHeader.tsx
  - osdu/gui/src/features/delivery/DeliveryTriggerFields.tsx
  - osdu/gui/src/features/delivery/RetrievalFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryCacheFlowVersions.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionBoard.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionBoardGrid.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionTestSheet.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionReportPage.tsx
  - osdu/gui/src/features/delivery/inventories/InventoryReport.tsx
  - osdu/gui/src/features/delivery/removals/DeliveryDeletionsPanel.tsx
  - osdu/gui/src/features/delivery/dimensions/DeliveryDimensionsPage.tsx
  - osdu/gui/src/features/delivery/dimensions/DimensionWorkspace.tsx
  - osdu/gui/src/features/delivery/DeliveryPartitionsPage.tsx
  - osdu/gui/src/features/delivery/AccessRestrictionsPage.tsx
  - osdu/gui/src/features/delivery/DeliveryHomeSection.tsx
  - osdu/gui/src/features/delivery/dimensions/DimensionViewWorkspace.tsx
  - osdu/gui/src/features/delivery/inventories/InventoryRecordsGrid.tsx
  - osdu/gui/src/features/delivery/inventories/duplicates/InventoryDuplicates.tsx
  - osdu/gui/src/features/delivery/downloads/DownloadFlowPanels.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RunSearchContributor.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - sqlflow/gui/src/auth/AuthContext.tsx
---

# The OSDU pages of the workbench

OSDU Delivery's GUI is SQLFlow's workbench with the OSDU Delivery module loaded. The workbench itself (Pipelines, runs,
schedules, lineage, the catalog, the trigger dialog, the assistant) is SQLFlow's and is documented there: the
[control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md) behind it, [sign-in and
scopes](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md), and the
[assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md). This page covers what the module adds:

- the product name, "OSDU Delivery, powered by SQLFlow", on the login page and the title bar;
- the whole menu, laid out by what a person does there, SQLFlow's pages and the ones below side by side, with the side
  bar closed and the menu folded the first time the workbench opens ([the menu](menu.md)), each entry of its own pages
  saying what the page is for, which a page's
  [Explain](../../../../sqlflow/docs/reference/guides/chat-assistant.md#explain) gives the assistant;
- a **Partition** picker in the title bar, which every OSDU page reads in;
- tabs and header actions on the pipeline and run pages of the eight kinds it adds (delivery, retrieval, cache, assertion,
  dimension, inventory, removal, download), and their fields in the trigger dialog;
- **records** and **runs** categories in the workbench's search: a run id, or a correlation id copied from a platform's log,
  lists the run with every run of its execution ([from a log line to the run](request-correlation.md#from-a-log-line-to-the-run)).

The pages it adds sit in three of the menu's groups, and the sections below follow them: **Ledger**, **OSDU**, and the
**Delivery** section of **Build**. No flow is configured on them. Flows of every kind, these included, are written in
their repository, synced and scheduled under **Build**, **Flows**, and a flow's own page is reached through
**Pipelines** like any other. On these kinds' pipeline pages,
SQLFlow's Transforms, Files and Definition tabs are hidden, and on their run pages its Files, Statements, Surrogate keys,
Assertions and Health metrics tabs.

## Who may use what

The control plane puts every OSDU endpoint in one of SQLFlow's authorization groups:

| Group | What it covers |
| --- | --- |
| read | every page's reads: the ledger, records, submissions, the audit trail, mappings, templates, caches, partitions, search terms, boards and reports |
| operate | anything that runs or reaches OSDU: the trigger dialog, release, redeliver, verify, sync, probe, read back, render, preview, value checks, removals, Delete ledger, reversals, cache approvals, inventory removals and restores, every explorer read, syncing the data definitions |
| author | refining, deleting and restoring search terms, proposing a mapping from the builder (SQLFlow's proposal endpoint) |
| admin | describing, making default and removing the partitions the control plane's configuration does not name (registering is the configuration's, or the API's and `sqlflow partition add`'s, never a page's); setting and removing central configuration; removing a dimension or a view its flow no longer declares; purging a partition cache's history |

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

The workbench opens on **Home**: SQLFlow's home page, with what this product adds to it. Under SQLFlow's headline
counts, on the brand's navy, a greeting, the product's line and **Ask the assistant**: a question typed there, or one of the three offered,
opens the **Assistant** asking it in a fresh conversation
([the chat assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md#asking-from-the-home-page)). The questions
offered are about deliveries, and the assistant's delivery tools answer them: which delivery flows have held or failed
records and why, what changed in the partition's cache since yesterday, and which mappings deliver well logs and what
they read from the source. When the assistant is not configured, the box says so.

Under the question box, **Delivery** says what the delivery flows hold in the title bar's partition: how much of
it is delivered, a bar of the records by where they stand, and their counts (delivered, pending, held, failed, drifted,
delivered in the last 24 hours), added up exactly as the [Delivery](gui-ledger.md#delivery) page adds them. **Find a record** looks a
record up by any of its keys, and **Open Delivery** opens the page. The newest runs and the runs by state follow.

## Ledger

**Delivery**, **Records**, **Removals**, **Audit trail** and **Orphaned ledgers**, with a record's page, a
submission's page and the dialogs they open, have [a page of their own](gui-ledger.md).

## A delivery flow's page

A delivery flow's pipeline page (Pipelines) gains six tabs, Delivery first, and sets them among the page's own by what
they are about: what the ledger holds (**Delivery**, **Records**, **Issues**, **Deletions**), then when and how the flow
ran (**Runs**, **Submissions**, **Schedules**; a run coordinates the submissions it plans), then what the flow is and
renders (**Preview**, **YAML**). A source that delivers several interfaces is read one interface at a time, through an
interface picker whose choice is in the address; a flow that names its partitions is read in the title bar's partition.

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
- **Records**: the ledger's records, searched by delivery key, or by the start of the label, source key, OSDU id or
  ingestion file, or of a word or key value a row shows inside them (`LOG-0002` finds `Wellbore B-1 / COMPOSITE / LOG-0002/1`),
  with a status picker,
  **Drifted only**, **Step held** (while an enrichment is set aside on any record,
  [an enrichment set aside on its hold](../flow/enrich-holds.md)), **Restricted** (while any record's access is restricted,
  [restricting access](access-restriction.md)) and **Match anywhere**; links from elsewhere narrow it to one run (`?run=`), what a submission last
  planned (`?submission=`) or delivered (`?delivered=`), or one issue (`?issue=`). **Export** downloads every record the
  filter matches, not the page in view, as an Excel workbook to work through where the records come from
  ([records export](../cli/records.md#records-export)); it says on hover why it cannot when nothing matches or more than
  25,000 records do. Rows tick; the selection bar offers
  **Select all N matching**, **Redeliver**, **Adopt** (when the selection holds a record held for an id OSDU holds; the
  others selected are left), **Sync timelines** and **Remove**.
- **Issues**: the blocked records grouped by the issue that keeps them blocked, the most records first, in a grid that
  fits the window. Each issue is headed by the property its error names (`data.Curves[].CurveUnit`) over what it says,
  with whether it is a **Set error** or **Row errors**, its records, held or failed, and when they last changed; the
  filter finds one by its words or id. The error reads as its records share it, with a chip (`n`, `value`) only where
  each says something of its own ([issues](record-lifecycle.md#issues)). A click on an issue, or Enter on it, opens it in
  the workbench's bottom panel over the grid, as a run's trace opens, and the address names it (`?issue=`), so a link
  opens it too; Up and Down step through the issues and Escape closes it. The panel shows the whole error, each reason it
  joins on its own and a `$coalesce`'s alternatives as a list; samples, each with **Check** (opens it on its Render tab)
  and **Try** (releases it alone and queues a deliver run); the files its records came from; **Records** (the Records tab
  narrowed to it) and **Release N**, which can queue a deliver run straight after. An issue of records held because OSDU
  already holds a record at the ids their keys make also offers **Adopt N**, which asks every record of it to adopt the
  record OSDU holds once the partition is typed back, and can queue a deliver run straight after.
- **Submissions**: every submission with what it read, when it was received and its counts, one line each, in a grid
  that fits the window; a row opens its page. The **Run** icon opens the run that coordinated it (its id on hover), and a
  submission that stopped wears a mark under **Error** whose card holds the whole reason with a copy. **Received** reads
  as the Records tab's times do: the clock alone today, the whole instant on hover.
- **Preview**: renders one record exactly as a delivery would and sends nothing. The bar is one line: the key (empty for
  the first record of the scope, or a source key, delivery key, OSDU id or the key's parts), a field for each parameter
  the flow declares, and **Preview**, which says on hover why it cannot run; what a preview does and what the key takes
  are on the bar's info mark, and a value the scope still needs is said on the line under it. The answer opens on its
  verdict: what the next run would do with the record and why, its OSDU id, how the ledger holds it (and whether that is
  the document rendered now), the file and row it came from and how it was picked, with **Open record** and
  **Download JSON**. Under it, a line for each thing to know before trusting it, only when there is one: what the
  partition's Schema service says of the document, the preflight's warnings, rows before it passed over, assertions that
  failed without holding it and references the cache does not hold, each in full on hover with a copy (the last two lead to
  the Render tab). When a delivery would hold the record, **Held for** says why, as a record's Render tab does. Five tabs
  follow, the first filling the window: **Document** (as the route sends it, or as the mapping renders it), **Schema
  service** (the document held to the schema the partition holds, [a dry run](dry-run.md#the-schema-service) says how, with
  how to meet each rule and where the template disagrees), **Render** (the mapping, kind, cache version and hash, what the
  mapping's assertions found, the platform searches, where each `$coalesce` took its value and what each enrichment
  added), **Delivery** (the route, its requests in order with each body on demand, the files it would upload and the
  records it refers to) and **Source** (its keys, where and how it was picked, the record row and its child rows).

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
**Issues**, **Deletions**, **Submissions** and **Preview** tabs read nothing (no counts, records, issues, deletions,
submissions or preview) and show a notice in their place:

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
| assertion | **Open the report** | Assertions, Passed, Failed, Warned, Errored, Skipped |
| dimension | **Open dimensions** | Built, Failed, Skipped, Values, Keys, and Views when the run reported any |
| inventory | **Open inventories** | Inventories, Would read, Completed, Failed, Listed, Raised |
| removal | **Open removals** | Found removed, Removed, Held, Left (a plan: Would find, Would hold) |

The other kinds' pipeline pages gain one tab each, which they open on: a retrieval flow **Retrievals** (every run with
how long it took, its window, location, counts and outcome); a cache flow **Cache versions** (the partition it fills and every version of
its cache, with a **Cache page** link); an assertion flow **Assertions**, **History** (its assertions against recent
runs, each with its pass rate and how often it flipped) and **Reports**; a dimension flow **Dimensions**; an inventory flow
**Inventories**; a removal flow **Removals** (each removal with its delivery flow, that ledger's deletions by state and its
last run, **Approve** for what a guard held, and **Open** to the Removals page); a download flow **Downloads** (each download
with its kind, its records by state, the bytes its files hold and its last run, its folder on hover of its name; a row opens
the download in place, on its records in a grid that fills the window, one tab per state with its count, found by any part
of an id or file name, and on its runs, each with what its listing changed, the files it wrote and why it wrote every record
again; the download open, its tab and its state live in the address). A download flow's trigger fields pick the downloads a
run takes and whether it writes every record again. A delivery flow also gains **Deletions**:
the keys removal flows found its source no longer holds, as the Removals page's grid shows them, with **Deliver again** for
picked records, and the removal runs that wrote to its ledger.

## OSDU

- **Explorer**: what the partition holds, read live from OSDU. See [the explorer](explorer.md).
- **Search terms**: the source columns the explorer searches by. See [search terms](search-terms.md).
- **Assertions**: the dashboard of every assertion in the partition, each by where it stands now, its latest result of
  the assertion as its document states it. The GUI calls each entry of a flow's `tests:` an assertion, and each item of
  its `assert:` a check. From the top: the title with one pill per set beside it (one picked narrows the page, kept in
  the address as `?set=`, with **Run the set**); the verdict in one sentence, saying where most of what fails is, with
  the health figure beside it and under them the counts of **Failing**, **Could not be checked**, **Warnings**, **Not
  yet checked** and **Passing**, each a filter whose hover says what it means; a
  map of where the data stands, a row per value of one dimension and a column per value of another (the reader picks
  them), each square how its assertions stand and the filter to them; and the assertions grouped by what they need, ten
  at a time, a search narrowing them. A row says in a line or two what the assertion found; one opens at a time on its checks
  (what each expected and found, why, and the records that fail it), its dimensions, its report, its flow and **Run
  again**. Disabled assertions are left out until **Include disabled**, then listed with the reason their document gives;
  one changed since its last result is not yet checked, and one whose proof values are past their day warns. **Present**
  shows it full screen, and **Download** saves it as one HTML page that works offline, and with a set picked the set's
  report as Markdown, JSON or JUnit XML. An assertion flow's **Assertions** tab is the same dashboard of its assertions,
  with **Run all**. See [assertion sets, titles and dimensions](../flow/assertion-sets.md). A report (`/delivery/assertions/runs/{n}`) shows one run's assertions by
  outcome, with **Run the N assertions not passing again**, **Run again**, and the report as HTML, Markdown, JSON or
  JUnit XML. See [assertion flows](../flow/assertion.md).
- **Inventories**: every inventory flow's inventories in the partition (kind, findings raised, when reconciled). One
  opens its report: the findings (Orphan, Foreign, Stale, Forgotten and the rest), tabs **Ids**, **Runs** and
  **Removals**, **Run pipeline**, **Export the ids as CSV**, and **Remove from OSDU** for picked ids where the flow allows
  it, through the removal dialog; picked gone ids offer **Restore in OSDU** (where the flow declares a removal) and
  **Remove from inventory** (nothing changes in OSDU) instead, and a soft-delete removal under Removals offers **Restore
  what it removed**; picked missing ids offer **Deliver again**, through the flows that delivered them, under their own ids
  or new ones ([inventory removals](../flow/inventory-removals.md)). An id opens as OSDU holds it in the workbench's bottom panel. **Filter** selects the ids
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
  by an admin with **Remove**. A dimension flow's views are listed on its pipeline page's Dimensions tab, with
  **Suggest joins**; a view opens on this page, with tabs **Columns**, **Joins**, **Checks**, **SQL** and **Definition**,
  and one no flow declares any more can be removed by an admin. See [dimension flows](../flow/dimension.md) and
  [dimension views](../flow/dimension-views.md).
- **Partitions**: the registry, as the control plane's configuration names it. Each partition with its description,
  whether it is the default, its cache, the flows that serve it, and when and by whom it was registered. The page
  registers nothing: the configuration names the partitions, and the control plane registers them when it starts. A
  configured partition carries a **configured** badge, whose tooltip names its setting (`Osdu:Partitions:<name>`), and has
  no actions. A registered partition the configuration does not name keeps, for an admin, **Make default** (hidden while
  the configuration names the default), **Describe** and **Remove**; removing deletes nothing kept under the partition
  (caches, ledgers and runs stay). The **Actions** column shows only when some row has one. With no partition at all
  the page says **No partition to work against** and how to name one in the configuration. See
  [configured partitions](partitions.md#configured-partitions).
- **Access restrictions**: the restrictions registered for the partition in the title bar, each with what it is for, its
  viewers and owners, how many records carry it, and who registered it, searched by name, purpose or group. An admin
  **Register**s one (a name, what it is for, and the groups as one email per line, checked to be data groups before
  anything is sent), **Describe**s it, or **Remove**s one no record carries; while records carry it, **Remove** says so on
  hover. A restriction's groups stay as registered. See [restricting access](access-restriction.md#the-registry).

## Build

The **Delivery** section of Build holds what a delivered record is built from and the order records reach OSDU in:
**Mappings**, the **Mapping builder**, **Templates**, **Cache** and **Order**, on
[a page of their own](gui-build.md). Its **Flows** and **Sources** sections are SQLFlow's pages.

## Records in the workbench's search

The module adds a **records** category to the workbench's search: a delivery key lands on its record, and an OSDU id, a
source key, a label or an ingestion file name lists the records that start with it, across every flow of the title bar's
partition, from the ledger's indexed lookup, the same one the Records page reads.
