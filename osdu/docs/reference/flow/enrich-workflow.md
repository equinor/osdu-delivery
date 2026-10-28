---
id: delivery-flow-enrich-workflow
title: "An enrichment's workflow (workflows.<name>): an OSDU workflow the flow runs over the record once it landed"
type: flow-reference
summary: "workflows.<name>: stages run through the Workflow service over the record and the records an enrichment of its mapping added, run again only when they move."
keywords:
  - enrich workflow
  - enrichment workflow
  - workflows
  - run a workflow on a dataset of a record
  - csv ingestion of a step's file
  - workflow over enrichment datasets
yamlPath: "workflows.<name>, interfaces.<name>.workflows.<name> (flowType: delivery)"
related:
  - delivery-flow-enrich
  - delivery-flow-enrich-records
  - delivery-flow-routes
  - delivery-concept-artifacts-and-undo
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/WorkflowMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduWorkflowProtocol.cs
  - osdu/docs/census/keys.delivery.json
---

# An enrichment's workflow (workflows.<name>): an OSDU workflow the flow runs over the record once it landed

An [enrichment](enrich.md) that registers a file ([records an enrichment adds](enrich-records.md)) can have the platform work
on it: a CSV parser storing the rows it holds, a converter reading an Energistics file. Which workflow runs is the flow's to
say, since it is about where and how the platform works rather than what the record holds: `workflows.<name>` declares the
runs over the enrichment `<name>` of the flow's mapping as [`target.workflow`](routes.md#workflow) declares them, and the
delivery runs them over the record once the record and what the enrichment added landed, in the same delivery, through the
workflow route itself.

```yaml
# The mapping declares the enrichment wells, adding a dataset--File.Generic per row (WellListFile@1.0.0).
source:
  datasets:
    wells:
      object: OsduData.silver.WellListFile
      join: { source_project: source_project, list_id: list_id }
      files:
        root: ../data/wells
        locationColumn: csv_folder
        hashColumn: csv_hash
        contentType: text/csv
workflows:
  wells:
    stages:
      - workflow: csv_ingestion
        context:
          id: "{dataset:enrich.wells}"
          dataPartitionId: "{partition}"
          data_service_to_use: file
    results:
      search:
        kind: "osdu:wks:master-data--Well:*"
        query: "data.Source:\"{record:id}\""
      minimum: 1
```

## The keys

`stages`, `results`, `secrets` and `runWhen` are the workflow route's, with its rules: each stage's context is checked
against its workflow's contract when the document loads and again before every trigger, a credential is a `{secret:name}`
placeholder, and a placeholder names only what the run has. An enrichment's workflow runs over the record the flow's route
wrote, so it names no `anchor` and no `anchorTag`, and its datasets are what the enrichment adds, so it names no `inputs`: a
context reads the enrichment's dataset as `{dataset:enrich.<name>}`, which names the dataset an enrichment taking one row
adds, and the record as `{record:<path>}` (`{record:data.Datasets}` for the datasets of one taking many rows). A name the
flow's mapping declares no enrichment by is refused when a run binds the flow to its mapping.

| `runWhen` | When a delivery runs the workflow |
| --- | --- |
| `changed` (the default) | When it registers the enrichment's datasets, because they moved; for an enrichment registering none, when it writes the record. |
| `created` | When it creates the record. |
| `requested` | When a redelivery names the workflow. |

A redelivery of the whole record runs it whatever `runWhen` says, and a record's first delivery runs it unless `runWhen` is
`requested`.

## How a run goes

The record and what the enrichment adds are written first. Then each stage is triggered, polled and its outputs read, and
the results are found and read back, exactly as the workflow route does for its anchor. The record is not written again: the
runs' values, the version of the record OSDU serves once they are done (a workflow may write the record itself), and what they
found are kept in the record's target state under the enrichment (`enrich.<name>.workflow.records`,
`enrich.<name>.stage.1.runId`), and the steps and artifacts are named under it (`enrich.<name>.stage-1`), so two enrichments'
runs never share a name.

- A run that fails fails the record: `enrich.wells: its workflow did not finish: ...`. The next try runs it again; a
  finished run's outputs are kept and a retry reuses them, as on the workflow route.
- A delivery that does not complete is undone by the workflow route: a run is waited for and kept, and the records it
  created are removed reversibly.
- Removing the record takes the records the runs created with it, at the same scope, when `results.remove` is true (the
  default).
- The probe asks the Workflow service whether the partition registers every workflow the stages name.

What the runs created is not linked into the record: the record is its mapping and its enrichments, rendered. A workflow that
should add to the record writes it itself, and the next delivery reads the version it wrote. The record's **Enrichments** tab
says the enrichment adds a workflow run.

## Related

- [Enrichments](enrich.md): the enrich block, its rows and how the chain renders.
- [Records an enrichment adds](enrich-records.md): the datasets an enrichment's workflow reads.
- [Routes](routes.md#workflow): the workflow route, its contracts, placeholders and results.
