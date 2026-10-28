---
id: delivery-wiki-0023-child-links
title: "Decision 0023: A canonical record links its children as it renders, once OSDU holds them, and a child landing plans it again"
type: decision
summary: "Why a canonical record links its custom children with $child, written in an open property, looked up as it renders, never held for them."
keywords:
  - "$child"
  - child record
  - interlinked records
  - linked template
  - canonical record
  - ExtensionProperties
  - tags
  - ChildLinkKey
referenceRefs:
  - delivery-flow-mapping-child
  - delivery-flow-mapping-parent
  - delivery-concept-templates
  - delivery-concept-change-detection
  - delivery-concept-lineage
  - delivery-table-record
related:
  - delivery-wiki-0019-linked-templates
  - delivery-wiki-0002-client-supplied-ids
  - delivery-wiki-0009-searched-references
  - delivery-wiki-linked-canonical-and-custom-records
  - delivery-wiki-0035-enrich-chains
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Children.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildLedgerTests.cs
updated: 2026-10-27
---
# 0023: A canonical record links its children as it renders, once OSDU holds them, and a child landing plans it again

Status: proposed. Documented in [mapping $child](../../reference/flow/mapping-child.md). Builds on
[0019](0019-linked-templates.md): the custom record and its canonical parent, the same minted id in two namespaces.

## Context

[0019](0019-linked-templates.md) linked a custom record to its canonical parent: the custom template names the canonical
kind, its records read sections of the parent with `$parent`, and a mapping can name the parent in a property of its
own. The link ran one way. A reader holding the canonical record, which is the one every OSDU consumer finds first, had
nothing in it pointing at the custom record that carries what the canonical schema has no place for.

What was wanted was records that supplement each other and are linked both ways, the links made by the delivery rather
than by hand: optional, written only where the mapping says, and present as soon as both records exist. Three facts
shape it. The canonical template is OSDU's own schema and is not changed to carry the link. The custom record is
delivered after its parent, because it reads it. And a record's document is what change detection compares, so a link
added later has to change the document to reach OSDU.

## Decision

- **The mapping says where, the template says who.** A canonical mapping writes the link with a `$child: <kind>` value
  node on a property of its own choosing. The kind must be one whose template names the mapping's kind as its parent,
  the link [0019](0019-linked-templates.md) already reads, so the two directions rest on the one statement in the custom
  template. A mapping without `$child` is as it was.
- **The link goes where a canonical schema takes any value.** A property of `ExtensionProperties`, which OSDU's schemas
  leave open, or a tag. The preflight accepts a property that takes text without a format, and judges a declared
  relationship or pattern against a reference of the child's entity type.
- **The child is the record's own id in the child's namespace**, made from the key exactly as the child's render makes
  its parent's, and looked up in storage by id as the record renders, in the same rounds and requests as parents. The
  link is the reference with no version, so it names the child's latest version.
- **A missing child is no value, never a hold.** The property is left out and the record goes out. `$required: true`,
  `$modifiers` and `$coalesce` around it are refused, since each would either hold the parent for its child or write
  something that is not the link.
- **The link is part of the document**, so it is in the hash: a record rendered once its child landed is sent, and one
  rendered with its child still missing, or still there, is not.
- **A child landing plans the record again.** Each record whose render looked for children keeps the unique segment the
  children's ids share with its own (`ChildLinkKey`, indexed per partition). A record of any flow landing under an id
  with that segment in another entity type asks every record keeping it, not blocked, to be planned again; its next run
  renders the link and sends it because the document changed.
- **Lineage reads nothing for a link.** The canonical flow keeps running before the custom flow that reads it.

## Alternatives considered

- **A property for the link in the canonical template.** Not taken: the canonical templates are OSDU's schemas, kept as
  OSDU publishes them; a copy changed to carry the link would no longer be the canonical kind.
- **`LineageAssertions` or `Artefacts`.** OSDU's own relationship lists on a work product component. Not taken: OSDU's
  schema defines a lineage assertion as directed from the asserting record to its ancestors, never to its children, and
  an artefact's `ResourceID` takes only `dataset--` ids.
- **Write the child's id without looking.** The id is known from the key alone. Not taken: a canonical record whose
  custom record was never delivered, or was removed, would point at nothing, and the link would claim a record exists.
  Looking it up is what makes the link true.
- **Hold the canonical record until its child is there.** Not taken: the child reads its parent and is held until the
  parent lands, so each would wait for the other; and the canonical record is the one consumers need first.
- **Look the child up in the ledger.** Not taken, as for the parent in [0019](0019-linked-templates.md): the child may be
  delivered by another flow, another instance or another system, and storage answers exactly, at once, under the flow's
  own identity.
- **Find the records to plan again through the child's `ParentId`.** The child's row names its parent when it reads one.
  Not taken: a custom mapping that reads nothing of its parent names none, so its records would never be linked. The
  unique segment kept on the linking record finds it whatever the child reads, and one column serves any number of
  child kinds, since all of a record's children share it.
- **Link every linked template's records without the mapping asking.** Not taken: the link is optional, and a canonical
  schema has no fixed place for it, so only the mapping can say where it goes.
- **A lineage edge from the child's kind.** Not taken: the custom flow already runs after the canonical one because it
  reads it; the edge would make each wait for the other.

## Consequences

- A canonical record's first delivery goes out without the link when its child is delivered later, as the child of a
  custom template reading its parent always is. The link reaches OSDU on the canonical flow's run after the child lands.
- Adding the link sends the canonical record again, a new version in OSDU. On a route that keeps the payload apart, such
  as the wellbore DDMS with `payloadDetect: contentHash`, only the record is written: its bulk data is unchanged and is
  not sent again. The new version landing asks the custom records reading it to render again
  ([0019](0019-linked-templates.md)); they render the same and nothing is sent, so the two settle after one exchange.
- A record of another entity type that shares the unique segment without being a linked child (two mappings minting
  from the same system and key) asks the record to render again, and it renders the same: a render, not a write.
- A child taken out of OSDU, or written outside the ledger, does not plan its parent again; the link follows the next
  time something else renders the parent.
- Both mappings must key the rows alike, as [0019](0019-linked-templates.md) requires; the preflight cannot see the other
  mapping.
- A run linking children makes one more round of questions, and one storage request per hundred records, shared with
  the parents of the same round.
