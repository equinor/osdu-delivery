---
id: delivery-flow-ddms
title: "DDMSs a delivery reaches (target.ddms): routing a record to its DDMS, and Wellbore DDMS bulk data"
type: flow-reference
summary: "Which DDMS a record goes to: target.ddms, ddmsRoot, a DDMS named by its registration, the Wellbore DDMS, its bulk data, sessions and chunk ceilings."
keywords:
  - target.ddms
  - ddms route
  - wellbore ddms
  - ddmsroot
  - which ddms
  - bulk data
  - bulk session
  - bulk link
  - bulkuri
  - parquet chunk
  - maxchunkcolumns
  - maxchunkvalues
  - sessiondatapath
  - sessioncommitpath
  - ddms registration
  - wellboreddmsv3
yamlPath: "target.ddms"
related:
  - delivery-flow-routes
  - delivery-flow-ddms-services
  - delivery-guide-bulk-data
  - delivery-concept-protocols
  - delivery-flow-delivery
  - delivery-flow-interfaces
  - delivery-cli-check
  - delivery-concept-removal-and-reversal
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/DdmsCatalog.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/WellboreDdmsBulkLimits.cs
  - osdu/src/SqlFlow.Delivery/Model/WellboreDdmsSessionChunks.cs
  - osdu/src/SqlFlow.Delivery/Engine/DdmsRouting.cs
  - osdu/src/SqlFlow.Delivery/Engine/DdmsDiscovery.cs
  - osdu/src/SqlFlow.Delivery/Engine/RouteChecks.cs
  - osdu/src/SqlFlow.Delivery/Engine/RecordRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/WellboreDdmsV3Shape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsRules.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/specs/wellbore-ddms/openapi.json
  - osdu/specs/core/register/openapi.yaml
---

# DDMSs a delivery reaches (target.ddms): routing a record to its DDMS, and Wellbore DDMS bulk data

A domain data management service (DDMS) keeps data of its own beside an OSDU record: a well log's curves, a sample
analysis's tables, a series of production points, a seismic dataset's files, a reservoir's rows. The `ddms`,
`fileAndDdms` and `manifestAndDdms` routes ([Routes](routes.md)) send each record to the DDMS serving its entity type,
then the data that DDMS keeps for it. This page says which DDMS a record goes to, what `target.ddms` declares, and how the
Wellbore DDMS is sent a record and its bulk data, with the ceilings a bulk chunk is held to. The other DDMS shapes (Well
Delivery, RAFS, the production historian, Seismic Store, Reservoir Management) and the services reached by routes of
their own (the Reservoir DDMS over ETP, the Production DDMS core service) or configured by records (External Data
Services) are on [DDMS shapes and services](ddms-services.md).

## Where a record goes

Every record id names its entity type (`dev:work-product-component--WellLog:...`), so a delivery, a verify, a read and a
removal all route from the id alone. The record goes to the first DDMS that serves its entity type, in this order:

1. The DDMSs under `target.ddms`, in the order the document declares them.
2. The Wellbore DDMS under `target.protocolOptions.ddmsRoot` (usually `/api/os-wellbore-ddms`).
3. In a flow in the single form that declares neither, the Wellbore DDMS is the endpoint itself.

A record goes to one DDMS, so the loader refuses an entity type two DDMSs under `target.ddms` serve:

```text
target.ddms serves master-data--Well from both 'wellbore' and 'welldelivery'. A record goes to one DDMS, so list the collections of one of them under its collections, leaving that entity type out.
```

The run's preflight and `sqlflow check` refuse a mapping whose kind no DDMS the flow reaches serves, and a bulk part sent
to a collection that holds records alone. `sqlflow check` says where each flow's records go:

```text
flows/welldb-trajectory-03-delivery.yaml: no DDMS this flow reaches serves work-product-component--WellboreTrajectory: it reaches the DDMS 'wellbore' (/api/os-wellbore-ddms), serving work-product-component--WellLog, master-data--Wellbore. Declare the DDMS that serves it under target.ddms, add the collection to one declared there, or name its paths under target.protocolOptions. (render.mapping WellboreTrajectory@1.0.0 renders osdu:wks:work-product-component--WellboreTrajectory:1.3.0.)
```

A flow that names its own paths (`recordPath`, `dataPath`, `sessionPath` and the rest, for a facade in front of the
Wellbore DDMS) has them used as written, and the collection serving its records' entity type supplies the paths it leaves
out. Paths of its own are refused for records that go to a DDMS of any other shape, which says every call it takes.

| `target.protocolOptions` key | The call | Default on a Wellbore DDMS collection |
| --- | --- | --- |
| `recordPath` | The record write. Naming it makes the flow's paths its own: a type no DDMS the flow reaches serves then needs every path below it uses. | `{collection}` (`{root}/ddms/v3/welllogs`) |
| `verifyPath`, `deletePath` | Reads one record back; removes it at the reversible scope. | `{collection}/{id}` |
| `dataPath` | The bulk data in one request (and, with `describe=true`, its description). | `{collection}/{id}/data` |
| `sessionPath` | Opens a bulk session, and lists a record's sessions for an undo. | `{collection}/{id}/sessions` |
| `sessionDataPath` | Sends one chunk of a session; `{sessionId}` is the session. | `{collection}/{id}/sessions/{sessionId}/data` |
| `sessionCommitPath` | Commits or abandons a session (`PATCH`), and reads its state (`GET`). | `{collection}/{id}/sessions/{sessionId}` |

A record with bulk data whose type no DDMS the flow reaches serves, under paths of the flow's own, is refused naming the
bulk paths it leaves out: `... so there is nowhere to send its bulk data. Name dataPath, sessionPath, sessionDataPath,
sessionCommitPath under target.protocolOptions as well.`

## The endpoint: the platform root, or the DDMS itself

Normally `target.endpoint` is the OSDU platform root and each DDMS sits under it: `ddmsRoot` for the Wellbore DDMS, or
`root` for a DDMS under `target.ddms`. Every DDMS of a source with interfaces names its root (or its registration).
`ddmsRoot` is a path starting with `/`, and is refused on any route that reaches no DDMS:

```text
target.protocolOptions.ddmsRoot only applies to the ddms route; the storage route reaches its services under the endpoint already.
```

A flow in the single form may instead point `target.endpoint` at the Wellbore DDMS itself, by declaring neither
`ddmsRoot` nor `target.ddms`. The DDMS's paths carry no `/api/<service>/` prefix, so such a flow does not reach the
storage or legal services by a path, and says where they are as whole URLs where it needs them:

```yaml
target:
  endpoint: ${env:OSDU_URL}            # the Wellbore DDMS itself
  protocol: ddms
  protocolOptions:
    legalValidatePath: https://osdu.example.com/api/legal/v1/legaltags:validate
    purgePath: https://osdu.example.com/api/storage/v2/records/{id}
    purgeVersionsPath: https://osdu.example.com/api/storage/v2/records/{id}/versions
```

Without `legalValidatePath` its runs log that the legal tags were not checked; without the purge paths its `history`
scope, and the `everything` scope of a records-only collection, are refused. A reversal cannot write its records back,
since the storage service that keeps their versions is not under the endpoint. Nor can such a flow pin a mapping whose
[enrichments](enrich.md) add records of their own: those go through the platform's Dataset and storage services, so the
flow is refused until its endpoint is the platform and `ddmsRoot` names the DDMS under it.

## target.ddms

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: ddms
  ddms:
    wellbore:
      root: /api/os-wellbore-ddms
      shape: wellboreDdmsV3
      collections:
        work-product-component--WellLog: { path: welllogs, bulk: true, columns: curveIdsAndWidths }
        master-data--Wellbore: { path: wellbores }
    rafs:
      root: /api/rafs-ddms
      shape: rafsV2
    historian:
      root: /api/pddms/ingest/v1
      shape: productionTimeSeriesV1
      queryRoot: /api/pddms/query/v1
      settleSeconds: 60
      pollSeconds: 5
      maxRequestBytes: 8000000
