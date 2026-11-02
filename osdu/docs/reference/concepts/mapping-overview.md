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
  - right-click a table
  - table-rows
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-values
  - delivery-concept-gui
  - delivery-concept-api
  - delivery-concept-dry-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingOutline.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryValueCheckEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/TableRowsOperation.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/gui/src/features/delivery/overview/MappingOverview.tsx
  - osdu/gui/src/features/delivery/overview/OverviewCanvas.ts
  - osdu/gui/src/features/delivery/overview/OverviewRows.tsx
  - osdu/gui/src/features/delivery/overview/outlineLayout.ts
  - osdu/gui/src/features/delivery/overview/outlineModel.ts
  - osdu/gui/src/features/delivery/overview/tableRows.ts
---

# A mapping's overview: what a mapping does, on one canvas, from its tables to the record

A mapping page opens on **Overview**, which draws what the [mapping](../flow/mapping.md) does: the tables it reads, what
happens to each value on its way, and the record it fills. It is read from the document alone, so a mapping no flow renders
with yet is drawn all the same, and nothing is rendered, read or sent unless a reader asks to follow a record through it.

Its first line says what the mapping does in a sentence:

```text
Each row of silver.WellLog becomes one WellLog record, with one item of Curves per row of silver.WellLogCurve.
```

## Reading the canvas

One line is one property of the record, read left to right.

| On the canvas | What it is |
| --- | --- |
| A table on the left | Where a row comes from: the flow's record table, and a child table level with the `$forEach` that repeats it. A key column carries a key. A table the catalog describes says what each column is, and how many it holds that the mapping does not read. |
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

## Working with it

| To | Do |
| --- | --- |
| Open or close a section | Click it. Up, Down, Left, Right and Enter do the same from the keyboard. **Open all** opens everything, and **Fold** goes back to where the page started. |
| See the alternatives of a `$coalesce` as lines of their own | Click its chevron. Closed, its line says `first of 3` and passes through every table they read. |
| Trace something | Point at it. Pointing at a property lights what enters it and the tables on its way, pointing at a column lights everywhere it goes, and pointing at a table lights everything that reads it, from the columns looked up in it to the properties they fill. A line counts as what it comes from, a line off the shelf as its table, and is lit down to the last row lit on it. |
| See how a property is filled | Click it. A panel slides in from the right with each step in plain words, its alternatives, its rules and the lines of the document that write it. A column or a table, or a line from one, lists what it reaches. Escape or a click on the canvas closes it. |
| Look at a table's rows | Right-click a table, one of its columns or a line from it, and pick **Preview the first 100 rows** (**Preview 100 cached records** for a cache table, **Preview 100 records in OSDU** for a search). The menu key or Shift+F10 opens the same menu from the keyboard, and the panel of a table or a column has the same button. The rows open under the canvas ([below](#looking-at-a-tables-rows)). Right-clicking the parameters, which hold no rows, opens their panel. |
| Find a property or a column | Type in **Property or column**, which opens what holds the matches and lights them. |
| See every property filled one way | Click **copied**, **changed**, **looked up**, **fixed** or **linked**, each with its count. |
| Read the document beside the canvas | **YAML** opens the mapping as written, each line tied to the canvas: pointing at a line lights the property it writes, and a click picks it. |
| Follow a real row through the mapping | **Show a record** previews a staged row of the flow picked in the toolbar and shows each column's value and each property's, a repeated property's from its first item. The sentence over the canvas gives way to the record, with the flow's scope beside it, and the arrows step through the rows. Beside the row, **staged** says no run has taken it into the ledger yet, or the status of its record says where it stands. A `$coalesce`'s alternatives say which gave the value. Nothing is sent. |
| Move around | Drag to pan, scroll to move up and down, and hold Ctrl and scroll to zoom. The corner of the canvas holds zoom, fit, and **Open all**. |

After a [dry run](dry-run.md), the properties it found broken carry a warning, on the section holding them while that is
closed.

## Looking at a table's rows

A table's rows open in a grid under the canvas, which a drag on the line between them resizes. The grid lists every column,
those the mapping reads first, in the order the table on the canvas lists them and marked in the table's colour, and the
others after them, muted. Pointing at a column's heading lights its lines on the canvas, a click on it opens its panel, and a
preview asked for from a column brings that column into view. Escape closes the grid once no panel is open.

| Table | What the grid holds |
| --- | --- |
| The record table, or a child table | The first 100 rows a delivery reads in the flow's scope, read with the flow's own connection: the scope's rows, those marked deleted left out, and for a child table the rows of those records. The order is the one a read pages in. The line over the grid counts the rows, up to 100,000. A value longer than 1,000 characters is cut by the database, and its column's heading says so. SQLFlow's system columns are left out. The flow's scope stands beside the line, and with **Show a record** on, the record's rows are marked. |
| A cache table | 100 records of the type, as the partition's current cache version holds them: the OSDU id (or a lookup row's key) and each name the type keeps. |
| A search | 100 records of the search's kind in OSDU, read through the [explorer](explorer.md): each id and the values of the paths the mapping reads, at most 8 of them. |

The record table's and a child table's rows come from `POST /api/v1/delivery/flows/{pipelineId}/table-rows` ([API](api.md)),
read in the control plane, at once. Nothing is written.

## The outline

The canvas draws what `POST /api/v1/delivery/mapping-builder/outline` answers for a mapping document
([API](api.md)): every source it reads, and the record's properties in the order the document writes them, each with the
reads that enter it, the steps it takes and the lines that write it. It is read from the definition a render reads, so every
node of the language has its place, those a builder draft cannot hold (`$lookup`, `$findAll`, lists) included.

- A document that does not load answers with the issue and no outline, and the tab shows the issue.
- A template that cannot be read costs the outline its types and no more, and an issue says so.

`GET /api/v1/delivery/flows/{pipelineId}/source-tables` gives the tables of the flow picked: their names, how a child table
joins its record, and what the catalog holds of their columns, without SQLFlow's system columns. A table the catalog has
not described lists no columns, and the canvas shows what the mapping reads of it. No table is read.
