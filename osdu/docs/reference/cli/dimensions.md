---
id: delivery-cli-dimensions
title: "sqlflow dimensions: read a dimension flow's values, keys, table, filters and composed searches"
type: cli-command
summary: "Read a dimension flow's values, keys, table, filters, searches, builds and views from a terminal, and remove a dimension or a view."
keywords:
  - sqlflow dimensions
  - dimension values
  - dimension keys
  - dimension table
  - attributes
  - search filter
  - compose search
  - export dimension
  - remove dimension
  - change log
  - dimension views
  - remove-view
  - "--pick"
  - "--attr"
cliCommand: dimensions
related:
  - delivery-flow-dimension
  - delivery-guide-dimensions
  - delivery-cli-run
  - delivery-flow-cache
  - cli-run
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryDimensionVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionTable.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionFilters.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionExport.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionStates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery.Search/OsduQuery.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerbContext.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliModuleSet.cs
---

# sqlflow dimensions: read a dimension flow's values, keys, table, filters and composed searches

`sqlflow dimensions` reads what a [dimension flow](../flow/dimension.md) built: each dimension's values and keys, its
table, the values an attribute holds, the search filter of the values picked, the search composed across dimensions,
its builds and change log, and the flow's views; it exports a dimension whole and removes a dimension or a view the
flow no longer declares. It is one of the
verbs OSDU Delivery adds to SQLFlow's command line (`SqlFlow.Delivery.Cli.Host`, published as `sqlflow`). It reads the
module's database only: it needs no control plane and sends nothing to OSDU.

Building a dimension is a run of its flow, not this verb:

```bash
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --payload '{"dimensions":["Wellbore"]}'
```

## Synopsis

```bash
sqlflow dimensions list       <flow.yaml> [--partition <id>]
sqlflow dimensions table      <flow.yaml> --dimension <name> [--search <text>] [--attr <attribute>=<value> ...] [--order value|key|records|id|<column>] [--desc] [--max <n>]
sqlflow dimensions values     <flow.yaml> --dimension <name> [--search <text>] [--attr <attribute>=<value> ...] [--order value|records] [--removed] [--max <n>]
sqlflow dimensions keys       <flow.yaml> --dimension <name> [--value <value> | --left-out] [--search <text>] [--attr <attribute>=<value> ...] [--order arrival|count] [--removed] [--max <n>]
sqlflow dimensions attributes <flow.yaml> --dimension <name> --attribute <name> [--attr <attribute>=<value> ...] [--value <value> ...] [--search <text>] [--max <n>]
sqlflow dimensions filter     <flow.yaml> --dimension <name> --value <value> [--value <value> ...]
sqlflow dimensions search     <flow.yaml> [--pick <dimension>=<value> ...] [--where <dimension>.<attribute>=<value> ...] [--kind <kind>] [--within <query>]
sqlflow dimensions history    <flow.yaml> --dimension <name> [--max <n>]
sqlflow dimensions changes    <flow.yaml> --dimension <name> [--build <n>] [--value <value>] [--change added|removed|moved|restored] [--max <n>]
sqlflow dimensions export     <flow.yaml> --dimension <name> [--set values|keys|table] [--format csv|jsonl] [--out <file>]
sqlflow dimensions remove     <flow.yaml> --dimension <name>
sqlflow dimensions views      <flow.yaml> [--view <name> | --suggest <dimension>]
sqlflow dimensions remove-view <flow.yaml> --view <name>
```

Every form also takes `--partition <id>`, `--db <conn-ref>`, `--json` and `-v`.

## Options every form takes

