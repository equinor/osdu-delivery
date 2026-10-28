---
id: delivery-flow-enrich
title: "Enrich (enrich): steps that extend each record a delivery flow renders, rendered as one chain and sent once"
type: flow-reference
summary: "The enrich block of a delivery flow: steps adding paths to each record from rows of their own, rendered after the mapping, the record sent once per change."
keywords:
  - enrich
  - enrich step
  - enhance a record
  - extend an existing record
  - add to a record after it is delivered
  - chained rendering
  - one change one write
  - fragment
  - step rows
  - rows one
  - rows many
  - after
  - base
  - enrich.dataset
  - multi-step record
  - steps of a record
yamlPath: "enrich (flowType: delivery), interfaces.<name>.enrich"
related:
  - delivery-flow-enrich-files
  - delivery-flow-enrich-holds
  - delivery-flow-enrich-bulk
  - delivery-flow-enrich-workflow
  - delivery-flow-mapping-fragment
  - delivery-flow-delivery
  - delivery-flow-interfaces
  - delivery-flow-mapping
  - delivery-concept-change-detection
  - delivery-concept-preflight
  - delivery-concept-lineage
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/CompanionFiles.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery/Engine/Preview/RecordPreview.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryPreviewVerbs.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/EnrichDeliveryTests.cs
  - osdu/docs/census/keys.delivery.json
---

# Enrich (enrich): steps that extend each record a delivery flow renders, rendered as one chain and sent once

