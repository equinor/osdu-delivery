---
id: delivery-guide-retrieving-records
title: "Retrieving OSDU records into tables: from the search index to JSON Lines files to a SQL table"
type: guide
summary: "Bring OSDU records back to the lake with a retrieval flow, incrementally, and load the files into a SQL table with a SQLFlow file flow that runs after it."
keywords:
  - retrieve records from osdu
  - export osdu data
  - osdu to sql table
  - retrieval flow
  - json lines files
  - incremental retrieval
  - modifytime
  - manifest
  - load jsonl into a table
  - jsonl file flow
  - retrievals tab
related:
  - delivery-flow-retrieval
  - delivery-concept-ledger
  - delivery-cli-run
  - source-type-json
  - concept-file-discovery-and-lifecycle
  - flow-schedule
  - concept-json-xml-flattening
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalLineage.cs
  - osdu/src/SqlFlow.Delivery/Model/RetrievalDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RetrievalExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Retrieval/RetrievalRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.cs
  - osdu/src/SqlFlow.Delivery/Storage/FileStore.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - sqlflow/src/SqlFlow.Sources/SourceFormatDetector.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
---

# Retrieving OSDU records into tables: from the search index to JSON Lines files to a SQL table

This guide brings the wellbore records of the `dev` partition back out of OSDU and into a SQL table: a
[retrieval flow](../flow/retrieval.md) writes them as JSON Lines files on the lake, a little more each night, and a
SQLFlow file flow loads the files into `OsduData.pre.RetrievedWellbore`, after the retrieval in the same scheduled fire.
Use it to analyse what OSDU holds, to compare it with the source, or to keep a copy beside your own tables.

A retrieval only reads OSDU. Each line it writes is a record as OSDU holds it; nothing is rendered or reshaped, so
projecting and joining the records is the table side's work.

## Before you start

- The OSDU references the flow uses: `OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET`, `OSDU_SCOPE`,
  and an identity that may read the kinds you retrieve.
- A lake location the node can write: an Azure Storage container (`abfss://` or `https://<account>.blob.core.windows.net`),
  written with the node's Azure credential (check it with [sqlflow auth](../cli/auth.md)), or a local folder.
- The module's database (`--db <conn-ref>` or `SQLFLOW_CATALOG_DB`) for incremental runs: the watermark and the run
  history live in its ledger. Without it a retrieval still writes its files, but every run starts again at `since`.

## 1. Choose what to retrieve

- **Kinds.** A retrieval reads kinds through the search index, one cursor per kind, and a kind may carry wildcards:
  `osdu:wks:master-data--Wellbore:1.*.*` takes every 1.x version of the wellbore schema.
- **A query.** `source.query` narrows every kind with Lucene, such as `data.Source:"welldb"`; left out, every record of
  the kinds.
- **Hits or whole records.** The index holds a projection of each record. `fetchRecords: true` reads each record back
  from storage, 100 ids per request, and writes it as storage keeps it. Without it you get the hits, narrowed to
  `returnedFields` if you list some.

## 2. Write the retrieval flow

`flows/welldb-retrieval-01-wellbores.yaml`:

```yaml
flowType: retrieval
name: welldb-retrieval-01-wellbores
batch: welldb
description: Wellbore records of the dev partition, back on the lake as JSON Lines, a day's changes at a time.

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
  incremental:
    field: modifyTime
    since: 2026-01-01T00:00:00Z
  fetchRecords: true

target:
  location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores

schedule:
  name: welldb-retrieval-nightly
  cron: "0 3 * * *"
  timezone: UTC
```

- `source.headers.data-partition-id` is required: it is the partition the flow reads and keeps its runs under.
- `incremental` makes each run take the records that changed after the last completed run's window, up to five minutes
  ago (`lagMinutes`), starting from `since` the first time: those whose `modifyTime` falls in the window, and those never
  modified (OSDU gives a record `modifyTime` only from its second version on) whose `createTime` does.
- `target.location` is the root; each run writes a directory of its own beneath it.
- The schedule is published under a name so the file flow in step 6 can join it.

```bash
sqlflow validate flows/welldb-retrieval-01-wellbores.yaml
```

```text
OK  'welldb-retrieval-01-wellbores' is valid (retrieval: ${env:OSDU_URL} -> abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores).
```

## 3. Count first with a plan

```bash
sqlflow run flows/welldb-retrieval-01-wellbores.yaml --operation plan
```

A plan asks the search for the exact count of what each kind's query matches in the window, and writes nothing. The run
log says, per kind, `plan osdu:wks:master-data--Wellbore:1.*.*: 48210 matching record(s) (query: (modifyTime:[2026-01-01T00:00:00.000Z TO 2026-10-09T02:55:00.000Z} OR (createTime:[2026-01-01T00:00:00.000Z TO 2026-10-09T02:55:00.000Z} AND NOT _exists_:modifyTime)))`,
then `plan: 48210 record(s) would be retrieved into <directory>`. The result has the window and `kinds[].totalCount`.

## 4. Retrieve

```bash
sqlflow run flows/welldb-retrieval-01-wellbores.yaml
```

The run opens a row in the ledger, pages through the cursor and streams each page into the files, rolling to a new file
every 100,000 records. Its log says what it is doing: the window, the total the index reports for each kind, progress
every 100 pages, and at the end `retrieve: <n> record(s) in <f> file(s), <b> byte(s) uncompressed; manifest <path>`.
Its result (`run.json`) has the `retrievalId`, the run's directory, the manifest, the window and the counts per kind.

