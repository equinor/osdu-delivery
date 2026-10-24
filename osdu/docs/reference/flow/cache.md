---
id: delivery-flow-cache
title: "Cache flow (flowType: cache): OSDU reference data, lookup tables and dictionaries in a partition's cache"
type: flow-reference
summary: "The flowType: cache document: kind, table, dictionary and dimension types, partitions, source, onChange, retentionDays, and refreshing some types."
keywords:
  - source.paging
  - createTime paging
  - cache flow
  - flowtype cache
  - reference data
  - lookup table
  - ingestion table
  - silver table
  - "types[].table"
  - "types[].kind"
  - "types[].dictionary"
  - "types[].dimension"
  - onchange
  - retentiondays
  - cache retention
  - refresh
  - "$cache"
  - payload types
  - refresh one type
yamlPath: "(root, flowType: cache)"
related:
  - delivery-concept-search-paging
  - delivery-concept-partition-cache
  - delivery-guide-lookup-table-cache
  - delivery-guide-reference-data-cache
  - delivery-flow-dictionary
  - delivery-cli-cache
  - delivery-flow-mapping-lookups
  - delivery-flow-dimension
  - flow-ing
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/CacheDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceCaptureSpec.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheRetention.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/TableCapture.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/DimensionCapture.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionConnection.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/docs/census/keys.cache.json
---

# Cache flow (flowType: cache): OSDU reference data, lookup tables and dictionaries in a partition's cache

A cache flow declares what a partition's cache holds: the values a mapping looks up while it renders a record. Each
type it declares comes from one origin: OSDU records searched by kind (reference data such as `UnitOfMeasure`), the
rows of an ingestion table your own flows load (a lookup table such as `OsduData.silver.UnitAlias`), the entries of a
[dictionary document](dictionary.md) kept in the repository, or the values a [dimension flow](dimension.md) built. A
run of the flow (the `refresh` operation) captures every declared type and merges it into the cache of the partition it
fills, which writes a new cache version when anything moved. Every delivery flow that delivers to that partition then
reads the types by name, `$cache.UnitAlias` or `$cache: UnitOfMeasure.id`, whatever filled them.

Use a cache flow whenever a mapping needs to translate or resolve a value: a source's unit spelling into the
partition's unit code, a code into the id of the reference record that holds it, a curve mnemonic into its curve type.
The cache lives in the module's database, never in the repository: the YAML defines what is cached, and its runs fill
the cache ([The partition cache](../concepts/partition-cache.md) explains versions, sharing and change detection).

## Minimal working examples

A cache flow of OSDU reference data, filling the caches of partitions `dev` and `test`, `osdu-reference-00-cache.yaml`:

```yaml
flowType: cache
name: osdu-reference-00-cache
batch: reference
description: The OSDU reference data the well log mappings resolve units, curve types and sampling domains against.
partitions: [dev, test]

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
  - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
    fields: [data.Code, data.Name]
  - kind: "osdu:wks:reference-data--LogCurveType:*"
    fields: [data.Code, data.Name]
    onChange: approve
  - kind: "osdu:wks:reference-data--WellLogSamplingDomainType:*"
    fields: [data.Code, data.Name]

retentionDays: 30       # keep a replaced version's records a month; 7 when left out

reliability:
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }
  timeoutSeconds: 100

schedule:
  cron: "0 1 * * *"
  timezone: UTC
  values:
    partition: "*"      # every fire refreshes dev, then test
```

A cache flow of lookup tables: two ingestion tables an `ing` flow loads, and one dictionary document. It reaches no
OSDU platform, so it declares no endpoint and no credentials, only the ingestion database, `welldb-lookups-00-cache.yaml`:

```yaml
flowType: cache
name: welldb-lookups-00-cache
batch: welldb
description: The lookup tables the well database's mappings translate its values through.

source:
  connection: ${env:OSDU_DATA_DB}

types:
  - table: OsduData.silver.UnitAlias
    key: source_unit
    fields: [osdu_unit]
  - table: OsduData.silver.CurveDictionary
    key: mnemonic
    fields:
      - log_curve_type_id
      - { column: unit, as: curve_unit }
  - dictionary: sampling-domain
    name: SamplingDomain

schedule: welldb-lookups
```

```bash
sqlflow validate welldb-lookups-00-cache.yaml
sqlflow run welldb-lookups-00-cache.yaml --set partition=dev
```

