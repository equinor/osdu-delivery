---
id: concept-described-lineage
title: "Described lineage: what other systems do, kept beside the lineage SQLFlow computes"
type: concept
summary: How lineage descriptions (flowType lin) extend lineage past the estate's own flows without touching waves or default views, and how to ask for them.
keywords:
  - described lineage
  - lineage description
  - flowtype lin
  - DescribedReads
  - DescribedWrites
  - upstream lineage
  - source system
  - waves unchanged
  - opt in
  - described objects
  - origin
  - open end
  - column mapping
  - assistant
related:
  - flow-lin
  - flow-lin-columns
  - guide-describe-an-upstream-step
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - guide-lineage-graph
  - cli-lineage
sourceRefs:
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Core/Lineage/LineageDescriptionFlow.cs
  - src/SqlFlow.Lineage/Graph/LineageDescriber.cs
  - src/SqlFlow.Lineage/LineageService.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Yaml/YamlLineageDescriptionFlowLoader.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DescribedLineage.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - tests/SqlFlow.ControlPlane.Tests/LineageDescribedApiTests.cs
  - tests/SqlFlow.Core.Tests/Integration/CatalogSyncDescribedLineageIntegrationTests.cs
  - gui/src/features/lineage/GraphControls.tsx
  - tools/sqlflow-mcp/src/server.rs
---

# Described lineage

SQLFlow computes lineage from the flows it runs: what each declares, what its runs did, what the database adds. That lineage ends where the estate ends: at the file a flow lands, and at the last table a flow writes.

A [lineage description](../flow/lin.md) (`flowType: lin`) says what happens beyond those ends, in a step another system runs. What descriptions say is the described lineage. It is an optional extension, and it is kept beside the computed lineage, never inside it.

## The rule: the computed lineage is the same with or without descriptions

Flows, objects, edges, relationships, dependencies, waves and cycles come out identical whether an estate holds descriptions or not. This is by construction, at three places:

| Where | How |
| --- | --- |
| The estate scan | A description contributes no fact, no server and no file drop. It is set aside for later. |
| The graph | The graph is built first, from the flows alone. The described lineage is placed afterwards by reading the finished graph, and is returned as a separate member of the report (`Described`). |
| The catalog | A described connection is stored with a relation of its own, `DescribedReads` or `DescribedWrites`. Everything that computes over `Reads` and `Writes` (waves, flow dependencies, object levels, data streams, run context) leaves it out without knowing of it. |

A description is stored as a pipeline row so its document has one home, but it is not a flow that runs:

- It has mode `disabled` and belongs to no wave (`-1`). The execution plan (`GET /repos/{id}/waves`) does not list it.
- Every run of it is refused before anything is queued: a trigger, the anchor of a node-scoped run, and a schedule that names it as a member all answer 400. A batch run leaves it out.
- The listings and counts of pipelines (`GET /pipelines`, its batches and projects, the dashboard's pipeline counts, the lineage graph's scope picker) leave it out. Asking for the kind lists it: `GET /pipelines?kind=lin`, or the kind filter of the Pipelines page. Search finds it by name.
- It never stands in a flow's place. A pipeline is identified by its name, so a description that has the name of a flow that runs is left out of the catalog with a finding, whichever of the two documents is read first: the flow keeps its pipeline, its wave, its schedule and its run history. A flow's document rewritten as a description under the same name leaves the plan: its wave becomes `-1`.
- It has no run history. A run folder is attributed to the flow of its name, never to a description.

## What a description adds

| Added | Where it is kept | Identity |
| --- | --- | --- |
| The step | A pipeline row of kind `lin`, with the document's text | Its name |
| A connection | A `catalog.LineageEdge` row, relation `DescribedReads` or `DescribedWrites`, tier `Declared` | The step and the object's key |
| An object only descriptions name | On the edges naming it (its name is on the edge). It has no row in the object registry. | `described:<system>`, then the name, case-folded |
| A column mapping | `catalog.LineageColumnMapping` ([column mappings](../flow/lin-columns.md)) | The step, the written object and the column |

An object the estate already holds is never duplicated. A description reaches it by naming the flow that reads it (`feeds`) or writes it (`fedBy`), and is connected to the node that flow declares.

Because a described object has no registry row, it appears in no object list, no catalog tree, no search of objects and no object level. Its key can never collide with a real object: no server reference starts with `described:`.

## Asking for it

Nothing returns described lineage unless it is asked to. Each surface has one way to ask:

