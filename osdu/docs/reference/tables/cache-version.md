---
id: delivery-table-cache-version
title: "osdu.CacheVersion: each version of a partition's cache, the whole cache as one merge left it"
type: table
summary: "One row per version of a partition's cache: its label and place in the history, who captured it and from where, its types and counts, and whether it was pruned."
keywords:
  - osdu.CacheVersion
  - cache version table
  - partition cache
  - cache capture
  - current version
  - cache retention
  - system properties
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-flow-cache
  - delivery-table-cache-item
  - delivery-table-cache-definition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheVersions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheStreams.cs
---

# osdu.CacheVersion: each version of a partition's cache, the whole cache as one merge left it

One version of a partition's cache: the whole cache as one merge left it, written by the cache flow whose capture moved
it ([Versions](../concepts/partition-cache.md#versions)). A capture that changes what the cache holds writes the next
version, which becomes current; one that changes nothing writes none, because a new version moves the render context of
every record built against the cache. A version's content never changes once written. Its records are
[`osdu.CacheItem`](cache-item.md) ranges, counted in `Sequence`. Not a ledger table: `Scope` names the partition.

Never deleted: a record's render context names the cache version it was rendered against. The cache's retention removes
an old version's records after `retentionDays` and marks the version pruned; its row still describes it
([Retention](../concepts/partition-cache.md#retention)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the partition and the version label. |
| `Scope` | `nvarchar(200)` | no | The partition whose cache the version belongs to. |
| `FlowName` | `nvarchar(200)` | no | The cache flow whose capture, or import, wrote the version. |
| `Version` | `nvarchar(64)` | no | The label, minted from the capture instant (`20260910T165153Z`), with the sequence appended when two captures share a second. |
| `Sequence` | `int` | no | The version's place in the partition's history, `1` for the first; the ranges of [`osdu.CacheItem`](cache-item.md) count in it. |
| `CapturedUtc` | `datetime2` | no | When the version was written. |
| `ContentHash` | `nvarchar(64)` | no | The hash of the whole content, checked on every load, so a version altered after it was written is refused. |
| `PreviousVersion` | `nvarchar(64)` | yes | The version that was current when this one was written, which the capture was merged onto; null for the first. |
| `Current` | `bit` | no | Whether this is the newest version, the one deliveries render against unless a flow pins another. |
| `RunId` | `uniqueidentifier` | yes | The platform run that captured the version; null for one imported from files. |
| `CapturedBy` | `nvarchar(200)` | no | Who asked for it: the capturing run's requester as SQLFlow records it (the token's subject for a run a person queued, `schedule:<name>` for one a schedule fired, `cli:<user>@<machine>` for a direct CLI run), or `cli:<user>@<machine>` for an import from the command line. |
| `Origin` | `nvarchar(1000)` | no | Where the content came from: the OSDU endpoint reference a capture searched, or the directory an import read. |
| `TypesJson` | `nvarchar(max)` | no | The types the version holds, by name, each with its entity type and record count: `[{ "name", "entityType", "items" }]`. |
| `SystemPropertiesJson` | `nvarchar(max)` | no | Default `N'[]'`. The partition's system properties the capture found, kept apart from the cached records because they describe the platform: `[{ "service", "name", "state", "source", "detail" }]`. Empty for a version written before captures recorded them, or by an import onto a cache no capture had asked about ([System properties](../concepts/partition-cache.md#system-properties)). |
| `Items` | `bigint` | no | How many cached records the version holds across its types. |
| `PrunedUtc` | `datetime2` | yes | When the cache's retention removed the version's records; null while they are kept. A pruned version can no longer be read, compared or rendered against. |
| `PrunedBy` | `nvarchar(200)` | yes | Who pruned them: the requester of the refresh that applied the retention, the account of the import, or the operator who purged the cache's history; null while they are kept. |
| `ChangesJson` | `nvarchar(max)` | yes | What the version changed against the one before it, per type, as counted from its records before a retention removed any: `[{ "type", "changed", "added", "removed" }]`. Null while the history can still count them from the records. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_CacheVersion` | `Id` | primary key, nonclustered | none | none | A version by its id; nonclustered, so the table is stored in partition order. |
| `IX_CacheVersion_Scope_Version` | `Scope`, `Version` | unique | none | none | A partition's version by its label, once. |
| `IX_CacheVersion_Scope_Sequence` | `Scope`, `Sequence` | unique, clustered | none | none | The table's clustered key: a partition's versions in order, so a refresh reads and writes its partition's versions as one range and refreshes of different partitions never wait on each other. A concurrent second write of the same partition collides on it, so two captures can never both claim the next version. |
| `IX_CacheVersion_RunId` | `RunId` | index | none | none | The versions a platform run captured. |

## Written and read by

- **Written** by a cache refresh, a pipeline's cache step and a cache import (`OsduCacheStore.cs`), and marked pruned by
  the retention or a purge of the history.
- **Read** by every render that reads the cache, the Cache page and its history, `sqlflow cache`, and the cache API
  (`CacheVersions.cs`, `DeliveryCacheStreams.cs`).
