---
id: delivery-guide-linked-records
title: "Delivering a custom record beside its canonical record, linked both ways: a linked template, $parent and $child"
type: guide
summary: "Copy a canonical kind into a custom template linked to it, deliver both records from the same rows, and link them both ways with $parent and $child."
keywords:
  - linked records
  - interlinked records
  - custom template
  - canonical kind
  - custom schema
  - link both ways
  - parent and child
  - "$parent"
  - "$child"
  - x-osdu-inheriting-from-kind
  - ExtensionProperties link
  - tag link
  - custom well log
  - DDMSDatasets
  - same minted id
related:
  - delivery-flow-mapping-parent
  - delivery-flow-mapping-child
  - delivery-concept-templates
  - delivery-flow-mapping
  - delivery-flow-routes
  - delivery-flow-ddms
  - delivery-concept-lineage
  - delivery-concept-change-detection
  - delivery-cli-check
  - delivery-cli-records
  - delivery-guide-writing-a-mapping
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateParent.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/RouteChecks.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Children.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ParentDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ChildDeliveryTests.cs
---

# Delivering a custom record beside its canonical record, linked both ways: a linked template, $parent and $child

A source often knows more about a record than the canonical OSDU schema has room for: a well log's pass type, the unit
each curve was measured in. The canonical schema is OSDU's and stays as OSDU publishes it, and some of what matters is
only in the canonical record (the wellbore DDMS stores a log's curve values and writes where it put them into the
canonical WellLog record alone). The shape that serves both is **two records of each log**, delivered from the same rows:

- the **canonical record**, of the OSDU kind, which every OSDU consumer reads and the domain service serves;
- a **custom record** beside it, of a custom template that copies the canonical kind and adds what it lacks;

and the two **linked both ways**: the custom record names its canonical **parent** and takes from it what only it holds,
and the canonical record links its custom **child** once the child is in OSDU. This guide builds it for well logs, with
the well database's `welldb` tables, the canonical WellLog 1.4.0 delivered through the wellbore DDMS, and a custom
`WellLogDetail` delivered through storage. Nothing here changes the canonical template.

