---
id: delivery-table-download-record
title: "osdu.DownloadRecord: each id a download lists, the file it is written to and the version that file holds"
type: table
summary: "One row per OSDU id a download lists: its file, the version, size and SHA-256 the file holds, when it was listed, changed and gone, and why a write failed."
keywords:
  - osdu.DownloadRecord
  - download record table
  - file name
  - written version
  - sha256
  - gone
  - downloaded record state
related:
  - delivery-concept-database
  - delivery-flow-download
  - delivery-table-download
  - delivery-table-download-run
  - delivery-table-download-scan
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Download.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Download.cs
  - osdu/src/SqlFlow.Delivery/Engine/Downloads/DownloadFileNames.cs
---

# osdu.DownloadRecord: each id a download lists, the file it is written to and the version that file holds

One OSDU id a [download](download.md) lists, from the first run that listed it on. The merge of a run's listing adds the
ids new to the download, with the file name each takes ([File names](../flow/download.md#file-names)), changes those
whose version or kind moved, and marks those it did not list gone; a write records the version the file holds. A row is
never removed: its file stays in the folder, gone or not, so its name is never given to another id. Where a record
stands (`written`, `due`, `problem`, `gone`) is read from its columns
([Where a record stands](../flow/download.md#where-a-record-stands)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `DownloadRecordId` | `bigint` identity | no | The row's number within the partition, which a page of records is keyset paged by. |
| `DownloadId` | `int` | no | The download that lists it ([`osdu.Download`](download.md)). |
| `TargetId` | `nvarchar(500)` | no | Collation `Latin1_General_100_BIN2`. The OSDU id, compared exactly. |
| `Kind` | `nvarchar(300)` | yes | Its kind, as the listing last gave it. |
| `Version` | `bigint` | yes | Its version, as the listing last gave it. |
| `FileName` | `nvarchar(240)` | no | Collation `Latin1_General_100_CI_AS`. The file's name in the download's folder: the id's own, or the one its hash makes distinct. Compared ignoring case, as the folders of Windows and macOS compare it. |
| `FirstSeenUtc` | `datetime2` | no | When a run first listed it. |
| `ChangedUtc` | `datetime2` | no | When the listing last gave another version or kind. |
| `GoneUtc` | `datetime2` | yes | When a complete listing first did not list it; null while OSDU serves it. |
| `WrittenVersion` | `bigint` | yes | The version of the record the file holds, as storage returned it; null until a file was written. |
| `WrittenUtc` | `datetime2` | yes | When the file was written. |
| `WrittenRunId` | `bigint` | yes | The run that wrote it ([`osdu.DownloadRun`](download-run.md)). |
| `Bytes` | `bigint` | yes | The file's size. |
| `Sha256` | `binary(32)` | yes | The SHA-256 of the file's bytes, which tells whether the file in the folder is the one the run wrote. |
| `Problem` | `nvarchar(1000)` | yes | Why its last write did not happen (storage did not return the record); null once a file holds it. |
| `ProblemUtc` | `datetime2` | yes | When that write was tried. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DownloadRecord` | `PartitionId`, `DownloadRecordId` | primary key | none | none | One record by its number: a write records what it came to here. |
| `IX_DownloadRecord_PartitionId_DownloadId_TargetId` | `PartitionId`, `DownloadId`, `TargetId` | unique | none | none | One row per id of a download: the merge seeks it by id. |
| `IX_DownloadRecord_PartitionId_DownloadId_FileName` | `PartitionId`, `DownloadId`, `FileName` | unique | none | none | One id per file of a download, whatever the case of its name: the merge gives a new id the name no other holds, and no write ever lets two ids share a file. |
| `IX_DownloadRecord_PartitionId_DownloadId_DownloadRecordId` | `PartitionId`, `DownloadId`, `DownloadRecordId` | index | none | `TargetId`, `Version`, `WrittenVersion`, `GoneUtc`, `FileName` | A download's records in order, with what says whether each is due: a run walks them for those it writes, and the Downloads tab pages through them. |
| `IX_DownloadRecord_PartitionId_TargetId` | `PartitionId`, `TargetId` | index | none | `DownloadId`, `FileName`, `WrittenVersion`, `GoneUtc` | An OSDU id across every download of the partition: which downloads wrote it, and to which file. |

## Written and read by

- **Written** by a run's merge (`SqlServerLedgerBulk.Download.cs`), and by its writes, a thousand records at a time.
- **Read** by a run, for the records it writes, and by the flow's Downloads tab and the API
  (`/api/v1/delivery/downloads/{partition}/{downloadId}/records`).
