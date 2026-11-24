---
id: delivery-flow-mapping-lookups
title: "Mapping lookups ($cache, $findBy, $findAll, $lookup, $search): reading cached records and searching the platform"
type: flow-reference
summary: "How a mapping finds a value outside the row: a cached record by $findBy, every matching row with $findAll, a named lookup, or a search of OSDU."
keywords:
  - "$cache"
  - "$findBy"
  - "$findAll"
  - "$lookup"
  - "$search"
  - lookups
  - searches
  - "$ignoreSeparators"
  - reference data lookup
  - find record by code
  - wellbore id by name
  - cache miss held record
yamlPath: "(documentType: mapping) lookups, searches, record.<property>.$cache | $lookup | $search"
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-modifiers
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-concept-change-detection
  - delivery-concept-preflight
  - delivery-flow-mapping-lookups-composite
  - delivery-flow-mapping-lookups-find-all
  - delivery-wiki-pattern-catalog
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.Cache.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.FindBy.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.FindAll.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.Search.cs
  - osdu/src/SqlFlow.Delivery/Rendering/CachedReferences.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SearchFields.cs
  - osdu/src/SqlFlow.Delivery/Rendering/IRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SystemProperty.cs
  - osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery.Search/OsduQuery.cs
  - osdu/src/SqlFlow.Delivery.Search/ServiceParser.cs
  - osdu/tests/SqlFlow.Delivery.Tests/AccessListTests.cs
---

# Mapping lookups ($cache, $findBy, $findAll, $lookup, $search): reading cached records and searching the platform

Most values a mapping writes come from the row it renders. The rest come from records the row only names: the unit
record a curve's unit code stands for, the wellbore a log belongs to, the access groups of that wellbore's field. A
mapping reaches them in two places. **The partition's cache** holds what cache flows captured: OSDU reference data,
other OSDU records, lookup tables loaded from ingestion tables, dictionaries and dimensions. A `$cache` node finds one
cached record with `$findBy`, a `$findAll` node reads every matching row, and a named lookup finds one record once for
every node that reads it. **A search of the platform** (`$search`) asks OSDU itself for the one record holding a value,
for record sets too large or too changeable to cache.

The mapping never names a cache. It reads the cache of the partition the rendering flow delivers to, at the version the
render pins; how types get there is the [cache flow](cache.md)'s and the
[partition cache](../concepts/partition-cache.md)'s business. The value nodes these lookups sit in (`$from`, `$value`,
`$coalesce`, lists, `$required`) are on [mapping values](mapping-values.md), and the mapping document itself on
[the mapping document](mapping.md). Changing an incoming value before it is compared (`trim`, `replace`) is a
[modifier](mapping-modifiers.md).

## Which one to use

