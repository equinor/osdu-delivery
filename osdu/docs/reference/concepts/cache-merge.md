---
id: delivery-concept-cache-merge
title: "The cache merge: how a refresh writes a version in statements of bounded size, whatever the size of a type"
type: concept
summary: "How a cache merge compares a capture with the current version by hash and writes only what moved, a page of rows per statement, and what a timeout says."
keywords:
  - cache merge
  - merge timeout
  - execution timeout expired
  - sqlexception timeout cache
  - cache refresh slow
  - large cache type
  - wellbore cache
  - fieldshash
  - cache item hash
  - cache membership
  - osdu.cachemember
  - open ranges
  - page of rows
  - command timeout
  - merge lock
related:
  - delivery-concept-partition-cache
  - delivery-flow-cache
  - delivery-table-cache-item
  - delivery-table-cache-member
  - delivery-table-cache-version
  - delivery-concept-database
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
---

# The cache merge

A refresh captures its types from their origin, then merges the capture into the partition's cache in one transaction
([What a refresh does](partition-cache.md#what-a-refresh-does)). The merge is the part that runs in the catalog's
database, under the partition's merge lock, and it is built so that no statement it sends grows with the size of a type:
the largest type of a partition (a hundred thousand wellbores and more) is merged in more statements than a small one,
each as short as a small type's, never in one statement as large as the type.

## What a merge reads and writes

Every statement reads or writes at most 2,000 rows.

| Step | What it reads or writes | How |
| --- | --- | --- |
| The current version | Every record the current version holds, unless this host has the version in memory. | Pages of 2,000 rows in the order the partition stores them, each a seek of the clustered key from where the last page ended. |
| Who holds the captured types | Every cache flow's hold of every record of the captured types ([`osdu.CacheMember`](../tables/cache-member.md)). | Type by type, pages of 2,000 holds in key order. |
| The records that changed | For each type whose content moved, the ranges the newest version holds, compared with the capture. | Pages of 2,000 from the index of the open ranges, which keeps each record's id and the hash of its values (`FieldsHash`): a record is compared by hash, and its values are never read back. |
| Ranges that end | A record that changed or left ends its range. | 1,000 rows per statement, by row id. |
| Ranges that begin | A record that arrived or changed begins a range. | 2,000 rows per save. |
| The flow's holds | Only what moved: a hold the capture still has is left as it is, one it lost is deleted by its key, one it gained is inserted. A refresh that found what it found last time writes no hold. | Deletes of 1,000 keys, inserts of 2,000 rows. |
| A type no flow declares any more | Every hold of the type. | 2,000 rows per statement until none is left. |

A refresh whose capture holds exactly what the current version holds writes no version and no row at all. One where a
handful of records moved reads its types' open ranges by hash and writes those records alone.

The database computes `FieldsHash` from the stored values (SHA-256 of `FieldsJson`), and the merge hashes the values it
would store the same way, so two values are equal exactly when the stored text is.

## When the database does not answer in time

Each statement runs with the catalog connection's command timeout (30 seconds unless the connection string sets
`Command Timeout`). A statement that passes it stops the merge, the transaction rolls back, and the partition keeps the
version it had. The run's error names the timeout and the step it stopped at:

```text
Cache flow 'osdu-reference-00-cache' could not merge into the cache of partition 'opendes': the database did not answer within the command timeout of 30 s while reading version 20261107T233751Z. Nothing of this capture was kept; the next refresh merges it again.
```

Since no statement grows with a type, a timeout means the database itself is slow or blocked: a server whose tier is
saturated (the run journal, the ledger and every cache share it), or a lock another session holds on the cache tables.
Look at the database's resource use for the run's time first.

The merge lock is separate: a second refresh of the same partition waits up to five minutes for the first to commit, and
says so when it gives up ([Several cache flows, one cache](partition-cache.md#several-cache-flows-one-cache)). The first
refresh of a large type writes every record of it, so the refreshes of the same partition that wait behind it may run
out of that wait. They hold nothing; the next run of each merges normally.

## The indexes that serve it

[`osdu.CacheItem`](../tables/cache-item.md) keeps three indexes for the merge and the cache's pages:

- The open ranges by type and record (filtered to `ToSequence IS NULL`), with each range's hash: what a merge compares
  its capture with.
- Every range by type and record, with the version that ended it: a version's listing, count and pages of a type.
- The closed ranges (filtered to `ToSequence IS NOT NULL`), with the version that began each: what the retention
  removes and the history counts.

## Related

- [The partition cache](partition-cache.md): versions, retention, and several flows sharing one cache.
- [Cache flow](../flow/cache.md): the document that declares what a cache holds.
