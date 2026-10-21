---
id: delivery-table-cache-member
title: "osdu.CacheMember: which cache flow's last capture of a type held each cached record"
type: table
summary: "One row per partition, type, cached record and cache flow whose last capture held it: what lets several cache flows share one partition's cache."
keywords:
  - osdu.CacheMember
  - cache member table
  - partition cache
  - several cache flows
  - shared cache
  - cached record
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-table-cache-item
  - delivery-table-cache-definition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
---

# osdu.CacheMember: which cache flow's last capture of a type held each cached record

That a cache flow's last capture of a type held a record. It is what lets several flows share one partition's cache: a
record a flow no longer finds leaves the cache only when no other flow's capture still holds it
([Several cache flows, one cache](../concepts/partition-cache.md#several-cache-flows-one-cache)). Current state, not
history: what each version held is in [`osdu.CacheItem`](cache-item.md). When the repository stops declaring a type
([`osdu.CacheDefinition`](cache-definition.md)), the sync deletes that flow's rows of it. Not a ledger table: `Scope`
names the partition.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Scope` | `nvarchar(200)` | no | The partition whose cache holds the record. |
| `TypeName` | `nvarchar(200)` | no | The cached type's short name (`UnitOfMeasure`). |
| `RecordId` | `nvarchar(512)` | no | Collation `Latin1_General_100_BIN2`. The OSDU record id, without a version. |
| `FlowName` | `nvarchar(200)` | no | The cache flow whose last capture of the type held the record. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheMember` | `Scope`, `TypeName`, `RecordId`, `FlowName` | primary key | none | none | A merge reading who holds each record of the captured types. |
| `IX_CacheMember_Scope_FlowName_TypeName` | `Scope`, `FlowName`, `TypeName` | index | none | none | A merge replacing one flow's rows of a type, and the sync releasing a type a flow no longer declares. |

## Written and read by

- **Written** by a cache refresh's merge (`OsduCacheStore.cs`), and deleted by the sync for a type no longer declared
  (`DeliveryCatalogSync.cs`).
- **Read** by the merge, which keeps a record another flow still holds.
