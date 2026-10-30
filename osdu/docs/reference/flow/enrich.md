---
id: delivery-flow-enrich
title: "Enrichments (enrich:): what a mapping adds to each record it renders, bound by the flow and kept under the record"
type: flow-reference
summary: "A mapping's enrich block: paths, records of any kind and bulk data added to each record from rows of their own, sent once per change, traced in the ledger."
keywords:
  - enrich
  - enrichment
  - enrichment mapping
  - enhance a record
  - extend an existing record
  - add to a record after it is delivered
  - add a record to a record
  - chained rendering
  - one change one write
  - rows one
  - rows many
  - after
  - $link
  - $base
  - multi-step record
  - Enrichments tab
  - records enrichments
yamlPath: "enrich (documentType: mapping); source.datasets.<name> and workflows.<name> (flowType: delivery)"
related:
  - delivery-flow-enrich-records
  - delivery-flow-enrich-holds
  - delivery-flow-enrich-bulk
  - delivery-flow-enrich-workflow
  - delivery-flow-mapping-enrichment
  - delivery-flow-mapping
  - delivery-flow-delivery
  - delivery-flow-interfaces
  - delivery-concept-change-detection
  - delivery-concept-preflight
  - delivery-concept-lineage
  - delivery-concept-ledger
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Enrich.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichTrace.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordEnrichments.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreview.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryArtifactVerbs.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryArtifactEndpoints.cs
  - osdu/docs/census/keys.mapping.json
  - osdu/docs/census/keys.delivery.json
---

# Enrichments (enrich:): what a mapping adds to each record it renders, bound by the flow and kept under the record

A record OSDU holds is often built from more than one source. A well log's header and curves come from one table, its LAS
file from a file register, a quality grade from a third table, each kept by someone else and changed at its own time. The
record is still one OSDU record with one writer. An **enrichment** is how its mapping adds what those other sources hold:
paths of the record itself, records of their own that the record links (a dataset per LAS file, a record of a custom kind),
or the record's DDMS bulk data. The mapping says what each enrichment adds and where the record links it; the flow says
where its rows and files are. The record with every enrichment in it is hashed and sent once, and the ledger keeps every
enrichment under the record it belongs to.

A mapping without `enrich:` renders, hashes and delivers exactly as it always did.

## An example

The well logs of the well database, delivered as WellLog 1.4.0. The mapping adds each log's LAS file as a dataset of its own
and a quality grade to the log itself:

```yaml
documentType: mapping
name: WellLog
version: 1.4.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
dataset:
  system: welldb
  key: [source_project, log_id]

enrich:
  lasfile:
    description: The log's original LAS file, registered as a dataset of the log.
    rows: one
    mapping: WellLogLasDataset@1.0.0     # of a dataset kind: adds a record per row
  quality:
    description: The quality grade the log office assigns.
    rows: one
    mapping: WellLogGrade@1.0.0          # of the log's own kind: adds paths to the log
    onHold: step

# parameters (dataPartition among them), record.acl and record.legal as the mapping page shows them
record:
  data:
    Name: { $from: log_name }
    Datasets:
      - $link: lasfile                   # the id of the dataset lasfile registers
```

The flow binds each enrichment's rows, as a child dataset named like it, and the files a row names:

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.WellLog
    key: [source_project, log_id]
  datasets:
    lasfile:
      object: OsduData.silver.WellLogFile
      join: { source_project: source_project, log_id: log_id }
      files:
        root: ../data/las
        locationColumn: las_folder
        pattern: "*.las"
        hashColumn: las_hash
        contentType: text/plain
    quality:
      object: OsduData.silver.WellLogQuality
      join: { source_project: source_project, log_id: log_id }
  work: ../.work/welllog
render:
  mapping: WellLog@1.4.0
