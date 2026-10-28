---
id: delivery-wiki-0035-enrich-chains
title: "Decision 0035: a record built in steps is rendered as one chain by its own flow, and sent once for any change"
type: decision
summary: "Why steps extending a record are an enrich block of its delivery flow, rendered with its mapping and written once, not flows writing the record in OSDU."
keywords:
  - enrich
  - fragment
  - "$base"
  - chained rendering
  - one change one write
  - enhance a record
  - multi-step record
  - storage patch
  - record versions
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/EnrichDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/EnrichmentGuardTests.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichParts.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepFilesDeliveryTests.cs
  - osdu/src/SqlFlow.Delivery/Rendering/KeptSteps.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepHoldsDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepBulkDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/StepWorkflowTests.cs
referenceRefs:
  - delivery-flow-enrich
  - delivery-flow-enrich-records
  - delivery-flow-enrich-holds
  - delivery-flow-enrich-bulk
  - delivery-flow-enrich-workflow
  - delivery-flow-mapping-enrichment
  - delivery-concept-change-detection
related:
  - delivery-wiki-0036-enrich-in-the-mapping
  - delivery-wiki-0004-preserved-keys
  - delivery-wiki-0019-linked-templates
  - delivery-wiki-0023-child-links
updated: 2026-10-28
---
# 0035: A record built in steps is rendered as one chain by its own flow, and sent once for any change

Status: accepted, amended by [0036](0036-enrich-in-the-mapping.md): the steps are now enrichments a mapping declares and
the flow binds, adding paths or records of any kind, which the record links with `$link`; how the chain renders and is sent
once, below, still holds. Documented in [enrichments](../../reference/flow/enrich.md),
[records an enrichment adds](../../reference/flow/enrich-records.md) and
[enrichment mappings](../../reference/flow/mapping-enrichment.md).

## Context

A record OSDU holds is often built from more than one source: a well log's header and curves from one table, its original
file from a file register, a grade from a quality system, a list of its children from another table again. Each arrives at
its own time. The owner asked for a way to enhance a record the ledger already holds, without creating another record,
over more than two steps, and set three rules: building a record step by step through OSDU is acceptable for its first
version; once the base structure is there the rendering engine takes over; and one small change must never create several
OSDU versions.

What OSDU and the module already have shaped the choice:

- `IX_Record_ClaimedTargetId` is unique: one OSDU record belongs to one record of one flow, so one ledger record is its
  writer.
- Storage's `PATCH /records` with JSON Patch writes a new version on every data patch, even an unchanged one, adds an
  array item again when a try resumes (`add .../-`), and checks no version; merge patch replaces arrays whole and answers
  with no version (osdu/specs/core/INTEGRATION.md section 2.3). The Wellbore DDMS refuses a record write whose `bulkURI`
  differs from the one it holds.
- `preserveDataKeys` carries whole `data` keys from the stored record into every update, so two writers cannot share one
  key such as `Datasets` ([0004](0004-preserved-keys.md)).
- A change to a child dataset's rows already makes its record a candidate of the run's window and moves its ingestion
  fingerprint, and payload parts already go only when their own hash moved.

## Decision

- **A step is part of the record's delivery flow.** `enrich:` on the delivery document (and on an interface) lists the
  steps; each renders a **fragment** (`fragment: true`), the paths it adds, from rows of its own read as a child dataset
  under its name. A flow without the block is as it was.
- **The chain renders as one.** The flow's mapping renders the record, each step renders over the record as it stands and
  reads it with `$base`, and the record with every step in it is the one document hashed and sent, through the flow's own
  route, under its own lease. A change to any part of the chain renders the whole chain again, so the record is written at
  most once for it.
- **A fragment only adds.** It sets a value where the record holds none, adds items a list does not hold, and goes into
  objects; anything that would take back what is there is a conflict that holds the record. Both operations are
  idempotent, which is what makes rendering the whole chain on every change safe.
- **The render context names every fragment**, its fingerprint, template version and parameters, so a fragment edit
  renders the records again, and the ledger says what rendered every part of a document.
- **The preflight refuses what could never render**: a step writing what another writes, writing under a preserved key,
  reading what nothing before it writes, or reading rows it does not have.
- **A step's links merge into a preserved list** (2026-10-28). A flow that carries `Datasets` over from the record OSDU
  holds, as the well log estate's Wellbore DDMS flow does, may still link a step's datasets there: an update keeps OSDU's
  items and adds or takes off the step's own, on every route that carries preserved keys. Copying the list whole would
  have kept a link only from the record's creation and never taken one off, and dropping `Datasets` from the flow's
  preserved keys would lose what other systems list.