| You need | Write | Reads |
| --- | --- | --- |
| The id, or a field, of the one record a code or name stands for | `$cache: <Type>.id` or `<Type>.<field>` with `$findBy` | The partition's cache |
| The one record several values stand for together (a well and a mnemonic) | `$findBy: <field> = <column> and <field> = <column>` | The partition's cache ([a line of several comparisons](mapping-lookups-composite.md)) |
| Several values of one record (its id and the groups of its field) | a `lookups:` entry, read with `$lookup: <name>.<field>` | The partition's cache, once per row |
| One value from every row that matches, as a list (access groups) | `$cache: <Type>.<field>` with `$findAll` | The partition's cache |
| The id of a business record too large a set to cache (wellbores) | a `searches:` entry, read with `$search: <name>` and `$findBy` | The OSDU search service, during the run |
| A translation of the incoming value (unit spellings, codes) | `replace: $cache.<Type>` | The partition's cache ([modifiers](mapping-modifiers.md#replace-from-the-cache)) |
| A reference built from the value without looking anything up | the `id` or `ref` modifier | The cache, to check the id exists where it holds that entity type ([modifiers](mapping-modifiers.md#id)) |

## $cache and $findBy

```yaml
CurveUnit:
  $cache: UnitOfMeasure.id
  $findBy:
    - Code = curve_unit
    - Name = curve_unit
```

Reads as: the id of the cached `UnitOfMeasure` whose `Code`, or else whose `Name`, is the row's `curve_unit`.

`$cache` names the cached type and what to read of the record found:

| Written as | Gives |
| --- | --- |
| `<Type>.id` | The record's OSDU id, with the trailing `:` OSDU relationships use (`dev:reference-data--UnitOfMeasure:m:`). |
| `<Type>.<field>` | A field the cache keeps for the record, such as `Name`, or a path inside a field cached whole (`NameAliases.AliasName`). A field holding an object (an OSDU path that yields one, or a lookup table's column read with [`decode: json`](cache-origins.md#a-column-holding-a-json-object)) is written whole where the template takes an object, such as the record's `tags`. |

The type is the name the cache flow gives it (`types[].name`). A node without `$findBy` or `$findAll` is refused:
`a $cache node needs $findBy, which says which record to read, ...`.

### findBy lines

Each line is `<field> = <operand>`:

- `<field>` is a field of the cached record, named as the cache keeps it: the captured path without its `data.` root
  (`Code` for `data.Code`), or the `as` name the cache flow gave it. The `data.` prefix is accepted too. A path inside a
  field cached whole (`NameAliases.AliasName`) works, and a field holding a set matches on any one of its values, so a
  wellbore with three aliases is found by each of them. A lookup table is matched on its key column by name, or any
  other column it keeps.
- `<operand>` is a column, named as `$from` names it (a bare name reads the row the node is in, `$dataset.<column>` the
  dataset's own row from inside a `$forEach`), or a quoted text (`'KellyBushing'`), which reads the mapping's
  parameters as every literal does (`'{$param.dataPartition}:master-data--GeoPoliticalEntity:NO:'`); a parameter the
  mapping does not declare is refused when the mapping is read.

One line, or a list. The lines are tried in order: a line whose value is empty is skipped, and the first line that finds
exactly one record wins. The lines tried before it are recorded with the render, each as it found nothing or several
records, so a version in which one of them finds exactly one record reaches the records built from the later line. A node's [modifiers](mapping-modifiers.md) change the incoming value before every line compares
it; what the cache holds is OSDU's own and is never modified, so `$modifiers` on a node whose lines compare only quoted
texts is refused (`$modifiers change incoming dataset values, and this node's $findBy reads none; cache values are never
modified.`).

A line of a `$cache` node or a lookup may compare several fields, joined by `and`, and finds the record that holds every
value: `well = wellbore_name and mnemonic = curve_mnemonic`. How such a line is matched, recorded and indexed is on
[a line of several comparisons](mapping-lookups-composite.md).

### How a value is matched

1. **Exactly**: the value, trimmed, as the field holds it. One record wins.
2. **Ignoring case**, only when the exact comparison found nothing, and only when exactly one record answers. OSDU codes
   that differ only by case are different records (`ft` is the foot and `fT` the femtotesla), so several answers select
   none of them.
3. **Ignoring separators**, only with `$ignoreSeparators: true` on the node, and only after both comparisons above found
   nothing. Letters and digits are kept in order (letters outside ASCII included, lower-cased) and every run of anything
   else counts as one separator, on the cached values and the incoming value alike: `WB A-12 ST2`, `WB_A_12_ST2` and
   `wb-a-12-st2` find the same name. It is for names, never codes (`s/m` and `S.M` would fold together). A value no
   record answers to even so is recorded with the render, and every version of the type is asked for it again, so one
   holding a record written another way (`WB_A_12` for `WB A-12`) reaches the record.

A value several records answer to, at whichever step, holds the record **whatever `$required` says**, naming the
candidates: `'<value>' matches 2 UnitOfMeasure records by Code once case is ignored (...) in <version>; make the incoming
value exact with a replace modifier`. A later line that finds exactly one record still wins over an earlier ambiguous one.

### Special values

- **A line on `id`** compares the record id, and the code the id ends with, written as the id encodes it
  (`Gamma%20Ray`) or decoded (`Gamma Ray`), when the type keeps no field of its own called `ID`. A table that names
  reference data by its code therefore finds the record that code is the id of, even where the record's own `Code` says
  something else. A type whose cache flow keeps a field called `ID` (the `data.ID` of reference data) compares that
  field under `id` and `ID` instead. `$cache: <Type>.id` always writes the record id.
- **A value that already is an OSDU id** (`dev:reference-data--UnitOfMeasure:m`) names its record by id, exactly, a code
  holding colons of its own included (`dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031:`). When
  the cache does not hold it, a `<Type>.id` node writes it as it is, ending in `:` unless it names a version
  (`dev:master-data--Wellbore:1234:1700000000000`, as OSDU's reference pattern reads it), and a node reading another field
  counts it as a miss (`'<value>' is already an OSDU id that <version> does not hold, ...`). A required node held for it
  waits for the record ([a record held for a reference waits for it](../concepts/partition-cache.md#a-record-held-for-a-reference-waits-for-it)).
- **A lookup table** (a `table` or `dictionary` type) holds rows, not OSDU records, so it has no id to write. Read one of
  its fields: `$cache: CurveDictionary.log_curve_type_id` with `$findBy: mnemonic = curve_mnemonic`. A value shaped like
  an OSDU id is matched there like any other text.

### What a cache node gives

| The cache answers | Required node (default) | `$required: false` |
| --- | --- | --- |
| One record | Its id or field | Its id or field |
| No record on any line | Record held: `no UnitOfMeasure matches 'furlong' by Code/Name in version <version> of the cache of partition 'dev'` | Property left out |
| Every line's value empty | Record held: `<columns> is empty, so there is nothing to find in the cache, and the entry is required`, or, for a value the node's modifiers turned into nothing, `<column> is '<value>', which its modifiers (...) turn into no value, ...` | Property left out |
| Several records | Record held | Record held |
| The record keeps nothing at the field read: no value, blank text, or an empty list | Record held, naming what the type caches | Property left out |
| The cache version holds no such type | Record held: `the cache holds no type '<Type>' in <version>` | Record held |

A cached field that holds a set writes a list where the template takes one, and holds the record where the template
takes a single value, unless the set holds exactly one value.

What a node looked for is recorded with the record: the record it found, or, for a type of OSDU records, every value no
record answered to. A later refresh of the cache that brings the missing record in tags every record built without it as
`listed`, and the rollout renders them again ([change detection](../concepts/change-detection.md)).

## Record ids held in cached fields

Where OSDU already models a translation, a cached field holds the id of the record a source value stands for, and the
mapping writes it as the reference. `reference-data--ExternalUnitOfMeasure` maps an external system's unit code to the
partition's `UnitOfMeasure` in `data.UnitOfMeasureID`. A cache flow keeps the code and the id for the well database's
namespace:

```yaml
types:
  - kind: "osdu:wks:reference-data--ExternalUnitOfMeasure:*"
    name: WelldbUnits
    query: 'data.NamespaceID:"dev:reference-data--ExternalCatalogNamespace:WELLDB:" AND data.MapStateID:"dev:reference-data--CatalogMapStateType:identical:"'
    fields: [data.Code, data.UnitOfMeasureID]
```

```yaml
CurveUnit:
  $cache: WelldbUnits.UnitOfMeasureID
  $findBy: Code = curve_unit
```

A cached field written to a relationship property is checked as a reference:

- The preflight refuses the mapping when a value the field holds is not an OSDU record id, or names an entity type the
  relationship does not allow. Where the cache holds records of the entity type an id names, an id it holds no record
  for is a warning, and a render meeting it holds the record whatever `$required` says: the reference would point at
  nothing. Where the cache holds no records of that type, the id is written as it is.
- The id is written with the `:` that separates a version, added where the cached value leaves it out; an id naming a
  version is written as it is. The record it names is recorded with the render, so a cache version without it reaches
  the records pointing at it.

## lookups and $lookup

A record often needs several values of one other record: a well log writes its wellbore's id, and its access list takes
the groups of the field and the country that wellbore lies in. A lookup finds that record once per row, in the cache,
and every node reads the record it found, so the id and the values derived from the record can never come from two
different records.

```yaml
lookups:
  wellbore:
    $cache: Wellbore
    $findBy:
      - FacilityName = wellbore_name
      - NameAliases.AliasName = wellbore_name
    $ignoreSeparators: true
    $description: The log's wellbore, by its name or one of its aliases.

record:
  data:
    WellboreID:
      $lookup: wellbore.id
```

| Key | Meaning |
| --- | --- |
| `lookups.<name>` | The name nodes read the lookup by: letters, digits, underscores and hyphens. |
| `$cache` | The cached type the record is found in, by name alone (`Wellbore`); the field is chosen where the lookup is read. |
| `$findBy` | Required. How the row finds its record, as on a `$cache` node. The columns read the dataset's own row, wherever the lookup is read. |
| `$modifiers` | Changes to the incoming value each line compares. `id` and `ref` are refused: the record's id is read with `$lookup: <name>.id`. |
| `$ignoreSeparators` | The separator fold, as on a `$cache` node. |
| `$description` | Free text. |

`$lookup: <name>.<field>` reads a field of the found record, or its `id` in the reference form, anywhere in the tree,
inside a `$forEach` item included. It takes only `$when`, `$required`, `$assert` and `$description` beside it; how the record is
found is the lookup's to say, so `$findBy`, `$findAll`, `$modifiers` and `$ignoreSeparators` on the node are refused
(`... a $lookup node reads the record its lookup finds; write $findBy on the lookup under lookups.`). A `$lookup` node
renders exactly as the `$cache` node it stands for, with the same holds and dependencies. Every lookup must be read by a
node (`lookups.<name> is read by no node; ...`), and `$required` on the lookup itself is refused, since it decides for
the node that reads it.

A lookup reads the cache, so it suits a type a capture can hold whole. For a set no capture can keep current, search
the platform instead ([searches](#searches-and-search)).

## $findAll: every matching row

`$findBy` names the one record a value stands for. `$findAll` reads every row of a cached type a value keys, however
many, narrowed by fields that must be empty and by one field of the row that must contain another, and the node gives
the field it reads from each of them, as a list: the access groups of a wellbore's field and country, for one.

```yaml
$cache: AccessGroup.GroupEmail
$findAll:
  - CountryID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID
  - FieldIDs is empty
  - GroupName contains CountryName
```

Its lines, how a row is matched, what it gives, the cache trail it records and its errors are on
[$findAll](mapping-lookups-find-all.md), with the access groups example.

## searches and $search

Some references point at business records that grow without bound and change daily, such as a partition's wellbores.
Capturing them all so each record can name one costs more every day and is always behind. A search asks the platform,
one value at a time, for the one record holding exactly that value.

```yaml
searches:
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"
    schema:
      kind: osdu:wks:master-data--Wellbore:1.3.0
      version: 58d6bdbd9d066a06
    description: The partition's wellbores, found by name or by any of their aliases.

record:
  data:
    WellboreID:
      $search: Wellbore
      $findBy:
        - data.FacilityName = wellbore_name
        - data.NameAliases.AliasName = wellbore_name
```

| Key | Meaning |
| --- | --- |
| `searches.<name>` | The name nodes read the search by, as `$search: <name>`: letters, digits, underscores and hyphens. |
| `kind` | The kind searched: one entity type, at one version or every version (`*`); only the version may be `*`. |
| `schema.kind`, `schema.version` | `schema.kind` is required. The [template](../concepts/templates.md) whose schema says how the kind's properties are indexed: the same entity type, and the same version when `kind` names one, read from the template folders on the mapping's path. `schema.version` names its version exactly; left out, the search reads the one version the nearest folder holding the kind holds ([template folders](../concepts/templates.md#template-folders)). |
| `description` | Free text. |

Every `$search` node names a declared search, every declared search is read by a node (an unread one is refused, since
each value it asks costs a call), and two searches may not look in one kind. A `$search` node gives the found record's
id only, with the trailing `:`. Its `$findBy` lines name properties under `data.`, as the schema names them, with dotted
names of letters, digits and underscores, one property a line (a line joined by `and` is refused), and are tried in
order: the first line that finds exactly one record wins, and a line is asked only when every line before it found
nothing. Modifiers change the incoming value before it is asked for.

### How each line is asked

The query follows how the pinned schema has the platform index the property, never the path alone:

| The property is | The query asks |
| --- | --- |
| a string, or a list of strings | its `keyword` sub-field, the whole value, case included: `data.FacilityName.keyword:"<value>"` |
| inside an array the schema marks `x-osdu-indexing: nested` | the service's nested form: `nested(data.NameAliases, (AliasName.keyword:"<value>"))` |
| inside an array marked `flattened`, or a legacy link whose pattern starts `^srn` | the property itself, which is stored as a keyword |

The preflight refuses a property the platform cannot match exactly, naming why: a date or a number, an object, an
array of objects with no indexing hint (not indexed inside), a nested array inside a nested array, a property the schema
does not have, or a schema no template folder on the mapping's path holds.

A value that cannot be asked for is not sent: an empty value, a value longer than the 256 characters the keyword
sub-field keeps, the text `null` (what the keyword holds for a property without a value), a control character, the text
`nested(` anywhere, and inside a nested query unbalanced parentheses or `AND`, `OR` or `NOT` followed by a word ending in
a colon. A value that already is an OSDU id of the searched entity type names its record without a search, and is
written with the version separator added unless it names a version; one of another entity type holds the record.

### What a search gives

| The platform answers | The node |
| --- | --- |
| Exactly one record | Its id. |
| No record, on every line asked | Holds the record when required (`no Wellbore on the platform (osdu:wks:master-data--Wellbore:*) matches: ...`), left out otherwise. |
| Several records | Holds the record whatever `$required` says, naming the first five and counting the rest. |
| The query is refused (400) | Holds the record whatever `$required` says, quoting the service. |
| A line could not be asked, and no other line found the record | Holds the record whatever `$required` says: a value that cannot be asked proves nothing about whether the record exists. |
| No answer: unreachable, credentials refused, failure past the flow's retries | Fails the run. A missing answer is never taken for "no record". |

**Case.** The keyword keeps the value as written. A partition whose indexer keeps a lowercased copy of text
(`keywordLower`, the indexer's system property `featureFlag.keywordLower.enabled`, which every cache capture reads from
the platform) gets a second question when no record holds the value exactly: the same value on the lowercased copy,
taken only when it finds one record. Several hold the record (make the value exact with a `replace` modifier). A
partition whose setting is off or unknown is asked exact questions alone, and so is a keyword property, which has no
lowercased copy. `sqlflow check` shows which rule applies on its `searches` line.

### How a run asks

The render does no I/O. A row whose question the run has not asked yet renders unfinished, naming the question; the
plan asks a batch's questions, each distinct question once, and renders the waiting rows again, so a row whose first
line found nothing asks its next line in the next round. Answers are kept for the run, found or not, and at most eight
queries are in flight at a time. Each query (`POST /api/search/v2/query`) asks for the ids of at most five matching
records, under the flow's own target, credentials and `data-partition-id`: a search finds only what the flow's identity
may view in the partition it delivers to. `sqlflow values` and `sqlflow preview` ask the same questions the same way.

- The search index is eventually consistent: a wellbore delivered moments ago is found once the indexer has picked it
  up. A record held because its wellbore was not found stays held until it is released or its row changes.
- What a search answered is not part of the render context. A delivered record renders again when its row, its mapping
  or the partition's `keywordLower` setting changes, not when the platform's wellbores do. A mapping that only searches
  renders against no cache version, so a cache refresh renders none of its records again.
- In lineage the mapping reads the searched kind, so the flow delivering wellbores to the partition runs before a flow
  whose mapping searches for them ([lineage](../concepts/lineage.md)).

## Errors when the mapping is read

`sqlflow validate` and every load report these with the file and the place in it (`record.data.WellboreID: ...`):

| Written | Message |
| --- | --- |
| `$cache: Wellbore` on a node | `$cache names the cached type and the field it reads, such as $cache: UnitOfMeasure.id or $cache: CurveDictionary.log_curve_type_id; 'Wellbore' is not one.` |
| `$findBy: FacilityName wellbore_name` | `$findBy 'FacilityName wellbore_name' must read <field> = <column>, or <field> = 'text' for a fixed text, such as Code = unit.` |
| `$findBy` on a `$from` node | `$findBy selects the record a $cache or a $search node reads; this node reads the column log_name.` |
| `$ignoreSeparators` on a node that is not `$cache` | `$ignoreSeparators loosens how a value is matched against the cache, so it only applies to a $cache node.` |
| `id` or `ref` on a `$cache` node | `the ref modifier builds the id a node writes from a dataset value, and a $cache node already gives what it writes; read the value with $from: <column>, and build the id from it.` |
| `$lookup` with no `lookups` block | `$lookup reads lookup 'wellbore', and the mapping declares no lookups; declare it under lookups, ...` |
| a lookup without `$findBy` | `lookups.wellbore: a lookup needs $findBy, which says how the row finds its record, ...` |
| `$search` with no `searches` block | `... searches Wellbore, and the mapping declares no searches; add a 'searches' block naming the kind each one looks in.` |
| `$findBy: FacilityName = ...` on a search | `... a search compares a property under data, named by dotted names of letters, digits and underscores, ...` |
| `$search: Wellbore.id` | `$search names one of the mapping's searches, such as $search: Wellbore, and gives the id of the record it finds; 'Wellbore.id' is not a search name.` |
| a search without `schema` | `search 'Wellbore' pins no schema; a search pins the template of the kind it searches ...` |
| a schema of another entity type | `search 'Wellbore' searches osdu:wks:master-data--Wellbore:* and pins the schema of osdu:wks:master-data--Well:1.3.0, another entity type; ...` |

What needs the cache or the templates (a type the cache version does not hold, a field it does not cache, a
cached id of the wrong entity type, a search property that cannot be matched) is the preflight's to find, before any row
is rendered: `sqlflow check`, the mapping builder, and every run report it ([the preflight](../concepts/preflight.md)).
