---
id: delivery-flow-enrich-bulk
title: "An enrichment's bulk data (enrich.<name>.bulk): the record's DDMS bulk data, named by the enrichment's row"
type: flow-reference
summary: "bulk: true on an enrichment: a well log's curves named by a table of their own, sent by the ddms route as the record's bulk data, only when they move."
keywords:
  - enrich bulk
  - enrichment bulk data
  - curves from another table
  - well log curves arrive later
  - DDMS bulk data of an enrichment
  - bulk payload from a child table
yamlPath: "enrich.<name>.bulk (documentType: mapping); source.datasets.<name>.files (flowType: delivery)"
related:
  - delivery-flow-enrich
  - delivery-flow-ddms
  - delivery-flow-routes
  - delivery-guide-bulk-data
  - delivery-concept-change-detection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Enrich.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceBindings.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/docs/census/keys.mapping.json
  - osdu/docs/census/keys.delivery.json
---

# An enrichment's bulk data (enrich.<name>.bulk): the record's DDMS bulk data, named by the enrichment's row

A well log's header comes from the log table, and its curves arrive later, as parquet files a table of their own names. On
the `ddms` route the curves are the record's bulk data, which the Wellbore DDMS keeps beside the record. An
[enrichment](enrich.md) with `bulk: true` names them: the record's payload set `bulk` is read from the enrichment's one row
instead of the record's, and everything the route does with bulk data stays as it is
([Well logs with curves](../guides/bulk-data.md)).

The mapping says the enrichment's row names the record's bulk data:

```yaml
enrich:
  grid:
    rows: one
    bulk: true
```

The flow binds the rows and where each row's bulk data is:

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: ddms
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms
source:
  datasets:
    grid:
      object: OsduData.silver.WellLogGrid
      join: { source_project: source_project, log_id: log_id }
      files:
        root: ../data/curves
        locationColumn: curve_folder
        pattern: "*.parquet"
        hashColumn: curves_hash
```

| Key | Default | Meaning |
| --- | --- | --- |
| `enrich.<name>.bulk` | `false` | `true`: the files the enrichment's one row names are the record's bulk data. Needs `rows: one`. |
| `source.datasets.<name>.files.root` | required | The folder the rows' bulk data folders are under, as a payload set's root is. |
| `source.datasets.<name>.files.locationColumn` | required | The enrichment's table's column naming each row's folder under `root`. |
| `source.datasets.<name>.files.pattern` | `*` | The files of a row's folder that are its bulk data. |
| `source.datasets.<name>.files.hashColumn` | required unless `change.payloadDetect: lastModified` | The column holding the content hash of the row's bulk data. Under `change.payloadDetect: lastModified` the files' names, sizes and modified times stand for it. |
| `source.datasets.<name>.files.chunkCountColumn` | none | The column holding how many files the row's bulk data has. |

The enrichment may render a mapping beside it (`mapping`): of the record's own kind, adding paths from the same row, or of
another kind, adding a record written through storage. One rendering datasets of a kind the Dataset service registers
would register the row's files with them, so it cannot name bulk data too. The flow's route is `ddms`, it
declares no payload set of the record's own, and one enrichment of the mapping names the bulk data. An enrichment's bulk data
on any other route is refused: a route that sends files and bulk data as parts (`fileAndDdms`, `manifestAndDdms`) reads both
from the record's row under `source.payloads`.

## What a change sends

The record's payload hash is the enrichment row's, so the record's own row and the enrichment's rows decide what goes:

| What changed | What is sent |
| --- | --- |
| The row's bulk data (its hash) | The bulk data alone; the record stays as OSDU holds it, and the Wellbore DDMS gives the record the version its bulk data commit makes. |
| The record's own row | The record, once, as the route sends it; the bulk data goes only when its hash moved. |
| A row of the enrichment arrived | The bulk data. |
| The row went | Nothing of the bulk data: the record keeps the bulk data OSDU holds. |
| Two rows of the enrichment for one record | The record is held: `enrich.grid: the step names the record's bulk data by its one row, and its dataset holds 2 rows for this record; ...`. |

A record without a row of the enrichment has no payload: it goes without bulk data, as a record of a flow without one does.

The plan checks the columns against the enrichment's table, not the record's, and lineage reads the bulk data's folder as it
reads any child dataset's files. The record's **Enrichments** tab names the row the delivered bulk data came from.

## Related

- [Enrichments](enrich.md): the enrich block, its rows and how the chain renders.
- [DDMSs](ddms.md): the Wellbore DDMS and its bulk data rules.
- [Well logs with curves](../guides/bulk-data.md): bulk data the record's own row names.
