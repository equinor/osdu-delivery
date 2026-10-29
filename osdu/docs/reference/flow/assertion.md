---
id: delivery-flow-assertion
title: "Assertion flow (flowType: assertion): data quality tests of what an OSDU partition holds"
type: flow-reference
summary: "Tests of what OSDU holds once data has landed: counts, field values, references, schema conformance, legal tags, the delivery ledger and bulk data."
keywords:
  - source.paging
  - createTime paging
  - assertion flow
  - data quality tests
  - test osdu records
  - assert
  - count
  - field condition
  - resolves
  - conforms
  - delivered
  - bulk data checks
  - severity
  - failrunon
  - indexsettleseconds
  - test report
  - bulk.columns
  - mapping assertions
  - "mapping: name@version"
  - knownValues
  - aggregate except
  - warnWithin
  - warning band
  - template of a mapping
  - "template: Name@version"
  - acceptable difference
  - within 10 percent warns
  - disabled test
  - asOf
  - validUntil
  - validFor
  - asOfField
  - needs new proof values
  - proof values expire
  - restate proof values
  - validity period of a test
yamlPath: "(root, flowType: assertion)"
related:
  - delivery-concept-search-paging
  - delivery-guide-data-quality-tests
  - delivery-cli-assertions
  - delivery-flow-mapping-assertions
  - delivery-flow-known-values
  - delivery-flow-delivery
  - delivery-concept-templates
  - delivery-concept-partitions
  - delivery-concept-ledger
  - flow-ing-quality
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Model/DdmsCatalog.cs
  - osdu/src/SqlFlow.Delivery/Engine/AssertionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/TestEvaluator.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/Evaluators.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/BulkEvaluators.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionQueries.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/ValueComparer.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/IndexSettling.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/WellboreBulk.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/TestResult.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionReport.cs
  - osdu/src/SqlFlow.Delivery/Engine/SyncedMappings.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingAssertionJudge.cs
  - osdu/src/SqlFlow.Delivery/Documents/ConditionReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAssertionEndpoints.cs
  - osdu/specs/core/search/openapi.yaml
  - osdu/specs/core/storage/openapi.yaml
  - osdu/specs/core/legal/openapi.yaml
---

# Assertion flow (flowType: assertion): data quality tests of what an OSDU partition holds

A `flowType: assertion` document holds tests of what an OSDU partition holds once your data has landed: that search
finds the records, that their values are the ones meant, that their references resolve, that they meet their schema,
that their legal tags are valid, that every record a delivery flow says it delivered is there, and that their bulk data
is in the Wellbore DDMS. Each test reads one OSDU kind in one version and holds what it reads to its assertions. Each run
keeps a report in the module's database, which the GUI, the API and `sqlflow assertions` read.

Use it after a [delivery flow](delivery.md), to check what users of OSDU see rather than what the delivery sent. A test
only reads: nothing is ever written to OSDU. SQLFlow's own `assertions:` on an `ing` flow
([data quality assertions](../../../../sqlflow/docs/reference/flow/ing-quality.md)) check a SQL table; an assertion flow
checks OSDU.

## Example

The estate's well log tests, `flows/welldb-welllog-04-assertion.yaml`:

```yaml
flowType: assertion
name: welldb-welllog-04-assertion
batch: welldb
description: What the well logs of the well database look like in OSDU once they are delivered.

partitions: [dev, test]

parameters:
  system:
    default: welldb
    description: The data.Source value the delivered records carry.

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
  ddmsRoot: /api/os-wellbore-ddms

defaults:
  maxRecords: 10000
  examples: 20

failRunOn: error

reliability:
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }

parallelism:
  recordsAtOnce: 4

tests:
  - name: logs-delivered
    description: Every log the delivery flow holds as delivered is in the search index, indexed cleanly.
    tags: [smoke]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    read: index
    assert:
      - count: { atLeast: 1 }
      - delivered: welldb-welllog-03-delivery
      - indexed: true

  - name: log-headers
    description: Each log header meets the WellLog schema, and its references resolve.
    tags: [smoke]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    assert:
      - conforms: true
      - field: data.WellboreID
        resolves: master-data--Wellbore
      - field: data.Curves.CurveUnit
        resolves: reference-data--UnitOfMeasure
        severity: warning
      - name: a depth curve is among the curves
        field: data.Curves.Mnemonic
        in: [MD, DEPT]
        values: any
      - field: acl.viewers
        length: { atLeast: 1 }
      - legal: valid
      - unique: [data.WellboreID, data.Name]
        severity: warning
      - aggregate: missing
        field: data.SamplingInterval
        equals: 0
        severity: info

  - name: log-curves
    description: Each log's curve data is in the Wellbore DDMS, indexed by a depth that only increases.
    tags: [bulk]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    maxRecords: 200
    sample: true
    bulk: { columns: [MD], maxRows: 2000000 }
    assert:
      - rowCount: { atLeast: 1 }
      - columns: { includes: [MD] }
      - column: MD
        monotonic: strictlyIncreasing
      - column: MD
        exists: true
        for: 99%

  - name: log-names
    description: No delivered log is filed under a placeholder name.
    tags: [coverage]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    assert:
      - groupBy: data.Name
        absent: [UNKNOWN]
        groupCount: { atLeast: 1 }
```

`sqlflow validate` prints `OK  'welldb-welllog-04-assertion' is valid (assertion: ${env:OSDU_URL} -> report).`
The queries find the records by `data.Source`, which assumes the mappings write `Source: welldb` under `data`; narrow
by whatever your records carry. [Testing what OSDU holds](../guides/data-quality-tests.md) builds a flow like this step
by step.

## Keys reference

The document is read strictly: an unknown key fails validation naming it.

