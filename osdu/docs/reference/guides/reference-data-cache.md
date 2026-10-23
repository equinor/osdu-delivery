---
id: delivery-guide-reference-data-cache
title: "Caching OSDU reference data (units, curve types) so a mapping can resolve reference ids"
type: guide
summary: "Capture OSDU reference data such as UnitOfMeasure and LogCurveType into a partition's cache, and turn a source code into its reference id in a mapping."
keywords:
  - reference data
  - unitofmeasure
  - logcurvetype
  - reference id
  - resolve a reference
  - cache flow
  - "$cache"
  - "$findby"
  - ref modifier
  - "$unverified"
  - kind
  - refresh the cache
related:
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-modifiers
  - delivery-guide-lookup-table-cache
  - delivery-cli-cache
  - delivery-cli-check
  - delivery-concept-preflight
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SystemPropertyCapture.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/samples/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
---

# Caching OSDU reference data (units, curve types) so a mapping can resolve reference ids

A well log record does not carry a unit as `gAPI`: it carries a reference to the partition's UnitOfMeasure record,
`dev:reference-data--UnitOfMeasure:gAPI:`. To write such references, a mapping needs to know which reference records the
partition holds. A [cache flow](../flow/cache.md) captures them, kind by kind, into the partition's cache, and the
mapping resolves each source code against that cache while it renders. A reference to a record the partition does not
hold is then caught before it is sent, and the record is held with the reason instead of reaching OSDU as a reference to
nothing.

This guide captures UnitOfMeasure, LogCurveType and WellLogSamplingDomainType for partitions `dev` and `test`, and
resolves them in a WellLog mapping.

## 1. Decide what to cache

Cache the closed vocabularies a mapping resolves codes against: units, curve types, sampling domains, measurement
types. Keep the fields a mapping matches on (`data.Code`, `data.Name`, and for units often `data.ID`). Business data a
mapping finds one record of per row, such as the wellbore a log belongs to, can be cached the same way, or searched for
on the platform as each record needs it ([mapping lookups](../flow/mapping-lookups.md)).

A type keeps every record its search matches. Narrow it with `query` only where the records' own values say which ones
a mapping reads: a mapping that builds a reference id looks the record up by its id, and a record's code need not match
the last part of its id.

## 2. Write the cache flow

The estate's reference data cache, `osdu-reference-00-cache.yaml`:

```yaml
flowType: cache
name: osdu-reference-00-cache
batch: reference
description: The OSDU reference data the well log mappings resolve units, curve types and sampling domains against.
partitions: [dev, test]

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
  - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
    fields: [data.Code, data.Name]
  - kind: "osdu:wks:reference-data--LogCurveType:*"
    fields: [data.Code, data.Name]
    onChange: approve
  - kind: "osdu:wks:reference-data--WellLogSamplingDomainType:*"
    fields: [data.Code, data.Name]

reliability:
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }
  timeoutSeconds: 100

schedule:
  cron: "0 1 * * *"
  timezone: UTC
  values:
    partition: "*"      # every fire refreshes dev, then test
```

- **`partitions`** names the caches the flow fills; each refresh searches with that partition's `data-partition-id` and
  resolves its references with the partition's own central configuration first. Leave it out to fill every partition
  registered with the catalog.
- **`source`** is the platform and the identity the searches use, written as a delivery flow's target is. Use the
  identity the deliveries use, so the cache holds what the delivering identity can see.
- **`types[].kind`** is the kind searched, with `*` per segment, so every version of the reference type is captured. The
  name a mapping reads it by is derived: `reference-data--UnitOfMeasure` gives `UnitOfMeasure`.
- **`fields`** are the paths kept, stored without `data.`: a mapping reads `Code` and `Name`. The record id is always
  kept.
- **`onChange: approve`** on LogCurveType holds the records a curve type change reaches until someone approves it; the
  other types update on their own.
- **`schedule`** refreshes both caches nightly, ahead of the deliveries. A fire names its partition in `values`, and
  `*` is every partition the flow names; a fire that names none refreshes only the registry's default partition, and
  fails unless the default is one of the two.

## 3. Check it, then count

```bash
sqlflow validate reference/osdu-reference-00-cache.yaml
sqlflow run reference/osdu-reference-00-cache.yaml --operation plan --set partition=dev
```

`validate` prints `OK  'osdu-reference-00-cache' is valid (cache: ${env:OSDU_URL} -> catalog).` The `plan` operation
asks the search service how many records of each kind match and writes nothing; its log reads `plan UnitOfMeasure: <n>
record(s) of kind osdu:wks:reference-data--UnitOfMeasure:* match the query *`.

## 4. Refresh

```bash
sqlflow run reference/osdu-reference-00-cache.yaml --set partition=dev
sqlflow run reference/osdu-reference-00-cache.yaml --set 'partition=*'   # dev, then test
```

A refresh sweeps every type in full and merges the capture into the partition's cache; the first refresh writes the
cache's first version. On a workstation, `sqlflow` needs the module's database, the catalog's (`--db`, by default
`${env:SQLFLOW_CATALOG_DB}`). In the GUI, **Refresh now** on the Cache page (a **Refresh** menu
naming the flows, when several fill the partition) refreshes the partition picked in the title bar, and the flow's
schedule refreshes both partitions every night.

Watch the log for two warnings: `the search matched no record of kind ... Check the kind and the query.` (a wrong kind or
query) and `no item carried '<path>' ... Check the path against the kind` (a path no record holds, which would make
every lookup on it miss). A type that cannot be read whole fails the refresh and the cache keeps the version it had.

