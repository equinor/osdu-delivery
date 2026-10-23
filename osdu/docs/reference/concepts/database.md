---
id: delivery-concept-database
title: "The osdu schema: every table of the module's database, what each is for, and how they are keyed"
type: concept
summary: "Every table of the osdu schema grouped by what it serves, with one line each and its own page; how the tables are keyed, and the objects outside the model."
keywords:
  - osdu schema
  - database tables
  - which table
  - data model
  - module database
  - table pages
  - OsduDbContext
  - PartitionId
  - __EFMigrationsHistory
related:
  - delivery-concept-ledger
  - delivery-concept-architecture
  - delivery-concept-availability-and-retention
  - delivery-concept-dimension-tables
  - delivery-cli-db
  - delivery-table-record
  - delivery-table-schema-version
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/tests/SqlFlow.Delivery.Tests/DatabaseTablePagesTests.cs
---

# The osdu schema: every table of the module's database, what each is for, and how they are keyed

The OSDU Delivery module keeps everything it records in the `osdu` schema, owned by its own EF Core context
(`OsduDbContext`), with its own migration history (`[osdu].[__EFMigrationsHistory]`) and its own schema version
([`osdu.SchemaVersion`](../tables/schema-version.md)). SQLFlow's catalog model holds no table of it, and no table of it has
a foreign key into SQLFlow's tables: a row names a run, a pipeline or a repository by its id alone. The schema lives in
the catalog's database, reached through the catalog's one connection ([Architecture](architecture.md),
[`sqlflow db`](../cli/db.md)).

Each table has a page of its own, of type `table`, in [`osdu/docs/reference/tables/`](../tables/): what one row is, every
column with its SQL type, nullability and what it holds, every key and index with what it serves, and what writes and
reads it. The MCP server's `search_docs` searches them when its `type` is `table`, and leaves them out of a search with no
type, so a question about a task finds the page that explains it; `get_doc`, `list_docs` and `related_docs` reach them as
any page. The suite checks every page against
the model the migrations build (`DatabaseTablePagesTests`): a table, column or index the model has and its page does not
fails it, and so does one a page names that the model does not.

## How the tables are keyed

- **Ledger tables** belong to a ledger: a flow's (or an interface's) rows in one partition, under one ledger identity, the
  `FlowId` the rows carry ([`osdu.Ledger`](../tables/ledger.md)). Their key starts with `PartitionId`, the two-byte number
  [`osdu.LedgerPartition`](../tables/ledger-partition.md) gives each partition, so one partition's rows are one range of
  every clustered index, and every index that serves a listing leads with it.
- **Cache tables** name their partition by its data-partition-id in `Scope`, and are clustered on it where a refresh
  reads them, so refreshes of different partitions never wait on each other.
- **Read models** of the repositories (mappings, interfaces, cache declarations, search terms) and the templates and
  lineage are keyed by stable ids derived from what they describe, and carry no partition number.
- **OSDU ids** are kept in a binary collation (`Latin1_General_100_BIN2`) wherever a table keys or matches on one, since
  OSDU ids that differ only in case are different records; so is the text a dimension compares exactly.

