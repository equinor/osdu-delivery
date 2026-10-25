---
id: delivery-flow-dimension-attributes
title: "Dimension labels and attributes: steps, keeping a key, several values, filters through a reference, lookups"
type: flow-reference
summary: "How a label and an attribute read the records a key names: steps, keep, many, decode, trim, filters through a reference, lookupKinds, collected values."
keywords:
  - labels and attributes
  - "keep: key"
  - "keep: id"
  - collected attributes
  - returnedFields
  - label search fields
  - "many: max"
  - "many: join"
  - "many: all"
  - "decode: url"
  - maxReferences
  - "trim: false"
  - "keep: reference"
  - lookupKinds
  - filter through a reference
  - "[Property->path=text]"
  - code point order
  - greatest value of a wellbore's fields
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-views
  - delivery-concept-dimension-tables
  - delivery-guide-dimensions
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionPath.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionLabeler.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionKeeping.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionElementReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
  - osdu/docs/census/keys.dimension.json
---

# Dimension labels and attributes: steps, keeping a key, several values, filters through a reference, lookups

A [dimension](dimension.md)'s label and attributes read the record each key names, step by step: each step but the
last reads the references the next step follows, and by default the first value the last step reaches is kept. An
attribute's settings say otherwise: which of several values it keeps, how many references a step follows, what each value
is decoded from, whether white space is kept, and how a reference is kept. A path's filter can compare a value of the
record a reference names, and a dimension can name the kind each entity type's records are looked up in. Together they
read what a pipeline aggregating the same records reads: the greatest of each of a wellbore's geographic contexts, codes
URL-decoded.

## Reading through steps

A label and an attribute read through steps. With one path, it is read from the record the key names. With a list, each
step but the last reads the references the next records are found by:

```yaml
label: data.FacilityName
attributes:
  Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
  Operator: [data.CurrentOperatorID, data.OrganisationName]
```

- A build finds the records by id through the search service, 500 ids a search, in the kind of the entity type the id
  names. A label and attributes that start at the same records are read in the same searches.
