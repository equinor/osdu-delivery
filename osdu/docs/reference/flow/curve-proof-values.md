---
id: delivery-flow-curve-proof-values
title: "Curve proof values: a log's curve data held to what its source files state"
type: flow-reference
summary: "Known values check each log's curve data in the Wellbore DDMS: a curve's value at a depth, its samples, values, first and last depth, step."
keywords:
  - curve proof values
  - curves
  - at
  - curveIndex
  - value of a curve at a depth
  - check value x on curve y at key z
  - curve value at a key
  - validate curve data
  - validate curve files
  - well log bulk data
  - wellbore ddms bulk data
  - curve count
  - curve min max
  - curve average
  - top and base depth of a curve
  - first and last value of a curve
  - depth step
  - missing values of a curve
  - compare curves with the source files
  - reference curve
yamlPath: "(flowType: assertion) tests[].assert[].knownValues.curves, knownValues.rows[].curves, knownValues.curveIndex"
related:
  - delivery-flow-known-values
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/KnownCurves.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/CurveKeys.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/KnownValuesEvaluator.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/WellboreBulk.cs
---

# Curve proof values: a log's curve data held to what its source files state

A log's curve data lands in the Wellbore DDMS, apart from its record. Its source files say what that data holds: the
value of a curve at a depth, how many samples each curve has, the range of its values, the depth it starts and ends at.
A [known values](known-values.md) row finds the log by what its record holds (`locateBy`), and `curves` holds the log's
bulk data to those proof values, curve by curve, beside the proof values of its record (`check`):

```yaml
tests:
  - name: logs-of-a-1-f-11
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    bulk: { maxRows: 2000000 }
    assert:
      - name: the curve data of A/1-F-11 holds what its source files state
        knownValues:
          tolerance: 0.0001
          locateBy:
            '[data.WellboreID, data.FacilityName]': A/1-F-11
          rows:
            - locateBy: { data.Name: COMPOSITE }
              curves:
                GR:
                  at: { 1500.1524: 74.2, 1600.02: 80.15, 1700.0: { equals: 91.3, tolerance: 0.05 } }
                  count: 5000
                  max: 150.2
                DT:
                  at: { 1500.1524: 98.4 }
                MD: { first: 1000, last: 1780.1356, step: 0.1524 }
```

Read `GR: { at: { 1500.1524: 74.2 } }` as "GR at 1500.1524 is 74.2": in the row whose depth is 1500.1524, the GR curve
holds 74.2. `count: 5000` is "GR has a value in 5000 rows".

| Key | Where | Meaning |
| --- | --- | --- |
| `curves` | `knownValues`, and each of its `rows` | Each curve, as the DDMS names it, mapped to its values at keys (`at`) and its measures, each to its proof value. What the assertion lists every row is held to; a row adds its own, and a value both list is refused. A row holds proof values for at most 100 curves. |
| `curveIndex` | `knownValues` | The curve the rows of the bulk data are indexed by: the key of `at`, and where `first` and `last` are read. Left out, the curve each record names at `data.ReferenceCurveID`. |