# target as the delivery flow page shows it
```

`WellLogGrade@1.0.0` and `WellLogLasDataset@1.0.0` are enrichment mappings ([enrichment mappings](mapping-enrichment.md)):
mappings whose header says `enrichment: true`, keying no record of their own.

## The keys

`enrich` is a block of the mapping that keys the records, a map of enrichments by name:

| Key | Type | Required | Meaning |
| --- | --- | --- | --- |
| `enrich.<name>` | map | | One enrichment. The name is letters, digits and `_`, not starting with a digit, at most 32 characters, not `record`, compared ignoring case. At most 16. |
| `enrich.<name>.description` | string | no | What it adds, shown with the record's composition and on its Enrichments tab. |
| `enrich.<name>.rows` | `one` or `many` | no | How many rows of the child dataset named after it one record takes. Left out, it reads no rows. |
| `enrich.<name>.mapping` | string | unless `bulk` | The enrichment mapping it renders, `Name@version`, found the way the flow's mapping is. Its kind says what the enrichment adds: the record's own kind adds paths, any other kind adds a record per row. |
| `enrich.<name>.key` | list | with records from `rows: many` | The columns telling each row's record from the others' ([records an enrichment adds](enrich-records.md#the-ids)). |
| `enrich.<name>.after` | list | no | The enrichments this one renders after, beside the order the document lists them in. |
| `enrich.<name>.onHold` | `record` or `step` | no | `record` (the default) holds the record when the enrichment holds; `step` sets it aside ([an enrichment set aside on its hold](enrich-holds.md)). |
| `enrich.<name>.bulk` | boolean | no | `true` when the files its one row names are the record's DDMS bulk data ([an enrichment's bulk data](enrich-bulk.md)). |

The flow binds what the mapping names. An enrichment with `rows` reads the child dataset of its own name
(`source.datasets.<name>`, or `interfaces.<name>.datasets.<name>`), whose `files` say where each row's files are; a workflow
run over an enrichment's records is `workflows.<name>` ([an enrichment's workflow](enrich-workflow.md)). `render.parameters`
supplies every mapping of the chain: each takes the values it declares, and a value none of them declares is refused.

A flow `enrich:` key is refused, saying where its parts go now: the enrichment to the mapping, its rows and files to
`source.datasets`, its workflow to `workflows`.

## What an enrichment adds

| Its mapping fills | It adds | Written | Linked |
| --- | --- | --- | --- |
| The record's own kind and template version | Paths of the record: values where the record holds none, items to lists | With the record | Part of the record |
| A dataset kind whose rows the flow binds `files` for | A dataset per row, registered with its files | Through the Dataset service, before the record | Where the mapping writes `$link: <name>` |
| Any other kind, a dataset without files among them | A record per row, as rendered | Through storage, before the record | Where the mapping writes `$link: <name>` |
| Nothing (`bulk: true` alone) | The record's DDMS bulk data | By the `ddms` route, after the record | Part of the record |

An enrichment that adds records and reads no rows adds one for the record. Every added record carries the record's access
list and legal block, so whoever may read the record may read what it adds, and is written under an id made from the record's
own ([records an enrichment adds](enrich-records.md)).

## How a record renders

For each record the plan reads:

1. The mapping renders the record. A `$link` writes the ids of the records the enrichment it names adds for the record's
   rows, made from the record's own id, so the record links them before anything is sent.
2. Each enrichment, in order, renders its enrichment mapping. Its rows are those of its child dataset that join the record:
   under `rows: one`, no row adds nothing and two or more hold the record; under `rows: many`, its mapping reads them with
   `$forEach: <name>`. It reads the record as the mapping and the enrichments before it left it with `$base`.
3. An enrichment adding paths adds them to the record
   ([how an enrichment adds to the record](mapping-enrichment.md#how-an-enrichment-adds-to-the-record)); one adding records
   renders each, beside the record.

The record with every enrichment in it is normalized, written as canonical JSON and hashed, taken with the hash of every
record its enrichments add: that hash is what change detection compares ([change detection](../concepts/change-detection.md)).
The order is the order the mapping lists the enrichments in, each moved after every one its `after:` names; a name no
enrichment has, one naming itself, a name given twice, and enrichments waiting for each other are refused when the mapping
loads.

A record the mapping holds is held as it always was. An enrichment that holds, or would change what the record holds, holds
the record with its reasons named `enrich.<name>: ...`, and the enrichments after it do not render, so every reason given is
a true one; one with `onHold: step` is set aside instead ([an enrichment set aside on its hold](enrich-holds.md)), and the
record links the records its last delivery added.

## One change, one write

An enrichment's rows are a child dataset of the flow's source, so a change to them is a change to the record:

- **Its rows changed, arrived or went.** The record is a candidate of the run's window and its ingestion fingerprint moved;
  the whole chain renders again and the record goes once.
- **The record's own row changed.** The chain renders again over the new row, every enrichment in the one write.
- **A mapping was edited, or an enrichment added, removed or reordered.** The render context names every enrichment's
  mapping, its fingerprint, template version, parameters and rules, so every record renders again.
- **A cache value a mapping of the chain read changed.** The record's cache usages are every mapping's, so the cache rollout
  reaches it; a pipeline's `cache.before: auto` brings up to date what every one of them reads ([cache](delivery.md#cache)).
- **Only a record an enrichment adds changed** (its row or its files). That record is written again under its id, and the
  record, which links it already, is not ([what a change sends](enrich-records.md#what-a-change-sends)).
- **The rows changed but nothing renders differently.** Nothing is sent.

So a record is written at most once for each change, whichever part of its chain moved.

## What the ledger keeps

Everything an enrichment did is kept with the record it belongs to, in the record's own ledger row, so it goes wherever the
record goes (a move, a reversal, a purge):

- **What rendered it.** The record's render context names each enrichment, its mapping, fingerprint and parameters, and for
  one adding records their kind, key and how they are written.
- **Where its rows came from.** The record's target state keeps, for each enrichment reading rows, how many rows the
  delivered document read and the ingestion files they came from (`enrich.source.<name>`); and for each record it adds, the
  file and row it was rendered from (`enrich.<id>.source`).
- **What OSDU holds of each record it adds.** Its id among the record's links (`enrich.records`), the hash of what was
  written (`enrich.<id>`), of its files (`enrich.<id>.files`) and the version that landed (`enrich.<id>.version`). Every
  write of it is a step of the record's [attempt](../tables/attempt.md) that made it, with the version it landed as and the
  one it replaced, so a record no longer linked stays listed with the writes made of it. A write landed whatever the
  record's own write did after it, so a try that held lists its writes too; a try that resumed one does not list it again.
- **What was set aside.** The record's `HeldSteps`.
- **How to find it.** Each added record's id is one of the record's identity values (kind `added`), so the lookup across
  flows finds the record from the id of a dataset it added ([the ledger](../concepts/ledger.md#finding-records)).

A record's **Enrichments** tab, `sqlflow records enrichments <flow.yaml> --key <key>` and
`GET /api/v1/delivery/records/{flowId}/{key}/enrichments` show it: each enrichment with what it adds, its mapping and where
its rows came from, then every record it added with whether the record links it, its version, its row and every write made
of it. Nothing there is read from OSDU.

## What the preflight checks

When the mapping loads, and before anything renders on every run and `sqlflow check`:

- an enrichment names an enrichment mapping (`enrichment: true`), and one of the record's kind fills the same template
  version as the mapping;
- the flow binds the child dataset of every enrichment reading rows, binds `files` only for an enrichment that registers
  them or names bulk data, and names `workflows` only for enrichments the mapping declares;
- the mapping does not repeat an enrichment's rows with `$forEach` itself: they are the enrichment's;
- a `$link` names an enrichment adding records, sits in a list the flow preserves only as the list itself, and writes one
  value only for an enrichment taking one row;
- an enrichment writes no path the mapping or another enrichment writes, unless both add items to one list, and nothing under
  a key the flow preserves;
- an enrichment reads with `$base` only what the mapping or an enrichment before it writes, and only the columns of its rows;
- `render.parameters` supplies no value no mapping of the chain declares;
- an enrichment is refused on the `dspdm` and `etp` routes, and one adding records on a `ddms` flow whose endpoint is the DDMS
  itself.

`sqlflow validate` finds every enrichment mapping beside the flow's mapping and binds the flow to them.

The operations that write nothing an enrichment adds (verify, delete, release, redeliver) read only the mapping document to bind
the flow, and go ahead when it does not read. A drain sends what the enrichments add with each record, so it refuses then:
`... the pending work is not sent, since it is written with what the mapping <Name@version> adds to each record through its
enrichments, and the flow could not be bound to them: ...`.

## Where it shows

- `sqlflow check` lists each enrichment in the order it renders: `enrich      lasfile: records of osdu:wks:dataset--File.Generic:1.0.0 (WellLogLasDataset@1.0.0), registered with their files through the Dataset service, one row of OsduData.silver.WellLogFile`.
- `sqlflow preview`, the flow's Preview tab and a record's Render tab show what each enrichment did: each path it set, each
  item it added, each record it adds with its id and row, or why it held the record (`document.enrichment`).
- A record's **Enrichments** tab shows what the ledger keeps of them.
- Lineage reads each enrichment mapping as a mapping node of its own, reading what it reads and writing the kind of the
  records it adds ([lineage](../concepts/lineage.md)).

The mapping builder lays out a mapping without enrichments; a mapping declaring `enrich:` is edited in its file.

## Related

- [Records an enrichment adds](enrich-records.md): datasets and records of any kind, their ids, `$link`, and what a change sends.
- [An enrichment set aside on its hold](enrich-holds.md): one that holds without holding the record.
- [An enrichment's bulk data](enrich-bulk.md): a well log's curves named by a table of their own.
- [An enrichment's workflow](enrich-workflow.md): an OSDU workflow run over the record and what an enrichment added.
- [Enrichment mappings](mapping-enrichment.md): `enrichment: true`, `$base`, and how paths are added to the record.
- [Delivery flow](delivery.md): the source, its child datasets and the target the record goes to.
- [The ledger](../concepts/ledger.md): what is kept of every record.
