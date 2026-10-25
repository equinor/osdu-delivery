# OSDU core services: integration brief

The OSDU core services are the platform services every OSDU Delivery route type goes through: Storage, File, Dataset,
Schema, Search, Legal, Entitlements, Register, Partition, Unit, CRS Catalog and CRS Conversion, plus the two Workflow
calls the manifest route makes. The storage, file, dataset, manifest, workflow and composed routes use them; the dataset
and workflow routes reach the File service's DMS endpoints through the Dataset service. This brief lists every call
those routes make or need, the rules the services apply to each call, and where the pinned contracts and the upstream
code disagree.
Every statement cites the contract or the upstream file it rests on.

## Sources

Contracts: the core specification set, copied into `osdu/specs/core`.

| Key | File | Base path (`servers`) | `info.version` |
| --- | --- | --- | --- |
| C-storage | `osdu/specs/core/storage/openapi.yaml` | `/api/storage/v2/` | 2.0.0 |
| C-file | `osdu/specs/core/file/openapi.yaml` | `/api/file/` | 2.0.0 |
| C-dataset | `osdu/specs/core/dataset/openapi.yaml` | `/api/dataset/v1/` | 1.0.0 |
| C-workflow | `osdu/specs/core/workflow/openapi.yaml` | `/api/workflow` | 2.0.5 |
| C-search | `osdu/specs/core/search/openapi.yaml` | `/api/search/v2/` | 2.0 |
| C-legal | `osdu/specs/core/legal/openapi.yaml` | `/api/legal/v1/` | 1.0.0 |
| C-schema | `osdu/specs/core/schema_service/openapi.yaml` | `/api/schema-service/v1` | 1.0 |
| C-register | `osdu/specs/core/register/openapi.yaml` | `/api/register/v1` | 1.0 |
| C-entitlements | `osdu/specs/core/entitlements/openapi.yaml` | `/api/entitlements/v2` | 2.0 |
| C-partition | `osdu/specs/core/partition/openapi.yaml` | `/api/partition/v1` | 1.0 |
| C-unit2, C-unit3 | `osdu/specs/core/unit/v2/openapi.yaml`, `osdu/specs/core/unit/v3/openapi.yaml` | `/api/unit` | 2.0.0, 3.0.0 |
| C-crscat | `osdu/specs/core/crs_catalog/openapi.yaml` | `/api/crs/catalog/` | 3.0.0 |
| C-crsconv | `osdu/specs/core/crs_conversion/openapi.yaml` | `/api/crs/converter/` | 4.0.0 |

All are OpenAPI 3.1.0. The core specification set is a local specification set, not a git project, so
`osdu/specs/sources.json` records these files with no commit (`"commit": null`) and the date each was copied
(2026-09-16 for the first nine rows above, 2026-09-17 for Partition, Unit and the two CRS files). Line numbers cited
here are those of the repository copies, which match the set line for line. The two CRS files are single-line JSON and
are cited by path, operationId and schema name only.

Upstream code and documentation, from `https://community.opengroup.org`, each read at the head of `master` on
2026-09-16:

| Key | Project | Commit (commit date) | Path prefixes used in citations |
| --- | --- | --- | --- |
| 44 | `osdu/platform/system/storage` | `11d7299591e2372c8b949b2a495f544c080cc350` (2026-09-10) | `SC/` = `storage-core/src/main/java/org/opengroup/osdu/storage/` |
| 90 | `osdu/platform/system/file` | `d7c25c2d7f5d2f42bed901c68a407098195389bb` (2026-09-11) | `FC/` = `file-core/src/main/java/org/opengroup/osdu/file/`; `FAZ/` = `provider/file-azure/src/main/java/org/opengroup/osdu/file/provider/azure/`; `FCP/` = `file-core-plus/src/main/java/org/opengroup/osdu/file/provider/gcp/provider/`; `FIBM/` = `provider/file-ibm/src/main/java/org/opengroup/osdu/file/provider/ibm/` |
| 118 | `osdu/platform/system/dataset` | `329f3c0b4924aaf2cb50261520108bfcd3826136` (2026-09-14) | `DC/` = `dataset-core/src/main/java/org/opengroup/osdu/dataset/`; `DAZ/` = `provider/dataset-azure/src/main/java/org/opengroup/osdu/dataset/provider/azure/`; `DCP/` = `dataset-core-plus/src/main/java/org/opengroup/osdu/dataset/provider/gcp/` |
| 26 | `osdu/platform/system/schema-service` | `b099bbca230f4b82cefb6541a677674a80a7b92e` (2026-09-15) | `SCH/` = `schema-core/src/main/java/org/opengroup/osdu/schema/`; `SS/` = `deployments/shared-schemas/osdu/` |
| 19 | `osdu/platform/system/search-service` | `731e6f5748416003b58b2e96eb018a82d4ddd8c9` (2026-09-16) | `SRC/` = `search-core/src/main/java/org/opengroup/osdu/search/` |
| 157 | `osdu/platform/system/register` | `326eca619287fc3939619e025b75e17ea622dffc` (2026-09-16) | `RC/` = `register-core/src/main/java/org/opengroup/osdu/register/`; `RAZ/` = `provider/register-azure/src/main/java/org/opengroup/osdu/register/provider/azure/`; `RCP/` = `register-core-plus/src/main/java/org/opengroup/osdu/register/` |
| 67 | `osdu/platform/system/lib/core/os-core-common` | `b54d3073f21f32db115083f3381fc4a164957cb2` (2026-08-31) | `CC/` = `src/main/java/org/opengroup/osdu/core/common/` |
| 1441 | `osdu/platform/system/lib/drivers/os-obm` | `c389fbea3de45d24c4f4847107dd3057bc988724` (head of `master`, read 2026-09-17) | `OC/`, `MIN/`, `S3/` = `os-obm-core/src/main/java/org/opengroup/osdu/core/obm/core/`, `os-obm-minio/src/main/java/org/opengroup/osdu/core/obm/drivers/minio/`, `os-obm-s3/src/main/java/org/opengroup/osdu/core/obm/drivers/s3/` |
| 1475 | `osdu/platform/system/lib/cloud/gcp/gc-obm` | `abb8463cb22db1d1314452d421387a04cafc7407` (head of `main`, read 2026-09-17) | `GS/` = `gc-obm-gs/src/main/java/org/opengroup/osdu/core/obm/drivers/gs/` |
| 77 | `os-core-lib-azure` | tag `v3.0.0`, the version the File service pins [90 provider/file-azure/pom.xml:39] | files cited by name |

The keys are the GitLab project ids. `os-core-common` (67) is the shared library: Storage, Dataset, File and Search
take their record, kind, ACL, legal, DMS and search validation from it, and their Storage, Legal and Entitlements
clients. The File service pins `os-obm-core` 0.31.0, and the driver files cited from 1441 are the same at its tag
`v0.31.0` [90 file-core-plus/pom.xml:66-70]. `SDK` is the Maven Central sources of the Azure Storage File Data Lake
client 12.27.0, the Azure Storage Blob client 12.34.0 and the MinIO Java client 8.5.2, the versions the File service
resolves (inference for which Azure BOM wins).

This repository: `OD` is `osdu/src/SqlFlow.Delivery/Engine/Protocols/`, where the protocols make their HTTP calls
(`osdu/src/SqlFlow.Delivery/Protocols/IDeliveryProtocol.cs` is the contract they implement).

How statements are marked:

- Contract: `[C-storage: PUT /records, createOrUpdateRecords]` names an operation of a pinned file;
  `[C-storage: Record, L1709-1779]` names a schema or lines of it. Role names quoted from a contract come from the
  operation's description text, which is prose, not a machine-checked rule.
- Source: `[44 SC/service/IngestionServiceImpl.java:94-105]` is upstream code or the project's own documentation at
  the commit above. Behaviour that only code shows is always cited this way, never as contract.
- Inference: a sentence that starts with "Inference:" is a conclusion drawn from the cited code that no contract or
  document states. Confirm it on a live partition before a route depends on it.
- Repository: `[OD OsduRecordProtocol.cs]` is this repository's code.

## 1. Base paths, versions, headers and auth

- Base paths are in the Sources table. File, Workflow, Unit and CRS put the API version in the operation path
  (`/v2/files/...`, `/v1/workflow/...`, `/v3/unit/...`, `/v4/convert`); the others put it in the base path.
- `Authorization: Bearer <token>`. Every contract declares one security scheme, `Authorization`, of type `http` with
  scheme `bearer` (written `Bearer` in Partition), and applies it to every operation (`components.securitySchemes`
  and top-level `security` in each file).
- `data-partition-id` is a required header on every operation this brief uses. The only business operations that do
  not declare it are `POST /v2/files/revokeURL` [C-file: POST /v2/files/revokeURL, revokeURL] and
  `PUT /schemas/system` [C-schema: PUT /schemas/system, upsertSystemSchema]. `GET /groups` and `PATCH /records`
  declare it twice [C-entitlements: GET /groups, listGroups; C-storage: PATCH /records, updateRecordsMetadata].
- `x-collaboration` is an optional header on the Storage operations [C-storage: PUT /records,
  createOrUpdateRecords]. OSDU Delivery does not send it.
- `frame-of-reference` is a required header of `POST /query/records:batch` [C-storage: POST /query/records:batch,
  fetchRecords] (section 5.1). `on-behalf-of` is an optional header of `GET /groups` [C-entitlements: GET /groups,
  listGroups].
- Request bodies are `application/json`, except the two Storage patch forms: `application/json-patch+json` on
  `PATCH /records` and `application/merge-patch+json` on `PATCH /records/{id}` [C-storage: PATCH /records,
  updateRecordsMetadata; PATCH /records/{id}, patchRecord].
- Every contract except Unit v2 and CRS Conversion has an `info` operation (`GET /info`; `GET /v2/info` in File,
  `GET /v1/info` in Workflow, `GET /v3/info` in Unit v3 and CRS Catalog).

### 1.1 Roles

Roles in a contract are description text. The code at the pinned commits checks these groups:

| Call | Contract text | Code |
| --- | --- | --- |
| Storage `PUT /records` | none | `service.storage.creator` or `service.storage.admin` [44 SC/api/RecordApi.java:122-124; 67 CC/model/storage/StorageRole.java:24-26] |
| Storage `POST /query/records`, `POST /query/records/headers`, `GET /records`, `GET /records/{id}`, `GET /records/{id}/{version}`, `GET /records/versions/{id}` | viewer, creator, admin (none for headers) | `service.storage.viewer`, `creator` or `admin` [44 SC/api/QueryApi.java:105-106, 153-154; SC/api/RecordApi.java:146-147, 186-187, 302-303, 327-328] |
| Storage `POST /query/records:batch` | `users.datalake.viewers`, `editors` or `admins` | `service.storage.viewer`, `creator` or `admin` [44 SC/api/QueryApi.java:131-132] |
| Storage `GET /query/records` | `service.storage.admin` | `service.storage.admin` [44 SC/api/QueryApi.java:79-80] |
| Storage `PATCH /records` (both media types) | `users.datalake.editors` or `admins` | `service.storage.creator` or `admin` [44 SC/api/PatchApi.java:83-84, 109-110] |
| Storage `PATCH /records/{id}` | `service.storage.creator` and `admin` | creator or admin, plus owner access [44 SC/api/RecordApi.java:348-349; SC/service/RecordServiceImpl.java:475-478] |
| Storage `POST /records/{id}:delete` | creator and admin "who is the OWNER" | creator or admin, plus owner access [44 SC/api/RecordApi.java:258-259; SC/service/RecordServiceImpl.java:351-356] |
| Storage `POST /records/delete` | `users.datalake.editors` or `admins` "who is the OWNER" | creator or admin; owner access checked per record [44 SC/api/RecordApi.java:281-282; SC/service/RecordServiceImpl.java:358-368] |
| Storage `DELETE /records/{id}`, `DELETE /records/{id}/versions` | admin "who is the OWNER" (none for versions) | `service.storage.admin`, plus owner access [44 SC/api/RecordApi.java:209-210, 231-232; SC/service/RecordServiceImpl.java:112-122, 381-388] |
| File `GET /v2/files/uploadURL`, `POST /v2/files/metadata` | `service.file.editors` | same [90 FC/api/FileLocationApi.java:125-126; FC/api/FileMetadataApi.java:55-56; FC/constant/FileServiceRole.java:7-9] |
| File `GET /v2/files/{id}/metadata` | `service.file.editors` | `service.file.viewers` [90 FC/api/FileMetadataApi.java:76-77] |
| File `DELETE /v2/files/{id}/metadata` | `users.datalake.editors` or `admins` | `service.file.editors` or `service.file.admin` [90 FC/api/FileMetadataApi.java:95-96] |
| File `GET /v2/files/{id}/downloadURL` | `service.file.viewers` | same [90 FC/api/FileDeliveryApi.java:51-52] |
| File `POST /v2/files/revokeURL` | `service.file.admin` | same [90 FC/api/FileAdminApi.java:47-48] |
| Dataset `PUT /registerDataset`, `POST /metadataRecord/{id}/softDelete`, `.../undelete` | `service.storage.creator` or `admin` | same [118 DC/api/DatasetRegistryApi.java:54-55, 108-109, 124-125] |
| Dataset `GET`/`POST /getDatasetRegistry` | storage creator, admin or viewer | same [118 DC/api/DatasetRegistryApi.java:72-73, 90-91] |
| Dataset `POST /storageInstructions` | `service.dataset.editors` | `service.dataset.editors` or `service.dataset.admin` [118 DC/api/DatasetDmsApi.java:62-63; 67 CC/dms/constants/DatasetConstants.java:25-27] |
| Dataset `GET`/`POST /retrievalInstructions` | `service.dataset.viewers` | viewers, editors or admin [118 DC/api/DatasetDmsApi.java:82-83, 101-102] |
| Dataset `POST /revokeURL` | `service.dataset.admin` | same [118 DC/api/DatasetDmsApi.java:118-119] |
| File DMS (hidden, called by Dataset with the caller's headers, section 2.5) | not in the contract | `storageInstructions`: `service.dataset.editors` only; `retrievalInstructions`: `service.dataset.viewers`; `copy`: `service.storage.creator` or `admin` [90 FC/api/FileDmsApi.java:81-82, 101-102, 122-123; FC/api/FileCollectionDmsApi.java:77-78, 97-98, 118-119] |
| Search `POST /query`, `POST /query_with_cursor`, `DELETE /query_with_cursor/{cursor}` | `users.datalake.viewers`, `editors`, `admins` or `ops` | `service.search.admin` or `service.search.user` [19 SRC/api/SearchApi.java:84-85, 103-104, 130-131; 67 CC/model/search/SearchServiceRole.java:22-23] |
| Schema `GET /schema/{id}`, `GET /schema` | `service.schema-service.viewers` | same [26 SCH/api/SchemaController.java:94-95, 114-115; SCH/constants/SchemaConstants.java:51-52] |
| Schema `POST /schema`, `PUT /schema` | `service.schema-service.editors` | same [26 SCH/api/SchemaController.java:63-64, 161-162] |
| Schema `PUT /schemas/system` | none | a service-principal check, `@authorizationFilterSA.hasPermissions()`; the constants also name `service.system-schema-service.editors` [26 SCH/api/SystemSchemaController.java:48-49; SCH/constants/SchemaConstants.java:190] |
| Register `GET /ddms`, `GET /ddms/{id}`, `GET /ddms/{id}/{type}/{localid}` | `users.datalake.ops`, `admins`, `editors` or `viewers` | same [157 RC/api/DdmsApi.java:127-129, 158-160, 223-225; RC/utils/ServiceRole.java:4-7] |
| Register `POST /ddms` | `users.datalake.editors`, `admins` or `ops` | same [157 RC/api/DdmsApi.java:96-98] |
| Register `DELETE /ddms/{id}` | `users.datalake.ops` or `admins` | same [157 RC/api/DdmsApi.java:188-189] |
| Workflow trigger and run status | `service.workflow.creator`; `service.workflow.viewer` [C-workflow: POST /v1/workflow/{workflow_name}/workflowRun, triggerWorkflow; GET /v1/workflow/{workflow_name}/workflowRun/{runId}, getWorkflowRunById] | not read |
| Legal, Entitlements, Partition, Unit, CRS Catalog, CRS Conversion | none named | not read |

The File contract says members of `users.datalake.editors`, `admins` and `ops` are added to `service.file.editors`
by default, and those plus `users.datalake.viewers` to `service.file.viewers` [C-file: POST /v2/files/metadata,
postFilesMetadata; GET /v2/files/{id}/downloadURL, downloadURL]. No contract states the same for the
`service.storage.*`, `service.dataset.*` or `service.search.*` groups, so a preflight checks those groups themselves
(section 2.11, P1).

## 2. The calls a writer makes

### 2.1 What OSDU Delivery sends today

| Where | Calls |
| --- | --- |
| Record route, `storage` [OD OsduRecordProtocol.cs] | `PUT /api/storage/v2/records` with an array body, up to the flow's batch size (default 100, at most 500; `osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs`), `?skipdupes=true` only when the flow opts in (default off); a 4xx for a whole batch is retried record by record. Verify with `GET /api/storage/v2/records/{id}` and `POST /api/storage/v2/query/records` (100 ids, `attributes: ["id"]`). Removals: `POST /records/{id}:delete`, `POST /records/delete` (500 ids), `DELETE /records/{id}/versions`, `DELETE /records/{id}`. Probe: `GET /api/storage/v2/info`. |
| File route, `file`, and the file half of the manifest route [OD FileUploads.cs] | `GET /api/file/v2/files/uploadURL[?expiryTime=]`, then `PUT` to `Location.SignedURL` (adding `x-ms-blob-type: BlockBlob` for an Azure Blob host), then `POST /api/file/v2/files/metadata` with no `id` and kind `osdu:wks:dataset--File.Generic:1.0.0` by default. A registration whose response was lost is looked up with `POST /api/search/v2/query` on `data.DatasetProperties.FileSourceInfo.FileSource`. The full purge scope also sends `DELETE /api/file/v2/files/{id}/metadata`. Probe: `GET /api/file/v2/info`. |
| Manifest route, `manifest` [OD OsduManifestProtocol.cs] | `POST /api/workflow/v1/workflow/{workflow}/workflowRun`, `GET /api/workflow/v1/workflow/{workflow}/workflowRun/{runId}`, read-back with `POST /api/storage/v2/query/records`, index wait with `POST /api/search/v2/query`. Probe: `GET /api/workflow/v1/info`. |
| Well log route, `ddms` [OD OsduDdmsProtocol.cs] | Its writes go to the Wellbore DDMS (`osdu/specs/wellbore-ddms/INTEGRATION.md`). The history purge goes to Storage `DELETE /records/{id}/versions`. |
| Legal check before a run [OD LegalTagValidator.cs] | `POST /api/legal/v1/legaltags:validate`, 25 names per request, each verdict kept for 10 minutes. |
| Retrieval, reading OSDU back (`osdu/src/SqlFlow.Delivery/Model/RetrievalDefinition.cs`) | `POST /api/search/v2/query_with_cursor`, `POST /api/search/v2/query`, `POST /api/storage/v2/query/records`, probe `GET /api/search/v2/info`. |

### 2.2 Storage upsert: `PUT /records`

Contract [C-storage: PUT /records, createOrUpdateRecords; CreateUpdateRecordsResponse, L1793-1816]:

- Body: a JSON array of `Record`, required. Query parameter `skipdupes` (boolean, optional). Headers
  `data-partition-id` (required) and `x-collaboration` (optional).
- 201 returns `CreateUpdateRecordsResponse {recordCount, recordIds[], skippedRecordIds[], recordIdVersions[]}`.
  Other responses: 400 "Invalid record format.", 401, 403 "User not authorized to perform the action.", 404
  "Invalid acl group.", 500, 502, 503.
- The array has no `maxItems`.

Batch size: 1 to 500 records (`@NotEmpty @Size(max = 500)`, message "Up to 500 records can be ingested at a time")
[44 SC/api/RecordApi.java:128; 67 CC/model/storage/validation/ValidationDoc.java:61].

What is checked, in order:

1. Model validation of each record: the `id` pattern, the kind (`@ValidKind`), `acl` (`@NotNull`, `@ValidAcl`),
   `legal` (`@ValidLegal` on the class), `data` (`@NotEmpty`), `ancestry` (`@ValidRecordAncestry`)
   [67 CC/model/storage/Record.java:49-95].
2. Kind format, then record ids, then ACL domains [44 SC/service/IngestionServiceImpl.java:94-97, 107-174].
3. The existing records and parents are read; parents must exist at the named version; then the caller's access to
   the existing records, the legal tags and countries, and owner access on existing records (or, with the OPA feature
   flag on, the policy service decides) [44 SC/service/IngestionServiceImpl.java:176-189, 245-251].
4. One version is assigned, change blocks are computed, duplicates are dropped when `skipdupes` is true, and the
   batch is persisted [44 SC/service/IngestionServiceImpl.java:191-243, 345-350].

Field rules:

| Field | Rule | Source |
| --- | --- | --- |
| `kind` | Required. Must match `^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$` (the dots in the version part are not escaped). A mismatch is 400: the model's message is "Not a valid record kind. Found: ..."; the service's own check says "Invalid kind". Storage does not check that a schema exists for the kind. | [67 CC/model/storage/validation/ValidationDoc.java:32, 44; CC/model/storage/validation/KindValidator.java:29-32; 44 SC/service/IngestionServiceImpl.java:124-134] |
| `kind` with no schema | Storage documentation: only fields with schema information are indexed, so the schema must exist before ingestion. The Search documentation lists index status 404 for "Schema is missing in Schema service". | [44 docs/docs/index.md:51; 19 docs/docs/api.md:1647-1650] |
| `id` absent | The contract lists `id` as required, but its own description and the operation description say an id is assigned when none is given. The service generates `{tenant}:{kind entity type}:{uuid without dashes}`, where `{tenant}` is the tenant name resolved for the request (in practice the partition). | [C-storage: Record, L1714-1720, L1774-1779; 67 CC/model/storage/Record.java:122-137; 44 SC/service/IngestionServiceImpl.java:170-172; 44 docs/docs/index.md:31] |
| `id` present | Must match `^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+$`. The first segment must equal the tenant name, compared case-insensitively. At most 512 bytes. An id may appear once per request, else 400 "Cannot update the same record multiple times in the same request. Id: ...". | [67 CC/model/storage/validation/ValidationDoc.java:30; CC/model/storage/Record.java:145-163; 44 SC/util/RecordConstants.java:35; SC/service/IngestionServiceImpl.java:148-167] |
| `id` second segment | The error text claims `<tenantId>:<kindSubType>:<uniqueId>`, but `Record.isRecordIdValid` returns true once format and tenant pass: the entity type segment is not compared with the kind. The Dataset service does compare it (section 2.5). Write `{partition}:{entity type of the kind}:{key}` on every route. | [44 SC/service/IngestionServiceImpl.java:154-160; 67 CC/model/storage/Record.java:172-186] |
| `version` | Read-only, assigned by the server. Every record the call writes gets the same version, `currentTimeMillis() * 1000 + random(1..1000)`. Inference: the upsert path never reads a submitted `version`, so `PUT` has no optimistic concurrency. | [C-storage: Record, L1721-1725; 67 CC/model/storage/TransferInfo.java:34-41; 44 SC/mapper/CreateUpdateRecordsResponseMapper.java:27-36; 67 CC/model/storage/RecordMetadata.java:72-79] |
| whole record | An update builds the new version's metadata only from the submitted record (id, kind, acl, legal, tags, ancestry) and its data block only from `data` and `meta`; `createUser`, `createTime` and the version history carry over. `tags` defaults to an empty map. Unknown top-level properties are ignored. Inference: an update is a full replace; leaving out `tags`, `ancestry` or `meta` drops them. | [44 SC/service/IngestionServiceImpl.java:207-229; 67 CC/model/storage/RecordMetadata.java:72-79; CC/model/storage/RecordData.java:59-62; CC/model/storage/Record.java:50, 101-102] |
| `kind` change | Allowed on update; the old kind travels as `previousVersionKind` in the change message. | [44 SC/service/IngestionServiceImpl.java:212-214; SC/service/PersistenceServiceImpl.java:93-96] |
| `acl` | Required; `viewers` and `owners` both non-empty; every entry must match `^data\.[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$`, else "Invalid group name". Every entry's domain must also equal, case-insensitively, the email domain of the caller's first entitlements group, else 400 "Invalid ACL" / "Acl not match with tenant or domain". Inference: this check compares domains only; it does not look the group up. | [C-storage: Acl, L1654-1678; 67 CC/model/entitlements/validation/AclValidator.java:33-66; CC/model/storage/validation/ValidationDoc.java:29; 44 SC/service/IngestionServiceImpl.java:107-122; SC/service/EntitlementsAndCacheServiceImpl.java:78-104] |
| `legal.legaltags` | Required and non-empty, unless the record has `ancestry.parents`; then the parents' tags are used. | [C-storage: Legal, L1679-1708; 67 CC/model/legal/validation/LegalValidator.java:36-67] |
| `legal.otherRelevantDataCountries` | Must be non-empty. The contract says `minItems: 1`; the Storage documentation says "at least 2 values"; the code enforces 1. | [C-storage: Legal, L1690-1697; 67 CC/model/legal/Legal.java:39-45; 44 docs/docs/index.md:38] |
| legal validation | Tags go to Legal `POST /legaltags:validate` in batches of 25; if any is invalid the call fails with 400 "Invalid legal tags" naming one of them. Tags that pass are cached; a cache read error counts as a hit. Country codes must be keys of `otherRelevantDataCountries` from Legal `GET /legaltags:properties`, fetched once per partition into a static map that this class never clears; `US` is always added to the codes checked; a bad code is 400 "The country code '%s' is invalid". The service sets `legal.status` to `compliant`. | [44 SC/service/LegalServiceImpl.java:52-55, 68-99, 143-209; 67 CC/legal/LegalService.java:86-100; 44 SC/service/IngestionServiceImpl.java:288-307] |
| `ancestry.parents` | Each entry is `{recordId}:{version}` (`^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+:[0-9]+$`); `parents` may not be null when `ancestry` is present. The parent id and that exact version must exist, else 404 "Record not found" or "RecordMetadata version not found". The parents' legal tags and countries are added to the child's. | [67 CC/model/storage/validation/RecordAncestryValidator.java:33-58; CC/model/storage/validation/ValidationDoc.java:31; 44 SC/service/IngestionServiceImpl.java:267-286, 360-389; SC/service/LegalServiceImpl.java:101-141] |
| `meta` | Array of free-form objects. Drives normalization on read (section 2.9) and is part of the `skipdupes` comparison. | [C-storage: Record, L1745-1750; 44 SC/util/RecordBlocks.java:105-112] |
| `tags` | Map of string to string. | [C-storage: Record, L1751-1755] |
| `data` | Required, at least one property. | [C-storage: Record, L1737-1741; 67 CC/model/storage/Record.java:86-91] |
| size | No per-record byte limit was found in the Storage files read; the only size rule found is the 512-byte id. | (absence in the 44 files read) |

Access on update: the caller must belong to one of the existing record's owner groups (only the part before `@` is
compared) or to `users.data.root`, else 403 "User is not authorized to update records.". The existing records must
also pass the provider's access check, else 403 "Access denied". With the OPA feature flag on, the policy service
decides instead [44 SC/service/IngestionServiceImpl.java:185-189, 253-265, 309-315, 416-456;
SC/service/EntitlementsAndCacheServiceImpl.java:106-123].

`skipdupes`:

- It applies to updates only, that is to ids that already exist. When true and nothing compared has changed, no
  version is written and the id is returned in `skippedRecordIds` [44 docs/docs/api.md:776-779;
  SC/service/IngestionServiceImpl.java:238-241, 317-328].
- What is compared: the hash of `data`, the hash of `meta`, and the hash codes of `acl`, `legal`, `tags` and
  `ancestry` [44 SC/util/RecordBlocks.java:39-112]. Inference: `kind` and the active or deleted status are not
  compared, so with `skipdupes=true` a `PUT` that changes only `kind` is skipped. This answers the question that keeps
  `SkipDuplicates` off by default in `osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs`: at this commit a change to
  `acl`, `legal` or `tags` alone is not skipped.
- Skipped ids are not in `recordIds` or `recordIdVersions` [44 SC/mapper/CreateUpdateRecordsResponseMapper.java:27-36].
  Both lists are created on the first record added, so when every record is skipped they are null (serialized as null
  or left out, depending on the serializer) [44 SC/response/CreateUpdateRecordsResponse.java:39-56].
- `recordCount` is the number of records submitted, not the number written; the contract describes it as "successfully
  created or updated" [67 CC/model/storage/TransferInfo.java:34-37; 44 SC/mapper/CreateUpdateRecordsResponseMapper.java:29].
  Settle each record from `recordIdVersions` and `skippedRecordIds`, as [OD OsduRecordProtocol.cs] does.
- Soft-deleted records: an update sets the status back to active [44 SC/service/IngestionServiceImpl.java:330-343],
  and the documentation says a deletion "can be reverted later by ingesting record with the same id one more time"
  [44 docs/docs/api.md:255]. Inference: with `skipdupes=true` and an unchanged payload the record is skipped and stays
  deleted, provided the metadata repository returns deleted records to the upsert (provider code, not read). To
  restore a record use `skipdupes=false` or `PATCH /records/{id}` with `{"deleted": false}`.

Atomicity and indexing: a batch writes the blobs, then the metadata. If either step throws, the service tries to
remove the new version from both and rethrows [44 SC/service/PersistenceServiceImpl.java:108-130]. After the commit it
publishes record-changed messages (only when no collaboration context is given, unless the collaboration feature is
on) [44 SC/service/PersistenceServiceImpl.java:72-106]. The Search documentation says indexing follows ingestion and
takes at least 30 seconds (section 5.2).

### 2.3 Storage patches

`PATCH /records` with `application/json` (bulk metadata update) [C-storage: PATCH /records, updateRecordsMetadata;
PatchOperation to BulkUpdateRecordsResponse, L2179-2258]:

- Body `{query: {ids[1..500]}, ops[]}`; an id may carry a `:version` suffix; ids must be unique. Each op is
  `{op, path, value[1..]}` with `op` one of `replace`, `add`, `remove` and `path` exactly `/acl/viewers`,
  `/acl/owners`, `/legal/legaltags` or `/tags`; a path may appear once per request [67 CC/model/storage/RecordQuery.java:39-48;
  CC/model/storage/validation/BulkQueryValidator.java:35-60; CC/model/storage/validation/PatchPathValidator.java:27-36;
  CC/model/storage/validation/PatchOpValidator.java:39-68; 44 SC/validation/impl/MetadataPatchValidator.java:50-60].
- Tag values are `"key:value"` strings for `add` and `replace`, and bare keys for `remove`; ACL values pass the domain
  check and legal values the Legal check [44 docs/docs/api.md:363-551; SC/validation/impl/MetadataPatchValidator.java:62-100].
- 200 when every record was updated; 206 when some were not, listing `notFoundRecordIds`, `unAuthorizedRecordIds` and
  `lockedRecordIds` (a record given with a version that is no longer the latest); 400 when a remove would empty the
  legal tags or an ACL list [44 SC/api/PatchApi.java:83-94; docs/docs/api.md:337-357].
- Metadata updates create no new version and do not change `modifyUser` or `modifyTime` [44 docs/docs/index.md:45].

`PATCH /records` with `application/json-patch+json` (RFC 6902) [C-storage: PATCH /records, updateRecordsMetadata;
PatchRecordsRequestModel to PatchRecordsResponse, L2260-2322]:

- Body `{query: {ids}, ops}`: 1 to 100 ids, no duplicates, each matching the record id pattern; 1 to 100 ops, no two
  identical; `op` one of `replace`, `add`, `remove`; every `path` starts with `/acl/viewers`, `/acl/owners`,
  `/legal/legaltags`, `/tags`, `/kind`, `/ancestry/parents`, `/data` or `/meta` and does not end with `/`; `/kind`
  accepts only `replace`, exactly at `/kind`, with a string value; `remove` on the whole `/acl/viewers`,
  `/acl/owners`, `/legal/legaltags` or `/ancestry/parents` array is refused (an index is required)
  [44 SC/validation/impl/BulkQueryPatchValidator.java:29-65; SC/validation/impl/JsonPatchValidator.java:58-139, 199-243;
  SC/util/RecordConstants.java:23-32; SC/validation/impl/PatchInputValidatorImpl.java:62-123].
- The ids should carry no version; the documentation says so, but the id pattern also accepts `id:version`
  [44 docs/docs/api.md:553-560; SC/validation/impl/BulkQueryPatchValidator.java:57-61].
- Response `{recordCount, recordIds (as id:version), notFoundRecordIds, failedRecordIds, errors}`; 206 when
  `notFoundRecordIds` or `failedRecordIds` is non-empty [44 SC/api/PatchApi.java:109-120].
- If any op touches `/data` or `/meta`, the service reads the records, applies the ops and re-ingests them with
  `skipdupes=false`, so every patched record gets a new version; ops that touch only metadata are applied in place and
  create none [44 SC/service/PatchRecordsServiceImpl.java:92-193; SC/util/JsonPatchUtil.java:70-79].
