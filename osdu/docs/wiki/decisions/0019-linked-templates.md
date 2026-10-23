---
id: delivery-wiki-0019-linked-templates
title: "Decision 0019: A custom template is linked to its canonical kind, and its records read their parent from OSDU"
type: decision
summary: "Why a custom template names its canonical parent kind, why a record's parent is the same minted id, and why it is read from storage as it renders."
keywords:
  - linked template
  - custom template
  - parent record
  - "$parent"
  - x-osdu-inheriting-from-kind
  - DDMSDatasets
  - same id other namespace
  - custom schema
referenceRefs:
  - delivery-flow-mapping-parent
  - delivery-concept-templates
  - delivery-flow-ddms
  - delivery-concept-change-detection
  - delivery-concept-record-lifecycle
  - delivery-table-record
related:
  - delivery-wiki-0018-template-folders
  - delivery-wiki-0009-searched-references
  - delivery-wiki-0004-preserved-keys
  - delivery-wiki-0002-client-supplied-ids
  - delivery-wiki-0023-child-links
  - delivery-wiki-linked-canonical-and-custom-records
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateParent.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ParentDeliveryTests.cs
updated: 2026-10-23
---
# 0019: A custom template is linked to its canonical kind, and its records read their parent from OSDU

Status: proposed. Documented in [mapping $parent](../../reference/flow/mapping-parent.md) and
[templates](../../reference/concepts/templates.md#linked-templates). Builds on [0018](0018-template-folders.md): a
custom template is a file in a template folder like any other.

## Context

OSDU's schemas do not always carry what a source knows. A well database records things about a log (its pass type, the
unit each curve was measured in) that the canonical WellLog schema has no property for, and some values it holds would
not pass the canonical schema's validation. A template of one's own already solved the shape: a full copy of the
canonical schema under a kind of its own, changed where the canonical one falls short, filled by a mapping like any
other ([0018](0018-template-folders.md)).

What the copy could not have is what only the canonical route produces. The wellbore DDMS serves the canonical kinds
alone: it stores a log's curve values as bulk data and writes where it put them into the canonical record
(`data.DDMSDatasets`, `data.ExtensionProperties.wdms`). A custom log goes through storage, so on its own it points at no
curve files, and the curve values are the most valuable part of a log.

What was wanted was both: the canonical record, which every OSDU consumer understands and the DDMS serves, and beside it
a custom record that carries every detail the source has and works on its own, pointing at the same curve files; and a
reader able to move from one to the other without a lookup.

## Decision

- **The link is the template's.** A custom template names the canonical kind its records stand beside in
  `x-osdu-inheriting-from-kind`, the key the OSDU data definitions already use for a kind's parents. The abstract schemas
  the data definitions list there name no record and are passed over; the one record kind listed is the parent, and a
  template linked to none is an ordinary template.
- **A record's parent is the same minted id in the parent's namespace.** Ids are computed from the data
  ([0002](0002-client-supplied-ids.md)), so the custom template takes an entity type of its own, so its records' ids differ from the canonical ones only in that segment: `...--WellLogDetail:<minted>` has the
  parent `...--WellLog:<minted>`. Moving between the two records is changing the entity type; nothing has to be looked up.
  A template linked to its own entity type is refused where a mapping reads its parent, since a record would be written
  over its parent.
- **The mapping decides what to take.** A `$parent: <path>` node copies a section of the parent whole, as the parent
  holds it, and is a value node like any other (`$coalesce`, `$required`, `$when`, `$assert`, `$modifiers` on a single
  value). It is a node of the record, never of a `$forEach` item, since a child row has no parent.
- **The parent is read from storage by id as the record renders**, in the same rounds a search is asked in: the render
  stops, the plan reads the batch's parents a hundred to a request, and renders again. Each parent is read once per run
  and kept trimmed to the sections the mapping reads.
- **A parent OSDU does not hold holds the record**, whatever `$required` says: what the record takes from it is unknown,
  not absent.
- **The ledger keeps the link.** Each record keeps its parent's id and the version of it each document was rendered from
  (the record's, the queued document's, each try's). When a record of any flow of the partition lands under a parent's
  id, the records reading it are planned again: one held at render because the parent was missing is released under an
  intervention the trail shows (actor `parent`), one not blocked is asked to be planned again and sent only if it renders
  differently, and one held with its document kept is left to its own hold.
- **Lineage orders the custom flow after the canonical one**, since its mapping reads the parent's kind.

## Alternatives considered

- **The link in the mapping document.** Weighed, since it is the mapping that renders the record. Not taken: the link
  is a fact about the custom kind (it stands beside one canonical kind), true for every mapping that fills it, and OSDU's
  own schemas already state it in the template. The split taken is that the template says which kind is the parent and the
  mapping says what to take from it.
- **Copy the sections from the canonical flow's ingestion rows or its ledger.** Not possible for the case that matters:
  the DDMS references are written by the DDMS when it stores the bulk data (`ExtensionProperties.wdms.bulkURI` and the
  `urn://wdms-1/uuid:` entries of `DDMSDatasets`), not by the canonical mapping, and the canonical route carries them over
  from what OSDU holds rather than rendering them ([0004](0004-preserved-keys.md)). OSDU is the only place they exist.
- **Find the parent with the search service.** The id is known exactly, and the search index is eventually consistent:
  a parent landed a moment ago may not be found. A storage read by id answers at once and exactly, and a parent the flow
  may not read is not handed over, which is the answer the hold needs.
- **A reference property naming the parent, or another unique part.** Either would need the parent's id to be found or
  stored somewhere and would make moving between the records a lookup. The same minted value in two namespaces needs
  neither.
- **A missing parent as an empty value.** A record sent without its parent's references would go out incomplete for a
  reason that is temporary; holding it, and releasing it when the parent lands, sends it once and complete.

## Consequences

- A custom kind is registered with OSDU's schema service like any template of one's own, outside OSDU Delivery; the link
  is read by OSDU Delivery alone.
- Both mappings must key the rows alike (the same `system`, key columns and `idFrom`), or the parent's id is not the
  canonical record's; the preflight cannot see the other mapping, so this is the estate's to keep.
- A parent written outside the ledger (by another system, or by the DDMS without a delivery) does not re-plan the custom
  records; they follow it the next time something else renders them.
- A run reading parents makes one more round of questions than its searches alone, and one storage request per hundred
  parents.
- The record page links a record and its parent both ways, and the mapping builder offers the parent as an input,
  defaulting to the variable's own path, since a custom template is a copy of its parent.
- The link in OSDU ran one way, from the custom record to its parent. [0023](0023-child-links.md) adds the other: the
  canonical record links its custom child with `$child`, resting on the same template link.
