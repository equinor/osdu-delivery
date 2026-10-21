---
id: delivery-table-schema-lineage-kind
title: "osdu.SchemaLineageKind: each kind of a built release, with the properties of its schema that refer to other records"
type: table
summary: "One row per kind of a built OSDU release: its entity type, version and status, and every property of its schema that refers to other records."
keywords:
  - osdu.SchemaLineageKind
  - schema lineage kind table
  - OSDU lineage
  - relationships
  - x-osdu-relationship
  - entity type
related:
  - delivery-concept-database
  - delivery-concept-osdu-order
  - delivery-table-schema-lineage
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ordering/SchemaLineageStore.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
---

# osdu.SchemaLineageKind: each kind of a built release, with the properties of its schema that refer to other records

One kind of a [release's lineage](schema-lineage.md): its entity type and version, its status in the release, and every
property of its schema that refers to other records, with the entity types each names, as the OSDU order reads a schema
([The OSDU order](../concepts/osdu-order.md)). Building a release again replaces its kinds.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `LineageId` | `uniqueidentifier` | no | The release ([`osdu.SchemaLineage`](schema-lineage.md)). |
| `Kind` | `nvarchar(400)` | no | The kind (`osdu:wks:master-data--Wellbore:1.3.0`). |
| `EntityType` | `nvarchar(256)` | no | The entity type it is of (`master-data--Wellbore`). |
| `Version` | `nvarchar(64)` | no | The kind's version (`1.3.0`). |
| `Status` | `nvarchar(32)` | yes | The kind's status in the release (`PUBLISHED`, `DEVELOPMENT`), when the release says. |
| `RelationshipsJson` | `nvarchar(max)` | no | The properties that refer to other records, each with the entity types its relationship names: a JSON array of `{ "property", "targets": [...] }`. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_SchemaLineageKind` | `LineageId`, `Kind` | primary key | none | none | A release's kind, once. |
| `IX_SchemaLineageKind_LineageId_EntityType` | `LineageId`, `EntityType` | index | none | none | The lineage page reading a release's kinds by entity type. |
| `IX_SchemaLineageKind_Kind` | `Kind` | index | none | none | The OSDU order looking a kind up across releases. |

## Written and read by

- **Written** with its release (`SchemaLineageStore.cs`).
- **Read** by the OSDU order (`OrderCatalog.cs`), the Order page's **Releases** tab and
  `GET /api/v1/delivery/order/releases/{release}/entities` and `.../graph`.