- A record that would end up with an empty ACL list or no legal tags fails on its own. On the data path the patched
  ACL arrays are de-duplicated; on the metadata path an op that would create a duplicate ACL entry is dropped
  [44 SC/service/PatchRecordsServiceImpl.java:112-127, 152-155; SC/util/JsonPatchUtil.java:81-132].
- Inference: on the data path the records are read with `POST /query/records` semantics, so an id the caller cannot
  see is in neither `notFoundRecordIds` nor `failedRecordIds` [44 SC/service/PatchRecordsServiceImpl.java:107-110;
  SC/service/BatchServiceImpl.java:126-129, 269-304].

`PATCH /records/{id}` with `application/merge-patch+json` (RFC 7396) [C-storage: PATCH /records/{id}, patchRecord;
RecordMergePatchRequest, L2323-2371]:

- Body may carry `kind`, `acl`, `legal`, `data`, `tags`, `ancestry`, `deleted`, `deletedAt`. Missing record: 404.
- Without `deleted`: the record must be active (400 "Record is not in active state, so cannot be patched"). ACLs,
  legal tags and kind are validated, the patch is merged onto the current record, and the result is re-ingested with
  `skipdupes=true`, so a version is written only if something compared changed. The response body is the merged record
  JSON; it does not carry the new version [44 SC/service/RecordServiceImpl.java:463-503, 526-541].
- With `deleted`: only the status changes, with no new version; the response is the current record JSON. Asking for
  the status the record already has is 400 "Record State already updated" [44 SC/service/RecordServiceImpl.java:504-518].
- `deletedAt` is removed from the patch and not used [44 SC/service/RecordServiceImpl.java:492-494].

### 2.4 File service

Upload location, `GET /v2/files/uploadURL?expiryTime=` [C-file: GET /v2/files/uploadURL, getLocationFile;
LocationResponse, L923-931]: returns `{FileID, Location: {...}}`; OSDU Delivery reads `Location.SignedURL` and
`Location.FileSource` [OD FileUploads.cs]. The service generates the file id (a UUID without dashes), signs a staging
location and records it [90 FC/api/FileLocationApi.java:125-135; FC/service/LocationServiceImpl.java:59-89, 117-123].

- Azure: `FileSource` is `/` + `<user id>/<epoch millis>-<yyyy-MM-dd-HH-mm-ss-SSS>/<file id>` in the staging container
  [90 FAZ/service/StorageServiceImpl.java:101-135, 147-149, 159-166].
- Core-plus (GC and baremetal): `FileSource` is `/` + `<uuid>/<file id>` in the staging bucket, and only a signed `PUT`
  URL is produced; nothing is written [90 FCP/service/ObmStorageService.java:83-104, 167-177;
  FCP/repository/ObmStorageRepository.java:53-56, 69-117].

Registering a file, `POST /v2/files/metadata` [C-file: POST /v2/files/metadata, postFilesMetadata; FileMetadata,
L676-712]: 201 returns `FileMetadataResponse {id}`. What the service does [90 FC/service/FileMetadataService.java:62-141]:

1. Checks the kind: four `:`-separated parts, source `wks` and entity `dataset--File.Generic`, both compared
   case-insensitively; anything else is refused [90 FC/service/FileMetadataService.java:198-208;
   FC/constant/FileMetadataConstant.java:15-17].
2. Generates the id and overwrites any `id` in the request (section 4).
3. Copies the staging object named by `data.DatasetProperties.FileSourceInfo.FileSource` to the persistent area.
4. Computes the checksum and writes it (below).
5. Upserts the record through Storage `PUT /records` (no `skipdupes`) [90 FC/service/storage/DataLakeStorageService.java:41-46].
6. Publishes status and dataset-details events.
7. Deletes the staging object, only if the record reads back; a failed delete is logged and ignored
   [90 FC/service/FileMetadataService.java:143-158].

On a Storage failure or an unexpected error the persistent copy is deleted and the error returned
[90 FC/service/FileMetadataService.java:121-139]. Every accepted `POST` therefore creates a new record; to change an
existing `File.Generic` record, write it with Storage `PUT /records` under its id.

Checksum:

- If the provider returns one, the service sets `FileSourceInfo.Checksum` and `FileSourceInfo.ChecksumAlgorithm`,
  overwriting what the request carried [90 FC/service/FileMetadataService.java:89-96]. The algorithm enum is `NONE`,
  `MD5`, `SHA1`, `SHA256` [90 FC/constant/ChecksumAlgorithm.java:3-8]; a provider that implements nothing returns no
  checksum [90 FC/provider/interfaces/IStorageUtilService.java:32-38].
- Azure uses the blob's Content-MD5 when set, otherwise computes MD5, except above a configured blob size, where no
  checksum is written [90 FAZ/service/StorageUtilServiceImpl.java:96-144].
- Core-plus takes the checksum from the object store driver and the algorithm from the environment
  [90 FCP/service/ObmCloudStorageUtilServiceImpl.java:122-143].
- Do not send `Checksum` through this route: the contract's pattern for it is malformed (section 7).

Landing zone and expiry:

- An uploaded file is deleted automatically if its metadata is not posted within 24 hours; the service never checks
  that an upload happened and never looks inside files [90 docs/docs/File-Service.md:52, 105-112].
- `expiryTime` must be absent or match `^[0-9]+M$`, `^[0-9]+H$` or `^[0-9]+D$` (upper case only); any other value is
  400. Absent means 1 hour; more than 7 days is capped at 7 days [90 FC/util/ExpiryTimeUtil.java:21-27, 46-64, 98-108].
  The contract says 1 hour by default and 7 days at most [C-file: GET /v2/files/{id}/downloadURL, downloadURL]; the
  File documentation says the download URL defaults to 7 days [90 docs/docs/File-Service.md:83-89].
- Core-plus ignores `expiryTime` on `uploadURL`: the four-argument `createSignedUrl` is an interface default that
  drops the parameters, core-plus implements only the three-argument form, and it signs with default parameters
  [90 FC/provider/interfaces/IStorageService.java:53-56; FCP/service/ObmStorageService.java:83-104;
  FCP/repository/ObmStorageRepository.java:53-56]. Azure passes it through [90 FAZ/service/StorageServiceImpl.java:101-135].

Other public routes:

| Route | operationId | Behaviour | Source |
| --- | --- | --- | --- |
| `GET /v2/files/{id}/downloadURL?expiryTime=` | downloadURL | Returns `{SignedUrl}`. | [C-file: GET /v2/files/{id}/downloadURL, downloadURL; DownloadUrlResponse, L916-922] |
| `GET /v2/files/{id}/metadata` | getFileMetadataById | Returns a `RecordVersion` (with `version`); 404 "Record Not Found" when Storage has no such record. | [C-file: GET /v2/files/{id}/metadata, getFileMetadataById; RecordVersion, L874-915; 90 FC/service/FileMetadataService.java:160-196] |
| `DELETE /v2/files/{id}/metadata` | deleteFileMetadataById | Reads the record, soft-deletes it through Storage `POST /records/{id}:delete`, then deletes the persistent file. The record can be restored; the bytes cannot. | [C-file: DELETE /v2/files/{id}/metadata, deleteFileMetadataById; 90 FC/service/FileMetadataService.java:216-240; FC/service/storage/DataLakeStorageService.java:55-60] |
| `POST /v2/files/revokeURL` | revokeURL | Body is a map of strings (on Azure `resourceGroup` and `storageAccount`); 204. | [C-file: POST /v2/files/revokeURL, revokeURL; 90 FAZ/service/StorageServiceImpl.java:240-254] |

Hidden or legacy routes, absent from the contract: `POST /v2/getLocation` and `POST /v2/getFileLocation` (documented as
to be deprecated) [90 FC/api/FileLocationApi.java:65-111; docs/docs/File-Service.md:32-42, 97-103];
`POST /v2/getFileList` [90 FC/api/FileListApi.java:50, 71-73]; `POST /v2/delivery/GetFileSignedUrl`, role
`service.delivery.viewer` [90 FC/api/DeliveryApi.java:52-53, 81-84; FC/constant/DeliveryRole.java:20]; and the DMS
endpoints of section 2.5. The public File contract has no collection route; collections go through the Dataset
service.

### 2.5 Dataset service

Routes:

| Route | operationId | Body and parameters | Source |
| --- | --- | --- | --- |
| `PUT /registerDataset` | createOrUpdateDatasetRegistry | `{datasetRegistries: Record[1..20]}` ("Only 20 Dataset Registries can be ingested at a time"). 201 returns `{datasetRegistries: Record[]}`, read back from Storage. | [C-dataset: PUT /registerDataset, createOrUpdateDatasetRegistry; CreateDatasetRegistryRequest, L789-800; GetCreateUpdateDatasetRegistryResponse, L915-921; 118 DC/model/request/CreateDatasetRegistryRequest.java:28-31; DC/model/validation/DatasetRegistryValidationDoc.java:27] |
| `POST /storageInstructions?kindSubType=&expiryTime=` | storageInstructions | No body. `kindSubType` required. 200 returns `{storageLocation: object, providerKey}`. | [C-dataset: POST /storageInstructions, storageInstructions; GetDatasetStorageInstructionsResponse, L939-947; 67 CC/dms/model/StorageInstructionsResponse.java:30-33] |
| `GET /retrievalInstructions?id=&expiryTime=` | retrievalInstructions | One dataset id. | [C-dataset: GET /retrievalInstructions, retrievalInstructions] |
| `POST /retrievalInstructions?expiryTime=` | retrievalInstructions_1 | `{datasetRegistryIds[1..20]}`; returns `{datasets: [{datasetRegistryId, retrievalProperties, providerKey}]}`. | [C-dataset: POST /retrievalInstructions, retrievalInstructions_1; GetDatasetRegistryRequest to RetrievalInstructionsResponse, L948-977] |
| `GET /getDatasetRegistry?id=`, `POST /getDatasetRegistry` | getDatasetRegistry, getDatasetRegistry_1 | Reads through Storage `POST /query/records`. | [C-dataset: GET /getDatasetRegistry, getDatasetRegistry; POST /getDatasetRegistry, getDatasetRegistry_1; 118 DC/service/DatasetRegistryServiceImpl.java:137-153] |
| `POST /metadataRecord/{id}/softDelete` | deleteMetadataById | Reads the record (404 if absent), copies it into a deleted-dataset store, then soft-deletes it in Storage; if that fails the copy is removed. 204. | [C-dataset: POST /metadataRecord/{id}/softDelete, deleteMetadataById; 118 DC/service/DatasetRegistryServiceImpl.java:155-202] |
| `POST /metadataRecord/{id}/undelete` | undeleteMetadataById | Reads the saved copy (404 if absent), removes it from the store, and upserts it through Storage; returns the saved record. | [C-dataset: POST /metadataRecord/{id}/undelete, undeleteMetadataById; 118 DC/service/DatasetRegistryServiceImpl.java:204-246] |
| `POST /revokeURL?kindSubType=` | revokeURL | Body is a map of strings; forwarded to the DMS; 204. | [C-dataset: POST /revokeURL, revokeURL; 118 DC/service/DatasetDmsServiceImpl.java:121-152] |

The common library's Storage client sends the upsert as `PUT /records`, the read-back as `POST /query/records` and the
soft delete as `POST /records/{id}:delete` [67 CC/storage/StorageService.java:65-68, 81-87, 100-103]. Inference: a
registration never uses `skipdupes`, so each call writes a new version; an undelete is a new upsert, so it writes a new
version too; and a registered record the caller cannot see is left out of the response.

`expiryTime` has the contract pattern `\d+([mhd]|[MHD])$`, and without it the URL is valid for 1 hour
[C-dataset: POST /storageInstructions, storageInstructions, L116-126]; the Dataset service applies the same pattern
[118 DC/controller/DatasetDmsController.java:40; DC/api/DatasetDmsApi.java:67-68]. The File service accepts upper-case
units only (section 2.4), so send `M`, `H` or `D`.

What `registerDataset` does [118 DC/service/DatasetRegistryServiceImpl.java:104-135]:

1. Kind: must match `^[\w\-\.]+:[\w\-\.]+:dataset--+[\w\-\.]+:\d+.\d+.\d+$` (group type `dataset`), else 400 "The
   record '%s' does not have a valid kind" [118 DC/service/DatasetRegistryServiceImpl.java:74, 273-278, 323-328].
2. Id, optional: Storage generates one when absent. A given id must pass the format check with the
   `data-partition-id` as the tenant, and its second segment must equal the kind's entity type (case-insensitive),
   else 400 "Invalid record id" [118 DC/service/DatasetRegistryServiceImpl.java:249-266, 280-284]. This is stricter
   than Storage.
3. Schema: the kind's schema must exist (Schema service `getSchema`); otherwise the Schema service's status and message
   are passed back. The record is not validated against the schema [118 DC/service/DatasetRegistryServiceImpl.java:286-316].
4. DMS: the service looks up the kind's entity type first as an exact key, then as the catch-all made of the text
   before the first `.` plus `.*` (for example `dataset--File.*` for `dataset--File.Image.JPEG`). Neither: 400 "No DMS
   handler for kindSubType '%s' is registered" [118 DC/service/DatasetRegistryServiceImpl.java:337-343, 366-398;
   DC/model/validation/DmsValidationDoc.java:25].
5. Copy: when the DMS supports a staging location, the service sends `POST {dms base url}/copy` with
   `{datasetSources: [records]}` and the caller's own headers; any `success: false` is 400 "Invalid dataset metadata"
   [118 DC/service/DatasetRegistryServiceImpl.java:345-364; DC/dms/DmsService.java:81-90, 97-112;
   67 CC/dms/model/CopyDmsRequest.java:30-36; CC/dms/model/CopyDmsResponse.java:29-32].
6. Upsert: all records go to Storage, so every rule of section 2.2 applies, and are read back
   [118 DC/service/DatasetRegistryServiceImpl.java:117-134].

Inference: if the Storage upsert fails after the copy, nothing undoes the copy. Registering an existing id again runs
the copy again, so the staging object must still be there. The Dataset documentation says the service verifies nothing
about uploads [118 docs/docs/validations.md:3-7].

`storageInstructions` looks up the DMS the same way; no match is 400 "No DMS handler for resource type '%s' is
registered", and a DMS with `allowStorage=false` gives 405 "DMS - Storage Not Supported"
[118 DC/service/DatasetDmsServiceImpl.java:57-98; DC/model/validation/DmsValidationDoc.java:24-26].
`retrievalInstructions` routes each id by its second segment, and an id whose first segment is not the partition is
400 "Dataset Registry: '%s' is an Invalid ID" [118 DC/service/DatasetDmsServiceImpl.java:100-119, 155-200].

`expiryTime` on `storageInstructions` does not reach the DMS as a query parameter: the Dataset service's DMS client
sends it as the JSON body of `POST {dms}/storageInstructions`, while the File DMS reads it from the query string
[118 DC/dms/DmsService.java:57-66, 97-112; 67 CC/http/HttpRequest.java:50-52; 90 FC/api/FileDmsApi.java:81-87]. Both
the core factory and the Azure factory build that client [118 DC/dms/DmsFactory.java:27-32;
DAZ/di/AzureDmsFactory.java:36-38; DAZ/service/AzureDmsService.java:25-41]. `retrievalInstructions` does pass it as a
query parameter [118 DC/dms/DmsService.java:68-79, 123-140]. Inference: signed upload locations from this route have
the File service's default lifetime of 1 hour, whatever `expiryTime` says. On core-plus the File service would ignore
it anyway [90 FC/provider/interfaces/IStorageService.java:65-78; FCP/service/ObmStorageService.java:106-118;
FC/provider/interfaces/IFileCollectionStorageService.java:49-51; FCP/service/ObmCollectionStorageService.java:98-118].

DMS map per provider:

| Provider | Map | Source |
| --- | --- | --- |
| Azure | `dataset--File.*` to `http://file/api/file/v2/files`, `dataset--FileCollection.*` to `http://file/api/file/v2/file-collections`, `dataset--ConnectedSource.*` to `http://eds-dms/api/eds/v1`; all three with `stagingLocationSupported=true`. | [118 provider/dataset-azure/src/main/resources/application.properties:57-63; DAZ/service/DatasetDmsServiceMapImpl.java:41-67; devops/azure/chart/templates/deployment.yaml:118-123] |
| Core-plus, GC | Read from the `DmsServiceProperties` datastore kind; the keys may only be `dataset--File.*`, `dataset--FileCollection.*` or `dataset--ConnectedSource.*`, any other key is a 500 while the map is built. `DMS_API_BASE` overrides every base URL for local use. The GC example maps File to `.../api/file/v2/files` and FileCollection to `.../api/file/v2/file-collections`. | [118 DCP/dms/DatasetDmsServiceMapImpl.java:50-78; provider/dataset-gc/docs/gc/README.md:35, 40-49] |
| IBM | `dataset--File.*` and `dataset--FileCollection.*`, both staging-capable. | [118 provider/dataset-ibm/src/main/java/org/opengroup/osdu/dataset/provider/ibm/DatasetDmsServiceMapImpl.java:27-35] |

