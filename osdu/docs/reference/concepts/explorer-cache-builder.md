---
id: delivery-concept-explorer-cache-builder
title: "Building a cached type in the explorer: picking what the cache keeps on the records"
type: concept
summary: "The explorer's cache builder: Build, A cached type, its steps from the records to the type, picks made on the records' values, the preview, and the YAML."
keywords:
  - build a cached type
  - cache builder
  - build a cache type
  - keep in the cache
  - add as an attribute
  - cache yaml
  - types item
  - preview the records
  - cache preview
  - what to build
  - build menu
  - dimension or cached type
  - write the flow file
  - whole cache flow file
  - cached already by
related:
  - delivery-concept-explorer
  - delivery-concept-explorer-dimension-builder
  - delivery-flow-cache
  - delivery-flow-cache-attributes
  - delivery-flow-cache-records
  - delivery-concept-partition-cache
  - delivery-flow-mapping-lookups
  - delivery-guide-cache-flow-file
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Cache.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheTypeBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheSample.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/ExploreOperation.cs
  - osdu/gui/src/features/delivery/explorer/BuilderMode.tsx
  - osdu/gui/src/features/delivery/explorer/cache/CacheBuildPanel.tsx
  - osdu/gui/src/features/delivery/explorer/cache/CacheSteps.tsx
  - osdu/gui/src/features/delivery/explorer/cache/CachePreview.tsx
  - osdu/gui/src/features/delivery/explorer/cache/CacheFieldActions.tsx
  - osdu/gui/src/features/delivery/explorer/cache/cacheDraft.ts
  - osdu/gui/src/features/delivery/explorer/cache/cacheStory.ts
  - osdu/gui/src/features/delivery/explorer/cache/useCacheBuild.ts
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderFoot.tsx
  - osdu/gui/src/features/delivery/explorer/dimension/BuilderFrom.tsx
---

# Building a cached type in the explorer: picking what the cache keeps on the records

**Build**, in the [explorer](explorer.md)'s header, offers what to build from the records in view: **A dimension**
([the dimension builder](explorer-dimension-builder.md)) or **A cached type**. The cache builder docks beside the
records, as the dimension builder does, and writes one item of a [cache flow](../flow/cache.md)'s `types:` list from
picks made on the records themselves. It saves nothing: no flow, nothing in the cache, nothing in OSDU. **Copy the
YAML** takes the item, to paste into a cache flow.

A cached type is what a mapping looks records up in while it delivers: the wellbores it links a well log to, found by
their name; the units it resolves a source's spelling against. Build one when a mapping needs to find an OSDU record by
something other than its id, or to write a value another record holds.

## One builder at a time, and the + follows it

The explorer builds one thing at a time. The head of the docked builder says which (**New dimension** or **New cached
type**) and opens the same choice: the other one starts a new draft from the same records, keeping the kind and the
query and nothing else, so copy the YAML first.

The **+** beside each value of a record offers what that value can be in the thing being built:

| Building | In a record of the type | In a record one of its links leads to |
| --- | --- | --- |
| A dimension | **One row for each value**, or **Collect into a column** | **Name each row by this**, or **Add as a column** |
| A cached type | **Keep in the cache** | **Add as an attribute** |

A cached type has one choice at each depth, so its **+** makes it at once, with no menu; its tooltip says what the click
does, or why it cannot be made. A value the draft reads already wears a mark beside it: the name it is kept under, or
`to <attribute>` on a link an attribute is read through.

## The steps

The builder tells the type as a story read top to bottom. Each step opens to its settings.

| Step | What it holds | Written as |
| --- | --- | --- |
| **From** | The records cached: their kind, every version of the type or the open record's alone, and a query narrowing them. | `kind`, `query` |
| **Keeps** | The values kept of each record, each under a name. A record's id is always kept. | `fields` |
| **With** | Attributes: values read through the records a cached record links to, kept on the cached record. | `attributes` |
| **Read** | Whether each record is read whole from storage, and whether a change waits for approval. | `fetchRecords`, `onChange` |
| **Makes** | The type, as a mapping reads it (`$cache.Wellbore`), the flows caching it already, and the lookup that finds a record of it. | |

