---
id: delivery-concept-partition-cache
title: "The partition cache: one versioned cache per OSDU partition, filled by cache flows and read by every delivery"
type: concept
summary: "How a partition's cache works: versions, retention, cache flows sharing one cache, pipelines updating it before and after they run, tags, rollout."
keywords:
  - partition cache
  - cache version
  - current version
  - cache refresh
  - cache retention
  - retentiondays
  - pruned version
  - purge cache history
  - prune history
  - several cache flows
  - onchange approve
  - approve a cache change
  - update tag
  - cache rollout
  - system properties
  - keywordlower
  - render.cacheversion
  - cache import
  - osdu.cacheversion
  - cache warm-up
  - first render slow
  - cache.before
  - cache.after
  - update the cache before a pipeline
  - update the cache after a pipeline
  - cache step
  - delivery holds a cached record
  - held record waits for a reference
related:
  - delivery-flow-cache
  - delivery-flow-dictionary
  - delivery-cli-cache
  - delivery-concept-change-detection
  - delivery-concept-partitions
  - delivery-concept-ledger
  - delivery-guide-lookup-table-cache
  - delivery-guide-reference-data-cache
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SystemProperty.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheRetention.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheWarmupService.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheVersions.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCachePruneEndpoints.cs
  - osdu/gui/src/features/delivery/CachePruneButton.tsx
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheSteps.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.Id.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SystemPropertyCapture.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheUpdateRolloutService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheStreams.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/gui/src/features/delivery/DeliveryCachePage.tsx
  - osdu/gui/src/module.tsx
---

# The partition cache: one versioned cache per OSDU partition, filled by cache flows and read by every delivery