`DmsServiceProperties` has `dmsServiceBaseUrl`, `allowStorage` (default true), `apiKey` and `stagingLocationSupported`
(default false) [118 DC/dms/DmsServiceProperties.java:24-36].

The File DMS behind the Dataset routes: two hidden controllers, `/v2/files/{storageInstructions|retrievalInstructions|copy}`
and `/v2/file-collections/{...}`, all `POST`, with the roles in section 1.1 [90 FC/api/FileDmsApi.java:58-61, 81-127;
FC/api/FileCollectionDmsApi.java:56-57, 77-123]. The Dataset service forwards the caller's headers, so the caller's
token needs those roles too [118 DC/dms/DmsService.java:97-112]. Inference: a caller holding only
`service.dataset.admin` passes the Dataset check but fails the File DMS `storageInstructions` check.

| Operation | Azure (`providerKey` `AZURE`) | Core-plus (`providerKey` from the environment) | Common logic |
| --- | --- | --- | --- |
| File storage instructions | `storageLocation = {signedUrl, fileSource, createdBy, expiryTime}`, `fileSource` as for `uploadURL` [90 FAZ/service/StorageServiceImpl.java:79, 292-309; FAZ/model/AzureFileDmsUploadLocation.java:28-33] | `{signedUrl, createdBy, fileSource}`, `fileSource` = `/` + `<uuid>/<file id>`; only a signed `PUT` URL is made [90 FCP/service/ObmStorageService.java:106-118; FCP/repository/ObmStorageRepository.java:53-56] | A new random file id per call [90 FC/service/FileDmsServiceImpl.java:175-182] |
| File retrieval | `retrievalProperties = {signedUrl, fileSource, createdBy, expiryTime}` [90 FAZ/service/StorageServiceImpl.java:260-290] | `{signedUrl, createdBy, fileSource}` with default expiry [90 FCP/service/ObmStorageService.java:134-160] | Reads the records through Storage; the path is `DatasetProperties.FileSourceInfo.FileSource`, else `PreloadFilePath`; no `DatasetProperties` or no `FileSourceInfo` is 400, neither path is 500, a trailing `/` is 500; the path is resolved against the persistent area [90 FC/service/FileDmsServiceImpl.java:73-91, 122-173]. Inference: both providers' signing calls leave `fileSource` unset, so it comes back empty [90 FAZ/service/StorageServiceImpl.java:232-237; FCP/service/ObmStorageService.java:120-132]. |
| File copy | same | same | Copies staging(FileSource) to persistent(FileSource) and returns `{success, datasetBlobStoragePath}` per record. No checksum is computed and the staging object is not deleted [90 FC/service/FileDmsServiceImpl.java:98-120]. |
| Collection storage instructions | `{signedUrl, fileCollectionSource, createdBy, expiryTime}` (the location class also has `fileCount` and `fileNames`, not set here); `fileCollectionSource` = `/` + `<user id>-<epoch millis>-<timestamp>-<directory id>` in the Data Lake staging file system [90 FAZ/service/FileCollectionStorageServiceImpl.java:81, 102-120, 208-238, 292-299; FAZ/model/AzureFileCollectionDmsUploadLocation.java:30-37] | `{url, createdBy, fileCollectionSource, signingOptions}`; `fileCollectionSource` is the bare directory id (no leading `/`); `signingOptions` is the object store driver's map for prefix `<directory id>/` in the staging bucket, with default expiry [90 FCP/service/ObmCollectionStorageService.java:98-157] | A new random directory id per call [90 FC/service/FileCollectionDmsServiceImpl.java:58-65] |
| Collection retrieval | `{signedUrl (read SAS for the directory), fileCollectionSource, fileNames[], fileCount, createdBy, expiryTime}` [90 FAZ/service/FileCollectionStorageServiceImpl.java:155-197, 248-285]. Inference: `fileCollectionSource` comes back empty, as the signing call does not set it. | signed directory properties [90 FCP/service/ObmCollectionStorageService.java:165-180] | Resolves `DatasetProperties.FileCollectionPath` against the persistent area [90 FC/service/FileCollectionDmsServiceImpl.java:106-122] |
| Collection copy | same | Builds the location as protocol + bucket + `FileCollectionPath` with no separator [90 FCP/service/ObmCollectionStorageUtilService.java:49-69]. Inference: on core-plus `FileCollectionPath` needs a leading `/`, which the storage instructions leave out. | Copies the whole directory from staging to persistent; no `DatasetProperties` or no `FileCollectionPath` is 400 [90 FC/service/FileCollectionDmsServiceImpl.java:124-177; FC/model/filecollection/DatasetProperties.java:39-47] |

Registering a file collection:

1. `POST /api/dataset/v1/storageInstructions?kindSubType=dataset--FileCollection.<Type>`; keep `providerKey` and
   `storageLocation`.
2. Upload every member file under the returned directory with provider tooling: the directory SAS on Azure, `url` and
   `signingOptions` on core-plus. OSDU checks nothing about this upload [118 docs/docs/validations.md:3-7].
3. `PUT /api/dataset/v1/registerDataset` with `{datasetRegistries: [record]}`, the record shaped as in section 3.3.
4. The service copies the staging directory to the persistent area, upserts through Storage and returns the stored
   record.
5. Verify with `GET /api/dataset/v1/retrievalInstructions?id=...` or the `POST` form (1 to 20 ids).

A single file through the Dataset route follows the same steps with `kindSubType=dataset--File.<Type>` and
`data.DatasetProperties.FileSourceInfo.FileSource` set to the returned `fileSource`.

Dataset route compared with the File route:

| Aspect | File service (`/api/file/v2/files/...`) | Dataset service (`/api/dataset/v1/...`) |
| --- | --- | --- |
| Kinds | `<any>:wks:dataset--File.Generic:<any>` only [90 FC/service/FileMetadataService.java:198-208] | Any `dataset--*` kind whose entity type maps to a DMS (File.\*, FileCollection.\*, ConnectedSource.\* on Azure) |
| Record id | Always generated; a request `id` is overwritten [90 FC/service/FileMetadataService.java:80-81] | Client id allowed and checked, so a repeated call with the same id updates the same record |
| Batch | 1 record per call [C-file: FileMetadata, L676-712] | 1 to 20 records per call |
| Schema check | Kind parts only | Schema must exist |
| Checksum | Computed after the copy and written to `FileSourceInfo` | Not computed |
| Staging clean-up | Staging object deleted after a successful upsert | No delete in the copy path |
| Upload location | `GET /v2/files/uploadURL` | `POST /storageInstructions` |
| Upload expiry | Honoured on Azure, ignored on core-plus | Not forwarded (above) |
| Roles | `service.file.editors` | `service.dataset.editors`, plus `service.storage.creator` for registration and the forwarded copy |
| Collections | Not supported | Supported |
| Delete | `DELETE /v2/files/{id}/metadata` removes the bytes | `softDelete` keeps the bytes and `undelete` reverses it |

#### 2.5.1 Uploading to a staging location, per provider

`storageInstructions` hands out a location the client uploads to with the provider's own protocol. No OSDU contract
describes that upload, and OSDU checks nothing about it [118 docs/docs/validations.md:3-7]. What each provider hands
out, and how its own tests upload to it:

| Provider (`providerKey`) | Single file (`dataset--File.*`) | File collection (`dataset--FileCollection.*`) | Source |
| --- | --- | --- | --- |
| Azure (`AZURE`) | `signedUrl` is a Blob SAS (`sr=b`, `sp=cw`) on `<user id>/<epoch millis>-<date>/<32 hex file id>` in the staging container of the partition's regular storage account, which the service creates empty first; the file id is random, not the client's file name. `fileSource` is `/` and that path. Inference: the upload is `PUT <signedUrl>` with `x-ms-blob-type: BlockBlob`. | `signedUrl` is a Data Lake directory SAS (`sr=d`, `sp=racwl`, `sdd=<segments>`, HTTPS only, a user delegation SAS under a managed identity) on `https://<account>.dfs.core.windows.net/<staging file system>/<directory>`, in the partition's hierarchical account; the service creates the directory first, and the directory is one segment, `<user id>-<epoch millis>-<timestamp>-<32 hex>`. `fileCollectionSource` is `/` and the directory. Each file: `PUT <directory>/<name>?resource=file`, `PATCH ...?action=append&position=<n>` with each part, then `PATCH ...?action=flush&position=<length>`, as the File service's own Azure test does it through the Data Lake client. | [90 FAZ/service/StorageServiceImpl.java:79, 101-135, 147-166, 292-309; FC/service/FileDmsServiceImpl.java:60-71; FAZ/service/FileCollectionStorageServiceImpl.java:81, 102-120, 208-238, 292-299; FAZ/repository/DataLakeRepository.java:52-78; testing/file-test-azure/.../Helper/DataLakeHelper.java:15-26; 77 DataLakeStore.java:86-106, 116-155; DataLakeClientFactoryImpl.java:113-134; SDK DataLakeSasImplUtil.java:254-259, 300-309; PathSasPermission.java:311-331] |
| Core-plus on MinIO (`ANTHOS`) | `signedUrl` is a presigned `PUT` for `<uuid>/<dataset id>` with no signed headers, so the client sends a plain `PUT`; `fileSource` is `/<uuid>/<dataset id>`. | `url` is the unsigned staging bucket URL (`<endpoint>/<bucket>/`, the external endpoint when one is set), `fileCollectionSource` the bare 32 hex directory id, and `signingOptions` a presigned POST policy with a `starts-with` condition on the key: `x-amz-algorithm`, `x-amz-credential`, `x-amz-security-token` (with session credentials only), `x-amz-date`, `policy`, `x-amz-signature`, and no `key`. Each file: `POST <url>` as `multipart/form-data` with every entry, then `key=<directory>/<name>`, then the `file` part last. The expiry is the driver's default: `expiryTime` has no overload to reach it. | [90 FCP/service/ObmStorageService.java:83-118, 167-177; FCP/repository/ObmStorageRepository.java:53-117; FCP/service/ObmCollectionStorageService.java:98-157; FCP/repository/ObmCollectionStorageRepository.java:51-54, 85-87; FC/provider/interfaces/IFileCollectionStorageService.java:49-51; 1441 OC/Driver.java:140-148; MIN/MinioDriver.java:442-464; MIN/MinioUrlProvider.java:39-50; testing/obm-test-minio/.../MinioFullFlowIT.java:64-98; SDK minio PostPolicy.java:185-236] |
| Core-plus on S3 (`S3`) | As on MinIO. | As on MinIO, with the policy's own field names (`policy`, `X-Amz-Algorithm`, `X-Amz-Credential`, `X-Amz-Date`, `X-Amz-Signature`) and `key=<directory>/`, to which the client appends the file name. Inference: a form field the policy does not name (a `Content-Type`, say) breaks the policy, `X-Amz-Date` is the service's local time with a literal `Z`, and the signing keys come from the service's properties even in partition mode. | [1441 S3/S3PostPolicyGenerator.java:37-86; S3/S3Driver.java:424-443; testing/obm-test-s3/.../S3FullFlowIT.java:73-102] |
| Core-plus on Google Cloud Storage (`GCP`) | As on MinIO. | `url` is `https://storage.googleapis.com/<bucket>/`; `signingOptions` is `{bucket, filepath, connectionString}`, `connectionString` a downscoped token that allows `storage.objectAdmin` on the objects under the folder. Each file: `POST https://storage.googleapis.com/upload/storage/v1/b/<bucket>/o?uploadType=media&name=<filepath><name>` with `Authorization: Bearer <connectionString>`. | [1475 GS/GcsDriver.java:73-79, 370-394, 489-505; GS/GcsUrlProvider.java:29-48; testing/gc-obm-test-gs/.../GcsFullFlowIT.java:53-61, 93-116] |
| IBM | Not read. | `{connectionString, credentials, unsignedUrl}`: temporary credentials (`AccessKeyId`, `SecretAccessKey`, `SessionToken`, `Expiration`) that allow `PutObject` and multipart uploads on the key and under it, and `s3://<bucket>/<directory id>`; no signed URL. Inference: the object store endpoint is not returned, so a client has to know it already. | [90 FIBM/service/FileCollectionStorageServiceImpl.java:44, 70-84; FIBM/model/file/TemporaryCredentials.java:20-41; FIBM/service/STSHelper.java:40-41, 166-211] |

Inference: the Azure directory SAS also signs the directory's blob path, so a blob host `PUT` with
`x-ms-blob-type: BlockBlob` should validate too [SDK DataLakeSasImplUtil.java:338-344]; no OSDU code uploads that way.

What `FileCollectionPath` must be: Azure's `fileCollectionSource` carries its leading `/` and is registered as it is (the
File service's Azure test sets `FileCollectionPath` to it) [90 testing/file-test-azure/.../apitest/TestFileCollection.java:84-86].
Core-plus builds the staging location as protocol, bucket and `FileCollectionPath` with no separator, so the path needs
a leading `/` and, inference, a trailing one: `/<directory id>/` [90 FCP/service/ObmCollectionStorageUtilService.java:49-69;
1441 OC/ObmPathProvider.java:36-67].

The copy a registration runs [90 FC/service/FileCollectionDmsServiceImpl.java:124-177; FC/service/FileDmsServiceImpl.java:98-120, 145-173]:

- A collection's copy reads only `DatasetProperties.FileCollectionPath` (400 without it), never `FileSourceInfos`.
- On Azure it renames the whole staging directory into the persistent file system: every file moves and the staging
  directory is consumed [90 FAZ/service/CloudStorageOperationImpl.java:116-164; 77 DataLakeStore.java:186-193].
  Inference: registering the same collection again needs its files uploaded again, to a new location.
- On core-plus it lists the objects under the prefix and copies each to the same key, fails on an empty prefix, and
  keeps the staging objects [90 FCP/service/ObmCloudStorageOperationImpl.java:97-141]. MinIO lists recursively and
  Google Cloud Storage pages through every result [1441 MIN/MinioDriver.java:295-301, 497-521;
  1475 GS/GcsDriver.java:143-155, 426-452]; S3 makes one `listObjects` call [1441 S3/S3Driver.java:503-588].
  Inference: on S3 only the first 1000 files of a collection are copied.
- IBM copies by prefix too and keeps the staging objects [90 FIBM/service/IBMCloudStorageOperationImpl.java:58-89].
- A single file's copy needs `FileSourceInfo.FileSource` (or `PreloadFilePath`) and keeps the staging object, where the
  File service's own `POST /v2/files/metadata` deletes it after its copy [90 FC/service/FileMetadataService.java:84-158].

`retrievalProperties` per provider [118 DC/api/DatasetDmsApi.java:82-105; 67 CC/dms/model/RetrievalInstructionsResponse.java:29-35]:

| Provider and type | `retrievalProperties` | Source |
| --- | --- | --- |
| Azure file | `{signedUrl, fileSource, createdBy, expiryTime}` | [90 FAZ/service/StorageServiceImpl.java:260-290] |
| Azure collection | `{signedUrl (a read-only dfs SAS), fileNames, fileCount, ...}` | [90 FAZ/service/FileCollectionStorageServiceImpl.java:176-197] |
| Core-plus file | `{signedUrl, createdBy, fileSource}` | [90 FCP/service/ObmStorageService.java:146-160] |
| MinIO and S3 collection | `{retrievalPropertiesList: [{signedUrl, fileSource}]}` | [1441 MIN/MinioSignedDirectoryPropertiesResolver.java:44-72] |
| Google Cloud Storage collection | `{token, fileSource}` | [1475 GS/GcsSignedDirectoryPropertiesResolver.java:38-53] |

An id the File service cannot read is left out of `datasets`: the Dataset service checks only the id's format, tenant
and entity type, and Storage answers the File service's `POST /query/records` with unknown and soft-deleted ids under
`invalidRecords`, which the File service ignores [118 DC/service/DatasetDmsServiceImpl.java:155-192;
90 FC/service/FileDmsServiceImpl.java:73-91; 44 SC/api/QueryApi.java:105-111; SC/service/BatchServiceImpl.java:99-124].
Inference: a request whose ids are all unreadable answers `200 {"datasets": []}`, so a writer checks that every id it
registered is listed.

