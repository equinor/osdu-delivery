---
id: delivery-wiki-0031-run-parameters-from-the-document
title: "Decision 0031: a run shows and may change what its document declares, by one rule, and maxRecords is a default"
type: decision
summary: "Why every kind's run form lists the parameters its document declares, why one rule resolves them, why a lookup reads a column, and why maxRecords is a default."
keywords:
  - run parameters
  - run inputs
  - choices
  - lookup
  - maxRecords
  - run settings
  - schedule values
  - RunValueResolution
referenceRefs:
  - delivery-flow-parameters
  - concept-run-inputs
  - delivery-flow-delivery
  - delivery-cli-run
  - delivery-concept-gui
  - flow-schedule
related:
  - delivery-wiki-0024-scope-value-lists
sourceRefs:
  - sqlflow/src/SqlFlow.Core/Runs/FlowRunInputs.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/RunInputEndpoints.cs
  - sqlflow/gui/src/features/runs/RunValuePicker.tsx
  - osdu/src/SqlFlow.Delivery/Documents/FlowParameterDeclarations.cs
  - osdu/src/SqlFlow.Delivery/Documents/KindRunInputs.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/ScopeReads.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryRunParameterLookup.cs
  - osdu/tests/SqlFlow.Delivery.Tests/RunInputsTests.cs
updated: 2026-10-30
---
# 0031: A run shows and may change what its document declares, by one rule, and maxRecords is a default

Status: accepted. Documented in [parameters](../../reference/flow/parameters.md), SQLFlow's [run
inputs](../../../../sqlflow/docs/reference/concepts/run-inputs.md) and [reading what a schedule gives each
flow](../../../../sqlflow/docs/reference/flow/schedule.md#reading-what-a-schedule-gives-each-flow).

## Context

A delivery flow's document could limit its runs to the first records of the scope (`source.record.maxRecords`), and a
run could only lower that limit. The run form did not show the document's value, nor the defaults of the parameters a
document declares: each kind wrote its own fields for them, and the Preview tab, the data check, a mapping's parameters
and the run each checked the values with a copy of the rule of their own. A schedule's `values` reached its flows
unseen: the Schedules page never showed them, so what a flow ran with was known only from its run page, after the fire.

The human asked that every parameter a flow's document declares be shown in the run form with the document's value and
be changeable for one run, since that is what parameters are for; that a schedule stay fixed but show what it gives each
flow; that a parameter offer its values to pick from, several at once where it takes several; that one central rule
replace the parallel ones; and that existing documents keep working as they do.

## Decision

- **A kind describes what a run may be given, and one rule resolves it.** A registered kind reads a flow's document
  into its run inputs (`IFlowDocumentKind.DescribeRun`, `FlowRunInputs`): the declared parameters with their defaults,
  descriptions, choices and lookups, the settings a run may change, and the run values the kind reads beside them (the
  partition of a flow that works in partitions). `RunValueResolution` decides each value: the one given, else the
  default, else none, refusing a required value missing, an undeclared name, several values where one is taken and a
  value outside the choices. The run, the run form's check, a schedule's preview, the Preview tab, the data check and a
  mapping's parameters all take it, so what the form shows is what the run takes. The rule is SQLFlow's, since any
  module's kinds have parameters; the OSDU kinds only describe theirs (`KindRunInputs`).
- **Every kind reads a declaration the same way.** The seven kinds (eight since the download flow of
  [0038](0038-download-flow.md)) convert `parameters` through one mapping
  (`FlowParameterDeclarations`), so `choices` and its checks are the same in each. A delivery flow alone names a
  `lookup`.
- **A lookup reads a column of a record table the flow already reads, never a query.** `lookup.column` names a column of
  an interface's record table, checked as the document loads, and a scope parameter is looked up in the column its scope
  binds. The values come from the module's existing read of a scope's values, through the flow's own connection, at
  most 500, narrowed to what the operator types. A query written in the document was rejected: the control plane would
  run whatever SQL a document holds each time a form opens. Only a delivery flow names a lookup, since the module already
  reads its record tables for a scope's values; the other kinds offer `choices`, and a lookup of theirs would be a read
  of its own for each kind.
- **Choices are enforced; looked-up values are offered.** A document's `choices` are fixed when it loads, so a run
  given another value is refused. A record table changes between the form and the run, so a value the lookup did not
  list is a warning, not a refusal.
- **A delivery flow's parameters follow the operation.** Only `deliver`, `plan`, `intake` and `replan` read the
  ingestion tables, and only they ever resolved the parameters (`DeliveryExecutor.ReadsSource`). The others (`verify`,
  `drain`, `sync` and the rest) take the values they are given as they are. The kind says which operations resolve them
  (`FlowRunInputs.ParameterOperations`), so the form asks those runs for nothing; resolving for every operation would
  have refused a verify for a value it never reads.
- **`maxRecords` is a default, not a ceiling.** The document's value is what a run takes unless it gives its own: more,
  fewer, or 0 to read the scope whole, and `replan` takes it too. The human chose this over keeping the document's value
  as a ceiling a run could only lower. That would have guaranteed a flow never reads more than its limit, whoever runs
  it, but it contradicts changing values at run time.
- **Records at most is the one run setting for now.** Showing the document's values without letting a run change them
  was rejected, since a parameter exists to be given. Making parallelism and change detection run settings too was left
  out: they change how hard a run pushes OSDU, and are more to test.
- **A schedule's values stay its own, and are shown.** A schedule keeps giving its `values` to every member it runs. The
  Schedules page and Run now show, per member, each parameter's value, where it comes from and what would refuse its
  run, resolved as the fire resolves it.
- **What worked before works the same.** Every document that loaded still loads; a parameter without `choices` or
  `lookup` stores the definition it stored before; the refusals keep their messages; and the module's own route for a
  scope's values stays. The one change a user can notice, a run raising `maxRecords` above the document's value, is in
  the change log under Changed.

## Consequences

- A person who may operate a flow may read more of its scope than its document's `maxRecords` in one run, or the whole
  scope with 0. Scheduled runs take the document's value.
- The run form is SQLFlow's for every kind: a kind that describes its run inputs gets its fields, pickers and checks with
  no GUI code of its own.
- A parameter's lookup reads a database each time a form asks for it, in the control plane's process.
- The `RunParameters` suite, run on demand (`bash tools/test.sh --suite RunParameters`), holds every kind to the rule.
