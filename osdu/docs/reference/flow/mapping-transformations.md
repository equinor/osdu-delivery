---
id: delivery-flow-mapping-transformations
title: "Mapping transformations: every function, modifier and lookup a mapping supports, with results"
type: flow-reference
summary: "The catalog of what a mapping can do to a value: expression functions, operators, modifiers, lookups and type conversion, each with its result."
keywords:
  - transformation
  - transformations
  - transform a value
  - supported functions
  - list of functions
  - which functions
  - function reference
  - cheat sheet
  - convert a value
  - text functions
  - string functions
  - math functions
  - unit conversion
  - date conversion
  - not supported
  - blank value
yamlPath: "(documentType: mapping) record.<property>"
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-expressions
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-assertions
  - delivery-guide-writing-a-mapping
  - flow-transform
  - flow-ing
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionFunctions.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionParser.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionNodes.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionValues.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/DateValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/NumberValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ReplaceTables.cs
  - osdu/tests/SqlFlow.Delivery.Tests/MappingTransformationsPageTests.cs
---

# Mapping transformations: every function, modifier and lookup a mapping supports, with results

This page lists everything a [mapping](mapping.md) can do to a value on its way from a row to an OSDU record: every
function, modifier, operator and lookup the engine has, each with what it gives. The list is closed, so what is not here
does not exist; [not supported](#not-supported-and-what-to-do-instead) says what to do instead. Each section links the
page that holds the full rules.

**Shape in the mapping, compute in the ingestion SQL.** A mapping cleans, converts and combines the values of one row. A
join, an aggregate or a calculation a reviewer has to think about belongs in the ingestion flow as a column, where it is
typed, tested and traced ([where heavier work goes](#where-heavier-work-goes)).

## At a glance

| Tool | Written as | What it does | What it offers |
| --- | --- | --- | --- |
| [Modifiers](#modifiers) | `$modifiers:` on a node | Change one incoming value, step by step | `trim` `upper` `lower` `split` `replace` `equals` `date` `number` `id` `ref` |
| [Expression functions](#expression-functions) | `$expr`, `$when`, `$where` | Compute a value from a row, or decide a condition | `iif` `coalesce` `nullif` `empty` `trim` `upper` `lower` `substring` `left` `right` `replace` `length` `contains` `startsWith` `endsWith` `number` `text` `round` `abs` |
| [Operators](#operators) | Inside an expression | Compare, join, calculate | `and` `or` `not` `=` `!=` `<` `<=` `>` `>=` `in` `not in` `&` `+` `-` `*` `/` |
| [Lookups](#lookups) | `$cache`, `$lookup`, `$search`, `replace: $cache.<Type>` | Translate a value, or find a record the row only names | The partition's cache, or a search of OSDU |
| [Value nodes](#value-nodes) | `$from`, `$value`, `$coalesce`, `$forEach` and others | Say where a property's value comes from | Columns, literals, first-of alternatives, repeated arrays, lists |
| [Type conversion](#automatic-type-conversion) | Nothing to write | Turn a value into the type the template declares | Text to number, integer, boolean; a single value to a list |
| [Assertions](mapping-assertions.md) | `$assert` on a node | Judge a value, and hold, report or leave it out | Conditions such as `between`, `in` and `matches` |

## How a value travels

A property's value passes through these steps, in this order:

1. **`$when`** decides whether the property applies to the row. When it does not hold, the property is left out.
2. **The node reads a value**: a column, an expression, a literal, the cache or a search.
3. **`$modifiers`** change it, top to bottom, each working on what the one before gave.
4. **`$required`** decides what an empty value does: the record is held (the default), or with `$required: false` the
   property is left out.
5. **The template** converts the value to the property's type.
6. **`$assert`** judges the value, if the mapping states rules for it. A `stage: incoming` rule judges it earlier, between
   steps 2 and 3.

On a `$cache`, `$search` or lookup the modifiers run first, on the value the lookup compares. What the cache or the
platform gives is never modified.

A **held** record is not sent. The ledger records why, and the record waits until its row or the mapping changes, or
someone releases it ([record lifecycle](../concepts/record-lifecycle.md)).

## Find the tool for the job

### Text

| You want to | Write | Value | Result |
| --- | --- | --- | --- |
| Remove spaces around a value | `$modifiers: [trim]` | `"  GR  "` | `"GR"` |
| Change case | `$modifiers: [upper]` or `[lower]` | `"gapi"` | `"GAPI"` |
| Take one part of delimited text | `- split: { separator: ",", part: 1 }` | `"MAIN,REPEAT"` | `"MAIN"` |
| Take characters by position | `$expr: substring(code, 4, 3)` | `"WB-1234"` | `"123"` |
| Replace text inside a value | `$expr: replace(name, " well", "")` | `"North WELL 7"` | `"North 7"` |
| Join columns and text | `$expr: log_source & " run " & log_run` | `"MAIN"`, `2` | `"MAIN run 2"` |
| Pad with zeros | `$expr: right("0000" & run, 4)` | `7` | `"0007"` |

### Numbers and units

| You want to | Write | Value | Result |
| --- | --- | --- | --- |
| Convert a unit | `$expr: depth_ft * 0.3048` | `12.5` | `3.81` |
| Round | `$expr: round(depth_ft * 0.3048, 2)` | `4102.5` | `1250.44` |
| Convert only some rows | `$expr: iif(unit = "FT", depth * 0.3048, depth)` | `"ft"`, `100` | `30.48` |
| Drop a placeholder | `$expr: nullif(depth, -999.25)` | `-999.25` | no value |
| Read a decimal comma | `- number: { decimal: "," }` | `"12,5"` | `12.5` |
| Read digit groups | `- number: { decimal: ",", group: " " }` | `"1 234,5"` | `1234.5` |
| Separate a value from its unit | `- split: { separator: " ", part: 1 }` | `"42.8 M"` | `42.8` on a number property |

### Dates

| You want to | Write | Value | Result |
| --- | --- | --- | --- |
| Read ISO 8601 text | `$modifiers: [date]` | `"2026-09-01T10:15:30+02:00"` | `"2026-09-01T08:15:30Z"` |
| Read any other layout | `- date: dd.MM.yyyy` | `"01.09.2026"` | `"2026-09-01T00:00:00Z"`, or `"2026-09-01"` where the template takes a date |
| Drop the time of day | `- split: { separator: T, part: 1 }` then `- date` | `"2026-09-01T10:15:30Z"` | `"2026-09-01"` |
| Take the year of a date column | `$expr: left(text(spud_date), 4)` | a date in 2026 | `"2026"` |

### Codes and references

| You want to | Write | Value | Result |
| --- | --- | --- | --- |
| Translate a few codes | `- replace: { RAW: Raw, EVAL: Evaluated }` | `"raw"` | `"Raw"` |
| Translate through a shared table | `- replace: $cache.UnitAlias` | `"gapi"` | `"gAPI"` |
| Make a reference from a code | `- ref` | `"m"` | `"dev:reference-data--UnitOfMeasure:m:"` |
| Build an id from another column | `- id: "{$param.dataPartition}:master-data--Well:{uwi}:"` | `uwi` is `"33/9-1"` | `"dev:master-data--Well:33%2F9-1:"` |
| Find a record by code or name in the cache | `$cache: UnitOfMeasure.id` with `$findBy: Code = unit` | `"FT"` | `"dev:reference-data--UnitOfMeasure:ft:"` |
| Find a record in a set too large to cache | `$search: Wellbore` with `$findBy: data.FacilityName = wellbore_name` | a wellbore's name | its id |

### Conditions and defaults

| You want to | Write | Value | Result |
| --- | --- | --- | --- |
| Use a default when a value is empty | `$expr: coalesce(log_name, log_source, "unnamed")` | both empty | `"unnamed"` |
| Fall back across sources (a column, the cache, a literal) | [`$coalesce`](#value-nodes) | the first gives nothing | the next that gives a value |
| Choose between two values | `$expr: iif(status = "OK", "Passed", "Failed")` | `" ok "` | `"Passed"` |
| Choose among several | `iif` inside `iif`, or a `replace` table | | |
| Write a property only on some rows | `$when: depth_coding = "REGULAR"` | any other value | property left out |
| Turn text into true or false | `- equals: REGULAR` | `" regular "` | `true` |
| Leave out a value that breaks a rule | `$assert` with `notIn: [-999.25]` and `onFail: omit` | `-999.25` | property left out, failure recorded |

### Arrays and lists

| You want to | Write | Result |
| --- | --- | --- |
| One array item per child row | `$forEach: curves` with `$item` | One object per row of the flow's child dataset |
| Keep only some child rows | `$where: curve_id != "DEPT"` beside `$forEach` | The rows that pass become items |
| Read the parent row from inside an item | `$dataset.log_name` | The record table's column |
| A list from several columns | A list of value nodes (`- $from: a`, `- $from: b`) | Each value once, whatever its case |
| Every matching cached row, as a list | `$cache: <Type>.<field>` with `$findAll` | One value per row found |
| Several values of one cached record | A `lookups:` entry, read with `$lookup: <name>.<field>` | All from the same record |

## Expression functions

Written in `$expr`, `$when` and `$where`. Names ignore case: `COALESCE(a, b)` is `coalesce(a, b)`. A function given no value
gives no value, except where the table says otherwise. [Mapping expressions](mapping-expressions.md) has the full rules.

### Choose and default

| Function | Gives | Example | Result |
| --- | --- | --- | --- |
| `iif(condition, value, other)` | `value` where the condition holds, otherwise `other`; only the branch taken is read. No value counts as false. | `iif(a = "OK", "Passed", "Failed")`, `a` is `" ok "` | `"Passed"` |
| `coalesce(value, value, ...)` | The first value that is not missing or blank. Two or more values. | `coalesce(a, b, "none")`, `a` is null, `b` is `"  "` | `"none"` |
| `nullif(value, other)` | No value when `value` is the same as `other`, otherwise `value`. The row may hold it as a number or as text. | `nullif(a, -999.25)`, `a` is `"-999.25"` | no value |

### Test

| Function | Gives | Example | Result |
| --- | --- | --- | --- |
| `empty(value)` | True when the value is missing or blank. | `empty(a)`, `a` is `"   "` | `true` |
| `contains(text, part)` | True when the text holds `part`, in any case. False for no value. | `contains(a, "run")`, `a` is `"MAIN RUN 2"` | `true` |
| `startsWith(text, part)` | True when the text starts with `part`, in any case. False for no value. | `startsWith(a, "main")`, `a` is `"MAIN RUN 2"` | `true` |
| `endsWith(text, part)` | True when the text ends with `part`, in any case. False for no value. | `endsWith(a, "2")`, `a` is `"MAIN RUN 2"` | `true` |

### Text functions

Characters are counted as a person reads them, so an accented letter or an emoji is one.

| Function | Gives | Example | Result |
| --- | --- | --- | --- |
| `trim(text)` | The text without the spaces around it. | `trim(a)`, `a` is `"  GR  "` | `"GR"` |
| `upper(text)` | The text in upper case. | `upper(a)`, `a` is `"gapi"` | `"GAPI"` |
| `lower(text)` | The text in lower case. | `lower(a)`, `a` is `"GAPI"` | `"gapi"` |
| `substring(text, start, length)` | Part of the text from `start`, counting from 1. Leave `length` out to read to the end. A start past the end gives no value. | `substring(a, 4)`, `a` is `"WB-1234"` | `"1234"` |
| `left(text, count)` | The first `count` characters, or all of them when there are fewer. | `left(a, 2)`, `a` is `"WB-1234"` | `"WB"` |
| `right(text, count)` | The last `count` characters, or all of them when there are fewer. | `right(a, 4)`, `a` is `"WB-1234"` | `"1234"` |
| `replace(text, find, with)` | The text with every occurrence of `find`, in any case, replaced by `with`. `find` must not be empty. | `replace(a, "well", "Wellbore")`, `a` is `"North WELL 7"` | `"North Wellbore 7"` |
| `length(text)` | How many characters the text holds. | `length(a)`, `a` is `"Grønn"` | `5` |

### Convert and calculate

| Function | Gives | Example | Result |
| --- | --- | --- | --- |
| `number(value)` | The value as a number, text read with `.` before the decimals. The [`number` modifier](#modifiers) reads other forms. | `number(a)`, `a` is `"12.5"` | `12.5` |
| `text(value)` | The value as text: a number in its shortest form, true or false, a date in RFC 3339. | `text(a)`, `a` is the number `12.50` | `"12.5"` |
| `round(number, digits)` | Rounded to `digits` decimals, 0 to 15, or 0 when left out. Halves go away from zero, as SQL rounds them. | `round(a, 2)`, `a` is `2.345` | `2.35` |
| `abs(number)` | The number without its sign. | `abs(a)`, `a` is `-3.2` | `3.2` |

## Operators

From the loosest binding to the tightest; brackets say otherwise.

| Operator | Means | Example |
| --- | --- | --- |
| `or` | Either condition holds. | `a = 1 or b = 2` |
| `and` | Both conditions hold. | `a > 1 and a < 5` |
| `not` | The condition does not hold. | `not empty(a)` |
| `=` `!=` `<` `<=` `>` `>=` | Compare two values, one comparison at a time. | `depth > 100` |
| `in`, `not in` | Whether a value is one of a list of at least one. | `curve_id not in ["DEPT", "MD"]` |
| `&` | Join values as text. No value joins as no text. | `log_source & " run " & log_run` |
| `+` `-` | Add, subtract. | `top + 10` |
| `*` `/` | Multiply, divide. Division by zero holds the record. | `depth * 0.3048` |
| `-x` | Negate a number. | `-depth` |

An expression reads a column by its bare name, `$dataset.<column>` for the record table's row from under a `$forEach`, and
`$param.<name>` for a parameter. It takes text in quotes, numbers, `true`, `false` and `null`. A column whose name holds
`-`, starts with a digit, or is a keyword or a function name goes in backticks.

- **Text compares ignoring case and the spaces around it:** `status = "final"` holds for `" FINAL "`.
- **Numbers compare as numbers:** `depth > 100` holds for the text `"150"`. Text that is not a number never equals a
  number, and ordering it against one, or calculating with it, holds the record.
- **No value is the same only as no value:** `status != "FINAL"` holds for a row without a status, and `status > 1` does
  not. Arithmetic with no value gives no value.
- **Limits:** an expression is at most 2000 characters and nests at most 48 levels.

## Modifiers

Listed under `$modifiers` on a node and run top to bottom. [Mapping modifiers](mapping-modifiers.md) has the full rules.

| Modifier | Written as | Value | Result | Good to know |
| --- | --- | --- | --- | --- |
| `trim` | `- trim` | `"  GR  "` | `"GR"` | Removes white space at both ends. |
| `upper` | `- upper` | `"gapi"` | `"GAPI"` | The same on every machine. |
| `lower` | `- lower` | `"GAPI"` | `"gapi"` | The same on every machine. |
| `split` | `- split: { separator: ",", part: 2 }` | `"MAIN, REPEAT "` | `"REPEAT"` | Keeps one part, counting from 1, trimmed. A separator of one space splits on any run of white space. A missing or empty part gives no value. |
| `replace`, a table | `- replace: { RAW: Raw, NONE: ~ }` | `" raw "`, `"NONE"`, `"OTHER"` | `"Raw"`, no value, `"OTHER"` | Matched trimmed; an exact key wins, then a key that differs only in case. A value the table does not list passes on. |
| `replace` with `otherwise` | `- replace: { RAW: Raw }` and, beside it, `otherwise: Unknown` | `"OTHER"` | `"Unknown"` | `otherwise: ~` makes an unlisted value no value. |
| `replace`, from the cache | `- replace: $cache.UnitAlias` | `"gapi"` | `"gAPI"` | Takes `match` and `field` to choose the cached fields, and `otherwise`. |
| `equals` | `- equals: REGULAR` | `" regular "`, `"DISCRETE"` | `true`, `false` | Fills a boolean property. Ignores case and the spaces around the value. |
| `date` | `- date` | `"2026-09-01 10:15"` | `"2026-09-01T10:15:00Z"` | ISO 8601 only. A value without an offset is UTC. `01/02/2026` holds the record: it could be either month. |
| `date`, a format | `- date: dd.MM.yyyy` | `"01.09.2026"` | `"2026-09-01T00:00:00Z"` | A .NET date format with a four-digit year, a month and a day, such as `yyyyMMdd` or `dd MMM yyyy`. |
| `number` | `- number: { decimal: ",", group: "." }` | `"1.234.567,89"` | `1234567.89` | `decimal` is `.` or `,`. `group` is `,`, `.`, a space or an apostrophe. `- number` alone reads `.` decimals. |
| `id` | `- id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:"` | `"ft"` | `"dev:reference-data--UnitOfMeasure:ft:"` | Builds an id from a template. Tokens: `{$value}`, `{<column>}`, `{$dataset.<column>}`, `{$cache.<Type>.<field>}`, `{$param.<name>}`. |
| `ref` | `- ref`, or `- ref: UnitOfMeasure` | `"m"` | `"dev:reference-data--UnitOfMeasure:m:"` | `id` with the template written for you, from the entity type the property points to. A code is percent-encoded: `m/s` is `m%2Fs`. |

- **They chain.** `[trim, lower, { replace: { metres: m, feet: ft } }, ref]` makes `" METRES "` into
  `"dev:reference-data--UnitOfMeasure:m:"`.
- **`id` and `ref` come last,** one to a node, and neither goes on a `$cache` or `$search` node, which already gives the id.
- **An id is checked.** Where the cache holds records of that entity type, it must hold this one, or the record is held.
  `$unverified: true` on the node writes the id all the same, for a source that is ahead of the partition.
- **`$modifiers` goes on** `$from`, `$expr` and an alternative of `$coalesce`; on `$cache`, `$search` and a `lookups:`
  entry, where it changes the value compared; and on a single value read by `$parent` or `$base`. It does not go on a
  literal, a `$child`, a `$link`, a `$lookup` node, or beside `$coalesce`.

## Lookups

The mapping never names a cache: it reads the cache of the partition the flow delivers to, and a `$search` asks OSDU's
search service during the run. [Codes and references](#codes-and-references) and [arrays and lists](#arrays-and-lists) say
which to use; [mapping lookups](mapping-lookups.md) has the full rules.

`$findBy` takes one line or a list, tried in order: `Code = curve_unit`, or `Code = 'KellyBushing'` for fixed text. A value
matches exactly first, then ignoring case when one record answers. `$ignoreSeparators: true` adds a last try with
punctuation and spacing folded away, so `wb_1-1 a` finds `WB 1/1-A`. Several records answering holds the record, whatever
`$required` says.

## Value nodes

Every property of the record tree is one of these. The right column is the input of the GUI's mapping builder that writes
it. [Mapping values](mapping-values.md) has the full rules.

| Node | Reads | In the builder |
| --- | --- | --- |
| A literal, `Source: welldb` | The value as written. Its text reads a parameter as `{$param.<name>}` and nothing else. | Static value |
| `$value` | A literal with a `$when` beside it, or an object or list written whole. | Static value |
| `$from` | A column: of the row the node is in, or `$dataset.<column>` for the record table's row. | Dataset column |
| `$expr` | What an [expression](#expression-functions) computes from the row. | Expression |
| `$cache`, `$lookup`, `$search` | A value from outside the row ([lookups](#lookups)). | Cache, Lookup, Platform search |
| `$coalesce` | The first of two or more alternatives that gives a value, each a node of its own. A literal can only be the last. | First value of |
| `$forEach` | One array item per child row, laid out under `$item`. | Repeat child rows |
| A list | Each item in order: value nodes, or objects. | List of values, List of objects |
| An object | Each property it holds, as that property's node writes it. | Object |
| `$parent` | A section of the record's parent, for a [custom template linked to one](mapping-parent.md). | Parent record |
| `$child` | The reference to the record's [child](mapping-child.md), once OSDU holds it. | Child record |
| `$base` | In an [enrichment mapping](mapping-enrichment.md): a section of the record as it stands. | The file |
| `$link` | The ids of the records an [enrichment](enrich-records.md) adds. | The file |

Beside the node go its settings: `$modifiers`, `$when`, `$required`, `$description`, `$assert`, and on a lookup `$findBy`,
`$findAll` and `$ignoreSeparators`; `$unverified` goes on a node that builds an id. An array takes `$where`.

## Automatic type conversion

The template decides what the record carries, whatever the source column's type, so most conversions need nothing written.

| The property takes | Value | Result |
| --- | --- | --- |
| Text | The number `12.50`; `true` | `"12.5"`; `"true"` |
| A number | `"23.5"` | `23.5` |
| A number | `"12,5"` | Held: use the `number` modifier |
| An integer | `"12"`, `"12.0"` | `12` |
| An integer | `"12.5"`, or a value outside the property's range | Held |
| A boolean | `true`, `1`, `yes`, `y`, `false`, `0`, `no`, `n`, in any case | `true` or `false` |
| A boolean | `"maybe"` | Held |
| A list of values | `"x"` | `["x"]` |
| A date, from a date column | a `datetime` value | RFC 3339, in the form the property's `format` takes |
| A date, from text | `"01.09.2026"` | Written as it stands, and the preflight warns: use the `date` modifier |

Details are on [mapping values](mapping-values.md#how-a-value-takes-its-propertys-type).

## Blank text and no value

A null column is no value: the modifiers are skipped, and `$required` decides. Blank text (`""`, `"   "`) is the same for
most of the language, with three exceptions that hold records in practice.

| The column holds | No modifier, `trim`, `upper`, `lower`, `split`, `replace`, `id`, `ref` | `equals` | `date`, `number` |
| --- | --- | --- | --- |
| Null | No value | No value | No value |
| Blank text | No value | `false` | Record held, whatever `$required` says |

So `$from: spud_date` with `$modifiers: [date]` and `$required: false` holds a record whose column is blank. Any of these
treats the blank as no value:

```yaml
# Best: clean it in the flow that lands the file, so no mapping sees a blank.
transform:
  columns:
    - { name: spud_date, expr: "NULLIF(@ColName, '')", type: varchar(20) }
```

```yaml
# In the mapping: leave the property out on those rows.
SpudDate:
  $from: spud_date
  $when: not empty(spud_date)
  $modifiers: [date]
```

```yaml
# In the mapping: turn the blank into no value, and let $required decide.
SpudDate:
  $expr: nullif(spud_date, "")
  $modifiers: [date]
  $required: false
```

`$required: false` covers a value that is missing. It never covers a value that is wrong: a date that is not a date, a
number written another way, a value several records answer to, and an expression that meets a value it cannot work with
all hold the record, whatever it says ([the full table](mapping-values.md#required-what-an-empty-value-does)).

## A worked example

One well log of the well database, in a complete mapping of `osdu:wks:work-product-component--WellLog:1.4.0` (with the
`acl` and `legal` lists every mapping lays out):

```yaml
record:
  data:
    Name:
      $from: log_name
      $modifiers: [trim, upper]
    Description:
      $expr: coalesce(log_comment, upper(log_source) & " run " & log_run)
    WellLogTypeID:
      $from: data_type
      $modifiers:
        - upper
        - replace: { INTERPRETED: Interpreted, RAW: Raw }
        - ref
    IsRegular:
      $from: depth_coding
      $modifiers:
        - equals: REGULAR
    SamplingStart:
      $expr: iif(index_unit = "FT", round(index_min * 0.3048, 2), index_min)
    SamplingInterval:
      $expr: nullif(index_increment, -999.25)
      $required: false
    VerticalMeasurement:
      VerticalMeasurement:
        $from: elev_meas_ref
        $modifiers:
          - split: { separator: " ", part: 1 }
    Curves:
      $forEach: curves
      $where: curve_id != "DEPT"
      $item:
        Mnemonic:
          $from: curve_id
          $modifiers: [upper]
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias
            - ref
          $required: false
        DateStamp:
          $from: update_date
          $modifiers:
            - date: dd.MM.yyyy
          $required: false
```

What each property reads, and what it makes of it:

| Property | The row holds | What happens | The record carries |
| --- | --- | --- | --- |
| `Name` | `log_name` is `"  gr main pass  "` | Trimmed, then upper case | `"GR MAIN PASS"` |
| `Description` | `log_comment` is null, `log_source` is `"composite"`, `log_run` is `2` | `coalesce` passes the null and takes the join | `"COMPOSITE run 2"` |
| `WellLogTypeID` | `data_type` is `"interpreted"` | Upper case, translated, made a reference | `"dev:reference-data--LogType:Interpreted:"` |
| `IsRegular` | `depth_coding` is `"regular"` | `equals` ignores case | `true` |
| `SamplingStart` | `index_unit` is `"ft"`, `index_min` is `4102.5` | `=` ignores case, so feet become metres, rounded | `1250.44` |
| `SamplingInterval` | `index_increment` is `-999.25` | `nullif` gives no value, and `$required: false` leaves it out | Nothing |
| `VerticalMeasurement` | `elev_meas_ref` is `"42.8 M"` | `split` gives `"42.8"`, and the template's number type converts it | `42.8` |
| `Curves` | Three child rows: `dept`, `gr`, `rhob` | `$where` drops `dept`, since `!=` ignores case too | Two items |
| `CurveUnit` | `curve_unit` is `"gapi"` | The `UnitAlias` table gives `gAPI`, and `ref` builds the reference | `"dev:reference-data--UnitOfMeasure:gAPI:"` |
| `DateStamp` | `update_date` is `"01.09.2026"` | Read with the format, written in RFC 3339 | `"2026-09-01T00:00:00Z"` |
| `rhob`'s `CurveUnit`, `DateStamp` | Null | `$required: false` leaves each out | Nothing |

## Not supported, and what to do instead

Where a name from another language has an equivalent here, the refusal names it (`len` is answered with `length(text)`).

| You might look for | What to do |
| --- | --- |
| `concat(a, b)` | `a & b` |
| `if`, `case`, `when`, `choose`, `a ? b : c` | `iif(condition, value, other)`, one inside another for several choices, or a `replace` table |
| `isnull`, `nvl`, `ifnull`, `a ?? b` | `coalesce(a, b)` |
| `len`, `char_length`; `substr`, `mid`; `ucase`, `lcase` | `length`; `substring`; `upper`, `lower` |
| `cast`, `convert`, `toString`; `toNumber`, `parse`, `val` | `text(value)`; `number(value)`. The template converts most values by itself. |
| `ltrim`, `rtrim` | `trim` removes both sides; there is no one-sided form |
| Regular expressions, `like`, `regexp_replace` | `replace()` for fixed text. `contains`, `startsWith` and `endsWith` to test. `$assert` takes `matches`, which judges a value and never changes it. |
| `floor`, `ceiling`, `power`, `sqrt`, `mod`, `%`, `^` | `+`, `-`, `*`, `/`, `round` and `abs` are what there is; `a * a` squares. Anything else goes in the ingestion SQL. |
| `min(a, b)`, `max(a, b)` | `iif(a < b, a, b)`, `iif(a > b, a, b)` |
| `sum`, `avg`, `count` over child rows | The ingestion SQL, as a column. A mapping reads one row at a time. |
| `now()`, `getdate()`, a random value | None, on purpose: a record renders the same every time its inputs do. Stamp the time in a column. |
| `dateadd`, `datediff`, `year`, date `format` | The ingestion SQL. For the year of a date column, `left(text(spud_date), 4)`. |
| Comparing text exactly, case included | `=` always ignores case and surrounding spaces; `$assert` takes `equals`, which compares exactly unless `ignoreCase` is set |
| Reading a part of a `split` from the end | `split` counts from 1 only; `right()` takes the last characters |
| A `split` or `date` inside an expression | They are modifiers, and run after the expression: `$expr` first, `$modifiers` on what it gives |
| Text split into an array | None. For an array of objects, make one row per item in the ingestion SQL and read them as a child dataset. |
| An array of values from rows | Refused: the items of a `$forEach` are objects. A list of value nodes, or `$findAll`, fills a list of values. |
| A `$forEach` inside a `$forEach` | Refused: a path steps into one array at most |
| A path inside a column (`$from: a.b`) | A column is read whole; flatten JSON in the ingestion flow |
| A column's value in a literal (`"{$dataset.a} run"`) | `$expr`, or the `id` modifier for an id |
| A join to another table | A [lookup](#lookups) of the cache, or the join in the ingestion SQL |
| A function of your own | The set is closed: a new function is a change to the engine |

### Where heavier work goes

The source's tables reach a mapping through SQLFlow's flows, and both of these run SQL on the way:

- **`transform.columns`** on a pre flow or an `ing` flow gives a column a SQL expression, where `@ColName` is the column
  ([transform section](../../../../sqlflow/docs/reference/flow/transform.md)).
- **`virtualColumns`** on an `ing` flow adds a computed column read from the source
  ([ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md)).

## Writing an expression in YAML

YAML reads the line before the expression language does, and these are what catch people out
([all of them](mapping-expressions.md#writing-an-expression-in-yaml)):

| The expression | Problem | Write it |
| --- | --- | --- |
| Starts with a quote: `"Run " & log_run` | YAML takes `"Run "` as the whole value | `$expr: '"Run " & log_run'` |
| Holds a comma, inside `{ ... }` | `{ $expr: nullif(a, -999) }` splits at the comma | One key per line, or quote the expression |
| Holds `: ` or ` #` | Read as a mapping, or as a comment | Quote the expression |

## Check it before a run

Nothing here sends anything to OSDU.

| Run | It tells you |
| --- | --- |
| `sqlflow validate <file>` | The document itself is right: every function, modifier and word exists, offline. |
| `sqlflow check <flow>` | It fits the template and the partition's cache ([preflight](../concepts/preflight.md)). |
| `sqlflow values <flow>` | What it makes of every row of the scope, property by property ([values](../cli/values.md)). |
| `sqlflow preview <flow>` | One record as a delivery would render it ([preview](../cli/preview.md)). |

The GUI's mapping builder runs the same checks as you edit ([writing a mapping](../guides/writing-a-mapping.md)).

## Related

- [The mapping document](mapping.md): the header, the OSDU id and what a rendered record contains.
- [Mapping values](mapping-values.md), [expressions](mapping-expressions.md), [modifiers](mapping-modifiers.md) and
  [lookups](mapping-lookups.md): the full rules behind each section here.
- [Mapping assertions](mapping-assertions.md): the rules a value must meet.
- [Enrichments](enrich.md): adding to a record from rows of its own.
- [Writing a mapping](../guides/writing-a-mapping.md): from a template to a mapping that checks clean.
