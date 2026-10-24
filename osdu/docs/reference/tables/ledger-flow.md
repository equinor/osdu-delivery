---
id: delivery-table-ledger-flow
title: "osdu.LedgerFlow: each retrieval, assertion, dimension and inventory flow as the repository sync found it"
type: table
summary: "One row per retrieval, assertion, dimension or inventory flow of a repository: its kind, file, document hash, and whether it keeps a ledger per partition."
keywords:
  - osdu.LedgerFlow
  - ledger flow
  - flow read model
  - document hash
  - renamed flow
  - partitioned ledger
related:
  - delivery-concept-database
  - delivery-concept-renaming-a-pipeline
  - delivery-table-interface
  - delivery-table-ledger
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/LedgerFlowCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerBindings.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
---

# osdu.LedgerFlow: each retrieval, assertion, dimension and inventory flow as the repository sync found it

One flow of a repository that keeps a ledger and delivers nothing (a retrieval, assertion, dimension or inventory flow), as
the repository sync found it: the hash of its document, by which the sync pairs a renamed flow with the one it replaced,
and whether it keeps a ledger in each partition it works in, which says the ledger it keeps in a partition
([Renaming a pipeline](../concepts/renaming-a-pipeline.md)). These flows keep their ledgers under their names, so a flow's
ledgers are found in [`osdu.Ledger`](ledger.md) by its kind and name. A delivery flow's interfaces are described in
[`osdu.Interface`](interface.md) instead. Not a ledger table: it has no `PartitionId`.

Rows are kept: a flow the repository no longer declares is marked inactive, with the hash it last had, since the catalog
keeps no copy of a flow that left.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository and the flow's name. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository the sync found it in, by id. |
| `FlowName` | `nvarchar(400)` | no | The flow's name: its pipeline in the repository, and the name its ledgers are kept under. |
| `Kind` | `nvarchar(16)` | no | `retrieval`, `assertion`, `dimension` or `inventory`: the flow's kind, which is the kind of ledger it keeps. |
| `Partitioned` | `bit` | no | Set for a flow that keeps a ledger of its own in each partition it works in (`name@partition`: one that names its partitions or follows the registry); clear for one that keeps one ledger, under its name, in the partition its header names. A retrieval flow is never partitioned. |
| `DocumentHash` | `nvarchar(64)` | no | SHA-256, in lowercase hex, of the flow document as its YAML reads, with its top-level `name` left out and the keys of every mapping taken in order, so comments, layout and key order play no part: one flow under two names has one hash, so the repository sync moves the ledgers of each name to the one it found last. |
| `RelativePath` | `nvarchar(1000)` | no | The flow document's file, relative to the repository root. |
| `Active` | `bit` | no | Clear once the repository no longer declares the flow. A flow whose file does not read or parse keeps its row as it was. |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found it, or found it again after it left: of the names of one document, the one found last owns its ledgers. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_LedgerFlow` | `Id` | primary key | none | none | A flow by its id. |
| `IX_LedgerFlow_RepoId_FlowName` | `RepoId`, `FlowName` | unique | none | none | A repository's flow of a name, once: the sync reconciles a repository's rows by it. |
| `IX_LedgerFlow_Kind_FlowName` | `Kind`, `FlowName` | index | none | none | Whether a declared flow keeps the ledgers of a kind and name: the orphaned ledgers, and the flow behind one. |

## Written and read by

- **Written** by the repository sync (`LedgerFlowCatalog.cs`, from `DeliveryCatalogSync.cs`), and by the control plane's
  backfill of a repository with none of these flows described, from its catalog copies
  (`InterfaceCatalogBackfillService.cs`).
- **Read** by the repository sync, which pairs a renamed flow with the one it replaced by its hash and asks for the move of
  its ledgers, and by the Orphaned ledgers page and a takeover (`LedgerBindings.cs`), which find the ledgers no declared
  flow keeps, where their flow was last declared, and the ledger a flow keeps in a partition.
