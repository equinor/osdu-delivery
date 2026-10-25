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
  - "many: max"
  - "decode: url"
  - lookupKinds
  - "nulls: keep"
  - fullLoadWhenRecordsLeave
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
  - delivery-flow-dimension-attributes
  - delivery-flow-dimension-elements
  - delivery-flow-dimension-clean
  - delivery-flow-dimension-table
  - delivery-flow-dimension-incremental
  - delivery-flow-dimension-build
  - delivery-flow-dimension-errors
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
| `parameters` | map | none | Parameters a dimension's `query` uses as `{name}` tokens, each with `default`, `required` and `description`. `partition` cannot be declared: `{partition}` is always the partition the run builds in. A parameter may list its `choices` ([parameters](parameters.md)). |
| `partitions` | list | none | The partitions the flow builds in, each a literal `data-partition-id`. See [Partitions](#partitions). |
| `source` | map | required | The OSDU platform the dimensions are read from. See [source](#source). |
| `dimensions` | list | required | The dimensions, at least 1 and at most 100, names unique ignoring case. |
| `target` | map | none | `connection`: the module's database, as the pipelines reading the flow's tables and views name it. Required with `views`. See [Views](#views). |
| `views` | list | none | Views over the flow's dimension tables, at most 50. See [Views](#views). |
| `reliability` | map | delivery defaults | The HTTP settings a delivery flow takes ([delivery flow](delivery.md)): timeout, retry, rate limit, TLS, URL allowlist and response size. |
| `parallelism` | map | defaults | `recordsAtOnce` (default 8, at least 1): how many dimensions build at once, and how many requests each asks of the search at once (value ranges, label and attribute searches, cursors). It is written as on a [delivery flow](delivery.md#parallelism), so a flow's settings read alike whatever its kind. A dimension flow takes no other parallelism key. It was `parallelism.concurrency`, and `reliability.concurrency` before that: a document that still writes either is refused, naming `parallelism.recordsAtOnce`, and a delivery flow's setting written under `reliability` (`fanOut`, `batchRecords`) is refused as one to remove. |
| `incremental` | map | none | How a build loads the dimensions after their first full load. Left out, every build loads every dimension in full. See [Full and incremental loads](dimension-incremental.md). |
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
| `name` | text | required | A letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. Unique in the flow ignoring case, and unique among the flows of the database, since the dimension's table is named after it alone (see [The dimension's table](dimension-table.md)). |
| `description` | text | none | What the dimension is for. |
| `kind` | text | required | The kind read, `authority:source:entityType:version`, wildcards per segment. |
| `query` | text | every record | A Lucene query narrowing the records read, with `{parameter}` and `{partition}` tokens. A token no parameter declares is refused. |
| `path` | text | required | The key: a property search can match exactly (see [Paths](#paths)). |
| `label` | path or list | none | Where a key's value is read: a path of the record the key names, or up to 3 paths, each but the last reading the reference the next record is found by. |
| `unlabelled` | text | none | The value of whatever is not read, at most 256 characters, for a dimension that reads a label or attributes: a key whose label is not read, an attribute a key has none of, and a collected attribute's records holding none of its values. |
| `attributes` | map | none | Further facts of each key, by name, at most 64 with the element fields. See [Labels and attributes](dimension-attributes.md). |
| `elements` | map | none | The objects of a nested array of each record, a row each with its fields. See [Elements](dimension-elements.md). |
| `columns` | map | from the paths | `{ key: <name>, value: <name> }`: the names of the two columns of the dimension's table holding the key and its value. |
| `clean` | list | none | The steps each value is cleaned by, at most 20. See [Clean steps](dimension-clean.md). |
| `countRecords` | boolean | `false` | Count each value's records exactly, with one count of the search per value whose keys could share a record. Without it, a value's count is exact where a record holds the path once, and otherwise the sum of its keys' counts. |
| `maxValues` | integer | 1,000,000 | The most distinct keys a build reads, 1 to 5,000,000. A field holding more fails the build before it writes anything. |
| `partitions` | list | every partition of the flow | The flow's partitions this dimension is built in. Refused on a flow whose partition is its header's. |
| `lookupKinds` | list | every kind of each type | The kind the records of an entity type are looked up in when a label or an attribute reads through a reference to one, one per entity type (`[osdu:wks:master-data--Basin:1.2.0]`). See [The kinds references are looked up in](dimension-attributes.md#the-kinds-references-are-looked-up-in). |

## Paths

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

## The pages of a dimension flow

| Page | What it holds |
| --- | --- |
| [Labels and attributes](dimension-attributes.md) | How a label and an attribute read the records a key names: steps and filters, keeping a key so tables join, several values, decoding, white space, filters through a reference, lookup kinds, collected attributes and the fields a label search returns. |
| [Elements](dimension-elements.md) | The objects of a nested array, a row each with their fields. |
| [Clean steps](dimension-clean.md) | How each value is cleaned into what a person picks. |
| [The dimension's table](dimension-table.md) | `osdu.dim_<dimension>`: its columns, their names, and how the table follows the document. |
| [Views](dimension-views.md) | Dimensions joined into one table, grouped or a row per row, typed, written and checked. |
| [Incremental loads](dimension-incremental.md) | When a build loads in full, and what an incremental one reads. |
| [Builds](dimension-build.md) | The operations a run takes, what a build does, and what a run returns. |
| [Errors](dimension-errors.md) | What is refused when the document is read, and what fails a build. |

## Views

A view puts dimensions of one flow side by side at the grain of one of them, so a pipeline, a report or a person reads
one table instead of writing the joins. A build writes each as `osdu.v_dim_<name>` after the flow's dimensions, checks it,
and drops a view the flow no longer declares. The `target` and `views` keys, how a view joins and types its columns, how
it is written and checked, and what the templates say of its joins are on [Dimension views](dimension-views.md).

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
  `target.connection` it also writes its tables and views on that connection ([Views](#views)), and its pipeline names
  that connection as its target, as an ingestion flow's names the server it loads.
- **The GUI.** A dimension flow's pipeline has a **Dimensions** tab. **OSDU**, **In OSDU**, **Dimensions** lists every
  dimension with the search builder; a dimension's page has the tabs Table, Values, Keys, Changes, Builds and
  Definition (the YAML beside a diagram of how each column is read: [The blueprint](../concepts/dimension-blueprint.md)).
- **The cache.** A [cache flow](cache.md) can hold a dimension's values as a lookup table (`dimension:` and
  `dimensionFlow:` in a cache type), so a mapping turns a raw key into its value as the dimension does.
