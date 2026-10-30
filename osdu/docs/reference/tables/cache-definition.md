---
id: delivery-table-cache-definition
title: "osdu.CacheDefinition: each cached type a cache flow declares, copied from the repository by the sync"
type: table
summary: "One row per cached type a cache flow declares in a partition: where its records come from, what of each it keeps, how changes are approved, and retention."
keywords:
  - osdu.CacheDefinition
  - cache definition table
  - cache flow
  - cached type
  - lookup table
  - dictionary
  - retentionDays
  - onChange
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-flow-cache
  - delivery-table-cache-version
  - delivery-table-cache-member
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
---

# osdu.CacheDefinition: each cached type a cache flow declares, copied from the repository by the sync

A cached OSDU type as one [cache flow](../flow/cache.md) declares it: which records it captures into its partition's
cache and which paths of each it keeps. The cache flow's YAML in the repository is the only place what is cached is
defined; the sync copies the declaration here, so a refresh knows every path the partition keeps
([The partition cache](../concepts/partition-cache.md)). A type the repository no longer declares is deleted, and its
members ([`osdu.CacheMember`](cache-member.md)) are released. Not a ledger table: it has no `PartitionId`; `Scope` names
the partition.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository id, the cache flow's name and the cached type's name. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository the sync found it in, by id. |
| `FlowName` | `nvarchar(200)` | no | The cache flow that declares the type. |
| `Scope` | `nvarchar(200)` | no | The partition whose cache the flow fills: one of the partitions it names, or for a flow that names none, its `source.headers.data-partition-id` resolved. |
| `DeclaresPartitions` | `bit` | no | Set when the flow works in partitions: it names the partitions it builds a cache for, or leaves them to the registry, and has a row per type and partition it serves. |
| `Origin` | `nvarchar(16)` | no | Default `N'osdu'`. Where the type's records come from: `osdu` (searched on the platform), `table` (an ingestion table), `dictionary` (a dictionary document in the repository) or `dimension` (the members a dimension flow's dimension holds in the partition). |
| `Endpoint` | `nvarchar(1000)` | yes | For an OSDU type: the endpoint the flow searches, as declared (a reference, never a resolved value). |
| `Connection` | `nvarchar(1000)` | yes | For a table type: the flow's source connection, as declared (a reference, never a resolved value). |
| `SourceObject` | `nvarchar(400)` | yes | For a table type: the three-part name of the ingestion table read; for a dimension type, `<dimensionFlow>/<dimension>`. |
| `KeyField` | `nvarchar(128)` | yes | For a table, dictionary or dimension type: the name each row's key is kept under. |
| `DictionaryPath` | `nvarchar(1000)` | yes | For a dictionary type: the dictionary document's file, relative to the repository root. |
| `RelativePath` | `nvarchar(1000)` | no | The cache flow's file, relative to the repository root. |
| `Name` | `nvarchar(200)` | no | The short name mappings use (`UnitOfMeasure`). |
| `EntityType` | `nvarchar(200)` | no | The OSDU entity type (`reference-data--UnitOfMeasure`), or `lookup--<Name>` for a table, dictionary or dimension type. |
| `Kind` | `nvarchar(400)` | yes | For an OSDU type: the search kind the capture sweeps. |
| `Query` | `nvarchar(4000)` | yes | For an OSDU type: the search query narrowing the capture. |
| `FieldsJson` | `nvarchar(max)` | no | What the cache keeps of each record, as JSON (`[{ "path": "data.Code", "as": "Code" }]`); the path a column for a table type, and for a dictionary type the fields its document names. |
| `OnChange` | `nvarchar(16)` | no | `approve` or `auto`: what a changed cached value of this type does to the records already built from it. |
| `RetentionDays` | `int` | no | Default `7`. How many days the partition's cache keeps the records of a version after a newer one replaced it, as the flow declares it (`retentionDays`). The partition keeps the longest any of its cache flows asks for. |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found it. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheDefinition` | `Id` | primary key | none | none | A declaration by its id. |
| `IX_CacheDefinition_RepoId_FlowName_Name_Scope` | `RepoId`, `FlowName`, `Name`, `Scope` | unique | none | none | A cache flow that names its partitions declares each of its types once per partition it builds a cache for. |
| `IX_CacheDefinition_Scope_Name` | `Scope`, `Name` | index | none | none | A refresh reading every declaration of its partition; the GUI listing a partition's types and the flows filling them. |
| `IX_CacheDefinition_FlowName` | `FlowName` | index | none | none | A flow's declarations. |

## Written and read by

- **Written** by the repository sync (`DeliveryCatalogSync.cs`).
- **Read** by every cache refresh (`OsduCacheStore.cs`), the Cache page, the mapping builder, the removal of a dimension (which
  refuses one a cached type captures), and the search terms.
