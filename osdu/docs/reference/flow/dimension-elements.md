---
id: delivery-flow-dimension-elements
title: "Dimension elements: the objects of a nested array, a row each with their fields"
type: flow-reference
summary: "A dimension's elements: a row per object of a nested array, its fields read inside it and up its path, numbered, nulls kept or not."
keywords:
  - elements
  - nested array rows
  - element column
  - fields
  - up
  - "nulls: keep"
  - "many: join"
yamlPath: "dimensions[].elements"
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-attributes
  - delivery-flow-dimension-views
  - delivery-flow-dimension-table
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionElementReader.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009160253_DimensionElements.cs
---

# Dimension elements: the objects of a nested array, a row each with their fields

A [dimension](dimension.md) with `elements` makes a row of every object of a nested array of each record, beside the key of the record holding it.

`elements` makes a row of every object of a nested array, beside the key of the record holding it, with every field of
one object on its row: what a collected attribute cannot do, since it collects one value a row. On a dimension keyed by a
well log's `id`, `elements: { path: data.Curves, ... }` is a table of curves, each curve's mnemonic, unit and depths
together:

```yaml
- name: LogCurve
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  path: id
  label: data.Name
  columns: { key: WellLogID, value: WellLogName }
  elements:
    path: data.Curves
    fields:
      Mnemonic: Mnemonic
      CurveUnit: CurveUnit                          # m, gAPI: the code a person reads
      CurveUnitID: { path: CurveUnit, keep: id }    # joins dim_RefUnitOfMeasure, keyed by id
      TopDepth: TopDepth
      LogVersion: { path: LogVersion, up: 1 }       # the log's own, on every curve's row
```

| Key | Default | Meaning |
| --- | --- | --- |
| `path` | required | The array, from the record's root, written as a label's path is. Every array on the way is stepped into, so `data.Curves.Columns` makes a row of every object of every curve's `Columns`; any segment can filter the objects it holds (`data.GeoContexts[GeoTypeID$=:Field:]`). What it ends at is the element: an object, or a plain value. |
| `fields` | required | The columns, each under its name: a path inside the element (`CurveUnit`, `Quantity.Code`, `Values[Type=Top].Depth`), `@` for the element itself (an array of plain values), or settings. |
| `fields.<name>.keep` | `value` | As an attribute's `keep`. |
| `fields.<name>.up` | 0 | How many objects up the element's path the field is read from: 1 the object holding the array, as many as the path has segments for the record. Not with `@`. |
| `fields.<name>.many` | `first` | A path reaching several values in one element: `first`, the first that is not empty; `join`, each once, in order, joined by `; `; `all`, every one as often as it is held; or `max`, the greatest, as an attribute's `many` says. |
| `fields.<name>.decode` | `none` | `url` decodes each value's percent-escapes before it is kept, as an attribute's `decode` says. Only with `keep: value`. |
| `fields.<name>.trim` | `true` | `false` keeps each value as the record holds it, white space at its ends too, as an attribute's `trim` says. Only with `keep: value`. |
| `nulls` | `skip` | What a null in the array is: `skip`, no element; `keep`, an element whose every field is null (a field read up the path still reads the object holding the array), as Spark's explode keeps one. A null a filter compares, and one on the way to the elements, is never one. |

The table gains an `element` column: each key's objects numbered from 1, in the order of the records' ids and then of
the objects in each record, so the same records number the same way build after build and a row keeps its `id`. A key
whose records hold no object is one row with no element; a view leaves such rows out with `where: element IS NOT NULL`.
A field keeps up to 4,000 characters, where an attribute keeps 256: no index holds a field, so a curve's description or a
list joined by `many: join` is kept whole. A field reaching no value is null on its row; a number reads
as written (`203.149`), a boolean as `true` or `false`; a null in the array is no element unless `nulls: keep`.

A build reads the elements in one pass over the dimension's records through the search cursor, asking only for the
fields read (and the properties the filters compare), cut into ranges of keys read side by side; at most 5,000,000
objects a build, past which it fails before writing. Attributes and fields together are at most 64 columns, field names
are unlike every attribute's and `element`, and a dimension with elements collects no attribute, since both make a row
per value.
