---
id: delivery-flow-mapping-enrichment
title: "Enrichment mappings (enrichment: true) and $base: what an enrichment renders into the record or beside it"
type: flow-reference
summary: "An enrichment mapping keys nothing and writes data and tags: of the record's kind it adds paths, of another kind it renders the records an enrichment adds."
keywords:
  - enrichment mapping
  - "enrichment: true"
  - fragment
  - "$base"
  - base record
  - read what an earlier enrichment added
  - add items to a list
  - set a path
  - composition
  - conflict
  - partial mapping
yamlPath: "enrichment (documentType: mapping), record.<property>.$base"
related:
  - delivery-flow-enrich
  - delivery-flow-enrich-records
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-parent
  - delivery-concept-preflight
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RenderTrail.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/docs/census/keys.mapping.json
---

# Enrichment mappings (enrichment: true) and $base: what an enrichment renders into the record or beside it

An **enrichment mapping** is what an [enrichment](enrich.md) of another mapping renders for each record that mapping renders.
It is a mapping document like any other, filed in the flow's `mappings/` folder and pinned as `Name@version` by the
enrichment, whose header says `enrichment: true`. It keys no record of its own: the record's key, id, access list and legal
block are the mapping's that keys it. What it renders depends on the kind it fills:

- **The record's own kind and template version:** the paths it adds to the record, composed into it
  ([how an enrichment adds to the record](#how-an-enrichment-adds-to-the-record)).
- **Any other kind:** the record the enrichment adds for each of its rows, beside the record
  ([records an enrichment adds](enrich-records.md)).

## What an enrichment mapping declares

| Key | An enrichment mapping |
| --- | --- |
| `enrichment` | `true`. |
| `template` | The kind and version it fills: the record's to add paths, another kind's to render the records the enrichment adds. |
| `dataset` | None. It keys no record: the record's key, id and label are the keying mapping's, and its rows are its enrichment's, the child dataset the flow binds under the enrichment's name. Declaring one is refused. |
| `enrich` | None. Every enrichment of a record is declared by the mapping that keys it. Declaring one is refused. |
| `parameters` | The values it reads, from the flow's `render.parameters`. `dataPartition` is not required of it. |
| `searches`, `lookups` | As in any mapping. |
| `record` | What it renders, under `data` and `tags` only. |

What an enrichment mapping cannot write, it cannot change: `acl`, `legal`, `id`, `kind`, `ancestry` and `meta` belong to the
record the keying mapping renders, and a record an enrichment adds carries that record's access list and legal block. An
enrichment mapping writing outside `data` and `tags` is refused (`... an enrichment mapping writes under record.data and
record.tags only ...`). `$parent` and `$child` find a record by the id the keying mapping makes from its key, which an
enrichment mapping has none of, so they are refused in it too, and so is `$link`, which names the keying mapping's
enrichments. An enrichment mapping of a DSPDM row is refused, since no enrichment extends one.

The old header `fragment: true` is refused with the word to write: `fragment: is written enrichment: true`.

The mapping builder lays out whole records, and says so when it is asked to open an enrichment mapping or a mapping declaring
`enrich:`; edit their files.

## The rows an enrichment mapping reads

- Under `rows: one`, the enrichment's one row is the mapping's own row: `$from: las_file` reads it, as a mapping reads the
  record table's row.
- Under `rows: many`, the enrichment's rows are read with `$forEach: <name>`, the enrichment's own name, as a mapping reads a
  child dataset. One rendering the records an enrichment adds renders once for each row, that row its own.
- An enrichment without rows has none: it reads the record with `$base`, literals and its parameters.

The record table's row is not an enrichment mapping's to read: what it needs of the record, it reads as the record holds it.

## $base: the record as the enrichments before left it

`$base: data.<path>` reads a section of the record: as the keying mapping rendered it and the enrichments before this one
added to it, read as the enrichment renders, never from the platform.

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
- It takes `$when`, `$required`, `$modifiers`, `$assert` and `$description` beside it, and `$findBy` is refused. It is read
  outside a `$forEach` only, since the record is one for every row: to copy a list, put `$base` on the list itself.
- Only an enrichment mapping reads `$base`. A mapping keying records of its own reading it is refused.

## How an enrichment adds to the record

An enrichment mapping of the record's kind does two things only, and both give the same record however many times they are
done, which is what lets a whole chain render again on every change:

| It writes | Where the record holds | It |
| --- | --- | --- |
| a value | nothing | sets it. |
| a value | the same value (compared as canonical JSON, so `1` and `1.0` are the same) | changes nothing. |
| an object | nothing | sets it, with everything in it. |
| an object | an object | goes into it, property by property. |
| a list | nothing | sets it. |
| a list | a list | adds each item the list does not hold yet, after the items it holds, in its own order. |

Anything else would take back what the keying mapping or an enrichment before wrote, and is a **conflict**: a different value
where the record holds one, an object or a list where it holds a single value, a single value where it holds an object or a
list. A conflict holds the record, naming the enrichment and the path, and leaves the record as it was there:

```text
enrich.clash: data.Name: the step sets "other", and the record already holds "a" there from the flow's mapping or a step before it
```

The preflight refuses a chain whose enrichments would conflict before anything renders ([enrich](enrich.md#what-the-preflight-checks)),
so a conflict at render time is one the rows made, such as a value the keying mapping writes only for some records.

## What the preflight checks

An enrichment mapping is checked against its template as any mapping is: every path is a property of the template, or lies in
an object it leaves open, every value fits the property it fills, every cache type and search it reads resolves, and every
parameter it needs is supplied. It is not held to the template's required properties: the record the keying mapping renders
is, and an added record is checked as it is written. The chain as a whole is checked on [enrich](enrich.md#what-the-preflight-checks).

## Errors when an enrichment mapping is read

| Written | Message |
| --- | --- |
| `fragment: true` | `fragment: is written enrichment: true: an enrichment mapping is what an enrichment of another mapping (its enrich: block) renders for each of that mapping's records ...` |
| `dataset:` beside `enrichment: true` | `an enrichment mapping (enrichment: true) renders part of the records another mapping keys, so it declares no dataset; its rows are its enrichment's, the child dataset of the enrichment's name the flow binds under source.datasets. Remove dataset.` |
| `enrich:` beside `enrichment: true` | `an enrichment mapping (enrichment: true) declares no enrichments of its own; declare every enrichment of the record in the enrich: block of the mapping that keys it ...` |
| `acl:` in its record | `... fills osdu.acl.owners, and an enrichment mapping writes under record.data and record.tags only ...` |
| `$parent` in it | `... $parent finds a record by the id the keying mapping makes from its key, and an enrichment mapping keys no record ...` |
| `$base` under `$forEach` | `$base reads the record the fragment adds to, which is one for every row of <name>; read it outside the $forEach, or take a whole array of it with $base on the array itself.` |
| `$base` in a mapping keying records | `... $base reads the record an enrichment renders into ... only an enrichment mapping (enrichment: true) reads one.` |
| It is pinned as a flow's `render.mapping` | `render.mapping names <Name@version>, an enrichment mapping (enrichment: true): what an enrichment of another mapping renders for its records, never a record keyed of its own ...` |
| A mapping keying records named by an enrichment | `enrich.<name> of the mapping <Name@version> names <Name@version>, a mapping that keys records of its own; an enrichment renders an enrichment mapping (enrichment: true) ...` |

## Related

- [Enrichments](enrich.md): the enrich block, its rows and how the chain renders.
- [Records an enrichment adds](enrich-records.md): what an enrichment mapping of another kind renders, and how it is written.
- [Mapping values](mapping-values.md): every value node an enrichment mapping writes with.
- [The mapping document](mapping.md): the header and the record tree every mapping shares.
