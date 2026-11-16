---
id: guide-describe-an-upstream-step
title: "Describing a step another system runs: from its code to a lineage description"
type: guide
summary: "How to write lineage descriptions for a pipeline another platform runs: one document per hop, what to name, and what to put in context."
keywords:
  - lineage description
  - flowtype lin
  - upstream pipeline
  - databricks job
  - fabric
  - dataflow
  - notebook
  - drop-off zone
  - landing zone
  - extract lineage from code
  - agent
  - evidence
  - context
related:
  - flow-lin
  - flow-lin-columns
  - concept-described-lineage
  - cli-validate
  - cli-lineage
sourceRefs:
  - src/SqlFlow.Yaml/YamlLineageDescriptionFlowLoader.cs
  - src/SqlFlow.Lineage/Graph/LineageDescriber.cs
  - samples/lineage-described/lineage/orders-u1-curate.yaml
  - samples/lineage-described/lineage/orders-u2-publish.yaml
---

# Describing a step another system runs

An estate often lands files, or reads tables, that another platform produces. SQLFlow's lineage starts at that file or table. A [lineage description](../flow/lin.md) continues it: a small YAML document per step, written from the code of the step, that says what the step reads and writes and what it does to the data.

This guide is for whoever writes those documents, a person or an agent reading the code.

## One document per hop between stored objects

A description is one hop from stored objects to stored objects: tables, file sets, an API's entities. Follow the code from where it reads to where it persists, and cut there.

| In the code | In descriptions |
| --- | --- |
| A job with three tasks, each writing a table | Three documents, chained by the tables. |
| Temporary views and data frames inside one task | Nothing: they are inside the hop. Say what they do in `context`. |
| One routine run for many sources, writing one table per source | One document, with a `*` in the object name (`raw.orders_*`) and a note saying what the star stands for. |
| A step that only moves or renames files | One document: it is still a hop. |
| A manual export someone uploads | One document, `system: manual`. There is no code, so no `evidence`. |

Name each document so the chain reads in order, for example `<source>-<object>-u1-<step>-lin`, `...-u2-...-lin` for the steps upstream of a landing flow.

## Start from the estate and work back

1. Find the flow of the estate that reads what the other system writes: the file flow over the landing folder, say.
2. Write the last step first. Its `writes` entry is `feeds: <that flow>`. Nothing about the location is repeated.
3. Name what that step reads, by system and name.
4. Write the step that produces those objects, with the same system and name under its `writes`. The two now chain.
5. Stop where the data starts, and mark those reads `origin: true`. Or stop earlier: a chain that ends without an origin is reported as an open end, which is honest.

Check as you go:

```text
sqlflow validate lineage/orders-u2-publish.yaml
sqlflow lineage .
```

`sqlflow lineage` prints each described step with what it reads and writes, and a finding for any connection it could not place, with what to write instead.

## What goes where

| Key | For a job on a lakehouse platform | For a Microsoft Fabric item | In general |
| --- | --- | --- | --- |
| `system` | `databricks` | `fabric` | A short name for the system that runs the step. |
| `unit` | The job and task: `job orders-daily, task curate_orders` | The workspace, item and activity: `workspace sales, pipeline load-orders, activity copy_orders` | How a person would look the step up. |
| `evidence` | The job definition, the code of the task, its configuration | The item's folder in the Git-connected repository (a notebook's `notebook-content.py`, a dataflow's `mashup.pq`) | The files the description was written from, with the commit. |
| Object `name` | `schema.table`, without the catalog | `lakehouse.schema.table`, without the workspace | The name as the system writes it, without what differs between environments. |
| A file set | The folder under the volume or container, without the account | The path under the lakehouse's `Files` | The folder, as the next reader knows it. |

Environments differ in catalogs, workspaces and storage accounts. Leave those out of object names: a description is the same document everywhere, and two descriptions chain by naming an object the same way. If the environment-specific name helps a reader, say it in the object's `note`.

One object reached under two names (a table and a shortcut to it, a storage path and a volume over it) is one object: pick one name and use it in every description.

## What to put in context

`context` is read by an assistant answering questions like "why is this record missing", "where does this value come from" and "when does a change reach us". Write what answers them:

- **Rows.** Every filter, inner join, deduplication and explosion: what is left out or added, and why. Say where a left-out row is reported, if anywhere.
- **Keys.** How a key is built, from which columns, with what escaping.
- **Timing.** What starts the step, when, and what it waits for.
- **Deleted records.** Whether a record deleted at the source is removed, marked or kept.
- **What the next reader relies on.** Write order, atomic renames, immutable folders, one writer at a time.
- **Repair.** How the step is rerun.
- **Unknown.** What could not be determined from the code, and why. Never guess.

Add [column mappings](../flow/lin-columns.md) where the code holds them plainly, such as a select list or a mapping table. Skip them where columns are built at run time; say so in `context`.

## For an agent writing descriptions

- Read the job or pipeline definition first: it names the tasks, their order and their parameters.
- Resolve names the code builds from configuration to the names they take, and use a `*` where one routine serves many.
- Quote rules and expressions from the code in `context` and in column `note`s, rather than paraphrasing them into something the code does not say.
- Pin `evidence` to the commit read. A later reader tells an old description from a current one by it.
- Validate every document, then run `sqlflow lineage` and resolve each finding.
- A description is reviewed like any flow: in a pull request, by someone who knows the step.

## Keeping descriptions current

Nothing in SQLFlow fetches the other system's code, so nothing notices when it changes. Two habits keep descriptions true:

- Change the description in the same pull request as the code, when both are in reach of one review.
- Otherwise compare the pinned `commit` with the code's history when a question's answer matters, and say in the answer which commit the description was written from.

## See also

- [Lineage description (flowType: lin)](../flow/lin.md)
- [Described lineage](../concepts/described-lineage.md)
- `samples/lineage-described`: a landing flow and the two descriptions upstream of it.
