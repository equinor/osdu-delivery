---
id: delivery-flow-mapping-lookups-find-all
title: "$findAll: reading every cached row that matches, as a list (access groups by field and country)"
type: flow-reference
summary: "A $findAll node reads every cached row a key finds, narrowed by empty fields and by one field containing another, as a list."
keywords:
  - "$findAll"
  - every matching row
  - access groups from cache
  - acl viewers by field and country
  - "findAll contains"
  - "field contains field"
  - group name contains country
  - "is empty"
  - country groups
  - relisted
yamlPath: "(documentType: mapping) record.<property>.$findAll"
related:
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-values
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-concept-change-detection
  - delivery-concept-preflight
  - delivery-flow-mapping-lookups-composite
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheUsage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/tests/SqlFlow.Delivery.Tests/AccessListTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/FindAllContainsTests.cs
---

# $findAll: reading every cached row that matches, as a list (access groups by field and country)

`$findBy` names the one record a value stands for ([findBy lines](mapping-lookups.md#findby-lines)). `$findAll` reads
every row of a cached type a value keys, however many, and the node gives the field it reads from each of them, as a
list.

```yaml
$cache: AccessGroup.GroupEmail
$findAll:
  - CountryID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID
  - FieldIDs is empty
  - GroupName contains CountryName
```

Reads as: the group address of every access group naming the wellbore's country, no field, and a name that says the
country.

## The lines

Each line is a condition of its own, and a row is read when it meets every one of them.

- **Exactly one key line**, `<field> = <operand>`. The operand is a column (after the node's modifiers), a quoted text,
  or `$lookup.<lookup>.<path>`, a path of the record a lookup finds, every value of which is a key of its own (a
  wellbore in two fields keys both). A `$lookup` operand must read another type than the node.
- **Any number of `<field> is empty` lines**, which keep only the rows holding nothing under that field.
- **Any number of `<field> contains <field>` lines**, which compare two fields of the same row and keep the rows whose
  text under the first field holds their text under the second. Both sides are fields of the row, so a rule such as "the
  group's name says its country" holds for every country without naming one.

A row matches the key line when any value of its key field is a key, **ignoring case**. A key that is an OSDU reference
also finds rows naming the record with or without the separator before its version (`dev:master-data--Field:1234:` and
`dev:master-data--Field:1234`).

A contains line compares the two texts by **their letters and digits alone, ignoring case**: spaces, dashes, dots and
every other mark are dropped from both before the comparison, so `United Kingdom` is found in
`dataoffice unitedkingdom seismic` and in `Data Office - United Kingdom`. Letters outside ASCII are kept and compared
without regard to case. It looks for a part, not a word, so `Oman` is found in `Romania`; the key line has already chosen
the rows, so a contains line only ever compares a row's two fields with each other. A field holding a list meets the
line when any of its values holds any value of the other. A row holding nothing under either field, or nothing but marks
under the second, does not meet it, since every text would hold an empty part.

Rows are read in the order of their ids, and a value two rows give (whatever its case) is written once. `<Type>.id`
writes each row's id in the reference form; a lookup table has no id to write.

## What it gives

A `$findAll` gives a list, so it fills a list property, or an item of a [list of values](mapping-values.md) such as
`acl.viewers`. When it finds no row, or the rows give nothing, a required node holds the record naming the keys and
what narrowed the rows (`no AccessGroup row with FieldIDs empty and GroupName containing its CountryName holds '...'
under CountryID in <version>`), and an optional one adds nothing. A lookup that finds no record gives no keys, which
counts the same way.

Every key is recorded with the ids of the rows it found, none included, and every field a row was judged by: each field
a row was asked to be empty, and both fields of each contains line, as the row held them. A refresh that lists another
row under a key, drops one, or changes a field a row was judged by (a group renamed, a country name corrected) tags every
record built from it as `relisted` or `changed`, so a record may go out without a group the cache does not list yet and
is delivered again when it does.

## Errors when the mapping is read

| Written | Message |
| --- | --- |
| `$findAll` beside `$findBy` | `... finds its record with $findBy and reads every matching row with $findAll; a node does one of them ...` |
| two `=` lines | `... it takes one such line, the key the rows are found by, and any number of <field> is empty and <field> contains <field>.` |
| only `is empty` or `contains` lines | `$findAll needs the line the rows are found by, <field> = <value>, ...` |
| on a `$from` or `$search` node | `$findAll reads every row of a cached type that matches, so it belongs to a $cache node; ...` |
| a line joined by `and` | `... joins conditions with and; a $findAll takes each condition as a line of its own, ...` |
| an empty line | `$findAll has an empty line; write each line as a condition, ...` |
| with `$ignoreSeparators` | `... remove $ignoreSeparators.` |
| `GroupName contains 'Norway'` or `GroupName contains $lookup.country.Name` | `... a contains line compares two fields of the same row, such as GroupName contains CountryName, ...` |
| `GroupName contains GroupName` | `... compares GroupName with itself, which every row holding a value meets; name two fields of the row.` |
| a field asked to be empty and read by a contains line | `... asks for CountryName to be empty and reads it in 'GroupName contains CountryName', ...; no row holds both.` |
| a line written twice | `... says it a second time.` |

The preflight also refuses a key field the cache version does not hold, a field asked to be empty that no row of the
type holds (`... no row of cache version '<version>' holds a <field>, so every row would pass. Capture data.<field> in
the cache flow that fills <Type>.`), a field of a contains line that no row holds (`... so no row would pass. Both sides
of a contains line are fields of the <Type> rows ...`, which is also how a column written where the line names a field
shows), a `$lookup` path no record holds, and a `$findAll` written to a single-value property.

## Access groups from cached records

An organisation that keeps its entitlement groups per field and per country as records in the partition can widen
each record's viewers from them. Here the partition holds a type the cache flow names `AccessGroup` (the kind below
stands for whatever kind your organisation defines), one record per group: the group address and name, the fields it
covers, and its country. The cache flow captures the paths the mapping reads:

```yaml
types:
  - kind: "osdu:wks:master-data--Wellbore:*"
    name: Wellbore
    fields:
      - data.FacilityName
      - data.NameAliases.AliasName
      - data.GeoContexts.FieldID
      - data.GeoContexts.GeoPoliticalEntityID
  - kind: "example:governance:reference-data--AccessGroup:*"
    name: AccessGroup
    fields: [data.GroupEmail, data.GroupName, data.FieldIDs, data.CountryID, data.CountryName]
```

The mapping finds the wellbore once and reads its field and country there:

```yaml
lookups:
  wellbore:
    $cache: Wellbore
    $findBy:
      - FacilityName = wellbore_name
      - NameAliases.AliasName = wellbore_name
    $ignoreSeparators: true

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers:
      - "{$param.aclViewer}"
      - $cache: AccessGroup.GroupEmail
        $findAll: FieldIDs = $lookup.wellbore.GeoContexts.FieldID
        $required: false
      - $cache: AccessGroup.GroupEmail
        $findAll:
          - CountryID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID
          - FieldIDs is empty
          - GroupName contains CountryName
        $required: false
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    WellboreID:
      $lookup: wellbore.id
```

The viewers are the flow's group, then the groups of the wellbore's fields, then the country groups of its country,
each once. A country group is a row naming the country, no field, and a name that says the country, so neither a
field's group nor a group the data office files under the country for another purpose widens to the whole country. A
wellbore in no field, or a field with no group yet, gives the record what was found and the flow's group at the least;
the refresh that brings the missing group redelivers it. The access lists must hold at least one literal value, so no
record goes out without a viewer; the legal lists take no nodes at all.
