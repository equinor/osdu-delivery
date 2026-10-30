---
id: delivery-flow-enrich-records
title: "Records an enrichment adds: datasets with their files and records of any kind, under ids kept for good and linked with $link"
type: flow-reference
summary: "An enrichment of another kind adds a record per row: a dataset with its files, or any record via storage, under an id kept for good and linked with $link."
keywords:
  - enrichment records
  - add a dataset to a record
  - add a record to a record
  - add a file to an existing record
  - LAS file of a well log
  - dataset of a well log
  - record of a custom kind
  - derived dataset id
  - $link
  - enrich.key
  - source.datasets.files
  - data.Datasets
  - registerDataset
  - storage records
  - one change one write
  - preserveDataKeys
  - preserved Datasets merge
  - cache after enrichment records
yamlPath: "enrich.<name>.mapping and enrich.<name>.key (documentType: mapping); source.datasets.<name>.files (flowType: delivery)"
related:
  - delivery-flow-enrich
  - delivery-flow-mapping-enrichment
  - delivery-flow-mapping-values
  - delivery-flow-routes
  - delivery-concept-artifacts-and-undo
  - delivery-concept-change-detection
  - delivery-concept-protocols
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichLinks.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichParts.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Validation/PayloadLocations.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/StepLinks.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheSteps.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/src/SqlFlow.Delivery/Storage/WorkBatchFile.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/docs/census/keys.mapping.json
  - osdu/specs/core/dataset/openapi.yaml
---

# Records an enrichment adds: datasets with their files and records of any kind, under ids kept for good and linked with $link

A well log goes to OSDU through the Wellbore DDMS with its curves. Later its original LAS file turns up, kept in another
table by someone else. The file belongs to the same OSDU record: a dataset the log lists in `data.Datasets`, not a new log.
Another log has a detail record of a custom kind that stands beside it. An **enrichment** whose mapping fills another kind
than the record's adds such records: one for each of its rows, under an id made from the record's own id and the
enrichment, linked from the record wherever its mapping writes `$link`. The id never changes: a changed row writes a new
version of the same record, and the record, which links it already, is not written again.

## An example

The mapping declares the enrichment and links what it adds:

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
    rows: one
    mapping: WellLogLasDataset@1.0.0
# parameters, record.acl and record.legal as the mapping page shows them
record:
  data:
    Datasets:
      - $link: lasfile
```

The enrichment mapping renders each dataset record:

```yaml
documentType: mapping
name: WellLogLasDataset
version: 1.0.0
template:
  kind: osdu:wks:dataset--File.Generic:1.0.0
enrichment: true
record:
  data:
    Name: { $from: las_file_name }
  tags:
    SourceProject: { $from: source_project }
```

The flow binds the rows and the files each names:

```yaml
source:
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
```

The first delivery registers `dev:dataset--File.Generic:<log>-enrich.lasfile` and writes the log with
`data.Datasets: ["dev:dataset--File.Generic:<log>-enrich.lasfile:"]`. A corrected LAS file registers version 2 of that
dataset and leaves the log as OSDU holds it.

## How a record is written

| The enrichment mapping fills | Its rows' files | The record is |
| --- | --- | --- |
| A dataset kind the Dataset service registers | Bound (`source.datasets.<name>.files`) | Uploaded with its files and registered through the Dataset service, and checked to answer retrieval instructions |
| Any other kind (an External Data Services proxy dataset, a custom kind, a dataset written as rendered) | None | Written through storage (`PUT /records`) as its mapping rendered it |

Either way the record is what the enrichment mapping rendered from the row, with the id the record's id and the enrichment
make, the enrichment mapping's kind, and the record's own access list and legal block: whoever may read the record may read
what it adds. A dataset whose mapping names no `data.Name` takes the name of its first file. An enrichment adding records
that reads no rows adds one record for the record.

## The keys

| Key | Where | Meaning |
| --- | --- | --- |
| `enrich.<name>.mapping` | the mapping | The enrichment mapping, `Name@version`, `enrichment: true`, filling the kind of the records. |
| `enrich.<name>.rows` | the mapping | `one`: a record for the record's one row; `many`: one for each row; left out: one for the record. |
| `enrich.<name>.key` | the mapping | The columns telling each row's record from the others': required under `rows: many`, refused otherwise. |
| `source.datasets.<name>.files.root` | the flow | The folder the rows' file folders are under, relative to the flow's file or a storage location, as a payload set's root is. |
| `source.datasets.<name>.files.locationColumn` | the flow | The column naming each row's folder under `root`. |
| `source.datasets.<name>.files.pattern` | the flow | The files of a row's folder that are its files; `*` when left out. A `dataset--File.*` kind takes one file, a `dataset--FileCollection.*` kind several. |
| `source.datasets.<name>.files.hashColumn` | the flow | The column holding the content hash of each row's files: what says they changed. Required. |
| `source.datasets.<name>.files.contentType` | the flow | The media type the files are uploaded as; the flow's `filesContentType`, else `application/octet-stream`. |

Files bound for a kind the Dataset service does not register are refused, and so are files bound on a dataset no
enrichment reads. The endpoint is the platform, where the Dataset and storage services are: a `ddms` flow whose endpoint is
the DDMS itself (no `ddmsRoot`, no `target.ddms`) is refused when it is bound to the mapping.

## The ids

Each record's id is the enriched record's with the added record's entity type and a suffix: `enrich.<name>` under
`rows: one` or no rows, and `enrich.<name>.<hash of the row's key values>` under `rows: many`. The id is known when the
record renders, before anything is sent, so the record links it from its first delivery, and it is the same on every render
whatever the row holds. The suffix holds a dot, which neither the dataset a record's own files go in (`files`, on the
dataset route) nor a workflow input's name can hold, so no route derives the same id for something else.

