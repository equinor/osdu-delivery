---
id: delivery-flow-cache-origins
title: "Cache type origins: OSDU kinds, ingestion tables, dictionaries and dimensions, how each is captured"
type: flow-reference
summary: "What each origin of a cache type captures and keeps: OSDU records by kind, an ingestion table's rows, a dictionary's entries, a dimension's values."
keywords:
  - cache type origin
  - kind type
  - table type
  - dictionary type
  - dimension type
  - DeletedDate_DW
  - maxRows
  - "types[].maxRows"
  - 1000000 rows
  - more rows than a million
  - lookup table too large
  - holds more than rows for type
  - key column
  - no item carried
  - search matched no record
  - "decode: json"
  - json column
  - json object in a lookup table
  - tags from a lookup table
  - holds no JSON object
yamlPath: "types[]"
related:
  - delivery-flow-cache
  - delivery-flow-cache-records
  - delivery-flow-cache-attributes
  - delivery-flow-dictionary
  - delivery-flow-dimension
  - delivery-guide-lookup-table-cache
  - delivery-guide-reference-data-cache
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceCaptureSpec.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/TableCapture.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/JsonColumnValue.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/DimensionCapture.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryCatalog.cs
---

# Cache type origins: OSDU kinds, ingestion tables, dictionaries and dimensions, how each is captured

Each type of a [cache flow](cache.md) names exactly one origin: `kind`, `table`, `dictionary`, or `dimension` with
`dimensionFlow`. Two in one entry fail with `types[<n>] names more than one origin; a type has one: ...`. A refresh
captures every type from its origin and merges them all into one version of the partition's cache.

## OSDU records (`kind`)

A refresh searches the kind through the search service's cursor (`POST /api/search/v2/query_with_cursor`), or with
`source.paging: createTime` through `POST /api/search/v2/query` in the order the records were created, and keeps,
for every record, its id and the declared paths. A path crosses arrays, so `data.NameAliases.AliasName` keeps the set of
aliases a record holds; whatever a path yields (a scalar, a set, an object) is kept as it is. A path is stored without
its `data.` root, so a mapping reads `data.Code` as `Code`. Two paths kept under one name, or a path kept as `id`, are
refused. A type is read whole or the refresh fails: a sweep that fails part way or comes back short is read once more
(through the cursor from the start, or in `createTime` order from where it stopped, reading again only what came back
short), and when that fails too nothing is written and the cache keeps the version it had.

The search index holds a projection of each record: only what the kind's schema describes, so a property of
`data.ExtensionProperties`, or any other part the schema leaves open, is not in it. A type with `fetchRecords: true`
reads each record the search finds whole from the storage service instead, and keeps any path the record holds
([records read whole](cache-records.md)). A type's `attributes` attach facts of the records a cached record references
(its well's name, its field's id), read as a dimension reads its attributes ([cache attributes](cache-attributes.md)).

The capture warns rather than fails when a search matches no record (`the search matched no record of kind ... Check
the kind and the query.`) or when no record carries a path (`no item carried '<path>' ... Check the path against the
kind`, which names `fetchRecords` for a type read from the index). Keep closed vocabularies here; business data that a mapping finds one record of per row (the wellbore a log
belongs to) can be cached too, or searched for as each record needs it ([lookups](mapping-lookups.md)).

