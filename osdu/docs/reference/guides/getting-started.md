---
id: delivery-guide-getting-started
title: "Getting started: your first delivery, from source files to a wellbore record in OSDU"
type: guide
summary: "Deliver a first wellbore end to end: land and key the files, keep the template, write the mapping and flow, check, preview, plan, deliver, read the ledger."
keywords:
  - getting started
  - quickstart
  - first delivery
  - deliver a wellbore
  - template capture
  - write a mapping
  - osdu record id
  - delivery flow
  - sqlflow check
  - sqlflow preview
  - plan run
  - deliver run
  - records list
  - read the ledger
related:
  - guide-getting-started
  - flow-ing
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-cli-check
  - delivery-cli-preview
  - delivery-cli-records
  - delivery-cli-template
  - delivery-cli-dev
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryPreviewVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreviewer.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceBindings.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Identity/TargetId.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Core/Secrets/LocalEnvFile.cs
  - osdu/specs/core/storage/openapi.yaml
  - osdu/specs/core/legal/openapi.yaml
---

# Getting started: your first delivery, from source files to a wellbore record in OSDU

This guide takes one source, a well database exported as CSV files (`welldb`), from a file in a folder to wellbore
records in an OSDU partition, and then reads back what the ledger kept about each one. It uses the command line, so
every step is visible; the same documents run unchanged on the control plane once a repository is synced. The first two
flows are plain SQLFlow and are only sketched here; the OSDU steps are shown in full.

| Step | What you make | Command |
| --- | --- | --- |
| 1 | `.sqlflow/env`: where the databases and OSDU are | |
| 2 | The catalog and the `osdu` schema | `sqlflow db migrate --create` |
| 3 | A file flow and an `ing` flow: the keyed table `OsduData.silver.Wellbore` | `sqlflow run` |
| 4 | The template the mapping fills | `sqlflow template capture` |
| 5 | The mapping, `Wellbore@1.0.0` | `sqlflow validate` |
| 6 | The delivery flow | `sqlflow validate` |
| 7 | The preflight | `sqlflow check` |
| 8 | One record, rendered and not sent | `sqlflow preview` |
| 9 | What a delivery would send | `sqlflow run --operation plan` |
| 10 | The delivery | `sqlflow run` |
| 11 | What the ledger kept | `sqlflow records list`, `records show` |

## What you need

- The `sqlflow` command line of OSDU Delivery: SQLFlow's CLI with the OSDU verbs added (the
  `SqlFlow.Delivery.Cli.Host` project, published as `sqlflow`). Its banner reads
  `sqlflow - the OSDU Delivery command line, powered by SQLFlow`.
