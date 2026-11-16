---
id: wiki-lineage-descriptions-beside-the-graph
title: "A lineage description is a flow type that is never run, kept beside the lineage SQLFlow computes"
type: decision
summary: "Why flowType lin is metadata only, why its edges carry relations of their own instead of a flag, why it takes no references, and why its meaning is prose."
keywords:
  - lineage description
  - flowType lin
  - described lineage
  - DescribedReads
  - DescribedWrites
  - metadata only
  - waves unchanged
  - no environment variables
  - context as prose
  - column mapping
  - optional extension
sourceRefs:
  - src/SqlFlow.Yaml/YamlLineageDescriptionFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Lineage/Graph/LineageDescriber.cs
  - src/SqlFlow.Lineage/LineageService.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/RunArtifactCollector.cs
  - gui/src/features/lineage/graphBands.ts
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/RunContextStore.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/KindEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/DescribedLineage.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - tests/SqlFlow.Core.Tests/LineageDescribedTests.cs
  - tests/SqlFlow.Core.Tests/Integration/CatalogSyncDescribedLineageIntegrationTests.cs
  - tests/SqlFlow.ControlPlane.Tests/LineageDescribedApiTests.cs
  - tests/SqlFlow.ControlPlane.Tests/LineageDescribedColumnTests.cs
referenceRefs:
  - flow-lin
  - flow-lin-columns
  - concept-described-lineage
  - guide-describe-an-upstream-step
  - concept-lineage-graph-and-plan
related:
  - wiki-schema-registration-flows
  - wiki-the-mcp-server-is-the-assistants-one-definition
updated: 2026-11-16
---

# A lineage description is a flow type that is never run, kept beside the lineage SQLFlow computes

A `flowType: lin` document describes a step another system runs, and what it says is kept as described lineage beside
the computed graph. What the document holds and how to ask for it are in
[Lineage description](../../reference/flow/lin.md) and
[Described lineage](../../reference/concepts/described-lineage.md). This page records why it has that shape.

## The gap it closed

Lineage ended at the file a flow lands. An estate that reads a drop-off zone another platform fills could say which flow
reads the zone and nothing about how the zone was formed. The assistant's upstream walk stopped there, so it could not
answer where a value came from or why a record was missing before the estate ever saw it. The human asked for a generic
flow type that extends lineage backwards, chains because one layer is not enough, and moves no data: a way to enrich
lineage and let an assistant understand the whole pipeline from the source.

## Metadata, not an execution model

The first design gave the kind an execution-shaped envelope: references for the parts of an object's identity that
differ between environments, parameters for a routine serving many sources, a list of the jobs running a step. The
human rejected all of it: the kind is pure metadata, it is never executed and supports no execution, and it needs no
environment variables. The references had been carried over from flows that run, where a location is written as a
reference so a run can resolve it. A description resolves nothing.

Two things followed. An object is named plainly, without what differs between environments, so one document describes
development and production alike. And a description meets the estate by naming the flow that reads its output
(`feeds`) or writes its input (`fedBy`): the estate's flow already holds the real location, so restating it would only
create a second spelling that has to match.

## Prose for meaning, structure for wiring

The first design also typed what a step does: operations with row effects, column derivations from a closed list, rules
with failure actions, write lifecycles. Matching it against two real pipelines forced nine additions in one pass,
which showed the model would grow with every platform. The human's framing settled it: the content is for an agent, and
the point is to connect objects and let the agent answer questions from one place.

So only the wiring is structured: the objects, what a step reads and writes, and pointers to the code. What happens to
the data is `context`, prose that nothing parses. Structure pays only where a machine joins or filters, and prose absorbs
the next platform's oddities without a change to the reader.

Column mappings came back as an optional level of detail, at the human's request and with their limit: SQLFlow's lineage
is object-level, the mapping is for the assistant, and it is not drawn. It kept the same economy: source columns and a
free-text note, no list of transformation types.

## Relations of their own, not a flag

The described lineage had to be told apart from the computed one, so it could be filtered away. Three ways were weighed:

- **A flag column on the edge.** Every reader would have to add the filter to leave descriptions out. The readers were
  counted first: run context, data streams, object levels, file flows, the project graph and several listings each
  compute over the relations `Reads` and `Writes`. One missed filter would have a description that never runs pass as
  the writer of a landed file, which is exactly what the human ruled out: the feature must not disturb the lineage
  computation.
- **A fourth tier.** Tiers are ranked to decide which observation wins, so a described tier would have competed with
  what SQLFlow ran.
- **Relations of their own** (chosen). `DescribedReads` and `DescribedWrites` are in the same table, so one walk can
  cross both, and every reader that asks for `Reads` or `Writes` leaves them out without knowing they exist. The
  schema registration flow's `Registers` relation is the precedent.

