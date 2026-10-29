---
id: delivery-flow-download
title: "Download flow (flowType: download): every record of OSDU kinds written to a folder, a JSON file each named after its id"
type: flow-reference
summary: "flowType: download: every record of OSDU kinds in a partition written to a folder, a JSON file each named after its id, rewritten only when new or changed."
keywords:
  - download flow
  - download records from osdu
  - one file per record
  - record metadata files
  - file named after the id
  - file name from osdu id
  - colon in file name
  - case-insensitive file names
  - target.location
  - downloads
  - force
  - rewrite
  - gone records
yamlPath: "(root, flowType: download)"
related:
  - delivery-flow-inventory
  - delivery-flow-retrieval
  - delivery-concept-search-paging
  - delivery-concept-partitions
  - delivery-concept-ledger
  - delivery-cli-run
  - delivery-table-download
  - delivery-table-download-run
  - delivery-table-download-record
  - delivery-table-download-scan
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DownloadFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DownloadDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/DownloadFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/DownloadExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Downloads/DownloadRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Downloads/DownloadFileNames.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReaders.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Downloads.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Download.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Download.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDownloadEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261029193513_DownloadFlows.cs
  - osdu/docs/census/keys.download.json
  - osdu/specs/core/storage/openapi.yaml
---

# Download flow (flowType: download): every record of OSDU kinds written to a folder, a JSON file each named after its id

A download flow writes every record one or more OSDU kinds hold in a partition to a folder, one JSON file per record,
named after the record's id, holding the record as OSDU's storage service holds it. It downloads a record's metadata,
never the data a dataset links to (the LAS file behind a `dataset--File.Generic`, the bulk data of a well log). Its ledger
keeps, for every id, the file it is written to and the version that file holds, so a run writes only what is new or
changed since the last, a newer version of a record replaces its file, and a record OSDU no longer serves is marked gone
while its file stays where it is. Nothing is written to OSDU.

Use a download flow when something reads the records one file each: a person browsing a folder, a tool that opens a
record by its id, a copy of a kind kept beside the source. A [retrieval flow](retrieval.md) is the other way back to the
lake: JSON Lines files with a manifest, a new folder per run, for loading into a table.

## Example

The estate's download flow, `flows/welldb-osdu-07-download.yaml`:

```yaml
flowType: download
name: welldb-osdu-07-download
batch: welldb
mode: manual
description: Every well and wellbore record OSDU holds, written to the lake a JSON file each, named after its id.
partitions: [dev]

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body: { scope: "${env:OSDU_SCOPE}" }

target:
  location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records

downloads:
  - name: Wells
    kind: "*:*:master-data--Well:*"
  - name: Wellbores
    kind: "*:*:master-data--Wellbore:*"
```

A run writes `.../osdu/records/Wells/dev~master-data--Well~W-0001.json` and so on, one file per well and per wellbore.

## Top-level keys