A read of a kind is checked against the search's total: one that comes back short is read again once, and one that is
short twice fails the run. A failed run keeps its window, so the next run reads it again.

## 5. Find the files

Each run writes a directory named for its start and its run id, with a directory per kind and the manifest:

```text
abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores/
  20261009T030000Z-1a2b3c4d/
    manifest.json
    osdu_wks_master-data--Wellbore_1.x.x/
      part-00001.jsonl
```

`manifest.json` names the flow, the run, the window, every file with its record count and uncompressed bytes, and per
kind the ids storage could not return (deleted since they were indexed, or not readable by the flow's identity).

The runs are also in the ledger's `osdu.Retrieval` table: the pipeline's **Retrievals** tab in the GUI lists them with
their status, when they started and how long they took, kinds, window, counts, location, who ran them and any error, and
so does `GET /api/v1/delivery/flows/{pipelineId}/retrievals`.

## 6. Load the files into a table

A SQLFlow file flow reads the files as JSON Lines into the ingestion database (see
[JSON / NDJSON source](../../../../sqlflow/docs/reference/flow/source-types/json.md) for its keys):

`flows/welldb-retrieval-02-wellbores-pre.yaml`:

```yaml
name: welldb-retrieval-02-wellbores-pre
batch: welldb

source:
  type: jsonl
  location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores
  options:
    srcFile: "part-*.jsonl"
    searchSubDirectories: "true"

target:
  connection: ${env:OSDU_DATA_DB}
  schema: pre
  table: RetrievedWellbore

schedule: welldb-retrieval-nightly
```

- `srcFile` takes the record files and leaves the manifests out; `searchSubDirectories` walks the run and kind
  directories.
- Nested properties become columns (`data.FacilityName` lands as `data_FacilityName`); how arrays and paths flatten is
  SQLFlow's [JSON flattening](../../../../sqlflow/docs/reference/concepts/json-xml-flattening.md).
- Which files a later run picks up, and what happens to them after, is SQLFlow's
  [file discovery and lifecycle](../../../../sqlflow/docs/reference/concepts/file-discovery-and-lifecycle.md).
- Keep the retrieval's `compression: none` (the default) for files a file flow reads: SQLFlow's file readers do not
  decompress gzip.

Both flows join the schedule `welldb-retrieval-nightly`, so one fire runs them as one group in lineage order. The
retrieval writes `part-*.jsonl` under the location the file flow reads, so lineage places the file flow after it:

```text
$ sqlflow lineage flows --no-observed
Lineage over 2 flow(s), 3 object(s), 4 edge(s) (declared)
  wave 1: welldb-retrieval-01-wellbores
  wave 2: welldb-retrieval-02-wellbores-pre
```

## 7. Later runs

- Each nightly run covers the window from the last completed run's upper bound to five minutes before it starts. Runs
  never leave a gap; a run whose window would be empty (a `since` still in the future, or a lag raised since the last
  run) has nothing to do and says so, and the next run starts where the window stood
  ([the incremental window](../flow/retrieval.md#the-incremental-window)).
- To start again from `since`, run with `--payload '{"force":true}'`.
- The schedule's `values` reach every flow that joins it, and a retrieval refuses a `partition` value: keep the
  retrieval off schedules that set one. It reads the partition its header names.

## When something goes wrong

| What you see | What it means and what to do |
| --- | --- |
| `source.headers must declare a non-empty 'data-partition-id'. ...` | Add the partition header; a retrieval reads exactly one partition. |
| `target.location uses '{region}', which is neither a run token ({run}, {date}) nor declared under parameters.` | Declare the parameter, or remove the token. |
| `No file writer handles location '<location>'. ...` | The location is neither a local path nor an Azure Storage URI. |
| `Could not open '<file>' for writing (status 403): ...` | The node's Azure credential may not write the container; check it with `sqlflow auth --scope storage`. |
| `The search of kind <kind> ... could not be read whole in 2 reads (...)` | The search service did not hand over every record twice in a row. Run again; the window has not moved. When it keeps happening on large kinds, set `source.paging: createTime`, which holds nothing on the service between pages and picks a failed read up where it stopped ([Search paging](../concepts/search-paging.md)). |
| `The search of kind <kind> ... could not be read whole in createTime order in 2 passes (...)` | Two passes in createTime order failed or came back short; each pass names the record it stopped after. Run again; the window has not moved. |
| `missing` above 0 in the manifest | Storage did not return ids the index listed: records deleted since they were indexed, or not readable by the flow's identity. Their ids are under `missingIds`. |
| `Retrieval flow '<name>' names no partitions: ... so a run cannot target '<p>'. Leave the partition out of its run.` | The run, or its schedule's `values`, named a partition. |
| `payload <property> does not apply to a retrieval flow: ...; a retrieval flow's payload names only force.` | The payload named something a retrieval has no use for. |
| Every run starts from `since` | The run has no module database, so there is no watermark: give the CLI `--db`, or run on the control plane. |

## See also

- [Retrieval flow](../flow/retrieval.md): every key, the window, the manifest and the ledger row.
- [Download flow](../flow/download.md): the records of OSDU kinds kept in a folder instead, a JSON file each named after
  its id.
- [Running OSDU flows](../cli/run.md): operations, values and payloads.
- [schedule](../../../../sqlflow/docs/reference/flow/schedule.md): named schedules and their member sets.
