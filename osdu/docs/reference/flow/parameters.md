---
id: delivery-flow-parameters
title: "Parameters (parameters): what a run of an OSDU flow may be given, its choices and lookups"
type: flow-reference
summary: "Every OSDU flow kind's parameters, their choices and a delivery flow's lookups, the one rule a run takes its values by, and how the run form shows them."
keywords:
  - parameters
  - choices
  - lookup
  - lookup.column
  - run values
  - --set
  - required parameter
  - default value
  - several values
  - run form
  - flow parameters
  - run settings
  - records at most
  - schedule values
  - partition run value
yamlPath: "parameters"
related:
  - delivery-flow-overview
  - delivery-flow-delivery
  - delivery-cli-run
  - delivery-concept-gui
  - delivery-concept-partitions
  - concept-run-inputs
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/FlowParameterDeclarations.cs
  - osdu/src/SqlFlow.Delivery/Documents/KindRunInputs.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/ScopeValuesOperation.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryRunParameterLookup.cs
  - sqlflow/src/SqlFlow.Core/Runs/FlowRunInputs.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/RunInputEndpoints.cs
---

# Parameters: what a run of an OSDU flow may be given

Every OSDU flow kind may declare `parameters`: named values a run is given with `--set name=value` on the CLI, a
trigger's `values` on the API, the fields of the run form in the GUI, or a schedule's `values`. A kind uses them where it
allows a `{name}` token (a delivery flow's work location and payload roots, a retrieval's query and location, a cache's,
dimension's and inventory's queries, an assertion's queries and texts, a removal's values and key locations, a download's queries and location), and a
delivery flow's `source.record.scope` binds them to columns of its record table. Every kind reads the declaration the
same way, and a run resolves its values by one rule, the one the run form and a schedule's preview explain them with,
so what they show is what the run takes ([run inputs](../../../../sqlflow/docs/reference/concepts/run-inputs.md)).

## Declaring a parameter

```yaml
parameters:
  logSource: { required: true, description: The well database's log source a run reads. }
  project: { default: PROJECT_A, choices: [PROJECT_A, PROJECT_B] }
  wellbore: { lookup: { column: facility_name, interface: wellbores } }
  note:
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `parameters.<name>.required` | boolean | `false` | A run without a value, and without a default to take, is refused. A schedule has to give it under `values`. |
| `parameters.<name>.default` | string | none | The value a run that gives none takes; one of the parameter's `choices` when it lists them. |
| `parameters.<name>.description` | string | none | What the parameter is for, shown under its field in the run form and on hover in a schedule's values. |
| `parameters.<name>.choices` | list | none | The values the parameter may take, in the order the run form lists them; a run given any other is refused. At least one and at most 500, each listed once and written as a run gives it (no surrounding space). Values are compared exactly, case included. |
| `parameters.<name>.lookup.column` | string | none | A delivery flow only: the column of a record table whose values the run form offers (below). |
| `parameters.<name>.lookup.interface` | string | the first | A delivery flow's interface whose record table holds the column. |

A parameter written with no body (`note:`) is optional with no default. A parameter lists `choices` or names a
`lookup`, not both. Only a delivery flow's parameter names a `lookup`: every other kind refuses the key as one it does
not know.

## How a run takes its values

A run takes, for each parameter in the document's order, the value it is given, else the parameter's default, else
none. It is refused, before anything is read or sent, when:

| The run gives | The refusal |
| --- | --- |
| no value for a required parameter without a default | `<file>: parameter '<name>' is required. Supply it with --set <name>=value or the run's values.` |
| a value for a name the document does not declare (names are compared exactly) | `<file>: parameter '<name>' is not declared under parameters.` |
| a value outside a parameter's `choices` | `<file>: parameter '<name>' is given '<value>', which is not one of its choices: <choices>.` |
| several values for a parameter that takes one | `<file>: parameter '<name>' is given <n> values, and takes one.` In a delivery flow: `... and takes one: only a parameter source.record.scope binds (<names>) takes several.` |

Only a parameter a delivery flow's `source.record.scope` binds takes several values, which the run takes as their set
([several values of a scope parameter](delivery.md#several-values-of-a-scope-parameter)). A value given empty is a
value; the run form gives none for a field left empty, so the default applies.

**Which operations take them.** A delivery flow's `deliver`, `plan`, `intake` and `replan` runs read the ingestion
tables, and resolve the parameters as above. Its `verify`, `drain`, `sync`, `reverse`, `undo`, `remove` and
`delete-ledger` runs read no rows the parameters scope: they take the values they are given as they are and require
none, and the run form asks them for none. Every operation of every other kind resolves them.

**The partition.** The partition a run acts in is a run value of its own, `partition`, not a parameter
([partitions](../concepts/partitions.md)). A flow that names its partitions, or leaves them to the registry, takes it
beside its parameters; a flow whose `data-partition-id` header names its partition takes none, and a run naming one is
refused.

## Lookups

A delivery flow's run form offers a parameter's values to pick from instead of having them typed:

- **A scope parameter.** A parameter `source.record.scope` binds is looked up in the column it binds, in the record table
  of the first interface whose scope binds it, so it names no `lookup` (one that does is refused).
- **Any other parameter.** `lookup.column` names a column of the record table of `lookup.interface`, or of the first
  interface, whose values it offers.

The values are the distinct values the column holds in rows not marked deleted, with how many rows hold each, the most
first, at most 500. When the column holds more, what the operator types is looked up too: the values whose text holds
it, ignoring case, its `%`, `_` and `[` taken as written. They are read in the control plane's process through the
flow's own connection reference, in the partition the run form is on, by the module's read of a scope's values
(`delivery-scope-values`, which `POST /api/v1/delivery/flows/{pipelineId}/scope-values` answers too); nothing is written. Looked-up values are offered, not enforced: the table may hold another by the time the run
reads it, so a value the list does not hold is marked as a warning, never refused. A column the table does not hold is
named when the values are read: `<file>: parameters.<name>.lookup names column '<column>', which the table <table> does
not hold. Columns: ...`, answered as `502 Lookup failed` with what the database said, any secret it quoted redacted.

## Run settings

A delivery flow's **Records at most** is a setting of its document a run may change for itself: the first records of the
scope a `deliver`, `plan`, `intake` or `replan` run reads ([the first records of the
scope](delivery.md#the-first-records-of-the-scope)). The document's `source.record.maxRecords` is the default every run
takes; a run's payload `maxRecords` stands in for it on that run, more, fewer, or 0 to read the scope whole
([a delivery flow's payload](../cli/run.md#a-delivery-flows-payload)).

## In the GUI and on a schedule

- **The run form.** The trigger dialog lists the flow's **Flow parameters** (each with its description and default, a list
  to pick from for its `choices` or its lookup, several values as chips for a scope parameter) and its **Run settings**
  (Records at most, with the document's value), beside the kind's own fields (the partition, force, the records or the
  submission a run works on). A field left empty gives the run nothing, so the document's own applies. The control
  plane checks the run as the operator fills it in, each problem under its field, and starts nothing it would refuse.
- **The Preview tab and a mapping's data check** give the flow's parameters in the same fields, checked as a `plan`
  would take them.
- **A schedule** gives its `values` to each member of a registered kind (one that declares the schedule's operation, or
  any when it names none). The Schedules page shows what each flow runs with: every parameter's value and where it comes
  from, the flow's run settings, and what would refuse its run. A refusal of the kind's own check of the schedule's
  arguments (several values given to a kind that takes one, say) fails the whole fire; a value the member's document
  refuses fails that member's run when it starts.

## Validation errors

| Message | Cause |
| --- | --- |
| `<file>: parameters.<name>.choices lists no values; list the values the parameter may take, or leave choices out.` | `choices: []`. |
| `<file>: parameters.<name>.choices lists '<value>' more than once.` | A choice listed twice. |
| `<file>: parameters.<name>.choices holds '<value>'; every choice is a value a run gives, written without surrounding space.` | An empty choice, or one with leading or trailing space. |
| `<file>: parameters.<name>.choices lists <n> values; a parameter lists at most 500. Leave choices out, so a run takes any value.` | Too many choices. |
| `<file>: parameters.<name>.default is '<value>', which is not one of its choices: <choices>.` | A default outside the choices. |
| `<file>: parameters.<name> lists choices and names a lookup; a parameter offers its own list or the values a column holds, so keep one of them.` | Both keys. |
| `<file>: parameters.<name>.lookup.column is missing: name the column of the record table whose values the parameter offers.` | A `lookup` without its column. |
| `<file>: parameters.<name>.lookup.column names a column '<column>'; a column name is 1 to 128 characters, without brackets, control characters or surrounding space.` | A column name the source cannot quote. |
| `<file>: parameters.<name>.lookup.interface names interface '<interface>', which the flow does not declare; it declares <interfaces>.` | An interface the source does not have. |
| `<file>: parameters.<name>.lookup.interface names interface '<interface>', and the flow declares no interfaces: its one record table is where the column is read, so leave interface out.` | `interface` in the single form. |
| `<file>: parameters.<name>.lookup: <path>.scope binds parameter '<name>' to column '<column>', whose values a run form offers for it; leave lookup out.` | A lookup on a scope parameter. |