Unknown keys are refused when the document is read.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `flowType` | text | required | `download`. |
| `name` | text | required | The flow's name: its pipeline identity, and the name its downloads are kept under (`name@partition` for a flow that works in partitions). |
| `description` | text | none | What the downloads are for. |
| `batch` | text | none | The grouping label the pipeline is filed under. |
| `parameters` | map | none | Parameters a download's `query` and `target.location` use as `{name}` tokens. `partition` cannot be declared: `{partition}` is always the partition the run reads ([parameters](parameters.md)). |
| `partitions` | list | none | The partitions the flow downloads from, settled as an [inventory flow's](inventory.md#top-level-keys) are: listed, hard-coded by `source.headers.data-partition-id`, or every registered partition. A run reads one partition; `*` is refused. |
| `source` | map | required | The OSDU platform, and how its ids are listed. See [source](#source). |
| `target` | map | required | Where the files go. See [target](#target). |
| `downloads` | list | required | The downloads, at least 1 and at most 100, names unique ignoring case. |
| `reliability` | map | delivery defaults | The HTTP settings a [delivery flow](delivery.md) takes: timeout, retry, rate limit, TLS, URL allowlist and response size. |
| `parallelism` | map | defaults | `recordsAtOnce` (default 8, at least 1): how many storage reads, each of up to a hundred records, a run has in flight at once. |
| `schedule`, `mode`, `lifecycle` | | none | SQLFlow's envelope keys ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). |

### source

The keys an [inventory flow's source](inventory.md#source) takes to list and read records, and none of the paths a
removal sends to: `endpoint`, `auth`, `headers`, `read` (`search` or `storage`), `queryPath`, `searchPath`, `paging`
(`cursor` or `createTime`), `recordQueryPath`, `headersPath` and `schemaPath`. A download lists its kind exactly as an
inventory does ([Reading OSDU](inventory.md#reading-osdu)): through the search index with a viewer's entitlements, or
from storage itself with its admin role, a wildcard kind expanded through the schema service. Whichever way it lists,
every record it writes is read from storage (`POST /query/records`, a hundred ids a request).

### target

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `location` | text | required | The folder each download's folder is made in: an absolute local path, or an `abfss://` or `https://<account>.blob.core.windows.net` URI. Takes `{partition}` and `{parameter}` tokens. |

Every run writes to the same folder, so `{run}` and `{date}` are refused. A relative path is refused too: a run reads
its flow from a checkout of its own, and the files would land inside it. A `${...}` reference is refused, as in every
location of a flow, and so is a credential in the URI (a SAS token): Azure Storage is written with the node's own Azure
credential ([sqlflow auth](../cli/auth.md)).

### A download

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | text | required | A letter or digit, then letters, digits, `.`, `_` and `-`, at most 100, not ending in `.` and no Windows device name (`CON`, `NUL`, `COM1`, `LPT1` and the like). It is the folder the download's files go to. |
| `description` | text | none | What the download holds. |
| `kind` | text | required | The kind read, `authority:source:entityType:version`, each segment a value, `*`, or a value with `*` inside it. |
| `query` | text | every record | A Lucene query narrowing a search read, with `{name}` and `{partition}` tokens, at most 4,000 characters. Refused on a storage read. |

## File names

A file is named after its record's id. An OSDU id is letters, digits, `_`, `-`, `.`, and from its third segment `:` and
`%` (the record id pattern of the storage API). Two of those cannot be written in a file name everywhere: Windows refuses
`:`, and Hadoop and Spark read `dev:...` as a URI scheme; `%` starts an escape in a URL, which a blob's address is. So:

- `:` is written `~` and `%` is written `=`, and the name ends `.json`: `dev:reference-data--UnitOfMeasure:ft%2Fs` is
  `dev~reference-data--UnitOfMeasure~ft=2Fs.json`. No id holds `~` or `=`, so the name turns back into the id, and no
  two ids share it. Writing `:` as `_` would not do: `_` is an id's own character, and `a:b` and `a_b` would share a file.
- OSDU ids are case-sensitive (the foot `...UnitOfMeasure:ft` and the femtotesla `...UnitOfMeasure:fT` are two records),
  and the folders of Windows and macOS are not. When an id's name is held by another id of the download but for its
  case, the id is named by its hash instead: its name as above, then `@` and the first 12 hex digits of the SHA-256 of the
  id (`dev~reference-data--UnitOfMeasure~ft@69ec6b78c414.json`). The id that sorts first by its exact characters takes its
  own name.
- A name longer than 240 characters (the 255 a file system takes, less room for the `.tmp` a local write writes first),
  or an id holding a character outside the pattern, is named by its hash the same way, cut to fit, a character outside
  the pattern written `_`.

A name holding `@` is one no id turns back from; the record's own `id` in the file says which it is, and so does the
download's ledger. An id keeps its name for its life, gone or not, so a name is never given to another id.

## What a run does

Each download the run takes is run in turn, in the order the flow declares them:

1. **List.** Every id the kind holds is read, whole or failing, and staged. A listing that fails part way merges
   nothing, so no id is marked gone because a page was lost.
2. **Merge.** The listing is merged into the download's ledger: new ids are added with their file names, ids whose
   version or kind moved are changed, ids served again return, and ids the listing did not list are marked gone.
3. **Write.** Every record OSDU serves whose file holds no version, or an older one than listed, is read from storage, a
   hundred to a request, `parallelism.recordsAtOnce` requests at once, and written to its file: the record as storage
   returned it, indented. A newer version replaces the file (a local file is written beside it and moved into place; a
   blob is overwritten). The ledger keeps the version written, the file's size and the SHA-256 of its bytes, and what was
   written is recorded every thousand records, and also when a run stops part way, so the ledger never misses a file the
   folder holds.

A record storage does not return (deleted since it was listed, or not readable by the flow's identity) gets no file; its
row says why, and the next run tries it again. A gone record's file stays, and the ledger lists it as gone.

A run writes every record OSDU serves again, not only what is due, when its payload asks (`force`), or when its folder is
not the one the last completed run wrote (the location, or a parameter in it, changed): that folder holds none of the
files. The run records why (`forced` or `moved`).

The trace says what each download did (ids listed, files written) and a line every ten thousand files, never one line
per record.

## Where a record stands

| State | Meaning |
| --- | --- |
| `written` | OSDU serves it and its file holds the version listed. |
| `due` | OSDU serves it and no file holds the version listed yet: the next run writes it. |
| `problem` | Its last write did not happen; the row says why. |
| `gone` | OSDU no longer serves it. Its file stays where it is. |

## Operations

| Operation | What it does |
| --- | --- |
| `download` (default) | Lists, merges and writes each download, as above. |
| `plan` | Counts through the search the records each download matches, names the folder it would write to, and lists the kinds a storage read expands to, reading no record and writing nothing. |

The payload takes `downloads` (names; none takes every download) and `force` (a `download` run writes every record
again). A plan takes no `force`:
`payload force does not apply to the plan operation: a plan writes no file, and only a download run writes every record again.`
Anything else is refused before the run starts, for example
`payload inventories does not apply to a download flow: only an inventory flow's runs select inventories; a download flow's payload names only downloads and force.`

```bash
sqlflow run flows/welldb-osdu-07-download.yaml --set partition=dev --operation plan
sqlflow run flows/welldb-osdu-07-download.yaml --set partition=dev
sqlflow run flows/welldb-osdu-07-download.yaml --set partition=dev --payload '{"downloads":["Wellbores"],"force":true}'
```

## What a run returns

A download run's result is the flow, the partition, how many downloads completed and failed, and per download its kind,
status, run number, folder, why it wrote every record again (when it did), the ids listed, added, changed, gone and
served again, the files written and their bytes, the records storage did not return, pages, requests and error. A plan's
result is, per download, the query, how it reads, the records it would list, the kinds a storage read expands to, the
folder, and its problems.

## What a run records

A run registers the flow's ledger in the partition it reads, and keeps its downloads in the module's database:
[`osdu.Download`](../tables/download.md) (one row per download, with the folder its last completed run wrote),
[`osdu.DownloadRun`](../tables/download-run.md) (one row per run), [`osdu.DownloadRecord`](../tables/download-record.md)
(one row per id, with its file and the version, size and hash the file holds) and
[`osdu.DownloadScan`](../tables/download-scan.md) (a run's listing, until it is merged).

## Lineage and the GUI

A download flow reads each download's kind on its platform and partition, and writes `*.json` into each download's
folder, so lineage orders it after the delivery flows writing those kinds, and a file flow reading a download's folder
after it ([lineage](../concepts/lineage.md)). A download flow's pipeline has a **Downloads** tab: each download with its
records by state, the bytes its files hold, its last run and, on hover of its name, its folder. A row opens the download
on its records, in a grid that pages as it scrolls, one tab per state and found by any part of an id or file name, and on
its runs ([the GUI](../concepts/gui.md)).

## Errors when the document is read

| Message | Cause |
| --- | --- |
| `downloads must list at least one download; a download flow without downloads has nothing to read.` | No `downloads`. |
| `downloads[1] is named 'wellbores', as an earlier download is; each download writes to the folder of its name, so each needs its own.` | Two downloads named alike, ignoring case. |
| `downloads[0].name 'CON' names the folder the download's files go to, and Windows makes no folder of that name (one ending in '.', or a device name such as CON, NUL, COM1 or LPT1). Name it otherwise.` | A name no folder can take. |
| `downloads[0] 'Wells': query narrows a search, and the flow reads storage (source.read: storage), which lists every record of a kind and takes no query. Take the query out, or read through search.` | A query on a storage read. |
| `target.location uses '{date}', and a download writes every run to the same folder, replacing a file when OSDU holds a newer version of its record; a folder per run would hold only what that run wrote. Take the token out.` | A run token in the location. |
| `target.location 'out/records' is a relative path, and a run reads its flow from a checkout of its own, so the files would land inside that checkout. Write an absolute path or a storage URI (abfss://... or https://<account>.blob.core.windows.net/...).` | A relative location. |
| `target.location carries a credential in its user info or query string (a SAS token); a flow document holds no credential, and a location takes none: write the storage URI alone, and the node writes it with its own Azure credential (sqlflow auth).` | A credential in the location. |
| `parameters declares 'partition', which a download flow keeps for the partition a run reads: {partition} in a query or the location is always that partition. Rename the parameter.` | A parameter named `partition`. |

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Download flow '<name>' reads one partition per run, since its ledger of what each file holds belongs to one partition; name the partition to read instead of '*'.` | A run asked for every partition. |
| `Download flow '<name>' writes to '<location>' once its tokens are filled, a relative path, ...` | A location opening with a parameter that fills in a relative path; that download fails, the others run. |
| `<download>: failed: <why>` | The download's listing, a storage read or a write failed; the run ends failed, every other download done, and what was written is recorded. |
