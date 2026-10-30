---
id: delivery-guide-multi-kind-source
title: "Delivering wellbores and their well logs from one source: several OSDU kinds with interfaces"
type: guide
summary: "Build one delivery flow with two interfaces, wellbores then well logs, where the log's reference to its wellbore decides the order and makes logs wait."
keywords:
  - multiple kinds one source
  - interfaces walkthrough
  - wellbore and well log
  - parent and child records
  - interface order
  - wellboreid reference
  - idfrom key
  - ref modifier
  - waiting records
  - consolidate flows
  - ledger adoption
  - run one interface
related:
  - delivery-flow-interfaces
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-flow-mapping-modifiers
  - delivery-cli-check
  - delivery-cli-run
  - delivery-concept-record-lifecycle
  - guide-table-to-table-ingestion
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/InterfaceOrder.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/InterfaceSchemas.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/ReferenceReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Identity/TargetId.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Waits.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/samples/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
---

# Delivering wellbores and their well logs from one source: several OSDU kinds with interfaces

This guide builds one delivery flow for the well database `welldb` that delivers two OSDU kinds: its wellbores as
`osdu:wks:master-data--Wellbore:1.3.0` records, and its well logs as
`osdu:wks:work-product-component--WellLog:1.4.0` records that point at their wellbore. The two are interfaces of one
source: one document, one connection and target, one schedule and one run, with a ledger per interface. Nobody writes
down that logs go after wellbores; the run works it out from the reference the well log mapping makes to the wellbore,
and a log whose wellbore has not landed yet waits for it.

It assumes the [first delivery](getting-started.md) is familiar: the pre and ing flows, a template folder, the partition
`dev`, `sqlflow check` and `sqlflow run`.

## 1. The ingestion tables

The delivery reads three keyed tables of the ingestion database `OsduData`, each loaded by SQLFlow's own pre-ingestion
and ingestion flows ([table-to-table ingestion](../../../../sqlflow/docs/reference/guides/table-to-table-ingestion.md)):

| Table | Key | Holds |
| --- | --- | --- |
| `OsduData.silver.Wellbore` | `wellbore_id` | one row per wellbore: `wellbore_name`, `wellbore_uwi`, ... |
| `OsduData.silver.WellLog` | `log_id` | one row per log: `log_name`, `log_source`, ..., and `wellbore_id`, the wellbore it was recorded in |
| `OsduData.silver.WellLogCurve` | `log_id`, `curve_id` | one row per curve of a log: `curve_mnemonic`, `curve_unit`, ... |

Each ing flow sets `target.identityColumn: RecId`, which gives the table the integer identity primary key a delivery pages
and fans out by, and `load.keyColumns` to the key above:

```yaml
flowType: ing
name: welldb-welllog-02-header-ing
batch: welldb

connections:
  osduData: ${env:OSDU_DATA_DB}

source:
  server: osduData
  object: OsduData.pre.v_WellLog

target:
  server: osduData
  object: OsduData.silver.WellLog
  identityColumn: RecId

load:
  keyColumns: [log_id]

systemColumns:
  insertedDate: true
  updatedDate: true
```

What matters for this guide is the column `wellbore_id` on the well log table: the well log row says which wellbore it
belongs to, in the wellbore table's own key.

## 2. Give the wellbore an id the well log can name

