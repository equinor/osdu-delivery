---
id: delivery-flow-mapping-lookups-composite
title: "A findBy line of several comparisons: finding a cached record by several fields together, joined by and"
type: flow-reference
summary: "A $findBy line joined by and finds the cached record holding every value: a lookup table keyed by one column, read by several of its columns."
keywords:
  - composite lookup
  - lookup by several columns
  - multi-column join
  - "$findBy and"
  - identity key
  - lookup table key
  - composite key
  - "findBy several fields"
yamlPath: "(documentType: mapping) record.<property>.$findBy, lookups.<name>.$findBy"
related:
  - delivery-flow-mapping-lookups
  - delivery-guide-lookup-table-cache
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-concept-change-detection
  - delivery-concept-preflight
  - delivery-table-cache-set-entry
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheUsage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
---

# A findBy line of several comparisons: finding a cached record by several fields together, joined by and

When no one field tells the records apart, a `$findBy` line compares several, joined by `and`, and finds the record that
holds every value. A cached type is kept under one key: a lookup table under one unique column of its own
([the lookup table guide](../guides/lookup-table-cache.md)), OSDU records under their ids. A table whose rows only
several columns tell apart is keyed by an identity column and read by those columns together.

```yaml
LogCurveTypeID:
  $cache: CurveMap.log_curve_type_id
  $findBy:
    - well = wellbore_name and mnemonic = curve_mnemonic
    - mnemonic = curve_mnemonic
  $required: false
```

Reads as: the curve type of the `CurveMap` row whose `well` is the row's `wellbore_name` and whose `mnemonic` is its
`curve_mnemonic`; else of the row whose `mnemonic` is the `curve_mnemonic`. The cache flow keeps the table under its
identity column and keeps the columns the line compares:

```yaml
types:
  - table: OsduData.silver.CurveMap
    key: row_id
    fields: [well, mnemonic, log_curve_type_id]
```

## How a line of several is read

- Each side of an `and` is a comparison as a line of one is, `<field> = <column>` or `<field> = 'text'`
  ([findBy lines](mapping-lookups.md#findby-lines)). The word `and` joins them in any case wherever it stands outside
  quotes, so `Name = 'Rock and Roll'` compares one field. A line compares each field once.
- The lines are still tried in order, and a line of several is one of them. It is skipped when any of its values is
  empty, and the node's modifiers change every column it compares.
- It reads a `$cache` node's type, or a `lookups` entry's. A search asks the platform one property at a time, so each of
  its lines compares one; a `$findAll` takes each condition as a line of its own.

## How the values are matched

By the steps a single value is ([how a value is matched](mapping-lookups.md#how-a-value-is-matched)), applied to the
values together:

1. **Exactly**: the records holding every value as written.
2. **Ignoring case**, only when no record holds them all exactly.
3. **Ignoring separators**, only with `$ignoreSeparators: true` and only when both steps above found nothing.

One record at the first step that finds any wins. Several hold the record whatever `$required` says, naming them:
`well 'C-3' and mnemonic 'GR' match 2 CurveMap records once case is ignored (4, 5) in <version>; make the incoming values
exact with a replace modifier, or compare another field that tells them apart`. A record holding only some of the values
is not found, however many hold each one.

A field holding a set meets its comparison by any one of its values. A comparison of `id` compares the record id and the
code it ends with, and a value naming a record (`dev:master-data--Well:1:`) is compared as the id it names.

When no line finds a record, the reason names every value of every line tried: `no CurveMap matches well 'Z-9' and
mnemonic 'NPHI' or mnemonic 'NPHI' in <version>`.

## How it stays fast

Each combination of fields a line compares is indexed in memory the first time a render asks for it, as a single field
is, so finding a record is one probe however common each value is alone. The index belongs to the cache version a run
loads and is built again from the next version. Nothing of it is stored, so a combination no mapping compares any more is
never indexed. A combination one record holds more than 64 sets of values under (sets at several fields multiply: ten
aliases beside ten names are a hundred) is not indexed, and is matched by reading the records the rarest of its values
names instead, with the same result.

## What a cache change does to it

- **Found**: the record the line found is recorded by each of its values, so a later version in which the record no
  longer holds one of them tags the records built from it `unmatched`.
- **Not found**: the values no record met together are recorded together, of a lookup table as of a type of OSDU records
  ([`osdu.CacheSetEntry`](../tables/cache-set-entry.md)). A later version holding a record that meets them all tags the
  records built without one `listed`, and a record sharing only some of the values reaches none of them. The Cache page
  lists them among what records were built without, as `well = Z-9 and mnemonic = NPHI`.
- Values too long to be kept whole as one ledger key (512 characters, written as a JSON list) are not recorded, as a key
  longer than any a table can hold is not.

## What is checked

The [preflight](../concepts/preflight.md) refuses a node when the cache version holds every field of none of its lines,
and warns of each field it does not hold, naming the line that therefore finds no record. A
[search term](../concepts/search-terms.md) does not follow a column a line compares together with others, since one of
its values does not name a record.

Refused when the mapping is read:

| Written | Message |
| --- | --- |
| `$findBy: well = wellbore_name and` | `$findBy 'well = wellbore_name and' has nothing on one side of an and; each side compares one field, ...` |
| `$findBy: well = a and well = b` | `$findBy 'well = a and well = b' compares well twice; a line compares each field once, ...` |
| `and` on a `$search` node's line | `... compares several properties at once, and a search asks the platform for one property at a time; write each as a line of its own, tried in order.` |
| `and` on a `$findAll` line | `... joins conditions with and; a $findAll takes each condition as a line of its own, ...` |

The mapping builder edits a line of several as one line: its first comparison, and each further one added with `+` under
it. A search entry's lines take none.
