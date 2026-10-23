---
id: delivery-flow-mapping
title: "Mapping document (documentType: mapping): how a row of an ingestion table becomes an OSDU record"
type: flow-reference
summary: "The mapping YAML a delivery flow pins: header, dataset key and OSDU id, parameters, the record tree laid out as the record, what a render writes."
keywords:
  - mapping
  - mapping document
  - "documenttype: mapping"
  - template.version
  - dataset.key
  - dataset.idfrom
  - osdu id
  - delivery key
  - record tree
  - "{$param.name}"
  - render.mapping
  - record shape
  - yaml anchors
  - acl and legal lists
related:
  - delivery-flow-mapping-values
  - delivery-flow-mapping-expressions
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-parent
  - delivery-flow-mapping-child
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-assertions
  - delivery-concept-templates
  - delivery-concept-preflight
  - delivery-flow-delivery
  - delivery-guide-writing-a-mapping
yamlPath: "(root, documentType: mapping)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingFingerprint.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLayout.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Identity/TargetId.cs
  - osdu/src/SqlFlow.Delivery/Identity/IdSegment.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ListValues.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceBindings.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/docs/census/keys.mapping.json
  - osdu/tests/SqlFlow.Delivery.Tests/MappingShapeTests.cs
---

# Mapping document (documentType: mapping): how a row of an ingestion table becomes an OSDU record

A mapping is the YAML document that turns one row of a delivery flow's ingestion tables into one OSDU record. It fills
one [template](../concepts/templates.md), the OSDU record of one kind with a variable for every property its schema
declares, and says for each property it fills where the value comes from: a column of the row, a value computed from the
row, a literal, the partition's cache, or a search of the platform. A property the mapping does not write is left out of
the record. A [delivery flow](delivery.md) pins exactly one mapping under `render.mapping`; the mapping never names a
connection, an endpoint or a partition, so the same mapping renders the same records in every estate.

You write a mapping once per kind and source: one for the wellbores of the well database, one for its well logs. Its
`record` block is laid out the way the rendered record is (`acl`, `legal`, `tags`, `data` and the properties below them),
so reading the mapping top to bottom reads the record top to bottom. This page covers the document as a whole: the
header, the OSDU id, the record tree and what a render writes. The value nodes are in
[mapping values](mapping-values.md), the expression language in [mapping expressions](mapping-expressions.md), cache and
platform lookups in [mapping lookups](mapping-lookups.md), modifiers in [mapping modifiers](mapping-modifiers.md), and
the business rules a property's value must meet in [mapping assertions](mapping-assertions.md).

## A complete mapping

