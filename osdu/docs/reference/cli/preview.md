---
id: delivery-cli-preview
title: "sqlflow preview: render one record of a delivery flow as it would be sent, and send nothing"
type: cli-command
summary: "Render one record of a delivery flow exactly as a delivery would: the document, what the next run would do, its files and the route's requests, sent nowhere."
keywords:
  - preview
  - preview a record
  - render one record
  - rendered document
  - what would be sent
  - next run
  - "--key"
  - source key
  - delivery key
  - placeholder
  - route steps
  - dry run
  - preview written whole
cliCommand: preview
related:
  - delivery-cli-check
  - delivery-cli-values
  - delivery-cli-records
  - delivery-concept-change-detection
  - delivery-flow-routes
  - delivery-flow-mapping-assertions
  - delivery-guide-writing-a-mapping
  - delivery-cli-db
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryPreviewVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/FindingLines.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreviewer.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreview.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/PreviewKeys.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RouteRehearsal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Planning/ChangeDetector.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliModuleSet.cs
---

# sqlflow preview: render one record of a delivery flow as it would be sent, and send nothing

`sqlflow preview` answers "what would this flow send for this record?" before anything is sent. It renders one record
exactly as a delivery renders it: the same planner, the mapping the flow pins, the template that mapping fills, the
partition's cache version, and the platform's search asked only what the mapping's searches ask a run. It shows the
document as the route sends it, what the next run would do with the record and why, the files it would upload and the
requests the route would make. Nothing is written anywhere: not OSDU, not the ledger, not the work location. It is not
a run, so it leaves no run history. It is the same preview a delivery flow's Preview tab shows in the GUI.

The command line is `sqlflow`: OSDU Delivery's CLI is SQLFlow's CLI with the OSDU verbs added. The options every
command shares (`--db`, `--json`, `-v`, the `.sqlflow/env` file) are described in
[CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md).

## Synopsis

```bash
sqlflow preview <flow.yaml> [--interface <name>] [--partition <id>] [--key <key>] [--set name=value]...
                [--out <file.json>] [--db <conn-ref>] [--json] [-v|--verbose]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<flow.yaml>` | yes | A delivery flow. Without it the verb prints `ERROR  name the flow document whose record to preview.` and its usage, and exits 1. |

## Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `--key <key>` | string | the scope's first record | The record to preview (see [Which record](#which-record)). |
| `--interface <name>` | string | every interface | Preview one interface of a source. A source is otherwise previewed one interface at a time, each with its own first record. `--key` needs it on a source with several interfaces: `ERROR  a key names a record of one interface; name it with --interface (<names>).` |
| `--partition <id>` | string | as a run settles it | The partition to preview a flow that works in partitions in, settled as `sqlflow check` settles it ([sqlflow check](check.md)). |
| `--set name=value` | repeatable | the declared defaults | Values for the flow's own parameters. They fill the scope the record is read from, as a run's values do; a name set more than once reads the rows of each of its values. |
| `--out <file.json>` | path | none | Also write the preview as JSON to this file, whole: one preview, or an array of one per interface, every rendered document kept whatever its size. The folder must exist (`--out names <path>, in a folder that does not exist.`); a refused folder is said before anything renders. The text answer names the file after the document it prints (`written to <path>`). |
| `--db <conn-ref>` | string | `${env:SQLFLOW_CATALOG_DB}` | The module database when the `osdu` schema lives in the catalog's database; `SQLFLOW_OSDU_DB` names it otherwise. See [The osdu module database in sqlflow db](db.md). |
| `--json` | switch | off | Print the preview as JSON on stdout instead of the text form. |

## What it needs

The template, the cache version and the ledger live in the module database, so `preview` needs it, as `check` does.
It also opens the flow's ingestion tables on this machine through the flow's own `source.connection`, reads the
record's payload files where the flow streams any, and asks the platform's search what the mapping's searches ask, so
those references have to resolve here (the `.sqlflow/env` file supplies them on a workstation).

A flow that cannot render at all fails as a run of it would: a template no template folder on the mapping's path holds, a cache version that is not saved, a
preflight error, or a table without a column the mapping reads. The preview opens the tables through the planner, so
it holds the mapping's columns against them, which `sqlflow check` does not.

## Which record

Without `--key`, the preview takes the first record of the scope in key order. It passes over rows that cannot render
(a row the ingestion table marked deleted, a row with an empty key part, a row the source read holds, such as one
with more child rows in a dataset than its `maxRowsPerRecord`), names the first five of them, and gives up after 1,000.

