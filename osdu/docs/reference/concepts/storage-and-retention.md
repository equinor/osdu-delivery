---
id: delivery-concept-storage-and-retention
title: "What the osdu schema stores, what is kept, and what may be removed"
type: concept
summary: "The osdu schema's storage areas on the Maintenance page: what each holds, which are kept, which clean themselves, and the three cleanups an admin may set."
keywords:
  - maintenance
  - storage
  - retention
  - cleanup
  - delivery attempts
  - activity logs
  - assertion runs
  - safe to remove
  - optional
  - osdu.attempts
  - osdu.activity-logs
  - osdu.assertion-runs
  - database size
referenceRefs:
  - concept-maintenance
related:
  - delivery-concept-availability-and-retention
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-partition-cache
  - delivery-concept-dimension-tables
  - concept-maintenance
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/Maintenance/OsduStorageContributor.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Reversals.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheRetention.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventorySelections.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicateTable.cs
  - osdu/src/SqlFlow.Delivery/Ledger/CacheSetKeeping.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.CacheSets.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheSetSweepService.cs
  - osdu/tests/SqlFlow.Delivery.ControlPlane.Tests/OsduStorageAreasTests.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/StorageArea.cs
---

# What the osdu schema stores, what is kept, and what may be removed

OSDU Delivery's own data lives in the `osdu` schema of the catalog's database, and **Maintenance** (the menu's Operate
group, under Health, `/settings/maintenance`) describes
it beside SQLFlow's catalog: every table of the schema belongs to one storage area with a verdict (keep, safe to remove,
optional), its size, and how its data leaves the database. The page itself, the verdicts, limits, previews, cleanups and
the API are SQLFlow's ([Maintenance](../../../../sqlflow/docs/reference/concepts/maintenance.md)); this page says what
the module's areas are and why each verdict is what it is.

Every verdict follows the product's rule: **every delivered record stays reconstructible from the ledger alone**, from
the source row it came from to the OSDU id and version it landed as, every operator action with who and when. So the
records, their submissions, the render context they point at and every audit row are kept; work in progress, working
sets and render context no record points at clean themselves; and three areas may be removed at a cost each states,
never until an admin sets how long they stay.

## What may be removed, and what it costs

| Area (id) | What a cleanup removes | Always kept | What is lost |
| --- | --- | --- | --- |
| Delivery attempts (`osdu.attempts`), `osdu.Attempt` | Attempts started before the cut-off that a later attempt of the same record follows | Each record's latest attempt, however old | A record's earlier tries leave its Timeline and its chain of changes, and a reversal or a removal of an old run or submission no longer finds what such a record held then (a reversal reads what OSDU held from the attempt). The delivered, pending, failed and deleted counts come from the records and are unaffected. |
| Activity logs (`osdu.activity-logs`), the `Log` column of `osdu.Activity` | The captured free-text log of activities that started before the cut-off and have finished | Every activity row: who, what, when, with which inputs, the outcome and the summary; and the log of an activity still running | The line-by-line account of an old run. |
| Assertion runs and results (`osdu.assertion-runs`), `osdu.AssertionRun`, `osdu.AssertionResult` | Finished runs started before the cut-off none of whose results is still the latest of its test, each run whole with its results. A run that completes already removes its flow's earlier runs its latest status no longer needs ([only the latest status is kept](../flow/assertion-sets.md#only-the-latest-status-is-kept)), so this reaches what that does not, such as the runs of a flow that no longer runs | Each test's latest result, and every run that holds one | The removed runs' reports, so a test's history and trend start at the oldest run kept. |

The counts are in the area's own unit (attempts, logs, runs), and a preview counts exactly what the cleanup then takes:
the count and the deletion share one query in the ledger (`CountPrunableAttemptsAsync` and `PruneAttemptsAsync`,
`CountActivityLogsAsync` and `ClearActivityLogsAsync`, `CountPrunableAssertionRunsAsync` and `PruneAssertionRunsAsync`).
Each deletes in bounded statements (4,000 attempts, 1,000 logs, 100 runs at a time), each its own short transaction,
across every partition. Traceability keeps every attempt, so leave the attempts' limit unset unless the ledger's size
calls for it, and choose it in years rather than months: statistics and trends read further back than an incident does.

