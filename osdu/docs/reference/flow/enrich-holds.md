---
id: delivery-flow-enrich-holds
title: "An enrichment set aside on its hold (enrich.<name>.onHold): the record goes ahead with what the enrichment last added"
type: flow-reference
summary: "onHold: step sets a held enrichment aside: the record goes ahead with what it added to its last delivery, and the ledger lists the record with it held."
keywords:
  - onHold
  - enrichment held
  - step held
  - set aside
  - hold isolation
  - an enrichment holds
  - keep what an enrichment last added
  - HeldSteps
  - steps held filter
  - enrich.step
yamlPath: "enrich.<name>.onHold (documentType: mapping)"
related:
  - delivery-flow-enrich
  - delivery-flow-enrich-records
  - delivery-flow-mapping-enrichment
  - delivery-table-record
  - delivery-concept-record-lifecycle
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Enrich.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichLinks.cs
  - osdu/src/SqlFlow.Delivery/Rendering/KeptSteps.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichParts.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/docs/census/keys.mapping.json
---

# An enrichment set aside on its hold (enrich.<name>.onHold): the record goes ahead with what the enrichment last added

An [enrichment](enrich.md) holds when it cannot render for a record: two rows where it reads one, a value its template
refuses, a value it would change, or files its row names that are not there. By default that holds the record, and OSDU
keeps the version it has until the record renders whole. An enrichment whose contribution is a side matter, say a quality
grade added to a well log, should not stop the log's own changes. `onHold: step` sets such an enrichment aside instead: the
record goes ahead with what the enrichment added to its last delivery, the enrichments after it render over that, and the
ledger lists the record with the enrichment held until it renders again.

```yaml
enrich:
  quality:
    rows: one
    mapping: WellLogQuality@1.0.0
    onHold: step
```

| Value | What a hold of the enrichment does |
| --- | --- |
| `record` (the default) | Holds the record with the enrichment's reasons (`enrich.quality: ...`); the enrichments after it do not render. |
| `step` | Sets the enrichment aside: the record goes ahead with what it added to its last delivery. |

## What the record goes ahead with

Every delivery of the record keeps, for each enrichment set aside on its holds, what it composed into the document that
landed (the paths its mapping added) and the ids of the records it added. It is kept in the record's target state
(`enrich.step.<name>`) and in the delivery's attempt. When the enrichment holds, the chain composes what was kept in its
place, exactly as its own render would be composed: a value set where the record holds none, items added to a list. The
record's `$link` to it writes the ids its last delivery added rather than those its rows make now, so the record never links
a record that was not written; when those differ the record renders once more to link them.

- **A record OSDU holds no version of** goes ahead without anything of the enrichment.
- **A record whose kept contribution composes** goes ahead with it. When nothing else changed, the record renders as OSDU
  holds it and nothing is sent; a change to the record's row, another enrichment or a mapping sends the record once with the
  kept contribution in it.
- **A record whose kept contribution no longer fits** (the keying mapping now writes another value where the enrichment set
  one) is held: `enrich.quality: the step is set aside on its holds, and what it added to the version OSDU holds no longer
  fits the record (...)`.
- **A record OSDU holds a version of that kept nothing of the enrichment**, because it was last delivered before the
  enrichment was set aside on its holds, is held until the enrichment renders, so nothing it added is lost.

An enrichment that holds composes nothing of itself into the record: what part of it would have composed is left out with the
rest.

An enrichment adding records that holds because a row's folder or hash is not there is set aside too: the record renders
again with it held, its records stay linked as the last delivery added them, and none of them is sent. The record's document
hash takes them in at the hash their last delivery kept, so the record is not planned again for them.

## What the ledger keeps

The record's `HeldSteps` ([`osdu.Record`](../tables/record.md)) names the enrichments its last render set aside,
comma-separated, and is cleared by the render one renders in again. It is written as the render is recorded, whether work is
queued, the record is held for another reason, or nothing is sent, so a hold that changes nothing still lists the record.

- The flow's counts carry `stepsHeld`, the records listed with an enrichment held; the Delivery page shows it beside each flow
  (`3 with a step held`).
- The flow's **Records** tab has **Step held** while any record is listed, the Records page has it across every flow, and both records APIs take `stepHeld=true`.
- The record's **Enrichments** tab marks the enrichment **set aside**.
- The preview and a record's Render tab show it as `held, set aside: keeps what it last added`, its reasons on hover, and
  what it kept; `sqlflow preview` writes `held and set aside, the record going ahead with what it added to its last
  delivery: ...`.

## Changing onHold

`onHold` is part of the enrichment's rules in the record's render context, so changing it renders every record again. Setting
an enrichment aside on records delivered before keeps nothing of it until each one's next delivery: a hold of it before then
holds the record, as the default did.

## Related

- [Enrichments](enrich.md): the enrich block, its rows and how the chain renders.
- [Records an enrichment adds](enrich-records.md): the records an enrichment set aside keeps linking.
- [Record lifecycle](../concepts/record-lifecycle.md): holds, releases and what keeps a record blocked.