`--key` names a record the way an operator holds it. The text is read, in this order, as:

| Form | Example | How it is read |
| --- | --- | --- |
| A JSON array of the key's parts | `'["WB-0001"]'` | One string per key column, in `source.record.key` order. |
| A delivery key | `3f2c0b1e-...` (either UUID form) | The record the ledger holds under it. |
| An OSDU id the ledger holds | `dev:master-data--Wellbore:...` | The record of this flow delivered as that id. An id of the flow's own kind made from the key's values (`dataset.idFrom: key`) is read back as the key; any other id the ledger does not hold is refused. |
| A source key | `welldb:WB-0001`, or `WB-0001` | As the Records page shows it, the source system's prefix optional. With several key columns the parts are written `a \| b` or `a/b`; since a part may hold a slash itself, every way the slashes split the text into the key's parts is tried (up to 32), the ledger first, then the table. |

A key text longer than 4,000 characters, or one holding a control character, is refused. A text that reads as two
records asks for the JSON form. A key that names no row is an answer, not a failure of the command: it says why, and
the command exits 1. For example:

```text
--  welldb-wellbore-03-delivery: nothing to preview
    why         The record table OsduData.silver.Wellbore holds no row with the key 'WB-9999' (wellbore_id).
```

A row outside the scope the parameter values name says so: `The row with the key <key> is outside the scope the
parameter values name (<scope>); preview it with the values of its own scope.`

## What it does

The row is planned twice by the planner a run uses, and neither pass writes anything:

- **Against the ledger**, for what the next run would do with the record: `create`, `updateMetadata`, `updatePayload`,
  `updateBoth`, `skip` (with the gate that skipped it: `fingerprint`, `contentHash`, `approval` or `stale`), `hold` or
  `blocked`, and why. The ledger's record of it follows: its status, the OSDU version it holds, and whether the document
  rendered now is the one it holds. See [Change detection](../concepts/change-detection.md).
- **Without the ledger**, which renders the record as a first delivery would, so a record a run would skip as unchanged
  still shows its document.

Where the route adds to the document, the preview shows it as the route sends it, with a placeholder for each value
only the platform gives (on the file and manifest routes, `<dataset id the File service returns for <file>>` for each
dataset id the File service mints). The route's requests follow in order, with the paths the flow's options give
([Routes](../flow/routes.md)). Then come the payload files the record would upload, part by part, with each parquet
file's rows and columns read from its footer; the records the document refers to, each with the ledger record that
holds it; the searches the render made; and the preflight's warnings, which do not stop a run. When the mapping states
assertions, the preview says what they found of the record and whether the check before sending would hold it.

## Bounds

The preview's answer is bounded whatever the record's size: child rows to 100 per dataset, a source value to 4,000
characters, payload files to 50 per part (every file still counted and sized), parquet footers to the first ten files,
and the references looked up in the ledger to 50. A rendered document longer than 2,000,000 characters of canonical
JSON is described by its size and hash and left out of the console (text and `--json`), as the GUI does; `--out`
writes it whole, and the console's note names the file:
`The rendered document is <n> characters, more than the 2,000,000 a preview shows. Its hash is <hash>; the whole preview is written to <path>.`
Without `--out` the note ends `'sqlflow preview' writes it whole with --out.`

## Output

The text answer, one block per interface previewed (lines without content are left out; an interface's block names it
as `<flow>/<interface>`, as a run names it):

```text
OK  welldb-wellbore-03-delivery: welldb:WB-0001 [<label>]
    asked       the scope's first record of <n>
    from        <file the row was landed from> row <n>
    mapping     Wellbore@1.0.0 on osdu:wks:master-data--Wellbore:1.3.0
    route       storage
    next run    create: not in ledger
    document    <OSDU id> (<n> characters, hash <first 16 characters of the hash>)
    step 1      Storage: PUT /api/storage/v2/records
                the record below, in one array with the other records of its batch (up to 100)
    document as rendered and sent:
{
  ...the rendered record...
}
```

