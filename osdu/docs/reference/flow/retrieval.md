---
id: delivery-flow-retrieval
title: "Retrieval flow (flowType: retrieval): OSDU records back to the lake as JSON Lines files"
type: flow-reference
summary: "Copy the records of OSDU kinds from the search index, or whole from storage, into JSON Lines files on the lake, incrementally, with a manifest per run."
keywords:
  - source.paging
  - createTime paging
  - retrieval flow
  - export records from osdu
  - osdu to the lake
  - json lines
  - jsonl
  - search cursor
  - incremental window
  - modifytime watermark
  - fetchrecords
  - returnedfields
  - target.location
  - manifest.json
  - retrieve operation
  - probepath refused
  - reference in target.location
yamlPath: "(root, flowType: retrieval)"
related:
  - delivery-concept-search-paging
  - delivery-guide-retrieving-records
  - delivery-flow-overview
  - delivery-flow-delivery
  - delivery-concept-ledger
  - delivery-cli-run
  - flow-schedule
  - source-type-json
  - concept-connections-and-secrets
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/OsduKind.cs
  - osdu/src/SqlFlow.Delivery/Model/RetrievalDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RetrievalExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Retrieval/RetrievalRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Storage/FileStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/specs/core/search/openapi.yaml
  - osdu/specs/core/storage/openapi.yaml
---

# Retrieval flow (flowType: retrieval): OSDU records back to the lake as JSON Lines files

A `flowType: retrieval` document runs the reverse direction of a delivery: it pages through OSDU's search index, one
cursor per kind, and writes every record it finds into JSON Lines files on the lake (a local folder or Azure Storage),
one record per line, rolled into a new file every so many records. Each run also writes a `manifest.json` next to the
files and keeps one row in the ledger table `osdu.Retrieval`. An incremental flow carries a watermark on a record
timestamp (`modifyTime` by default) from run to run, so each run takes only what changed since the last one.

Use a retrieval flow when you want OSDU's records back as data: to analyse them on the lake, to compare them with the
source, or to feed a SQLFlow file flow that loads them into a table ([Retrieving OSDU records into tables](../guides/retrieving-records.md)).
Nothing is rendered and no mapping is involved: each line is the record as OSDU holds it (the search hit, or with
`fetchRecords: true` the record as storage keeps it). A retrieval writes nothing to OSDU. It is not how mappings read
reference data either; that is a [cache flow](cache.md).

## Minimal example

Every wellbore record of the partition, read whole on every run, `welldb-retrieval-00-wellbores.yaml`:

```yaml
flowType: retrieval
name: welldb-retrieval-00-wellbores
batch: welldb
description: Every wellbore record of the dev partition, all of it on every run, back as JSON Lines files on the lake.

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
  headers:
    data-partition-id: dev
  kind: osdu:wks:master-data--Wellbore:1.*.*

target:
  location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores-all
```

```bash
sqlflow validate welldb-retrieval-00-wellbores.yaml
sqlflow run welldb-retrieval-00-wellbores.yaml --operation plan   # count what would be retrieved
sqlflow run welldb-retrieval-00-wellbores.yaml                    # retrieve
```

`validate` prints `OK  'welldb-retrieval-00-wellbores' is valid (retrieval: ${env:OSDU_URL} -> abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores-all).`
The estate's nightly wellbore retrieval, `welldb-retrieval-01-wellbores`, reads a day's changes at a time
([retrieving records](../guides/retrieving-records.md)).

## A fuller example

Two kinds narrowed by a query with a parameter, an incremental window, whole records read back from storage, and
gzip files under a folder per day:

```yaml
flowType: retrieval
name: welldb-retrieval-02-logs
batch: welldb
description: Well logs and wellbores of the well database changed since the last run, read back whole from storage.

parameters:
  system:
    default: welldb
    description: The data.Source value of the records to retrieve.

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
  headers:
    data-partition-id: dev
  kinds:
    - osdu:wks:work-product-component--WellLog:1.*.*
    - osdu:wks:master-data--Wellbore:1.*.*
  query: 'data.Source:"{system}"'
  pageSize: 1000
  incremental:
    field: modifyTime
    since: 2026-01-01T00:00:00Z
    lagMinutes: 5
  fetchRecords: true
  fetchParallelism: 4

target:
  location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/{system}/{date}
  format: jsonl
  compression: gzip
  rollRecords: 100000
  manifest: manifest.json

reliability:
  concurrency: 2
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }
  timeoutSeconds: 100

schedule:
  cron: "0 3 * * *"
  timezone: UTC
```