## 5. Look at what it holds

```bash
sqlflow cache list dev
```

lists the versions, newest first, with the types each one moved, and the partition's system properties (the indexer
and search feature flags). On the Cache page (OSDU, Build, Cache), the Records tab browses a type's records and
searches every value they hold; a record shows how a mapping reads it (`$cache: UnitOfMeasure.id` and a `$findBy` line to
copy). The Setup tab lists the cache flows and the partition's OSDU feature flags.

## 6. Resolve references in the mapping

Three ways, each suited to a different source, shown side by side in `WellLog@1.2.0`, a version of the well log mapping
written to compare them; the well database's own `WellLog@1.0.0` translates its spellings through lookup tables and
builds each reference with `ref` ([lookup table guide](lookup-table-cache.md)):

```yaml
documentType: mapping
name: WellLog
version: 1.2.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
description: Well logs from the well database, with every reference resolved against the partition's reference data.

dataset:
  system: welldb
  key: [log_id]

parameters:
  dataPartition: { required: true }
  aclOwner: { required: true }
  aclViewer: { required: true }
  legalTag: { required: true }

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    Name:
      $from: log_name
    # The code as the source writes it, built into the reference id and checked against the cached records.
    SamplingDomainTypeID:
      $from: sampling_domain
      $modifiers:
        - ref
    Curves:
      $forEach: curves
      $item:
        Mnemonic:
          $from: curve_mnemonic
        # The cached UnitOfMeasure record whose Code, else whose Name, is the source's unit.
        CurveUnit:
          $cache: UnitOfMeasure.id
          $findBy:
            - Code = curve_unit
            - Name = curve_unit
        # The cached curve type by code; else the code as an unverified reference the partition may not hold yet.
        LogCurveTypeID:
          $coalesce:
            - $cache: LogCurveType.id
              $findBy: Code = curve_type
            - $from: curve_type
              $unverified: true
              $modifiers:
                - ref
          $required: false
```

- **Find the record: `$cache: <Type>.id` with `$findBy`.** The lines are tried in order and the first that finds a record
  wins. An exact match wins; case is ignored only when that finds exactly one record, because codes that differ only by
  case can be different records. The node writes the record's id with its version separator,
  `dev:reference-data--UnitOfMeasure:gAPI:`. No record found: `$required` decides (the record is held, or the property
  left out). Several records found: the record is held.
- **Build the id: `ref`.** When the source already writes the partition's code, `ref` builds the reference
  `{dataPartition}:reference-data--WellLogSamplingDomainType:<code>:`, taking the entity type from the template's
  relationship for the property. When the cache version holds records of that entity type, the id has to name one of
  them; otherwise it is a miss and `$required` decides. When it holds none of that type, the id is written as built.
- **Accept a reference the partition does not hold yet: `$unverified: true`.** Where the source is the authority for the
  code and the partition only lags behind, the id is written anyway and recorded as unverified; when a later refresh
  captures the record, the change is tagged `found` and the record is built again against it. `$coalesce` tries its
  alternatives in order, so the verified lookup comes first.

When the source spells codes its own way, translate first with a lookup table: `replace: $cache.UnitAlias` before `ref`,
or in the `$modifiers` of a `$cache` node ([lookup table guide](lookup-table-cache.md)).

## 7. Check the mapping against the cache

With the well log delivery flow pinning it (`render.mapping: WellLog@1.2.0`):

```bash
sqlflow check flows/welldb-welllog-03-delivery.yaml --partition dev
```

runs the [preflight](../concepts/preflight.md) against the pinned template and the partition's current cache version,
and names the version it read. The cache-related failures read:

| Message | Fix |
| --- | --- |
| `<property> reads UnitOfMeasure from the cache, and cache version '<v>' does not hold it. Cached: ...` | Declare the type in a cache flow of the partition and refresh it. |
| `<property> finds UnitOfMeasure by Code, and cache version '<v>' caches none of those. Cached: ...` | Add the path (`data.Code`) to the type's `fields` and refresh. |
| `<property> reads '<field>' out of <Type>, which cache version '<v>' does not cache. Cached: ...` | The same, for a field the node writes. |

## 8. Keep it current

The schedule refreshes the caches; a refresh that finds nothing new writes no version and redelivers nothing. When a
reference record changes, the refresh tags exactly the delivered records built from it: under `onChange: auto` they
are delivered again on their flow's next run, and under `onChange: approve` (LogCurveType here) they wait on the Cache
page's Deliveries tab until someone approves or rejects the change ([The partition
cache](../concepts/partition-cache.md)).

Each refresh also prunes the records of the versions the partition no longer needs: a replaced version keeps them for 7
days, or for the cache flow's `retentionDays`, and stays listed on the History tab with what it changed. Set
`retentionDays: 30` on the flow to keep a month of history to compare against ([retentionDays](../flow/cache.md#retentiondays)).

Several cache flows may fill one partition, from different projects: a type declared by two of them is one type in the
cache, holding every path either declares. Two declarations that disagree (another entity type, or one field name from
two paths) are refused; make them agree or rename one.

## Related

- [Cache flow](../flow/cache.md): every key, and the other origins.
- [Getting your own lookup table into the cache](lookup-table-cache.md): translating source spellings before resolving.
- [Mapping lookups](../flow/mapping-lookups.md) and [modifiers](../flow/mapping-modifiers.md): `$cache`, `$findBy`,
  `$findAll`, `ref` and `id` in full.
- [`sqlflow cache`](../cli/cache.md): listing versions, and importing type files where no platform is reachable.
