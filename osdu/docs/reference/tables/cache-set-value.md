---
id: delivery-table-cache-set-value
title: "osdu.CacheSetValue: each cached value a render read, what it was read from and what it held, kept once"
type: table
summary: "One row per distinct cached value a render read: where it was read from, how it was used and what it held, kept once for every set that holds it."
keywords:
  - osdu.CacheSetValue
  - cache set value table
  - cache dependency
  - impact query
  - cached value
  - match
  - lookup
  - render context
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-concept-storage-and-retention
  - delivery-table-cache-set
  - delivery-table-cache-set-entry
  - delivery-table-update-tag
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.CacheSets.cs
  - osdu/src/SqlFlow.Delivery/Ledger/CacheSetKeeping.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheUsage.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261115192942_CacheSetValues.cs
---

# osdu.CacheSetValue: each cached value a render read, what it was read from and what it held, kept once

A cached value as a render read it: what it was read from (the partition, the cached type, the cached record, the path,
and how the render used it) and what it held. The sets of an estate hold mostly the same values (every record that reads
a unit reads the same name of it), so a value is kept here once and a set names it by its number
([`osdu.CacheSetEntry`](cache-set-entry.md)). A cache change finds the sets holding a cached record by seeking the record
here, then the sets through the values' numbers
([From a cache change to OSDU](../concepts/partition-cache.md#from-a-cache-change-to-osdu)).

When a cached value changes, the renders after it read another value of the same place, which is another row; the row
of what it held before stays for as long as a set a record points at holds it. A value no set holds is read by nothing,
and the render-context sweep removes it ([The render context](../concepts/storage-and-retention.md#the-render-context)).

The table is clustered on `ValueId`, an identity: a key of eight bytes that only grows. A value is found by what it was
read from through the unique index, whose prefix names the cached record. It came with the migration `CacheSetValues`
(module version 1.74.0), which carried every row of the render context over
([decision 0059](../../wiki/decisions/0059-cached-values-kept-once.md)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `ValueId` | `bigint` identity | no | The value's id: what a set's entry names ([`osdu.CacheSetEntry`](cache-set-entry.md) `ValueId`). |
| `Scope` | `nvarchar(200)` | no | The partition whose cache the value was read from: the partition the delivery flow delivers to. |
| `TypeName` | `nvarchar(200)` | no | The cached type's short name (`UnitOfMeasure`). |
| `ItemId` | `nvarchar(512)` | no | Collation `Latin1_General_100_BIN2`. The cached record's OSDU id; for a lookup that listed no row, the key as it is folded for the lookup; for values a [line of several comparisons](../flow/mapping-lookups-composite.md) found no record meeting together, a space, `~` when the node folds separators, and the values folded as a JSON list (` ["Z-9","NPHI"]`), a prefix no other item starts with. |
| `Path` | `nvarchar(400)` | no | What the mapping read: `id`, `Name`, `NameAlias.AliasName`, the field a lookup compared, or the fields a line compared together, joined by ` and `. |
| `Kind` | `nvarchar(16)` | no | How the render used it: `match` (the value the source was matched against), `value` (a value written into the document), `empty` (a path that held no value), `unlisted` (a key a lookup table listed no row under, or values no record met together), `listed` (the rows a `$findAll` found by a key), `unverified` (an id written as an unverified reference) or `missing` (an id the cache did not hold, which held the record). |
| `ValueHash` | `binary(32)` | no | The SHA-256 of the whole value consumed: what tells two values of one place apart, also when they read alike to their first 400 characters. The ledger hands it out as lower-case hex. |
| `ValueText` | `nvarchar(400)` | no | The value as it was consumed, truncated for display; for values looked up together, the values as a JSON list. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheSetValue` | `ValueId` | primary key | none | none | A value by its id: the order the table is kept in, and how an entry reaches its value. |
| `IX_CacheSetValue_Scope_TypeName_ItemId_Path_Kind_ValueHash` | `Scope`, `TypeName`, `ItemId`, `Path`, `Kind`, `ValueHash` | unique | none | none | A value once, which a set being written finds its values by. Its prefix is the impact query: the values read from a cached record of a partition's cache, and, by the prefix of their items, a type's values looked up together. |

The unique index's columns can together be longer than the 1,700 bytes an index key holds, which SQL Server says when the
index is made; a value whose key would be is refused when it is written. The table it replaced refused one past 900 bytes,
so nothing that was kept before is refused now.

## Written and read by

- **Written** by the ledger when it stages a rendered record whose set holds a value not kept yet
  (`SqlServerLedgerBulk.CacheSets.cs`): the new values first, under an application lock (`osdu.CacheSetValue.add`) that
  orders their writers, so two runs minting sets that share a new value at once keep it once; then the set and its
  entries, under a lock (`osdu.CacheSetValue.use`) shared between sets being written and taken alone by the sweep, so an
  entry never names a value that is gone.
- **Read** by a cache change's impact analysis (`CacheImpact.cs`), the cache's gaps, and a record's `/cache` route.
- **Deleted** by the render-context sweep (`SqlServerLedgerBulk.CacheSets.cs`) every hour, after the sets: every value
  no entry names, whichever left it, a removed set or a set that was never written whole.
