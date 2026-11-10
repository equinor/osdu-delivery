---
id: delivery-concept-mapping-overview
title: "A mapping's overview: what a mapping does, on one canvas, from its tables to the record"
type: concept
summary: "The Overview tab of a mapping: the tables it reads, what each value goes through, the record it fills, and one staged record followed through it."
keywords:
  - overview
  - mapping overview
  - mapping canvas
  - what a mapping does
  - how a mapping is populated
  - mapping outline
  - show a record
  - cache table
  - source table
  - found by
  - preview rows
  - first 100 rows
  - search a table's rows
  - find text in any column
  - double-click for details
  - right-click a table
  - table-rows
  - rendered record
  - parameter values
  - right-click menu
  - open in lineage
  - where a table is populated from
  - zoom to a search
  - canvas toolbar
  - explain the yaml
  - explain this
  - ask the assistant about a mapping
  - show in the yaml
  - where a column is read in the yaml
  - show the sql
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-values
  - delivery-concept-gui
  - delivery-concept-api
  - delivery-concept-dry-run
  - delivery-concept-lineage
  - delivery-concept-mapping-overview-sql
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingOutline.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryValueCheckEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/TableRowsOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreviewer.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/gui/src/features/delivery/overview/MappingOverview.tsx
  - osdu/gui/src/features/delivery/overview/OverviewCanvas.ts
  - osdu/gui/src/features/delivery/overview/OverviewExplain.tsx
  - osdu/gui/src/features/delivery/overview/OverviewInspector.tsx
  - osdu/gui/src/features/delivery/overview/OverviewLineage.tsx
  - osdu/gui/src/features/delivery/overview/OverviewRail.tsx
  - osdu/gui/src/features/delivery/overview/OverviewRendered.tsx
  - osdu/gui/src/features/delivery/overview/OverviewRows.tsx
  - osdu/gui/src/features/delivery/overview/OverviewYaml.tsx
  - osdu/gui/src/features/delivery/overview/outlineLayout.ts
  - osdu/gui/src/features/delivery/overview/outlineLineage.ts
  - osdu/gui/src/features/delivery/overview/outlineModel.ts
  - osdu/gui/src/features/delivery/overview/partLines.ts
  - osdu/gui/src/features/delivery/overview/partTargets.ts
  - osdu/gui/src/features/delivery/overview/recordMarkdown.ts
  - osdu/gui/src/features/delivery/overview/tableRows.ts
  - osdu/gui/src/features/delivery/overview/yamlExplain.ts
  - osdu/gui/src/features/delivery/lineageTargets.ts
---

# A mapping's overview: what a mapping does, on one canvas, from its tables to the record

A mapping page opens on **Overview**, which draws what the [mapping](../flow/mapping.md) does: the tables it reads, what
happens to each value on its way, and the record it fills. It is read from the document alone, so a mapping no flow renders
with yet is drawn all the same, and nothing is rendered, read or sent unless a reader asks to follow a record through it.

Beside the tabs and the search, one sentence says what the mapping does, and goes under them when the window is too narrow
for both:

```text
Each row of silver.WellLog becomes one WellLog record, with one item of Curves per row of silver.WellLogCurve.
```

## Reading the canvas

One line is one property of the record, read left to right.

| On the canvas | What it is |
| --- | --- |
| A table on the left | Where a row comes from: the flow's record table, and a child table level with the `$forEach` that repeats it. A key column carries a key. A table the catalog describes says what each column is, and how many it holds that the mapping does not read. A column a [lookup table joined into the read](../flow/delivery-lookups.md) gives the row carries a link, sits after the table's own columns with the rest of its lookup's, and its tooltip names the lookup table and the key columns the row finds it by; the table's details list its lookups. |
| A table on the shelf above | What a value is looked up in: a type of the partition's cache, a record set searched in OSDU, and the flow's parameters. A field marked **found by** is one a lookup compares; the others are what it reads. |
| A dot | Where a line crosses a table on the shelf: the value is looked up there, or for the parameters, is read from there. |
| A mark on a line | Something done to the value (`trim`, `split`, `replace`, `equals`, `date`, `number`, `id`, `ref`, `expr`), in the order a render does it. A mark before the dots changes the value a lookup compares ([mapping lookups](../flow/mapping-lookups.md)). |
| A dashed mark | A condition: `when` decides whether the property is written, and `where` which child rows become items ([mapping expressions](../flow/mapping-expressions.md)). |
| A dashed line | A column a condition tests, rather than a value the property takes. |
| A chip in quotes | A literal, written as it stands on every record. |
| A hollow end | An optional property: an empty value leaves it out rather than holding the record. |
| A shield or a warning glyph beside a property | Rules the property states ([mapping assertions](../flow/mapping-assertions.md)), or something wrong with it. |

