---
id: delivery-guide-dimensions
title: "Building a dimension of OSDU values and using it to filter search, drop-downs and reports"
type: guide
summary: "Build a dimension flow end to end: pick key, label and attributes, keep templates, plan, build, check the values, then search, query the table or cache it."
keywords:
  - build a dimension
  - pick list of osdu values
  - drop-down values
  - filter osdu search
  - wellbore names
  - curve mnemonics
  - group spellings
  - cascading filters
  - dimension table sql
  - dimension in the cache
related:
  - delivery-flow-dimension
  - delivery-cli-dimensions
  - delivery-flow-cache
  - delivery-flow-dictionary
  - delivery-concept-templates
  - delivery-wiki-pattern-catalog
  - cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCleaner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRemoval.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryDimensionVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/gui/src/module.tsx
---

# Building a dimension of OSDU values and using it to filter search, drop-downs and reports

This guide builds a [dimension flow](../flow/dimension.md) over the well logs the welldb flows deliver: a `Wellbore`
dimension that lists every wellbore the logs name by its name, with its country and the logging services its logs
hold, and a `CurveMnemonic` dimension that groups the spellings of curve mnemonics. It then uses them: a pick list, the
search that finds the logs holding the values picked, a cascade of drop-downs, a SQL query over the dimension's own
table, and a cache type a mapping reads.

A dimension only reads OSDU. Nothing here writes to OSDU or changes what a delivery sends.

## 1. Decide the key, the value and the attributes

Start from the question a person asks, and split it three ways:

| You want | Write | Example |
| --- | --- | --- |
| What the search compares | `path`: the key, exactly as the index holds it | `data.WellboreID` on the well logs |
| What a person picks | `label`, read from the record the key names; or the key itself, cleaned | `data.FacilityName` of the wellbore |
| Facts to narrow by | `attributes`, read the same way, or collected from the dimension's own records | the wellbore's country; the logs' `data.LoggingService` |
| Spellings grouped | `clean` steps, and a dictionary for the cases a rule cannot catch | `GAMMA` and `GRC` grouped under `GR` |

Reading through the well logs (rather than the wellbores) keeps only the wellbores that have logs, and every filter the
dimension writes then finds logs. The kind is the one your search will read.

## 2. Keep the templates of the kinds it reads