The test needs a `bulk` block, which says the DDMS collection the kind's bulk data is in and the most rows it reads
([Bulk data](assertion.md#bulk-data)).

## Values at a key

`at` maps keys to the curve's value there. A key is a value of the curve the rows are indexed by: a depth of the log's
reference curve (`MD`), or a time for a log indexed by time (`"2026-01-01T00:01:00Z"`). The row whose index holds the key
is the row checked, and its value of the curve is held to the proof value.

- A key is matched exactly as written: `1500.1524` is the row the DDMS holds as 1500.1524, whatever binary value either
  side stores, and `1500` is `1500.0`. A time is matched as the instant it names, so `2026-01-01T01:01:00+01:00` is
  `2026-01-01T00:01:00Z`.
- The value is compared as any proof value compares: a number within its `tolerance`, text as text (a lithology curve's
  `SAND`).
- A curve checks its value at 1,000 keys at most; two keys naming one number (`1500` and `1500.0`) are refused.

Where no row holds the key, the reason says where it would lie, so a depth off by a rounding is found at once:
`GR at 1500.1 has no row: the index MD holds no 1500.1 (nearest: 1500 in row 3 and 1500.1524 in row 4)`. A key outside
the log says how far the log runs instead, and a key the index holds in more than one row names those rows, since a key
names one row.

## The measures

| Measure | Of the curve |
| --- | --- |
| `at` | Its value in the row whose index holds each key ([Values at a key](#values-at-a-key)). |
| `count` | The rows it has a value in. |
| `missing` | The rows it has no value in: a null, or a number that is not finite (the DDMS writes a gap in a float curve as NaN). |
| `min`, `max` | Its smallest and largest value; for a curve of ISO 8601 dates, its earliest and latest. |
| `sum`, `avg` | The sum and the mean of its numbers. |
| `first`, `last` | The index at the first and the last row it has a value in: where the curve starts and ends, such as its top and base depth. |
| `step` | The difference between its numbers row after row, rows without a value passed over: every one of them meets the proof value. |

`min`, `max`, `sum` and `avg` take the curve's values as a column `aggregate` takes them; a value that is neither a number
nor a date is left out of them, and the result says how many were. A `step` is taken between the numbers as they are
written, so depths written 0.1524 apart are 0.1524 apart, not the 0.15240000000002 their binary values differ by, and
`step: 0.1524` holds of a log sampled every 0.1524 m without a tolerance.

## Proof values

Each measure, and each key of `at`, takes the value it equals (`count: 5120`), or a mapping of one comparison with what
qualifies it, as a [proof value](known-values.md#proof-values) of a record does:

| Written | Holds when |
| --- | --- |
| `<measure>: <value>` | The measure equals it. |
| `<measure>: { <comparison>: <operand> }` | The measure meets the comparison: `equals`, `notEquals`, `atLeast`, `atMost`, `greaterThan`, `lessThan` or `between: [low, high]`. |
| `tolerance` | How far a number may be from the operand and still meet it. Left out, the assertion's `tolerance`. |
| `warnWithin` | How far the measure may miss and only warn ([A warning band](assertion.md#a-warning-band)). Left out, the assertion's `warnWithin`. |
| `disabled` | Why it is not checked; the result counts it as disabled. |

`count` and `missing` compare whole numbers of zero or more. `min`, `max`, `first` and `last` compare numbers, or ISO 8601
dates for a curve or an index of dates. `step` holds every step to one range, so it takes no `notEquals`. A value at a key
compares whatever the curve holds.

The DDMS keeps a curve in the type it was delivered in, and many logs hold their values as 32-bit floats: a source's
74.2 reads back as 74.19999694824219. Give the known values a `tolerance` (`tolerance: 0.0001` serves every proof value
that gives none) so such values compare as the source states them. A depth held as a 64-bit float, as an index curve
usually is, compares exactly as written, and so do `first`, `last`, `step` and the keys of `at`.

## How the curve data is read

Each record a row finds is read once, however many rows find it, for every curve those rows name and its index curve
when `at`, `first` or `last` needs it, through the DDMS route the bulk assertions read by: its description (its curves and
its row count), then only those curves, a page at a time. Records are read at once as the flow's
`parallelism.recordsAtOnce` allows. Every row of the bulk data is read; a log holding more rows than `bulk.maxRows` cannot
be, and `sample` does not change that. A `bulk.columns` list must include every curve the proof values name, and
`curveIndex`; a list leaving one out is refused when the flow loads.

The result holds the measures and the values at the keys checked, never the rest of the curve.

## What a row comes to

A row's curve proof values are judged with its record's proof values, and the row comes to one verdict
([What a row comes to](known-values.md#what-a-row-comes-to)). Its reason names each proof value its log does not hold:

| Reason | When |
| --- | --- |
| `GR at 1500.1524 is 70.4 (row 3121), expected it equals 74.2` | The value at a key differs; the row is the one holding the key. |
| `GR at 1500.1 has no row: the index MD holds no 1500.1 (nearest: 1500 in row 3 and 1500.1524 in row 4); expected it equals 74.2` | No row holds the key. |
| `GR at 2000 has no row: the index MD holds no 2000 (it runs from 1000 in row 1 to 1780.1356 in row 5120); expected it equals 1` | The key lies outside the log. |
| `GR at 2 is in 2 rows (2, 3), and a key names one row; expected it equals 2` | The index holds the key in several rows. |
| `GR at 1000 has no value (row 1); expected it equals 5` | The row holding the key has no value of the curve. |
| `GR max is 120, expected it equals 125` | A measure differs. |
| `MD step is from 0.1524 (to row 12) to 0.3048 (to row 40) over 5119 steps, expected each step equals 0.1524` | A step differs: the smallest and the largest, and the rows they end at. |
| `GR first is 1000.1524 (row 2), expected it equals 1000` | A curve starts at another depth than stated; the row is the curve's first value. |
| `CALI is not among the curves of its bulk data (MD, GR, DT)` | The bulk data holds no curve of that name. |
| `GR at 1500.1524 cannot be read: the record names no data.ReferenceCurveID, so the curve its rows are indexed by is not known; name it with the assertion's curveIndex` | No index curve for `at`, `first` and `last`; also when the index curve is not among the bulk data's curves. |
| `GR max has no value: GR holds no value in any of its 5120 rows; expected it equals 150.2` | The curve has no value to measure. |
| `its curves cannot be measured: the DDMS holds no bulk data for the record` | The DDMS answers 404. |
| `its curves cannot be measured: its bulk data holds 2400000 rows, more than the test's bulk.maxRows 2000000, ...` | Raise `bulk.maxRows`, at most 100,000,000. |
| `its curves cannot be measured: the DDMS describes its bulk data as 5120 rows and answered 5000, ...` | A read that came short, which would measure part of the curve as the whole. |

A row finding more than one record reads none of their curves; it fails on that, naming the record proof values each
holds. The assertion's result counts curve proof values among its proof values and says how many records' curves it read:
`1 row(s) with 22 proof value(s): 1 hold their values, ...; the curves of 1 record(s) measured`.

## Refused when the flow loads

`<at>` is where the known values are written, such as `flow.yaml: tests[0] 'logs': assert[0].knownValues`. The editor
marks a key that is not a measure as it is typed.

| Message | Cause |
| --- | --- |
| `<at> holds curve proof values, which are measured over each record's bulk data; give the test a bulk block (bulk: { maxRows: 1000000 }).` | No `bulk` block. |
| `<at>.curves.GR maps the curve's measures to their proof values, such as GR: { count: 5120, max: 150.2 }; a measure is one of count, missing, min, max, sum, avg, first, last, step, at.` | A curve not mapped to measures. |
| `<at>.curves.GR has 'median', which is not a measure of a curve; ...` | A measure that is not one. |
| `<at>.curves.GR.at maps keys of the curve the rows are indexed by to GR's value there, such as at: { 1500.1524: 74.2 }; ...` | `at` not a mapping of keys. |
| `<at>.curves.GR.at has the key "top"; a key is a depth of the curve the rows are indexed by, such as 1500.1524, or a time, ...` | A key that is neither a depth nor a time. |
| `<at>.curves.GR.at names the key 1500 twice; give each row once.` | Two keys naming one number, such as `1500` and `1500.0`; keys written apart say both (`as 1500 and 1500.00`). |
| `<at>.curves.GR.max has 'matches'; a curve proof value is the number the measure equals, or a comparison: ...` | A key a proof value does not take; also a value of `~`, a list, or a mapping with no comparison. |
| `<at>.curves.GR.avg compares GR's avg, which is a number; give a number, not "high".` | An operand of another type. |
| `<at>.curves.GR.count compares how many rows GR has a value in, a whole number of zero or more, not 5.5.` | A count that is not a whole number. |
| `<at>.curves.MD.step holds every step of MD to it, and notEquals is not a range every step can lie in; ...` | `notEquals` on a step. |
| `<at>.curveIndex names the curve a curve's first and last values are read at, and no row holds a first or last proof value; ...` | A `curveIndex` nothing reads. |
| `<at>.rows[0] checks GR max, which the assertion lists for every row already; give it in one place.` | A value both the assertion and a row list. |
| `flow.yaml: tests[0] 'logs': bulk.columns lists the only columns the test reads, and leaves out 'GR' (assert[0]); ...` | A `bulk.columns` list without a curve the proof values name. |

## See also

- [Known values](known-values.md): how a row finds its record, and its record's proof values.
- [Bulk data](assertion.md#bulk-data): the `bulk` block, and the assertions over every record's bulk data.
- [Testing what OSDU holds](../guides/data-quality-tests.md#6-check-the-curve-data-in-the-wellbore-ddms): curve data in a test.