| Key | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | none | `assertion`. |
| `name` | string | yes | none | The flow's name: its pipeline identity, and the name its reports are kept under (`name@partition` for a flow that tests partitions). |
| `batch`, `description` | string | no | none | The SQLFlow batch, and free text. |
| `telemetry.operation` | string | no | `ast` | The flow's operation code in the correlation id every OSDU request of its runs carries, `osdu-delivery-<repository>-<operation>-<run id>`: a lower-case letter, then lower-case letters and digits, at most 16, no hyphen. See [request correlation](../concepts/request-correlation.md). |
| `partitions` | list | no | none | The partitions the flow tests, 1 to 64, each once (see [Which partition a run tests](#which-partition-a-run-tests)). |
| `parameters` | map | no | none | Named parameters (`required`, `default`, `description`), used as `{name}` tokens in queries, ids and the text a test compares with. `partition` is reserved: `{partition}` is always the partition the run tests. A parameter may list its `choices` ([parameters](parameters.md)). |
| `source` | map | yes | none | The platform and where its services are. |
| `defaults` | map | no | see below | What a test takes when it says nothing itself. |
| `failRunOn` | string | no | `error` | Which outcomes fail the platform run: `error`, `warning` or `never` (see [Outcomes](#outcomes)). |
| `tests` | list | yes | none | The tests, 1 to 500. |
| `reliability` | map | no | defaults | The HTTP settings, as on a [delivery flow](delivery.md): timeout, retry, rate limit, TLS, URL allowlist and response size. |
| `parallelism` | map | no | defaults | `recordsAtOnce`: how many tests run at once, and how many requests one test has in flight (default 8, at least 1). It is written as on a [delivery flow](delivery.md#parallelism), so a flow's settings read alike whatever its kind. An assertion flow fans nothing out across the fleet, so it takes no other parallelism key. It was `parallelism.concurrency`, and `reliability.concurrency` before that: a document that still writes either is refused, naming `parallelism.recordsAtOnce`, and a delivery flow's setting written under `reliability` (`fanOut`, `batchRecords`) is refused as one to remove. |
| `schedule`, `mode`, `lifecycle` | | no | none | SQLFlow's envelope keys: see [schedule](../../../../sqlflow/docs/reference/flow/schedule.md). |

### source

| Key | Default | Meaning |
| --- | --- | --- |
| `endpoint` | required | The platform's base URL, usually `${env:OSDU_URL}`: the one the delivery flows deliver to. |
| `auth` | `type: none` | Written as `target.auth` on a delivery flow. Secrets are references only (`${env:NAME}`, `${keyvault:NAME}`): a literal is refused when the flow is read. |
| `headers` | none | Headers sent with every request; `data-partition-id` only for a flow tested in one fixed partition. A header that carries a credential holds a reference; a literal is refused when the flow is read. |
| `queryPath` | `/api/search/v2/query` | The search that counts, groups and finds index errors. |
| `searchPath` | `/api/search/v2/query_with_cursor` | The cursor search records are paged through. |
| `paging` | `cursor` | How a test's records are paged: `cursor` through `searchPath`, or `createTime` through `queryPath` in the order each record was created, which holds nothing on the service between pages. A test that declares its own `sort` is read through the cursor either way. See [Search paging](../concepts/search-paging.md). |
| `recordQueryPath` | `/api/storage/v2/query/records` | Storage's read of records and references by id, 100 at a time. |
| `legalPath` | `/api/legal/v1` | The legal service, under which `/legaltags:validate` is asked (25 names a request). |
| `ddmsRoot` | `/api/os-wellbore-ddms` | Where the Wellbore DDMS is, for tests that read bulk data. |

A path starts with `/` and has no whitespace, query or fragment.

### defaults

| Key | Default | Range | Meaning |
| --- | --- | --- | --- |
| `maxRecords` | 10000 | 1 to 1,000,000 | The most records a test reads. |
| `examples` | 20 | 1 to 500 | The most failing records an assertion names. Counts are always exact. |
| `read` | `storage` | `storage`, `index` | Where a test reads its records. |
| `indexSettleSeconds` | 300 | 0 to 3600 | How long the index is given to list a change (see [Records the index may not list yet](#records-the-index-may-not-list-yet)). |

## Which partition a run tests

A run tests one partition and keeps its report under it. A flow says which partitions it tests in one of three ways:

| The flow | A run tests |
| --- | --- |
| `partitions: [dev, test]` | The partition the run names (`--set partition=test`, a schedule's `values: { partition: test }`, the GUI's title bar), which has to be one of them. With none named: the registry's default partition if the flow names it, else the flow's only partition. |
| Neither `partitions` nor a `data-partition-id` header | Any partition registered with the catalog: the one the run names, or the registry's default. |
| `source.headers: { data-partition-id: dev }` | That partition, always. A run that names a partition is refused. |

Partitions are written literally, never as references; `*` is not a partition, and a run asking for `*` is refused
(`Assertion flow '<name>' tests one partition per run, since a report describes one partition; name the partition to test instead of '*'.`).
A flow that names its partitions may not also name the header. A test's own `partitions` narrows it to some of the
flow's partitions; a run in another records it as `skipped`. See [Partitions](../concepts/partitions.md).

## Tests

| Key | Default | Meaning |
| --- | --- | --- |
| `name` | required | Unique in the flow, ignoring case: a letter or digit followed by letters, digits, `.`, `_` and `-`, at most 100 characters. Runs and reports name tests by it. |
| `description` | none | What the test checks, shown on the board and in reports. |
| `tags` | none | Up to 20 labels, written as names are. A run can select tests by tag. |
| `partitions` | all | The flow's partitions the test runs in. |
| `kind` | required | The one type the test reads, in one version: `authority:source:entityType:major.minor.patch`, as the delivering mapping's `template.kind` names it. Wildcards are refused. |
| `template` | the folder's | The template its fields are checked against, read from the [template folders](../concepts/templates.md#template-folders) on the flow's path. The mapping that renders the records, as `Name@version` (`template: WellLog@1.5.0`): the kind and version that mapping fills, read from the synced mapping each time the test is checked, so the test follows the mapping when it moves to another version. Or a template version itself (`template: 2f8a99cb38d32480`), as `sqlflow template folders` lists it, which a folder on the flow's path must hold. Left out, the one version of the kind the nearest template folder holding the kind holds. |
| `query` | none | A Lucene query narrowing the kind, with `{parameter}` and `{partition}` tokens. Left out, every record of the kind. |
| `ids` | none | 1 to 1,000 record ids to read from storage instead of searching, with tokens. Not with `query`, `spatial` or `sort`. |
| `spatial` | none | The search's `spatialFilter`: a geo `field` and exactly one of `byBoundingBox` (`topLeft`, `bottomRight`), `byDistance` (`point`, `distance` in metres), `byGeoPolygon` (`points`), `byIntersection` (`polygons`, each with `points`), `byWithinPolygon` (`points`); a point is `{ latitude, longitude }`. |
| `sort` | none | 1 to 5 `{ field, order: asc or desc }`: the order the search returns records in. |
| `read` | `defaults.read` | `storage`: each record as storage holds it, read by the ids the search finds. `index`: the search hits themselves, only what the index holds. |
| `maxRecords` | `defaults.maxRecords` | The most records the test reads; a test naming more ids than this is refused. |
| `sample` | `false` | When more records match than `maxRecords`: `false` evaluates no assertion over the records and says so; `true` evaluates the first `maxRecords` and marks the result as a sample. |
| `indexSettleSeconds` | `defaults.indexSettleSeconds` | This test's settle window. |
| `severity` | `error` | The severity of its assertions that state none: `error`, `warning` or `info`. |
| `bulk` | none | Read each record's bulk data: `columns` (the columns read; they include every column the assertions name) and `maxRows` (default 1,000,000, at most 100,000,000). See [Bulk data](#bulk-data). |
| `disabled` | none | Why the test is disabled. It stays in the flow and in every report, recorded as `skipped` with this reason, and reads nothing. See [Disabling a test, and the day its values hold until](#disabling-a-test-and-the-day-its-values-hold-until). |
| `asOf` | none | The day (an ISO 8601 date or date and time) the values the test expects were stated; every result quotes it beside what it expected. |
| `validFor` | none | How long after `asOf` those values hold (`90d`, `12w`, `6m`, `1y`); past it the test is not judged, and is flagged as needing new proof values. |
| `validUntil` | none | The last day (or instant) those values hold, in place of `validFor`. |
| `asOfField` | none | The date a record holds on or before `asOf` to be among the test's records: `createTime`, `modifyTime`, or a date of its data. See [Disabling a test, and the day its values hold until](#disabling-a-test-and-the-day-its-values-hold-until). |
| `assert` | required | The assertions, 1 to 100. |

A test first counts what its query matches (`POST /api/search/v2/query` with `trackTotalCount`), or reads its ids from
storage. Assertions that the search answers on its own (`count`, `groupBy`, `indexed`) read no record. When an assertion
needs the records, the test pages through them with a cursor and, reading `storage`, reads each page's ids back 100 at a
time; every record is handed to every assertion once, so memory holds a page, not the partition. A record the index
lists and storage does not return is left out and noted. An id storage does not hold, or a record of another kind than
the test's, is left out and noted too.

```yaml
  - name: wellbores-in-area
    kind: osdu:wks:master-data--Wellbore:1.3.0
    spatial:
      field: data.SpatialLocation.Wgs84Coordinates
      byBoundingBox:
        topLeft: { latitude: 31.0, longitude: -98.0 }
        bottomRight: { latitude: 29.0, longitude: -94.0 }
    assert:
      - count: { atLeast: 1, atMost: 5000 }

  - name: named-wellbores
    kind: osdu:wks:master-data--Wellbore:1.3.0
    ids:
      - "{partition}:master-data--Wellbore:1001"
      - "{partition}:master-data--Wellbore:1002"
    assert:
      - count: 2
      - field: data.FacilityName
        matches: '^[A-Z0-9 /-]+$'
```

## Assertions

An assertion has exactly one subject, the keys that subject takes, and optionally `name` (what reports call it, at most
200 characters; left out, a label read off what it asserts), `description`, `severity`, and `disabled`, `asOf`,
`validFor` and `validUntil`, which say of the assertion what a test's say of the test and take over from its test's. A key its subject
does not take fails validation: `<test>: assert[<n>] is a <subject> assertion, which does not take <keys>.`

| Subject | Takes | Holds when |
| --- | --- | --- |
| `count: <n or comparison>` | `warnWithin` | The number of records the test matches (the index's exact count, or for `ids` the records storage returns of the kind) compares true. |
| `field: <path>` | one condition, `for`, `values`, `optional`, `where`, `ignoreCase`, `tolerance` | The condition holds of the path's values in the records read, for the share `for` asks. |
| `aggregate: min, max, sum, avg, count, distinct, missing` | `field` (or `column`), comparison keys, `tolerance`, `warnWithin`, `except` | The aggregate of the field over every record read (or of the column over each record's rows, record by record) compares true. `except` lists values the aggregate leaves out, as if they were no value: a log's index curve `MD` among its curves, a null sentinel among a column's values. |
| `unique: [paths]` | | No two records read share the values of all the paths (1 to 20). Records holding none of them are passed over. |
| `groupBy: <path>` | `groups`, `mode`, `absent`, `groupCount` | The search's groups of the path's values (`aggregateBy`): each group under `groups` has a count that compares true (`mode: exact` allows no other group; `includes`, the default, does), no group under `absent` is there, and the number of groups compares true with `groupCount`. Reads no record. Not with `ids`. |
| `recordSet: { columns, rows, mode }` | | The records projected onto `columns` (1 to 20 paths) match `rows` (at most 1,000): `exact` (the same rows in any order, the default), `ordered` (in the test's `sort` order), `includes` or `excludes`. |
| `knownValues: { locateBy, check, rows, ... }` | | Every row finds one record by the full paths and values it is located by, and that record holds the row's proof values. Paths may reach the record a reference names and the records naming one. Not with `ids`. See [Known values](known-values.md). |
| `conforms: true` | | Every record read meets the schema of its kind as the template states it (required properties, types, formats, patterns, enumerations), and is of the test's kind. Needs `read: storage`. |
| `mapping: <Name@version>` | | Every record read meets what that mapping asserts of the values a record carries (its `$assert` on the record stage). Needs `read: storage`. See [Holding records to a mapping's assertions](#holding-records-to-a-mappings-assertions). |
| `indexed: true` | | No record the test matches has an index status of 201 or above (`index.statusCode:[201 TO *]`): the indexer mapped every record whole. Not with `ids`. |
| `legal: valid` | | Every record read carries a legal tag, and every tag is valid now, as Legal's `POST /legaltags:validate` answers. |
| `delivered: <delivery flow>` | `interface`, `exact` | Every record that flow's ledger holds as delivered in the partition is among what the test matches; with `exact: true`, nothing else is. |
| `rowCount: <n or comparison>` | | Each record's bulk data has that many rows. |
| `columns: { includes, excludes, equals }` | | Each record's bulk data has these columns, lacks those, or has exactly these (in any order). |
| `column: <name>` | one condition, `for`, `optional`, `where`, `ignoreCase`, `tolerance` | The condition holds of the column's values in each record's bulk data, for the share of its rows `for` asks. |
| `monotonic: increasing, decreasing, strictlyIncreasing, strictlyDecreasing` | `column` | The column's values only go that way, row after row, in each record's bulk data. Absent values are passed over. |

An assertion with no subject fails with `names no subject; an assertion is one of count, field, column, aggregate, unique, groupBy, recordSet, knownValues, conforms, mapping, indexed, legal, delivered, rowCount, columns or monotonic.`;
one with two fails with `names <a> and <b>; an assertion has one subject. Write one assertion for each.`

When there is nothing to check (no record matched, or none had a value), `field`, `column`, `resolves`, `unique`,
`conforms`, `mapping`, `legal` and the bulk assertions fail, except a `field` or `column` assertion with `for: none`. An `aggregate`
compares what it found (`count`, `distinct` and `missing` come to 0; `sum`, `avg`, `min` and `max` fail with no number),
and so do `recordSet` (an `exact` set with no rows asserts that no record matches) and `delivered`.

`delivered` reads the ledger of the delivery flow named (ignoring case) in the run's partition. A flow delivering several
interfaces needs `interface`. It cannot be judged on a sample, and errors when the ledger holds more delivered records
than the test's `maxRecords`.

### Comparisons of numbers: count, rowCount, groupCount, groups, length

These take a whole number (equals) or a mapping of `equals`, `notEquals`, `atLeast`, `atMost`, `greaterThan`,
`lessThan` and `between: [low, high]`, every one of which has to hold: `count: { atLeast: 1, atMost: 5000 }`. An
`aggregate` takes the same keys beside it (`aggregate: avg`, `field: ...`, `between: [0.1, 0.2]`); `min` and `max`
compare numbers or ISO 8601 dates, the others numbers.

### A warning band

A number that is a little off is often still acceptable, but worth a look: a count of curves that drifts as logs are
added, a depth a source rounds differently. `warnWithin` says how far a `count`, an `aggregate` or a known value's
proof value may miss what it is compared with and only warn: a distance (`warnWithin: 5`) or a percentage of each number
compared with (`warnWithin: 10%`).

```yaml
      - aggregate: count
        field: data.Curves.CurveID
        except: [MD]
        equals: 7842
        warnWithin: 10%
```

| What it comes to | `equals: 7842`, `warnWithin: 10%` | Outcome |
| --- | --- | --- |
| Within `tolerance` (exactly, without one) | 7842 | `passed` |
| Within the band | 7058 to 8626 | `failed` at severity `warning`, so its test is `warned` |
| Beyond the band | 7000 | `failed` at its own severity |

- The band widens each number its own way: an equality both ways, `atLeast` and `greaterThan` downwards, `atMost` and
  `lessThan` upwards, and each end of `between` outwards by its own share. `in` (on a proof value) is within the band of
  any of its values.
- A miss within the band weighs `warning`, or the assertion's own severity when that is milder: an `info` assertion stays
  `info`. A test whose only failures are warnings is `warned`, which `failRunOn: error` does not fail the run for.
- The result says by how much it missed:
  `count(data.Curves.CurveID) is 7600 (948 value(s) left out: "MD"); expected equals 7842 (warning within 10%), leaving out "MD"; 242 below 7842 (3.09%), within the warning band of 10%, so it warns.`
- A bulk-data `aggregate` of a `column` warns when every record that misses, misses within the band.
- `warnWithin` widens numbers only, so it is refused beside a date, beside `notEquals` and `notIn`, which have nothing
  to widen, at 0 or below, above 100%, and where it is no wider than the `tolerance` (a percentage of 0 is nothing). A
  `field`, `column`, `rowCount` or `groupBy` assertion does not take it.

### Conditions

A `field` or `column` assertion, and each `where` condition, takes exactly one of these operators:

| Operator | Operand | A value meets it when |
| --- | --- | --- |
| `equals`, `notEquals` | a value | It equals (or does not equal) the operand. |
| `in`, `notIn` | 1 to 1,000 values | It equals one of them (or none). |
| `atLeast`, `atMost`, `greaterThan`, `lessThan` | a number or text | It orders that way against the operand. |
| `between` | `[low, high]` | It lies between them, both included; both numbers, or both text (dates compare as instants), low first. |
| `matches`, `notMatches` | a .NET regular expression | Its text matches (or does not). One second at most per value; no tokens are substituted. |
| `startsWith`, `endsWith` | text | It is text starting (or ending) with it. |
| `contains`, `notContains` | a value | It is a list holding a value equal to the operand, or text containing it. |
| `exists` | `true` or `false` | The record (or row) has a value there at all, or has none. |
| `empty` | `true` or `false` | It is empty (null, empty text, an empty list or object), or not. |
| `type` | `string`, `number`, `integer`, `boolean`, `object`, `array`, `null` | It is of that JSON type; `integer` is a number without a fraction. |
| `length` | a number or a comparison | Its text length, or its list's item count, compares true. |
| `resolves` | `true`, or an entity type such as `master-data--Wellbore` | It is a record id (`partition:entity-type:id`, with or without a version) storage holds, of that entity type when one is named. Fields only, and not in `where`. |

How values compare:

- A number compares as a number, exactly as written unless `tolerance` is given, when it may be that far off. Text
  holding a number equals that number, and text `true` or `false` that boolean, since OSDU keeps some values as text.
- Text compares ordinally, or ignoring case with `ignoreCase: true`. Two ISO 8601 dates compare as the instants they
  name, so `greaterThan: "2026-01-01T00:00:00Z"` orders dates.
- A list or an object never equals a single value: test each item with `[*]`, or use `contains`.
- `ignoreCase` applies to `equals`, `notEquals`, `in`, `notIn`, `matches`, `notMatches`, `startsWith`, `endsWith`,
  `contains` and `notContains`; `tolerance` to the equalities, `in`, `notIn`, the orderings and `between`, against a
  number.
- `{parameter}` and `{partition}` tokens are substituted in the text a condition compares with, in `recordSet` rows and
  in group names, never in a regular expression. A `recordSet` compares values exactly as JSON: `5` and `"5"` differ there.

What decides a record:

| Key | Default | Meaning |
| --- | --- | --- |
| `for` | `all` | The share of records (rows, for a column) the condition has to hold for: `all`, `any`, `none`, or a percentage such as `99%`. `all`, `any` and a percentage fail when nothing was checked. |
| `values` | `all` | For a path yielding several values in one record: `all` holds the record when every value meets the condition, `any` when one does. Fields only. |
| `optional` | `false` | `true` passes over a record (or row) with no value at all, instead of failing it. |
| `where` | none | 1 to 10 conditions, each with its own `field` (or `column`) and operator, selecting the records (or rows) looked at: all of them have to hold, and one holds when one of its values meets it. |

`resolves` takes no `for` or `values`: every reference the records carry is looked up (100 a request, at most 200,000
distinct references per assertion).

```yaml
      - field: data.TopMeasuredDepth
        atLeast: 0
        where:
          - field: data.ReferenceCurveID
            equals: MD
      - field: data.Curves.Mnemonic
        equals: gr
        ignoreCase: true
        values: any
        for: 95%
      - field: data.Name
        equals: UNKNOWN
        for: none
      - aggregate: max
        field: modifyTime
        atLeast: "2026-01-01T00:00:00Z"
```

### Paths

A path is dotted from the record's root, which is one of `id`, `kind`, `version`, `acl`, `legal`, `data`, `tags`,
`ancestry`, `meta`, `createTime`, `createUser`, `modifyTime` and `modifyUser`. Arrays are crossed implicitly
(`data.Curves.Mnemonic` is every curve's mnemonic), or with `[*]` or `[n]`. A null is no value.

## Bulk data

A test with `bulk` reads each record's bulk data from the Wellbore DDMS
(`GET {ddmsRoot}/ddms/v3/{collection}/{id}/data`, first `?describe=true` for its columns and rows, then the rows a page
at a time in JSON). Only kinds the DDMS keeps bulk data for take it:

| Entity type | Collection |
| --- | --- |
| `work-product-component--WellLog` | `welllogs` |
| `work-product-component--WellboreTrajectory` | `wellboretrajectories` |
| `work-product-component--PPFGDataset` | `ppfgdataset` |
| `work-product-component--WellPressureTestRawMeasurement` | `wellpressuretestrawmeasurement` |

`rowCount`, `columns`, `column`, a column `aggregate` and `monotonic` need it. Each record's bulk data is held to each
assertion on its own, records in parallel up to `parallelism.recordsAtOnce`, and the assertion holds when every record's
does. A record the DDMS holds no bulk data for (it answers 404) fails, so does one lacking a column an assertion names,
and so does one the DDMS answers a page of without a column the read asked for:
`the DDMS answered the page of its bulk data from row <n> without the column <c>, which the read asked for and its description lists`.
One holding more rows than `bulk.maxRows` fails the assertions that read its rows, unless the test sets `sample: true`,
which reads the first `maxRows` rows.
`NaN` and infinite values read as no value.

`bulk.columns` (no commas, at most 256 characters each) is the columns read. Left out, the read takes exactly the
columns the assertions name: a `column` assertion's own column and its `where` columns, a column `aggregate`'s, a
`monotonic`'s. Listed, only those are read, so the list must include every one of them; a list that leaves one out is
refused when the flow loads. The example's `log-curves` test, given a fifth assertion `column: GR` while its
`bulk.columns` stays `[MD]`:

```text
<file>: tests[2] 'log-curves': bulk.columns lists the only columns the test reads, and leaves out 'GR' (assert[4]); add it to bulk.columns, or leave bulk.columns out to read exactly the columns the assertions name.
```

Other DDMSs, and the files behind dataset records, are not read by a test; their storage records are tested like any
other record.

## Holding records to a mapping's assertions

The `mapping` subject holds every record a test reads to the business rules a delivery's mapping states beside its
properties ([mapping assertions](mapping-assertions.md)): the mapping's record-stage assertions, judged on each record as
storage holds it by the code a delivery judges the documents it renders with.

```yaml
  - name: log-rules
    description: Each delivered log meets what its mapping asserts.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    assert:
      - mapping: WellLog@1.4.0
```

- **Which mapping.** `mapping` is `Name@version`, as a delivery flow's `render.mapping` names it, without `/`, `\`, `..`
  or whitespace. A run reads it once, when it starts, from the mappings the repository sync keeps in the module's
  database. Two repositories that declare the reference with the same document are read as one.
- **What it needs.** Records as storage holds them, so `read: index` is refused. The test's `kind` is the kind the
  mapping renders, the mapping states at least one record-stage assertion, and each of those fits the template the test
  is checked against: its property is one of the kind's, and its condition suits the property's type. The incoming
  assertions are not judged, since OSDU does not hold the rows the records were rendered from.
- **What it counts.** A record meets it when none of the mapping's assertions fails on it, whatever each assertion's
  `onFail`, and every record read has to (it takes no `for`). The result says how many records meet it, how many
  judgements were made, and the five assertions failed by the most records
  (`3 of 5 record(s) meet it; 31 judgement(s) made; failed most: data.Curves[].Mnemonic "a depth curve is among the curves" (2)`);
  each example names its record's first failure and how many more it has
  (`data.VerticalMeasurement.VerticalMeasurement fails "reference-elevation": is 312, outside 0 to 250 (and 1 more)`).

A mapping that cannot be read, or does not fit the test, leaves the test `errored` with why, and nothing is evaluated.
Left without a `name`, the assertion is called `meets what mapping <Name@version> asserts`:

```text
'meets what mapping WellLog@1.4.0 asserts': no mapping WellLog@1.4.0 is synced; sync the repository that declares it, then run again.
'meets what mapping WellLog@1.4.0 asserts': mapping WellLog@1.4.0 is synced from 2 repositories whose documents differ (mappings/WellLog@1.4.0.yaml, mappings/WellLog@1.4.0.yaml), so there is no one mapping to read; give each its own version.
'meets what mapping WellLog@1.4.0 asserts': mapping WellLog@1.4.0 renders osdu:wks:work-product-component--WellLog:1.4.0, and the test reads osdu:wks:work-product-component--WellLog:1.5.0; a test holds the records of the kind a mapping renders to its assertions.
'meets what mapping WellLog@1.4.0 asserts': mapping WellLog@1.4.0 asserts nothing of the values a record carries (stage: record), so there is nothing to hold the records to; its incoming assertions judge the rows records were rendered from, which OSDU does not hold.
```

A mapping the sync recorded as not loading is answered with
`mapping <Name@version> (<file>) does not load: <why>`. A delivery judges the same assertions on the documents it renders,
before they are sent; a test judges what OSDU holds afterwards, records delivered before the assertions were written
included.

## Known values

A `knownValues` assertion holds records to proof values a source states about them without naming them by id: each row
finds its record by full paths and the values the record holds there, including paths up to the record a reference names
(`'[data.WellboreID, data.FacilityName]'`, a list of paths read as a dimension's label is) and down to the records
naming one, and holds it to its proof values, each a full
path and a value or a condition. It says, row by row, which rows hold, which differ and in what, which are not found and
by which path, and which find more than one record. [Known values](known-values.md) has every key, how the records are
found, and what a row comes to.

## Disabling a test, and the day its values hold until

A test, an assertion, a row of known values or one proof value can be switched off without leaving the flow:
`disabled: <why>` keeps it in every report, `skipped` with that reason, and the run neither judges nor fails on it. A test
whose every assertion is disabled is skipped as a whole.

Proof values are stated on a day and change as the source does: a count taken today is not tomorrow's. A test, or an
assertion, says when its values were stated and how long they hold, and a run past that does not judge it against values
that may no longer be so: it flags it, so the tests that need new proof values stand out on the board.

```yaml
  - name: interpretation-logs
    disabled: No flow of this estate delivers interpretation logs to OSDU yet.
  - name: north-counts
    query: 'tags.SourceProject:"NORTH"'
    asOf: 2026-10-13
    validFor: 90d
    asOfField: createTime
    assert:
      - { aggregate: distinct, field: data.WellboreID, equals: 62 }
      - name: curves, restated weekly
        aggregate: count
        field: data.Curves.CurveID
        equals: 7842
        asOf: 2026-10-13
        validFor: 1w
```

| Key | Meaning |
| --- | --- |
| `asOf` | The day (an ISO 8601 date or date and time) the values were stated. Every result quotes it beside what it expected (`equals 62 (stated as of 2026-10-13)`). |
| `validFor` | How long after `asOf` they hold: a whole number of days, weeks, months or years (`90d`, `12w`, `6m`, `1y`, or ISO 8601's `P90D`). Needs `asOf`. |
| `validUntil` | The last day (or instant) they hold, in place of `validFor`; not before `asOf`. |
| `asOfField` | Tests only: the date a record holds on or before `asOf` to be among the test's records (below). |

A date holds to the end of that day in UTC, a date and time to that instant. An assertion's own `asOf`, `validFor` and
`validUntil` take over from its test's.

**Past the day.** An assertion whose values are past the day they hold until is not judged, and comes out as a `warning`
that has not judged anything (`not judged`), saying
`needs new proof values: stated as of 2026-10-13 and valid until 2027-01-11 (90d after asOf), and the run is past that; restate them and move asOf`.
Its test is `warned` (the other assertions are judged as ever), and a test whose every judged assertion is past its day
asks OSDU nothing. A warning does not fail a run unless the flow says `failRunOn: warning`. Restating is giving the new
values and the day they were stated on: change the values and move `asOf`.

**What OSDU held on the day.** `asOfField` narrows every search of the test (its count, its groups, the records it reads,
the searches of its known values, a plan) to the records whose date there is on or before `asOf`:
`(<query>) AND (createTime:[* TO "2026-10-14T00:00:00.000Z"})`. It takes `createTime` (when OSDU created the record),
`modifyTime`, or a date property of the record's data the index holds outside a nested list. With `createTime`, a count
stated today keeps meaning the records there today as more arrive. It cannot rebuild the past: a record changed since
`asOf` is read as it is now, one deleted since is not found, and `createTime` is when OSDU received a record, not when its
source had it, so a record first delivered after `asOf` is left out even when the source held it then. Not with `ids`.

## Checked against the template

Before a test that reads fields (a field condition, a field aggregate, `unique`, `groupBy`, `recordSet`, `knownValues`,
`conforms`, `mapping`, a `sort` or a `spatial` filter) reads anything, it is checked against the template of its kind (see
[Templates](../concepts/templates.md)): every path has to be a property of the schema, every operator has to suit the
property's type, and every value compared with has to be one the property can hold (its type, date format and pattern).
A test that does not fit is reported `errored` with each problem, and is not evaluated:

- `'<label>': '<path>' is not a property of <kind> (template <version>); did you mean '<nearest>'?`
- `'<label>': '<path>' is a list in <kind> (template <version>); equals compares one value, so test each item with '<path>[*]', or ask what the list contains.`
- `'<label>': '<path>' refers to master-data--Wellbore in <kind> (template <version>), never to master-data--Well.`
- `The test's fields are checked against the template of <kind>, and no template folder on the path of flows/<flow>.yaml holds template <kind>. ...` (the rest says which folders it read and how to put the template's file in one)
- `The test is checked against template <kind> version <version>, the one mapping <Name@version> fills, and ...`
- `The test is checked against the template mapping <Name@version> fills, and no mapping <Name@version> is synced; sync the repository that declares it, then run again.`
- `Mapping <Name@version> fills a template of <kind>, and the test reads <other kind>; name the mapping of <other kind>, or a template version of it.`

A test that only counts, checks the index, legal tags or the ledger, or reads bulk data, needs no template.

## Records the index may not list yet

OSDU indexes a change from a queue, so for a while after records are written, removed or put back at an earlier version,
search lists them as they were, and a test judged then fails on records that are fine. Before a run judges anything it
reads from the ledger what this module's delivery flows changed in OSDU in the partition within each test's
`indexSettleSeconds`: records written, records taken out or put back at an earlier version, and records deleted from a
ledger that OSDU had held. A test whose entity type (its kind's, or for `ids` theirs) changed within its window is
`skipped`, and its result says which ledgers changed what, the latest change, and from when a run judges it. Other
versions of the same entity type count. A test that does not fit its template is reported as such whatever changed.

A skipped test does not fail the run. Changes other systems make are not in the ledger and are not waited for. With
`indexSettleSeconds: 0` a test is judged at once. The window says when a test is judged, not what it checks, so changing
it does not mark the test as changed.

## Outcomes

| Level | Outcomes |
| --- | --- |
| Assertion | `passed`, `failed`, `errored` (it could not find out: a refused query, an unreachable service, a limit passed, more records matched than the test reads) or `skipped`. A failed one keeps what it expected, what it found, how many it checked and failed, and the first failing records (`examples`), each with its id, value and reason. |
| Test | `errored` when an assertion errored or the test does not fit its template; else `failed` when an `error` assertion failed; `warned` when only `warning` ones did; `passed` otherwise (a failed `info` assertion is reported and changes nothing; an assertion past the day its values hold until is a `warning` that needs new proof values); `skipped` when it does not run in the partition, the index has not settled, or it is disabled. |
| Run | `failed` when a test failed, else `errored` when one errored, else `passed`; `cancelled` when stopped, and `errored` when the run itself could not go on. A run whose tests failed or errored keeps, as its reason, how many did and which; a run that stopped keeps why, and records fewer results than tests. A run with no test to judge in its partition asks OSDU nothing and completes `passed`, its tests skipped. |

`failRunOn` decides whether the platform run fails: `error` (the default) on a failed or errored test, `warning` on a
warned one as well, `never` not at all. A run failed by its tests still carries its outcome, and its error names them:
`assertion flow '<name>' in partition '<p>': <n> of <m> test(s) evaluated, <k> passed; 1 failed on an assertion of severity error (log-headers).`
Notifications fire on it as on any failed run.

## Operations and the run payload

| Operation | What it does |
| --- | --- |
| `test` (default) | Selects the tests, checks them against their templates, runs them (up to `parallelism.recordsAtOnce` at once) and keeps the report. Needs the module's database. |
| `plan` | Selects and checks the same tests and counts what each matches and would read, evaluating nothing and recording nothing. It needs the module's database only for the templates. |

The payload takes `tests` (names) and `tags` and nothing else; a test runs when it is named or carries one of the tags,
and with neither every test runs. Each list holds at most 500 names, each once.

```bash
sqlflow run welldb-welllog-04-assertion.yaml --set partition=dev
sqlflow run welldb-welllog-04-assertion.yaml --set partition=dev --operation plan
sqlflow run welldb-welllog-04-assertion.yaml --set partition=dev --payload '{"tags":["smoke"],"tests":["log-curves"]}'
```

A name that is not a test fails the run: `Assertion flow '<name>' has no test named '<x>'; its tests are <names>.`, and so
does a tag no test carries. Any other payload property is refused before the run starts, for example
`payload recordKeys does not apply to an assertion flow: only a delivery flow's runs are scoped to records; an assertion flow's payload names only tests and tags.`
A schedule's `values` can name the partition and the parameters; a scheduled run runs every test. The values of a
schedule several flows join reach every one of them, so an assertion flow sharing its delivery flow's schedule declares
the delivery's parameters too, or fails with `parameter '<name>' is not declared under parameters.` On the control plane,
`${env:NAME}` references resolve from the central configuration (the partition's own values first) before the node's
environment. See [Running OSDU flows](../cli/run.md).

The run's `result` names the report (`assertionRunId`), `partition`, `status`, the counts (`tests`, `passed`, `failed`,
`warned`, `errored`, `skipped`) and the tests that did not pass first (`results`, at most 50, each with `test`, `kind`,
`outcome`, `matched` and the first failed assertions); a plan's lists each test with its `query`, `matched`, `wouldRead`,
`template`, `problems` and `error`.

## The report

Each run keeps one `osdu.AssertionRun` row and one `osdu.AssertionResult` row per test, written the moment the test
finishes, so a long run shows its progress and a stopped one keeps what it found. A result keeps the whole of what each
assertion found, up to 1,000,000 characters; past that it keeps fewer examples, marked as cut back. Each result carries
its test's definition hash, so a result that answers an earlier definition of the test shows as changed. See
[the ledger](../concepts/ledger.md).

Where to read it:

- The GUI: **OSDU**, **Assertions** is the board of every assertion flow in the partition, where each test
  is an assertion and each of its assertions a check. An assertion flow's pipeline has an **Assertions** tab (its
  board), a **History** tab (its assertions against its runs) and a **Reports** tab (its runs); a report page runs the
  assertions not passing again. A report page shows "The run stopped" only for a run that
  stopped (cancelled, or errored with fewer results than tests); a run whose tests errored shows its reason instead.
- The CLI: [sqlflow assertions](../cli/assertions.md) `list`, `status` and `report`.
- The API, every route a `GET` under `/api/v1/delivery`: `/assertions` (the board of every flow),
  `/flows/{pipelineId}/assertions` (one flow's board), `/flows/{pipelineId}/assertion-runs`,
  `/flows/{pipelineId}/assertion-matrix` (tests against runs), `/flows/{pipelineId}/assertions/{test}/history`,
  `/assertion-runs/{id}` (a run with every result) and `/assertion-runs/{id}/report?format=json|md|html|junit`. A read is
  in the partition `?partition=` names, else the `X-Osdu-Partition` header's, else the one a run would test.

A report renders as JSON, Markdown, self-contained HTML or JUnit XML (a suite per kind, a case per test: failed is a
failure, errored an error, skipped skipped, warned passes with its warnings in its output). Runs stay until an admin sets a
limit for the assertion runs on the Maintenance page; a cleanup then removes a finished run older than its cut-off only
whole, once a later result of each of its tests superseded it, so every test's latest result stays
([What the osdu schema stores](../concepts/storage-and-retention.md)).

## Lineage

The flow reads the kind of each test, in its one version, on its platform and partition, and writes nothing. That is
the type node the delivery flow writing the kind links to through its mapping's `template.kind`, so the delivery is
ordered before the tests, and a schedule firing both runs the tests after the records land. See
[Lineage](../concepts/lineage.md).

## Limits

| Limit | Value |
| --- | --- |
| Tests per flow, names and tags per payload | 500 |
| Assertions per test | 100 |
| Tags per test | 20 |
| Ids per test | 1,000 |
| Values in `in`, `notIn`, `except`; rows in a `recordSet` or of `knownValues` | 1,000 |
| Paths a row of `knownValues` is located by; proof values it checks | 8; 60 |
| Steps of a `knownValues` path beyond the record's own | 3 |
| Records reached through others by one `knownValues` assertion | 200,000 |
| Fields in `unique`; columns in a `recordSet` | 20 |
| Records compared by `unique` | `maxRecords`, at most 1,000,000 |
| Conditions in a `where` | 10 |
| Distinct references per `resolves` | 200,000 |
| Distinct values per `distinct` | 1,000,000 |
| Distinct legal tags per `legal` | 10,000 |
| Time per value for `matches` | 1 second |

Past one of the last four, the assertion does not pass, and its result says why.

## Validation errors

| Message (after the file path) | Cause |
| --- | --- |
| `tests must list at least one test; an assertion flow without tests has nothing to run.` | No tests. |
| `tests[<i>] is named '<name>', as an earlier test is; a run and a report name each test by its name, so each needs its own.` | A duplicate test name. |
| `<test>: kind '<kind>' has wildcards; a test reads one type in one version, ...` | A kind with `*`. |
| `<test> reads records by ids, and names a query as well: a test reads the records its ids name, or the records a search finds, not both.` | `ids` with `query`, `spatial` or `sort`. |
| `<test>: query uses '{<token>}', which is neither {partition} nor declared under parameters.` | An undeclared token. |
| `<test> reads bulk data, and its kind '<kind>' is not one the Wellbore DDMS keeps bulk data for; it serves ...` | `bulk` on another kind. |
| `<test>: assert[<n>] is a monotonic assertion, which reads each record's bulk data; give the test a bulk block (bulk: { columns: [...] }).` | A bulk assertion without `bulk`. |
| `<test>: bulk.columns lists the only columns the test reads, and leaves out '<column>' (assert[<n>]); add it to bulk.columns, or leave bulk.columns out to read exactly the columns the assertions name.` | A `bulk.columns` list without a column an assertion names. |
| `source.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: ...` | A literal secret. |
| `<test>: assert[<n>] names atLeast and atMost; a condition has one operator. Write between for a range, or one assertion for each.` | Two operators. |
| `<test>: assert[<n>].field '<path>' starts at '<root>', which is not a property of an OSDU record; ...` | A path that does not start at a record root. |
| `<test>: assert[<n>] asserts that every record meets its template, which needs each record as storage holds it; ... Take read: index out.` | `conforms` with `read: index`. |
| `<test>: assert[<n>].mapping '<value>' names the mapping whose assertions the records are held to as Name@version, such as mapping: WellLog@1.4.0.` | A `mapping` that is not `Name@version`. |
| `<test>: assert[<n>] holds every record to what mapping <Name@version> asserts, which needs each record as storage holds it; the test reads the index (read: index), which holds only what it indexed. Take read: index out.` | `mapping` with `read: index`. |
| `<test>: assert[<n>] compares the records in order, and the test names no sort; ... Give the test a sort.` | `recordSet` `mode: ordered` without `sort`. |
| `source.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it tests. Remove the header.` | Both ways of naming the partition. |
| `partitions[<i>] '<name>' is not a data-partition-id: letters, digits, underscore, hyphen and dot, at most 200 characters.` | A partition such as `*`. |
| `parameters declares 'partition', which an assertion flow keeps for the partition a run tests: {partition} in a test is always that partition. Rename the parameter.` | A parameter named `partition`. |
| `<test>: assert[<n>].knownValues is located by nothing; give it locateBy, the full paths and values its record holds, such as data.Name: COMPOSITE.` | A row of known values with no `locateBy`. |
| `<test>: assert[<n>].knownValues.rows[<i>] is located as rows[<j>] is (<values>); a record is held to one row of proof values, so locate each once.` | Two rows located alike. |
| `<test>: assert[<n>].knownValues.check.<path> has '<key>'; a proof value is a value, or a condition: one of equals, ...` | A proof value with a key it does not take. |
| `<test>: assert[<n>].knownValues.locateBy.<path> '<path>' names an item of a list by its position; ...` | A path with `[0]`. |
| `<test>: assert[<n>] finds its records by the values its rows give, and the test names its records by id; ...` | `knownValues` in a test with `ids`. |
| `<test>.disabled says why it is disabled, in at most 200 characters, ...` | An empty `disabled`. |
| `<test>: template '<text>' names the mapping whose template the test is checked against as Name@version, such as template: WellLog@1.5.0.` | A `template` with `@` that is not one mapping's `Name@version`. |
| `<test>: asOf '<text>' is not an ISO 8601 date, such as 2026-10-13 or 2026-10-13T12:00:00Z.` | A date that is not ISO 8601. |
| `<test>.validUntil <day> comes before asOf <day>; values hold from when they are stated.` | A `validUntil` before its `asOf`. |
| `<test>.validFor '<text>' is not a period: a whole number of days, weeks, months or years, such as 90d, 12w, 6m or 1y.` | A `validFor` that is not a period. |
| `<test>.validFor counts from the day the values were stated, and no asOf says which; give asOf.` | `validFor` without `asOf`. |
| `<test> gives validUntil and validFor; values hold until a day, or for a period after asOf, so give one.` | Both. |
| `<test>.asOfField '<path>' is not a date every record carries: createTime (when OSDU created the record), modifyTime, or a date property of its data.` | Another `asOfField`. |
| `<test>: assert[<n>].warnWithin '<text>' is not a warning band: a distance greater than zero, such as 5, or a percentage of the value compared with, such as 10%.` | A band that is not a number or a percentage, or is 0. |
| `<test>: assert[<n>].warnWithin widens the numbers a value is compared with, and notEquals says what the value must not be, which has nothing to widen.` | A band beside `notEquals` or `notIn`. |
| `<test>: assert[<n>].warnWithin 1 is no wider than the tolerance 2 the comparison already allows, so nothing would warn; widen it.` | A band within the tolerance. |

## See also

- [Testing what OSDU holds](../guides/data-quality-tests.md): writing, running and scheduling tests, step by step.
- [sqlflow assertions](../cli/assertions.md): reading the reports from a terminal or a CI job.
- [Delivery flow](delivery.md): what the tests check.
- [Mapping assertions](mapping-assertions.md): the rules a mapping states, which the `mapping` subject holds records to.
- [Known values](known-values.md): proof values of records a sheet names by what they hold.
- [Templates](../concepts/templates.md): saving the template a test is checked against.
