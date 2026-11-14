---
id: delivery-table-repository-team
title: "osdu.RepositoryTeam: the team each folder of a synced repository belongs to, as its repository document names it"
type: table
summary: "One row per folder of a synced repository whose osdu-delivery.yaml names a team, written by every sync; compute usage is charged by it."
keywords:
  - osdu.RepositoryTeam
  - team
  - team tag
  - repository document
  - osdu-delivery.yaml
  - compute usage team
related:
  - delivery-concept-compute-usage
  - delivery-flow-repository
  - delivery-concept-database
  - delivery-table-compute-usage
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/ComputeUsage/ComputeUsageStore.cs
---

# osdu.RepositoryTeam: the team each folder of a synced repository belongs to

One row per folder of a synced repository whose [repository document](../flow/repository.md) (`osdu-delivery.yaml`)
names a `team`. A flow belongs to the team of the nearest such folder: its own, or the nearest above it. The work a node
does for the flow is charged to that team when the node records it
([Compute usage](../concepts/compute-usage.md#teams)).

Every sync of a repository rewrites its rows from the documents it holds: a document that is gone, or that names no team
any more, loses its row. A document the sync cannot read is reported in the sync's result and leaves its folder with no
row, so the flows under it take the team of the document above.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `RepoId` | `uniqueidentifier` | no | The repository. |
| `Folder` | `nvarchar(400)` | no | The folder holding the document, relative to the repository's root with forward slashes; empty for the root. Collation `Latin1_General_100_BIN2`: a folder's name is matched exactly, as a node on Linux tells folders apart. |
| `Team` | `nvarchar(64)` | no | The team the document names. |
| `UpdatedUtc` | `datetime2` | no | When a sync last wrote the row: when it first found the team, or found it changed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RepositoryTeam` | `RepoId`, `Folder` | primary key | none | none | A repository's folders together: what a sync reconciles, and what a piece of work's team is found in. |

## Written and read by

- **Written** by every sync of a repository (`DeliveryCatalogSync.SyncTeamsAsync`).
- **Read** by each node's recorder as it writes the work it measured (`ComputeUsageStore.RecordAsync`), and by the
  Compute usage page's Team tags.
