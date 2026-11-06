# Wellbore DDMS: integration brief

The Wellbore Domain Data Management Service (Wellbore DDMS) is the OSDU service for wellbore-domain records and the
tabular bulk data attached to them (log curves, trajectory stations, pressure test measurements). It validates each
record against its kind's schema, stores it through the Storage service, and hands bulk data to a separate bulk worker
service. OSDU Delivery reaches it through two route types: the generic tabular DDMS route, for the four collections
that carry bulk data (`welllogs`, `wellboretrajectories`, `ppfgdataset`, `wellpressuretestrawmeasurement`), which
generalises the current `ddms` protocol; and the DDMS record route, for the five record-only collections
(`wellbores`, `wells`, `wellboremarkersets`, `wellboreintervalsets`, `welllogacquisition`). This brief lists every
call those routes make, each with its contract citation, and the service behaviour the contract does not state, each
with its source citation.

## Sources

| Key | Source | Marking |
| --- | --- | --- |
| `[C: ...]` | `osdu/specs/wellbore-ddms/openapi.json`. OpenAPI 3.1.0, `info.version` "0.29", 62 paths, 84 operations (80 under `/ddms/v3`), 28 component schemas, 470057 bytes, SHA-256 `371b134a9c7cb57ce6a649398f560784fdee402891af755d929bcee733278e21`. Copied from project 98 `docs/api/community/v1/openapi.json` at commit `e5641a0e4e2dde1c67c12eb916087b67e8087c43` (2026-09-10), as `osdu/specs/sources.json` records. | Contract. Cited by method, path and operationId, or by schema pointer. |
| `[98 path:lines]` | Project 98, `osdu/platform/domain-data-mgmt-services/wellbore/wellbore-domain-services`, branch `master`, commit `e5641a0e4e2dde1c67c12eb916087b67e8087c43`: the same commit as the contract, and the branch head on 2026-09-16 and 2026-09-17. | Source: what the code does. The contract does not promise it. |
| `[R029: ...]` | Project 98 `spec/generated/openapi.json` at commit `918175251426c6d890a0b0f219abb9f5ed35ff87` (2026-06-18), the head of branch `release/0.29` on 2026-09-16 and 2026-09-17. `docs/api/community/v1/openapi.json` does not exist at that commit. | Contract of release 0.29. Used in section 8.2 only. |
| `[98-r0.29 path:lines]` | Project 98 source at the same `release/0.29` head, commit `918175251426c6d890a0b0f219abb9f5ed35ff87`. | Source. Section 8.2 only. |
| `[98-v0.29.2 path:lines]` | Project 98 source at tag `v0.29.2`, commit `fa3616c1e3ce0e794669579a3cd2abae6dfc3bfe` (2026-02-24), the latest `v0.29.*` tag on 2026-09-17. | Source. Section 8.2 only. |
| `[1392 path:lines]` | Project 1392, `osdu/platform/domain-data-mgmt-services/wellbore/lib/wellbore-core/wellbore-schema-manipulation`, tag `v0.29.0`, commit `6bc4417ce41b03ecabbd1e7d23295f8d4e79b40f`. Project 98 pins `wellbore-schema-manipulation==0.29.0` [98 pyproject.toml:57]. | Source of a library the service calls. That the published 0.29.0 package was built from this tag is an inference. |
| `[OD path:lines]` | This repository: the current `ddms` protocol and its options. | Current engine behaviour, for comparison only. |
| `!NNN` | Project 98 merge requests, state read on 2026-09-17. | Pending work. Not in the pinned contract or code. |

How statements are marked:

- A `[C: ...]` citation is the contract. The `[98 ...]`, `[98-r0.29 ...]`, `[98-v0.29.2 ...]` and `[1392 ...]`
  citations are code: behaviour the contract does not promise. "Inference" marks a conclusion drawn from cited code or
  contract text that neither states outright.
- In a citation, `{c}` stands for each of the four bulk collections (`welllogs`, `wellboretrajectories`,
  `ppfgdataset`, `wellpressuretestrawmeasurement`). Their operationIds are in section 4.4.
- Other project 98 commits are named in full where they are used.

Provenance notes:

- The core specification set's copy of this contract (`wellbore_ddms/openapi.json` in that set) is byte-identical to
  the pinned file (same size and SHA-256). So is project 98 `docs/api/community/v1/openapi.json` at the
  `release/0.30` head (`dcb6633ccb0a185da75a147fd7a2dccb28990491`) and at tag `v0.30.14`
  (`e43574d81b19d7017bd62d85e028f57ef9ec1f0e`).
- The pinned file is not the release 0.29 contract, although `info.version` reads "0.29". The label comes from
  `__version__ = '0.29'` in `app/__init__.py`, a file that "will be updated in the build pipeline"
  [98 `app/__init__.py`:15-20; 98 app/wdms_app.py:92-96]. The content is the current master (and release 0.30)
  contract. Section 8.2 compares it with the real release 0.29 contract.
- Project 98 `spec/generated/openapi.json` at the pinned commit is the same document without the `servers` entry.

## 1. Base path, versions, headers and auth

### 1.1 Base path

- `servers: [{"url": "/api/os-wellbore-ddms"}]` [C: servers]. Every path in this brief is relative to it. A full URL
  has the shape `https://<host>/api/os-wellbore-ddms/ddms/v3/<collection>/...`.
- The prefix is deployment configuration: `OPENAPI_PREFIX`, default `/api/os-wellbore-ddms`, passed to FastAPI as
  `root_path` [98 app/conf.py:196-200; 98 app/wdms_app.py:92-96]. The route takes the service root from its
  configuration, not from the contract. The current protocol prepends `ProtocolOptions.DdmsRoot` when its endpoint is
  the platform root [OD osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs:522-531;
  OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:541-549].
- `/about`, `/version` and `/log-recognition/*` sit directly under the root. Every entity route sits under `/ddms/v3`
  [C: paths].

### 1.2 Versions

- There is one API version, `/ddms/v3`, and no other prefix [C: paths]. The code mounts only
  `DDMS_V3_PATH = '/ddms/v3'`; `ALPHA_APIS_PREFIX = '/alpha'` is defined and used nowhere
  [98 app/wdms_app.py:242-243]. No branch name contains `v4`, and none of the seven merge requests open on 2026-09-17
  mentions a `v4` or alpha prefix.
- `GET /about` and `GET /version` report `version` "0.29" and `release` "M26 Venus Preview 1" at the pinned commit,
  from a file that says the build pipeline updates it [98 `app/__init__.py`:15-20; 98 app/routers/about.py:34-42, 53-83].
  A route does not infer capabilities from them.
- Deployments built from release 0.29 serve the same URLs with a few behaviour differences. Section 8.2 lists what a
  route must allow for.

### 1.3 Authentication and roles

- `components.securitySchemes.HTTPBearer = {type: http, scheme: bearer}`, with no global `security`
  [C: #/components/securitySchemes/HTTPBearer]. Each of the 83 operations other than `GET /about` declares
  `security: [{"HTTPBearer": []}]` and documents 401 and 403. All 84 operations document 500 [C: every operation].
- Send `Authorization: Bearer <token>`. The service reads it with FastAPI's `HTTPBearer` and keeps it as the request's
  credential [98 app/auth/auth.py:24-31]. Its Storage and Schema clients send the caller's token as
  `Authorization: Bearer` [98 `app/clients/__init__.py`:60-83; 98 app/clients/clients_middleware.py:52-74;
  98 app/schemas/schema_manager.py:127-139], and so do its bulk calls to the worker
  [98 app/context.py:331-344; 98 app/bulk_persistence/bulk_io_wdms_worker.py:52-53].
- Roles, from the operation descriptions [C: e.g. POST /ddms/v3/welllogs, post_welllog_osdu;
  GET /ddms/v3/welllogs/{record_id}, get_welllog_osdu]:
  - write: `users.datalake.editors` or `users.datalake.admins`;
  - read: `users.datalake.viewers`, `users.datalake.editors` or `users.datalake.admins`, and "users must be a member
    of data groups to access the data".
- The role text is absent from the session, statistics and `family` operations, from `GET /version`, and from the GET,
  DELETE and versions operations of `ppfgdataset` and `wellpressuretestrawmeasurement` [C: those operations].

### 1.4 Headers

`data-partition-id`

- Declared on the 82 operations other than `GET /about` and `GET /version`: `in: header`, `required: false`, schema
  `string` with `minLength: 1`, "identifier of the data partition to query" [C: every such operation].
- In code it is `Header(default=None, min_length=1)`, and its value becomes the request's partition
  [98 app/middleware/basic_context_middleware.py:105-110]. Every Storage and Schema call carries that partition
  [98 app/routers/record_utils.py:46-58, 87-98; 98 app/schemas/schema_manager.py:136-139], and it selects the tenant
  for session and bulk blob storage [98 app/routers/sessions.py:97-103; 98 app/routers/delete/delete_bulk_data.py:76-80].
- Inference: the contract makes it optional, but without it those calls carry no partition. The route always sends it.

`Content-Type` on bulk writes

- Contract: the body is `application/json` or `application/x-parquet`, and "The header "Content-Type" must be set
  accordingly to the format sent" [C: POST /ddms/v3/{c}/{record_id}/data;
  POST /ddms/v3/{c}/{record_id}/sessions/{session_id}/data].
- Code: the whole header value, lower-cased, must equal `application/json`, `application/x-parquet` or
  `application/parquet`, or, with dots removed, `json` or `parquet`. Anything else, including a value with parameters
  such as `application/json; charset=utf-8`, gets 400 `Content-Type invalid: "<value>"`
  [98 app/routers/common_parameters.py:14-23; 98 app/bulk_persistence/mime_types.py:18-36, 46-58]. The route sends
  the bare media type. The current protocol's default is `application/x-parquet`
  [OD osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs:496-497].

`Accept` on bulk reads

- Contract: "The desired format can be specify in the "Accept" header, default is Parquet", and with `describe` "the
  response is always provided in JSON" [C: GET /ddms/v3/{c}/{record_id}/data].
- Code: no `Accept`, or any value that contains `*/*`, selects Parquet. Otherwise Parquet is selected if
  `application/x-parquet` or `application/parquet` appears anywhere in the value, then JSON if `application/json`
  does. Nothing matching gives 400 `No supported type found in "<value>"` [98 app/routers/common_parameters.py:15, 26-33].
  The response is labelled with the selected type, whatever the worker returned
  [98 app/bulk_persistence/bulk_io_wdms_worker.py:119-131].
- Inference: for a JSON read, `describe=true` included, the route sends exactly `Accept: application/json`. A client
  default such as `application/json, */*` selects Parquet.

Optional request headers the code reads (not in the contract)

- `correlation-id` and `Request-ID` (a new UUID when absent), `appKey`, `x-api-key`, `x-user-id`, `x-collaboration`
  [98 app/conf.py:551-559; 98 app/middleware/basic_context_middleware.py:57-68].
- The partition, the bearer token, `correlation-id`, `x-user-id`, `x-app-id` when set, and trace headers are forwarded
  to the bulk worker [98 app/context.py:331-344].

Response header

- `Server-Timing: total;dur=<ms>` when `OS_WELLBORE_DDMS_SERVER_TIMINGS_HDR` is true, the default
  [98 app/conf.py:244-249; 98 app/wdms_app.py:462-463; 98 app/middleware/basic_context_middleware.py:30-37].

