---
id: delivery-flow-cache-records
title: "Whole records in a cache type (types[].fetchRecords): data.ExtensionProperties and what else the search index leaves out"
type: flow-reference
summary: "Keep data.ExtensionProperties, or any part of an OSDU record the search index does not hold, by reading each record whole out of storage."
keywords:
  - fetchRecords
  - "types[].fetchRecords"
  - extension properties
  - ExtensionProperties
  - data.ExtensionProperties
  - whole record
  - full record
  - storage read
  - storage query records
  - not in the search index
  - not indexed
  - no item carried
  - cache meta
  - open object
yamlPath: "types[].fetchRecords"
related:
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-guide-reference-data-cache
  - delivery-flow-mapping-lookups
  - delivery-flow-retrieval
  - delivery-table-cache-definition
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceCaptureSpec.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/specs/core/storage/openapi.yaml
  - osdu/specs/core/INTEGRATION.md
---

# Whole records in a cache type (types[].fetchRecords): data.ExtensionProperties and what else the search index leaves out

A [cache flow](cache.md)'s OSDU type is captured through the search service, and the search index holds a projection of
each record: what the kind's schema describes and nothing else. A value under `data.ExtensionProperties`, which every
OSDU schema leaves open, is in the record and never in the index, so a path to it caches nothing and the refresh warns
`no item carried '<path>'`. With `fetchRecords: true` on the type, the search only finds the records, and each one is read
whole from the storage service, so every path the record holds can be cached.

```yaml
flowType: cache
name: welldb-wellbores-00-cache
batch: welldb
description: The wellbores the well log mappings link to, with the well database's own codes kept in their extension properties.
partitions: [dev]

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}

types:
  - kind: "osdu:wks:master-data--Wellbore:*"
    fetchRecords: true
    fields:
      - data.FacilityName
      - { path: data.ExtensionProperties.WellDb.WellboreCode, as: WellboreCode }
      - { path: data.ExtensionProperties.WellDb, as: WellDb }
```

A mapping then finds a wellbore by the code the source holds, as by any cached value ([lookups](mapping-lookups.md)):

```yaml
WellboreID:
  $cache: Wellbore.id
  $findBy:
    - WellboreCode = wellbore_code
```

## What can be cached

Any path of the record as storage holds it (openapi storage v2, `Record`): `data` with everything under it, `meta`,
`acl`, `legal`, `ancestry`, `tags`, `kind`, `version`, and the create and modify users and times. A path that ends at an
object or a list keeps it whole, as on any cached type, so `data.ExtensionProperties.WellDb` above keeps the object, and a
mapping reads a value inside it by its path. The properties the indexer adds to its projection and storage does not hold
(`namespace`, `type`, `authority`, `source`, `index`) are still read from the search hit, so a type can keep both.

The values are the ones storage holds, as they were written. The search index holds them as the indexer normalised them
(units converted to SI and coordinates to WGS 84 for a record with `meta[]` entries, and `Wgs84Coordinates` the indexer
computed from `AsIngestedCoordinates`), so turning `fetchRecords` on for a type of such records changes those values in
the cache, which the next refresh tags like any other change ([onChange](cache.md#onchange)).

## How a refresh reads the records

1. The type's kind and query are searched page by page, as for any OSDU type ([search paging](../concepts/search-paging.md)),
   asking for each record's id alone, and the paths under a property only the index holds. The next page is asked for
   once the page's records are read, so the search cursor stays open meanwhile; for a large type, `source.paging:
   createTime` holds nothing open on the service between pages.
2. Each page's records are read from `POST /api/storage/v2/query/records`, 100 ids a request and 4 requests at a time,
   under the flow's credentials, retry and rate limit. A type of 40,000 records costs 400 storage requests beside its
   search; a run with `operation: plan` names the number for each type before anything is read.
3. A record the index lists and storage does not return (deleted since it was indexed) is not cached; the refresh logs how
   many and names the first five. A record storage asks to be retried, and again when it is, fails the refresh: whether
   it holds the record is unknown, so the type is not read whole, nothing is written, and the cache keeps its version.
4. The declared paths of each record are kept, and the capture is merged as any other.

Storage's read takes the role `service.storage.viewer` (or `creator` or `admin`), where the search takes
`service.search.user`; the data groups (`users.datalake.viewers` and above) usually hold both. A record's ACL decides
whether storage hands it over, as it decides whether the search finds it, so a record the flow's identity cannot read is
counted with the records storage did not return.

## Several flows, one type

A type is shared by every cache flow of its partition that declares it, and a captured record replaces the cached one
whole. So when any of them reads the type whole, every capture of it does: the refresh of a flow that does not say
`fetchRecords` itself, and a delivery flow's `cache.before` step, read each record whole too, or they would drop the
values only storage holds from every record they found. The [Cache page](../concepts/partition-cache.md#where-to-see-it)
shows for each type where its values are read from, and which flows ask for whole records. A `cache.after` step reads
what its run landed from storage whatever the type says, since the index holds a record only a moment after it is written.

## What goes wrong

| Message | Cause |
| --- | --- |
| `types[<n>] holds table <table>, which takes no 'fetchRecords': ...` (or `dictionary`, `dimension`) | `fetchRecords` on a lookup table, which holds no OSDU record. |
| `Reference type <name>: storage did not say whether it holds <n> record(s) the search of kind <kind> found, asked twice (retryRecords: ...)` | Storage answered some ids with `retryRecords` twice; refresh again. |
| `Reference type <name>: no item carried '<path>' ... is read with fetchRecords: true on the type.` (a warning) | The path is not in the index; read the type whole. |
| `Reference type <name>: no record storage holds carried '<path>' ...` (a warning) | No record holds the path, even read whole; check its spelling against a record in the explorer. |

## Related

- [Cache flow](cache.md): every key of a cache flow.
- [The partition cache](../concepts/partition-cache.md): versions, several flows sharing a cache, and the cache steps.
- [Retrieval flow](retrieval.md): `fetchRecords` there writes whole records to the lake.
- [`osdu.CacheDefinition`](../tables/cache-definition.md): the declaration's `FetchRecords` column.