A record's OSDU id is `{partition}:{entityType}:{unique}`, and by default the unique part is the delivery key, a UUID
derived from the mapping's `dataset.system` and key values. No other mapping can compute it. `dataset.idFrom: key` makes
the unique part the key's own value instead, so the wellbore `WB-0001` is delivered as
`dev:master-data--Wellbore:WB-0001`, an id the well log mapping can build from its own row. The wellbore mapping of the
[first delivery](getting-started.md#step-5-write-the-mapping) is written that way already; this is the same file,
`mappings/Wellbore@1.0.0.yaml`:

```yaml
documentType: mapping
name: Wellbore
version: 1.0.0
template:
  kind: osdu:wks:master-data--Wellbore:1.3.0
  version: 58d6bdbd9d066a06
description: Wellbores from the well database, one record per wellbore.

dataset:
  system: welldb
  key: [wellbore_id]
  idFrom: key
  label: "{wellbore_name}"
  identity: [wellbore_name, wellbore_uwi]

parameters:
  dataPartition:
    required: true
    description: The OSDU data partition record ids are minted in.
  aclOwner:
    required: true
    description: The entitlements group that owns every record (acl.owners).
  aclViewer:
    required: true
    description: The entitlements group that may read every record (acl.viewers).
  legalTag:
    required: true
    description: The legal tag every record carries (legal.legaltags).

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    FacilityName:
      $from: wellbore_name
      $modifiers: [trim]
    FacilityID: { $from: wellbore_id }
    NameAliases:
      - AliasName: { $from: wellbore_uwi }
        AliasNameTypeID: "{$param.dataPartition}:reference-data--AliasNameType:UniqueIdentifier:"
```

Choose this before the first wellbore is delivered: a record keeps the OSDU id it first claimed, and a mapping whose
`idFrom` changes later holds every record it would move, naming both ids (a changed system or key makes new records of
the ledger instead). Before a record first claims an
id made from its key, the intake asks OSDU whether a record already sits there; one that no record of the ledger claimed
is another system's, and the record is held rather than writing over it until an operator adopts it
([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)).

## 3. Point the well log at its wellbore

The WellLog template declares `data.WellboreID` as a relationship (`x-osdu-relationship`) to `master-data--Wellbore`,
with the pattern `^[\w\-\.]+:master-data\-\-Wellbore:[\w\-\.\:\%]+:[0-9]*$`. The `ref` modifier builds exactly that
reference from a value: `{$param.dataPartition}:master-data--Wellbore:{$value}:`, the entity type taken from the
relationship ([modifiers](../flow/mapping-modifiers.md)). The well log mapping, `mappings/WellLog@1.0.0.yaml`, writes
`WellboreID` that way:

```yaml
documentType: mapping
name: WellLog
version: 1.0.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
description: Well logs from the well database, one record per log and one curve item per curve row.

dataset:
  system: welldb
  key: [log_id]
  label: "{wellbore_id} / {log_name}"
  identity: [log_id, wellbore_id]

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
  tags:
    DeliveredBy: welldb
  data:
    Source: welldb
    Name:
      $from: log_name
      $modifiers: [trim]
    WellboreID:
      $from: wellbore_id
      $modifiers: [ref]
    LogSource: { $from: log_source }
    TopMeasuredDepth: { $from: top_depth }
    BottomMeasuredDepth: { $from: base_depth }
    ReferenceCurveID: { $from: index_curve }
    SamplingDomainTypeID:
      $from: sampling_domain
      $modifiers:
        - replace: $cache.SamplingDomain
        - ref
    Curves:
      $forEach: curves
      $item:
        CurveID: { $from: curve_id }
        Mnemonic: { $from: curve_mnemonic }
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias
            - ref
```

A log of wellbore `WB-0001` renders `"WellboreID": "dev:master-data--Wellbore:WB-0001:"`. Without its trailing `:`
(the version separator) that is the id the wellbore interface delivers to, which is what lets the run line the two up.
Write both templates into the template folder first, and commit them with the mappings
([templates](../concepts/templates.md)):

```bash
sqlflow template capture --kind osdu:wks:master-data--Wellbore:1.3.0 --out welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json
sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

The mapping also reads two lookup tables from the partition's cache, `UnitAlias` for the curves' units and the
`SamplingDomain` dictionary, so a cache flow that declares them has to have refreshed the partition before the first
run ([your own lookup tables](lookup-table-cache.md)).

## 4. One document, two interfaces

The source document, `flows/welldb-03-delivery.yaml`, says once what both kinds share, and under `interfaces` what
each delivers:

```yaml
flowType: delivery
name: welldb-03-delivery
batch: welldb
description: The well database's wellbores and well logs, delivered as one source.

partitions: [dev, test]

source:
  connection: ${env:OSDU_DATA_DB}
  lastModified: update_date
  work: ../.work/welldb

target:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}

