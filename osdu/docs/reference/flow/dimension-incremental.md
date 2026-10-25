---
id: delivery-flow-dimension-incremental
title: "Dimension incremental loads: full and incremental builds, their windows, and records that leave"
type: flow-reference
summary: "A dimension flow's incremental block: when a build loads in full, how a window reads what changed, and records that leave the index."
keywords:
  - incremental load
  - full load
  - keycolumns
  - datecolumns
  - modifytime
  - fullloadafterhours
  - fullLoadWhenRecordsLeave
  - backfill window
yamlPath: "incremental"
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-build
  - delivery-concept-search-paging
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.Incremental.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/RecordChanges.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261010190424_DimensionIncrementalLoads.cs
---

# Dimension incremental loads: full and incremental builds, their windows, and records that leave

A [dimension flow](dimension.md) with an `incremental` block reads only what changed since a dimension was last read, and loads in full only when it has to.

Without an `incremental` block every build loads every dimension in full (see [What a build does](dimension-build.md#what-a-build-does)).
With one, a build reads only what changed since the dimension was last read, and loads in full only when it has to:

```yaml
incremental:
  keyColumns: [id]                        # required: the properties that identify one record
  dateColumns: [modifyTime, createTime]   # when a record last changed: the first it holds
  lagMinutes: 5
  fullLoadAfterHours: 168                 # a full load at least once a week
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `keyColumns` | list | required | The properties whose values together identify one record: `[id]` for an OSDU record. A full load keeps the keys each record holds by them, so an incremental load reads again what a changed record held before. Each a property a record holds once, 1 to 3, none twice. |
| `dateColumns` | list | `[modifyTime, createTime]` | When a record last changed: the first of these dates it holds. OSDU writes `createTime` alone when it creates a record and `modifyTime` only from its second version on (or when its ACL, legal tags or tags are patched), so a record never modified is read by its `createTime`. Each a date a record holds once, 1 to 3, none twice. |
| `lagMinutes` | integer | 5 | How far behind now a window ends, so records the indexer has not caught up with are read by the next build; a window also begins this far before the last one ended. 0 to 1440. |
| `fullLoadAfterHours` | integer | none | A build loads a dimension in full when its last full load completed more than this many hours ago. 1 to 8784. Left out, a dimension is loaded in full only when a run asks or a build has to. |
| `fullLoad` | boolean | `false` | Every build loads in full, as without the block, keeping its settings. |
| `fullLoadWhenRecordsLeave` | boolean | `false` | A build loads a dimension in full when at least one record its last full load read is gone from the index (a soft delete) or no longer matches its query, which no incremental load can see; left false, such a record's keys and counts stay until the next full load. Two counts a build. |

A build of such a flow loads each dimension **in full** when the dimension has not been loaded in the partition; when its
last full load kept no record of the keys each record holds, or kept them by other `keyColumns`; when its declaration or
its query changed since its last full load; when its last full load is older than `fullLoadAfterHours`; when the run asks
for one (SQLFlow's `fullLoad`, `--full`) or the block says `fullLoad: true`; when the block says
`fullLoadWhenRecordsLeave: true` and a record the last full load read has left the index; and when an incremental load finds more than
a quarter of the dimension's records or keys changed, or more than 100,000 records of one type its labels are read
through, so reading every key costs less. A full load of such a flow also reads every record once more, its unique key
and the keys it holds, and keeps them (`osdu.DimensionRecord`); a flow without the block keeps none.

Otherwise it loads **incrementally**, over the window from the lag before where the dimension's completed builds have
read up to, to the build's start less the lag:

1. **The records that changed** in the window, by when each last changed (`(modifyTime:[from TO to} OR (createTime:[from
   TO to} AND NOT _exists_:modifyTime))` with the default `dateColumns`), each read with its unique key and the keys it
   holds now; and the keys those records held when a load last read them, by their unique keys.
2. **The records a label or attribute was read through** that changed in the window, by OSDU's own `modifyTime` and
   `createTime`, in the entity types the dimension's keys were read through (`osdu.DimensionKeyRecord`); and the keys
   read through them.
3. **Every key of either, read again whole** over the dimension's query: its count (a query's worth of keys at a time,
   each group's records found by its filter), its label and attributes, its collected values and its elements. A key
   read again that no record holds is removed, as a full load removes one. The values those keys were and are under are
   laid out again from all their keys, and one left with none is removed.
4. **The write** holds those keys and values only: every other key stays as the last load left it, with its count, label
   and attributes. A build whose window held no change completes with nothing written.

A record that left the index (a soft delete) cannot be seen by any search, so an incremental load cannot see it: the keys
only it held, and the counts it made, stay until the next full load. An incremental load says how many at least have
left: `At least <n> record(s) the last full load read (<time>) are gone from the index or no longer match the query; ...`.
A run can name the window an incremental load reads with SQLFlow's backfill window (`--from`, `--to`): it ends no later
than the build's start less the lag, and the next build still reads on from the latest any build read up to, so a window
named for the past moves nothing back. A flow without an `incremental` block refuses a window.

Every build records how it loaded (`full` or `incremental`), its window, the records it found changed and the keys it read
again, and a plan says how a build started now would load each dimension and how many records changed in its window.
