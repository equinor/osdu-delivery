---
id: delivery-table-download-run
title: "osdu.DownloadRun: each run of a download, where it wrote and what it wrote"
type: table
summary: "One row per run of a download: who asked, where it wrote, why it wrote every record again, what its listing changed, and the files it wrote."
keywords:
  - osdu.DownloadRun
  - download run table
  - download history
  - files written
  - rewrite forced moved
related:
  - delivery-concept-database
  - delivery-flow-download
  - delivery-table-download
  - delivery-table-download-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Download.cs
  - osdu/src/SqlFlow.Delivery/Engine/Downloads/DownloadRunner.cs
---

# osdu.DownloadRun: each run of a download, where it wrote and what it wrote

One run of a [download](download.md): opened when the run starts on the download, closed with what it did. A run its
process left `running` is closed as failed by the next run of the download. The rows are never removed: they are the
history of what each run wrote to the folder ([What a run does](../flow/download.md#what-a-run-does)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DownloadRunId` | `bigint` identity | no | The run's number within the partition. |
| `DownloadId` | `int` | no | The download it ran ([`osdu.Download`](download.md)). |
| `RunId` | `uniqueidentifier` | yes | The platform run. |
| `Actor` | `nvarchar(200)` | no | Who asked for the run. |
| `Status` | `nvarchar(16)` | no | `running`, `completed` or `failed`. |
| `StartedUtc` | `datetime2` | no | When it started. |
| `CompletedUtc` | `datetime2` | yes | When it ended; null while it runs. |
| `ReadMode` | `nvarchar(16)` | no | How it listed OSDU: `search` or `storage`. |
| `Location` | `nvarchar(2000)` | no | The folder it wrote the files to. |
| `Rewrite` | `nvarchar(16)` | yes | Why it wrote every record OSDU serves again: `forced` (the run asked) or `moved` (its folder is not the one the last completed run wrote); null when it wrote only what was due. |
| `Listed` | `bigint` | no | The ids its listing gave, repeats left out. |
| `Pages` | `int` | no | The pages its listing read. |
| `Requests` | `bigint` | no | The requests it sent: the listing's and the storage reads'. |
| `Added` | `bigint` | no | Ids new to the download. |
| `Changed` | `bigint` | no | Ids whose version or kind moved. |
| `Gone` | `bigint` | no | Ids OSDU no longer serves. |
| `Returned` | `bigint` | no | Ids OSDU serves again. |
| `Written` | `bigint` | no | The files it wrote. |
| `Bytes` | `bigint` | no | The bytes of those files. |
| `Missing` | `bigint` | no | The records due that storage did not return, so no file was written for them. |
| `Error` | `nvarchar(4000)` | yes | Why it failed, redacted. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DownloadRun` | `PartitionId`, `DownloadRunId` | primary key | none | none | One run by its number: the run that completes it, and a download's last run. |
| `IX_DownloadRun_PartitionId_DownloadId_DownloadRunId` | `PartitionId`, `DownloadId`, `DownloadRunId` | index | none | none | A download's runs, newest first: its page lists them, and a run closes the earlier ones its process left open. |

## Written and read by

- **Written** by a run of the flow, when it starts on a download and when it ends (`OsduLedger.Download.cs`).
- **Read** by the flow's Downloads tab and the API (`/api/v1/delivery/downloads/{partition}/{downloadId}/runs`).
