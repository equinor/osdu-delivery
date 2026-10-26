---
id: delivery-wiki-0024-scope-value-lists
title: "Decision 0024: A scope parameter takes several values in one run, the set is its scope, and SQLFlow carries the list"
type: decision
summary: "Why a run value may be a list, carried by SQLFlow as JSON array text, why only a scope parameter takes one, and why the set is a scope of its own."
keywords:
  - several values
  - scope parameter
  - run value list
  - source.record.scope
  - watermark
  - removal scope
  - --set repeated
  - RunValueList
referenceRefs:
  - delivery-flow-delivery
  - delivery-flow-removal
  - delivery-cli-run
  - delivery-concept-gui
  - delivery-table-source-watermark
  - delivery-table-submission
  - flow-schedule
related:
  - delivery-wiki-0022-removal-flows
  - delivery-wiki-0031-run-parameters-from-the-document
sourceRefs:
  - sqlflow/src/SqlFlow.Core/Runs/RunValueList.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunValuesJsonConverter.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunParameters.cs
  - sqlflow/src/SqlFlow.Yaml/YamlRunValues.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - sqlflow/gui/src/features/runs/RunValuePicker.tsx
  - osdu/tests/SqlFlow.Delivery.Tests/ScopeValueListRunTests.cs
updated: 2026-10-26
---
# 0024: A scope parameter takes several values in one run, the set is its scope, and SQLFlow carries the list

Status: proposed. Documented in [several values of a scope
parameter](../../reference/flow/delivery.md#several-values-of-a-scope-parameter) and SQLFlow's [schedule
values](../../../../sqlflow/docs/reference/flow/schedule.md#operation-and-values).

## Context

A delivery flow scopes its read with `source.record.scope`: each entry binds a column of the record table to a parameter,
and the run reads `[column] = @parameter`. A flow that serves several values of that column (several log sources of one
well log table, say) took one value per run, so delivering several meant one run each, by hand or as the members of a
schedule, and the GUI's field for the parameter picked one value.

Three ways were put to the human. Leave the product as it is and keep one value per run, chaining the runs on a
schedule; drop the scope and let a run deliver whatever changed, steering what reaches OSDU by what the upstream export
writes; or let a scope parameter take several values. The human chose the last, and asked that it be SQLFlow's: a run's
parameter selection taking several values is not peculiar to OSDU, and the OSDU module should only extend it.

## Decision

- **SQLFlow carries a list; the kind decides where one applies.** A run value of a registered kind may be a list: an
  array in a trigger, in a run's detail and on the node protocol, a sequence in a schedule's `values`, `--set` repeated
  on the CLI, and `string | string[]` in the trigger dialog's kind fields. SQLFlow checks the list's shape (1 to 100
  values, 16,000 characters) as it checks a value's, and nothing else: which parameters take several is the kind's to
  say, at its own trust boundary.
- **In memory a list is its JSON array text, in the map every kind already reads.** `RunParameters.Values` stays one
  string per name, and a list is the compact JSON array of its values (`RunValueList`), turned into a real array at every
  JSON boundary (`RunValuesJsonConverter`). A typed map would have changed every entry point of every kind, and the
  delivery engine alone passes the map to dozens of places that never see a list; with the text form a kind changes only
  where it applies lists, and the catalog's `nvarchar(max)` columns hold the new form with no migration. The cost is one
  rule: a single value cannot be text that reads as a JSON array of strings, which no parameter value here is.
- **Only a scope parameter takes several.** A parameter `source.record.scope` binds reads `[column] IN (@p0, @p1, ...)`,
  each value bound as the column's own type, and one value keeps the `=` it always had, so a run of one value sends the
  same statement as before. Every other place a value goes takes one and refuses a list by name: a `{token}` in a query
  (an assertion's search), a payload `root`, the `partition`, and every parameter of the other kinds, whose parameters
  fill queries and locations.
- **The set is the scope.** The values resolve to their set, each once and in ordinal order, so the same values named in
  another order are the same scope and keep one watermark, the existing rule that each distinct set of values is a scope
  of its own. A list of one value is that value, so it shares the single value's watermark. Per-value watermarks inside
  one run were rejected: each value would need its own window in the read, and the run's one submission would cover
  several windows, which nothing else in the ledger does.
- **The work root names the set; a payload root takes one.** A list fills a `{token}` in `source.work` with its values
  joined by `+`, since the work root only keeps one run's batches apart from another's and a set names a folder as a
  value does; flows already write `work: ../.work/{logSource}`, and refusing the list there would have blocked them for
  nothing. A payload root is where a record's files sit, under one of the values, so it refuses a list; a flow that
  serves several values names each record's folder in its `locationColumn`.
- **A removal compares a record when every value its submission carried is among the removal's.** A submission records
  the set as an array, and a removal's `values` may be a sequence. Equality, the rule while every value was single, would
  have left the records planned under a set uncompared by any removal but one naming exactly that set. Membership (any
  value shared) was rejected because it is unsafe: a removal of `COMPOSITE` reads the record table's rows of `COMPOSITE`
  only, so a record planned under `[COMPOSITE, WIRELINE]` that holds `WIRELINE` would be missing from what it read and
  found removed. With the subset rule a single-value removal compares what it always did.
- **A set too long to keep a watermark for is refused before it plans.** The ledger keeps a watermark under a scope key
  of at most 400 characters and refused a longer one when the plan completed, after its records were sent. A set makes a
  long key easy, so the intake refuses it first, with the same message.
- **`--set` repeated gives a list.** Splitting one value on commas was rejected: a value may hold a comma, and the CLI
  does not know which parameters take lists. A repeated `--set` used to keep its last value; that is the one change a
  user of a single value can notice, and it is in the change log.

## Consequences

- One run, one submission and one watermark cover the set, and a record's redelivery reads the scope it was planned
  under, as the submission recorded it.
- Rows written with a list (a run, a schedule, a submission) are read only by code that knows the array form: a control
  plane and its nodes are deployed together, and going back to a version before this after lists were used fails those
  rows when they are read.
- The field the GUI gives a scope parameter takes several values: chips in a form, one token with how many more in the
  value check's line, and the value being typed counting as one, so typing one value and running behaves as it did. It is
  SQLFlow's run value picker, the one the run form gives every parameter of every kind
  ([0031](0031-run-parameters-from-the-document.md)).
