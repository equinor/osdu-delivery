---
id: delivery-wiki-0003-rendering-location
title: "Decision 0003: rendering runs in the delivery service"
type: decision
summary: "Why the mapping is interpreted by the delivery against a pinned template and cache version, not by a facade service."
keywords:
  - "rendering"
  - "mapping renderer"
  - "offline plan"
  - "content hash"
  - "facade"
  - "recordpath"
referenceRefs:
  - delivery-concept-templates
  - delivery-concept-change-detection
related:
  - delivery-wiki-0009-searched-references
  - delivery-wiki-0018-template-folders
updated: 2026-10-21
---
# 0003: Rendering runs in the delivery service

Status: proposed. Documented in [templates](../../reference/concepts/templates.md) and [architecture](../../reference/concepts/architecture.md).

> **Amended by [0018](0018-template-folders.md).** The decision stands: the delivery renders the mapping against a
> template and a cache version. The template is no longer read from the catalog: it is the version the template folders
> on the mapping's path hold, and the catalog's saved templates only assist.

## Context

The mapping could be interpreted in a facade service in front of the DDMS (which compiled it before this
system existed) or in the delivery service.
Either works; the pinned inputs are direction-neutral. The choice decides whether the translate renderer is
vendored and whether the facade keeps a mapping at all.

## Decision

The delivery service interprets the mapping (`MappingRenderer`) against its pinned template (the OSDU
schema, saved in the catalog) and a version of the cache (also in the catalog), and sends finished OSDU records. A facade, when used, is a pass-through for the record and the
bulk data; its generated mappers, endpoints and PySpark selects are no longer a contract this system depends
on.

## Consequences

- One interpreted mapping, one place, no generator drift.
- The content hash is computable offline, which is what makes `plan` work without a network and what makes
  change detection honest. A mapping that searches the platform for the records it refers to needs the platform's
  search to render ([0009](0009-searched-references.md)); one that searches nothing still plans offline.
- The reference cache moves out of the facade's per-replica memory into a versioned cache in the catalog, whose
  version the render context records.
- A facade's routes are reached through `protocolOptions` path overrides (`recordPath: /welllogs` and so
  on). If the facade should instead keep rendering, the protocol would post a source-shaped request and the
  service would lose offline planning; that is a regression this decision deliberately avoids.
