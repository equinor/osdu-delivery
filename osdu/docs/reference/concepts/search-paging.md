---
id: delivery-concept-search-paging
title: "Search paging: reading every record a search matches, through the cursor or in createTime order"
type: concept
summary: "How flows read an OSDU search whole, through the search cursor or in createTime order, and when to read in createTime order instead."
keywords:
  - search paging
  - source.paging
  - paging createTime
  - createTime order
  - sort by createTime
  - sort by create date
  - batch by date
  - keyset pagination
  - deep pagination
  - query_with_cursor
  - search cursor
  - cursor expired
  - can't find the given cursor
  - too many cursor requests
  - scroll context
  - search_after
  - point in time
  - 10000 limit
  - could not be read whole
  - read whole
related:
  - delivery-flow-retrieval
  - delivery-flow-inventory
  - delivery-flow-dimension
  - delivery-flow-assertion
  - delivery-flow-cache
  - delivery-flow-download
  - delivery-guide-retrieving-records
  - delivery-concept-partition-cache
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.Ordered.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/RecordChanges.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DownloadDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
---

# Search paging

Several flows read every record an OSDU search matches: a [retrieval](../flow/retrieval.md) its kinds, an
[inventory](../flow/inventory.md) its search reads, a [dimension flow](../flow/dimension.md) the scans an aggregation
cannot answer, a [cache flow](../flow/cache.md) its OSDU types, an [assertion flow](../flow/assertion.md) the records
its tests read, and a [download flow](../flow/download.md) its kinds. They all go through one reader, which hands each
record out once and either hands out every record the search matches or fails the run. `source.paging` says how that reader pages:

```yaml
source:
  endpoint: ${env:OSDU_URL}
  paging: createTime   # cursor (the default) or createTime
```

The key is the same in all six flow kinds, and its values are matched without regard to case. Anything else is refused
when the flow is read: `'source.paging' value 'scroll' is not one of cursor, createTime.`

## The two ways

| | `cursor` (the default) | `createTime` |
| --- | --- | --- |
| Request | `POST /api/search/v2/query_with_cursor` (`source.searchPath`) | `POST /api/search/v2/query` (`source.queryPath`) |
| What the service holds between pages | A search context: a scroll, or a point in time, kept 90 seconds after each page | Nothing |
| Order | The service's, or the one an assertion test declares | Each record's `createTime`, then its id |
| A page whose answer is lost | The context has moved on, so the read starts again from the first page | The same page is asked again, from the last record handed out |
| Passes before the run fails | 2, each from the first page | 2, the second picking up where the first stopped |
| Requests beside the pages | A count when the first page names no exact total; the release of the context | The newest record, before the first page; a count after the last (none for a read of one page); a count per batch only when the read comes back short |

## Why a cursor read fails part way

