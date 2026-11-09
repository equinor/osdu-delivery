---
id: delivery-flow-mapping-property-names
title: "Property names holding a dot: a tag such as lra.securityClassification, and tags['...'] in a path"
type: flow-reference
summary: "A mapping key is one property name, a dot in it included: where the template takes such a name, and how each path writes it, tags['lra.securityClassification']."
keywords:
  - dotted tag
  - tag name with a dot
  - property name with a dot
  - lra tags
  - lra.securityClassification
  - bracket notation
  - quoted name
  - "tags['name']"
  - free keys
  - open object
  - nest the properties a path names
  - is not a property name
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-assertions
  - delivery-flow-assertion
  - delivery-flow-cache-origins
  - delivery-concept-templates
  - delivery-concept-preflight
  - delivery-cli-validate
yamlPath: "record.<property> (documentType: mapping)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Json/RecordPathNotation.cs
  - osdu/src/SqlFlow.Delivery/Json/JsonPathReader.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplatePath.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/ConditionReader.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Validation/SchemaWalk.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SearchFields.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/Evaluators.cs
  - osdu/gui/src/features/delivery/recordPath.ts
  - osdu/specs/core/INTEGRATION.md
---

# Property names holding a dot: a tag such as lra.securityClassification, and tags['...'] in a path

