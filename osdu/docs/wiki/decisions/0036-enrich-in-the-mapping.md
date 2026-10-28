---
id: delivery-wiki-0036-enrich-in-the-mapping
title: "Decision 0036: what a record is enriched with is its mapping's to say, the flow binds the rows, and the ledger keeps it under the record"
type: decision
summary: "Why enrichments moved from the flow to the mapping, add records of any kind linked with $link, and are traced from the record they belong to."
keywords:
  - enrich
  - enrichment
  - enrichment mapping
  - $link
  - mapping is the recipe
  - records of any kind
  - traceability
  - Enrichments tab
updated: 2026-10-28
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Enrich.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichLinks.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichTrace.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordEnrichments.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs
referenceRefs:
  - delivery-flow-enrich
  - delivery-flow-enrich-records
  - delivery-flow-mapping-enrichment
  - delivery-flow-mapping
  - delivery-concept-ledger
related:
  - delivery-wiki-0035-enrich-chains
  - delivery-wiki-0004-preserved-keys
  - delivery-wiki-0023-child-links
---
# 0036: What a record is enriched with is its mapping's to say, the flow binds the rows, and the ledger keeps it under the record

Status: accepted. Amends [0035](0035-enrich-chains.md), which still holds for how a chain renders and is sent. Documented in
[enrichments](../../reference/flow/enrich.md), [records an enrichment adds](../../reference/flow/enrich-records.md) and
[enrichment mappings](../../reference/flow/mapping-enrichment.md).

## Context

[0035](0035-enrich-chains.md) put the steps that extend a record in the delivery flow (`enrich:`), each pinning a fragment,
with a step's files, the kind they registered as and the path the record linked them from (`files.link`) as flow keys. The
first estate to add a LAS file to its well logs showed what that cost, and the owner set three requirements:

- **The mapping is the recipe.** The mapping turns rows into the record; the flow says where data is and where records go,
  and a flow change redelivers nothing ([the mapping](../../reference/flow/mapping.md#parameters)). Steps in the flow put
  render decisions there: editing the flow re-rendered records, and two flows on one mapping version could render different
  records.
- **One clear way to add a record of another kind.** The dataset a step registered was rendered by "a fragment over the
  dataset kind", which was neither a fragment of the record nor the mapping of a record of its own, and could not say what
  it adds or where the record links it.
- **Generic, and fully traceable.** A step could only add datasets, link them as a list of ids at one path, and kept what it
  registered in the record's target state without its source rows; the record's history could not answer which row a dataset
  came from, nor could a dataset's id find its record.

## Decision

- **The mapping declares the enrichments** (`enrich:` in the mapping): each with its rows (`one`, `many` or none), its
  enrichment mapping, the key telling its records apart, the order it renders in, its hold and whether its row names the
  record's bulk data. The flow binds them: the enrichment's rows are the child dataset of its name
  (`source.datasets.<name>`), whose `files` say where each row's files are, and the workflow run over it is
  `workflows.<name>`. A flow `enrich:` key is refused, saying where its parts went.
- **An enrichment renders an enrichment mapping** (`enrichment: true`, which replaces `fragment: true`), and its kind says what
  the enrichment adds: the record's own kind adds paths to the record; any other kind adds a record per row (one per record
  without rows), a dataset registered with its files through the Dataset service when the flow binds them, any other record
  written through storage. Every added record carries the record's access list and legal block.
- **The record links what an enrichment adds with `$link: <name>`**, a value node of the mapping placed wherever the
  template takes the reference, in any list or as a single value, rather than a flow key naming one path.
- **The runtime joins the two** (`EnrichBinding`) when a run, a preview, a target operation, lineage or `sqlflow validate`
  opens the flow, and refuses what the mapping and the flow cannot do together, each message naming both.
- **The ledger keeps every enrichment in the record's own row**, which goes wherever the record goes: the render context names
  each enrichment and its mapping; the target state keeps where each enrichment's rows came from, and for each added record
  its row, hash and landed version; the record's attempts hold every write; each added record's id is one of the record's identity
  values (`added`), so a search for it finds the record. The record's Enrichments tab, `sqlflow records enrichments` and
  `GET .../records/{flowId}/{key}/enrichments` read it back.

## Alternatives considered

- **Keep the steps in the flow, and add `$link`.** Not taken: the render would still depend on the flow, against the rule that
  the mapping alone says what a record becomes.
- **A table of enrichments beside the record** (`osdu.RecordEnrichment`). Considered and not taken, for the reason 0035
  gave against a table of step contributions: every path that moves, purges, reverses or retires records would need it too,
  while the record's own row already goes everywhere the record goes. What the table would have held is held there instead,
  and the identity index gives the lookup by an added record's id.
- **Every write of an added record read from its artifacts.** Not taken: an artifact exists for an undo, and once a delivery
  completes, the ledger keeps only the ids that delivery minted, so a record's or a version's write leaves the list. Each
  write is a step of the attempt that made it, which the ledger keeps for every try.
- **An enrichment as a delivery of its own**, its records in a ledger of their own. Not taken, as in 0035: the record and
  what it links would land in two units of work, and a failure between them would leave a record linking what is not there.
- **Any kind written through storage, datasets included.** Not taken for a dataset whose rows name files: the Dataset service
  stores the files and answers retrieval instructions, which storage alone does not.
- **`$link` under `$forEach` and in items of lists of objects.** Not taken in this version: an enrichment set aside keeps the
  ids its last delivery added, which a row of today need not match. A list of values and a single value cover the references
  the canonical templates take, and the composition stays exact.

## Consequences

- A flow without enrichments, and a mapping without `enrich:`, render, hash and deliver exactly as before.
- Enrichments were unreleased when they moved, so no document is migrated: a flow written with `enrich:` is refused with what
  to write instead.
- The mapping builder does not lay out `enrich:` or `$link`, and refuses to open such a mapping rather than drop them on save;
  it is edited in its file.
- Every record an enrichment adds is traced from the record: its row, its versions, every write, its id in the lookup.