## Keys reference

Unknown keys are refused: the document is read strictly, so a misspelled key fails validation naming it
(`invalid YAML at line N, column M - Property 'partitions' not found on type ...`).

| Key | Type | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | none | `retrieval`. |
| `name` | string | yes | none | The flow's name: its pipeline identity. The flow id, and so its ledger, derive from it. |
| `batch` | string | no | none | The SQLFlow batch the pipeline is filed under. |
| `description` | string | no | none | Free text. |
| `parameters` | map | no | none | Named parameters, each with `required`, `default` and `description`. `{name}` tokens use them in `source.query` and `target.location`. A run supplies values with `--set name=value` or a schedule's `values`; a value for an undeclared name is refused. |
| `source` | map | yes | none | The OSDU side: platform, credentials, partition, kinds, query, paging and watermark. |
| `target` | map | yes | none | The lake side: where a run's files and manifest go and how the files are cut. |
| `reliability` | map | no | defaults | The HTTP settings, written as on a [delivery flow](delivery.md) (`timeoutSeconds`, `retry`, `rateLimitRps`, `verifyTls`, `urlAllowlist`, `maxResponseBytes`), and `concurrency`: how many kinds are retrieved at once (default 8, at least 1, at most 64 run at once). `parallelInterfaces` is refused; the other delivery-only settings are accepted and unused. |
| `schedule`, `mode`, `lifecycle` | | no | none | SQLFlow's envelope keys, as on every flow: see [schedule](../../../../sqlflow/docs/reference/flow/schedule.md) and [the flow overview](../../../../sqlflow/docs/reference/flow/overview.md). |

There is no `partitions` key: a retrieval reads the one partition its `source.headers` name.

