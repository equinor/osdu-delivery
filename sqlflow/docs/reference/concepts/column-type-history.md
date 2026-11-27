---
id: concept-column-type-history
title: "Column type history: what each run did about a column's type, and runs that succeed with warnings"
type: concept
summary: "Each change to an existing column's type a run reports, kept per column with its outcome, and the warnings a run raised, so one that succeeded with them shows."
keywords:
  - column type history
  - type change
  - type changes
  - ColumnTypeChange
  - CatalogColumnTypeChange
  - TypeChangeReport
  - warning count
  - WarningCount
  - succeeded with warnings
  - needs attention
  - schema.convertTypes
  - checked conversion
  - widening
related:
  - concept-shadow-catalog
  - concept-schema-evolution
  - concept-control-plane
  - concept-maintenance
sourceRefs:
  - src/SqlFlow.Core/Model/TypeChangeReport.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogDbContext.cs
  - src/SqlFlow.Catalog/CatalogProjection.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/RepoStore.cs
  - src/SqlFlow.Catalog/Migrations/20261127122828_RunWarningCountAndColumnTypeHistory.cs
  - src/SqlFlow.ControlPlane/Api/Contracts.cs
  - src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/PipelineRunDurations.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.cs
  - gui/src/components/StatusBadge.tsx
  - gui/src/features/runs/TypeChanges.tsx
  - gui/src/features/runs/typeChangeOutcome.ts
  - gui/src/features/pipelines/PipelineDetailPage.tsx
---

# Column type history

When the type a source declares for a column differs from the type the column already has, the engine decides what to
do about it and reports the decision: the column, the types, the change's tier, and its outcome
([Schema evolution](schema-evolution.md) says how the engine decides). An ingestion flow reports the changes of its
target, a file flow those of its typed view. Each report lands in run.json under `result.typeChanges`, one
`TypeChangeReport` (src/SqlFlow.Core/Model/TypeChangeReport.cs) each, and every report that needs an operator is also a
run event at level `warning` (step `target.typechange` or `view.typechange`).

The catalog keeps both: every report as a row of the column's type history, and the run's warnings as a count on the
run, so a run that succeeded but left a change for an operator is told apart from a clean one.

## Tiers and outcomes

| Tier | What it is |
| --- | --- |
| `Widening` | Within a family, to a type that holds every value of the old one. Applied by itself. |
| `CheckedConversion` | A conversion whose success depends on the data. Applied only with `schema.convertTypes`, after every stored value is shown to convert without loss. |
| `Manual` | Refused. The report names the change and the steps to make it by hand. |

| Outcome | Needs attention | What became of the change |
| --- | --- | --- |
| `Applied` | no | Applied; the column holds the new type. |
| `Resumed` | no | A rewrite an earlier run started was finished. |
| `KeptWider` | no | A narrowing left alone: the column keeps its wider type, which holds every source value. |
| `Planned` | no | A plan's verdict: a run would apply it. |
| `Pending` | yes | Needs an opt-in the flow has not given (`requiresOptIn` names it); the table is untouched. |
| `Refused` | yes | Stored values do not convert without loss; the table is untouched. |
| `Manual` | yes | Not applied by the engine; the manual steps say how. |
| `RolledBack` | yes | A rewrite an earlier run started was rolled back, leaving the column as it was. |

The outcomes that need attention are the ones `TypeChangeReport.StatusNeedsAttention` names: the engine warns by that
rule, and the API marks each row by it (`needsAttention`).

## A run's warnings

`Run.WarningCount` is how many entries of the artifact's top-level `events` array are at level `warning`, read by the
rule the `RunEvent` projection uses (an entry without a message or a timestamp is no event). Every path that records a
run from its artifact counts it: the run queue's completion (`RunQueueStore.CompleteFromArtifactAsync`), the per-run
write-back after `sqlflow run` and the full sync (`CatalogSync`), all through `CatalogProjection.RunFromJson`.

A run that succeeded with warnings keeps the status `succeeded`; the count is what shows it apart. A run still
executing, and a run recorded before the count was kept, carry zero (the migration
`RunWarningCountAndColumnTypeHistory` does not count the events of runs already recorded).

## The ColumnTypeChange table

`catalog.ColumnTypeChange` (`CatalogColumnTypeChange`) holds one row per report. Its identity is the clustered key.