Two rows naming the same `key` values, and a row whose key column is empty, hold the record
(`enrich.lasfile: rows 1 and 2 of the enrichment's dataset name the same record ...`).

## Linking with $link

`$link: <name>` is a value node of the mapping that keys the record ([mapping values](mapping-values.md#link)): it writes
the ids of the records the enrichment adds for the record, each as OSDU names a reference, with the version separator and no
version.

- As an item of a list of values it gives every one of them, after the literal items before it:
  `Datasets: [ "{$param.dataPartition}:dataset--File.Generic:shared-legend:", { $link: lasfile } ]`.
- As a value of its own it gives the one record an enrichment taking one row adds: `ExtensionProperties: { Las: { $link: lasfile } }`.
- It never holds the record: an enrichment adding nothing for it gives nothing, and the property is left out. It takes
  `$when` and `$description`, and is written for the record as a whole: never under a `$forEach`, in an item of a list of
  objects, in a `$coalesce`, or in an enrichment mapping.

An enrichment set aside on its hold adds nothing new, so the record links what its last delivery added instead
([an enrichment set aside on its hold](enrich-holds.md)). A mapping may also link none of what an enrichment adds: the
records stand beside the record, traced from it in the ledger all the same.

## What a change sends

The plan reads where each row's files are and their hash, and the record's document hash takes in every added record's (the
document's own when it has none), so a change to an added record plans the record again though its document is the same.

| What changed | What is sent |
| --- | --- |
| A row's files (its hash), or what the enrichment mapping renders from it | That record, written again under its id as a new version; the enriched record is not written. |
| The record's own row, or an enrichment adding paths | The record, once; an added record whose hash did not move is not written. |
| A row arrived | Its record, then the enriched record with the new link. |
| A row went | The enriched record without its link. The added record stays live, since the record's earlier versions name it, and the ledger lists it as no longer linked. |
| Nothing | Nothing. |
| A redelivery of the record or its metadata, or a record never delivered | The record and every record its enrichments add. |
| A redelivery of the payload naming `files`, on a flow whose route sends a payload | Every added record, with what the route sends of the record's own payload. |
| OSDU lost the record, and its repair asked for it again | The record, brought back under its id, and every added record. |

So one change sends at most one write of each thing it touched.

## How a delivery goes

The flow's own route is wrapped (`EnrichPartsProtocol`): for each record, each added record due is written first, then the
record.

1. A dataset's files go where the Dataset service says for its kind (`storage-enrich-<n>`, `upload-enrich-<n>-<i>` in the
   record's steps).
2. Each write is declared before it is sent (`enrich-register-intent-<n>`), with whether storage holds the id, so an answer
   lost after it landed is found again or undone, never written twice blind.
3. The records are written, twenty at a time: datasets registered through the Dataset service in one request, each checked
   to answer retrieval instructions; every other record written through storage one by one (`enrich-register-<n>`, with
   the version OSDU answered). A registration refused as a whole is tried record by record, so one bad record fails its
   enriched record alone.
4. The record goes through the flow's route as it always does, unless the plan found its document the one OSDU holds.

A record whose added record fails is not written, and its try is retried as any is; a retry resumes after the writes that
landed. A delivery that does not complete is undone at the reversible scope: an added record the try created is removed
once storage confirms OSDU created it after the try began (a dataset through the Dataset service's soft delete, any other
record through storage's), and one it wrote a new version of is given back the version before
([artifacts and undo](../concepts/artifacts-and-undo.md)). The files a registration staged stay in the platform's storage,
which no public OSDU operation deletes.

## What holds a record

- A row whose files `locationColumn` is empty, or names a folder that climbs out with `..` or lies outside the flow's roots.
- A row whose files `hashColumn` is empty.
- Two rows with the same `key` values, or an empty key column.
- A record with no `acl` or `legal` to copy onto what its enrichments add.
- What the enrichment mapping holds the row for, named `the record of row <n>: ...`.

## Removing a record

A removal at the `record` scope takes the record out and leaves what its enrichments added, which its versions name and a
restored record finds again. `history` removes the record's history alone. `everything` purges the record and every record its
enrichments added.

## Preserved keys

Other systems list datasets of a record too, so a flow may carry the list over from the record OSDU holds
(`target.protocolOptions.preserveDataKeys: [Datasets]`, as a well log flow over the Wellbore DDMS does). An update then merges
the list rather than copying it whole:

- OSDU's items stay, in OSDU's order, other systems' among them;
- the records the enrichments add now are added after them, each once;
- those the last delivery linked that the record no longer adds (a row that went) are taken off.

Every route that carries preserved keys merges the same way. A record OSDU holds without the list gets the one rendered, and a
list the enrichments link nothing in, now or before, is copied whole as before. What else the mapping renders in a preserved
list reaches the record only when it is created. A `$link` inside a preserved key (`data.ExtensionProperties.Files` with
`ExtensionProperties` preserved) is refused, since only the list itself merges, and so is an enrichment mapping writing paths
under a preserved key. A verify leaves preserved keys out of the content it compares (`ownedContent.excluded`,
[routes](routes.md#storage)), so it does not report a link another system took off.

## The cache

A pipeline's cache steps ([cache](delivery.md#cache)) take what enrichments add as they take its records. `cache.after` may
name a type holding the kind an enrichment adds, and `after: auto` takes such a type when a cache flow declares it without a
query. As the run ends, the records the changed records link (`enrich.records` in their target state) are read back from
storage with them, and each goes into the types holding its own kind. A record no longer linked keeps its place, since OSDU
still holds it.

A run has one before step and one after step, never a refresh between two enrichments: every enrichment renders in one pass
before anything is sent, and the ids they add are made from the record's, so the chain needs nothing from the cache that it
does not render itself. `cache.before: auto` takes what every enrichment mapping reads.

## Where it shows

- `sqlflow check` lists the enrichment: `enrich      lasfile: records of osdu:wks:dataset--File.Generic:1.0.0 (WellLogLasDataset@1.0.0), registered with their files through the Dataset service, one row of OsduData.silver.WellLogFile`.
- `sqlflow preview` and the Preview and Render tabs list each record an enrichment adds with its id and row
  (`document.enrichment[].records`).
- A record's **Enrichments** tab lists every record its enrichments added, with whether it links it, its version, its row and
  every write made of it ([enrich](enrich.md#what-the-ledger-keeps)); searching for one of their ids finds the record.
- Lineage reads the files where their rows say and writes the kind of the records ([lineage](../concepts/lineage.md)).

## Related

- [Enrichments](enrich.md): the enrich block, its rows, how the chain renders and what the ledger keeps.
- [Enrichment mappings](mapping-enrichment.md): what an enrichment mapping declares and reads.
- [Mapping values](mapping-values.md#link): `$link` among the value nodes.
- [Routes](routes.md): the route a flow's records go by, which the added records go before.
- [Artifacts and undo](../concepts/artifacts-and-undo.md): what an unfinished delivery leaves and how it is undone.
