---
id: delivery-table-update-tag
title: "osdu.UpdateTag: each change to the cache that delivered records were built from, and its rollout"
type: table
summary: "One row per cache change reaching delivered records: what changed, between which versions, whether it needs approval, its decision, and its rollout."
keywords:
  - osdu.UpdateTag
  - update tag table
  - cache change
  - approve
  - rollout
  - redelivery
  - onChange
related:
  - delivery-concept-database
  - delivery-concept-partition-cache
  - delivery-table-cache-set
  - delivery-table-cache-version
  - delivery-flow-cache
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheStreams.cs
---

# osdu.UpdateTag: each change to the cache that delivered records were built from, and its rollout

One change to the cache that delivered records were built from, and what happens about it. A tag is written per change,
not per record: a corrected unit name is one decision covering every record that read it, with the count of what it
affects. Under `approve` the affected sets are gated ([`osdu.CacheSet`](cache-set.md) `Gated`) until someone decides;
under `auto` it is approved as written. Either way the update is rolled out in batches from `Cursor`, so a rollout that
stops resumes ([From a cache change to OSDU](../concepts/partition-cache.md#from-a-cache-change-to-osdu)). Not a ledger
table: `Scope` names the partition.

Never deleted: a record's render context names the cache version it was rendered against, and the tag says why it moved.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `TagId` | `bigint` identity | no | The tag's id: a partition's tags of one status are ordered by it, newest last. |
| `Kind` | `nvarchar(16)` | no | What caused the tag: `cache`. |
| `Scope` | `nvarchar(200)` | no | The partition whose cache the change was found in. |
| `TypeName` | `nvarchar(200)` | no | The cached type's short name. |
| `ItemId` | `nvarchar(512)` | no | Collation `Latin1_General_100_BIN2`. The cached record the change is to. |
| `Path` | `nvarchar(400)` | no | The path whose value changed. |
| `Change` | `nvarchar(16)` | no | `changed` (the value moved, or a path that gave none gives one), `removed` (the cached record is gone), `unmatched` (what it matched by is gone), `listed` (a lookup table now lists a key records found no row under, or a type now holds a record by a value records found none by), `relisted` (the rows a key finds are no longer the ones records read) or `found` (the cache now holds a record records reference as an unverified id). |
| `OldValue` | `nvarchar(400)` | yes | The value before. |
| `NewValue` | `nvarchar(400)` | yes | The value after. |
| `FromVersion` | `nvarchar(64)` | yes | The cache version the change was found against. |
| `ToVersion` | `nvarchar(64)` | no | The cache version that holds the change ([`osdu.CacheVersion`](cache-version.md)). |
| `Mode` | `nvarchar(16)` | no | `auto` or `approve`, as the cache declared for this type when the tag was written. |
| `Status` | `nvarchar(16)` | no | `pending`, `approved`, `rejected`, `rolling` (the rollout is marking records), `delivering` (every record is marked, and the change waits for the flows that build them to render them again) or `applied` (no record is still built from the old value). |
| `SetIds` | `nvarchar(max)` | no | The cache sets holding the changed value, comma separated; the records are found through them. |
| `AffectedRecords` | `bigint` | no | How many delivered records were built from the old value when the change was found. |
| `Processed` | `bigint` | no | How many of them the rollout has marked for redelivery so far. |
| `Cursor` | `uniqueidentifier` | yes | Where the rollout got to, in delivery-key order and then flow order, so a pass resumes rather than restarts: the delivery key of the last record marked. |
| `CursorFlowId` | `uniqueidentifier` | yes | The flow of the last record marked; with `Cursor`, where the rollout got to. |
| `DetectedUtc` | `datetime2` | no | When the change was found. |
| `DecidedUtc` | `datetime2` | yes | When it was approved or rejected. |
| `DecidedBy` | `nvarchar(200)` | yes | Who approved or rejected it. |
| `StartedUtc` | `datetime2` | yes | When its rollout started. |
| `CompletedUtc` | `datetime2` | yes | When its rollout completed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_UpdateTag` | `TagId` | primary key | none | none | A tag by its id. |
| `IX_UpdateTag_Status` | `Status` | index | none | none | The rollout queue, across every partition. |
| `IX_UpdateTag_Scope_TypeName_ItemId_Path_Status` | `Scope`, `TypeName`, `ItemId`, `Path`, `Status` | index | none | none | The tags of one cached value, by status. |
| `IX_UpdateTag_Scope_Status` | `Scope`, `Status` | index | none | none | A partition's changes of one status, newest first: the tag id the table is keyed by orders them. |

## Written and read by

- **Written** by the impact analysis of a new cache version (`CacheImpact.cs`, through `OsduLedger.cs`), by an operator's
  approval or rejection on the Cache page, and by the rollout as it marks records for redelivery.
- **Read** by the Cache page's changes, `sqlflow cache`, the rollout, and the cache API (`DeliveryCacheStreams.cs`).
