---
id: guide-follow-a-data-stream
title: "Following one data stream end to end: what arrived, and what each flow did with it"
type: guide
summary: "A stream's own page: the path its data takes, the fires through it, what each flow's run did in one fire, and the API answer behind it."
keywords:
  - data streams
  - follow a stream
  - stream path
  - what did the vendor deliver
  - pre table
  - ingestion table
  - fire
  - run group
  - files delivered
  - where did the data stop
  - datastreams path
related:
  - concept-data-stream-detection
  - guide-lineage-graph
  - concept-lineage-graph-and-plan
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.Path.cs
  - src/SqlFlow.ControlPlane/Api/StreamPathWalk.cs
  - src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
  - gui/src/features/datastreams/StreamPathPage.tsx
  - gui/src/features/datastreams/StreamPathCanvas.tsx
  - gui/src/features/datastreams/StreamPathPanel.tsx
  - gui/src/features/datastreams/StreamFireStrip.tsx
  - gui/src/features/datastreams/streamPathModel.ts
  - gui/unit/streamPath.test.ts
  - tests/SqlFlow.ControlPlane.Tests/StreamPathWalkTests.cs
  - tests/SqlFlow.ControlPlane.Tests/StreamPathApiTests.cs
---

# Following one data stream end to end

The **Data streams** board answers "which tables stopped receiving data", one table per row, split into vendor
deliveries and our own processing. A source's data goes through several of those rows: a pre-ingestion flow lands the
vendor's files in a `pre` table, an ingestion flow keys them into the archive, and further flows carry them on. A
stream's own page puts that chain back together, so one question is answered in one place: what did the vendor
deliver, and what did each of our flows do with it.

Click a row on the board to open it, or **Open data stream** beside a verdict in the lineage graph's panel. The page's
address is `/datastreams/{pipelineId}`; the picker in its heading switches to any other stream the board checks, by
source, each with its verdict's glyph.

## What the page shows, top to bottom

**What is wrong, first.** One or two sentences, each naming the flow it is about (a click opens it on the canvas). The
latest fire's failure leads, with the first line of its error, because it is what is broken now; the worst verdict on
the path over the window follows, with how many more flows on it need a look. A healthy path says so in a quiet line.
A flow whose lineage was never computed says that its path is the flow alone until its repository syncs.

**The fires.** A fire is one start that ran the path's flows: a run group (a schedule's fire, a flow run with its
descendants), or a run started on its own. The strip lists them oldest on the left, one column per fire and one row
per flow, in the canvas's own top-to-bottom order. A filled dot is a run that moved data, a ring is a run that moved
none, red is a failure, a dash is no run. Read across, a row is one stage over time: a stage that stopped while the one
above kept delivering is the row that goes hollow. Click a column (or step with the arrow keys) to draw that fire on
the canvas; the newest is drawn when the page opens. The strip's heading says when the fire started, how its runs went,
what started it, and links the fire's own page; **Show older** reads the fires before the forty shown.

**The path.** The canvas draws the path top to bottom in the order the data moves, with the lineage graph's banded
layout: each flow in the band of the wave that runs it, what it writes under it, where the data enters above its first
reader. Every card says what happened in the fire picked:

| Card | Its second line |
|---|---|
| Where the data enters (a file location, an endpoint) | The files the reading run took in: `2 files · 15 rows · 150 B`. |
| A flow of SQLFlow's own kinds | What its run took in, then what it did to its table: `15 in · 3 new, 2 changed`, or `5 in · nothing changed`, and how long it took. |
| A flow of a kind a module adds | The kind's own counts, as a list of its runs shows them (a kind that counts records rather than rows). |
| A table or a view | What landed in it: `+3 ~2 -1`, or `no change`. |

A flow's corner wears its verdict over the window: quiet when healthy, in the status colour when not, with the finding
on hover. A `+N` on a card's top edge counts what the path stands for without drawing: inputs that reach a flow from
other paths, readers that write nothing (a check, a report), flows past the depth drawn; hover names them, and the
node's panel lists them all.

