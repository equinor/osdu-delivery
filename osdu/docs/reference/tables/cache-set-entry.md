---
id: delivery-table-cache-set-entry
title: "osdu.CacheSetEntry: that a cache set holds a cached value, the set's number beside the value's"
type: table
summary: "One row per cached value a cache set holds: the set and the value (osdu.CacheSetValue), each by its number, so a value many sets hold is kept once."
keywords:
  - osdu.CacheSetEntry
  - cache set entry table
  - cache dependency
  - impact query
  - cached value
  - render context
  - EntryId
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-table-cache-set
  - delivery-table-cache-set-value
  - delivery-table-update-tag
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.CacheSets.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261115192942_CacheSetValues.cs
---

# osdu.CacheSetEntry: that a cache set holds a cached value, the set's number beside the value's

That a [cache set](cache-set.md) holds a cached value: the set's number and the value's
([`osdu.CacheSetValue`](cache-set-value.md)), nothing else. What was read and what it held is the value's row, kept once
however many sets hold it, so a set of three hundred values costs three hundred rows of two numbers. A later cache
version can say exactly which records its changes touch from these rows and the values they name, without rendering the
estate again ([From a cache change to OSDU](../concepts/partition-cache.md#from-a-cache-change-to-osdu)). Written with
its set, and deleted with it ([The render context](../concepts/storage-and-retention.md#the-render-context)).

The table is clustered on `EntryId`, an identity: a key of eight bytes that only grows, which is all its other indexes
carry to reach a row. A set's entries are written together, so they lie together.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `EntryId` | `bigint` identity | no | The row's id, in the order the sets were written. |
| `SetId` | `bigint` | no | The set that holds the value ([`osdu.CacheSet`](cache-set.md)). |
| `ValueId` | `bigint` | no | The value the set holds: what was read, and what it read at the time ([`osdu.CacheSetValue`](cache-set-value.md)). |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheSetEntry` | `EntryId` | primary key | none | none | A row by its id: the order the table is kept in. |
| `IX_CacheSetEntry_SetId_ValueId` | `SetId`, `ValueId` | unique | none | none | What a set holds, each value once: what a record's `/cache` route reads, and what the sweep deletes a removed set by. |
| `IX_CacheSetEntry_ValueId` | `ValueId` | index | none | none | The sets holding a value: the second half of the impact query, and how the sweep tells a value no set holds. |

## Written and read by

- **Written** with its set when the ledger stages a rendered record (`OsduLedger.cs`,
  `SqlServerLedgerBulk.CacheSets.cs`), after the values it names.
- **Read** by a cache change's impact analysis (`CacheImpact.cs`) and a record's `/cache` route, each joining it to
  [`osdu.CacheSetValue`](cache-set-value.md).
- **Deleted** with its set by the render-context sweep (`SqlServerLedgerBulk.CacheSets.cs`).
