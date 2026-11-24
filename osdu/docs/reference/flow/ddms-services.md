---
id: delivery-flow-ddms-services
title: "DDMS shapes and services: Well Delivery, RAFS, production historian, Seismic Store, Reservoir Management, ETP, DSPDM, EDS"
type: flow-reference
summary: "What each DDMS shape and service is sent, checked and removed: Well Delivery, RAFS, historian, Seismic Store, Reservoir Management, ETP, DSPDM, EDS."
keywords:
  - well delivery ddms
  - welldeliveryv1
  - rafs
  - rock and fluid samples
  - production historian
  - production time series
  - seismic store
  - seismic dataset
  - reservoir management ddms
  - reservoir ddms
  - target.etp
  - target.dspdm
  - dspdm business objects
  - external data services
  - target.eds
yamlPath: "target.ddms.<name> (shapes other than wellboreDdmsV3), target.etp, target.dspdm, target.eds"
related:
  - delivery-flow-ddms
  - delivery-flow-routes
  - delivery-concept-protocols
  - delivery-flow-delivery
  - delivery-concept-removal-and-reversal
  - delivery-concept-artifacts-and-undo
  - delivery-cli-check
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/FlowMapper.Ddms.cs
  - osdu/src/SqlFlow.Delivery/Documents/FlowMapper.DdmsCollections.cs
  - osdu/src/SqlFlow.Delivery/Documents/FlowMapper.Target.cs
  - osdu/src/SqlFlow.Delivery/Model/DdmsCatalog.cs
  - osdu/src/SqlFlow.Delivery/Model/EtpTarget.cs
  - osdu/src/SqlFlow.Delivery/Model/DspdmTarget.cs
  - osdu/src/SqlFlow.Delivery/Engine/EdsRecordRules.cs
  - osdu/src/SqlFlow.Delivery/Engine/RecordRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/WellDeliveryShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/RafsShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/ProductionTimeSeriesShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/TimeSeriesPoints.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/SeismicStoreShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/ReservoirManagementShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDspdmProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Dspdm/DspdmCatalog.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduEtpProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Etp/EtpObjectXml.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Etp/EtpArrays.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/specs/production-dspdm/swagger-api.json
---

# DDMS shapes and services: Well Delivery, RAFS, production historian, Seismic Store, Reservoir Management, ETP, DSPDM, EDS

Beside the Wellbore DDMS, a DDMS under `target.ddms` can have one of five other shapes, each a call pattern of its own:
the Well Delivery DDMS, the Rock and Fluid Sample DDMS, the production historian, Seismic Store and the Reservoir
Management DDMS. Which DDMS a record goes to, what `target.ddms` declares and the Wellbore DDMS are on
[DDMSs a delivery reaches](ddms.md); this page says what each other shape is sent, checked and removed. It also covers
the two services reached by routes of their own (the Reservoir DDMS over ETP, the Production DDMS core service) and
External Data Services, which no route writes to but whose configuration records a flow delivers.

## Well Delivery DDMS (wellDeliveryV1)

The Well Delivery DDMS keeps well planning and drilling entities in a store of its own, and copies each into Storage where
the deployment says so. It holds records alone. By default it serves the entity types its brief lists (`Well`,
`Wellbore`, `WellPlanningWell`, `WellPlanningWellbore`, `WellboreTrajectory`, `WellLog`, `PPFGDataset`,
`WellboreMarkerSet` and others), so a flow that also sends well logs to the Wellbore DDMS lists the Well Delivery
collections it wants; each is served under its type, lowercased, so no `path` is given.

```yaml
target:
  ddms:
    welldelivery:
      root: /api/well-delivery
      shape: wellDeliveryV1
      mirror: true
      provider: azure
      concurrency: 1
      collections:
        master-data--WellPlanningWell: {}
        master-data--WellPlanningWellbore: {}
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms
```

| Key | Default | Meaning |
| --- | --- | --- |
| `mirror` | true | Whether the deployment copies every entity into Storage. The copy's id is kept, and a removal takes the copy through Storage too, since the DDMS never deletes it. |
| `provider` | none | `azure`, `aws`, `gc` or `ibm`. On `ibm` an entity is never written again under a version it already has, since that store refuses the second save. |
| `concurrency` | 1 | Writes one node sends to the deployment at once, 1 to 16. Its Mongo and Cosmos stores keep the current collection in shared state. |