The same reasoning put the placement after the graph is built. The graph builder is unchanged: a description is, to it,
what a source-control snapshot already was, a pipeline outside the graph. A test computes one estate with and without
descriptions and compares the computed lineage byte for byte, and a catalog sync test compares the stored edges,
dependencies and waves row for row. Before the change was proposed, the same comparison was made on a production
estate of 119 flows, 712 edges and 11 waves with seven descriptions of the chain upstream of it: every member of the
report but the described lineage was unchanged.

## A kind every loader reads, but no registered kind

The kind uses the registered-document path, so every consumer of a flow document already handles it: the header
projection, the catalog sync, the offline check, the executor's parameter rule. It is not in the list of kinds a host
registered, because that list is what a client offers operations from, and a description has none. The loader's one
rule for run parameters refuses it, which covers a trigger, a schedule, a batch, the command line and the executor with
one sentence.

## A pipeline row, left out of what lists pipelines

Storing a description as a pipeline gave its document one home: the text, its history, its page, its place in search.
The relations kept it out of the lineage, but a pipeline row is also read by everything that lists or counts pipelines,
and the first run of the API test showed the leak: the execution plan listed the descriptions as a group of flows
lineage gave no wave. The human had asked for exactly the opposite, that waves leave the kind out and that it be an
extension one can filter away.

So the listings and counts of pipelines (the list, its batches and projects, the plan's waves, the graph's scopes, the
dashboard's counts) hold the flows that run, through one rule (`PipelineListings.WithoutLineageDescriptions`), and a
description is listed when it is asked for by its kind. The alternative, a table of its own for descriptions, would have
needed a second sync path, a second document page and a second search surface for the same YAML file.

The mode `disabled` was not enough to keep a description from being queued. A node-scoped run keeps its anchor whatever
its mode, and a schedule made through the API takes any active pipeline as a member; both would have queued a run that
fails at the node. They now ask the same rule the loader applies (`YamlDocumentLoader.NeverRunReason`) before anything
is queued.

## A description never stands in a flow's place

An independent review of the change, before it was proposed, found the one way a description could still alter the
plan: by its name. A pipeline is identified by its name and the first document in path order becomes it, so a
description named like a flow, in a folder read before the flow's, became that flow's pipeline: kind `lin`, mode
disabled, the flow's wave and edges stamped on a row that never runs, and the flow's run history attributed to a
document that has none. The described lineage had always reported such a description as left out; the flow set the
catalog is built from had not been told.

The estate scan now takes a description out of the flow set when it has the name of a flow that runs or of a
description read before it, the run history is attributed only to flows that run, and a document rewritten as a
description gives up its wave. While an estate holds a description left out this way, a sync computes lineage, so the
finding is reported until the document is renamed, although no pipeline's content changed.

The same review bounded what a caller can ask of the column level (a column made from many, each made from many again,
is followed within a budget and the answer says when it was cut), bounded a document as a whole (an anchor repeated
under every write no longer multiplies past the per-object limits), and made the reader refuse a credential in any
value and a `${...}` reference in any value that names something. A note is stored and served as written, so what the
reader accepts is all that keeps a secret out of an assistant's answer.

## The graph follows descriptions upstream

The project graph walks downstream from its seeds, which is right for flows: a scope is its flows and what they feed.
Descriptions mostly lie the other way. A description kept in another folder, or another repository, than the flow it
feeds was not drawn when the switch was on. The graph now takes each description that writes an object it draws, with
what that description reads, up the chain. It follows only descriptions, and only upwards, so switching the described
lineage on adds where the data comes from and never widens the scope sideways.

The first rendering on a real estate showed a second way the described lineage could disturb how the plan reads: the
layout gave a described step the first free band, so a chain of seven steps took bands one to seven, pushed the
estate's first wave down to the eighth, and the bands lost their wave names. The layout now computes the bands of the
flows that run as if no description were drawn, and gives each described step a band of its own between two of them, at
a half step: above wave 1 for what leads into the estate, right after the wave it reads for what only consumes. A
wave's band holds the same flows under the same name with the switch on or off.

## Objects only descriptions name stay out of the registry

Registering them would have put them in every object list, tree and search, each of which would then need a filter.
Their name is on the edges that name them, which is all a walk needs. The cost is that they are found through the
described steps and not through the object search.

## Open ends are facts

An earlier draft warned when a described chain did not reach a marked origin. The human's requirement that the kind be
an optional extension made that wrong: a team may describe one step and stop. The end of a chain is carried on the
object as `origin` or `open`, and reported to the reader as what is known.

## What it leaves open

- Nothing compares a description with the code it was written from. `evidence` carries a commit; checking it is left
  to review.
- `feeds` and `fedBy` name a flow of the same repository.
- The reader has no notion of a platform. What to write for a given one is guidance in
  [the guide](../../reference/guides/describe-an-upstream-step.md), not code.
