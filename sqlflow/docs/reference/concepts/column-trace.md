---
id: concept-column-trace
title: "Column trace: one column followed back to its source"
type: concept
summary: How one column is followed back through the flows SQLFlow runs and the steps lineage descriptions state, what each step rests on, and how the answer says what is missing.
keywords:
  - column trace
  - trace a column
  - column lineage
  - where does this column come from
  - explain the lineage of a column
  - trace_column
  - information missing
  - assumed by name
  - declared transform
  - described lineage
  - origin
  - assistant
related:
  - concept-described-lineage
  - flow-lin-columns
  - flow-lin
  - concept-lineage-graph-and-plan
  - guide-describe-an-upstream-step
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/ColumnTrace.cs
  - src/SqlFlow.ControlPlane/Api/DescribedLineage.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - tests/SqlFlow.ControlPlane.Tests/ColumnTraceApiTests.cs
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/chat/columnLineage.ts
---

# Column trace

A column trace follows one column of one object back toward its source: through the flows SQLFlow runs, and through the steps [lineage descriptions](../flow/lin.md) (`flowType: lin`) state for other systems. It answers "where does this value come from, and what happens to it on the way".

SQLFlow's lineage is object-level: it records which objects a flow reads and writes, not which columns. So a trace is built from what the catalog holds beside the lineage, and it is strict about the difference:

- **Every step says what it rests on.** A step is stated by a description, declared by a flow, or only assumed because the column keeps its name.
- **Every gap is reported as missing.** Where nothing says what a value comes from, the path ends there with a sentence saying what is missing. Nothing is filled in.

## What a step rests on

| `basis` | The step is known from | Carries |
| --- | --- | --- |
| `described` | The [column mapping](../flow/lin-columns.md) of a lineage description. | `note` (how the value is made), `system` and `unit` (where the step runs). |
| `declared` | What the flow's own document declares for the column (`transform.columns`). | `expression` and `dataType`, and the source column when the flow renames it. |
| `name` | Nothing but the lineage: the flow reads the source object and writes this one, and the column is taken to keep its name. | `sourceColumnKnown: true` when the catalog's column list of the source object holds the column. |

A `name` step is an assumption, and a reader says so. The trace narrows it with the catalog's column lists: when a flow reads several objects, only those known to hold the column are followed. An object with no whole column list in the catalog (a file, a table no run has created yet) is followed on the name alone. A list made only of what flows declare for an object shows that a column is there, never that one is not.

What a flow reads through a view it also reads is reached through the view, one step further on.

## Where a path ends

| `kind` | Meaning | Missing? |
| --- | --- | --- |
| `origin` | A lineage description marks the object as where the data starts (`origin: true`). | no |
| `made` | A step makes the value itself: a described column with no source, or a column the flow computes. `detail` holds the note or the expression. | no |
| `flow` | A flow writes the object and reads no object the lineage records: the value starts in that flow, whose document says from where. | no |
| `notDescribed` | Nothing writes the object: no flow, and no lineage description. It is where the recorded lineage starts: the source itself, or something whose producer is not written down. | yes |
| `notMapped` | A lineage description writes the object, and its column mapping does not cover the column. `detail` lists what the step reads. | yes |
| `derived` | A flow writes the object, and none of the objects it reads holds a column of that name: the flow derives or renames it. Its document says how; the lineage does not. | yes |
| `limit` | The trace stopped at its depth or at a size limit. | yes |

`outcome` sums the ends up: `complete` when every path ends at a source, `partial` when some do, `missing` when none does. `missing` lists the sentences of the ends that are missing, and that the column is not in the object's column list when the catalog knows it is not.

An estate with no lineage description answers `describedLineageUsed: false` and ends at the object its first flow reads, as `notDescribed`. That is the answer: what happens before the estate reads the data is not recorded.

## Asking

```text
GET /api/v1/lineage/objects/column-trace?key=<object key>&column=<name>[&depth=<1-24>]
```

| Parameter | Meaning |
| --- | --- |
| `key` | The object's key, from a search, a dossier or a lineage walk. An object only descriptions name may be traced too. |
| `column` | One column by its name. A `*` is refused. |
| `depth` | How many steps the column is followed through. Default 12, at most 24. |

The assistant asks with `trace_column(key, column)`. It is told to give the path from the source to the column, to say which steps are assumed, and to report every entry of `missing` as missing information with what would supply it: a lineage description of the step that writes the object, or a `columns` mapping in the description named. To say what a described step does to the rows, it reads that step's document (`get_pipeline`), whose `context` says.

In the GUI, pages that list an object's columns offer the question as **Explain its lineage**, answered in the Explain drawer.

## Example

A table loaded from a landed file, with two descriptions of the steps before the file:

```text
dbo.Orders . order_id
  <- dbo.v_OrdersPre . order_id        ing flow            name
  <- data/orders . order_no            landing flow        declared   CAST([order_no] AS int)
  <- curated.orders . order_no         orders-u2-publish   described  lakehouse
  <- sales.orders . order_no           orders-u1-curate    described  lakehouse
  origin: sales.orders (erp)
outcome: complete
```

The same question for a column the second description does not map:

```text
dbo.Orders . status
  <- dbo.v_OrdersPre . status          ing flow            name
  <- data/orders . status              landing flow        name
  notMapped: The lineage description 'orders-u2-publish' writes 'data/orders', and its column mapping does
             not cover column 'status'. The step reads 'curated.orders'; which of their columns it comes from
             is not written down.
outcome: missing
```

## Limits

- **One column at a time.** A `*` is refused; a mapping of every column of an object is [the column mapping](../flow/lin-columns.md).
- **Upstream only.** The trace goes toward the source. What depends on a column is not traced.
- **Bounded.** At most 200 columns are followed at one depth, and one answer holds at most 400 steps and 200 ends. Past a limit the answer says `truncated: true`, and a path cut by the depth ends as `limit`.
- **A step by name is as good as the catalog's column lists.** Without a whole list of the source object, the trace cannot tell a column carried by name from one the flow makes under that name.
- **Flows that are not loads.** A stored procedure, an export or a module's body may compute a column from several. The trace says `derived` or follows the name; the flow's document or the object's script holds the rest.

## See also

- [Described lineage](described-lineage.md)
- [Column mappings in a lineage description](../flow/lin-columns.md)
- [Lineage description](../flow/lin.md)
