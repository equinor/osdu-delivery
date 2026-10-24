---
id: delivery-flow-dimension
title: "Dimension flow (flowType: dimension): every distinct value of an OSDU property, labelled and cleaned for filtering search"
type: flow-reference
summary: "The flowType: dimension document: every distinct value of an OSDU property, labelled and cleaned for search, its tables joined into views with typed columns."
keywords:
  - source.paging
  - createTime paging
  - dimension flow
  - distinct values
  - facet values
  - drop-down filter
  - label
  - attributes
  - collect
  - clean steps
  - unlabelled
  - aggregationsize
  - maxvalues
  - "osdu.dim_"
  - search filter
  - views
  - join dimension tables
  - typed columns
  - "osdu.v_dim_"
  - target.connection
  - datatype
  - key_hash
  - incremental load
  - full load
  - keycolumns
  - datecolumns
  - modifytime
  - fullloadafterhours
  - blueprint
  - Definition tab
  - how a dimension is built
  - template check
  - join agrees differs unchecked
  - suggest joins
  - returnedFields
  - label search fields
  - DimensionMember
  - DimensionValue
  - DimensionAttribute
  - DimensionRun
  - DimensionView
  - dimension tables
yamlPath: "(root, flowType: dimension)"
related:
  - delivery-flow-dimension-views
  - delivery-concept-dimension-blueprint
  - delivery-concept-dimension-tables
  - delivery-concept-search-paging
  - delivery-guide-dimensions
  - delivery-cli-dimensions
  - delivery-flow-cache
  - delivery-flow-dictionary
  - delivery-concept-templates
  - delivery-concept-partitions
  - delivery-concept-api
  - delivery-concept-explorer
  - concept-schema-evolution
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionColumnNames.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionPath.cs
  - osdu/src/SqlFlow.Delivery/Engine/DimensionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRecordReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/RecordChanges.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCleaner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionLabeler.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCollector.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DistinctValues.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionFilters.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRemoval.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SearchFields.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionViewMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViews.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewExpressions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionBlueprint.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionYamlSource.cs
  - osdu/src/SqlFlow.Delivery/Templates/SchemaPathReading.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.Blueprint.cs
  - osdu/gui/src/features/delivery/dimensions/DimensionDefinition.tsx
  - osdu/gui/src/features/delivery/dimensions/BlueprintDiagram.tsx
  - osdu/gui/src/features/delivery/dimensions/BlueprintInspector.tsx
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewTemplates.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionViewEndpoints.cs
  - osdu/gui/src/features/delivery/dimensions/DimensionViewTabs.tsx
  - osdu/gui/src/features/delivery/dimensions/DimensionViewSuggest.tsx
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260930103543_DimensionFlows.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261001163440_DimensionTables.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009204033_DimensionViews.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261010190424_DimensionIncrementalLoads.cs
  - osdu/docs/census/keys.dimension.json
  - osdu/specs/core/INTEGRATION.md
---

# Dimension flow (flowType: dimension): every distinct value of an OSDU property, labelled and cleaned for filtering search

A dimension flow reads every distinct value one property of an OSDU kind holds in a partition (a curve mnemonic, a
wellbore id, a legal tag), turns each into the value a person picks from a list, and keeps both in the module's
database with the search filter that finds the records holding it. Use it when an application or a person needs a
complete pick list over what OSDU holds, wants spellings grouped, or wants to filter records by a fact of another record
(the wellbore's name, its country) that a search alone cannot join. OSDU's own aggregation returns at most 1,000
distinct values by default and pages none of them; a dimension build reads them all.

A dimension flow only reads OSDU. Nothing it does is written to OSDU, and nothing it keeps changes what a delivery
sends. Building is a run like any other flow's run; reading what it built is the [`sqlflow dimensions`](../cli/dimensions.md)
verb, the GUI's **Dimensions** page, or the dimension's own table in SQL. The [dimensions guide](../guides/dimensions.md)
walks through one end to end.

## Keys, values and attributes

A dimension keeps three things apart:

- A **key** is a value exactly as the search index holds it: the text a query compares. For a reference such as
  `data.WellboreID`, the key is the record id (`dev:master-data--Wellbore:WB-0001:`), never a name.
- A **value** is what a person picks. With a `label`, it is read from the record the key names (the wellbore's
  `data.FacilityName`) and cleaned; without one, it is the key itself, cleaned. A key that names an OSDU record and has
  no label is valued by the code its id ends with, its escapes decoded (`dev:reference-data--UnitOfMeasure:us%2Fft:` is
  `us/ft`). Keys whose values come out the same are one value: with an `upper` step, `GR`, `gr` and `GR ` become one `GR`.
- An **attribute** is a further fact of each key, read the way a label is (a wellbore's country or operator), or
  collected from the dimension's own records (the logging services of a wellbore's well logs). Values and keys are
  looked up by attribute, and a search picks keys by them.

Every key carries the filter that finds exactly its records, and every value the filter that finds the records holding
any of its keys. Values picked across several dimensions compose one search.

## Example

A dimension flow over the well logs the welldb flows deliver, built in the `dev` and `test` partitions,
`flows/welldb-welllog-05-dimensions.yaml`:

```yaml
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

parallelism: { recordsAtOnce: 4 }

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

  - name: CurveUnit
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.Curves.CurveUnit
    columns: { key: UnitID, value: Unit }

  - name: LegalTag
    kind: "*:*:*:*"
    path: legal.legaltags
    countRecords: true
    partitions: [dev]
```

`Wellbore` keys each well log's wellbore id, values it by the wellbore's name, and keeps each wellbore's country (read
through its geopolitical contexts, keeping the one whose type is a country), its operator's name, and the logging
services its well logs name. `CurveMnemonic` groups spellings through its clean steps and a dictionary
(`dictionaries/curve-aliases.yaml`, a [dictionary document](dictionary.md) of pairs). `CurveUnit` keys unit references
and values each by its decoded code. `LegalTag` reads a property of every record of every kind, in `dev` only.