A dimension reading a `data` path learns how the index stores it from the template of every kind its pattern matches in
the partition, as the template folders on the flow's path hold it. Put the template of each in a folder on the flow's
path ([template folders](../concepts/templates.md#template-folders)). The commands in this guide read and write the
module's database, which is the catalog's (`SQLFLOW_CATALOG_DB`, or `--db <conn-ref>`) unless `SQLFLOW_OSDU_DB` names one
of its own:

```bash
sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

A dimension reading a property of the record itself (`kind`, `legal.legaltags`, `acl.owners`, `tags.<name>`) needs no
template.

## 3. Write the flow

Start small: one dimension, a key and a label. This is the start of the estate's
`flows/welldb-welllog-05-dimensions.yaml`; step 6 adds to it, and the whole file is the
[dimension flow's example](../flow/dimension.md). `{deliveredBy}` is the flow's parameter, `welldb` unless a run
names another.

```yaml
# flows/welldb-welllog-05-dimensions.yaml, its top and its first dimension, a key and a label: step 6 adds to it
flowType: dimension
name: welldb-welllog-05-dimensions
description: The wellbores, curve mnemonics and units of the well logs the welldb flows deliver, ready for a search panel.
batch: welldb
partitions: [dev, test]

parameters:
  deliveredBy: { default: welldb }

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body: { scope: "${env:OSDU_SCOPE}" }

parallelism: { concurrency: 4 }

dimensions:
  - name: Wellbore
    description: Every wellbore the well logs name, by its name, with its country and operator.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.WellboreID
    label: data.FacilityName
```

Check it offline:

```bash
sqlflow validate flows/welldb-welllog-05-dimensions.yaml
```

```text
OK  'welldb-welllog-05-dimensions' is valid (dimension: ${env:OSDU_URL} -> dimensions).
```

`validate` checks the document: every path is one a query can name, the table's column names work, every clean step
and pattern can run, and every dictionary a `map` step names is found and reads. It does not reach OSDU or the
templates.

## 4. Plan, then build

A plan settles each dimension's field from the templates and counts the records it would read, reading no value and
keeping nothing:

```bash
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --operation plan
```

A missing template is reported here, naming the kind and how many records it holds. Then build:

```bash
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev
```

A build reads every distinct wellbore id however many there are (OSDU's own aggregation stops at 1,000), reads each
wellbore's name by id, 500 ids a search, and keeps it all in the module's database. A run started from the GUI
(**Run pipeline** on the flow's pipeline) does the same, and a dimension can be picked to build only it; from the
command line, `--payload '{"dimensions":["Wellbore"]}'`.

## 5. Look at what it built

```bash
sqlflow dimensions list   flows/welldb-welllog-05-dimensions.yaml --partition dev
sqlflow dimensions values flows/welldb-welllog-05-dimensions.yaml --dimension Wellbore --order records --partition dev
sqlflow dimensions keys   flows/welldb-welllog-05-dimensions.yaml --dimension Wellbore --search WB-0001 --partition dev
```

`values` lists each name with its records and the ids behind it; `keys` lists each wellbore id with the name read for it
and the search filter that finds its logs. In the GUI, **OSDU**, **In OSDU**, **Dimensions** opens the same, and the
dimension's **Definition** tab draws how each column is read from the YAML.

Two things to look for:

- **Keys with no label.** A wellbore the search does not hold, or one with no `FacilityName`, has no label, and its value
  is the code its id ends with. `history` shows how many and why. To list them under one value a person can pick, add
  `unlabelled: Not specified`.
- **Keys of no value.** `sqlflow dimensions keys ... --left-out` lists keys cleaning left out, with the reason.

## 6. Add attributes and clean steps

Add the wellbore's country and operator, the logging services of its logs, and a second dimension whose spellings are
grouped. The file's first two dimensions then read (the file goes on with `CurveUnit` and `LegalTag`):

```yaml
dimensions:
  - name: Wellbore
    description: Every wellbore the well logs name, by its name, with its country and operator.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.WellboreID
    label: data.FacilityName
    unlabelled: Not specified
    attributes:
      Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
      Operator: [data.CurrentOperatorID, data.OrganisationName]
      LoggingService: { collect: data.LoggingService }

  - name: CurveMnemonic
    description: The curve mnemonics of the well logs, spellings grouped.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.Curves.Mnemonic
    clean:
      - trim
      - collapseSpaces
      - upper
      - replace: { pattern: '^(\w+)_\d+$', with: '$1' }
      - map: curve-aliases
    countRecords: true
```

`Country` follows each geopolitical entity the wellbore names and keeps the one whose type is a country; `Operator`
follows the wellbore's current operator to the organisation's name.
`LoggingService` is collected from the logs themselves, so each wellbore holds every logging service its logs name,
with how many logs name each. The clean steps trim, collapse runs of spaces, upper-case and strip a numeric suffix
(`GR_2` is `GR`) before `map: curve-aliases` reads the [dictionary](../flow/dictionary.md)
`dictionaries/curve-aliases.yaml` above the flow:

```yaml
documentType: dictionary
name: curve-aliases
description: Curve mnemonics the well database spells several ways, each with the mnemonic a person picks.
entries:
  GAMMA: GR
  GRC: GR
  DEPTH: DEPT
  MD: DEPT
```

`countRecords: true` counts each value's records exactly, since a log holds many curves. Build again. Only what changed
is written: `sqlflow dimensions changes ... --dimension Wellbore` lists the keys that arrived, left, came back or moved
to another value (a renamed wellbore moves to its new name).

## 7. Search with the values picked

The search that finds every log of the wellbore `Wellbore A-1` holding a gamma ray curve:

```bash
sqlflow dimensions search flows/welldb-welllog-05-dimensions.yaml --pick 'Wellbore=Wellbore A-1' --pick CurveMnemonic=GR --partition dev
```

The query goes to the console alone, ready for a search request (`--json` gives the whole request body). Values picked in
one dimension are joined with OR, dimensions with AND, and each dimension's own query is added once. A pick by attribute
selects keys: `--where 'Wellbore.Country=United States'` compares every wellbore of that country. The GUI's search builder on the
**Dimensions** page composes the same search.

## 8. Feed a cascade of drop-downs

Each drop-down lists the values of one attribute among the keys the other picks leave:

```bash
sqlflow dimensions attributes flows/welldb-welllog-05-dimensions.yaml --dimension Wellbore --attribute Country --partition dev
sqlflow dimensions attributes flows/welldb-welllog-05-dimensions.yaml --dimension Wellbore --attribute LoggingService --attr 'Country=United States' --partition dev
```

An application reads the same from the control plane (`GET /api/v1/delivery/dimensions/{id}/attributes/{name}`), or from
the dimension's table.

## 9. Query the dimension's table

Every dimension is a table in the module's database, `osdu.dim_<dimension>`, named after what it reads: `WellboreID`
and `FacilityName` for the key and its value, a column per attribute, and `id`, the number a table of facts joins on.
With a collected attribute, a row is a key and one value it collects.

```sql
SELECT [FacilityName], [Country], [LoggingService], [records]
FROM [osdu].[dim_Wellbore]
WHERE [partition] = 'dev' AND [Country] = 'United States'
ORDER BY [FacilityName];
```

The table follows the flow: an attribute added gets its column on the next build, and nothing is dropped. Changing the
path or label renames the key's or value's column on the next build; give a name that stays with
`columns: { key: <name>, value: <name> }`. `sqlflow dimensions export ... --set table --out wellbores.csv` writes it whole.

## 10. Use a dimension in a mapping

A [cache flow](../flow/cache.md) holds a dimension's values as a lookup table, keyed by value, with its keys, records,
filter and the attributes `fields` names beside it:

```yaml
types:
  - dimension: CurveMnemonic
    dimensionFlow: welldb-welllog-05-dimensions
  - dimension: Wellbore
    dimensionFlow: welldb-welllog-05-dimensions
    fields: [Country]
```

The cache reads what the dimension's last completed build kept in the partition, so lineage orders the cache flow after
the dimension flow. How a mapping reads a lookup table is in [mapping lookups](../flow/mapping-lookups.md).

## 11. Keep it current, and retire a dimension

Schedule the flow after the delivery flows that write the well logs ([schedules](../../../../sqlflow/docs/reference/flow/schedule.md));
lineage orders it after them. To have each build read only what changed since the last one, give the flow an
`incremental` block naming a record's unique key, and how often a full load should still settle what deleted records
held ([full and incremental loads](../flow/dimension.md#full-and-incremental-loads)):

```yaml
incremental:
  keyColumns: [id]
  fullLoadAfterHours: 168
```

A dimension taken out of the YAML is built no more and keeps what it held; declaring it
again brings its ids back. To delete one for good (here `CurveUnit`), take it out of the flow's file first, and make
sure no cache flow captures it:

```bash
sqlflow dimensions remove flows/welldb-welllog-05-dimensions.yaml --dimension CurveUnit --partition dev
```

## When something goes wrong

| Symptom | Cause and fix |
| --- | --- |
| `... and the flow reads no template of <kind> ...` | Put the template of every kind the pattern matches in a template folder on the flow's path (step 2), or narrow `kind`. |
| `... which the kinds its pattern matches index differently ...` | Two schema versions store the path differently; narrow `kind` to the versions that agree. |
| `<path> holds more than <n> distinct values ...` | Narrow `query`, or raise `maxValues` (at most 5,000,000). |
| `... would write the table osdu.dim_<name>, which dimension <name> of <flow> writes ...` | Dimension names are unique across the flows of a database; rename one. |
| A value you expect is missing | `keys --left-out` shows keys cleaning left out; a text over 256 characters cannot be a key, and the build's notes count those. |
| Names are ids | The label is not read: check `label` against the record the key names, and the build's notes. |
| A search is refused for too many clauses | Pick fewer values, or narrow by attribute; one search holds at most 1,000 clauses. |