```

Each key under `target.ddms` names a DDMS (a letter, then letters, digits, `_` and `-`, at most 64 characters, unique
regardless of case); an empty map is refused. `target.ddms` is taken only by the routes that reach a DDMS, and in a
source with interfaces only when an interface goes through one.

| Key | Default | Meaning |
| --- | --- | --- |
| `shape` | `wellboreDdmsV3` | The DDMS's call pattern: `wellboreDdmsV3`, `wellDeliveryV1`, `rafsV2`, `productionTimeSeriesV1`, `seismicStoreV3` or `reservoirManagement`. The shape decides which other keys the DDMS takes; a key of another shape is refused. |
| `root` | the endpoint itself | Where the DDMS is under the endpoint, a path starting with `/`. Required for `productionTimeSeriesV1`, `seismicStoreV3` and `reservoirManagement`, whose records go through the storage service under the same platform, and for every DDMS of a source with interfaces unless it names `register`. A DDMS without a root makes the endpoint that DDMS, so it must be the only one the flow reaches. |
| `collections` | the shape's own | The entity types (with their group, `work-product-component--WellLog`) the DDMS serves. Each takes `path`, the collection's path segment; `bulk`, whether it keeps bulk data beside its records (default `false`); `columns`, what the bulk data's columns are checked against (`unchecked`, `curveIds`, `curveIdsAndWidths` or `trajectoryStations`, only on a bulk collection of a Wellbore DDMS); and `typedContent`, a RAFS content collection holding several content types. The historian takes none. |
| `register` | none | The id a Wellbore DDMS is registered under in the Register service; [below](#a-ddms-named-by-its-registration). |

The Wellbore DDMS's keys are [below](#the-wellbore-ddms-wellboreddmsv3); the keys of every other shape are on
[DDMS shapes and services](ddms-services.md).

### A DDMS named by its registration

A DDMS of the `wellboreDdmsV3` shape can be named by its Register service registration instead of declared:

```yaml
target:
  ddms:
    partner:
      register: partner-wdms          # 2 to 50 letters, digits and '-'
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms
```

What the flow leaves out is read from the registration (`GET /api/register/v1/ddms/{id}` under the endpoint, or
`protocolOptions.registerPath`) once per run, when the flow's protocol is built: the root from the one server its
registered OpenAPI documents name, and the collections from their retrieval operations (`x-ddms-retrieve-entity`). A
collection the Wellbore DDMS shape knows by its path takes that shape's entity type and rules. A missing registration
(404), a registration naming several servers for a DDMS without a declared root, and a retrieval path that is not a
collection of the shape fail the run, naming the registration. The lookup by type (`GET /ddms?type=`) is not used: its
`type` pattern cannot carry an entity type with its group. `register` is refused on any other shape, and on a DDMS that
declares both its root and its collections.

## The Wellbore DDMS (wellboreDdmsV3)

The Wellbore DDMS v3 serves nine collections. Four keep bulk data beside their records; five hold records alone:

| Entity type | Collection | Bulk data | Columns checked against |
| --- | --- | --- | --- |
| `work-product-component--WellLog` | `welllogs` | yes | `data.Curves[].CurveID`, and each curve's `NumberOfColumns` |
| `work-product-component--WellboreTrajectory` | `wellboretrajectories` | yes | `data.AvailableTrajectoryStationProperties[].Name` |
| `work-product-component--PPFGDataset` | `ppfgdataset` | yes | `data.Curves[].CurveID` |
| `work-product-component--WellPressureTestRawMeasurement` | `wellpressuretestrawmeasurement` | yes | `data.Curves[].CurveID`, and each curve's `NumberOfColumns` |
| `master-data--Well` | `wells` | no | |
| `master-data--Wellbore` | `wellbores` | no | |
| `work-product-component--WellboreMarkerSet` | `wellboremarkersets` | no | |
| `work-product-component--WellboreIntervalSet` | `wellboreintervalsets` | no | |
| `master-data--WellLogAcquisition` | `welllogacquisition` | no | |

**The record.** `POST {root}/ddms/v3/{collection}` with a one-element array. Before anything is sent, the record is
checked against the rules the service applies to its kind, and held naming the rule it breaks: a WellLog's `CurveID`s
are unique and its `ReferenceCurveID` is one of them; a WellboreTrajectory's station property names are unique; a
PPFGDataset names its `ContextTypeID` and `ReferenceWellTrajectoryID`, keeps unique `CurveID`s and a
`PrimaryReferenceCurveID` among them; a WellPressureTestRawMeasurement keeps unique `CurveID`s. With bulk data, every
curve carries a `CurveID`, which the service matches columns by. A value counts as given the way the service's Python
reads it: not null, not empty, not zero, not false.

**The bulk link.** The DDMS writes `data.ExtensionProperties.wdms.bulkURI` on every bulk write and session commit, and
refuses a record write whose bulkURI differs from the one its latest version holds, a write that leaves it out included.
So an update of a record in a bulk collection reads the stored version first and carries its bulkURI, and the
`urn://wdms-1/uuid:` entries the DDMS added to `data.DDMSDatasets`; a bulkURI the mapping renders is replaced by the
DDMS's, with a warning. When the ledger knew no version of the record and the DDMS refuses the write, the record is read
once and written again if the DDMS holds a link after all (an earlier delivery whose answer was lost). A records-only
collection keeps no link, and its records are written without a read. `preserveDataKeys` need not name
`ExtensionProperties` or `DDMSDatasets` for the link to be kept.

