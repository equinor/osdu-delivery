---
id: delivery-flow-routes
title: "Routes (target.protocol): how records reach OSDU through storage, file, dataset, manifest, DDMS, workflow, DSPDM or ETP"
type: flow-reference
summary: "The ten delivery routes a delivery flow names with target.protocol, or an interface gets from what it declares: what each sends, its keys, and removal."
keywords:
  - which route to use
  - choose a delivery route
  - target.protocol
  - route
  - interfaces route
  - storage route
  - file route
  - dataset route
  - manifest ingestion
  - fileandddms
  - manifestandddms
  - workflow route
  - target.workflow
  - protocoloptions
  - manifestbyreference
  - removal scope
yamlPath: "target.protocol"
related:
  - delivery-flow-delivery
  - delivery-flow-ddms
  - delivery-flow-ddms-services
  - delivery-concept-protocols
  - delivery-flow-interfaces
  - delivery-concept-removal-and-reversal
  - delivery-concept-artifacts-and-undo
  - delivery-guide-bulk-data
  - delivery-wiki-pattern-catalog
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Protocols/DeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/WorkflowMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/PayloadParts.cs
  - osdu/src/SqlFlow.Delivery/Model/WorkflowCatalog.cs
  - osdu/src/SqlFlow.Delivery/Model/WorkflowRoute.cs
  - osdu/src/SqlFlow.Delivery/Engine/RouteChecks.cs
  - osdu/src/SqlFlow.Delivery/Engine/RecordRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/ReversalRoute.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OwnedContent.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduFileProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/FileUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDatasetProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/DatasetService.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/DatasetUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/DatasetCollections.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduManifestProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WorkflowClient.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduFileAndDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduManifestAndDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduWorkflowProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Workflows/WorkflowTemplate.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDspdmProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduEtpProtocol.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/specs/core/storage/openapi.yaml
  - osdu/specs/core/file/openapi.yaml
  - osdu/specs/core/dataset/openapi.yaml
  - osdu/specs/core/workflow/openapi.yaml
---

# Routes (target.protocol): how records reach OSDU through storage, file, dataset, manifest, DDMS, workflow, DSPDM or ETP

