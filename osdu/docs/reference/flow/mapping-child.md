---
id: delivery-flow-mapping-child
title: "Mapping $child: a canonical record links the custom records that stand beside it, once OSDU holds them"
type: flow-reference
summary: "How a record links its children, the records of the custom templates linked to its kind with the same minted id, found in OSDU as it renders."
keywords:
  - "$child"
  - child record
  - linked template
  - custom template
  - canonical record
  - interlinked records
  - link to custom record
  - x-osdu-inheriting-from-kind
  - ExtensionProperties
  - tags
  - same id other namespace
  - child landed
  - plan again
related:
  - delivery-flow-mapping-parent
  - delivery-concept-templates
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-concept-preflight
  - delivery-concept-change-detection
  - delivery-concept-record-lifecycle
  - delivery-concept-lineage
  - delivery-table-record
yamlPath: "record.<property>.$child (documentType: mapping)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Rendering/IRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SearchFields.cs
  - osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Children.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildMappingTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildRenderTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildRecordReadTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildLedgerTests.cs
  - osdu/docs/census/keys.mapping.json
---

# Mapping $child: a canonical record links the custom records that stand beside it, once OSDU holds them

A custom template linked to a canonical kind ([linked templates](../concepts/templates.md#linked-templates)) gives each
of its records a parent: the canonical record with the same minted id, which the custom record reads with
[`$parent`](mapping-parent.md) and names in a link of its own. A **`$child`** node is the other direction: the canonical
record links its child, the record of the custom template that stands beside it, so a reader of either record finds the
other from the record it holds. The two records supplement each other and are linked both ways.

The link is optional: a canonical mapping writes it only where it declares a `$child` node, on a property its template has
room for. A canonical template is OSDU's own schema, which the estate does not change, so the link goes where that schema
takes any value: a property of `ExtensionProperties`, the object OSDU's schemas leave open, or a tag. A template of the
estate's own can declare a property for it. A flow whose route carries `ExtensionProperties` over from the record OSDU
holds (`protocolOptions.preserveDataKeys`, [routes](routes.md)) would put back what OSDU holds in place of the link, and
is refused when its mapping writes there: link through a tag, or carry over only the keys the route needs (the wellbore
DDMS route keeps its own `wdms` key in every update by itself, so `[Datasets, DDMSDatasets]` serves it).

## An example

The custom template `welldb:wks:work-product-component--WellLogDetail:1.0.0` is linked to WellLog 1.4.0 and its mapping
reads the canonical log's DDMS references with `$parent` ([the example there](mapping-parent.md#an-example)). The
canonical WellLog mapping links each log's detail record back:

```yaml
documentType: mapping
name: WellLog
version: 1.4.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
dataset:
  system: welldb
  key: [source_project, log_id]
record:
  data:
    Name: { $from: log_source }
    ExtensionProperties:
      WellDb:
        # The detail record of the same log, once OSDU holds it.
        WellLogDetailID: { $child: welldb:wks:work-product-component--WellLogDetail:1.0.0 }
  tags:
    WellLogDetail: { $child: welldb:wks:work-product-component--WellLogDetail:1.0.0 }
```

The first delivery of a log goes out without the link, since its detail record is delivered after it. Once the detail
record lands, the ledger asks for the WellLog record to be planned again; its flow's next run renders it with the link and
sends it, because its document changed ([when a child lands](#when-a-child-lands)).

## The child of a record

The child is named by the record's own id: the same partition and the same unique part, in the entity type of the kind the
node names. Under the default `idFrom: deliveryKey`, the canonical record `dev:work-product-component--WellLog:5f0c...`
has the child `dev:work-product-component--WellLogDetail:5f0c...`; under `idFrom: key` the child's id is made from the
key's values the same way ([the OSDU id](mapping.md#the-osdu-id)). This is exactly the id the child's own render gives its
parent, so the link is the canonical record's only when both mappings key the rows alike: the same `system`, the same key
columns in the same order, the same `idFrom`, and the same partition.

A record whose key is incomplete has no id, and looks for no child; it is held for its key as any record is.

## Writing $child

`$child` names the kind of the child it links, `authority:source:group--Entity:major.minor.patch`, a kind whose template
the folders on the mapping's path hold and that names the mapping's own kind as its parent.

- It links the record's child, so it is a node of the record, not of a `$forEach` item: a row of a child dataset has no
  children of its own.
- It takes `$when`, `$assert` and `$description` beside it. It never holds its record ([what a $child node
  gives](#what-a-child-node-gives)), so `$required: true` is refused and `$required: false` says what it does anyway.
- It writes OSDU's own id of the child, so `$modifiers` are refused, and so are `$findBy`, `$findAll`, `$ignoreSeparators`
  and `$unverified`, which find, match or build a value.
- It is never an alternative of `$coalesce`: a child not there gives nothing, and no other value stands in for a link.
  Several children are linked as the items of a list of values, each a `$child` node, and the list holds the links of the
  children OSDU holds:

```yaml
ExtensionProperties:
  Links:
    - { $child: welldb:wks:work-product-component--WellLogDetail:1.0.0 }
    - { $child: welldb:wks:work-product-component--WellLogQuality:1.0.0 }
```

## What a $child node gives

| The child | The node |
| --- | --- |
| Is in OSDU, and the flow's identity may read it | The reference to it, `<partition>:<group>--<Entity>:<unique>:`, with the version separator and no version, so the link names whichever version of the child is latest. |
| Is not in OSDU yet, was deleted, or the flow's identity may not read it | No value: the property is left out, and the record goes out without the link. Never a hold: the child is delivered after its parent and reads it, and a parent waiting for its child would wait for ever. |

The link is part of the document, rendered before the document is hashed: a record rendered once its child landed hashes
differently from the one OSDU holds and is sent, and a record rendered again with its child still missing, or still there,
hashes the same and is not ([change detection](../concepts/change-detection.md)). The record's history shows the document
each try sent, the link in it or not.

## How a run looks for a child

The render does no I/O, as with [a parent](mapping-parent.md#how-a-run-reads-the-parent). A record that links a child
renders unfinished the first time, naming the child's id; the plan looks the children of a batch up in the storage service
and renders the records again. A mapping that links its children takes one more round of questions than it would without.

- **By id, from storage** (`POST /api/storage/v2/query/records`, or the path the flow's `protocolOptions.recordQueryPath`
  names), a hundred ids to a request and at most eight requests in flight, under the flow's own target, credentials and
  `data-partition-id`, in the same requests as the parents the same round reads. Only whether storage hands the child over
  is kept, with its version.
- **Each child once.** However many nodes link a child, it is looked up once per run; a run keeps at most 20,000 answers
  about children, letting the oldest go.
- **Not there is an answer.** An id storage lists as invalid, or does not return, is a child the record goes out without. A
  storage service that asks twice to retry, a failed request, or a record of another entity type under the child's id fails
  the run instead, since writing a record without a link to a child storage may hold would be writing it wrong.

`sqlflow preview` and `sqlflow values` look children up the same way, against the flow's target.

## What the ledger keeps

Each record whose render looked for its children keeps the unique segment their ids share with the id the record's key
gives ([`osdu.Record`](../tables/record.md) `ChildLinkKey`), indexed per partition so a landing finds the records that
link it. It is set as the render is recorded, whatever it came to: work queued, a hold, and a render that sent nothing
because the document was the one OSDU holds, as a record rendered again under a mapping that now links its children is
while no child is there. A record whose render reached no `$child` node (a `$when` that did not apply, a mapping that links
none) keeps none.

## When a child lands

A record landing in OSDU through the ledger, delivered by any flow of the partition, under an id whose unique segment a
record of the partition keeps, in another entity type than that record's own, reaches the record:

- **A record not blocked** (queued, waiting, being delivered or delivered) is asked to be planned again. Its flow's next run
  renders it, finds the child, and sends it only when its document changed: a child landing for the first time adds the
  link, and one delivered again changes nothing.
- **A blocked record** is held for something else, since a child never holds its parent, and is left to its own hold; its
  next render looks for its children again.
- A record landing under the record's own id (its own delivery, or another instance delivering the same record) is not its
  child. A deleted or reverted record is not reached, and a record already asked keeps the request it had.

The landings settled together reach at most 10,000 records; the rest follow their child's next landing or their own next
render. A child written outside the ledger, or taken out of OSDU, does not re-plan the record: it is linked, or its link
dropped, the next time something else renders the record (its row, its mapping, its template, a redelivery).

## What the preflight checks

Before anything renders, `sqlflow check`, the mapping builder and every run refuse a mapping that links a child when
([preflight](../concepts/preflight.md)):

- the kind is of the mapping's own entity type, so a record and its child would share an id;
- no template folder on the mapping's path holds the kind (it is settled the way a kind named alone is), or its file cannot
  be read;
- the kind's template names no parent, names one that cannot be read, or names one of another entity type than the
  mapping's own, so the records it links are not this mapping's records' children;
- the property cannot take the reference: a type other than text, a text with a `format`, an object or a list of objects,
  or a relationship or pattern a reference of the child's entity type does not meet. A property of an object the template
  leaves open, as `ExtensionProperties`, and a tag take it.

## Lineage

A mapping that links its children reads none of them: it is written without the link while a child is not there, and the
ledger plans it again when one lands. So the flow delivering the canonical records still runs before the flow delivering
the custom ones that read them, and neither waits for the other ([lineage](../concepts/lineage.md)).

## Errors when the mapping is read

| Written | Message |
| --- | --- |
| `$child` under `$forEach` | `$child links one of the record's children, which are found by the record's own id, and a row of curves has none of its own; write the link outside the $forEach.` |
| `$child: WellLogDetail` | `$child names the kind of the child it links, 'authority:source:group--Entity:major.minor.patch', as $child: welldb:wks:work-product-component--WellLogDetail:1.0.0; 'WellLogDetail' is not one.` |
| `$child: osdu:wks:AbstractWPCGroupType:1.2.0` | `$child 'osdu:wks:AbstractWPCGroupType:1.2.0' names an abstract schema, which no record is of; name the kind of the custom template linked to this one, ...` |
| `$required: true` beside `$child` | `a $child node never holds its record: the link is written once OSDU holds the child, and left out until then, since a parent waiting for a child that waits for its parent would wait for ever; remove $required.` |
| `$modifiers` beside `$child` | `$modifiers change a value the row gives, and a $child node writes the reference to the record's child as OSDU names it, with the version separator and no version; remove $modifiers.` |
| `$child` as an alternative of `$coalesce` | `a $child node links one of the record's children, and gives nothing while OSDU does not hold it, which no other value stands in for; write it as a node of its own, or, to link several children, as the items of a list.` |

## Related

- [Mapping $parent](mapping-parent.md): the custom record reading sections of its canonical parent, the other direction.
- [Templates](../concepts/templates.md#linked-templates): custom templates, and how one is linked to its parent.
- [Mapping value nodes](mapping-values.md): `$when`, `$assert` and the other words beside `$child`.
- [Record lifecycle](../concepts/record-lifecycle.md): holds and requests to plan a record again.
