---
id: delivery-guide-lookup-table-cache
title: "Getting your own lookup table into the cache for a mapping: JSON or CSV files, a staging table, a silver table, then a cache flow"
type: guide
summary: "Put a lookup table you load yourself (JSON or CSV files into staging and a silver table) into the cache with a cache flow table type, and read it in a mapping."
keywords:
  - lookup table
  - silver table
  - staging table
  - staging to silver
  - ingestion table
  - cache
  - mapping
  - json
  - csv
  - "types[].table"
  - "replace: $cache"
  - translate source values
  - unit mapping
  - cache import
related:
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-flow-dictionary
  - delivery-flow-mapping-modifiers
  - delivery-guide-reference-data-cache
  - flow-ing
  - guide-table-to-table-ingestion
  - concept-pre-ingestion-transform
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheLineage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/TableCapture.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ReplaceTables.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - sqlflow/src/SqlFlow.Core/Engine/FlowRunner.cs
---

# Getting your own lookup table into the cache for a mapping: JSON or CSV files, a staging table, a silver table, then a cache flow

> "I read JSON files into staging and then into a silver table. The next step is to read this into the cache so a
> mapping can use it."

**Declare the silver table as a `table` type of a [cache flow](../flow/cache.md), run the cache flow after the `ing`
flow that loads the table, and translate through it in the mapping with `replace: $cache.<Name>`.** That is all. Lineage
orders the cache flow after the ingestion flow, the cache flow's refresh captures the table into the cache of the
partition it refreshes, and every mapping delivering to that partition reads it like any cached type.

Do not use `sqlflow cache import` for this. That verb merges OSDU reference records from JSON type files, for work
without an OSDU platform, and it refuses a lookup table outright (`... a lookup table is captured from its origin, never
imported`). A lookup table always reaches the cache by a refresh of the cache flow that declares it.

This guide builds the whole chain for one table: `OsduData.silver.UnitAlias`, which says how the well database spells a
unit (`source_unit`) and what the partition's UnitOfMeasure code for it is (`osdu_unit`).

## The chain

| Step | Document | Reads | Writes |
| --- | --- | --- | --- |
| 1. Land the files | `welldb-unitalias-01-pre.yaml` (a SQLFlow file flow) | `data/unit-alias/*.json` | `OsduData.pre.UnitAlias` and the typed view `pre.v_UnitAlias` |
| 2. Key them | `welldb-unitalias-02-ing.yaml` (`flowType: ing`) | `OsduData.pre.v_UnitAlias` | `OsduData.silver.UnitAlias`, one row per `source_unit` |
| 3. Cache them | `welldb-lookups-00-cache.yaml` (`flowType: cache`) | `OsduData.silver.UnitAlias` | the type `UnitAlias` in each partition's cache |
| 4. Use them | `mappings/WellLog@1.0.0.yaml` | `$cache.UnitAlias` | the delivered WellLog records |

Steps 1 and 2 are plain SQLFlow; if your silver table already exists, start at step 3. In the repository:

```text
lookups/
  data/unit-alias/unit_alias_20261001.json
  welldb-unitalias-01-pre.yaml
  welldb-unitalias-02-ing.yaml
  welldb-lookups-00-cache.yaml
mappings/
  WellLog@1.0.0.yaml
schedules.yaml
```

## 1. Land the files

A pre flow lands the files into a staging table in the `pre` schema and refreshes the typed view `pre.v_UnitAlias` over
it. The files are JSON arrays of rows:

```json
[
  { "source_unit": "GAPI", "osdu_unit": "gAPI" },
  { "source_unit": "OHMM", "osdu_unit": "ohm.m" },
  { "source_unit": "G/C3", "osdu_unit": "g/cm3" },
  { "source_unit": "M", "osdu_unit": "m" }
]
```

```yaml
name: welldb-unitalias-01-pre
batch: welldb

source:
  type: json
  location: data/unit-alias
  options:
    srcFile: "*.json"

target:
  connection: ${env:OSDU_DATA_DB}
  schema: pre
  table: UnitAlias

transform:
  columns:
    - { name: source_unit, type: nvarchar(50) }
    - { name: osdu_unit, type: nvarchar(50) }

incremental:
  dateColumn: FileDate_DW

schedule: welldb-lookups
```

