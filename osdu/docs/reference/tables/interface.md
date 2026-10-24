---
id: delivery-table-interface
title: "osdu.Interface: each interface of a delivery flow as the repository sync found it, and the ledger it keeps"
type: table
summary: "One row per interface of a delivery flow, per partition it names: its pipeline, ledger identity, route, mapping, cache version and record table."
keywords:
  - osdu.Interface
  - interface table
  - interface read model
  - delivery flow
  - ledger identity
  - route
  - mapping
related:
  - delivery-concept-database
  - delivery-flow-interfaces
  - delivery-concept-osdu-order
  - delivery-table-ledger
  - delivery-table-mapping
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
---

# osdu.Interface: each interface of a delivery flow as the repository sync found it, and the ledger it keeps

One interface of a delivery flow document, as the repository sync found it: the pipeline it belongs to, the ledger
identity its records, submissions and statistics are kept under, and what it delivers and how
([The interface read model](../flow/interfaces.md#the-interface-read-model)). The API and the GUI find the pipeline of a
ledger identity here. A document in the single form is one row with an empty `Interface`, and a flow that names its
partitions has a row per partition. Not a ledger table: it has no `PartitionId`.

Rows are kept: an interface the repository no longer declares is marked inactive, not deleted.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository, the flow's name and the interface's name, and for a flow that names its partitions the partition's name as well. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository the sync found it in, by id. |
| `FlowName` | `nvarchar(400)` | no | The flow's name: its pipeline in the repository. |
| `Interface` | `nvarchar(64)` | no | The interface's name; empty for a document in the single form. |
| `Partition` | `nvarchar(200)` | no | The partition whose ledger the row describes; empty for a flow that names no partitions. |
| `TargetPartition` | `nvarchar(200)` | no | Default `N''`. The partition the interface's records reach, as the OSDU order groups deliveries ([The OSDU order](../concepts/osdu-order.md)): the one its `target.headers` name, as the document writes it, or `*` for a source that leaves its partitions to the registry. |
| `Ordinal` | `int` | no | Where the interface is in its document, from 0. |
| `LedgerFlowId` | `uniqueidentifier` | no | The ledger identity: the flow id every ledger row of the interface carries ([`osdu.Ledger`](ledger.md)). |
| `LedgerName` | `nvarchar(200)` | no | The name the ledger identity is derived from: the flow, `flow/interface`, or the ledger it adopts. |
| `Route` | `nvarchar(32)` | no | The route the interface is delivered by: `storage`, `file`, `dataset`, `manifest`, `ddms`, `fileAndDdms`, `manifestAndDdms` or `workflow`. |
| `RouteReason` | `nvarchar(1000)` | yes | Why it goes by that route; null for the single form, whose document names its protocol. |
| `MappingReference` | `nvarchar(200)` | no | The mapping it pins, `<name>@<version>` ([`osdu.Mapping`](mapping.md)). |
| `CacheVersion` | `nvarchar(64)` | yes | The version of the partition's cache it renders against, as its `render.cacheVersion` says: `current`, or the label of the version it pins. The cache's retention keeps the records of every version an active interface pins. |
| `Kind` | `nvarchar(200)` | no | The OSDU kind the mapping fills, when the repository holds a valid mapping of that reference; empty otherwise. |
| `RecordObject` | `nvarchar(400)` | no | The record table it reads. |
| `AfterJson` | `nvarchar(4000)` | no | The interfaces of the document it waits for (`after:`), as a JSON array of names. |
| `RelativePath` | `nvarchar(1000)` | no | The flow document's file, relative to the repository root. |
| `DocumentHash` | `nvarchar(64)` | yes | SHA-256, in lowercase hex, of the flow document as parsed with its name and path left out, and of the content hash of the mapping document each interface pins ([`osdu.Mapping`](mapping.md)): one pipeline under two names has one hash, so the repository sync moves the ledger of each name to the one it found last ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)). Null for a row written before it was recorded: the control plane's backfill gives an active row its hash from the pipeline's catalog copy, and the row of a pipeline that left before keeps none, so its ledger is taken over by hand. |
| `Active` | `bit` | no | Clear once the repository no longer declares the interface. |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found it. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Interface` | `Id` | primary key | none | none | An interface by its id. |
| `IX_Interface_RepoId_FlowName_Interface_Partition` | `RepoId`, `FlowName`, `Interface`, `Partition` | unique | none | none | A repository's interface of a flow in a partition, once. |
| `IX_Interface_LedgerFlowId` | `LedgerFlowId` | index | none | none | A record's page, a submission's page and the record search finding the pipeline behind a ledger identity. |

## Written and read by

- **Written** by the repository sync (`DeliveryInterfaceCatalog.cs`), and by the control plane's backfill of rows a
  sync wrote before a column existed (`InterfaceCatalogBackfillService.cs`).
- **Read** by the OSDU order (`OrderCatalog.cs`), the cache's retention, the pipeline and partition endpoints, every
  page that resolves a ledger identity to its pipeline, the repository sync's warning about a renamed flow, and the
  intake's claim check (`SqlServerLedgerBulk.cs`), which asks whether a pipeline still keeps the ledger of the record
  that claimed an id, and the Orphaned ledgers page and a takeover (`LedgerBindings.cs`), which find the delivery ledgers
  no declared interface keeps, where their pipeline was last declared, and the interface a ledger goes to.
