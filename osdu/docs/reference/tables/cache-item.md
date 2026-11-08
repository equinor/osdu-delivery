---
id: delivery-table-cache-item
title: "osdu.CacheItem: each cached record as a run of consecutive versions of its partition's cache held it"
type: table
summary: "One row per cached record and run of versions it held unchanged: the OSDU id, the values captured at the declared paths, and the versions it held them in."
keywords:
  - osdu.CacheItem
  - cache item table
  - cached record
  - partition cache
  - cache history
  - version ranges
  - FromSequence
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-concept-cache-merge
  - delivery-table-cache-version
  - delivery-table-cache-member
  - delivery-table-cache-definition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheVersions.cs
---

# osdu.CacheItem: each cached record as a run of consecutive versions of its partition's cache held it

One cached record as a run of consecutive versions of its partition's cache held it: the OSDU id and the values captured
at the declared paths, valid from the version at `FromSequence` up to, and not including, the one at `ToSequence`, which
is null while the newest version still holds it unchanged ([Versions](../concepts/partition-cache.md#versions)). A record
is stored once per partition however many cache flows capture it ([`osdu.CacheMember`](cache-member.md) says which do), and
a merge writes rows only for the records that changed, which it tells by `FieldsHash` without reading the values back
([the cache merge](../concepts/cache-merge.md)). Not a ledger table: `Scope` names the partition.

The retention removes the closed ranges of versions it prunes ([Retention](../concepts/partition-cache.md#retention)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `ItemId` | `bigint` identity | no | The row's id. |
| `Scope` | `nvarchar(200)` | no | The partition whose cache holds the record. |
| `TypeName` | `nvarchar(200)` | no | The cached type's short name (`UnitOfMeasure`). |
| `EntityType` | `nvarchar(200)` | no | The cached type's OSDU entity type, or `lookup--<Name>` for a table, dictionary or dimension type ([`osdu.CacheDefinition`](cache-definition.md)). |
| `RecordId` | `nvarchar(512)` | no | Collation `Latin1_General_100_BIN2`. The OSDU record id, without a version. |
| `FieldsJson` | `nvarchar(max)` | no | The captured values as JSON, in whatever shape the paths yielded, names in ordinal order. |
| `FieldsHash` | `binary(32)` | no | Computed by the database, not persisted: `CAST(HASHBYTES('SHA2_256', [FieldsJson]) AS binary(32))`, the SHA-256 of the values as stored. The index of the open ranges keeps it, so a merge compares a record's values by it alone. |
| `Terms` | `nvarchar(max)` | no | Every scalar the item holds, newline separated: what a search over cached values matches on. |
| `FromSequence` | `int` | no | The sequence of the first version that holds the record with these values ([`osdu.CacheVersion`](cache-version.md)). |
| `ToSequence` | `int` | yes | The sequence of the first version that no longer holds it so; null while the newest version still does. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheItem` | `ItemId` | primary key, nonclustered | none | none | A row by its id; nonclustered, so the table is stored in partition order. |
| `IX_CacheItem_Scope_ItemId` | `Scope`, `ItemId` | unique, clustered | none | none | The table's clustered key: a refresh reads and closes only its own partition's rows, whatever plan the table's size leads to. |
| `IX_CacheItem_Scope_TypeName_RecordId_FromSequence` | `Scope`, `TypeName`, `RecordId`, `FromSequence` | unique | none | `ToSequence` | A record holds one range per distinct content: the type listing is its prefix, so this answers both a version's listing of a type and one record's history. With each range's end beside it, a version's count and pages of a type are read from the index, and only the rows a page shows are looked up. |
| `IX_CacheItem_Scope_TypeName_RecordId` | `Scope`, `TypeName`, `RecordId` | index | `[ToSequence] IS NULL` | `EntityType`, `FieldsHash`, `ToSequence` | The ranges the newest version holds, by type in record order: what a merge compares its capture against, a page at a time, by hash. `ToSequence` is included because SQL Server otherwise looks the filtered column up in the table to check a query's `IS NULL`. |
| `IX_CacheItem_Scope_ToSequence` | `Scope`, `ToSequence` | index | `[ToSequence] IS NOT NULL` | `FromSequence`, `TypeName`, `RecordId` | The closed ranges: what a retention removes and the history counts as ended and changed, from the index alone. |
| `IX_CacheItem_Scope_FromSequence` | `Scope`, `FromSequence` | index | none | `TypeName`, `RecordId` | The rows each version began: what a version changed is counted from them, by the history and by a retention recording a version's changes before it removes its records. |

## Written and read by

- **Written** by a cache refresh's merge, a pipeline's cache step and a cache import (`OsduCacheStore.cs`), and removed by
  the retention.
- **Read** by every render that reads the cache, the cache's search, the Cache page's types and history, and the impact
  analysis of a new version (`CacheVersions.cs`).