- Every reference a step reads is followed, at most 20 a key (an attribute's `maxReferences` says otherwise); the first
  record reached that holds a non-empty value at the last path gives it, unless the attribute's `many` keeps every value or
  the greatest ([Dimension attributes](dimension-attributes.md)). A label is cut at 1,024 characters, an attribute value
  at 256.
- A segment holding objects can filter them: `[Property=text]` keeps those whose property equals the text,
  `[Property*=text]` those whose property contains it ignoring case, `[Property$=text]` those whose property ends with it
  ignoring case. So the `Country` attribute above keeps, of every geopolitical entity a wellbore names, the one whose type
  is a country. `[Property->path=text]` compares a value of the record the property's reference names instead
  ([A filter through a reference](dimension-attributes.md#a-filter-through-a-reference)).
- A key that names no record, a record the search does not hold, and one holding nothing at the last path, have no label
  (or no such attribute); the build's notes count them and say why.
- A label or an attribute value that is itself a record reference is kept as the code its id ends with, decoded.

An attribute's name is a letter, then letters, digits and underscores, at most 64, unique ignoring case, none of
`value`, `keys`, `key`, `key_id`, `records`, `filter`, `label`, `id`, `partition`, and not the name of the dimension's
key or value column.

## Keeping a key, so tables join

An attribute is written as settings to give it a `keep`:
`WellboreID: { path: data.WellboreID, keep: key }`. There are four:

| `keep` | Kept as | `dev:reference-data--UnitOfMeasure:us%2Fft:` becomes | Joins to |
| --- | --- | --- | --- |
| `value` (default) | As a value shows it: a record reference by the code its id ends with, its escapes decoded | `us/ft` | nothing; it is for reading |
| `key` | Exactly as the record holds it | `dev:reference-data--UnitOfMeasure:us%2Fft:` | the key column of a dimension keyed by the same path |
| `id` | A reference as the id of the record it names, its version (or the latest's trailing colon) taken off | `dev:reference-data--UnitOfMeasure:us%2Fft` | the key column of a dimension keyed by `id` |
| `reference` | A reference with only the latest's trailing colon taken off, a version it names kept | `dev:reference-data--UnitOfMeasure:us%2Fft` | the key column of a dimension keyed by `id`; a reference to a version, nothing |

```sql
SELECT l.WellLogName, w.Country, s.SamplingDomainTypeName
FROM osdu.dim_WellLog AS l
LEFT JOIN osdu.dim_Wellbore AS w                 -- keyed by data.WellboreID; l.WellboreID is keep: key
    ON w.partition = l.partition AND w.WellboreID = l.WellboreID
LEFT JOIN osdu.dim_RefSamplingDomainType AS s    -- keyed by id; l.SamplingDomainTypeID is keep: id
    ON s.partition = l.partition AND s.SamplingDomainTypeID = l.SamplingDomainTypeID
```

`path` takes a path or a list of paths, exactly as the bare form does, and `keep` works on a collected attribute and an
element's field too. A value longer than a dimension keeps (256 characters for an attribute, 4,000 for an element's
field) is cut under `keep: value` and left out under `key`, `id` and `reference`, since a key cut joins to nothing; the
build's notes count them.

## Several values, decoding and white space

| Key | Default | Meaning |
| --- | --- | --- |
| `many` | `first` | Which of the values the last step reaches, over every record it reaches, the attribute keeps: `first`, the first record's that holds one; `join`, every one, each once, in the order reached, joined by `; `; `all`, every one as often as it is held, joined the same way; `max`, the greatest, compared by Unicode code point (the order of their UTF-8 bytes, as Spark's `max` orders text). Each attribute takes its own greatest, as a `MAX` of each column does: `FieldName` and a `FieldUUID` beside it may come from two fields. |
| `maxReferences` | 20 | How many records each step but the last follows for one key, 1 to 1,000: a key whose records name more follows the first that many, in the order they are reached, and the build's notes count the keys bounded so (`<n> key(s) name more than 20 record(s) at a step of FieldName, and only the first 20 were followed; raise maxReferences`). Raise it where `max`, `join` or `all` must read every record a key names. |
| `decode` | `none` | `url` decodes each value's percent-escapes before it is kept, their bytes read as UTF-8 (a sequence that is no UTF-8 becomes U+FFFD): what Spark's `try_url_decode` gives once a `+` is escaped. A `+` stays a `+`, and a value holding a `%` that two hexadecimal digits do not follow is kept as it is. Only with `keep: value`. |
| `trim` | `true` | `false` keeps each value exactly as the record holds it, white space at its ends and white space alone too, as a pipeline that keeps text as it reads it has it. An empty text is no value either way. Only with `keep: value`: a key, an id and a reference are always trimmed, as the text a search compares. |
| `keep` | `value` | Besides `value`, `key` and `id` ([Keeping a key](dimension-attributes.md)), `reference`: a reference with only the trailing colon of the latest taken off, a version it names kept (`dev:master-data--Well:W1:` is `dev:master-data--Well:W1`, `dev:master-data--Well:W1:1700000000` stays as it is). It joins a dimension keyed by `id` as `id` does, so a reference to a version joins nothing, as in a pipeline that strips a trailing colon alone. |

Each value is decoded, then kept, then chosen among, so `max` compares what the column holds, and `%2F` and `/` are one
value to `join`. A value longer than an attribute keeps (256 characters) is chosen whole and cut after, under
`keep: value`, and left out under `key`, `id` and `reference`; the build's notes count them. A collected attribute
(`collect`) takes none of `many`, `maxReferences`, `decode` and `trim`: each value it collects is a row of its own, and the
text its search filter matches. An element's field takes `many` (`first`, `join`, `all`, `max`), `decode`, `trim` and `keep`
the same way, over the values its path reaches in one element.

## A filter through a reference

A path's segment can filter the objects it holds by a value of the record each object's reference names, rather than by
the reference itself: `[Property->path=text]`, with `=`, `*=` or `$=` as any filter compares
([Labels and attributes](dimension-attributes.md)). `data.GeoContexts[GeoTypeID->data.Code=Country]` keeps the
contexts whose `GeoTypeID` names a record holding `Country` at `data.Code`, whatever its id says; a filter on the id's text
(`[GeoTypeID*=:Country:]`) takes a type whose id holds `:Country:` for a country, and misses a country whose id does not.

- The path after `->` is plain segments from the referenced record's root, no filter of its own.
- A reference is followed with or without its version; text that names no record keeps nothing, and so does a reference
  to a record the search does not hold.
- A build looks the referenced records up after the records the step reads, by id, 500 a search, asking only for what the
  filters read of them, and each is a record the key is read through: an incremental load reads the key again when one
  changes.
- Only a label's and an attribute's steps filter through a reference. A dimension's key path, a collected path and an
  element's path and fields are read in one pass over the records, with no record looked up, and refuse one.

## The kinds references are looked up in

A build finds the records a step reads by id, in the kind `*:*:<entityType>:*` of the entity type each id names, so a
record of any version and authority of the type is read. `lookupKinds` names, per entity type, the one kind its records are
looked up in, as a pipeline that downloads one kind reads them: `lookupKinds: [osdu:wks:master-data--Basin:1.2.0]`. Each is
`authority:source:entityType:version` with the entity type written out (another segment may be a wildcard), one per entity
type. A record of another kind of a type named is not found, as a record the search does not hold is not, and an
incremental load finds the records that changed in the same kinds.

## Collected attributes

`{ collect: <path> }` collects the values of the dimension's own records instead of reading
the record a key names: `LoggingService: { collect: data.LoggingService }` on a dimension keyed by the well logs'
`data.WellboreID` gives each wellbore every logging service its logs name, with how many logs hold each. Picking a
collected value in a search finds the records holding it, not every record of the keys holding it. A build reads the
values the cheaper way: a read per value when there are few, or one pass over the records through the search cursor,
split into ranges read side by side. A collected path is a path search matches exactly, like the dimension's own. A
dimension collects one attribute: a second is refused, since two would pair values no record holds together.

## The fields a label search returns

A build reads a label's and an attribute's records step by step. At each step it gathers the ids every label and
attribute reaches there, groups them by the entity type each id names, and asks the search's offset search
(`source.queryPath`) for each group in the kind `*:*:<entityType>:*`, with a query matching at most 500 of its ids exactly.
The search is asked to return only these fields, `id` first and the rest in ordinal order:

| Field | Why |
| --- | --- |
| `id` | Matches each record that comes back to the id it was asked by. |
| Each path read at this step from records of that entity type, its filters taken out | The value read, or the reference the next step follows. |
| For each segment that filters, the property the filter compares, under the path up to that segment | What the filter compares the objects by. |

So, for the [example](dimension.md#example)'s `Wellbore` dimension, the first step's search over `master-data--Wellbore` returns `id`,
`data.CurrentOperatorID`, `data.FacilityName` and `data.GeoContexts.GeoPoliticalEntityID`, and the `Country` attribute's
second step, `data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName`, asks the
`master-data--GeoPoliticalEntity` search for `id`, `data.GeoPoliticalEntityName` and `data.GeoPoliticalEntityTypeID`.
Nothing else of a record comes back, however much it holds. A collected attribute and elements are not read by id: they
are read from the dimension's own records, as [Collected attributes](dimension-attributes.md) and [Elements](dimension-elements.md)
say.

## Errors when the document is read

| Message (abridged) | Cause |
| --- | --- |
| `attributes.Field.many is 'every'; it is first, ... join, ... all, ... or max, ...` | A `many` the engine does not read by. |
| `attributes.Field.maxReferences is '0'; it is how many records each step but the last follows for one key, a whole number from 1 to 1000 ...` | Out of range, or not a whole number. |
| `attributes.Code keeps each value as id, the text the dimension it joins is keyed by, which a decoded value is not; ...` | `decode` with `keep: key`, `id` or `reference`. |
| `attributes.Name keeps each value as key, which is always trimmed, as the text a search compares; ...` | `trim: false` with `keep: key`, `id` or `reference`. |
| `attributes.Source collects its values, and many reads the value from the record a key names; ...` | `many`, `maxReferences`, `decode` or `trim` on a collected attribute. |
| `lookupKinds[0] 'osdu:wks:*:1.0.0' names no one entity type; ...` | A lookup kind whose entity type is a wildcard or is not one. |
| `lookupKinds[1] '...' is a second kind of master-data--Basin, beside '...'; ...` | Two kinds of one entity type. |
| `elements.path '...' filters through a reference (->), which only a label's and an attribute's steps do: ...` | A filter through a reference in an element's path or field. |
| `the filter [GeoTypeID->=Country] reads '' through GeoTypeID, which is not a path (it is empty)` | A filter through a reference without its path, or with one that is not a path. |