## Top-level keys

Unknown keys are refused when the document is read (`invalid YAML at line 5, column 3 - Property 'bogus' not found on
type ...`).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `flowType` | text | required | `dimension`. |
| `name` | text | required | The flow's name: its pipeline identity, and the name its dimensions are kept under. A flow that builds in partitions keeps them per partition, as `name@partition`. |
| `description` | text | none | What the dimensions are for. |
| `batch` | text | none | The grouping label the pipeline is filed under (`welldb`). |
| `parameters` | map | none | Parameters a dimension's `query` uses as `{name}` tokens, each with `default`, `required` and `description`. `partition` cannot be declared: `{partition}` is always the partition the run builds in. |
| `partitions` | list | none | The partitions the flow builds in, each a literal `data-partition-id`. See [Partitions](#partitions). |
| `source` | map | required | The OSDU platform the dimensions are read from. See [source](#source). |
| `dimensions` | list | required | The dimensions, at least 1 and at most 100, names unique ignoring case. |
| `target` | map | none | `connection`: the module's database, as the pipelines reading the flow's tables and views name it. Required with `views`. See [Views](#views). |
| `views` | list | none | Views over the flow's dimension tables, at most 50. See [Views](#views). |
| `reliability` | map | delivery defaults | The HTTP settings a delivery flow takes ([delivery flow](delivery.md)): timeout, retry, rate limit, TLS, URL allowlist and response size. |
| `parallelism` | map | defaults | `recordsAtOnce` (default 8, at least 1): how many dimensions build at once, and how many requests each asks of the search at once (value ranges, label and attribute searches, cursors). It is written as on a [delivery flow](delivery.md#parallelism), so a flow's settings read alike whatever its kind. A dimension flow takes no other parallelism key. It was `parallelism.concurrency`, and `reliability.concurrency` before that: a document that still writes either is refused, naming `parallelism.recordsAtOnce`, and a delivery flow's setting written under `reliability` (`fanOut`, `batchRecords`) is refused as one to remove. |
| `incremental` | map | none | How a build loads the dimensions after their first full load. Left out, every build loads every dimension in full. See [Full and incremental loads](#full-and-incremental-loads). |
| `schedule`, `mode`, `lifecycle` | | none | SQLFlow's envelope keys ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). A schedule's `values` may name the partition (`partition: dev`) and the flow's parameters. |

### source

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | text | required | The platform's base URL, usually `${env:OSDU_URL}`. |
| `auth` | map | `type: none` | How requests authenticate, written as a delivery flow's `target.auth` is. Secrets are `${env:NAME}` or `${keyvault:NAME}` references only; a literal is refused when the flow is read. |
| `headers` | map | none | Headers sent with every request. `data-partition-id` here hard-codes the one partition the flow builds in; a flow that names `partitions` may not set it. A header that carries a credential holds a reference; a literal is refused when the flow is read. |
| `queryPath` | text | `/api/search/v2/query` | The search service's offset search: aggregations, counts, and the searches that read labels by id. |
| `searchPath` | text | `/api/search/v2/query_with_cursor` | The cursor search a build scans records through where an aggregation cannot answer. |
| `paging` | `cursor` or `createTime` | `cursor` | How a build's scans page: through `searchPath`, or through `queryPath` in the order each record was created, which holds nothing on the service between pages. See [Search paging](../concepts/search-paging.md). |
| `aggregationSize` | integer | 1000 | How many groups the platform's aggregation returns (its `AGGREGATION_SIZE`), 10 to 10,000. A range answered with fewer groups is taken as complete, so this must not be more than the platform's setting; less only costs requests. |

A path must start with `/` and hold no whitespace, query or fragment.

### Partitions

A flow names its partitions in one of three ways:

- `partitions: [dev, test]`: a run builds in the partition it names (`--set partition=test`), or, naming none, in the
  registry's default when the flow lists it, else in the flow's only partition.
- `source.headers.data-partition-id`: the flow builds in that one partition, and a run names none.
- Neither: the flow builds in every partition registered with the catalog, one per run, the one the run names or the
  registry's default.

A run builds in one partition; `*` is refused. A dimension's own `partitions` narrows it to some of the flow's
partitions, and a build elsewhere skips it. See [partitions](../concepts/partitions.md).

## A dimension

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | text | required | A letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. Unique in the flow ignoring case, and unique among the flows of the database, since the dimension's table is named after it alone (see [The dimension's table](#the-dimensions-table)). |
| `description` | text | none | What the dimension is for. |
| `kind` | text | required | The kind read, `authority:source:entityType:version`, wildcards per segment. |
| `query` | text | every record | A Lucene query narrowing the records read, with `{parameter}` and `{partition}` tokens. A token no parameter declares is refused. |
| `path` | text | required | The key: a property search can match exactly (see [Paths](#paths)). |
| `label` | path or list | none | Where a key's value is read: a path of the record the key names, or up to 3 paths, each but the last reading the reference the next record is found by. |
| `unlabelled` | text | none | The value of whatever is not read, at most 256 characters, for a dimension that reads a label or attributes: a key whose label is not read, an attribute a key has none of, and a collected attribute's records holding none of its values. |
| `attributes` | map | none | Further facts of each key, by name, at most 20. See [Labels and attributes](#labels-and-attributes). |
| `elements` | map | none | The objects of a nested array of each record, a row each with its fields. See [Elements](#elements). |
| `columns` | map | from the paths | `{ key: <name>, value: <name> }`: the names of the two columns of the dimension's table holding the key and its value. |
| `clean` | list | none | The steps each value is cleaned by, at most 20. See [Clean steps](#clean-steps). |
| `countRecords` | boolean | `false` | Count each value's records exactly, with one count of the search per value whose keys could share a record. Without it, a value's count is exact where a record holds the path once, and otherwise the sum of its keys' counts. |
| `maxValues` | integer | 1,000,000 | The most distinct keys a build reads, 1 to 5,000,000. A field holding more fails the build before it writes anything. |
| `partitions` | list | every partition of the flow | The flow's partitions this dimension is built in. Refused on a flow whose partition is its header's. |

### Paths

A path is segments of letters, digits and underscores separated by dots. It goes into a query unquoted.

- **A property of the record itself**, which the indexer maps the same way for every kind and needs no template: `id`,
  `kind`, `type`, `namespace`, `authority`, `source`, `version`, `createUser`, `modifyUser`, `createTime`,
  `modifyTime`, `collaborationId`, `acl.viewers`, `acl.owners`, `legal.legaltags`, `legal.otherRelevantDataCountries`,
  `legal.status`, `ancestry.parents`, `index.statusCode`, and one tag as `tags.<name>`.
- **A property of data** (`data.Curves.Mnemonic`), whose shape is read from the template of every kind the pattern
  matches in the partition, as the template folders on the flow's path give it. Each such kind needs its template in one
  of those folders ([template folders](../concepts/templates.md#template-folders)), and all of them must index the path
  the same way.

How the index stores the path decides how keys are read and compared: text through its `keyword` sub-field (the whole
value, up to 256 characters), a keyword, number, boolean or date as it is, and a property inside a nested array through
a nested aggregation, counted per object rather than per record. A number reads in its canonical form (`1.5`), a date as
`yyyy-MM-ddTHH:mm:ss.fffZ`. A string with a `date-time`, `date` or `time` format is read as a date, and one with an
`int32`, `integer` or `int64` format as a number. An object, a list of objects, an array of objects with no
`x-osdu-indexing` hint, a property without a type, and a nested array inside a nested array are refused, naming why and
what to read instead.

### Labels and attributes

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
- Every reference a step reads is followed, at most 20 a key; the first record reached that holds a non-empty value at
  the last path gives it. A label is cut at 1,024 characters, an attribute value at 256.
- A segment holding objects can filter them: `[Property=text]` keeps those whose property equals the text,
  `[Property*=text]` those whose property contains it ignoring case, `[Property$=text]` those whose property ends with it
  ignoring case. So the `Country` attribute above keeps, of every geopolitical entity a wellbore names, the one whose type
  is a country.
- A key that names no record, a record the search does not hold, and one holding nothing at the last path, have no label
  (or no such attribute); the build's notes count them and say why.
- A label or an attribute value that is itself a record reference is kept as the code its id ends with, decoded.

An attribute's name is a letter, then letters, digits and underscores, at most 64, unique ignoring case, none of
`value`, `keys`, `key`, `key_id`, `records`, `filter`, `label`, `id`, `partition`, and not the name of the dimension's
key or value column.

**Keeping a key, so tables join.** An attribute is written as settings to give it a `keep`:
`WellboreID: { path: data.WellboreID, keep: key }`. There are three:

| `keep` | Kept as | `dev:reference-data--UnitOfMeasure:us%2Fft:` becomes | Joins to |
| --- | --- | --- | --- |
| `value` (default) | As a value shows it: a record reference by the code its id ends with, its escapes decoded | `us/ft` | nothing; it is for reading |
| `key` | Exactly as the record holds it | `dev:reference-data--UnitOfMeasure:us%2Fft:` | the key column of a dimension keyed by the same path |
| `id` | A reference as the id of the record it names, its version (or the latest's trailing colon) taken off | `dev:reference-data--UnitOfMeasure:us%2Fft` | the key column of a dimension keyed by `id` |

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
field) is cut under `keep: value` and left out under `key` and `id`, since a key cut joins to nothing; the build's notes
count them.

**Collected attributes.** `{ collect: <path> }` collects the values of the dimension's own records instead of reading
the record a key names: `LoggingService: { collect: data.LoggingService }` on a dimension keyed by the well logs'
`data.WellboreID` gives each wellbore every logging service its logs name, with how many logs hold each. Picking a
collected value in a search finds the records holding it, not every record of the keys holding it. A build reads the
values the cheaper way: a read per value when there are few, or one pass over the records through the search cursor,
split into ranges read side by side. A collected path is a path search matches exactly, like the dimension's own. A
dimension collects one attribute: a second is refused, since two would pair values no record holds together.

#### The fields a label search returns

A build reads a label's and an attribute's records step by step. At each step it gathers the ids every label and
attribute reaches there, groups them by the entity type each id names, and asks the search's offset search
(`source.queryPath`) for each group in the kind `*:*:<entityType>:*`, with a query matching at most 500 of its ids exactly.
The search is asked to return only these fields, `id` first and the rest in ordinal order:

| Field | Why |
| --- | --- |
| `id` | Matches each record that comes back to the id it was asked by. |
| Each path read at this step from records of that entity type, its filters taken out | The value read, or the reference the next step follows. |
| For each segment that filters, the property the filter compares, under the path up to that segment | What the filter compares the objects by. |

So, for the [example](#example)'s `Wellbore` dimension, the first step's search over `master-data--Wellbore` returns `id`,
`data.CurrentOperatorID`, `data.FacilityName` and `data.GeoContexts.GeoPoliticalEntityID`, and the `Country` attribute's
second step, `data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName`, asks the
`master-data--GeoPoliticalEntity` search for `id`, `data.GeoPoliticalEntityName` and `data.GeoPoliticalEntityTypeID`.
Nothing else of a record comes back, however much it holds. A collected attribute and elements are not read by id: they
are read from the dimension's own records, as [Collected attributes](#labels-and-attributes) and [Elements](#elements)
say.

### Elements

`elements` makes a row of every object of a nested array, beside the key of the record holding it, with every field of
one object on its row: what a collected attribute cannot do, since it collects one value a row. On a dimension keyed by a
well log's `id`, `elements: { path: data.Curves, ... }` is a table of curves, each curve's mnemonic, unit and depths
together:

```yaml
- name: LogCurve
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  path: id
  label: data.Name
  columns: { key: WellLogID, value: WellLogName }
  elements:
    path: data.Curves
    fields:
      Mnemonic: Mnemonic
      CurveUnit: CurveUnit                          # m, gAPI: the code a person reads
      CurveUnitID: { path: CurveUnit, keep: id }    # joins dim_RefUnitOfMeasure, keyed by id
      TopDepth: TopDepth
      LogVersion: { path: LogVersion, up: 1 }       # the log's own, on every curve's row
```

| Key | Default | Meaning |
| --- | --- | --- |
| `path` | required | The array, from the record's root, written as a label's path is. Every array on the way is stepped into, so `data.Curves.Columns` makes a row of every object of every curve's `Columns`; any segment can filter the objects it holds (`data.GeoContexts[GeoTypeID$=:Field:]`). What it ends at is the element: an object, or a plain value. |
| `fields` | required | The columns, each under its name: a path inside the element (`CurveUnit`, `Quantity.Code`, `Values[Type=Top].Depth`), `@` for the element itself (an array of plain values), or settings. |
| `fields.<name>.keep` | `value` | As an attribute's `keep`. |
| `fields.<name>.up` | 0 | How many objects up the element's path the field is read from: 1 the object holding the array, as many as the path has segments for the record. Not with `@`. |
| `fields.<name>.many` | `first` | A path reaching several values in one element: `first`, the first that is not empty, or `join`, each once, in order, joined by `; `. |

The table gains an `element` column: each key's objects numbered from 1, in the order of the records' ids and then of
the objects in each record, so the same records number the same way build after build and a row keeps its `id`. A key
whose records hold no object is one row with no element; a view leaves such rows out with `where: element IS NOT NULL`.
A field keeps up to 4,000 characters, where an attribute keeps 256: no index holds a field, so a curve's description or a
list joined by `many: join` is kept whole. A field reaching no value is null on its row; a number reads
as written (`203.149`), a boolean as `true` or `false`; a null in the array is no element.

A build reads the elements in one pass over the dimension's records through the search cursor, asking only for the
fields read (and the properties the filters compare), cut into ranges of keys read side by side; at most 5,000,000
objects a build, past which it fails before writing. Attributes and fields together are at most 20 columns, field names
are unlike every attribute's and `element`, and a dimension with elements collects no attribute, since both make a row
per value.

### Clean steps

Each value is cleaned by its steps in order: the label when one was read, the `unlabelled` text for a key whose label was
not read, otherwise the key (for a key naming a record, its decoded code). The result is trimmed. A step is written as
its name, or as a one-key map with its settings:

| Step | What it does |
| --- | --- |
| `trim` | Removes white space at both ends. |
| `collapseSpaces` | Makes every run of white space one space. |
| `upper`, `lower` | Changes case by the invariant culture's rules. |
| `nfc`, `nfkc` | Unicode normalization form C, or KC (compatibility characters folded). |
| `foldSeparators` | Keeps letters and digits in order, makes every run of anything else one hyphen, and lower-cases: `Gamma_Ray (API)` is `gamma-ray-api`. |
| `replace: { pattern, with }` | Replaces a regular expression's matches; `$1` names a group, `with: ''` removes them. The pattern runs on .NET's non-backtracking engine, at most one second a value, so a backreference, lookaround or atomic group is refused when the document is read. |
| `map: <dictionary>` or `map: { dictionary, field, otherwise }` | Looks the value up in a [dictionary document](dictionary.md) (`dictionaries/<name>.yaml` above the flow), exactly or ignoring case when that finds one entry, and replaces it with the entry's value (`field` names which, for a dictionary of several fields). `otherwise` says what an unlisted value comes to: left out, it is kept; `~`, the key is left out of every value; a text, that text. |

A key is left out of every value, with the reason kept on it, when cleaning leaves nothing, when the value is longer
than 256 characters, when `map` leaves it out, or when a step fails on it. `sqlflow validate` finds and reads the
dictionary each `map` step names, in the nearest `dictionaries/` folder walking up from the flow file, and refuses a flow
whose dictionary is missing or does not load
(`<file>: dimension '<name>': Dictionary '<dictionary>' was not found under '../dictionaries'. Expected <dictionary>.yaml or <dictionary>.yml.`).
A build of a dimension whose dictionary cannot be read fails, and the other dimensions build.

## The dimension's table

Each dimension is one table in the module's database, `osdu.dim_<dimension>` (`osdu.dim_Wellbore`): the dimension's
name with anything not a letter, digit or underscore made an underscore. It is what a report, a query, a cascade of
drop-downs or a table of facts reads. A build makes it and keeps it; no line of the document asks for it, and no
migration creates it.

| Column | Holds |
| --- | --- |
| `id` | The row's number: an identity, the clustered primary key, and what a table of facts joins on. It stays the same for as long as the dimension holds the row. |
| `partition` | The partition the row was read in. A flow building in several partitions writes them all to this table. |
| `key_id` | The key's number, the same in every row of the key. |
| the key's column | The key exactly as the index holds it. |
| `key_hash` | The key's SHA-256 (`HASHBYTES('SHA2_256', ...)`, 32 bytes), what a build finds a key's rows by through `IX_key_hash` (`partition`, `key_hash`). |
| the value's column | The key's value. |
| `records` | The records of the row: those holding the collected value, or every record of the key. |
| `filter` | The search filter finding the key's records; null when no query can carry the key. |
| one per attribute | The attribute's value for the row, under the attribute's name; null where the key has none. |
| `element` | Only for a dimension that reads elements: the element's place among the key's, from 1, in the order of the records' ids and then of the objects in each record; one column per element field follows the attributes. |

A row is a key and the value it collects: a dimension with a collected attribute has a row for each value a key holds,
any other a row a key. A key left out by cleaning, and a key no build finds any more, is no row.

**Column names.** The key's column is named after the property `path` ends with (`data.WellboreID` is `WellboreID`);
the value's after the property the last `label` path ends with (`FacilityName`), or after the dimension's own name when
it reads no label (`CurveMnemonic` beside `Mnemonic`). Where the two would be the same ignoring case (a dimension
`Source` reading `data.Source` with no label), the value's column keeps the name and the key's takes `Key` at its end:
`Source` and `SourceKey`. `columns: { key, value }` names either one; it has to where the name would be one of `id`,
`partition`, `key_id`, `records` or `filter`, an attribute's name, or no column name at all. A name is a letter, then
letters, digits and underscores, at most 64; the key's column cannot be named `value` nor the value's `key`, the words
that ask for the two columns whatever they are named.

**The schema follows the document.** SQLFlow's [schema evolution](../../../../sqlflow/docs/reference/concepts/schema-evolution.md)
brings the table to the declaration before each write: an attribute added gets its column on the next build, and
nothing is dropped or narrowed. An attribute taken out keeps its column, emptied; a column somebody added is left alone.
When the key's or value's column is named otherwise (the path or label changed, or `columns` says so), the next build
renames it where it is, keeping every row and its `id`; a query naming the old column has to change with the flow, and a
name given under `columns` stays through a change of path or label. A build writes only the rows that differ, in the
transaction that writes the rest of the dimension. When the collected attribute is added, taken out or replaced, the
partition's rows are written again under new numbers.

The table is read through two indexes the build makes: `IX_key` (`partition`, `key_id`) and `IX_value` (`partition`, the
value's column, `id`).

## Views

A view puts dimensions of one flow side by side at the grain of one of them, so a pipeline, a report or a person reads
one table instead of writing the joins. A build writes each as `osdu.v_dim_<name>` after the flow's dimensions, checks it,
and drops a view the flow no longer declares. The `target` and `views` keys, how a view joins and types its columns, how
it is written and checked, and what the templates say of its joins are on [Dimension views](dimension-views.md).

## Full and incremental loads

Without an `incremental` block every build loads every dimension in full (see [What a build does](#what-a-build-does)).
With one, a build reads only what changed since the dimension was last read, and loads in full only when it has to:

```yaml
incremental:
  keyColumns: [id]                        # required: the properties that identify one record
  dateColumns: [modifyTime, createTime]   # when a record last changed: the first it holds
  lagMinutes: 5
  fullLoadAfterHours: 168                 # a full load at least once a week
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `keyColumns` | list | required | The properties whose values together identify one record: `[id]` for an OSDU record. A full load keeps the keys each record holds by them, so an incremental load reads again what a changed record held before. Each a property a record holds once, 1 to 3, none twice. |
| `dateColumns` | list | `[modifyTime, createTime]` | When a record last changed: the first of these dates it holds. OSDU writes `createTime` alone when it creates a record and `modifyTime` only from its second version on (or when its ACL, legal tags or tags are patched), so a record never modified is read by its `createTime`. Each a date a record holds once, 1 to 3, none twice. |
| `lagMinutes` | integer | 5 | How far behind now a window ends, so records the indexer has not caught up with are read by the next build; a window also begins this far before the last one ended. 0 to 1440. |
| `fullLoadAfterHours` | integer | none | A build loads a dimension in full when its last full load completed more than this many hours ago. 1 to 8784. Left out, a dimension is loaded in full only when a run asks or a build has to. |
| `fullLoad` | boolean | `false` | Every build loads in full, as without the block, keeping its settings. |

A build of such a flow loads each dimension **in full** when the dimension has not been loaded in the partition; when its
last full load kept no record of the keys each record holds, or kept them by other `keyColumns`; when its declaration or
its query changed since its last full load; when its last full load is older than `fullLoadAfterHours`; when the run asks
for one (SQLFlow's `fullLoad`, `--full`) or the block says `fullLoad: true`; and when an incremental load finds more than
a quarter of the dimension's records or keys changed, or more than 100,000 records of one type its labels are read
through, so reading every key costs less. A full load of such a flow also reads every record once more, its unique key
and the keys it holds, and keeps them (`osdu.DimensionRecord`); a flow without the block keeps none.

Otherwise it loads **incrementally**, over the window from the lag before where the dimension's completed builds have
read up to, to the build's start less the lag:

1. **The records that changed** in the window, by when each last changed (`(modifyTime:[from TO to} OR (createTime:[from
   TO to} AND NOT _exists_:modifyTime))` with the default `dateColumns`), each read with its unique key and the keys it
   holds now; and the keys those records held when a load last read them, by their unique keys.
2. **The records a label or attribute was read through** that changed in the window, by OSDU's own `modifyTime` and
   `createTime`, in the entity types the dimension's keys were read through (`osdu.DimensionKeyRecord`); and the keys
   read through them.
3. **Every key of either, read again whole** over the dimension's query: its count (a query's worth of keys at a time,
   each group's records found by its filter), its label and attributes, its collected values and its elements. A key
   read again that no record holds is removed, as a full load removes one. The values those keys were and are under are
   laid out again from all their keys, and one left with none is removed.
4. **The write** holds those keys and values only: every other key stays as the last load left it, with its count, label
   and attributes. A build whose window held no change completes with nothing written.

A record that left the index (a soft delete) cannot be seen by any search, so an incremental load cannot see it: the keys
only it held, and the counts it made, stay until the next full load. An incremental load says how many at least have
left: `At least <n> record(s) the last full load read (<time>) are gone from the index or no longer match the query; ...`.
A run can name the window an incremental load reads with SQLFlow's backfill window (`--from`, `--to`): it ends no later
than the build's start less the lag, and the next build still reads on from the latest any build read up to, so a window
named for the past moves nothing back. A flow without an `incremental` block refuses a window.

Every build records how it loaded (`full` or `incremental`), its window, the records it found changed and the keys it read
again, and a plan says how a build started now would load each dimension and how many records changed in its window.

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Loads each dimension, in full or incrementally as the flow's `incremental` block says ([Full and incremental loads](#full-and-incremental-loads)): reads its keys, their labels and attributes, cleans keys into values, and keeps what changed. |
| `plan` | Settles each dimension's field from the templates, counts the records it would read, says how a build would load it (and, loading incrementally, the records that changed in its window), and gives each view's statement and what would stop a build writing it; reads no value and keeps nothing. |

The run payload takes one field, `dimensions`, the names of the dimensions to build; a run naming none builds every
one. A name the flow does not declare fails the run, listing the flow's dimensions. Anything else in the payload is
refused before the run starts, for example
`payload tests does not apply to a dimension flow: only an assertion flow's runs select tests; a dimension flow's payload names only dimensions.`
SQLFlow's `fullLoad` (`--full`) makes a build load every dimension in full, and its backfill window (`--from`, `--to`)
names the window a flow with an `incremental` block reads; its other flags (`--files`, `--filter`, ...) are refused.

```bash
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --payload '{"dimensions":["Wellbore"]}'
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --operation plan
```

Both operations read the templates from the template folders on the flow's path, and a build keeps the version of each
it read. See [running an OSDU flow](../cli/run.md) for the run options.

## What a build does

For each dimension, in the run's partition:

1. **Kinds and the field.** For a `data` path, one aggregation by `kind` lists the concrete kinds the pattern and query
   match, and the template of each, the one version the nearest template folder holding that kind holds, says how the
   index stores the path. A kind no folder on the flow's path holds fails the dimension. A partition holding no record of
   the kind leaves the dimension with no value and says so.
2. **Every distinct key.** The search aggregates the field over the whole query. A range answered with fewer groups
   than `aggregationSize` is complete; one answered with that many is split at a key it returned and each half is asked
   again, until every range answers whole. A range that cannot be split is read record by record through the search
   cursor. Ranges are asked `recordsAtOnce` at a time. A text value over 256 characters is not in the `keyword` field and
   is counted, never a key; a key over 1,024 characters is counted and left out.
3. **Labels, attributes and collected values**, read side by side when the dimension has both.
4. **Values and filters.** Each key is cleaned into its value. A key's filter is written the way the index holds the
   field; a value's filter holds at most 500 keys a query, and a value with more has its filter in parts. A key no query
   can carry (a control character, `nested(`, or inside a nested array a value the service rewrites) stays under its
   value, marked unfilterable.
5. **The write**, in one transaction under a lock per dimension: new keys and values are added, changed ones rewritten,
   what the build no longer found marked removed (keeping its id should a later build find it again), every key that
   arrived, left, moved or came back logged, and the dimension's table brought to the same rows. The write has no time
   limit: it grows with the dimension (one of well log curves stages a row for every field of every curve), so it takes
   as long as its rows do, and cancelling the run stops it with nothing written.

Dimensions build `recordsAtOnce` at a time. A dimension that fails keeps what the build before it wrote, and the run ends
failed with every other dimension built. A second build of the same dimension waits for the first's lock and fails after
two minutes: `Another build of dimension <id> held its write lock for more than 120 seconds (sp_getapplock answered ...),
so this build wrote nothing. Build it again when the other has finished.`

A build reads the index as it stands: a record written seconds before may not be in it yet. When the counts read do not
add up to the records holding a value, the build keeps what it read and says so in its notes.

## What a run returns

A build's result (`run.json`, the run page) is the flow, the partition, how many dimensions were built, failed and
skipped (not built in this partition), and per dimension its status (`completed`, `failed` or `skipped`), build number,
values, keys, keys of no value, unfilterable keys, labelled keys, what it changed (values and keys added, removed,
restored, keys moved), requests, the field it aggregated, how it loaded (`load`: `full` or `incremental`, the window
`windowFrom` and `windowTo`, `changedRecords` and `touchedKeys`), its error, and its first 5 notes. Every note is on the build in
the ledger (`sqlflow dimensions history`). A plan's result is, per dimension, the query, the field it would read,
whether a record holds it more than once, the records it would read, the kinds with their records and templates, and
what stops it, and how a build would load it (`load`, `fullBecause`, `windowFrom`, `windowTo`, `changedRecords`). Both say, per view, what the build did or would do: a build's `views` give each view's status
(`written`, `unchanged` or `failed`), its check (`passed` or `failed`), its rows and its first notes, and `viewsDropped`
the views it dropped; a plan's `views` give each view's tables, its statement, what stops it and what a build will do. A
view that cannot be written or read fails the run, every dimension still built.

## The blueprint and the tables behind a dimension

How a dimension is built, from each line of its YAML to the columns it fills, is what its
[blueprint](../concepts/dimension-blueprint.md) answers and the Definition tab draws. The module's tables that hold
a dimension's keys, values, attributes, builds and change log, and how its `dim_` table and `v_dim_` views are laid
out from them, are on [the tables a dimension keeps](../concepts/dimension-tables.md).

## Removing a dimension

A dimension taken out of the flow is built no more and keeps what its last build wrote; declaring it again brings its
ids and history back. An admin removes it for good with **Remove** on the GUI, `DELETE /api/v1/delivery/dimensions/{id}`,
or [`sqlflow dimensions remove`](../cli/dimensions.md#remove). A removal is refused for a dimension the flow still
declares and for one a cache flow of its partition captures, takes everything of the dimension in its partition (its
rows in its table, which is dropped when no partition writes it any more), and is recorded as a `remove-dimension`
activity of the flow with who asked. A dimension a view reads is refused too, naming the view.

A view the flow no longer declares is dropped by its next build. One whose flow is gone is removed by an admin with
**Remove** on the view's page, `DELETE /api/v1/delivery/dimensions/views/{name}`, or
[`sqlflow dimensions remove-view`](../cli/dimensions.md), recorded as a `remove-view` activity.

## Lineage, the GUI and the cache

- **Lineage.** A dimension flow reads each dimension's kind on its platform and partition, the dictionary files its
  `map` steps name, and writes each dimension. It is ordered after the delivery flows writing the kinds it reads, and a
  cache flow holding one of its dimensions is ordered after it ([lineage](../concepts/lineage.md)). With
  `target.connection` it also writes its tables and views on that connection ([Views](#views)).
- **The GUI.** A dimension flow's pipeline has a **Dimensions** tab. **OSDU**, **In OSDU**, **Dimensions** lists every
  dimension with the search builder; a dimension's page has the tabs Table, Values, Keys, Changes, Builds and
  Definition (the YAML beside a diagram of how each column is read: [The blueprint](../concepts/dimension-blueprint.md)).
- **The cache.** A [cache flow](cache.md) can hold a dimension's values as a lookup table (`dimension:` and
  `dimensionFlow:` in a cache type), so a mapping turns a raw key into its value as the dimension does.

## Errors when the document is read

| Message (abridged) | Cause |
| --- | --- |
| `dimensions must list at least one dimension; a dimension flow without dimensions has nothing to build.` | No `dimensions`. |
| `dimensions[1] 'Well-Type' would share its table with 'Well.Type': a table's name keeps the letters, digits and underscores of a dimension's name, and theirs are the same. Rename one of them.` | Two names that make the same table name. |
| `path legal.foo is not a value the index holds of the record; under legal it holds legal.legaltags, legal.otherRelevantDataCountries, legal.status.` | A record property the indexer does not map. |
| `the key's column would be named 'id', after the property its path ends with, which is a column every dimension's table has already (id, partition, key_id, records, filter). Name it in the document: columns: { key: <name> }.` | `path: id` without `columns.key`. |
| `the value's column would be named 'FacilityName', after the property its label ends with, which is the name of its attribute FacilityName, ...` | An attribute named like a column. |
| `unlabelled is the value of a key whose label or attribute is not read, and the dimension reads neither.` | `unlabelled` without `label` or `attributes`. |
| `attributes.B is a second collected attribute beside A; a dimension collects one, ...` | Two `collect` attributes. |
| `label reads through 4 records; a label reads through at most 3, a search per step for every key.` | Too many steps. |
| `clean[0]: the pattern /(a)\1/ cannot be used: it uses something the non-backtracking engine cannot run ...` | A pattern needing backtracking. |
| `query uses '{logSource}', which is not declared under parameters.` | An undeclared token. |
| `source.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it builds in. Remove the header.` | Both ways of naming a partition. |
| `source.aggregationSize is 20000; ... between 10 and 10000.` | Out of range. |
| `incremental names no keyColumns. An incremental load keeps the keys each record holds by the record's unique key, ... name it, keyColumns: [id] for an OSDU record.` | An `incremental` block without `keyColumns`. |
| `incremental.keyColumns names 4 properties; it names from 1 to 3.` | Too many, or none. |
| `incremental.dateColumns names modifyTime twice.` | A column named twice. |
| `incremental.lagMinutes is -1; ... from 0 to 1440 minutes.` | Out of range. |
| `incremental.fullLoadAfterHours is 0; ... from 1 to 8784. ...` | Out of range. |
| `views needs target.connection: the module's database as the pipelines reading the views name it ...` | Views without `target.connection`. |
| `views[0] 'Curve': join[0].on 'Mnemonic' holds no key: Mnemonic is a field kept as a value ...` | A join on a column holding no key. |
| `views[0] 'Curve': join[0] joins 'WellLogID' to RefUnitOfMeasure, and it holds the id of a work-product-component--WellLog and the dimension is keyed by the id of a reference-data--UnitOfMeasure, ...` | Keys that never meet. |
| `views[0] 'Curve': join[0] joins Wellbore, whose table holds a row per value it collects of each key, so a join to it would repeat the view's rows. ...` | A join to a dimension of several rows a key. |
| `views[0] 'Curve': columns.Interval: the expression 'BaseDepth - TopDepth' gives the operator - text (BaseDepth), where it takes a number; ...` | An implicit conversion. |
| `views[0] 'Curve': columns.Curve: the expression '...' uses a subquery, which a view's expression may not: ...` | Something an expression may not use. |
| `views[0] 'Curve': columns.Depth: dataType converts to 'varchar(10)'; varchar and char would turn every character outside their code page into ? ...` | A type a view does not convert to. |

## Errors when a dimension builds

| Message (abridged) | What to do |
| --- | --- |
| `Dimension Wellbore reads data.WellboreID of <pattern>, and the flow reads no template of <kind> (<n> record(s)), so how the index stores the field there is not known: no template folder on the path of ... holds template <kind>. ...` | Put the template of every kind the pattern matches in a template folder on the flow's path (`sqlflow template capture --kind <kind> --out <folder>/<file>.json`), or narrow `kind`. |
| `Dimension <name> reads <path>, which the kinds its pattern matches index differently: ... One field is read one way; narrow the kind to the versions that agree.` | Narrow `kind`. |
| `<path> holds more than <n> distinct values, the most this dimension keeps (maxValues). Narrow the dimension's query, or raise maxValues if the partition really holds that many.` | Narrow or raise `maxValues`. |
| `Dimension <name> of <flow> would write the table osdu.dim_<name>, which dimension <name> of <other flow> writes: ... Rename one of them.` | Dimension names are unique across the flows of a database. |
| `Dimension <name> could not read dictionary <dictionary>: ...` | Add or fix `dictionaries/<name>.yaml`. |
| `The dimension's table osdu.dim_<name> has a column <name> already ... Drop that column, or name the <role>'s column otherwise in the flow: columns: { <role>: <name> }.` | A rename would take a name another column holds. |
| `Dimension flow '<flow>': target.connection ${env:<NAME>} reaches another database than this host's module database, ... Point it at the module's database.` | Point the reference at the module's database. Nothing was built. |
| `View <name> is declared by dimension flow '<other>' as well, and a view's name is unique among the flows of a database, so osdu.v_dim_<name> was not written. ...` | Rename one of the views, or remove the other flow's. |
| `osdu.v_dim_<name> is in the database, and no build of a dimension flow made it, so it is not written over. ...` | Drop the object, or name the view otherwise. |
| `Column <column> of view <name> could not be computed for every row of partition '<partition>': Arithmetic overflow ...` | Write the expression around the value. |
| `incremental.keyColumns of dimension flow '<flow>', for dimension <name>, names <path>, which a record of <kind> can hold more than once, so it cannot stand for one record. ...` | Name properties each record holds once, such as `id`. |
| `incremental.dateColumns of dimension flow '<flow>' names <path>, which the index holds as text, not a date, so no window can be read on it. ...` | Name dates, such as `modifyTime` and `createTime`. |
| `Dimension flow '<flow>' declares no incremental block, so every build loads its dimensions in full and a backfill window has nothing to bound. ...` | Declare `incremental`, or run without `--from` and `--to`. |
| `The records of dimension <name> hold more than 20,000,000 keys between them, more than a load keeps by record for incremental loads. ...` | Narrow its query, split it, or take out `incremental`. |
