---
id: flow-lin
title: "Lineage description (flowType: lin)"
type: flow-reference
summary: "flowType: lin describes a step another system runs, so lineage continues before a landed file or after a written table. Metadata only, never executed."
keywords:
  - lineage description
  - flowtype lin
  - described lineage
  - upstream lineage
  - drop-off zone
  - landing zone
  - where does the data come from
  - step another system runs
  - feeds
  - fedBy
  - metadata only
  - never executed
  - databricks
  - fabric
  - origin
yamlPath: "(root, flowType: lin)"
related:
  - flow-lin-columns
  - concept-described-lineage
  - guide-describe-an-upstream-step
  - flow-overview
  - concept-lineage-graph-and-plan
  - cli-lineage
  - cli-validate
sourceRefs:
  - src/SqlFlow.Yaml/YamlLineageDescriptionFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Core/Lineage/LineageDescriptionFlow.cs
  - src/SqlFlow.Lineage/Graph/LineageDescriber.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Catalog/FlowLoadProfiles.cs
  - docs/reference/flow/keys.lin.json
  - samples/lineage-described/lineage/orders-u2-publish.yaml
---

# Lineage description (flowType: lin)

A `flowType: lin` document describes one step that another system runs: a job on a data platform, a pipeline, a notebook, a manual export. It says what the step reads and what it writes, so lineage can be followed past the flows SQLFlow runs: back to the source a landed file was made from, or on to what consumes a table the estate writes.

It is metadata, and only that:

- **It is never executed.** There is nothing to run, schedule or trigger. `sqlflow run`, a trigger, a schedule and a batch all refuse it with the same sentence.
- **It moves no data and connects to nothing.** It declares no connection, no credential and no location.
- **It changes nothing SQLFlow computes.** Waves, dependencies, object levels and data streams are the same with or without descriptions. What a description says is kept beside the computed lineage as [described lineage](../concepts/described-lineage.md), and is shown only when asked for.
- **It is optional.** An estate without descriptions is complete. A description may cover one step of a chain and leave the rest undescribed.

## Minimal example

```yaml
flowType: lin
name: orders-u2-publish-lin
system: lakehouse
reads:
  - { name: curated.orders }
writes:
  - feeds: described-land-orders
```

This says: a step on the system `lakehouse` reads `curated.orders` and writes what the flow `described-land-orders` reads. The location of those files is written once, in that flow, and never repeated here.

## What each line does

| Line | Effect |
| --- | --- |
| `flowType: lin` | The document is a lineage description. It becomes a pipeline row of kind `lin`, stored with mode `disabled`, in no wave, and left out of the listings of pipelines unless asked for by its kind. |
| `name:` | The description's identity: the pipeline's name, and the flow its connections are attributed to. |
| `system:` | The system that performs the step. Also the system of every object named without one. |
| `reads:` / `writes:` | One connection per entry, each a described edge from the step to an object. |
| `name:` on an entry | An object the estate does not manage. It gets a described object, keyed by its system and name. |
| `feeds:` on a write | The object a flow of the estate reads. The description is connected to the node that flow already declares. |
| `fedBy:` on a read | The object a flow of the estate writes. Same, in the other direction. |
| `context:` | Prose on what happens to the data. Stored with the document, read by people and by an assistant, never parsed. |
| `columns:` on a write | The optional [column mappings](lin-columns.md). |

## Keys reference

