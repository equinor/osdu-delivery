---
id: delivery-flow-inventory
title: "Inventory flow (flowType: inventory): every id OSDU serves, compared with the ledgers to find orphans and missing records"
type: flow-reference
summary: "The flowType: inventory document: list every id a kind holds in a partition, compare it with every ledger, and report orphan, missing and stale records."
keywords:
  - source.paging
  - createTime paging
  - inventory flow
  - orphan records
  - missing records
  - reconcile
  - owners
  - maxmissingchecks
  - "source.read"
  - kind wildcard
yamlPath: "(root, flowType: inventory)"
related:
  - delivery-flow-inventory-builds
  - delivery-flow-inventory-selections
  - delivery-flow-inventory-removals
  - delivery-flow-inventory-duplicates
  - delivery-concept-search-paging
  - delivery-guide-finding-orphans
  - delivery-cli-inventory
  - delivery-concept-ledger
  - delivery-concept-removal-and-reversal
  - delivery-concept-partitions
  - delivery-flow-delivery
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/InventoryFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/InventoryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReaders.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReport.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Inventories.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261008024435_InventoryFlows.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009094531_InventoryCheckedUtc.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryInventoryVerbs.cs
  - osdu/docs/census/keys.inventory.json
  - osdu/specs/core/storage/openapi.yaml
---

# Inventory flow (flowType: inventory): every id OSDU serves, compared with the ledgers to find orphans and missing records

An inventory flow lists every record id one or more OSDU kinds hold in a partition, with its version and who created and
last changed it, keeps the list in the module's database, and compares every id with every ledger of the partition.
The ledgers say what the delivery flows delivered and minted; the inventory says what OSDU serves; where the two
disagree is the report. Use it to find records no ledger knows (orphans), records a ledger expects that OSDU no longer
holds (missing), ids an unfinished delivery left behind, and records a ledger removed or forgot that OSDU still serves.
The ids OSDU stopped serving stay in the inventory as `gone`, with when they went, until an operator removes them from it.
An operator can also select its ids by what OSDU holds of them, a property of the records or a column of a source system,
and remove what the selection holds ([Selecting ids by what OSDU holds](inventory-selections.md)); and find the
records OSDU holds more than once, by a key of the properties their copies hold alike, keep the most complete copy of each,
and remove the others ([Finding duplicate records](inventory-duplicates.md)).

A ledger can fall out of step with OSDU for reasons no delivery controls: a service that acted after its answer was
lost, an ingestion workflow that wrote more than it reported, a record written outside the flows, a ledger deleted or a
record purged from it, a restore of OSDU or of the database. None of this is visible from the ledger alone. A build, a
reconcile and a plan only read OSDU and never write a ledger. A flow that declares `removal` also lets an operator
remove the orphan, stale or forgotten ids it found. The [orphans guide](../guides/finding-orphans.md) walks through it;
[`sqlflow inventory`](../cli/inventory.md) reads the results.

## Example

The estate's inventory flow, `flows/welldb-06-inventory.yaml`:

```yaml
flowType: inventory
name: welldb-06-inventory
description: Every wellbore and well log OSDU serves in the partition, set against the ledgers of the welldb flows.
batch: welldb
partitions: [dev, test]

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body: { scope: "${env:OSDU_SCOPE}" }
  read: search

owners: [welldb-delivery@example.com]
maxMissingChecks: 100000
removal: { findings: [orphan, stale], purge: false }
parallelism: { recordsAtOnce: 4 }

inventories:
  - name: Wellbores
    description: Every wellbore record, whatever its authority, source and schema version.
    kind: "*:*:master-data--Wellbore:*"
  - name: WellLogs
    kind: "*:*:work-product-component--WellLog:*"
    versions: all
  - name: LogFiles
    kind: "osdu:wks:dataset--File.Generic:*"
    query: 'tags.DeliveredBy:"welldb"'
```

