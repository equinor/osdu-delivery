---
id: delivery-flow-mapping-fragment
title: "Fragments (fragment: true) and $base: the paths a step of enrich adds to a record, and how they are added"
type: flow-reference
summary: "A fragment mapping: no dataset or envelope, written under data and tags, reading its step's rows and the record with $base, and how it adds to a record."
keywords:
  - fragment
  - "fragment: true"
  - "$base"
  - base record
  - read what an earlier step added
  - add items to a list
  - set a path
  - composition
  - conflict
  - enrich mapping
  - partial mapping
yamlPath: "fragment (documentType: mapping), record.<property>.$base"
related:
  - delivery-flow-enrich
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-parent
  - delivery-concept-preflight
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RenderTrail.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/FragmentMappingTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/RecordComposerTests.cs
  - osdu/docs/census/keys.mapping.json
---

# Fragments (fragment: true) and $base: the paths a step of enrich adds to a record, and how they are added

A **fragment** is the mapping a step of a delivery flow's [`enrich:`](enrich.md) renders: the paths the step adds to every
record the flow's own mapping renders, never a record of its own. It is a mapping document like any other, filed in the
flow's `mappings/` folder and pinned as `Name@version`, whose header says `fragment: true`.

## What a fragment declares

| Key | A fragment |
| --- | --- |
| `fragment` | `true`. |
| `template` | The kind and version the flow's mapping fills: a step adds to that record, so both fill one version of one template. |
| `dataset` | None. A fragment keys no record: the record's key, id and label are the flow's mapping's, and the step's rows are declared under `enrich.<step>.dataset`. Declaring one is refused. |
| `parameters` | The values it reads, from the flow's `render.parameters`. `dataPartition` is not required of it. |
| `searches`, `lookups` | As in any mapping. |
| `record` | The paths it adds, under `data` and `tags` only. |

What a fragment cannot write, it cannot change: `acl`, `legal`, `id`, `kind`, `ancestry` and `meta` belong to the record the
flow's mapping renders, so a fragment writing outside `data` and `tags` is refused
(`... a fragment writes under record.data and record.tags only ...`). `$parent` and `$child` find a record by the id the
flow's mapping makes from its key, which a fragment has none of, so they are refused in it too. A fragment of a DSPDM row is
refused, since no step extends one.

The mapping builder lays out whole records, and says so when it is asked to open a fragment; edit its file.

## The rows a fragment reads

- Under `rows: one`, the step's one row is the fragment's own row: `$from: las_file` reads it, as a mapping reads the record
  table's row.
- Under `rows: many`, the step's rows are read with `$forEach: <step>`, the step's own name, as a mapping reads a child
  dataset. No row is the fragment's own, so a column read outside the `$forEach` is refused.
- A step without a dataset has no row: the fragment reads the record with `$base`, literals and its parameters.

The record table's row is not a fragment's to read: what it needs of the record, it reads as the record holds it.

## $base: the record as the steps before left it

`$base: data.<path>` reads a section of the record the fragment adds to: the record as the flow's mapping rendered it and
the steps before this one added to it, read as the step renders, never from the platform.

```yaml
data:
  ExtensionProperties:
    WellDb:
      Note: { $base: data.ExtensionProperties.WellDb.LasFile, $modifiers: [upper] }
  Description:
    $coalesce:
      - $from: comment
      - $base: data.Name
```

- The path is property names joined by dots from the record's root, as `$parent` writes one; a path stepping into an array
  (`data.Curves[].CurveID`) is refused: name the array, and it is taken whole with every item.
- The section is copied whole: an object or a list as the record holds it. A single value passes through `$modifiers`;
  modifiers on an object or a list hold the record.
- A record without the section, or with empty text there, is an empty value, which `$required` decides: held by default
  (`... the record holds no data.Nested.Inner when the fragment renders, and the entry is required`), left out with
  `$required: false`, or the next alternative of a `$coalesce`.
