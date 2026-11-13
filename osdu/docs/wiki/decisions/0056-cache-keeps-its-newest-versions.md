---
id: delivery-wiki-0056-cache-keeps-its-newest-versions
title: "Decision 0056: a partition's cache keeps its newest three versions, and an admin sets another number"
type: decision
summary: "Why a partition's cache keeps the records of its newest 3 versions by default, beside retentionDays, and why the number is the partition's, set by an admin."
keywords:
  - partition cache
  - cache retention
  - versions kept
  - CacheVersionsKept
  - retentionDays
  - storage
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheRetention.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryPartitionRegistry.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCachePruneEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/gui/src/features/delivery/CacheVersionsKeptButton.tsx
referenceRefs:
  - delivery-concept-partition-cache
  - delivery-flow-cache
  - delivery-cli-cache
  - delivery-table-partition
  - delivery-concept-storage-and-retention
related:
  - delivery-wiki-0029-storage-maintenance
  - delivery-wiki-0053-render-context-keeps-what-records-point-at
updated: 2026-11-13
---
# 0056: A partition's cache keeps its newest three versions, and an admin sets another number

Status: accepted. Documented in
[How many versions are kept](../../reference/concepts/partition-cache.md#how-many-versions-are-kept). Leaves
[0029](0029-storage-maintenance.md) as it is: the cache's retention was never one of the Maintenance page's areas an
admin turns on, and it ran by default before this.

## Context

Every refresh and import of a partition's cache prunes the records of the versions the partition no longer needs. It
kept every version replaced less than `retentionDays` ago, 7 days unless a cache flow declares another number, and set
no limit on how many. A partition filled by many cache flows on schedules writes a version whenever any of them changes
what the cache holds, so a week holds many versions: an estate's Cache page offered fifteen readable versions from the last
five days, several a day. No cache flow in the estates this project knows declares `retentionDays`.

The owner, working through the growth of a production catalog database (24.5 GB of 32 GB in four weeks), asked for a
cache to keep at most three versions by default, the number tunable in an admin section, and the older ones removed
automatically.

## Decision

- **A count beside the days, in the one retention pass.** A version keeps its records while it is among the partition's
  newest versions, the current one counted, and was replaced less than the retention ago; the current version, the one
  it replaced and every version a delivery flow pins keep theirs whatever the number and the days. Every refresh, import
  and purge applies it, so there is still one way a version loses its records.
- **The number is the partition's: 3 until an admin sets another**, from 2 to 1000, kept on the partition's registry row
  (`osdu.Partition.CacheVersionsKept`). The cache, its history and its purge are a partition's, and the registry is
  where a partition's settings live. A cache whose partition is not registered keeps 3.
- **An admin sets it where the history is described**: on the Cache page's Setup tab beside the **History kept** line,
  with `PUT /cache/retention` and `sqlflow cache keep`. The page shows what a number prunes before it is saved, and the
  number applies at once rather than at the next refresh.
- **A pinned version takes no place among the newest.** It is kept because a delivery flow renders against it, not
  because it is recent.
- **Pruning removes only records, as before.** A pruned version stays listed with who captured it, when and what it
  changed, and a delivered record keeps the cached values it was built from in its cache set
  ([0053](0053-render-context-keeps-what-records-point-at.md)), so the number never reaches the ledger.

## Rejected

- **Replacing `retentionDays` with the count.** A document declaring it would be refused at its next sync, in an estate
  this project cannot see; kept beside the count, it still shortens the history of a rarely refreshed cache, and every
  document stays valid.
- **The number in a cache flow's YAML.** Several flows fill one partition's cache; one flow's author cannot speak for the
  others, and the number is a storage decision an operator makes, which the owner asked to tune from an admin section.
- **The number in the control plane's configuration.** Changing it would take a deployment. A deployment works from its
  configuration alone without it, since the default holds, and the configuration never sets it, so a partition the
  configuration names takes an admin's number as well.
- **One number for the whole estate.** Partitions differ in how often their caches change; the retention, the purge and
  the history are each a partition's already.
- **Off until an admin sets it**, as 0029 has every Maintenance area start. The cache's retention was on by default before
  this, the owner asked for the older versions to go without anyone acting, and nothing a delivered record needs goes.

## Consequences

- At the first refresh after the upgrade, each partition's cache prunes the records of every version but its newest 3
  (and its pinned ones); the History tab keeps listing them, marked pruned.
- A version older than the newest three can no longer be browsed or compared on the Cache page, nor pinned with
  `render.cacheVersion` once pruned; an admin keeps more versions first to compare further back.
- `retentionDays` reads as a limit within the newest versions: a weekly refresh keeps three weeks of versions only when
  a flow asks for that many days.
