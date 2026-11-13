---
id: delivery-wiki-0053-render-context-keeps-what-records-point-at
title: "Decision 0053: the render context keeps only the sets of cached values a record points at"
type: decision
summary: "Why an hourly sweep removes each cache set no record points at once unused for a day, and why a ledger checks a set it remembers every hour."
keywords:
  - render context
  - cache set
  - CacheSetEntry
  - sweep
  - storage
  - change detection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/CacheSetKeeping.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.CacheSets.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheSetSweepService.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
referenceRefs:
  - delivery-concept-storage-and-retention
  - delivery-table-cache-set
  - delivery-table-cache-set-entry
  - delivery-concept-partition-cache
related:
  - delivery-wiki-0029-storage-maintenance
  - delivery-wiki-0056-cache-keeps-its-newest-versions
updated: 2026-11-13
---
# 0053: The render context keeps only the sets of cached values a record points at

Status: accepted. Narrows [0029](0029-storage-maintenance.md), which kept the render context whole. Documented in
[The render context](../../reference/concepts/storage-and-retention.md#the-render-context).

## Context

A render hands the ledger every cached value it read, and the record points at the set holding exactly those values
(`osdu.CacheSet`, `osdu.CacheSetEntry`), so a cache change finds the records it reaches through the sets. The set was
designed to be shared: records that read the same values share one, and the trail was expected to be a few thousand sets.
Nothing removed a set, and the Maintenance page called the area kept, never removed.

A production estate's catalog database (Azure SQL, 32 GB at most) reached 24.5 GB in four weeks from about 5 GB of source
data. Measured on that estate: `osdu.CacheSetEntry` held 11.6 GB, 15.3 million values in 47,866 sets for 12,932 records.
35,610 of the sets (11.5 million values, about 8.8 GB) were pointed at by no record, and 11,866 of the 12,256 sets that were
served exactly one record. Sets came in bursts of about 10,000 on the days the estate rendered again: a record's set is
replaced whenever a render reads other values, and one changed value every record reads (a group mapping) gives every
record a new set.

Nothing reads a set no record points at. A record's page reads its current set, a cache change's impact already skipped a
set with no record under it (`CacheImpact.cs`), holding and releasing a record go through the set it points at or is held
on, and no attempt or other history names a set.

## Decision

- **Every hour the control plane removes each set no record points at**, as its set or as the one it is held on, that no
  render has used for a day, with its values (`CacheSetSweepService`). The change log (`osdu.UpdateTag`) stays.
- **A round is small.** It looks at the next 200 sets past a cursor and removes at most 2,000 values, set row and values in
  one transaction, judging each set again under the delete's own locks, so a pass of any size never takes a lock on the
  whole table the renders write to.
- **A set a render was given is never one the sweep takes.** A ledger marks a set used when it hands it to a render, and
  checks a set it remembers against the database at least every hour, so a set it hands out was used within the hour, well
  inside the day. A set removed while a ledger remembered it is found gone at the check and minted again.

## Rejected

- **Removing the old set when a record moves to a new one.** A render does not know whether other records still point at
  the set it leaves without a query per record, and the delete would sit on the delivery's path; a sweep beside the
  deliveries does the same work in bulk.
- **A retention an admin sets.** Nothing reads these sets, so no setting chooses how long to keep them; the day is only
  there so no render's set is taken before its record points at it.
- **Keeping the sets as history.** No history names a set, and what a cache change did is in the change log.

## Consequences

- The space the sweep frees goes back to the database for its tables to reuse; the data file keeps its size, and the
  entries' second index stays sparse until it is rebuilt.
- A burst still adds a set for every record it renders again, for the day until the sweep takes the sets the records
  left. The sets records point at keep each value once per set, which a later change can store once per estate.
