---
id: delivery-table-search-term
title: "osdu.SearchTerm: each search term a repository's pipelines give an entity type, written by every sync"
type: table
summary: "One row per repository, term and entity type: a source column the active delivery flows read, with every route by which it reaches the records they render."
keywords:
  - osdu.SearchTerm
  - search term table
  - search terms
  - explorer
  - source column
  - routes
  - find records by source value
related:
  - delivery-concept-database
  - delivery-concept-search-terms
  - delivery-concept-explorer
  - delivery-table-search-term-refinement
  - delivery-table-mapping
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliverySearchTermCatalog.cs
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTermDirectory.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/SearchTermCatalogRefreshService.cs
---

# osdu.SearchTerm: each search term a repository's pipelines give an entity type, written by every sync

A [search term](../concepts/search-terms.md) a repository's pipelines give for one entity type: a column of a source
table the repository's active delivery flows read, with every route by which it reaches the records of that type they
render. A term is the table's column, whichever flows read the table and whichever mappings render it; a row is the term
in one repository and one entity type. The sync writes the rows again whenever it runs, from the flows and the mappings
they pin, and deletes a term no pipeline gives any longer. What a person makes of a term is kept apart, in
[`osdu.SearchTermRefinement`](search-term-refinement.md), by the term's identity, so a sync never undoes it. Not a ledger
table: it has no `PartitionId`. Added by migration `20261008170839_SearchTerms`.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository id, the entity type and the term's key. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository whose pipelines give the term, by id. |
| `TermId` | `uniqueidentifier` | no | The term's identity, the same in every repository, entity type and host: derived from its key alone. |
| `TermKey` | `nvarchar(600)` | no | The term's key as text: its table and column (`osdudata.arc.welllog/wellbore_uwi`), at most 600 characters. |
| `Source` | `nvarchar(400)` | no | The table the column is read from, its parts without brackets (`OsduData.arc.WellLog`). |
| `EntityType` | `nvarchar(200)` | no | The entity type the routes fill (`work-product-component--WellLog`). |
| `Column` | `nvarchar(128)` | no | The column as the mappings name it. |
| `RoutesJson` | `nvarchar(max)` | no | The routes by which the column reaches the records of the entity type, as JSON ([Routes](../concepts/search-terms.md#routes)). |
| `FlowsJson` | `nvarchar(max)` | no | The active delivery flows whose mappings read the column, as a JSON list of names. |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found it. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_SearchTerm` | `Id` | primary key | none | none | A term row by its id. |
| `IX_SearchTerm_RepoId_TermId_EntityType` | `RepoId`, `TermId`, `EntityType` | unique | none | none | A repository's term of an entity type, once. |
| `IX_SearchTerm_EntityType_TermId` | `EntityType`, `TermId` | index | none | none | The Search terms page and the explorer listing the terms of one entity type, from every repository. |

## Written and read by

- **Written** by the repository sync and the control plane's start (`DeliverySearchTermCatalog.cs`,
  `SearchTermCatalogRefreshService.cs`), never by a person.
- **Read** by the Search terms page, the explorer, inventory selections, and the search terms API
  (`SearchTermDirectory.cs`).
