---
id: delivery-flow-overview
title: "Anatomy of the OSDU documents: six flow kinds, mappings and dictionaries"
type: flow-reference
summary: "Which documents OSDU Delivery adds to SQLFlow (six flowType kinds, mappings, dictionaries), how they chain after pre and ing flows, and how to lay them out."
keywords:
  - flowtype
  - documenttype
  - document kinds
  - osdu flow kinds
  - delivery flow
  - cache flow
  - mapping document
  - dictionary document
  - repository layout
  - flow naming
  - mappings folder
  - strict keys
  - operations
yamlPath: "(root: flowType, documentType)"
related:
  - flow-overview
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-flow-cache
  - delivery-concept-overview
  - delivery-cli-validate
  - delivery-cli-run
  - cli-validate
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryDocumentKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLayout.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryCatalog.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunArtifacts.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - sqlflow/src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - sqlflow/src/SqlFlow.Yaml/FlowDocumentKinds.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
---

# Anatomy of the OSDU documents: six flow kinds, mappings and dictionaries

OSDU Delivery is SQLFlow with a module installed, so every SQLFlow document still works exactly as
[SQLFlow's flow overview](../../../../sqlflow/docs/reference/flow/overview.md) describes it: file flows, `ing`, `exp`,
`sp`, `batch` and the rest. The module adds eight document kinds of its own: six flow kinds, selected by `flowType` like
SQLFlow's, and two companion documents, selected by `documentType`, that flows read but that never run. This page is the
map of those eight: what each is for, how SQLFlow dispatches them, what they share, how they chain after SQLFlow's own
flows, and how a repository lays them out. Each kind's keys are on its own page.

## Where the OSDU documents sit in a chain

Data reaches OSDU through SQLFlow's flows first. A file flow lands the source files into a `pre` table and refreshes its
typed view, an `ing` flow upserts that view into a keyed ingestion table, and only then does an OSDU flow read the table.
The module adds no reader of its own for source files.

```text
source files --file flow--> pre.Wellbore + pre.v_Wellbore --ing flow--> silver.Wellbore --delivery flow--> OSDU
                                                                                         \--> the ledger (osdu schema)
```

| Step | Document | Documented by |
| --- | --- | --- |
| Land the files | A file flow (no `flowType`) | SQLFlow: [source](../../../../sqlflow/docs/reference/flow/source.md), [pre-ingestion views](../../../../sqlflow/docs/reference/concepts/pre-ingestion-transform.md) |
| Key the rows | `flowType: ing` | SQLFlow: [ingestion flows](../../../../sqlflow/docs/reference/flow/ing.md) |
| Render and deliver | `flowType: delivery`, pinning a `documentType: mapping` | This corpus: [delivery flow](delivery.md), [mapping](mapping.md) |
| Everything after delivery | `cache`, `retrieval`, `assertion`, `dimension`, `inventory`, `removal` | This corpus, one page per kind |

SQLFlow's lineage orders all of them in waves, so a delivery runs after the `ing` flow that loads its table, and an
assertion after the delivery that writes the type it tests ([lineage](../concepts/lineage.md)).

## The document kinds

| Discriminator | Kind | What it does | Default operation | Page |
| --- | --- | --- | --- | --- |
| `flowType: delivery` | Delivery flow | Reads the rows an ingestion table changed, renders each through its pinned mapping and delivers it to OSDU, recording everything in the ledger | `deliver` | [delivery](delivery.md) |
| `flowType: cache` | Cache flow | Captures OSDU reference data, ingestion tables, dictionaries and dimensions into one versioned cache per partition, which mappings render against | `refresh` | [cache](cache.md) |
| `flowType: retrieval` | Retrieval flow | Pages OSDU's search index into JSON Lines files on the lake, with a manifest per run | `retrieve` | [retrieval](retrieval.md) |
| `flowType: assertion` | Assertion flow | Runs tests of what a partition holds (counts, values, references, schema conformance, bulk data) and keeps each run's report | `test` | [assertion](assertion.md) |
| `flowType: dimension` | Dimension flow | Reads every distinct value of an attribute of OSDU records, cleans each into a value a person picks, and keeps both with the search filter they stand for | `build` | [dimension](dimension.md) |
| `flowType: inventory` | Inventory flow | Keeps every id and version a kind holds in a partition and compares them with the ledgers: orphans, missing records, ids a ledger forgot | `build` | [inventory](inventory.md) |
| `flowType: removal` | Removal flow | Reads the keys a source holds now, whole, and the keys it lists as removed, compares them with a delivery flow's ledger, and takes out of OSDU, once, the records the source no longer holds | `remove` | [removal](removal.md) |
| `documentType: mapping` | Mapping | How one row of an ingestion table becomes one OSDU record of one kind, pinned by `Name@version` | (never runs) | [mapping](mapping.md) |
| `documentType: dictionary` | Dictionary | A small lookup table kept in the repository, held in a partition's cache by a cache flow | (never runs) | [dictionary](dictionary.md) |

The OSDU verbs of the `sqlflow` command line (`check`, `preview`, `values`, `records`, `cache`, `template`,
`assertions`, `dimensions`, `inventory`, `removal`, `config`, `partition`) work with these documents and what their runs record.
Running one is SQLFlow's `sqlflow run` or `sqlflow trigger` with the kind's operation
([running an OSDU flow](../cli/run.md)).

## How SQLFlow dispatches them

The module registers each flow kind with SQLFlow's document loader (`IFlowDocumentKind`) and each companion document as
an `ICompanionDocumentKind`, in every host: the control plane, a worker node, the CLI. SQLFlow's loader probes the root
`flowType`, finds no built-in kind of that name, and hands the document to the registered kind. A `flowType` nothing knows
fails with SQLFlow's menu, followed by the registered kinds:

```text
ERROR  flows/x.yaml: unknown flowType 'deliver'. Use 'ing' for a table-to-table ingestion flow, ... or omit flowType
for a file flow. Registered kinds: 'assertion' for run qualified tests of what an OSDU partition holds ..., 'cache' for
capture OSDU reference data, dictionaries and ingestion tables into a versioned cache ..., 'delivery' for deliver records
from ingestion tables into OSDU, 'dimension' for ..., 'inventory' for ..., 'removal' for ..., 'retrieval' for ....
```

A document with a `documentType` is a companion: `sqlflow validate` and the repository sync read it under its own type
and never treat it as a file flow. An unknown one fails the same way:

```text
ERROR  b.yaml: unknown documentType 'mappings'. Registered document types: 'dictionary' for a lookup table a cache flow
holds in the partition's cache, 'mapping' for how a delivery flow renders the rows of an ingestion table into records of
one OSDU kind.
```

One registration owns both the delivery flows and the mappings they pin, and each says what it is in its own words: the
mapping's description in that list is what a mapping is, not what a delivery flow does.

## What every OSDU document shares

### Unknown and repeated keys are errors

SQLFlow's own loaders ignore a key they do not know. The module's loaders do not: every OSDU document is read strictly,
so a misspelled key and a key written twice in one map both fail the load with the line and column.

```text
ERROR  flows/welldb-wellbore-03-delivery.yaml: invalid YAML at line 14, column 3 - Property 'wrok' not found on type 'SqlFlow.Delivery.Documents.FlowSourceYaml'.
ERROR  flows/welldb-wellbore-03-delivery.yaml: invalid YAML at line 4, column 1 - Encountered duplicate key batch
```

The type named in the first message tells you which block the key was in (`FlowSourceYaml` is `source:`).

### The platform envelope

`name`, `batch`, `description`, `schedule`, `mode` and `lifecycle` are SQLFlow's envelope keys: the platform reads them
for every kind, built in or registered, and the OSDU loaders let them pass. A schedule, a membership such as
`schedule: welldb-hourly`, `mode: manual` and `lifecycle: development` behave exactly as SQLFlow documents them
([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)).
`name` is required on every flow kind, and the flow's identity derives from it as for any SQLFlow flow.

A schedule fire of an OSDU flow can name the operation and the values it runs with (`schedule.operation`,
`schedule.values`), since a fire supplies nothing else.

### Secrets are references

Every credential an OSDU document holds (`target.auth` or `source.auth` secrets, the token URL, the ingestion connection)
is a `${env:NAME}` or `${keyvault:vault/secret}` reference, resolved on the host that runs the flow. Each kind reports
its credential references to SQLFlow's secret-hygiene check, so a literal secret is flagged the way SQLFlow flags one in
any flow ([connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md)).

### Parameters and partitions

Every flow kind may declare `parameters`: named run values a run supplies with `--set name=value` (or a schedule with
`values`), used as `{name}` tokens where the kind allows them. A parameter may list its `choices`, and a delivery flow's
may look its values up in a column of its record table; the run form offers them, and one rule decides what a run takes
([parameters](parameters.md)). The OSDU partition a run acts in is not a parameter but a
run value of its own, `partition`. Every kind except retrieval may list its partitions under `partitions`; a flow that
names none and has no `data-partition-id` header serves every partition registered with the catalog
([partitions](../concepts/partitions.md)).

### Operations

A run of an OSDU flow performs one operation of its kind, named with `--operation` (the default when left out is the
first one listed):

| Kind | Operations |
| --- | --- |
| delivery | `deliver`, `plan`, `intake`, `drain`, `verify`, `replan`, `sync`, `reverse`, `undo`, `remove`, `delete-ledger` |
| cache | `refresh`, `plan` |
| retrieval | `retrieve`, `plan` |
| assertion | `test`, `plan` |
| dimension | `build`, `plan` |
| inventory | `build`, `reconcile`, `plan`, `remove` |
| removal | `remove`, `plan` |

`plan` writes nothing to OSDU on every kind. Each kind also accepts a JSON payload (`--payload`) of its own and refuses
what does not belong to it: a cache run takes only the configuration the control plane supplies, a retrieval run only
`force`, an assertion run names `tests` or `tags`, a dimension run `dimensions`, an inventory run `inventories`, a removal
run `entries`. What each
operation and payload does is on [running an OSDU flow](../cli/run.md).

### Run artifacts

A run writes the same artifacts as any SQLFlow run, under `.sqlflow/runs/<flow>/` beside the flow file: `run.json` (the
kind's outcome), `run.log` and `trace.sql`. The `flowKind` recorded is the `flowType` value (`delivery`, `cache`, ...).
`sqlflow run` echoes the run log live and ends with SQLFlow's line for a registered kind:

```text
OK  delivery 'welldb-wellbore-03-delivery' completed in 4.2186943s
  run log: .../flows/.sqlflow/runs/welldb-wellbore-03-delivery/...
```

`--json` prints the kind's outcome instead ([run artifacts](../../../../sqlflow/docs/reference/concepts/run-artifacts.md)).

### Which kinds need the repository

A node runs a flow from a snapshot of its one file unless the flow needs files beside it. A delivery flow always needs
the repository tree, because its mappings live there. A cache flow needs it only when it holds a dictionary, and a
dimension flow only when a cleaning step maps through a dictionary. A removal flow always needs it, because it opens the
delivery flows it names and reads their mappings. Retrieval, assertion and inventory flows need nothing but their own
document.

## The shape of each kind

One document of each kind, from the example estate used throughout this corpus (a well database exported as files,
`welldb`): the whole file where it is short, else its top, with `# ...` where lines are left out and a first line that
names the file. Each kind's page shows the whole file and has the full key reference.

### flowType: delivery

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

`render.mapping` is pinned as `Name@version`; a floating reference fails with
`render.mapping 'Wellbore' must be pinned as 'Name@version'; floating references are not allowed.` A source that
delivers several kinds declares `interfaces` instead of one record table and mapping ([interfaces](interfaces.md)).

### documentType: mapping

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

`template.version` is the 16-character version of the template, which a template folder on the mapping's path holds
([template folders](../concepts/templates.md#template-folders)); left out, the mapping fills the one version of the kind
the nearest folder holding it holds.
`dataset.idFrom: key` makes each record's OSDU id from its key (`dev:master-data--Wellbore:WB-0001`), so other mappings
can refer to it ([the OSDU id](mapping.md#the-osdu-id)).

### flowType: cache

```yaml
flowType: cache
name: welldb-lookups-00-cache
batch: welldb
description: The lookup tables the well database's mappings translate its values through.

source:
  connection: ${env:OSDU_DATA_DB}

types:
  - table: OsduData.silver.UnitAlias
    key: source_unit
    fields: [osdu_unit]
  - table: OsduData.silver.CurveDictionary
    key: mnemonic
    fields:
      - log_curve_type_id
      - { column: unit, as: curve_unit }
  - dictionary: sampling-domain
    name: SamplingDomain

schedule: welldb-lookups
```

A type has one origin: `kind` (searched on OSDU, which needs `source.endpoint`), `table` (an ingestion table, which needs
`source.connection`), `dictionary`, or `dimension`.

### documentType: dictionary

```yaml
documentType: dictionary
name: sampling-domain
description: How the well database writes a log's sampling domain, as the codes of the partition's WellLogSamplingDomainType records.
entries:
  MD: Depth
  DEPTH: Depth
  TVD: Depth
  TIME: Time
  UNKNOWN: ~
```

The file is `dictionaries/sampling-domain.yaml`, and its `name` must match the file name.

### flowType: retrieval

```yaml
flowType: retrieval
name: welldb-retrieval-01-wellbores
batch: welldb
description: Wellbore records of the dev partition, back on the lake as JSON Lines, a day's changes at a time.

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
  headers:
    data-partition-id: dev
  kind: osdu:wks:master-data--Wellbore:1.*.*
  incremental:
    field: modifyTime
    since: 2026-01-01T00:00:00Z
  fetchRecords: true

target:
  location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/wellbores

schedule:
  name: welldb-retrieval-nightly
  cron: "0 3 * * *"
  timezone: UTC
```

### flowType: assertion

```yaml
# flows/welldb-welllog-04-assertion.yaml: its top and first test (the whole file is on the assertion flow page)
flowType: assertion
name: welldb-welllog-04-assertion
batch: welldb
description: What the well logs of the well database look like in OSDU once they are delivered.

partitions: [dev, test]

parameters:
  system:
    default: welldb
    description: The data.Source value the delivered records carry.

source:
  endpoint: ${env:OSDU_URL}
  # ... auth as on the delivery flow, and ddmsRoot

# ... defaults, failRunOn, reliability, parallelism

tests:
  - name: logs-delivered
    description: Every log the delivery flow holds as delivered is in the search index, indexed cleanly.
    tags: [smoke]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    read: index
    assert:
      - count: { atLeast: 1 }
      - delivered: welldb-welllog-03-delivery
      - indexed: true
  # ... three more tests
```

### flowType: dimension

```yaml
# flows/welldb-welllog-05-dimensions.yaml: its top and first dimension (the whole file is on the dimension flow page)
flowType: dimension
name: welldb-welllog-05-dimensions
description: The wellbores, curve mnemonics and units of the well logs the welldb flows deliver, ready for a search panel.
batch: welldb
partitions: [dev, test]

parameters:
  deliveredBy: { default: welldb }

source:
  endpoint: ${env:OSDU_URL}
  # ... auth as on the delivery flow

dimensions:
  - name: Wellbore
    description: Every wellbore the well logs name, by its name, with its country and operator.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.WellboreID
    label: data.FacilityName
    # ... unlabelled and attributes
  # ... three more dimensions
```

### flowType: inventory

```yaml
# flows/welldb-06-inventory.yaml: its top and first inventory (the whole file is on the inventory flow page)
flowType: inventory
name: welldb-06-inventory
description: Every wellbore and well log OSDU serves in the partition, set against the ledgers of the welldb flows.
batch: welldb
partitions: [dev, test]

source:
  endpoint: ${env:OSDU_URL}
  # ... auth as on the delivery flow
  read: search

# ... owners, maxMissingChecks, removal, reliability, parallelism

inventories:
  - name: Wellbores
    description: Every wellbore record, whatever its authority, source and schema version.
    kind: "*:*:master-data--Wellbore:*"
  # ... two more inventories
```

### flowType: removal

```yaml
# flows/welldb-wellbore-05-removal.yaml: the whole file
flowType: removal
name: welldb-wellbore-05-removal
description: The wellbores the well database no longer holds, taken out of OSDU.
batch: welldb
partitions: [dev]

guard:
  maxPercent: 20

removals:
  - flow: welldb-wellbore-03-delivery
    keys:
      type: csv
      location: ../data/wellbore
      options:
        delimiter: ","
        header: true
        srcFile: "*.csv"

schedule: welldb-removal
```

## Laying out a repository

An OSDU flow lives in a git repository the control plane syncs, next to what it renders with and the flows that feed
it. The product only reads the repository. The usual layout is one folder per source, which the catalog and the GUI call
a project:

```text
repo/
  .sqlflow/env                                  local development values (git-ignored), see SQLFlow's environment variables
  welldb/
    data/wellbore/                              where the pre flow's files are dropped
    flows/welldb-wellbore-01-pre.yaml           file flow: lands the files into pre.Wellbore
    flows/welldb-wellbore-02-ing.yaml           flowType: ing: keys them into silver.Wellbore
    flows/welldb-wellbore-03-delivery.yaml      flowType: delivery
    flows/welldb-welllog-01-curves-pre.yaml
    flows/welldb-welllog-01-header-pre.yaml
    flows/welldb-welllog-02-curves-ing.yaml
    flows/welldb-welllog-02-header-ing.yaml
    flows/welldb-welllog-03-delivery.yaml
    flows/welldb-welllog-04-assertion.yaml      flowType: assertion
    flows/welldb-welllog-05-dimensions.yaml     flowType: dimension
    flows/welldb-06-inventory.yaml              flowType: inventory
    flows/welldb-wellbore-05-removal.yaml       flowType: removal
    flows/welldb-retrieval-01-wellbores.yaml    flowType: retrieval
    mappings/Wellbore@1.0.0.yaml                documentType: mapping
    mappings/WellLog@1.0.0.yaml
    cache/welldb-lookups-00-cache.yaml          flowType: cache over the lookup tables and dictionaries
    cache/osdu-reference-00-cache.yaml          flowType: cache over the partition's reference data
    dictionaries/curve-aliases.yaml             documentType: dictionary, which the dimension flow maps through
    dictionaries/sampling-domain.yaml           documentType: dictionary
```

How a flow finds what it reads:

| Document | Where it is found |
| --- | --- |
| A mapping a delivery flow pins | `render.mappings` (relative to the flow file) when set; otherwise the nearest `mappings/` folder walking up from the flow file, at most 16 levels. The file is `<Name>@<version>.yaml` (or `.yml`), or `<Name>/<version>.yaml`. |
| A dictionary a cache or dimension flow names | The nearest `dictionaries/` folder walking up from the flow file. The file is `<name>.yaml` or `<name>.yml`. |
| The template a mapping fills | The [template folders](../concepts/templates.md#template-folders) on the mapping's path, folders named `templates`, nearest first. |
| A partition's cache | Not in the repository: written into the module's database by the runs of cache flows ([partition cache](../concepts/partition-cache.md)). |

A mapping or dictionary file must declare the name (and, for a mapping, the version) it is filed under, so a reference
always reaches the reviewed file:

```text
mappings/Wellbore@1.0.0.yaml: declares 'Wellbore@1.1.0' but is filed as 'Wellbore@1.0.0'. The file name and the document must agree.
```

A reference carrying a path (`/`, `\` or `..`) is refused before any file is read. The delivery flow's own
`source.work` (where the intake writes its work batches) is also resolved against the flow file; keep it out of git.

### Naming flows so the folder reads in order

Nothing in the code enforces a flow name beyond SQLFlow's rules, but a name of the form `<source>-<entity>-<nn>-<stage>`
makes a sorted folder read in the order the chain runs: every `01` lands files, every `02` keys them, `03` delivers,
and later numbers read what was delivered. An entity loaded from several tables puts the area between the number and
the stage, so `welldb-welllog-01-curves-pre` and `welldb-welllog-01-header-pre` sit together. Cache flows that every delivery depends
on take `00`. SQLFlow orders runs by lineage, not by name; the numbers are for people.

## Validating the documents

`sqlflow validate` reads OSDU documents through the same loaders a run uses, offline: no catalog, no OSDU. Over a
folder it prints a line per document, naming its kind and name. For the folder above (its welllog pre flows aside):

```text
$ sqlflow validate welldb
OK      cache\osdu-reference-00-cache.yaml  (cache 'osdu-reference-00-cache')
OK      cache\welldb-lookups-00-cache.yaml  (cache 'welldb-lookups-00-cache')
OK      dictionaries\curve-aliases.yaml  (dictionary 'curve-aliases (4 entries, key key)')
OK      dictionaries\sampling-domain.yaml  (dictionary 'sampling-domain (5 entries, key key)')
OK      flows\welldb-06-inventory.yaml  (inventory 'welldb-06-inventory')
OK      flows\welldb-retrieval-01-wellbores.yaml  (retrieval 'welldb-retrieval-01-wellbores')
OK      flows\welldb-wellbore-01-pre.yaml  (file 'welldb-wellbore-01-pre')
OK      flows\welldb-wellbore-02-ing.yaml  (ing 'welldb-wellbore-02-ing')
OK      flows\welldb-wellbore-03-delivery.yaml  (delivery 'welldb-wellbore-03-delivery')
OK      flows\welldb-welllog-02-curves-ing.yaml  (ing 'welldb-welllog-02-curves-ing')
OK      flows\welldb-welllog-02-header-ing.yaml  (ing 'welldb-welllog-02-header-ing')
OK      flows\welldb-welllog-03-delivery.yaml  (delivery 'welldb-welllog-03-delivery')
OK      flows\welldb-welllog-04-assertion.yaml  (assertion 'welldb-welllog-04-assertion')
OK      flows\welldb-welllog-05-dimensions.yaml  (dimension 'welldb-welllog-05-dimensions')
OK      mappings\Wellbore@1.0.0.yaml  (mapping 'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0')
OK      mappings\WellLog@1.0.0.yaml  (mapping 'WellLog@1.0.0 -> osdu:wks:work-product-component--WellLog:1.4.0')
16 valid, 0 broken of 16 document(s) under <folder>.
```

One file prints one line in SQLFlow's single-file form, which names what the document reads and writes:

```text
OK  'welldb-wellbore-03-delivery' is valid (delivery: OsduData.silver.Wellbore -> ${env:OSDU_URL}).
OK  'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0' is valid (mapping).
```

Validation also reads the files a flow names beside it, from disk and the way a run finds them: the mapping a delivery
flow pins, and the dictionaries a cache or dimension flow names. A flow pinning a mapping that is not there is refused:

```text
ERROR  flows/welldb-wellbore-03-delivery.yaml: render.mapping: Mapping 'Wellbore@9.9.9' was not found under '../mappings'. Expected one of: Wellbore@9.9.9.yaml, Wellbore@9.9.9.yml, 9.9.9.yaml, 9.9.9.yml.
```

It does not read the template the mapping fills (a file of the template folders on its path) or the cache it reads (in
the module's database):
`sqlflow check` is the preflight that reads all of them together ([check](../cli/check.md),
[what validate checks](../cli/validate.md)).

## See also

- [SQLFlow's flow overview](../../../../sqlflow/docs/reference/flow/overview.md): the built-in kinds and the envelope.
- [What OSDU Delivery is](../concepts/overview.md) and [its architecture](../concepts/architecture.md).
- [Delivery flow](delivery.md), [mapping](mapping.md), [cache flow](cache.md), [dictionary](dictionary.md).
- [Your first delivery, end to end](../guides/getting-started.md).
- [Running an OSDU flow](../cli/run.md) and [what validate checks](../cli/validate.md).