Every key here is SQLFlow's: the [JSON source](../../../../sqlflow/docs/reference/flow/source-types/json.md) (nested
JSON is shaped with `rootPath`, `explodePaths` and the other options, see [exploring JSON and
XML](../../../../sqlflow/docs/reference/guides/explore-json-xml.md)), the [typed view the transform
generates](../../../../sqlflow/docs/reference/concepts/pre-ingestion-transform.md), and the
[incremental](../../../../sqlflow/docs/reference/flow/incremental.md) file-date watermark that lands only new files. For
CSV files, use `source.type: csv` with its own options ([CSV source](../../../../sqlflow/docs/reference/flow/source-types/csv.md)).
[Turning landed files into tables](../../../../sqlflow/docs/wiki/patterns/file-ingestion-shaping.md) covers the
choices.

## 2. Key them into the silver table

An `ing` flow reads the view and upserts the silver table, keyed by the column the lookup is keyed by:

```yaml
flowType: ing
name: welldb-unitalias-02-ing
batch: welldb

connections:
  osduData: ${env:OSDU_DATA_DB}

source:
  server: osduData
  object: OsduData.pre.v_UnitAlias

target:
  server: osduData
  object: OsduData.silver.UnitAlias

load:
  keyColumns: [source_unit]

incremental:
  columns: [FileDate_DW]

schedule: welldb-lookups
```

The keys are SQLFlow's [ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md) keys
([table-to-table ingestion](../../../../sqlflow/docs/reference/guides/table-to-table-ingestion.md) walks through them).
Two choices matter for the cache:

- **Key the table by the lookup key.** `keyColumns: [source_unit]` keeps one row per spelling, and a newer file
  replaces a spelling's row. The cache refuses a table where two rows share a key.
- **Mark removed rows deleted, if rows can disappear.** The cache leaves out every row whose `DeletedDate_DW` is set,
  which the ing flow's deleted-row detection (`load.matchKeysInSourceAndTarget` with `matchKeys.action: tag`,
  [ing-load](../../../../sqlflow/docs/reference/flow/ing-load.md)) stamps on rows whose keys left the source.

## 3. Capture the table into the cache

The cache flow declares the table as a `table` type. It reaches no OSDU platform, so it has no endpoint and no
credentials, only the connection the table is read over. The estate's `welldb-lookups-00-cache.yaml` holds the lookup table
beside the curve dictionary and a dictionary document:

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

| Line | What it does |
| --- | --- |
| `source.connection` | The ingestion database, as a `${env:...}` or `${keyvault:...}` reference, resolved on the node that runs the refresh. A literal password is refused. |
| `table` | The silver table, as a three-part name. |
| `key` | The column a row is found by. The mapping matches incoming values against it. |
| `fields` | The columns kept beside the key: here the one value a replace gives. Write `{ column: osdu_unit, as: unit }` to keep a column under another name. |
| `name` (not written) | The name a mapping reads the type by; it defaults to the table's name, `UnitAlias`. |

