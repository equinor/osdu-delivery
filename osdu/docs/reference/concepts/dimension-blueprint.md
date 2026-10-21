---
id: delivery-concept-dimension-blueprint
title: "The dimension blueprint: how a dimension is built, line by line, from its YAML to its columns"
type: concept
summary: "What a dimension's blueprint answers and the Definition tab draws: the YAML line by line, the records and paths read, warnings, and each column's coverage."
keywords:
  - blueprint
  - Definition tab
  - how a dimension is built
  - dimension diagram
  - path classification
  - oneOf anyOf branch
  - column coverage
  - records read by id
related:
  - delivery-flow-dimension
  - delivery-concept-dimension-tables
  - delivery-guide-dimensions
  - delivery-concept-api
  - delivery-concept-explorer
  - delivery-concept-templates
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionBlueprint.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionYamlSource.cs
  - osdu/src/SqlFlow.Delivery/Templates/SchemaPathReading.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.Blueprint.cs
  - osdu/gui/src/features/delivery/dimensions/DimensionDefinition.tsx
  - osdu/gui/src/features/delivery/dimensions/BlueprintDiagram.tsx
  - osdu/gui/src/features/delivery/dimensions/BlueprintInspector.tsx
---

# The dimension blueprint: how a dimension is built, line by line, from its YAML to its columns

