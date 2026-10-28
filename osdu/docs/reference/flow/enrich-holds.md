---
id: delivery-flow-enrich-holds
title: "A step set aside on its hold (enrich.<name>.onHold): the record goes ahead with what the step last added"
type: flow-reference
summary: "onHold: step sets a held step aside: the record goes ahead with what the step added to its last delivery, and the ledger lists it with the step held."
keywords:
  - onHold
  - step held
  - set aside
  - hold isolation
  - a step holds
  - keep what a step last added
  - HeldSteps
  - steps held filter
  - enrich.step
yamlPath: "enrich.<name>.onHold, interfaces.<name>.enrich.<name>.onHold"
related:
  - delivery-flow-enrich
  - delivery-flow-enrich-files
  - delivery-flow-mapping-fragment
  - delivery-table-record
  - delivery-concept-record-lifecycle
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/KeptSteps.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichParts.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepHoldsDeliveryTests.cs
  - osdu/docs/census/keys.delivery.json
---

# A step set aside on its hold (enrich.<name>.onHold): the record goes ahead with what the step last added

A step of [enrich](enrich.md) holds when it cannot render for a record: two rows where it reads one, a value its template
refuses, a value it would change, or files its row names that are not there. By default that holds the record, and OSDU
keeps the version it has until the record renders whole. A step whose contribution is a side matter, say a quality grade
added to a well log, should not stop the log's own changes. `onHold: step` sets such a step aside instead: the record goes
ahead with what the step added to its last delivery, the steps after it render over that, and the ledger lists the record
with the step held until the step renders again.

```yaml
enrich:
  quality:
    dataset:
      object: OsduData.silver.WellLogQuality
      join: { source_project: source_project, log_id: log_id }
    mapping: WellLogQuality@1.0.0
    onHold: step
```

| Value | What a hold of the step does |
| --- | --- |
| `record` (the default) | Holds the record with the step's reasons (`enrich.quality: ...`); the steps after it do not render. |
| `step` | Sets the step aside: the record goes ahead with what the step added to its last delivery. |

## What the record goes ahead with

Every delivery of the record keeps, for each step set aside on its holds, what the step composed into the document that
landed: its fragment and the links to its datasets, and the ids of those datasets. It is kept in the record's target state
(`enrich.step.<name>`) and in the delivery's attempt. When the step holds, the chain composes what was kept in the step's
place, exactly as the step's own render would be composed: a value set where the record holds none, items added to a list.

- **A record OSDU holds no version of** goes ahead without anything of the step.
- **A record whose kept contribution composes** goes ahead with it. When nothing else changed, the record renders as OSDU
  holds it and nothing is sent; a change to the record's row, another step or a fragment sends the record once with the
  kept contribution in it.
- **A record whose kept contribution no longer fits** (the flow's mapping now writes another value where the step set one)
  is held: `enrich.quality: the step is set aside on its holds, and what it added to the version OSDU holds no longer fits
  the record (...)`.
- **A record OSDU holds a version of that kept nothing of the step**, because it was last delivered before the step was set
  aside on its holds, is held until the step renders, so nothing the step added is lost.

A step that holds composes nothing of itself into the record: what part of it would have composed is left out with the rest.

A step that registers files and holds because a row's folder or hash is not there is set aside too: the record renders
again with the step held, its datasets stay linked as the last delivery registered them, and none of them is sent. The
record's document hash takes them in at the hash their last delivery kept, so the record is not planned again for them.

## What the ledger keeps

The record's `HeldSteps` ([`osdu.Record`](../tables/record.md)) names the steps its last render set aside, comma-separated,
and is cleared by the render a step renders in again. It is written as the render is recorded, whether work is queued, the
record is held for another reason, or nothing is sent, so a hold that changes nothing still lists the record.

- The flow's counts carry `stepsHeld`, the records listed with a step held; the Delivery page shows it beside each flow
  (`3 with a step held`).
- The flow's **Records** tab has **Step held** while any record is listed, and the records API takes `stepHeld=true`.
- The preview and a record's Render tab show the step as `held, set aside: keeps what it last added`, its reasons on hover,
  and what it kept; `sqlflow preview` writes `held and set aside, the record going ahead with what it added to its last
  delivery: ...`.

## Changing onHold

`onHold` is part of the step's rules in the record's render context, so changing it renders every record again. Setting a
step aside on records delivered before keeps nothing of it until each one's next delivery: a hold of the step before then
holds the record, as the default did.

## Related

- [Enrich](enrich.md): the steps, their rows and how the chain renders.
- [Files a step registers](enrich-files.md): a step's datasets, which a step set aside keeps linking.
- [Record lifecycle](../concepts/record-lifecycle.md): holds, releases and what keeps a record blocked.
