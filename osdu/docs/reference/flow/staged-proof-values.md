---
id: delivery-flow-staged-proof-values
title: "Staged proof values: OSDU compared with what a query of the staged tables answers"
type: flow-reference
summary: "An assertion compares OSDU with a query's answer: a count, a value, or one row of known values per staged row, asked when the run judges."
keywords:
  - staged proof values
  - proof values a query answers
  - compare osdu with a database
  - database versus osdu
  - staged data
  - staged tables
  - ingestion tables
  - queries
  - query
  - sql
  - connection
  - inline query
  - named query
  - known values of a query's rows
  - dynamic proof values
  - expected value a query answers
  - count equals a query
  - are all records uploaded
  - read-only query
  - timeoutSeconds
  - maxRows
yamlPath: "(flowType: assertion) queries, tests[].assert[].<comparison>: { query } or { connection, sql }, tests[].assert[].knownValues.query, knownValues.connection, knownValues.sql, knownValues.maxRows"
related:
  - delivery-flow-assertion
  - delivery-flow-known-values
  - delivery-flow-staged-accounting
  - delivery-guide-data-quality-tests
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/ConditionReader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionOperands.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/StagedSql.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/StagedQueries.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/StagedBinding.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
  - sqlflow/src/SqlFlow.SqlServer/Query/SqlServerQueryRunner.cs
  - sqlflow/src/SqlFlow.SqlServer/Query/ReadOnlyQueryGuard.cs
---

# Staged proof values: OSDU compared with what a query of the staged tables answers

A value written into a test goes stale: the estate grows, a count moves, a header is corrected at its source. A test can
compare OSDU with what the staged data holds now instead. A query asks the staged tables when the run judges the test,
and its answer is the value OSDU is held to: a count, a single value, or one row of [known values](known-values.md) for
each row it answers. Where a query runs is part of the query: each one names its database beside its SQL.

```yaml
flowType: assertion
name: welldb-wellbore-05-assertion
partitions: [dev, test]

parameters:
  system: { default: welldb }

queries:
  staged-wellbores:
    connection: ${env:OSDU_DATA_DB}
    sql: |
      SELECT COUNT(*) FROM OsduData.silver.Wellbore
      WHERE DeletedDate_DW IS NULL AND source_system = {system}
  staged-wellbore-rows:
    connection: ${env:OSDU_DATA_DB}
    sql: |
      SELECT wellbore_id, wellbore_name, source_system
      FROM OsduData.silver.Wellbore
      WHERE DeletedDate_DW IS NULL AND source_system = {system}

source:
  endpoint: ${env:OSDU_URL}
  auth: { ... }

tests:
  - name: wellbore-count
    kind: osdu:wks:master-data--Wellbore:1.3.0
    query: 'data.Source:"{system}"'
    assert:
      - count: { query: staged-wellbores }
        warnWithin: 1%
      - count:
          atMost: { connection: "${env:OSDU_DATA_DB}", sql: "SELECT COUNT(*) FROM OsduData.silver.Wellbore" }

  - name: wellbore-headers
    kind: osdu:wks:master-data--Wellbore:1.3.0
    query: 'data.Source:"{system}"'
    assert:
      - knownValues:
          query: staged-wellbore-rows
          locateBy: { data.FacilityID: wellbore_id }
          check:
            data.FacilityName: wellbore_name
            data.Source: source_system
```

The first test holds the number of wellbores OSDU holds to the number staged, warning within 1%, and to no more than the
whole table holds. The second holds each staged wellbore's record to its staged name and source: a staged wellbore
OSDU does not hold is a row not found. The SQL is the expression: whatever a comparison needs (`COUNT(*) * 0.99`, a join,
a `CASE`) is written in the query, and the assertion compares with its answer.

To account for every staged row through the delivery flow's ledger instead, with the ledger's reason for each row it held
back, use a [staged delivered assertion](staged-accounting.md).

## Queries

`queries` names the queries the tests compare with. A query written where it is compared with takes the same keys.