parallelism:
  recordsAtOnce: 8
  interfacesAtOnce: 2

failWhen:
  failedPercent: 20

interfaces:
  wellbores:
    description: One master-data--Wellbore record per wellbore.
    record:
      object: OsduData.silver.Wellbore
      key: [wellbore_id]
      primaryKey: RecId
    mapping: Wellbore@1.0.0

  welllogs:
    description: One work-product-component--WellLog record per log, referring to its wellbore.
    record:
      object: OsduData.silver.WellLog
      key: [log_id]
      primaryKey: RecId
    datasets:
      curves:
        object: OsduData.silver.WellLogCurve
        join:
          log_id: log_id
        orderBy: [curve_id]
    mapping: WellLog@1.0.0
    failWhen:
      consecutiveFailures: 50

schedule:
  cron: "0 * * * *"
  timezone: UTC
  values:
    partition: dev
```

- **Shared:** the connection, the work folder, `lastModified` (both tables carry `update_date`), the target and its
  credentials, `reliability` and `failWhen`. Neither interface names a route: neither declares `files` or `bulk`, so both
  write through the storage service.
- **Per interface:** the record table, the child table, the mapping, and for `welllogs` a stop rule of its own laid over
  the shared one.
- **No `after:`.** The order comes from the mapping. `after:` is for an order the templates do not show.
- **No `render.parameters`.** The mappings' `aclOwner`, `aclViewer` and `legalTag` take the kind's defaults
  (`${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}`, `${env:OSDU_LEGAL_TAG}`), and `dataPartition` is the partition each
  run targets ([delivery flow](../flow/delivery.md#render)).

Validate the repository folder offline, the flows and the mappings beside them:

```bash
sqlflow validate .
```

```text
OK      flows\welldb-03-delivery.yaml  (delivery 'welldb-03-delivery')
OK      flows\welldb-welllog-02-header-ing.yaml  (ing 'welldb-welllog-02-header-ing')
OK      mappings\Wellbore@1.0.0.yaml  (mapping 'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0')
OK      mappings\WellLog@1.0.0.yaml  (mapping 'WellLog@1.0.0 -> osdu:wks:work-product-component--WellLog:1.4.0')
4 valid, 0 broken of 4 document(s) under ...\welldb.
```

## 5. See the order before anything runs

`sqlflow check` resolves each interface's mapping against the template its template folders hold and works out the order the run will take
([sqlflow check](../cli/check.md)):

```bash
sqlflow check flows/welldb-03-delivery.yaml --partition dev
```

```text
welldb-03-delivery: 2 of 2 interface(s) checked, in the order they run: wellbores then welllogs
OK  welldb-03-delivery/wellbores (...)
    ledger      welldb-03-delivery/wellbores@dev
    route       storage: interfaces.wellbores declares no files and no bulk, so each record is written through the storage service
    wave        1
    waits for   nothing
    ...
OK  welldb-03-delivery/welllogs (...)
    ledger      welldb-03-delivery/welllogs@dev
    route       storage: interfaces.welllogs declares no files and no bulk, so each record is written through the storage service
    wave        2
    waits for   wellbores: osdu.data.WellboreID refers to master-data--Wellbore, which wellbores delivers (osdu:wks:master-data--Wellbore:1.3.0)
    ...
