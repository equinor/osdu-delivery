---
id: delivery-concept-gui-build
title: "The Build pages of the workbench: Mappings, the Mapping builder, Templates, Cache and Order"
type: concept
summary: "The Delivery section of Build: Mappings, the Mapping builder, Templates, Cache and Order, what a delivered record is built from and how it is ordered."
keywords:
  - cache page
  - mapping builder
  - templates page
  - mappings page
  - value check
  - order page
related:
  - delivery-concept-gui
  - delivery-concept-gui-ledger
  - delivery-concept-mapping-overview
  - delivery-concept-dry-run
  - delivery-concept-templates
  - delivery-concept-partition-cache
  - delivery-concept-osdu-order
  - delivery-guide-writing-a-mapping
sourceRefs:
  - osdu/gui/src/features/delivery/ValueCheckRecords.tsx
  - osdu/gui/src/features/delivery/DeliveryDocumentsPage.tsx
  - osdu/gui/src/features/delivery/TemplateVariableExplorer.tsx
  - osdu/gui/src/features/delivery/MappingBuilderPage.tsx
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
  - osdu/gui/src/features/delivery/DeliveryCachePage.tsx
  - osdu/gui/src/features/delivery/DeliveryCacheApprovals.tsx
  - osdu/gui/src/features/delivery/order/OrderPage.tsx
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueIssuesWorkbook.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
---

# The Build pages of the workbench

The **Delivery** section of Build: what a delivered record is built from, and the order records reach OSDU in. Its
**Flows** and **Sources** sections are SQLFlow's pages. The rest of what the module adds to the workbench is on
[the OSDU pages of the workbench](gui.md).

## Mappings

The mapping documents the repositories hold, as the last sync found them, one row each, with **New mapping** (opens the
builder). The search comes first and has the cursor when the page opens: it finds a mapping by anything its row shows (name,
kind, source, template version or path), every word typed in any order, and Enter opens the first row listed. **All repos**
narrows the list to a repository.

A mapping opens on a page of its own, at `/delivery/documents/{id}`, in a workbench tab named after it, so a link, a reload
and Back land on it. The page fills the window and does not scroll. Its heading is the mapping's name, a click from a
searchable list of every mapping; the kind it renders; its path and its jump to lineage; the template version it fills (a link
to Templates, its tooltip naming the template folder file the version was read from, or saying no folder on the mapping's
path holds it); and **Open in builder**.

Under the heading is one toolbar: the three views, **Overview**, **Staged records** and **Properties**, then which rows are
read, and **Dry run**. Overview reads the document, so it takes that place for its own search and leaves which rows are read until
a record is shown. Elsewhere the toolbar picks its scope's values and how many rows, and which flow's rows are read only when several flows render
with the mapping (each names its record table). A required parameter with no default starts on its first option, the value its record table holds most, so the page opens on rows
rather than on a refusal: the field shows the value taken, another is a pick away, and a field emptied stays empty. Nothing is
read of the rows until the values are known to do. A run's own form never fills a value in this way. Under the toolbar, when
there is something to say, one line says the dry run is under way, why it failed, why the rows cannot be read, or whether the
rows can be delivered.