Which pipelines bring a type up to date before or after they run is the pipelines' to say, not the cache flow's: a
delivery flow names the types in its `cache` block ([cache](delivery.md#cache)).

## Ingestion tables (`table`)

A table type holds the rows of a table SQLFlow's own flows load, usually the keyed silver table an `ing` flow writes.
It is kept as the lookup table `lookup--<name>`, keyed by `key`, with the `fields` columns beside the key. A refresh
reads the table over `source.connection`:

- **Every row the ingestion flow has not marked deleted.** When the table has `DeletedDate_DW`, rows where it is set are
  left out, so a row the ing flow's deleted-row detection tags leaves the cache at the next refresh.
- **Each key trimmed, unique, at most 256 characters,** not empty and without control characters. A key that breaks a
  rule, or two rows whose keys are the same once trimmed, refuses the whole capture, naming the first five of each: `Cache flow
  '<flow>': table <table> has rows type <name> could not be keyed by <key>, so nothing was captured: ...`. Keys compare
  exactly, so `GAPI` and `gapi` are two rows.
- **At most 1,000,000 rows, or the number the type's `maxRows` names** ([more rows than a million](#more-rows-than-a-million)).
  A larger table refuses the whole capture, and the cache keeps the version it had.
- **Every value as text,** as the delivery reader gives it, so a value read from the cache and the same value read from a
  dataset are the same text. A NULL column is not kept, and reads as no value. A column read with `decode: json` is kept
  as the JSON object its text holds instead ([A column holding a JSON object](#a-column-holding-a-json-object)).
- **The key column must be comparable:** a text or number column of bounded length. A missing column fails naming the
  columns the table has; a missing table fails with `the table <table>, which type <name> reads, was not found on the
  source database, or the identity this node connects with cannot see it. Check the ingestion flow that loads it has run,
  and that the node's login is granted SELECT on it.`

Neither the key nor a field may be called `id` in any casing, and the key is not listed among the fields. A table type
takes no `entityType` or `query`. Only one cache flow of a partition may declare a given lookup table.

### More rows than a million

A table type captures at most 1,000,000 rows. A table that really holds more says how many it may hold with `maxRows`,
a whole number from 1 to 5,000,000:

```yaml
types:
  - table: OsduData.silver.CurveAlias
    key: source_mnemonic
    fields: [curve_type, unit]
    maxRows: 2500000
```

A table holding more rows than its type takes refuses the capture, naming the number and how far it may be raised:

```text
Cache flow 'welldb-lookups-00-cache': table OsduData.silver.CurveAlias holds more than 1000000 rows for type CurveAlias, the 1000000 a table type holds when its maxRows says nothing: every row is held in memory with the cache version a render reads. Raise the type's maxRows (at most 5000000) if the table really holds that many, or join a table that large into the ingestion tables a delivery reads instead. Nothing was captured.
```

A plan (`operation: plan`) counts every row and logs the same reason as a warning when a refresh would refuse the table.
A pipeline's `cache.before` step reads the table with the same `maxRows`, as the repository sync records it.

The number is about memory: a render reads the whole cache version it renders against, and the control plane and every
node keep the versions they read last in memory ([versions](../concepts/partition-cache.md#versions)), each holding every
row of every lookup table. Raise `maxRows` for a table that really is that large; join a table larger still, or one a
mapping reads only a few rows of per record, into the ingestion tables the delivery flow reads. A dictionary holds at most
100,000 entries, and a dimension type 100,000 values; neither takes `maxRows`.

### A column holding a JSON object

A column whose text is a JSON object, such as the tags of each well kept in an `nvarchar(max)` column, is kept as that
object when the type reads it with `decode: json`. A mapping then writes the object whole where the template takes an
object, or reads a path into it, as it would an object an OSDU type caches:

```yaml
types:
  - table: OsduData.silver.WellTags
    key: well_id
    fields:
      - well_name
      - { column: tags, as: Tags, decode: json }
```

```yaml
record:
  tags:
    $cache: WellTags.Tags
    $findBy: well_id = well_id
```

A row whose `tags` holds `{ "lra.dataID": "LRA-17", "Source": "welldb" }` writes exactly those two properties into the
record's `tags`. Without `decode`, the column is kept as text, and the mapping is held with `a single value cannot be
written where the template takes an object`.

- **Opt in, per column.** Only the columns that name `decode: json` are decoded; every other column stays text. Only a
  table type's column takes it: an OSDU type's path yields its JSON as it is, and `decode` there is refused.
- **Names and types as the column holds them.** Every property name is kept exactly, a name holding a dot (`lra.dataID`)
  included (a mapping can also write such a tag as a key of its own, [property names holding a dot](mapping-property-names.md)), and every string, number, boolean, array and object keeps its JSON type. The object is kept as the cache
  keeps every object: its properties in ordinal order of their names, a property holding `null` left out, and each number
  in its shortest form (`1.50` is `1.5`). So a row whose text changes only in spacing or property order is the same value,
  and writes no new version.
- **A NULL column reads as no value,** as any column does.
- **A value that holds no JSON object refuses the capture.** Text that is not valid JSON, empty text, JSON of anything
  but an object (an array, a string, a number, `true`, `null`), an object naming one property twice, or a number beyond
  the range JSON numbers are kept in fails the whole type, naming the first five values by row key and column, and nothing
  of the table is captured: `Cache flow '<flow>': table <table> has 2 values type WellTags decodes as JSON that hold no JSON
  object, so nothing was captured: row 'W-2', column 'tags' (kept as Tags): the value is a JSON array, not an object;
  row 'W-7', column 'tags' (kept as Tags): the value is not valid JSON (...). A column read with decode: json holds the
  text of one JSON object in each row, or NULL where a row has none.` The reason says where the text breaks, as the JSON
  reader reports it, rather than repeating the text.
- **A text column only.** A `decode: json` column that is not `char`, `varchar`, `nchar`, `nvarchar`, `text`, `ntext` or
  `json` is refused before a row is read, by a refresh and a plan alike: `type WellTags decodes column 'revision' (int) of
  table ... as JSON, and only a text column holds the text of a JSON object: ...`.
- **Any other word** fails the flow when it is read: `'types[0].fields[1].decode' value 'xml' is not one of none, json.`

A pipeline whose `cache` block brings the type up to date reads the column the same way ([cache](delivery.md#cache)),
since the catalog keeps each column's `decode` with the cache flow's declaration.

## Dictionary documents (`dictionary`)

A dictionary type holds the entries of a [dictionary document](dictionary.md): a small lookup table reviewed in the
repository. It names only the dictionary (and optionally `name`); the document names its key and fields, so `kind`,
`entityType`, `query`, `fields` and `key` are refused. The file is found in the nearest `dictionaries/` folder walking up
from the flow's folder, and read at the run's commit: every cache flow runs from a checkout of its repository, since a
refresh also reads the [repository document](repository.md) above it. `sqlflow validate` on the flow finds and reads
the file the same way, and refuses a flow whose dictionary is missing or does not load
(`<file>: type 'SamplingDomain': Dictionary 'sampling-domain' was not found under '../dictionaries'. Expected sampling-domain.yaml or sampling-domain.yml.`).
The repository sync reads it too, and leaves a type whose dictionary is missing or invalid out of the cache with a
warning naming the file.

## Dimension values (`dimension`, `dimensionFlow`)

A dimension type holds a dimension's values as its last completed build in the partition left them: one row per value,
keyed by `value`, with `keys` (every raw key the value stands for), `records` and `filter` beside it, plus each attribute
`fields` names. It takes no `entityType`, `query` or `key`.

```yaml
flowType: cache
name: welldb-dimensions-00-cache
batch: welldb
partitions: [dev]

source: {}

types:
  - dimension: Field
    dimensionFlow: welldb-welllog-05-dimensions
    fields: [Country]
```

A refresh before the dimension's first completed build fails, naming the build to run; so does a field the dimension
does not read. A dimension over 100,000 values or 500,000 keys refuses the capture. A mapping turns a raw key into its
value with `replace: $cache.Field`, `match: keys` and `field: value` ([modifiers](mapping-modifiers.md)).