| Key | Type | Required | Description |
| --- | --- | --- | --- |
| `flowType` | string | yes | Must be `lin`. |
| `name` | string | yes | The description's name. One line, at most 400 characters, and a name no other flow of the repository has. |
| `description` | string | no | What the step does, in a sentence. At most 1000 characters. |
| `batch` | string | no | A grouping label. No other effect. |
| `system` | string | yes | Who performs the step: `databricks`, `fabric`, `adf`, `dbt`, `manual`, or any name. A letter, then letters, digits or hyphens, at most 32. Kept lower-case. |
| `unit` | string | no | Where the step is found in that system: a job and task, a pipeline and activity, a notebook. Free text, at most 512. |
| `owner` | string | no | Who to ask about the step. |
| `url` | string | no | Where the step can be looked at. Shown as written, never fetched. May not carry a credential. |
| `evidence` | list | no | What the description was written from. Each entry is a path, or a mapping with `path` and optionally `repo`, `commit`, `note`. At most 50. |
| `reads` | list | one of `reads`, `writes` | What the step reads. At most 200 entries. |
| `reads[].name` | string | one of `name`, `fedBy` | An object by its name, as its system writes it. A `*` stands for a family of objects named alike. |
| `reads[].system` | string | no | The object's system, when it is not the description's own. Goes with `name`. |
| `reads[].fedBy` | string | one of `name`, `fedBy` | The flow of the estate whose output the step reads. |
| `reads[].at` | string | no | Which of that flow's objects, when it writes several: its name or a part of it. Goes with `fedBy`. |
| `reads[].as` | string | no | An alias for the object, used by column mappings. |
| `reads[].origin` | bool | no | `true` when the object is where the data starts. Default `false`. |
| `reads[].note` | string | no | What the object holds. At most 2000 characters. |
| `writes` | list | one of `reads`, `writes` | What the step writes. At most 200 entries. |
| `writes[].name` | string | one of `name`, `feeds` | An object by its name. |
| `writes[].system` | string | no | The object's system, when it is not the description's own. Goes with `name`. |
| `writes[].feeds` | string | one of `name`, `feeds` | The flow of the estate that reads what the step writes. |
| `writes[].at` | string | no | Which of that flow's objects, when it reads several. Goes with `feeds`. |
| `writes[].note` | string | no | What the object holds. |
| `writes[].columns` | map | no | [Column mappings](lin-columns.md). |
| `context` | string | no | What happens to the data, in prose. At most 20000 characters. |

`docs/reference/flow/keys.lin.json` is the full census with the exact validation, and is what the editor reads.

The reader is strict. An unknown key, a value of the wrong shape, a value over its limit or a repeated entry keeps the document from loading, and every problem is reported with its line, all of them together:

```text
lineage/step.yaml: 2 problems in the lineage description:
  lineage/step.yaml:4: the lineage description has 'schedule', which a flow that runs takes; a lineage description is never executed, so remove it.
  lineage/step.yaml:9: 'from' of column 'x' of writes entry 1 names the alias 'z', but no reads entry declares an alias; give the read object an 'as'.
```

A key a running flow takes (`schedule`, `mode`, `lifecycle`, `connections`, `parameters`, `source`, `target`, `onErrorResume`) is refused with the reason.

Three rules hold for every value, because a description is stored, shown and handed to an assistant as written:

| Rule | Applies to | Message says |
| --- | --- | --- |
| No credential | Every value, prose included | `appears to carry a credential`. The value is never echoed. |
| No `${...}` reference | Every value that names something: `name`, `feeds`, `fedBy`, `at`, `as`, `system`, `batch`, `url`, an evidence `repo`, `path` and `commit`, a column and its sources | `holds a reference`. A description resolves nothing, so write the value plainly. |
| Totals | The document | At most 5000 mapped columns and 20000 source columns over everything it writes, however the mappings are written (a YAML anchor repeated under several writes counts each time). |

Prose (`description`, `unit`, `owner`, every `note`, `context`) may quote a reference, since nothing resolves it.

Inside `{ }` a comma starts the next key, so a note that holds a comma goes in quotes. The reader says so when it meets one:

```text
lineage/step.yaml:9: reads entry 1 reads 'and why.' as a key with no value; inside { } a comma starts the next key, so put a value that holds a comma in quotes.
```

## Naming an object

An entry names its object in exactly one of two ways.

**By name**, for an object the estate does not manage:

```yaml
reads:
  - { system: erp, name: sales.orders, origin: true }
  - { name: curated.orders }          # in the description's own system
```

Write the name as its system writes it, and leave out anything that differs between environments: `curated.orders`, not a catalog, a workspace or a storage account. A description is the same document in development and in production. Two descriptions that name the same system and name mean the same object, whatever case they use. That is the whole of how descriptions chain: one writes the object the next reads.

A `*` stands for a family of objects named alike (`raw.orders_*`, one table per region). The name is the identity as written; nothing is matched against it.

**By a flow of the estate**, for an object the estate already holds:

```yaml
writes:
  - feeds: described-land-orders                     # what that flow reads
reads:
  - { fedBy: orders-03-publish, at: dbo.Orders }     # what that flow writes
```

`feeds` and `fedBy` connect the description to the node the named flow already declares, so the estate's location is never restated and a change to it needs no change here. When the flow reads (or writes) more than one object, `at` says which: the object's name or a part of it. A whole name wins over a part, so `at: Orders` means the table and not also a view named `v_Orders`.

## Where the data starts