## What is kept

| Area | Tables (`osdu.`) | How its data leaves |
| --- | --- | --- |
| Delivery records | `Record`, `RecordIdentity` | Only an operator's delete: removed records deleted from the ledger, or a flow's whole ledger; one line of each stays in Purged records |
| Submissions and work batches | `Submission`, `WorkBatch`, `SourceWatermark` | Deleted with the flow's ledger |
| Objects created in OSDU | `Artifact` | A step's progress row goes when its unit commits; the objects' rows stay, even when the ledger is deleted |
| Purged records | `PurgedRecord` | Never removed |
| Keys the source let go of | `RemovedKey` | Lifted when the source holds the key again, a newer delivery comes, or an operator lifts it |
| Ledger directory | `Ledger`, `LedgerPartition`, `LedgerFlow`, `Interface` | Never removed; a flow that leaves its repository is marked inactive |
| Activities and reversals | `Activity`, `ActivityRecord`, `Reversal`, `ReversalItem` | Activities never; reversals with the flow's ledger |
| Partition cache | `CacheVersion`, `CacheItem`, `CacheMember`, `CacheDefinition` | Every refresh prunes the records of the versions older than the newest the partition keeps (3 by default, an admin's number) or replaced longer ago than the cache flow's `retentionDays` (7 by default), keeping the current version, the one it replaced and every pinned version ([Retention](partition-cache.md#retention)) |
| Render context of delivered records | `CacheSet`, `CacheSetEntry`, `UpdateTag` | A set no record points at is removed once no render has used it for a day ([The render context](#the-render-context)); the change log is never removed |
| Dimensions | `Dimension`, `DimensionMember`, `DimensionValue`, `DimensionAttribute`, `DimensionAttributeName`, `DimensionElement`, `DimensionCollectedText`, `DimensionKeyRecord`, `DimensionRecord`, `DimensionView`, and every `dim_*` table | Rewritten by each build, which first deletes what the build before removed ([What a build keeps](../flow/dimension.md#what-a-build-keeps)); a dimension its flow no longer declares is removed with its table on the Dimensions page |
| Dimension builds and changes | `DimensionRun`, `DimensionChange` | Removed with the dimension |
| Inventories | `Inventory`, `InventoryRecord`, `InventoryVersion` | Rewritten by each build; gone ids stay until an operator removes them from the inventory |
| Inventory builds and removals | `InventoryRun`, `InventoryRemoval`, `InventoryRemovalItem` | Never removed |
| Removal runs | `RemovalRun` | Never removed |
| Retrievals | `Retrieval` | Never removed; the latest finished one is the next run's watermark |
| Downloads | `Download`, `DownloadRecord` | Updated by each run; ids OSDU no longer serves stay listed as gone, as their files stay in the folder |
| Download runs | `DownloadRun` | Never removed |
| Mappings, templates and search terms | `Mapping`, `RepositoryTemplate`, `SearchTerm`, `SearchTermRefinement` | Rewritten by each repository sync |
| OSDU schema lineage | `SchemaLineage`, `SchemaLineageKind` | One set per release built; building it again replaces it |
| Partitions and configuration | `Partition`, `AccessRestriction`, `ConfigProperty`, `SchemaVersion`, `__EFMigrationsHistory` | Managed on the Partitions and Access restrictions pages and by the configuration; an access restriction goes only once no record carries it |

The page flags the kept areas that grow with use and that nothing removes (submissions, objects created in OSDU, purged
records, activities, dimension builds, inventory builds and removals, removal runs, retrievals,
download runs), so
their growth is visible even though nothing should remove them.

**The largest areas are content, not history.** On a working estate the dimensions and the partition cache take most of
the schema: the dimensions are what the dimension flows publish as they stand now (each build rewrites them), and the
cache is the reference data the deliveries render against. Neither shrinks by a cleanup. A dimension shrinks when its
flow declares less, and goes, with its table, when the flow no longer declares it; a flow can keep its `dim_` tables and
views in another database by its [target](../flow/dimension-target.md), leaving only the dimensions' record here. The
cache's history is pruned by its own retention, which keeps its newest 3 versions unless an admin sets another number,
and its current version is as large as the reference data the cache flows capture.

## What cleans itself

| Area | Tables (`osdu.`) | Rule |
| --- | --- | --- |
| Leases and pending steps | `Lease`, `RecordEvent` | Removed when a worker's lease closes or, after a worker stopped, when the next run recovers it |
| View checks | `DimensionViewCheck` | The newest 50 checks per view and partition are kept |
| Matches and duplicate checks | `InventoryScan`, `InventorySelection`, `InventorySelectionItem`, `InventoryDuplicateCheck`, `InventoryDuplicateGroup`, `InventoryDuplicateMember`, and every `dup_*` table | A build's staging when it ends; a match or a duplicate check 7 days after it was made (one a removal used keeps its row); a check's table when its download fails or a later download completes |
| Removal staging | `RemovalKey` | Cleared when the run closes, or by the next run of the removal |
| Download staging | `DownloadScan` | Cleared when the run merges its listing or fails, or by the next run of the download |
| Dimension build staging | Every `dimstage_*` table, seven a build | Dropped when the build ends, written or not; what a build whose worker stopped left, by the next build of the same dimension once the stopped build is closed or has run for 48 hours; every build's, when the dimension is removed ([A dimension build's staging](../flow/dimension-staging.md)) |

## The render context

A render hands the ledger the cached values it read, and the record points at the set holding exactly those values
([`osdu.CacheSet`](../tables/cache-set.md) and its [entries](../tables/cache-set-entry.md)). Records that read the same
values share a set, but a record whose render reads other values (a changed unit name, another curve) moves to another
set, and the one it left is read by nothing: a record's page, a cache change's impact, and holding and releasing a
record all start from the set a record points at now, and no attempt or other history names a set. Left alone, those
sets come to outweigh everything else the ledger holds, since a set holds every value a render read.

So every hour the control plane removes each set no record points at (as its set, or as the one it is held on) that no
render has used for a day, with its values (`CacheSetSweepService`). A round looks at the next 200 sets in key order and
removes at most 2,000 values, set row and values in one short transaction, judging each set again under the delete's
own locks, so a pass of any size never holds the tables a render writes to. A ledger marks a set used whenever it hands
it to a render and checks a set it remembers against the database at least every hour, so no set a render was given is
removed before its record points at it, and a set removed while a ledger remembered it is minted again the next time
a render needs it. The change log ([`osdu.UpdateTag`](../tables/update-tag.md)) stays: what changed, when, how many
records it reached and what was decided ([decision 0053](../../wiki/decisions/0053-render-context-keeps-what-records-point-at.md)).

## Outside the database

Two things a delivery writes are not in the database and are not on the page: the files of rendered documents each work
batch names under the flow's `source.work`, which the module does not delete, and each run's folder under
`.sqlflow/runs` on the node, of which SQLFlow keeps the newest 50 per flow
([Run artifacts](../../../../sqlflow/docs/reference/concepts/run-artifacts.md)).

## The module's side of the contract

`OsduStorageContributor` describes the areas, and the control-plane module registers it with
`AddStorageContributor`. `OsduStorageAreasTests` holds the areas to the module's model: every table of the model belongs
to exactly one area, the tables builds create at run time are claimed by their prefix (`dim_`, `dup_`), the areas and
SQLFlow's follow the rules together, and only the three areas above take a limit. A table added to the model without an
area fails that test, naming the table, so the page never misses one.

The retention pass the module used to offer as `POST /api/v1/delivery/ledger/prune` is gone: its three cleanups are the
three areas above, set and run from the Maintenance page or through SQLFlow's maintenance API with their ids.
