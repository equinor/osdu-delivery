---
id: delivery-wiki-linked-canonical-and-custom-records
title: "Pattern: a custom record beside its canonical record, linked both ways and supplementing each other"
type: pattern
summary: "When OSDU's schema lacks what a source knows: a custom copy of the kind, linked to it, the two records reading and linking each other."
keywords:
  - linked records
  - interlinked records
  - custom template
  - canonical kind
  - custom schema
  - "$parent"
  - "$child"
  - link both ways
  - x-osdu-inheriting-from-kind
  - ExtensionProperties
referenceRefs:
  - delivery-guide-linked-records
  - delivery-flow-mapping-parent
  - delivery-flow-mapping-child
  - delivery-concept-templates
  - delivery-flow-ddms
  - delivery-flow-routes
related:
  - delivery-wiki-0019-linked-templates
  - delivery-wiki-0023-child-links
  - delivery-wiki-0018-template-folders
  - delivery-wiki-pattern-catalog
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateParent.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Engine/RouteChecks.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Children.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildDeliveryTests.cs
updated: 2026-10-23
---

# Pattern: a custom record beside its canonical record, linked both ways and supplementing each other

## The problem

A source records more about a record than the canonical OSDU schema has a place for, or values its validation would
refuse, and the canonical schema is OSDU's own, which the estate does not change. Delivering only a custom kind loses
what only the canonical route gives (a domain service such as the wellbore DDMS serves the canonical kinds alone, and
writes where it put the bulk data into the canonical record), and every consumer that reads the canonical kind. Delivering
only the canonical kind loses what the source knows.

## The shape

Deliver **both**, from the same ingestion rows, and **link them both ways**:

| Piece | What it does |
| --- | --- |
| A custom template | A full copy of the canonical kind under a kind and entity type of its own, adding what the source knows, naming the canonical kind in `x-osdu-inheriting-from-kind`: the link both directions rest on. |
| The custom mapping | Keys the rows exactly as the canonical mapping does, so a log's two records share their minted id; takes what only the canonical record holds with `$parent` and names it with `$parent: id`. |
| The canonical mapping | Links its custom record with `$child: <custom kind>`, in a property of `ExtensionProperties` or a tag, since its schema stays as OSDU publishes it. |
| The two flows | The canonical one on its own route; the custom one through storage, after it in lineage. |

The [guide](../../reference/guides/linked-records.md) builds it, every piece as code, and the reference pages say every
rule: [mapping $parent](../../reference/flow/mapping-parent.md), [mapping $child](../../reference/flow/mapping-child.md),
[linked templates](../../reference/concepts/templates.md#linked-templates).

## Why this shape

- **One minted id in two namespaces.** Ids are computed from the data ([0002](../decisions/0002-client-supplied-ids.md)),
  so a log's canonical and custom records differ only in their entity type: each finds the other by its own id, and no
  link is ever looked up or stored to be found ([0019](../decisions/0019-linked-templates.md)).
- **The template says who, the mappings say what and where.** The custom template's link is the one statement both
  directions rest on; the custom mapping says which sections it takes from its parent, and the canonical mapping where it
  writes the link back ([0023](../decisions/0023-child-links.md)).
- **Both links are read from OSDU as the records render**, so a link is written only to a record OSDU holds: the custom
  record waits for its parent, and the canonical record goes out without its child until the child lands. Waiting the
  other way too would leave each waiting for the other.
- **The links are part of the documents**, so change detection sends a record when its link first appears and never
  again for it: the two flows settle after one exchange.

## What to watch for

- **Key both mappings alike**: the same `dataset.system`, key columns in the same order, and `idFrom`. A canonical
  mapping minting under a system of its own (a second canonical version delivered beside the first, say) has ids of its
  own, so its records have no custom children, and its mapping links none.
- **The canonical flow's carried-over keys.** A route that carries `ExtensionProperties` over from OSDU into every update
  (`preserveDataKeys`) is refused when the mapping links there; the wellbore DDMS route needs only the dataset lists, and
  a flow that must carry the object over links through a tag.
- **The link arrives one canonical run after the custom record lands.** On schedules of their own, that is the canonical
  schedule's next run after the custom one; a canonical run by hand brings it sooner.
- **Register the custom kind** with the schema service, outside OSDU Delivery, before relying on search to find its
  records; storage takes them before, and the indexer flags each one `schema not found`.
- **A record taken out of OSDU** leaves the other's link until the other renders again; the ledger re-plans on landings
  only.

## Production exemplar

The production estate's well log folder delivers each log from one pair of ingestion tables (a header row per log, a row
per curve) three ways: as the canonical WellLog 1.4.0 through the wellbore DDMS, curve values and all; as WellLog 1.5.0
under a dataset system of its own, so with ids of its own and no custom children; and as a custom copy of WellLog 1.4.0
through storage that adds the unit each curve came with, as the source spells it, on the curve itself. The custom records
take their DDMS references and their parent's id from the canonical record of the same minted id, and the canonical
mapping links each log's custom record in a property of `ExtensionProperties`, beside the source's unit spellings it
keeps there. The canonical flow carries over only `Datasets` and `DDMSDatasets`. The custom flow is on a schedule of its
own an hour and a half after the canonical one's, kept disabled until its kind is registered with the schema service, so
its runs are asked for by hand.

The human ran the estate's flows with this shape on 2026-10-23 and confirmed the records linked both ways. The end-to-end
suite (`ChildDeliveryTests`) runs the same exchange over the sample estate: the canonical record delivered without its
child link, the custom record with its parent's references and id, the canonical record sent again with the link, and
nothing sent after.

## Related

- [The guide](../../reference/guides/linked-records.md): the shape built step by step.
- [0019](../decisions/0019-linked-templates.md) and [0023](../decisions/0023-child-links.md): why each direction is the way
  it is, and what was rejected.
- [Pattern catalog](../maps/pattern-catalog.md): the problem-first index.