| Line | Says |
| --- | --- |
| `OK  <flow>: <source key> [<label>]` | The record previewed. `--  <flow>: nothing to preview` and a `why` line when none was found. |
| `asked` | `the scope's first record of <n>` (with `, after <m> row(s) that cannot render`), or `'<key>' read as <form>`. |
| `passed over` | One line for each of the first five rows passed over, and why. |
| `from` | The file and row the record's row was landed from, or `no file recorded`. |
| `mapping` | The mapping and kind, with `, cache <version>` when it reads the cache. |
| `route` | The route, with where the DDMS takes the record on a DDMS route. |
| `next run` | The action, the skip gate in parentheses for a skip, and the reason. |
| `ledger` | The ledger's status of the record, its OSDU version, and whether the document is the one it holds. |
| `document` | The OSDU id, size and hash of the document; or `none: <why>` when the row renders no document. |
| `held` | Why a delivery would hold the record instead of sending it. |
| `asserted` | What the mapping's assertions found of the document (`<n> of <m> assertion judgement(s) of <mapping> failed (...)`, or that every one was met), with `; the check before sending would hold the record, its document kept` when a failure holds it; then each failure, at most ten, as `<where> fails "<assertion>" (<stage>, <onFail>): <why>`. The document shown is already without what `omit` left out ([mapping assertions](../flow/mapping-assertions.md)). |
| `coalesce` | Which alternative of each `$coalesce` node gave the value written. |
| `unverified` | A reference the mapping writes with `$unverified` that names no record the cache holds. |
| `refers to` | Each OSDU id the document refers to, the property it is in, and the ledger record holding it. |
| `payload` | Each payload part: its files, count and size, and per parquet file its rows and columns. |
| `step <n>` | Each request of the route, in order, with what it carries on the next line. |
| `note`, `issue` | What the route does beyond its requests; the preflight's warnings. |
| `placeholder` | Each value of the sent document the platform gives when the record is sent. |
| `document as ...` | The document itself: `as the route sends it` where the route adds to it, else `as rendered and sent`. |

### --json and --out

The JSON is the preview record, camelCase, properties without a value left out: `flow`, `interface`, `flowId`, `asked`
(`key`, `how`, `keyParts`, `keyColumns`, `passedOver`, `passedOverWhy`, `scopeRecords`, `values`), `found`, `reason`,
`inputs` (`mapping`, `kind`, `templateVersion`, `cachePartition`, `cacheVersion`, `contextHash`), `route` (`protocol`,
`reason`, `ddms`), `source` (`sourceKey`, `keyParts`, `deliveryKey`, `label`, `identities`, `originFile`, `originRow`,
`originUpdatedUtc`, `fingerprint`, `deletedUtc`, `hold`, `row`, `datasets`, `omitted`), `decision` (`action`,
`skipTier`, `reason`, `deliverMetadata`, `deliverPayload`, `ledger`), `document` (`targetId`, `kind`, `metadataHash`,
`characters`, `held`, `holds`, `rendered`, `sent`, `placeholders`, `omitted`, `searches`, `cacheValues`, `choices`,
`unverified`, `assertions`: the findings a verdict carries), `noDocument`, `references`, `referenceCount`, `payload`, `steps` (`order`, `service`, `request`, `what`,
`returns`, `repeats`, `body`), `notes`, `issues` and `previewedUtc`. A source previewed one interface at a time gives
an array of these.

## Examples

Preview the first wellbore of the `dev` partition:

```bash
sqlflow preview flows/welldb-wellbore-03-delivery.yaml --partition dev
```

Preview one wellbore by its source key, and keep the whole preview in a file:

```bash
sqlflow preview flows/welldb-wellbore-03-delivery.yaml --partition dev --key welldb:WB-0001 --out WB-0001.json
```

Preview one well log of a source with interfaces, by its key parts:

```bash
sqlflow preview flows/welldb-03-delivery.yaml --partition dev --interface welllogs --key '["LOG-0042"]'
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | A record was found and previewed for every interface previewed. |
| 1 | No record was found for an interface (the answer says why); a usage error; the flow could not render (a template, cache version or table it needs is missing, or the preflight failed); or `--out` could not be written. Errors print one `ERROR  <message>` line on stderr, credentials redacted. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [sqlflow check](check.md): the flow's documents against the template and cache, without reading a row.
- [sqlflow values](values.md): what the mapping makes of every row of the scope.
- [sqlflow records](records.md): what the ledger holds for the record after a run.
- [Change detection](../concepts/change-detection.md): why a run would create, update, skip or hold a record.
- [Writing a mapping](../guides/writing-a-mapping.md): preview as one step of getting a mapping right.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
