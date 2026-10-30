---
id: delivery-flow-mapping-assertions
title: "Mapping assertions ($assert): business rules a mapping states beside a property, judged on every record"
type: flow-reference
summary: "Rules a mapping states beside a property: what its value must be on every record, and whether a record that breaks one is held, sent, or sent without the value."
keywords:
  - "$assert"
  - mapping assertion
  - business rule
  - data quality rule in a mapping
  - value must be between
  - onfail
  - "onfail: hold"
  - "onfail: report"
  - "onfail: omit"
  - stage incoming
  - placeholder value
  - "values: any"
  - assertion failures
  - held for an assertion
related:
  - delivery-flow-mapping-values
  - delivery-flow-mapping
  - delivery-flow-assertion
  - delivery-concept-preflight
  - delivery-concept-record-lifecycle
  - delivery-concept-ledger
  - delivery-concept-explorer
  - delivery-cli-values
yamlPath: "(documentType: mapping) record.<property>.$assert"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/ConditionReader.cs
  - osdu/src/SqlFlow.Delivery/Model/NodeAssertion.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingAssertionJudge.cs
  - osdu/src/SqlFlow.Delivery/Validation/AssertionFindings.cs
  - osdu/src/SqlFlow.Delivery/Validation/ValidationVerdict.cs
  - osdu/src/SqlFlow.Delivery/Validation/ValidationTally.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/ValueComparer.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/Evaluators.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/ValidationGate.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueTally.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerChecks.cs
  - osdu/src/SqlFlow.Delivery/Engine/SyncedMappings.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Storage/WorkBatchFile.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261010205917_RecordAssertions.cs
  - osdu/src/SqlFlow.Delivery.Cli/FindingLines.cs
  - osdu/tests/SqlFlow.Delivery.Tests/MappingAssertionLoaderTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/MappingAssertionRenderTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/MappingAssertionLedgerTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ValidationGateTests.cs
  - osdu/docs/census/keys.mapping.json
---

# Mapping assertions ($assert): business rules a mapping states beside a property, judged on every record

A template says what OSDU accepts: the type of a value, its pattern, the properties a record must carry. It cannot say
what the source's values mean. A mapping can: beside a property, `$assert` states what its value must be for the record
to be right, such as "the elevation of the depth reference lies between 0 and 250", "the interval is never the source's
placeholder" or "every log carries a depth curve". Each assertion is judged on every record the mapping renders, and says
what a record that breaks it does: it is **held** with its document kept, it is **sent** and the failure recorded, or the
value is **left out** and the record sent without it.

What a record's assertions found is part of the verdict of the check before sending, recorded on every attempt, and the
record keeps how many failed. Whether a record met its mapping's rules, which ones it broke and with what value is
therefore answered from its history alone ([ledger](../concepts/ledger.md)).