`origin: true` on a read marks an object as where the data starts. A lineage walk reports such an object as the origin. An object a description reads that no description writes, and that is not marked, is reported as an open end: what produces it is not described. That is a fact a reader is told, never a fault, because descriptions are optional.

## Context

`context` is where the meaning goes, in prose. Nothing parses it. It is what a person, or an assistant answering a question about the data, reads to learn what the step does. Useful headings:

| Heading | Answers |
| --- | --- |
| Rows | Which rows are left out or added, and why |
| Keys | How a key is formed |
| Timing | When the step runs and what starts it |
| Deleted records | What becomes of a record deleted at the source |
| Repair | How to rerun or correct the step |
| Unknown | What could not be determined |

## When a connection cannot be placed

A description that cannot be used in full is used in part. Each of these leaves one connection out with a finding that says what to write, and keeps the rest of the step:

| Finding | Cause |
| --- | --- |
| `names flow 'x' with 'feeds', which this repository does not declare` | The flow's name is misspelt, or the flow is in another repository. |
| `names 'x' with 'feeds', which is itself a lineage description` | Two descriptions meet on an object: name the same object in one's `writes` and the other's `reads`. |
| `that flow reads 3 objects (...)` | Add `at` with a part of the name of the one meant. |
| `at 'y', which matches none of the objects that flow reads (...)` | The findings list what the flow does read. |
| `has the name of the ing flow in <file>` | A pipeline is identified by its name. The description is left out until it has a name of its own. The flow keeps its pipeline, its wave and its run history, whichever of the two documents is read first. |
| `is declared by 2 documents` | The first in path order is used. |
| `reads entry 2 is the same object as an earlier reads entry` | It is counted once. Its alias still names the object, so columns mapped through it keep their source. |

Findings are reported by `sqlflow lineage` and by a catalog sync, prefixed `described lineage:`.

## CLI

```text
$ sqlflow validate lineage/orders-u2-publish.yaml
OK  'orders-u2-publish-lin' is valid (lineage description of a lakehouse step: 1 read(s), 1 write(s), 5 mapped column(s); metadata only, never run).

$ sqlflow lineage .
Lineage over 1 flow(s), 2 object(s), 2 edge(s) (declared)
  wave 1: described-land-orders
  described lineage: 2 step(s) other systems run, 3 object(s) only they name, 9 mapped column(s); no part of the plan above
    orders-u1-curate-lin (lakehouse): sales.orders [origin], sales.exchange_rates [origin] -> curated.orders
    orders-u2-publish-lin (lakehouse): curated.orders -> data/orders (of flow described-land-orders)

$ sqlflow run lineage/orders-u2-publish.yaml
ERROR  a lineage description (flowType: lin) describes a step another system runs and is never executed; it is read for lineage only, so there is nothing to run, schedule or trigger.
```

The flow and edge counts, and the waves, are those of the estate's own flows. The described steps are listed apart.

## Full example

The estate in `samples/lineage-described` lands a daily orders file. Two descriptions say where the file comes from:

```yaml
# lineage/orders-u1-curate.yaml
flowType: lin
name: orders-u1-curate-lin
description: Builds the curated orders table from the order system's orders and exchange rates.
system: lakehouse
unit: job orders-daily, task curate_orders
owner: data-platform@example.com
evidence:
  - { repo: lakehouse-jobs, path: jobs/orders/curate_orders.py, commit: 4f1c2ab9, note: The transformation. }
reads:
  - { system: erp, name: sales.orders, as: src, origin: true }
  - { system: erp, name: sales.exchange_rates, as: fx, origin: true }
writes:
  - name: curated.orders
    note: One row per order, with the amount in euro.
context: |
  ## Rows
  - Order lines are summed to one row per order.
  - Test orders (customer_no below 1000) are left out.
  ## Deleted orders
  An order deleted in the order system stays in the curated table: nothing removes it.
```

```text
erp sales.orders ─┐
                  ├─▶ orders-u1-curate-lin ─▶ curated.orders ─▶ orders-u2-publish-lin ─▶ data/orders ─▶ described-land-orders
erp exchange_rates┘                                                                      (the estate's own flow, wave 1)
```

## See also

- [Column mappings in a lineage description](lin-columns.md)
- [Described lineage](../concepts/described-lineage.md): how it is kept apart from the computed lineage, and how to see it.
- [Describing a step another system runs](../guides/describe-an-upstream-step.md): what to write for a job on a data platform.
- [sqlflow lineage](../cli/lineage.md)
