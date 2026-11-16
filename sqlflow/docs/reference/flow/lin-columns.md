---
id: flow-lin-columns
title: "Column mappings in a lineage description (writes[].columns)"
type: flow-reference
summary: "The optional column level of a lineage description: for a written object's columns, the source columns they come from and how each value is made."
keywords:
  - column mapping
  - column lineage
  - source to target mapping
  - where does this column come from
  - writes columns
  - from
  - alias
  - star column
  - lineage description
  - flowtype lin
yamlPath: "writes[].columns"
related:
  - flow-lin
  - concept-described-lineage
  - guide-describe-an-upstream-step
sourceRefs:
  - src/SqlFlow.Yaml/YamlLineageDescriptionFlowLoader.cs
  - src/SqlFlow.Core/Lineage/LineageDescriptionFlow.cs
  - src/SqlFlow.Lineage/Graph/LineageDescriber.cs
  - src/SqlFlow.Catalog/CatalogProjection.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.ControlPlane/Api/DescribedLineage.cs
  - docs/reference/flow/keys.lin.json
  - samples/lineage-described/lineage/orders-u1-curate.yaml
---

# Column mappings in a lineage description

A [lineage description](lin.md) connects objects. Under an object it writes, it may also say where columns come from: for each mapped column, the source columns and how the value is made. This is an optional level of detail:

- **A description is complete without it.** SQLFlow's lineage is object-level everywhere, and a description with no `columns` loses nothing.
- **It may be partial.** A column that is not listed is not mapped, which says nothing about whether it exists.
- **It takes no part in lineage.** Mappings are stored beside the described connections and read only on request. No graph, wave or list reads them, and the lineage graph in the GUI never draws them.
- **It is for questions.** It answers "what is the mapping from this table to that file" and "where does this column come from", for a person or an assistant.

## Example

```yaml
flowType: lin
name: orders-u1-curate-lin
system: lakehouse
reads:
  - { system: erp, name: sales.orders, as: src, origin: true }
  - { system: erp, name: sales.exchange_rates, as: fx, origin: true }
writes:
  - name: curated.orders
    columns:
      order_id: src.order_no                 # one source
      customer_id: src.customer_no
      order_date: { from: src.ordered_at, note: Cast to a date in UTC. }
      amount_eur:
        from: [src.amount, src.currency, fx.rate]
        note: amount * rate of the order's currency on the order date.
      loaded_at:                             # the step makes it itself
```

A source is written `alias.column`, where the alias is the `as` of a `reads` entry. The alias names the read object; the rest, after the first dot, is the column, which may itself hold dots or colons.

## The forms of a column

| Form | Meaning |
| --- | --- |
| `col: alias.source` | The value comes from one column. |
| `col: [a.x, b.y]` | The value is made from several columns. |
| `col: { from: ..., note: ... }` | The same, with how the value is made. |
| `col:` (nothing) | The step makes the column itself: a timestamp, a hash, a generated key. |
| `col: { note: ... }` | Made by the step, with how. |

`note` is free text: a cast, a unit conversion, an expression, a rule. At most 2000 characters. There is no list of transformation types to choose from.

## Every other column

A `*` in a column name stands for every column not listed by name, and takes a source with a `*` of its own, for the column of the same name:

```yaml
columns:
  order_id: src.order_no          # listed by name
  "*": "src.*"                    # every other column, carried across by name
```

Text around the star is kept, which covers a step that prefixes or renames a whole set:

```yaml
columns:
  "raw:*": "src.*"                # raw:amount comes from src.amount
```

A name carries at most one `*`. A source may carry one only when its column does. When a column is asked for by name, its own mapping answers; the star entry answers only for a column no entry names.

## Rules the reader checks

| Problem | Message says |
| --- | --- |
| A source without an alias (`from: id`) | A source is written `alias.column`. |
| An alias no `reads` entry declares | Which aliases exist. |
| A column mapped twice | Column names are compared without regard to case. |
| A source listed twice in one column | Which one. |
| More than one `*` in a name | A name carries at most one. |
| A `*` source for a column without one | It goes with a mapped column that has a `*` too. |
| More than 2000 columns on one object, or 50 sources on one column | The limit. Use a `*` entry for columns carried across by name. |
| More than 5000 columns, or 20000 sources, in one document | The document's limit. Use `*` entries, or describe the step in several documents. |
| A credential in a `note`, or a `${...}` reference in a column or a source | A description holds no secret and resolves nothing. |

A source whose `reads` entry could not be placed in lineage (it names a flow that does not exist, say) is left out of the mapping with a finding. The column, and its other sources, stay.

## Asking for a mapping

Mappings are served by the control plane on request, and through it by the assistant's `column_mapping` tool:

| Request | Answer |
| --- | --- |
| `GET /api/v1/lineage/described/columns?to=<key>` | Each mapped column of the object, with its direct sources. |
| `...?to=<key>&from=<key>` | Only what comes from that object, followed through every described step between the two. Each source lists the columns in between under `via`. |
| `...?to=<key>&column=<name>` | One column. |
| `...?pipelineId=<id>` | Everything one description maps. |

Object keys come from a lineage walk that includes described lineage, or from the list of described steps. `depth` bounds how many steps a column is followed through (default 8, at most 12).

An answer that holds nothing says why in `notes`: no description maps the object's columns, the column asked for is not mapped, or the two objects are not connected by column mappings. None of them means the column does not exist. `truncated: true` means a limit cut the answer.

A column is followed through described steps only. The estate's own flows state no column mapping here, so a column is not followed across one: from a landed file back to the source system works, and from a table the estate loads back past the file that fed it does not. [The column trace](../concepts/column-trace.md) does cross the estate's flows, for one column at a time: `GET /api/v1/lineage/objects/column-trace`, and the assistant's `trace_column`. It uses these mappings for the described steps, and says of each step across a flow whether the flow declares the column or the column is only assumed to keep its name.

Following a column through several steps reads each object's mappings once, and a column already followed on a path is not followed again, so mappings that feed each other end. A column made from many columns, each made from many again, multiplies the paths at every step, so one answer follows at most 20000 columns, reads at most 60 objects and reports at most 500 sources for a column; past any of them it stops and says `truncated: true`. Narrow the question with `column` to get a whole answer.

A `*` entry is followed too. One step up, its source is the `*` of the object it reads. Further up, it becomes every column that object maps by name, other than the ones the description lists by name itself (those it does not stand for), plus that object's own `*` entry for the rest. So a file written with `"*": "orders.*"` from a table that maps `order_id: src.order_no` answers `order_no`, by way of `order_id`, when asked what comes from `src`.

## What lands in the catalog

`catalog.LineageColumnMapping` holds one row per source column of a mapped column, or a single row without a source for a column the step makes itself. Rows are repo-scoped and replaced wholesale on each sync, like lineage edges.

| Column | Holds |
| --- | --- |
| `PipelineId`, `Flow` | The description declaring the mapping. |
| `TargetObjectKey`, `TargetObjectName`, `TargetColumn` | The written object and column. |
| `Ordinal` | The column's position among the object's mapped columns, in declaration order. |
| `SourceOrdinal` | The source's position among the column's sources; 0 on the row of a column without one. |
| `SourceObjectKey`, `SourceObjectName`, `SourceColumn` | The read object and column; null for a column the step makes itself. |
| `Note` | How the value is made. |

## See also

- [Lineage description](lin.md)
- [Described lineage](../concepts/described-lineage.md)
