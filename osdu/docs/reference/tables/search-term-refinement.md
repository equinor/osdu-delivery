---
id: delivery-table-search-term-refinement
title: "osdu.SearchTermRefinement: what a person made of a search term, kept apart from what a sync writes"
type: table
summary: "One row per search term a person refined: the name it is searched by, whether it is left out, the route it is searched through, a note, and who changed it."
keywords:
  - osdu.SearchTermRefinement
  - search term refinement table
  - rename a term
  - delete a term
  - restore a term
  - route
related:
  - delivery-concept-database
  - delivery-concept-search-terms
  - delivery-table-search-term
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTermDirectory.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliverySearchTermCatalog.cs
---

# osdu.SearchTermRefinement: what a person made of a search term, kept apart from what a sync writes

What a person made of a [search term](../concepts/search-terms.md#refining-a-term): the name it is searched by, whether
it is left out of the search, which of its routes it is searched through, and a note on it, with who changed it last and
when. Kept by the term's identity, apart from the terms a sync writes ([`osdu.SearchTerm`](search-term.md)), so it holds
across every sync and every mapping version that keeps the column, and is listed when no sync gives the term. A sync
changes none of it, except to move a refinement made while terms were keyed by their mapping's source system, entity
type and dataset onto the term that column is now; the newest of several moves, and the rest are listed as no longer
found. Deleting a term a pipeline still reads sets `Excluded` and keeps the row; deleting a term no longer
found removes the row for good ([Deleting and restoring terms](../concepts/search-terms.md#deleting-and-restoring-terms)).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `TermId` | `uniqueidentifier` | no | The term refined. |
| `TermKey` | `nvarchar(600)` | no | The term's key when it was refined, which names it while no mapping gives it. |
| `EntityType` | `nvarchar(200)` | no | The entity type the term searches, within which no two terms share a name. |
| `Name` | `nvarchar(100)` | yes | The name the term is searched by; null to keep the column's own. |
| `Excluded` | `bit` | no | Set when the term is left out of the search (deleted). |
| `Route` | `nvarchar(1100)` | yes | The route the term is searched through; null for the one the explorer prefers. |
| `Note` | `nvarchar(1000)` | yes | A note on the term, shown beside it wherever it is offered; null for none. |
| `UpdatedBy` | `nvarchar(200)` | no | Who changed the refinement last. |
| `UpdatedUtc` | `datetime2` | no | When it was last changed. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_SearchTermRefinement` | `TermId` | primary key | none | none | A term's refinement by its identity. |
| `IX_SearchTermRefinement_EntityType_Name` | `EntityType`, `Name` | index | none | none | A name is the term's within its entity type: a rename checks it is free there. |

## Written and read by

- **Written** by the Search terms page through the search terms API (`SearchTermDirectory.cs`).
- **Moved** by the sync, for refinements made under the earlier term keys (`DeliverySearchTermCatalog.cs`).
- **Read** by the Search terms page and the explorer.