The conditions are the assertion flows' own ([conditions](assertion.md#conditions)): one reader and one comparer serve
both, so a rule a mapping states and a test an assertion flow states mean the same thing. The same rules can be judged on
what OSDU already holds, from the explorer and from an assertion flow ([below](#judging-what-osdu-already-holds)).

## An example

Four rules on the well logs of the well database, written into `mappings/WellLog@1.4.0.yaml` beside the properties they
judge (the rest of the mapping as in the sample):

```yaml
record:
  data:
    SamplingInterval:
      $from: index_increment
      $required: false
      $assert:
        - name: interval-placeholder
          description: The export writes -999.25 where a depth log has no regular interval.
          stage: incoming
          notIn: [-999, -999.25]
          onFail: omit
          where:
            - column: index_unit
              in: [M, FT]
    VerticalMeasurement:
      VerticalMeasurement:
        $from: elev_meas_ref
        $modifiers:
          - split: { separator: " ", part: 1 }
        $assert:
          - name: reference-elevation
            description: The depth references of the well database lie between sea level and 250 m above it.
            between: [0, 250]
    Curves:
      $forEach: curves
      $assert:
        - name: index-and-one-curve
          length: { atLeast: 2 }
          onFail: report
      $item:
        Mnemonic:
          $from: curve_id
          $assert:
            - name: a depth curve is among the curves
              in: [MD, DEPT]
              values: any
            - matches: '^[A-Z0-9_]{1,16}$'
              onFail: report
              where:
                - field: data.Curves.CurveID
                  notEquals: MD
```

What each line reads, and what it makes of a record:

| Line | Reads | Makes |
| --- | --- | --- |
| `SamplingInterval` `stage: incoming` | `index_increment` of the log's header row, as the ingestion table holds it, before any modifier | The source's own value is judged, not the number the record would carry. |
| `notIn: [-999, -999.25]` | that value | Fails a row holding either placeholder; text holding the number fails too, since text holding a number equals it. |
| `where: [ { column: index_unit, in: [M, FT] } ]` | `index_unit` of the same row | Only logs indexed in metres or feet are judged; for any other log the assertion is not judged at all. |
| `onFail: omit` | | A row that fails gives the record no `data.SamplingInterval`; the record is sent without it and the failure is recorded. |
| `$required: false` | | Lets the record go without the property, which `omit` needs. |
| `VerticalMeasurement` `between: [0, 250]` | `data.VerticalMeasurement.VerticalMeasurement` as the record carries it: `elev_meas_ref` (`42.8 M`) after `split`, written as the number `42.8` | A log whose depth reference lies outside the range is held at the check before sending, its document kept until a release sends it. `stage: record` and `onFail: hold` are the defaults. |
| `Curves` `length: { atLeast: 2 }` | the `data.Curves` array as written | A log with fewer than two curves (the index curve and one more) is sent, and the failure recorded (`onFail: report`). |
| `Mnemonic` `in: [MD, DEPT]`, `values: any` | `data.Curves[].Mnemonic` of every curve of the record, together | A log none of whose curves is a depth curve is held. Without `values: any`, every curve would have to be one. |
| `Mnemonic` `matches: '^[A-Z0-9_]{1,16}$'` | each curve's mnemonic | A curve whose mnemonic is not upper case letters, digits and underscores is reported, as `data.Curves[<n>].Mnemonic`. |
| `where: [ { field: data.Curves.CurveID, notEquals: MD } ]` | the `CurveID` of the same curve | The index curve is not judged: a field inside the same repeated array reads the same item. |
| `name`, `description` | | What the hold message, the verdict, the ledger and the reports call the assertion. Left out, the label is read off the condition (`matches "^[A-Z0-9_]{1,16}$" where data.Curves.CurveID notEquals "MD"`). |

The record shape draws each assertion as a note on its property
(`osdu.data.VerticalMeasurement.VerticalMeasurement: asserts between 0 and 250 of the value the record carries (reference-elevation); a record that fails it is held, its document kept until a release accepts it`),
so the mapping's Record shape tab and `POST /api/v1/delivery/mapping-builder/shape` show the rules beside the record
([the record shape](mapping.md#the-record-shape)).

## The words of an assertion

`$assert` is a list of 1 to 20 assertions. Its keys are words of the assertion, not of the record, so they carry no `$`,
as a modifier's settings do not:

| Key | Default | Meaning |
| --- | --- | --- |
| one condition | required | Exactly one of `equals`, `notEquals`, `in`, `notIn`, `atLeast`, `atMost`, `greaterThan`, `lessThan`, `between`, `matches`, `notMatches`, `startsWith`, `endsWith`, `contains`, `notContains`, `exists`, `empty`, `type`, `length`, with `ignoreCase` and `tolerance` beside it, written and compared exactly as an assertion flow's field condition ([conditions](assertion.md#conditions)). `resolves` is refused ([below](#why-resolves-is-not-a-condition-of-a-mapping)). |
| `stage` | `record` | Which value is judged. `record`: the value the record carries, after the modifiers and the conversion to the property's type, which is what OSDU holds. `incoming`: the value the row gives the node before its modifiers, the column or what `$expr` computes. |
| `onFail` | `hold` | What a record that breaks it does. `hold`: it is not sent; its document is kept until the row or the mapping changes, or a release sends it. `report`: it is sent, and the failure recorded. `omit`: the value that fails is left out, and the failure recorded. |
| `values` | `all` | For a property holding several values (the items of a repeated array, a list of values): `all`, every value has to meet it; `any`, one has to. Record stage only. |
| `where` | none | 1 to 10 conditions selecting when the assertion is judged, every one of which has to hold. On the record stage each names a `field` of the record; on the incoming stage each names a `column` of the row the node reads, or `$dataset.<column>` for the record table's row. Each takes one operator, `ignoreCase` and `tolerance`. |
| `name` | a label read off the condition | 1 to 200 characters: what messages, the ledger and the reports call it. Each assertion of a node has a name of its own. |
| `description` | none | Free text. |

A condition holds of a field or a column as an assertion flow's `where` does: one of the values it reads has to meet it,
and `exists: false` and `empty: true` hold of a value that is not there. A `where` field of the record stage starts at a
root of the record (`data`, `acl`, `tags`, ...); arrays are crossed implicitly or with `[*]`.

## Where an assertion is written

| Node | Record stage | Incoming stage |
| --- | --- | --- |
| `$from`, `$expr` | yes | yes |
| `$cache`, `$search`, `$lookup` | yes | no: the value comes from the cache, a search of the platform or a lookup's record, not from the row |
| `$parent`, `$child`, `$base` | yes | no: the value comes from the record's parent, the reference to its child, or the record an enrichment renders into, not from the row |
| `$link` | refused: it takes only `$when` and `$description` | refused |
| `$coalesce` | yes, beside the list: the value of whichever alternative gave it | no: the value is the first its alternatives give |
| `$forEach` | yes: the array as a whole, which the preflight lets `exists`, `empty` and `length` judge | no |
| A property of a `$forEach` item, or of an item of a list of objects | yes, in each item | as its node allows |
| A literal, or a `$value` | refused: every record is given the same value | refused |
| An alternative of `$coalesce` | refused: `$assert` goes beside `$coalesce`, since it decides for whichever alternative gives the value | refused |
| An item of a list of values | refused: the list has no node of its own; assert on the property that writes the list | refused |

An object of properties is not a node, so its assertions are written on the properties inside it.

### Why resolves is not a condition of a mapping

Whether a referenced record exists is checked where references are made: an id the `id` or `ref` modifier builds is
looked up in the partition's cache ([mapping modifiers](mapping-modifiers.md)), and `target.verifyReferences: storage`
looks every reference up in OSDU's storage service before the record is sent ([preflight](../concepts/preflight.md#references-checked-in-storage)).
An assertion flow's `resolves` checks what OSDU holds afterwards ([assertion flow](assertion.md#conditions)).

## How a record is judged

### The record stage

The record-stage assertions are judged once the record is assembled, on the record as it will be sent, and before any
value is left out, so every assertion judges the same record whatever another leaves out. The same code judges a record
OSDU stores, so a rule gives the same answer for a document rendered here and for the record OSDU holds.

- **One value.** A property outside any repeated array is judged once (`data.VerticalMeasurement.VerticalMeasurement`).
- **Repeated items.** A property inside a `$forEach` item or an item of a list of objects is judged in each item, and a
  failure names the item (`data.Curves[3].Mnemonic`), beside the property every record shares (`data.Curves[].Mnemonic`).
- **Lists.** A property holding a list of values is judged value by value (`data.CandidateReferenceCurveIDs[0]`),
  except by `exists`, `empty`, `length`, `contains` and `notContains`, which judge the list whole.
- **`values: any`.** The assertion holds when one value meets it: in a repeated array, the values of every item its
  `where` selects, judged together once; on a list, the list's values. A failure says
  `none of its <n> values meets it; one <why the first failed>`. With no value to judge (no item, or none selected), it
  is not judged.
- **`where` fields.** A field inside the same repeated array as the property (`data.Curves.CurveID`,
  `data.Curves[*].CurveID`) reads the same item; any other field, or a path with an index (`data.Curves[0].CurveID`),
  reads the record.

### The incoming stage

An incoming assertion is judged as the node reads its value, for the row it reads (the record table's row, or under a
`$forEach` the child row), before the modifiers change it: the column as the ingestion table holds it, or what `$expr`
computes. It is judged only when the node's `$when` holds; an expression that cannot be computed holds the record, as it
always does, and nothing is judged. A failure names where the value came from: `dataset.index_increment`, a child row
counting from zero (`dataset.curves[3].curve_id`), or the row and the expression (`dataset.curves[3]: <expression>`).

A value that fails an incoming assertion with `onFail: omit` is left out before the modifiers run, so the node gives no
value; a record-stage assertion of the same property then finds none.

### Values

- **No value.** A property the record does not carry (no value, a `$when` that does not hold, an optional node that gave
  nothing), a null, and blank text from a row are no value. No value is judged only by `exists` (it fails
  `exists: true`) and `empty` (it meets `empty: true`); every other condition passes it over. Whether a property must be
  there is `$required`'s and the schema's to say, so give `exists: true` a `where` when the property is written only on
  some records.
- **Comparisons.** Values compare as the assertion flows compare them: numbers as numbers (text holding a number against
  a number), ISO 8601 dates as the instants they name, text ordinally or ignoring case with `ignoreCase`, `tolerance`
  for numbers. A value that cannot be compared fails with the reason, such as
  `is a string (n/a), which does not order against the number 0`. `length` counts text in UTF-16 code units, and a
  list by its items.
- **Patterns.** `matches` and `notMatches` take at most one second per value. A pattern that runs out of time fails its
  assertion (`took longer than 1s to match; the expression backtracks too much for a value this long`) rather than hang
  the render.

## What a record that fails does

| `onFail` | The document | The record | The failure is counted as |
| --- | --- | --- | --- |
| `hold` (default) | Unchanged | Held at the check before sending, its document kept. A release sends that document as it is. | held |
| `report` | Unchanged | Sent | reported |
| `omit` | The failing value left out | Sent, unless another assertion holds it | omitted |

Several assertions on one value are each judged on their own, and each failure is recorded; any failure whose action is
`hold` holds the record.

### Leaving a value out: omit

- The property is removed from the object that holds it; an object left with nothing is removed with it, up to `data`,
  which always stays. An item of a repeated array or of a list of objects left with nothing is removed from its list.
- A property whose value is a list (a `$forEach` array that fails `length`, a list read whole) is written empty, since the
  mapping defines it.
- On a list of values judged value by value, the failing value is removed from the list; the list stays, empty when it
  held nothing else.
- `omit` needs `$required: false` on the node, since the record may then go without the property, and the preflight
  refuses `$required: false` on a property the schema requires. It is not taken with `values: any`, which judges the
  values together, so no one value fails.
- What is left out is out of the document before it is hashed, so the change detection sees the record OSDU will hold.

## The check before sending

The findings travel with the document they judge: the work batch line holds them beside the rendered document, so the
check before sending reads the findings of exactly the document it sends ([preflight](../concepts/preflight.md#validation-before-a-record-is-sent)).

- **The verdict gains `assertions`.** The mapping, how many judgements were made and how many failed, held, were reported
  and left out, and the failures, each with its property (`at`), where it was found (`path`), the assertion, its
  `stage` and `onFail`, why it fails (`message`, at most 500 characters) and the value (at most 200 characters). Every
  value and message is redacted before it is kept. A record lists at most 50 failures and counts every one.
- **A failure whose action is `hold` holds the record**, with its document kept, whatever `target.validation` says; one
  that reports or leaves a value out never does. The schema check goes on as before: when the policy holds the record
  for its schema too, the message says both.
- **`target.verifyReferences: storage`** still holds a record that names an id storage does not hold, before anything
  else and whatever a release accepted.
- **A try that sends the payload alone** checks nothing of the document and judges no assertion.
- **A document queued before mappings could assert** carries no findings and is judged by its schema alone.

The hold's error names the property, the assertion and the value of the first three failures that hold (and how many
more), the values in single quotes, so the records one assertion holds share one issue whatever values they hold
([issues](../concepts/record-lifecycle.md#issues)):

```text
assertion: the record fails what its mapping WellLog@1.4.0 asserts: data.VerticalMeasurement.VerticalMeasurement fails "reference-elevation" with '312'. Its mapping holds a record that fails them (onFail: hold), so it is held; release it to send this document as it is, or correct the source or the mapping.
```

The attempt's verdict (`result.validation`) carries the findings:

```json
"assertions": {
  "mapping": "WellLog@1.4.0",
  "checked": 12,
  "failed": 1,
  "held": 1,
  "reported": 0,
  "omitted": 0,
  "failures": [
    {
      "at": "data.VerticalMeasurement.VerticalMeasurement",
      "path": "data.VerticalMeasurement.VerticalMeasurement",
      "assertion": "reference-elevation",
      "stage": "record",
      "onFail": "hold",
      "message": "is 312, outside 0 to 250",
      "value": "312"
    }
  ]
}
```

A verdict longer than 16,000 characters lists fewer failures (10, then 3) and says `shortened`; the counts stay exact.

### Releasing a record an assertion holds

A release accepts the held document as it is: the next try sends it, and its verdict is marked accepted while still
listing what the assertions found ([releasing a record](../concepts/record-lifecycle.md#releasing-a-record)). A held
record whose mapping then changes keeps the document and the findings it was held for, and a release sends that
document; a redelivery plans it again under the new mapping. Correcting the row plans it again on the next run.

### What the ledger keeps

- Each attempt's `result.validation.assertions`, as above.
- `osdu.Record.AssertionFailures` and `osdu.RecordEvent.AssertionFailures`: how many judgements failed on the last
  document the check before sending read (held, reported or left out); `0` when every one was met, null when the mapping
  states no assertion or no check has reached the record. A try that sends the payload alone keeps the last count. A
  filtered index holds the records whose last checked document failed one (migration `RecordAssertions`, module version
  1.39.0; [ledger](../concepts/ledger.md)).

### A document that does not change is not judged again

A record whose rendered document is the one OSDU already holds is not sent again, so the check before sending does not
judge it, and its ledger row keeps what its last check found. Adding or changing an assertion moves the mapping's
fingerprint, so the next run renders every record of its scope again ([when a mapping changes](mapping.md#when-a-mapping-changes));
only the records whose document changed (a failing `omit` changes it) reach the check before sending. To judge what OSDU
already holds, use the explorer or an assertion flow.

## Where the findings are shown

| Where | What it shows |
| --- | --- |
| A plan (`sqlflow run ... --operation plan`) | Each delivery line adds what its assertions found and whether it would be held (`to be held before it is sent, as 1 of 12 assertion judgement(s) of WellLog@1.4.0 failed (1 holding), first data.VerticalMeasurement.VerticalMeasurement "reference-elevation"`); the summary adds `of the deliveries, <n> fail an assertion of the mapping, <m> of them to be held before they are sent`, and the result `failingAssertions` and `heldByAssertions` ([running an OSDU flow](../cli/run.md)). |
| `sqlflow preview` | An `asserted` line with what the record's assertions found and whether the check before sending would hold it, then each failure; the document shown is already without what `omit` left out ([sqlflow preview](../cli/preview.md)). |
| `sqlflow values` (Check values) | An `asserted` outcome per variable and assertion, the rows failing any assertion and those an assertion would hold; a row held by an assertion fails the check ([sqlflow values](../cli/values.md)). |
| A run's trace | Per template, how many documents failed an assertion, the failures holding, reported and left out, and the assertions failed most, once per drain ([run trace](../concepts/run-trace-and-metrics.md)). |
| A record's history | Each attempt's verdict with its findings; `sqlflow records show` prints a `checked` line for the record and for each attempt, with its failures ([sqlflow records](../cli/records.md#records-show)). |
| The explorer, an assertion flow | What OSDU holds, judged by a mapping's record-stage assertions ([below](#judging-what-osdu-already-holds)). |
| The GUI | A record's timeline: each attempt says what the check before sending found (`checked valid, 2 assertion failures`) and opens to its verdict, whose Assertions section lists each failure with the assertion and what its failure does. The preview and a record's Render tab: what the record's assertions found, and that the check before sending would hold the record. Check values: an `Assertion failed` outcome per attribute, counted apart from the template's outcomes, by rows and by items of a repeated array, each finding with what its failures do. The Redeliver dialog: of the records that would be sent, those failing an assertion and those to be held. A mapping's Properties tab: how many assertions each property states, listed in its tooltip and in the property's detail. The template a mapping fills, attribute by attribute: a mark on each attribute whose entry states assertions, listed in its tooltip and in the attribute's Filled by panel, and the filter finds an attribute by an assertion's name or what it asserts. |

## Judging what OSDU already holds

A mapping read from the module's database (as the repository sync keeps it) can judge the records OSDU stores:

- **The explorer.** Validate, and Validate these records, judge the record-stage assertions of the synced mapping a
  check names (`mapping`, the mapping's id as `GET /api/v1/delivery/mappings` lists it) beside the schema check. A
  record page's check judges the assertions of the record's own flow's mapping unless the request names another
  ([explorer](../concepts/explorer.md#validate)).
- **An assertion flow.** A test's `mapping: WellLog@1.4.0` subject holds every record it reads to the mapping's
  record-stage assertions ([assertion flow](assertion.md#holding-records-to-a-mappings-assertions)).

In the GUI, the explorer's Validate and Validate these records pick the mapping beside the schema (the synced mappings
of the records' kind, those stating assertions first); a field whose value fails an assertion carries a mark with the
failures in its tooltip, and the list counts the records failing an assertion and the assertions failed most often.

Both judge with the code a render uses, on the record as OSDU holds it. The incoming assertions are not judged, since
OSDU does not hold the rows records were rendered from: the explorer's answer says how many it passed over, and a test
whose mapping states only incoming assertions does not fit its template check. In the explorer, a record of another
entity type than the mapping renders is not judged, and one of another version of the same type is judged on what it
holds at the same properties, the answer saying so; a test reads the one kind its mapping renders. Every failure counts
there, whatever its `onFail`: nothing is held or left out of a record OSDU already holds.

## The preflight

The preflight gate ([preflight](../concepts/preflight.md#the-preflight-gate)) checks each assertion against the pinned
template before any row renders:

- A record-stage condition suits the type of the value the property carries: text conditions (`matches`, `startsWith`,
  `endsWith`) on text, an order on numbers, dates and text, never on a boolean. A list of values is judged value by
  value, except by `exists`, `empty`, `length`, `contains` and `notContains`; a list of objects (a `$forEach` array) only
  by `exists`, `empty` and `length`; an object only by `exists` and `empty`. Inside an open object nothing is typed, and
  nothing is checked.
- What a condition compares with is a value the property can hold: a number for a number, `true` or `false` for a
  boolean, an ISO 8601 date for a date, and for `equals` and `in`, a value the property's pattern allows.
- Every `where` field is a property of the template, and its condition suits that property.
- Every column an incoming `where` reads is checked against the ingestion tables, as every column a mapping reads is.

```text
<file>: record.data.SamplingStart.$assert[0]: 'data.SamplingStart' is a number in osdu:wks:work-product-component--WellLog:1.4.0 (template 26a3c3441882db4f); matches compares text.
<file>: record.data.Curves.$assert[0]: 'data.Curves' is a list of objects in osdu:wks:work-product-component--WellLog:1.4.0 (template 26a3c3441882db4f); an assertion on the list asks whether it is there (exists), whether it is empty (empty) or how many items it holds (length), and equals on a property of its items is written beside that property.
```

## The mapping builder

The builder keeps every assertion of a mapping it opens: each draft entry carries `assertions`, each with `operator`, its
`operand` written as the document writes it after the operator (`[0, 250]`, `'GR'`, `{ atLeast: 1 }`), `stage`,
`onFail`, `anyValue`, `ignoreCase`, `tolerance`, `where` (each with what it `reads`, `field` or `column`, its `path`,
`operator`, `operand`, `ignoreCase` and `tolerance`), `name` and `description`. Compose writes them back as `$assert`, as
an author writes them, and names on the entry what the loader would refuse (an assertion on a literal or a list, an
operator that is not a condition, `omit` on a required property, the incoming stage on a value that does not come from
the row, two assertions of one name). Whether an operand suits the property is the composed mapping's preflight to say
([writing a mapping](../guides/writing-a-mapping.md)).

In the GUI, the entry editor of an entry that takes assertions lists them, one line each, and adds, edits, reorders and
removes them; the form offers only what the entry allows (the incoming stage on a column or an expression, `omit` on a
property that is not required, one of several values on the record stage) and explains each setting in its tooltip.
An entry that takes none (a fixed value, a list, a coalesce alternative, an item of a list) shows no assertions, and
saving it as one leaves out those it held.

## Limits

| Limit | Value |
| --- | --- |
| Assertions per node | 1 to 20 |
| Conditions in a `where` | 1 to 10 |
| `name` | 1 to 200 characters |
| Values in `in`, `notIn` | 1 to 1,000 |
| Time per value for `matches`, `notMatches` | 1 second |
| Failures a record lists | 50, every failure counted |
| Value and message a failure keeps | 200 and 500 characters, redacted |
| Failures a hold names | 3, then `and <n> more` |
| Failures the terminal lists per record | 10, then `and <n> more failure(s)` |

## Edge cases

| Case | Behaviour |
| --- | --- |
| A row value an incoming assertion cannot compare (text where a number is ordered) | The assertion fails with the reason; its `onFail` decides. |
| A value nothing holds (null, blank text, a property left out) | Judged only by `exists` and `empty`. |
| A `$when` that does not hold | The property is not written; an incoming assertion is not judged, and a record-stage one finds no value, which only `exists` and `empty` judge. |
| A `where` field the record does not carry | Selects nothing, so the assertion is not judged there, unless the condition is `exists: false` or `empty: true`. |
| A render that holds the record for another reason | Held at render as before, and nothing is queued; a preview and Check values still show what the assertions found. |
| `omit` on a property the schema requires | Refused: `omit` needs `$required: false`, which the preflight refuses on a required property. |
| `omit` leaving an object or an item empty | The object or item is left out; a list the mapping defines stays, empty. |
| Several assertions on one value | Each judged on its own and each failure recorded; any `hold` holds the record. |
| A queued document from before mappings could assert | Carries no findings; judged by its schema alone. |
| A held record whose mapping then changes | Keeps the document and the findings it was held for; a release sends it as it is; a redelivery plans it again under the new mapping. |
| The same document as the one delivered | Not sent and not judged again; judge what OSDU holds with the explorer or an assertion flow. |
| A thousand curves failing one assertion | 50 listed, all counted; the hold names the first three. |
| A pattern that backtracks | One second per value; a timeout fails the assertion and never hangs a render. |
| An assertion the judge cannot evaluate | Counted as failed under its `onFail`, with `could not be judged (<error>), so it counts as failed`; an `omit` then counts as reported, and nothing is left out. |
| Unicode text | Compared ordinally as UTF-16; `length` counts UTF-16 code units, as an assertion flow's `length` does. |
| A value that looks like a secret | Every value and message is redacted before it is stored or shown. |

## Errors when the mapping is read

Each message starts with the file and the place, such as `mappings/WellLog@1.4.0.yaml: record.data.SamplingInterval`.
The conditions are refused with the assertion flows' own messages ([validation errors](assertion.md#validation-errors)).

| Mistake | Message |
| --- | --- |
| `$assert` not a list | `$assert is a list of assertions, such as $assert: [ { between: [0, 250] } ]; write one assertion as the list's only item.` |
| More than 20 | `$assert lists 21 assertions, and a node states at most 20; fold the ones that ask the same thing into one (in lists the values allowed, between a range).` |
| A word with a `$` | `the words of an assertion carry no '$', as a modifier's settings do not, since they are not properties of the record; write stage, not $stage.` |
| A misspelled word | `'betwen' is not a word of an assertion. Did you mean 'between'?` |
| No condition, or two | `record.data.SamplingInterval.$assert[0] names no condition: one of equals, notEquals, in, ...` / `... names atLeast and atMost; a condition has one operator. Write between for a range, or one assertion for each.` |
| An unknown stage or action | `stage is 'before'; write record or incoming (...)` / `onFail is 'warn'; write hold, report or omit (...)` |
| Incoming on a value not from the row | `stage: incoming judges the value the row gives a node before its modifiers, and this node's value comes from the partition's cache; an assertion of this node judges the value the record carries (stage: record, the default).` |
| `values` on the incoming stage | `values says whether every value of a property holding several has to meet the assertion, or one of them, and stage: incoming judges the one value the row gives the node; remove values.` |
| `omit` on a required node | `onFail: omit leaves the value out of the record, and the node is required ($required is true unless it says false); write $required: false beside it so the record may go without it, or hold the record (onFail: hold) or send it as it is (onFail: report).` |
| `omit` with `values: any` | `onFail: omit leaves out each value that fails the assertion, and values: any judges the values together, so no one value fails it; write values: all, or hold or report the record.` |
| `resolves` | `record.data.WellboreID.$assert[0].resolves asks whether a reference resolves, which a mapping does not assert: an id the id or ref modifier builds is looked up in the partition's cache, and target.verifyReferences: storage looks every reference up in OSDU before the record is sent.` |
| A column on the record stage | `... reads a column of the row, and the assertion judges the value the record carries (stage: record), so its conditions read fields of the record, such as where: [ { field: data.ReferenceCurveID, equals: MD } ]; an assertion that judges the row's value is written with stage: incoming.` |
| A field on the incoming stage | `... reads a field of the record, and the assertion judges the value the row gives (stage: incoming), before the record is written, so its conditions read columns of the row, such as where: [ { column: depth_unit, equals: FT } ].` |
| The same assertion twice | `record.data.SamplingStart.$assert[1] asserts what record.data.SamplingStart.$assert[0] asserts (atLeast 0); remove one of them.` |
| One name twice | `... is named 'positive', as record.data.SamplingStart.$assert[0] is; each assertion of a node has a name of its own, since what it finds is recorded under it.` |
| On a literal | `$assert judges the value a record is given, and a literal $value gives every record the same one; remove $assert, or read the value from the row with $from or $expr.` |
| In an alternative of `$coalesce` | `record.data.Name.$coalesce[0]: $assert decides for the whole $coalesce node; write it beside $coalesce, not in one of its alternatives.` |
| On an item of a list of values | `record.acl.viewers[1]: $assert judges the value of a property, and this item gives one of the values of the list at record.acl.viewers, which has no node of its own to assert on; a list of values takes no assertions.` |
| A name too long | `name is what the assertion is called wherever what it finds is shown: 1 to 200 characters.` |

What does not fit the template is the [preflight's](#the-preflight) to refuse.

## Related

- [Mapping values](mapping-values.md): the nodes an assertion is written beside, `$required` and `$when`.
- [Assertion flow](assertion.md): the conditions, and tests of what OSDU holds.
- [Preflight](../concepts/preflight.md): the check before sending and the verdict.
- [Record lifecycle](../concepts/record-lifecycle.md): held records, issues and releases.
- [sqlflow values](../cli/values.md) and [sqlflow preview](../cli/preview.md): what the assertions find before a run.
- [Testing what OSDU holds](../guides/data-quality-tests.md): when to state a rule in the mapping, and when in a test.
