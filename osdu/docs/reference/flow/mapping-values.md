---
id: delivery-flow-mapping-values
title: "Mapping value nodes: $from, $value, $when, $required, $coalesce, $forEach, lists and open objects"
type: flow-reference
summary: "How each property in a mapping's record tree gets its value: columns, literals, conditions, required, first-of alternatives, repeated arrays and lists."
keywords:
  - value node
  - "$from"
  - "$value"
  - "$when"
  - "$required"
  - "$coalesce"
  - "$foreach"
  - "$item"
  - "$where"
  - "$dataset"
  - list of values
  - list of objects
  - open object
  - child dataset
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-expressions
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-parent
  - delivery-flow-mapping-child
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-assertions
  - delivery-concept-preflight
  - delivery-flow-delivery
  - delivery-guide-writing-a-mapping
yamlPath: "record.<property> (documentType: mapping)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.Id.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ListValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/NumberValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SourceRow.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ListOfObjectsTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/OpenObjectTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ExpressionTests.cs
  - osdu/docs/census/keys.mapping.json
---

# Mapping value nodes: $from, $value, $when, $required, $coalesce, $forEach, lists and open objects

Every property in a [mapping's](mapping.md) `record` tree is a node that says where its value comes from. This page is
the reference for those nodes: reading a column, writing a literal, deciding when a property applies and what an empty
value does, taking the first of several sources, repeating a child dataset's rows as an array, and filling lists and
open objects. Every word of the mapping language starts with `$`; every other key is a property of the record.

## An example

A wellbore mapping for `osdu:wks:master-data--Wellbore:1.3.0`, its record table `OsduData.silver.Wellbore` (key
`wellbore_id`):

```yaml
record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers:
      - "{$param.aclViewer}"           # a literal every record carries
      - $from: viewer_group            # and a group the row names, when it names one
        $required: false
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    FacilityName:                      # a column, trimmed
      $from: wellbore_name
      $modifiers: [trim]
    FacilityID: { $from: wellbore_id }
    Source: welldb                     # a literal
    SequenceNumber:                    # an integer property: the column's text "2" is written as 2
      $from: sequence_number
      $required: false                 # no sequence number leaves the property out
    FacilityDescription:
      $coalesce:                       # the first alternative that gives a value
        - $from: wellbore_remark
        - $expr: wellbore_name & " (" & wellbore_uwi & ")"
      $description: The source's remark, else the name and the UWI.
    NameAliases:                       # a list of objects, each item laid out as the record is
      - AliasName: { $from: wellbore_uwi }
      - AliasName:
          $from: wellbore_short_name
          $required: false             # an item none of whose properties gives a value adds nothing
    VerticalMeasurements:
      - VerticalMeasurementID: KB
        VerticalMeasurement: { $from: kb_elevation }
        VerticalMeasurementTypeID: "{$param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"
      - VerticalMeasurementID:
          $value: RT
          $when: not empty(rt_elevation)
        VerticalMeasurement:
          $from: rt_elevation
          $required: false
        VerticalMeasurementTypeID:
          $value: "{$param.dataPartition}:reference-data--VerticalMeasurementType:RotaryTable:"
          $when: not empty(rt_elevation)
    ExtensionProperties:               # an open object: laid out as the record is, values written as they arrive
      WellDb:
        Status:
          $from: wellbore_status
          $required: false
```

This block, under the header of `mappings/Wellbore@1.0.0.yaml` (system `welldb`, key `[wellbore_id]`, the four
parameters `dataPartition`, `aclOwner`, `aclViewer` and `legalTag`), validates with `sqlflow validate`.

## The words of a value node

A value node reads its value with exactly one of these:

| Word | Reads |
| --- | --- |
| `$from` | A column: a bare name reads the row the node is in, `$dataset.<column>` the record table's row from anywhere. |
| `$expr` | A value an [expression](mapping-expressions.md) computes from the row: `coalesce(log_name, log_source)`. |
| `$value` | A literal written as it is: text, a number, a boolean, a list or an object. |
| `$cache` | A field of the cached record `$findBy` finds (`UnitOfMeasure.id`), or of every row `$findAll` finds. See [mapping lookups](mapping-lookups.md). |
| `$search` | The id of the one record a search of the platform finds by `$findBy`, in one of the mapping's `searches`. See [mapping lookups](mapping-lookups.md). |
| `$lookup` | A field of the record a lookup of the `lookups` block finds (`wellbore.id`). See [mapping lookups](mapping-lookups.md). |
| `$parent` | A section of the record's parent, by its path (`data.DDMSDatasets`), for a template linked to a parent. See [mapping $parent](mapping-parent.md). |
| `$child` | The reference to the record's child of a kind (`welldb:wks:work-product-component--WellLogDetail:1.0.0`) whose template is linked to the mapping's own, once OSDU holds it. See [mapping $child](mapping-child.md). |
| `$base` | In an enrichment mapping only: a section of the record (`data.Name`), as its mapping and the enrichments before it left it. See [enrichment mappings](mapping-enrichment.md#base-the-record-as-the-enrichments-before-left-it). |
| `$link` | The ids of the records an enrichment of the mapping adds to the record, each with the version separator and no version: every one as an item of a list of values, the one as a value of its own. See [$link](#link). |
| `$coalesce` | The first of two or more alternatives that gives a value. See [$coalesce](#coalesce-the-first-value-that-is-there). |

and takes its settings beside it:

| Setting | Meaning | Taken by |
| --- | --- | --- |
| `$modifiers` | Changes to an incoming dataset value, top to bottom. See [mapping modifiers](mapping-modifiers.md). | `$from`, `$expr`, `$cache`, `$search`, and `$parent` on a single value |
| `$when` | A condition: when the property applies to the row. See [$when](#when-when-a-property-applies). | every node |
| `$required` | What an empty value does: `true` (the default) holds the record, `false` leaves the property out. See [$required](#required-what-an-empty-value-does). | every node but `$value`; only `false` on `$child`, its default |
| `$description` | Free text. | every node |
| `$findBy` | Which record to read: one line or a list, tried in order. | `$cache`, `$search` |
| `$findAll` | Every cached row that matches, read as a list. | `$cache` |
| `$ignoreSeparators` | A last attempt at matching with punctuation and spacing folded away. | `$cache` |
| `$unverified` | Write an id an `id` or `ref` modifier builds even when the cache does not hold its record. See [mapping modifiers](mapping-modifiers.md). | `$from`, `$expr` with `id` or `ref` |
| `$assert` | Business rules the value must meet, each holding, reporting or leaving out a record that breaks it. See [mapping assertions](mapping-assertions.md). | every node but `$value`, an alternative of `$coalesce` and an item of a list of values |

A `$value` takes only `$when` and `$description`; a `$lookup` takes only `$when`, `$required`, `$assert` and
`$description`; a `$coalesce` takes `$when`, `$required`, `$assert` and `$description` beside its list; a `$forEach`
takes `$item`, `$where`, `$when`, `$required`, `$assert` and `$description`. Anything else is refused by name, such as
`record.data.Description: a literal $value takes only $when and $description beside it; $findBy, $findAll, $modifiers, $required, $ignoreSeparators and $unverified belong to a node that reads the dataset, the cache or a search.`

## Reading a column: $from

`$from: <column>` writes the value of one column. A bare name reads the row the node is in: the record table's row, or
under a `$forEach` the child row. `$dataset.<column>` reads the record table's row from anywhere, inside a `$forEach`
included. Column names are letters, digits, `_` and `-`, and are matched ignoring case. Every column a mapping reads is
checked against the flow's ingestion tables before a run ([preflight](../concepts/preflight.md)).

The form the language used to write is refused with the one to write now:

```text
record.data.Name: $from: 'dataset.log_name' reads a column the way the mapping language no longer writes it: a column of the dataset's own row is $dataset.log_name, and a column of the dataset's own row is log_name alone.
```

A column holding blank text is no value, the same as a missing one.

## Literals and $value

A property written as a plain value is a literal, carried by every record: `Source: welldb`, `ReferenceCurveID: MD`,
`Tags: [welldb, well-log]`. A literal's text reads a parameter as `{$param.<name>}` and nothing else, so
`"{$param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"` becomes
`dev:reference-data--VerticalMeasurementType:KellyBushing:` in partition `dev`. Any other `{$...}` token, or a parameter
written without its marker, is refused:

```text
record.data.Name: the literal '{$dataset.log_name} run' holds {$dataset.log_name}, and a literal reads only a parameter, {$param.<name>}; read a column's value with $from, and build an id from values with the id modifier.
```

Text in braces that is not a token, such as `{log_name}`, is written as it stands. An unquoted YAML scalar keeps the
type YAML reads it as (`Count: 5` is a number, `Count: "5"` is text), and the template's type decides what the record
carries ([below](#how-a-value-takes-its-propertys-type)).

`$value` is a literal with settings: a text, number, boolean, list or object written as it is, with a `$when` and a
`$description` beside it. Use it for a literal that applies only on some rows, or for an object written whole:

```yaml
LogRemark:
  $value: Reprocessed by the vendor
  $when: log_status = "REPROCESSED"
```

A map of properties is an object laid out property by property; to write an object as one literal, wrap it in
`$value`. An empty object is written `{ $value: {} }`, and an empty `$value` is refused
(`$value is empty; leave the property out by removing it.`). A list of objects written entirely as literals is carried
by every record as it is. A literal takes no `$required`: it always has a value.

A property whose own name starts with `$` is written with one more `$` in the tree (`$$Odd: kept` writes `"$Odd": "kept"`);
what `$value` holds is written verbatim.

## Computing a value: $expr

`$expr` writes the value an [expression](mapping-expressions.md) computes from the row, the record table's row and the
parameters. The value then passes through `$modifiers` and `$required` like a column's, so `$expr` takes the settings
`$from` takes. An expression that reads no column is refused, since it would give every record the same value; write it
as a literal.

## From the cache or the platform

`$cache`, `$search` and `$lookup` read values the source row does not hold: a record of the partition's cache, a record
the platform is searched for, or a record a named lookup finds once per row. `$findBy` says which record, `$findAll`
reads every matching cached row as a list, and `$ignoreSeparators` loosens a name match. [Mapping lookups](mapping-lookups.md)
documents all of them. `$modifiers` on such a node change the dataset value its `$findBy` compares; what the cache or the
platform gives is never modified.

`$parent` reads a section of the record's parent, the record of the canonical kind a custom template is linked to with
the same minted id, from the platform as the record renders, and copies it whole; a single value it reads passes
through its `$modifiers` as a column's does ([mapping $parent](mapping-parent.md)).

`$child` links the record's child, the record of a custom template linked to the mapping's own kind with the same minted
id, by its reference once OSDU holds it, and leaves the property out until then without holding the record
([mapping $child](mapping-child.md)).

## $link

`$link: <name>` links what the mapping's enrichment `<name>` adds to the record ([records an enrichment adds](enrich-records.md#linking-with-link)):
the ids made from the record's own id, the enrichment and its rows, each with the version separator and no version, so the
record names them before anything is sent.

```yaml
enrich:
  lasfile: { rows: many, key: [file_name], mapping: WellLogLasDataset@1.0.0 }
record:
  data:
    Datasets:
      - "{$param.dataPartition}:dataset--File.Generic:shared-legend:"   # a literal every record carries
      - $link: lasfile                                                 # and every LAS file the log's rows add
```

As an item of a list of values it gives every id, after the items before it; as a value of its own, the one an enrichment
taking one row adds. It takes `$when` and `$description`, never holds the record (an enrichment adding nothing for it gives
nothing, and the property is left out), and is written for the record as a whole: never under a `$forEach`, in an item of
a list of objects, in a `$coalesce`, or in an enrichment mapping. It names an enrichment of the same mapping whose enrichment
mapping fills another kind than the record's.

## Changing a value: $modifiers

`$modifiers` is a list applied top to bottom to the incoming value: `trim`, `upper`, `lower`, `split`, `replace`,
`equals`, `date`, `number`, `id` and `ref`. `id` and `ref` build an OSDU id from the value and are always last;
`$unverified: true` lets an id they build go out when the cache holds records of its entity type but not this one.
[Mapping modifiers](mapping-modifiers.md) documents each.

## $when: when a property applies

`$when` is a condition, an [expression](mapping-expressions.md) that gives true or false, read against the row the node
is in. When it does not hold, the property is left out for that row whatever `$required` says. A condition that the row
gives a value it cannot test (text where a number is compared) holds the record with the reason, because whether the
property belongs in the record is then unknown.

- On a value node, a `$coalesce` or a `$lookup`, it decides for that property.
- On a `$forEach`, it decides for the whole array and reads the record table's row; `$where` decides for each child row.
- On a property of an item of a list of objects, it decides for that property; an item has no settings of its own.
- The four access and legal lists themselves take none: they apply to every record. A value node among the items of
  `acl.owners` or `acl.viewers` may have one.

## $required: what an empty value does

`$required` is `true` by default: a value the node should give and does not holds the record, and the ledger records why.
`$required: false` leaves the property out instead. It never introduces a value, and it never lets a mistake through. A
`$child` node is the one exception: its default is `false`, and `$required: true` is refused, since a child is never a
reason to hold its parent ([mapping $child](mapping-child.md#what-a-child-node-gives)).

| Situation | `$required: true` (default) | `$required: false` |
| --- | --- | --- |
| The column, or what `$expr` gives, is empty after the modifiers | Record held | Property left out |
| The cache holds no record `$findBy` finds, or no row `$findAll` finds | Record held | Property left out |
| An `id` or `ref` builds an id of an entity type the cache holds, and the cache holds no record with that id | Record held (unless `$unverified`) | Property left out (unless `$unverified`) |
| A token of an `id` template has no value | Record held | Property left out |
| No record on the platform matches a search | Record held | Property left out |
| No alternative of a `$coalesce` gives a value | Record held, naming why each gave nothing | Property left out |
| A `$forEach` finds no child row, none its `$where` keeps, or none that gives its item a value | Record held | List written empty |
| `$when` does not hold | Property left out | Property left out |
| Several cached records answer to the value | Record held | Record held |
| Several platform records match, the search is refused, or the value cannot be searched for | Record held | Record held |
| A `date` or `number` modifier cannot read the value, or the value cannot take the property's type | Record held | Record held |
| An expression or a condition meets a value it cannot work with | Record held | Record held |

A held record's reason names the variable and what was missing:

```text
osdu.data.TechnicalAssurances[].Comment: dataset.remark is empty, and the entry is required
osdu.data.Depth: nullif(depth, -999) * 0.3048 gives no value, and the entry is required
osdu.data.Curves: dataset.curves has no rows with values, and the entry is required
```

A property the schema requires in `data` must not be `$required: false`; the [preflight](../concepts/preflight.md)
refuses that, since the record could go out without it.

## $coalesce: the first value that is there

A property is often best taken from one place and, where that gives nothing, from another. A `$coalesce` node lists its
alternatives in the order they are tried, each a value node of its own with its own `$findBy`, `$modifiers`,
`$ignoreSeparators` and `$unverified`, and writes the first that gives a value:

```yaml
Name:
  $coalesce:
    - $from: log_name
    - $from: log_source
      $modifiers: [upper]
    - $value: unnamed              # a literal is the last alternative
  $description: The log's own name, else its source.
```

- **A miss passes to the next alternative**: an empty value, no cached record, a key a lookup table does not list, a
  search that finds nothing, an id the partition's cache holds no record under.
- **A mistake holds the record**, as the alternative would on its own: a date that is not a date, a value several
  records answer to, a search the platform refuses. What it would have given is unknown rather than absent, so a later
  alternative never stands in for it.
- **A search not asked yet stops the node** until the answer is in, so a later alternative never stands in for an
  earlier one the platform has not been asked.
- **`$when`, `$required`, `$assert` and `$description` go beside `$coalesce`** and decide for all the alternatives;
  written in an alternative they are refused. An assertion judges the value whichever alternative gave it. When none gives a value, `$required` decides, and a held record names why each gave
  nothing (`none of the 3 alternatives of $coalesce gives a value, and the entry is required: 1. ...; 2. ...; 3. ...`).
- **An alternative reads one value**: it is not a `$forEach` or a `$coalesce` of its own, and a literal (`$value`) can
  only be the last, so a `$coalesce` ending in one fills its property on every row.
- **What every alternative tried read from the cache is recorded**, so a later cache version that would let an earlier
  alternative give a value reaches the record. A preview (`sqlflow preview`) says which alternative gave each value.

A `$coalesce` lists two or more alternatives:
`$coalesce lists 1 alternative, and it takes the first of two or more that gives a value; one alternative is a node of its own, written without $coalesce.`
For values computed from columns alone, the `coalesce()` function of an [expression](mapping-expressions.md) is often
simpler.

## $forEach: repeated arrays from a child dataset

A `$forEach` node fills an array of objects with one item per row of a child dataset: a table the flow declares under
`source.datasets` by that name, joined to the record table on the record key ([delivery flow](delivery.md)). `$item` lays
out the properties each row fills, and its bare column names read the child row:

```yaml
Curves:
  $forEach: curves
  $where: curve_id != "DEPT" and not empty(curve_unit)
  $item:
    CurveID: { $from: curve_id }
    TopDepth: { $from: top_depth }
    BaseDepth: { $from: base_depth }
    CurveDescription:
      $expr: coalesce(curve_description, curve_id & " of " & $dataset.log_name)
```

| Key | Meaning |
| --- | --- |
| `$forEach` | The child dataset whose rows become the items (letters, digits, `_` and `-`). |
| `$item` | The properties each row fills, each a node as anywhere in the tree. |
| `$where` | A condition each child row must hold to become an item, read against the child row. |
| `$when` | Whether the array is written at all, read against the record table's row. |
| `$required` | `true` (default) holds a record that ends with no item; `false` writes the list empty. |
| `$description` | Free text. |

An item none of whose properties gives a value is left out of the array. Two `$forEach` nodes may repeat the same child
dataset (the curves of a log, and the source's own notes about them in `ExtensionProperties`). A path steps into one array
at most, so these are refused by name:

| Refused | Message |
| --- | --- |
| A `$forEach` inside a `$forEach`'s item | `record.data.Curves.$item.Samples repeats rows inside the items of the $forEach over curves; a repeated array inside a repeated item is not supported.` |
| A `$forEach` inside an item of a list of objects | `... repeats rows inside an item of the list of objects at record.data.TechnicalAssurances; a repeated array inside the item of a list is not supported.` |
| An array of values from rows | `record.data.Tags.$item is a node that reads one value, and the items of a repeated array are objects here: ... An array of values from rows is not supported yet.` |
| No `$item` | `record.data.Curves repeats the rows of curves and lays out no item; $item names each property a row fills, such as $item: { CurveID: { $from: curve_id } }.` |
| `$item` without `$forEach` | `record.data.Curves lays out $item, the items of an array whose rows a $forEach node repeats; add $forEach: <child dataset>.` |
| A value read beside `$forEach` | `... repeats rows with $forEach and reads a value with $from; a node does one of them. What each row gives is a property under $item.` |

## Lists of values

A list some of whose items are value nodes is a list of values: each item is a literal or a value node (a `$findAll`
among them), and the list is what they give, one after another, a value given twice (whatever its case) written once
where it is first given. An item that gives nothing adds nothing; one that holds holds the record, as it would on its
own. A list none of whose items gives a value is written empty, since the mapping defines it.

```yaml
viewers:
  - "{$param.aclViewer}"           # a literal every record carries
  - $from: viewer_group            # and a group the row names, when it names one
    $required: false
```

An item is never a list, a `$coalesce` (write each alternative as an item: the list keeps every value its items give) or
a `$forEach`. `acl.owners` and `acl.viewers` take a list of values with at least one literal; the legal lists take
literals only ([mapping](mapping.md#access-and-legal-lists)).

## Lists of objects

A list some of whose items are objects is a list of objects. Each item is an object laid out as the record tree lays out
an object: its properties are literals, objects, value nodes, `$coalesce` nodes or lists of values, with their own
`$modifiers`, `$when` and `$required`, and they read the record table's row. Each property fills the variable of the
list's items it names (`record.data.TechnicalAssurances[0].TechnicalAssuranceTypeID` fills
`osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID`) and is converted to that variable's type; a `ref` builds the
reference of the entity type that variable points to.

```yaml
TechnicalAssurances:
  - TechnicalAssuranceTypeID:
      $expr: iif(log_status = "APPROVED", "Certified", "Unevaluated")
      $modifiers: [ref]
    Comment: Set by the well database export
```

- The list is the objects its items give, in the order the items are written, an object two items give alike written
  once.
- A property that gives no value is left out of its item; an item none of whose properties gives a value adds nothing;
  a list none of whose items gives one is written empty.
- A property that holds holds the record, naming the variable inside the items
  (`osdu.data.TechnicalAssurances[].Comment: dataset.remark is empty, and the entry is required`).
- An item written entirely as literals is carried by every record.
- An item has no settings of its own, so a condition goes on the properties it decides for. The wellbore example above
  writes its second vertical measurement only when `rt_elevation` has a value by giving each of its properties the
  same condition.
- A list is one of objects or one of values: `record.data.TechnicalAssurances[1] is a value, and record.data.TechnicalAssurances[0] is an object: the items of a list are all objects, or all values.`
- An item never repeats rows (no `$forEach` inside it), never holds a list of objects of its own, and a list of objects
  is never inside the items of a `$forEach`.

For an array with one item per child row, use a [`$forEach`](#foreach-repeated-arrays-from-a-child-dataset).

## Open objects

An object the schema leaves open declares no properties, names no type for its keys and refuses none:
`data.ExtensionProperties`, which every OSDU kind carries, is one. A mapping lays out what it writes inside one as it
lays out the record: objects, values, literals, lists and a `$forEach`'s items, at any depth.

```yaml
ExtensionProperties:
  WellDb:
    Curves:
      $forEach: curves
      $required: false
      $item:
        CurveID: { $from: curve_id }
        SourceUnit:
          $from: curve_unit
          $required: false
```

- Nothing in the schema types what goes there, so a value is written as it arrives: text stays text (`0100` included),
  unless a modifier such as `number` or `date` writes another form, and nothing checks its shape.
- Everything else holds as anywhere: a value is required unless it says otherwise, a list the mapping defines and no row
  fills is written empty, and a path steps into one array at most.
- A single value written over the open object itself is refused by the preflight (`... is one value, and osdu.data.ExtensionProperties is an object the schema does not break into properties; fill the properties inside it instead`);
  write the properties inside it, or the whole object as a `$value`.
- An object that refuses undeclared keys (`additionalProperties: false`) or offers a choice of forms (`oneOf`, `anyOf`) is
  not open; a property its schema does not declare is refused there as anywhere.
- A flow whose route carries a key over from the stored record into every update (`target.protocolOptions.preserveDataKeys`)
  refuses a mapping that writes under that key, since what it wrote would reach a record only when it is created
  ([routes](routes.md)).

## How a value takes its property's type

The template decides what the record carries, whatever the source column's type:

| The property takes | What is written |
| --- | --- |
| A string | Text as it is; a number in its shortest form; `true` or `false`; a date value in RFC 3339: a full-date where the format is `date` (a value with a time of day holds the record there), a UTC date-time otherwise. Text is never read as a date without the `date` modifier. |
| A number | A number, or text that states one with `.` before the decimals (`"23.5"` is 23.5). Text in another form needs the `number` modifier. A whole number is written exactly, anything else as the nearest double. |
| An integer | A whole number inside the range the property's format declares (`int32`, or 64 bits otherwise). A fraction or a value out of range holds the record. |
| A boolean | `true` or `false`; text `true`, `1`, `yes`, `y`, `false`, `0`, `no` or `n`, ignoring case. Anything else holds the record. |
| A list of values | A list, each item converted to the item type; a single value becomes a list of one. |
| An object | An object (a `$value`, or a cached field holding one). A single value on an object fails the preflight. |
| A list of objects | A list of objects; one object becomes a list of one. |
| Nothing typed (inside an open object) | The value as it arrives: text stays text, a number stays a number, a date value is written as a UTC date-time. |

Several values given to a property that takes one hold the record:
`<n> values were given but the template takes one <type>; read one value or fill a list`. A value that cannot take the
type holds it with the value and the reason (`value 'many' is not a valid integer (...)`). NaN and Infinity are never
written. [Mapping](mapping.md#what-the-record-contains) has what the record carries as a whole: what is left out, lists
written empty, and `meta`.

## Errors

Each message starts with the file. The loader refuses, when the mapping is read:

| Mistake | Message |
| --- | --- |
| A word mixed with properties | `record.data.Name holds '$from', words of the mapping language, beside the property 'Description'; a node's keys all start with '$'. ...` |
| A misspelled word | `record.data.Name: '$form' is not a word of the mapping language. Did you mean '$from'? A property whose name starts with '$' is written $$form.` |
| Two sources | `record.data.Description reads its value with $from and $expr; a node reads one of $from, $expr, $value, $cache, $search, $lookup or $coalesce.` |
| No source | `record.data.Description reads no value: a node reads one with $from, $expr, $value, $cache, $search, $lookup or $coalesce, or repeats the rows of a child dataset with $forEach, and takes its settings ($when) beside it.` |
| A property with no value | `record.data.Description has no value; give it one, or remove it.` |
| An empty object | `record.data.VerticalMeasurement is an empty object; name the properties it holds, or write { $value: {} } for an empty object.` |
| `$required: yes` | `record.data.LogRun: $required is true or false.` |
| `$unverified` without `id` or `ref` | `record.data.LogRun: $unverified lets an id the node builds with id or ref go out when the cache holds no record under it, and this node builds no id; build one, or remove $unverified.` |
| A literal before the last alternative | `record.data.Description.$coalesce[1] is never tried: the literal before it always gives a value. A literal is the last alternative, the value taken when none of the others gives one.` |
| An undeclared parameter | `record.data.LogRun uses {$param.region}, but the mapping declares no parameter 'region'.` |

What a `$when`, a `$where` or an `$expr` is refused for (a condition in the old form, a condition that gives a value, an
expression the language cannot read) is on [mapping expressions](mapping-expressions.md#errors-when-the-mapping-is-read).
The checks against the template (a property it does not have, a shape that does not agree, a required property that may
be left out) and against the ingestion tables and the cache are the [preflight's](../concepts/preflight.md).

## Related

- [Mapping](mapping.md): the document, the header and what a rendered record contains.
- [Mapping expressions](mapping-expressions.md): what `$expr`, `$when` and `$where` can say.
- [Mapping lookups](mapping-lookups.md) and [mapping modifiers](mapping-modifiers.md).
- [Mapping assertions](mapping-assertions.md): `$assert`, the rules a property's value must meet.
- [Writing a mapping](../guides/writing-a-mapping.md): the mapping builder, `sqlflow values` and `sqlflow preview`.
