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
  - 100000 rows
  - key column
  - no item carried
  - search matched no record
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
- **At most 100,000 rows.** A larger table fails (`... holds more than 100000 rows for type <name>, and a lookup table
  holds at most that many: every row is loaded with the cache version a render reads. ...`): join a table that large into
  the ingestion tables the delivery flow reads instead.
- **Every value as text,** as the delivery reader gives it, so a value read from the cache and the same value read from a
  dataset are the same text. A NULL column is not kept, and reads as no value.
- **The key column must be comparable:** a text or number column of bounded length. A missing column fails naming the
  columns the table has; a missing table fails with `the table <table>, which type <name> reads, was not found on the
  source database, or the identity this node connects with cannot see it. Check the ingestion flow that loads it has run,
  and that the node's login is granted SELECT on it.`

Neither the key nor a field may be called `id` in any casing, and the key is not listed among the fields. A table type
takes no `entityType` or `query`. Only one cache flow of a partition may declare a given lookup table.

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
