---
id: delivery-table-download
title: "osdu.Download: each download of a download flow in a partition, and the folder its last run wrote"
type: table
summary: "One row per download of a download flow's ledger in a partition: the kind it reads, the folder its last completed run wrote the files to, and that run."
keywords:
  - osdu.Download
  - download table
  - download flow
  - download folder
  - last download run
related:
  - delivery-concept-database
  - delivery-flow-download
  - delivery-table-download-run
  - delivery-table-download-record
  - delivery-table-download-scan
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Download.cs
---

# osdu.Download: each download of a download flow in a partition, and the folder its last run wrote

One download of a [download flow](../flow/download.md) in a partition, created by its first run and kept in step with
what the flow declares by every run after. It says what the download reads and where its last completed run wrote the
files; its records are [`osdu.DownloadRecord`](download-record.md), its runs [`osdu.DownloadRun`](download-run.md), and
its counts by state are read from its records. A download the flow no longer declares keeps its row, so the files it
wrote stay accounted for.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DownloadId` | `int` identity | no | The download's number within the partition, which the API and the GUI name it by. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the download flow in the partition ([`osdu.Ledger`](ledger.md)). |
| `FlowName` | `nvarchar(200)` | no | The flow's name. |
| `Name` | `nvarchar(200)` | no | The download's name in its flow: the folder its files are written to. |
| `Kind` | `nvarchar(300)` | no | The kind it reads, as the flow declares it (wildcards allowed). |
| `Query` | `nvarchar(4000)` | yes | The query narrowing a search read, as declared; null for every record of the kind. |
| `ReadMode` | `nvarchar(16)` | no | How it lists OSDU: `search` or `storage`. |
| `Location` | `nvarchar(2000)` | yes | The folder the last completed run wrote the files to; null until a run completed. A run writing to another folder writes every record again. |
| `CreatedUtc` | `datetime2` | no | When its first run registered it. |
| `UpdatedUtc` | `datetime2` | no | When a run last registered or completed it. |
| `LastRunId` | `bigint` | yes | The last run that completed ([`osdu.DownloadRun`](download-run.md)). |
| `LastCompletedUtc` | `datetime2` | yes | When it completed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Download` | `PartitionId`, `DownloadId` | primary key | none | none | One download by its number, as the API and the GUI ask for it. |
| `IX_Download_PartitionId_FlowId_Name` | `PartitionId`, `FlowId`, `Name` | unique | none | none | One row per download of a flow's ledger in the partition: a run finds its own by flow and name, and a flow's page lists them. |

## Written and read by

- **Written** by a run of the flow, when it registers the download and when it completes (`OsduLedger.Download.cs`), and by
  a move of the flow's ledger, which takes the rows with it.
- **Read** by the flow's Downloads tab and the API (`/api/v1/delivery/flows/{pipelineId}/downloads`).