A record the Dataset service registered can be read through the File service too: `GET /v2/files/{id}/metadata` only
reads Storage, and `GET /v2/files/{id}/downloadURL` reads `FileSourceInfo.FileSource` and `Name` by JSON path with no
kind check [90 FC/service/FileMetadataService.java:74, 160-208; FC/service/FileDeliveryService.java:44-117].
Inference: the download works once the registration has copied the file, since the copy keeps the relative key; a
`.segy` file is served as `application/octet-stream` [90 FC/service/FileDeliveryService.java:119-133].

How OSDU Delivery uploads [OD DatasetUploads.cs, DatasetCollections.cs]: a single file with `PUT <signedUrl>` and the
flow's upload headers (a blob type on an Azure blob host); a collection by the provider's protocol above, the Data Lake
appends in parts of at most 100 MiB, each request stating the service version the SAS was signed with; a location with
temporary credentials and no endpoint (IBM) holds the record and says why. A signed location is never logged, stored or
returned, so a retry asks for a new one unless every upload of the earlier try completed.

### 2.6 Manifest route: the core calls around the workflow

The manifest route registers its files as in section 2.4, triggers the workflow with
`POST /v1/workflow/{workflow_name}/workflowRun` (`TriggerWorkflowRequest {runId, executionContext}`) and polls
`GET /v1/workflow/{workflow_name}/workflowRun/{runId}`; both return `WorkflowRunResponse`, whose `status` enum is
`INPROGRESS`, `PARTIAL_SUCCESS`, `SUCCESS`, `FAILED`, `SUBMITTED`, while the run listing's `WorkflowRun.status` is
lower case (`submitted`, `running`, `finished`, `failed`, `success`, `queued`) [C-workflow: POST
/v1/workflow/{workflow_name}/workflowRun, triggerWorkflow; GET /v1/workflow/{workflow_name}/workflowRun/{runId},
getWorkflowRunById; TriggerWorkflowRequest, WorkflowRunResponse, WorkflowRun, L982-1137]. It then reads the records
back with Storage `POST /query/records` (section 5.1) and waits for the index with Search `POST /query` (section 5.2)
[OD OsduManifestProtocol.cs]. The manifest payload and the ingestion workflow are covered by the ingestion workflows
brief, `osdu/specs/workflows/INTEGRATION.md`.

### 2.7 DDMS discovery: Register service

| Route | operationId | Notes | Source |
| --- | --- | --- | --- |
| `GET /ddms?type=` | queryDMS | `type` required, `^[A-Za-z0-9]{1,50}`; returns `Ddms[]`. | [C-register: GET /ddms, queryDMS; 157 RC/api/DdmsApi.java:158-165] |
| `POST /ddms` | postDMS | Body `Ddms`; 201; 409 when the id exists. | [C-register: POST /ddms, postDMS; 157 RC/api/DdmsApi.java:96-103; RAZ/ddms/DdmsRepository.java:54-74; RCP/ddms/OsmDdmsRepository.java:64-89] |
| `GET /ddms/{id}` | getDMS | `id` `^[A-Za-z0-9-]{2,50}`; 404 when absent. | [C-register: GET /ddms/{id}, getDMS; 157 RC/api/DdmsApi.java:127-134; RAZ/ddms/DdmsRepository.java:76-86; RCP/ddms/OsmDdmsRepository.java:91-97] |
| `DELETE /ddms/{id}` | deleteDMS | 204; 404 when absent. | [C-register: DELETE /ddms/{id}, deleteDMS; 157 RC/api/DdmsApi.java:188-200] |
| `GET /ddms/{id}/{type}/{localid}` | redirectToDms | `id` and `type` `^[\w\.-]{2,50}`; `localid` must match the record id pattern and may carry a version; 307 with `location`. | [C-register: GET /ddms/{id}/{type}/{localid}, redirectToDms; 157 RC/api/DdmsApi.java:223-233] |

Model [C-register: Ddms, L1677-1709; RegisteredInterface, L1710-1723; 157 RC/ddms/model/Ddms.java:40-64;
RC/ddms/model/RegisteredInterface.java:49-58]:

- `Ddms`: `id` (required, `^[A-Za-z0-9-]{2,50}`), `name` (required, `^[A-Za-z0-9- ]{2,50}`), `description`
  (`^[A-Za-z0-9. ]{0,255}`), `contactEmail`, `createdDateTimeEpoch`, `interfaces[]` (unique items).
- `RegisteredInterface`: `entityType` (required, `^[\w\.-]{2,50}`) and `schema` (required object).
- A registration needs 1 to 10 interfaces and no two with the same `entityType` [157
  RC/ddms/model/validators/RegisteredInterfacesValidator.java:38-50].

The interface `schema` is an OpenAPI 3 document ("Create a DDMS registration using an OpenApi spec V3 document")
[C-register: POST /ddms, postDMS; 157 docs/docs/HowToBecomeADDMS.md:17-107]:

- It must parse as OpenAPI 3 and declare exactly one `servers` entry ("There must be one server only")
  [157 RC/ddms/model/validators/OpenApi3SpecValidator.java:41-70; RC/ddms/model/ExtensionValidationMessages.java:23].
- Retrieval: the interface must have exactly one `GET` operation carrying the `x-ddms-retrieve-entity` extension (the
  code checks that the extension is present; the documentation sets it to `true`), and its path must have as many
  template variables as parameters given, which is one [157 RC/ddms/model/RegisteredInterface.java:84-121;
  RC/ddms/model/OpenApiExtensionNames.java:20; RC/utils/UriResolver.java:31-55; docs/docs/HowToBecomeADDMS.md:21-23, 70].
- Only the servers rule is checked at registration. A missing or ambiguous retrieval operation shows up at redirect
  time as 404 "Either the DDMS with the given id or the retrieval endpoint on DDMS interface was not found"
  [157 RC/ddms/services/ConsumptionService.java:40-66]. The server URL must be `http` or `https`, also checked at
  redirect time [157 RC/utils/UriResolver.java:69-72].
- Registering more of the API is optional [157 docs/docs/HowToBecomeADDMS.md:21-23].

Finding the DDMS for an entity type:

1. `GET /ddms?type=<entityType>` returns every registration in the partition with an interface whose `entityType`
   equals `type` [157 RAZ/ddms/DdmsRepository.java:88-105; RCP/ddms/OsmDdmsRepository.java:99-103]. `type` must match
   `^[A-Za-z0-9]{1,50}`, but `entityType` may contain `.`, `-` and `_`. Inference: entity types such as
   `master-data--Wellbore` cannot be looked up with `?type=`.
2. `GET /ddms/{id}` reads one registration.
3. `GET /ddms/{id}/{type}/{localid}` redirects to `<server url>/<retrieval path>` with `localid` substituted; the
   interface is found with a case-insensitive match on `type` [157 RC/ddms/model/Ddms.java:66-71;
   RC/ddms/services/ConsumptionService.java:40-66].
4. The documented pattern: records carry `data.ddmsId`, `data.entityType` and `data.localId`; a DDMS stores the bulk
   data first and then creates the Storage record, forwarding the caller's token; the documentation's example
   `GET /api/register/v1/ddms/wellbore` is the id route [157 docs/docs/HowToBecomeADDMS.md:156-166, 211-219, 400-431].

The Register service only helps discovery: it tells a route that a DDMS is registered for an entity type and where its
retrieval call is. The DDMS's write calls are whatever its own OpenAPI declares.

### 2.8 Checks before writing: Schema, Legal, Entitlements, Partition

Schema service:

- `GET /schema/{id}` (getSchema), where `id` is the schema id, `authority:source:entityType:major.minor.patch`, the
  same string as the kind (example `osdu:wks:wellbore:1.0.0`); the Dataset service looks a kind up this way.
  200 returns the resolved schema object; 404 "Requested Schema not found in repository" [C-schema: GET /schema/{id},
  getSchema; SchemaIdentity, L557-602; 26 SCH/api/SchemaController.java:94-100; docs/docs/index.md:12;
  118 DC/service/DatasetRegistryServiceImpl.java:286-291]. The service looks in the partition
  first, then in the shared (system) store [26 SCH/service/serviceimpl/SchemaService.java:99-113]. References are
  resolved when a schema is created, so the response is complete [26 docs/docs/index.md:10;
  SCH/service/serviceimpl/SchemaService.java:270-277].
- `GET /schema` (getSchemaInfoList): `authority`, `source`, `entityType`, `schemaVersionMajor`, `schemaVersionMinor`,
  `schemaVersionPatch`, `status`, `scope`, `latestVersion`, `limit` (0 to 100), `offset`; returns
  `SchemaInfoResponse {schemaInfos[], offset, count, totalCount}` [C-schema: GET /schema, getSchemaInfoList;
  SchemaInfoResponse, L682-705]. The contract gives `status` a default of `PUBLISHED` and `scope` a default of
  `INTERNAL`; the code applies no default to either, omitting `scope` returns both shared and partition schemas, and
  `limit` defaults to 100 [26 SCH/api/SchemaController.java:114-146; SCH/service/serviceimpl/SchemaService.java:280-323].
  With `latestVersion=true`, a minor filter needs a major filter and a patch filter needs a minor filter
  [26 SCH/service/serviceimpl/SchemaService.java:376-386].
- Status: `PUBLISHED` schemas are immutable, `DEVELOPMENT` schemas are mutable, and only `DEVELOPMENT` can become
  `OBSOLETE` [26 docs/docs/index.md:17-20].
- Creating: `POST /schema` needs a unique identity, registers a missing authority, source or entity type, checks for
  breaking changes against lower minor versions, and refuses references to `DEVELOPMENT` schemas; referenced fragments
  must already be registered [C-schema: POST /schema, createSchema; 26 SCH/service/serviceimpl/SchemaService.java:136-185;
  docs/docs/index.md:221-224]. `PUT /schema` creates or replaces `DEVELOPMENT` schemas and can move one to `PUBLISHED`
  or `OBSOLETE` [C-schema: PUT /schema, upsertSchema; 26 SCH/service/serviceimpl/SchemaService.java:330-360]. Version
  increment rules: [26 docs/docs/index.md:244-307].
- Scope: the contract says the system sets `SHARED` for the common tenant and `INTERNAL` for a private one
  [C-schema: POST /schema, createSchema]. The code sets `INTERNAL` for every `POST` or `PUT /schema` and `SHARED` only
  through `PUT /schemas/system` [26 SCH/service/serviceimpl/SchemaService.java:394-400].
- A delivery engine can create schemas with `service.schema-service.editors`, and they get `INTERNAL` scope; but
  published schemas never change and schemas are meant to be governed centrally [26 docs/docs/index.md:4-5, 17-20].
  Recommendation: treat a missing kind as a preflight failure and register custom kinds as a separate, reviewed
  operation, never inside a delivery run.

Legal service (the contract names no roles):

| Check | Route | Parameters and result | Source |
| --- | --- | --- | --- |
| Tags are valid | `POST /legaltags:validate` (validateLegalTags) | `{names: [1..25]}` returns `{invalidLegalTags: [{name, reason}]}`; an empty list means all valid; 404 "LegalTag names were not found." Storage makes the same call. | [C-legal: POST /legaltags:validate, validateLegalTags; RequestLegalTags to InvalidTagsWithReason, L1068-1099; 67 CC/legal/LegalService.java:86-93] |
| Tag properties and expiry | `GET /legaltags/{name}` (getLegalTag), `POST /legaltags:batchRetrieve` (getLegalTags, 1 to 25 names) | `LegalTagDto {name, description, properties}`; `properties` has `countryOfOrigin`, `contractId`, `expirationDate`, `originator`, `dataType`, `securityClassification`, `personalData`, `exportClassification`, `extensionProperties`. 404 when a tag (or one of the batch) does not exist. | [C-legal: GET /legaltags/{name}, getLegalTag; POST /legaltags:batchRetrieve, getLegalTags; LegalTagDto, Properties, L992-1050] |
| Country codes allowed | `GET /legaltags:properties` (getLegalTagProperties) | Every code in a record's `otherRelevantDataCountries`, plus `US`, must be a key of the returned `otherRelevantDataCountries` map. | [C-legal: GET /legaltags:properties, getLegalTagProperties; ReadablePropertyValues, L1136-1175; 44 SC/service/LegalServiceImpl.java:86-99] |
| Listing | `GET /legaltags?valid=` (default true), `POST /legaltags:query` (405 when the query API is disabled) | | [C-legal: GET /legaltags, listLegalTags; POST /legaltags:query, queryLegalTag] |

`GET /jobs/updateLegalTagStatus` is described as the compliance job status check [C-legal: GET
/jobs/updateLegalTagStatus, checkLegalTagStatusChanges], and a record's `legal.status` (`compliant` or `incompliant`)
is assigned by the server [C-storage: Legal, L1698-1705]. Inference: Storage caches the tags it has accepted
[44 SC/service/LegalServiceImpl.java:68-84, 180-209], so it may accept a tag that became invalid a moment ago. A route
therefore asks `legaltags:validate` itself on every run, as [OD LegalTagValidator.cs] does, and checks
`expirationDate`.

Entitlements (the contract names no roles for these reads):

| Purpose | Route | Notes | Source |
| --- | --- | --- | --- |
| Caller's groups | `GET /groups?roleRequired=` (listGroups) | `roleRequired` boolean, default false; optional `on-behalf-of` header. Returns `{desId, memberEmail, groups: [{name, description, role, email}]}`. | [C-entitlements: GET /groups, listGroups; ListGroupResponseDto, ParentReference, L1139-1169] |
| All groups of the partition | `GET /groups/all?type=&cursor=&limit=` (listAllPartitionGroups) | `type` required (`NONE`, `DATA`, `USER`, `SERVICE`); `limit` default 100, minimum 1. Returns `{groups[], cursor, totalCount}`. | [C-entitlements: GET /groups/all, listAllPartitionGroups; ListGroupsOfPartitionDto, L1276-1291] |
| Members of a group | `GET /groups/{group_email}/members?role=&includeType=` (listGroupMembers) | `role` is `MEMBER` or `OWNER`. | [C-entitlements: GET /groups/{group_email}/members, listGroupMembers] |
| Groups of a member | `GET /members/{member_email}/groups?type=&appid=&roleRequired=` (listGroupsOnBehalfOf) | `type` required. | [C-entitlements: GET /members/{member_email}/groups, listGroupsOnBehalfOf] |
| Member count | `GET /groups/{group_email}/membersCount?role=` (getMembersCount) | The contract types the 200 body as `ListMemberResponseDto`. | [C-entitlements: GET /groups/{group_email}/membersCount, getMembersCount] |

Storage reads the caller's groups through the common library's Entitlements client, which calls `GET /groups`, and
caches them per partition, token and user; the cache lifetime is not set in the files read
[44 SC/service/EntitlementsAndCacheServiceImpl.java:158-186, 211-215; 67 CC/entitlements/EntitlementsService.java:73-80].
Inference: a membership change can take effect in Storage only after that cache entry expires.

What to check before writing:

1. The caller holds the service groups the route needs, read from `GET /groups`: `service.storage.creator` to write
   and `service.storage.viewer` (or higher) to read back; `service.file.editors` for upload and metadata;
   `service.dataset.editors` for storage instructions plus `service.storage.creator` for registration and the
   forwarded copy; `service.schema-service.viewers` for kind checks; `service.search.user` or `service.search.admin`
   for index checks; for deletes `service.storage.creator` plus owner access, and for purges `service.storage.admin`
   plus owner access (section 1.1).
2. Every ACL entry matches the pattern of section 2.2; its domain equals the domain of the caller's first group, which
   is what Storage compares [44 SC/service/EntitlementsAndCacheServiceImpl.java:78-104]; and the group exists, either
   among the caller's groups or in `GET /groups/all?type=DATA`.
3. The caller is in at least one `owners` group (compared by the name before `@`) or in `users.data.root`; otherwise
   every later update or delete of these records is refused with 403 [44 SC/service/IngestionServiceImpl.java:253-265;
   SC/service/DataAuthorizationService.java:65-74; SC/service/EntitlementsAndCacheServiceImpl.java:106-123].
4. The caller is in a `viewers` or `owners` group of the record; otherwise a batch read-back leaves the record out
   without saying so (section 5.1) [44 SC/service/EntitlementsAndCacheServiceImpl.java:125-156].

Partition service: `GET /partitions` lists partition ids and `GET /partitions/{partitionId}` returns the partition's
properties as a map of `{sensitive, value}` (the contract's `Map` schema names its property `< * >`, a generator
artefact) [C-partition: GET /partitions, list; GET /partitions/{partitionId}, get; Property, Map, L221-242]. The
contract names no roles. A delivery route has no Partition call to make; the partition comes from the flow's
configuration.

### 2.9 Units and CRS

What the platform does:

- Normalization happens on read. `POST /query/records:batch` with a `frame-of-reference` header converts records, and
  the Indexer reads records that way, so Search holds normalized values [44 docs/docs/api.md:42-57;
  19 docs/docs/api.md:52-68]. Inference: Storage keeps values exactly as written; nothing in the upsert path converts
  them [44 SC/service/IngestionServiceImpl.java:94-105].
- The Storage documentation says conversion covers units and CRS; date-times are converted except inside objects and
  arrays; elevation and azimuth are not converted yet; a failed conversion returns the original values with a
  conversion status per record [44 docs/docs/api.md:43-56]. The Search documentation names unit, CRS and date-time
  [19 docs/docs/api.md:52].
- Conversion runs only for records with a non-null `meta[]` entry, or with an `AsIngestedCoordinates` object of type
  `AnyCrsFeatureCollection` inside `SpatialLocation`, `ProjectedBottomHoleLocation`, `GeographicBottomHoleLocation`,
  `SpatialArea`, `SpatialPoint`, `ABCDBinGridSpatialLocation`, `FirstLocation`, `LastLocation` or `LiveTraceOutline`;
  an attribute that already has a non-null `Wgs84Coordinates` is not converted [44 SC/conversion/DpsConversionService.java:83,
  125-170, 319-360; 67 CC/Constants.java:52, 58-62]. It is skipped entirely when the header is missing or `none`, or
  when no CRS converter is configured [44 SC/service/BatchServiceImpl.java:170-183].
- `meta[]` unit entries: `unitOfMeasureID` takes precedence over `persistableReference` [44 docs/docs/api.md:82-97].
  Storage reads the `reference-data--UnitOfMeasure` record named there (trailing `:` removed) and puts its
  `data.PersistableReference` into the entry; if the record cannot be read or has none, it logs a warning and keeps the
  given `persistableReference`. Records read this way are cached for 60 seconds by default
  [44 SC/conversion/DpsConversionService.java:55-56, 227-252, 274-301].
- Coordinates, as documented: `Wgs84Coordinates` present is used as is; only `AsIngestedCoordinates` present is
  converted; both present, `AsIngestedCoordinates` is ignored; `AsIngestedCoordinates` is not indexed unless the
  Indexer's feature flag is on; a failed conversion skips the shape and indexes status 400 [19 docs/docs/api.md:54-68].

So a delivery route does not need to convert units or CRS before writing. It needs to write correct `meta[]` entries
and CRS references, make sure the referenced `UnitOfMeasure` and CRS reference records exist (P6), and either supply
`Wgs84Coordinates` itself or rely on the platform and check `index.statusCode` afterwards (P9).

Optional endpoints for mapping and quality checks (neither contract names roles):

| Route | operationId | Use | Source |
| --- | --- | --- | --- |
| `GET /v3/unit/symbol?namespaces=&symbol=` | getUnitBySymbol | Resolve a source unit symbol within a namespace list to one `Unit` (`name`, `namespace`, `displaySymbol`, `essence`, `essenceJson`, `deprecationInfo`, ...). | [C-unit3: GET /v3/unit/symbol, getUnitBySymbol; Unit, L2530-2550] |
| `GET /v3/unit/symbols?symbol=` | getUnitsBySymbol | Every unit with that symbol, as a `QueryResult`. | [C-unit3: GET /v3/unit/symbols, getUnitsBySymbol] |
| `POST /v3/unit` | postUnit | `UnitRequest {persistableReference or essence}`; returns the `Unit`. | [C-unit3: POST /v3/unit, postUnit; UnitRequest, L2686-2696] |
| `GET /v3/conversion/scale?namespaces=&fromSymbol=&toSymbol=`, `POST /v3/conversion/scale` | getConversionScaleOffsetBySymbols, postConversionScaleOffset | The `POST` takes `{fromUnit or fromUnitPersistableReference, toUnit or toUnitPersistableReference}`; both return `ConversionResult {abcd, scaleOffset {scale, offset}, fromUnit, toUnit}`. Needed only when a target property requires a fixed unit. | [C-unit3: GET /v3/conversion/scale, getConversionScaleOffsetBySymbols; POST /v3/conversion/scale, postConversionScaleOffset; ConversionScaleOffsetRequest, ConversionResult, L2799-2831] |
| `GET`/`POST /v3/conversion/abcd` | getConversionABCDBySymbols, postConversionABCD | Energistics ABCD parameters. | [C-unit3: GET /v3/conversion/abcd, getConversionABCDBySymbols; POST /v3/conversion/abcd, postConversionABCD; ConversionABCDRequest, L2832-2852] |
| `GET /v3/catalog/lastmodified` | getLastModified | `{lastModified}`, to invalidate a local unit cache. | [C-unit3: GET /v3/catalog/lastmodified, getLastModified] |
| v2 equivalents with path parameters, for example `GET /v2/conversion/scale/{namespaces}/{fromSymbol}/{toSymbol}` | getConversionScaleOffsetBySymbols (v2) | Namespace lists are in priority order, for example `LIS,RP66,ECL,Energistics_UoM`. | [C-unit2: GET /v2/conversion/scale/{namespaces}/{fromSymbol}/{toSymbol}, getConversionScaleOffsetBySymbols; GET /v2/unit/symbol/{namespaces}/{symbol}, getUnitBySymbol; L860-1016, L1366-1523, L1918-2093, L2251-2320] |

CRS Catalog (`/api/crs/catalog/`):

| Route | operationId | Use | Source |
| --- | --- | --- | --- |
| `POST /v3/coordinate-reference-system` | getCoordinateReferenceSystems | `CoordinateReferenceSystemsQuery` (`codeSpace`, `code`, `name`, `id`, `kind`, `coordinateReferenceSystemType`, `returnBoundProjectedAndProjectedBasedOnWgs84`, `returnBoundGeographic2DAndWgs84`, `baseCRS`, `datum`, `extent`, `persistableReferenceSearch`, `horizontalAxisUnitId`, `verticalAxisUnitId`, `latitude`, `longitude`, `includeDeprecated`, `offset`, `limit`, `returnAllFields`, `returnedFields`); returns `SearchResponse {searchResults, cursorSearchResults, query}`. Maps an EPSG code or a name to a CRS record id and persistable reference. | [C-crscat: POST /v3/coordinate-reference-system, getCoordinateReferenceSystems; CoordinateReferenceSystemsQuery; SearchResponse] |
| `GET /v3/coordinate-reference-system?recordId=&dataId=` | getCoordinateReferenceSystem | Look up by id. | [C-crscat: GET /v3/coordinate-reference-system, getCoordinateReferenceSystem] |
| `POST`/`GET /v3/coordinate-transformation` | getCoordinateTransformations, getCoordinateTransformation | `CoordinateTransformationsQuery` (`sourceCRS` and `targetCRS` record ids, `kind`, `code`, `latitude`, `longitude`, ...); choose a transformation. | [C-crscat: POST /v3/coordinate-transformation, getCoordinateTransformations; GET /v3/coordinate-transformation, getCoordinateTransformation; CoordinateTransformationsQuery] |
| `POST /v3/points-in-aou` | getAouInfo | `InPolygonQuery {recordId, dataId, points[{latitude, longitude}], offset, limit, returnedFields}`; returns `PointsInAouSearchResult {bboxFailedPoints[{point, index, approximateKmDistanceOutside}], maxDistKmOutsideBBox}` for points outside the CRS or transformation area of use. A quality check for swapped axes or a wrong CRS. | [C-crscat: POST /v3/points-in-aou, getAouInfo; InPolygonQuery; PointsInAouSearchResult; PointsInAouSearchPoint] |

The CRS Catalog query schemas type numbers and flags (`latitude`, `limit`, `includeDeprecated`, ...) as strings
[C-crscat: CoordinateReferenceSystemsQuery].

CRS Conversion (`/api/crs/converter/`); each operation answers 503 "CRS-converter overloaded; try again later":

| Route | operationId | Use | Source |
| --- | --- | --- | --- |
| `POST /v4/convert` | convertPointV4 | `{fromCRS, toCRS, points[{x, y, z}]}` required, `transformation` optional; returns `{successCount, points, operationsApplied}`, failed points as NaN. | [C-crsconv: POST /v4/convert, convertPointV4; ConvertPointsRequestV4; ConvertPointsResponse] |
| `POST /v4/convertGeoJson` | convertGeoJsonV4 | `{featureCollection, toCRS}` required, `toUnitZ` and `transformation` optional; the collection is GeoJSON (WGS 84) or an AnyCrs collection whose `persistableReferenceCrs` is the source CRS (it also has `CoordinateReferenceSystemID`, `VerticalUnitID`, `persistableReferenceUnitZ`); returns `{successCount, totalCount, featureCollection, operationsApplied}`. Use it to compute `Wgs84Coordinates` when a route writes both blocks. | [C-crsconv: POST /v4/convertGeoJson, convertGeoJsonV4; ConvertGeoJsonRequestV4; ConvertGeoJsonResponse; GeoJsonFeatureCollection] |
| `POST /v4/convertTrajectory` | convertTrajectory | `trajectoryCRS`, `unitZ`, `method`, `inputStations[1..]` required; `referencePoint`, `azimuthReference`, `unitXY`, `unitMD`, `inputKind`, `interpolate`, `MD_i` optional. Only when a target kind needs computed stations. | [C-crsconv: POST /v4/convertTrajectory, convertTrajectory; ConvertTrajectoryRequestV4; ConvertTrajectoryResponseV4] |

The conversion `Point` types `x`, `y`, `z` as strings with format `double` [C-crsconv: Point].

### 2.10 Write route types at a glance

| Id | Route type | Calls | Constraints |
| --- | --- | --- | --- |
| W1 | Storage upsert | `PUT /api/storage/v2/records[?skipdupes=]`, body `[Record]` | 1 to 500 records; ids `{partition}:{entity type}:{key}`, at most 512 bytes, unique in the batch; full replace; one version for the batch; settle from `recordIdVersions` and `skippedRecordIds` (2.2). |
| W2 | Storage merge patch | `PATCH /api/storage/v2/records/{id}`, `application/merge-patch+json` | Owner access; a version only if something compared changed; the response has no version (2.3). |
| W3 | Storage JSON patch | `PATCH /api/storage/v2/records`, `application/json-patch+json`, `{query: {ids[1..100]}, ops[1..100]}` | `/data` and `/meta` ops version every record; metadata-only ops version none; 206 on partial success (2.3). |
| W4 | Storage metadata bulk patch | `PATCH /api/storage/v2/records`, `application/json`, `{query: {ids[1..500]}, ops[]}` | `acl`, `legal` and `tags` paths only; no new version; 206 on partial success (2.3). |
| W5 | File (`File.Generic`) | `GET /api/file/v2/files/uploadURL`, `PUT` signed URL, `POST /api/file/v2/files/metadata` | Kind `<any>:wks:dataset--File.Generic:<any>`; a new id on every accepted call; checksum computed by the service; metadata within 24 hours of upload (2.4). |
| W6 | Dataset (`File.*`, `FileCollection.*`) | `POST /api/dataset/v1/storageInstructions?kindSubType=`, provider upload (2.5.1), `PUT /api/dataset/v1/registerDataset` | Kind group `dataset`; id entity type must match the kind; schema must exist; `FileCollectionPath` required for collections; no checksum computed; `expiryTime` not forwarded (2.5). |
| W7 | DDMS | Discover with `GET /api/register/v1/ddms/{id}` (or `?type=` for alphanumeric types); call what the DDMS declares | One server per interface; one retrieval operation with `x-ddms-retrieve-entity` (2.7). |
| W8 | Manifest | Section 2.6 and `osdu/specs/workflows/INTEGRATION.md` | |

### 2.11 Preflight checks

| Id | Check | Call | Pass condition |
| --- | --- | --- | --- |
| P1 | Token, partition, service groups | `GET /api/entitlements/v2/groups` | The groups of section 2.8, item 1, are present. |
| P2 | ACL groups | The ACL pattern locally; `GET /groups` and `GET /groups/all?type=DATA` | Every group exists; every domain equals the caller's group domain; the caller is in an owners group or `users.data.root`, and in a viewers or owners group. |
| P3 | Legal | `POST /api/legal/v1/legaltags:validate` (25 names per call); `GET /legaltags/{name}` or `POST /legaltags:batchRetrieve`; `GET /legaltags:properties` | `invalidLegalTags` empty; `expirationDate` in the future; every country code (and `US`) a key of `otherRelevantDataCountries`. |
| P4 | Kind exists | `GET /api/schema-service/v1/schema/{kind}`, or `GET /schema?authority=&source=&entityType=&schemaVersionMajor=&schemaVersionMinor=&schemaVersionPatch=&status=PUBLISHED` | 200; `PUBLISHED` preferred. |
| P5 | Syntax and batch shape, locally | Kind pattern; id pattern; id first segment equals the partition; id second segment equals the kind's entity type; id at most 512 bytes and unique per batch; batch at most 500 (Storage) or 20 (Dataset); `data` non-empty; legal tags non-empty unless the record has parents; country list non-empty; parents as `id:version` | All pass. |
| P6 | Referenced records exist | `POST /api/storage/v2/query/records/headers`, chunks of 1000 ids, `attributes: ["version"]`, covering ancestry parents, reference data (including the `UnitOfMeasure` ids of `meta[].unitOfMeasureID`), CRS records and master data | `notFound` and `invalidRecords` empty; parent versions match exactly. |
| P7 | Dataset DMS available | Only right before an upload: `POST /api/dataset/v1/storageInstructions?kindSubType=` | 200 (400: no DMS for the type; 405: storage not allowed). It only prepares a signed location; nothing is written. |
| P8 | Data quality, optional | `POST /api/crs/catalog/v3/points-in-aou`; `GET /api/unit/v3/unit/symbol` | No `bboxFailedPoints`; every unit resolves. |
| P9 | After the write | V1 for the landed version; after at least 30 seconds, V5 for `index.statusCode` | Version equals the one from `recordIdVersions`; status 200. |
| P10 | DDMS registered (DDMS kinds only) | `GET /api/register/v1/ddms/{id}`, or `?type=` for alphanumeric types | A registration with the entity type exists and has one server. |

## 3. Payload and bulk data shapes

### 3.1 Storage record

`Record` [C-storage: Record, L1709-1779]: `id` (pattern above), `version` (read-only), `kind` (required), `acl`
(required: `viewers[]`, `owners[]`), `legal` (required: `legaltags[]` and `otherRelevantDataCountries[]`, both
`minItems: 1` and unique; `status` read-only), `data` (required object, `minProperties: 1`), `ancestry`
(`parents[]`, unique, `{id}:{version}`), `meta[]` (objects), `tags` (string map), and read-only `createUser`,
`createTime`, `modifyUser`, `modifyTime`. The contract's `required` list also holds `id` (section 2.2). The Storage
documentation example, without its read-only fields [44 docs/docs/index.md:9-43]:

```json
{
  "id": "data-partition-id:hello:123456",
  "kind": "schema-authority:wks:hello:1.0.0",
  "acl": {
    "viewers": ["data.default.viewers@data-partition-id.[osdu.opengroup.org]"],
    "owners": ["data.default.owners@data-partition-id.[osdu.opengroup.org]"]
  },
  "legal": {
    "legaltags": ["data-partition-id-sample-legaltag"],
    "otherRelevantDataCountries": ["FR", "US", "CA"]
  },
  "data": { "msg": "Hello World, Data Ecosystem!" }
}
```

GeoJSON values are accepted in `data` [44 docs/docs/api.md:781-790].

### 3.2 File metadata record

`FileMetadata` [C-file: FileMetadata, L676-712; FileData, L606-675; DatasetProperties, L598-605; FileSourceInfo,
L713-760]: required `kind`, `acl`, `legal`, `data`; `data.DatasetProperties` is required, with a required
`FileSourceInfo` whose `FileSource` (required, at least one character) is the `FileSource` the upload location
returned. Optional: `data.Name`, `data.TotalSize` (`^[0-9]+$`), `FileSourceInfo.Name`, `FileSourceInfo.FileSize`,
`PreloadFilePath` and the other preload fields, and `ExtensionProperties`. OSDU Delivery sends `kind`, `acl`, `legal`
and `data {Name, TotalSize, DatasetProperties.FileSourceInfo {FileSource, Name, FileSize}}` [OD FileUploads.cs]. Leave
out `Checksum`, `ChecksumAlgorithm`, `EncodingFormatTypeID` and `SchemaFormatTypeID`: the service computes the
checksum, and the contract's patterns for these fields accept no real value (section 7).

### 3.3 Dataset records

Schema identities in the shared schema deployment are `{{schema-authority}}:wks:dataset--<name>:<version>`, the
authority filled in at deployment [26 SS/dataset/FileCollection.Generic.1.0.0.json:6-10]. The entity types published
under `SS/dataset/` at the pinned commit:

- `dataset--File.*`: CompressedVectorHeaders, EML, Generic, GeoJSON, HDF5, Image.JPEG, Image.PNG, Image.TIFF,
  Image.WorldFile, OGC.GeoTIFF, PRODML, SeismicHistogram, SeismicLineGeometry, TabularData, WITSML.
- `dataset--FileCollection.*`: Bluware.OpenVDS, EPC, Esri.Shape, Generic, SEGA, SEGB, SEGD, SEGY, Slb.OpenZGY,
  TGS.MDIO.
- Others: `dataset--ConnectedSource.Generic`, `dataset--ETPDataspace`, `dataset--PhysicalMedia`.

Inference: `dataset--ETPDataspace` and `dataset--PhysicalMedia` match none of the provider maps of section 2.5, so
`registerDataset` refuses them unless a platform maps them; deliver those through Storage `PUT`.

Each dataset schema constrains the id to its own entity type, for example
`^[\w\-\.]+:dataset\-\-FileCollection.Generic:[\w\-\.\:\%]+$` [26 SS/dataset/FileCollection.Generic.1.0.0.json:25-29].
`File.Generic` 1.1.0 and `FileCollection.Generic` 1.1.0 use `AbstractFile` 1.0.1 and `AbstractFileCollection` 1.0.1
[26 SS/dataset/File.Generic.1.1.0.json:112-115; SS/dataset/FileCollection.Generic.1.1.0.json:112-115]:

- File collection, `data.DatasetProperties`: `FileCollectionPath` required, a string with no pattern (the schema's
  example is an `s3://` folder URL); optional `IndexFilePath`, `FileSourceInfos[]` and `Checksum`
  (`^[0-9a-fA-F]{32}`) [26 SS/abstract/AbstractFileCollection.1.0.1.json:31-86]. Write the `fileCollectionSource` the
  storage instructions returned, adding a leading `/` on core-plus (see the inference in section 2.5).
- Each `FileSourceInfos[]` entry: `FileSource` required; in a collection it is relative to `FileCollectionPath`;
  optional `FileSize` (`^[0-9]+$`), `Checksum` (`^([0-9a-fA-F]{2})+$`), `ChecksumAlgorithm`, `EncodingFormatTypeID`
  (an R3 reference id pattern) [26 SS/abstract/AbstractFileSourceInfo.1.0.1.json:26-108]. The client supplies these
  checksums; the Dataset route computes none.
- Single file, `data.DatasetProperties.FileSourceInfo` (the same abstract), plus an MD5 `Checksum` property
  (`^[0-9a-fA-F]{32}`) [26 SS/abstract/AbstractFile.1.0.1.json:31-46].

A file collection registration, shaped from those schemas:

```json
{
  "datasetRegistries": [{
    "id": "<partition>:dataset--FileCollection.Generic:<key>",
    "kind": "<authority>:wks:dataset--FileCollection.Generic:1.1.0",
    "acl": { "viewers": ["data.<group>.viewers@<domain>"], "owners": ["data.<group>.owners@<domain>"] },
    "legal": { "legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<country>"] },
    "data": {
      "DatasetProperties": {
        "FileCollectionPath": "<fileCollectionSource>",
        "FileSourceInfos": [{ "FileSource": "<name relative to the collection>", "FileSize": "<bytes>" }]
      }
    }
  }]
}
```

### 3.4 `meta[]` and coordinates

A unit entry, from the Storage documentation [44 docs/docs/api.md:82-97]:

```json
{
  "kind": "Unit",
  "name": "ft",
  "persistableReference": "",
  "propertyNames": ["FacilitySpecifications[0].FacilitySpecificationQuantity", "VerticalMeasurements[0].VerticalMeasurement"],
  "unitOfMeasureID": "osdu:reference-data--UnitOfMeasure:ft:"
}
```

Unit conversion covers arrays of values and properties of arrays of objects when the array element is the root
object; an array nested inside another is not converted [44 docs/docs/api.md:47-54]. Coordinates follow section 2.9.

## 4. Identities and versions

| Route | Id | Version |
| --- | --- | --- |
| Storage `PUT /records` | Client id, or `{tenant}:{kind entity type}:{uuid without dashes}` when absent [67 CC/model/storage/Record.java:122-137]. The entity type segment is not checked (2.2). | One per call for every record written; skipped records keep theirs (2.2). |
| File `POST /v2/files/metadata` | Always generated: `{data-partition-id}:{kind entity type as sent}:{uuid with dashes}` [90 FC/util/FileMetadataUtil.java:15-22; FC/service/FileMetadataService.java:80-81] | From the Storage upsert; the response carries only `id` [C-file: FileMetadataResponse, L791-798]. |
| Dataset `PUT /registerDataset` | Client id, checked against the kind's entity type; generated by Storage when absent (2.5) | From the Storage upsert, returned in the read-back record. |
| Storage JSON patch | Unchanged | New version for data or meta ops; none for metadata ops (2.3). |
| Storage merge patch | Unchanged | New version only if something compared changed; not returned (2.3). |
| Storage metadata bulk patch | Unchanged | None; an id with `:version` must be the latest or it is reported as locked (2.3). |

Versions are `currentTimeMillis() * 1000 + random(1..1000)` [67 CC/model/storage/TransferInfo.java:34-41], stored as
version paths `kind/id/version` [67 CC/model/storage/RecordMetadata.java:95-97]. `GET /records/versions/{id}` lists
them [C-storage: GET /records/versions/{id}, getRecordVersions; RecordVersions, L2440-2449]. Kinds are compared
case-insensitively by Search [19 docs/docs/api.md:270-281].

## 5. Reads, verification and deletes

### 5.1 Storage reads

| Route | operationId | Limits and behaviour | Source |
| --- | --- | --- | --- |
| `POST /query/records`, body `{records[], attributes[]}` | getRecords | 0 to 100 ids. Returns `{records[], invalidRecords[], retryRecords[]}`. Missing, soft-deleted or versionless ids go to `invalidRecords`; ids whose blob came back empty go to `retryRecords`; ids the caller cannot see are left out and listed nowhere, so compare the returned ids with the requested ones. | [C-storage: POST /query/records, getRecords; MultiRecordIds, MultiRecordInfo, L2033-2063; 44 SC/service/BatchServiceImpl.java:89-166, 269-314] |
| `POST /query/records:batch`, body `{records[]}`, header `frame-of-reference` | fetchRecords | 1 to 20 ids. The header value is `none` or `units=SI;crs=wgs84;elevation=msl;azimuth=true north;dates=utc;` (compared case-insensitively); any other value is 400 when a converter is configured. Returns `{records[] (JSON strings), notFound[], conversionStatuses[]}`. Conversion is skipped when the header is missing or `none` or no converter is configured. Every requested id not returned is added to `notFound`. | [C-storage: POST /query/records:batch, fetchRecords; MultiRecordRequest to MultiRecordResponse, L2064-2101; 44 SC/service/BatchServiceImpl.java:55, 168-259, 316-329; docs/docs/api.md:42-57] |
| `POST /query/records/headers`, body `{records[], attributes[]}` | getRecordsHeaders | 1 to 1000 ids. `attributes` is an optional projection over `version`, `kind`, `acl`, `legal`, `ancestry`, `tags`, `createUser`, `createTime`, `modifyUser`, `modifyTime` (at most 10; the code matches them case-insensitively). No data payload. Malformed ids go to `invalidRecords`; missing, soft-deleted, versionless or hidden ids go to `notFound`. It reads Storage, not the index, so it is the best existence and version check. | [C-storage: POST /query/records/headers, getRecordsHeaders; MultiRecordHeadersRequest to RecordHeadersDTO, L2102-2178; 44 SC/api/QueryApi.java:140-161; SC/model/MultiRecordHeadersRequest.java:38-69; SC/service/BatchServiceImpl.java:348-463] |
| `GET /records/{id}?attribute=` | getLatestRecordVersion | 404 when the record is missing, has no version, or is soft-deleted; 400 when the first id segment is not the tenant; 403 when the caller is in neither the viewers nor the owners groups. | [C-storage: GET /records/{id}, getLatestRecordVersion; 44 SC/service/QueryService.java:28-30; SC/service/QueryServiceImpl.java:201-272] |
| `GET /records/{id}/{version}` | getSpecificRecordVersion | One version; the same status rules as the latest read. | [C-storage: GET /records/{id}/{version}, getSpecificRecordVersion; 44 SC/service/QueryServiceImpl.java:93-103] |
| `GET /records/versions/{id}` | getRecordVersions | `{recordId, versions[]}`. | [C-storage: GET /records/versions/{id}, getRecordVersions; 44 SC/service/QueryServiceImpl.java:105-129] |
| `GET /records?kind=&deleted=&modifiedAfterDate=&limit=&cursor=&sortOrder=` | getAllRecords | `limit` 1 to 100, default 20; `deleted` default false; `sortOrder` `ASC` or `DESC`, default `DESC`; paged by `cursor`. The contract types `modifiedAfterDate` as `date-time`; the code parses `yyyy-MM-dd`. | [C-storage: GET /records, getAllRecords; 44 SC/api/RecordApi.java:146-172; docs/docs/api.md:105-131] |
| `GET /query/records?kind=&cursor=&limit=` | getAllRecords_1 | Record ids of a kind (`kind` required). Admin only. | [C-storage: GET /query/records, getAllRecords_1; 44 SC/api/QueryApi.java:79-90] |
| `GET /query/kinds` | none (hidden) | In the code, hidden from the contract and marked deprecated; creator or admin. | [44 SC/api/QueryApi.java:163-172; docs/docs/api.md:15-25] |

OSDU Delivery's batch verify sends `POST /query/records` with `attributes: ["id"]` and treats a record that is not
returned as missing [OD OsduRecordProtocol.cs]. Inference: a record hidden by its ACL looks the same as a missing one
there; `POST /query/records/headers` reports both under `notFound` as well, so only P2 separates the two.

### 5.2 Search: queries, limits, index lag and status

Routes [C-search: POST /query, queryRecords; POST /query_with_cursor, queryWithCursor; DELETE
/query_with_cursor/{cursor}, closeCursor; QueryRequest, L627-716; QueryResponse, L725-754; CursorQueryRequest,
L413-496; CursorQueryResponse, L587-609]:

- `POST /query`: `QueryRequest` with `kind` (required; a string or an array, each `^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$`),
  `query` (Lucene syntax), `limit`, `offset`, `returnedFields`, `excludedFields`, `sort {field[], order[], filter[]}`,
  `queryAsOwner`, `trackTotalCount`, `aggregateBy`, `spatialFilter`, `highlightedFields`, `suggestPhrase`. Returns
  `{results[], aggregations[], phraseSuggestions[], totalCount}`.
- `POST /query_with_cursor?search_after=`: `CursorQueryRequest` (no `offset` or `aggregateBy`; `cursor` instead);
  returns `{cursor, results[], totalCount}`.
- `DELETE /query_with_cursor/{cursor}?search_after=`: the code releases only search-after contexts; for a scroll
  cursor it does nothing [19 SRC/api/SearchApi.java:103-116, 130-138].
- 502 means "Search service scale-up is taking longer than expected. Wait 10 seconds and retry." [C-search: POST
  /query, queryRecords].
- Besides the service groups (section 1.1), the caller must be a member of the record's data groups
  [19 docs/docs/api.md:6-14, 43-49].

Limits:

| Item | Limit | Source |
| --- | --- | --- |
| `limit` | Documented: default 10, minimum 1, maximum 1000. The contract says `minimum: 0`; the code treats 0 as the default and caps larger values at the configured maximum instead of refusing them. | [19 docs/docs/api.md:182; C-search: QueryRequest, L646-654; 67 CC/model/search/Query.java:67-69; CC/model/search/QueryUtils.java:22-26] |
| `offset + limit` | At most 10,000 | [67 CC/model/search/validation/OffsetValidator.java:29-47; 19 docs/docs/api.md:193] |
| Query clauses | 1024 | [19 docs/docs/api.md:180] |
| `totalCount` | Capped at 10,000 unless `trackTotalCount=true` | [C-search: QueryResponse, L749-754; 19 docs/docs/api.md:186] |
| Kinds per request | Together at most 3,840 characters, counting one separator per kind (an index alias counts instead of the kind where aliases are used) | [67 CC/model/search/validation/MultiKindValidator.java:28-33, 48-69] |
| Kind case | Case-insensitive | [19 docs/docs/api.md:270-281] |
| Cursor | The context lives 1 minute and each call renews it; 500 concurrent cursors per partition, beyond that 429 | [19 docs/docs/api.md:1403, 1711-1713] |
| Response size | 100 MB, beyond that 413 | [19 docs/docs/api.md:1715-1717] |
| Sort | A sort over a broad kind can time out after 60 seconds with 504 | [19 docs/docs/api.md:861] |
| Wildcards in terms | Leading wildcards are disabled | [19 docs/docs/api.md:448] |
| Reserved characters | Escape `+ - = && \|\| > < ! ( ) { } [ ] ^ " ~ * ? : \ /`; `<` and `>` cannot be escaped | [19 docs/docs/api.md:426-434] |
| `aggregateBy` buckets | At most `aggregationSize` (`AGGREGATION_SIZE`, 1000 by default), a platform setting and not a request parameter; ordered by count descending, then key ascending, `minDocCount` 1; no paging, and `query_with_cursor` takes no `aggregateBy` | [19 SRC/config/SearchConfigurationProperties.java:23; SRC/util/AggregationParserUtil.java:49-135; docs/docs/api.md:1313] |
| Bucket keys | The string term, or for a long or double its `key_as_string`, which only a formatted field (a date, a boolean) has: a plain number can come back with no key | [19 SRC/provider/impl/CoreQueryBase.java:203-249, 452-470] |

Kind wildcards: any segment may be `*`, for example `*:*:*:*`, `opendes:*:*:*`, `osdu:wks:*:*`,
`*:*:master-data--Well:*` [19 docs/docs/api.md:607, 652, 756, 1405-1482; C-search: QueryRequest, L643-645].

Distinct values: `aggregateBy` names a field (`data.Name.keyword`), or `nested(path, field)` for a property of a nested
array, whose bucket counts are then the array's objects rather than records [19 SRC/util/AggregationParserUtil.java:49-135].
A text property aggregates only through its `keyword` sub-field, which the indexer maps with `ignore_above: 256` and
`null_value: "null"`: a longer value is in no bucket, and a null is the text `null` (indexer-service
`indexer-core/src/main/java/org/opengroup/osdu/indexer/util/TypeMapper.java:262-268`, commit
`423a9aee9099528361b738f3961d6a03aa13bd8b`). The query reaches Elasticsearch as `query_string`, so a range such as
`data.Name.keyword:["a" TO "m"}` narrows the records an aggregation reads; dimension flows read every distinct value of a
field this way, splitting a range the bucket limit cuts off
([flow/dimension-build.md](../../docs/reference/flow/dimension-build.md), What a build does).

Index lag and status:

- After Storage accepts a record, "it can take *at least 30 seconds* to become searchable" [19 docs/docs/api.md:173].
- Each indexed record has `index.statusCode`: 200 all OK, 404 schema missing, 400 a field could not be mapped or
  converted; with `index.trace` and `index.lastUpdateTime`. The `index` block is returned only when named in
  `returnedFields`, for example `"kind": "*:*:*:*", "query": "index.statusCode:404", "returnedFields": ["id", "index"]`
  [19 docs/docs/api.md:1626-1701].
- Soft-deleted records are removed from the index [44 docs/docs/api.md:255].

### 5.3 Verifying that referenced ids exist

- Preferred: Storage `POST /query/records/headers` with up to 1000 ids and `attributes: ["version", "kind"]`. Anything
  in `notFound` is missing, soft-deleted or hidden from the caller. It reads Storage, so there is no index lag (5.1).
- Search alternative: `kind` from the id's entity type (for example `*:*:master-data--Wellbore:*`) or `*:*:*:*`, a
  query on `id` with quoted values, `returnedFields: ["id"]`, `limit` up to 1000, within 1024 clauses. The Search
  documentation shows no query on `id`; ids contain the reserved `:`, so quote or escape them (5.2). OSDU Delivery
  already queries a quoted value this way for `FileSource` [OD FileUploads.cs].
- Both return only records the caller can see: a "missing" reference may be hidden by its ACL.

### 5.4 Deletes, undelete and purge