**The bulk data.** The payload's files are the chunks, sent in file name order as `payloadContentType` (default
`application/x-parquet`; the service also takes `application/json` in pandas' split form):

- **One chunk** goes in one request, `POST {collection}/{id}/data`, which replaces the whole bulk. Only one chunk ever goes
  this way, since several sent to it would overwrite each other.
- **More than one chunk** (or `sessionThresholdChunks: 0`, which sends even one chunk through a session) opens a session
  (`POST {collection}/{id}/sessions` with `mode: overwrite`, the version the record is at, and a time to live of 1440
  minutes), sends each chunk in order (`POST .../sessions/{sessionId}/data`) and commits it (`PATCH .../sessions/{sessionId}`
  with `state: commit`), which aggregates the chunks into one new version. Any failure abandons the session. Session
  create, chunks and commit are never resent blind: a chunk sent twice lands twice, so a chunk whose outcome is unclear
  fails the session, and the next try opens a new one. A commit whose outcome is unclear (a lost connection, a 5xx, a
  409 or 412) is settled by reading the session's state: `committed` is the commit that worked.
- After the bulk lands, the record is read back for the version the DDMS now serves, since the bulk write creates a new
  version. After a session, the committed bulk is described (`GET {collection}/{id}/data?describe=true`), and fewer rows
  or columns than the chunks carried holds the record. A target that cannot describe its bulk leaves the delivery
  unchecked, with a warning.

`sessionThresholdChunks` is 1 (the default) or 0; anything else is refused when the flow is read:

```text
target.protocolOptions.sessionThresholdChunks must be 1 (a single chunk goes straight to the bulk endpoint, more open a session) or 0 (always open a session). The bulk endpoint replaces the whole bulk on every write, so several chunks sent to it would overwrite each other.
```

**Checked from the chunks' parquet footers before the first request** (never their rows):