The lines are inked by whether data moved through them in that fire: a solid blue line carried data, a dashed one ran
and carried nothing, a red one failed, and a faint dotted one had no run. "How far did this delivery get" is answered
by where the blue line stops: in the example below the ingestion read five rows and changed none, so the line runs
into it and goes dashed after it.

```text
Shop/orders              1 file · 5 rows · 1.6 KiB
        │  (blue)
shop_orders_01_pre       5 in · 5 new
        │  (blue)
pre.Orders               +5
        │  (blue)
shop_orders_02_ing       5 in · nothing changed
        ┆  (dashed)
arc.Orders               no change
```

**A node's panel.** A click opens the node on the right of the canvas (Escape or the cross closes it). A flow's panel
has its run in the fire (outcome, when it started, how long it took, how many parts it split into, its counts, its
error whole with a copy button, and the files it took in), then its verdict over the window with the learned pattern,
the daily chart, the measures and every detector's reasoning: the same analysis the board runs. An object's panel says
which flow wrote it and which read it in the fire, and for where data enters, the files that arrived by name. Every
panel lists in full what its node stands for without drawing, and offers the node's lineage; a flow's panel also opens
the flow, its runs, and follows it as a stream of its own.

The fire and the node picked are kept in the address (`?fire=...&node=...`), so a link opens the same fire and node.
The window (30, 60 or 90 days) and **Include backfills in the verdicts** are the board's own preferences, shared with it
and with the lineage graph, so the three never judge one table over different spans.

## How the path is chosen

The path is the selected flow's lineage trace, cut down to the data's journey:

- **Upstream**, everything the flow reads is walked back to where it enters the estate: the flows that write it, and,
  where nothing does, the external input itself (a file location, an endpoint, a dataset nothing here writes). A flow
  reading back its own table is not an input.
- **Downstream**, only flows that write something are followed: a flow that only reads (a check, a report) ends the
  journey and is counted on the object it reads. A downstream flow's other inputs are counted on it, not walked, so a
  path does not grow into every source that meets this one further down.
- Every hand-over between two flows is drawn through one object: the producer's own target when the reader reads it
  (the same target the board names for that flow), otherwise the first object they share. A flow that writes a table
  and a view over it is one line, and what else it writes is counted on it.
- Fans are bounded at four flows drawn side by side, three external inputs per flow, and six flows each way from the
  selected one. What is cut is counted on the node it was cut from, by name, never dropped.

## The API

`GET /api/v1/datastreams/{pipelineId}/path?days=60&includeBackfills=false&before=...` answers one stream's path (404
for a flow the catalog does not hold; `days` is 1 to 180 as on the board):

| Field | What it holds |
|---|---|
| `nodes` | Each flow (`pipelineId`, `kind`, `wave`) and object (`objectKey`, `kind` as the lineage graph captions it, `qualifiedName`, `external`), with its `role` (`selected`, `upstream`, `downstream`) and `omitted`: what it stands for without drawing, as `{ what, count, names }` where `what` is `producers`, `inputs`, `outputs`, `readers`, `upstream` or `downstream` and `names` lists at most fifty. |
| `edges` | `{ from, to }` by node id: a flow writing an object, an object read by a flow. |
| `streams` | Each flow's verdict over the window, exactly as the board reports it (see [data stream detection](../concepts/data-stream-detection.md)). |
| `fires` | The latest forty fires, newest first: `key` (the group id, or the run id of a run on its own), `groupId`, `startedUtc`, and `runs` in execution order, each the run as the runs list serves it (with `resultCounts`), the number of `parts` a fan-out split it into, and the `files` it took in totalled (`files`, `rows`, `bytes`, `newestModified`). A fan-out's member runs are parts of their root, not runs of the fire. |
| `totalFires`, `olderThanUtc` | Every fire in the window, and the `before` that reads the fires older than these (null when there are none). |
| `lineageKnown` | False for a flow with no lineage at all, whose path is the flow alone. |

The fires list every run in the window, backfills included: they are part of what happened, and each run's summary
says how it was scoped. `includeBackfills` changes the verdicts only, as on the board.

## See also

- [Data stream detection](../concepts/data-stream-detection.md): the analysis behind every verdict on the path.
- [Reading the lineage graph](lineage-graph.md): the whole estate's lineage, and the trace this page cuts down.