- **The name** heads the panel. Left empty, the type takes the kind's own (`Wellbore` of `master-data--Wellbore`), which
  is what a cache flow does when a type names none.
- **Keeps.** Open a record of the type and click **+** beside a value. A property of the record's data keeps the name
  its path gives (`data.FacilityName` is `FacilityName`). A value inside an object or a list is kept under the property
  it ends with (`data.NameAliases.AliasName` as `AliasName`), since the path's own name would hold a dot. Rename one in
  the step; a path keeps whatever it yields, a list or an object whole.
- **With.** Follow a link of the record to the record it names, and click **+** beside a value there. The attribute
  reads every link on the way, the first in the cached record itself, through at most two other records
  ([cache attributes](../flow/cache-attributes.md)). Where a step passes through one item of a list whose items answer
  differently (a wellbore's geo contexts), the builder asks which, and writes the filter, as the dimension builder does.
- **Read.** **Read each record whole from storage** keeps a value the search index leaves out, such as one under
  `data.ExtensionProperties` ([records read whole](../flow/cache-records.md)). **Changes wait for approval** holds the
  records built from a changed value until someone approves it on the Cache page
  ([onChange](../flow/cache.md#onchange)).

## What is checked

The control plane writes the draft as YAML and reads it back through the loader a cache flow is read by, so the item is
exactly what a flow would declare. What the loader refuses is an error at the step it is about, in the loader's words.

A partition has one cache, filled by every cache flow declaring a type of a name, their values added up
([the partition cache](partition-cache.md)). So the draft is compared with the cache flows of the catalog:

| Finding | Shown as |
| --- | --- |
| A cache flow caches a type of this name already, and the draft agrees with it | A note under **Makes**, naming the flow: the item can go in that flow, or in another filling the same partition. |
| A flow caches the name from another kind | A warning at the name: a refresh into a partition both fill is refused. |
| A flow keeps a value of the same name from another path | A warning at the value: the two must read it alike. |

A warning does not stop the YAML. The foot says **Ready to paste**, or how many things to put right, a click away from
the step each is at.

## The preview

**Preview** reads the first 25 records the search finds, through the partition's own connection, and makes them what
the cache would keep: a column for each value under the name a mapping reads it by, the attributes read through the
records they name, the records whole from storage when the type reads them so. It is the read a refresh makes of a page
of the type, so the preview is what a refresh of that YAML would cache. Nothing is written.

Above the grid it says how many records the type would hold. Under it, a path no sampled record holds is named, with
the likely cause (a path the search index leaves out wants the records read whole), and what the attributes' reads had
to say.

## The YAML, and the whole flow file

**YAML** shows the item as it is copied, the findings above it.

**Write the flow file**, beside **Copy the YAML** once the YAML loads and while the assistant is configured, asks the
assistant for the whole cache flow file holding the type, modelled on the estate's cache flows
([Writing a cache flow file](../guides/cache-flow-file.md)). The conversation opens in the assistant's panel, which
slides in over the records and leaves the builder in view.

## Kept in the address

The draft is the explorer's address (its `cache` part), so a link, Back and a refresh land on the same build. A
dimension's draft is the `dim` part; an address holding both builds the dimension.

## The API

| Route | Scope | Answers |
| --- | --- | --- |
| `POST /api/v1/delivery/explorer/cache/compose` | operate | A draft written as a cache flow's item and checked: `yaml`, `lines`, `type` (as the loader read it), `issues`, `valid`, `declaredBy`. |
| `POST /api/v1/delivery/explorer/cache/preview` | operate | The first records as a refresh would cache them: `total`, `columns` (each with how many records gave it), `records`, `notes`; 400 while a refresh would refuse the draft. |
