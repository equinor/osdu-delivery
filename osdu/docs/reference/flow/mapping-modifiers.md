---
id: delivery-flow-mapping-modifiers
title: "Mapping modifiers ($modifiers): trim, upper, lower, split, replace, equals, date, number, id and ref"
type: flow-reference
summary: "Every modifier a mapping applies to an incoming value: cleaning text, translating codes from a table or the cache, reading dates and numbers, building OSDU ids."
keywords:
  - "$modifiers"
  - replace
  - "replace: $cache"
  - otherwise
  - split
  - equals
  - date format
  - number decimal separator
  - id modifier
  - ref modifier
  - build osdu reference id
  - unit translation
  - "$unverified"
  - percent-encoding
yamlPath: "(documentType: mapping) record.<property>.$modifiers"
related:
  - delivery-flow-mapping-values
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-expressions
  - delivery-flow-dictionary
  - delivery-guide-lookup-table-cache
  - delivery-guide-reference-data-cache
  - delivery-concept-change-detection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Model/IdTemplate.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.Id.cs
  - osdu/src/SqlFlow.Delivery/Rendering/IdValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ReplaceTables.cs
  - osdu/src/SqlFlow.Delivery/Rendering/CachedReferences.cs
  - osdu/src/SqlFlow.Delivery/Rendering/DateValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/NumberValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Identity/IdSegment.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
---

# Mapping modifiers ($modifiers): trim, upper, lower, split, replace, equals, date, number, id and ref

Modifiers change an incoming dataset value on its way into the record. They are listed under `$modifiers` on a value
node and applied top to bottom, each to what the one before gave. On a `$from` or `$expr` node, and on a single value a
`$parent` or `$base` node reads, they shape the value the property gets. On a `$cache`, `$search` or `$findAll` node, and on a lookup, they shape the value the node compares
before it looks anything up ([mapping lookups](mapping-lookups.md)); what the cache or the platform holds is OSDU's own
and is never modified. A modifier never runs on no value: once a value is gone (a null column, a `replace` to `~`, a
missing `split` part) the rest are skipped and `$required` decides ([mapping values](mapping-values.md)).

