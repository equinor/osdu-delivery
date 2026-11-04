---
id: delivery-flow-assertion-errors
title: "Assertion flow load errors: what sqlflow validate refuses in a flowType: assertion document, and each message"
type: flow-reference
summary: "Each message the loader refuses an assertion flow with when it is read, and its cause: tests, assertions, bulk data, conditions and dates."
keywords:
  - assertion flow validation errors
  - what validate refuses in an assertion flow
  - names no subject
  - does not take
  - undeclared token
  - bulk block required
  - assertion flow load errors
yamlPath: "(root, flowType: assertion)"
related:
  - delivery-flow-assertion
  - delivery-flow-known-values
  - delivery-flow-staged-proof-values
  - delivery-flow-staged-accounting
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/ConditionReader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
---

# Assertion flow load errors: what sqlflow validate refuses in a flowType: assertion document, and each message

An [assertion flow](assertion.md) is read strictly: `sqlflow validate`, the repository sync and every run refuse a
document that breaks a rule, with the file and the message below. The refusals of
[known values](known-values.md), [curve proof values](curve-proof-values.md),
[proof values from staged data](staged-proof-values.md) and [staged accounting](staged-accounting.md) are on their own
pages.

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