| Surface | Default | With described lineage |
| --- | --- | --- |
| Lineage graph in the GUI | The estate's own flows and objects. | The **Described steps** switch, under **Other systems**. Offered only when a description touches what is drawn. |
| `GET /lineage/project-graph` | No description among the seeds, no described edge. `describedAvailable` says whether there is any. | `described=true`. The graph then also takes each description that writes an object it draws, with what that description reads, up the described chain, wherever the description is kept. Also included without asking when the graph is seeded on a description or a described object. |
| `GET /lineage/objects/graph` (the lineage walk) | Stops where the estate ends. `describedAvailable` says whether descriptions continue from there. | `described=true`. Each hop a description carries is marked `described`. |
| `GET /repos/{id}/lineage/edges` | Described edges left out. | `described=true`, or `relation=DescribedWrites`. |
| `GET /lineage/objects/dossier` | Described edges left out. | `described=true`. |
| `GET /lineage/described/steps` | (always described) | The described steps, with what each is connected to. |
| `GET /lineage/described/columns` | (always described) | [Column mappings](../flow/lin-columns.md). |
| `GET /lineage/objects/column-trace` | (always follows descriptions) | [One column followed back to its source](column-trace.md), through the estate's flows and the described steps, with what is missing said. |
| `sqlflow lineage` | The plan, then a `described lineage:` summary listing each step. | The summary is always printed; the plan above it never includes a description. |
| The assistant (MCP) | `object_lineage` walks the estate's lineage. | `object_lineage(described: true)`, `list_lineage_descriptions`, `column_mapping`, `trace_column`. |

## In the lineage graph

With the switch on, a description draws as a flow card captioned **Lineage description, never run here**, an object only descriptions name as a data node captioned with its system and **described**, and every arrow into or out of a described node dotted. Dotted means stated in a document, not run or observed.

A description has no wave. It is laid by what it feeds: above the first flow that reads what it writes, each arrow still running down the page. Bands that hold descriptions carry no wave label.

Nothing offers to run a description: its node menu has no run entries, and its pipeline page has no **Trigger run**.

A description's own page is its document. Its header says **never run here** and names the step: the system, where the step is found there, who to ask, and how many objects it reads and writes. It keeps the **YAML** and **Definition** tabs and has none of the tabs a run fills (runs, files, schedules, transforms).

A link onto a description opens the graph with the switch on: **View in lineage** on its page, a hit of **Find**, and the lineage jump of a search result all ask for the described lineage, since the step would not be in the drawing without it. A link that names a description without asking (one typed by hand, say) says the traced node is not in the scope and offers **Show described steps**.

## Where a described chain ends

Descriptions are optional and may cover part of a chain. So the end of a described chain is reported as a fact:

| Boundary | Meaning |
| --- | --- |
| `origin` | A description reads the object, none writes it, and a read marks it with `origin: true`: the data starts here. |
| `open` | A description reads the object, none writes it, and no read marks it: what produces it is not described. |

Neither is a warning. A lineage walk carries the boundary on the object, and `sqlflow lineage` prints `[origin]` or `[not described further]` beside it.

## Findings

A description that cannot be used in full is used in part, and the part left out is said. A finding is reported as `described lineage: <file>: lineage description '<name>' ...` by `sqlflow lineage` and by a catalog sync. It never fails a sync, never removes the step, and never changes the computed lineage. [The reference page](../flow/lin.md#when-a-connection-cannot-be-placed) lists them.

A description that does not load at all (a misspelt key, say) is a flow document that does not load: the scan reports it and continues, as for any flow.

## What the assistant does with it

The assistant's lineage walk says when descriptions continue beyond the estate (`describedAvailable`). Asked to follow them, it gets each described hop marked as such, the description behind it (its system, where the step lives there, what it does), and for each object only descriptions name whether the chain ends there. The step's document, read with `get_pipeline`, carries the `context`: what happens to the data in that step. `column_mapping` answers source-to-target questions.

A described hop is what a document states about another system. The assistant is told to report it that way.

Asked where one column comes from, the assistant uses `trace_column`: [the column trace](column-trace.md) follows the column through the estate's flows and the described steps, and ends every path it cannot follow with what is missing. The assistant is told to report that as missing, and never to fill it in.

## Known limits

- **A description meets the flows of its own repository.** `feeds` and `fedBy` name a flow the same repository declares.
- **Names are compared as written.** Two descriptions chain when they name the same system and name. A different spelling of one object is two objects.
- **Nothing checks a description against the system it describes.** `evidence` points at the code and its commit; SQLFlow fetches and compares nothing.
- **The lineage walk is bounded.** It follows at most 8 levels, described or not, and says when it was cut (`truncated`).
- **Column mappings stop at the estate's own flows.** A mapping is what a description states, and the estate's flows state none here. A column is followed from a landed file back to its source, through as many described steps as lie between. It is not followed across the flow that loads the file into a table, so a mapping asked from a report after the estate back to a source before it answers with nothing and says why. The object-level walk crosses both, and so does [the column trace](column-trace.md), one column at a time, saying which of its steps are only assumed.
- **The graph follows descriptions upstream only as far as they are needed.** With **Described steps** on, a description upstream of what is drawn is drawn with what it reads. What else that description writes, and what reads it, is not pulled in: the scope stays what was asked for plus where its data comes from.

## See also

- [Lineage description](../flow/lin.md)
- [Column mappings in a lineage description](../flow/lin-columns.md)
- [Column trace](column-trace.md): one column followed back to its source.
- [Describing a step another system runs](../guides/describe-an-upstream-step.md)
- [The lineage graph and the execution plan](lineage-graph-and-plan.md)