The blueprint says how a build makes each column of a dimension's table, from the flow's YAML and the templates of the
records it reads, before any build and after one. The dimension's **Definition** tab draws it, and
`GET /api/v1/delivery/flows/{pipelineId}/dimensions/{name}/blueprint?partition=<partition>` answers it
([API](api.md#assertions-dimensions-and-inventories)). It reads the catalog's copy of the flow, the templates the
[template folders](templates.md#template-folders) on its path held at the repository's last sync, and the ledger; nothing
is sent to OSDU and no record is read. A dimension the explorer drafts, which no flow holds yet, is described by the
saved templates. The explorer's
[dimension builder](explorer.md#building-a-dimension) describes the item it writes with the same blueprint.

| Field | Holds |
| --- | --- |
| `flow`, `dimension`, `partition` | The flow, the dimension, and the partition it was laid out in. |
| `declared` | Whether the flow declares the dimension now. One its ledger keeps that the flow no longer declares is laid out from the declaration its last build read with. |
| `dimensionId` | The dimension's number in the ledger; null before its first build. |
| `yaml` | The dimension's item of the flow's YAML, line by line (below). |
| `yamlMissing` | Why `yaml` is null: `<flow> no longer declares <dimension>: what is shown is what its last build read with.`, or `The YAML of <file> could not be laid out line by line; the pipeline's page shows the whole file.` |
| `blueprint.source` | The dimension's own records and the paths read from them. |
| `blueprint.records` | The records read by id, step by step. |
| `blueprint.columns` | The table's columns, each with the reads it is written from. |
| `coverage` | How much of each attribute the dimension's builds read. |

The request is refused with 400 when it names no dimension, 404 when the pipeline is not found or the flow neither
declares the dimension nor keeps one of that name, and 409 when the pipeline is not a dimension flow or the catalog's copy
of its YAML does not parse (`Re-sync the repository.`).

## The YAML, line by line

`yaml` holds the file, the line the item starts on (`firstLine`), its `lines`, and `spans`: where each thing the item
declares is written, as `target`, `line`, `column`, `endLine` and `endColumn` (lines and columns from 1, the end column
the one after the last character). The item is the one under `dimensions` whose `name` matches ignoring case; the comments
just above it (as deep as its dash) and those after its last value (deeper than its dash) belong to it. An item longer
than 400 lines is cut there, with `cut` true.

| Target | What it spans |
| --- | --- |
| `kind`, `path`, `query`, `label`, `attributes`, ... | Each key of the item, from the key to the end of its value. |
| `label.0`, `clean.1`, `partitions.0` | Each item of a list, by its place; a single value written where a list may be is `.0`. |
| `attributes.Country` | An attribute, from its name to the end of its value. |
| `attributes.Country.0`, `attributes.Country.1` | Each path of an attribute's steps, by its place, in the bare form and under `path:` alike. |
| `attributes.Source.collect`, `attributes.WellboreID.keep` | Each setting of an attribute written as settings. |
| `columns.key`, `columns.value` | Each name `columns` gives. |
| `elements.path`, `elements.fields.Mnemonic` | Each setting of `elements`, and each of its fields. |

A list or a map written in brackets ends after its closing bracket, and a folded text (`description: >`) on its last line
of text, so pointing at a line finds exactly what it declares. A view's **Definition** tab lays out its item under `views`
the same way, with the targets `from`, `where`, `join.0`, `join.0.on` and `columns.<name>`, `columns.<name>.dataType`.

## The dimension's own records

`blueprint.source` holds the dimension's `kind` pattern and `query`, the kinds of its records, the template its paths
are described by, and the reads made of them: the key (read id `key`), then each collected attribute
(`collect:<attribute>`).

- **The kinds.** After a build, and while the kind pattern is the one that build read, they are the kinds the build read
  (`built` true), the most records first, each with its records and the version of the template it was read against;
  the template is the first of them that had one. Before a build, or once the pattern changed, they are the saved
  templates the pattern matches, segment by segment with `*` for any text: one per kind (the newest captured), the
  newest kind version first, which is the template.
- **How the search reads each path** (`index`): as a build settles its field, the index's type (`text`, `keyword`,
  `number`, `boolean` or `date`), the nested array it sits in, the field aggregated (`aggregateBy`) and whether a record
  holds it more than once (`repeats`). A property of the record itself is classified by the indexer's own mapping; a path
  of `data` only when a template describes it. A path the search cannot read says why (`problem`:
  `A build cannot read <path>: ...`).

## The records read by id

Each step of the label and of each attribute read through the records a key names (not a collected one) reads the
records the step before names. Which records those are is the template's to say: the entity types the read's last segment
refers to (`x-osdu-relationship`, on the property or on an array's items), so the logs' `data.WellboreID` leads to
`master-data--Wellbore`. Each entry of `blueprint.records` is one set of records:

| Field | Holds |
| --- | --- |
| `id` | `<depth>:<entity types>`, such as `1:master-data--Wellbore`, or `<depth>:from:<read>` when no template says what the read names. |
| `depth` | 1 for the records the keys name, 2 for those they name, and so on, up to 3. |
| `entityTypes` | The entity types the records are of; empty where no template says. |
| `from` | The read whose ids name them. |
| `template` | The newest template the flow reads of the first of those entity types that has one (the newest kind version), or the newest saved for a draft. |
| `missing` | Why no template describes them. |
| `reads` | The paths read from them, each with its `uses`: the role (`label` or `attribute`), the attribute, the step from 0, and whether it is the step whose value is kept. |

The records are grouped as a build groups its searches, one set of entity types at one depth, so a wellbore read for its
name, its operator and its country is one entry, and a path two chains read there is one read with two uses. Each read
names the entry its ids lead to (`leadsTo`).

**How a path is read through a template.** Each read holds `schema`: the path segment by segment as the template
describes it, with the property's type (and its items' for a list), format, title, description, how the indexer stores
an array of objects (`nested` or `flattened`), and the entity types it refers to.

- A segment is looked up in the object's own properties; then in the forms its `oneOf` or `anyOf` allows, forms of forms
  included, where `branch` names by their titles the forms that declare it when only some do (a wellbore's `GeoContexts`
  holds five kinds of context, and only the field's declares `FieldID`); then in the schema of its free keys
  (`additionalProperties`).
- A segment that filters (`[GeoPoliticalEntityTypeID*=...]`) looks the property it compares up in the objects the segment
  holds (an array's items, or the object itself): `filter` gives the property, how it compares (`equals`, `contains`,
  `endsWith`), the text, whether those objects declare the property (`known`) and its description.
- The description stops at the first segment the template does not declare, each after it marked not found, and says why:
  `<kind> declares no <segment> at <path>: the object there is one of <n> forms (...), and none declares it.`,
  `... the value there holds no properties.`, or `<kind> declares no <segment> at <path>.`

**What is wrong is said, not refused.** A build reads a label's records as they are, so the blueprint refuses nothing and
marks what would read nothing or the wrong thing (`problem` on a read, `missing` on a set of records). A build still
fails where [What a build does](../flow/dimension.md#what-a-build-does) says, such as a `data` path of a kind no template folder on the flow's path holds.

| Said | When |
| --- | --- |
| `No template the flow reads matches <kind>, and a build reads a path of data only by the template of every kind it matches. Put the kind's template in a template folder on the flow's path.` (for a draft: `No saved template matches <kind>, ...`) | The dimension's path is a path of `data` and no template it reads matches its kind. |
| `The template does not declare <path> a reference to another record: a key's label and attributes are read only where the key is a record id.` | The dimension reads a label or attributes through a key the template declares no reference. |
| `The template does not declare <path> a reference to another record, so the next step reads records only where it holds a record id.` | A step before the last. |
| `<path> holds an object, not a value; a build keeps only text it finds there, so name one of its properties.` | The last step of a label or an attribute. |
| `The flow reads no template of <entity types>, so the paths read from these records are read as written, unchecked. Put one in a template folder on the flow's path to check them.` (for a draft: `No template of <entity types> is saved, ...`) | Records of a type no template it reads describes; the Definition tab repeats it under the diagram. |
| `The template does not say which records <path> names: a build reads whatever records its ids name.` | A read whose template names no entity type. |
| `The last build read template <kind> version <version>, which the flow no longer reads, ...` and `The last build read <path> without a template of <kinds>.` | The template the dimension's own records were read against is no longer in a folder on its path, or never was. |

## The columns and what was read for them

`blueprint.columns` lists the table's columns in its order, each with its `name`, its `role` (`id`, `partition`,
`keyId`, `key`, `value`, `records`, `filter` or `attribute`), the attribute it is, and `from`, the reads it is written
from: `key_id`, the key's column and `filter` from the key; the value's column from the label's last step, or from the key
when the dimension reads no label; `records` from the key and each collected attribute; an attribute from its last step,
or from its collected read. The blueprint lays out the key, the label and the attributes; the `element` column and an
element's fields, and the `key_hash` column views join on, are not among its columns.

`coverage` gives, for each attribute any key the dimension holds now has a value read for, how many of those keys
(`keys`) and how many distinct values (`values`), counted from the index on attribute and value. A key given the
`unlabelled` value holds no value read. An attribute missing from the list has none: the Definition tab shows each
attribute's share of the keys (`all`, a percentage, or `none`), with the counts on hover.

## On the Definition tab

The diagram runs left to right: the records searched, each depth of records found by id, then the table. A dashed line
runs from a read to the records its ids name, a solid one from a read to the column written from it. Pointing at a line
of the YAML, a read or a column lights everything that makes the same thing, in the diagram and in the YAML; a click
explains it in the panel beside the YAML, with what the template says of each segment of its path. Once the dimension is
built, an example key, stepped through the 25 keys with the most records, fills each part with what it read: the key,
its label and the record it came from, each attribute's value, and its row.
