---
id: delivery-table-download-scan
title: "osdu.DownloadScan: each id a download run listed, staged until the listing is whole and merged"
type: table
summary: "One row per id a download run's listing gave, with the names its file may take, held only until the run merges or fails."
keywords:
  - osdu.DownloadScan
  - download scan table
  - staging
  - download listing
  - merge
related:
  - delivery-concept-database
  - delivery-flow-download
  - delivery-table-download-run
  - delivery-table-download-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Download.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Download.cs
---

# osdu.DownloadScan: each id a download run listed, staged until the listing is whole and merged

One id a [download](download.md) run listed, staged until the listing is whole and merged into
[`osdu.DownloadRecord`](download-record.md). The listing appends what it gives 10,000 rows a bulk copy, each in its own
transaction, so nothing of the download changes while it lists, and a listing that fails part way leaves the download as
the last complete run left it. The merge deletes the run's rows in the transaction that merges them; a failed run's rows
are deleted when it closes, and those of a run whose process stopped are deleted by the next run of the download.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ScanId` | `bigint` identity | no | The row's id within the partition. |
| `DownloadRunId` | `bigint` | no | The run that listed it ([`osdu.DownloadRun`](download-run.md)). |
| `TargetId` | `nvarchar(500)` | no | Collation `Latin1_General_100_BIN2`. The OSDU id, compared exactly. |
| `Kind` | `nvarchar(300)` | yes | Its kind, as the listing gave it. |
| `Version` | `bigint` | yes | Its version, as the listing gave it. |
| `PlainName` | `nvarchar(240)` | yes | Collation `Latin1_General_100_CI_AS`. The id's own file name; null when it cannot be one (too long, or a character outside the id pattern). |
| `DistinctName` | `nvarchar(240)` | no | Collation `Latin1_General_100_CI_AS`. The name its hash makes distinct, which the file takes when the id's own is none, or another id of the download holds it but for its case. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DownloadScan` | `PartitionId`, `ScanId` | primary key | none | none | A partition's staged rows together, in the order the listing appended them. |
| `IX_DownloadScan_PartitionId_DownloadRunId_TargetId` | `PartitionId`, `DownloadRunId`, `TargetId` | index | none | none | A run's ids by id: its merge reads them in this order, and a run's leftovers are deleted by run. |

## Written and read by

- **Written** by a run's listing, a chunk at a time (`SqlServerLedgerBulk.Download.cs`).
- **Read** by the run's merge, and nothing else.