Blue comes from the row and orange is looked up, in the cache or by a search; what the flow supplies stays neutral. A
table's columns sit in the order the record first reads them, key first, so the lines from one table run side by side
rather than crossing. That order holds whatever is open.

Closing a part of the record joins its lines into one and counts what it holds: `Curves[] 13` with `ref ×5` on its line. The
page opens on `data`, one level down. `id` is always there, with the key columns entering it, because no node fills it: the
engine makes it from the key ([the OSDU id](../flow/mapping.md#the-osdu-id)).

The record's heading says how many properties of the [template](templates.md) nothing writes, and goes to **Properties**
for them.

## The toolbar

The toolbar runs down the canvas's left side. Each glyph says what it does when pointed at, and one that switches something
on stays lit while it is on.

| Glyph | What it does |
| --- | --- |
| Rows | **Show a record**: follows a staged row through the mapping ([below](#working-with-it)). |
| Document | **YAML**: the mapping as written, beside the canvas. |
| Copy, function, database search, pin, link | Lights the properties filled one way: **copied** (a column, written as it is), **changed** (a column or an expression, changed on its way), **looked up** (in the partition's cache, or by a search of OSDU), **fixed** (a literal or a parameter, the same on every record) or **linked** (read from another record). Pointing at one says how many of the mapping's properties it lights, such as `7 of 40 looked up`; a way no property is filled has no glyph. A second click puts the light out, and so does a search. |
| At its foot | **Open all** or **Fold**, zoom in, zoom out, and **Fit to the window**. |

## Working with it

| To | Do |
| --- | --- |
| Open or close a section | Click it, or press Space on it; Right opens it and Left closes it, and Up and Down move from line to line. **Open all** opens everything, and **Fold** goes back to where the page started. |
| See the alternatives of a `$coalesce` as lines of their own | Click its chevron. Closed, its line says `first of 3` and passes through every table they read. |
| Trace something | Point at it. Pointing at a property lights what enters it and the tables on its way, pointing at a column lights everywhere it goes, and pointing at a table lights everything that reads it, from the columns looked up in it to the properties they fill. A line counts as what it comes from, a line off the shelf as its table, and is lit down to the last row lit on it. |
| Keep a part lit | Click it, or press Space on it. A property, a column, a table or a line from one stays lit and shaded once the pointer leaves it, and nothing opens over the canvas. A click on another part moves the light; Escape or a click on the canvas puts it out. |
| See how a property is filled | Double-click it, or press Enter on it (**Details** on its menu does the same). A panel slides in from the right with the way its value takes ([below](#the-way-a-value-is-filled)), its alternatives, its rules and the lines of the document that write it. A column or a table, or a line from one, lists what it reaches. While the panel is open, a click on another part shows that part's. Escape closes it and leaves the part lit; a click on the canvas closes it and puts the light out. |
| Do something with a part | Right-click it, or press the menu key or Shift+F10 on it. Every part has a menu, named for what it is on ([below](#the-menus)). |
| See where a part is populated from | **Open in lineage** on its menu, or the lineage button on its panel, opens the [lineage graph](lineage.md) on its node: a table's, its cache type's, the kind it searches, or for a property each node its value comes from ([below](#opening-a-part-in-lineage)). |
| Look at a table's rows | **Preview the first 100 rows** on a table, one of its columns or a line from it (**Preview 100 cached records** for a cache table, **Preview 100 records in OSDU** for a search). The panel of a table or a column has the same button. The rows open under the canvas ([below](#looking-at-a-tables-rows)). |
| See the parameters' values | **Preview the values** on the parameters, or one of them ([below](#the-parameters-values)). |
| Read the record as it renders | **Preview the rendered record** on the record's heading ([below](#the-rendered-record)). |
| Find a property or a column | Type in **Property or column**, which opens what holds the matches, lights them and counts them beside the field (`14 of 40 found`). Once the typing stops, the canvas zooms to them, close enough to read: the properties found with the columns they read, up to 140% and never under 100%, each property found and each column named by the search shaded. Where the columns are too far from their properties to fit at 100%, the properties alone are framed, from the first. Clearing the search takes the view back to where it was, unless you moved it since. |
| See every property filled one way | Click its glyph on [the toolbar](#the-toolbar). |
| Read the document beside the canvas | **YAML** on the toolbar opens the mapping as written, each line tied to the canvas: pointing at a line lights the property it writes, and a click keeps it lit. A property, a column or a table lit anywhere lights the lines that write or read it (for a column, those of the `dataset` or `parameters` block naming it too), marks a column where they name it, comments aside, and a click brings the first into view. |
| Have lines of the document explained | **Explain** in the YAML's heading asks the assistant about the lines selected, or the whole mapping, and the answer opens under the canvas ([below](#explaining-the-yaml)). |
| Follow a real row through the mapping | **Show a record** on the toolbar previews a staged row of the flow rendering with the mapping (picked beside the record when several do) and shows each column's value and each property's, a repeated property's from its first item. The sentence beside the search gives way to the record, with the flow's scope beside it, and the arrows step through the rows. Beside the row, **staged** says no run has taken it into the ledger yet, or the status of its record says where it stands. A `$coalesce`'s alternatives say which gave the value. Nothing is sent. |
| Move around | Drag to pan, scroll to move up and down, and hold Ctrl and scroll to zoom. The foot of the toolbar holds **Open all**, zoom and fit. |

After a [dry run](dry-run.md), the properties it found broken carry a warning, on the section holding them while that is
closed.

## The way a value is filled

A property's panel lays out how its value comes, in the order a render takes it. Each stage shows only when the value takes
it, and a column, a field or a table in it opens its own panel on a click.

| Stage | What it shows |
| --- | --- |
| **Input** | The columns the value starts from, each with its table, the flow's parameters it reads, or the literal it writes. A column a condition only tests is not an input. |
| **Changed** | The modifiers that change the value, each as the document writes it and what it does; **Computed** for an expression. |
| **Compared in** a table | For a lookup: the table it looks in, and each line it finds the record by, the table's field beside the column or text it has to equal (`FacilityName = wellbore_uwi`), numbered in the order they are tried. Under them, how the lines decide: in turn until one finds exactly one record, as written and then ignoring case (and punctuation and spacing with `$ignoreSeparators`), and what several records or none do ([mapping lookups](../flow/mapping-lookups.md)). A value a step changed first is compared as that step leaves it. |
| **Fetched** | The field read from what the lookup found: `id` of `Wellbore`. |
| **Written to** | The property. |

With **Show a record** on, each column shows the row's value, under the input and at the first line comparing it, and the
property shows the value the record holds, so the panel reads as `wellbore_uwi` `WB A-12` compared with
`FacilityName`, then `NameAliases.AliasName`, giving the `id` written to `data.WellboreID`. A line's value is the row's only
when no step changed it first.

## The menus

| Right-click | The menu offers |
| --- | --- |
| A table, one of its columns, or a line from it | A look at what it holds (its rows, or the parameters' values), **Show the SQL** for a flow's table or a cache table ([the SQL of a table's rows](mapping-overview-sql.md)), its **Details**, **Show in the YAML** where the document names it, **Open in lineage**, and a copy of its name: a flow's table by the name the flow gives it. |
| Another record a mapping reads (a parent, a child) | Its **Details**, **Open in lineage** when its kind is known, and a copy of its name. |
| A property, or a dot on its line | Its **Details**, **Show in the YAML**, which opens the document at the lines that write it, **Open in lineage** for what its value comes from, and a copy of its path. |
| The record's heading | **Preview the rendered record**, the YAML, **Open in lineage** for the kind, and a copy of the kind. |
| The canvas | **Fit to the window**, **Open all** or **Fold**, the YAML, and **Open in lineage** for the mapping. |
| A line of the YAML | **Explain this** and **Copy**, over the lines selected, or else the lines that write the property the line is in, or else the line alone; the menu's heading names the lines and the property. Without the assistant the browser's own menu opens. |

Escape closes a menu first, then the details, then what is shown under the canvas. In an explanation, Escape is the
conversation's and leaves it open.

## Opening a part in lineage

**Open in lineage** on a part's menu opens a picker that names what is about to open and where it stands in the
[lineage graph](lineage.md); choosing it opens the graph on that node, with what populates it to its left. The panel of a
part, and the panel of a table's rows, of the parameters' values or of the rendered record, carries the same as a button in
its heading. Each part opens the node it is, or the one it belongs to:

| Part | Opens |
| --- | --- |
| The record table, a child table | The table's node as the flow picked reads it, over the flow's own connection: the node the `ing` flow loading the table writes. Until a flow is picked the canvas does not know the table, and offers none. |
| A column, or a line from one | Its table's node: lineage holds no node of a column. |
| A cache table | The cache type in the workbench's partition, which its cache flows write. |
| A search | The kind it searches, as the mapping writes it. A kind with wildcards is a node of its own, which the mapping reads. |
| A parent, a child record | Its kind: a child's as the mapping names it, a parent's as the template's link names it, so a mapping read without its template offers none for its parent. |
| The records an enrichment registers | The enrichment's mapping, a mapping node of its own. |
| The parameters | The flow picked, in its repository's graph. |
| A property, or a section | Each node its value comes from: the tables its lines read, the tables it is looked up in and the child table it repeats, and for a section everything it holds. With more than one, the menu and the panel list them, each opening its own picker. A literal comes from no node, and offers none. |
| The record's heading | The OSDU type the mapping fills, which the flows delivering it write. |
| The canvas | The mapping's node, between what it reads of the cache and the flows rendering with it. |

A node is looked for in the partition the workbench works in, and in every partition when that one has none of it. The
record an enrichment adds to opens nothing: its kind is the other mapping's. When no synced flow reads or writes a node,
the picker says so.

## The parameters' values

The values the flow's parameters render with, under the canvas: the ones the mapping reads first, in the order the parameters
list them on the canvas, then any other the mapping declares. They are the values that reach a record, each a mapping's default
under what the flow supplies, `${env:...}` and `${keyvault:...}` references resolved
([mapping parameters](../flow/mapping.md)). They are read from one record's render, the record shown or the scope's first, so
the flow picked and its scope decide them. Pointing at one lights its lines, a click keeps them lit, and a double click opens
its details.

## The rendered record

One staged record as the mapping renders it, under the canvas: the record shown, or with none shown the scope's first, rendered
as a delivery would render it now. Nothing is sent. It reads as a document:

- The record's name heads it, then what it holds at its top, its kind and id first.
- Each part of the record (`acl`, `legal`, `tags`, `data`...) is a section with a line per property, a nested property by its
  dotted path.
- A list of objects, such as `data.Curves`, is a table of its own with a row per item, at most 200 of them.

The outline beside it lists the parts with how many properties or items each holds, marks the one being read, and goes to one on
a click. **JSON** shows the rendered document as it is. **Copy as Markdown** copies the document as Markdown, which reads
the same pasted into a ticket or a chat. A record the render would hold says why above it.

## Looking at a table's rows

A table's rows open in a grid under the canvas, which a drag on the line between them resizes. The grid lists every column,
those the mapping reads first, in the order the table on the canvas lists them and marked in the table's colour, and the
others after them, muted. Pointing at a column's heading lights its lines on the canvas, a click keeps them lit, a double click
opens its panel, and a preview asked for from a column brings that column into view. Escape closes the grid once no panel is
open and nothing is lit.

| Table | What the grid holds |
| --- | --- |
| The record table, or a child table | The first 100 rows a delivery reads in the flow's scope, read with the flow's own connection: the scope's rows under the table's `filter`, those marked deleted left out, each lookup table joined, and for a child table the rows of those records under its own `filter`. The order is the one a read pages in. The line over the grid counts the rows, up to 100,000. A value longer than 1,000 characters is cut by the database, and its column's heading says so. SQLFlow's system columns are left out. The flow's scope stands beside the line, and with **Show a record** on, the record's rows are marked. |
| A cache table | 100 records of the type, as the partition's current cache version holds them: the OSDU id (or a lookup row's key) and each name the type keeps. |
| A search | 100 records of the search's kind in OSDU, read through the [explorer](explorer.md): each id and the values of the paths the mapping reads, at most 8 of them. |

The record table's and a child table's rows come from `POST /api/v1/delivery/flows/{pipelineId}/table-rows` ([API](api.md)),
read in the control plane, at once. Nothing is written. **SQL** in the grid's heading shows instead the T-SQL that reads the
same rows in SSMS, a flow's table's or a cache table's, to cross-check a lookup join against the cache
([the SQL of a table's rows](mapping-overview-sql.md)).

### Searching the rows

The field at the head of the grid (**Find text in any column**, **Find an id or a value** for a cache table, **Find words in
OSDU** for a search) searches every row the table holds, where it is held, not only the rows shown. Once the typing stops, the
grid shows the first 100 rows found, the line over it counts them (`The first 100 of 2,340 rows a delivery reads in this scope
holding "16/2"`), and each value marks where it matched. The rows found before stay in view, dimmed, while the next search is
read. A search that finds nothing says how it looked. Escape in the field clears it, and the grid stays open.

| Table | What a search finds |
| --- | --- |
| The record table, or a child table | The rows one of whose values, as the grid shows it, holds the text anywhere in it, in any case: a moment in UTC (`2024-05-06T10:00:00Z`), a number in its shortest form, a bit as `true` or `false`. A number or a moment typed whole is found however the database writes it, such as `1234567.123456789` in a `float` column or `2024-01-02T10:15:00.12Z` in a `datetime2` one. A binary column is not searched. The database searches the rows a delivery reads in the scope, those marked deleted left out, so it reads every one of them when few hold the text; the count stops at 100,000 as it does without a search. The search is `search` in the request's body, one line of at most 200 characters. |
| A cache table | The records whose id or one of whose values holds the text, as the [cache's records](partition-cache.md#where-to-see-it) search them. |
| A search | The records OSDU's search finds by the text, through the [explorer](explorer.md): whole words, and an id by its start. |

## Explaining the YAML

With the [assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md#explain) configured, the YAML's heading
offers **Explain**, as every code window does: it asks about the lines selected, each line whole, or the whole mapping when
nothing is selected. A right-click on a line offers **Explain this** over the lines selected, or else over the lines that
write the property the line is in ([the menus](#the-menus)).

The answer opens under the canvas, where a table's rows open, rather than in the drawer over the page, so the lines it
explains stay in view beside it; a drag on the line between them resizes it, and the size is kept apart from the rows'.
The question says which lines of how many (`YAML, lines 97 to 102 of 250`), the panel's heading repeats it, and the
conversation is followed up in the panel. Each question is a new conversation, kept with the others on the Assistant page,
where it stays after the panel closes. Looking at a table's rows, the parameters' values or the rendered record takes the
panel's place.

## The outline

The canvas draws what `POST /api/v1/delivery/mapping-builder/outline` answers for a mapping document
([API](api.md)): every source it reads, and the record's properties in the order the document writes them, each with the
reads that enter it, the steps it takes and the lines that write it. It is read from the definition a render reads, so every
node of the language has its place, those a builder draft cannot hold (`$lookup`, `$findAll`, lists) included.

- A document that does not load answers with the issue and no outline, and the tab shows the issue.
- A template that cannot be read costs the outline its types and no more, and an issue says so.

`GET /api/v1/delivery/flows/{pipelineId}/source-tables` gives the tables of the flow picked: their names, their nodes in
lineage (`objectKey`, where the flow's read lands), how a child table joins its record, and what the catalog holds of their
columns through the flow's own connection (or, when it holds none there, through another), without SQLFlow's system
columns. A table the catalog has not described lists no columns, and the canvas shows what the mapping reads of it. No
table is read.