- Every column belongs to the record: a WellLog, PPFGDataset or WellPressureTestRawMeasurement column is a
  `data.Curves[].CurveID` (a column labelled `NAME[...]` is one column of the array curve `NAME`), with as many columns per
  curve as its `NumberOfColumns` (1 when not given) where the collection checks widths; a WellboreTrajectory column is a
  `data.AvailableTrajectoryStationProperties[].Name`.
- A session aggregates its chunks by row label, so two chunks that give the same labels to different rows would lose rows
  while the commit still succeeds. Chunks that split a log's rows need labels that continue from one chunk to the next;
  chunks with exactly the same labels and different curves (a log whose curves were split) go together.
- A session's chunks carry the reference curve (`data.ReferenceCurveID`, or the measured depth station of a trajectory)
  as a column, not only as the dataframe's row index, or the service refuses the commit after every chunk was accepted.
- A parquet file whose footer will not read, or whose pandas metadata a dataframe reader cannot read, holds the record.

What needs the rows (a monotonic reference, sampling bounds) is the service's to check.

### The bulk ceilings

Every chunk is also checked against ceilings before the first request, so an oversized chunk holds the record instead of
failing after the record write has landed:

| Ceiling | Where it comes from | Declared as |
| --- | --- | --- |
| Request body bytes | The estate: the ingress, the API gateway, the service's host. Raise it where it is configured. | `reliability.maxRequestBodyBytes` (default 0, not declared) |
| 10,000,000 values per chunk (rows times columns) | The Wellbore DDMS itself; it cannot be raised. | `target.protocolOptions.maxChunkValues` (default 10000000) |
| 3,000 columns per chunk | The Wellbore DDMS itself: 500 on OSDU M23 and M25, 3,000 from M26. | `target.protocolOptions.maxChunkColumns` (default 3000) |

The service's OpenAPI description of the bulk write says data above "10 millions values or > 3000 columns" goes through
the chunking (session) APIs. The two numbers bound the frame the service materialises, not the request body, so a chunk
small enough to send can still be too large to accept, and the service does not reliably reject it: exceeding one shows
up as a slow write or an out-of-memory worker. The shape is read only when the payload content type is parquet; 0 turns a
ceiling off.

```text
payload chunk 0 (chunk_000.parquet) has 3412 columns, above the wellbore DDMS ceiling of 3000 columns per chunk (openapi wellbore_ddms, POST /ddms/v3/welllogs/{record_id}/data; target.protocolOptions.maxChunkColumns); split the curves across chunks in prepare
```

**Removal.** The reversible scope is the DDMS's logical `DELETE {collection}/{id}`. Everything is
`DELETE {collection}/{id}?purge=true` on a bulk collection, which purges the record and its bulk data; a records-only
collection's DELETE is logical only, so everything goes to the storage service's purge. The DDMS has no operation on a
record's versions, so the history scope goes to the storage service's version purge.

**Probe.** `GET {root}/about`.

## Errors

```text
target.ddms.wellbore names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS. Give every DDMS its root under the endpoint, or declare that one alone.
target.ddms.rafs.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape. Declare the root of a DDMS of the rafsV2 shape, and its collections where they differ from the ones its shape serves.
target.ddms.wellbore.mirror describe a Well Delivery DDMS deployment, and target.ddms.wellbore has the wellboreDdmsV3 shape. Remove them, or declare shape: wellDeliveryV1.
target.ddms.wellbore.collections.work-product-component--WellLog.columns says what bulk data columns are checked against, and the collection holds records alone (bulk is false).
```

Each message is prefixed with the flow file's path.

## See also

- [DDMS shapes and services](ddms-services.md): Well Delivery, RAFS, the production historian, Seismic Store, Reservoir
  Management, External Data Services, the Reservoir DDMS over ETP and DSPDM.
- [Routes](routes.md): the `ddms`, `fileAndDdms` and `manifestAndDdms` routes and their options.
- [Well logs with their curves](../guides/bulk-data.md): a WellLog record and its bulk data, step by step.
