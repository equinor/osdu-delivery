---
id: delivery-concept-api-flow-kinds
title: "The delivery API for the other flow kinds: assertions, dimensions, inventories, removal flows, downloads, retention"
type: concept
summary: "The /api/v1/delivery routes of the flow kinds beside delivery: assertions, dimensions, inventories, removal flows, downloads, and the retention areas."
keywords:
  - api/v1/delivery
  - assertion routes
  - dimension routes
  - inventory routes
  - removal flow routes
  - removals api
  - lift-removal
  - download routes
  - retention areas
  - maintenance storage
related:
  - delivery-concept-api
  - delivery-flow-assertion
  - delivery-flow-dimension
  - delivery-flow-inventory
  - delivery-flow-removal
  - delivery-flow-download
  - delivery-concept-storage-and-retention
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Duplicates.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAssertionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Dimension.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDownloadEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionViewEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryRemovalEndpoints.cs
---

# The delivery API for the other flow kinds

The routes under `/api/v1/delivery` of the flow kinds beside delivery, and the module's retention areas. They behave as
every delivery route does: the scopes, partition and interface parameters and answers that
[the delivery API](api.md#how-the-routes-behave) describes.

## Assertions, dimensions and inventories

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /assertions` | read | `partition`, `set` | The board of every active assertion flow, or of the flows of one set: each test with its title, its dimensions and its latest result. |
| `GET /flows/{pipelineId}/assertions` | read | `partition` | One assertion flow's board; 409 for another kind. |
| `GET /flows/{pipelineId}/assertion-runs` | read | `partition`, `max` (50, at most 500) | Its runs, newest first. |
| `GET /flows/{pipelineId}/assertion-matrix` | read | `partition`, `runs` (30, at most 200) | Recent runs against every test. |
| `GET /flows/{pipelineId}/assertions/{test}/history` | read | `partition`, `runs` (30, at most 200) | One test's results over its recent runs. |
| `GET /assertion-runs/{assertionRunId}` | read | | One run with every test's result. |
| `GET /assertion-runs/{assertionRunId}/report` | read | `format` (`json`, `md`, `html`, `junit`) | The run's report as a file; 400 for another format. |
| `POST /assertion-runs/{assertionRunId}/source-sql` | operate | body `test`, `assertion` (the check's position in the test, from 0), `rows` (`listed`, the default, or `all`), `maxRows` (for `all`; 0, the default, for every row) | The staged rows a check of the run read, as T-SQL over its delivery flow's tables, answered as `POST /flows/{pipelineId}/source-sql` answers. 404 for a run, test or check that does not exist, or a delivery flow the repository no longer declares; 409 for a check that read no staged rows, one that lists none by its key when `listed` is asked, or a delivery flow that no longer takes the values the run read it under ([the rows in the source](../flow/staged-accounting.md#the-rows-in-the-source)). |
| `GET /assertion-results` | read | `ids` (1 to 100, comma-separated) | Tests' results whole, by the result ids a board gives; an id that names no result is left out, 400 for one that is not a number. |
| `GET /assertion-sets/{set}/report` | read | `partition`, `format` (`json`, `md`, `junit`) | A set's report as a file; 404 for a set no flow names, 400 for another format ([assertion sets](../flow/assertion-sets.md#the-routes)). |
| `GET /dimensions` | read | `partition` | Every active dimension flow's dimensions. |
| `GET /flows/{pipelineId}/dimensions` | read | `partition` | One dimension flow's dimensions; 409 for another kind. |
| `GET /flows/{pipelineId}/dimensions/{name}/blueprint` | read | `partition` | How a dimension is built: its YAML beside what each line reads and makes. |
| `GET /dimensions/{dimensionId}` | read | | One dimension, its pipeline and parameters. |
| `GET /dimensions/{dimensionId}/table` | read | `search`, `attr`, `order`, `dir`, `offset`, `limit` (100, at most 1,000) | A page of the dimension's own table. |
| `GET /dimensions/{dimensionId}/values` | read | `search`, `order`, `removed`, `attr`, `after`, `limit` | A page of its values. |
| `GET /dimensions/{dimensionId}/values/{valueId}` | read | | One value with its keys, filter and history. |
| `GET /dimensions/{dimensionId}/keys` | read | `search`, `value`, `leftOut`, `removed`, `attr`, `order`, `after`, `limit` | A page of its keys. |
| `GET /dimensions/{dimensionId}/attributes/{name}` | read | `search`, `limit`, `attr`, `value` | The values an attribute holds among its keys. |
| `GET /dimensions/{dimensionId}/builds` | read | `max` (30, at most 500) | Its builds, newest first. |
| `GET /dimensions/{dimensionId}/changes` | read | `build`, `key`, `value`, `change`, `before`, `limit` | A page of its change log. |
| `GET /dimensions/{dimensionId}/export` | read | `set` (`values`, `keys`, `table`), `format` (`csv`, `jsonl`) | The dimension as a file, written a page at a time. |
| `GET /runs/{runId}/dimension-builds` | read | | The builds a platform run made. |
| `POST /dimensions/{dimensionId}/filter` | read | body `valueIds`, `values` | The search filter of the values named. Reads only. |
| `POST /dimensions/search` | read | body `picks`, `kind`, `within` | The OSDU search finding records that hold the picked values. Nothing is sent to OSDU. |
| `GET /dimensions/flow-patterns` | read | | The catalog's dimension flows, each with its document but its dimensions and views, and the connections the estate's flows keep their data through ([Writing a dimension flow file](../guides/dimension-flow-file.md)). |
| `POST /dimensions/flow-check` | read | body `yaml`, `path` | A whole dimension flow document checked as a sync and a build take it. Reads only. |
| `GET /cache/flow-patterns` | read | | The catalog's cache flows, each with its document but its types, and the connections the estate's flows keep their data through ([Writing a cache flow file](../guides/cache-flow-file.md)). |
| `POST /cache/flow-check` | read | body `yaml`, `path` | A whole cache flow document checked as a sync and a refresh take it. Reads only. |
| `DELETE /dimensions/{dimensionId}` | admin | | Removes a dimension its flow no longer declares, and everything kept of it. 409 for one the flow declares. |
| `GET /dimensions/views` | read | | Every view: those the active dimension flows declare, and those builds wrote that no flow declares any more. |
| `GET /flows/{pipelineId}/dimensions/views` | read | | One dimension flow's views; 409 for another kind. |
| `GET /flows/{pipelineId}/dimensions/views/suggest` | read | `from` (a dimension of the flow) | The joins a view whose rows are that dimension's could make, as the templates on the flow's path say, with the YAML to keep or change ([what the templates say of a view's joins](../flow/dimension-views.md#what-the-templates-say-of-a-views-joins)); nothing is written. 400 without `from`, 404 for a dimension the flow does not declare, 409 for another kind. |
| `GET /dimensions/views/{name}` | read | | One view: what its flow declares and what a build recorded, its newest checks, its SQL as written and as declared, and its YAML. |
| `DELETE /dimensions/views/{name}` | admin | | Removes a view no flow declares any more: the view and its record. 409 for one its flow declares. |
| `GET /inventories` | read | `partition` | The inventories of the partition, or of every partition. |
| `GET /inventories/lookup` | read | `id`, `partition` | What every inventory of the partition holds of one OSDU id; 400 when no partition is named or sent. |
| `GET /flows/{pipelineId}/inventories` | read | `partition` | An inventory flow's inventories; 409 for another kind. |
| `GET /inventories/{partition}/{inventoryId}` | read | | One inventory with its counts by finding, owners and last runs. |
| `GET /inventories/{partition}/{inventoryId}/records` | read | `finding`, `selection`, `after`, `limit` (100, at most 1,000) | A page of its ids; with `selection`, only those the selection holds, each with the version and values its match read. |
| `GET /inventories/{partition}/{inventoryId}/runs` | read | `limit` (30, at most 200) | Its builds and reconciles. |
| `GET /inventories/{partition}/{inventoryId}/export` | read | `finding`, `selection` | Its ids as CSV; with `selection`, only those the selection holds, with a column for each property its conditions ask. |
| `GET /inventories/{partition}/{inventoryId}/selections/{selectionId}` | read | | One selection: where its match stands, whether it is stale, what it read and found, and its ids counted by finding; for one a duplicate check made, the check and its key. 404 for one of another inventory. |
| `GET /inventories/{partition}/{inventoryId}/duplicates` | read | `limit` (20, at most 100) | Its duplicate checks, newest first, each as it stands. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}` | read | | One check: its key, the properties it downloaded, its table and who set its key, where it stands (downloading or picking, while it runs), whether it is stale, what it found, its groups by outcome and by size, the groups waiting to be picked with their copies, and whether the selection last made of its picks still stands. 404 for one of another inventory. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups` | read | `outcome` (`review`, `picked`, `chosen`, `settled`, `pending`), `after`, `limit` (50, at most 500) | A page of its groups, the largest first, each with its copies (every copy beside the inventory's row of it) when it holds at most 100. 400 for another outcome. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/copies` | read | `after`, `limit` (100, at most 1,000) | A page of one group's copies: how a group too large to list with its page is read. 404 for a group the check does not hold. |
| `GET /inventories/{partition}/{inventoryId}/duplicates/{checkId}/export` | read | | Every copy of every group as CSV, a column for each property of the key. |
| `POST /inventories/{partition}/{inventoryId}/duplicates` | operate | body `key` (each `path`), `refresh`, `pool` | Finds the ids that hold equal values at the key: 200 with the inventory's current check grouped by the key in SQL when it downloaded every property of it, else 202 with a new check downloading them, queued as a `duplicates` run of the flow; `refresh` always downloads. 400 for a key that is not one; 409 for an inventory never built or no longer declared, or a current check downloading or picking with another key. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/pick` | operate | body `pool` | Asks for a pick: a `duplicates` run reading the copies of the groups waiting to be picked from storage, and the copy each keeps picked. 202 with the check. 409 for a check not completed, picking, stale, holding no group to pick, or a flow no longer declared. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/groups/{groupNumber}/keep` | operate | body `inventoryRecordId` | Keeps that copy of the group, recorded as the caller's choice, the group judged again with it kept; null takes a choice back. 200 with the group. 400 for a copy not of the group or not held by storage; 409 for a check not completed or picking, stale, a group waiting to be picked or too broad to read, or a flow no longer declared. |
| `POST /inventories/{partition}/{inventoryId}/duplicates/{checkId}/selection` | operate | | Makes the copies the check picked to remove a selection a removal takes: 200 with the selection already made when no choice was written since, else 201. 409 for a check not completed, stale, or one that picked nothing. |
| `GET /inventories/{partition}/{inventoryId}/removals` | read | `limit` | Its removals and restores, newest first (a restore's `scope` is `restore`, its count `restored`). |
| `GET /inventories/{partition}/removals/{removalId}` | read | | One removal or restore. |
| `GET /inventories/{partition}/removals/{removalId}/items` | read | `outcome`, `after`, `limit` | What the removal (or restore) did to each id. |
| `POST /inventories/{partition}/{inventoryId}/removals` | operate | body `finding`, `scope`, `expected`, `ids` or `selection`, `confirm`, `pool` | 202 with the run that removes the ids of one finding from OSDU (every one, those named, or those a selection holds), or (`gone`, scope `inventory`) from the inventory. |
| `POST /inventories/{partition}/{inventoryId}/restores` | operate | body `expected`, `ids` or `removal`, `confirm`, `pool` | 202 with the `restore` run that brings back in OSDU the soft-deleted records behind the inventory's gone ids (every one, or those named) or behind what one soft-delete removal of it removed ([Restoring gone ids](../flow/inventory-removals.md#restoring-gone-ids)). 400 for a request that does not say what it restores or names both ids and a removal; 404 for a removal the inventory does not hold; 409 for a flow that declares no `removal`, an inventory never reconciled, another count than shown, a removal that was no soft delete or still runs, or a flow already busy. |
| `POST /inventories/{partition}/{inventoryId}/selections` | operate | body `filters`, `refresh`, `pool` | Selects the ids that hold what the conditions ask in OSDU: 200 with the match already made of the same conditions against the inventory's last build, else 202 with a new one queued as a `match` run of the flow. 400 for conditions that cannot be asked; 409 for an inventory never built or no longer declared. |
| `POST /inventories/{partition}/{inventoryId}/repairs` | operate | body `expected`, `ids`, `newIds`, `confirm`, `pool` | Delivers again the records behind missing ids through their flows, under their own ids or new ones: 202 with each flow's marked records and queued run, and the ids left with why (200 when it queued none). |

See [assertion flows](../flow/assertion.md), [dimension flows](../flow/dimension.md) and
[inventory flows](../flow/inventory.md).

## Removal flows

A removal flow keeps its runs, and the deletions it finds, under the ledgers of the delivery flows it names
([removal flow](../flow/removal.md)), so its runs are read by its partition and the deletions by their ledger. The partition
is the one the query names, else the workbench's (`X-Osdu-Partition`).

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /removals` | read | `partition` | Every active removal flow that removes in the partition (at most 200), each as the next route answers it; one whose catalog copy does not load is listed with its problem. |
| `GET /removals/flows/{pipelineId}` | read | `partition` | One removal flow: its guard and parameters, and each removal with the delivery flow (and pipeline) it writes to, what it reads, that ledger's identity in the partition, its deletions by state, the count waiting for this removal's approval and its last run. A partition the flow does not remove in, or a copy that does not load, is answered with the flow as declared and `problem`; 404 for another kind. |
| `GET /removals/flows/{pipelineId}/runs` | read | `partition`, `entry`, `limit` (50, at most 1,000) | Its runs in the partition, newest first, of one removal with `entry`; 404 for a removal it does not declare. |
| `GET /removals/{partition}/runs/{removalRunId}` | read | | One run with every count it kept. |
| `GET /removals/ledgers/{ledgerFlowId}/keys` | read | `state` (`pending`, `awaiting-approval`, `removed`), `run` (the removal run that found them), `after` (a delivery key), `limit` (200, at most 1,000) | A page of a delivery ledger's deletions in key order, with `next` (the `after` of the next page, when there is one) and `counts` by state. 400 for another state. |
| `GET /removals/ledgers/{ledgerFlowId}/runs` | read | `limit` (50, at most 1,000) | The removal runs that wrote to a delivery ledger, newest first, whichever removal flow ran them. |
| `POST /removals/flows/{pipelineId}/approve` | operate | body `entry`, `expected` (the count the operator was shown waiting for approval), `confirm` (the partition, typed back), `pool` | 202 with the `remove` run that approves what the removal's guard held and takes it out ([The guard](../flow/removal.md#the-guard)); the run checks the count again on the node. 400 without a removal, a count or a partition; 404 for a removal the flow does not declare; 409 when the delivery flow keeps no ledger in the partition, another number waits than the caller was shown, or a run of the flow is queued or running. |

The deletions of a record are also on its detail (`GET /records/{flowId}/{key}`, `removedKey`), and delivering records
again is an intervention (`POST /flows/{pipelineId}/lift-removal`, [Interventions](#interventions)).

## Downloads

A download flow keeps its downloads under its ledger in each partition it reads ([download flow](../flow/download.md)). The
flow's route reads the partition the query names, else the workbench's (`X-Osdu-Partition`), else the one a run of the flow
would read. Every route answers from the module's database: nothing here reads OSDU or a file of a download's folder.

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /flows/{pipelineId}/downloads` | read | `partition` | One download flow in the partition: how it lists OSDU, its `location`, its parameters, and each download it declares (then each it keeps and no longer declares) with what the partition keeps of it: its folder, its records by state (`written`, `due`, `problem`, `gone`) and their bytes, and its last run. A partition the flow does not read, or a copy that does not load, is answered with the flow as declared and `problem`; 409 for another kind, 404 for no pipeline. |
| `GET /downloads/{partition}/{downloadId}/runs` | read | `limit` (30, at most 200) | A download's runs, newest first. 404 for a download the partition does not keep. |
| `GET /downloads/{partition}/{downloadId}/records` | read | `state` (`written`, `due`, `problem`, `gone`), `q` (text an id or a file name holds, ignoring case, at most 500 characters), `after`, `limit` (100, at most 1,000) | A page of a download's records in the order they were first listed, with `next` (the `after` of the next page, when there is one): each with its file name, where it stands, the version listed and the version its file holds, its size and SHA-256, and why its last write did not happen. 400 for another state. |

## Retention

| Route | Policy | Parameters | Answers |
| --- | --- | --- | --- |
| `GET /api/v1/maintenance/storage` and the routes under `/api/v1/maintenance/areas/{areaId}` | read, operate, admin | SQLFlow's [Maintenance](../../../../sqlflow/docs/reference/concepts/maintenance.md) routes | The `osdu` schema's storage areas beside SQLFlow's; the three that take a limit are `osdu.attempts`, `osdu.activity-logs` and `osdu.assertion-runs` ([What the osdu schema stores](storage-and-retention.md)). The module's own retention pass, `POST /ledger/prune`, is gone. |

## See also

- [The delivery API](api.md): every other route under `/api/v1/delivery`.
