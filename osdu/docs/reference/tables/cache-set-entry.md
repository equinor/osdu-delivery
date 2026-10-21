---
id: delivery-table-cache-set-entry
title: "osdu.CacheSetEntry: each cached value inside a cache set, what a render read and what it read at the time"
type: table
summary: "One row per cached value in a cache set: the partition, type, cached record and path read, how it was used, and the value as it was consumed."
keywords:
  - osdu.CacheSetEntry
  - cache set entry table
  - cache dependency
  - impact query
  - cached value
  - match
  - lookup
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-table-cache-set
  - delivery-table-update-tag
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheUsage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
---

# osdu.CacheSetEntry: each cached value inside a cache set, what a render read and what it read at the time

One cached value inside a [cache set](cache-set.md): what a render read, and what it read at the time. A later cache
version can say exactly which records its changes touch from these rows, without rendering the estate again
([From a cache change to OSDU](../concepts/partition-cache.md#from-a-cache-change-to-osdu)). Written with its set, and never
deleted.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `SetId` | `bigint` | no | The set the entry belongs to ([`osdu.CacheSet`](cache-set.md)). |
| `Scope` | `nvarchar(200)` | no | The partition whose cache the value was read from: the partition the delivery flow delivers to. |
| `TypeName` | `nvarchar(200)` | no | The cached type's short name (`UnitOfMeasure`). |
| `ItemId` | `nvarchar(512)` | no | Collation `Latin1_General_100_BIN2`. The cached record's OSDU id; for a lookup that listed no row, the key as it is folded for the lookup. |
| `Path` | `nvarchar(400)` | no | What the mapping read: `id`, `Name`, `NameAlias.AliasName`, or the field a lookup compared. |
| `Kind` | `nvarchar(16)` | no | How the render used it: `match` (the value the source was matched against), `value` (a value written into the document), `empty` (a path that held no value), `unlisted` (a key a lookup table listed no row under), `listed` (the rows a `$findAll` found by a key), `unverified` (an id written as an unverified reference) or `missing` (an id the cache did not hold, which held the record). |
| `ValueHash` | `nvarchar(64)` | no | The hash of the whole value consumed. |
| `ValueText` | `nvarchar(400)` | no | The value as it was consumed, truncated for display. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheSetEntry` | `SetId`, `Scope`, `TypeName`, `ItemId`, `Path`, `Kind` | primary key | none | none | A set's entries, each once. |
| `IX_CacheSetEntry_Scope_TypeName_ItemId` | `Scope`, `TypeName`, `ItemId` | index | none | none | The impact query: which sets hold a cached value of a partition's cache. |

## Written and read by

- **Written** with its set when the ledger stages a rendered record (`OsduLedger.cs`).
- **Read** by a cache change's impact analysis (`CacheImpact.cs`) and a record's `/cache` route.
