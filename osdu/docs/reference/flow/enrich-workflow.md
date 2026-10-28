---
id: delivery-flow-enrich-workflow
title: "A step's workflow (enrich.<name>.workflow): an OSDU workflow run over the record once it landed"
type: flow-reference
summary: "enrich.<name>.workflow: stages run through the Workflow service over the record and its step's dataset, run again only when the step's files move."
keywords:
  - enrich workflow
  - step workflow
  - run a workflow on a dataset of a record
  - parse a file after it is registered
  - csv parser on a step's file
  - runWhen
  - dataset placeholder
yamlPath: "enrich.<name>.workflow, interfaces.<name>.enrich.<name>.workflow"
related:
  - delivery-flow-enrich
  - delivery-flow-enrich-files
  - delivery-flow-routes
  - delivery-concept-artifacts-and-undo
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/WorkflowMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduWorkflowProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepWorkflowTests.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/specs/core/workflow/openapi.yaml
---

# A step's workflow (enrich.<name>.workflow): an OSDU workflow run over the record once it landed

A step of [enrich](enrich.md) that registers a file ([files a step registers](enrich-files.md)) can have the platform work on
it: a CSV parser storing the rows it holds, a converter reading an Energistics file. `workflow:` declares the runs as
[`target.workflow`](routes.md#workflow) declares them, and the step runs them over the record once the record and the
step's dataset landed, in the same delivery, through the workflow route itself.

```yaml
enrich:
  wells:
    dataset:
      object: OsduData.silver.WellListFile
      join: { source_project: source_project, list_id: list_id }
    files:
      root: ../data/wells
      locationColumn: csv_folder
      hashColumn: csv_hash
      contentType: text/csv
    workflow:
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
against its workflow's contract when the document loads and again before every trigger, a credential is a
`{secret:name}` placeholder, and a placeholder names only what the step has. A step's workflow runs over the record the
flow's route wrote, so it names no `anchor` and no `anchorTag`, and its datasets are its `files:`, so it names no
`inputs`: a context reads the step's dataset as `{dataset:enrich.<step>}`, which names the `dataset--File.Generic` a step
taking one row registers under its default `files.kind`, and the record as `{record:<path>}` (`{record:data.Datasets}` for the
datasets of a step taking many rows, or of another kind).

| `runWhen` | When a delivery runs the workflow |
| --- | --- |
| `changed` (the default) | When it registers the step's datasets, because their files moved; for a step registering none, when it writes the record. |
| `created` | When it creates the record. |
| `requested` | When a redelivery names the workflow. |

A redelivery of the whole record runs it whatever `runWhen` says, and a record's first delivery runs it unless `runWhen` is
`requested`.

## How a run goes

The record and the step's datasets are written first, as the step's files and the flow's route write them. Then each
stage is triggered, polled and its outputs read, and the results are found and read back, exactly as the workflow route
does for its anchor. The record is not written again: the runs' values, the version of the record OSDU serves once they
are done (a workflow may write the record itself), and what they found are kept in the record's target state under the
step (`enrich.<step>.workflow.records`, `enrich.<step>.stage.1.runId`), and the steps and artifacts are named under it
(`enrich.<step>.stage-1`), so two steps' runs never share a name.

- A run that fails fails the record: `enrich.wells: its workflow did not finish: ...`. The next try runs it again; a
  finished run's outputs are kept and a retry reuses them, as on the workflow route.
- A delivery that does not complete is undone by the workflow route: a run is waited for and kept, and the records it
  created are removed reversibly.
- Removing the record takes the records the runs created with it, at the same scope, when `results.remove` is true (the
  default).
- The probe asks the Workflow service whether the partition registers every workflow the stages name.

What the runs created is not linked into the record: the record is the flow's mapping and its steps, rendered. A workflow
that should add to the record writes it itself, and the next delivery reads the version it wrote.

## Related

- [Enrich](enrich.md): the steps, their rows and how the chain renders.
- [Files a step registers](enrich-files.md): the datasets a step's workflow reads.
- [Routes](routes.md#workflow): the workflow route, its contracts, placeholders and results.