Keep the work small: the ingestion SQL is where data is computed and joined, and the mapping shapes one value at a time
([expressions](mapping-expressions.md) choose between and combine a row's values).

## Every modifier

| Modifier | Written as | Incoming value | Result |
| --- | --- | --- | --- |
| trim | `- trim` | `" GR "` | `"GR"` |
| upper, lower | `- upper` | `"gapi"` | `"GAPI"` |
| split | `- split: { separator: ",", part: 1 }` | `"MAIN,REPEAT"` | `"MAIN"` |
| replace | `- replace: { RAW: Raw, NONE: ~ }` | `"RAW"`, `"NONE"` | `"Raw"`, no value |
| replace from the cache | `- replace: $cache.UnitAlias` | `"metre"` | what the cached row keyed `metre` gives |
| equals | `- equals: REGULAR` | `"regular"`, `"DISCRETE"` | `true`, `false` |
| date | `- date: dd.MM.yyyy` (`- date` reads ISO 8601) | `"01.09.2026"` | `"2026-09-01T00:00:00Z"`, or `"2026-09-01"` where the template takes a date |
| number | `- number: { decimal: ",", group: " " }` (`- number` reads `.` decimals) | `"1 234,5"` | `1234.5` |
| id | `- id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:"` | `"m/s"` | `"dev:reference-data--UnitOfMeasure:m%2Fs:"` |
| ref | `- ref`, `- ref: UnitOfMeasure` or `- ref: reference-data--UnitOfMeasure` | `"m/s"` | `"dev:reference-data--UnitOfMeasure:m%2Fs:"` |

A modifier is a name (`trim`) or a map of one entry (`split: {...}`). The settings of a modifier (`separator`, `part`,
`decimal`, `otherwise`...) are words of the modifier, not of the record, so they carry no `$`. Only `replace` takes
settings beside it, as further keys of the same list item.

```yaml
record:
  data:
    Name:
      $from: log_name
      $modifiers: [trim]
    WellLogTypeID:
      $from: log_type
      $modifiers:
        - upper
        - replace: { RAW: Raw, EVAL: Evaluated, COMP: Composite }
          otherwise: ~
        - ref
      $required: false
    Curves:
      $forEach: curves
      $item:
        Mnemonic:
          $from: curve_mnemonic
          $modifiers: [trim, upper]
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias
            - ref
```

### Rules that hold for every modifier

- `$modifiers` is a list (`$modifiers: [trim]`), never a single name.
- `id` and `ref` build the value a node writes, so a node takes one of them, it is the last modifier, and it goes only
  on a node that reads one value (`$from`, `$expr`, or `$parent` and `$base` on a single value): a `$cache` or `$search`
  node already gives an id.
- On a node whose `$findBy` lines compare only quoted texts there is no incoming value to change, so `$modifiers` there
  is refused.
- A modifier that cannot read the value it is given (a date that is not a date, a number written another way) holds
  the record **whatever `$required` says**: the value was there and it was wrong. A modifier that turns the value into
  no value (`~`, a missing part) leaves it to `$required`.

## trim, upper, lower

`trim` removes surrounding white space. `upper` and `lower` change case, the same on every machine (invariant culture).
None of them takes a setting.

## split

```yaml
LogActivity:
  $from: log_pass
  $modifiers:
    - split: { separator: " ", part: 1 }
    - lower
```

`split` cuts the value at `separator` and keeps the part numbered `part`, counting from one, trimmed. A separator of a
single space splits on any run of white space. A part the value does not have, or an empty part, gives no value. Both
settings are required; anything else beside them is refused (`split takes 'separator' and 'part', not '...'`).

## equals

```yaml
IsRegular:
  $from: depth_coding
  $modifiers:
    - equals: REGULAR
```

`equals` gives `true` when the value, trimmed, is the text given, ignoring case, and `false` otherwise. It compares with
one text (`equals compares with one text, such as equals: REGULAR.`). A node whose last modifier is `equals` must fill a
boolean; the preflight refuses one written to anything else.

## replace

```yaml
CurveUnit:
  $from: curve_unit
  $modifiers:
    - replace: { M: m, METRE: m, FT: ft, NONE: ~ }
      otherwise: ~
    - ref
  $required: false
```

A written table lists incoming values and what each becomes. A value is matched trimmed, by the cache's own rules: an
exact key wins, case is ignored only when that finds one key, and a value several keys answer to only once case is
ignored holds the record, naming them, unless they all give the same value.

| Written as | Result |
| --- | --- |
| `NONE: m` | The listed value becomes `m`. |
| `NONE: ~` | The listed value becomes no value, and `$required` decides. |
| no `otherwise` | A value the table does not list passes on unchanged, trimmed. |
| `otherwise: ~` | A value the table does not list becomes no value. |
| `otherwise: Unevaluated` | A value the table does not list becomes `Unevaluated`. |

`otherwise` is written beside `replace`, never inside its table, so no incoming value is ever read as a setting. It
applies only to a value that is there and not listed: an empty value stays empty, and a listed value that becomes no
value is not unlisted. A quoted `"~"` is the text `~`. The mapping is refused when the table lists an empty key, two keys
that are the same once trimmed, or a value that is not text (`replace maps 'A' to something that is not text; write a
text, or ~ for no value.`).

A table one property of one mapping uses belongs here. A table shared by mappings, or one another team maintains,
belongs in the partition's cache, where every mapping reads it the same way.

### replace from the cache

```yaml
CurveUnit:
  $from: curve_unit
  $modifiers:
    - replace: $cache.UnitAlias
    - ref
```

`replace: $cache.<Type>` reads its table from the version of the partition's cache the render reads: a lookup table a
cache flow loads from an ingestion table, a [dictionary](dictionary.md), or any OSDU type a cache flow captures
([cache flow](cache.md), [your own lookup table](../guides/lookup-table-cache.md)). The incoming value is matched on the
`match` field by the rules above, and replaced by what the matched row holds at `field`. The mapping names the type,
never a cache, so every flow delivering to a partition reads the same table.

| Setting | Default | Meaning |
| --- | --- | --- |
| `match` | the table's key | The cached field the incoming value is compared with. A type of OSDU records has no key, so it names one. |
| `field` | the one field a lookup table keeps beside its key (`value` for a dictionary of pairs) | The cached field whose value replaces the incoming one. A lookup table keeping several fields, and a type of OSDU records, name one. |
| `otherwise` | the value passes on unchanged | What a value no row holds becomes, as for a written table. |

```yaml
CurveDescription:
  $from: curve_mnemonic
  $modifiers:
    - replace: $cache.CurveDictionary
      field: log_curve_type_id
      otherwise: ~
    - replace: $cache.LogCurveType
      match: id
      field: Name
  $required: false
```

Reads as: the curve dictionary gives the mnemonic's curve type code, and the partition's `LogCurveType` record whose id
ends in that code gives its name. `match: id` on a type of OSDU records compares the record id and the code it ends
with, as a `$findBy` line on `id` does.

- A matched row with nothing in `field` gives no value; `otherwise` applies only when no row holds the value.
- The record is held, whatever `$required` says, when the cache version holds no such type, when `match` or `field`
  cannot be settled (`lookup table CurveDictionary holds 2 fields beside its key mnemonic (...); name the one that
  replaces the value, such as field: ...`), when the matched row holds several values or an object at `field`, and when
  two rows answer to the value only once case is ignored and give different values.
- `match` and `field` beside a written table are refused: they choose the fields of a cached table.
- Every row a replacement was decided by is recorded with the record's other cache dependencies, and so is a key a
  lookup table does not list. A new version of the table that changes an entry, empties or removes it, or comes to list a
  key that records looked up and did not find, tags exactly the records built from it
  ([change detection](../concepts/change-detection.md)).

The preflight checks that the type is in the cache version and holds `match` and `field`, and warns about rows that hold
several values at `field`, several rows holding the same `match`, and a table with no rows. Where the replaced value is
then looked up by the node's own `$findBy` (a `$cache` node with a `replace`), every value the replace can give (a
written table's values, a cached table's `field` values, a text `otherwise`) is looked up there too, and the ones that
find nothing are listed as a warning before any row arrives.