| Id | Route | operationId | Behaviour | Reversible | Source |
| --- | --- | --- | --- | --- | --- |
| D1 | `POST /records/{id}:delete` | deleteRecord | Soft delete. 400 when the first id segment is not the tenant; 404 when the record is missing or not active; 403 without owner access. Publishes a soft-delete message; the record leaves the index. | Yes | [C-storage: POST /records/{id}:delete, deleteRecord; 44 SC/service/RecordServiceImpl.java:225-250, 317-335, 351-356; docs/docs/api.md:236-257] |
| D2 | `POST /records/delete`, body `["id", ...]` | bulkDeleteRecords | 1 to 500 ids; an id whose first segment is not the tenant fails the whole request with 400. 204 when all were deleted; 207 when some were missing or refused, and the others are still deleted. The 207 body is a JSON array with one `{"notDeletedRecordId", "message"}` per id kept (`Record with id '...' not found`, `The user is not authorized to perform delete record with id ...`); the contract's `DeleteRecordsException {notDeletedRecords: [{key, value}]}` describes the exception behind it, not the body. Inference: an id that is already soft-deleted is not reported and is marked deleted again. | Yes | [C-storage: POST /records/delete, bulkDeleteRecords; DeleteRecordsException, PairStringString, L1898-2032; 44 SC/api/RecordApi.java:281-288; SC/service/RecordServiceImpl.java:252-284, 337-368; SC/util/GlobalExceptionMapper.java:139-152; SC/util/RecordUtilImpl.java:62-71; docs/docs/api.md:254-275] |
| D3 | `PATCH /records/{id}` with `{"deleted": false}` | patchRecord | Undelete; no new version. The documentation's example sends `"false"` as a string; the contract types it boolean. | n/a | [C-storage: RecordMergePatchRequest, L2365-2371; 44 SC/service/RecordServiceImpl.java:504-518; docs/docs/api.md:214-234] |
| D4 | `POST /api/dataset/v1/metadataRecord/{id}/softDelete`, `.../undelete` | deleteMetadataById, undeleteMetadataById | Section 2.5; the undelete re-upserts the saved copy. | Yes | [118 DC/service/DatasetRegistryServiceImpl.java:155-246] |
| D5 | `DELETE /api/file/v2/files/{id}/metadata` | deleteFileMetadataById | Soft delete of the record, then physical deletion of the persistent file. | Record yes, bytes no | [90 FC/service/FileMetadataService.java:216-240] |
| D6 | `DELETE /records/{id}` | purgeRecord | Physical deletion of all versions; admin plus owner; works on soft-deleted records too; 404 only when the record is unknown. | No | [C-storage: DELETE /records/{id}, purgeRecord; 44 SC/api/RecordApi.java:209-218; SC/service/RecordServiceImpl.java:112-148, 317-335] |
| D7 | `DELETE /records/{id}/versions?versionIds=&limit=&from=` | purgeRecordVersions | Admin plus owner. One of `versionIds`, `limit` or `from` is required (else 400). `versionIds`: comma-separated digits, at most 50, all existing, not the latest. `limit`: greater than 0 and at most the version count minus one; deletes the oldest. `from`: an existing version; deletes it and all older ones (the older ones only, when `from` is the latest); with `limit`, deletes the `limit` versions up to and including `from` (up to but excluding it, when it is the latest), and a `limit` larger than the number of versions up to `from` is 400. The latest version is never purged; a record with one version is 400 "No Record versions to purge". | No | [C-storage: DELETE /records/{id}/versions, purgeRecordVersions; 44 SC/api/RecordApi.java:231-244; SC/service/RecordServiceImpl.java:150-223, 370-461; SC/validation/impl/VersionIdsValidator.java:22-61; SC/util/RecordConstants.java:33-34; SC/validation/ValidationDoc.java:35-42; docs/docs/api.md:306-323] |

For OSDU Delivery the reversible removal scope is D1 and D2 (and D4 for Dataset records); D5, D6 and D7 destroy data.

### 5.5 Read and verify route types

| Id | Purpose | Call | Limit |
| --- | --- | --- | --- |
| V1 | Existence, version, ACL, legal, without index lag | `POST /api/storage/v2/query/records/headers` | 1000 ids |
| V2 | Full read-back | `POST /api/storage/v2/query/records` | 100 ids; hidden ids are left out |
| V3 | Normalized view | `POST /api/storage/v2/query/records:batch` with `frame-of-reference` | 20 ids |
| V4 | Versions | `GET /api/storage/v2/records/versions/{id}`, `GET /api/storage/v2/records/{id}/{version}` | one record |
| V5 | Index visibility and status | `POST /api/search/v2/query` (or `query_with_cursor`) with `returnedFields: ["id", "index"]` | wait at least 30 seconds; `limit` at most 1000 |
| V6 | Bytes reachable | `GET /api/dataset/v1/retrievalInstructions?id=` (or `POST`, 1 to 20 ids); `GET /api/file/v2/files/{id}/downloadURL` | |

## 6. Limits and errors

| Item | Limit | Source |
| --- | --- | --- |
| `PUT /records` records per call | 1 to 500 | [44 SC/api/RecordApi.java:128] |
| Record id | 512 bytes | [44 SC/util/RecordConstants.java:35] |
| `POST /records/delete` ids | 1 to 500 | [44 SC/api/RecordApi.java:284] |
| Metadata bulk patch ids | 1 to 500 | [C-storage: RecordQuery, L2216-2229; 67 CC/model/storage/RecordQuery.java:39-48] |
| JSON patch ids, ops | 1 to 100 each | [44 SC/util/RecordConstants.java:30-32] |
| `POST /query/records` ids | 0 to 100 | [C-storage: MultiRecordIds, L2041-2042] |
| `POST /query/records:batch` ids | 1 to 20 | [C-storage: MultiRecordRequest, L2072-2073] |
| `POST /query/records/headers` ids, attributes | 1 to 1000; at most 10 | [C-storage: MultiRecordHeadersRequest, L2110-2111, L2131-2132] |
| `GET /records` page | 1 to 100, default 20 | [44 SC/api/RecordApi.java:150-152] |
| Purge `versionIds` | at most 50 | [44 SC/util/RecordConstants.java:34] |
| `registerDataset` records | 1 to 20 | [C-dataset: CreateDatasetRegistryRequest, L789-800] |
| Dataset retrieval ids | 1 to 20 | [C-dataset: GetDatasetRegistryRequest, L948-959] |
| Legal names per validate or batch read | 1 to 25 | [C-legal: RequestLegalTags, L1068-1080] |
| Signed URL lifetime (File) | default 1 hour, at most 7 days | [90 FC/util/ExpiryTimeUtil.java:21-27] |
| Landing zone | a file without metadata is deleted after 24 hours | [90 docs/docs/File-Service.md:52] |
| Search | section 5.2 | |
| Register interfaces | 1 to 10 per registration | [157 RC/ddms/model/validators/RegisteredInterfacesValidator.java:38-50] |
| Schema list `limit` | 0 to 100 in the contract; the code defaults it to 100 | [C-schema: GET /schema, getSchemaInfoList; 26 SCH/api/SchemaController.java:135-136] |

Errors a writer meets, and what they mean:

| Status | From | Meaning |
| --- | --- | --- |
| 400 | Storage `PUT /records` | Model validation, "Invalid kind", "Invalid record id" (format, tenant, size, duplicate), "Invalid ACL" (domain), "Invalid legal tags", "Invalid other relevant data countries" (section 2.2). |
| 403 | Storage writes | "User is not authorized to update records." (no owner access), "Access denied" (the provider's access check on existing records) [44 SC/service/IngestionServiceImpl.java:253-265, 309-315]; a caller without the service group is refused by the operation's `@PreAuthorize` check (section 1.1). |
| 404 | Storage `PUT /records` | A parent record or parent version is missing. The contract describes 404 as "Invalid acl group." [C-storage: PUT /records, createOrUpdateRecords]; no code path for that was found in the files read. |
| 500 | Storage `PUT /records` | "Unknown error happened when validating ACL" when the caller has no groups or the first group's email does not look like one [44 SC/service/EntitlementsAndCacheServiceImpl.java:78-91]. |
| 206, 207 | Storage patches, bulk delete | Partial success; settle per id from the lists (sections 2.3, 5.4). |
| 400, 405 | Dataset `storageInstructions` | No DMS for the type; storage not allowed (2.5). |
| 400 | Dataset `registerDataset` | Kind not in group `dataset`, id entity type mismatch, no DMS, copy reported `success: false` (2.5). |
| status of the Schema service | Dataset `registerDataset` | The kind's schema lookup failed; the Schema service's code and message are passed on [118 DC/service/DatasetRegistryServiceImpl.java:286-303]. |
| 400 | File `POST /v2/files/metadata` | Kind is not `wks` and `dataset--File.Generic` [90 FC/service/FileMetadataService.java:128-132, 198-208; FC/exception/KindValidationException.java:22-23]. |
| 400 | File `uploadURL`, `downloadURL`, DMS | `expiryTime` present but not `<digits>M`, `<digits>H` or `<digits>D` [90 FC/util/ExpiryTimeUtil.java:98-108]. |
| 404 | Register redirect | No registration, no interface for the type, or no single retrieval operation (2.7). |
| 409 | Register `POST /ddms` | The id is already registered (2.7). |
| 429, 413, 504, 502 | Search | Too many cursors, response over 100 MB, sort timeout, scale-up in progress (5.2). |
| 503 | CRS Conversion | Converter overloaded; retry (2.9). |

## 7. Where the contract and the code differ

| Topic | Contract | Code or documentation |
| --- | --- | --- |
| `PUT /records` batch size | No `maxItems` | 500 [44 SC/api/RecordApi.java:128] |
| `POST /records/delete` batch size | No `maxItems` | 500 [44 SC/api/RecordApi.java:284] |
| JSON patch ids and ops | No limits (`RecordQueryPatch.ids` unbounded, `JsonPatch` an empty schema) | 100 and 100 [44 SC/util/RecordConstants.java:30-32] |
| Record `id` | In `required` | Optional; generated when absent [44 SC/service/IngestionServiceImpl.java:170-172] |
| `recordCount` | "successfully created or updated" | Number of records submitted [67 CC/model/storage/TransferInfo.java:34-37] |
| Country list minimum | 1 | Code 1; Storage documentation 2 [67 CC/model/legal/Legal.java:39-45; 44 docs/docs/index.md:38] |
| `frame-of-reference` | Required | Optional; missing means no conversion [44 SC/service/BatchServiceImpl.java:170-183] |
| `GET /records` `modifiedAfterDate` | `format: date-time` | `yyyy-MM-dd` [44 SC/api/RecordApi.java:157] |
| `GET /query/kinds` | Absent | Present, hidden, deprecated [44 SC/api/QueryApi.java:163-172] |
| Storage roles | `records:batch`, `PATCH /records` and `POST /records/delete` name `users.datalake.*` | `service.storage.*` (section 1.1) |
| Merge patch `deletedAt` | "set to null to undelete" | Ignored; only `deleted` counts [44 SC/service/RecordServiceImpl.java:481-518] |
| Merge patch `data` | `additionalProperties: {type: object}` (the example uses string values) | `Map<String, Object>`: any JSON value [44 SC/dto/RecordMergePatchRequest.java:72-73; SC/service/RecordServiceImpl.java:489-497] |
| JSON patch remove on `/ancestry/parents` | Not described | Documentation allows it; the validator refuses a remove on the whole array [44 docs/docs/api.md:587, 721; SC/validation/impl/JsonPatchValidator.java:199-209, 241-243; SC/util/RecordConstants.java:24-25] |
| Headers-read `attributes` | Exact-case enum | Case-insensitive pattern [44 SC/model/MultiRecordHeadersRequest.java:64-69] |
| File `Checksum` pattern | `^[0-9a-fA-F]32}$`, malformed; a real MD5 does not match it | Shared schemas use `^([0-9a-fA-F]{2})+$` and `^[0-9a-fA-F]{32}` [C-file: FileData, L665-668; FileSourceInfo, L752-758; 26 SS/abstract/AbstractFileSourceInfo.1.0.1.json:93-98; SS/abstract/AbstractFile.1.0.1.json:38-43] |
| File `EncodingFormatTypeID`, `SchemaFormatTypeID` patterns | `^srn:<namespace>:reference-data\\/...`, an old SRN form no R3 reference id matches | The shared schema uses `^[\w\-\.]+:reference-data\-\-EncodingFormatType:[\w\-\.\:\%]+:[0-9]*$` [C-file: FileData, L625-632; FileSourceInfo, L748-751; 26 SS/abstract/AbstractFileSourceInfo.1.0.1.json:85] |
| File `GET /{id}/metadata` role | `service.file.editors` | `service.file.viewers` [90 FC/api/FileMetadataApi.java:76-77] |
| File `DELETE /{id}/metadata` role | `users.datalake.editors` or `admins` | `service.file.editors` or `service.file.admin` [90 FC/api/FileMetadataApi.java:95-96] |
| File download default expiry | 1 hour | Code 1 hour; File documentation 7 days [90 FC/util/ExpiryTimeUtil.java:21-22; docs/docs/File-Service.md:88-89] |
| File `uploadURL` `expiryTime` | Honoured | Ignored on core-plus (2.4) |
| Dataset `expiryTime` | Honoured; the pattern allows lower-case units | `storageInstructions` does not forward it to the DMS as a query parameter; `retrievalInstructions` does, and the File service refuses lower-case units with 400 (2.4, 2.5) |
| Dataset `storageInstructions` role | `service.dataset.editors` | Editors or admin in Dataset; editors only in the File DMS (1.1) |
| Search roles | `users.datalake.viewers`, `editors`, `admins`, `ops` (documentation: the first three) | `service.search.admin` or `service.search.user` [19 SRC/api/SearchApi.java:84-85] |
| Search `limit` | `minimum: 0` | 0 means the default; values above the maximum are capped (5.2) |
| Search `DELETE /query_with_cursor/{cursor}` | Releases the cursor | Does nothing for scroll cursors [19 SRC/api/SearchApi.java:130-138] |
| Schema `GET /schema` `status`, `scope` | Defaults `PUBLISHED`, `INTERNAL` | No default applied; omitted `scope` returns both [26 SCH/service/serviceimpl/SchemaService.java:287-302] |
| Schema scope on create | `SHARED` for the common tenant | `INTERNAL` for every non-system create [26 SCH/service/serviceimpl/SchemaService.java:394-400] |
| Register `?type=`, `id`, `name`, `entityType` patterns | `^[A-Za-z0-9]{1,50}` and similar, with no `$` | The server applies them as whole-string matches (Java `@Pattern`); a contract check that searches (as `osdu/tests/SqlFlow.Delivery.Tests/Contracts/SchemaCheck.cs` does with `Regex.IsMatch`) accepts values the server refuses, such as `master-data--Wellbore` for `type` [157 RC/api/DdmsApi.java:162; RC/ddms/model/RegisteredInterface.java:53] |
| Unit contract texts | 409 "A LegalTag with the given name already exists." on unit operations; `data-partition-id` declared without a schema | Copy artefacts of the generated contract [C-unit3: GET /v3/unit/symbol, getUnitBySymbol] |

What the contract tests will not catch, because the pinned contract is wrong or silent: the File checksum and format
type patterns (a route must not send those fields), the Register pattern anchoring (check `type` locally), the batch
limits Storage enforces without `maxItems` (check them locally), and every role (the contracts carry roles as prose).

## 8. Open items

- `skipdupes` and soft-deleted records: whether the metadata repository returns deleted records to the upsert (2.2).
  Test on a live partition before relying on `skipdupes=true` to restore records.
- `recordIds` and `recordIdVersions` when every record is skipped: null or absent depends on the serializer
  configuration, which was not read (2.2).
- 404 "Invalid acl group" on `PUT /records`: no code path was found; confirm whether any provider raises it (6).
- Cache lifetimes: Storage's legal tag cache and entitlements cache are configured outside the files read (2.2, 2.8).
- Dataset `expiryTime`: confirm on Azure that a Dataset upload location has the default 1-hour lifetime whatever the
  request says (2.5).
- Core-plus collections: confirm that `FileCollectionPath` needs a leading `/` for the copy and the retrieval (2.5, 2.5.1).
- S3 collections: confirm that a collection of more than 1000 files is copied whole (2.5.1).
- IBM collections: the object store endpoint the temporary credentials are for is not returned (2.5.1); the dataset
  route holds such a record until a way to name the endpoint is known.
- Re-registering a Dataset record after the landing-zone clean-up: whether the copy fails once the staging object is
  gone (2.5).
- Dataset retrieval `fileSource`: confirm that it comes back empty on both providers (2.5).
- Search: whether a quoted `id` query matches reliably, and which entitlements groups carry `service.search.user` on a
  given platform (5.3, 1.1).
- Schema `GET /schema` with no `status`: what the stores return (2.8).
- Roles of Legal, Entitlements, Partition, Unit and CRS: the contracts name none and the code was not read (1.1).
- Workflow: the manifest run's status values and the records it creates are covered by
  `osdu/specs/workflows/INTEGRATION.md` (2.6).