Storage takes any text as the name of a tag: a record's `tags` is a map of string to string
([osdu/specs/core/INTEGRATION.md](../../../specs/core/INTEGRATION.md), the record's `tags`). Some estates name their tags
with a dot, such as `lra.securityClassification`. In a mapping's record tree a key is one property name, whatever it
holds, so such a tag is written as the key it is, beside the record's other tags:

```yaml
record:
  tags:
    DeliveredBy: osdu-delivery
    lra.securityClassification: { $from: security_classification }
    lra.dataID:
      $cache: Classification.data_id
      $findBy: well_bk = well_bk
    Data Owner:
      $from: owner
      $required: false
```

Each tag is a node of its own, with its own lineage, its own `$required`, `$when` and `$assert`: a tag a row gives no
value for is left out (or holds the record, when it is required) while the others are written. The record carries
exactly the names written:

```json
"tags": {
  "DeliveredBy": "osdu-delivery",
  "lra.securityClassification": "Open",
  "lra.dataID": "6632",
  "Data Owner": "Operations"
}
```

## Where a name holding a dot is taken

The key is read as one name wherever it is written; whether the object it is written in takes that name is the
template's to say, and the [preflight](../concepts/preflight.md) checks it before a run sends anything, as it checks every
target.

| Written under | Taken | Why |
| --- | --- | --- |
| `tags`, and any object whose schema gives the type of its free keys (`additionalProperties: { type: ... }`) | Yes, as one key | The object takes any name; the value takes the type its schema gives (text under `tags`). |
| An open object, such as `data.ExtensionProperties`, at any depth | Yes, as one key | The object declares no properties and refuses none ([open objects](mapping-values.md#open-objects)). |
| An object that declares its properties by name: `data`, `data.VerticalMeasurement`, the items of `data.Curves` | No | No schema declares a property named with a dot, so the key is a path written as a key. The preflight refuses it and names the path it meant. |
| The record itself (the keys directly under `record`) | No | The record's own properties (`acl`, `legal`, `tags`, `data`, ...) never hold a dot; the key is refused as the mapping is read. |

Nesting is never the way to write such a tag: `tags: { lra: { securityClassification: ... } }` writes an object under the
tag `lra`, which takes text, and the preflight refuses it, naming the one key it meant.

A key's surrounding spaces are not part of its name, as for every key of the tree; spaces inside it are. A key that is
empty is refused. A name that starts with `$` is written `$$name`, whatever else it holds (`$$lra.marker`).

## How a path writes such a name

Everything that names a property by its path names a name holding a dot, a bracket or a space as one step, in brackets
and single quotes, as JSONPath writes a name (RFC 9535): `tags['lra.securityClassification']`. Every other name is
written after a dot, as it always has been, so no path that holds no such name changes.

| Where | Path |
| --- | --- |
| A template variable: the preflight, a preview, the coverage view, a data check's `--target` and the builder | `osdu.tags['lra.securityClassification']` |
| A place a check of a record names (a schema rule a value breaks, a failed `$assert`) | `tags['lra.securityClassification']` |
| A node's location in the mapping's messages | `record.tags['lra.securityClassification']` |
| A field an assertion flow or a mapping's `$assert` reads (`field`, `where`, `sort`, `groupBy`, `unique`, `recordSet.columns`, `asOfField`) | `tags['lra.securityClassification']` |
| The index a search reads | `tags.lra.securityClassification` |

Reading a path, a name in brackets may be quoted with `'` or `"`, may have blanks around it inside the brackets, and may
follow a dot (`tags.['lra.dataID']`). Inside the quotes a backslash escapes a quote, a backslash, `/`, `b`, `f`, `n`,
`r`, `t` and `uXXXX`; a path writes a quote and a backslash escaped, and a control character as its escape. A quoted name
that a path could write bare is the same name: `osdu.tags['Source']` is `osdu.tags.Source`.

A search names a tag by its name after `tags.`: the index keeps `tags` as a flattened map, which takes a dotted key and
the objects it would name alike, so `tags['lra.securityClassification']` is searched as `tags.lra.securityClassification`.
A tag whose name holds a space, a colon or another character a query reads as syntax cannot be written in a query's text,
and what has to write it there (known values found by it, a search term over it) is refused, naming the tag.

## What keeps plain dotted paths

These name a value of another record, or of the cache, by a path of plain names, and take no name in brackets:
`$parent` and `$base` sections, the fields `$cache`, `$lookup` and `$findAll` read, a cache flow's `fields`, a dimension's
`path` and `label`, and an inventory's duplicate key. To read a tag holding a dot from such a record, read it into a
cached table's column and read the column.

## Offline validation

`sqlflow validate` reads a mapping without its template, so it reads a key holding a dot under `data` as the one name it
is; [`sqlflow check`](../cli/check.md), the builder and every run's preflight read it against the template and refuse it
where the template does not take it.

## Errors

| Situation | Message |
| --- | --- |
| A key holding a dot directly under `record` | `record['data.Name']: 'data.Name' is not a property name. A key in the record is one property of it, so nest the properties a path names rather than writing the path.` |
| An empty key | `record.tags: a key is empty, and a property of the record is named; write its name, or remove it.` |
| A dotted name under an object that declares its properties | `record.data['VerticalMeasurement.VerticalMeasurement'] fills a variable that template <kind> version <version> does not have: osdu.data declares its properties by name, and none is called 'VerticalMeasurement.VerticalMeasurement'. A key is one property name, and a name holding a dot, a bracket or a space is written only where the template takes names it does not declare, such as tags or an open object; to write osdu.data.VerticalMeasurement.VerticalMeasurement, nest the properties a path names rather than writing the path.` |
| A tag name written as nested objects | `record.tags.lra.securityClassification fills a variable that template <kind> version <version> does not have: osdu.tags takes a text under each name it holds, so osdu.tags.lra holds no properties of its own. A name holding a dot is one key: write 'lra.securityClassification' as one key under tags, which fills osdu.tags['lra.securityClassification'].` |
| A name in brackets without quotes in a field | `field 'tags[lra.dataID]' is not a path into a record: names separated by dots, such as data.FacilityName or data.VerticalMeasurements[*].VerticalMeasurement, and a name holding a dot, a bracket or a space in brackets and quotes, such as tags['lra.securityClassification'].` |
| A data check naming a target that is not one | `'osdu.tags[lra.dataID]' is not a variable a check can name: target 'osdu.tags[lra.dataID]' has an invalid property name: [lra.dataID] is not a step: a name in brackets is written in quotes, ['lra.dataID'], and an item by its place, such as [0].` |
| A search over a tag whose name a query cannot name | `tags['Data Owner'] is a tag whose name holds what a query reads as syntax; the search names a tag by its name after tags., in letters, digits, underscores and dots only, so no query can name this one.` |

## Related

- [Mapping](mapping.md): the record tree, and how a key names a property.
- [Mapping value nodes](mapping-values.md): what each node writes, open objects among them.
- [Mapping assertions](mapping-assertions.md) and [assertion flows](assertion.md): the fields they read.
- [Cache origins](cache-origins.md): a table column decoded as JSON, which writes a whole `tags` object, dotted names kept.
- [Templates](../concepts/templates.md): the variables a mapping fills.