## 2. Collections

| Collection | Tag | Bulk and sessions | `purge` on DELETE | `record_id` pattern in the contract | Kind of the contract body example | Kind check in the code |
| --- | --- | --- | --- | --- | --- | --- |
| `/ddms/v3/wellbores` | `Wellbore` | no | no | `^[\w\-\.]+:master-data\-\-Wellbore:[\w\-\.\:\%]+$` | `osdu:wks:master-data--Wellbore:1.3.0` | `master-data--Wellbore` |
| `/ddms/v3/wells` | `Well` | no | no | `^[\w\-\.]+:master-data\-\-Well:[\w\-\.\:\%]+$` | `osdu:wks:master-data--Well:1.2.0` | `master-data--Well` |
| `/ddms/v3/wellboremarkersets` | `Marker` | no | no | `^[\w\-\.]+:work-product-component\-\-WellboreMarkerSet:[\w\-\.\:\%]+$` | `osdu:wks:work-product-component--WellboreMarkerSet:1.2.1` (a single object, although the body is an array) | `work-product-component--WellboreMarkerSet` |
| `/ddms/v3/wellboreintervalsets` | `Wellbore IntervalSet` | no | no | `^[\w\-\.]+:work-product-component\-\-WellboreIntervalSet:[\w\-\.\:\%]+$` | `osdu:wks:work-product-component--WellboreIntervalSet:1.0.0` | `work-product-component--WellboreIntervalSet` |
| `/ddms/v3/welllogacquisition` (singular) | `WellLog Acquisition` | no | no | `^[\w\-\.]+:master-data\-\-WellLogAcquisition:[\w\-\.\:\%]+$` | `osdu:wks:master-data--WellLogAcquisition:1.0.0` | none: no map entry |
| `/ddms/v3/welllogs` | `WellLog` | yes, and statistics | yes | `^[\w\-\.]+:work-product-component\-\-WellLog:[\w\-\.\:\%]+$` | `osdu:wks:work-product-component--WellLog:1.2.0` | `work-product-component--WellLog` |
| `/ddms/v3/wellboretrajectories` | `Trajectory v3` | yes | yes | `^[\w\-\.]+:work-product-component\-\-WellboreTrajectory:[\w\-\.\:\%]+$` | `osdu:wks:work-product-component--WellboreTrajectory:1.1.0` | `work-product-component--WellboreTrajectory` |
| `/ddms/v3/ppfgdataset` (singular) | `PPFGDataset v3` | yes | yes | none | none (no body example) | none: the map key is misspelled |
| `/ddms/v3/wellpressuretestrawmeasurement` (singular) | `WellPressureTestRawMeasurement v3` | yes | yes | none | none (no body example) | `work-product-component--WellPressureTestRawMeasurement` |

Contract basis: the patterns appear on the GET, DELETE, versions and versions/{version} operations of the first seven
collections and on no other operation; the examples are on each POST; `purge` is on four DELETEs; the tags are on
every operation [C: operations of each collection]. The WellLogAcquisition POST description points to WellLog 1.5.0
`WellLogAcquisitionDetails.WellLogAcquisitionID` as the reference from a WellLog to this record
[C: POST /ddms/v3/welllogacquisition, post_welllogacquisitionid_osdu].

Kind check (source)

- The check compares only the entity-type segment of `kind` (`authority:source:entity-type:version`) with a fixed map.
  Authority, source and version are free [98 app/routers/ddms_v3/ddms_v3_utils.py:15-24, 29-40;
  98 app/model/entity_utils.py:79-87].
- `welllogacquisition` has no map entry. The PPFGDataset entry is keyed `"ppfgdatset"` while the entity value is
  `"ppfgdataset"`. Neither collection checks the kind [98 app/routers/ddms_v3/ddms_v3_utils.py:22, 36;
  98 app/model/entity_utils.py:29].
- A mismatch is 422 `Record is not an OSDU <entity>` on POST, and 400 with the same text on the other routes, where
  `<entity>` is the lower-case entity value (`welllog`, `trajectory`, `marker`, ...)
  [98 app/routers/ddms_v3/ddms_v3_utils.py:29-47; 98 app/model/entity_utils.py:19-30].

Record id check on the two generic collections (source)

- `ppfgdataset` and `wellpressuretestrawmeasurement` validate `record_id` in code against
  `^[\w\-\.]+:work-product-component\-\-PPFGDataset:[\w\-\.\:\%]+$` and the WellPressureTestRawMeasurement equivalent,
  and answer 422 `Invalid recordID: <message>` on GET, DELETE, versions and versions/{version}
  [98 app/model/osdu_record_id.py:43-44; 98 app/model/api_configuration.py:23-31;
  98 app/routers/ddms_v3/generic_ddms_v3.py:25-41]. The code uses the same pattern family for all nine types
  [98 app/model/osdu_record_id.py:36-44].

Schemas (source)

- A schema must exist for the kind. Schemas bundled with the service are loaded at startup. Any other kind is fetched
  from the Schema service with the caller's token, and only `osdu:wks` kinds are cached
  [98 app/schemas/schema_manager.py:69-93, 127-181]. Bundled `osdu:wks` versions at the pinned commit
  [98 app/schemas/known_schemas/]:

| Entity | Bundled versions |
| --- | --- |
| Well | 1.0.0, 1.1.0, 1.2.0, 1.3.0 |
| Wellbore | 1.0.0, 1.1.0, 1.1.1, 1.2.0, 1.3.0, 1.4.0 |
| WellLogAcquisition | 1.0.0 |
| WellLog | 1.0.0, 1.1.0, 1.2.0, 1.3.0, 1.4.0, 1.5.0 |
| WellboreTrajectory | 1.0.0, 1.1.0, 1.2.0, 1.3.0 |
| WellboreMarkerSet | 1.0.0, 1.1.0, 1.2.0, 1.2.1, 1.3.0, 1.4.0 |
| WellboreIntervalSet | 1.0.0, 1.1.0, 1.2.0 |
| PPFGDataset | 1.2.0 |
| WellPressureTestRawMeasurement | 1.0.0, 1.1.0 |

Record-route operationIds [C]:

| Collection | POST | GET `{record_id}` | DELETE | GET versions | GET versions/{version} |
| --- | --- | --- | --- | --- | --- |
| `wellbores` | `post_wellbore_osdu` | `get_wellbore_osdu` | `del_osdu_wellbore` | `get_osdu_wellbore_versions` | `get_osdu_wellbore_version` |
| `wells` | `post_well_osdu` | `get_well_osdu` | `del_osdu_well` | `get_osdu_well_versions` | `get_osdu_well_version` |
| `wellboremarkersets` | `post_wellboremarkerset_osdu` | `get_wellbore_markerset_osdu` | `del_osdu_wellboremarkerset` | `get_osdu_wellboremarkerset_versions` | `get_osdu_wellboremarkerset_version` |
| `wellboreintervalsets` | `post_wellboreintervalsetid_osdu` | `get_wellboreintervalsetid_osdu` | `del_osdu_wellboreintervalsetid` | `get_osdu_wellboreintervalsetid_versions` | `get_osdu_wellboreintervalsetid_version` |
| `welllogacquisition` | `post_welllogacquisitionid_osdu` | `get_welllogacquisitionid_osdu` | `del_osdu_welllogacquisitionid` | `get_osdu_welllogacquisitionid_versions` | `get_osdu_welllogacquisitionid_version` |
| `welllogs` | `post_welllog_osdu` | `get_welllog_osdu` | `del_osdu_welllog` | `get_osdu_welllog_versions` | `get_osdu_welllog_version` |
| `wellboretrajectories` | `post_wellboretrajectory_osdu` | `get_wellbore_trajectory_osdu` | `del_osdu_wellboretrajectory` | `get_osdu_wellboretrajectory_versions` | `get_osdu_wellboretrajectory_version` |
| `ppfgdataset` | `create_or_update_osdu_record` | `get_osdu_record` | `delete_osdu_record` | `get_record_versions` | `get_specific_record_version` |
| `wellpressuretestrawmeasurement` | `post__ddms_v3_wellpressuretestrawmeasurement` | `get__ddms_v3_wellpressuretestrawmeasurement__record_id_` | `delete__ddms_v3_wellpressuretestrawmeasurement__record_id_` | `get__ddms_v3_wellpressuretestrawmeasurement__record_id__versions` | `get__ddms_v3_wellpressuretestrawmeasurement__record_id__versions__version_` |

Routes outside delivery

- `POST /log-recognition/family` (body `GuessRequest`, 200 `GuessResponse`, 404 "Family not found") and
  `PUT /log-recognition/upload-catalog` (body `CatalogRecord`, 200 `CreateUpdateRecordsResponse`; a replaced catalog
  takes up to 5 minutes to apply) [C: POST /log-recognition/family, family;
  PUT /log-recognition/upload-catalog, upload-catalog].
- `GET /about` and `GET /version`: section 6.6.

## 3. The calls a writer makes

| # | Purpose | Request | Contract |
| --- | --- | --- | --- |
| 1 | Create or update records | `POST /ddms/v3/<collection>`, body `array<Record>` | [C: POST /ddms/v3/welllogs, post_welllog_osdu] and the POST of each collection (section 2) |
| 2 | Write the whole bulk | `POST /ddms/v3/{c}/{record_id}/data` | [C: POST /ddms/v3/{c}/{record_id}/data] |
| 3 | Open a session | `POST /ddms/v3/{c}/{record_id}/sessions` | [C: POST /ddms/v3/{c}/{record_id}/sessions] |
| 4 | Send one chunk | `POST /ddms/v3/{c}/{record_id}/sessions/{session_id}/data` | [C: POST /ddms/v3/{c}/{record_id}/sessions/{session_id}/data] |
| 5 | Commit or abandon | `PATCH /ddms/v3/{c}/{record_id}/sessions/{session_id}` | [C: PATCH /ddms/v3/{c}/{record_id}/sessions/{session_id}] |
| 6 | Read a session's state | `GET /ddms/v3/{c}/{record_id}/sessions/{session_id}` | [C: GET /ddms/v3/{c}/{record_id}/sessions/{session_id}] |
| 7 | Read the record back | `GET /ddms/v3/<collection>/{record_id}` | [C: GET /ddms/v3/welllogs/{record_id}, get_welllog_osdu] and siblings |
| 8 | List versions | `GET /ddms/v3/<collection>/{record_id}/versions` | [C: GET /ddms/v3/welllogs/{record_id}/versions, get_osdu_welllog_versions] and siblings |
| 9 | Describe the bulk | `GET /ddms/v3/{c}/{record_id}/data?describe=true` | [C: GET /ddms/v3/{c}/{record_id}/data] |
| 10 | Delete | `DELETE /ddms/v3/<collection>/{record_id}` | [C: DELETE /ddms/v3/welllogs/{record_id}, del_osdu_welllog] and siblings |
| 11 | Probe | `GET /about` | [C: GET /about, get__about] |

Sequence for a record with bulk:

1. Call 1 writes the metadata and returns `<id>:<version>`.
2. Either call 2 once with the entire bulk, or call 3, then call 4 once per chunk, then call 5 with `commit`. After a
   failure between call 3 and the commit, call 5 with `abandon`.
3. Calls 7 and 9 read back the version, the rows and the columns (section 6.4).

The record-only collections use calls 1, 7, 8 and 10 (without `purge`).

Order rules:

- Call 1 comes first. The bulk and session calls read the existing record
  [98 app/routers/bulk/bulk_routes.py:106; 98 app/routers/sessions.py:143-145].
- A whole-bulk write reads the latest record and writes it back, with the new bulkURI, as a new version
  [98 app/routers/bulk/bulk_routes.py:106, 125-128].
- A commit reads the record at the session's `fromVersion` and writes that record back, with the new bulkURI, as the
  latest version [98 app/routers/bulk/bulk_routes.py:308, 336; 98 app/routers/bulk/utils.py:81-86]. Inference: a
  metadata version written between opening and committing a session is replaced in the next version by the older
  metadata, so the session is opened after the record's last metadata write. The current protocol opens the session
  with `fromVersion` set to the version its metadata write returned
  [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:437-446].

### 3.1 Create or update the record (call 1)

`POST /ddms/v3/<collection>` [C: POST /ddms/v3/welllogs, post_welllog_osdu;
POST /ddms/v3/wellboretrajectories, post_wellboretrajectory_osdu; POST /ddms/v3/ppfgdataset,
create_or_update_osdu_record; POST /ddms/v3/wellpressuretestrawmeasurement,
post__ddms_v3_wellpressuretestrawmeasurement; the record-only POSTs in section 2].

- Request: `application/json`, required. The body is a JSON array of `Record` (`{type: array, items: Record}`), with
  schema title `Welllogs`, `Wellboretrajectories` and so on, and `Input Records` on the two generic collections. The
  record shape is in section 4.1.
- Response 200: `CreateUpdateRecordsResponse` [C: #/components/schemas/CreateUpdateRecordsResponse]: `recordCount`
  (int64), `recordIds[]`, `recordIdVersions[]`, `skippedRecordIds[]`, all nullable, none required.
- Each `recordIdVersions` entry is `<id>:<version>`; the service parses its own Storage responses that way
  [98 app/routers/bulk/bulk_routes.py:341-343]. The DDMS returns the Storage create-or-update response as it is
  [98 app/routers/ddms_v3/welllog_ddms_v3.py:177-182; 98 app/routers/ddms_v3/generic_ddms_v3.py:160-165]. The current
  protocol reads the version from `recordIdVersions[0]` [OD osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs:499-500].
- Documented errors: 400 "Missing mandatory parameter or unknown parameter", 401, 403, 422, 500 [C].
- On the four bulk collections the description adds the BulkURI rules (section 5.3) [C: POST /ddms/v3/{c}].

Checks, in order (source) [98 app/routers/ddms_v3/welllog_ddms_v3.py:158-182;
98 app/routers/ddms_v3/wellbore_trajectory_ddms_v3.py:152-169; 98 app/routers/ddms_v3/generic_ddms_v3.py:145-165]:

1. JSON-schema validation of every record against its kind's schema, in mode `EXTRA_FORBID_OPTIMISED`
   [98 app/schemas/schema_manager.py:183-211]. That mode validates against a copy of the schema where
   `additionalProperties: false` is added beside every `properties` outside `allOf`, `unevaluatedProperties: false`
   beside every `allOf` and on array items that have properties, and `$schema` becomes `https://json-schema.org/draft/2019-09/schema#`
   [98 app/schemas/schema_manager.py:44-67; 1392 src/wellbore_schema_manipulation/schema_manipulation.py:54-95, 119-132].
   A property the schema does not declare is refused. Properties whose value is null are dropped before validation
   [98 app/schemas/schema_manager.py:192-193]. Failure: 422 `{"errors": "Value of <path> is invalid: <message>"}`
   [98 app/errors/validation_error.py:41-52]. An unknown kind fails in the Schema lookup, with the Schema service's
   status passed through [98 app/errors/client_error.py:115-130].
2. Kind check (section 2). Failure: 422 [98 app/routers/ddms_v3/ddms_v3_utils.py:42-47].
3. BulkURI rules, bulk collections only (section 5.3). Failure: 400 [98 app/routers/ddms_v3/ddms_v3_utils.py:49-140].
4. Record consistency, bulk collections only. Failure: 400.
   - WellLog: `data.Curves[].CurveID` unique, else `All CurveID in WellLog[<i>] should be unique`. A non-empty
     `data.ReferenceCurveID` must be one of them, else `WellLog[<i>] should have a curve with a curveID value equal to
     the ReferenceCurveID value: '<value>'` [98 app/consistency/welllog_consistency.py:50-73;
     98 app/routers/ddms_v3/welllog_ddms_v3.py:162-175]. The current protocol holds such a record before sending it
     [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:91-98, 191-218].
   - WellboreTrajectory: `data.AvailableTrajectoryStationProperties[].Name` unique, else
     `All station properties in WellboreTrajectory[<i>] should be unique`
     [98 app/consistency/trajectory_consistency.py:37-60; 98 app/routers/ddms_v3/wellbore_trajectory_ddms_v3.py:155-162].
   - PPFGDataset: `data.ContextTypeID` and `data.ReferenceWellTrajectoryID` are required (non-empty), `CurveID` is
     unique, and a non-empty `data.PrimaryReferenceCurveID` must be a `CurveID`
     [98 app/consistency/ppfgdataset_consistency.py:59-92]. These exceptions are raised without a message and the 400
     `detail` is `str(e)`, so it is empty [98 app/routers/ddms_v3/generic_ddms_v3.py:151-159]. The route names the
     rule itself.
   - WellPressureTestRawMeasurement: `CurveID` unique, else `All CurveIDs in the metadata must be unique.`
     [98 app/consistency/wellpressuretestrawmeasurement_consistency.py:33-43].
   - Uniqueness ignores curves whose id is empty or absent [98 app/consistency/unique.py:35-59].
5. Storage `create_or_update_records` with the request's partition.

The record-only collections run steps 1, 2 and 5 only [98 app/routers/ddms_v3/well_ddms_v3.py:130-142;
98 app/routers/ddms_v3/wellbore_ddms_v3.py:135-149; 98 app/routers/ddms_v3/markerset_ddms_v3.py:142-154;
98 app/routers/ddms_v3/wellbore_interval_set_ddms_v3.py:133-146; 98 app/routers/ddms_v3/welllog_acquisition_v3.py:136-148].

Create or update:

- One endpoint does both: "Create or update the WellLogs using osdu schema" [C: POST /ddms/v3/welllogs,
  post_welllog_osdu]; "Create or update record using osdu schema" [C: POST /ddms/v3/ppfgdataset,
  create_or_update_osdu_record].
- The DDMS does not choose between them; it passes the array to Storage. A record without `id` is a create. The
  BulkURI check reads the latest stored version of a body `id` and treats a 404 there as "no previous version"
  [98 app/routers/ddms_v3/ddms_v3_utils.py:76-99].
- The records of one request are checked concurrently, and every check runs before the Storage call, so one failing
  record fails the request before anything is written [98 app/routers/ddms_v3/ddms_v3_utils.py:139-140;
  98 app/routers/ddms_v3/generic_ddms_v3.py:145-165].

### 3.2 Whole-bulk write (call 2)

`POST /ddms/v3/{c}/{record_id}/data` [C: POST /ddms/v3/welllogs/{record_id}/data, write_record_data; the other three
`post__ddms_v3_{c}__record_id__data`].

- `record_id`: path, string, no pattern (no bulk or session operation has one). No query parameters, and no `orient`
  parameter on writes [C].
- Body, required: `application/json` (pandas DataFrame JSON, orient "split") or `application/x-parquet` (string,
  binary) [C: requestBody]. Details in section 4.2.
- Contract semantics [C: description]: "Writes data to the associated record. It creates a new version. Payload is
  expected to contain the entire bulk which will replace as latest version any previous bulk. Previous bulk versions
  are accessible via the get bulk data version API." "Support JSON and Parquet format ('Content_Type' must be set
  accordingly). Support http chunked encoding transfer."
- Responses: 200 `application/json` with an empty schema `{}`; 401, 403, 404, 422, 500 [C].
- Source: the 200 body is the Storage create-or-update response for the new record version (the
  `CreateUpdateRecordsResponse` shape, new version in `recordIdVersions[0]`)
  [98 app/routers/bulk/bulk_routes.py:125-130; 98 app/routers/bulk/utils.py:81-86]. The contract does not promise it;
  the current protocol reads the record back instead
  [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:155-166].

What the code does [98 app/routers/bulk/bulk_routes.py:95-130]:

1. Reads the latest full record and checks its kind.
2. Reads the whole request body into memory and sends it to the bulk worker (`POST /data/<record id>` on
   `SERVICE_HOST_WDMS_WORKER`), with the collection's reference curve name as `reference`
   [98 app/bulk_persistence/bulk_io_wdms_worker.py:133-166; 98 app/conf.py:128-130]. It then runs the collection's
   bulk consistency checks on the description the worker returns (section 4.3). A worker error status is passed
   through with the worker's body as `detail` [98 app/bulk_persistence/bulk_io_wdms_worker.py:35-40;
   98 app/bulk_persistence/errors.py:22-34].
3. Sets `data.ExtensionProperties.wdms.bulkURI` to `urn:wdms-1:uuid:<bulk id>`. For the kinds and versions below it
   also appends `urn://wdms-1/uuid:<bulk id>` to `data.DDMSDatasets` [98 app/routers/bulk/bulk_routes_dependencies.py:13-22, 55-74;
   98 app/bulk_persistence/bulk_uri.py:93-113; 98 app/bulk_persistence/bulk_storage_version.py:17].
4. Writes the record to Storage as a new version [98 app/routers/bulk/utils.py:81-86].

| Kind (`...--<Type>:<version>`) | `DDMSDatasets` appended from version |
| --- | --- |
| WellboreIntervalSet | 1.1.0 |
| WellboreTrajectory | 1.2.0 |
| WellboreMarkerSet | 1.3.0 |
| WellLog | 1.3.0 |
| PPFGDataset | 1.1.0 |
| WellPressureTestRawMeasurement | 1.0.0 |

Inference: the append happens on every bulk write and commit, and nothing removes older entries, so `DDMSDatasets`
gains one entry each time.

### 3.3 Session write (calls 3 to 6)

Open a session (call 3): `POST /ddms/v3/{c}/{record_id}/sessions` [C: `post__ddms_v3_{c}__record_id__sessions`].

