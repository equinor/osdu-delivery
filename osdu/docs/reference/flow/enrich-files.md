---
id: delivery-flow-enrich-files
title: "Files a step registers (enrich.<name>.files): datasets a step adds to each record, under ids kept for good"
type: flow-reference
summary: "A step's files: one dataset per row, registered under an id made from the record's, linked from the record, sent again only when its files move."
keywords:
  - enrich files
  - step files
  - add a dataset to a record
  - add a file to an existing record
  - LAS file of a well log
  - dataset of a well log
  - derived dataset id
  - preserved ids
  - files.link
  - files.key
  - files.mapping
  - data.Datasets
  - registerDataset
  - one change one write
  - preserveDataKeys
  - preserved Datasets merge
  - cache after step datasets
yamlPath: "enrich.<name>.files, interfaces.<name>.enrich.<name>.files"
related:
  - delivery-flow-enrich
  - delivery-flow-mapping-fragment
  - delivery-flow-routes
  - delivery-concept-artifacts-and-undo
  - delivery-concept-change-detection
  - delivery-concept-protocols
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichParts.cs
  - osdu/src/SqlFlow.Delivery/Validation/PayloadLocations.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/StepLinks.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheSteps.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/src/SqlFlow.Delivery/Storage/WorkBatchFile.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepFilesDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/EnrichPartsProtocolTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepLinksTests.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/specs/core/dataset/openapi.yaml
---

# Files a step registers (enrich.<name>.files): datasets a step adds to each record, under ids kept for good

A well log goes to OSDU through the Wellbore DDMS with its curves. Later its original LAS file turns up, kept in another
table by someone else. The file belongs to the same OSDU record: a dataset the log lists in `data.Datasets`, not a new log.
A step of [enrich](enrich.md) with `files:` does that. For each of the step's rows, the files the row names are uploaded
through the Dataset service and registered as one dataset record, under an id made from the record's own id and the step,
and the record links it. The id never changes: new files register a new version of the same dataset, and the record, which
names it already, is not written again.

## An example

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
# source and render as the delivery flow page shows them
target:
  endpoint: ${env:OSDU_URL}
  protocol: ddms
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms

enrich:
  lasfile:
    description: The log's original LAS file, registered as a dataset of the log.
    dataset:
      object: OsduData.silver.WellLogFile
      join: { source_project: source_project, log_id: log_id }
    files:
      root: ../data/las
      locationColumn: las_folder
      pattern: "*.las"
      hashColumn: las_hash
      contentType: text/plain
      mapping: WellLogLasDataset@1.0.0
```

The first delivery registers `dev:dataset--File.Generic:<log>-enrich.lasfile` and writes the log with
`data.Datasets: ["dev:dataset--File.Generic:<log>-enrich.lasfile:"]`, after the datasets it lists already. A corrected LAS
file registers version 2 of that dataset and leaves the log as OSDU holds it.

## The keys

| Key | Default | Meaning |
| --- | --- | --- |
| `files.root` | required | The folder the rows' file folders are under, relative to the flow's file or a storage location, as a payload set's root is. |
| `files.locationColumn` | required | The step table's column naming each row's folder under `root`. |
| `files.pattern` | `*` | The files of a row's folder that are its files. |
| `files.hashColumn` | required | The column holding the content hash of each row's files: what says they changed. |
| `files.kind` | `osdu:wks:dataset--File.Generic:1.0.0` | The dataset kind they are registered as. A `dataset--File.*` kind takes one file, a `dataset--FileCollection.*` kind several. A kind the Dataset service does not register, and an External Data Services proxy dataset, are refused. |
| `files.key` | none | The columns telling each row's dataset from the step's other rows': required under `rows: many`, refused under `rows: one`. |
| `files.mapping` | none | A fragment over the dataset kind, `Name@version`, rendered from the row into each dataset record (its name, description, tags); it may read the record with `$base`. Left out, the dataset record holds its file properties and the name of its first file. |
| `files.link` | `data.Datasets` | The record's list the datasets are linked from, each as its id with a trailing colon. |
| `files.contentType` | the flow's `filesContentType`, else `application/octet-stream` | The media type the files are uploaded as. |

A step with `files` declares a `dataset`, since its rows name the files, and may declare a `mapping` beside them; it needs
neither one alone. The endpoint is the platform, where the Dataset service is: a `ddms` flow whose endpoint is the DDMS
itself (no `ddmsRoot`, no `target.ddms`) is refused when the document loads.

## The ids

Each dataset's id is the record's with the dataset's entity type and a suffix: `enrich.<step>` under `rows: one`, and
`enrich.<step>.<hash of the row's files.key values>` under `rows: many`. The id is known when the record renders, before
anything is sent, so the record links it from its first delivery, and it is the same on every render whatever the files
hold. The suffix holds a dot, which neither the dataset a record's own files go in (`files`, on the dataset route) nor a
workflow input's name can hold, so no route derives the same id for something else.

Two rows naming the same `files.key` values, and a row whose key column is empty, hold the record
(`enrich.lasfile: rows 1 and 2 of the step's dataset name the same dataset ...`).

## What a change sends

The plan reads where each row's files are and their hash, and the record's document hash takes in every dataset's (the
document's own when it has none), so a change to the files plans the record again though its document is the same. What
the delivery then sends:

| What changed | What is sent |
| --- | --- |
| A row's files (its hash) or what `files.mapping` renders into its dataset | That dataset, registered again under its id as a new version; the record is not written. |
| The record's own row, or a fragment's | The record, once; a dataset whose hash did not move is not registered. |
| A row arrived | Its dataset, then the record with the new link. |
| A row went | The record without its link. The dataset stays live, since the record's earlier versions name it, and the ledger marks it superseded. |
| Nothing | Nothing. |
| A redelivery of the record or its metadata, or a record never delivered | The record and every dataset. |
| A redelivery of the payload naming `files`, on a flow whose route sends a payload | Every dataset, with what the route sends of the record's own payload. |
| OSDU lost the record, and its repair asked for it again | The record, brought back under its id, and every dataset. |

