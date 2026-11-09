---
id: delivery-flow-mapping-dataset-assertions
title: "Rules on the incoming rows (dataset.assert): what the rows a mapping renders from must hold"
type: flow-reference
summary: "Rules a mapping states on the rows it renders from: a column of each row, how many child rows, values no two rows share, per record or over the run."
keywords:
  - dataset.assert
  - rule on the incoming rows
  - row rule
  - incoming record set
  - child rows count
  - unique rows
  - duplicate rows in the run
  - "per: run"
  - "rows: curves"
  - "unique: [curve_id]"
  - "count: { atLeast: 2 }"
  - assertion on a column
  - held for a rule on the rows
related:
  - delivery-flow-mapping-assertions
  - delivery-flow-mapping
  - delivery-flow-delivery
  - delivery-flow-assertion
  - delivery-concept-preflight
  - delivery-concept-explorer
  - delivery-cli-values
yamlPath: "(documentType: mapping) dataset.assert"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.DatasetAssertions.cs
  - osdu/src/SqlFlow.Delivery/Model/DatasetAssertion.cs
  - osdu/src/SqlFlow.Delivery/Validation/DatasetAssertionJudge.cs
  - osdu/src/SqlFlow.Delivery/Validation/RunRowsFindings.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingAssertionJudge.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Source/IIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueChecker.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingOutline.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DatasetAssertionLoaderTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DatasetAssertionRenderTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DatasetAssertionRunTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/RunRowsFindingsTests.cs
  - osdu/docs/census/keys.mapping.json
---

# Rules on the incoming rows (dataset.assert): what the rows a mapping renders from must hold

Some rules are about a property's value: "the elevation lies between 0 and 250". They are written beside the property,
in its `$assert` ([mapping assertions](mapping-assertions.md)). Other rules are about the rows themselves: "the ACL
column reads as a whole", "every curve row has a unit", "a log has its index curve and one more", "no two logs of the run
share a name". They belong to no property, so they are written beside the dataset that names the rows, under
`dataset.assert`, and the mapping reads in the order a person thinks about it: what the rows must hold, then what the
record is made of.

A rule is judged on the rows as the ingestion tables hold them, before any record is written. A record whose rows break
it is **held** with its document kept, or **sent** with the failure recorded, exactly as a property's assertion holds or
reports it; what it found joins the record's verdict, its ledger history and every place a property's assertions are
shown.

## An example

Five rules on the well logs of the well database, written in `mappings/WellLog@1.4.0.yaml` (the rest of the mapping as in
the sample):

```yaml
dataset:
  system: welldb
  key: [source_project, log_id]
  assert:
    - name: acl-read-whole
      description: The export writes the ACL groups it could not read into AclUnreadable.
      column: AclUnreadable
      empty: true
    - name: every-curve-has-a-unit
      rows: curves
      column: curve_unit
      exists: true
      where:
        - column: curve_id
          notEquals: MD
    - name: index-and-one-curve
      rows: curves
      count: { atLeast: 2 }
    - name: one-row-per-curve
      rows: curves
      unique: [curve_id]
    - name: one-log-per-name
      per: run
      unique: [log_name]
      onFail: report
```

What each line reads, and what it makes of a record:

| Line | Reads | Makes |
| --- | --- | --- |
| `column: AclUnreadable`, `empty: true` | `AclUnreadable` of the log's row of the record table | A log whose export left anything there is held, its document kept until a release sends it. `onFail: hold` is the default. |
| `rows: curves`, `column: curve_unit`, `exists: true` | `curve_unit` of each of the log's curve rows | A log with a curve row lacking a unit is held; the failure names the row, `dataset.curves[3].curve_unit`. |
| `where: [ { column: curve_id, notEquals: MD } ]` | `curve_id` of the same curve row | The index curve's row is not judged: a condition on the rows selects the rows the rule reads. |
| `rows: curves`, `count: { atLeast: 2 }` | how many curve rows the log has | A log with fewer than two curve rows is held, with `the record has 1 row of curves, and the rule asks for count atLeast 2`. |
| `rows: curves`, `unique: [curve_id]` | `curve_id` of each curve row | A log two of whose curve rows hold the same id is held; the failure names the value and the rows, `curve_id 'GR'`, `rows 0 and 2 of curves share it`. |
| `per: run`, `unique: [log_name]` | `log_name` of every row of the record table in the run's scope, compared by the database once a run | The logs whose names another log of the scope shares are sent, and the failure recorded (`onFail: report`). |