OSDU Delivery keeps one cache per OSDU data partition. Every [cache flow](../flow/cache.md) that serves a partition
writes into that partition's cache, and every delivery flow that delivers to the partition reads from it while it
renders records: the reference data a mapping resolves ids against, the lookup tables it translates source values
through, the dictionaries and dimension values it reads. The cache is versioned, so every delivered record can say
exactly which cached values it was built from, and a later change to one of those values can find and redeliver exactly
the records it reaches. It lives in the module's database (the `osdu` schema), never in a repository: the cache flow
files define what is cached, and their runs fill it. Its history is bounded: every refresh prunes the records of the
versions the partition no longer needs ([Retention](#retention)).

## One cache per partition

A cache is keyed by the partition the flows reach, after references are resolved: a flow naming `partitions: [dev]`,
one whose `source.headers.data-partition-id` is `dev`, and one whose header is `${env:OSDU_PARTITION}` resolving to
`dev` all fill the same cache. A capture, an import, the repository sync and a render resolve the partition the same
way, so a flow can move from a header to named partitions and keep the cache it had. A partition name is letters,
digits, underscore, hyphen and dot, at most 200 characters, or a `${env:...}` or `${keyvault:...}` reference.

A delivery run reads the cache of the partition it delivers to ([partitions](partitions.md)). Its mapping reads each
type by name (`$cache.UnitAlias`, `$cache: UnitOfMeasure.id`), whichever cache flow filled it and whatever its origin.

## Versions

Each refresh that changes what a partition's cache holds writes a new version of the whole cache:

- **The label is the capture instant,** `20261009T012000Z`; when two captures of one partition share a second, the
  sequence is appended (`20261009T012000Z-7`).
- **The newest version is always current.** Every delivery renders against the current version unless its flow pins an
  earlier one with `render.cacheVersion`.
- **Every version stays listed,** because the render context of a delivered record names the version it was rendered
  against. A version records the cache flow and run that wrote it, who asked, where its content came from and what it
  changed. Its records are kept for the partition's [retention](#retention), then pruned.
- **A version that would change nothing is not written.** A version label enters the render context, so writing one for
  a capture that found nothing new would re-render every record built from the cache. A refresh that finds exactly what
  the current version holds writes nothing, and nothing renders again: refreshing often is free.
- **Each type carries its own content hash,** how it compares with the version before (`added`, `changed` or
  `unchanged`), and the version its content dates from. A version moves when any type or system property moves; a type
  that only rode along keeps its hash, and nothing built from it is analysed again.
- **Every load is checked.** A version whose records no longer match the hash it was written with is refused (`Version
  <v> of the cache of partition '<p>' does not match the content hash it was written with ...: its records were altered
  after the version was written, so nothing renders against it.`).
- **A version is read into memory once per process, ahead of the renders that need it.** A render reads the whole
  version it renders against; a process keeps the eight versions asked for last, and callers asking for a version while it
  is read wait on that one read. A version of a few hundred thousand records takes seconds to read and hundreds of MB to
  hold, so the control plane and every node read each partition's current version at start, and a version a refresh
  writes within `PollSeconds` of it becoming current (the [cache warm-up](control-plane.md#background-services)): the
  first run, Render or Preview after a start or a refresh finds it in memory. The warm-up logs `Version <v> of the cache
  of partition '<p>' (<n> records) is in memory, <s> s after it was asked for`.

In the database a version is a row of `osdu.CacheVersion`; the records are kept by version range in `osdu.CacheItem`
(a record is stored once and a new row is written only when it changes, arrives or leaves); `osdu.CacheMember` says
which cache flow's last capture holds each record, and which pipeline landed it when its after step put it in
(`delivery:<ledger name>`) ([the ledger](ledger.md)).

## Retention

A version's records serve whoever reads that version: the Cache page browsing or comparing it, and a delivery flow
pinning it. A delivered record needs none of them once it is delivered: the values its render read stay with it in the
ledger (its cache set), and a change tag keeps the values before and after. So every refresh and every import of a
partition's cache, whether or not it writes a version, ends by pruning the records of the versions the partition no
longer needs. It keeps the records of:

- **the current version,** which every delivery renders against;
- **the version the current one replaced,** which a run that resolved the current version just before it moved may still
  load, and which the latest change is compared with;
- **every version a delivery flow pins** with `render.cacheVersion`;
- **every version replaced less than the retention ago.** A version stops being current when the next one is captured,
  and its retention runs from that instant.

The retention is the cache flow's `retentionDays` ([cache flow](../flow/cache.md#retentiondays)), 7 days when it declares
none. When several cache flows fill one partition, the partition keeps the longest any of them declares.

Pruning a version removes only the stored rows no kept version shares: a record that did not change since is one row,
kept for the versions that still hold it. Before any row goes, the version's changes (how many records of each type
changed, arrived and left) are recorded on its row, so the History tab reads exactly as it did. A pruned version then
stays listed, marked `pruned` with when, and its records can no longer be read:

| Where | What a pruned version answers |
| --- | --- |
| A delivery flow pinning it | The run fails: `render.cacheVersion pins version <v> of the cache of partition '<p>', whose records the cache's retention pruned at <time>, so nothing can render against it. Pin a version the cache still holds ('sqlflow cache list <p>' lists them), or remove render.cacheVersion to render against the current version.` The repository sync warns about such a pin too. |
| The Cache page | History lists it with its counts; its records and its comparison with the version before are not offered. |
| The API | `GET /cache/items` and `GET /cache/diff` answer `410 Gone` naming the version and when it was pruned; `GET /cache/versions` and `/cache/history` list it with `prunedUtc`. |
| `sqlflow cache list` | The version is marked `pruned`, with when. |

Pruning is safe beside everything else that touches the cache. It removes only rows a version up to the current one
closed, so a merge running beside it loses nothing, and it is repeatable: a pass cut short is finished by the next
refresh. A version a delivery flow pins is kept from the first refresh after the pin is recorded, by a repository sync
or by the control plane when it starts, which describes again from the catalog's copies every delivery flow described
before pins were recorded. While an active delivery flow that may read the partition has no recorded pin (after an
upgrade, until the control plane has started once), the retention prunes nothing and names the flows it waits on. The
control plane also makes inactive the interfaces of a repository the catalog holds no delivery flow of any more, so a
repository removed from the catalog leaves nothing to wait on. A retention that fails is logged and reported in the
run's result; the refresh itself stands, and the next one applies the retention again.

### Purging the history by hand

An admin can purge a partition cache's history now, rather than wait for its next refresh: **Prune history** on the
Cache page's History tab, `POST /cache/prune` ([the API](api.md)), or `sqlflow cache prune` ([the CLI](../cli/cache.md)).
It is the same pass, with the days the admin names in place of the partition's retention, whatever its cache flows
declare: the current version, the one it replaced and every pinned version keep their records whatever the days, and 0
keeps those alone. The page starts from the partition's retention and shows what the days chosen prune (the versions,
and how many stored rows) before anything goes; `POST /cache/prune/preview` and `--dry-run` say the same. The purge is
an admin's, since what it removes cannot be brought back; a preview is anyone's who may operate.

Every pruned version records when and by whom: the requester of the refresh, the account of the import, or the admin who
purged. History shows it on the version's `pruned` mark, `sqlflow cache list` beside it, and the API as `prunedBy`.

In the database, `osdu.CacheDefinition.RetentionDays` holds what each flow declares, `osdu.CacheVersion.PrunedUtc` and
`PrunedBy` when and by whom a version was pruned and `ChangesJson` what it changed, and `osdu.Interface.CacheVersion` the
version each delivery interface renders against (migrations `CacheRetention`, module version 1.40.0, and
`CachePruneActor`, module version 1.41.0).

## Several cache flows, one cache

Several cache flows may fill one partition, in one repository or several, so each project declares the reference data it
needs without copying another project's file. The cache holds the union of what they declare:

- **Types are shared by name.** Flows declaring a type under the same name share one type. A refresh of any of them
  fetches every path any synced flow of the partition declares for the type, so a field one project asks for is there
  for every pipeline reading the partition, and every flow's query adds records to it.
- **The newest capture of a record wins.** A captured record replaces what the cache held for it. A record the capturing
  flow no longer finds leaves the cache only when no other flow's last capture still holds it, and no delivery holds it
  while OSDU still does ([Pipelines bring the cache up to date](#pipelines-bring-the-cache-up-to-date)). Types a capture does not
  cover stay as they are, except a type no synced flow declares any more, which the merge removes.
- **A lookup table has one owner.** A table, dictionary or dimension type is declared by one cache flow of a partition:
  its capture replaces the whole type, so a second flow declaring it would replace the first one's rows.
- **Declarations must agree.** Two declarations of one type that disagree on the entity type, or cache one field name from
  different paths, are refused. The repository sync leaves the declaration synced second out of the catalog with a
  warning naming both flows, and a refresh of a flow that disagrees fails before it captures anything.
- **One platform per partition.** The sync warns when the cache flows of a partition search different endpoints.
- **Refreshes of one partition merge one after another.** A refresh takes the partition's merge lock, so a second waits for
  the first to commit and merges onto the version it wrote. After five minutes of waiting it gives up, keeps nothing of
  its capture, and says to run the refresh again. Refreshes of different partitions never wait on each other.
- **onChange is the strictest.** A type's changes wait for approval when any flow declaring it says `onChange: approve`.

When a flow stops declaring a type, the sync deletes that flow's hold on the type's records, so a later capture by
another flow lets go of the records only the first one kept.

## What a refresh does

1. Resolves the partition and reads every synced declaration of it; a conflict with another flow stops here.
2. Captures each declared type from its origin: OSDU types through the search service (every path the partition keeps
   for the type), table types from the ingestion database, dictionaries from the repository tree, dimension types from
   the dimension's last build. A type that cannot be read whole fails the refresh before anything is written. A run
   that names `types` captures those alone.
3. Asks OSDU's storage service about the records of each OSDU type that a delivery holds and the search did not find
   ([below](#pipelines-bring-the-cache-up-to-date)), and reads the partition's system properties, when it reached OSDU
   ([System properties](#system-properties)).
4. Merges the capture into the current version and writes the next version, unless nothing moved.
5. Applies the partition's [retention](#retention): prunes the records of the versions it no longer keeps.
6. Compares each type that moved with the version before, and tags the changes that delivered records were built from.

A run over several partitions does this for each in turn; a partition that fails leaves the others refreshed.

## Pipelines bring the cache up to date

A refresh brings the cache up to date on the cache flow's schedule. Within one schedule, a pipeline that lands the records
a later pipeline looks up (the units the wells refer to, the wellbores the logs belong to) would leave them out of the
cache until the next refresh, and the later pipeline would render without them: a reference written unverified, or a
record held for a reference the cache does not hold. So a pipeline says, in its `cache` block, which entities of its
partition's cache must be current before it runs, and which it brings up to date after it runs
([cache](../flow/delivery.md#cache)):

```yaml
cache:
  before: [UnitOfMeasure]
  after: [master-data--Wellbore]
```

An entry is a cache type by its name, or an OSDU entity type, which stands for every type of the cache holding records of
it, whichever repository's cache flow declares it; `auto` works the entries out from the pipeline. The run resolves them
against what the partition's synced cache flows declare before it reads anything, and stops there, with nothing sent, when
one names nothing the cache holds or a type the step cannot work on.

**The before step** runs first. For each OSDU type it names, every declaring cache flow's kind and query are searched
through the pipeline's own connection to its partition, with every path the partition keeps for the type, and the capture
is merged as that flow's own, exactly as a refresh of the flow narrowed to these types would merge it. A lookup table from
an ingestion table is read over its cache flow's connection reference, as that flow reads it, and replaces the type whole.
A dictionary or a dimension type is refused: only its cache flow reads it. A record a delivery
holds that the search did not find stays while OSDU's storage service still holds it. A type whose every declaration
searches with a query that takes the cache flow's parameters is refused: only that flow's run fills them. The step is the
run's to complete: when a type cannot be read whole, the run fails before anything is sent.

**The run then renders against the newest version of the cache**, whatever the step, an earlier pipeline or an earlier
interface of the same source put in: a source opens every interface before the first runs, and each takes the newest
version as it starts. A flow that pins `render.cacheVersion` keeps its version.

**The after step** runs as the run ends, after its last delivery, its fan-out members' too, and after the sweep of
unfinished deliveries; a drain of the whole flow, a removal and a reversal end with it as well.

1. **It takes what the run changed from the ledger:** the records the flow delivered since the run started, and the ones
   it deleted or reverted (a minute earlier, for the clocks of the nodes a fanned-out run delivers on), at most 50,000. A
   run changing more is a bulk load: the step reads nothing, and says to run the cache flows' refresh. When a type it
   names holds a kind the enrichments of the flow's mapping add, the records those records link (`enrich.records` in their
   target state) come with them ([records an enrichment adds](../flow/enrich-records.md#the-cache)).
2. **It reads them back from OSDU's storage service by id** (`POST /api/storage/v2/query/records`, 100 at a time), not the
   search service, whose index holds a record a moment after it is written.
3. **A record storage returns goes in as it is now,** into the types holding its kind, with the paths the partition keeps
   for each type, held under `delivery:<ledger name>`; **one storage no longer holds is taken out,** whichever cache flow
   or delivery held it.

The after step's failure is not the run's: what the run did in OSDU stands, its log warns that the cache was not brought
up to date, and the next refresh of the cache flows brings the records in. A removal that deletes its records from the
ledger as it goes hands the step the ids it took out of OSDU, and a deletion of the ledger takes the step before the
ledger is deleted, while it still names every record.

Each step that writes a version tags what its changes reach, as a refresh does ([below](#from-a-cache-change-to-osdu)),
and carries the approved changes out at once, up to 100,000 records, asking for them to be planned again before the
pipelines after it run; the control plane's rollout carries the rest at its pace. The run's result carries each step as
`cacheBefore` and `cacheAfter` (the partition, the types, the records taken in and read, those taken out, the version, the
records planned again, and a sentence saying it), and the run page shows the sentences under **Cache**:

```text
The cache of partition 'dev' (Wellbore) took the 5 record(s) the run landed, as version 20261020T101500Z; 12 record(s) built without what changed are planned again.
```

A record a delivery holds stays in the cache when a refresh's search does not find it, since the search index may not
have caught up with it yet. The refresh asks storage about those records alone: one storage still holds stays, and one it
no longer holds (deleted, or not readable with the cache flow's credentials) leaves the cache, unless another cache flow's
last capture still holds it.

Lineage orders the steps: a pipeline writes the types its steps name, so a flow reading one runs after it, and the kinds
its before step searches are read, so the flows delivering them run first ([lineage](lineage.md)). Flows in other
schedules are not waited for.

### A record held for a reference waits for it

A record is held when an id its mapping builds names a record of an entity type the cache holds records of, and not this
one. The held render records the id it looked for, in every type that would hold it. When a later version holds the
record, by a refresh, a before step or an after step, the held record is released to be planned again, and built against
it, whichever flow, partition or repository it belongs to. Each flow's records are released under an activity of their
own, `release` by `cache:<partition>@<version>`, so a record's history says why it went out again and when. What a held
render read is kept apart from what a delivered document was built from, so a record that was delivered before still
says what the document OSDU holds was built from. One version releases at most 100,000 held records; the rest wait for
the next version bringing in what they missed, or an operator's release.

## From a cache change to OSDU

Every delivered record points at the set of cached values its render read: the record a value was found by, each value
written into the document, each path read that held nothing, each key a lookup table did not list, and each id written as
an unverified reference. A refresh compares
the new version with the one it replaces over those sets (thousands of them, never the records themselves) and writes
one tag per change: the partition, the type, the cached record, the path, the value before and after, the versions it
moved between, and how many delivered records it reaches.

| Change | Meaning |
| --- | --- |
| `changed` | A value records read now reads differently. |
| `removed` | A cached record records used is no longer in the cache. |
| `unmatched` | A record no longer matches by the value records found it by. |
| `listed` | A lookup table now lists a key records looked up and found no row under, or an OSDU type now holds a record by a value records found none by. |
| `relisted` | The rows a key finds are no longer the ones records read every one of. |
| `found` | The cache now holds a record that records reference as an unverified id, or that a held record's reference named and the cache did not hold. |

What a tag does depends on the type's `onChange`:

- **`auto`** (the default): the tag is approved as it is written, and each affected record is delivered again on its
  flow's next run.
- **`approve`**: the affected records are held back; a plan skips them as `awaiting approval` ("a cache change is tagged
  against this record and is waiting for approval"), so OSDU keeps the documents it has, until someone decides on the
  Cache page. Approving lets the rollout carry the change out; rejecting leaves the delivered documents alone. If the
  value moves again after an approval and before the rollout carried it, the tag reopens, because the approval was for
  the value someone looked at.

A tag's status moves through `pending`, `approved` or `rejected`, `rolling` (its records are being marked), `delivering`
(all are marked and some flow has not rendered them again yet) and `applied`.

The control plane carries approved changes out in batches, so a corrected unit that reaches millions of records drains
at a set pace and resumes after a restart. The marking is metadata only: no payload is re-uploaded because a reference
value changed. A held or failed record is marked too, and its next plan renders it under the change; a `reverted` or
`deleted` record is not, since an operator's reversal, step back or removal decided what OSDU holds of it, and a tag does
not wait for one. A record held back while a change awaits approval keeps any request to plan it again (a release, a
redelivery) for the plan that runs once the change is decided.

| Setting (`Osdu:CacheRollout`) | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Turns the automatic rollout off; approved changes then wait. |
| `BatchSize` | `5000` | Records marked for redelivery per batch. |
| `BatchesPerPass` | `2` | Batches per pass, across all approved changes. |
| `PollSeconds` | `60` | Seconds between passes. |

## System properties

A refresh that reaches OSDU also reads the partition's system properties: the feature flags the indexer and the search
service report through `GET /api/indexer/v2/info` and `GET /api/search/v2/info` (their `featureFlagStates`). They are
kept with the version, apart from the cached records: they have no record id, and no mapping reads them. The engine
relies on one, `featureFlag.keywordLower.enabled`: where the indexer keeps a lowercased copy of every text property, a
search that finds no record exactly asks again regardless of case.

- A state reported for the partition wins over one reported for no partition; states for other partitions are ignored.
- A service that cannot be asked fails nothing: the refresh logs why and keeps what the cache knew of that service. A
  property the engine relies on that no service reports is recorded as unknown, and nothing relies on it being on.
- A changed state is a change of the cache, so the refresh writes a version even when every record is the same.
- A flow of lookup tables alone reaches no platform and reads none.

## Filling a cache without a platform

For work without OSDU, `sqlflow cache import <cache flow> --from-dir <dir>` merges type files into the cache of the
flow's partition as that flow's capture, and applies the partition's retention as a refresh does
([`sqlflow cache`](../cli/cache.md)). The files stand in for what a search of
the partition would return: every record is an OSDU record of the declared entity type in the flow's partition. A lookup
table is never imported: it is captured from its table, dictionary or dimension wherever the flow runs.

## Where to see it

- **The Cache page** (OSDU, Build, Cache) shows the cache of the partition picked in the title bar: the cache flow files
  that fill it, with **Cache files** and **Refresh now** (a **Refresh** menu naming the flows when several fill the
  partition, since a refresh captures what one flow declares); and four tabs. **Records** browses what a version holds, a type
  at a time, with how a mapping reads each row. **History** lists the versions and which types each changed, a pruned
  version marked as such, and offers an admin **Prune history**.
  **Deliveries** lists the changes with what they reach, the approvals waiting, and the records built without something
  the cache did not hold yet. **Setup** lists the cache flows, the types each declares, the retention each declares and
  the one the partition keeps, and the partition's OSDU feature flags.
- **A cache flow's pipeline page** opens on its **Cache versions** tab. A cache flow's run dialog lists its types, to
  refresh some of them.
- **A delivery run's page** shows what its cache steps did under **Cache**.
- **`sqlflow cache list <partition>`** prints the partition's retention, the versions, newest first, each pruned one
  marked, and the current version's system properties.
- **The API** (`/api/v1/delivery`): `GET /caches`, `/cache/versions`, `/cache/items`, `/cache/history`, `/cache/diff`,
  `/cache/tags`, `/cache/gaps`, `/cache/streams` (each type's upstream flows, from files to the cache flow, and who reads
  it), `POST /cache/tags/decide`, and `POST /cache/prune` with its preview ([the API](api.md)).

## Related

- [Cache flow](../flow/cache.md): the document that declares what a cache holds.
- [Change detection](change-detection.md): what else makes a record deliver again.
- [The ledger](ledger.md): where cache sets and tags are kept.
