---
id: delivery-table-schema-lineage
title: "osdu.SchemaLineage: OSDU's lineage as one release of the OSDU data definitions declares it"
type: table
summary: "One row per built release of the OSDU data definitions: its tag and commit, who built it and when, its kinds and relationships, and what failed."
keywords:
  - osdu.SchemaLineage
  - schema lineage table
  - OSDU lineage
  - data definitions release
  - OSDU order
  - relationships
related:
  - delivery-concept-database
  - delivery-concept-osdu-order
  - delivery-table-schema-lineage-kind
  - delivery-table-template
  - delivery-wiki-0016-osdu-order
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ordering/SchemaLineageStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
---

# osdu.SchemaLineage: OSDU's lineage as one release of the OSDU data definitions declares it

OSDU's lineage as one release of the OSDU data definitions declares it: which release and commit it was read from, when and
by whom it was built, and how much it holds. What each kind refers to is in [`osdu.SchemaLineageKind`](schema-lineage-kind.md).
A release is built once and kept; building it again replaces its kinds under the same row. A kind the release lists that
cannot be read is kept as a failure with why ([OSDU's lineage by release](../concepts/osdu-order.md#osdus-lineage-by-release)).
The OSDU order reads a kind from the newest built release holding it when no template folder on the mapping's path holds
the kind's template. Not a ledger table: it has no `PartitionId`.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | The release's row id. |
| `Release` | `nvarchar(100)` | no | The release's tag (`v0.29.0`). |
| `Commit` | `nvarchar(64)` | no | The commit the release's tag names. |
| `PublishedUtc` | `datetime2` | yes | When the release was published, when the repository says. |
| `BuiltUtc` | `datetime2` | no | When it was built. |
| `BuiltBy` | `nvarchar(200)` | no | Who built it: a user, or the command line's local account. |
| `Kinds` | `int` | no | How many kinds it holds. |
| `Relationships` | `int` | no | How many properties of those kinds refer to other records. |
| `FailuresJson` | `nvarchar(max)` | no | The kinds the release lists that could not be read, each with why: a JSON array of `{ "kind", "reason" }`. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_SchemaLineage` | `Id` | primary key | none | none | A release's row by its id. |
| `IX_SchemaLineage_Release` | `Release` | unique | none | none | A release is built once: building it again replaces its kinds under the same row. |

## Written and read by

- **Written** by `POST /api/v1/delivery/order/releases` and the Order page's **Releases** tab (`SchemaLineageStore.cs`).
- **Read** by the OSDU order, `GET /api/v1/delivery/order/releases`, and the repository sync, which computes a
  repository's lineage again when a release built after its mappings were synced holds a kind one of them fills that no
  template folder on its path holds (`DeliveryCatalogSync.cs`).
