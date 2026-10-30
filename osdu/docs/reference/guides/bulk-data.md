---
id: delivery-guide-bulk-data
title: "Delivering well logs with their curves: the WellLog record and its bulk data in the Wellbore DDMS"
type: guide
summary: "Deliver a well log header as a WellLog record and its curve values as bulk data to the Wellbore DDMS: tables, parquet chunks, mapping, flow, checks and holds."
keywords:
  - well log
  - curves
  - bulk data
  - wellbore ddms
  - welllog record
  - parquet chunks
  - ddms route
  - source.payloads
  - ddmsroot
  - bulk session
  - curveid
  - referencecurveid
related:
  - delivery-flow-ddms
  - delivery-flow-routes
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-cli-check
  - delivery-cli-preview
  - delivery-guide-multi-kind-source
  - flow-ing
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/PayloadParts.cs
  - osdu/src/SqlFlow.Delivery/Model/DdmsCatalog.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Model/WellboreDdmsBulkLimits.cs
  - osdu/src/SqlFlow.Delivery/Model/WellboreDdmsSessionChunks.cs
  - osdu/src/SqlFlow.Delivery/Storage/PayloadFiles.cs
  - osdu/src/SqlFlow.Delivery/Engine/DdmsRouting.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/WellboreDdmsV3Shape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsRules.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/samples/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
  - osdu/specs/wellbore-ddms/openapi.json
---

# Delivering well logs with their curves: the WellLog record and its bulk data in the Wellbore DDMS