The search service keeps a cursor's position on its side. In scroll mode the cursor names an Elasticsearch scroll; in
search-after mode (the service's `query-with-search-after` feature flag, or `?search_after=true` on the request) it
names a cached point in time and the sort values of the last page. Either way the service moves the context on as it
answers, so a page asked again after its answer was lost is answered with the page after it, and the lost page cannot be
read through that cursor again. The context is dropped 90 seconds after the last page was asked for, a read that holds
a page longer than that (a retrieval reading every hit back from storage, a slow lake) finds it gone, and in scroll mode
the service refuses a new cursor while the partition holds as many scroll contexts as it allows (`429 Too many cursor
requests`).

The reader therefore asks for a page after the first only once with a cursor, repeats it only after a status saying the
service refused it unread (401 with a renewed token, 408, 425, 429), and otherwise reads the search again from its first
page with a new cursor, handing out only the records the first read did not. A long read over a busy service can fail
twice and fail the run: `The search of kind <kind> could not be read whole in 2 reads (read 1: ...; read 2: ...)`.

A retrieval may name search-after mode in its path (`searchPath: /api/search/v2/query_with_cursor?search_after=true`);
the reader releases the context with the same query, so the service closes the point in time rather than look for a
scroll. Inventory, dimension, assertion and download flows refuse a query in a path, and a cache flow's paths are fixed.

## Reading in createTime order

A read in createTime order keeps its position itself, so nothing on the service can expire or be lost:

1. It asks for the newest record the search matches (sorted on `createTime` and `id`, descending, one hit) and reads no
   further than its `createTime`. A record created while the read runs is left to the next one, and the read has a fixed
   end.
2. Every page is a plain search at offset 0, sorted on `createTime` and then `id`, ascending, narrowed to the records after
   the last one handed out. For a read of `osdu:wks:master-data--Wellbore:1.*` with the query
   `data.FacilityName:*`, the page after a record created at `2024-03-01T08:02:22.000Z` asks:

   ```text
   (data.FacilityName:*) AND ((createTime:{2024-03-01T08:02:22.000Z TO 2024-03-01T08:07:56.000Z] OR (createTime:[2024-03-01T08:02:22.000Z TO 2024-03-01T08:02:22.000Z] AND id:{"dev:master-data--Wellbore:0137821" TO *])))
   ```

3. A page is a request like any read: the flow's retry policy repeats it on a timeout, a dropped connection or a 5xx,
   whatever became of the answer. Because every page starts at offset 0, the search's window of 10,000 results never
   applies, however many records the search matches.

The id is part of the order because records share a `createTime` all the time: storage stamps every record one
`PUT /records` request creates with the same instant, up to 500 of them. A read that paged on `createTime` alone and
started each page after the last instant would skip the rest of a run of records that a page boundary fell in; one that
started at the last instant would hand some out twice. `createTime` is the field to order on because it never changes:
storage keeps a record's `createTime` through every later version, so a record updated during the read stays where it
was. A read ordered on `modifyTime` would meet a record changed during the read twice, or not at all.

### How a read checks that it is whole

The reader checks every page before it hands any of it out. A hit before the one ahead of it, a hit outside the range
it asked for, or a full page that brings no record past the last one fails the run, since a service that does not keep to
the order cannot say what it skipped:
`The search of kind <kind> came back out of createTime order: <what>. ... source.paging: cursor reads the search through the search cursor instead.`

A read that reaches its end is checked against what the search matches up to its end. While it reads, it keeps a tally
for each batch of about 10,000 records, a batch being a range of `createTime` that closes where the time moves on, so the
records sharing an instant never straddle two batches. A read that returned fewer records than the search matches (a
record indexed late, behind where the read had got to) counts each batch, and its second pass reads again only the
batches short of their count, handing out only the records not yet handed out. A page that fails after the retry policy,
or comes back without being a page, ends the pass, and the second pass picks up from the last record handed out. When
the second pass fails or comes back short too, the run fails:
`The search of kind <kind> could not be read whole in createTime order in 2 passes (pass 1: ...; pass 2: ...).`

A record a search matches that holds no `createTime` cannot be reached in this order, so the run fails rather than leave
it out: `<n> record(s) the search of kind <kind> matches hold no createTime, ...`. Storage gives every record a
`createTime` when it creates it, so this is an index fault to raise with the platform's operators.

### What it asks of the service

The index maps `createTime` as a date and `id` as a keyword, so the service sorts on both and takes a range on both.
A deployment that refuses the query fails the run with the service's own words:
`The search service refused the search of kind <kind> in createTime order: <words>. ... source.paging: cursor reads the search through the search cursor instead.`

A search whose fields are narrowed (a retrieval's `returnedFields`, a cache type's `fields`) is asked for the `id` and
`createTime` as well, since the order is kept by them; a record handed on carries the `createTime` only when the flow
asked for it.

## Choosing

Keep `cursor` when its reads succeed. Choose `createTime` when:

- reads fail part way with `could not be read whole in 2 reads`, or a log says a read was made again from its first page;
- searches are large (hundreds of thousands of records and more), where restarting from the first page is costly and a
  failure is more likely somewhere along the read;
- a flow takes long between pages, such as a retrieval with `fetchRecords` over a slow lake, and a cursor would expire;
- many reads run at once (a retrieval's kinds under `parallelism.recordsAtOnce`, several flows on one partition) and the
  service refuses new cursors.

An assertion test that declares its own `sort` is read through the cursor whatever `source.paging` says, since only the
search service can page an order the test chose. The explorer's searches and a workflow route's search for the records
its runs wrote are small and always use the cursor.
