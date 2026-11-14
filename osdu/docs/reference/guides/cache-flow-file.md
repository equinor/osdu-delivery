---
id: delivery-guide-cache-flow-file
title: "Writing a cache flow file: the whole file from a built type, modelled on the estate's flows"
type: guide
summary: "Write the flow file, in the cache builder: the assistant writes the whole cache flow on the estate's own, in the flow caching the type already, checked first."
keywords:
  - write the flow file
  - whole cache flow file
  - cache flow from the builder
  - delivery_flow_file
  - flowType cache
  - flow patterns
  - envelope
  - check a cache flow
  - cache flow-check
  - cache flow-patterns
  - type cached already
related:
  - delivery-concept-explorer-cache-builder
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-guide-dimension-flow-file
  - delivery-guide-mcp
sourceRefs:
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderFoot.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/flowFileQuestion.ts
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheFlowEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Cache.cs
  - osdu/hosts/osdu-delivery-mcp/src/tools/dimensions.rs
---

# Writing a cache flow file: the whole file from a built type, modelled on the estate's flows

The [cache builder](../concepts/explorer-cache-builder.md) writes one item of a flow's `types:` list. A
[cache flow](../flow/cache.md) needs more around it: the OSDU source and its auth, the partitions it fills, its
retention and a schedule. **Write the flow file** has the assistant write all of it, the way the estate already writes
its cache flows, so the file is ready to add to a repository as it is. A dimension's file is written the same way
([Writing a dimension flow file](dimension-flow-file.md)).

## In the builder

**Write the flow file** sits in the builder's foot beside **Copy the YAML**, while the assistant is configured. It
waits until the YAML loads, and its tooltip says why while it waits. A click opens a conversation asking for the whole
file in the assistant's panel, which slides in from the left over the records and leaves the builder in view: wide,
resizable, and maximized to read the file alone. The question names the item as the builder wrote it and the partition
it was built over, and asks where a flow caches a type of that name already.

Nothing is saved by asking. The assistant shows the file in one YAML block. It can also propose it to the repository
when asked, through the change the person approves (`propose_pipelines`).

## What the assistant does

The steps live in the MCP server, the same in the GUI chat, VS Code or any client:

1. **Reads the patterns.** `delivery_flow_file` with `flowType: cache` and no `yaml` answers the catalog's cache flows.
   Each comes with its repository, file, batch, partitions, parameters, the connection its table types read, the types
   it declares, and its **envelope**: the document without its `types`, comments and all. It also lists the connections
   the estate's flows read and write their data through. Flows a sync saw last come first.
2. **Decides where the type goes.** A partition's cache keeps one type of a name, filled by every flow declaring it
   ([the partition cache](../concepts/partition-cache.md)). When a flow caches the type already, the item belongs in
   that flow, its values added to the type's; otherwise it goes in the nearest flow's shape, as a new file.
3. **Writes the file**, taking the source, auth, partitions and retention as written, references and all, never a value.
4. **Checks it** with `delivery_flow_file` and the `yaml`, until it is valid, before showing it.

## The check

A whole document is checked as a sync and a refresh would take it:

| Finding | Severity |
| --- | --- |
| The loader refuses the document (an unknown key, a literal secret, a source the types do not use), in its own words | error |
| A flow of another kind has the name | error |
| A cache flow of the same name is in the catalog: the document replaces it | warning |
| A partition the registry does not hold: a run in it is refused until it is registered | warning |
| Another cache flow caches a type of the same name from another origin, or a value of it from another path, in a partition both surely fill: a refresh is refused | error |
| The same, where the two flows may fill a partition in common | warning |
| A dimension type names a dimension, or a dimension flow, the catalog does not hold | warning |
| `source.connection` of a flow with a table type is a reference no flow of the catalog names: a refresh must find it set | warning |

Two flows surely fill one partition when both name their partitions and the types are built for one in common, or when
both serve every registered partition. `valid` is true when the document loads and no finding is an error. The answer
also lists the `types` a refresh would cache, each with where it comes from, and `existing`, the catalog's flow of the
same name.

## The API

Both routes read, and nothing is written. Both are open to an assistant run's delegated token
([the MCP server](mcp.md)).

| Route | Body | Answers |
| --- | --- | --- |
| `GET /api/v1/delivery/cache/flow-patterns` | | `flows` (at most 50, `moreFlows` the rest), `connections` |
| `POST /api/v1/delivery/cache/flow-check` | `yaml`, `path` (the file it is meant for) | `loads`, `valid`, `name`, `partitions`, `types`, `existing`, `issues` (`severity`, `message`, `type`, `target`) |