Body `CreateDataSessionRequest`, required [C: #/components/schemas/CreateDataSessionRequest]:

| Field | Type | Default | Contract text |
| --- | --- | --- | --- |
| `mode` | `SessionUpdateMode`: `overwrite` or `update` | required | "merge mode at commit. If 'update', existing data will be merged with the data sent during the session. If 'overwrite', existing data will be ignored, the final result will only contains data sent within the session." |
| `fromVersion` | int64 | 0 | "specify the version on top of which update will be applied. By default use the latest one (0). Not relevant if overwrite is set to True." |
| `timeToLive` | int64 | 1440 | "optional - time to live in minutes." |
| `meta` | object with string values, or null | none | "miscellaneous metadata associated to the session. The session creator can set some data here." |

- Named examples sit under `schema.examples`: `update` `{"mode":"update"}`, `overwrite` `{"mode":"overwrite"}`,
  `full` `{"fromVersion":123456789,"mode":"update"}`, and `set session meta`
  `{"mode":"update","meta":{"extendedLoadCompleted":true}}` [C: POST /ddms/v3/welllogs/{record_id}/sessions]. The last
  sends a boolean, although `meta` values are typed string.
- Response 200: `Session` [C: #/components/schemas/Session]: `id` (uuid), `recordId`, `fromVersion` (int64), `mode`,
  `expiry` (date-time), `createdTime`, `updatedTime`, `state` (`SessionState`), all required, and optional `meta`.
  Also documented: 401, 403, 404, 422, 500.
- Contract text [C: POST /ddms/v3/{c}/{record_id}/sessions]: "Initiate a session based on record version provided.
  The session is isolated from any other modifications. Inside a session, individual chunk doesn't generate new
  individual record version." "A new single version is created only at session completion 'aggregating' all
  updates." Its typical workflow: create a session, send X chunks ("can be parallelized"), commit the session.
  "Session has an expiry time. If the session is not completed before, it's automatically dropped. The session
  duration is specified in the request but cannot exceeds 24 hours." "For `WellLog` and `WellboreTrajectory` kind,
  the attribute extendedLoadCompleted can be set into session.meta to set "record.data.IsExtendedLoad" to False
  after completing the session" (emphasis dropped). `Session.expiry`: "If the session is not committed before this
  dead line, session is automatically abandoned." [C: #/components/schemas/Session].

What the code does [98 app/routers/sessions.py:131-173; 98 app/bulk_persistence/sessions_storage.py:240-250]:

- Reads the latest record and checks its kind. `fromVersion: 0` becomes the record's current version. Any other value
  must exist in Storage; otherwise the Storage error is passed through.
- Stores the collection's reference curve name (section 4.3) as `meta.reference_curve`.
- Sets `expiry` to now plus `timeToLive` minutes, with no upper bound. No code path reads `Session.is_expired`
  [98 app/bulk_persistence/sessions_storage.py:86-88], so expiry enforcement is not visible in this service.
- Converts `meta` values with `str()` [98 app/model/model_utils.py:21-31], so `true` is stored as `"True"`. The commit
  lower-cases the value, so both forms work at the pinned commit; section 8.2 explains why the route sends `"true"`.
- Keeps the session as a JSON blob `sessions/<record id>/<session id>` in the tenant's blob storage, updated under
  etags [98 app/bulk_persistence/sessions_storage.py:186-209].
- `fromVersion` is recorded in `overwrite` mode too, and the commit reads the record at that version (section 3,
  order rules).

Send a chunk (call 4): `POST /ddms/v3/{c}/{record_id}/sessions/{session_id}/data`
[C: POST /ddms/v3/welllogs/{record_id}/sessions/{session_id}/data, post_chunk_data; the other three
`post__ddms_v3_{c}__record_id__sessions__session_id__data`].

- `session_id`: path, string, uuid. Body and content types are those of the whole-bulk write. "Support http chunked
  encoding." [C].
- Response 200: `DataframeBasicDescribe` [C: #/components/schemas/DataframeBasicDescribe]: `rowCount` and
  `columnCount` (int64), `columns[]` ("May be truncated if too many columns, then contains the firsts and lasts
  once"), `indexStart`, `indexEnd`, `indexType` (strings), all required.
- Documented errors: 400 "Record not found", 401, 403, 404, 422, 500 [C].
- Source [98 app/routers/bulk/bulk_routes.py:151-185; 98 app/routers/sessions.py:92-94;
  98 app/bulk_persistence/sessions_storage.py:170-175, 227-228; 98 app/bulk_persistence/bulk_io_wdms_worker.py:42-71]:
  - The route does not read the record.
  - An unknown session is 404 (`... not found.`). A session that is not `open` is 400
    `Session cannot accept data, state=<state>`. That state check is the only 400 the route itself raises.
  - The chunk is read into memory and sent to the worker with the session's reference curve.
  - With the worker backend, `columns` is always `[]`.
  - No consistency check runs per chunk; the checks run at commit (inference: the chunk path calls none).

Commit or abandon (call 5): `PATCH /ddms/v3/{c}/{record_id}/sessions/{session_id}`
[C: `patch__ddms_v3_{c}__record_id__sessions__session_id_`].

- Body `UpdateSessionState {state}`, `state` required, `UpdateSessionStateValue` = `commit` or `abandon`; named
  examples `commit` and `abandon` under `schema.examples` [C: #/components/schemas/UpdateSessionState].
- Response 200: `CommitSessionResponse`, the `Session` fields plus `version` (int64 or null), "Record version in case
  of successful commit" [C: #/components/schemas/CommitSessionResponse].
- Documented errors: 401, 403, 404, 409 "Conflict", 412 "Precondition failed", 422, 500 [C].
- Contract text: commit "validates the session' bulk data, a new version of record will be created with data sent
  within the session"; abandon leaves the record unchanged; "bulk data consistency check will be run when committing
  bulk data" [C].

**The reference curve of a session has to be a bulk column (observed live, not in the contract).** Each chunk of a
session may carry the reference curve as its dataframe's row index, and every chunk is then accepted with a 200 whose
`indexStart` and `indexEnd` are that curve's values; the commit answers `422 {"detail": "Bulk error: reference curve
'<name>' do not cover the entire bulk, <n> values are missing."}`, where `<n>` is every row of the session. The same
chunks, with the reference curve written as a column and the rows labelled by a range that continues from one chunk to
the next, commit with a 200. A whole-bulk write (call 2) of a file whose index is the reference curve is accepted and
reads back correctly, so the rule belongs to the session path alone. Seen on ADME 0.29 (partition `dev`) on
2026-09-17 with two chunks of five and four rows; the worker raises it, so neither the contract nor project 98 carries
it. The route holds a record whose session chunks do not carry the reference curve as a column
[OD osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsRules.cs, `SessionReferenceProblem`].

Commit, in code [98 app/routers/bulk/bulk_routes.py:298-350; 98 app/bulk_persistence/sessions_storage.py:276-311]:

1. The session moves to `committing`.
2. The record is read at `session.fromVersion` and its kind checked.
3. In `update` mode, that record's bulkURI is the merge base. An unparsable one is 422.
4. The worker completes the session (`PATCH /data/<record id>/session/<session id>` with `completion=<mode>`,
   `reference`, `from_bulk`), and the consistency checks run on the description it returns
   [98 app/bulk_persistence/bulk_io_wdms_worker.py:73-96].
5. If `meta.extendedLoadCompleted`, lower-cased, is `"true"` and the record has `data.IsExtendedLoad`, that field is
   set to false, whatever the kind [98 app/routers/bulk/bulk_routes.py:328-331].
6. The record, with the new bulkURI, is written to Storage as a new version. `version` is parsed from
   `recordIdVersions[0]`.
7. The session moves to `committed`, and the response is built from it, so a successful commit answers
   `state: committed` [98 app/bulk_persistence/sessions_storage.py:294-311; 98 app/routers/bulk/bulk_routes.py:338-350].

Abandon moves the session through `abandoning` to `abandoned` and returns it without `version`
[98 app/routers/bulk/bulk_routes.py:353-365].

Failures and conflicts (source):

- If a step fails after the state change, the session is forced back to `open`, so the commit or abandon can be sent
  again [98 app/bulk_persistence/sessions_storage.py:298-311].
- 409 (`SessionInvalidState`): the session is already `committed` or `abandoned`; or it is `committing` or
  `abandoning` and was updated less than 5 minutes ago; or a commit is asked of a session in `abandoning`.
- 412 (`SessionUpdatedEtagUnmatched`): the session blob changed concurrently.
- Sources: [98 app/bulk_persistence/sessions_storage.py:154-167, 186-209, 335-390].
- The current protocol settles a commit answered with 409, 412 or 5xx by reading the session's state (call 6)
  [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:473-505].

Session states [C: #/components/schemas/SessionState]: `open`, `committing`, `abandoning`, `committed`, `abandoned`.
Allowed changes in code [98 app/bulk_persistence/sessions_storage.py:340-390]:

| From | To |
| --- | --- |
| `open` | `committing`, `abandoning` |
| `committing` | `committed`; `committing` or `abandoning` again only after 5 minutes without update |
| `abandoning` | `abandoned`; `abandoning` again only after 5 minutes without update |
| `committed`, `abandoned` | none (409) |
| any, after a failed commit or abandon | `open` (forced) |

Inspect sessions (call 6 and its list form):

- `GET /ddms/v3/{c}/{record_id}/sessions` returns `Session[]`, and `GET /ddms/v3/{c}/{record_id}/sessions/{session_id}`
  returns `Session`. Both document 404 [C].
- Both read the latest record and check its kind first [98 app/routers/sessions.py:182-189, 212-227].

Not in the contract: `DELETE /{record_id}/sessions/{session_id}` exists with `include_in_schema=False` and the summary
"TEMPORARY: delete session." [98 app/routers/sessions.py:192-203]. The route does not use it.

## 4. Payload and bulk data shapes

### 4.1 Record

`Record` [C: #/components/schemas/Record], with `kind`, `acl`, `legal` and `data` required:

| Field | Contract type |
| --- | --- |
| `kind` | string, required |
| `acl` | `StorageAcl`, required: `viewers[]` and `owners[]`, both required string arrays |
| `legal` | `Legal`, required: `legaltags[]` and `otherRelevantDataCountries[]`, each a string array or null, neither required |
| `data` | object, additional properties allowed, required |
| `id` | string or null |
| `version` | int64 or null |
| `ancestry` | `RecordAncestry` (`parents[]`, string array or null) or null |
| `meta` | array of objects, or null |
| `tags` | object with string values, or null |
| `createTime`, `modifyTime` | date-time string or null |
| `createUser`, `modifyUser` | string or null |

- The contract leaves `data` open, while the service validates it against the kind's schema with undeclared
  properties refused (section 3.1). Inference: the contract schema alone does not tell whether a record passes.
- Body examples exist for the record-only collections, `welllogs` and `wellboretrajectories`, and not for
  `ppfgdataset` or `wellpressuretestrawmeasurement` [C: POST operations]. They use placeholders such as
  `{{datapartitionid}}` and `data.default.owners@{{datapartitionid}}.{{domain}}`.

### 4.2 Bulk data

- JSON: a pandas DataFrame serialised with orient "split", `{"columns":[...],"index":[...],"data":[[...],...]}`, for
  example `{"columns":["Ref","col_1","col_2"],"index":[0,1,2,3,4],"data":[[0.0,1111.1,2222.1],...]}`
  [C: requestBody description of POST /ddms/v3/{c}/{record_id}/data]. The JSON media type has no structural schema,
  only a string `example` [C: same requestBody].
- Parquet: `application/x-parquet`, schema `{type: string, format: binary}` [C: same requestBody].
- "Ensure all curve's values are in the same chunk to be sent" [C: POST /ddms/v3/{c}/{record_id}/data description].
- Column names are the record's curve ids (section 4.3). An array curve is sent as one column per element, labelled
  `<name>[<i>]`. The checks group labels of the form `<name>[...]` under `<name>` and count them
  [98 app/bulk_persistence/consistency_checks.py:195-227]. Reads select them as `ARR`, `ARR[100]` or `ARR[10:50]`
  [C: parameter `curves` examples].
- The v3 bulk routes register a validator requiring string column names [98 app/routers/bulk/utils.py:23-28;
  98 app/wdms_app.py:272]. The service also defines validators for a non-empty frame, a numeric or datetime unique
  index, at most 3000 columns, and the reserved column names `__index_level_<n>__`, `__null_dask_index__` and
  `_wdms_index_` [98 app/bulk_persistence/dataframe_validators.py:42-101; 98 app/bulk_persistence/utils.py:26].
- The worker backend receives the validator and never calls it [98 app/bulk_persistence/bulk_io_wdms_worker.py:42-71, 141-166].
  Which of these rules apply is up to the worker (section 9).

### 4.3 Per-collection bulk consistency (source)

These checks run on the whole-bulk write and at session commit, on the worker's description of the data
[98 app/bulk_persistence/bulk_io_wdms_worker.py:93-95, 163-165]. A failure is 400 (`ConsistencyException`)
[98 app/bulk_persistence/consistency_checks.py:16-17].

| | WellLog | WellboreTrajectory | PPFGDataset | WellPressureTestRawMeasurement |
| --- | --- | --- | --- | --- |
| Each column name must be | a `data.Curves[].CurveID`. Columns with no `data.Curves` fail. A curve without `CurveID` fails. | a `data.AvailableTrajectoryStationProperties[].Name`. The property is required when the bulk has columns. | a `data.Curves[].CurveID`. Columns with no `data.Curves` fail. | a `data.Curves[].CurveID`. Columns with no `data.Curves` fail. |
| Columns per curve | must equal `NumberOfColumns` (default 1), for curves present in the bulk | not checked | not checked: a width of 1 is assigned and never compared | must equal `NumberOfColumns` (default 1) |
| Reference column (also the session's `reference_curve`) | `data.ReferenceCurveID` | `Name` of the first station whose `TrajectoryStationPropertyTypeID` contains `:reference-data--TrajectoryStationPropertyType:MD:` | `data.PrimaryReferenceCurveID` | none |
| Reference checks | Only when the reference is a bulk column and the reference curve's `LogCurveFamilyID` equals `<namespace>:reference-data--LogCurveFamily:Measured%20Depth:`, where `<namespace>` is the first segment of the record id. No repeated values, no NaN, increasing or decreasing. `SamplingStart` and `SamplingStop`, when present, equal the first and last row values (`math.isclose`, default relative tolerance 1e-9). | Only when the reference is a bulk column. Same monotonic rule. `TopDepthMeasuredDepth` and `BaseDepthMeasuredDepth`, when present, equal the first and last row values. | none | none |
| Source | 98 app/consistency/welllog_consistency.py:76-212 | 98 app/consistency/trajectory_consistency.py:63-166 | 98 app/consistency/ppfgdataset_consistency.py:95-156 | 98 app/consistency/wellpressuretestrawmeasurement_consistency.py:46-113 |

- The reference rules are in [98 app/consistency/reference_check.py:9-46]. The namespace comes from
  [98 app/model/entity_utils.py:66-67].
- The comparison is one-way: every bulk column must be declared, and the record may declare curves the bulk does not
  carry (inference from the code).
- The bundled PPFGDataset 1.2.0, WellLog 1.5.0 and WellPressureTestRawMeasurement 1.0.0 and 1.1.0 schemas do not
  require `CurveID` in `Curves[]` items [98 app/schemas/known_schemas/]. Only the WellLog check guards a curve without
  one. In the PPFGDataset and WellPressureTestRawMeasurement checks it raises `KeyError`, which no handler maps to a
  client error, so a bulk write or commit answers 500 [98 app/consistency/ppfgdataset_consistency.py:147-149;
  98 app/consistency/wellpressuretestrawmeasurement_consistency.py:89-92; 98 app/errors/unhandled_error.py:20-25]
  (inference from the code). The route requires a `CurveID` on every curve before sending.

### 4.4 The four bulk collections on the wire

Operation by operation, ignoring `operationId` and `tags`, the contract is identical for the four collections on POST
and GET `/sessions`, GET and PATCH `/sessions/{session_id}`, POST and GET `/data`, POST `/sessions/{session_id}/data`
and GET `/versions/{version}/data`. The only other difference is the generated title of the session list response,
`Response List Session Ddms V3 <Collection>  Record Id  Sessions Get` [C]. The code mounts the same `sessions.router`
and `bulk_routes.router` under each prefix, each with its collection's consistency checks
[98 app/wdms_app.py:281-415].

The differences:

1. Collection segment: `welllogs` and `wellboretrajectories` are plural; `ppfgdataset` and
   `wellpressuretestrawmeasurement` are singular. The segment cannot be derived from the kind name.
2. operationIds: only `welllogs` keeps the explicit `write_record_data` and `post_chunk_data`. Every other bulk
   operationId is generated. The code keeps the first use of an explicit id and names every other operation
   `<method>_<path>`, lower-cased, with every character other than letters, digits and dots replaced by `_`
   [98 app/routers/bulk/bulk_routes.py:73-74; 98 app/wdms_app.py:151-190].
3. Tags: `WellLog`, `Trajectory v3`, `PPFGDataset v3`, `WellPressureTestRawMeasurement v3`.
4. Statistics routes exist only under `welllogs` [C; 98 app/wdms_app.py:341-354].
5. Record routes: only `welllogs` and `wellboretrajectories` have a `record_id` pattern. The two generic collections
   have no role text on GET, DELETE and versions, and their 404 reads "Record not found" instead of
   `<Type> not found`.
6. Record POST: same body schema, different titles, and body examples only for `welllogs` and `wellboretrajectories`.
7. Code only: the consistency rules (section 4.3) and the missing PPFGDataset kind check (section 2).

Query parameters of the two data reads [C: GET /ddms/v3/{c}/{record_id}/data;
GET /ddms/v3/{c}/{record_id}/versions/{version}/data]:

| Parameter | Schema | Contract text |
| --- | --- | --- |
| `offset` | int64, minimum 0, or null | "The number of rows that are to be skipped and not included in the result." |
| `limit` | int64, minimum 1, or null | "The maximum number of rows to be returned." |
| `curves` | string or null | "List of curves to be returned. The curves are returned in the same order as it is given." Examples: `""` (all), `MD,GR`, `ARR[10:50]`, `ARR[100]`, `MD,ARR[50:55],GR`. |
| `describe` | boolean or null, default false | "request a description of the matching result. (number of rows, columns name)" |
| `filter` | string array or null | `$column_name:$operator:$value`, operators `lt, lte, gt, gte, eq, neq, in`; a column name containing `:` goes in double quotes. Examples: `["MD:gte:1000"]`, `["MD:lte:1000"]`, `["MD:gt:1000","MD:lt:42000"]`. |
| `orient` | `JSONOrient` (`split` or `columns`), default `split` | "format for JSON only." |

Bulk operationIds [C]:

| Suffix after `/ddms/v3/<collection>` | `welllogs` | `wellboretrajectories`, `ppfgdataset`, `wellpressuretestrawmeasurement` |
| --- | --- | --- |
| POST `/{record_id}/sessions` | `post__ddms_v3_welllogs__record_id__sessions` | `post__ddms_v3_{c}__record_id__sessions` |
| GET `/{record_id}/sessions` | `get__ddms_v3_welllogs__record_id__sessions` | `get__ddms_v3_{c}__record_id__sessions` |
| GET `/{record_id}/sessions/{session_id}` | `get__ddms_v3_welllogs__record_id__sessions__session_id_` | `get__ddms_v3_{c}__record_id__sessions__session_id_` |
| PATCH `/{record_id}/sessions/{session_id}` | `patch__ddms_v3_welllogs__record_id__sessions__session_id_` | `patch__ddms_v3_{c}__record_id__sessions__session_id_` |
| POST `/{record_id}/data` | `write_record_data` | `post__ddms_v3_{c}__record_id__data` |
| GET `/{record_id}/data` | `get__ddms_v3_welllogs__record_id__data` | `get__ddms_v3_{c}__record_id__data` |
| POST `/{record_id}/sessions/{session_id}/data` | `post_chunk_data` | `post__ddms_v3_{c}__record_id__sessions__session_id__data` |
| GET `/{record_id}/versions/{version}/data` | `get__ddms_v3_welllogs__record_id__versions__version__data` | `get__ddms_v3_{c}__record_id__versions__version__data` |
| GET `/{record_id}/data/statistics` | `get__ddms_v3_welllogs__record_id__data_statistics` | none |
| GET `/{record_id}/versions/{version}/data/statistics` | `get__ddms_v3_welllogs__record_id__versions__version__data_statistics` | none |
| POST `/{record_id}/versions/{version}/data/statistics` | `post__ddms_v3_welllogs__record_id__versions__version__data_statistics` | none |

## 5. Identities and versions

### 5.1 Record ids

- `Record.id` is optional [C: #/components/schemas/Record]. The ids written come back in `recordIds` and, with their
  versions, in `recordIdVersions` [C: #/components/schemas/CreateUpdateRecordsResponse]. Inference: without an `id`,
  Storage assigns one, since the DDMS passes the records to Storage as they are (section 3.1).
- Contract path patterns, on the record routes of seven collections: `^[\w\-\.]+:<group>\-\-<Type>:[\w\-\.\:\%]+$`,
  with `<group>` = `master-data` or `work-product-component` [C]. The code applies the same family to all nine types
  (section 2) [98 app/model/osdu_record_id.py:36-44].
- The bulk, session and statistics paths have no pattern in the contract. The statistics routes validate the WellLog
  pattern in code [98 app/routers/bulk/statistics_routes.py:106-109, 163-166, 218-221].
- Contract example ids: `{{datapartitionid}}:work-product-component--WellLog:{{welllogId}}`
  [C: POST /ddms/v3/welllogs example]. The wellbore example ends with a colon,
  `{{datapartitionid}}:master-data--Wellbore:{{wellboreId}}:` [C: POST /ddms/v3/wellbores example].

Version suffix on `record_id` (source):

- The GET, DELETE and versions routes of every collection accept `<id>:<version>` and strip the suffix before calling
  Storage [98 app/model/osdu_record_id.py:8-32; 98 app/routers/ddms_v3/welllog_ddms_v3.py:59, 90, 110, 132;
  98 app/routers/ddms_v3/well_ddms_v3.py:48, 71, 87, 108; 98 app/routers/ddms_v3/generic_ddms_v3.py:57, 83, 97, 116].
- The WellLog and WellboreTrajectory versions routes read the record with the unstripped value first
  [98 app/routers/ddms_v3/welllog_ddms_v3.py:107-110; 98 app/routers/ddms_v3/wellbore_trajectory_ddms_v3.py:101-103].
  The bulk and session routes do not strip.
- Inference from the regex: an id with four or more `:`-separated parts whose last part is all digits is read as an
  id plus a version, and a trailing colon is dropped. Such ids cannot be addressed as they are on those routes.
- The route sends bare ids.

### 5.2 Versions

- `Record.version` is int64 [C]. Ids and versions come from Storage; the DDMS passes Storage's answers through
  (sections 3.1 and 3.2).
- New versions come from:
  - each record POST (a Storage create-or-update);
  - each whole-bulk write: "It creates a new version" [C: POST /ddms/v3/{c}/{record_id}/data];
  - each session commit: "a new version of record will be created" [C: PATCH /ddms/v3/{c}/{record_id}/sessions/{session_id}].
- Chunks do not create versions: "individual chunk doesn't generate new individual record version"
  [C: POST /ddms/v3/{c}/{record_id}/sessions].
- Old bulk stays readable: "Previous bulk versions are accessible via the get bulk data version API"
  [C: POST /ddms/v3/{c}/{record_id}/data], through `GET /ddms/v3/{c}/{record_id}/versions/{version}/data` [C].
- Inference: after a bulk step, the version to record is the one that step created, not the metadata write's. The
  current protocol reads the record back after the payload step to learn it
  [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:155-166].

### 5.3 BulkURI and DDMSDatasets

Contract text, on the POST of the four bulk collections only [C: POST /ddms/v3/{c}]:

> BulkURI consistency:
>
> - ExtensionProperties.wdms.bulkURI is managed by WBDDMS and must not be set when creating a new record.
> - When updating an existing record, bulkURI must match the previous version exactly.
>
> Requests that violate these rules are rejected with HTTP 400.

The full rule in code, where "previous version" is the latest stored version of the body's `id`
[98 app/routers/ddms_v3/ddms_v3_utils.py:49-116]:

| bulkURI in body | `id` in body | Latest stored version | Its bulkURI | Result |
| --- | --- | --- | --- | --- |
| no | no | n/a | n/a | accepted (create) |
| no | yes | none (Storage 404) | n/a | accepted (create) |
| yes, equal | yes | exists | yes | accepted (update) |
| no | yes | exists | no | accepted (update) |
| no | yes | exists | yes | 400 `Record[<i>] error : Bulk URI isn't matching with the previous version one` |
| yes | no | n/a | n/a | 400 `Record[<i>] error : no Bulk URI can be specified without record id` |
| yes | yes | none | n/a | 400 `Record[<i>] error : no Bulk URI can be specified, given record_id has no previous version` |
| yes, different | yes | exists | yes | 400 `Record[<i>] error : Bulk URI isn't matching with the previous version one` |
| yes | yes | exists | no | 400 `Record[<i>] error : no Bulk URI can be specified, given record_id has no bulkURI in its previous version` |

- A Storage error other than 404 on that read is passed through [98 app/routers/ddms_v3/ddms_v3_utils.py:86-99].
- Location and format: `data.ExtensionProperties.wdms.bulkURI`, `urn:wdms-1:uuid:<uuid>` (bulk storage version 1) or
  `urn:uuid:<uuid>` (version 0). An example in the code is `urn:wdms-1:uuid:38f0438e-71b8-4806-924b-9753796a77c1`
  [98 app/routers/bulk/bulk_routes_dependencies.py:13, 37-53; 98 app/bulk_persistence/bulk_uri.py:54-69, 93-128;
  98 app/bulk_persistence/bulk_storage_version.py:14-20; 98 app/routers/record_utils.py:71-76].
- A value with another prefix, or with an invalid UUID, makes decoding raise `ValueError`. Reads turn that into 422
  `Record contains an invalid bulk URI` [98 app/routers/bulk/bulk_routes.py:224-228]. The record POST does not catch
  it, so it answers 500 through the generic handler (inference) [98 app/errors/unhandled_error.py:20-25].
- The record-only collections apply no bulkURI rule (section 3.1).

Writer consequence (inference):

- A metadata update of a record that already has bulk must carry `data.ExtensionProperties.wdms.bulkURI` exactly as
  the latest version holds it. The route reads the latest version first and copies the value.
- `data.DDMSDatasets`, which the service appends to and never checks, is carried the same way to keep its entries.
- The next bulk write or commit replaces the bulkURI.
- This repository's existing mechanism is `ProtocolOptions.PreserveDataKeys`, empty by default, which copies named
  top-level `data` keys from the current record before a write [OD osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs:547-551;
  OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs:537-566]. It copies whole keys, so preserving
  `ExtensionProperties` would also replace whatever the mapping renders under that key.

## 6. Reads, verification and deletes

### 6.1 Record reads

| Route | Returns | Contract |
| --- | --- | --- |
| `GET /ddms/v3/<collection>/{record_id}` | `Record`, latest version. 404 `<Type> not found` (collection wording), "Record not found" on the generic two. | [C: GET /ddms/v3/welllogs/{record_id}, get_welllog_osdu; siblings] |
| `GET /ddms/v3/<collection>/{record_id}/versions` | `RecordVersions {recordId, versions[int64]}`, both nullable | [C: #/components/schemas/RecordVersions] |
| `GET /ddms/v3/<collection>/{record_id}/versions/{version}` | `Record` at `version` (path, int64) | [C: GET /ddms/v3/welllogs/{record_id}/versions/{version}, get_osdu_welllog_version; siblings] |

Source:

- The GET routes validate the stored record against its kind's schema with undeclared properties refused, so a stored
  record that does not validate cannot be read through the DDMS (422). Most also check the kind (400 on mismatch)
  [98 app/routers/ddms_v3/welllog_ddms_v3.py:54-66, 128-138; 98 app/routers/ddms_v3/generic_ddms_v3.py:54-64, 113-123].
- The versions route reads the latest record, checks its kind, and returns Storage's version list
  [98 app/routers/ddms_v3/welllog_ddms_v3.py:104-114; 98 app/routers/ddms_v3/generic_ddms_v3.py:95-101].

### 6.2 Bulk reads and describe

| Route | Returns | Contract |
| --- | --- | --- |
| `GET /ddms/v3/{c}/{record_id}/data` | bulk of the latest version | [C: GET /ddms/v3/{c}/{record_id}/data] |
| `GET /ddms/v3/{c}/{record_id}/versions/{version}/data` | bulk of that record version | [C: GET /ddms/v3/{c}/{record_id}/versions/{version}/data] |

A bulk read fetches the record from Storage under the caller's token before it reads the bulk the record names, so
whoever may not read the record may not read its bulk data either: the record's ACL governs both [98 at `5413d816`
(2026-11-04) app/routers/bulk/bulk_routes.py:216-237].

Contract:

- 200 `application/json`, empty schema, named examples `json-data-split-example`, `json-data-columns-example` and
  `data-describe` (`{"columns":["MD","GR","NEU","DEN"],"numberOfRows":5}`).
- 200 `application/x-parquet`, no schema, example `parquet-example`.
- Documented errors: 401, 403, 404, 422, 500.
- "The requested columns must not exceed 3000." On 400 "Too many columns requested", 400 "Too many values requested",
  or 413 "the resource requested exceeds the limit" (when the worker is enabled), read the curve names and row count
  with `describe`, then read in batches, each fetching "as many as columns it is possible until upper limits are
  reached (> 10 millions values or > 3000 columns)" [C: description].

Source [98 app/routers/bulk/bulk_routes.py:199-272; 98 app/routers/record_utils.py:60-84;
98 app/bulk_persistence/bulk_io_wdms_worker.py:98-131; 98 app/bulk_persistence/errors.py:36-48]:

- The route reads only `data.ExtensionProperties.wdms` of the record, through a Storage attribute filter, and checks
  the kind.
- An unparsable bulkURI is 422 `Record contains an invalid bulk URI`. A record without one is 404
  `bulk for record <id> not found`.
- It forwards `limit`, `offset` (when non-zero), `curves`, `filter`, `orient` and `describe` to the worker with the
  negotiated `accept`, and returns the worker's body as it is, labelled with the negotiated type.
- The describe body is therefore the worker's. The service defines a `DataframeDescribe {numberOfRows, columns}`
  model that no route uses [98 app/bulk_persistence/model_chunking.py:160-162].

### 6.3 Statistics (welllogs only)

| Route | Returns | Contract |
| --- | --- | --- |
| `GET /ddms/v3/welllogs/{record_id}/data/statistics` | `BulkDataStatisticsResponse` for the latest version; `curves` query ("All curves if empty") | [C: get__ddms_v3_welllogs__record_id__data_statistics] |
| `GET /ddms/v3/welllogs/{record_id}/versions/{version}/data/statistics` | the same for a version; `version` is typed `anyOf(int64, null)` although it is a required path parameter | [C: get__ddms_v3_welllogs__record_id__versions__version__data_statistics] |
| `POST /ddms/v3/welllogs/{record_id}/versions/{version}/data/statistics` | 200 "Statistics computation started" (empty schema); 404 "Statistics or record not found"; 409 "Statistics computation already running or complete" | [C: post__ddms_v3_welllogs__record_id__versions__version__data_statistics] |

- `BulkDataStatisticsResponse`, all fields required: `computationStartDatetime` (date-time), `recordId`,
  `recordVersion` (int64), `computationStatus` (`BulkStatisticsStatus`: `error`, `started`, `running`, `complete`),
  and `data`, a map from curve name to `CurveStatistics {mean, std, min, 10%, 50%, 90%, max, totalCount,
  nonAbsentValuesCount}`, all strings and all required [C: #/components/schemas/BulkDataStatisticsResponse,
  CurveStatistics, BulkStatisticsStatus].
- Supported data types: int, float, date. "No unit conversion is supported. Statistics will be returned using the same
  units as recorded in Curves[].CurveUnit" [C: descriptions].
- 404 examples on the GETs: `{"detail":"Record not found"}`, and `{"errorType": ..., "message": ...}` with
  `DATA_NOT_FOUND`, `CURVES_NOT_FOUND` or `COMPUTATION_NOT_COMPLETE` [C: responses.404].
- Source: the POST description says "at its last version", but the route reads the record at the path `version`. A
  record without a valid bulkURI is 422 `Record contains an invalid bulk URI`, not the documented 404. The worker's
  status and body are returned as they are [98 app/routers/bulk/statistics_routes.py:204-239;
  98 app/bulk_persistence/bulk_io_wdms_worker.py:168-219].

### 6.4 Verification recipe

1. Version: from call 1's `recordIdVersions`, from call 5's `version`, or, after a whole-bulk write, from a read-back
   with call 7, which is what the contract supports [C]. The whole-bulk response carries it in code only (section
   3.2).
2. Content: `GET /ddms/v3/{c}/{record_id}/data?describe=true` with `Accept: application/json`, then compare
   `numberOfRows` and `columns` with what was sent [C: example `data-describe`].
3. Record: call 7 shows the version, and after a bulk step `data.ExtensionProperties.wdms.bulkURI` is set (source,
   section 3.2).
4. Session: a successful commit answers `state: committed` (source, section 3.3).
5. WellLog only: statistics as an extra content check.

The current protocol reads the record back after every payload step, describes the bulk after a session, and settles
an unclear commit with call 6 [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:141-166, 342-400, 473-505].

### 6.5 Delete (call 10)

- `DELETE /ddms/v3/<collection>/{record_id}` answers 204 "Record deleted successfully" or 404, and documents 401,
  403, 422 and 500 [C: every collection's DELETE]. Its summary on all nine is "... The API performs a logical deletion
  of the given record. No recursive delete for OSDU kinds" [C].
- `purge`: query, boolean, default false, no description, only on `welllogs`, `wellboretrajectories`, `ppfgdataset`
  and `wellpressuretestrawmeasurement` [C: DELETE /ddms/v3/welllogs/{record_id}, del_osdu_welllog;
  DELETE /ddms/v3/wellboretrajectories/{record_id}, del_osdu_wellboretrajectory;
  DELETE /ddms/v3/ppfgdataset/{record_id}, delete_osdu_record;
  DELETE /ddms/v3/wellpressuretestrawmeasurement/{record_id}, delete__ddms_v3_wellpressuretestrawmeasurement__record_id_].
- The record-only collections delete logically only, calling Storage's delete directly
  [98 app/routers/ddms_v3/wellbore_ddms_v3.py:70-75].
- There is no operation to delete one record version, one bulk version or a session [C].

Source [98 app/routers/delete/delete_bulk_data.py:30-94]:

- `purge=false`: Storage `delete_record`, a logical delete.
- `purge=true`:
  1. Lists every version and reads each one to collect its bulkURI; a version Storage answers 404 for is skipped.
  2. Calls Storage `purge_record`.
  3. Lists the tenant's blobs under the hashed record id and schedules the deletion of each blob whose name contains a
     collected bulk id with `asyncio.ensure_future`, without awaiting it. A failed deletion is only logged, so a 204
     does not show that the bulk blobs are gone.
- A record Storage does not hold gives Storage's 404, passed through (section 7.3).

Project rule: `purge=true` is a Storage purge, so live cleanup never uses it. The reversible scope is DELETE without
`purge`. The current protocol sends `?purge=true` for its `Everything` removal scope and a Storage version purge for
`History` [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:244-288].

### 6.6 Probe, about and version

| Route | Contract | Auth | Returns |
| --- | --- | --- | --- |
| `GET /about` | [C: GET /about, get__about] | none: no `security`, no partition header | `AboutResponse {service, version, buildNumber, cloudEnvironment, release}`, all nullable; 500 |
| `GET /version` | [C: GET /version, get__version] | bearer; no partition header | `VersionDetailsResponse {service, version, buildNumber, release, details}` with `details` a map of strings; 401, 403, 500 |
| `GET /healthz` | not in the contract | none | `{"status":"healthy"}` [98 app/routers/probes.py:24-26] |
| `GET /readiness` | not in the contract | none | `{"status":"healthy"}` [98 app/routers/probes.py:29-31] |
| `GET /` | not in the contract (load balancer liveness) | none | `{"status":"healthy"}` [98 app/routers/probes.py:34-36] |

- All of these are relative to the service root.
- `details` in `/version` holds the build details, `environment_name`, `cloud_provider`, `de_client_config_timeout`,
  `enable_read_fast_track`, `bulk_backend` (`Bulk worker service`), and `enable_wdms_bulk_worker` when the worker host
  is set [98 app/routers/about.py:53-83; 98 app/bulk_persistence/bulk_io_wdms_worker.py:29-30].
- The current protocol probes `GET /about` [OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:30].

## 7. Limits and errors

### 7.1 Size limits

Contract, in the write descriptions of all four collections, including the literal `welllogs` path
[C: POST /ddms/v3/{c}/{record_id}/data]:

- "Double check whether bulk data is big enough to be sent with chunking APIs: meaning > 10 millions values or > 3000
  columns"; "If no, use instead POST /ddms/v3/welllogs/MY_RECORD_ID/data API".
- "Ensure all curve's values are in the same chunk to be sent".
- "Each chunk should contain as many as columns it is possible until upper limits are reached
  (> 10 millions values or > 3000 columns)".
- Read limits: section 6.2.

Where the limits are enforced:

- The only `BulkIO` binding is `BulkIOWdmsWorker` [98 app/injector/main_injector.py:67-71, 181-185]. It forwards every
  bulk read and write to the worker (`SERVICE_HOST_WDMS_WORKER`) [98 app/conf.py:128-130] and never calls the
  dataframe validator it receives (section 4.2).
- Limit enforcement therefore belongs to the worker, which this brief does not cover. The contract figures are the
  authority.

Source constants, indicative only. Only `READ_MAX_COLUMNS_COUNT` is used by a route, in the read description text
[98 app/bulk_persistence/constants.py:15-22; 98 app/routers/bulk/bulk_routes.py:189-196]:

| Constant | Value |
| --- | --- |
| `READ_MAX_COLUMNS_COUNT` | 3000 |
| `READ_MAX_TOTAL_VALUES_COUNT_FILTERED` | 10,000,000 ("~100MB in parquet") |
| `READ_MAX_TOTAL_VALUES_COUNT_UNFILTERED` | 100,000,000 ("~1GB in parquet") |
| `WRITE_MAX_TOTAL_VALUES_COUNT` | 10,000,000 ("restrict chunk to ~100MB") |
| `WRITE_MAX_COLUMNS_COUNT` | 3000 |
| `WRITE_MAX_CONFLICTED_COLUMNS_COUNT` | 10,000 per session |

- The service reads each bulk request body fully into memory before forwarding it
  [98 app/bulk_persistence/bulk_io_wdms_worker.py:133-139]. Inference: chunked transfer encoding does not reduce the
  memory a large body takes in this service, and a deployment's ingress bounds the body size as well.
- Session time to live: section 3.3.

### 7.2 Status codes by call

| Call | Contract | Causes in code |
| --- | --- | --- |
| 1 Record POST | 200, 400, 401, 403, 422, 500 | 400 BulkURI or record consistency; 422 schema validation, kind check or request validation; Storage and Schema statuses passed through; 504 on a Storage client timeout; 500 for a malformed bulkURI (inference) |
| 2 Whole bulk | 200, 401, 403, 404, 422, 500 | 400 `Content-Type invalid` or bulk consistency; 400 kind mismatch; worker statuses passed through; 500 for a PPFGDataset or WellPressureTestRawMeasurement curve without `CurveID` (inference) |
| 3 Open session | 200, 401, 403, 404, 422, 500 | 400 kind mismatch; Storage status for a missing record or `fromVersion` |
| 4 Chunk | 200, 400, 401, 403, 404, 422, 500 | 400 session not `open` or `Content-Type invalid`; 404 unknown session; worker statuses passed through |
| 5 Commit or abandon | 200, 401, 403, 404, 409, 412, 422, 500 | 400 bulk consistency; 409 and 412 as in section 3.3; 422 unparsable bulkURI in `update` mode; worker statuses passed through |
| 7, 8 Record reads | 200, 401, 403, 404, 422, 500 | 400 kind mismatch; 422 stored record fails validation; 422 `Invalid recordID` on the generic two |
| 9 Bulk read | 200, 401, 403, 404, 422, 500 | 400 `No supported type found`; 404 no bulk; 422 invalid bulkURI; 400 and 413 from the worker (section 6.2) |
| 10 Delete | 204, 401, 403, 404, 422, 500 | Storage status passed through |

### 7.3 Error bodies

Contract: 422 is `HTTPValidationError {errors: [ValidationError {loc[], msg, type}]}`, with `loc` items string or
int64. No other error body is described [C: #/components/schemas/HTTPValidationError, ValidationError].

Code:

| Cause | Status | Body |
| --- | --- | --- |
| Request validation (FastAPI, pydantic) | 422 | `{"errors": [<pydantic error objects>]}` [98 app/errors/validation_error.py:30-38; 98 app/errors/exception_handlers.py:50-51] |
| Record schema validation | 422 | `{"errors": "Value of <path> is invalid: <message>"}`, a string [98 app/errors/validation_error.py:41-52] |
| `HTTPException` (kind, record id, bulkURI, consistency, session, content type) | as raised | `{"detail": "<text>"}` [98 app/errors/exception_handlers.py:37-45, 58] |
| Storage error response | Storage's status | `{"origin": "osdu-data-ecosystem-storage", "errors": [<Storage body>]}` [98 app/errors/client_error.py:83-103] |
| Storage client timeout | 504 | `{"origin": "osdu-data-ecosystem-storage", "errors": ["Storage client timeout after <n>s: ..."]}`, with `<n>` from `DE_CLIENT_CFG_TIMEOUT` (default 10) [98 app/errors/client_error.py:91-95; 98 app/conf.py:143-148] |
| Other Storage client failure | 500 | same shape [98 app/errors/client_error.py:96-101] |
| Schema service error | Schema's status | `{"origin": "osdu-data-ecosystem-schema", "errors": [...]}` [98 app/errors/client_error.py:115-130] |
| Statistics error | 404 | `{"errorType": ..., "message": ...}` [98 app/routers/bulk/statistics_routes.py:127-145, 188-200] |
| Worker error | worker's status | `{"detail": "<worker body text>"}` [98 app/bulk_persistence/bulk_io_wdms_worker.py:35-40; 98 app/bulk_persistence/errors.py:22-34] |
| Anything else | 500 | `{"error": ["<message>"]}` [98 app/errors/unhandled_error.py:20-25] |

Retries inside the service (source): the Storage and Schema clients retry on `RemoteProtocolError`,
`TimeoutException` and `ResponseHandlingException` with exponential backoff, for at most `DE_CLIENT_BACKOFF_MAX_RETRIES`
tries (default 4) and `DE_CLIENT_BACKOFF_MAX_WAIT` seconds between tries (default 5), before the 504 or 500 above
[98 `app/clients/__init__.py`:60-83; 98 app/clients/clients_middleware.py:47-49; 98 app/clients/backoff_policy.py:10-28;
98 app/conf.py:164-179]. Inference: a Storage write that timed out after Storage had acted can be sent again by the
service, and so land as an extra record version.

Inference for the route's retries: 409 and 412 on a commit are settled by reading the session; 504 and Storage 5xx are
transient; 400 and 422 are faults of the record and are not retried as they are.

## 8. What differs

### 8.1 Contract versus code at the pinned commit

1. `data-partition-id` is optional in the contract and in code, but every Storage and Schema call depends on it
   (section 1.4).
2. `Content-Type` must match exactly; `application/parquet`, `json` and `parquet` are also accepted; parameters are
   refused (section 1.4).
3. `Accept`: any `*/*` selects Parquet, and Parquet wins when both types are listed. Responses carry the negotiated
   type even for `describe` (section 1.4).
4. 422 bodies: besides the documented `{"errors": [...]}`, the code returns `{"errors": "<string>"}` for schema
   validation and `{"detail": "<text>"}` for kind, record id and bulkURI failures (section 7.3).
5. Records are validated against the kind's schema with undeclared properties refused; the contract's `Record.data`
   is open (section 3.1).
6. The kind check is skipped for `ppfgdataset` (misspelled map key) and `welllogacquisition` (no map entry)
   (section 2).
7. `ppfgdataset` and `wellpressuretestrawmeasurement` enforce an id pattern the contract does not state; the
   statistics routes enforce the WellLog pattern the contract does not state (sections 2 and 5.1).
8. The whole-bulk write documents an empty 200 schema; the code returns the Storage create-or-update response
   (section 3.2).
9. Session duration: the contract caps it at 24 hours and says an expired session is dropped; the code applies
   `timeToLive` without a cap, and no code path reads the expiry (section 3.3).
10. `extendedLoadCompleted`: the contract names WellLog and WellboreTrajectory; the code applies it to any record that
    has `data.IsExtendedLoad`. The contract's own example sends a boolean where the schema says string, and the
    pinned code converts values to strings (section 3.3).
11. The `CreateDataSessionRequest` schema description asks, as an open note, whether a `fromVersion` should force
    `update` mode or be refused with `overwrite` [C: #/components/schemas/CreateDataSessionRequest]. The code does
    neither [98 app/routers/sessions.py:131-171].
12. The chunk operation documents 400 "Record not found"; the route does not read the record, and its own 400 is the
    session state check (section 3.3).
13. A commit writes back the record as it was at `fromVersion`; the contract only says a new version is created
    (sections 3 and 3.3).
14. Statistics POST: "at its last version" in the contract, the path version in code; a missing or invalid bulkURI is
    422, not the documented 404 (section 6.3).
15. `purge` has no description in the contract. The code purges every version in Storage and deletes the bulk blobs
    without waiting (section 6.5).
16. The contract's examples do not all validate against its own schemas: `Session.id` "xx1234" is not a uuid; the
    `BulkDataStatisticsResponse.recordVersion` example is a 37-digit string for an int64; the marker set body example
    is an object for an array body; the session-create example sends a boolean `meta` value [C]. Request-body named
    examples sit in `schema.examples` as a map, where JSON Schema expects an array
    [C: POST /ddms/v3/{c}/{record_id}/sessions; PATCH /ddms/v3/{c}/{record_id}/sessions/{session_id}]. A contract test
    harness that validates examples meets these.

### 8.2 Deployments on release 0.29

`[R029]` has the same 84 operations on the same URLs as the pinned contract. Its component schema set is the same:
none added or removed, nine differing only by `format: int64`, and identical security schemes [R029; C]. The
differences that matter to a route serving both:

| Area | Release 0.29 | Pinned contract and code | What the route does |
| --- | --- | --- | --- |
| Path parameter names | `{welllogid}`, `{wellboretrajectoryid}`, `{wellboreid}`, `{wellid}`, `{wellboremarkersetid}`, `{wellboreintervalsetsid}`, `{welllogacquisitionid}`, `{osdu_record_id}` on the 36 GET, DELETE, versions and versions/{version} operations [R029] | `{record_id}` everywhere (commit `f597422eb736a5e77d84a1c15537946345545d38`) | Nothing on the wire. Generated clients change, and the four generated operationIds of the `wellpressuretestrawmeasurement` record routes changed from `..._osdu_record_id_...` to `..._record_id_...`. |
| Integer formats | 43 bare `integer` nodes | `format: int64` on all 43 (12 in component schemas; 31 in parameters: `version`, `offset`, `limit`), with the named examples of 18 `curves` and `filter` parameters moved to parameter level, both done by `_patch_openapi_schema` (commit `3d10517d3496d5e54f8114dcad60a56b516fb50f`) [98 app/wdms_app.py:102-149] | Nothing; generated client types only. |
| `servers` | absent | `/api/os-wellbore-ddms` | Nothing; the root comes from configuration. |
| BulkURI rules | enforced but undocumented; the `ppfgdataset` and `wellpressuretestrawmeasurement` POSTs have no description | documented (commit `f594e3e1b913e7ac9e7db4c23e282d43f64e0cfd`) | The same rule on both: `ddms_v3_utils.py` is identical at the two commits [98-r0.29 app/routers/ddms_v3/ddms_v3_utils.py]. |
| PATCH session 409 and 412 | raised but undocumented | documented (commit `3d10517d3496d5e54f8114dcad60a56b516fb50f`) | The same handling on both: `sessions_storage.py` is identical at the two commits [98-r0.29 app/bulk_persistence/sessions_storage.py]. |
| Session `meta` values | The `release/0.29` head converts them to strings [98-r0.29 app/model/model_utils.py:21-31]: it merges !999 (commit `85a87f4117a556b37b024bbd5715ccf46e14030c`), the cherry-pick of master commit `dda38184fde0c8acb7915e3955a699331f70f471`. Tag `v0.29.2` types `meta` as `Dict[str, str]` with no conversion [98-v0.29.2 app/routers/sessions.py:52-55; 98-v0.29.2 app/bulk_persistence/sessions_storage.py:68-70]. | converted [98 app/model/model_utils.py:21-31] | Sends `"extendedLoadCompleted": "true"` as a string, which every version accepts; `v0.29.2` applies the same lower-cased comparison at commit [98-v0.29.2 app/routers/bulk/bulk_routes.py:326-329]. Inference: tagged 0.29 builds refuse the boolean in the contract's own example [98-v0.29.2 app/routers/common_parameters.py:199-206]; the commit that adds the conversion adds tests for boolean and integer values. |
| Version suffix on record ids | GET by id strips it in every collection. DELETE and versions/{version} strip it only for `welllogs`, `wellboretrajectories` and `wellboremarkersets`, and every versions list reads the record with the raw value [98-r0.29 app/routers/ddms_v3/well_ddms_v3.py:48, 72, 86, 107-108; 98-r0.29 app/routers/ddms_v3/wellbore_ddms_v3.py:48, 72, 88, 112-113; 98-r0.29 app/routers/ddms_v3/wellbore_interval_set_ddms_v3.py:50, 74, 89, 110-111; 98-r0.29 app/routers/ddms_v3/markerset_ddms_v3.py:51, 77, 96, 119; 98-r0.29 app/routers/ddms_v3/welllog_acquisition_v3.py:51, 73-75, 87-92, 105-111; 98-r0.29 app/routers/ddms_v3/generic_ddms_v3.py:56, 80-83, 94-99, 111-117; 98-r0.29 app/routers/ddms_v3/welllog_ddms_v3.py:59, 90, 107, 132; 98-r0.29 app/routers/ddms_v3/wellbore_trajectory_ddms_v3.py:55, 84, 101, 126] | Stripped on every GET, DELETE and versions route, except that the `welllogs` and `wellboretrajectories` versions lists still read the record with the raw value first (section 5.1). Commit `f1f48c11921fb62f0fbf823a4755ab9ba26ef87d` made the change in the well, wellbore, marker set, interval set, WellLogAcquisition and generic routers. | Sends bare ids. |
| WellLog curve without `CurveID` at bulk write or commit | no guard: `KeyError`, answered 500 (inference) [98-r0.29 app/consistency/welllog_consistency.py:148-150] | 400 (commit `8659e364b59cb87427c3545193dd7f18c882a8a9`) | Requires `CurveID` on every curve before sending, and treats that 500 as a fault of the record on 0.29. |
| Storage client timeout | 500 [98-r0.29 app/errors/client_error.py:88-90] | 504 (commit `12ab1a2a53cae8c2cceaca658b927f0bef10dec4`) | Treats both as transient. |
| FastAPI request validation | no handler for `RequestValidationError`, so FastAPI's default handler answers (inference) [98-r0.29 app/errors/exception_handlers.py:47-56] | 422 `{"errors": [...]}` (commit `3d10517d3496d5e54f8114dcad60a56b516fb50f`) | Reads both `errors` and `detail` from a 422. |
| Wording | "Not found" on 18 responses; role text missing its closing quote on 23 operations; "WDDMS" on the 8 data reads | "Not Found"; closing quote added; "WBDDMS" | Nothing. |

## 9. What is still open

- The bulk worker is a separate service. It decides parsing, index rules, size limits, how `update` and `overwrite`
  sessions merge chunks, and what a `describe` read returns when Parquet is negotiated. None of it is in the pinned
  contract or in project 98, and the project and commit that hold it are not established here. One of its rules is
  established by observation (section 3.3, the reference curve of a session), the rest are not.
- Where session expiry is enforced is not visible in project 98 (section 3.3).
- Storage calls go through the generated `odes_storage` client (package `osdu-data-ecosystem-storage==0.29.0`),
  wrapped by the service's own middleware [98 pyproject.toml:49; 98 `app/clients/__init__.py`:60-70]. The client
  itself was not read for this brief, so what it sends (for example `skipdupes`) is not established.
- Whether a logically deleted record can still be purged through `purge=true` is not established.
- Merge requests open on 2026-09-17; none is part of the pinned commit:
  - !983 (to `release/0.30`): `x-collaboration` namespace support on the CRUD endpoints, behind a feature toggle;
    requests without the header keep the current behaviour.
  - !996 (to `release/0.30`): set `IsExtendedLoad` to false without sessions.
  - !1016 (to `master`): forward merge of !1009, merged into `release/0.30` on 2026-08-21, which removes
    `fillna("NaN")` from the JSON serializer. The pinned commit still has that line in
    `DataframeSerializerSync.to_json`, which no route of the pinned commit calls
    [98 app/bulk_persistence/dataframe_serializer.py:59-71]. The `release/0.30` code therefore differs from master
    there, although their contracts are identical.
  - !1020 (to `master`): answer 422 instead of 500 when a validation error involves a non-finite value, without
    echoing the value.
  - !995 (to `master`): add `docs/api/community/v1/openapi.yaml`.
  - !984 and !1002 concern CI and telemetry.

## 10. Conclusion for the route types

One generic "record, then tabular bulk (whole or session)" route type, parameterised by collection, serves the four
bulk collections. For those four, the contract has the same shapes for:

- the record POST (`array<Record>` in, `CreateUpdateRecordsResponse` out, the same BulkURI rules);
- the whole-bulk POST (same content types, no query parameters);
- the session create, chunk, and commit or abandon calls (same bodies, responses, states, 409 and 412);
- the read-back routes and their query parameters;
- DELETE with `purge` (sections 3 to 6).

The code mounts one shared bulk router and one shared session router under each prefix. The current `ddms`
flow (record POST; bulk POST or an `overwrite` session; record read-back; `describe` after a session; DELETE; `/about`
probe) maps onto the other three collections by changing the collection segment
[OD osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs:23-30].

What the route type takes per collection:

1. The collection segment (`welllogs`, `wellboretrajectories`, `ppfgdataset`, `wellpressuretestrawmeasurement`).
2. The expected kind entity type and id pattern, checked before sending. The service skips the kind check for
   `ppfgdataset`, and the contract states no id pattern for the two generic collections.
3. Where the column names come from: `data.Curves[].CurveID` for WellLog, PPFGDataset and
   WellPressureTestRawMeasurement; `data.AvailableTrajectoryStationProperties[].Name` for WellboreTrajectory.
4. The column-width rule: `NumberOfColumns` (default 1) for WellLog and WellPressureTestRawMeasurement; not checked for
   PPFGDataset and WellboreTrajectory.
5. The record rules a write must satisfy:
   - WellLog: unique `CurveID`; `ReferenceCurveID` among them; when the reference curve is Measured Depth, a
     monotonic reference whose first and last values match `SamplingStart` and `SamplingStop`.
   - WellboreTrajectory: unique station names; a monotonic MD station whose first and last values match
     `TopDepthMeasuredDepth` and `BaseDepthMeasuredDepth`.
   - PPFGDataset: `ContextTypeID` and `ReferenceWellTrajectoryID` present; unique `CurveID`;
     `PrimaryReferenceCurveID` among them; a `CurveID` on every curve.
   - WellPressureTestRawMeasurement: unique `CurveID`; a `CurveID` on every curve.
6. Verification: `describe` and versioned reads exist for all four; statistics only for `welllogs`.
7. `extendedLoadCompleted`: documented for WellLog and WellboreTrajectory, applied in code to any record with
   `data.IsExtendedLoad`. Sent as the string `"true"`.
8. operationIds, only if a generated client is used: explicit (`write_record_data`, `post_chunk_data`) for `welllogs`
   only, generated otherwise.

What all four share:

- the service root, auth and headers;
- the content types (`application/json` "split" or `application/x-parquet`, sent bare);
- the whole-versus-session threshold: a session when the bulk exceeds 10 million values or 3000 columns, and at most
  that per request or chunk;
- the session body, states, timeouts and error codes;
- the response shapes;
- the BulkURI carry-over on metadata updates (section 5.3);
- opening a session only after the last metadata write;
- the delete semantics, and the rule that live cleanup never sends `purge=true`.

The DDMS record route serves the record-only collections (`wellbores`, `wells`, `wellboremarkersets`,
`wellboreintervalsets`, `welllogacquisition`) with the record half of the same calls: POST `array<Record>`, GET,
versions, and DELETE without `purge`. It has no bulk, no BulkURI rule and no consistency checks, but the same schema
validation with undeclared properties refused, and a kind check everywhere except `welllogacquisition`.