| Key | Default | Meaning |
| --- | --- | --- |
| `queries.<name>` | none | One query. Its name is what a test names it by (`{ query: <name> }`): a letter or digit followed by letters, digits, `.`, `_` and `-`, at most 100 characters, unique ignoring case. A flow names at most 100. |
| `connection` | required | Where the query runs: the database, as a `${env:NAME}` or `${keyvault:vault/secret}` reference, or a SQL Server connection string whose password is such a reference. It is resolved on the node that runs the flow, as a delivery flow's `source.connection` is; a literal password is refused when the flow is read. |
| `sql` | required | What the query asks: one `SELECT` (a `WITH ... SELECT` included), with `{parameter}` and `{partition}` tokens. See [What a query may do](#what-a-query-may-do) and [Parameters](#parameters). |
| `timeoutSeconds` | 300 | How long the query may run, 1 to 3600 seconds. |

A run asks each query once, whichever assertions and tests compare with it, with the same values. Two queries written
alike (the same connection, statement and values) are one query to the run.

## Where a query's value is compared with

A query's value stands wherever a comparison or a condition takes a value:

| Written | Meaning |
| --- | --- |
| `{ query: staged-wellbores }` | The value the query the flow declares under that name answers. |
| `{ connection: ..., sql: ..., timeoutSeconds: ... }` | The value of a query written in place. |

The query answers one row of one column. It may stand in:

- a count, a row count and a group count: `count: { query: q }` (equals), or `count: { atLeast: { query: q } }`;
- an aggregate's comparison: `aggregate: max`, `field: data.TopDepth`, `atMost: { query: q }`;
- a field's or a column's condition: `equals`, `notEquals`, `atLeast`, `atMost`, `greaterThan`, `lessThan`, either bound
  of `between`, `contains`, `notContains`;
- a proof value of known values, and a curve proof value.

`in`, `notIn`, `matches`, `startsWith`, `endsWith` and the rest compare with what the document writes. A warning band
(`warnWithin`) and a `tolerance` apply to a query's value as to a written one.

## Known values from a query

Known values read their rows from a query instead of listing them: one row of known values for each row the query
answers.

| Key | Default | Meaning |
| --- | --- | --- |
| `knownValues.query` | none | The query the flow declares under this name. |
| `knownValues.connection`, `knownValues.sql`, `knownValues.timeoutSeconds` | none | A query written here, in place of `query`. |
| `knownValues.maxRows` | 100,000 | The most rows the query may answer, 1 to 1,000,000. A query answering more judges none of them, and says so. |

With a query, every value `locateBy`, `check` and `curves` compare with names a column of its rows
(`data.FacilityName: wellbore_name`), matched ignoring case; a value that is the same in every row is written into the
query (`SELECT 'welldb' AS source_system`). The known values list no `rows` of their own. Each row is located and judged as
a written row is ([What a row comes to](known-values.md#what-a-row-comes-to)), and its label is the values it is located
by. Where the comparison is `in` or `notIn`, each value listed names a column; `matches`, `startsWith`, `endsWith`,
`exists`, `empty`, `type` and `length` take what the document writes, as they do in written rows. A curve's `at` keys are
written in the document, and the value at each names a column.

A row whose locating column holds no value (SQL NULL) locates nothing and is not found, saying why:
`query staged-wellbore-rows answered no value in wellbore_id, which the row is located by (data.FacilityID)`. A query
answering no row at all errors the assertion, since there is nothing to judge.

Rows located alike are found in searches of 50 rows at a time, as written rows are. More than 1,000 rows located alike are
found by reading every record the test's query matches once, a page at a time, and matching them in memory: the test's
`maxRecords` bounds that read, so raise it to the number of records the query's rows can find.

## Parameters

A query's `{name}` tokens (a parameter of the flow) and `{partition}` (the partition the run tests) are bound as values:
each becomes an `@name` parameter of the statement, and its value is sent beside it, never written into it. So a value
holding a quote reads as text, and a parameter can never change what the query does.

- Write a token where a value goes, without quotes: `WHERE source_system = {system}`. A token inside a string literal
  (`'{system}'`) or a quoted name (`[{system}]`) would be read as text, and is refused when the flow loads.
- A token inside a comment is left as it is.
- A token is a parameter the flow declares, or `{partition}`; another is refused when the flow loads.

## What a query may do

A query only reads. It is checked when the flow loads, and again before it runs, by SQLFlow's read-only query guard, which
parses the statement rather than matching its text: it is one `SELECT`, in one batch, and it holds no `SELECT ... INTO`,
`EXEC`, procedure call, `OPENQUERY`, `OPENROWSET`, `OPENXML`, `NEXT VALUE FOR` or reference to a system catalog. The run then
reads it inside a transaction that is always rolled back, so nothing it could reach persists. The guard's refusal says
what was refused: `queries.q.sql is not a query a test may run: Only a SELECT may be run here; this is a DELETE. ...`

The connection is opened on the node that runs the flow, named after the flow (`Application Name=OSDU Delivery/<flow>`),
with the node's own access to the database: grant that login `SELECT` on what the queries read, and nothing more.

## How a value compares

A query's values come back as a test compares them:

| SQL type | Compared as |
| --- | --- |
| `int`, `bigint`, `smallint`, `tinyint` | The number. |
| `decimal`, `numeric`, `money` | The number, written without the zeros the column's scale adds: `1250.4000` is `1250.4`. |
| `float`, `real` | The number the column holds: a `real` keeps single precision, so compare it with a `tolerance`. |
| `nvarchar`, `varchar`, `char` | The text, whole. |
| `datetime2`, `datetime`, `date` | An ISO 8601 instant, read as UTC: `2026-01-02T03:04:05Z`. It equals a value OSDU holds at that instant, however written. |
| `datetimeoffset` | An ISO 8601 instant with its offset. |
| `bit` | `true` or `false`. |
| `uniqueidentifier` | Its 36-character form. |
| `varbinary` | Hexadecimal text, `0x0A0B`. |
| NULL | No value: equals compares it with a value that is absent or null. |

## What a run reports

A query's value is quoted where it is compared with, with the query it came from: `equals 4 (query staged-wellbores)`. The
test's result notes what each query answered (`Query staged-wellbores answered 4.`,
`Query staged-wellbore-rows answered 1204 row(s) of known values.`), so a report says what OSDU was compared with without a
second look at the database. The query's statement is part of the test's definition: change it, and the test's earlier
results no longer count as its latest.

An assertion whose query cannot be answered is not judged; it errors, saying why, and the test's other assertions are
judged as ever:

| Reason | When |
| --- | --- |
| `query <name> failed (SQL error <n>): <message>` | The database refused or failed the statement (a missing table is error 208). |
| `query <name>: its connection reference could not be resolved on this node: ...` | The reference names a variable or a secret this node does not hold. |
| `query <name> answered no row; a value compared with is one row of one column, ...` | A value's query found nothing. |
| `query <name> answered more than one row; ...`, `... answered <n> columns; ...` | A value's query answered more than one value. |
| `Query <name> answered "<text>", and a count is compared with a whole number, zero or more.` | The answer is not a value the comparison takes; an ordering takes a number or text. |
| `query <name> answers more than <n> rows, more than these known values judge, so none was judged: ...` | Known values' query answered more than `maxRows`. |
| `The known values name the column <c>, which query <name> does not answer; it answers <columns>.` | A value names a column the query does not answer. |
| `query <name> answers the column '<c>' twice; ...` | Two columns of the answer share a name, so a proof value could not name one. |

## Refused when the flow loads

| Message | Cause |
| --- | --- |
| `queries.<name>.connection names the database the query runs against, ...` | A query with no connection. |
| `queries.<name>.connection carries a literal password. ...` | A connection string holding a password. |
| `queries.<name>.sql is the query's one SELECT, ...` | A query with no statement. |
| `queries.<name>.sql is not a query a test may run: ...` | A statement the read-only guard refuses. |
| `... writes {<token>} inside a string literal ('...'), where it would be read as text. ...` | A token inside quotes. |
| `... uses '{<token>}', which is neither {partition} nor declared under parameters.` | A token the flow does not declare. |
| `queries.<name>.timeoutSeconds is how long the query may run: ...` | A timeout outside 1 to 3600. |
| `... names the query '<name>', which the flow does not declare under queries; it declares ...` | A value naming no declared query. |
| `... names the query <name> the flow declares, ...; connection, sql and timeoutSeconds go with it under queries.` | A named query with keys of its own. |
| `... takes a single value, or the value of a query: ...` | A mapping that is neither a value nor a query. |
| `... reads its rows from a query, and lists rows as well; ...` | Known values with a query and `rows`. |
| `... names a column of the rows the known values' query answers, and <v> is not a column name; ...` | A number or a boolean where known values from a query name a column. |
| `... maxRows bounds the rows a query answers, and the known values read no query; ...` | `maxRows` without a query. |

## See also

- [Assertion flow](assertion.md): the tests, the assertions and the report.
- [Known values](known-values.md): how rows are located and judged.
- [Staged accounting](staged-accounting.md): every row a delivery flow stages, accounted for through its ledger.
- [Testing what OSDU holds](../guides/data-quality-tests.md): a flow built step by step.