So one change sends at most one write of each thing it touched: the record when its document moved, a dataset when its
files did.

## How a delivery goes

The flow's own route is wrapped (`EnrichPartsProtocol`): for each record, each dataset due is sent first, then the record.

1. Its files go where the Dataset service says for its kind (`storage-enrich-<n>`, `upload-enrich-<n>-<i>` in the
   record's steps).
2. Its registration is declared before it is sent (`enrich-register-intent-<n>`), with whether storage holds the id, so an
   answer lost after it landed is found again or undone, never registered twice blind.
3. The datasets are registered, twenty to a request (`enrich-register-<n>`); a request refused as a whole is tried record by
   record, so one bad dataset fails its record alone.
4. Each is checked to answer retrieval instructions: one that does not fails its record.
5. The record goes through the flow's route as it always does, unless the plan found its document the one OSDU holds.

A record whose dataset fails is not written, and its try is retried as any is; a retry resumes after the registrations that
landed. A delivery that does not complete is undone at the Dataset service's reversible scope: a dataset the try created is
removed through its soft delete once storage confirms OSDU created it after the try began, and one it wrote a new version
of is given back the version before ([artifacts and undo](../concepts/artifacts-and-undo.md)). The files a registration
staged stay in the platform's storage, which no public OSDU operation deletes.

The record's target state keeps, for each dataset, `enrich.<id>` (the hash of its record and files) and `enrich.<id>.files`
(its files' hash), `enrich.datasets` (the ids the record links) and `enrich.document` (the hash of the document last
written).

## What holds a record

- A row whose `locationColumn` is empty, or names a folder that climbs out with `..` or lies outside the flow's roots.
- A row whose `hashColumn` is empty.
- Two rows with the same `files.key` values, or an empty key column.
- A record with no `acl` or `legal` to copy onto its datasets.

## Removing a record

A removal at the `record` scope takes the record out and leaves its datasets, which its versions name and a restored record
finds again. `history` removes the record's history alone. `everything` purges the record and every dataset its steps
registered.

## Preserved keys

Other systems list datasets of a record too, so a flow may carry the link's list over from the record OSDU holds
(`target.protocolOptions.preserveDataKeys: [Datasets]`, as a well log flow over the Wellbore DDMS does). An update then
merges the list rather than copying it whole:

- OSDU's items stay, in OSDU's order, other systems' among them;
- the step's links the record renders now are added after them, each once;
- the step's links the last delivery wrote that the record no longer renders (a row that went) are taken off.

Every route that carries preserved keys merges the same way. A record OSDU holds without the list gets the one rendered,
and a list the steps link nothing in, now or before, is copied whole as before. What the flow's mapping renders in a
preserved list besides the steps' links reaches the record only when it is created, as before. Links are compared as the
record holds them, the dataset's id and a colon. A verify leaves preserved keys out of the content it compares
(`ownedContent.excluded`, [routes](routes.md#storage)), so it does not report a link another system took off.

## The cache

A pipeline's cache steps ([cache](delivery.md#cache)) take a step's datasets as they take its records. `cache.after` may
name a type holding the kind the step registers (`dataset--File.Generic`), and `after: auto` takes such a type when a cache
flow declares it without a query. As the run ends, the datasets the records it changed link (`enrich.datasets` in their
target state) are read back from storage with the records, and each goes into the types holding its own kind: the logs
into a type of logs, their datasets into a type of datasets. A dataset a record stopped linking keeps its place, since OSDU
still holds it. A flow later in the schedule can then find the datasets in the cache before the next refresh.

A run has one before step and one after step, never a refresh between two steps of `enrich:`: every step renders in one
pass before anything is sent, and the ids a step registers are made from the record's, so the chain needs nothing from the
cache that it does not render itself. `cache.before: auto` takes what every fragment reads, a step's own and its dataset
fragment's, as it takes what the flow's mapping reads.

## What the preflight checks

`files.link` names a list of values the template declares (`data.Datasets` on a work product component), or a path in an
object the template leaves open; a list of objects, a single value or a path the template lacks is refused. Every writer
of the link's list, the flow's mapping and every step, adds items to it, so they share it. A step may read with `$base`
the links an earlier step added. `files.mapping` is checked as every fragment is, and its `$base` reads count as the step's.

The link may be a list the flow preserves (`data.Datasets` with `Datasets` in `preserveDataKeys`), whose links an update
merges ([Preserved keys](#preserved-keys)). A link inside a preserved key (`data.ExtensionProperties.Files` with
`ExtensionProperties` preserved) is refused, since only the list itself merges, and so is a fragment writing under a
preserved key.

## Where it shows

- `sqlflow check` lists the step: `enrich      lasfile: files as osdu:wks:dataset--File.Generic:1.0.0, linked from data.Datasets`.
- `sqlflow preview` and the Preview and Render tabs list each dataset a step registers with its id and row
  (`document.enrichment[].datasets`).
- Lineage reads the step's files where its rows say and writes its dataset kind ([lineage](../concepts/lineage.md)).

## Related

- [Enrich](enrich.md): the steps, their rows and how the chain renders.
- [Fragments](mapping-fragment.md): what `files.mapping` and a step's `mapping` are.
- [Routes](routes.md): the route a flow's records go by, which the step's datasets go before.
- [Artifacts and undo](../concepts/artifacts-and-undo.md): what an unfinished delivery leaves and how it is undone.
