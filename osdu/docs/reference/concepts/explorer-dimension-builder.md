---
id: delivery-concept-explorer-dimension-builder
title: "Building a dimension in the explorer: picking it on the records, step by step"
type: concept
summary: "The explorer's dimension builder: its steps from the records to the table, picks made on the records' values, the example row, the preview, and the YAML."
keywords:
  - build a dimension
  - dimension builder
  - pick a key
  - one row per
  - name each row by this
  - row name
  - add as a column
  - collect into a column
  - open the record a key names
  - example row
  - preview the table
  - dimension preview
  - rename a column
  - copy the yaml
  - dimension yaml
  - key suggestions
  - clean steps
  - where a key's record holds nothing
related:
  - delivery-concept-explorer
  - delivery-flow-dimension
  - delivery-flow-dimension-attributes
  - delivery-flow-dimension-clean
  - delivery-concept-dimension-blueprint
  - delivery-guide-dimensions
  - delivery-concept-templates
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Dimension.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionKeyCandidates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionSampler.cs
  - osdu/gui/src/features/delivery/explorer/dimension/DimensionBuildPanel.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderStory.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderRow.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderPreview.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderYaml.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/StoryFrame.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuildFieldActions.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderFilterDialog.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/dimensionStory.ts
  - osdu/gui/src/features/delivery/explorer/dimension/dimensionDraft.ts
  - osdu/gui/src/features/delivery/explorer/dimension/useDimensionBuild.ts
---

# Building a dimension in the explorer: picking it on the records, step by step

**Build a dimension**, in the [explorer](explorer.md)'s header, docks the dimension builder beside the records (the
workbench's side bar folds meanwhile). The builder writes the `dimensions:` item of a [dimension flow](../flow/dimension.md)
from picks made on the records themselves, as they are browsed and their links followed, and previews the table the
item makes. It saves nothing: no flow, no dimension, nothing in OSDU or the database. **Copy the YAML** takes the
item, to paste into a flow.

## The story

The builder tells the dimension as one sentence, a step per choice, read top to bottom along a rail:

| Step | Says | Its settings |
| --- | --- | --- |
| **From** | the records read: their type, every version or one, how many there are | the kind; every version of the type, or the open record's version alone; a query narrowing the records (`query`) |
| **One row per** | the key (`path`): the type of record its values name, or the property, and how many keys there are | the keys the kind's template suggests; counting each key's records exactly (`countRecords`); the most keys (`maxValues`) |
| **Named by** | the row's name (`label`): a value read in the record the key names, or, when none is picked, the code the key's id ends with (a key naming no record, its own text) | the path it is read through; the name a row takes where its key's record holds nothing there (`unlabelled`) |
| **With** | the columns beside the name (`attributes`), to narrow by | each column, where it is read or collected, removed one by one |
| **Cleaned** | how each row's name is cleaned before rows are grouped by it (`clean`) | the plain steps, picked in order, and replacements of a pattern |
| **Makes** | the table a build writes, `osdu.dim_<name>`, and its example row | the name of each column, changed in the row |

The rail marks where each step stands: chosen, asking for a pick, open (nothing chosen, which a dimension does well
without), or waiting for a step before it. Only the step asking for a pick says what to do, with the one action that does
it: a type at first, then the key, then, when the key names records, what a row is named by. A step's settings open in
place when its sentence is clicked; the others stay one line each.

A row's name is the dimension's value: what its filters, its pages and its views show, and what its rows are grouped
by, keys whose names clean alike being one value. The columns beside it are carried as they are read. A key naming a
record is best named by something that record holds, such as its name, so **Named by** asks for it next.

The dimension's name heads the builder, and its table is named after it. Starting from a type, the builder takes the
likeliest key the type's template suggests (one the open record holds a value at, when a record is open), and names the
dimension after the type of record that key names.

## Picking on the records

Beside each value of a record a **+** offers what that value can be, with the words of the steps; its tooltip says
which, and a click opens them:

| Where the record is | Choices |
| --- | --- |
| A record of the dimension's kind (the first on the trail) | **One row for each value**: the key. **Collect into a column**: every value a row's records hold here, with how many hold each. |
| A record reached from it through the key, link after link (up to three) | **Name each row by this**: the row's name. **Add as a column**: a column read from the record the key names or one it names. |

A value marks what the draft makes of it (the key, the name, a column, or a link on the way to one). A record
reached through another link than the key's offers that link as the key instead. A pick passing through one item of a
list whose items answer differently asks which items are meant and suggests the filter keeping the one picked.

**Open the *type* record**, in the **Named by** and **With** steps, opens the record the example key names on the trail,
ready for its values to be picked: from the open record when it holds the key, else from a record of the kind the search
finds holding it, which opens first.

## The example row

**Makes** ends with one row of the table as a build would make it, for one key: the open record's own first, then those
the most records hold, stepped through with the arrows. Each column shows its name and what the key reads there; a
column's name is changed in the row, and one left as the build names it is not written in the YAML. The row says how many
records hold its key, and **How it is built** draws the records searched, each record found by id, and the table, filled
with the row ([a dimension's blueprint](dimension-blueprint.md)). **Preview the table** shows its rows.

## The preview

**Preview**, beside **Build** and **YAML** once the draft has a key, shows the table a build of the YAML would write:
the rows of the 100 keys most records hold, read from OSDU through the partition's connection as a build reads them,
each key made into its row as the example's is. The rows are laid out as [the table](../flow/dimension-table.md) is:
a row a key, or, for a dimension collecting a column, a row for each value a key collects, with the records holding
that value; under the names the columns take, with the records of each row. The line above says how many rows the
keys made and of how many keys, or that they are the whole table.

Under the rows are the keys that make no row (cleaning left them no name) and what the reads had to say: a key longer
than a dimension keeps, more keys than `maxValues` allows (a build of that fails before it writes), a template the
synced repositories do not hold. The preview is read while it is shown, once the YAML loads, and again as the draft
changes; until the YAML loads it says so, and the foot names what to put right. Nothing is written.

## Problems and the YAML

The control plane writes the draft as YAML and reads it back through the loader a flow is read by, so the builder finds
what a flow would refuse. Each finding is said at the step it is about (a column's under the column, the name's under
the name) and marks that step on the rail; a finding about the whole is said at **Makes**. The foot of the builder says
whether the YAML is ready to paste, and names the first thing to put right, which a click shows where it is. A template
a synced repository does not hold is a warning, with where to download one.

**YAML** shows the item as it is copied, the findings above it, each lighting the lines it is about.

## Kept in the address

The draft lives in the explorer's address (`dim`), so a link or a refresh brings it back; closing the builder drops it, and
asks first once the draft has a key. The view shown, the story, the preview or the YAML, is not kept. The routes the
builder calls are in [the explorer's API](explorer.md#the-api).
