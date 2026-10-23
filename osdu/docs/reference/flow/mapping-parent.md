---
id: delivery-flow-mapping-parent
title: "Mapping $parent: a custom template's record reads sections of its canonical parent record from OSDU"
type: flow-reference
summary: "How a record of a linked custom template takes sections of its parent, the canonical record of the same minted id, read from OSDU as it renders."
keywords:
  - "$parent"
  - parent record
  - linked template
  - custom template
  - custom schema
  - canonical record
  - x-osdu-inheriting-from-kind
  - DDMSDatasets
  - ExtensionProperties
  - wdms
  - bulk references
  - curve files
  - same id other namespace
  - parent not in osdu
  - held for parent
  - parent version
related:
  - delivery-concept-templates
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-lookups
  - delivery-concept-preflight
  - delivery-concept-change-detection
  - delivery-concept-record-lifecycle
  - delivery-concept-lineage
  - delivery-flow-ddms
  - delivery-table-record
  - delivery-table-attempt
  - delivery-flow-mapping-child
yamlPath: "record.<property>.$parent (documentType: mapping)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateParent.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
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
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ParentDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ParentRenderTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ParentRecordReadTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ParentLedgerTests.cs
  - osdu/docs/census/keys.mapping.json
---

# Mapping $parent: a custom template's record reads sections of its canonical parent record from OSDU