| Option | Meaning |
| --- | --- |
| `<flow.yaml>` | The dimension flow's file. Its dimensions are named as it declares them now. |
| `--partition <id>` | The partition whose dimensions are read. Left out, it is settled as a run settles it: the registry's default among the flow's partitions, else its only partition. A named partition is taken as it is, so a partition taken out of the registry keeps its dimensions readable; for a flow that names its partitions, it has to be one of them. A flow whose partition is its `source.headers` takes none. |
| `--dimension <name>` | The dimension, by its name in the flow (ignoring case). Required by every form but `list`, `search`, `views` and `remove-view`. |
| `--db <conn-ref>` | The database the module's tables live in: the catalog's (`--db`, else `${env:SQLFLOW_CATALOG_DB}`). |
| `--json` | Writes exactly one JSON document to the console; notes go to the error stream. |
| `--max <n>` | The most rows a page lists, 1 to 1,000; 50 when left out. |

A value is named exactly as the dimension holds it (`--value "Not specified"`). `--attr <attribute>=<value>` is split at
its first `=`, so a value may hold one; repeated for one attribute it means any of the values, for several attributes all
of them, held by one key.

## list

Each dimension the flow declares, in the partition: what it reads, where its label is read, its table's key and value
columns, its attributes, how many values and keys it holds, when it was built, its table, and a newer build that failed
or is running.

```text
welldb-welllog-05-dimensions@dev: 4 dimension(s)
  Wellbore  (osdu:wks:work-product-component--WellLog:1.4.0 data.WellboreID labelled by data.FacilityName; key WellboreID, value FacilityName; attributes Country, Operator, LoggingService)  1184 value(s) from 1203 key(s), built 2026-10-08 03:00:41Z
      table osdu.dim_Wellbore
  CurveMnemonic  (osdu:wks:work-product-component--WellLog:1.4.0 data.Curves.Mnemonic; key Mnemonic, value CurveMnemonic)  412 value(s) from 655 key(s), built 2026-10-08 03:00:29Z
      table osdu.dim_CurveMnemonic
      newest build 58 failed at 2026-10-09 03:00:05Z: Dimension CurveMnemonic could not read dictionary curve-aliases: ...
  CurveUnit  (osdu:wks:work-product-component--WellLog:1.4.0 data.Curves.CurveUnit; key UnitID, value Unit)  not built yet
  LegalTag  (*:*:*:* legal.legaltags; key legaltags, value LegalTag)  6 value(s) from 6 key(s), built 2026-10-08 03:00:09Z
      table osdu.dim_LegalTag
```

With `--json`: `flow`, `ledger`, and `dimensions`, each with `dimension`, `dimensionId`, `kind`, `path`, `label`,
`attributes` (each `name` with `steps` or `collect`, and `keep`; one read through `steps` also `many` and `decode`), `aggregateBy`, `table`, `keyColumn`, `valueColumn`, `values`,
`keys`, `lastBuiltUtc` and `latestBuild`.

## table

A page of the dimension's own table, `osdu.dim_<dimension>`: a row per key and value it collects, with the row's `id`
(what a table of facts joins on), its records, its value, its key and a column per attribute.

| Option | Meaning |
| --- | --- |
| `--search <text>` | Rows whose key, value or any attribute contains the text, ignoring case. |
| `--attr <attribute>=<value>` | Rows whose attribute column holds the value exactly. |
| `--order <column>` | A column by its name in the table (the key's, the value's, `records`, `id` or an attribute's), or the words `value` (the default) and `key`, which name those two columns whatever the dimension calls them. |
| `--desc` | Largest first. |

```bash
sqlflow dimensions table flows/welldb-welllog-05-dimensions.yaml --dimension Wellbore --max 3 --partition dev
```

```text
Wellbore: 3 of 2410 row(s) of osdu.dim_Wellbore
          id       records  FacilityName  WellboreID  Country  Operator  LoggingService
         412            12  "Wellbore A-1"  dev:master-data--Wellbore:WB-0001:  Country="United States"  Operator="Example Operator"  LoggingService="GR-RES"
         413             3  "Wellbore A-1"  dev:master-data--Wellbore:WB-0001:  Country="United States"  Operator="Example Operator"  LoggingService="Not specified"
          97            40  "Wellbore A-2"  dev:master-data--Wellbore:WB-0002:  Country="United States"  Operator="Not specified"  LoggingService="SONIC"
  more rows follow: raise --max (now 3), narrow with --search or --attr, or export the table.
```

A column name the table does not have is refused, listing the columns it has
(`The table osdu.dim_Wellbore has no column 'Field' to order by; it has FacilityName, WellboreID, records, id, Country,
Operator, LoggingService.`). A dimension built before dimensions had tables has its table made by this read. With
`--json`: `dimension`, `table`, `keyColumn`, `valueColumn`, `total`, `more`, and `rows`, each row every column under its
own name (`id`, `partition`, `key_id`, `WellboreID`, `FacilityName`, each attribute, `records`, `filter`).

## values

A page of the dimension's values, each with its records (`~` before the number when summed from its keys rather than
counted), its commonest keys, and its attribute values.

