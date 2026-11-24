---
id: guide-follow-a-data-stream
title: "Following data on the lineage graph: what arrived, what each flow did with it, and where it stopped"
type: guide
summary: "The lineage graph's data view: what every node moved in a day, charted over the window, with the lines weighed by it."
keywords:
  - what moved
  - day by day
  - follow data
  - data streams
  - what did the vendor deliver
  - pre table
  - ingestion table
  - where did the data stop
  - fire
  - files delivered
  - rows read
  - rows changed
  - lineage graph data
  - datastreams activity
related:
  - guide-lineage-graph
  - concept-data-stream-detection
  - concept-lineage-graph-and-plan
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.Activity.cs
  - src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
  - gui/src/features/lineage/dataLens.ts
  - gui/src/features/lineage/LineageGraphPage.tsx
  - gui/src/features/lineage/LineageNodeCard.tsx
  - gui/src/features/lineage/LensBars.tsx
  - gui/src/features/lineage/GraphCanvas.tsx
  - gui/src/features/lineage/DataRail.tsx
  - gui/src/features/lineage/LensNodeDetail.tsx
  - gui/src/features/lineage/LensLegend.tsx
  - gui/src/features/lineage/graphUrl.ts
  - gui/src/modules/runMeasure.ts
  - gui/unit/dataLens.test.ts
  - tests/SqlFlow.ControlPlane.Tests/FlowActivityApiTests.cs
---

# Following data on the lineage graph

The [lineage graph](lineage-graph.md) says how nodes connect: this flow should fill that table. Its data view says
what happened: the files that arrived, the rows each flow read and changed, what landed in each table, and where the
data stopped. It is the same drawing with the same controls, so one question is answered where the chain is already
drawn: what did the vendor deliver, and what did each of our flows do with it.

## Turning it on

- **From the Data streams board.** Click a row. The graph opens on that flow's trace, with every object on it drawn
  and the data view on, on the latest day anything on the trace ran.
- **In the graph.** **What moved: Day by day** in the bar turns the view on for whatever is drawn: a whole repository's
  flows, a project, a trace, or the data alone. A node's right-click menu has **Follow its data, day by day**, which
  traces the node and turns the view on, and so does **Follow its data** beside a delivery verdict in the details panel.

Turning it off (the same button, the cross at the end of the day rail, or the part in the line under the bar) draws
the graph as it was.

## What a card says

A card of the data view is taller. Under the node's name it leads with what the node did in the day looked at, and
under that it charts the same measure over the window, one mark per day:

| Card | What it leads with |
| --- | --- |
| Where data enters (a file location, a source nothing here writes) | What arrived: `5 files`, with `130 rows · 12.2 KiB` beside it, or the rows its readers read. |
| A flow of SQLFlow's own kinds | What it wrote: `130 new rows`, or `6 rows changed` with `of 15 read` beside it. A flow that read rows and changed none says `No change` and `15 read`. |
| A flow of a kind a module adds | The kind's own measure (`RunMeasure`): what a run of it moved, in the kind's words, with what failed beside it. |
| A table or a view some flow writes | What landed: `+130 rows`, or `6 rows changed` with `3 new, 2 updated, 1 removed`. |
| A folder or a stack of flows | How many of its flows ran, and how many failed or moved data. |

A number leads only when data moved. `No change`, `No run` and `Not written` are said in quiet words, and `Failed` in
the status colour with its glyph, so a healthy chain reads calmly and the node that needs someone stands out. A flow
with one failed run among good ones keeps its figure and says `a run failed` beside it.

In a card's chart a bar is a day data moved (taller for more, on a square-root scale so one heavy day does not flatten
the rest), a stub at the baseline is a day the node ran and moved nothing, and a red stub is a day it failed. The day
looked at is marked in every chart. Pointing at a day in any chart lights the same day in all of them and names it;
clicking it looks at that day. A table or a view is named with its schema (`pre.Orders`, `arc.Orders`), because a chain
holds the same name twice.

## What a line says

A line is inked by what it carried in the day looked at, and weighed by how much:

- **solid blue**: data moved, heavier for more;
- **dashed grey**: the flow ran and nothing moved;
- **red**: the run failed;
- **faint dotted**: no run.

A line out of a flow carries what the flow wrote; a line into a flow carries what reached it. So a chain whose
ingestion read rows and changed none shows data reaching the ingestion and stopping there:

```text
Shop/orders            5 files     130 rows · 12.2 KiB
        │  (solid)
shop_orders_01_pre     130 new rows
        │  (solid)
pre.Orders             +130 rows
        │  (solid)
shop_orders_02_ing     No change   130 read
        ┆  (dashed)
arc.Orders             No change
```

The legend sits on the canvas, beside the zoom controls. A flow that reads back the table it writes carries nothing
on, so that line stays faint.

