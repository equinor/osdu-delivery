---
id: delivery-table-cache-set
title: "osdu.CacheSet: each distinct combination of cached values that delivered records were built from"
type: table
summary: "One row per distinct set of (type, cached record, path, value) a render consumed: what a record's CacheSetId names, and whether a change gates it."
keywords:
  - osdu.CacheSet
  - cache set table
  - cache dependencies
  - CacheSetId
  - gated sets
  - cache change
  - approval
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-table-cache-set-entry
  - delivery-table-update-tag
  - delivery-table-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
---

# osdu.CacheSet: each distinct combination of cached values that delivered records were built from

One distinct combination of cached values that records were built from: the set of (type, cached record, path, value) a
render consumed, whose entries are [`osdu.CacheSetEntry`](cache-set-entry.md) rows. Records share sets heavily (every log
that resolved metres and the same wellbore shares one), so the estate's dependency trail is a few thousand sets rather
than a row per record per value. A record points at its set ([`osdu.Record`](record.md) `CacheSetId`, and
`HeldCacheSetId` for what a held render missed); a cache change finds the sets that hold the changed value, and through
them the records ([From a cache change to OSDU](../concepts/partition-cache.md#from-a-cache-change-to-osdu)).

Never deleted: a record names its set.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `SetId` | `bigint` identity | no | The set's id: what a record's `CacheSetId` and `HeldCacheSetId` name. |
| `SetHash` | `nvarchar(64)` | no | The content hash of the set's entries: the identity a render computes without a round trip. |
| `EntryCount` | `int` | no | How many entries the set holds. |
| `Gated` | `bit` | no | Set while a change to one of its values is tagged and unapproved ([`osdu.UpdateTag`](update-tag.md)). The planner reads the gated sets once per run and skips their records, so holding records back never means writing to them. |
| `FirstSeenUtc` | `datetime2` | no | When a render first wrote the set. |
| `LastSeenUtc` | `datetime2` | no | When a render last used it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheSet` | `SetId` | primary key | none | none | A set by its id. |
| `IX_CacheSet_SetHash` | `SetHash` | unique | none | none | A set by its content, once: what a render looks its set up by. |
| `IX_CacheSet_Gated` | `Gated` | index | `[Gated] = 1` | none | The gated sets, which the planner reads every run; the filter keeps it to the handful that are gated. |

## Written and read by

- **Written** by the ledger when it stages a rendered record (`OsduLedger.cs`), and gated and ungated as cache changes
  are tagged and decided.
- **Read** by the planner, a record's `/cache` route, and a cache change's impact.
