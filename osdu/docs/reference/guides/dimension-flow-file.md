---
id: delivery-guide-dimension-flow-file
title: "Writing a dimension flow file: the whole file from a built dimension, modelled on the estate's flows"
type: guide
summary: "Write the flow file, in the dimension builder: the assistant writes the whole flow on the estate's own, its tables where its data is, checked first."
keywords:
  - write the flow file
  - whole dimension flow file
  - dimension flow from the builder
  - delivery_dimension_flow
  - flow patterns
  - envelope
  - data database
  - dimension tables in the data database
  - check a dimension flow
  - flow-check
  - flow-patterns
related:
  - delivery-concept-explorer-dimension-builder
  - delivery-flow-dimension
  - delivery-flow-dimension-target
  - delivery-guide-dimensions
  - delivery-guide-mcp
sourceRefs:
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderFlowFile.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/flowFileQuestion.ts
  - osdu/gui/src/features/delivery/explorer/dimension/DimensionBuildPanel.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.Flow.cs
  - osdu/hosts/osdu-delivery-mcp/src/tools/dimensions.rs
---

# Writing a dimension flow file: the whole file from a built dimension, modelled on the estate's flows

The [dimension builder](../concepts/explorer-dimension-builder.md) writes one item of a flow's `dimensions:` list. A
[dimension flow](../flow/dimension.md) needs more around it: the OSDU source and its auth, the partitions it builds in,
its parameters, a schedule, and where its tables are kept. **Write the flow file** has the assistant write all of it,
the way the estate already writes its dimension flows, so the file is ready to add to a repository as it is.

## In the builder

**Write the flow file** sits in the builder's foot beside **Copy the YAML**, while the assistant is configured. It
waits until the YAML loads, and its tooltip says why while it waits. A click opens a conversation in the builder's
panel, under **Flow file** beside **Build**, **Preview** and **YAML**, asking for the whole file. The conversation is
followed up there, like any other, and is kept on the Assistant page after it is closed. The question names the item
as the builder wrote it and the partition it was built over. Asking again with the same YAML shows the same
conversation; once the YAML has changed, it asks anew. **Start over** drops it.

Nothing is saved by asking. The assistant shows the file in one YAML block. It can also propose it to the repository
when asked, through the change the person approves (`propose_pipelines`).

## What the assistant does

The steps live in the MCP server, the same in the GUI chat, VS Code or any client:

1. **Reads the patterns.** `delivery_dimension_flow` with no `yaml` answers the catalog's dimension flows. Each comes
   with its repository, file, batch, partitions, parameters, target, dimensions, the kinds they read, and its
   **envelope**: the document without its `dimensions` and `views`, comments and all. It also lists the connections the
   estate's flows read and write their data through, counted by flow kind, the module's own marked. Flows a sync saw
   last come first.
2. **Writes the file** on the nearest flow (the same repository, then the same kinds or batch). It takes the source,
   auth, partitions and parameters as written, references and all, never a value.
3. **Keeps the tables where the data is.** The flow's `target.connection` names the database the estate's `ing` and
   pre flows write, not the module's database beside the catalog
   ([Dimension target](../flow/dimension-target.md)). The dimension's own record stays in the module's database either
   way.
4. **Checks it** with `delivery_dimension_flow` and the `yaml`, until it is valid, before showing it.

## The check

A whole document is checked as a sync and a build would take it:

| Finding | Severity |
| --- | --- |
| The loader refuses the document (an unknown key, a literal secret, a bad path), in its own words | error |
| A dimension's template is not in a synced repository, or a read through it is refused, as the builder says | error or warning, as in the builder |
| Another flow's dimension writes, or would write, the same table (`dim_<name>`): a build is refused | error |
| A flow of another kind has the name | error |
| A dimension flow of the same name is in the catalog: the document replaces it | warning |
| A partition the registry does not hold: a run in it is refused until it is registered | warning |
| No target, or a target in the module's database: the connections the estate writes its data through are named | warning |
| `target.connection` is a reference no flow of the catalog names: a build must find it set | warning |

`valid` is true when the document loads and no finding is an error. The answer also lists each dimension's table
where the target keeps it (`dim.dim_Wellbore`), and `existing`, the catalog's flow of the same name.

## The API

Both routes read, and nothing is written. Both are open to an assistant run's delegated token
([the MCP server](mcp.md)).

| Route | Body | Answers |
| --- | --- | --- |
| `GET /api/v1/delivery/dimensions/flow-patterns` | | `flows` (at most 50, `moreFlows` the rest), `connections`, `moduleConnection` |
| `POST /api/v1/delivery/dimensions/flow-check` | `yaml`, `path` (the file it is meant for) | `loads`, `valid`, `name`, `target`, `dimensions`, `views`, `existing`, `issues` (`severity`, `message`, `dimension`, `target`) |
