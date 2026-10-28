---
id: delivery-flow-enrich-bulk
title: "A step's bulk data (enrich.<name>.bulk): the record's DDMS bulk data, named by the step's row"
type: flow-reference
summary: "enrich.<name>.bulk: a well log's curves named by a table of their own, sent by the ddms route as the record's bulk data, only when they move."
keywords:
  - enrich bulk
  - step bulk data
  - curves from another table
  - well log curves arrive later
  - DDMS bulk data of a step
  - bulk payload from a child table
yamlPath: "enrich.<name>.bulk, interfaces.<name>.enrich.<name>.bulk"
related:
  - delivery-flow-enrich
  - delivery-flow-ddms
  - delivery-flow-routes
  - delivery-guide-bulk-data
  - delivery-concept-change-detection
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceBindings.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepBulkDeliveryTests.cs
  - osdu/docs/census/keys.delivery.json
---

# A step's bulk data (enrich.<name>.bulk): the record's DDMS bulk data, named by the step's row

A well log's header comes from the log table, and its curves arrive later, as parquet files a table of their own names.
On the `ddms` route the curves are the record's bulk data, which the Wellbore DDMS keeps beside the record. A step of
[enrich](enrich.md) with `bulk:` names them: the record's payload set `bulk` is read from the step's one row instead of the
record's, and everything the route does with bulk data stays as it is ([Well logs with curves](../guides/bulk-data.md)).

```yaml
target:
  endpoint: ${env:OSDU_URL}
  protocol: ddms
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms

enrich:
  grid:
    dataset:
      object: OsduData.silver.WellLogGrid
      join: { source_project: source_project, log_id: log_id }
    bulk:
      root: ../data/curves
      locationColumn: curve_folder
      pattern: "*.parquet"
      hashColumn: curves_hash
```

| Key | Default | Meaning |
| --- | --- | --- |
| `bulk.root` | required | The folder the rows' bulk data folders are under, as a payload set's root is. |
| `bulk.locationColumn` | required | The step table's column naming each row's folder under `root`. |
| `bulk.pattern` | `*` | The files of a row's folder that are its bulk data. |
| `bulk.hashColumn` | required unless `change.payloadDetect: lastModified` | The step table's column holding the content hash of the row's bulk data. |
| `bulk.chunkCountColumn` | none | The step table's column holding how many files the row's bulk data has. |

The step may render a fragment beside it (`mapping`), and declares its `dataset`; it takes one row per record, since a
record has one set of bulk data. The flow's route is `ddms`, it declares no payload set of the record's own, and one step
supplies the bulk data. A step's bulk data on any other route is refused: a route that sends files and bulk data as parts
(`fileAndDdms`, `manifestAndDdms`) reads both from the record's row under `source.payloads`.

## What a change sends

The record's payload hash is the step row's, so the record's own row and the step's rows decide what goes:

| What changed | What is sent |
| --- | --- |
| The step row's bulk data (its hash) | The bulk data alone; the record stays as OSDU holds it, and the Wellbore DDMS gives the record the version its bulk data commit makes. |
| The record's own row | The record, once, as the route sends it; the bulk data goes only when its hash moved. |
| A row of the step arrived | The bulk data. |
| The row went | Nothing of the bulk data: the record keeps the bulk data OSDU holds. |
| Two rows of the step for one record | The record is held: `enrich.grid: the step names the record's bulk data by its one row, and its dataset holds 2 rows for this record; ...`. |

A record without a row of the step has no payload: it goes without bulk data, as a record of a flow without one does.

The plan checks the step's columns against the step's table, not the record's, and lineage reads the bulk data's folder
as it reads any payload set's.

## Related

- [Enrich](enrich.md): the steps, their rows and how the chain renders.
- [DDMSs](ddms.md): the Wellbore DDMS and its bulk data rules.
- [Well logs with curves](../guides/bulk-data.md): bulk data the record's own row names.