- **Overview**, which the page opens on: what the mapping does, on one canvas: the tables it reads, what happens to each value on
  its way, and the record it fills, read from the document alone ([a mapping's overview](mapping-overview.md)).
- **Staged records**: the rows staged for the mapping, before anything is delivered, beside what they render to, with a handle
  between them that gives either more room and remembers the width. The list reads the table a page at a time and asks it for a
  search, by key, label or file; each line says what became of the row, or how many rows its child tables hold of it, and Up and
  Down step through them. After a [dry run](dry-run.md), **Ready**, **Worth a look**, **Held** and **Passed over** carry a count
  and narrow the list, each opening its first record. Its foot says how many rows are listed and of which table. Beside the list
  is one section, with one page at a time:
  - **Render**: the record picked, the first the list holds until one is: whether it is only staged or in the ledger, what the next
    run would do with it and why, then its
    **Document**, what the **Schema service** says of it, **How it rendered**, how a **Delivery** would send it and its **Source
    rows**, as the flow's **Preview** shows them. Picking a row shows this page, from whichever page was open.
  - **Source YAML**: the mapping as written. With no flow to read rows from, the section opens here.
  - **Record shape**: the shape the mapping gives a record, with no data read; the mapping's parameters fill what uses them.
  - **Dry run**, once one has run, which is where it opens: what was checked of every record alike, the rules broken most often
    with an example of each a click away, and what differs between the template and the Schema service.

  Everything picked stays when the reader moves to **Properties** and back.
- **Properties**: the template the mapping fills as the record's tree, beside the selected attribute. The views
  **Filled**, **Missing**, **Unfilled**, **Failing** (after a dry run) and **All** each carry a count; **View** adds
  **Required**, **Minted** and **Nested**. The selected attribute reads in **Filled by**, **Data**, **Failing records**,
  **SQL** and **Schema**. What the toolbar's **Dry run** found reads here attribute by attribute, on a line over the tree. An
  attribute's **Check values** checks it alone. See [writing a mapping](../guides/writing-a-mapping.md).
  - **Failing records** goes through the rows a check found wrong with the attribute, one record at a time, on one page
    read top to bottom. At the top, and staying in view, the reason (picked from a list when there are several) and
    **Record n of m**, m being the rows that reason holds for; past the records listed so far, the next are read from a
    node. Then the record, with its source key, file and row; **What is wrong in this record**, each value with its item,
    its reason and where to fix it (**Fix at**: the file, the row and the column); **In the source**, the columns the
    attribute reads and what the row holds in them, a child dataset's rows with the ones found wrong marked and the cell to
    fix picked out, null, empty and blank told apart, and **The rest of the row** for its other columns;
    and **In the rendered record**, the document as a delivery would render it, sent nowhere, folded down to the attribute,
    each value of it marked and each place it is left out said where it would have been. A row of **Data** opens its record
    here, and the arrow beside a record opens it in the flow's **Preview**. An attribute no row fails shows the first record
    of the scope, and says so.
  - **SQL** writes the T-SQL that shows what the attribute reads, to copy into SSMS: each row's key, the source columns the
    attribute reads (a group's, those of everything it holds) and the file and row it came from, as the flow's tables hold them
    now, before the mapping's lookups and expressions. It reads the rows a delivery reads, with the same scope predicate: the
    toolbar's flow and scope values, in the order a read pages in, as many rows as the toolbar reads, and without rows marked
    deleted. A child table is left joined, so a record with no child rows shows, with nulls, and its columns are named
    `curves.curve_unit`; each child table beyond one has a statement of its own, so their rows do not multiply. Once a check has
    found rows wrong with the attribute, **Failing rows** narrows the query to the records it listed, as many as a request can
    carry, and says how many of the rows that fail those are. A column the tables do not hold is named and left out, and an
    attribute that reads no source column says so. The page writes the query from the tables' shape: it reads no row and runs
    nothing.
  - **Export rows**, in **Data**, saves the rows failing that attribute as an Excel workbook, reading them again on a node
    as the check read them. **Issues** is a row for each issue: the record, what it came to and why; where to fix it
    (**Source file**, **Source row**, **Source column** and what it holds, **Source value**), an item of a repeated array
    naming its child row's file and row; then the base data, the record's key and the columns the attribute reads with what
    this row holds in them, named as the mapping names them (`dataset.aliases.alias_name`). Colour connects them: amber is
    what to fix, on the headers saying where and on the cell itself among the base data, which can be filtered on; blue
    heads the columns the attribute reads, and grey the key. The column to fix is the one the reason names, else the one
    holding the value, else the attribute's only column, else those it reads that are empty; a row none of these settles
    has no amber cell. **Summary** adds them up by reason, and **About** says what was checked, with what, and what each
    column and colour means.
    A workbook holds up to what a node task's result does, about 5 MB; one with more issues than that lists the first of
    them, says so in **About** and on the screen, and keeps every count. `sqlflow values --rows` lists every one, however
    many.
- **YAML**: the document as written.
- **Record shape**: the record the mapping renders, placeholders in angle brackets.

## Mapping builder

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

## Templates

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

## Cache

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

## Order

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