- It takes `$when`, `$required`, `$modifiers`, `$assert` and `$description` beside it, and `$findBy` is refused. It is
  read outside a `$forEach` only, since the record is one for every row: to copy a list, put `$base` on the list itself.
- Only a fragment reads `$base`. A mapping of a whole record reading it is refused.

## How a fragment adds to the record

Two things only, and both give the same record however many times they are done, which is what lets a whole chain render
again on every change:

| The fragment writes | Where the record holds | It |
| --- | --- | --- |
| a value | nothing | sets it. |
| a value | the same value (compared as canonical JSON, so `1` and `1.0` are the same) | changes nothing. |
| an object | nothing | sets it, with everything in it. |
| an object | an object | goes into it, property by property. |
| a list | nothing | sets it. |
| a list | a list | adds each item the list does not hold yet, after the items it holds, in the fragment's order. |

Anything else would take back what the flow's mapping or a step before wrote, and is a **conflict**: a different value
where the record holds one, an object or a list where it holds a single value, a single value where it holds an object or a
list. A conflict holds the record, naming the step and the path, and leaves the record as it was there:

```text
enrich.clash: data.Name: the step sets "other", and the record already holds "a" there from the flow's mapping or a step before it
```

The preflight refuses a chain whose fragments would conflict before anything renders ([enrich](enrich.md#what-the-preflight-checks)),
so a conflict at render time is one the rows made, such as a value the flow's mapping writes only for some records.

## What the preflight checks

A fragment is checked against its template as any mapping is: every path is a property of the template, or lies in an
object it leaves open, every value fits the property it fills, every cache type and search it reads resolves, and every
parameter it needs is supplied. It is not held to the template's required properties: the record the flow's mapping
renders is. The chain as a whole is checked on [enrich](enrich.md#what-the-preflight-checks).

## Errors when a fragment is read

| Written | Message |
| --- | --- |
| `dataset:` beside `fragment: true` | `a fragment (fragment: true) adds paths to the records a delivery flow's own mapping keys, so it declares no dataset; its rows are the step's, declared under enrich.<step>.dataset in the flow. Remove dataset.` |
| `acl:` in a fragment's record | `... fills osdu.acl.owners, and a fragment writes under record.data and record.tags only: the id, kind, access list, legal block and lineage of the record it adds to are the flow's own mapping's.` |
| `$parent` in a fragment | `... $parent finds a record by the id the flow's own mapping makes from its key, and a fragment keys no record; write $parent in the flow's mapping, and read what the record holds with $base.` |
| `$base` under `$forEach` | `$base reads the record the fragment adds to, which is one for every row of <step>; read it outside the $forEach, or take a whole array of it with $base on the array itself.` |
| `$base` in a mapping of a whole record | `... $base reads the record a fragment adds to, as the flow's mapping and the steps before it rendered it, and this mapping renders a record of its own; only a fragment (fragment: true), which a step of enrich: renders, reads one.` |
| A fragment pinned as a flow's `render.mapping` | `render.mapping names <Name@version>, a fragment (fragment: true): the paths a step of enrich: adds to a record, never a record of its own.` |

## A fragment of a step's datasets

A step registering files ([files a step registers](enrich-files.md)) may name a fragment under `files.mapping`, which fills
the dataset kind rather than the record's template and renders once for each of the step's rows into that row's dataset
record: its name, description and tags. It reads the row with `$from` and the record as the chain left it with `$base`;
the file properties (`data.DatasetProperties.FileSourceInfo`, or a collection's) are set when the dataset is registered, and
what the fragment renders elsewhere under `data.DatasetProperties` is kept, as on the dataset route.

## Related

- [Enrich](enrich.md): the steps that render fragments, their rows and their order.
- [The mapping document](mapping.md) and [value nodes](mapping-values.md): everything else a fragment writes as any mapping does.
- [Mapping $parent](mapping-parent.md): the other section a mapping reads of a record, its parent in OSDU.
