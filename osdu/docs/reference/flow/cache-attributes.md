---
id: delivery-flow-cache-attributes
title: "Cache attributes (types[].attributes): parent record properties attached to each cached record, read as a dimension reads them"
type: flow-reference
summary: "Attach a parent record's properties to each cached record: a dimension's attributes on a cache type, steps through references, filters, keep and many."
keywords:
  - "types[].attributes"
  - "types[].lookupKinds"
  - cache attributes
  - parent record
  - parent document
  - nested record
  - through a reference
  - follow a reference
  - attach properties
  - build a dataset
  - dimension attributes in the cache
  - keep id
  - many join
  - maxReferences
yamlPath: "types[].attributes"
related:
  - delivery-flow-cache
  - delivery-flow-dimension-attributes
  - delivery-flow-cache-records
  - delivery-concept-partition-cache
  - delivery-flow-mapping-lookups
  - delivery-table-cache-definition
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceCaptureSpec.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheAttributeReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionLabeler.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionPath.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
---

# Cache attributes (types[].attributes): parent record properties attached to each cached record, read as a dimension reads them

A cache type's `attributes` attach facts of other records to each cached record: the name of a wellbore's well, the
field and the country it lies in, a property of its well's extension properties. They are a
[dimension's attributes](dimension-attributes.md), written the same way and read by the same engine, with each cached
record's id as the key. A refresh reads them while it captures the type and keeps each under its name beside the type's
`fields`, so the cache stays the dataset a mapping reads in memory: a mapping finds a wellbore by its well's name, or writes
its field's id, as it reads any cached field, and nothing is read from OSDU while a record renders.

```yaml
flowType: cache
name: welldb-wellbores-00-cache
batch: welldb
description: The wellbores the well log mappings link to, each with its well's name, its field and its country.
partitions: [dev]

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}

types:
  - kind: "osdu:wks:master-data--Wellbore:*"
    fields: [data.FacilityName, data.NameAliases]
    attributes:
      WellName: [data.WellID, data.FacilityName]
      FieldID: { path: "data.GeoContexts[GeoTypeID$=:Field:].FieldID", keep: id }
      Country: [data.GeoContexts.GeoPoliticalEntityID, "data[GeoPoliticalEntityTypeID$=:Country:].GeoPoliticalEntityName"]
    lookupKinds: [osdu:wks:master-data--Well:1.2.0]
```

A mapping reads them as any cached field ([lookups](mapping-lookups.md)):

```yaml
lookups:
  wellbore:
    $cache: Wellbore
    $findBy:
      - FacilityName = wellbore_name
      - WellName = well_name
```

## How an attribute is written

Exactly as a dimension's ([Dimension labels and attributes](dimension-attributes.md)): a path, a list of up to three
paths, each but the last reading the references the next records are found by, or settings.

| Written | Reads |
| --- | --- |
| `Operator: data.CurrentOperatorID` | A path of the cached record itself. |
| `WellName: [data.WellID, data.FacilityName]` | The well each wellbore's `data.WellID` names, then its `data.FacilityName`. |
| `FieldID: { path: "data.GeoContexts[GeoTypeID$=:Field:].FieldID", keep: id }` | Of the wellbore's contexts, the one whose type ends with `:Field:`, its field's id. |
| `Fields: { path: [data.GeoContexts.FieldID, data.FieldName], many: join, maxReferences: 50 }` | Every field the wellbore names, up to 50, their names joined by `; `. |

- **Filters:** `[Property=text]`, `[Property*=text]` (containing it, ignoring case), `[Property$=text]` (ending with it,
  ignoring case), and `[Property->path=text]`, which compares what the record each reference names holds at the path.
- **Settings:** `keep` (`value`, `key`, `id`, `reference`), `many` (`first`, `join`, `all`, `max`), `decode` (`none`,
  `url`), `maxReferences` (1 to 1000, 20 by default) and `trim`, each as a dimension's attribute takes it. `keep: value`,
  the default, shows a reference as the code its id ends with, so an attribute a mapping writes as a reference keeps
  `id` or `key`.
- **`lookupKinds`** names the one kind each entity type's records are looked up in, as a dimension's does; an entity type
  none names is looked up in every kind of it, `*:*:<entityType>:*`. It is refused on a type that reads no attribute.
- **Names:** a letter, then letters, digits and underscores, at most 64, at most 64 attributes, unique ignoring case
  among the attributes and the fields, and not `id`.
- **A value is text, at most 256 characters,** as a dimension keeps it: a longer one is cut under `keep: value` and left
  out under `key`, `id` and `reference`. A path among `fields` keeps whatever it yields, whole, so an object or a long text
  of the record itself belongs there.
- **`collect` is refused:** each cached record is its own key, so the values of the records holding a key are the
  record's own, which `fields` keeps. A type with attributes may leave `fields` out.

## How a refresh reads them

1. The type's search asks for what the first step of each attribute reads of a record (its path without filters, and
   the property each filter compares), beside the type's own paths.