- **A step's cache reads and datasets join the pipeline's cache steps** (2026-10-28), not a refresh of a step's own:
  `cache.before: auto` takes what every fragment reads, and `cache.after` may take the types holding a kind a step
  registers, whose datasets are read back with the records. A refresh between two steps would read nothing new, since
  the chain renders in one pass before anything is sent and a step's dataset ids are made from the record's.
- **A step that holds holds the record**, unless it is set aside on its holds (`onHold: step`, 2026-10-28). OSDU keeps the
  version it has until the record renders whole. A step set aside lets the record go ahead with what it added to the
  record's last delivery, which every delivery keeps in the record's target state; a record whose kept contribution no
  longer fits, or that OSDU holds a version of that kept none, is held, so going ahead never takes anything away. The
  record's `HeldSteps` lists the steps set aside, through an index of its own, for the Records tab's filter and the count.
- **A step's files are parts of the same delivery**, not deliveries of their own (2026-10-28). Each of the step's rows is
  registered through the Dataset service as a dataset under an id derived from the record's and the step
  (`<record>-enrich.<step>`, and a hash of the row's key under `rows: many`), so the id is known when the record renders and
  never changes: the record links it from its first write, and new files land as a new version of the same dataset. The
  record's hash takes in each dataset's, and the plan, which holds what the last delivery kept, decides whether the
  document itself moved; a delivery registers the datasets whose hash moved and writes the record only when its document
  did. One change therefore writes the record at most once, and a change to the files alone writes no record version.

## Alternatives considered

- **A flow kind of its own for a step**, writing the record OSDU holds. Not taken: it needs a second ledger per record and
  a second writer of an id the ledger gives one writer; the owner's next write erases what it added unless every key is
  preserved; storage's patch checks no version and mints one per call; and a change to the original re-runs the first step
  only, so the later steps are stale or each costs a version.
- **Steps that land in OSDU one after another on every change**, each landing planning the next, as `$child` links do
  ([0023](0023-child-links.md)). Not taken for updates: a change costs a version per step. It remains acceptable for a
  record's first version, which the owner allowed; this decision renders every step in the first delivery already.
- **The flow recomposing stored fragments** each step left in the ledger. Not taken: a fragment that read the record would
  be stale when the record changed, which is the case rendering the chain again answers.
- **`preserveDataKeys` for each step's keys.** Not taken: it shares no key between writers and keeps a value only from the
  record OSDU holds, never from what the step would render now.
- **Keeping a held step's last fragment while the rest goes ahead.** Taken as an option of the step (`onHold: step`), not
  the default: a record that goes ahead carries a part rendered from rows the source no longer holds, which is right for a
  side matter and wrong for what the record is.
- **Leaving a held step's paths out while the rest goes ahead.** Not taken: the record would lose what the step added and
  gain it back when the step renders again, a version each way.
- **A table of each record's step contributions** beside the record. Not taken: every path that moves, purges or retires
  records would need it too, while the record's target state already goes where the record goes; the step's contribution
  is small and read only when the step holds.
- **A step's files as a sub-delivery** with a ledger of its own (`<flow>/<step>`), each dataset a record of it. Not taken:
  the record and its datasets would land in two units of work, a failure between them leaving a record linking a dataset
  that is not there or the other way round; two ledgers would claim ids of one record's family; and the record's write
  would wait for another ledger's landing. As parts of the record's own delivery, the datasets go first and the record
  after, in one unit of work that undoes what it left.
- **The File service's ids for a step's files.** Not taken: it mints a new id for every registration, so new files would
  need the record written again to link them, a version per change of the files.

## Consequences

- One change to a record, its steps' rows, a fragment or the cache values any of them read sends the record once, and a
  change that renders the same record sends nothing; the suite proves it end to end (`EnrichDeliveryTests`).
- A flow without steps keeps its render context, its document hash and its records exactly, which the guard tests pin in
  the tiers on every pull request (`EnrichmentGuardTests`, the document hash taken from the code before steps existed).
- Steps share the flow's source connection, its key, its runs and its schedule; a step owned elsewhere is added by editing
  the flow's document.
- A step adds paths, registers files, supplies the record's DDMS bulk data and runs a workflow, each as part of the record's
  one delivery, so the rule of one write per change holds for them (2026-10-28). A step's bulk data is the record's payload
  read from the step's row, not a part of its own, so the ddms route sends it as it always sent bulk data and only that
  route takes it; a step's workflow runs through the workflow route itself over the record its flow wrote, kept under the
  step's name, and what it creates is not linked into the record, which stays what the mapping and the steps render.
- A step's dataset ids hold a dot, which neither the dataset route's own `files` dataset nor a workflow input's name can, so
  no route derives the same id for something else. A dataset the record stops linking stays live and is marked superseded,
  since the record's earlier versions name it; removing everything purges it with the record.
- Adding steps to a flow moves its rules, so its scope is read whole once; records the steps add nothing to render the
  same and are not sent.