`sqlflow validate` prints `OK  'welldb-lookups-00-cache' is valid (cache: ${env:OSDU_DATA_DB} -> catalog).` The
[lookup table guide](../guides/lookup-table-cache.md) builds the second flow's first table end to end, from files to a
mapping.

## Keys reference

The loader is strict: a key it does not know fails the load (`Property '<key>' not found on type ...`), so a misspelled
key never passes silently.

| Key | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | none | `cache`. |
| `name` | string | yes | none | The flow's pipeline identity, and the name the cache versions it writes and the records it holds are recorded under. Named globally: the repository sync warns when a second file of the same repository declares the name (the first file wins), and when another repository's cache flow has it (rename one, since both would capture under it). |
| `description` | string | no | none | Free text. |
| `batch` | string | no | none | SQLFlow's batch label for the pipeline and its runs. |
| `partitions` | list | no | none | The partitions whose caches the flow fills, each a data-partition-id written literally, at most 64, each once. See [Which partitions a flow fills](#which-partitions-a-flow-fills). |
| `parameters` | map | no | none | Named values a type's `query` uses as `{name}` tokens: each with `required`, `default` and `description`. |
| `source` | map | yes | none | Where the types come from: `endpoint`, `auth` and `headers` for OSDU types, `connection` for table types. Required even when empty (`source: {}` for a flow of dictionaries or dimensions only); missing, it fails with `'source' is required.` |
| `types` | list | yes | none | The types the flow caches, at least one, each from one origin. |
| `onChange` | `auto` or `approve` | no | `auto` | What a changed cached value does to the records already built from it; a type may override it. See [onChange](#onchange). |
| `retentionDays` | integer, 1 to 36500 | no | `7` | How many days the partition's cache keeps the records of a version after a newer one replaced it; every refresh and import prunes the rest. See [retentionDays](#retentiondays). |
| `reliability` | map | no | defaults | The HTTP settings of the OSDU searches, written as on a [delivery flow](delivery.md): timeout, retry, rate limit, TLS, URL allowlist and response size. A cache flow has no `parallelism` block, and refuses a parallelism setting written under `reliability` (`reliability.concurrency is not a setting here any more: a delivery flow writes it as parallelism.recordsAtOnce, and a cache flow has no use for it. Remove it.`). |
| `schedule`, `mode`, `lifecycle` | | no | none | SQLFlow's envelope, as on every flow ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md)); a fire runs a refresh. |

`makeCurrent` is no longer a setting: a flow that still declares it fails with `makeCurrent is not a setting any more:
every version a refresh writes becomes the current version of its partition's cache. Remove it.` A delivery flow that
must stay on an earlier version pins it with `render.cacheVersion`.

### source

| Key | Meaning |
| --- | --- |
| `source.endpoint` | The OSDU platform the `kind` types are searched on, usually `${env:OSDU_URL}`. Required when the flow declares a `kind` type (`source.endpoint is required: the flow declares a type searched on OSDU, by its kind.`). Refused, with `source.auth`, when every type is a lookup table (`source.endpoint and source.auth reach the OSDU platform, and the flow declares no type searched there; every type it declares is a lookup table. Remove them.`). |
| `source.auth` | How the searches authenticate, with the same auth types and keys as a delivery flow's `target.auth`; every secret is a `${env:...}` or `${keyvault:...}` reference, and a literal is refused when the flow is read. |
| `source.headers` | Headers every search carries. `data-partition-id` here hard-codes the one partition the flow fills; it is refused beside `partitions`. A header that carries a credential holds a reference; a literal is refused when the flow is read. |
| `source.paging` | How an OSDU type's search is paged: `cursor` (the default) through the search cursor, or `createTime` through the plain search in the order each record was created, which holds nothing on the service between pages and picks a failed read up where it stopped. See [Search paging](../concepts/search-paging.md). |
| `source.connection` | The ingestion database the `table` types are read from: a whole `${env:NAME}` or `${keyvault:vault/secret}` reference, or a SQL Server connection string whose password is a reference (or none, with Azure AD). Required when the flow declares a `table` type, refused when it declares none. A literal password fails: `source.connection carries a literal password. A flow document holds references only: ...`. It is resolved on the node that runs the flow. |

### types[]

Each type names exactly one origin: `kind`, `table`, `dictionary`, or `dimension` with `dimensionFlow`. Two in one entry
fail with `types[<n>] names more than one origin; a type has one: ...`.

| Key | Applies to | Meaning |
| --- | --- | --- |
| `kind` | OSDU | The kind searched, `authority:source:entityType:version`, with `*` allowed per segment (`osdu:wks:reference-data--UnitOfMeasure:*`). |
| `entityType` | OSDU | The entity type the records hold. Derived from the kind; needed only when the kind names none. Refused on a lookup table. |
| `name` | all | The name a mapping reads the type by. Defaults: the part of the entity type after `--` for an OSDU type (`UnitOfMeasure`), the table's own name for a table (`UnitAlias`), the dictionary's or the dimension's name. Unique within each partition the flow fills. |
| `query` | OSDU | A Lucene query narrowing the records kept, `*` when left out; `{name}` tokens are the flow's parameters. Refused on a lookup table, which holds every row. |
| `fields` | OSDU, table, dimension | OSDU: the paths of each record to keep, bare (`data.Code`, kept as `Code`) or `{ path: ..., as: ... }`; required. Table: the columns kept beside the key, bare or `{ column: ..., as: ... }`; required. Dimension: the attributes of the dimension each row carries. A dictionary takes none. |
| `table` | table | The ingestion table, as a three-part name `[database].[schema].[table]` (`OsduData.silver.UnitAlias`). |
| `key` | table | The column each row is keyed by, which a mapping matches on. Required for a table, refused on any other origin. |
| `dictionary` | dictionary | The name of a dictionary document, `dictionaries/<name>.yaml` in the nearest `dictionaries/` folder above the flow. |
| `dimension`, `dimensionFlow` | dimension | The dimension, and the dimension flow that declares it. Both are required. |
| `onChange` | all | Overrides the flow's `onChange` for this type. |
| `partitions` | all | The partitions this type is built for, when not all of the flow's. Needs `partitions` at the top of the document, or a flow that serves every registered partition. |

## The four origins

### OSDU records (`kind`)

A refresh searches the kind through the search service's cursor (`POST /api/search/v2/query_with_cursor`) and keeps,
for every record, its id and the declared paths. A path crosses arrays, so `data.NameAliases.AliasName` keeps the set of
aliases a record holds; whatever a path yields (a scalar, a set, an object) is kept as it is. A path is stored without
its `data.` root, so a mapping reads `data.Code` as `Code`. Two paths kept under one name, or a path kept as `id`, are
refused. A type is read whole or the refresh fails: a sweep that fails part way or comes back short is read once more
from the start, and when that fails too nothing is written and the cache keeps the version it had.

The capture warns rather than fails when a search matches no record (`the search matched no record of kind ... Check
the kind and the query.`) or when no record carries a path (`no item carried '<path>' ... Check the path against the
kind`). Keep closed vocabularies here; business data that a mapping finds one record of per row (the wellbore a log
belongs to) can be cached too, or searched for as each record needs it ([lookups](mapping-lookups.md)).

Which pipelines bring a type up to date before or after they run is the pipelines' to say, not the cache flow's: a
delivery flow names the types in its `cache` block ([cache](delivery.md#cache)).

### Ingestion tables (`table`)

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

### Dictionary documents (`dictionary`)

A dictionary type holds the entries of a [dictionary document](dictionary.md): a small lookup table reviewed in the
repository. It names only the dictionary (and optionally `name`); the document names its key and fields, so `kind`,
`entityType`, `query`, `fields` and `key` are refused. The file is found in the nearest `dictionaries/` folder walking up
from the flow's folder, so a flow holding a dictionary needs the repository's tree when it runs, and reads the file at
the run's commit. `sqlflow validate` on the flow finds and reads the file the same way, and refuses a flow whose
dictionary is missing or does not load
(`<file>: type 'SamplingDomain': Dictionary 'sampling-domain' was not found under '../dictionaries'. Expected sampling-domain.yaml or sampling-domain.yml.`).
The repository sync reads it too, and leaves a type whose dictionary is missing or invalid out of the cache with a
warning naming the file.

### Dimension values (`dimension`, `dimensionFlow`)

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

## Which partitions a flow fills

A flow fills one cache per partition ([partitions](../concepts/partitions.md)), in one of three ways:

| The document | Partitions it fills | A run fills |
| --- | --- | --- |
| Names `partitions: [dev, test]` | Those it names. | The one the run names; every one in turn for `*`; with none named, the registry's default when the flow names it, else the flow's only partition. |
| Names neither `partitions` nor a header | Every partition registered with the catalog. | The one named (it has to be registered); every registered one for `*`; with none named, the registry's default. |
| Sets `source.headers.data-partition-id` | That one partition. | That partition; a run that names a partition fails. |

A run names the partition under the run value `partition`: the partition picked in the GUI's title bar, `--set
partition=<name>` on `sqlflow run` and `sqlflow trigger`, or a schedule's `values`. So a parameter called `partition`
is refused on a flow that names its partitions or serves the registry. A run over several partitions refreshes each on
its own: one that fails leaves the others refreshed, and the run ends failed naming it. `types[].partitions` narrows
one type to some of the flow's partitions; every partition the flow names has to be given at least one type.

## onChange

A refresh compares each type with the version it replaces and tags every changed value that delivered records were built
from. Under `auto` (the default) a tag is approved as it is written and the next run of each delivery flow carries the
new document. Under `approve` the affected records are held back until someone approves or rejects the change on the
Cache page. Set it for the flow and override it per type. When several cache flows of a partition declare one type,
its changes wait for approval when any of them says `approve`. [The partition cache](../concepts/partition-cache.md)
describes the tags and the rollout.

## retentionDays

Every refresh and every import of a partition's cache ends by pruning the records of the versions the partition no
longer needs, so the module's database holds the current cache and a bounded history rather than every version ever
captured. `retentionDays` says how long that history is: a version keeps its records for that many days after a newer
version replaced it.

```yaml
retentionDays: 30
```

- **Left out, it is 7.** A whole number from 1 to 36500; `retentionDays: 0` fails with `retentionDays is 0, and it is the
  number of days the partition's cache keeps the records of a version after a newer one replaced it: a whole number from
  1 to 36500. Leave it out to keep them 7 days.`, and `retentionDays: 7d` fails as invalid YAML.
- **Some versions are always kept:** the current version, the one it replaced, and every version a delivery flow pins
  with `render.cacheVersion`.
- **The longest wins.** When several cache flows fill one partition, the partition keeps the longest retention any of
  them declares, so no project's history is pruned sooner than it asked for.
- **Nothing a delivered record needs is pruned.** A record keeps the cached values its render read in the ledger, and a
  pruned version stays listed with who captured it, when and what it changed. Only its records go: it can no longer be
  browsed, compared, or rendered against.

[The partition cache](../concepts/partition-cache.md#retention) describes what is kept and when it goes.

## Operations and the run payload

| Operation | What it does |
| --- | --- |
| `refresh` | The default: a run with no operation and every scheduled fire refreshes. Captures every declared type and merges it into the partition's cache, writing a version when the cached content moved, then prunes what the partition's [retention](#retentiondays) no longer keeps and tags what the changes reach. |
| `plan` | Counts what each type would hold (the records an OSDU search matches, the rows of a table, the entries of a dictionary, the values of a dimension) and writes nothing. |

Any other operation is refused, for example `operation must be one of refresh, plan for 'cache' flows; 'deliver' is
not.`

A run's payload may name `types`, the types it refreshes or plans, at most 500 names, each once: `{ "types":
["UnitOfMeasure"] }` captures that type alone and leaves every other type of the partition's cache as it is. Names are
matched without regard to case. A name the flow does not declare for the partition is refused before anything is read:
`payload types names 'Well', which cache flow 'osdu-reference-00-cache' does not declare for partition 'dev'; it declares
UnitOfMeasure, LogCurveType, WellLogSamplingDomainType.` In the GUI, the run dialog of a cache flow lists its types to
pick from; none picked refreshes every one. The payload takes nothing else beside the configuration the control plane
supplies (`payload force does not apply to a cache flow: only delivery and retrieval runs force; a cache flow's payload names only types.`),
and a cache run takes none of SQLFlow's per-run overrides: `--full`, a backfill window and `--file-pattern` fail with `... do(es) not
apply to 'cache' flows; a refresh sweeps every declared type in full.`, with or without a payload.

A refresh writes into the module's database, so it needs it: on a node or the control plane it is always there; on a
workstation, `sqlflow` reads it in the catalog's database (`--db`, by default `${env:SQLFLOW_CATALOG_DB}`). The
run's result lists every type with its origin, record count, `change` (`added`, `changed` or `unchanged`) and content
hash, and `retention`: the days applied, how many versions keep their records, the versions pruned and the stored rows
removed. Its log names the types that moved and what the retention pruned. A refresh that finds exactly what the
current version holds writes no version, so refreshing as often as you like costs no redelivery; it still applies the
retention.

## Lineage

A cache flow is a node of SQLFlow's lineage like any flow ([lineage](../concepts/lineage.md)): it reads each table type's
ingestion table, each dictionary's file, each dimension's build and each OSDU kind, and writes each type into its
partition's cache. So the `ing` flow that loads `OsduData.silver.UnitAlias` runs before the cache flow that captures it,
a dimension flow before the cache flow holding its dimension, and the cache flow before every delivery flow whose
mapping reads its types. Flows sharing one schedule run in that order.

A pipeline whose `cache` block names a type writes that type too, since its cache steps bring it up to date
([cache](delivery.md#cache)): a flow reading the type runs after that pipeline, in the same schedule, whether or not the
cache flow capturing it runs in between.

A cache type is a lineage node of its partition, so a cache flow and a delivery flow are ordered when they share a
partition, named under `partitions` or as a literal `data-partition-id` header (a cache flow naming `partitions: [dev]`
and a delivery flow whose header is `dev` are ordered), or when both serve every registered partition. A cache flow that
serves the registry and a delivery flow that hard-codes `data-partition-id: dev` are not ordered against each other;
`sqlflow lineage <folder>` shows the waves.

## What goes wrong

| Message | Cause |
| --- | --- |
| `types is required: list the types the cache holds, ...` | No `types`, or an empty list. |
| `types[<n>] needs a kind (...), a dictionary (...) or a table (...).` | A type with no origin. |
| `types[<n>].kind '<kind>' is not authority:source:entityType:version (wildcards allowed per segment).` | A malformed kind. |
| `types[<n>].key is required: the column of <table> each row is keyed by and a mapping finds the row by.` | A table type without `key`. |
| `types[<n>] names a key, which only a table type takes: an OSDU record is kept under its id.` | `key` on a kind type. |
| `types[<n>].fields lists no column of <table> to keep beside the key; list the columns a mapping reads.` | A table type without `fields`. |
| `types[<n>].table '<name>' must be a three-part name [database].[schema].[table]: ...` | A table named in one or two parts. |
| `types[<n>]: Cached type '<name>' lists its key '<key>' among its fields; the key is kept under its own name already.` | The key repeated in `fields`. |
| `types[<n>]: Cached type '<name>' is keyed by '<key>'; a lookup row's key is its id, so ...` | A key called `id`. |
| `types[<n>] holds dictionary <name>, which takes no '<setting>': ...` | `fields`, `key`, `kind`, `entityType` or `query` on a dictionary type. |
| `types declares ... more than once ...; a mapping reads a type by its name, ...` | Two types with one name for the same partition. |
| `types[<n>].query uses '{<token>}', which is not declared under parameters.` | A query token without a parameter. |
| `source.connection is required: the flow declares a type read from an ingestion table.` | A table type without `source.connection`. |
| `source.headers names 'data-partition-id', and the flow names its partitions: ...` | A header beside `partitions`. |
| `source.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: ...` | A literal secret. |
| `type '<name>': Dictionary '<dictionary>' was not found under '<folder>'. Expected <dictionary>.yaml or <dictionary>.yml.` | A dictionary type whose file `sqlflow validate` does not find. |
| `retentionDays is <n>, and it is the number of days ... a whole number from 1 to 36500. Leave it out to keep them 7 days.` | A retention below one day or above a hundred years. |

A refresh can also fail when another cache flow of the same partition declares the same type differently (another entity
type, a field from another path, or a second declaration of a lookup table): `Cache flow '<flow>' disagrees with another
cache flow of partition '<partition>' about what the cache holds, so nothing was captured or imported: ...`. Make the
declarations agree, or rename one type.

## Related

- [The partition cache](../concepts/partition-cache.md): versions, several flows sharing a cache, tags and rollout.
- [Getting your own lookup table into the cache](../guides/lookup-table-cache.md) and [caching OSDU reference
  data](../guides/reference-data-cache.md): the two common tasks, end to end.
- [Dictionary document](dictionary.md), [dimension flow](dimension.md).
- [Mapping lookups](mapping-lookups.md) and [modifiers](mapping-modifiers.md): how a mapping reads a cached type.
- [`sqlflow cache`](../cli/cache.md): list a partition's versions; SQLFlow's [ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md) loads the tables a table type reads.