2. Each page of cached records is handed to the dimension's labeler, keyed by the records' ids. The cached records in hand
   answer the first step; the records a later step reaches are read by id: through the search, 500 ids a search, as a
   dimension reads them, or for a type with [`fetchRecords: true`](cache-records.md) whole from storage, 100 ids a
   request, a record of another kind than `lookupKinds` names left out. So a parent's extension property is read when the
   type reads its records whole.
3. What each record reached gives is kept for up to 50,000 records across the capture, so a well a hundred wellbores
   name is read once. Attributes looking records up in the same kinds are read together.
4. Each attribute's value is kept on the cached record under its name. Why a record has none (its reference names no
   record, the record holds nothing at the path), how many named more records than `maxReferences`, and how many values
   were cut, the refresh logs as a dimension build's notes say them.

A `cache.before` step reads a type's attributes as its refresh does, and a `cache.after` step reads them for the records
its run landed, so what a step puts into the cache reads the same as a refresh would. A change to a parent reaches the
cached records that read it at the next capture of their type: the refresh, or a step naming the type, finds the new
value and tags the records built from it like any changed cached value ([onChange](cache.md#onchange)).

## Several flows, one type

An attribute is a field of the type: every cache flow of the partition declaring the type reads every attribute any of them
declares, and two declarations of one name must read it alike, every setting the same, or the second is left out
(`Wellbore.WellName is cached from { path: [data.WellID, data.FacilityName] } by cache flow 'project-a' and from { path:
[data.WellID, data.FacilityName], many: join } by 'project-b'`). The Cache page lists an attribute among the values a type
keeps, with that definition.

## What a dimension does that a cache type does not

A dimension builds a table of the distinct values of a key, so its `label`, `clean`, `columns`, `countRecords`,
`maxValues` and `views` describe that table, which a cache type does not build: it keeps the records themselves.
`elements` makes a table row of each object of a nested array; a cache type keeps the array whole under a field
(`fields: [data.Curves]`) and a mapping reads the paths inside it. The values of the records that reference a key (a
wellbore's logs) are a dimension keyed by that reference with a collected attribute, cached as a [dimension
type](cache-origins.md#dimension-values-dimension-dimensionflow).

## What goes wrong

| Message | Cause |
| --- | --- |
| `types[<n>]: attributes.<name> collects <path>, and a cached type is keyed by each record's own id ...` | `collect` on a cache type. |
| `types[<n>]: lookupKinds names the kind each entity type's records are looked up in ..., and the type reads no attribute.` | `lookupKinds` without `attributes`. |
| `types[<n>]: attributes.<name> takes a name a cached record holds already (id); ...` | An attribute named `id`. |
| `types[<n>]: Cached type '<type>' caches two paths under the name '<name>'; ...` | An attribute named like a field. |
| `types[<n>] holds table <table>, which takes no 'attributes': ...` | Attributes on a table, dictionary or dimension type. |
| `types[<n>]: attributes.<name> reads through 4 records; an attribute reads through at most 3, ...` | More than three steps. |

Every other message about an attribute's path or settings is a dimension's ([Dimension errors](dimension-errors.md)).

## Related

- [Cache flow](cache.md) and [records read whole](cache-records.md).
- [Dimension labels and attributes](dimension-attributes.md): the grammar, the same here.
- [Mapping lookups](mapping-lookups.md): reading a cached type's fields.