What may be pruned and what never is: [Availability and retention](availability-and-retention.md#retention).

## The tables

### The ledger directory

| Table | One row is |
| --- | --- |
| [`osdu.LedgerPartition`](../tables/ledger-partition.md) | A data partition the ledger has rows in, and the number every ledger key starts with |
| [`osdu.Ledger`](../tables/ledger.md) | A ledger identity: its partition, kind, flow, interface and name |

### Delivering records

| Table | One row is |
| --- | --- |
| [`osdu.Submission`](../tables/submission.md) | A plan of a delivery flow over its ingestion tables: its window, work batches, status and counts |
| [`osdu.Record`](../tables/record.md) | A record a flow delivers: what OSDU holds of it, where it came from, the work queued and its status |
| [`osdu.RecordIdentity`](../tables/record-identity.md) | A value a record can be found by, folded to upper case for prefix lookups |
| [`osdu.Attempt`](../tables/attempt.md) | A try of a record, or a decision about it written as one, append-only |
| [`osdu.WorkBatch`](../tables/work-batch.md) | A file of rendered documents of a submission, claimed and drained as one unit |
| [`osdu.Lease`](../tables/lease.md) | A worker's claim on a work batch or on records due for a retry |
| [`osdu.RecordEvent`](../tables/record-event.md) | A step or a try's outcome a worker appended under its lease, not yet applied |
| [`osdu.SourceWatermark`](../tables/source-watermark.md) | How far the last whole-scope plan of a flow scope read |

### Interventions and history

| Table | One row is |
| --- | --- |
| [`osdu.Activity`](../tables/activity.md) | A run or an operator intervention: the audit trail |
| [`osdu.ActivityRecord`](../tables/activity-record.md) | A record an intervention for many records changed |
| [`osdu.PurgedRecord`](../tables/purged-record.md) | The one line kept of a record deleted from the ledger |
| [`osdu.Artifact`](../tables/artifact.md) | An object a delivery created in OSDU, or set out to create, and how its undo settled it |
| [`osdu.Reversal`](../tables/reversal.md) | A reversal of a run or a submission |
| [`osdu.ReversalItem`](../tables/reversal-item.md) | A record a reversal reaches, what OSDU held before and what the reversal did |

### Inventories

| Table | One row is |
| --- | --- |
| [`osdu.Inventory`](../tables/inventory.md) | An inventory of an inventory flow in a partition |
| [`osdu.InventoryRun`](../tables/inventory-run.md) | A build or reconcile of an inventory |
| [`osdu.InventoryScan`](../tables/inventory-scan.md) | An id a build listed, staged until the build merges |
| [`osdu.InventoryRecord`](../tables/inventory-record.md) | An id an inventory holds or a ledger expects, and its finding |
| [`osdu.InventoryVersion`](../tables/inventory-version.md) | A version storage keeps of a record an inventory holds |
| [`osdu.InventoryRemoval`](../tables/inventory-removal.md) | A removal of an inventory's ids an operator asked for |
| [`osdu.InventoryRemovalItem`](../tables/inventory-removal-item.md) | What one removal did to one id |
| [`osdu.InventorySelection`](../tables/inventory-selection.md) | A match of an operator's conditions against an inventory |
| [`osdu.InventorySelectionItem`](../tables/inventory-selection-item.md) | An id a match found holding what its conditions ask |

### Removals

| Table | One row is |
| --- | --- |
| [`osdu.RemovedKey`](../tables/removed-key.md) | A key a removal flow found a delivery flow's source no longer holds: the ledger's deletion, and how far taking it out has come |
| [`osdu.RemovalRun`](../tables/removal-run.md) | A run of one removal of a removal flow, kept under the delivery ledger it wrote to |
| [`osdu.RemovalKey`](../tables/removal-key.md) | A key a running removal staged from one of its sources or the record table |

### Retrieval and assertions

| Table | One row is |
| --- | --- |
| [`osdu.Retrieval`](../tables/retrieval.md) | A run of a retrieval flow: its window, where its files went, and its outcome |
| [`osdu.AssertionRun`](../tables/assertion-run.md) | A run of an assertion flow's tests in a partition |
| [`osdu.AssertionResult`](../tables/assertion-result.md) | A test's result in one run, with everything its assertions found |

### Dimensions

Every table but the views' record belongs to a ledger of kind `dimension`; [The tables a dimension keeps](dimension-tables.md)
says how they fit and how each dimension's `dim_` table is laid out from them.

| Table | One row is |
| --- | --- |
| [`osdu.Dimension`](../tables/dimension.md) | A dimension of a flow in a partition: its declaration, its `dim_` table, its counts |
| [`osdu.DimensionRun`](../tables/dimension-run.md) | A build of a dimension: how it read, what it found and what it changed |
| [`osdu.DimensionMember`](../tables/dimension-member.md) | A value (clean value) of a dimension |
| [`osdu.DimensionValue`](../tables/dimension-value.md) | A key (original) of a dimension, exactly as the index holds it |
| [`osdu.DimensionAttributeName`](../tables/dimension-attribute-name.md) | An attribute or element field of a dimension, under its number |
| [`osdu.DimensionAttribute`](../tables/dimension-attribute.md) | A value of an attribute of a key |
| [`osdu.DimensionCollectedText`](../tables/dimension-collected-text.md) | A text the records hold at a collected attribute's path |
| [`osdu.DimensionElement`](../tables/dimension-element.md) | A field of an object of a nested array a key's records hold |
| [`osdu.DimensionKeyRecord`](../tables/dimension-key-record.md) | A record a key's label and attributes were read through |
| [`osdu.DimensionRecord`](../tables/dimension-record.md) | A key a record held when a load last read it |
| [`osdu.DimensionChange`](../tables/dimension-change.md) | A change a build made to a key |
| [`osdu.DimensionView`](../tables/dimension-view.md) | A view a dimension flow declares, as its builds last wrote it |
| [`osdu.DimensionViewCheck`](../tables/dimension-view-check.md) | What a build's check of a view found in a partition |

### The partition cache

| Table | One row is |
| --- | --- |
| [`osdu.CacheDefinition`](../tables/cache-definition.md) | A cached type a cache flow declares, copied from the repository |
| [`osdu.CacheVersion`](../tables/cache-version.md) | A version of a partition's cache |
| [`osdu.CacheItem`](../tables/cache-item.md) | A cached record over a run of versions that held it unchanged |
| [`osdu.CacheMember`](../tables/cache-member.md) | A cache flow whose last capture of a type held a record |
| [`osdu.CacheSet`](../tables/cache-set.md) | A distinct set of cached values records were built from |
| [`osdu.CacheSetEntry`](../tables/cache-set-entry.md) | A cached value inside a set |
| [`osdu.UpdateTag`](../tables/update-tag.md) | A cache change reaching delivered records, its decision and rollout |

### Repositories, templates and lineage

| Table | One row is |
| --- | --- |
| [`osdu.Mapping`](../tables/mapping.md) | A mapping document a repository sync found |
| [`osdu.Interface`](../tables/interface.md) | An interface of a delivery flow, per partition it names, and the ledger it keeps |
| [`osdu.SearchTerm`](../tables/search-term.md) | A search term a repository's pipelines give an entity type |
| [`osdu.SearchTermRefinement`](../tables/search-term-refinement.md) | What a person made of a search term |
| [`osdu.RepositoryTemplate`](../tables/repository-template.md) | A file of a repository's template folder, with the kind and version it holds, or why it holds none |
| [`osdu.SchemaLineage`](../tables/schema-lineage.md) | A built release of the OSDU data definitions |
| [`osdu.SchemaLineageKind`](../tables/schema-lineage-kind.md) | A kind of a built release, with the properties that refer to other records |

### Partitions and configuration

| Table | One row is |
| --- | --- |
| [`osdu.Partition`](../tables/partition.md) | An OSDU partition registered with the catalog, and whether it is the default |
| [`osdu.ConfigProperty`](../tables/config-property.md) | A value of the central configuration the control plane supplies to runs |

### The schema itself

| Table | One row is |
| --- | --- |
| [`osdu.SchemaVersion`](../tables/schema-version.md) | The one row naming the schema's version and the last migration applied |

## Objects outside the model

Three kinds of object live in the `osdu` schema with no entity of the model behind them:

- `[osdu].[__EFMigrationsHistory]`: EF Core's record of the module's migrations, written by `sqlflow db migrate`.
- `[osdu].[dim_<dimension>]`: each dimension's own table, created and widened at run time by its builds through SQLFlow's
  schema evolution, and named by [`osdu.Dimension`](../tables/dimension.md) `TableName`
  ([The dimension's table](../flow/dimension.md#the-dimensions-table)).
- `[osdu].[v_dim_<view>]`: each view a dimension flow declares, written from the flow's document alone and recorded in
  [`osdu.DimensionView`](../tables/dimension-view.md), so a build writes and drops only views it made
  ([Dimension views](../flow/dimension-views.md)).

No migration creates or alters the `dim_` tables and `v_dim_` views; every other object of the schema ships with the
migration that makes it.