## The words of a rule

`dataset.assert` is a list of 1 to 50 rules. A rule judges exactly one thing: `column`, `count` or `unique`.

| Key | Default | Meaning |
| --- | --- | --- |
| `column` | | A column of each row the rule reads, with exactly one condition beside it: `equals`, `notEquals`, `in`, `notIn`, `atLeast`, `atMost`, `greaterThan`, `lessThan`, `between`, `matches`, `notMatches`, `startsWith`, `endsWith`, `contains`, `notContains`, `exists`, `empty`, `type` or `length`, with `ignoreCase` and `tolerance`, written and compared exactly as an assertion flow's conditions ([conditions](assertion.md#conditions)). |
| `count` | | How many rows the rule reads, in the assertion flows' words for a count: a number meaning equals, or a map of `equals`, `notEquals`, `atLeast`, `atMost`, `greaterThan`, `lessThan` and `between` (`count: { atLeast: 2, atMost: 500 }`). |
| `unique` | | The columns no two of the rows the rule reads may share, one or a list of 1 to 10 (`unique: [curve_id, run]`). Two rows clash when every column holds the same value. |
| `rows` | the record's own row | A child dataset the flow declares under `source.datasets`: the rule reads each record's rows of it. |
| `per` | `record` | `record`: judged as each record renders, on its own rows. `run`: judged once a run, by the database, on every row of the record table in the run's scope ([below](#rules-over-the-runs-rows)). |
| `values` | `all` | For a `column` rule over a child dataset's rows: `all`, every row has to meet the condition; `any`, one row has to. |
| `where` | none | 1 to 10 conditions, each a `column` and one operator, with `ignoreCase` and `tolerance` ([below](#which-rows-a-rule-reads)). |
| `onFail` | `hold` | `hold`: the record is not sent; its document is kept until the rows or the mapping change, or a release sends it. `report`: it is sent, and the failure recorded. |
| `name` | a label read off the rule | 1 to 200 characters: what messages, the ledger and the reports call it. Each rule has a name of its own. |
| `description` | none | Free text. |

The words carry no `$`, since they are not properties of the record. `stage` and `field` are refused: a rule always
judges the rows, before the record exists. `onFail: omit` is refused: it leaves a property's value out, and a rule on the
rows judges no property.

## Which rows a rule reads

| Written | The rows | Judged |
| --- | --- | --- |
| no `rows`, no `per` | the record's own row of the record table | as the record renders |
| `rows: curves` | the record's rows of the child dataset `curves` | as the record renders |
| `per: run` | every row of the record table in the run's scope | once a run, before any record renders |

A `count` or a `unique` rule needs `rows` or `per: run`, since a record has one row of the record table.

A `where` condition does one of two things, by the row its column belongs to:

- **A column of a child dataset's rows** (`column: curve_id` beside `rows: curves`) selects the rows the rule reads. A
  `count` counts the rows selected, a `unique` compares them, and a `column` rule judges them.
- **A column of the record's row** (any column of a rule without `rows`, or `$dataset.<column>` beside `rows`) decides
  whether the rule is the record's to meet at all, as a property assertion's `where` does. A log whose `kind` is not
  `LOG` is not judged by a rule with `where: [ { column: $dataset.kind, equals: LOG } ]`, so its count is not taken.

A `per: run` rule takes no `where`: narrow the rows a run reads with the flow's scope.

## How a rule is judged

- **A column.** A value is judged as an incoming property assertion judges one: blank text and a null are no value,
  which only `exists` and `empty` judge ([values](mapping-assertions.md#values)). Over a child dataset's rows, each row
  is a judgement and each failing row a failure, named `dataset.<child>[<row>].<column>` counting from zero. With
  `values: any`, the rows are judged once together: the rule fails when none of the rows it read meets it
  (`none of its 3 rows meets it; one <why it fails>`), and a record with no row to read is not judged.
- **A count.** One judgement per record: the rows selected are counted, none counting zero. A record with no row of
  the child dataset fails `count: { atLeast: 1 }`.
- **Unique.** One judgement per record. Rows are compared by the values they hold, exactly; a row lacking a value in one
  of the columns is compared with none, since no value is no value to share. Each set of rows sharing values is one
  failure, named by its first row (`dataset.curves[0]`) and holding the values (`curve_id 'GR'`).
- **A rule that cannot be judged** counts as failed under its `onFail`, with
  `could not be judged (<error>), so it counts as failed`, and the other rules are still judged.

## Rules over the run's rows

A `per: run` rule is judged by the database over the record table, once a run, as soon as the run has opened its read
and before any record renders:

- It reads every row of the record table in the run's scope that the ingestion table does not mark deleted, under the
  flow's `source.record.filter`, whatever window the run reads changes in. A run that reads three changed logs compares
  their names with every log of the scope.
- `count` counts those rows. A count that fails reaches every record the run renders.
- `unique` asks the database for the rows sharing values with another row. Text compares exactly (a binary collation,
  so `GR` and `gr` differ), and a row whose column is null or blank shares nothing. The failure reaches each record the
  run renders whose row is among them, matched by its record key. At most 100,000 such rows are named one by one; when
  more share values, every record the run renders is judged to fail it.
- A column the database does not compare (of unbounded length, such as `nvarchar(max)`, or of a type such as `xml`,
  `text` or `geography`) is refused, naming the rule and the column, as is a column the table does not hold.
- Every record the run renders counts one judgement per rule, failing or not.
- The run's trace says once what the rules found:
  `Mapping WellLog@1.4.0: one-log-per-name: 4 rows of the record table in the run's scope share values of log_name with another row (first log_name 'GR', in 2 rows); the record of each such row the run renders is sent, the failure recorded`,
  or, when every rule holds, `the 2 rule(s) judged over the run's rows hold`.
- A run that finds nothing to plan does not ask the database.

## What a failure does, and where it is shown

A failure is an incoming-stage finding of the record, named by what the rule read: `dataset.AclUnreadable`,
`dataset.curves[3].curve_unit`, `dataset.curves` for a count, `dataset.curves[0]` for rows sharing values, and
`dataset` for a rule over the run's rows. It is held or reported as the rule says, and counted, recorded and shown as a
property assertion's failure is ([the check before sending](mapping-assertions.md#the-check-before-sending)):

| Where | What it shows |
| --- | --- |
| A plan, `sqlflow preview` | Each record's failures among its assertion findings, and whether the check before sending would hold it. |
| `sqlflow values` (Check values) | A check of every attribute counts the rows a rule fails among the rows failing an assertion and those an assertion would hold. A rule is about no attribute, so a check of some attributes does not judge the rules. |
| A record's history | Each attempt's verdict with the failures, as `sqlflow records show` and the record's timeline show them. |
| The run's trace | What the rules over the run's rows found, once a run. |
| The GUI | The mapping's Overview lists the rules on a table's card (Rules on these rows), and a field's detail lists the rules that read it (Rules reading it). The mapping builder lists the mapping's rules, read only, and writes them back unchanged. The Mappings list counts them among the mapping's assertions. |

A record whose document is the one OSDU already holds is not sent again, so the check before sending does not judge it
([a document that does not change](mapping-assertions.md#a-document-that-does-not-change-is-not-judged-again)). Adding or
changing a rule moves the mapping's fingerprint, so the next run renders every record of its scope again
([when a mapping changes](mapping.md#when-a-mapping-changes)).

The rules are not judged on what OSDU holds: OSDU does not hold the rows. The explorer's Validate says so
(`2 rule(s) of WellLog@1.4.0 on the rows it renders from (dataset.assert) judge the ingestion tables, which OSDU does not hold, so they were not judged`),
and an assertion flow's `mapping:` subject holds records to the mapping's record-stage assertions only.

## dataset.assert or $assert

| The rule is about | Write it |
| --- | --- |
| The value a property of the record carries | `$assert` on the property ([mapping assertions](mapping-assertions.md)). |
| The value the row gives one property, before its modifiers, and what that property does when it fails (`onFail: omit`) | `$assert` with `stage: incoming` on the property. |
| A column no property reads, or a column as a whole check of the row | `dataset.assert` with `column`. |
| How many child rows a record has, or rows sharing values | `dataset.assert` with `count` or `unique`, and `rows`. |
| The run's rows together | `dataset.assert` with `per: run`. |

## The preflight

The preflight gate ([preflight](../concepts/preflight.md#the-preflight-gate)) checks every rule against the ingestion
tables before any row renders, as it checks every column a mapping reads:

```text
<file>: dataset.assert[2] reads the rows of child dataset 'logs', which the flow does not declare under source.datasets.
<file>: dataset.assert[1] reads dataset.curves.run, which child dataset 'curves' does not hold. Columns: curve_id, curve_unit, ...
```

## Limits

| Limit | Value |
| --- | --- |
| Rules in `dataset.assert` | 1 to 50 |
| Columns of a `unique` | 1 to 10 |
| Conditions in a `where` | 1 to 10 |
| `name` | 1 to 200 characters |
| Rows sharing values a run names one by one | 100,000 |
| Failures a record lists | 50, every failure counted |

## Edge cases

| Case | Behaviour |
| --- | --- |
| A record with no row of the child dataset | A `column` rule judges nothing, a `count` counts zero, a `unique` finds nothing shared. |
| A where on the record's row that does not hold | The rule is not judged for the record, and counts no judgement. |
| A row lacking a value in a `unique` column | Compared with none. |
| `GR` and `gr` in a `unique` column | Different values, per record and over the run. |
| A `per: run` rule in a run of three changed records | Compares every row of the scope; the three records it renders carry what it found. |
| A record its own render holds for another reason | Held at render; nothing is queued, as for a property assertion. |
| A record whose document did not change | Not sent and not judged again. |
| An enrichment mapping | Declares no dataset, so no rules. |

## Errors when the mapping is read

Each message starts with the file and the rule, such as `mappings/WellLog@1.4.0.yaml: dataset.assert[1]`.

| Mistake | Message |
| --- | --- |
| Nothing to judge | `dataset.assert[0] says nothing to judge: name a column and one condition ({ column: AclUnreadable, empty: true }), a count of rows ({ rows: curves, count: { atLeast: 2 } }), or the columns no two rows share ({ per: run, unique: [log_name] }).` |
| Two things to judge | `dataset.assert[0] names column and count; a rule judges one of them. Write one rule for each.` |
| `stage` | `a rule on the incoming rows judges the rows as the ingestion tables hold them, so it takes no stage; stage belongs to a property's $assert, ...` |
| `field` | `a rule on the incoming rows reads the rows before any record is written, so it names a column of them, not a field of the record; ...` |
| `onFail: omit` | `onFail: omit leaves a property's value out of the record, and a rule on the incoming rows judges no property; hold the record (onFail: hold, the default) or send it and record the failure (onFail: report). ...` |
| A count of the record's row | `dataset.assert[0] counts the record's own row of the record table, and a record has one; write rows: <child dataset> to count the record's rows of a child dataset, or per: run to count every row of the record table in the run's scope.` |
| `per: run` with a `column` | `per: run judges the run's rows together, by how many there are (count) or that no two share some values (unique); a condition on each row's column is judged as each record renders, so leave out per: run.` |
| `per: run` with `rows` or `where` | `per: run reads every row of the record table in the run's scope, and rows names a child dataset; ...` / `per: run is judged by the database over every row of the run's scope, which a where does not narrow; ...` |
| `values` where there is one row | `values says whether every row of a child dataset has to meet the condition, or one of them, and this rule judges the record's one row of the record table; remove values.` |
| A condition beside `count` | `a count is compared inside it, such as count: { atLeast: 2 }, not with equals beside it.` |
| `$dataset.<column>` as the judged column beside `rows` | `'$dataset.kind' reads the record's row, and the rule judges the rows of curves; judge a column of the record's row in a rule without rows, or select the rows of curves by it with where: [ { column: $dataset.kind, ... } ].` |
| A misspelled word | `'colum' is not a word of a rule on the incoming rows. Did you mean 'column'?` |
| The same rule twice, or one name twice | `dataset.assert[1] asserts what dataset.assert[0] asserts (...); remove one of them.` / `dataset.assert[1] is named 'x', as dataset.assert[0] is; ...` |

## Related

- [Mapping assertions](mapping-assertions.md): `$assert`, the rules a property's value must meet, and the check before sending.
- [Mapping](mapping.md#dataset-the-record-tables-key-label-and-identity): the dataset block.
- [Delivery flow](delivery.md): `source.record`, its scope and filter, and `source.datasets`.
- [Preflight](../concepts/preflight.md): the preflight gate and the verdict.
- [sqlflow values](../cli/values.md) and [sqlflow preview](../cli/preview.md): what the rules find before a run.