A record often holds more than one source knows. A well log's header and curves come from one table, its original LAS file
name from another, a quality grade from a third, each kept by someone else and changed at its own time. The record is
still one OSDU record, with one id and one writer. `enrich:` is how a delivery flow builds it: the flow's own mapping
renders the record, then each **step**, in order, renders a **fragment** of it from rows of its own and adds the fragment
to the record. The record that goes to OSDU is the record with every step in it, hashed and sent once. A step may also
register the files its rows name as datasets of the record, under ids kept for good ([files a step registers](enrich-files.md)),
supply the record's DDMS bulk data ([a step's bulk data](enrich-bulk.md)), and run an OSDU workflow over the record once it
landed ([a step's workflow](enrich-workflow.md)).

A flow without `enrich:` renders, hashes and delivers exactly as it always did: its render context, its document hash and
its records are unchanged.

## An example

The well logs of the well database, delivered as WellLog 1.4.0, with two steps: the log's LAS file name from a table of
its own, then a note the second step makes from what the first added.

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
# source, render and target as the delivery flow page shows them

enrich:
  lasfile:
    description: The name of the log's original LAS file, from the file register.
    dataset:
      object: OsduData.silver.WellLogFile
      join: { source_project: source_project, log_id: log_id }
    mapping: WellLogFile@1.0.0
  note:
    description: A note naming the file, in capitals.
    after: [lasfile]
    mapping: WellLogNote@1.0.0
```

`WellLogFile@1.0.0` is a fragment ([fragments](mapping-fragment.md)): a mapping whose header says `fragment: true`, filling
the same template as the flow's mapping, writing only the paths the step adds.

```yaml
documentType: mapping
name: WellLogFile
version: 1.0.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
fragment: true
record:
  tags:
    LasFile: { $from: las_file }
  data:
    ExtensionProperties:
      WellDb:
        LasFile: { $from: las_file }
```

`WellLogNote@1.0.0` reads no rows: it reads the record as the mapping and the first step left it.

```yaml
documentType: mapping
name: WellLogNote
version: 1.0.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
fragment: true
record:
  data:
    ExtensionProperties:
      WellDb:
        Note: { $base: data.ExtensionProperties.WellDb.LasFile, $modifiers: [upper], $required: false }
```

## The keys

`enrich` is a map of steps by name, in the single form at the root of the document and in the interface form under each
interface (`interfaces.<name>.enrich`), since each interface renders records of its own kind. At the source level of a
document with interfaces it is refused.

| Key | Type | Required | Meaning |
| --- | --- | --- | --- |
| `enrich.<name>` | map | | One step. The name is letters, digits and `_`, not starting with a digit, at most 32 characters, not `record` and not the name of a child dataset, compared ignoring case. At most 16 steps. |
| `enrich.<name>.description` | string | no | What the step adds, shown with the record's composition. |
| `enrich.<name>.mapping` | string | one of `mapping`, `files`, `bulk` and `workflow` | The fragment the step renders, pinned as `Name@version` and found the way the flow's mapping is. |
| `enrich.<name>.files` | map | one of `mapping`, `files`, `bulk` and `workflow` | The files each of the step's rows names, registered as a dataset of the record and linked from it ([files a step registers](enrich-files.md)). Needs `dataset`. |
| `enrich.<name>.dataset` | map | no | The step's rows: `object`, `join`, `orderBy` and `maxRowsPerRecord`, as a child dataset declares them ([delivery flow](delivery.md#source)). `join` covers every key column of the record table. Left out, the step reads no rows and renders from what the record holds and its parameters. |
| `enrich.<name>.rows` | string | no | `one` (the default) or `many`: how many of the step's rows one record takes. Only with `dataset`. |
| `enrich.<name>.after` | list | no | The steps this one renders after, beside the order the document lists them in. |
| `enrich.<name>.bulk` | map | no | The record's DDMS bulk data, named by the step's one row: the payload the `ddms` route sends after the record ([a step's bulk data](enrich-bulk.md)). |
| `enrich.<name>.workflow` | map | no | An OSDU workflow the step runs over the record once it and the step's datasets landed, declared as `target.workflow` is ([a step's workflow](enrich-workflow.md)). |
| `enrich.<name>.onHold` | string | no | `record` (the default) holds the record when the step holds; `step` sets the step aside, the record going ahead with what the step added to its last delivery ([a step set aside on its hold](enrich-holds.md)). |

`render.parameters` supplies every mapping of the chain: each mapping, the flow's and every fragment, takes the values it
declares, and a value none of them declares is refused.

A step is refused on the `dspdm` and `etp` routes, which write DSPDM rows and Energistics objects rather than OSDU records.

## How a record renders

For each record the plan reads, the flow's mapping renders the record, and then each step in order:

1. The step's rows are the rows of its dataset that join the record, read beside the record's row as a child dataset under
   the step's name.
2. Under `rows: one`, a record with no row gets nothing from the step; one row is the fragment's own row, which `$from`
   reads; two or more hold the record (`enrich.<step>: the step reads one row per record (rows: one), and its dataset holds 2
   for this record; ...`). Under `rows: many`, the fragment reads every row with `$forEach: <step>`, and no row is its own.
   A step without a dataset renders once for every record.
3. The fragment renders over the record as the mapping and the steps before it left it, which it reads with `$base`.
4. What it renders is added to the record ([how a fragment adds to the record](mapping-fragment.md#how-a-fragment-adds-to-the-record)).

The record with every step in it is normalized, written as canonical JSON and hashed: that hash is the document hash
change detection compares ([change detection](../concepts/change-detection.md)). The order is the order the document lists
the steps in, each moved after every step its `after:` names; a name no step has, a step naming itself, a name given
twice, and steps waiting for each other are refused when the document loads.

A record the flow's mapping holds is held as it always was, and its steps do not render. A step that holds (a required
value empty, a value its template refuses, several rows under `rows: one`) or that would change what the record holds
holds the record, its reasons named `enrich.<step>: ...`, and the steps after it do not render, so every reason given is a
true one. OSDU keeps the version it has until the record renders whole. A step with `onHold: step` is set aside instead,
and the record goes ahead with what the step added to its last delivery ([a step set aside on its hold](enrich-holds.md)).

## One change, one write

The steps' rows are child datasets of the flow's source. A change to any of them is a change to the record:

- **A step's rows changed, arrived or went.** The record is a candidate of the run's window, as a record whose child rows
  changed always is, and its ingestion fingerprint moved. The whole chain renders again and the record is sent once.
- **The record's own row changed.** The chain renders again over the new row: a step reading what changed renders the new
  value, and every step is in the one write.
- **A mapping or a fragment was edited, a step added, removed or reordered.** The render context names every step's fragment,
  its fingerprint, its template version and its parameters, so the rules moved: the scope is read whole once and every
  record renders again.
- **A cache value a fragment read changed.** The record's cache usages are its mapping's and every fragment's, so the
  cache rollout reaches it. A pipeline's `cache.before: auto` brings up to date what every fragment reads as well as what
  the mapping reads ([cache](delivery.md#cache)).
- **The rows changed but the record renders the same** (a row written again with the same values). Nothing is sent.

So a record is written at most once for each change, however many steps it has and whichever part of its chain moved.

## What the preflight checks

Before anything renders, every run and `sqlflow check` refuse a chain when:

- a step's mapping is not a fragment, fills another kind than the flow's mapping, or another version of its template;
- a fragment's own preflight fails, as any mapping's does ([fragments](mapping-fragment.md#what-the-preflight-checks));
- a step writes what the flow's mapping or another step writes, unless both add items to one list, or writes inside what
  another writes whole;
- a step writes under a key the flow preserves (`target.protocolOptions.preserveDataKeys`), which every update carries over
  from the record OSDU holds, unless it is a step's files linked from the preserved list itself, which an update merges
  ([preserved keys](enrich-files.md#preserved-keys));
- a step reads with `$base` what neither the flow's mapping nor a step before it writes, what the step itself writes, or what
  a later step writes (`... which step 'inner' writes after this one renders; name it under enrich.copy.after ...`);
- a fragment reads a column its step does not have: any column when the step declares no dataset, a column outside its
  `$forEach` under `rows: many`, or a `$forEach` over anything but the step's own rows;
- `render.parameters` supplies a value no mapping of the chain declares.

`sqlflow validate` finds each step's fragment beside the flow's mapping and refuses one that is not there or not a fragment.

## Where the chain shows

- `sqlflow check` lists each step in the order it renders: `enrich      lasfile: WellLogFile@1.0.0, one row of OsduData.silver.WellLogFile`; `--json` lists them under `enrich`.
- `sqlflow preview`, the flow's Preview tab and a record's preview show what each step did to the record: each path it set
  and each item it added, or why it held the record (`document.enrichment`).
- A record's render context, on its Render tab and in `sqlflow records show`, names every step's fragment.
- Lineage reads each fragment as a mapping node of its own, reading the cache types and kinds it reads
  ([lineage](../concepts/lineage.md)).

## Related

- [Files a step registers](enrich-files.md): datasets a step adds to each record, their ids, and what a change sends.
- [A step set aside on its hold](enrich-holds.md): a step that holds without holding the record.
- [A step's bulk data](enrich-bulk.md): a well log's curves named by a table of their own.
- [A step's workflow](enrich-workflow.md): an OSDU workflow run over the record and the step's dataset.
- [Fragments](mapping-fragment.md): the mapping a step renders, `$base`, and how a fragment adds to the record.
- [Delivery flow](delivery.md): the source, its child datasets and the target the record goes to.
- [A source with interfaces](interfaces.md): an interface's own `enrich`.
- [Change detection](../concepts/change-detection.md): what makes a record render and send again.