```

Each interface keeps its own ledger, `<flow>/<interface>`, here in the `dev` partition. Add `--connect` to open the
ingestion tables as well and see how many rows each would read.

## 6. Plan, then deliver

```bash
sqlflow run flows/welldb-03-delivery.yaml --set partition=dev --operation plan
sqlflow run flows/welldb-03-delivery.yaml --set partition=dev
```

A run checks both interfaces before it plans anything (the ledger, mappings, templates, routes, tables, the service and
the legal tag), then runs them in waves: `wellbores` alone in the first, `welllogs` in the second. Each interface plans,
fans out and drains as a flow of its own would, under its own submission. The run's result lists each interface with its
route, wave, what it waited for and how it ended ([running an OSDU flow](../cli/run.md)).

## 7. When a wellbore is held

Waves order the interfaces, not every record. Say wellbore `WB-0001` (named `Wellbore A-1`) is held while its logs render
fine. When a worker claims such a log, the id its `WellboreID` holds belongs to a record of the ledger that has not
landed, so the log is left **waiting** rather than sent with a reference to nothing:

```text
waits for dev:master-data--Wellbore:WB-0001 (data.WellboreID): record 'Wellbore A-1' of welldb-03-delivery/wellbores holds it and has not delivered it yet (held)
```

Waiting charges the log no attempt. Fix the wellbore's row (or release the wellbore), and when the wellbore lands every
log waiting for it goes back to pending, for the next claim of the `welllogs` interface to send. A log whose `wellbore_id` names a wellbore no record of the ledger
holds is not waited for: that wellbore is taken to be OSDU's already, or another system's. To refuse such references
instead, set `target.verifyReferences: storage` ([preflight](../concepts/preflight.md#references-checked-in-storage)).
Statuses and releases are on [record lifecycle](../concepts/record-lifecycle.md).

## 8. Run one interface

A payload `interfaces` list runs only those interfaces. The interfaces they depend on are not run; their records are read
from the ledger as they are, and the waits above still apply:

```bash
sqlflow run flows/welldb-03-delivery.yaml --set partition=dev --payload '{"interfaces":["welllogs"]}'
```

A run on chosen records, or a redelivery, names its interface with `interface` in the payload, and the module's verbs
take `--interface` (`sqlflow records list flows/welldb-03-delivery.yaml --interface welllogs --partition dev`).

## 9. Bring existing flows into the source

If wellbores and logs were delivered so far by two single-form flows (`welldb-wellbore-03-delivery` and
`welldb-welllog-03-delivery`), adopt their ledgers rather than starting new ones, so nothing that has not changed is sent
again:

```yaml
interfaces:
  wellbores:
    ledger: welldb-wellbore-03-delivery
    record:
      object: OsduData.silver.Wellbore
      key: [wellbore_id]
      primaryKey: RecId
    mapping: Wellbore@1.0.0
```

Then remove the old flow documents. While two flows keep one ledger, the repository sync warns, naming both; and two
flows delivering the same rows under ids made from the key would claim the same OSDU ids, the second being held. Moving
a flow into partitions changes ledger names too; `keepLedger` keeps the old one for one partition
([partitions](../concepts/partitions.md)).

## 10. Decide when an interface gives up

A log with bad data is held and the run goes on. The document's `failWhen.failedPercent: 20` stops an interface once a
fifth of the records it settled (after the first 100) were held or failed, and the outage rule, on by default, stops it
after 25 records in a row could not reach OSDU or were refused. `welllogs` adds `consecutiveFailures: 50`. If `wellbores`
stops, `welllogs` is skipped (`it waits for wellbores, which did not complete`), the run ends failed, and the next run
picks up where it stopped ([when an interface stops](../flow/interfaces.md#when-an-interface-stops)).

## What you built

One flow, `welldb-03-delivery`, that delivers a source's wellbores and well logs in the order their references need,
with a ledger per kind, logs that wait for their wellbore, and stop rules per interface. More kinds of the same source
(trajectories, documents) are more entries under `interfaces`; their routes follow from what each declares
([routes](../flow/routes.md)).

## See also

- [A source with interfaces](../flow/interfaces.md): every key, the order rules and cycles.
- [Delivery flow](../flow/delivery.md): the keys both forms share.
- [Mapping](../flow/mapping.md) and [modifiers](../flow/mapping-modifiers.md): `dataset.idFrom` and `ref`.
- [Record lifecycle](../concepts/record-lifecycle.md): waiting, held and released records.