The well logs of the well database, rendered into `osdu:wks:work-product-component--WellLog:1.4.0`. The record table is
`OsduData.silver.WellLog` (key `log_id`), and the flow declares `OsduData.silver.WellLogCurve` as the child dataset
`curves`. It is `WellLog@2.0.0`, a version of the well log mapping written to show most kinds of node in one document,
and it finds the wellbore on the platform by name, as for wellbores another system delivered; the well database's own
`WellLog@1.0.0` refers to the wellbores it delivers with `ref`
([one source, several kinds](../guides/multi-kind-source.md#3-point-the-well-log-at-its-wellbore)).

```yaml
documentType: mapping
name: WellLog
version: 2.0.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f            # optional: the template version, 16 hexadecimal characters, which a template
                                       # folder on the mapping's path must hold; left out, the one version the nearest
                                       # folder holding the kind holds
description: Well logs from the well database, one record per logging run, one curve item per curve row.

dataset:
  system: welldb                       # enters the delivery key, and so the OSDU id
  key: [log_id]                        # the record table's key, as the flow's source.record.key names it
  label: "{wellbore_name} / {log_source} / run {log_run}"   # display and search only
  identity: [wellbore_uwi, log_id]     # indexed by the ledger for lookup; never in the record

parameters:                            # what the flow supplies under render.parameters
  dataPartition: { required: true }    # always declared: ids and references are minted in it
  aclOwner: { required: true }
  aclViewer: { required: true }
  legalTag: { required: true }

searches:                              # record sets a $search node looks in on the platform
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"
    schema: { kind: osdu:wks:master-data--Wellbore:1.3.0, version: 58d6bdbd9d066a06 }

record:                                # laid out as the rendered record is
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  tags:
    DeliveredBy: welldb                # a literal: written as it is
  data:
    Name:
      $from: log_name                  # a column of the record table's row
      $modifiers: [trim]
    Description:
      $expr: coalesce(log_description, log_source & " run " & log_run)
    LogSource: { $from: log_source }
    LogRun: { $from: log_run }
    WellboreID:
      $search: Wellbore                # the id of the one wellbore the platform finds
      $findBy: data.FacilityName = wellbore_name
    TopMeasuredDepth: { $from: index_min }
    BottomMeasuredDepth: { $from: index_max }
    SamplingInterval:
      $from: index_increment
      $when: depth_coding = "REGULAR"  # left out of the record for any other row
    IsRegular:
      $from: depth_coding
      $modifiers:
        - equals: REGULAR
    VerticalMeasurement:               # an object: each property it holds is a node in turn
      VerticalMeasurement: { $from: kb_elevation }
      VerticalMeasurementTypeID: "{$param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"
    Curves:
      $forEach: curves                 # one item per row of the flow's child dataset 'curves'
      $where: curve_id != "DEPT"       # the child rows that become items
      $item:
        CurveID: { $from: curve_id }   # under $item a bare column name reads the curve's row
        Mnemonic: { $from: curve_id }
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias  # the source's spelling as the partition's unit code
            - ref                        # a reference to that UnitOfMeasure record
          $required: false             # a curve without a unit leaves CurveUnit out
        DepthUnit:
          $cache: UnitOfMeasure.id
          $findBy: Code = $dataset.depth_unit   # $dataset reads the log's own row from inside the item
```

Saved as `mappings/WellLog@2.0.0.yaml`, it validates offline, without a catalog or OSDU:

```bash
sqlflow validate mappings/WellLog@2.0.0.yaml
```

```text
OK  'WellLog@2.0.0 -> osdu:wks:work-product-component--WellLog:1.4.0' is valid (mapping).
```

`sqlflow validate` reads the document and runs every check that needs nothing but the document (the header, every node
of the record tree, the `$findBy` lines, modifiers and expressions). The checks against the template, the ingestion
tables and the partition's cache run before a delivery renders anything: that is the
[preflight](../concepts/preflight.md), which `sqlflow check` runs on its own ([check](../cli/check.md)).

## Where a mapping lives and how a flow finds it

A mapping is a file in the flow repository, reviewed and versioned like the flows. A flow pins it as `Name@version` under
`render.mapping`, and the file is looked for in the flow's mappings directory, in this order:

| File | Example |
| --- | --- |
| `<Name>@<version>.yaml` | `mappings/WellLog@1.0.0.yaml` |
| `<Name>@<version>.yml` | `mappings/WellLog@1.0.0.yml` |
| `<Name>/<version>.yaml` | `mappings/WellLog/1.0.0.yaml` |
| `<Name>/<version>.yml` | `mappings/WellLog/1.0.0.yml` |

The mappings directory is `render.mappings` when the flow sets it (relative to the flow file); otherwise it is the nearest
folder named `mappings` walking up from the flow file (at most 16 levels), so a flow several folders deep still finds
the repository's shared mappings. The file must declare the name and version it is filed under:

| Situation | Message |
| --- | --- |
| The reference has no `@` | `<flow file>: render.mapping 'WellLog' must be pinned as 'Name@version'; floating references are not allowed.` (when the flow is read) |
| The name or the version is empty | `Mapping reference 'WellLog@' must be pinned as 'Name@version'.` |
| The name or version holds `/`, `\` or `..` | `Mapping reference '...' names a path; a mapping is named by its name and version alone, without '/', '\' or '..'.` |
| No file is found | `Mapping 'WellLog@1.0.0' was not found under '<directory>'. Expected one of: WellLog@1.0.0.yaml, WellLog@1.0.0.yml, 1.0.0.yaml, 1.0.0.yml.` |
| The document declares another name or version | `<file>: declares 'WellLog@1.0.1' but is filed as 'WellLog@1.0.0'. The file name and the document must agree.` |

The repository sync reads every YAML file of the repository whose top-level `documentType` is `mapping`, settles the
template each one fills from the template folders on its path ([template folders](../concepts/templates.md#template-folders)),
and the GUI's Mappings page lists each one with that template version, the file it was read from, and whether it loads. A
mapping whose template no folder on its path settles does not load, keeps the reference it declares, and says where it
looked; one that names a version still pins it, so the version cannot be deleted from under it. A mapping that fails to
load for another reason is listed with its error and still pins the template version its `template` block names. A
reference declared by two files is synced once, with the warning `mapping '<Name@version>' is declared more than once in
the repository; the first file wins.`

## Header keys

The loader is strict: a key it does not know is refused (`Property 'mappings' not found on type ...`), and so is a key
written twice in one map (`Encountered duplicate key Name`).

| Key | Type | Required | Meaning |
| --- | --- | --- | --- |
| `documentType` | string | yes | Always `mapping`. |
| `name` | string | yes | With `version`, the reference `Name@version` a delivery flow pins under `render.mapping`. |
| `version` | string | yes | The mapping's version. It enters every record's render context, so a new version renders the records again. |
| `description` | string | no | Free text. |
| `template.kind` | string | yes | The OSDU kind the records are, `authority:source:entityType:major.minor.patch`, such as `osdu:wks:work-product-component--WellLog:1.4.0`. |
| `template.version` | string | no | The template version the mapping fills exactly: the 16 hexadecimal characters `sqlflow template folders` and the Templates page show. A template folder on the mapping's path must hold it. Left out, the mapping fills the one version of the kind the nearest template folder holding the kind holds, and follows that file as it changes ([template folders](../concepts/templates.md#template-folders)). |
| `dataset.system` | string | yes | The source system. It enters the delivery key, and so the OSDU id. |
| `dataset.key` | list | yes | The record table's columns that identify a record, in order. |
| `dataset.label` | string | no | Display text for the ledger and the GUI, with `{column}` tokens. Never in the record. |
| `dataset.identity` | list | no | Columns whose values identify the record to a person; the ledger indexes them. Never in the record. |
| `dataset.idFrom` | `deliveryKey` or `key` | no | What the last part of the OSDU id is made from. Default `deliveryKey`. See [The OSDU id](#the-osdu-id). |
| `parameters` | map | yes (`dataPartition`) | The values the flow supplies, each `required`, `default` and `description`. See [parameters](#parameters). |
| `searches` | map | no | The record sets `$search` nodes look in on the platform: a `kind`, the template (`schema.kind`, and `schema.version` when it means one exactly) that says how it is indexed, read from the template folders on the mapping's path, and a `description`. One search per kind, each read by a node. See [mapping lookups](mapping-lookups.md). |
| `lookups` | map | no | Records found once in the partition's cache and read by name with `$lookup`: `$cache`, `$findBy`, `$modifiers`, `$ignoreSeparators`, `$description`. Each is read by a node. See [mapping lookups](mapping-lookups.md). |
| `record` | map | yes | The record the mapping renders, laid out as the record is. See [The record tree](#the-record-tree). |

A mapping written when mappings carried example rows is refused with what to delete:
`a mapping holds no fixtures; delete its fixtures block. Every record a run renders is checked against the template, and a plan run shows which records a change would deliver again.`
The same holds for `fixtureDefaults`.

### dataset: the record table's key, label and identity

The "dataset" is the flow's record table (`source.record`), one row per record, and the child datasets its
`source.datasets` declares. A bare column name in the mapping reads the record table's row; under a `$forEach` it reads
the child row ([mapping values](mapping-values.md#foreach-repeated-arrays-from-a-child-dataset)).

- **`dataset.key`** names the record table's key columns as they are (letters, digits, `_` and `-`), each once. It must
  name the same columns, in the same order, as the flow's `source.record.key`, which is the ingestion flow's
  `load.keyColumns`; otherwise a run stops, before it reads a row, with
  `source.record.key is [wellbore_id] but mapping 'WellLog@1.0.0' keys its records by [log_id]. A record's identity is one thing: the two have to name the same columns in the same order.`
  A key column need not be written into the record. A row whose key has an empty column is held:
  `dataset key incomplete: every key column must be non-empty` (a preview names the key:
  `dataset key incomplete (welldb:<null>): ...`).
- **`dataset.system`** enters the delivery key, compared trimmed and ignoring case. Two mappings that deliver the same
  rows into the same entity type and partition need different systems or keys, because one OSDU record belongs to one
  flow.
- **`dataset.label`** is the text the ledger, the GUI and the run trace show for a record. A token is `{column}` or
  `{$dataset.column}`, a column of the record table's row; the rendered label is trimmed and cut at 400 characters. It
  never enters the record or its hash.
- **`dataset.identity`** lists columns whose values a person holds when they come looking for a record (a wellbore UWI, a
  log id). The ledger indexes each non-empty value, so the Records page finds the record by any of them across every
  flow. Like the label, it never enters the record or its hash.

### parameters

`parameters` declares the values a mapping takes from the flow. Each takes `required` (default `false`), `default` (text,
taken when the flow supplies none) and `description`. `dataPartition` is always declared:
`the mapping must declare the 'dataPartition' parameter; record ids and references are minted in that partition.`

| Where a parameter is read | Written as |
| --- | --- |
| The text of a literal | `"{$param.aclOwner}"`, `"{$param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"` |
| An expression | `$param.region` ([mapping expressions](mapping-expressions.md)) |
| An `id` modifier's template | `{$param.dataPartition}` ([mapping modifiers](mapping-modifiers.md)) |

A token or expression naming a parameter the mapping does not declare is refused when the mapping is read
(`record.data.LogRun uses {$param.region}, but the mapping declares no parameter 'region'.`).

The flow supplies values under `render.parameters`, as literals or `${env:NAME}` and `${keyvault:NAME}` references, which
are resolved before the render, so the render context and the ledger hold the value that reached the record and never
the reference. Four parameters belong to the OSDU flow kind: when the mapping declares one of them and the flow leaves it
out, it takes the kind's own reference, which also takes the place of a `default` the mapping gives it.

| Parameter | Fills | Taken from when the flow leaves it out |
| --- | --- | --- |
| `dataPartition` | the partition every id and reference is minted in | the partition the run is bound to; the flow's `target.headers.data-partition-id`, as written, for a flow bound by its header; `${env:OSDU_DATA_PARTITION}` only for a flow bound to no partition |
| `aclOwner` | the owners group | `${env:OSDU_ACL_OWNER}` |
| `aclViewer` | the viewers group | `${env:OSDU_ACL_VIEWER}` |
| `legalTag` | the legal tag | `${env:OSDU_LEGAL_TAG}` |

The references resolve from the repository's central configuration and then the node's environment
([environment variables](../concepts/environment-variables.md)). A flow bound to partitions writes the partition it
delivers to as `dataPartition` itself, and a flow bound by its `data-partition-id` header writes its header's value. A parameter's `default` is mapping content and is taken literally, never expanded
as a reference. The partition must be a valid id segment (letters, digits, `_`, `-` and `.`), or the run stops with
`The mapping parameter 'dataPartition' is '<value>', which is not a valid OSDU id segment ...`. A flow value for a
parameter the mapping does not declare, and a required parameter with no value, fail the preflight.

**Why a flow cannot supply a value the mapping does not declare.** The preflight refuses it before anything renders:

```text
<flow>: the flow supplies parameter 'region', which the mapping does not declare. Undeclared values would be overrides, and overrides are not allowed (osdu/docs/reference/flow/mapping.md, parameters).
```

Besides its own rows, what a record becomes is decided by the pinned mapping, its template, the cache version and the
parameter values (and, for a mapping that searches, the partition's system properties), and nothing else: that is the
render context, recorded against every record, and change detection compares a record against it ([change
detection](../concepts/change-detection.md#render-affecting-versus-operational)). A value the mapping does not declare
could only reach a record as an override of what the mapping says, and then two flows on the same mapping version would
render different documents. Overrides would have to enter the render context, so the flow document would become a render
input, its edits could change what is delivered without a mapping change, and the validated mapping would no longer be
what renders: the preflight would check the mapping while the records came from the mapping plus whatever each flow
added. The flow stays operational instead: a change to its endpoint, reliability or schedule redelivers nothing.

Parameters are the sanctioned way to vary. A value that differs between flows on one mapping (the partition, the access
groups, the legal tag, a source system name) is declared under `parameters`, so the mapping says where it lands and is
validated with it, and its resolved value enters the render context of every record, so a changed value renders those
records again. A value that differs from row to row belongs in the ingestion table as a column; a source whose columns
differ in name is normalised by its ingestion flow, or is a different shape and has a mapping of its own.

## The OSDU id

No node fills the record's `id`: the engine writes it from the key, as `{partition}:{entityType}:{unique}`, where the
partition is the `dataPartition` parameter and the entity type is the template's (`work-product-component--WellLog`).
`dataset.idFrom` says what the unique part is made from.

| `idFrom` | The unique part | Example |
| --- | --- | --- |
| `deliveryKey` (the default) | The delivery key: a UUIDv5 over `dataset.system` (trimmed, lower case) and the key's values (trimmed), written as 32 hexadecimal digits. | `dev:work-product-component--WellLog:5f0c...` (32 digits) |
| `key` | The key's own values, as OSDU's reference catalogs name records by their code. | `dev:reference-data--ExternalUnitOfMeasure:g%2Fcc` |

Under `idFrom: key`:

- **Each value is written as the id carries it.** Values are trimmed and keep their case, since OSDU and the ledger
  compare ids exactly. ASCII letters, digits, `_`, `-`, `.` and `:` stand as they are; every other character is
  percent-encoded as its UTF-8 bytes with upper-case hex digits (`g/cc` is `g%2Fcc`, `°C` is `%C2%B0C`, a space is `%20`),
  and `%` is always `%25`, so a value that looks encoded never lands on the id of another.
- **One key column keeps its colons**, so a code such as `LIS-LAS::GAPI` stays as OSDU's catalogs write it. With several
  key columns, each value's colons are written `%3A` and the values are joined with `:`, so the key can be read back from
  the id.
- **A key that gives no id holds the record**, with the reason: an empty value, text that is not valid Unicode, an id
  longer than the 500 characters the ledger keeps, or an id the template's `id` pattern refuses
  (`id: the mapping makes the OSDU id from the key (dataset.idFrom: key), and welldb:... gives none: ...`). An id is never
  cut short or changed to fit.
- **Case is the source's to fold.** The id keeps whatever case the key has; a source whose codes ignore case should fold
  them in the ingestion SQL.
- **An id OSDU already holds is taken over only when an operator adopts it.** Before a record first claims an id made from
  its key, the run asks the flow's target whether OSDU holds a record there. One no record of the ledger claimed was written
  by another system, or another instance of the delivery: the record is held (`OSDU already holds a record at <id> ..., the OSDU id made from the key, and no record of the ledger claimed it: ...`),
  keeping the id and the version OSDU holds, until an operator adopts it, every record held so at once or a selection of
  them; the next run then claims the id and writes the row there ([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)).
  An answer that is not an absence holds the record too, and a release or the next run asks again.

Under either form the delivery key stays the record's identity in the ledger, and **a record keeps the id it claimed**
when it first queued a document. A changed `idFrom`, a kind of another entity type or another `dataPartition` gives
delivered records other ids; each such record is held, naming both ids and what moved them, and nothing is sent
(`the mapping now gives this record the OSDU id <new id>, and the record claimed <old id> ...; or put back what moved the id: the mapping's dataset.idFrom, the entity type of the kind it renders, or the partition the flow mints ids in (dataPartition)`). A changed `system` or `key` makes other delivery keys, so the rows become new records of
the ledger: under the default `idFrom` they are delivered under new ids, beside the records already delivered, and under
`idFrom: key` a new record whose id an earlier record of the ledger claimed is held (`OSDU id <id> is already claimed by
...`). Either way OSDU never gains a record the ledger does not name. To move delivered records to new ids, remove them
from OSDU at the `record` scope, then deliver the rows
under a ledger of their own (an [interface's](interfaces.md) `ledger:`) or delete them from the ledger with the removal
([removal and reversal](../concepts/removal-and-reversal.md)). Choose the id form before anything is delivered.

## The record tree

**Every word of the mapping language starts with `$`, and every other key is a property of the record.** A template's
property is therefore never read as the language, whatever it is called, and neither is a column. The `record` block holds
the record's own properties (`acl`, `legal`, `tags`, `data`, and any other the template declares), and below each the
properties it holds, nested as the record nests them. A key is one property name, never a dotted path:
`record.data: 'VerticalMeasurement.VerticalMeasurement' is not a property name. A key in the record is one property of it, so nest the properties a path names rather than writing the path.`

A property is one of these nodes ([mapping values](mapping-values.md) has each in full):

| Node | Written as | Writes |
| --- | --- | --- |
| A literal | `ReferenceCurveID: MD`, `otherRelevantDataCountries: [US]` | The value as written. Its text reads a parameter as `{$param.<name>}` and nothing else. |
| An object | `VerticalMeasurement:` and the properties it holds | Each property it holds, as that property's node writes it. |
| A value node | `Name: { $from: log_name, $modifiers: [trim] }` | One value, read with `$from`, `$expr`, `$value`, `$cache`, `$search` or `$lookup`, and the settings beside it. |
| A `$coalesce` node | `Name: { $coalesce: [ { $from: log_name }, { $from: log_source } ] }` | The value of the first alternative that gives one. |
| A `$forEach` node | `Curves: { $forEach: curves, $item: { CurveID: { $from: curve_id } } }` | An array with one item per row of a child dataset. |
| A list of values | `viewers: ["{$param.aclViewer}", { $cache: ..., $findAll: ... }]` | What each item gives, in order. |
| A list of objects | `TechnicalAssurances: [ { TechnicalAssuranceTypeID: { $expr: ... } } ]` | The object each item gives, in order. |

A map holding any key that starts with `$` is a node, and all its keys start with `$`. A map mixing the language's words
with properties is refused, and so is a word the language does not have, with the one it most likely meant:

```text
record.data.Name holds '$from', words of the mapping language, beside the property 'Description'; a node's keys all start with '$'. ...
record.data.Name: '$form' is not a word of the mapping language. Did you mean '$from'? A property whose name starts with '$' is written $$form.
```

A property whose own name starts with `$` is written with one more, `$$name`. Each property the tree writes is a template
variable named by its path in the record: `record.data.Curves.$item.CurveID` fills `osdu.data.Curves[].CurveID`. Messages
name a node by where the document writes it, and the preflight, the coverage view and the builder name the variable it
fills.

### Access and legal lists

Every OSDU record carries `acl.owners`, `acl.viewers`, `legal.legaltags` and `legal.otherRelevantDataCountries`, so every
mapping lays out all four, each with at least one literal text value, no value twice, and no `$when`:

- `acl.owners` and `acl.viewers` may be [lists of values](mapping-values.md#lists-of-values): value nodes among the
  literals add groups a record's own data gives, and the literals are carried whatever the nodes find.
- `legal.legaltags` and `legal.otherRelevantDataCountries` are literal lists only, because the legal service checks a
  run's tags and countries before the run, which it can do only for a list that is the same on every record.

| Mistake | Message |
| --- | --- |
| A list is missing | `every OSDU record carries viewers, so the mapping lays out record.acl.viewers as a list of at least one text value.` |
| An access list has no literal | `record.acl.viewers must list at least one literal text value, which every record carries whatever its nodes find, such as ["{$param.aclViewer}", { $cache: ... }].` |
| A legal list reads values | `record.legal.otherRelevantDataCountries reads values with record.legal.otherRelevantDataCountries[1], and the countries are a literal list: the legal service checks them before a run, for every record alike.` |
| A value twice | `record.acl.owners lists '<value>' more than once; the list is a set.` |
| A `$when` on one of them | `record.acl.owners applies to every record, so it takes no $when.` |

A mapping whose kind has `dspdm` as its source segment renders a DSPDM business object row rather than an OSDU record: it
has no access or legal block (filling one is refused), and every property it fills is under `record.data`
([routes](routes.md)).

## What the record contains

The engine starts from nothing, writes `id` and `kind` (the template's), then writes each property's value at its place.

- **The template decides the type.** Text becomes a number, an integer or a boolean where the schema says so, a single
  value written to a list of values becomes a list of one, and a value that cannot take the type holds the record with a
  reason naming the variable (`osdu.data.TechnicalAssurances[].Score: value 'many' is not a valid integer ...`).
  [Mapping values](mapping-values.md#how-a-value-takes-its-propertys-type) has the rules.
- **What is not written is left out.** A property the mapping does not write, a node whose `$when` does not hold, and an
  optional node with no value are left out, never written as null, as an empty text, a zero, `false` or `{}`. An object
  left with nothing in it is left out too.
- **A list the mapping defines is written empty when it gives nothing** (`"Curves": []`), at any depth: a `$forEach`, a
  list of values, a list of objects, or a node whose variable is a list. A list the mapping does not define is left out.
- **`meta` is always written.** A list of the record's own outside `data` (`meta` in OSDU's schemas) is a field Storage
  reads back as null when it is left out, so the record carries it empty (`"meta": []`) whenever nothing fills it.
- **No null where the template takes none.** A null item is dropped from a list whose items take no null (a list read
  whole from the cache may hold one), in every object the record holds.
- **The schema's required data must be there.** A property the schema requires in `data` that renders empty holds the
  record: `schema-required property data.<Name> rendered empty`.
- **`id`, `kind` and what OSDU sets are not the mapping's.** A node filling `record.id` or `record.kind` fails the
  preflight (`osdu.id is written by OSDU Delivery (from dataset.key), not by a mapping.`), and so does one filling
  `version`, `createTime`, `createUser`, `modifyTime` or `modifyUser` (`... is set by OSDU when the record is stored, not by a mapping.`).

A held record is never sent; the ledger records why, and the record waits for its row or the mapping to change, or for a
release ([record lifecycle](../concepts/record-lifecycle.md)). Every rendered record is also judged against its template
before it is sent ([preflight](../concepts/preflight.md)).

## Assertions: the rules a value must meet

A template says what OSDU accepts; a mapping can also say what a property's value must be for the record to be right.
`$assert` beside a node lists such rules in the assertion flows' words, each judged on every record the mapping renders,
on the value the record carries or on the value the row gives the node:

```yaml
    VerticalMeasurement:
      VerticalMeasurement:
        $from: elev_meas_ref
        $modifiers:
          - split: { separator: " ", part: 1 }
        $assert:
          - name: reference-elevation
            between: [0, 250]
```

A record whose elevation lies outside the range is held at the check before sending, its document kept until a release
sends it; an assertion can instead send the record and record the failure (`onFail: report`), or leave the value out
(`onFail: omit`). What each record's assertions found joins its verdict on every attempt, and the record keeps how many
failed. [Mapping assertions](mapping-assertions.md) has every word, where an assertion may be written, how each value is
judged, and where the findings are shown.

## The record shape

The record shape is what the records a mapping renders look like, drawn without a source row or a cache: the GUI's
Mappings page shows it on a mapping's Record shape tab, and `POST /api/v1/delivery/mapping-builder/shape` draws it for
any mapping document (with the parameter values given). It is drawn by the renderer a delivery uses, so `id`, `kind`, the
envelope, the nesting, the arrays and the types are the ones a render writes:

- a value read from a row or the cache is a placeholder naming the type the template gives the property and where the
  value comes from, with its modifiers, `optional` and its condition;
- a literal shows as it renders, and a parameter without a value shows as its `{$param.name}` token;
- a `$forEach` array has one item, and a note says it takes one item per row of its child dataset and which rows its
  `$where` keeps;
- each assertion is a note on its property, saying what it asserts of which value and what a record that fails it does
  ([mapping assertions](mapping-assertions.md)).

Drawn for the mapping above with `dataPartition` set to `dev`, the shape reads in part:

```json
{
  "id": "dev:work-product-component--WellLog:<delivery key from welldb, dataset.log_id>",
  "kind": "osdu:wks:work-product-component--WellLog:1.4.0",
  "acl": { "owners": ["{$param.aclOwner}"], "viewers": ["{$param.aclViewer}"] },
  "legal": { "legaltags": ["{$param.legalTag}"], "otherRelevantDataCountries": ["US"] },
  "tags": { "DeliveredBy": "welldb" },
  "meta": [],
  "data": {
    "Name": "<string from dataset.log_name | trim>",
    "WellboreID": "<string from search.Wellbore.id by data.FacilityName = dataset.wellbore_name>",
    "SamplingInterval": "<number from dataset.index_increment, when depth_coding = \"REGULAR\">",
    "IsRegular": "<boolean from dataset.depth_coding | equals(REGULAR)>",
    "Curves": [
      {
        "CurveID": "<string from dataset.curves.curve_id>",
        "CurveUnit": "<string from dataset.curves.curve_unit | replace from $cache.UnitAlias | ref, optional>",
        "DepthUnit": "<string from cache.UnitOfMeasure.id by Code = dataset.depth_unit>"
      }
    ]
  }
}
```

with the note `osdu.data.Curves: one item per row of dataset.curves where curve_id != "DEPT"; a record without any is held`,
and one note per parameter drawn as its token. Nothing is read, rendered for delivery or stored. The Properties tab beside
it lays the template out with the mapping over it (what each variable is filled by, on every row or on some), and
`POST /api/v1/delivery/mapping-builder/coverage` answers the same for any document. To see a real row rendered, use a
preview ([preview](../cli/preview.md)); to see what the mapping makes of every row of a scope, check its values
([values](../cli/values.md)).

## When a mapping changes

The mapping reference (`name@version`), a fingerprint of the document, the template version and the parameter values
are the render's rules, and enter every record's render context. The fingerprint is the document read as YAML and
written as canonical JSON, so an edit to the record tree, a lookup, a parameter's default, the template or any other
value (a `description` included) moves it, and a comment, a blank line or a reordered key does not.

When a scope was last planned under other rules, its next run reads every row in scope again rather than only the
changed rows, and each record's own document hash decides what is sent: a record whose document did not change is not
sent again ([change detection](../concepts/change-detection.md)). An assertion added or changed moves the fingerprint
too, and is judged on the records whose document then changes; a record whose document is the one OSDU holds is not sent
or judged again ([mapping assertions](mapping-assertions.md#a-document-that-does-not-change-is-not-judged-again)). A
`plan` run shows what a mapping change would send before anything is sent ([running an OSDU flow](../cli/run.md)). Give a mapping a new version when its records should be
traceable to the change; the ledger records the mapping version and the cache version that rendered every document
([ledger](../concepts/ledger.md)).

## Writing a node once: YAML anchors

A node, a list of modifiers or a condition used in several places can be written once with a YAML anchor and repeated
with an alias. The mapping reads an alias exactly as if the value were written out again:

```yaml
Curves:
  $forEach: curves
  $item:
    CurveUnit:
      $from: curve_unit
      $modifiers: &unit
        - replace: $cache.UnitAlias
        - ref
    DepthUnit:
      $from: depth_unit
      $modifiers: *unit
```

Only the value is shared, never where it sits: a bare column name in a repeated node reads the row of the node it is
repeated into. YAML's merge key (`<<`) is not supported; it is read as a key like any other and refused (inside a node:
`... holds '$from', words of the mapping language, beside the property '<<' ...`).

## Errors when the mapping is read

Each message starts with the file. A sample of what `sqlflow validate`, the sync and a run refuse:

| Mistake | Message |
| --- | --- |
| No `dataPartition` parameter | `the mapping must declare the 'dataPartition' parameter; record ids and references are minted in that partition.` |
| A template version that is not 16 hex characters | `template.version '26a3c344' is not a template version. A version is the 16 hexadecimal characters the Templates page shows for a template file, such as 26a3c3441882db4f; leave it out to fill the one version of the kind the nearest template folder holds.` |
| A template no folder on the mapping's path settles | `template: no template folder on the path of welldb/mappings/WellLog@1.4.0.yaml holds template osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f. ...` (the whole message says where it looked and how to put the file there: [template folders](../concepts/templates.md#template-folders)) |
| A kind named alone where no template folder is known | `template names osdu:wks:work-product-component--WellLog:1.4.0 without a version, which is settled from the template folders on the mapping's path, and the mapping was read where no template folder is known. ...` |
| A kind that is not a record kind | `template.kind 'WellLog:1.4.0' must be 'authority:source:entityType:major.minor.patch', each of the first three made of letters, digits, underscore, hyphen and dot, as the storage service requires of every record it accepts.` |
| An empty key | `dataset.key must name at least one column, such as key: [log_id].` |
| An unknown `idFrom` | `dataset.idFrom is 'code'; write deliveryKey for an OSDU id made from the delivery key (the default), or key for one made from the values of dataset.key, ...` |
| An empty `record` | `'record' lays out the record the mapping renders, with its acl, legal and data, such as record: { data: { Name: { $from: name } } }.` |
| A declared search or lookup nothing reads | `search 'Wellbore' is declared and nothing reads it; ...` / `lookups.wellbore is read by no node; ...` |

The node, condition and expression messages are on [mapping values](mapping-values.md#errors) and
[mapping expressions](mapping-expressions.md#errors-when-the-mapping-is-read).

## Related

- [Mapping values](mapping-values.md): every node of the record tree, `$required`, lists and open objects.
- [Mapping expressions](mapping-expressions.md): `$expr`, `$when` and `$where`.
- [Mapping lookups](mapping-lookups.md) and [mapping modifiers](mapping-modifiers.md).
- [Mapping assertions](mapping-assertions.md): `$assert`, the rules a property's value must meet.
- [Mapping $parent](mapping-parent.md): a custom template's records reading sections of their canonical parent.
- [Mapping $child](mapping-child.md): a canonical record linking the custom records that stand beside it.
- [Templates](../concepts/templates.md): the template folders a mapping reads, and where `template.version` comes from.
- [Delivery flow](delivery.md): `render.mapping`, `render.parameters`, `source.record` and `source.datasets`.
- [Writing a mapping](../guides/writing-a-mapping.md): from a template to a mapping that passes the preflight.
- SQLFlow's [`sqlflow validate`](../../../../sqlflow/docs/reference/cli/validate.md) and
  [ingestion flows](../../../../sqlflow/docs/reference/flow/ing.md), which load the tables a mapping reads.