`Wellbores` and `WellLogs` cover their entity types whole, so a record a ledger delivered that OSDU no longer serves is
reported missing. `WellLogs` keeps every version of each record. `LogFiles` is narrowed by a query, so it reports only
the ids it listed before and no longer lists. An operator may soft delete the orphan and stale ids it finds.

## Top-level keys

Unknown keys are refused when the document is read.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `flowType` | text | required | `inventory`. |
| `name` | text | required | The flow's name: its pipeline identity, and the name its inventories are kept under (`name@partition` for a flow that works in partitions). |
| `description` | text | none | What the inventories are for. |
| `batch` | text | none | The grouping label the pipeline is filed under. |
| `parameters` | map | none | Parameters an inventory's `query` uses as `{name}` tokens. `partition` cannot be declared: `{partition}` is always the partition the run reads. |
| `partitions` | list | none | The partitions the flow keeps inventories of, each a literal `data-partition-id`. Partitions are settled as a [dimension flow's](dimension.md#partitions) are: listed, hard-coded by `source.headers.data-partition-id`, or every registered partition. A run reads one partition; `*` is refused. |
| `source` | map | required | The OSDU platform, and how it is read. See [source](#source). |
| `owners` | list | inferred | The identities this estate's records are written as (OSDU's `createUser`: a user's e-mail, an application's client id), at most 50, each at most 256 characters with no whitespace, unique ignoring case. See [Owners](#owners). |
| `maxMissingChecks` | integer | 100,000 | How many ids a ledger expects that one build reads from storage, to tell `missing` from `unlisted`: 0 (none) to 1,000,000. Never-asked ids first, then the longest ago asked, so successive builds take turns through all of them. |
| `inventories` | list | required | The inventories, at least 1 and at most 100, names unique ignoring case. |
| `removal` | map | none | What an operator may remove from OSDU of what the inventories found. Left out, the flow only reads OSDU. Gone ids are removed from the inventory whatever it declares. See [Removal](#removal). |
| `reliability` | map | delivery defaults | The HTTP settings a [delivery flow](delivery.md) takes: timeout, retry, rate limit, TLS, URL allowlist and response size. |
| `parallelism` | map | defaults | `recordsAtOnce` (default 8, at least 1): how many version lists a build reads at once for `versions: all`. Inventories are read one after another. It is written as on a [delivery flow](delivery.md#parallelism), so a flow's settings read alike whatever its kind. An inventory flow takes no other parallelism key. It was `parallelism.concurrency`, and `reliability.concurrency` before that: a document that still writes either is refused, naming `parallelism.recordsAtOnce`, and a delivery flow's setting written under `reliability` (`fanOut`, `batchRecords`) is refused as one to remove. |
| `schedule`, `mode`, `lifecycle` | | none | SQLFlow's envelope keys ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). A schedule's `values` may name the partition (`partition: dev`) and the flow's parameters. |

### source

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | text | required | The platform's base URL, usually `${env:OSDU_URL}`. |
| `auth` | map | `type: none` | How requests authenticate, written as a delivery flow's `target.auth` is; secrets are references only, and a literal is refused when the flow is read. |
| `headers` | map | none | Headers sent with every request; `data-partition-id` only for a flow that names no `partitions`. A header that carries a credential holds a reference; a literal is refused when the flow is read. |
| `read` | `search` or `storage` | `search` | How the inventories read OSDU. See [Reading OSDU](#reading-osdu). |
| `queryPath` | text | `/api/search/v2/query` | The offset search a plan counts through. |
| `searchPath` | text | `/api/search/v2/query_with_cursor` | The cursor search a search read pages through. |
| `paging` | `cursor` or `createTime` | `cursor` | How a search read pages: through `searchPath`, or through `queryPath` in the order each record was created, which holds nothing on the service between pages. See [Search paging](../concepts/search-paging.md). |
| `recordQueryPath` | text | `/api/storage/v2/query/records` | Storage's listing of a kind's ids (GET) and its read of records by id (POST). |
| `headersPath` | text | `/api/storage/v2/query/records/headers` | Storage's read of up to 1,000 records' system properties by id. |
| `versionsPath` | text | `/api/storage/v2/records/versions` | Storage's list of a record's versions, for `versions: all`. |
| `schemaPath` | text | `/api/schema-service/v1/schema` | The schema service, which expands a kind with wildcards for a storage read. |
| `bulkDeletePath` | text | `/api/storage/v2/records/delete` | Storage's soft delete of a list of records, which a removal sends 500 ids at a time. |
| `deletePath` | text | `/api/storage/v2/records/{id}:delete` | Storage's soft delete of one record, which a removal falls back to; must name `{id}`. |
| `purgePath` | text | `/api/storage/v2/records/{id}` | Storage's purge of a record and every version, one id at a time; must name `{id}`. |

A path must start with `/` and hold no whitespace, query or fragment.

### Reading OSDU

- **`read: search`** (the default) pages the search index (`query_with_cursor`, 1,000 ids a page) returning `id`, `kind`,
  `version`, `createUser`, `createTime`, `modifyUser` and `modifyTime`. A viewer's entitlements are enough. The index
  lags writes and leaves out a record it failed to index, so such a record is not listed; when a ledger expects it, it is
  read from storage by id and reported `unlisted`.
- **`read: storage`** lists every active record of each kind from storage itself (`GET /query/records?kind=`, 1,000 ids a
  page, the `service.storage.admin` role), and reads their system properties 1,000 at a time (`POST
  /query/records/headers`), or 100 at a time through `POST /query/records` where a deployment does not serve the headers
  route. A kind with wildcards is expanded through the schema service into the kinds it matches, where `*` stands for
  any text within its segment (`1.*.*` takes every 1.x.y version), matched ignoring case as the search service matches a
  kind. Unindexed records are listed. A storage read takes no `query`.

A soft-deleted record is not served, so it is in no inventory.

### An inventory

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | text | required | A letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. A run's payload, the CLI and the GUI name the inventory by it. |
| `description` | text | none | What the inventory holds. |
| `kind` | text | required | The kind read, `authority:source:entityType:version`, each segment a value, `*`, or a value with `*` inside it (`1.*.*`), at most 300 characters. |
| `query` | text | every record | A Lucene query narrowing a search read, with `{name}` and `{partition}` tokens, at most 4,000 characters. Refused on a storage read. |
| `versions` | `latest` or `all` | `latest` | `all` also keeps every version storage keeps of each record, read (`GET /records/versions/{id}`) only for a record that is new or whose latest version moved since its versions were read. |

**Covering an entity type whole.** An inventory whose kind names the entity type and leaves the authority, source and
version as `*` (`"*:*:master-data--Wellbore:*"`), with no query, holds every record of that type OSDU serves. Its
reconcile also reads from storage the ledgers' delivered records and live minted ids of the type it holds no row of,
and reports each `missing` or `unlisted`. An inventory narrowed further (a query, or a concrete authority or version)
reports only the ids it listed before and no longer lists.

### Owners

An id no ledger knows is an `orphan` when one of the owners created it, else `foreign`. The owners are the flow's
`owners`; left out, a reconcile infers them: every identity that created at least 1% of the inventory's ids a ledger
claims (and at least one). Each reconcile records which identities it used, how many claimed ids each created, and how
it knew them (`declared`, `inferred`, or `none` when nothing is claimed).

### Removal

```yaml
removal: { findings: [orphan, stale, forgotten], purge: false }
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `findings` | list | required | The findings whose ids an operator may remove, at least one, each once: `orphan`, `stale`, `forgotten`. Every other finding is acted on through its ledger, and `foreign` ids are never removable. |
| `purge` | boolean | `false` | Whether a removal may purge (`DELETE /records/{id}`: every version destroyed, not reversible) rather than soft delete (`POST /records/delete`, reversible in OSDU). |

The removal goes through the flow's own `source` and credentials. A soft delete needs `users.datalake.editors` (or
admins) and the identity in each record's owners ACL; a purge needs `service.storage.admin` and owner (storage OpenAPI).
See [What a removal does](inventory-removals.md#what-a-removal-does).

`removal` governs what is removed from OSDU. Gone ids, which OSDU no longer serves, are not named in it: any inventory
flow removes them from the inventory alone (scope `inventory`), since nothing is asked of OSDU. See
[Removing gone ids](inventory-removals.md#removing-gone-ids).

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Reads every id of each inventory whole, merges it, reads versions where asked, and reconciles. |
| `reconcile` | Compares each inventory, as its last build left it, with the ledgers as they stand now, reading from storage only the ids a ledger expects. Refused for an inventory never built. |
| `plan` | Counts through the search the records each inventory matches (and lists the kinds a storage read expands to), reading no id and keeping nothing. |
| `remove` | Removes from OSDU the ids of one finding of one inventory, as an operator asked, for a flow that declares `removal`; or removes gone ids from the inventory alone, for any inventory flow. |
| `match` | Finds which ids of one inventory hold what an operator's conditions ask in OSDU and keeps them as the selection the payload names ([Selecting ids by what OSDU holds](inventory-selections.md)). Reads OSDU's search; changes nothing there. |
| `duplicates` | For the check the payload names: downloads the properties of its key for every id of one inventory into the check's table and groups the ids holding equal values there, or, when a pick was asked of it, reads each group's copies and picks the most complete to keep ([Finding duplicate records](inventory-duplicates.md)). Reads OSDU's search and storage; changes nothing there. |

The payload takes `inventories` (names; none takes every inventory). A `match` run names exactly one inventory and the
`selection` it fills, and a `duplicates` run exactly one inventory and the `duplicateCheck` it fills. A `remove` run also
takes `removal` and `confirm`, and names exactly one inventory:

```json
{
  "inventories": ["WellLogs"],
  "removal": { "finding": "orphan", "scope": "record", "expected": 37 },
  "confirm": "dev"
}
```

| Field | Meaning |
| --- | --- |
| `removal.finding` | `orphan`, `stale` or `forgotten`, one the flow's `removal.findings` names; or `gone`, whatever the flow declares. |
| `removal.scope` | `record` (a soft delete) or `everything` (a purge, only where `removal.purge` is true) for an id OSDU serves; `inventory` (the inventory's own row, nothing asked of OSDU) for a gone id, its only scope, and only its. |
| `removal.expected` | How many ids the operator was shown, at least 1. With no `ids`, the inventory must hold exactly that many ids of the finding. |
| `removal.ids` | The ids picked, 1 to 1,000, each once, as many as `expected`. Left out, every id of the finding is removed. |
| `removal.selection` | The selection whose ids of the finding are removed, every one of them, as many as `expected`; only for `orphan`, `stale` and `forgotten`, and never beside `ids` ([Removing what a selection holds](inventory-selections.md#removing-what-a-selection-holds)). |
| `selection` | The selection a `match` run fills, by its number. |
| `duplicateCheck` | The duplicate check a `duplicates` run fills, by its number. |
| `confirm` | The partition the run acts in, typed back; it must be the run's partition. |

`removal` and `confirm` are refused on any other operation, `selection` on any but `match`, and `duplicateCheck` on any
but `duplicates`. Anything else in the payload is refused before the run starts, for example
`payload runId does not apply to an inventory flow: only a delivery flow's reverse run names the run it reverses; an inventory flow's payload names only inventories, removal, confirm, selection and duplicateCheck.`

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation reconcile --payload '{"inventories":["WellLogs"]}'
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation remove --payload @removal.json
```

`build`, `reconcile` and `remove` keep what they do in the module's database; a `plan` keeps nothing. See
[running an OSDU flow](../cli/run.md).

## The pages of the inventory flow

| Page | What it says |
| --- | --- |
| [Inventory builds](inventory-builds.md) | What a build and a reconcile do, what each finding means, and the tables an inventory keeps. |
| [Selecting ids by what OSDU holds](inventory-selections.md) | A `match` of conditions on the records, the selection it fills, and removing what it holds. |
| [Inventory removals](inventory-removals.md) | A `remove` run: what it checks and records, repairing missing ids, and removing gone ids. |
| [Finding duplicate records](inventory-duplicates.md) | A `duplicates` run: the key, the download grouped in SQL, the pick, which copy is kept, an operator's choice, and removing the copies picked. |

## What a run returns

A build's or reconcile's result is the flow, the partition, how many inventories completed and failed, and per inventory
its kind, status, run number, ids listed, added, changed, gone and served again, versions read, ids checked in storage,
counts by finding, the owners and how they were known, pages, requests and error. A plan's result is, per inventory, the
query, how it reads, which versions, the records it would read, the kinds a storage read expands to, and its problems. A
removal's result is its number, the finding, the scope, how many ids were asked for, and how many were removed, already
gone, skipped and failed, with why it stopped. A match's result is the selection it filled, its status, how it read OSDU and
the query, the ids the inventory served, how many matched, how many records were outside the inventory and how many ids
changed since its build, the search requests it sent, its notes, and why it stopped. A duplicate check's result is in
[Finding duplicate records](inventory-duplicates.md#what-a-run-returns).

## Lineage and the GUI

An inventory flow reads each inventory's kind on its platform and partition and writes nothing another flow reads, so
lineage orders it after the delivery flows writing those kinds ([lineage](../concepts/lineage.md)). An inventory flow's
pipeline has an **Inventories** tab. **OSDU**, **In OSDU**, **Inventories** lists every inventory with what its last
reconcile raised; an inventory's report has its counts by finding over a grid of ids, the tabs Ids, Runs and (for a flow
that allows removal, or one that has removed anything) Removals, a lookup by OSDU id, and a CSV export. The **gone** tab
picks ids, or every one, to remove from the inventory, and the **missing** tab picks ids to deliver again. **Filter**
selects the ids by what OSDU holds ([In the GUI](inventory-selections.md#in-the-gui)).

## Errors when the document is read

| Message | Cause |
| --- | --- |
| `inventories must list at least one inventory; an inventory flow without inventories has nothing to read.` | No `inventories`. |
| `inventories[0] 'A': query narrows a search, and the flow reads storage (source.read: storage), which lists every record of a kind and takes no query. Take the query out, or read through search.` | A query on a storage read. |
| `source.read is 'index'; it is search (the index, a viewer's entitlements, the default) or storage (every active record, the storage service's admin role).` | An unknown read mode. |
| `inventories[0] 'A': versions is 'every'; it is latest (the latest version of each record, the default) or all (every version storage keeps, read for what is new or moved).` | An unknown `versions`. |
| `removal.findings[0] is 'missing'; the ids an inventory may remove are those of orphan, stale, forgotten: what OSDU serves that no ledger holds live. Every other finding is acted on through its ledger.` | A finding that is not removable. |
| `removal.findings[0] is 'gone'; a gone id is one OSDU no longer serves, so there is nothing of it in OSDU to remove. Its row is removed from the inventory, whatever the flow declares; ...` | `gone` named in the document: it is removed from the inventory without it. |
| `maxMissingChecks is 2000000; it is how many ids a ledger expects that one build reads from storage to tell missing from merely unlisted, between 0 (none) and 1,000,000.` | Out of range. |
| `source.purgePath '/api/storage/v2/records' must name the record it acts on as {id}, as '/api/storage/v2/records/{id}' does.` | A record path without `{id}`. |

## Errors when it runs

Each operation's page lists the errors its runs end with: a build or a reconcile in
[Inventory builds](inventory-builds.md#errors-when-it-runs), a `match` and a removal by a selection in
[Selecting ids by what OSDU holds](inventory-selections.md#errors-when-it-runs), a `remove` in
[Inventory removals](inventory-removals.md#errors-when-it-runs), and a `duplicates` run in
[Finding duplicate records](inventory-duplicates.md#errors-when-it-runs).
