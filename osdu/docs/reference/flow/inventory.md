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
  - findings
  - owners
  - maxmissingchecks
  - "source.read"
  - removal
  - soft delete
  - purge
  - remove gone ids
  - obsolete inventory rows
  - repair missing records
  - deliver again
  - restore soft deleted record
  - new osdu id
  - unlisted
  - drift between osdu and ledger
  - kind wildcard
  - missing checks in turn
  - inventory tables
  - InventoryRecord
  - InventoryScan staging
  - select ids by attribute
  - filter inventory by facility name
  - match operation
  - selection
  - remove by selection
  - InventorySelection
yamlPath: "(root, flowType: inventory)"
related:
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
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Match.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/SearchRequestCounter.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventorySelections.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventorySelection.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.Selections.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261020222949_InventorySelections.cs
  - osdu/gui/src/features/delivery/inventories/inventorySelection.ts
  - osdu/gui/src/features/delivery/inventories/InventorySelectionParts.tsx
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReaders.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReport.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Inventories.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRemovals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRepairs.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Inventory.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.InventoryRemoval.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261008024435_InventoryFlows.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261008132311_InventoryRemovals.cs
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
and remove what the selection holds ([Selecting ids by what OSDU holds](#selecting-ids-by-what-osdu-holds)).

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
reliability: { concurrency: 4 }

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
| `reliability` | map | delivery defaults | The HTTP settings a [delivery flow](delivery.md) takes, and `concurrency` (default 8): how many version lists a build reads at once for `versions: all`. Inventories are read one after another. `parallelInterfaces` is refused. |
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
See [What a removal does](#what-a-removal-does).

`removal` governs what is removed from OSDU. Gone ids, which OSDU no longer serves, are not named in it: any inventory
flow removes them from the inventory alone (scope `inventory`), since nothing is asked of OSDU. See
[Removing gone ids](#removing-gone-ids).

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Reads every id of each inventory whole, merges it, reads versions where asked, and reconciles. |
| `reconcile` | Compares each inventory, as its last build left it, with the ledgers as they stand now, reading from storage only the ids a ledger expects. Refused for an inventory never built. |
| `plan` | Counts through the search the records each inventory matches (and lists the kinds a storage read expands to), reading no id and keeping nothing. |
| `remove` | Removes from OSDU the ids of one finding of one inventory, as an operator asked, for a flow that declares `removal`; or removes gone ids from the inventory alone, for any inventory flow. |
| `match` | Finds which ids of one inventory hold what an operator's conditions ask in OSDU and keeps them as the selection the payload names ([Selecting ids by what OSDU holds](#selecting-ids-by-what-osdu-holds)). Reads OSDU's search; changes nothing there. |

The payload takes `inventories` (names; none takes every inventory). A `match` run names exactly one inventory and the
`selection` it fills. A `remove` run also takes `removal` and `confirm`, and names exactly one inventory:

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
| `removal.selection` | The selection whose ids of the finding are removed, every one of them, as many as `expected`; only for `orphan`, `stale` and `forgotten`, and never beside `ids` ([Removing what a selection holds](#removing-what-a-selection-holds)). |
| `selection` | The selection a `match` run fills, by its number. |
| `confirm` | The partition the run acts in, typed back; it must be the run's partition. |

`removal` and `confirm` are refused on any other operation, and `selection` on any but `match`. Anything else in the
payload is refused before the run starts, for example
`payload runId does not apply to an inventory flow: only a delivery flow's reverse run names the run it reverses; an inventory flow's payload names only inventories, removal, confirm and selection.`

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation reconcile --payload '{"inventories":["WellLogs"]}'
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation remove --payload @removal.json
```

`build`, `reconcile` and `remove` keep what they do in the module's database; a `plan` keeps nothing. See
[running an OSDU flow](../cli/run.md).

## The findings

Each id gets one finding. "Live" artifacts are the datasets and other ids a delivery minted.

| Finding | What OSDU serves | What the ledgers hold | Raised |
| --- | --- | --- | --- |
| `orphan` | the record | nothing; an owner created it | yes |
| `missing` | nothing | a delivered record, or a live minted id, that storage does not hold | yes |
| `undoing` | the id | a minted id whose undo is due or failed | yes |
| `forgotten` | the record | a record purged from its ledger, or an id minted by a delivery of one | yes |
| `stale` | the record | a record marked removed, or a minted id removed, gone or kept by an undo | yes |
| `unconfirmed` | the record | a record that never confirmed a delivery (pending, held or failed, no version), or a minted id still in intent or pending | yes |
| `drifted` | the record, at another version | a delivered record at another version | yes |
| `unlisted` | not in the read | a delivered record, or a live minted id, that storage holds: the index has not caught up, or it is outside the query | yes |
| `foreign` | the record | nothing; another identity created it | no |
| `superseded` | the dataset | a minted id a later delivery replaced, kept live on purpose | no |
| `tracked` | the record | a delivered record at this version, or a live minted id | no |
| `gone` | nothing | nothing: listed before, and no ledger expects it, or a ledger expects it and storage has not been asked for it yet (its reason says which) | no |
| `unreconciled` | the record | not compared yet: a build listed it and no reconcile has run since | no |

Every finding keeps the ledger and record, or the minted id, it rests on, and why in a line (`the ledger holds version
3, OSDU serves version 4`). A raised finding is one the report counts as a disagreement. What to do about each is in
the [orphans guide](../guides/finding-orphans.md#4-act-on-each-finding).

## What a build does

1. **Read.** Each inventory is read whole into a stage, 10,000 ids a write. A read that fails part way merges nothing:
   an id missing from a complete read is marked gone, so a partial read would mark live records gone. A run whose
   process stopped is closed failed by the next run of the inventory, which discards its stage.
2. **Merge**, in one transaction under a lock per inventory: new ids are added (`unreconciled` until compared), changed
   ones rewritten, ids served again brought back, and ids the read did not list marked gone, never deleted. A second
   build of the same inventory waits for the lock and, after ten minutes, fails with `Another build of the inventory held
   it for ten minutes; this build merged nothing.`
3. **Versions**, for `versions: all`: the version list of every record that is new or moved, `concurrency` at a time.
4. **Reconcile**: the owners, the finding of every id served, then up to `maxMissingChecks` ids a ledger expects that the
   read did not list, read by id from storage (`missing` when storage does not hold it, `unlisted` when it does). The ids
   are asked for least recently asked first (never asked first, by the row's `CheckedUtc`), so builds that each reach the bound take turns through
   all of them. An id a build did not ask for keeps what storage last answered; one never asked is `gone`, its detail
   saying a ledger expects it and a later build asks. With `maxMissingChecks: 0` nothing is read from storage and every
   id no longer listed is `gone`. A build that reaches the bound logs that more may be missing.

Inventories run one after another; one that fails leaves the others to complete, and the run ends failed naming it.

## Selecting ids by what OSDU holds

An inventory knows each id by its finding; what the record holds (its name, the well it belongs to, the system it came
from) is OSDU's. To act on part of a finding, say the orphans of one wellbore, an operator selects the ids by what OSDU
holds of them: conditions on the records' properties, matched with the inventory's own ids by the OSDU id. The ids that
hold them are a selection, which the report lists and a removal can take whole.

The conditions are the explorer's ([conditions and properties](../concepts/explorer.md#conditions-and-properties)), at
most 12, all holding together: a property of the inventory's kind, asked the way the platform indexes it (`contains`,
`is`, `is one of`, `starts with`, a range, `has a value`, and their negations), or a column of a source system the flows
read ([search terms](../concepts/search-terms.md)), carried through its mapping before OSDU is asked. A selection is
opened with them (`POST /api/v1/delivery/inventories/{partition}/{inventoryId}/selections`, body `filters`), and filled
by a `match` run of the inventory flow, through the flow's own `source` and credentials, as its build reads.

### How a match reads OSDU

1. A source column reached through other records (the well's name, for a wellbore) is read first, as the explorer reads
   it, into a condition on the inventory's own records; every condition is then one clause of the query.
2. The records the conditions find in the inventory's kind, narrowed by its `query`, are counted: one request.
3. They are read the way that sends fewer searches:
   - **by conditions** (`conditions`): every record they find, 1,000 a page, paged as the flow's `source.paging` says;
     this is the way nearly always, since the conditions find no more records than the kind holds; or
   - **by ids** (`ids`): the ids the inventory serves, 500 to a search (fewer when the conditions compare many values,
     since the search takes 1,024 clauses in a query), each search asking the conditions of those ids alone. A kind that
     grew far past the inventory since its last build is read this way, so the match costs a search per 500 of the
     inventory's ids however many records of the kind the conditions find.
4. Each record found that the inventory serves a row of is kept, matched by its OSDU id, with the version the search index
   holds and what it holds at each property the conditions ask (the first 20 values of each, cut to 4,000 characters in
   all). A record the inventory serves no row of (written since its last build, or outside its query) is counted as
   outside the inventory, and not kept.

Only ids the inventory serves can be selected: OSDU's search does not serve a missing, unlisted or gone id, so no
condition reaches one. For an inventory read through storage, a record the search index does not hold is never selected
either. An id whose indexed version is not the one the inventory listed changed since its last build; the match counts
it, and a removal skips it.

A match keeps each page as it reads it, so the page following it shows how far it got. One that stops (the search refusing
or failing, the run cancelled, its process stopped) keeps no id, and its selection says why; a run tried again starts its
match afresh. A match is a run of the flow, so one queued while a build or a removal of the flow runs waits for it.

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation match --payload '{"inventories":["Wellbores"],"selection":41}'
```

The run fills a selection the control plane opened; the page and the API open them, and a run naming no selection, or one
of another inventory, is refused.

### What a selection says

| Status | Meaning |
| --- | --- |
| `queued` | Opened, its match queued as a run of the flow. |
| `running` | Reading OSDU: how much it has to read (`toCheck`, the records the conditions find or the inventory's ids) and has read (`checked`). |
| `completed` | Read OSDU whole: it holds the inventory's ids that hold what the conditions ask (`matched`), with the records found outside the inventory (`outside`), the ids at another version than the inventory listed (`changed`), and the search requests it sent. |
| `failed` | Stopped, holding no id; `error` says why. A selection whose run ended without finishing it, or was never queued, is closed so when it is read. |
| `expired` | Older than a week and kept for the removal that took its ids: its conditions and counts stay, its ids were retired. |

A completed selection made against an earlier build of the inventory is `stale`: its ids are still listed, and a removal
refuses to take them until the conditions are matched again. The same conditions, however they are spelled, asked again of
the same build answer with the match already made of them (200); `refresh` reads OSDU again (202). Each id of a selection
is counted under the finding the inventory gives it now, so its counts follow a reconcile or a removal.

A selection is kept a week. The next match of the partition retires those older: deleted whole, or, when a removal took
their ids, kept `expired` with their ids deleted, so the removal still says what it removed by.

### Removing what a selection holds

A removal names a `selection` in place of `ids`: every id of the finding the selection holds, as many as `expected`,
however many that is. Only `orphan`, `stale` and `forgotten` ids, which go from OSDU, are taken from a selection; a removal
names ids or a selection, not both. It is refused unless the selection is one of the inventory's, its match completed and
was made against the inventory's last build, and it holds exactly `expected` ids of the finding. Every id is then checked
again just before it goes, as any removal checks it ([What a removal does](#what-a-removal-does)). The removal keeps the
selection it took its ids from, and the **Removals** tab says which conditions chose them.

```json
{
  "inventories": ["Wellbores"],
  "removal": { "finding": "orphan", "scope": "record", "expected": 212, "selection": 41 },
  "confirm": "dev"
}
```

### In the GUI

**Filter**, at the end of the Ids tab's line, opens the explorer's editor on the inventory's kind: its properties, the
columns of the source systems its flows read, and the values the kind's records hold, the commonest first. Each condition
is a chip over the grid, changed by a click and dropped by its cross, and the conditions live in the page's address (`f`),
so a link opens the same selection. At the end of the chips' row the match says where it stands: asking, matching with how
far it got and its run, matched (when, and behind the mark what it found, the query and how it read OSDU), built again
since the match, or failed with why. While it holds, every tab counts its matching ids before its own (`212 / 60,000`),
the grid lists only them, with a column for each property the conditions ask, and **Select all** picks every matching id
of a removable finding for one removal by the selection. The CSV export adds the matching ids, with their values.

## What a removal does

A removal is a `remove` run, queued from the inventory's page in the GUI (`POST
/api/v1/delivery/inventories/{partition}/{inventoryId}/removals`) or run with `sqlflow run --operation remove`. Before it
reads anything it is refused unless the flow allows the finding and the scope, `confirm` names the run's partition, the
inventory has been reconciled, and the inventory holds as many ids of the finding as `expected` (or a row of every id
named; or, for a removal by a selection, the selection holds as many, matched against the inventory's last build). The page
and the API also refuse while a run of the flow is queued or running.

It then reads the ids 500 at a time, in order. Each id is checked again just before it goes, and left in OSDU, skipped
with why, when the inventory no longer finds it so, the ledgers give it another finding now, storage serves another
version than the inventory listed, or (an orphan) storage says no owner created it; an id storage no longer holds is
`gone`. The rest are removed through the flow's `source`: a soft delete 500 ids a request, or a purge one id at a time. A
soft delete answered 207 has deleted every id it does not name, and only the ids it names are sent again one at a time, so
each has its own outcome: one storage no longer holds is `gone`, one it refuses is `failed`. Storage refuses a record with
403 when the flow's credentials are no owner of it (its `acl.owners`), so such a record fails every removal until its ACL
names them or one of its owners removes it, and its outcome says so. A 207 that names no id it was sent leaves every id of
the chunk `failed` with that answer, since asking again could not tell an id it deleted from one that was gone before.

Every chunk is recorded before the next is read: each id's outcome (`removed`, `gone`, `skipped`, `failed`) with its
version and why, the inventory's rows marked gone at once, and, for a stale record, its own ledger's removal (an attempt
naming who asked). A removal stopped part way keeps what it did; when its process stopped before it finished, the next
removal of the inventory closes it as failed (`the removal ended without finishing: its process stopped before it did;
what it recorded stands`) before it starts. OSDU refusing every id of a chunk with 401 or 403 stops
the removal there: `OSDU refused the removal (403) of every id of a chunk: the flow's source credentials must be an owner
of the records (users.datalake.editors, and in each record's owners ACL). Nothing more was asked of it.` The removal is
an `inventory-remove` activity of the audit trail.

### Repairing missing ids

A `missing` id is a record a ledger delivered that OSDU no longer holds: a cleanup or an accident soft deleted or purged
it. On the **missing** tab, pick ids or **Select all**, and **Deliver again**: each record is delivered again through the
flow that delivered it, and a deliver run of each such flow is queued
([decision 0015](../../wiki/decisions/0015-lost-records.md)).

- **Under their own ids** (the default). Each record is marked as OSDU having lost it and asked to be planned again, as
  a verify with `reconcile` marks one. The storage route brings a soft-deleted record back before writing it (`PATCH
  /records/{id}` with `{"deleted": false}`), so the keys the write keeps of it (`preserveDataKeys`) are kept, and writes a
  purged one again under the same id. Records that name the id are whole again once the run lands.
- **Under new OSDU ids**, with the partition typed back. Each record claims a new id in the same partition and entity
  type, made from its key and the id it replaces, and is written under it from then on whatever its mapping makes from its
  key. Its history keeps both ids, and it is found by the new one. Records that name the old id keep naming it: a new id
  is for the case where the old one must not be used again.

Only a record its ledger holds as delivered is reached. A dataset a delivery minted, a record held, failed, removed or
reverted since the reconcile, and one whose flow is no longer in the catalog are left, and the answer says why. The API is
`POST /api/v1/delivery/inventories/{partition}/{inventoryId}/repairs` with `expected`, `ids` (at most 1,000), `newIds`
and `confirm`; the repair is an `inventory-repair` activity of the inventory flow, and each flow's marking an activity of
its own naming every record. No `removal` is needed in the inventory flow's document: the records are written by their
own flows, with their own credentials.

### Removing gone ids

A gone id stays in the inventory, with when OSDU stopped serving it, so the report says what disappeared and when. When
that history is no longer wanted, a removal with `finding: gone` and `scope: inventory` takes the rows out of the
inventory, with the versions kept of them (`versions: all`). It asks nothing of OSDU, so any inventory flow allows it,
whatever its `removal` declares, and the flow's credentials play no part.

```json
{
  "inventories": ["WellLogs"],
  "removal": { "finding": "gone", "scope": "inventory", "expected": 60955 },
  "confirm": "dev"
}
```

It is asked for and checked as any removal is: the partition typed back, the inventory reconciled and holding exactly
`expected` gone ids (or a row of every id named), and no run of the flow queued or running. It then takes 2,000 ids a
round, each round in one transaction under the lock a build's merge holds, so a round and a merge go one after the
other. An id stays, `skipped` with why, when the inventory no longer finds it gone, OSDU serves it again (the last build
listed it), or a ledger expects it (a later build asks storage whether it is missing). The rest are removed.

A removal from the inventory keeps how many ids it removed, who asked and when, but not the ids themselves: a row of
each would keep what it removes. Its items are the ids it kept. A lookup by OSDU id no longer finds a removed id in the
inventory, no ledger changes, and a later build lists again, as a new id, any id OSDU serves again.

## What a run returns

A build's or reconcile's result is the flow, the partition, how many inventories completed and failed, and per inventory
its kind, status, run number, ids listed, added, changed, gone and served again, versions read, ids checked in storage,
counts by finding, the owners and how they were known, pages, requests and error. A plan's result is, per inventory, the
query, how it reads, which versions, the records it would read, the kinds a storage read expands to, and its problems. A
removal's result is its number, the finding, the scope, how many ids were asked for, and how many were removed, already
gone, skipped and failed, with why it stopped. A match's result is the selection it filled, its status, how it read OSDU and
the query, the ids the inventory served, how many matched, how many records were outside the inventory and how many ids
changed since its build, the search requests it sent, its notes, and why it stopped.

## Lineage and the GUI

An inventory flow reads each inventory's kind on its platform and partition and writes nothing another flow reads, so
lineage orders it after the delivery flows writing those kinds ([lineage](../concepts/lineage.md)). An inventory flow's
pipeline has an **Inventories** tab. **OSDU**, **In OSDU**, **Inventories** lists every inventory with what its last
reconcile raised; an inventory's report has its counts by finding over a grid of ids, the tabs Ids, Runs and (for a flow
that allows removal, or one that has removed anything) Removals, a lookup by OSDU id, and a CSV export. The **gone** tab
picks ids, or every one, to remove from the inventory, and the **missing** tab picks ids to deliver again. **Filter**
selects the ids by what OSDU holds ([In the GUI](#in-the-gui)).

## The tables an inventory keeps

An inventory flow keeps what it reads and finds in the module's database, in the `osdu` schema, under its own ledger in
the partition: every build and reconcile registers that ledger (kind `inventory`, named `name@partition` for a flow that
works in partitions) before it reads anything. Every table is keyed by the partition first, so each partition's inventories are
apart, and no table has a foreign key into another. The comparison with the ledgers reads the delivery ledgers'
`osdu.Record` (by the id each record claims), `osdu.Artifact` and `osdu.PurgedRecord` (by OSDU id), and never writes them.

| Table | One row per |
| --- | --- |
| [`osdu.Inventory`](../tables/inventory.md) | inventory of a flow in a partition |
| [`osdu.InventoryRun`](../tables/inventory-run.md) | build or reconcile of one inventory |
| [`osdu.InventoryScan`](../tables/inventory-scan.md) | id one build's read listed, while the build runs |
| [`osdu.InventoryRecord`](../tables/inventory-record.md) | id an inventory holds, or a ledger of the partition expects it to hold |
| [`osdu.InventoryVersion`](../tables/inventory-version.md) | version storage keeps of a record (`versions: all`) |
| [`osdu.InventoryRemoval`](../tables/inventory-removal.md) | removal an operator asked for |
| [`osdu.InventoryRemovalItem`](../tables/inventory-removal-item.md) | id a removal reached; for a removal of gone ids, only each id it kept |
| [`osdu.InventorySelection`](../tables/inventory-selection.md) | selection an operator's conditions opened |
| [`osdu.InventorySelectionItem`](../tables/inventory-selection-item.md) | id of an inventory a match found |

Each table's page says what every column holds, what each index serves, and what writes and reads it.

A repair keeps no table of its own: it reads the inventory's `missing` rows (each with the ledger and record that claims
it) and is an `inventory-repair` activity of the inventory flow's ledger, while each delivery ledger records the marking
of its own records ([Repairing missing ids](#repairing-missing-ids)). A removal's `ActivityId` names its
`inventory-remove` activity. OSDU ids (`TargetId`) are kept in a binary collation, so they are compared exactly, as OSDU
compares them.

### What a row of an inventory holds

An id's system properties as OSDU last served them, when a build first listed it, last saw it change and first found it
gone, when a reconcile last asked storage for it, its finding with when it last changed and what it rests on, and the
ledger, record and artifact that claim it. Every column: [`osdu.InventoryRecord`](../tables/inventory-record.md).

### How a build moves rows through the scan

1. **Open.** The run of an inventory first closes any run of the same inventory still `running` as `failed` (`the run
   ended without finishing: its process stopped before it did`), deletes every row its earlier runs left in
   `osdu.InventoryScan`, and opens its own `osdu.InventoryRun`.
2. **Stage.** The read appends what it lists to `osdu.InventoryScan` under the run's id, 10,000 rows a bulk copy, each in
   its own transaction. Nothing of `osdu.InventoryRecord` changes while the read runs, so a read that fails part way
   leaves the inventory as the last complete build left it.
3. **Merge**, in one transaction holding the inventory's lock (`osdu-inventory-<partition>-<inventory>`, the lock a
   removal of gone ids takes too). The run's staged rows are cut to one per id: the highest version a repeat listed, and
   of equal versions the last listed. Then, in this order, each counted:

   | Count | What changes in `osdu.InventoryRecord` |
   | --- | --- |
   | served again | A row gone, or never listed, that the read lists: its properties are written, `GoneUtc` cleared, `FirstSeenUtc` set when empty. |
   | changed | A row the read lists whose kind, version or system properties differ: they are rewritten and `ChangedUtc` set. |
   | added | An id with no row: inserted, `unreconciled`, first seen now. |
   | gone | A row listed before that the read did not list: `GoneUtc` set. The row stays. |

   The run's staged rows are then deleted in the same transaction, so `osdu.InventoryScan` holds nothing of a merged run.
4. **Versions** (`versions: all`): the records served whose `VersionsAt` is not their version, 500 at a time, have their
   versions read and their rows in `osdu.InventoryVersion` replaced.
5. **Reconcile.** The finding of every row OSDU serves is set from the ledgers, written only where the finding, the
   ledger record or the detail changed. Then the ids a ledger expects and the read did not list are asked of storage, up
   to `maxMissingChecks`, and in one transaction: the rows not asked become `gone` (or keep what storage last answered),
   the rows asked become `missing` or `unlisted` with `CheckedUtc` set, and an id a ledger expects that has no row is
   inserted with no `FirstSeenUtc`.
6. **Close.** A completed run writes its counts and findings to `osdu.InventoryRun` and its last build, last reconcile
   and owners to `osdu.Inventory`. A failed run's staged rows are deleted when it closes; those of a run whose process
   stopped are deleted by the next run of the inventory (step 1).

A `reconcile` runs steps 1, 5 and 6 and stages nothing. Rows leave `osdu.InventoryRecord` only through a removal of gone
ids ([Removing gone ids](#removing-gone-ids)); a removal from OSDU marks its ids gone and keeps them.

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

| Message | Cause |
| --- | --- |
| `Inventory 'WellLogs' has never been built in 'dev', so there is nothing to reconcile yet; build it first.` | `reconcile` before a build. |
| `Inventory flow 'welldb-06-inventory' allows no removal: its document declares no 'removal', so it only reads OSDU. ...` | `remove` on a flow without `removal`. |
| `Inventory flow 'welldb-06-inventory' allows soft deletes only; removal.purge: true lets it purge, which destroys every version for good. Nothing was removed.` | `scope: everything` without `purge: true`. |
| `The removal confirms partition 'test', and inventory flow 'welldb-06-inventory' reads 'dev'. Nothing was removed.` | `confirm` names another partition. |
| `Inventory 'WellLogs' holds 41 orphan id(s) now, and the removal was asked for 37: what it holds changed since it was shown. Nothing was removed; look at it again and ask again.` | The inventory changed since the operator looked. |
| `payload removal asks for gone ids with scope record: a gone id is one OSDU no longer serves, so there is nothing of it in OSDU to remove; its removal takes the inventory's own row (inventory).` | A gone removal with another scope than `inventory`. |
| `payload removal asks for orphan ids with scope inventory: OSDU serves an id the inventory finds orphan, and the next build would list it again; ...` | `scope: inventory` for an id OSDU serves. |
| `Storage no longer knows the cursor of its listing of <kind> part way through, so the listing cannot be read whole; the next build reads it again from the start.` | A storage listing lost its cursor. |
| `Inventory 'Wellbores' has not been built in 'dev', so it holds no id to match; build it first. Nothing was matched.` | `match` before a build; the selection is closed with it. |
| `Inventory 'Wellbores' in 'dev' holds no selection 41: it was opened for another inventory, or retired since. Nothing was matched.` | `match` naming a selection the inventory does not hold. |
| `Selection 41 of inventory 'Wellbores' holds 209 orphan id(s) now, and the removal was asked for 212: what it holds changed since it was shown. Nothing was removed; look at it again and ask again.` | The selection's ids changed finding since the operator looked. |
| `Selection 41 was matched against an earlier build of inventory 'Wellbores', and the inventory was built again since: match its conditions again before removing what it holds. Nothing was removed.` | A removal by a stale selection. |
| `Selection 41 of inventory 'Wellbores' is running, not completed: only a match that read OSDU whole says which ids hold what its conditions ask. Nothing was removed.` | A removal by a selection whose match did not complete. |