OSDU's schemas do not always carry what a source knows, and a record OSDU's validation would refuse cannot be delivered
under the canonical kind. A **custom template** is the way around it: a full copy of the canonical schema under a kind of
its own, changed where the canonical one falls short, and filled by a mapping like any other template. A custom template
can be **linked** to the canonical kind it stands beside ([linked templates](../concepts/templates.md#linked-templates)),
and then every record it renders has a **parent**: the record of the canonical kind with the same minted id in the
canonical entity type's namespace. A `$parent` node reads a section of that record from OSDU as the record renders.

The case it was made for is a well log. The canonical WellLog record is delivered to the wellbore DDMS
([ddms](ddms.md)), which stores the curve values as bulk data and writes where they are into the record
(`data.DDMSDatasets`, `data.ExtensionProperties.wdms`). A custom log template cannot go through the DDMS, which accepts
only the kinds it serves, so its records go through storage; with `$parent` they take the DDMS references from the
canonical log of the same id, and a reader of the custom record finds the same curve files the canonical record points
at. Delivering the canonical and the custom record of each log from the same rows, the two ids differ only in their
entity type, so a reader moves from one to the other by changing it.

## An example

The custom template is a copy of WellLog 1.4.0 under the kind `welldb:wks:work-product-component--WellLogDetail:1.0.0`,
filed in a template folder like any other, with the pass type the well database records and the canonical schema has no
property for, and the link:

```json
{
  "x-osdu-schema-source": "welldb:wks:work-product-component--WellLogDetail:1.0.0",
  "x-osdu-inheriting-from-kind": [
    { "name": "WellLog", "kind": "osdu:wks:work-product-component--WellLog:1.4.0" }
  ]
}
```

Its mapping reads the same dataset with the same `system` and `key` as the canonical WellLog mapping, so each log gets
the same minted id in both namespaces, and takes the DDMS's sections from the parent:

```yaml
documentType: mapping
name: WellLogDetail
version: 1.0.0
template:
  kind: welldb:wks:work-product-component--WellLogDetail:1.0.0
dataset:
  system: welldb
  key: [source_project, log_id]
parameters:
  dataPartition: { required: true }
  aclOwner: { required: true }
  aclViewer: { required: true }
  legalTag: { required: true }
record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    Name: { $from: log_source }
    LogPassType: { $from: log_pass_type }
    # Where the wellbore DDMS put the curve values of the canonical log of the same id.
    DDMSDatasets: { $parent: data.DDMSDatasets }
    ExtensionProperties: { $parent: data.ExtensionProperties }
```

The flow delivering it is an ordinary delivery flow on the `storage` route ([routes](routes.md)), reading the canonical
flow's ingestion tables; lineage runs it after the flow delivering the parents ([lineage](#lineage)).

## The parent of a record

The parent is named by the record's own id: the same partition and the same unique part, in the entity type of the
kind the template is linked to. Under the default `idFrom: deliveryKey`, the custom record
`dev:work-product-component--WellLogDetail:5f0c...` has the parent `dev:work-product-component--WellLog:5f0c...`;
under `idFrom: key` the parent's id is made from the key's values the same way ([the OSDU id](mapping.md#the-osdu-id)).
The parent's id is therefore the canonical record's only when both mappings key the rows alike: the same `system`, the
same key columns in the same order, the same `idFrom`, and the same partition.

A record whose key is incomplete has no id, and no parent; it is held for its key as any record is. The template's
entity type must differ from its parent's, or a record and its parent would have the same id and the record would be
written over the parent: the [preflight](#what-the-preflight-checks) refuses such a mapping.

## Writing $parent

`$parent` names the path of a section of the parent from the record's root, its property names joined by dots:
`data.DDMSDatasets`, `data.ExtensionProperties.wdms`, `data.Name`. The section is taken whole: an object with everything
in it, a list with every item.

- It reads the record's parent, so it is a node of the record, not of a `$forEach` item: a row of a child dataset has
  no parent. To take a parent's array, put `$parent` on the array itself.
- A path does not step into an array (`data.Curves[].CurveID`, `data.Curves[0]`): name the array.
- It takes `$when`, `$required`, `$modifiers`, `$assert` and `$description` beside it, and is an alternative of a
  `$coalesce` like any value node:

```yaml
Description:
  $coalesce:
    - $from: log_comment
    - $parent: data.Description
TechnicalAssurances: { $parent: data.TechnicalAssurances, $required: false }
LogSource: { $parent: data.LogSource, $modifiers: [upper] }
```

- `$findBy`, `$findAll` and `$ignoreSeparators` select or match a record, and the parent is found by the record's id,
  so they are refused on it.

## What a $parent node gives

| The parent | The node |
| --- | --- |
| Holds the section | A copy of it, an object or a list as the parent holds it. A single value passes through the node's `$modifiers` as a column's value does; `$modifiers` on an object or a list hold the record. |
| Holds no such section, or an empty text | An empty value, which `$required` decides: held by default, the property left out with `$required: false`, or the next alternative of a `$coalesce`. |
| Is not in OSDU, or the flow's identity may not read it | The record is held, whatever `$required` says, because what it takes from the parent is unknown rather than absent: `data.DDMSDatasets: the record's parent dev:work-product-component--WellLog:5f0c... (osdu:wks:work-product-component--WellLog:1.4.0) is not in OSDU, or the flow's identity may not read it, and the record takes data.DDMSDatasets from it`. Nothing is sent, and it waits for its parent ([when the parent changes](#when-the-parent-changes)). |

The record is written as it renders: the parent's sections in the custom record are what the parent held when the
record rendered, and the ledger records which version that was ([what the ledger keeps](#what-the-ledger-keeps)).

## How a run reads the parent

The render does no I/O, as with a [search](mapping-lookups.md#how-a-run-asks). A record that reads its parent renders
unfinished the first time, naming the parent's id; the plan reads the parents of a batch from the storage service and
renders the records again. A mapping that reads its parent takes one more round of questions than its searches alone,
since a `$coalesce` reaches a `$parent` alternative only after the ones before it.

- **By id, from storage** (`POST /api/storage/v2/query/records`, or the path the flow's `protocolOptions.recordQueryPath`
  names), a hundred ids to a request and at most eight requests in flight, under the flow's own target, credentials and
  `data-partition-id`: a run reads only the parents its identity may read in the partition it delivers to.
- **Each parent once.** However many records of the run read a parent, and however many sections they take, it is read
  once, and kept trimmed to the sections the mapping reads. A run keeps at most 20,000 parents, letting the oldest go,
  since a record reads its parent in the round right after the one that read it.
- **Not there is an answer.** An id storage lists as invalid, or does not return, is a parent the flow cannot read: the
  record is held. A storage service that asks twice to retry, a failed request, or a record of another entity type under
  the parent's id fails the run instead, because holding records for a parent storage may hold would be holding them for
  a reason that is not true.
- **Any version of the kind.** A parent delivered since under a newer version of its kind (WellLog 1.5.0 under the same
  id) is read all the same: the id names the parent, and the sections are read by path.

`sqlflow preview` and `sqlflow values` read parents the same way, against the flow's target.

## What the ledger keeps

Each record of a linked template keeps the parent its render read ([`osdu.Record`](../tables/record.md)):

- `ParentId`: the parent's OSDU id, set when the record's render reads its parent, held or not, and indexed per
  partition so a landing finds the records that read it. A record whose render never reached a `$parent` node (a
  `$when` that did not apply, a `$coalesce` that found its value first) names none.
- `PendingParentVersion`: the version of the parent the queued document was rendered from, and `ParentVersion` the
  version the delivered document was rendered from, moved over when the document lands as its metadata hash is.
- Each try keeps the version its document was rendered from ([`osdu.Attempt`](../tables/attempt.md) `ParentVersion`),
  and a claim's event the same ([`osdu.RecordEvent`](../tables/record-event.md) `ClaimParentVersion`).

So the history of a custom record says, for each document sent, which version of the canonical record it was rendered
from.

## When the parent changes

A parent landing in OSDU through the ledger, delivered by any flow of the partition, reaches every record of the
partition that reads it:

- **A record held at its render** (its parent was not in OSDU, so it keeps no document) is released and planned again,
  under an activity of kind `release` whose actor is `parent`, naming the parents that landed. Its flow's next run renders
  it from the parent and sends it.
- **A record not blocked** (queued, waiting, being delivered or delivered) is asked to be planned again. Its flow's next
  run renders it from the parent's new version and sends it only when its document changed
  ([change detection](../concepts/change-detection.md)): a parent delivered again with the same DDMS references renders
  the same custom record, and nothing is sent.
- **A record held with its document kept** (a try failed, or an assertion held it) is held for something else, and a
  release would send the document as it stands, so it is left to its own hold.
- A deleted or reverted record is not reached.

The landings settled together reach at most 10,000 records; the rest follow their parent's next landing, or an
operator's release. A parent written outside the ledger (by another system, or by the DDMS writing bulk data without a
delivery) is not seen: a custom record renders again from it when something else renders it again (its row, its
mapping, its template, a redelivery).

## What the preflight checks

Before anything renders, `sqlflow check`, the mapping builder and every run refuse a mapping that reads `$parent`
when ([preflight](../concepts/preflight.md)):

- its template is linked to no parent, or names one that cannot be read (`x-osdu-inheriting-from-kind` that is not a
  list of `{ name, kind }` objects, a kind that is not a record kind, two record kinds);
- its template's entity type is its parent's;
- no template folder on the mapping's path holds the parent's kind (it is settled the way a kind named alone is), or
  its file cannot be read;
- a path is not a property of the parent's template; a path inside an object the parent's template leaves open (one
  declaring no properties, as `ExtensionProperties`) is accepted, since the parent may hold anything there;
- a section's shape cannot fill its property: an object where no object goes, a list where a single value or an object
  goes, a single value where an object goes. A single value fills a list of values as a list of one, and a property
  either template leaves untyped takes anything.

The repository sync warns of a template whose link cannot be read, or that is linked to its own entity type, naming the
file ([templates](../concepts/templates.md#linked-templates)).

## Lineage

A mapping that reads `$parent` reads the parent's kind, once, as the kind its template is linked to: the flow
delivering the parents to the partition runs before the flow delivering the custom records
([lineage](../concepts/lineage.md)). A mapping of a linked template that reads no section of the parent reads nothing
for the link.

## Errors when the mapping is read

| Written | Message |
| --- | --- |
| `$parent` under `$forEach` | `$parent reads the record's parent, which a row of curves has none of; read the whole array from the parent instead, with $parent on the array itself (as $parent: data.DDMSDatasets).` |
| `$parent: data.Curves[].CurveID` | `$parent 'data.Curves[].CurveID' steps into an array, and a section of the parent is read whole: name the array itself, as $parent: data.DDMSDatasets, and it is taken with every item.` |
| `$parent: DDMS Datasets` | `$parent names the path of a section of the record's parent from the record's root, its property names joined by dots, as $parent: data.DDMSDatasets or $parent: data.ExtensionProperties.wdms; 'DDMS Datasets' is not one.` |
| `$findBy` beside `$parent` | `$findBy selects the record a $cache or a $search node reads; this node reads its record's parent, which is found by the record's own id.` |

## Related

- [Mapping $child](mapping-child.md): the canonical record linking the custom record that reads it, the other direction.
- [Templates](../concepts/templates.md#linked-templates): custom templates, and how one is linked to its parent.
- [Mapping value nodes](mapping-values.md): `$required`, `$coalesce`, `$when` and the other words beside `$parent`.
- [Mapping lookups](mapping-lookups.md): `$search`, the other value read from the platform as a record renders.
- [The DDMS route](ddms.md): how the canonical log's curve values reach the wellbore DDMS.
- [Record lifecycle](../concepts/record-lifecycle.md): holds and releases.
