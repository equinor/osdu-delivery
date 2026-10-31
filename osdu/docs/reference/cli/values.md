---
id: delivery-cli-values
title: "sqlflow values: which rows will not give a mapping's properties the values the template expects"
type: cli-command
summary: "Render a delivery flow's rows (10,000 by default, all with --max-rows 0) as a delivery would and hold each value to the template: held, invalid, empty, why."
keywords:
  - values
  - check values
  - value check
  - mapping values
  - invalid value
  - held record
  - template rules
  - data quality
  - failing rows
  - "--target"
  - "--rows"
  - csv of failing rows
cliCommand: values
related:
  - delivery-cli-preview
  - delivery-cli-check
  - delivery-flow-mapping-values
  - delivery-flow-mapping-assertions
  - delivery-concept-preflight
  - delivery-guide-writing-a-mapping
  - delivery-concept-templates
  - delivery-cli-db
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryValueCheckVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueChecker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueCheck.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueTally.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueIssuesWorkbook.cs
  - osdu/src/SqlFlow.Delivery/Validation/TemplateValueRules.cs
  - osdu/src/SqlFlow.Delivery/Validation/SchemaWalk.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreview.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
---

# sqlflow values: which rows will not give a mapping's properties the values the template expects

A preview shows one record. `sqlflow values` answers the same question across a flow's rows: "which rows will not give
this property the value its template expects, and why?" It reads the rows of the flow's scope in the order a run reads
them (the first 10,000 unless `--max-rows` says otherwise, `0` for every one), renders each one exactly as a delivery renders it (over the ingestion tables, the partition's cache version and
the platform's search, asked in rounds as a run asks), and holds every value written to what the template says of its
property. Every count is exact however many rows fail; what is listed is bounded. Nothing is written: not OSDU, not the
ledger, not the work location. It is the same check as **Check values** on a mapping in the GUI's Mappings page.

The command line is `sqlflow`: OSDU Delivery's CLI is SQLFlow's CLI with the OSDU verbs added. The options every
command shares (`--db`, `--json`, `-v`, the `.sqlflow/env` file) are described in
[CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md).

## Synopsis

```bash
sqlflow values <flow.yaml> [--interface <name>] [--partition <id>] [--target <osdu.path>]... [--set name=value]...
               [--max-rows <n>] [--samples <n>] [--skip <n>] [--rows <file.csv>] [--out <file.json>]
               [--db <conn-ref>] [--json] [-v|--verbose]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<flow.yaml>` | yes | A delivery flow. Without it the verb prints `ERROR  name the flow document whose values to check.` and its usage, and exits 1. |

## Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `--target <osdu.path>` | repeatable | every entry of the mapping | A property to check, as a template path (`osdu.data.FacilityName`, `osdu.data.Curves[].CurveUnit`), or a group (`osdu.data.VerticalMeasurement`) for everything it holds. It checks every entry that fills the property, something inside it, or a value holding it, and nothing else, so the platform is asked nothing for another property. Repeat it for more. |
| `--interface <name>` | string | every interface | Check one interface of a source. A source is otherwise checked one interface at a time. `--target` needs it on a source with several interfaces: `ERROR  a variable is one of an interface's mapping; name the interface with --interface (<names>).` |
| `--partition <id>` | string | as a run settles it | The partition to check a flow that works in partitions in, settled as `sqlflow check` settles it. |
| `--set name=value` | repeatable | the declared defaults | Values for the flow's own parameters. They fill the scope the rows are read from, as a run's values do; a name set more than once reads the rows of each of its values. |
| `--max-rows <n>` | whole number | `10000` | The rows to read, in the order a run reads them. `0` reads the whole scope. |
| `--samples <n>` | whole number | `20` | Example records each finding names, at most 500. |
| `--skip <n>` | whole number | `0` | Example records of each finding passed over before the samples are taken: the next page of examples. |
| `--rows <file.csv>` | path | none | Write every failing row to a CSV file as the check meets it, however many there are (see [The --rows file](#the---rows-file)). The text answer then ends with `Every failing row is in <path>.` |
| `--out <file.json>` | path | none | Also write the whole check as JSON to this file. |
| `--db <conn-ref>` | string | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema. See [The osdu module database in sqlflow db](db.md). |
| `--json` | switch | off | Print the check as JSON on stdout instead of the text form. |

`--max-rows`, `--samples` and `--skip` take digits only. Anything else stops the command with
`ERROR  --max-rows, --samples and --skip each take a whole number from 0 (--max-rows 0 reads the whole scope).`
More than 500 samples fails with `A finding names between 0 and 500 example records; <n> is outside that.` The folder
of `--rows` and `--out` must exist: `--rows names <path>, in a folder that does not exist.`

A `--target` that is not a template path fails with `'<text>' is not a variable a check can name: <why>.`, and one the
mapping does not reach with
`<path>: no entry of mapping <Name@version> fills it, anything inside it, or a value holding it, so a check has nothing to evaluate there.`

## What it needs

Like `sqlflow preview`, it needs the template folders on the mapping's path, the module database (the cache version), the flow's ingestion tables
through its own `source.connection` on this machine, and the platform's search when the mapping searches. A flow whose
mapping fails the preflight, or whose tables lack a column the mapping reads, fails the check as a run of it would.

## What each row comes to

Every computation a mapping makes is covered, because the check is the render: a column, an expression, a literal, a
cache lookup, a platform search, a `$coalesce` and its alternatives, a list, a repeated array's items, and every
modifier. For each property checked, each row (and each item of a repeated array) comes out as one of:

| Outcome | Means | Fails the check |
| --- | --- | --- |
| `held` | No value could be produced where one is needed, so the record is not delivered: the render's own hold reason. | yes |
| `invalid` | A value is written that breaks a rule of the template; the record is still sent. | yes |
| `empty` | An optional entry gives no value, and the record goes without the property. The reason says why it gave none. | no |
| `notApplicable` | The mapping means no value for the row: the entry's `$when` does not hold, or an item entry meets a row with no child row. | no |
| `valid` | Written with a value the template accepts. | no |
| `asserted` | A value fails an assertion of the mapping; counted beside the outcome above, which the template decides. | when the assertion holds the record (`onFail: hold`) |

The template rules a value is held to are the JSON Schema rules the template states on the property and on everything
inside a value written whole: `type`, `format` (RFC 3339 `date-time`, `date` and `time`, `uri`, `uri-reference`,
`email`, `uuid`, `ipv4`, `ipv6`), `pattern`, `enum`, `const`, `minLength`, `maxLength`, `minimum`, `maximum`,
`exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `minItems`, `maxItems`, `uniqueItems`, `required`,
`additionalProperties`, `anyOf` (a value matching none of a `oneOf` or `anyOf`), and `relationship` (the entity types
an `x-osdu-relationship` lets a reference point to). A problem inside a value written whole is reported at the property
it is at (`osdu.data.NameAliases[].AliasNameTypeID`).

For each property, a row counts once, by the worst it came to: held, then invalid, then empty, then valid. As a record,
a row counts among the rows with a held property, with an invalid value, and leaving a property out, for each that
applies (one row can be in more than one), and as clean when it is in none.

**The mapping's assertions** ([mapping assertions](../flow/mapping-assertions.md)) are judged on every row as a delivery
judges them, and counted apart from the template's rules, since a value can meet the template and break a rule of the
mapping. A value that fails one is an `asserted` finding on the property the assertion is written beside, its rule the
assertion's name and its message what the failure does (`fails "reference-elevation" (record); the record is held before it is sent`,
`...; the record is sent and the failure recorded`, `...; the value is left out`); each example record says why its own
value fails. A property counts the rows a value of it failed an assertion in. As a record, a row counts among the rows
failing an assertion and among those an assertion would hold, and is not clean when it fails one. A value a record-stage
`omit` would leave out is shown as written; one an incoming `omit` leaves out is `empty`, its reason naming the
assertion. Checking one property (`--target`) also evaluates the properties its assertions' `where` conditions read. A row the ingestion table marks deleted, or
one the source cannot give whole, is passed over, as a delivery passes it over; a row with an empty key part is counted
as keyless, since its record cannot be tracked.

## Output

The text answer, one block per interface checked (an interface's block names it as `<flow>/<interface>`, as a run names
it).
The first line starts with `OK` when no row is held, invalid, held by an assertion or keyless, and with `!!` when one is:

```text
!!  welldb-wellbore-03-delivery: Wellbore@1.0.0 on osdu:wks:master-data--Wellbore:1.3.0
    rows        10,000 checked of 10,000 read (the scope holds about <n>)
    outcome     <n> clean, <n> held, <n> with an invalid value, <n> leaving a variable out
    asserted    <n> failing an assertion of the mapping, <n> of them to be held before they are sent
    keyless     <n> rows have an empty key part, so their records cannot be tracked
    passed over <n>: the ingestion table marks the row deleted, and a deleted row is never delivered
    osdu.data.FacilityName: <n> held, <n> invalid, <n> empty, <n> valid, <n> not applicable[; <n> failing an assertion]
      <outcome>  <count>[ at <path inside>]  <reason>
               values  '<value>' x<n>, ...
               e.g.    welldb:WB-0001 (<file> row <n>)
    note        <what the answer left out, and why>
    issue       <a preflight warning>
```

| Line | Says |
| --- | --- |
| `rows` | Rows checked of rows read, and `the whole scope` or how many the scope holds. |
| `outcome` | The rows as records: clean, held, with an invalid value, leaving a property out. |
| `asserted` | Only when a row fails an assertion of the mapping: those rows, and how many of them an assertion would hold. |
| `keyless`, `passed over` | Rows with an empty key part; rows a delivery never renders, by reason. |
| `<property>:` | Only for a property some row fails: its rows by outcome, and the rows failing an assertion. |
| finding lines | Each reason, with its outcome (`held`, `invalid`, `empty` or `asserted`), count and where inside the property it is; the five most frequent values behind it; the first three example records with their file, row and item. |
| `... more occurrences under reasons not listed` | Occurrences of findings past the 50 a property lists. |

### --json and --out

The JSON is the check, camelCase, properties without a value left out: `flow`, `interface`, `flowId`, `inputs`
(`mapping`, `kind`, `templateVersion`, `cachePartition`, `cacheVersion`), `asked` (`targets`, `maxRows`, `samples`,
`skipSamples`, `values`), `rows` (`scopeRecords`, `read`, `checked`, `complete`, `passedOver`, `passedOverWhy`,
`keyless`, `keylessSamples`, `clean`, `withHeld`, `withInvalid`, `withEmpty`, `withFailedAssertion`, `heldByAssertion`),
`variables`, `issues`, `notes`, `startedUtc` and `checkedUtc`. Each variable has `target`, `entry`, `columns` (the source
columns the entry filling it reads, as the mapping names them: `dataset.curves.curve_unit`), `required`,
`repeater`, `rows` and `items` (each `valid`, `invalid`, `empty`, `notApplicable`, `held`, `asserted`, `total`;
`asserted` counts each row, or each item of a repeated array, a value failed an assertion in, once however many failed,
and `total` leaves it out, since those rows and items count under another outcome too; a failure of a repeated array's
values together, `values: any`, counts its row and no item), `values`, `distinctValues`, `moreValues`, `findings` and
`unlisted`; each finding has `outcome`, `at`, `rule`, `onFail` (for an `asserted` finding, what a failure of the
assertion does: `hold`, `report` or `omit`), `message`, `count`, `rows`, `values`, `otherValues`,
`samples` (`sourceKey`, `label`, `deliveryKey`, `file`, `row`, `item`, `value`, `message`, and `columns`, the source
columns to fix: the ones the reason names, else the ones holding the value, else the property's only column, else those it
reads that are empty) and `samplesFrom`. A source
checked one interface at a time gives an array of these.

### Bounds

Counts are exact; listings are capped so the answer stays the same size for a million failing rows as for a thousand:
50 findings per property (the occurrences of the rest counted as unlisted), 25 values per finding (500 distinct values
tracked), 10 values per property (1,000 tracked), values quoted to 300 characters, five example records per reason
rows are passed over, and the samples `--samples` asks for. Rows are inspected 200 at a time.

### The --rows file

UTF-8 CSV (RFC 4180 quoting), a header line, then one line per row, or per item of a repeated array, that a property is
held, invalid or empty for, or whose value fails an assertion of the mapping, written as the check meets it:

| Column | Holds |
| --- | --- |
| `flow` | The flow; for an interface of a source, `<flow>/<interface>`. |
| `variable` | The property checked. |
| `at` | Where the reason is: the property, or a property inside the value written there. |
| `outcome` | `held`, `invalid`, `empty` or `asserted`. |
| `rule` | For an invalid value, the template rule it breaks; for an asserted one, the assertion it fails. |
| `reason` | The reason, as this row states it: for an asserted value, why it fails the assertion. |
| `value` | The value written or the value that caused the reason, clipped. |
| `source_key`, `label`, `delivery_key` | The record. |
| `file`, `row` | The file the row was landed from, and its row in that file: for an item of a repeated array, those of its child row. |
| `item` | For an item of a repeated array, the child row it was written from, counting from 1. |

It is the full list the answer's example records are the first of. The GUI's **Export rows** on a mapping's attribute
writes that attribute's part of it as an Excel workbook, with the source columns it reads
([the Mappings page](../concepts/gui.md#mappings)).

## Examples

Check every property over the first 10,000 wellbores of the `dev` partition:

```bash
sqlflow values flows/welldb-wellbore-03-delivery.yaml --partition dev
```

Check one property over the whole scope, and write every failing row to a file:

```bash
sqlflow values flows/welldb-wellbore-03-delivery.yaml --partition dev \
  --target osdu.data.FacilityName --max-rows 0 --rows failing.csv
```

Page further examples of each finding, and keep the whole answer as JSON:

```bash
sqlflow values flows/welldb-wellbore-03-delivery.yaml --partition dev --samples 50 --skip 20 --out check.json
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | No row checked is held, writes a value the template does not accept, fails an assertion that would hold it, or has an empty key part. A row that leaves an optional property out, and one failing an assertion that only reports or leaves a value out, are reported without failing the check. |
| 1 | A row is held, invalid, held by an assertion or keyless; a usage error; the flow could not render; or `--rows` or `--out` could not be written. Errors print one `ERROR  <message>` line on stderr, credentials redacted. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [sqlflow preview](preview.md): one record rendered whole.
- [sqlflow check](check.md): the mapping against its template and cache, without reading a row.
- [Mapping values](../flow/mapping-values.md): the value nodes a check evaluates.
- [Mapping assertions](../flow/mapping-assertions.md): the rules a check counts as `asserted`.
- [Templates](../concepts/templates.md): the schemas the values are held to.
- [Writing a mapping](../guides/writing-a-mapping.md): checking values as one step of getting a mapping right.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