## The day looked at

The rail under the canvas is one column per day of the window. A column stands for how many of the drawn flows moved
data, ran with no change, or had a failed run, the failed at its foot, so the days worth looking at are found by eye.

- Click a column to look at that day; the arrows, and the arrow keys with the rail focused, step to the nearest day
  anything drawn ran. **Latest** returns to the latest day anything ran, and follows it.
- A day with several starts lists them: **Whole day**, or one start and only what it ran. A start is a run group (a
  schedule's fire, a flow run with its descendants) or a run started on its own.
- The window is the Data streams board's own (30, 60 or 90 days), set here or there.

Looking at another day changes the cards and the lines at once. It never moves the canvas, and nothing is read again
but that day's starts.

## A node's panel

A click opens the node on the right, as in the graph, with a section for the day looked at:

- **A flow**: what it read and changed (or its kind's counts), the files it took in, how many runs and how long, then
  each run of the day: when it started, how it went, its error whole with a copy button, the files it took in by name,
  and **Open run**. Under that, how the day compares with the flow's normal: its delivery verdict over the window with
  the pattern learned from its own history, the daily chart against what was expected and every detector's reasoning,
  the same analysis the [Data streams](../concepts/data-stream-detection.md) board runs.
- **A table, a view or a file location**: the flows that wrote it and the flows that read it, each with what it moved or
  took in, and, where data enters, the files that arrived by name.
- **A folder or a stack**: its flows, the ones with news first.

Every flow named there selects its node.

## How the drawing is placed

A chain of seven steps is taller than a window is high, and shrunk until it fits none of its figures could be read. So
the data view places a drawing whole when that leaves it readable, and otherwise at the least readable zoom from its
top, where data enters, with the traced node in view. The map in the corner shows the whole drawing at once, each node
in the ink of what happened there, which is where the data stopped at a glance; drag it, or the canvas, to move down
the chain. The fit button still fits everything.

## What is counted

- A day is a UTC day, and a run belongs to the day its result was written, as the Data streams analysis counts it.
- Every run counts, backfills included: this is what happened, not a judgement of it. The verdicts are the board's,
  which leave reprocessing out unless asked.
- A run that split its work over parts (a fan-out) is one run: its parts are its own work, which its result reports
  whole.
- One of SQLFlow's own kinds moved what it inserted, updated and deleted, or what it read when it reports no such
  breakdown (a file load, an export). A kind a module adds says what a run of it moved through
  `FlowKindContribution.runMeasure`, read from the counts its result reports; without one it moved the rows it loaded.
- An object's card says what its writers moved. A flow reports one total, so a flow writing a table and a view over it
  shows that total on both.
- One read takes at most 200 flows. A scope within that is read whole, so drawing more or less of it asks for nothing
  new; a larger one is read for the flows the drawing holds, and past that the view says to narrow it.

## The address

The data view is part of the drawing, so it is part of the link: `data=on` turns it on, `day=2026-01-31` names the day
looked at (left out, the latest day anything drawn ran), and `fire=...` the one start of that day.

## The API

`GET /api/v1/datastreams/activity?pipelineId=...&pipelineId=...&days=60` answers what the named flows did, day by day
(400 for none or more than 200 flows; `days` is 1 to 180 as on the board):

| Field | What it holds |
| --- | --- |
| `windowDays`, `fromUtc`, `asOfUtc` | The window: `windowDays` days from `fromUtc`, a UTC midnight. |
| `flows` | Each flow that ran in the window, with its `days`: one entry per day it ran, oldest first. |
| `days[].day` | The day, counted from `fromUtc`. |
| `days[].runs`, `failed` | The runs that finished that day, and the failed among them. A run still queued or running is in neither. |
| `days[].rowsRead` | What the runs took in. |
| `days[].breakdown`, `rowsInserted`, `rowsUpdated`, `rowsDeleted` | What they did to their target, and whether any run reported it at all. |
| `days[].files`, `fileRows`, `fileBytes` | The files the runs took in, totalled. |
| `days[].seconds` | How long the runs ran. |
| `days[].counts` | The counts the runs' own results report, summed by name; null when none reported any. |

`GET /api/v1/datastreams/activity/fires?pipelineId=...&day=2026-01-31` answers one day's starts through the named
flows, newest first (at most fifty; `totalFires` counts them all): `key` (the group id, or the run id of a run on its
own), `groupId`, `startedUtc`, and `runs` in execution order, each the run as the runs list serves it (with
`resultCounts`), the number of `parts` a fan-out split it into, and the `files` it took in totalled (`files`, `rows`,
`bytes`, `newestModified`).

## See also

- [Reading the lineage graph](lineage-graph.md): the drawing the data view is shown on, and its controls.
- [Data stream detection](../concepts/data-stream-detection.md): the analysis behind every verdict.