Each entity goes alone to `PUT {root}/storage/v1/{type}` under a version the route chooses (a 13-digit
epoch-millisecond value above the one the ledger holds), recorded as step `version` before the write, so a retry sends
the same version and the DDMS replaces it in place. The service answers 201 whether or not the entity passed its schema
check; its findings are kept on the attempt (`wellDelivery.schemaFindings`) and the record is delivered with a warning.
References to entities this DDMS holds are sent with the version it holds, because its index takes only versioned
references; one it does not hold yet is sent as rendered and named on the attempt (`wellDelivery.unpinned`). Removal:
the reversible scope soft-deletes every version (`DELETE .../{entityId}`) and the Storage copy; everything purges both
(`DELETE .../{entityId}:purge`, which the service's contract says cannot be undone); the history scope is refused, because other entities' references
cite versions. Probe: `GET {root}/info`.

## Rock and Fluid Sample DDMS (rafsV2)

RAFS writes sample records through Storage and keeps the tabular content of their analyses. Its collections:
`masterdata` (GenericFacility, GenericSite, Sample, SampleAcquisitionJob, SampleChainOfCustodyEvent, SampleContainer)
and `samplesanalysesreport` hold records alone; `samplesanalysis` and `fluidmodel` hold several content types each;
`saturationfunctionset`, `reservoirsimulationrockphysicsmodel` and `depthshift` hold one each.

A record goes to `POST {root}/v2/{collection}` in an array, typed exactly `application/json`. Its payload holds the
content tables, one file per content type, named after it: `<contentType>.json`, `<contentType>.parquet`, or
`<contentType>.<schemaVersion>.parquet` for a schema version other than `protocolOptions.contentSchemaVersion` (default
`1.0.0`). Each type and version is checked against the service's catalogue before anything is written, and a depth shift
table must hold exactly one row. Each table is a step (`content-<type>`) posted to `.../{id}/data` (or
`.../{id}/data/{contentType}`), which registers a `dataset--File.Generic` (in RAFS's default dataset mode) and writes
a new record version; the ledger
keeps those dataset ids (`rafs.datasets`), since RAFS never removes them. `data.DDMSDatasets` belongs to RAFS, so a
metadata update carries the URNs the stored record holds. Removal: the reversible scope is RAFS's logical delete and the
storage service's reversible delete of each content dataset; the history scope purges the record's earlier versions
through the storage service, and everything purges the record and its content datasets there. Probe: `GET {root}/info`, then the analysis type catalogue.

## Production historian (productionTimeSeriesV1)

The historian keeps the points of the series a `work-product-component--ProductionValues` record (2.0.0 or later)
defines, one per `data.ProductionMetricValues` entry by its `DDMSDatasetID` and `ParameterKindID`. The record is a
Storage record, written with `PUT /api/storage/v2/records` with its link to its points
(`urn://pddms/production-values/{id}/timeseries`) in `data.DDMSDatasets`.

| Key | Default | Meaning |
| --- | --- | --- |
| `root` | required | Where the ingestion service is under the endpoint (usually `/api/pddms/ingest/v1`). |
| `queryRoot` | `/api/pddms/query/v1` | Where the query service is; accepted points are read back there. |
| `settleSeconds` | 60 | How long a delivery reads accepted points back for, 0 to 3600; 0 delivers on the acceptance alone. |
| `pollSeconds` | 5 | The pause between two reads, 1 to 60. |
| `maxRequestBytes` | 8000000 | The largest request the points are sent in, 10000 to 64000000; a smaller `reliability.maxRequestBodyBytes` bounds it instead. |

The payload holds the points: a wide `.parquet` table (a `timestamp` column and one column per series, named by its
`DDMSDatasetID`) or the ingestion service's own `.json` body (up to 64 MiB). Every point is checked before the record is
written: each series one the record defines, values of its kind, timestamps increasing. Points go to
`POST {root}/production-values/{id}/timeseries` in requests under the size limit, each a step (`points-<n>`) recording
its body's hash and the version each series was accepted under, so a later try sends only what no try recorded. Each
accepted version is read back through the query service until it serves the points sent. Removal: every scope is
Storage's; the historian has no delete for points, so they stay, and the outcome says so. Probe: the `/info` of both
services.

## Seismic Store (seismicStoreV3)

Seismic Store v3 registers a dataset for each `dataset--FileCollection.*` record (by default SEGY, Slb.OpenZGY,
Bluware.OpenVDS and Generic) and keeps its files in the object store of the cloud it runs on. The record is the
dataset's `seismicmeta`, a Storage record; its payload holds the files. The dataset is
`sd://<tenant>/<subproject>/<folder>/<key>`, the key being the part of the record id after its type (letters, digits,
`_`, `.` and `-`).

```yaml
target:
  ddms:
    seismic:
      root: /api/seismic-store/v3
      shape: seismicStoreV3
      subproject: welldb-seismic
      folder: surveys/north
      provider: anthos
      objectStore: https://s3.example.com
      region: us-east-1
      chunkMiB: 32
      readOnly: false
```

| Key | Default | Meaning |
| --- | --- | --- |
| `root` | required | Seismic Store with its version path (usually `/api/seismic-store/v3`, or `/seistore-svc/api/v3` on Azure). |
| `subproject` | required | The subproject the datasets are registered in; an operator provisions it, the route never creates one. |
| `tenant` | the flow's `data-partition-id` | The tenant the datasets are registered under. |
| `folder` | the subproject's root | The folder under the subproject (`surveys/north`). |
| `provider` | the one the service names | `azure`, `gc`, `anthos` or `ibm`: which object store the files go to, and how. |
| `objectStore` | none | The object store's absolute http(s) address. Required on `anthos` and `ibm`; on `gc`, another endpoint than Google's. |
| `region` | `us-east-1` | The region S3 requests are signed for on `anthos` and `ibm`. |
| `chunkMiB` | 32 | 0 to 256: on Azure a single file is cut into blobs of this size (0 keeps it whole); everywhere, the size each part goes up in. |
| `readOnly` | false | Whether a delivered dataset is closed read-only; a later delivery lifts the flag, writes and closes it again. |

The dataset is written under a write lock id the record keeps for every delivery, recorded as step `lock` before its
first use; it is registered (`register`), its files uploaded with credentials the service issues (`upload`, which records
how many objects landed so a later try sends only the rest), then closed with its file metadata (`close`). The record's
version is read from Storage. Removal: the reversible scope soft-deletes the record in Storage and leaves the dataset,
which has no reversible delete; everything deletes the dataset with its files, then purges the record, and is refused on
`gc`, where one dataset's delete removes the files of every dataset in the subproject. Probe: the service status, the
status behind the token, and the subproject.

## Reservoir Management DDMS (reservoirManagement)

The Reservoir Management DDMS keeps copies of nine kinds of records, and the rows of tables below them, in its own
database. The record is a Storage record, written under its own id; the service's own record write (which writes
records without their ids) and delete (which purges) are never called.

| Collection | Entity type | Tables below it |
| --- | --- | --- |
| `estimated-volumes` | `work-product-component--ReservoirEstimatedVolumes` | `estimated-volumes-det` |
| `pvt-properties` | `master-data--FluidSystem` | none |
| `geological-labels` | `work-product-component--GeoLabelSet` | none |
| `petro-properties` | `work-product-component--ReservoirModelScenario` | none |
| `tank-datum` | `work-product-component--AcquiferInterpretation` | `aquifer-datum` |
| `fluid-synthesis` | `work-product-component--FluidSystemCharacterization` | `fluid-synthesis-tank-pvt`, `fluid-synthesis-tank-blackoil` |
| `kr-synthesis` | `work-product-component--PersistedCollection` | `kr-synthesis-rt` and the table below it |
| `phi-k-synthesis` | `work-product-component--PersistedCollection` | `phi-k-synthesis-rt` and the table below it |
| `forecast` | `work-product-component--ProductionValues` | `forecast-fluid`, `forecast-det` and the table below it |

A Kr synthesis and a Phi-K synthesis are the same kind of record, so a DDMS that lists no `collections` sends
`PersistedCollection` records to `phi-k-synthesis`; a flow delivering Kr syntheses lists
`work-product-component--PersistedCollection: { path: kr-synthesis }`. The historian serves `ProductionValues` too, so a
flow declaring both lists the collections of one of them.

```yaml
target:
  ddms:
    reservoir:
      root: /api/rm-ddms                 # required: the prefix the deployment gives the service
      shape: reservoirManagement
      settleSeconds: 60
      pollSeconds: 5
```

The payload holds the rows: `.json` files of the tables below the collection, each an array of rows, each row an object
of its columns and of the tables below it. Everything the service would refuse is checked first. Rows go under the
service's copy of the record, which only its list call creates from records Search serves, so a delivery with rows waits
for the copy (`settleSeconds`, `pollSeconds`, step `sync`). Each row is posted alone, and the keys the service answers
feed the rows below; keys are recorded every 50 rows (`rows-<n>`), so a later try finds what an earlier one posted
instead of posting it twice. The rows an earlier delivery posted are deleted once the new ones are in. Removal: the
reversible scope soft-deletes the record in Storage and leaves the rows; everything deletes the rows, then purges the
record.

## External Data Services

External Data Services pulls from an external source and takes no pushed data, so it has no route of its own. What a
flow delivers for it are the records that configure it: connected source registry entries
(`master-data--ConnectedSourceRegistryEntry`), connected source data jobs (`master-data--ConnectedSourceDataJob`) and
proxy datasets (`dataset--External`, `dataset--ConnectedSource.Generic`), usually through the `storage` route. A fetch is
a workflow (`eds_ingest`, `eds_scheduler`) the [workflow route](routes.md#workflow) can run.

```yaml
target:
  eds:
    checks: true        # hold the records EDS could not use (default true)
    retrieval: true     # registry entries need the DatasetURL eds-dms retrieves through (default true)
    build: azure        # corePlus | azure | gc; only gc takes GcpServiceAccount schemes
```

Before anything is sent, each rendered record of those types is checked for what eds-dms and the EDS workflows need,
and held with every rule it breaks (`External Data Services could not use this connected source data job: ...`):

| Record | What is checked |
| --- | --- |
| Registry entry | `data.DatasetURL`, an absolute http(s) URL (unless `retrieval` is false); `data.SecuritySchemes`, each with a unique `Name`, a `TypeID` and a `FlowTypeID` naming a flow eds-dms builds, with the keys that flow requires; `GcpServiceAccount` only on the `gc` build; never `Implicit`. |
| Data job | `ConnectedSourceRegistryEntryID` a reference with its trailing colon; `ActiveIndicator` true or false; `FetchKind`, `Filter`, the two partition ids, `OnIngestionLegalTags`, `OnIngestionAcl`; `ScheduleUTC` a cron of five or six fields; `LimitRecords` above zero when given; one `Workflows` entry tagged `FETCH` with the handler `eds_ingest`, an absolute `Url` and a `SecuritySchemeName`. |
| Proxy dataset | `data.DatasetProperties` holds the registry entry, the data job, the source partition and the source record ids. |

EDS writes a data job itself after a fetch (`LastSuccessfulRunDateUTC`, `FailedRecords`, `CreateTimeMax`). Every update
of a data job carries those keys from the version OSDU holds, and a version whose only changes are in them is not drift.
A job is stopped by delivering it with `ActiveIndicator: false`. A proxy dataset names a dataset that stays in the
source, so the routes that register files refuse its kind. `checks: false` checks nothing, and then `retrieval` and
`build` are refused.

## Reservoir DDMS (etp)

The Reservoir DDMS keeps RESQML, WITSML and PRODML content as Energistics data objects in dataspaces of its own store,
reached over ETP 1.2 on a WebSocket. The `etp` route writes them; a record is one object, with no OSDU id, version or soft
delete. The mapping fills a template whose kind has the source `etp` (`<authority>:etp:<type>:<version>`).

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: etp
  etp:
    dataspace: welldb/study
    objectsPerMessage: 100
```

| Key | Default | Meaning |
| --- | --- | --- |
| `path` | `/api/reservoir-ddms-etp/v2/` | Where the ETP WebSocket answers under the endpoint. |
| `dataspace` | none | The dataspace a record goes into when its document names none (`data.Dataspace`): at least three characters of letters, digits and `_ - . /`. |
| `objectsPerMessage` | 100 | Objects per message, which is also the batch the route is handed; 1 to 10000. |
| `maxMessageBytes` | 16000000 | The largest message this side offers, 64000 to 2000000000; the session settles on the smaller side. |
| `maxArrayBytes` | 268435456 | The most bytes of one array a delivery reads into memory before it holds the record instead. |
| `lock` | false | Whether a delivery leaves the dataspace locked (read-only) until the next one unlocks it. |

The object is its XML: the record's `files` part (one file) or `data.Xml`. It is checked before a session opens: a root
`uuid` and schema version, a `Citation` under the root, and a namespace and version the store files (RESQML 2.0 and 2.2,
EML 2.0 and 2.3, WITSML 2.1, PRODML 2.2). The arrays the XML names are declared under `data.Arrays`, each with its
`Path`, `Type`, `Dimensions` and values (inline `Values`, or a `Column` of the `bulk` part); an array the XML names and the
document does not declare, or the other way round, holds the record. A dataspace is created only when missing, with the
record's own ACLs and legal tags. One transaction per dataspace carries a batch; an array too large for a message is
filled slice by slice afterwards. A verify lists the dataspace's resources once per batch. Removal: everything deletes
the object; the record and history scopes are refused, and no dataspace is ever deleted.

## Production DDMS core service (dspdm)

The Production DDMS core service (DSPDM) keeps production data as rows of business objects in its own database. The
`dspdm` route writes them. The mapping fills a template whose kind has the source `dspdm`
(`<authority>:dspdm:<entity>:<version>`), the attributes as properties under `data` and nothing else: no access or legal
block, which the loader refuses.

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: dspdm
  dspdm:
    root: /api/dspdm/v1
    timezone: GMT+00:00
    existingRows: hold
    businessObjects:
      well_test: { name: WELL TEST, key: [UWI, TEST_NUM] }
```

| Key | Default | Meaning |
| --- | --- | --- |
| `root` | the endpoint itself | Where DSPDM is under the endpoint (`/api/dspdm/v1`). |
| `timezone` | `GMT+00:00` | The zone every request names: `GMT+hh:mm` or `GMT-hh:mm`, from GMT-12:00 to GMT+14:00. |
| `existingRows` | `hold` | A row holding a record's key that the record did not write: `hold` holds the record naming the row; `update` takes the row over. |
| `businessObjects.<entity>.name` | the entity in upper case, `_` as a space | The business object the entity's rows are (`WELL TEST`). |
| `businessObjects.<entity>.key` | its one unique constraint | The attributes a row is found again by; one of the business object's unique constraints. |

Before a run the route reads each business object's metadata from DSPDM and refuses one it cannot write (unknown,
inactive, a metadata or equipment catalog table, a primary key that is not one whole number, no usable unique
constraint). Each row is checked against its attributes and types and held with every reason. The rows of a delivery
are found in one read by their keys, then saved in one call (`POST {root}/save`, one transaction); a save DSPDM refuses is
sent again row by row. A row's version is when DSPDM last changed it. Removal: everything deletes the row for good
(`DELETE {root}/delete/{businessObject}/{id}`); the record and history scopes are refused, since DSPDM keeps no deleted
rows and no versions. Probe: `GET {root}/health`, then a read of DSPDM's metadata.

## Errors

```text
target.ddms.historian.root is required. The historian's records are written through Storage and its points through its ingestion service, so the flow's endpoint is the platform both are under; say where the ingestion service is under it (usually /api/pddms/ingest/v1).
target.ddms.seismic.subproject is required: the Seismic Store subproject the datasets are registered in, which an operator provisions.
target.eds.checks is false, so nothing reads target.eds.retrieval or target.eds.build. Remove them, or turn the checks back on.
```

Each message is prefixed with the flow file's path. The errors of `target.ddms` itself and of the Wellbore DDMS are on
[DDMSs a delivery reaches](ddms.md#errors).

## See also

- [DDMSs a delivery reaches](ddms.md): routing a record to its DDMS, `target.ddms`, and the Wellbore DDMS.
- [Routes](routes.md): the `ddms`, `etp` and `dspdm` routes and their options.
- [Removal and reversal](../concepts/removal-and-reversal.md): what each removal scope does on each route.