## date

`date` reads a date and writes it in the form the template's `format` names. OSDU schemas are JSON Schema draft-07,
whose `date-time` and `date` formats are the RFC 3339 forms: where the template takes a `date` the modifier writes
`2026-09-01`, and anywhere else an RFC 3339 date-time in UTC, `2026-09-01T10:15:30Z`, the form OSDU's own timestamps
take.

| Written as | Reads | Refuses |
| --- | --- | --- |
| `- date` | ISO 8601 only: `2026-09-01`, or a date, `T` or a space, and a time of hours and minutes with optional seconds and up to seven fractional digits, followed by `Z`, an offset, or nothing. | `01/02/2026` (either month), a time alone, `Sep 1 2026`, `20260901`, `2026-02-30` |
| `- date: dd.MM.yyyy` | Exactly that .NET date format, such as `yyyyMMdd` or `dd MMM yyyy HH:mm`. | When the mapping is read: a format without a four-digit year, a month and a day of the month, a one-letter standard pattern, an unclosed quote. |

A value without an offset is taken as UTC. A `datetime` or `datetime2` column of the ingestion table already is a date,
and is written in its property's form with or without the modifier. A value `date` cannot read holds the record
whatever `$required` says, and so does a value with a time of day where the template takes a date, since writing it
would drop the time. Take the date part first:

```yaml
TechnicalAssurances:
  - TechnicalAssuranceTypeID: "{$param.dataPartition}:reference-data--TechnicalAssuranceType:Unevaluated:"
    EffectiveDate:
      $from: qc_timestamp
      $modifiers:
        - split: { separator: T, part: 1 }
        - date
      $required: false
```

A node whose last modifier is `date` must fill text whose format is `date` or `date-time`, or no format. Without `date`,
a text value is written exactly as it arrives, so the preflight warns about every node filling a `date` or `date-time`
property from a column without it.

Refused when the mapping is read, for example: `the date format 'dd.MM.yy' reads a two-digit year, which does not say
its century; the values must carry a four-digit year (yyyy).`

## number

Text is read as a number when it is digits with an optional sign, `.` before the decimals and an optional exponent
(`-1234.5`, `1.2E-3`). A value written any other way holds the record rather than being guessed at, because `12,5` is
twelve and a half or, with `,` between digit groups, a hundred and twenty-five. `number` reads text written with other
separators:

| Written as | Incoming value | Result |
| --- | --- | --- |
| `- number: { decimal: "," }` | `"12,5"` | `12.5` |
| `- number: { decimal: ",", group: " " }` | `"1 234 567,89"` | `1234567.89` |
| `- number: { decimal: ",", group: "." }` | `"1.234.567,89"` | `1234567.89` |
| `- number: { group: "," }` | `"1,234,567.89"` | `1234567.89` |

`decimal` is `.` (the default) or `,`. `group` is `,`, `.`, a space or an apostrophe, never the same as `decimal`;
without it no group separator is read, and with a space a no-break, narrow no-break or thin space counts as one. After a
first group of one to three digits every group is exactly three, so `1.23,5` holds rather than being read as 123.5. The
sign may be the Unicode minus. The separators are checked when the mapping is read (`number cannot use ',' both between
digit groups and before the decimals.`).

What the number becomes depends on the property it fills:

| The property takes | Written as | The record is held for |
| --- | --- | --- |
| `number` | The number: a whole value exactly (`12.0` is `12`), anything else as the nearest double. | `NaN` or `Infinity`; a value beyond a double's range or too close to zero to tell apart from it. |
| `integer` | The whole number, `12.0` and `1e3` included. | A fraction; a value outside its format's range (`int32`, else 64 bits); a double beyond 2^53. |
| text without a `format` | The shortest text that reads back as the same number. | `NaN` or `Infinity`. |

A node whose last modifier is `number` must fill a number, an integer, or text without a `format`. A value `number`
cannot read holds the record whatever `$required` says. Values from the ingestion tables are read as the numbers their
SQL types hold, without the modifier.

## id

`id` builds the OSDU id a node writes from a template, so a mapping generates references instead of looking each one
up: reference data, master data, work product components and datasets alike.

```yaml
Curves:
  $forEach: curves
  $item:
    LogCurveTypeID:
      $from: curve_mnemonic
      $modifiers:
        - id: "{$param.dataPartition}:reference-data--LogCurveType:{$cache.CurveDictionary.log_curve_type_id}:"
      $required: false
```

The template is text with tokens, quoted, since YAML reads text starting with `{` as a map.

| Token | Gives |
| --- | --- |
| `{$value}` | The node's value, after the modifiers before `id`. |
| `{<column>}` | A column of the row the node reads: the dataset's own row, or under `$forEach` the item's row. |
| `{$dataset.<column>}` | A column of the dataset's own row, from anywhere. |
| `{$cache.<Type>.<field>}` | `field` of the row of a cached lookup table whose key is the node's value, matched as a replace matches it. |
| `{$param.<name>}` | A parameter the mapping declares, as the flow supplies it. |

A bare name is always a column, so a column called `value` is `{value}` and the node's own value is `{$value}`. The
rest of the template is written as it stands, and holds what an id carries: ASCII letters, digits, `_`, `-`, `.`, `:`
and percent-escapes such as `%2F`. Its own text holds the colons that part an id, `<partition>:<group>--<Entity>:<code>`,
and a reference ends with `:` and the version it pins, if any.

**Encoding.** Each token's value is trimmed and percent-encoded as UTF-8 with upper-case hex digits: letters, digits,
`_`, `-`, `.` and `:` stay as they are, a percent-escape already in the value is kept so nothing is encoded twice, and
anything else is escaped (`m/s` gives `m%2Fs`, `deg C` gives `deg%20C`). A `:` stays because a code may hold one
(`Projected:EPSG::23031`).

**A value that already is an OSDU id** names its record and is written as it is, with the version separator added,
when the template reads `{$value}`; one of another entity type than the template builds holds the record.

**The cache check.** Where the version of the partition's cache the render reads holds records of the entity type the
id names (reference data a [cache flow](cache.md) captured from the partition), the id has to name one of them, by its
exact id: a reference to a record the partition does not hold would reach OSDU pointing at nothing. Looking the id up,
rather than a code, finds the record whatever its `Code` says. The record found is recorded with the render, so a later
version that drops it reaches the record. A version holding no record of that entity type answers nothing, and the id
is written as built. An id whose code carries colons of its own and that ends in a version is not looked up.

**`$unverified: true`** on the node writes an id the cache holds no record under all the same, recorded as an
unverified reference. When a later refresh of the cache holds the record, the change is tagged `found` and the record
is built again against it. Use it where the source is the authority for the code and the partition only lags behind,
never where a code the partition does not hold means the value is wrong. It is refused on a node that builds no id.

| Situation | `$required: true` (default) | `$required: false` |
| --- | --- | --- |
| The node's value is empty | Record held: `dataset.<column> is empty, and the entry is required`, as for any empty value | Property left out |
| A token has no value (an empty column, a key the lookup table does not list, a row with nothing in the field) | Record held: `the id <template> cannot be built, since <token> has no value, and the entry is required` | Property left out |
| The cache holds records of the entity type, and none under this id | Record held: `... holds no such <entity type> record in <types>, so the reference would point at nothing` | Property left out |
| A parameter the flow gives no value; a cache token on a type that is missing or holds OSDU records; two rows answering only once case is ignored with different values; a row holding several values in the field; text that is not valid Unicode | Record held | Record held |
| The id built is not OSDU's id shape, does not match the variable's pattern, or names an entity type its relationship does not allow | Record held | Record held |

A half-built id is never written. Every row a cache token read, and every key a lookup table did not list, is recorded
with the record's cache dependencies, as for a replace.

Refused when the mapping is read:

| Written | Message |
| --- | --- |
| `{param.dataPartition}` | `{param.dataPartition} is not a column; the mapping's own tokens start with '$', so write {$param.dataPartition}.` |
| fewer than two colons of its own | `an OSDU id is <partition>:<group>--<Entity>:<code>, ... the template writes 1 colon(s) of its own, and a token's value never parts an id` |
| `...:Wellbore:...` | `the part between the first and second colon is the entity type, <group>--<Entity> ..., and the template writes 'Wellbore' there` |
| no token that changes by record | `the template reads nothing that changes from record to record, so every record would get the same id; write it as a literal, ...` |
| a space or another character an id cannot carry | `'U+0020' at position 54 is not a character an OSDU id carries; ...` |
| a lone `%` | `the '%' at position 54 starts no percent-escape; write a '%' an id carries as %25` |
| `{$param.region}` not declared | `builds an id from {$param.region}, but the mapping declares no parameter 'region'.` |
| a modifier after `id` | `id builds what the node writes, so it is the last modifier; move trim before it.` |
| `id` and `ref` on one node | `a node builds one id, and its $modifiers list ref and id; keep one.` |

The preflight checks that the variable takes text without a `format`; that each cache token reads a lookup table the
cache version holds, at a field its rows hold; that each parameter has a value; and builds the id with a stand-in for
every row value, refusing a template whose ids are of an entity type the variable's relationship does not allow or do
not match its pattern (a reference without its trailing `:`, say). Where a token writes the entity type, that is checked
on each record as its id is built.

## ref

```yaml
SamplingDomainTypeID:
  $from: sampling_domain
  $modifiers:
    - replace: $cache.SamplingDomain
    - ref
```

`ref` is `id` with the template written for you: `{$param.dataPartition}:<group>--<Entity>:{$value}:`, the reference
to the record whose code is the value, in the flow's partition. Written alone, the entity type is the one the
variable's `x-osdu-relationship` names in the pinned template. A variable that points to several names the one meant,
by its entity (`- ref: UnitOfMeasure`, which also completes a relationship that names only a group) or in full
(`- ref: reference-data--UnitOfMeasure`, which also serves a variable the template gives no relationship). Everything
the `id` section says holds for `ref`: the encoding, a value that already is an id, the cache check, `$unverified`, the
holds and the checks.

The loader refuses a `ref` naming text that is no entity type (`ref 'master data' is not an entity type; name it as the
template does, such as ref: UnitOfMeasure, or in full, such as ref: reference-data--UnitOfMeasure.`). The preflight
refuses a `ref` the template does not settle, naming the types the variable points to (`... points to <types>, so a bare
ref cannot tell which the value is a code of; name it, such as ref: <Entity>`), and a render meeting one holds the
record with the same reason. Use `id` when the id is built from anything but the value: another column, a cached lookup,
a fixed code.

## Errors when the mapping is read

Besides those above, each naming the file and the modifier's place (`record.data.Name $modifiers[0]: ...`):

| Written | Message |
| --- | --- |
| `[strip]` | `'strip' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date, number, id and ref.` |
| `[split]` | `'split' needs settings, such as split: { separator: ",", part: 1 }.` |
| `split: { separator: ",", part: 0 }` | `split needs the part to keep, counting from one, such as split: { separator: ",", part: 1 }.` |
| `split` with `otherwise` beside it | `a modifier is one setting, ...; split and otherwise are written as one. Only replace takes a setting beside it (otherwise).` |
| `replace: { " ": x }` | `replace lists an empty incoming value; an empty value is never replaced, it stays empty.` |
| `replace: { "A": x, "A ": y }` | `replace lists 'A' and 'A ', which are the same value once surrounding spaces are removed, and a value is matched trimmed.` |
| `default:` beside `replace` | `replace takes 'otherwise', 'match' and 'field' beside it, not 'default'.` |
| `match:` beside a written table | `'match' and 'field' choose the fields of a table read from the cache (replace: $cache.<Type>); a table written in the mapping matches on its keys and replaces with what each lists.` |
| `number: { thousands: "," }` | `number takes 'decimal' and 'group', not 'thousands'.` |
| `$modifiers: trim` | `$modifiers is a list, such as $modifiers: [trim].` |
| `$unverified: true` without `id` or `ref` | `$unverified lets an id the node builds with id or ref go out when the cache holds no record under it, and this node builds no id; build one, or remove $unverified.` |

What needs the template or the cache (an `equals`, `date` or `number` that cannot fill its property, a cached table that
is not there, an id the variable does not take) is reported by the preflight: `sqlflow check`, the mapping builder and
every run ([the preflight](../concepts/preflight.md)).