A well log has two halves in OSDU: the `work-product-component--WellLog` record, which describes the log and lists its
curves, and the curve values themselves, which the Wellbore DDMS keeps as bulk data beside the record. This guide takes a
well database (`welldb`) that exports its logs as files, and delivers each log as a WellLog record through the Wellbore
DDMS, with its curve values sent as bulk data after the record. The route that does this is `ddms`
([Routes](../flow/routes.md#ddms)); what the Wellbore DDMS checks and how bulk data travels is on
[DDMSs](../flow/ddms.md#the-wellbore-ddms-wellboreddmsv3).

## 1. What the source gives

The export has three parts, and SQLFlow's own flows land two of them into keyed silver tables, as for any other source
([ingestion flows](../../../../sqlflow/docs/reference/flow/ing.md)):

| Part | Flows | Where it ends up |
| --- | --- | --- |
| One header row per log | `welldb-welllog-01-header-pre`, `welldb-welllog-02-header-ing` | `OsduData.silver.WellLog`, key `log_id` |
| One row per curve of a log | `welldb-welllog-01-curves-pre`, `welldb-welllog-02-curves-ing` | `OsduData.silver.WellLogCurve`, keys `log_id`, `curve_id` |
| The curve values | none: they stay where the export writes them | parquet files in a folder per log, under `data/curves/` |

The header row names its log's folder and says what is in it: `curve_folder` (the folder, relative to the payload root),
`curves_hash` (a hash of the folder's files, which changes when the curve values change) and `chunk_count` (how many files
there are). It also carries the log's own columns: `wellbore_id`, `log_name`, `log_source`, `top_depth`, `base_depth`,
`index_curve` (the curve the others are indexed by, such as `DEPT`), `sampling_domain` and `update_date`. The curve rows carry `curve_id`,
`curve_mnemonic` and `curve_unit`, ordered by `curve_id`. The index curve is a curve of the log too, with a row of its own.

The mapping writes `top_depth` and `base_depth` into number properties (`TopMeasuredDepth`, `BottomMeasuredDepth`), so
the header pre flow declares their type rather than leaving them to inference. SQLFlow's type inference reads a column whose values are all 0 or 1 (or `true`, `false`, `yes`, `no`, `y`, `n`) as
`bit`, which renders `false` where the template takes a number, and every record is then held with
`value 'false' is not a valid number (a boolean is not a number)`. A log
export whose depths are all 0 is enough. The header pre flow's `transform` block:

```yaml
transform:
  inferTypes: true
  columns:
    - { name: log_id, type: varchar(50) }
    - { name: top_depth, expr: "NULLIF(@ColName, '')", type: "decimal(38,18)" }
    - { name: base_depth, expr: "NULLIF(@ColName, '')", type: "decimal(38,18)" }
    - { name: update_date, type: datetime2 }
```

`NULLIF(@ColName, '')` turns an empty field into no value rather than a conversion error.

The curve ingestion flow is an ordinary `ing` flow. Its target gets an identity column, which the delivery flow pages by:

```yaml
flowType: ing
name: welldb-welllog-02-curves-ing
batch: welldb

connections:
  osduData: ${env:OSDU_DATA_DB}

source:
  server: osduData
  object: OsduData.pre.v_WellLogCurve

target:
  server: osduData
  object: OsduData.silver.WellLogCurve
  identityColumn: RecId

load:
  keyColumns: [log_id, curve_id]
```

## 2. What the curve files must be

The Wellbore DDMS reads each file as a dataframe and matches its columns to the record's curves, so the files follow its
rules. OSDU Delivery checks every one of them from the files' parquet footers before it sends anything, and holds the
record naming the rule when one is broken, so nothing half-delivered is left behind:

- **Columns are curves.** Every column is named by a `CurveID` the record lists under `data.Curves`. An array curve's
  columns are labelled `NAME[0]`, `NAME[1]` and so on, and the curve's `NumberOfColumns` says how many there are (1 when
  not given).
- **One file, or several.** One file goes in one request, which replaces the log's whole bulk. Several files go through a
  bulk session, in file name order (`chunk_000.parquet`, `chunk_001.parquet`), and are committed as one new version.
- **Several files: carry the index curve as a column of each, and keep the row labels apart.** A session takes each file's
  index curve column as its reference, and refuses the commit when the reference curve is only the dataframe's row index.
  It aggregates the files by row label, so files that split a log's rows need labels that continue from one file to the
  next; files that split a log's curves carry the same labels for the same rows.
- **Within the ceilings.** At most 3,000 columns (500 on targets before OSDU M26, declared with `maxChunkColumns: 500`) and
  10,000,000 values (rows times columns) per file, and no file above the estate's request body limit when the flow
  declares one (`reliability.maxRequestBodyBytes`). A larger log is cut into more files.

## 3. The mapping

The well database's well log mapping, `mappings/WellLog@1.0.0.yaml`, fills the WellLog 1.4.0 template from the header
row, and one `Curves` item per curve row:

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

What matters for the bulk data:

- **`CurveID` is what the columns are matched by.** It has to be unique within the log, and it has to be the column name
  in the files (here, the curve row's `curve_id`).
- **`ReferenceCurveID` names one of the curves.** The DDMS refuses a WellLog whose reference curve is not one of its
  `CurveID`s, which is why the index curve has a curve row of its own.
- **The bulk link is not the mapping's.** The DDMS writes `data.ExtensionProperties.wdms.bulkURI` itself and refuses an
  update that changes it, so the route carries the link the stored record holds into every update. A mapping leaves
  `ExtensionProperties.wdms` alone (a rendered link is replaced, with a warning), and the flow need not list it under
  `preserveDataKeys`.

`WellboreID` is the reference `ref` builds from the row's `wellbore_id`, which is the id the wellbore delivery gives the
wellbore (`dataset.idFrom: key` in `Wellbore@1.0.0`, [one source, several kinds](multi-kind-source.md)). The units go
through the `UnitAlias` lookup table and the sampling domain through the `SamplingDomain` dictionary, both in the
partition's cache; see [Modifiers](../flow/mapping-modifiers.md) and
[Getting your own lookup table into the cache](lookup-table-cache.md). The mapping's value nodes are on
[Mapping values](../flow/mapping-values.md).

## 4. The delivery flow

The well logs go by the `ddms` route. This guide's flow is a smaller one of its own, `flows/welldb-welllog-03-ddms-delivery.yaml`
(the sample estate's `welldb-welllog-03-header-delivery` delivers its logs by the same route, keyed by `source_project`
and `log_id`):

```yaml
flowType: delivery
name: welldb-welllog-03-ddms-delivery
batch: welldb
description: Well log headers as WellLog records, their curve values as bulk data in the Wellbore DDMS.

partitions: [dev, test]

source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [log_id]
    primaryKey: RecId
  datasets:
    curves:
      object: OsduData.silver.WellLogCurve
      join: { log_id: log_id }
      orderBy: [curve_id]
  payloads:
    bulk:
      root: ../data/curves
      locationColumn: curve_folder
      pattern: "*.parquet"
      hashColumn: curves_hash
      chunkCountColumn: chunk_count
  lastModified: update_date
  work: ../.work/welllog

render:
  mapping: WellLog@1.0.0

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
  protocol: ddms
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms
```

- `source.datasets.curves` is the child table the mapping's `$forEach: curves` repeats, joined on the record key.
- `source.payloads.bulk` is where each log's curve files are: the folder `curve_folder` names under `root`, the files
  matching `pattern`, and `curves_hash` saying whether they changed since the last delivery. It is the only payload set,
  so the route streams it without `protocolOptions.payload`. `chunkCountColumn` spares a listing when the run plans.
- `protocol: ddms` sends each record to the DDMS serving its entity type. WellLog is served by the Wellbore DDMS's
  `welllogs` collection, which keeps bulk data, and `ddmsRoot` says where the Wellbore DDMS is under the platform.
- `partitions` names the partitions the flow delivers to ([Delivery flow](../flow/delivery.md#partitions)); each run
  targets one, which mints the ids and fills the mapping's `dataPartition`. The access groups and the legal tag come from
  `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER` and `OSDU_LEGAL_TAG` when the flow sets no `render.parameters`.

The keys of the document are on [Delivery flow](../flow/delivery.md#source) and the pages it links. `sqlflow validate`
checks it offline:

```text
OK  'welldb-welllog-03-ddms-delivery' is valid (delivery: OsduData.silver.WellLog -> ${env:OSDU_URL}).
```

## 5. Check, preview, deliver

Check the flow against the template, the cache and the route before anything is sent. These commands read the template
from the template folders on the mapping's path, and the cache from the module's database, the catalog's: the one `--db`
names, else `SQLFLOW_CATALOG_DB`:

```bash
sqlflow check flows/welldb-welllog-03-ddms-delivery.yaml --partition dev
sqlflow preview flows/welldb-welllog-03-ddms-delivery.yaml --partition dev --key welldb:LOG-0042
sqlflow run flows/welldb-welllog-03-ddms-delivery.yaml --set partition=dev
```

`sqlflow check` says where the records go (`work-product-component--WellLog records go to the welllogs collection of the
DDMS 'wellbore' (/api/os-wellbore-ddms).`) and refuses a mapping whose kind no DDMS the flow reaches serves.
`sqlflow preview` renders one record and shows the document, the files it would send and the requests the route would
make, sending nothing ([preview](../cli/preview.md)).

For each new log, the run then:

1. Checks the record against the DDMS's WellLog rules and the curve files against the record, the ceilings and each other.
2. Writes the record: `POST /api/os-wellbore-ddms/ddms/v3/welllogs` (step `metadata`).
3. Sends the curve values: one file to `.../welllogs/{id}/data`, or several through a session (step `session`) committed
   as one version. Either way step `bulk` records that they landed, so a later try does not send them again.
4. Reads the record back for the version the DDMS now serves, since writing the bulk makes a new version (step `payload`).
   After a session, the committed bulk is described, and fewer rows or columns than the files carried holds the record.

A retry after a failure resumes after the last step that landed: a record written by the previous try is not written
again. A delivery that does not complete is undone (an open session abandoned, a record the try created removed), as
[How a delivery runs on any route](../concepts/protocols.md#when-a-delivery-does-not-complete) describes.

## 6. When a log changes

The record and its bulk data are compared separately. A header change (a new name, a new depth range) renders a
different document, and only the record is written, carrying the DDMS's bulk link. New curve values change
`curves_hash`, and only the bulk data is sent, from the version the ledger holds. A change to both sends both. A
redelivery of the payload sends the bulk data whatever its hash says. How changes are detected is on
[Change detection](../concepts/change-detection.md).

## 7. When a record is held

| What you see | Why | What to do |
| --- | --- | --- |
| `the bulk column(s) TENS match no data.Curves[].CurveID of the record (DEPT, GR, RHOB); ...` | A file has a column no curve row describes. | Add the curve row, or drop the column from the files. |
| `curve widths differ between the bulk data and the record: ...` | An array curve has a different number of columns than its `NumberOfColumns`. | Fill `NumberOfColumns` from the source, or fix the files. |
| `data.ReferenceCurveID is 'DEPT' but data.Curves describes only GR, RHOB; ...` | The index curve has no curve row. | Give the index curve a row in `WellLogCurve`. |
| `data.Curves has the CurveID 'GR' more than once; ...` | Two curve rows share a `curve_id`. | Make the curve ids unique within a log. |
| `the session's chunks carry the column(s) GR, RHOB and not the reference curve 'DEPT'; ...` | Several files, and the index curve is not a column of them. | Write the index curve as a column of every file. |
| `payload chunks 0 (...) and 1 (...) give the same row labels to different rows. ...` | Files that split the rows number them from zero each. | Continue the row index from one file to the next. |
| `payload chunk 0 (chunk_000.parquet) has 3412 columns, above the wellbore DDMS ceiling of 3000 columns per chunk ...` | A file is above a Wellbore DDMS ceiling. | Cut the log into more files. |
| `no payload chunk files were found for the record` | The folder `curve_folder` names holds no file matching `pattern`. | Check the folder and the pattern; the record is held, not sent without its curves. |
| `... records go to the welllog collection of the DDMS 'welldelivery' (/api/well-delivery), which holds records alone and takes no bulk data, so source.payloads.bulk would never be sent. ...`, from `sqlflow check` and the run's preflight, which stops the run before anything is planned or held | A DDMS declared under `target.ddms` before the Wellbore DDMS serves WellLog and keeps records alone (a Well Delivery DDMS does by default). | List that DDMS's `collections` without WellLog, so the Wellbore DDMS takes the logs. |

A held record stays held until its source changes or it is released; `sqlflow records issues` groups held records by
cause ([records](../cli/records.md)). The rules and ceilings behind each message are on
[DDMSs](../flow/ddms.md#the-wellbore-ddms-wellboreddmsv3).

## 8. The same log in a source with interfaces

When the well database delivers wellbores and well logs from one document, the well log interface declares its curve
files as `bulk`, and that is what makes its route `ddms` ([Interfaces](../flow/interfaces.md),
[Delivering several kinds from one source](multi-kind-source.md)):

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms

interfaces:
  wellbores:
    record: { object: OsduData.silver.Wellbore, key: [wellbore_id], primaryKey: RecId }
    mapping: Wellbore@1.0.0
  welllogs:
    record: { object: OsduData.silver.WellLog, key: [log_id], primaryKey: RecId }
    datasets:
      curves: { object: OsduData.silver.WellLogCurve, join: { log_id: log_id }, orderBy: [curve_id] }
    bulk: { root: ../data/curves, locationColumn: curve_folder, pattern: "*.parquet", hashColumn: curves_hash, chunkCountColumn: chunk_count }
    mapping: WellLog@1.0.0
```

Wellbores go by the `storage` route; well logs by `ddms`. A log whose files also include its original LAS file would
declare `files` beside `bulk`, and go by `fileAndDdms`: the LAS file registered through the file service and named in
the record's `Datasets`, the curves sent as bulk data ([Routes](../flow/routes.md#fileandddms-and-manifestandddms)).

## Removing a well log

The record scope deletes the record logically in the Wellbore DDMS, which can be reverted; the everything scope purges the
record and its bulk data (`?purge=true`); the history scope purges the record's earlier versions through the storage
service. See [Removal and reversal](../concepts/removal-and-reversal.md).