| Piece | Says | Page |
| --- | --- | --- |
| The custom template | Its own kind and entity type, the canonical kind as its parent (`x-osdu-inheriting-from-kind`), the properties it adds, a property naming its parent | [templates](../concepts/templates.md#linked-templates) |
| The custom mapping | The canonical mapping's dataset and key, the sections it takes from its parent (`$parent`), the link to it | [mapping $parent](../flow/mapping-parent.md) |
| The canonical mapping | The link to its child (`$child`), in `ExtensionProperties` or a tag | [mapping $child](../flow/mapping-child.md) |
| The canonical flow | Its route (the wellbore DDMS here), carrying over only what that route needs | [the DDMS route](../flow/ddms.md) |
| The custom flow | The storage route, over the same ingestion tables, run after the canonical flow | [routes](../flow/routes.md) |

## 1. The custom template

Copy the canonical template's file into the template folder the canonical one is in, and change three things in it:

1. **Its own kind**, under an authority of the estate's own and an **entity type of its own**: the entity type is the
   namespace a record's id is minted in, so a custom record never shares an id with its canonical one.
2. **The link to the canonical kind**, the key OSDU's data definitions name a kind's parents with. Keep the abstract
   schemas the data definitions list there; the one record kind is the parent.
3. **What the canonical kind lacks**: the source's own properties, and a property naming the parent, a relationship to
   the canonical entity type.

```json
{
  "$id": "https://schema.example.org/json/work-product-component/WellLogDetail.1.0.0.json",
  "x-osdu-schema-source": "welldb:wks:work-product-component--WellLogDetail:1.0.0",
  "title": "WellLogDetail",
  "x-osdu-inheriting-from-kind": [
    { "name": "WorkProductComponent", "kind": "osdu:wks:AbstractWPCGroupType:1.2.0" },
    { "name": "WellLog", "kind": "osdu:wks:work-product-component--WellLog:1.4.0" }
  ],
  "properties": {
    "data": {
      "allOf": [
        "... the canonical template's parts, as they are ...",
        {
          "type": "object",
          "properties": {
            "LogPassType": { "type": "string", "description": "The pass type the well database records for the log." },
            "ParentWellLogID": {
              "type": "string",
              "description": "The WellLog record this record stands beside: its parent, with the same minted id.",
              "pattern": "^[\\w\\-\\.]+:work-product-component\\-\\-WellLog:[\\w\\-\\.\\:\\%]+:[0-9]*$",
              "x-osdu-relationship": [ { "GroupType": "work-product-component", "EntityType": "WellLog" } ]
            }
          }
        }
      ]
    }
  }
}
```

Name the file after its kind, each colon an underscore (`welldb_wks_work-product-component--WellLogDetail_1.0.0.json`).
The sync records its parent and the Templates page shows it; the canonical template's sheet names `WellLogDetail` among
its **children** ([templates](../concepts/templates.md#linked-templates)).

Register the custom kind with OSDU's schema service, outside OSDU Delivery. Storage accepts records of a kind the schema
service does not hold, but its indexer flags each of them `schema not found`, so search finds them only once the kind is
registered.

## 2. The custom mapping: the parent's sections, and the link to it

The custom mapping keys the rows **exactly as the canonical mapping does**: the same `dataset.system`, the same key
columns in the same order, the same `idFrom`. That is what makes a log's two records the same minted id in two entity
types (`dev:work-product-component--WellLog:5f0c...` and `dev:work-product-component--WellLogDetail:5f0c...`), so each
finds the other by its own id, with nothing looked up. Copy the canonical mapping, then change its name, its template,
and what it adds:

```yaml
documentType: mapping
name: WellLogDetail
version: 1.0.0
# The kind alone: the one version the template folder holds is filled.
template:
  kind: welldb:wks:work-product-component--WellLogDetail:1.0.0
# The canonical mapping's system and key, unchanged, or the parent is not the canonical record.
dataset:
  system: welldb
  key: [source_project, log_id]
record:
  # ... acl, legal, tags and every property as the canonical mapping writes them ...
  data:
    LogPassType: { $from: log_pass_type }
    # The link to the parent: its id as OSDU holds it; ref adds the version separator and no version, so the
    # reference names whichever version of the parent is latest.
    ParentWellLogID:
      $parent: id
      $modifiers: [ref]
    # Where the wellbore DDMS put the log's curve values, taken from the parent as it renders, so both records point at
    # the same curve files. A log without curve values has neither; a parent OSDU does not hold holds the record.
    DDMSDatasets:
      $parent: data.DDMSDatasets
      $required: false
    ExtensionProperties:
      wdms:
        $parent: data.ExtensionProperties.wdms
        $required: false
```

A `$parent` node is read from storage by the record's own id in the parent's entity type, as the record renders. A custom
record whose parent is not in OSDU yet is held, naming the parent, and is released and sent once the parent lands
([mapping $parent](../flow/mapping-parent.md)).

## 3. The canonical mapping: the link to the child

The canonical template has no property for the link and is not changed, so the link goes where an OSDU schema takes any
value: a property of `ExtensionProperties`, or a tag. Add one `$child` node, naming the custom kind, to the canonical
mapping:

```yaml
record:
  tags:
    # Either here, as a tag ...
    WellLogDetail: { $child: welldb:wks:work-product-component--WellLogDetail:1.0.0 }
  data:
    # ... or here, in the open object, beside whatever else the mapping keeps there.
    ExtensionProperties:
      WellDb:
        WellLogDetailID: { $child: welldb:wks:work-product-component--WellLogDetail:1.0.0 }
```

Each render looks the child up in storage by the record's own id in the child's entity type. Once storage holds it, the
reference is written, with the version separator and no version (`dev:work-product-component--WellLogDetail:5f0c...:`);
until then the property is left out and the record goes out without it, never held for it.

- Write nothing else beside it but `$when`, `$assert` and `$description`: it is never required and takes no modifiers.
- Keep it outside a `$forEach` and out of `$coalesce`. Several custom kinds are linked as the items of a list:
  `Links: [ { $child: <kind> }, { $child: <other kind> } ]`.
- Name a kind whose template is linked to the canonical kind: the preflight refuses any other
  ([what the preflight checks](../flow/mapping-child.md#what-the-preflight-checks)).

## 4. The two flows

**The canonical flow** is the one that delivers the canonical kind already (`flows/welldb-welllog-03-header-delivery.yaml`
in the sample estate), its mapping now linking the child. One setting matters: a flow that carries `ExtensionProperties`
over from the record OSDU holds (`protocolOptions.preserveDataKeys`) would put back what OSDU holds in place of the link,
and is refused when its mapping links there. The wellbore DDMS route keeps its own `wdms` key in every update by itself,
so carry over the dataset lists alone:

```yaml
target:
  protocol: ddms
  protocolOptions:
    # Not ExtensionProperties: the mapping writes the link there.
    preserveDataKeys: [Datasets, DDMSDatasets]
```

A flow that has to carry `ExtensionProperties` over links through a tag instead.

**The custom flow** reads the same ingestion tables and delivers through storage: the record alone, since the curve
values are the parent's. Its work folder is its own.

```yaml
flowType: delivery
name: welldb-welllogdetail-03-delivery
batch: welldb
source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [source_project, log_id]
    primaryKey: RecId
  lastModified: update_date
  work: ../.work/welllogdetail
render:
  mapping: WellLogDetail@1.0.0
change:
  detect: renderedHash
  onUnchanged: skip
target:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
  # Storage: the record alone. Its parents are read through the same endpoint and credentials, so the flow's identity
  # must be able to read the canonical records.
  protocol: storage
```

Lineage orders the custom flow after the canonical one, since its mapping reads its parent's kind; the canonical
mapping's `$child` reads nothing, so the two never wait for each other ([lineage](../concepts/lineage.md)). In one
schedule the canonical flow runs first; on schedules of their own, time the custom one after the canonical one.

## 5. Check both before anything runs

```bash
sqlflow check flows/welldb-welllog-03-header-delivery.yaml
sqlflow check flows/welldb-welllogdetail-03-delivery.yaml
```

| `check` says | Fix |
| --- | --- |
| `... links the record's child of <kind>, and that template is linked to no parent, so its records are no record's children; ...` | The custom template's `x-osdu-inheriting-from-kind` (step 1). |
| `... links the record's child of <kind>, and that template cannot be read: ...` | The custom template's file is not in a template folder on the canonical mapping's path. |
| `... and that template is linked to <kind>, a <type> record, where this mapping renders <type> records, ...` | The custom template names another canonical kind; name this one. |
| `target.protocolOptions.preserveDataKeys carries data.ExtensionProperties over from the record OSDU holds into every update, ... what it writes there would reach a record only when it is created. ...` | Carry over `[Datasets, DDMSDatasets]` (step 4), or link through a tag. |
| `the mapping reads its records' parent with $parent, and template <kind> is linked to no parent; ...` | The custom template's link (step 1). |
| `... reads <path> of the record's parent, and template <kind> version <v> declares no <path>.` | A `$parent` path the canonical template does not have. |

## 6. What each run does

The canonical records are delivered before their custom records exist, so the links arrive over two runs of the
canonical flow:

| Run | What it does |
| --- | --- |
| 1. The canonical flow | Delivers every log, curve values and all. No custom record is there yet: each goes out without the child link. |
| 2. The custom flow | Renders each log reading its parent: the DDMS references and `ParentWellLogID` from the canonical record that just landed. As each custom record lands, the ledger asks for its canonical record to be planned again. |
| 3. The canonical flow | Its rows unchanged, it renders the records it was asked to: each now links its child, so its document changed and it is sent, the record alone on the DDMS route, its curve values not sent again. |
| 4. The custom flow | The canonical records landing again asked the custom ones to render again; their parent's sections did not change, so they render the same and nothing is sent. The two flows have settled. |

From then on, a log whose source row changes is delivered by both flows as any record is, each link kept, and a new log
follows the same four steps.

To see it:

- `sqlflow records show flows/welldb-welllog-03-header-delivery.yaml --key <key>` after run 2 says
  `(asked to be planned again)` ([sqlflow records](../cli/records.md)).
- The canonical record's page names the custom record it is **read by**, and the custom record's page names its
  **parent**; the OSDU tab of either shows the link in the document as OSDU holds it.

## 7. When the link does not come

| Seen | Why, and what to do |
| --- | --- |
| The custom record held: `the record's parent <id> (<kind>) is not in OSDU, or the flow's identity may not read it, ...` | The canonical flow has not delivered that log yet, or the custom flow's identity may not read it. It is released and sent once the parent lands. |
| The canonical record never links its child | The custom record is not in OSDU (look at the custom flow's ledger), or the two mappings do not key the rows alike (step 2), so the ids differ. |
| The child landed, and the canonical record still has no link | The canonical flow has not run since: run it, or wait for its schedule. A child delivered outside the ledger asks nothing; the link comes the next time something renders the canonical record. |
| The custom records are not found by search | The custom kind is not registered with the schema service (step 1). |

## See also

- [Mapping $parent](../flow/mapping-parent.md) and [mapping $child](../flow/mapping-child.md): every rule of the two nodes.
- [Templates](../concepts/templates.md#linked-templates): linked templates.
- [Change detection](../concepts/change-detection.md): why a link landing sends the canonical record once, and nothing after.
- [Writing a mapping](writing-a-mapping.md): the loop of check, values and preview for each mapping.