- A SQL Server with two databases: one for the catalog (SQLFlow's metadata, with the `osdu` schema beside it), and
  `OsduData` for the source data. SQLFlow creates schemas and tables, never the data database itself, so create
  `OsduData` first and allow snapshot isolation on it, since a delivery reads a record under snapshot isolation by
  default:

  ```sql
  ALTER DATABASE [OsduData] SET ALLOW_SNAPSHOT_ISOLATION ON;
  ```

- An OSDU partition (`dev` here) and a client allowed to write to it: a client id and secret, the token URL and scope,
  an existing legal tag, and the owner and viewer entitlement groups the records will carry.

## Step 1: say where everything is

On a checkout of OSDU Delivery, `dev.bat setup` (Windows) or `./dev.sh setup` (macOS, Linux) writes this file for you,
asking for each value and testing the OSDU client, and creates the databases of step 2
([osdu-delivery-setup](../cli/dev.md)); what follows is the same by hand.

Secrets never go into a document. The documents name references such as `${env:OSDU_URL}`, and the values come from the
environment. For local work, put them in a git-ignored `.sqlflow/env` at the repository root; the CLI applies the
nearest one, searching up from the flow file it is given (or from the current directory), and the process environment
always wins ([SQLFlow's environment variables](../../../../sqlflow/docs/reference/concepts/environment-variables.md)).

```text
# .sqlflow/env (git-ignored)
SQLFLOW_CATALOG_DB=Server=localhost;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True
OSDU_DATA_DB=Server=localhost;Database=OsduData;Integrated Security=True;TrustServerCertificate=True
OSDU_URL=https://osdu.example.com
OSDU_TOKEN_URL=https://login.example.com/oauth2/token
OSDU_CLIENT_ID=<client id>
OSDU_CLIENT_SECRET=<client secret>
OSDU_SCOPE=<scope>
OSDU_ACL_OWNER=data.default.owners@dev.example.com
OSDU_ACL_VIEWER=data.default.viewers@dev.example.com
OSDU_LEGAL_TAG=dev-welldb-public
```

`SQLFLOW_CATALOG_DB` is the database every OSDU verb reads (templates, caches and the ledger live in its `osdu`
schema); `--db <reference>` on a command overrides it, and `SQLFLOW_OSDU_DB` gives the module a database of its own.
The last three are what the four parameters every OSDU record needs default to when a flow supplies no value:
`aclOwner`, `aclViewer` and `legalTag` take `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`,
and `dataPartition` takes the partition the run delivers to.

## Step 2: create the catalog and the osdu schema

```bash
sqlflow db migrate --create
```

`migrate` applies SQLFlow's catalog migrations and then the module's, into the `osdu` schema of the same database;
`--create` provisions the catalog database when it does not exist yet. `sqlflow db status` reports both and exits 2
until every database is current ([the module database](../cli/db.md)).

## Step 3: land the files and key the rows

This is SQLFlow, unchanged. A file flow lands the CSV files into `pre.Wellbore` and refreshes the typed view
`pre.v_Wellbore` ([file flow source](../../../../sqlflow/docs/reference/flow/source.md),
[typed views](../../../../sqlflow/docs/reference/concepts/pre-ingestion-transform.md)); an `ing` flow upserts the view into
the keyed table the delivery reads ([ingestion flows](../../../../sqlflow/docs/reference/flow/ing.md)). The repository:

```text
repo/
  .sqlflow/env
  welldb/
    data/wellbore/wellbores.csv
    flows/welldb-wellbore-01-pre.yaml
    flows/welldb-wellbore-02-ing.yaml
    flows/welldb-wellbore-03-delivery.yaml      (step 6)
    mappings/Wellbore@1.0.0.yaml                (step 5)
```

```text
wellbore_id,wellbore_name,wellbore_uwi,update_date
WB-0001,Wellbore A-1,US-0001-A1,2026-10-01 08:00:00
WB-0002,Wellbore B-2,US-0002-B2,2026-10-01 08:00:00
```

`flows/welldb-wellbore-01-pre.yaml`:

```yaml
name: welldb-wellbore-01-pre
batch: welldb

source:
  type: csv
  location: ../data/wellbore
  options:
    delimiter: ","
    header: true
    srcFile: "*.csv"

target:
  connection: ${env:OSDU_DATA_DB}
  schema: pre
  table: Wellbore

transform:
  inferTypes: true
  columns:
    - { name: wellbore_id, type: varchar(50) }
    - { name: update_date, type: datetime2 }
```

Declare the type of every column the mapping writes into a number property (depths, sizes), for example
`- { name: top_depth, expr: "NULLIF(@ColName, '')", type: "decimal(38,18)" }`. SQLFlow's type inference reads a column whose values are all 0 or 1 (or `true`, `false`, `yes`, `no`, `y`, `n`) as
`bit`, which renders `false` where the template takes a number, and every record is then held with
`value 'false' is not a valid number (a boolean is not a number)`. The wellbore file
has no such column; the well log header's depths do ([well logs with their curves](bulk-data.md)).

`flows/welldb-wellbore-02-ing.yaml`:

```yaml
flowType: ing
name: welldb-wellbore-02-ing
batch: welldb

connections:
  osduData: ${env:OSDU_DATA_DB}

source:
  server: osduData
  object: OsduData.pre.v_Wellbore

target:
  server: osduData
  object: OsduData.silver.Wellbore
  identityColumn: RecId

load:
  keyColumns: [wellbore_id]

systemColumns:
  insertedDate: true
  updatedDate: true
```

```bash
sqlflow run welldb/flows/welldb-wellbore-01-pre.yaml
sqlflow run welldb/flows/welldb-wellbore-02-ing.yaml
```

Three things here matter to the delivery. The key (`wellbore_id`) is the record's identity, and the mapping and the
delivery flow will name the same column. `UpdatedDate_DW`, which the `ing` flow stamps on every row it inserts or
changes, is what the delivery's incremental read windows on, so keep `systemColumns.updatedDate` on. `identityColumn:
RecId` gives the table the identity primary key a delivery pages by. The provenance columns the file flow wrote
(`FileName_DW`, `RowNumber_DW`) pass through the view into the table, and become each record's origin in the ledger.

## Step 4: keep the template the mapping fills

A mapping fills a template: the OSDU schema of one kind, in one version, which a template folder on the mapping's path
holds, a folder named `templates` committed with the mappings ([template folders](../concepts/templates.md#template-folders)).
Write the wellbore kind's schema into the project's folder from the OSDU data definitions (the Open Group's public
repository of OSDU schemas, newest release unless `--release` names one):

```bash
sqlflow template capture --kind osdu:wks:master-data--Wellbore:1.3.0 --out welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json
```

```text
wrote template osdu:wks:master-data--Wellbore:1.3.0 version 58d6bdbd9d066a06 to <repo>/welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json
```

The version is a 16-character hash of the schema. A mapping can pin it (`template.version`), or name the kind alone and
fill the one version the nearest folder holding the kind holds. The file is the template: commit it with the mappings,
and the repository's sync records it; nothing of it is kept in a database. **Download** on the Browse OSDU tab of the
GUI's Templates page gives the same file. `sqlflow template folders` lists what the repository's template folders hold:

```text
welldb/templates
  welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json  osdu:wks:master-data--Wellbore:1.3.0  58d6bdbd9d066a06
```

Copy the version the capture printed into the mapping in the next step: a different release can bundle a different
schema and give a different version. More on [templates](../concepts/templates.md).

## Step 5: write the mapping

The mapping says how one row of `silver.Wellbore` becomes one record of the template. It lives in the `mappings/`
folder next to the flows, in a file named after its name and version. Write it by hand, or start one from the template in
the GUI's mapping builder, opened from the Mappings page ([writing a mapping](writing-a-mapping.md)).

`welldb/mappings/Wellbore@1.0.0.yaml`:

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

What each part does:

- `dataset.system` and `dataset.key` make the delivery key: a UUID derived from `welldb` and the row's `wellbore_id`, so
  the same row is always the same record in the ledger. `dataset.idFrom: key` makes the record's OSDU id from the key's
  own value in the partition the run delivers to, `dev:master-data--Wellbore:WB-0001`, rather than from the delivery key
  (the default), so a well log mapping can later point at its wellbore from the `wellbore_id` on its own row
  ([one source, several kinds](multi-kind-source.md)). Choose the id form before the first delivery: a record keeps the
  id it first claims ([the OSDU id](../flow/mapping.md#the-osdu-id)). The mapping never writes `id` or `kind`.
- `label` is how the record is shown, and `identity` names the columns the ledger indexes, so the Records page finds a
  wellbore by its name or UWI.
- `record` is laid out as the record is. `$from` reads a column of the row, `{$param.name}` a parameter, and anything
  else is a literal ([mapping](../flow/mapping.md), [value nodes](../flow/mapping-values.md)).

```bash
sqlflow validate welldb/mappings/Wellbore@1.0.0.yaml
```

```text
OK  'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0' is valid (mapping).
```

Validation reads the mapping alone. Whether every path it fills exists in the template, and every value fits, is
checked by `sqlflow check` and `sqlflow values`, which read the template from the template folder.

## Step 6: write the delivery flow

`welldb/flows/welldb-wellbore-03-delivery.yaml`:

```yaml
flowType: delivery
name: welldb-wellbore-03-delivery
batch: welldb

partitions: [dev]

source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.Wellbore
    key: [wellbore_id]
    primaryKey: RecId
  lastModified: update_date
  work: ../.work/wellbore

render:
  mapping: Wellbore@1.0.0

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
  protocol: storage
```

| Key | What it does here |
| --- | --- |
| `partitions: [dev]` | The partition a run delivers to. With one listed, a run needs to name none; list `[dev, test]` later and a run names one with `--set partition=test`, each partition keeping its own ledger. |
| `source.record` | The keyed table, the same key column as the mapping's `dataset.key`, and its identity primary key. |
| `source.lastModified` | The row's own business version: a row older than what the ledger holds is never sent. |
| `source.work` | Where the plan writes the rendered documents for the delivery to read back, relative to the flow file. Keep it out of git. |
| `render.mapping` | The mapping, pinned by `Name@version`, found in the nearest `mappings/` folder above the flow. |
| `target.protocol: storage` | The route: each record goes to OSDU's storage service (`PUT /api/storage/v2/records`). Other routes send files, bulk data and manifests ([routes](../flow/routes.md)). |

Every other key has a default: change detection compares what a row renders to with what the ledger holds, unchanged
records are skipped, and a run reads only the rows the `ing` flow changed since the last one
([delivery flow](../flow/delivery.md)).

```bash
sqlflow validate welldb/flows/welldb-wellbore-03-delivery.yaml
```

```text
OK  'welldb-wellbore-03-delivery' is valid (delivery: OsduData.silver.Wellbore -> ${env:OSDU_URL}).
```

## Step 7: check the flow with everything it renders with

`sqlflow check` is the preflight: it reads the flow, the mapping it pins, the template that mapping fills and the cache it
reads, and runs the mapping's checks against the template, without reading a row or sending anything:

```bash
sqlflow check welldb/flows/welldb-wellbore-03-delivery.yaml
```

```text
OK  welldb-wellbore-03-delivery (<flow id>)
    mapping     Wellbore@1.0.0
    template    osdu:wks:master-data--Wellbore:1.3.0 version 58d6bdbd9d066a06 (welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json)
    cache       none (the mapping reads nothing from a cache)
    context     <render context hash>
    mappings    <repo>/welldb/mappings
    source      ${env:OSDU_DATA_DB} OsduData.silver.Wellbore
    payloads    none (the flow streams no payload files)
    tables      not read (run with --connect to open the ingestion tables)
```

`--connect` also opens the table through the flow's own connection and reports the window a run would read, how many
records are in it, the key columns and their SQL types. A mapping that fills a path the template does not have, writes a
value the template takes in another shape, or reads a parameter nothing supplies fails here with every problem listed,
for example `record.data.FacilityNam fills a variable that template osdu:wks:master-data--Wellbore:1.3.0 version
58d6bdbd9d066a06 does not have.` ([check](../cli/check.md), [preflight](../concepts/preflight.md)).

## Step 8: preview one record

```bash
sqlflow preview welldb/flows/welldb-wellbore-03-delivery.yaml
```

The preview reads the first record of the scope (or the one `--key WB-0002` names), renders it exactly as a delivery
would, and sends nothing. It says where the row came from, what the next run would do with it, and shows the document:

```text
OK  welldb-wellbore-03-delivery: welldb:WB-0001 [Wellbore A-1]
    asked       the scope's first record of 2
    from        wellbores.csv row 1
    mapping     Wellbore@1.0.0 on osdu:wks:master-data--Wellbore:1.3.0
    route       storage
    next run    create: not in ledger
    document    dev:master-data--Wellbore:WB-0001 (<n> characters, hash <hash>)
    ...
```

It exits 1 when there is nothing to preview, and says why. `--out preview.json` writes the whole preview as JSON
([preview](../cli/preview.md)). To check every row at once against the template's rules, run `sqlflow values` on the
flow ([values](../cli/values.md)).

## Step 9: plan the run

A plan is a run of the flow that reads the changed rows, renders them, compares them with the ledger and reports what a
delivery would send, writing nothing to OSDU:

```bash
sqlflow run welldb/flows/welldb-wellbore-03-delivery.yaml --operation plan
```

The run log is echoed as it runs (each line starts with its time, level and step), and names the first records and what
would happen to each; its outcome line counts them all, and SQLFlow's result line follows:

```text
...
plan: 2 record(s): 2 to deliver, 0 unchanged, 0 awaiting approval, 0 stale, 0 held, 0 blocked, 0 untracked
...
OK  delivery 'welldb-wellbore-03-delivery' completed in 1.8124566s
  run log: <repo>/welldb/flows/.sqlflow/runs/welldb-wellbore-03-delivery/<run folder>
```

`--json` prints the outcome instead (`records`, `deliveries`, `skips`, `holds`, `blocked`, `untracked`, the issues, and up
to 20 sampled records). This is also where a disagreement between the flow and its tables shows, before anything is
sent:

```text
flows/welldb-wellbore-03-delivery.yaml: source.record.key is [wellbore_uwi] but mapping 'Wellbore@1.0.0' keys its records
by [wellbore_id]. A record's identity is one thing: the two have to name the same columns in the same order.
```

## Step 10: deliver

```bash
sqlflow run welldb/flows/welldb-wellbore-03-delivery.yaml
```

`deliver` is the default operation. Before it plans, the run asks OSDU's legal service whether the mapping's legal tags
are valid (`POST /api/legal/v1/legaltags:validate`) and stops if one is not:

```text
The legal service refuses 1 of the legal tag(s) mapping Wellbore@1.0.0 puts on every record, so nothing was planned or
sent: dev-welldb-public: <the legal service's reason>
```

Then it registers a submission, plans the records into work batches, and delivers each batch through the route, writing
one attempt per try to the ledger. The run log ends with what this run did:

```text
...
this run: 2 planned, 2 delivered, 0 unchanged, 0 held, 0 failed; submission <submission id> ...
...
OK  delivery 'welldb-wellbore-03-delivery' completed in 6.0517739s
```

A record OSDU refuses for something a retry cannot fix (a value its schema rejects, say) is held with the error, and
the run goes on with the rest. Run the flow again without changing the table and it reads nothing: no row changed in
its window. Change a row and run again, and only that record is rendered, compared and, if its document differs, sent as
a new version of the same OSDU record ([change detection](../concepts/change-detection.md),
[record lifecycle](../concepts/record-lifecycle.md)).

## Step 11: read the ledger

```bash
sqlflow records list welldb/flows/welldb-wellbore-03-delivery.yaml
```

```text
welldb-wellbore-03-delivery: 2 record(s)
  <delivery key>  Delivered   welldb:WB-0001
      dev:master-data--Wellbore:WB-0001 v<OSDU version>
  <delivery key>  Delivered   welldb:WB-0002
      dev:master-data--Wellbore:WB-0002 v<OSDU version>
```

Each record shows its delivery key, its status, its source key (`welldb:` and the key's values), the OSDU id and the
version OSDU gave the last delivery, and its last error when it has one. `--status held`, `--search` and `--issue`
narrow the list. One record, with every try it took:

```bash
sqlflow records show welldb/flows/welldb-wellbore-03-delivery.yaml --key welldb:WB-0001
```

```text
welldb-wellbore-03-delivery: welldb:WB-0001
  key        <delivery key>
  status     Delivered
  osdu id    dev:master-data--Wellbore:WB-0001 v<OSDU version>
  mapping    Wellbore@1.0.0
  from       wellbores.csv row 1
  delivered  2026-10-09 09:20:31Z
  1 attempt(s) shown, ...; newest first:
    ...
```

`--key` takes the delivery key, or the source key as the list shows it (`welldb:WB-0001`). Each attempt shows when it ran, its outcome and phase, the
node that ran it, the OSDU version it produced, what each step of the route answered, and its error. The same history is
on the record's page in the GUI, with the record's chain back to the `ing` run and the landing run that brought its file
in ([the ledger](../concepts/ledger.md), [records](../cli/records.md)).

## If your mapping reads reference data

This wellbore mapping writes its one reference (`AliasNameTypeID`) as a fixed id, so it needs no cache. A mapping that
resolves references from values (a unit spelled `ft` as the partition's `UnitOfMeasure` record, say) reads them from the
partition's cache, which a cache flow captures from OSDU. Run that cache flow once before the delivery, or `sqlflow check`
stops with:

```text
mapping WellLog@1.0.0 reads the cache of partition 'dev', which holds no version yet. Refresh a cache flow that builds it
(one naming 'dev' under partitions, or whose source.headers.data-partition-id is 'dev') to capture one.
```

[Caching OSDU reference data](reference-data-cache.md) and [your own lookup tables](lookup-table-cache.md) walk
through both.

## Running it on the control plane

Commit the folder to the repository the control plane syncs. Every flow becomes a pipeline, ordered by lineage: the
pre flow, then the `ing` flow, then the delivery ([lineage](../concepts/lineage.md)). Join the three to one schedule
with `schedule: <name>` and a fire runs them in that order; or trigger one from the GUI, or from a terminal:

```bash
sqlflow trigger --repo <repo> --flow welldb-wellbore-03-delivery --operation plan --follow
```

On the control plane, the OSDU values this guide put in `.sqlflow/env` belong to the partition: its configuration names
`dev` and gives them as `Osdu:Partitions:dev:Properties:<NAME>` (the client secret as a `${keyvault:...}` reference), so
every run bound to `dev` carries them ([configured partitions](../concepts/partitions.md#configured-partitions)). The
control plane's start-up is not done (`/health/startup`) until at least one partition is registered. A node that runs a delivery needs the data
database (`OSDU_DATA_DB`) in its own environment, and the module's database through `SQLFLOW_OSDU_DB`, since a node opens
no catalog connection ([the node](../cli/worker.md)).

## What goes wrong

| Message (start) | Cause | What to do |
| --- | --- | --- |
| `mapping Wellbore@1.0.0 cannot render: no template folder on the path of ... holds template osdu:wks:master-data--Wellbore:1.3.0 version ...` | No template folder on the mapping's path holds that version. | Write it into the folder (step 4), and name the version it prints in the mapping, or leave the version out. |
| `Mapping 'Wellbore@1.0.0' was not found under '...mappings'. Expected one of: Wellbore@1.0.0.yaml, ...` | The mapping file is not in the nearest `mappings/` folder, or is named differently. | Move or rename it, or set `render.mappings`. |
| `... caches live in the module's database, which this host was started without.` | No module database: no `SQLFLOW_CATALOG_DB`, `--db` or `SQLFLOW_OSDU_DB`. | Set one (step 1). |
| `Environment variable 'OSDU_ACL_OWNER' is not set.` | A reference the flow or a default parameter names has no value here. | Add it to `.sqlflow/env` or the environment; on a control plane, to the partition's values in its configuration (`Osdu:Partitions:dev:Properties:OSDU_ACL_OWNER`) or to the central configuration (`sqlflow config set`). |
| `source.record.key is [...] but mapping '...' keys its records by [...]` | The flow and the mapping name different key columns. | Name the same columns, in the same order. |
| `... the source database does not allow snapshot isolation ...` | `OsduData` does not allow snapshot isolation. | Run the `ALTER DATABASE` above, or set `source.incremental.isolation: readCommitted`. |
| `The legal service refuses ... of the legal tag(s) ...` | The legal tag does not exist or is not valid in the partition. | Use a valid tag in `OSDU_LEGAL_TAG`, or supply `legalTag` under `render.parameters`. |
| `OSDU already holds a record at dev:master-data--Wellbore:WB-0001, the OSDU id made from the key, and no record of the ledger claimed it ...` (a held record's error) | Another system, or another instance of the delivery, already wrote a record at the id this wellbore's key gives; the record is held and nothing is sent. | When it is this wellbore's record, adopt it (**Adopt** on the record's page, `sqlflow records adopt`); otherwise remove or rename that record in OSDU, or change the mapping's key or `dataset.idFrom` before anything is delivered. |
| `invalid YAML at line ..., column ... - Property '...' not found on type '...'` | A misspelled or misplaced key: OSDU documents refuse unknown keys. | Fix the key the message names. |

## Where to go next

- [Writing a mapping](writing-a-mapping.md): the builder, `sqlflow values`, and what the checks catch.
- [One source, several kinds](multi-kind-source.md): wellbores and their well logs from one document.
- [Well logs with curves](bulk-data.md): the header record and its bulk data.
- [The operations runbook](operations-runbook.md): held and failed records, releasing, redelivering.
- [Which delivery problem maps to which shape](../../wiki/maps/pattern-catalog.md).