A route is the call pattern a delivery flow sends its records by. There are ten, each written once in code and
parameterised by the flow: `storage`, `file`, `dataset`, `manifest`, `ddms`, `fileAndDdms`, `manifestAndDdms`,
`workflow`, `dspdm` and `etp`. The name is the same everywhere: what a flow writes, what an error quotes, what the
ledger's `Route` column holds and what the GUI shows. A flow in the single form names its route with `target.protocol`;
an interface of a source with interfaces gets one from what it declares ([below](#an-interfaces-route)).

What every route does the same way (resumable steps, payload parts, the legal tag check, the undo of an unfinished
delivery) is on [How a delivery runs on any route](../concepts/protocols.md). The DDMSs the `ddms`, `fileAndDdms` and
`manifestAndDdms` routes reach are on [DDMSs](ddms.md), and the other DDMS shapes and the `dspdm` and `etp` routes' own
blocks on [DDMS shapes and services](ddms-services.md). The rest of
`target` (endpoint, auth, headers) is on [Delivery flow](delivery.md#target).

## The routes at a glance

| Route | Services | What each record gets | Records per request |
| --- | --- | --- | --- |
| `storage` | storage | The record, upserted under its own id through the storage service's array write. | up to `batchSize` (default 100, at most 500) |
| `file` | file, storage | Its files uploaded to signed landing-zone URLs and registered as datasets, then the record naming them. | the record write batches as `storage` |
| `dataset` | dataset, storage | Its files stored where the Dataset service says and registered: a record of a dataset kind is the dataset; any other record refers to one dataset holding its files. | up to 20 registrations |
| `manifest` | file, dataset, workflow, search, storage | Its files registered, then the batch in one manifest handed to the ingestion workflow, the run polled, every record read back. | one run per batch of up to `batchSize` |
| `ddms` | the DDMS serving the record's entity type | The record through that DDMS's collection, then the data the DDMS keeps for it (bulk data, content tables, points, files, rows). | one record per request |
| `fileAndDdms` | file, the DDMS | Its files registered, the record through its DDMS naming them, then its bulk data. | one record per request |
| `manifestAndDdms` | file, dataset, workflow, search, storage, the DDMS | The batch through a manifest, then each record's bulk data through its DDMS. | as `manifest` |
| `workflow` | dataset, workflow, search, storage; Airflow's REST API when declared | The record written first, its inputs registered, up to four workflow runs in order, what they wrote found and read back. | one record per run |
| `dspdm` | the Production DDMS core service | A business object row, not an OSDU record: found by its unique key, then inserted or updated. | up to `batchSize` rows per save (at most 256) |
| `etp` | the Reservoir DDMS, over ETP 1.2 on a WebSocket | An Energistics data object in a dataspace, with the arrays it names, in one transaction per dataspace. | `objectsPerMessage` (default 100) |

The kind a mapping renders has to suit the route, and the run's preflight and `sqlflow check` refuse what cannot work:
a DSPDM kind (source `dspdm`) only on `dspdm`, an Energistics kind (source `etp`) only on `etp`, a dataset on the `file`
route (deliver it by `dataset`), a non-dataset record on `dataset` when `datasetKind` is not a dataset kind, an External
Data Services proxy dataset on any route that registers files, and on the DDMS routes a kind no DDMS the flow reaches
serves ([DDMSs](ddms.md#where-a-record-goes)).

## Naming the route

### In the single form

`target.protocol` is required in a flow without `interfaces`. It takes the route names above, compared ignoring case.
The names the routes carried before (`osduRecord` for `storage`, `osduWellLog` for `ddms`, and `osduFile`,
`osduDataset`, `osduManifest`, `osduFileAndDdms`, `osduManifestAndDdms`, `osduWorkflow`, `osduDspdm`, `osduEtp`) still
load, so older documents keep working; nothing writes them any more. Any other value is refused:

```text
'target.protocol' value 'osduLog' is not one of storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms, workflow, dspdm, etp.
```

A route that streams one payload set (`file`, `dataset`, `manifest`, `ddms`) reads it from `source.payloads`
([Delivery flow](delivery.md#source)): the only set declared, or the one `target.protocolOptions.payload` names when there are
several. The composed routes read their parts by name instead (`files`, `bulk`), and refuse `payload`:

```text
target.protocolOptions.payload names 'bulk', and the fileAndDdms route sends its parts by their own names (files, bulk). Remove it.
```

### An interface's route

In a source with interfaces ([Interfaces](interfaces.md)), `target.protocol` and `target.protocolOptions.payload` are
refused at the source level. An interface's route follows from what it declares:

| The interface declares | Route |
| --- | --- |
| no `files`, no `bulk` | `storage` |
| `files` | `file` |
| `bulk` | `ddms` |
| `files` and `bulk` | `fileAndDdms` |
| `workflow` | `workflow` |
| `route: dataset` and `files` | `dataset` |
| `route: manifest`, with or without `files` | `manifest` |
| `route: manifest` and `bulk`, with or without `files` | `manifestAndDdms` |
| `route: dspdm` | `dspdm` |
| `route: etp`, with `files`, `bulk`, both or neither | `etp` |

`interfaces.<name>.route` names a route outright, by its current name only; a named route and a part only another
route sends make the two routes' composition (`file` with `bulk`, or `ddms` with `files`, is `fileAndDdms`; `manifest`
with `bulk` is `manifestAndDdms`).
A route that cannot deliver what the interface declares is refused when the document loads, naming the key:

```text
interfaces.welllogs.route is storage, which writes the record alone, so interfaces.welllogs.bulk would never be sent. Remove it, or choose the route that delivers it.
```

The same holds for `file` or `dataset` without `files`, `dataset` or `workflow` with `bulk`, `dspdm` with `files` or
`bulk`, `route: workflow` without a `workflow` block, a `workflow` block on another route, `fileAndDdms` without both
`files` and `bulk`, and `manifestAndDdms` without `bulk`. An interface delivered through a DDMS has to know where its DDMS is (a DDMS under `target.ddms`,
`ddmsRoot` in the source's or its own `protocolOptions`, or paths of its own), or the document is refused:

```text
interfaces.welllogs is delivered through a DDMS, and the source's endpoint is the platform every interface reaches its services under. Say where the DDMS is under it: declare it under target.ddms with its root, or set target.protocolOptions.ddmsRoot or interfaces.welllogs.protocolOptions.ddmsRoot for the Wellbore DDMS (usually deployed under /api/os-wellbore-ddms).
```

`target.ddms`, `target.dspdm` and `target.etp` belong to the interfaces taking those routes, and each is refused when no
interface does. `sqlflow check` prints each interface's route and why it got it.

## Options every route reads

These `target.protocolOptions` keys apply across routes. Any path option may be written as an absolute http(s) URL, which
goes through the same address guard and `reliability.urlAllowlist` as every other request; a relative path resolves
under `target.endpoint`.

| Key | Default | Meaning |
| --- | --- | --- |
| `recordPath`, `recordMethod` | `PUT /api/storage/v2/records`; on the `ddms` route the collection, with `POST` | The record write. A path set here is used as written (a facade in front of the Wellbore DDMS, for example). |
| `versionPath` | `recordIdVersions[0]` | The JSON path of the `id:version` string in a single-record write's answer. |
| `verifyPath` | `/api/storage/v2/records/{id}` | Reads one record back, for a verify and for the read before an update. |
| `verifyBatchPath` | `/api/storage/v2/query/records` | The batched read a verify pass uses, 100 ids per request. |
| `deletePath` | `/api/storage/v2/records/{id}:delete` | The reversible removal. |
| `bulkDeletePath` | `/api/storage/v2/records/delete` | The storage service's bulk soft delete, which the route sends 500 ids a request (its own chunk size, not a limit of the service), for the reversible scope only. |
| `purgeVersionsPath` | `/api/storage/v2/records/{id}/versions` | The purge of a record's earlier versions; the latest stays live. |
| `purgePath` | `/api/storage/v2/records/{id}` | The purge of the record and every version. |
| `probePath` | the route's own | The path the probe asks; by default the info endpoint of the route's main service (`/api/storage/v2/info`, `/api/file/v2/info`, `/api/dataset/v1/info`, `/api/workflow/v1/info`) or each DDMS's description. |
| `batchSize` | 100 | Records per write request where the service takes arrays (`storage`, `file`, `manifest`), 1 to 500; up to 20 per registration on `dataset`, up to 256 rows per save on `dspdm`. |
| `preserveDataKeys` | none | Keys under `data` other systems write (`Datasets`, `DDMSDatasets`, `ExtensionProperties`, a key a platform adds), read from the stored record and carried into every update. A mapping that writes under a preserved key is refused when the flow is planned, since what it wrote would reach a record only when it is created. A list a step of `enrich:` links its datasets in is merged instead: OSDU's items stay, the step's links are added and those it no longer renders are taken off ([preserved keys](enrich-files.md#preserved-keys)). A record OSDU lost (its verify outcome `missing`) is brought back from its soft delete first (`PATCH` on the verify path, `{"deleted": false}`), so these are read from it; a purged one has none to keep. |
| `skipDuplicates` | false | Sends `skipdupes=true` on the storage write (`storage`, `file`). Off by default: the storage contract does not say which parts of a record it compares, and a change to `acl`, `legal` or `tags` alone must not be skipped. |
| `payloadContentType` | `application/x-parquet` | The media type the payload goes as: the bulk data on a DDMS route, the files where they are the payload. |
| `filesContentType` | `payloadContentType` where the files are the payload; `application/octet-stream` beside bulk data | The media type a record's files are uploaded as. Refused on `storage` and `ddms`, which upload no files. |
| `validateLegalTags`, `legalValidatePath` | true, `/api/legal/v1/legaltags:validate` | The legal tag check before a run ([concepts](../concepts/protocols.md#before-a-run-the-legal-tag-check)). |

On the `file`, `dataset`, `manifest` and `workflow` routes the files go as `filesContentType`, or else as
`payloadContentType`, whose default is parquet: a flow uploading LAS, CSV or XML files sets one of them.

## storage

The record is written through the storage service's array endpoint, up to `batchSize` records per request
(`PUT /api/storage/v2/records`). The answer's `recordIdVersions` supply each record's version. A batch the service
refuses as a whole (a 4xx other than 401, 408, 425 and 429) is sent again one record at a time, so one bad document holds
only itself.

An update of a record that carries preserved keys reads the stored record first and copies those keys in. A connected
source data job of External Data Services always carries the run state EDS writes on it (`LastSuccessfulRunDateUTC`,
`FailedRecords`, `CreateTimeMax`; see [DDMS shapes and services](ddms-services.md#external-data-services)).

Storage keeps what this route writes, so every write records the hash of the content the flow owns (`ownedContent.hash`,
leaving out the preserved keys it carried, listed in `ownedContent.excluded`). A verify that finds a newer version reads the
record and reports a match, not drift, when only those keys changed, or when the content is what the flow wrote: another
writer wrote the same record again, such as another instance of the delivery linked to it
([Adopting a record OSDU already holds](../concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)). A record a
delivery before module 1.56.0 wrote without carrying keys has no hash until its next write, and its verify compares versions
alone.

With `skipDuplicates`, storage names no version for a record it skips as unchanged (osdu/specs/core/INTEGRATION.md
section 2.2), and the version it holds may be newer than the one the ledger knew, written by another writer. The route
reads the versions of the skipped records after the write, 100 ids a request, and records the version each stands at; a
read that fails keeps the version the ledger knew, and the attempt's note says so. A skipped record storage does not serve
is one it holds soft-deleted, which a skipped write leaves deleted: the route brings it back under its id (`PATCH` on the
verify path, `{"deleted": false}`, step `restore`) at the version it had. One it cannot bring back is not delivered: a
403 holds it (the flow's credentials are no owner of the record), and a record storage holds no trace of any more is tried
again, which writes it under its id.

The `storage` route makes one atomic write per record, so it declares nothing to undo. A reversal can write an earlier
version of its records back.

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: storage
  protocolOptions:
    batchSize: 200
```

## file

The files go first, then the record that refers to them (file v2, storage v2):

1. For each payload file, `GET /api/file/v2/files/uploadURL` hands out a signed URL and a `FileSource`; the file streams to
   the signed URL with `PUT`. An Azure blob host (`*.blob.core.*`) gets `x-ms-blob-type: BlockBlob` unless the flow
   declares it; other landing zones get only the flow's `uploadHeaders`. The signed URL is never logged or stored.
2. Each uploaded file is registered as a dataset record (`POST /api/file/v2/files/metadata`): `datasetKind` (default
   `osdu:wks:dataset--File.Generic:1.0.0`), the record's own `acl` and `legal`, and `FileSourceInfo`. The step is marked
   before the request goes, because the service mints a dataset per accepted registration: a try that stops between the
   answer and the report asks the search service which dataset that landing-zone path became, for up to
   `datasetIndexWaitSeconds`, and takes it over instead of registering the file twice.
3. The record is written through storage as `storage` writes it, with `data.<datasetsProperty>` (default `Datasets`)
   naming the datasets as `{id}:`, the form the work product component schemas require. References the mapping rendered
   are kept.

A change to the record alone rewrites it with the datasets of its earlier delivery. A payload change registers new
datasets and points the record at them; the earlier datasets stay live, since earlier versions of the record name them,
and the ledger marks them superseded. An empty file, or one above `reliability.maxRequestBodyBytes`, holds the record
before anything is sent.

| Key | Default | Meaning |
| --- | --- | --- |
| `uploadUrlPath` | `/api/file/v2/files/uploadURL` | Hands out the signed landing-zone location. |
| `uploadUrlExpiry` | the service's one hour | How long the signed URL stays valid: a whole number of minutes, hours or days (`30M`, `12H`, `2D`). |
| `uploadHeaders` | none | Extra headers on the signed-URL upload, sent as written and never resolved. A header that carries a credential is refused outright, reference or not (`<file>: target.protocolOptions.uploadHeaders.<header> carries a credential, and the upload headers go to the signed URL as written, never resolved. The signed URL carries its own credential: remove the header.`). |
| `fileMetadataPath` | `/api/file/v2/files/metadata` | Registers a file's dataset record. |
| `fileDeletePath` | `/api/file/v2/files/{id}/metadata` | Deletes a dataset record and its file, when everything is removed. |
| `datasetKind` | `osdu:wks:dataset--File.Generic:1.0.0` | The kind each file is registered as. |
| `datasetsProperty` | `Datasets` | The `data` property listing the record's datasets. |
| `datasetIndexWaitSeconds`, `searchQueryPath` | 120, `/api/search/v2/query` | How long, and where, a resumed registration looks for the dataset an interrupted try may have made (0 does not wait). |

## dataset

The files and the record go through the Dataset service (dataset v1, storage v2):

1. `POST /api/dataset/v1/storageInstructions?kindSubType=<entity type>` hands out a staging location for the record's
   entity type, or `datasetKind`'s for a record of another kind. A 400 (no DMS serves the type) or 405 (the DMS stores no
   files) holds the record.
2. The files go where the location says, as the provider that signed it takes them: one file with `PUT` to its signed URL,
   a file collection created, appended and flushed under an Azure Data Lake directory, posted with the POST policy of MinIO
   or S3, or uploaded under a Google Cloud Storage folder. A location with temporary credentials for an endpoint it does
   not name (IBM) holds the record. A dataset kind other than `dataset--FileCollection.*` takes exactly one file, and a
   collection holding two files of one name is held.
3. A record of a dataset kind is registered as the dataset under its own id (`PUT /api/dataset/v1/registerDataset`, up to
   20 per request), with `DatasetProperties` pointing at its files. Any other record refers to one dataset of
   `datasetKind` holding its files, registered under an id derived from the record's
   (`<partition>:<dataset entity type>:<key>-files`), so new files land on the same dataset; the record is then written
   through storage, its dataset list naming it.
4. `POST /api/dataset/v1/retrievalInstructions` checks that the service hands out every dataset it registered; one it
   leaves out fails its record for the try.

A change to a dataset record alone is written through storage with the `DatasetProperties` OSDU holds, since a
registration would copy the staging area again. The four Dataset service paths are `datasetInstructionsPath`,
`datasetRegisterPath`, `datasetRetrievalPath` and `datasetSoftDeletePath`, each a path or an absolute URL. The signed
location is a credential: a retry reuses it only when every upload of the earlier try completed.

## manifest

OSDU's own ingestion path: the batch's files, one manifest, one workflow run (file v2, workflow v1, storage v2).

1. The batch's files are uploaded and registered as on the `file` route, and each record's dataset list names them.
   Registration comes first because it is what makes a file retrievable.
2. The registered datasets are waited for until the search index lists them (`datasetIndexWaitSeconds`, default 120; 0
   does not wait), because ingestion checks a record's references against the index and drops a record whose dataset it
   cannot find yet while the run still finishes. Each record's current version is read from storage and kept.
3. One manifest (`manifestKind`, default `osdu:wks:Manifest:1.0.0`) carries every record of the batch in the section its
   kind's group names (`ReferenceData`, `MasterData`, `WorkProduct`, `WorkProductComponents`, `Datasets`), or the one
   `manifestSection` names. `POST /api/workflow/v1/workflow/{workflow}/workflowRun` triggers `workflowName` (default
   `Osdu_ingest`) with a run id the route chooses, so a trigger the service already took answers 409 and is polled, never
   run twice.
4. The run is polled every `workflowPollSeconds` (default 10) until `SUCCESS`, `PARTIAL_SUCCESS`, `FINISHED` or
   `FAILED`, or until `workflowTimeoutMinutes` (default 60) pass. A timeout fails the try, and the next try resumes polling
   the same run; a failed run fails the batch, and the next try triggers a new one.
5. Every record is read back from storage (`recordQueryPath`, 100 ids per request). A record present at a version that
   moved is delivered; one missing, or still at the version it had before the run, was not written by it and goes into a
   new run on the next try.

**By reference.** `manifestByReference` sends the manifest as a stored dataset to `byReferenceWorkflowName` (default
`Osdu_ingest_by_reference`): `always`, or `auto` when the trigger request (measured as JSON indented by four) is above
`manifestInlineLimitKb` (default 12000, 1 to 1048576) and the partition registers that workflow. `auto` on a partition
without it splits the batch into manifests under the limit; `always` there fails the batch. The stored manifest is
removed reversibly once its run has settled. Only the manifest routes take the option:

```text
target.protocolOptions.manifestByReference says how manifests reach the ingestion workflow, and the flow's route (storage) sends none.
```

| Key | Default | Meaning |
| --- | --- | --- |
| `workflowName` | `Osdu_ingest` | The ingestion workflow. |
| `workflowRunPath`, `workflowStatusPath`, `workflowPath` | the Workflow service's v1 paths | Trigger a run, read its status, describe a workflow. |
| `workflowPollSeconds`, `workflowTimeoutMinutes` | 10, 60 | How often a run is polled, and how long it may take; each at least 1. |
| `workflowAppKey`, `workflowPayload` | `osdu-delivery`, none | `executionContext.Payload.AppKey`, and extra `Payload` entries, sent as written. An entry named as a secret is refused outright (`<file>: target.protocolOptions.workflowPayload.<entry> is named as a secret, and the workflow payload goes as written into a run the Workflow service keeps. The service authenticates the run with the flow's own auth: remove the entry.`). |
| `manifestKind`, `manifestSection` | `osdu:wks:Manifest:1.0.0`, from each kind | The manifest's kind, and the section every record goes into. |
| `recordQueryPath` | `/api/storage/v2/query/records` | Where the records are read back. |
| `manifestByReference`, `manifestInlineLimitKb`, `byReferenceWorkflowName` | `never`, 12000, `Osdu_ingest_by_reference` | Manifests by reference, above. |

## ddms

Each record goes to the collection of the DDMS serving its entity type, and by that DDMS's call pattern (its shape): the
Wellbore DDMS v3, the Well Delivery DDMS, the Rock and Fluid Sample DDMS, the production historian, Seismic Store v3 or
the Reservoir Management DDMS. The record goes first; then, on a collection that keeps data beside its records, that
data. What a DDMS checks is checked before the first request, so a record it would refuse is held with the rule it
breaks. Where each record goes, the keys of `target.ddms`, each shape and the Wellbore DDMS's bulk rules are on
[DDMSs](ddms.md); a worked example is [Well logs with curves](../guides/bulk-data.md).

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: ddms
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms
```

## fileAndDdms and manifestAndDdms

The composed routes send a record's files and its DDMS data as two parts, each when its own content hash moves or a
redelivery names it ([payload parts](../concepts/protocols.md#payload-parts)).

**fileAndDdms** (payload sets `files` and `bulk`, both required): the record and its bulk data are checked against the
DDMS's rules first, so a record it would refuse is held before any file is uploaded; the files are uploaded and
registered as on `file` (as `filesContentType`, default `application/octet-stream`); the record goes through its DDMS
collection, its dataset list naming them, carrying the bulk link the DDMS keeps; then the bulk data goes as `ddms` sends
it. The probe asks the file service and every DDMS.

**manifestAndDdms** (payload set `bulk`, and `files` when the record has some): each record's DDMS rules and bulk data
are checked first; a record whose document changed, whose files moved, or which is new goes through the manifest as
`manifest` sends it; once the run has written it, its bulk data goes through its DDMS from the version the manifest
wrote. Ingestion writes through storage, past the DDMS, so a record that already holds DDMS data has the link to it
carried into the manifest from one batched storage read; without it the record would lose its link. A record whose bulk
data alone changed skips the manifest. The probe asks the Workflow service and every DDMS.

```yaml
source:
  payloads:
    files: { root: ../data/las, locationColumn: las_folder, pattern: "*.las", hashColumn: las_hash }
    bulk: { root: ../data/curves, locationColumn: curve_folder, pattern: "*.parquet", hashColumn: curves_hash }
target:
  protocol: fileAndDdms
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms
    filesContentType: text/plain
```

## workflow

One route drives every ingestion workflow a partition registers. For each record:

1. The record itself, the anchor, is written first: registered with its `files` through the Dataset service
   (`anchor: dataset`, the default when the record has files, which needs the mapping to render a dataset kind), or
   written through storage (`anchor: storage`). With `anchorTag`, the route writes a tag on it,
   `osdu-delivery-<24 hex>` derived from its id, which the workflows copy onto what they write.
2. Each input whose hash moved is registered through the Dataset service under ids derived from the anchor's.
3. When a run is due (`runWhen`), each stage in order: the context filled (secrets only in the request) and checked
   against the workflow's payload contract, the run triggered under a run id recorded before the call (a resend answers
   409 and is polled), then polled until it ends. A failed run fails the record, and the next try triggers a new one; a
   finished run's outputs are kept, and a retry reuses them.
4. What the runs wrote is found the way `results` says and read back from storage, until `minimum` records are present or
   `waitSeconds` pass. Fewer fail the try, and the next try looks again without running the workflows again.

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: workflow
  protocolOptions:
    filesContentType: text/csv
  workflow:
    anchor: dataset                   # the CSV file, registered as the dataset--File.Generic the mapping renders
    anchorTag: welldbAnchor
    stages:
      - workflow: csv_ingestion       # the name this partition registers the CSV parser under
        context:
          id: "{record:id}"
          dataPartitionId: "{partition}"
    results:
      search:
        kind: "osdu:wks:master-data--Wellbore:*"
        query: "tags.welldbAnchor:\"{anchorTag}\""
      minimum: 1
```

| Key | Default | Meaning |
| --- | --- | --- |
| `anchor` | `dataset` with files, else `storage` | How the record is written first. `dataset` without files is refused. |
| `anchorTag` | none | A tag key (a letter, then letters, digits and `_`, at most 64 characters) written on the record; `{anchorTag}` in a template is its value. |
| `runWhen` | `changed` | `changed` (the delivery writes the record or an input), `created` (a new record only) or `requested` (only a redelivery naming `workflow`). |
| `inputs.<name>` | none | Up to 8 payload sets (`root`, `locationColumn`, `pattern`, `hashColumn`, `chunkCountColumn`) registered as one dataset per file or one collection, with `datasetKind` (default `osdu:wks:dataset--File.Generic:1.0.0`) and `optional`. A name is a letter, then letters, digits, `_` and `-`, at most 32 characters, and not `files` or `bulk`. |
| `secrets.<name>` | none | A whole `${env:NAME}` or `${keyvault:NAME}` reference a context names as `{secret:name}`. Resolved only for the trigger request; every step and message shows `***`. A context entry named as a secret must name one of these. |
| `stages[]` | required, 1 to 4 | Run in order. Each has `workflow` (as the partition registers it), `contract` (when `workflow` is not one of the contract's names), `context` (any YAML, with placeholders), `timeoutMinutes` (0 to 10080; 0 takes the longer of `workflowTimeoutMinutes` and the contract's), `pollSeconds` (0 to 3600; 0 takes `workflowPollSeconds`) and `outputs`. |
| `stages[].outputs.<name>` | none | A `value` template, or an `xcom` entry (`task`, `key`, `match`), read by later stages and the results as `{stage:n.name}`. |
| `results` | none | One of `anchor: true`, `ids` (a template), `artefact` (`role`, `kind`), `search` (`kind`, `query`), `manifest` (a template naming a manifest dataset) or `xcom`, with `minimum` (default 1, or 0 with no way named or with `anchor`), `waitSeconds` (0 to 86400, default `datasetIndexWaitSeconds`), `keep` (ids kept on the record, 0 to 1000, default 100) and `remove` (default true: a removal takes the records the runs created). |

**Contracts.** The workflows OSDU Delivery knows each have a payload contract: `osduIngest`, `osduIngestByReference`,
`csvParser`, `energymlConverter`, `energymlDelivery`, `enyparserTranslation`, `segyToVds`, `segyToZgy`, `segyToMdio`,
`edsIngest`, `edsScheduler` and `edsNaturalization`. A context is checked against its contract when the document loads
and again, filled, before every trigger; a context the workflow would fail on is never sent. A contract key whose value
does not read as a template is refused with its reason. Whatever the workflow, with a contract or without, a value
under a key the contract marks a credential, or under any key named as a secret (a password, secret, token, assertion,
signature, authorization, an API or account key), is a single `{secret:name}` placeholder; a literal is refused when
the document loads, because it would be sent to the Workflow service and kept in the record's step history:
`<file>: target.workflow.stages[0].context: 'Payload.client_secret' is named as a secret; give it as {secret:name} with the reference under secrets, never as a value in the document.` A workflow that only
translates (`Energyml_Converter`, `Enyparser_Translation`) is followed by a stage that ingests its manifest. The Workflow
service's test DAG (`manifest_ingestion`) is refused.

**Placeholders.** `{partition}`, `{appKey}`, `{runId}`, `{anchorTag}`, `{record:<path>}` (a value of the anchor,
`data.Name` or `data.Curves[*].CurveID`), `{input:<name>}` or `{input:<name>[n]}`, `{dataset:<suffix>}` (a
`dataset--File.Generic` id derived from the anchor's), `{stage:<n>.<output>}` and `{secret:<name>}`; the modifiers
`|id`, `|ref`, `|list`, `|first` and `|json` shape a value. A string that is one placeholder takes the value's JSON shape.
A placeholder naming a secret, input or output the route does not have, or a stage that has not run by then, is refused
when the document loads.

**target.airflow.** An XCom output is read through the Workflow service's `latestInfo`, which serves the run's latest task
only. `target.airflow` names the Airflow behind the Workflow service instead: `endpoint`, `apiVersion` (`v1` for Airflow
2, `v2` for Airflow 3, where basic credentials are exchanged for a token at `/auth/token`), `auth` (the same schemes as
`target.auth`) and `headers`. Only the workflow route reads it.

The probe asks the Workflow service, and whether the partition registers every workflow the stages name, so a name the
partition does not know stops the run in its preflight.

## dspdm and etp

**dspdm** writes rows of the Production DDMS core service's business objects, not OSDU records. The mapping fills a
template whose kind has the source `dspdm` (`<authority>:dspdm:<entity>:<version>`), with the business object's
attributes under `data` and no access or legal block. Each row is found again by a unique key of its business object
before it is saved, because DSPDM's save inserts a row sent without its primary key again. `target.dspdm` and the
checks are on [DDMS shapes and services](ddms-services.md#production-ddms-core-service-dspdm).

**etp** writes Energistics data objects into a dataspace of the Reservoir DDMS over ETP 1.2. The mapping fills a template
whose kind has the source `etp`; the object is its XML (the `files` part, or `data.Xml`) with the arrays it names (the
`bulk` part, or values in the document). `target.etp` is on [DDMS shapes and services](ddms-services.md#reservoir-ddms-etp).

Neither route has an OSDU version or a soft delete: their records are not storage records.

## What a removal does

A removal takes a record out of OSDU at one of three scopes ([Removal and reversal](../concepts/removal-and-reversal.md)):
`record` (reversible), `history` (earlier versions purged, the latest live) and `everything` (purged). What each scope
calls depends on the route:

| Route | `record` | `history` | `everything` |
| --- | --- | --- | --- |
| `storage` | `POST /api/storage/v2/records/{id}:delete`; several records at once through `POST /records/delete`, 500 ids a request (the route's chunk size) | `DELETE /api/storage/v2/records/{id}/versions` | `DELETE /api/storage/v2/records/{id}` |
| `file`, `manifest` | as `storage`; the datasets stay, so OSDU can restore the record whole | as `storage` | as `storage`, then each dataset the files became, with its file (`fileDeletePath`) |
| `dataset` | a dataset record: `POST /api/dataset/v1/metadataRecord/{id}/softDelete`, which the Dataset service's undelete restores; any other record: as `storage`, its dataset left | as `storage` | as `storage`, and for a record of another kind its `-files` dataset too; the files a registration copied stay in the platform's storage |
| `ddms` | the DDMS's own removal: on the Wellbore DDMS a logical `DELETE {collection}/{id}` | the storage service's version purge (the DDMSs have none) | a Wellbore DDMS bulk collection: `DELETE {collection}/{id}?purge=true`, which removes the bulk data too; a records-only collection: the storage purge |
| `fileAndDdms`, `manifestAndDdms` | as `ddms` | as `ddms` | as `ddms`, then the files' datasets |
| `workflow` | a dataset anchor: the Dataset service's `softDelete`; a storage anchor: as `storage`; and, with `results.remove`, the records the runs created at the same scope | the anchor's earlier versions | the anchor, the records the runs created (with `results.remove`), and the inputs and files it registered |
| `dspdm` | refused: DSPDM keeps no deleted rows | refused: DSPDM keeps no versions | `DELETE {root}/delete/{businessObject}/{row id}` |
| `etp` | refused: the store keeps no deleted objects | refused: the store keeps no earlier versions | `DeleteDataObjects` for the object's URI; a dataspace is never deleted |

The other DDMS shapes remove as [DDMS shapes and services](ddms-services.md) describes for each. A `ddms` flow whose endpoint is the DDMS itself does
not reach the storage service by a path, so its `history` scope (and the `everything` scope of a records-only
collection) needs `purgeVersionsPath` and `purgePath` written as whole URLs; without them those scopes are refused rather
than sent somewhere nobody chose.

A reversal ([Removal and reversal](../concepts/removal-and-reversal.md)) writes records back at an earlier version on the
`storage` route, and on the `ddms` route for Wellbore DDMS records under a platform endpoint (through the storage service,
with the bulk link that version names). Every other route refuses, since a version of the record does not bring back the
files, datasets, runs, rows or objects beside it.

## Errors

```text
target.protocolOptions.ddmsRoot only applies to the ddms route; the storage route reaches its services under the endpoint already.
target.ddms declares the DDMSs the ddms route delivers to, and this flow's protocol is storage. Remove target.ddms.
target.protocolOptions.filesContentType says what type a record's files are uploaded as, and the flow's route (ddms) uploads no files; payloadContentType names the type of what it sends.
target.protocolOptions.batchSize must be between 1 and 500.
the dataset route stores and registers each record's files, and source.payloads.files declares none.
the workflow route runs the workflow target.workflow declares, and the document declares none.
the fileAndDdms route sends files and bulk, and source.payloads.bulk is not declared.
target.etp declares the Reservoir DDMS the etp route writes Energistics objects to, and this flow's route is ddms. Remove target.etp.
target.dspdm declares the Production DDMS core service the dspdm route writes rows to, and this flow's route is storage. Remove target.dspdm.
```

Each message is prefixed with the flow file's path.