| Option | Meaning |
| --- | --- |
| `--search <text>` | Values whose text, or any of whose keys, contains the text, ignoring case. |
| `--attr <attribute>=<value>` | Values holding a key with that attribute value. |
| `--order value\|records` | Value order (the default) or the most records first. |
| `--removed` | Also values no build finds any more. |

```bash
sqlflow dimensions values flows/welldb-welllog-05-dimensions.yaml --dimension CurveMnemonic --order records --max 2 --partition dev
```

```text
CurveMnemonic: 2 of 412 value(s)
         1,870  GR  <- "GR", "GRC", "GAMMA"
           935  DEPT  <- "DEPT", "MD"
```

With `--json`: `dimension` and `values`, each with `valueId`, `value`, `records`, `recordsExact`, `keys`,
`unfilterable`, `filter` (null when the value's filter is in parts), `filterParts`, `removedUtc`, `top` and `attributes`.

## keys

A page of the dimension's keys exactly as the index holds them, each with its count, its value or why it has none, its
label when it differs from the value, its attributes and its own search filter.

| Option | Meaning |
| --- | --- |
| `--value <value>` | Only the keys of one value. |
| `--left-out` | Only the keys of no value (cleaning left nothing, the value was too long, a `map` step left it out, or a step failed). Not with `--value`. |
| `--search <text>` | Keys whose text or label contains the text, ignoring case. |
| `--attr <attribute>=<value>` | Keys holding that attribute value. |
| `--order arrival\|count` | The order the keys arrived in (the default), or the most records first. |
| `--removed` | Also keys no build finds any more. |

```text
Wellbore: 1 of 1203 key(s)
            15  "dev:master-data--Wellbore:WB-0001:"  -> Wellbore A-1
                Country: United States; Operator: Example Operator; LoggingService: GR-RES (12), Not specified (3)
                search data.WellboreID.keyword:"dev:master-data--Wellbore:WB-0001:"
```

A key no query can carry is marked `(no query can carry it)`. With `--json`: `dimension` and `keys`, each with `keyId`,
`key`, `label`, `labelFrom`, `value`, `leftOut`, `note`, `count`, `filterable`, `filter`, `attributes` (a collected
attribute as a list of `value` and `records`) and `removedUtc`.

## attributes

The values the attribute `--attribute` names holds among the dimension's keys, the most records first, each with how
many keys hold it. This is the list one drop-down of a cascade shows.

| Option | Meaning |
| --- | --- |
| `--attribute <name>` | The attribute (required). |
| `--attr <attribute>=<value>` | Only keys holding the other attribute values picked. An `--attr` of this attribute itself does not narrow its own list. |
| `--value <value>` | Only keys belonging to the values named (repeatable). |
| `--search <text>` | Attribute values containing the text. |

```text
Wellbore.Operator: 2 value(s)
           640  "Example Operator"  (311 key(s))
            97  "Not specified"  (54 key(s))
```

With `--json`: `dimension`, `attribute`, `collect` (the path a collected attribute reads, else null) and `values`, each
with `value`, `keys` and `records`.

## filter

The search that finds every record holding one of the named values' keys. Each search is printed on its own line on the
console, the dimension's own query joined in, ready to send to the search service; what the filter covers and leaves out
goes to the error stream. A filter holds at most 500 keys a query, so a value with more keys prints more than one line.

```bash
sqlflow dimensions filter flows/welldb-welllog-05-dimensions.yaml --dimension CurveMnemonic --value GR --partition dev
```

```text
(tags.DeliveredBy:"welldb") AND ((nested(data.Curves, (Mnemonic.keyword:"GAMMA"))) OR (nested(data.Curves, (Mnemonic.keyword:"GR"))) OR (nested(data.Curves, (Mnemonic.keyword:"GRC"))))
```

The error stream says `CurveMnemonic: kind osdu:wks:work-product-component--WellLog:1.4.0, 1 value(s), 3 key(s) in 1
search(es)`, then the keys no query can carry, the values no build finds any more, and the names that are no value of
the dimension. At most 1,000 values are named at once, holding at most 50,000 keys. It exits 1 when no search can be
written. With `--json`: `dimension`, `kind`, `query`, `aggregateBy`, `filters` (the filters alone), `searches`, `values`,
`keys`, `unfilterable`, `removed` and `missing`.

## search

The search across the flow's dimensions: every record holding one of the values picked in each dimension.

| Option | Meaning |
| --- | --- |
| `--pick <dimension>=<value>` | A value of a dimension, split at the first `=` (repeatable). Several of one dimension are joined with OR. |
| `--where <dimension>.<attribute>=<value>` | The keys of a dimension holding an attribute value, split at the first `=` and before it at the last `.` (repeatable). |
| `--kind <kind>` | The kind to search, which every dimension's kind has to cover segment by segment. Needed when the dimensions read different kinds. |
| `--within <query>` | A query of your own the search is narrowed by. |

The parts are joined with AND, each dimension's own query added once, every term in parentheses. A pick by attribute
compares exactly the keys holding it, fewer than 1,000. A collected value picks records: `--where
Wellbore.LoggingService=SONIC` finds the logs whose `data.LoggingService` is one of the texts the value stands for, and
picking the `unlabelled` value finds the logs holding none of the attribute's texts (refused past 1,000 texts). A
search combines at most 20 dimensions and 1,000 clauses (each key or text compared is one; the service allows 1,024),
and is refused rather than cut short past them.

```bash
sqlflow dimensions search flows/welldb-welllog-05-dimensions.yaml --pick 'Wellbore=Wellbore A-1' --pick CurveMnemonic=GR --partition dev
```

```text
(tags.DeliveredBy:"welldb") AND (data.WellboreID.keyword:"dev:master-data--Wellbore:WB-0001:") AND ((nested(data.Curves, (Mnemonic.keyword:"GAMMA"))) OR (nested(data.Curves, (Mnemonic.keyword:"GR"))) OR (nested(data.Curves, (Mnemonic.keyword:"GRC"))))
```

The query goes to the console alone; the error stream says `kind osdu:wks:work-product-component--WellLog:1.4.0, 2
dimension(s), 5 clause(s)` and one line per dimension with its values and keys, then what was left out. With `--json`:
`kind`, `query`, `request` (the body to send to the search service: `kind`, `query`, `limit: 1000`), `clauses`, `parts`
(each `dimension`, `aggregateBy`, `values`, `keys`, `unfilterable`, `filter`, `query`, `attributes`), `removed`,
`missing` and `notes`.

## history

The dimension's builds, newest first: status, what each found and changed, how it read the index and the labels, who
ran it, its error and its first 5 notes.

```text
Wellbore: 1 build(s)
        57  completed  2026-10-08 03:00:02Z  1184 value(s) from 1203 key(s), 0 of none; 6 arrived, 1 left, 2 moved, 0 came back; 3 aggregation(s), 1 split(s), 0 scan page(s), 1203 labelled in 7 search(es)  by cli:analyst@workstation
```

With `--json`: `dimension` and `builds`, each with `build`, `runId`, `status`, `values`, `keys`, `leftOut`,
`unfilterable`, `keysAdded`, `keysRemoved`, `keysMoved`, `keysRestored`, `labelled`, `unlabelled`, `labelQueries`,
`records`, `withValue`, `aggregations`, `splits`, `scanPages`, `actor`, `startedUtc`, `completedUtc` and `error`.

## changes

The change log, newest first: keys that arrived (`added`), left (`removed`), came back (`restored`) or moved to another
value (`moved`). `--build <n>` narrows it to one build, `--value <value>` to one value, `--change` to one kind.

```text
Wellbore: 1 change(s)
  2026-10-08 03:00:40Z  build 57      moved     "dev:master-data--Wellbore:WB-0007:"  from Not specified to Wellbore A-7
```

With `--json`: `dimension` and `changes`, each with `changeId`, `build`, `key`, `change`, `from`, `to` and `changedUtc`.

## export

The whole of a dimension, as CSV (the default, with a header row) or JSON Lines (`--format jsonl`), to `--out` or the
console. A file is written beside its name and moved into place, so a failure never leaves half a file; the line
`Wrote <n> row(s) of the table of Wellbore to <path>` goes to the error stream. It is the same file the control plane's
export gives.

| `--set` | One row per | Columns |
| --- | --- | --- |
| `values` (default) | value, in value order | `value_id`, `value`, `records`, `records_exact`, `keys`, `unfilterable`, `filter_parts`, `filter`, `first_seen_utc`, then `attribute_<name>` per attribute (several values joined by `; `) |
| `keys` | key, in arrival order | `key_id`, `key`, `label`, `label_from`, `value`, `left_out`, `note`, `count`, `filterable`, `filter`, `first_seen_utc`, then `attribute_<name>` |
| `table` | row of `osdu.dim_<dimension>` | `id`, `partition`, `key_id`, the key's column, the value's column, one per attribute, `records`, `filter` |

In JSON Lines every value is kept exactly (property names in camel case for `values` and `keys`, the table's column names
for `table`). In CSV a cell a spreadsheet would run as a formula is written with a leading apostrophe.

## remove

Removes a dimension the flow file no longer declares, and everything kept of it in the partition, for good: its builds,
values, keys, attribute values, collected texts, change log and its rows in its table, which is dropped when no
partition writes it any more. It is refused for a dimension the file declares
(`welldb-welllog-05-dimensions declares dimension CurveUnit, so it is not removed. Take it out of the flow's file first:
only a dimension the flow no longer declares is removed.`) and for one a cache flow of the partition captures, naming
the cache flow and its type. It is recorded as a `remove-dimension` activity of the flow under `cli:<user>@<machine>`.

```text
Removed dimension CurveUnit of welldb-welllog-05-dimensions in dev: 38 value(s), 38 key(s), 0 attribute value(s), 0 collected text(s), 2 change(s) and 6 build(s); dropped the table osdu.dim_CurveUnit.
```

The control plane offers the same to an admin alone (`DELETE /api/v1/delivery/dimensions/{dimensionId}`); this form is
for whoever holds the module database's credentials.

## views

The flow's [views](../flow/dimension-views.md): each it declares, as `osdu.v_dim_<view>`, with its `from` dimension, its
joins, whether a build wrote it as the file declares it (`written as declared`, `changed: the next build writes it
again`, `not written yet`, or why a build dropped it), and its last check (the partition, the rows, what each join found
and each conversion could not read); then each view a build of the flow wrote that the file no longer declares.

```text
welldb-welllog-07-metadata-dimensions: 1 view(s)
  Curve  osdu.v_dim_Curve  from LogCurve joining WellLog as Log, RefUnitOfMeasure as Unit  written as declared
      last check passed in 'dev' at 2026-10-09 21:14:02Z: 88310 row(s)
      CurveUnitID: 1,204 of 88,310 rows name no row of RefUnitOfMeasure, for example 'dev:reference-data--UnitOfMeasure:ft%2Fs'.
```

`--view <name>` shows one view: its columns with their types and expressions, its newest checks, and the statement a
build last wrote it with (or, before any build, the one a build writes). With `--json`, the view, its joins, columns,
checks, and both statements.

`--suggest <dimension>` offers the joins a view whose rows are that dimension's could make: each column holding a key,
joined to the dimension of the flow keyed by what it holds, one row a key, of the entity type the template of its
records names (`x-osdu-relationship`), as the template folders on the flow's path give it, and through each dimension so
joined. It prints the `join:` block of a view's YAML to keep or change, each join with why it is offered; nothing is
written. A column no template the flow reads describes is offered nothing, since a record's id can name a record of any
type.

```text
    from: LogCurve
    join:
      - { on: WellLogID, to: WellLog, as: WellLog }    # WellLogID and WellLog both hold ids of work-product-component--WellLog.
      - { on: CurveUnitID, to: RefUnitOfMeasure, as: RefUnitOfMeasure }    # The template says CurveUnitID names reference-data--UnitOfMeasure, which RefUnitOfMeasure reads.
```

## remove-view

Removes a view the flow file no longer declares, for good: the view from the database and its record with its checks. It
is refused for a view the file declares (its next build would write it again; take it out of the file and build the
flow, which drops it) and for one another flow wrote. It is recorded as a `remove-view` activity of the flow under
`cli:<user>@<machine>`.

```text
View Curve of welldb-welllog-07-metadata-dimensions removed: osdu.v_dim_Curve dropped, 12 check(s) taken with its record.
```

## Exit codes and errors

Every form exits 0 on success. A usage mistake prints `ERROR  <reason>` and the verb's usage lines and exits 1; any other
failure prints `ERROR  <message>` and exits 1; `filter` also exits 1 when no search can be written. Ctrl+C exits 130.

| Message | Cause |
| --- | --- |
| `name the dimension flow document whose dimensions these are.` | No `<flow.yaml>` after the form (with the usage). |
| `say what to do: list, table, values, keys, attributes, filter, search, history, changes, export or remove.` | An unknown form (with the usage). |
| `Dimensions live in the module's database. Run 'sqlflow dimensions' with --db <conn-ref>, or set the catalog variable.` | No module database. |
| `Name the dimension with --dimension <name>; welldb-welllog-05-dimensions declares Wellbore, CurveMnemonic, CurveUnit, LegalTag.` | `--dimension` missing. |
| `Dimension Wellbore has not been built in welldb-welllog-05-dimensions@dev yet. Build it with: sqlflow run <flow.yaml> --payload '{"dimensions":["Wellbore"]}'` | No build in the partition. |
| `Dimension Wellbore has no value 'wellbore a-1'. Values are named exactly as the dimension holds them.` | A `--value` that is no value (compared exactly, case included). |
| `Dimension Wellbore reads no attribute 'Field'; it reads Country, Operator, LoggingService.` | An `--attr` or `--attribute` the dimension does not read. |
| `The dimensions picked in read different kinds (...); a search reads one. Name the kind to search, which each dimension's kind has to cover.` | `search` across kinds without `--kind`. |
| `The picks hold <n> keys and <n> clauses in all, and one search holds at most 1000 (the service allows 1024). Pick fewer values, or search each dimension's values on their own.` | Too many keys picked. |

The control plane answers the same reads under `/api/v1/delivery/dimensions` ([API](../concepts/api.md)), and the GUI's
**Dimensions** page shows them with the search builder.
