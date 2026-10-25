---
id: delivery-flow-known-values
title: "Known values (knownValues): proof values of records found by what they hold"
type: flow-reference
summary: "Rows of proof values a sheet states about records it does not name by id: each row finds its record by full paths and values, then holds it to its proof values."
keywords:
  - knownValues
  - known values
  - proof values
  - locateBy
  - check
  - locate record by attributes
  - full path
  - parent record path
  - child record path
  - path into an osdu record
  - list of paths through a reference
  - not found
  - found more than once
  - warning band of a proof value
  - qc sheet
  - find a well log by its wellbore name
  - check curve values of a well log
  - locate a record by a value of its parent
yamlPath: "(flowType: assertion) tests[].assert[].knownValues"
related:
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
  - delivery-flow-dimension
  - delivery-flow-mapping-assertions
  - delivery-flow-mapping-lookups
  - delivery-concept-templates
  - delivery-concept-search-paging
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/ConditionReader.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/KnownValuesShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/KnownValuesEvaluator.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/TestEvaluator.cs
  - osdu/src/SqlFlow.Delivery.Search/OsduQuery.cs
---

# Known values (knownValues): proof values of records found by what they hold

A `knownValues` assertion holds records to values a source states about them without naming them by id: a QC sheet
that says the COMPOSITE log of wellbore A/1-F-11 has a DT curve in `us/ft` from 1023.0612 m. Each row finds its record
by full paths and the values the record holds there (`locateBy`), then holds that record to its proof values (`check`),
each a full path and the value, or the condition, the record has to hold there. Every path is a path in the OSDU record
([Full paths](#full-paths)).

```yaml
  - name: composite-log
    kind: osdu:wks:work-product-component--WellLog:1.5.0
    assert:
      - name: the curves of the COMPOSITE log of A/1-F-11
        knownValues:
          locateBy:
            '[data.WellboreID, data.FacilityName]': A/1-F-11
            tags.SourceProject: NORTH
            data.Name: COMPOSITE
          check:
            data.SamplingInterval: { equals: 0.1524, tolerance: 0.00005 }
          rows:
            - locateBy: { data.Curves.CurveID: DT }
              check:
                data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:us%2Fft:"
                data.Curves.TopDepth: { equals: 1023.0612, tolerance: 0.00005 }
                data.LogSource: { equals: OPERATOR, disabled: the log source is renamed next release }
            - locateBy: { data.Curves.CurveID: GR }
              check:
                data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:gAPI:"
            - locateBy: { data.Curves.CurveID: CALI }
              disabled: CALI is not delivered yet
```

| Key | Meaning |
| --- | --- |
| `locateBy` | Full paths and the values every row is located by, as `path: value` (or `path: { equals: value, ignoreCase, tolerance }`). |
| `check` | Proof values every row holds its record to, as `path: value` or `path: <condition>`. |
| `rows` | 1 to 1,000 rows, each with its own `locateBy` and `check`, added to the shared ones (a path both give is refused), and an optional `disabled`. Left out, the shared paths are the one row. |
| `tolerance`, `ignoreCase` | What a proof value takes when it says nothing itself: the tolerance where it compares a number, ignoreCase where it compares text. |
| `warnWithin` | The [warning band](assertion.md#a-warning-band) every proof value comparing a number takes when it gives none: a distance (`5`) or a percentage (`10%`). |
| `notFound` | The severity of a row whose record is not found: `error`, `warning` or `info`. Left out, the assertion's own, so such a row fails it. |

A row is located by 1 to 8 paths and checks at most 60 proof values. No two rows are located alike, and a row is not
located and checked by the same path.

## Full paths

Every path is a path in the OSDU record, from its root, written as the assertion's [`field`](assertion.md#assertions), a
mapping's [assertions](mapping-assertions.md#the-record-stage) and a [dimension](dimension.md#paths) write one:
`data.Name` is the `Name` in the record's `data` block, the property a mapping's `record:` block writes under `data:` and
its template names `osdu.data.Name`, and `tags.SourceProject` is one of the record's tags. An assertion flow reads only
what OSDU holds, so no path names a source column.

A path reads another record as a list of paths, read step by step as a dimension's
[label and attributes](dimension-attributes.md) are: each entry but the last reads the reference the next
record is found by, and the last reads the value in it. One kind of entry steps the other way: `group--Entity[reference]`
is the records of that type whose reference names the record reached so far, and the entry after it reads them.

| Written | Reads |
| --- | --- |
| `data.Name` | The record's own value. |
| `'[data.WellboreID, data.FacilityName]'` | The value in the record the reference names (its parent): the name of the wellbore a log belongs to. The reference's schema relationship says which type of record it names, and has to name one. |
| `'[data.WellboreID, data.WellID, data.FacilityName]'` | Steps chain: the name of the well of the wellbore of the log. |
| `'[work-product-component--WellLog[data.WellboreID], data.Name]'` | The values in the records of that type whose reference names the record (its children): the names of a wellbore's logs. |
| `'[data.WellboreID, work-product-component--WellLog[data.WellboreID], data.Name]'` | The names of every log of the same wellbore. |

A list reads through at most 4 paths: the record's own and three more. It is quoted, in a block mapping and inside
`{ }` alike, since a key is text and YAML reads an unquoted `[` as the start of a list; an entry inside it may be quoted
too, as a dimension's list quotes a path with a filter. A path names properties, never an item by its position (`[0]` is
refused); `[*]` is implied. A dimension's filter on a segment (`[Property=text]`) is not taken: a row picks the item of a
list it reads by locating it, as below.

**One item of a list.** A row located by a path inside a list of objects (`data.Curves.CurveID`) is one item of that list:
the log found by the row's other paths, and the curve in it with that mnemonic. Every proof value under the same list
(`data.Curves.CurveUnit`, `data.Curves.TopDepth`) is read from that one item, as a mapping assertion's `where` field in
the same repeated array reads the same item; any other is read from the record. A row is located inside one list at
most, and also by a value of the record, so it says which record the item is in.

## Proof values

| Written | Holds when |
| --- | --- |
| `path: value` | Every value the path reads equals it (numbers as numbers, text ordinally). |
| `path: ~` | The path reads no value. |
| `path: { <comparison>: <operand> }` | Every value the path reads meets the condition: `equals`, `notEquals`, `in`, `notIn`, `atLeast`, `atMost`, `greaterThan`, `lessThan`, `between`, `matches`, `notMatches`, `startsWith`, `endsWith`, `contains`, `notContains`, `exists`, `empty`, `type` or `length`, as a [field condition](assertion.md#conditions) takes them, with `tolerance` and `ignoreCase`. |
| `path: { ..., values: any }` | One value the path reads meets it. |
| `path: { equals: 1023.0612, warnWithin: 1% }` | As `equals`; a value off by no more than 1% of it differs only within the [warning band](assertion.md#a-warning-band). |
| `path: { aggregate: count, except: [MD], equals: 20 }` | The aggregate (`min`, `max`, `sum`, `avg`, `count`, `distinct`) of the values the path reads, `except` the values listed, compares true: the curves of a log, the logs of a wellbore. |
| `path: { ..., disabled: <why> }` | Not checked; the result counts it as disabled. |

A path that reads no value fails every condition but `exists: false` and `empty: true`, and says why when it reads
through a reference: `(data.WellboreID holds no reference)`, or `(data.WellboreID names <id>, which storage does not return)`.

## What a row comes to

| Verdict | When | Its example in the result |
| --- | --- | --- |
| holds | One record (or item) holds the row's values, and it holds every proof value. | none |
| within the warning band | One record holds them, and every proof value it misses, it misses by no more than its `warnWithin`. | `differs within the warning band in 1 proof value(s): data.SamplingStart is 205.1304, expected it equals 200; 5.1304 above 200 (2.57%), within the warning band of 5%, so it warns` |
| differs | One record holds them, and some proof values do not hold. | `differs in 2 proof value(s) of item 2 of data.Curves: data.Curves.CurveUnit is "dev:reference-data--UnitOfMeasure:API:", expected it equals "dev:reference-data--UnitOfMeasure:gAPI:"; ...` |
| not found | No record holds them, at the first path the records run out. | `not found: no master-data--Wellbore record holds data.FacilityName "B/2-X-7"`, `not found: of the 2 records found with ..., none has data.Name "PRESSURE"`, `not found: 1 record with ... hold no item of data.Curves with data.Curves.CurveID "BS"` |
| found more than once | Several records (or items) hold them. | `found 2 times: <id> holds its values; <id> differs in data.SamplingStart` |

The assertion fails, at its severity, when a row differs or is found more than once, at `notFound`'s when a row is
not found, and at `warning` (or its own severity, when milder) when rows differ only within the warning band, so a test
whose rows are only a little off is `warned`; it passes when every row holds. When it fails for several reasons, it weighs
the heaviest. What it found counts each verdict, the proof values checked and disabled, the
records read, and the paths differing most:
`3 row(s) with 6 proof value(s): 1 hold their values, 1 differ, 1 not found, 0 found more than once; 4 record(s) read in 3 search(es); disabled: 1 row(s), 1 proof value(s); differing most in data.Curves.CurveUnit (1)`,
which counts the rows within the warning band as well (`..., 2 within the warning band, ...`) when a proof value has one.
Its examples are the rows that do not hold, each named by the values it is located by, up to the flow's `examples`.

## How the records are found

Known values do not read every record the test's query matches. Rows located by the same paths are searched together,
fifty at a time: the test's query, narrowed by the values the rows are located by, each compared exactly as a
[`$search`](mapping-lookups.md#how-each-line-is-asked) compares it (the `keyword` sub-field of text, `nested(...)` inside a
nested list). A path that leaves the record narrows by the records it reaches: the records of the type at its end holding
the values are searched first (`*:*:master-data--Wellbore:*` for wellbores by name), then, step by step back, the records
naming them, so the test's records are searched for references starting with those ids, which finds a reference written
with a version as well as one without. A path inside a list, or one the index cannot compare, narrows nothing and is
judged once the records are read. Every value is then compared exactly with what was read, so a wellbore named
`A/1-F-11 A` is not taken for `A/1-F-11`.

The records the paths reach from the records found (the ones they name, from storage; the ones naming them, found by a
search of their type) are read once each, whichever row reaches them. A test reads at most its `maxRecords` records,
and errors past it; a search through other records that finds more than 1,000 does not narrow, and its rows are judged
all the same; at most 200,000 records are reached through others.

## Checked against the templates

Every path is checked against the template of each kind it reads, as the template folders on the flow's path give it
([template folders](../concepts/templates.md#template-folders)): the test's own, and for a step to other records the
newest template the flow reads of that entity type (a reference's type, named by its schema relationship, or the type a
step to the records naming one names). A path that is not a property, a reference that names no one type of record, a
step to records the index cannot find by their reference, a type no template folder on the flow's path holds, and a value
a property cannot hold leave the test `errored`, each said once:

- `'<label>': '<path>' follows data.Name, which is a string in <kind>, not a reference to a record.`
- `'<label>': no template folder on the flow's path holds a template of master-data--Well, so '<path>' cannot be checked against what those records hold. ...`
- `'<label>': a row is located by paths inside data.Curves alone, so nothing says which record its item is in; ...`

## See also

- [Assertion flow](assertion.md): the tests known values are part of, and how a test is disabled and its proof values dated.
- [Testing what OSDU holds](../guides/data-quality-tests.md#values-a-sheet-states-about-records-it-does-not-name): a sheet's rows as known values.
- [Mapping lookups](mapping-lookups.md#how-each-line-is-asked): how a value is compared exactly in a search.
- [Dimension flow](dimension-attributes.md): the list of paths a label reads through references.