| Column | What it holds |
| --- | --- |
| `RepoId`, `RunId`, `PipelineId` | The run that reported the change, and its repository and pipeline. |
| `ObjectKey` | The changed object's catalog key, as `Object.Key` and `ObjectColumn.ObjectKey` hold it (see below). |
| `TableName` | The table or view as the run named it: `[database].[schema].[name]` for a target, `[schema].[name]` for a typed view. |
| `Layer` | `target` or `view`. |
| `ColumnName`, `FromType`, `ToType` | The column and its types, rendered with length or precision. |
| `Tier`, `Status` | The tier and the outcome, as named above. |
| `RewritesTable`, `IsKey` | Whether the change rewrites every row, and whether the column is a key, hash, identity or primary-key column. |
| `RequiresOptIn` | The flow setting the change needs, or null. |
| `Rows`, `FailingValues` | The rows the table held, and for a refusal how many stored values do not convert. |
| `SampleJson` | For a refusal, a sample of the values that do not convert, `[{"value": ..., "rows": ...}]`, each value cut to 100 characters. |
| `Message` | The full explanation (nvarchar(max)), redacted like every engine text the catalog stores. |
| `ManualStepsJson` | The steps to make the change by hand, a JSON array of strings, or null. |
| `OccurredUtc` | The run's end (when it names none, when it was recorded). |
| `RequestedBy` | Who asked for the run; null for a run recorded from its artifact alone. |

Two indexes serve the reads: `(ObjectKey, ColumnName, OccurredUtc DESC)` for a column's history in its order, and
`RunId` for a run's reports.

### How a report is keyed

The object key is found the way the catalog keys the object, so a row joins to the object's columns and its lineage:

1. Among the objects the run's pipeline writes or creates (its `LineageEdge` rows with relation `Writes` or `Creates`),
   the one with the reported schema and name, in the reported database when the name carries one.
2. For a two-part name (a typed view), the one database those objects agree on, since the catalog may have resolved
   the view's database from its connection.
3. When lineage names no such object (it has not been computed yet, or the name is ambiguous), the key is composed as
   lineage composes a declared object's: the pipeline's `TargetServer`, then the reported database, schema and name.

### How it is written

The rows are written by `CatalogSync.AddRunDetailAsync`, the one path every run detail takes, so the run queue, the
per-run write-back and the full sync record them identically. Projecting a run again replaces its rows rather than
adding to them.

A change that was made to a target column (`Applied`, or `Resumed`) also sets that column's `ObjectColumn.DataType`
to the new type at once, unless the catalog already records a later change of the same column made by another run, so
a run recorded late never moves a column back. The next connected sync reads the column's type from the database
itself.

The rows are kept like the schema-change feed: a history that forgets is not one. They are removed only with their
repository, and the Maintenance page counts them in its Schema changes area ([Maintenance](maintenance.md)).

## The API

Both routes are on the read surface (any signed-in caller; open to an assistant run's delegated token) and page like
every list (`page`, `pageSize`, at most 200 a page), answering `{items, page, pageSize, total}`.

| Route | What it answers |
| --- | --- |
| `GET /api/v1/runs/{runId}/type-changes` | The run's reports, in the order it reported them. 404 for an unknown run. |
| `GET /api/v1/schema-changes/columns?objectKey=&column=` | A column's type history, newest first; without `column`, the history of every column of the object. 400 without `objectKey`. |

Each item is a `ColumnTypeChangeDto`: `id`, `runId`, `pipelineId`, `objectKey`, `table`, `layer`, `column`,
`fromType`, `toType`, `tier`, `status`, `needsAttention`, `rewritesTable`, `isKey`, `requiresOptIn`, `rows`,
`failingValues`, `sample` (`[{value, rows}]`), `message`, `manualSteps`, `occurredUtc` and `requestedBy`.

A run's summary and detail (`GET /api/v1/runs`, `GET /api/v1/runs/{runId}`, and the run group's stream) carry
`warningCount`, and so do the `last` run of a pipeline's duration profile (`GET /api/v1/pipelines/{id}/runs/durations`)
and the CLI's `sqlflow` run detail, which prints `succeeded, 2 warning(s)`.

```bash
# A column's type history, newest first (the object key comes from the object's own page or GET /lineage/objects)
curl -s -G http://localhost:5000/api/v1/schema-changes/columns \
  -H "Authorization: Bearer $TOKEN" \
  --data-urlencode "objectKey=$OBJECT_KEY" --data-urlencode "column=Depth"
```

## The GUI

- **A run's status.** Wherever a run's status shows (the run board, a run's page, a run group, a pipeline's runs, the
  home page's recent runs), a run that succeeded with warnings wears the warning tone and an alert glyph, its label
  saying how many ("succeeded with 2 warnings"). A pipeline header's **Last run** says the count beside the duration.
- **A run's page.** A **Type changes** card lists what the run did about each change: the column, from which type to
  which, the tier and the outcome. Opening a row shows the explanation, how many stored values do not convert with a
  sample of them, and the steps to make the change by hand. A run that considered no change shows no card.
- **A pipeline's page.** When the pipeline's latest finished run left changes that need attention, they show above the
  tabs, with a link to that run. They stay while the latest run reports them, and go once a run no longer does.