The flow names no `partitions`, so it fills the cache of every partition registered with the catalog, and the same
document serves `dev` and `test`. Name `partitions: [dev, test]` instead to fix the list in the document
([which partitions a flow fills](../flow/cache.md#which-partitions-a-flow-fills)).

What a refresh keeps of the table:

| Rule | Detail |
| --- | --- |
| Rows | Every row the ing flow has not marked deleted (`DeletedDate_DW` empty, when the table has the column). |
| Keys | Trimmed; not empty; at most 256 characters; no control characters; unique once trimmed. Keys compare exactly, so `GAPI` and `gapi` are two rows. One bad key refuses the whole capture, naming the first five bad keys and the first five duplicates. |
| Size | At most 100,000 rows; a larger table refuses the capture. |
| Values | Text, as the delivery reader gives it; a NULL is not kept and reads as no value. |
| Names | Neither the key nor a field may be called `id`; the key is not listed among the fields. |
| Owner | One cache flow of a partition declares a given lookup table. |

## 4. Translate through it in the mapping

The well log mapping, `mappings/WellLog@1.0.0.yaml`, reads the table by its name. An excerpt of it, its curves (the
whole mapping is on [one source, several kinds](multi-kind-source.md#3-point-the-well-log-at-its-wellbore)):

```yaml
record:
  data:
    # ... the log's own properties
    Curves:
      $forEach: curves
      $item:
        CurveID: { $from: curve_id }
        Mnemonic: { $from: curve_mnemonic }
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias
            - ref
```

- **`replace: $cache.UnitAlias`** matches the trimmed incoming value against the table's key, `source_unit`: an exact
  key wins, and case is ignored only when that finds one row. It replaces the value with the one field the table holds
  beside the key, `osdu_unit`; a table with several fields names the one it wants (`field: osdu_unit`, written under
  the `replace` line), and `match:` names another column to match on. A value the table does not list passes on
  unchanged; `otherwise: ~` makes it no value, `otherwise: <text>` a fixed text.
- **`ref`** turns the translated code into the reference `dev:reference-data--UnitOfMeasure:gAPI:`. When the partition's
  cache also holds UnitOfMeasure records (captured by a reference data cache flow, [the reference data
  guide](reference-data-cache.md)), the id has to name one of them, or the record is held with the reason.

On a `$cache` node, the same replace translates the value before `$findBy` compares it. A mapping that also writes each
curve's depth unit finds the partition's UnitOfMeasure record whose `Code` is the translated spelling, and writes its id:

```yaml
DepthUnit:
  $cache: UnitOfMeasure.id
  $findBy: Code = depth_unit
  $modifiers:
    - replace: $cache.UnitAlias
  $required: false
```

The table can also be read like any other cached type: `$cache: UnitAlias.osdu_unit` with `$findBy: source_unit =
curve_unit` reads one row's field, and `{$cache.UnitAlias.osdu_unit}` inside an `id` template reads the row keyed by the
value. Never `$cache: UnitAlias.id`: a lookup row is not an OSDU record, and the preflight refuses a mapping that writes
its id. [Modifiers](../flow/mapping-modifiers.md) and [lookups](../flow/mapping-lookups.md) describe every form.

The render records every row it read, and every key it looked up and did not find, as part of the record's cache
dependencies. That is what lets a later change of the table redeliver exactly the records it reaches.

## 5. Run the chain

Lineage orders the three flows: the pre flow writes the staging table the ing flow reads, the ing flow writes the silver
table the cache flow reads, and the cache flow writes the type `UnitAlias` that the delivery flows reading the mapping
consume. `sqlflow lineage <folder>` prints the waves:

```text
wave 1: welldb-unitalias-01-pre
wave 2: welldb-unitalias-02-ing
wave 3: welldb-lookups-00-cache
```

The three flows share the schedule `welldb-lookups` (a `schedules.yaml` anywhere in the repository defines it), so a
fire runs them as one group, a wave at a time ([schedules](../../../../sqlflow/docs/reference/flow/schedule.md)).
Schedule it ahead of the deliveries that read the table. A scheduled refresh names no partition unless the schedule's
`values` name one, so it fills the registry's default partition; refresh another by naming it.

To run the chain by hand:

```bash
sqlflow run lookups/welldb-unitalias-01-pre.yaml
sqlflow run lookups/welldb-unitalias-02-ing.yaml
sqlflow run lookups/welldb-lookups-00-cache.yaml --set partition=dev    # or 'partition=*' for every registered partition
```

The refresh writes into the module's database, so on a workstation `sqlflow` needs it (the catalog's database: `--db`,
by default `${env:SQLFLOW_CATALOG_DB}`), and `OSDU_DATA_DB` must resolve where it runs. In
the GUI, **Refresh now** on the Cache page (a **Refresh** menu naming the flows, when several fill the partition) runs
the same refresh for the partition picked in the title bar. Add `--operation plan` to count the rows the refresh would
capture without writing anything.

Lineage connects a cache flow to a delivery flow through the partition: they are ordered when they share a partition
(named under `partitions` or as a literal `data-partition-id` header), or when both serve every registered partition. A
cache flow that serves the registry and a delivery flow that hard-codes `data-partition-id: dev` are not ordered against
each other; `sqlflow lineage` shows whether they are.

## 6. See the result

- **The run's result** lists the type with its origin `table`, its row count and `change` (`added` on the first
  refresh).
- **`sqlflow cache list dev`** prints the new version, `types moved: UnitAlias (added)` under it.
- **The Cache page** (Build, Delivery, Cache, with `dev` picked in the title bar): the Records tab lists `UnitAlias` among
  the types, its rows with `source_unit` and `osdu_unit`, and, on a row, how a mapping reads it. The Setup tab shows the
  cache flow and the table it declares.
- **`sqlflow check`** on the delivery flow checks the mapping against the current cache version. A table the cache does
  not hold yet fails with `replace reads $cache.UnitAlias, which cache version '<version>' does not hold. Cached: ...`.

## 7. Change the table later

Add or correct a row in a new file and run the chain again. The refresh writes a new version only if the table moved,
then tags exactly the delivered records it reaches: those built from a row that changed (`changed`) or was removed
(`removed`), and those that looked up a key the table did not list before and now does (`listed`). With the default
`onChange: auto` they are delivered again on their flow's next run; with `onChange: approve` on the type they wait for
a decision on the Cache page's Deliveries tab ([The partition cache](../concepts/partition-cache.md)). A refresh that
finds the same rows writes nothing and redelivers nothing.

## When a dictionary or a dimension is the better home

| Your table | Home |
| --- | --- |
| Arrives as files or comes from another database, or more than a few hundred rows | An ingestion table and a `table` type (this guide). |
| A short, static list people edit by hand and review in a pull request | A [dictionary document](../flow/dictionary.md) in `dictionaries/` and a `dictionary` type: no pre or ing flow at all. |
| The distinct values of a property OSDU already holds, cleaned into labels | A [dimension](../flow/dimension.md) and a `dimension` type. |
| Reference data OSDU holds (units, curve types) | A `kind` type ([reference data guide](reference-data-cache.md)). |
| More than 100,000 rows | Not the cache: join it into the ingestion tables the delivery flow reads. |

## What goes wrong

| Symptom | Cause and fix |
| --- | --- |
| `Cache flow '<flow>': the table OsduData.silver.UnitAlias, which type UnitAlias reads, was not found on the source database, or the identity this node connects with cannot see it. ...` | The ing flow has not run yet, or the node's login has no SELECT on the table. |
| `... is keyed by column '<key>', which table <table> does not hold. Columns: ...`, or `... keeps column '<column>', which table <table> does not hold. Columns: ...` | `key` or a `fields` column is misspelled; the message lists the table's columns. |
| `... table <table> has rows type UnitAlias could not be keyed by source_unit, so nothing was captured: ...` | Empty keys, keys over 256 characters or holding a control character, or two rows whose keys are the same once trimmed (spaces around a key are trimmed, not refused). Fix the source or the ing flow's key. |
| `... holds more than 100000 rows for type UnitAlias ...` | The table is too large for the cache. |
| `source.endpoint and source.auth reach the OSDU platform, and the flow declares no type searched there; ...` | A lookup-only cache flow copied from a reference data flow; remove the endpoint and auth. |
| `... a lookup table from a table or a dictionary is declared by one cache flow of a partition only` | Two cache flows declare `UnitAlias` for one partition; keep one. |
| A record held with `replace reads $cache.UnitAlias, and version <v> of the cache of partition 'dev' holds no type 'UnitAlias'` | The cache flow has not refreshed that partition yet. |
| A record held with `... the id ... gives dev:reference-data--UnitOfMeasure:<code>:, and version <v> of the cache of partition 'dev' holds no such reference-data--UnitOfMeasure record in UnitOfMeasure, so the reference would point at nothing` | The table translates to a code the partition does not hold; correct the row, or let the property go out unverified ([modifiers](../flow/mapping-modifiers.md)). |

## Related

- [Cache flow](../flow/cache.md), the `table` origin in full.
- [The partition cache](../concepts/partition-cache.md): versions and how a change reaches delivered records.
- SQLFlow's [staging to silver recipe](../../../../sqlflow/docs/wiki/narratives/recipe-staging-to-silver.md) for the
  ingestion side.