### source

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | string | required | The platform's base URL, usually `${env:OSDU_URL}`. |
| `auth` | map | `type: none` | How requests authenticate, written exactly as `target.auth` on a delivery flow (`type: oauth2ClientCredentials` with `secretRef`, `secondarySecretRef` and `token`, or `bearer`, `apiKeyHeader`, `basic`). Secrets are references only: `${env:NAME}` or `${keyvault:NAME}`; a literal is refused when the flow is read. |
| `headers` | map | required | Headers sent with every request. `data-partition-id` is required and names the partition the flow reads and keeps its ledger in. A value may be a reference, resolved on the node; a header that carries a credential holds one, and a literal there is refused when the flow is read. |
| `kind` | string | | One kind to retrieve. |
| `kinds` | list | | Several kinds. Give `kind`, `kinds` or both (they are combined); at least one kind in all, none twice. |
| `query` | string | none | A Lucene query narrowing every kind, with `{parameter}` tokens. Left out, every record of the kinds. |
| `returnedFields` | list | `[]` | Project each hit onto these fields; empty returns whole hits. `id` is always added, since a read knows its records by id. |
| `pageSize` | int | `1000` | Records per search page, 1 to 1000 (the search service's limit on `limit`). |
| `incremental` | map | none | The watermark (see [The incremental window](#the-incremental-window)). Without it every run takes everything the query matches. |
| `incremental.field` | string | `modifyTime` | The record field the window is taken on. No whitespace. On `modifyTime`, a record never modified is taken by its `createTime` (see [The incremental window](#the-incremental-window)). |
| `incremental.since` | string | none | Where the first run, and a forced run, starts: an RFC 3339 date and time such as `2026-01-01T00:00:00Z`. Left out, from the beginning. |
| `incremental.lagMinutes` | int | `5` | How far behind now the window ends, so records the indexer has not caught up with are left for the next run. Not negative. |
| `fetchRecords` | bool | `false` | Read every hit back from storage and write the record as storage holds it (see [Search hits or whole records](#search-hits-or-whole-records)). |
| `fetchParallelism` | int | `4` | Storage read-backs in flight per page, each for up to 100 ids. 1 to 64. |
| `searchPath` | string | `/api/search/v2/query_with_cursor` | The cursor search a retrieve pages through. |
| `paging` | string | `cursor` | How each kind is paged: `cursor` through `searchPath`, or `createTime` through `queryPath`, in the order each record was created, which holds nothing on the service between pages and picks a failed read up where it stopped. See [Search paging](../concepts/search-paging.md). |
| `queryPath` | string | `/api/search/v2/query` | The search a `plan` counts with (and a read checks its total against). |
| `recordQueryPath` | string | `/api/storage/v2/query/records` | Storage's read of records by id, used by `fetchRecords`. |

`source.probePath`, a delivery flow's setting, is refused on a retrieval flow: the target probe covers delivery flows
only, and a retrieval run never calls it. A retrieval that cannot reach the search service fails on its first page and
says why.

Every path must start with `/`. A kind is `authority:source:entityType:version` with wildcards allowed in any segment
(`osdu:wks:master-data--Wellbore:1.*.*`, `osdu:wks:*:*`), the shape the search service takes for `kind`.

### target

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `location` | string | required | The root a run's directory is made under: a local path, `abfss://<container>@<account>.dfs.core.windows.net/...` or `https://<account>.blob.core.windows.net/<container>/...`. Takes `{parameter}`, `{run}` and `{date}` tokens; any other token is refused, and so is a `${...}` reference. |
| `format` | string | `jsonl` | The file format. `jsonl` is the only one: an OSDU record is free-form JSON. |
| `compression` | string | `none` | `none` writes `part-00001.jsonl`, `gzip` writes `part-00001.jsonl.gz`. |
| `rollRecords` | int | `100000` | A new file every this many records. At least 1. |
| `manifest` | string | `manifest.json` | The manifest's file name inside the run's directory; a name, not a path. |

## Kinds, the query and the cursor

Each kind is read through its own search cursor (`POST /api/search/v2/query_with_cursor`), and up to
`reliability.concurrency` kinds are read at once. The first page asks for the exact total (`trackTotalCount`), every
later page is asked for once with the cursor the service last named, and the cursor is closed
(`DELETE /query_with_cursor/{cursor}`) when the read stops for any reason.

A read that reaches the end is checked against the total: a read that failed part way, or returned fewer distinct
records than the search matches, is made once more from the first page, and only the records not yet written are
written. When the second read is not whole either, the run fails with
`The search of kind <kind> for '<query>' could not be read whole in 2 reads (read 1: ...; read 2: ...). What was read is part of what the search matches, so it is not used as the whole.`
A failed run does not advance the watermark, so the next run reads the same window again.

With `paging: createTime` each kind is read through the plain search instead, in the order of each record's
`createTime` and then its id, every page from the last record written: no cursor is opened or closed, a page that fails
is asked again from where the read stopped, and a read that comes back short reads again only the batch it is short in.
A kind that cannot be read whole in two passes fails the run with
`The search of kind <kind> for '<query>' could not be read whole in createTime order in 2 passes (pass 1: ...; pass 2: ...).`
A record created while the run reads is left to the next run. See [Search paging](../concepts/search-paging.md).

The trace says what each kind is doing (the total the index reports, progress every 100 pages, the records and files
when it finishes), never one line per record.

## The incremental window

With `source.incremental`, a run covers `[from, to)` on `field`:

- `to` is the run's start minus `lagMinutes`.
- `from` is the upper bound of the latest completed run of the flow (`done`), or `since` when there is none or `since`
  is later. Only a completed run moves it: a failed or cancelled run leaves it where it was.
- The window is appended to the query as `(<query>) AND <field>:[<from> TO <to>}` (with `*` for an open `from`), times
  written as `yyyy-MM-ddTHH:mm:ss.fffZ` in UTC.
- On `modifyTime` the window is `(modifyTime:[<from> TO <to>} OR (createTime:[<from> TO <to>} AND NOT _exists_:modifyTime))`.
  OSDU writes `createTime` alone when it creates a record and `modifyTime` only from the record's second version on (or
  when its ACL, legal tags or tags are patched), and the indexer indexes `modifyTime` only when storage holds one, so a
  range on `modifyTime` alone would never take a record created and not changed since. Each record is taken by the time
  it last changed, in the one window that time falls in.
- Consecutive runs cover adjacent windows: the next run starts where the last one ended, so windows never leave a gap
  and do not overlap.
- A run whose window is empty (`from` is not before its start minus the lag: a `since` still in the future, or a lag
  raised since the last run) writes nothing, completes as `done` and says `the window is empty ... nothing to do`. Its
  upper bound is its start, so the next window starts where this one stood: a window never moves backwards, and never
  starts before `since`.
- `{"force": true}` in the run's payload restarts at `since` (or the beginning).

The watermark lives in the ledger. A run without the module's database (a CLI run with no `--db` and no catalog
variable) still writes its files and manifest, but keeps no ledger row, so every such run starts at `since`.

## Search hits or whole records

The search index holds a projection of each record. By default a run writes the hits as the search returns them,
narrowed by `returnedFields` when you list some.

With `fetchRecords: true`, every page's ids are read back from storage (`POST /api/storage/v2/query/records`, at most
100 ids per request, `fetchParallelism` requests at a time) and the records are written in page order as storage holds
them. An id the index lists and storage does not return (deleted since it was indexed, or not readable by the flow's
identity) is counted as missing for its kind, logged with the first ids, and listed in the manifest (the first 1,000 per
kind). It costs one storage request per hundred records, so turn it on when the projection is not enough.

## Where the files go

A run's directory is `target.location` with its tokens substituted:

| Token | Becomes |
| --- | --- |
| `{parameter}` | The parameter's value for the run. |
| `{run}` | The run id, 32 hex digits without dashes. |
| `{date}` | The run's start date in UTC, `yyyy-MM-dd`. |

Without a `{run}` token, each run gets a directory of its own under the location, named for its start and its run id
(`20261009T030000Z-1a2b3c4d`), so runs never overwrite each other. Inside, each kind has a directory named after it with
`:` written as `_` and `*` as `x` (`osdu_wks_master-data--Wellbore_1.x.x`), holding `part-00001.jsonl`,
`part-00002.jsonl` and so on, plus the manifest at the top:

```text
abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores/
  20261009T030000Z-1a2b3c4d/
    manifest.json
    osdu_wks_master-data--Wellbore_1.x.x/
      part-00001.jsonl
      part-00002.jsonl
```

Files are streamed as they are written (a local file is written beside and moved into place; a blob is written in
4 MB blocks), never held in memory. A kind that matches nothing gets no file. A location no store handles fails with
`No file writer handles location '<location>'. Snapshots, work batches and known-state publications go to a local path or an Azure Storage URI.`
Azure Storage is written with the node's Azure credential (see [sqlflow auth](../cli/auth.md)).

A relative local path is resolved against the working directory of the process running the flow, not the flow file;
lineage (and so the repository sync) warns about one. Use an absolute path or a storage URI. A location is not resolved
from a reference, so a `${...}` reference in `target.location` is refused when the flow loads:
`<file>: target.location '${env:LAKE_ROOT}/osdu' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.`

## The manifest

`manifest.json` describes the run:

| Field | Meaning |
| --- | --- |
| `flow`, `flowId`, `runId` | The flow and the platform run. |
| `startedUtc`, `completedUtc` | When the run started and when the manifest was written. |
| `endpoint`, `format`, `compression` | As the flow declares them. |
| `window` | `field`, `from`, `to` for an incremental run; `null` otherwise. |
| `records`, `files`, `bytes` | Totals over every kind; bytes are uncompressed. |
| `kinds[]` | Per kind: `kind`, the `query` as it ran (window included), `records`, `missing`, `files[]` (`path`, `records`, `bytes`), and `missingIds` when storage could not return some. |

## Operations and the run payload

| Operation | What it does |
| --- | --- |
| `retrieve` (default) | Reads the window (or everything) and writes the files, the manifest and the ledger row. |
| `plan` | Counts what each kind's query matches in the window (`POST /api/search/v2/query` with `trackTotalCount`) and names the directory a run started now would write to. Writes nothing and keeps no ledger row. |

```bash
sqlflow run welldb-retrieval-02-logs.yaml --operation plan
sqlflow run welldb-retrieval-02-logs.yaml --set system=welldb
sqlflow run welldb-retrieval-02-logs.yaml --payload '{"force":true}'   # start again at since
```

The payload takes `force` and nothing else; anything more is refused before the run starts, for example
`payload submissionId does not apply to a retrieval flow: only a delivery flow's runs name a submission; a retrieval flow's payload names only force.`
SQLFlow's own backfill options are refused: `fullLoad do(es) not apply to 'retrieval' flows; force in the payload restarts an incremental retrieval at its declared start.`
Any other operation fails with `A retrieval flow runs the retrieve and plan operations; '<name>' is not one of them.`

A run that names a partition (`--set partition=test`, or a schedule whose `values` name one) is refused:
`Retrieval flow '<name>' names no partitions: it retrieves from the partition its source.headers name, so a run cannot target '<partition>'. Leave the partition out of its run.`
Running the flow is otherwise SQLFlow's [sqlflow run](../../../../sqlflow/docs/reference/cli/run.md) and `sqlflow trigger`; see
[Running OSDU flows](../cli/run.md) for the options every OSDU kind shares. On the control plane, `${env:NAME}`
references in the flow resolve from the central configuration ([sqlflow config](../cli/config.md)) before the node's
own environment.

The run's `result` (in `run.json`):

| Operation | Result fields |
| --- | --- |
| `retrieve` | `retrievalId`, `location` (the run's directory), `manifest`, `windowField`, `windowFrom`, `windowTo`, `records`, `files`, `bytes`, `nothingToDo`, `kinds[]` (`kind`, `query`, `records`, `missing`, `files[]`). |
| `plan` | `windowField`, `windowFrom`, `windowTo`, `location`, `kinds[]` (`kind`, `query`, `totalCount`), `total`. |

## What a run records

A retrieve run registers the flow's ledger in the partition its header names, then opens an `osdu.Retrieval` row when it
starts and closes it with its counts: the kinds and query it read, its window (`WindowTo` of the latest `done` row is the
next run's `from`), where its files and manifest went, what it wrote, and its status. Every column, key and index:
[`osdu.Retrieval`](../tables/retrieval.md).

The pipeline's **Retrievals** tab in the GUI lists these rows, newest first, and so does
`GET /api/v1/delivery/flows/{pipelineId}/retrievals?max=<n>` (100 by default, at most 1,000; 409 for a pipeline that is
not a retrieval flow). See [the ledger](../concepts/ledger.md).

## Lineage

The flow reads each kind it names on its platform and partition, and writes `part-*.jsonl` (or `part-*.jsonl.gz`) and
the manifest under `target.location`. A SQLFlow file flow that reads that location is ordered after the retrieval,
as after any flow that lands files. See [Lineage](../concepts/lineage.md).

## Validation errors

| Message | Cause |
| --- | --- |
| `source.kind (one kind) or source.kinds (a list) is required.` | No kind. |
| `source kind '<kind>' is not authority:source:entityType:version (wildcards allowed per segment).` | A kind of the wrong shape. |
| `source.kinds lists the same kind more than once.` | A kind twice. |
| `source.headers must declare a non-empty 'data-partition-id'. Every OSDU service requires it and rejects a request without it.` | No partition header. |
| `source.pageSize must be between 1 and 1000.` | Page size out of range. |
| `source.fetchParallelism must be between 1 and 64.` | Read-backs out of range. |
| `source.incremental.since '<value>' is not a date and time (use RFC 3339, such as 2026-01-01T00:00:00Z).` | An unreadable `since`. |
| `source.incremental.lagMinutes must not be negative.` | A negative lag. |
| `source.incremental.field '<field>' must be a field path without whitespace.` | A field with spaces. |
| `<key> must be a path under the endpoint, starting with '/'.` | A service path without a leading `/`. |
| `target.format '<format>' is not supported; a retrieval writes jsonl.` | Another format. |
| `target.compression must be none or gzip.` | Another compression. |
| `target.rollRecords must be at least 1.` | A roll below 1. |
| `target.manifest is a file name inside the run's directory, not a path.` | A `/` or `\` in the manifest name. |
| `target.location uses '{<token>}', which is neither a run token ({run}, {date}) nor declared under parameters.` | An undeclared token. |
| `target.location '<location>' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.` | A `${...}` reference in the location. |
| `source.probePath is not a key of a retrieval flow: the target probe covers delivery flows only, and a retrieval run never calls it. A retrieval that cannot reach the search service fails on its first page and says why; remove the key.` | A delivery flow's probe path. |
| `source.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: ...` | A literal secret. |
| `reliability.concurrency must be at least 1.` | Concurrency below 1. |
| `reliability.parallelInterfaces says how many interfaces of a delivery flow run at once; this flow declares none.` | A delivery-only setting. |

Each message is prefixed with the file path.

## See also

- [Retrieving OSDU records into tables](../guides/retrieving-records.md): the end-to-end walkthrough.
- [Flow documents overview](overview.md): how the OSDU kinds fit with SQLFlow's flows.
- [JSON / NDJSON source](../../../../sqlflow/docs/reference/flow/source-types/json.md): reading the files with a SQLFlow file flow.
- [Connection references and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md).
