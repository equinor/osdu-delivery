---
id: delivery-table-template
title: "osdu.Template: each saved OSDU schema version, the library the explorer, the builder and the assistant read"
type: table
summary: "One row per saved template: the OSDU kind, the content version (a hash of the bundled schema), the bundled JSON Schema, where it came from and who saved it."
keywords:
  - osdu.Template
  - template table
  - saved template
  - OSDU schema
  - template version
  - schema capture
  - import
related:
  - delivery-concept-database
  - delivery-concept-templates
  - delivery-cli-template
  - delivery-table-mapping
  - delivery-table-repository-template
  - delivery-table-schema-lineage
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
---

# osdu.Template: each saved OSDU schema version, the library the explorer, the builder and the assistant read

A saved template, the library ([Templates](../concepts/templates.md#the-library-saved-templates)): the OSDU schema of one
kind, captured from the OSDU data definitions or imported from a file by a person. A version is identified by the kind
and the hash of its schema and never changes. It is not linked to the repositories: their documents read their templates
from their [template folders](../concepts/templates.md#template-folders), whose files the sync keeps copies of with their
own rows ([`osdu.RepositoryTemplate`](repository-template.md)), never here. The library is what the Templates page, the
explorer, the mapping builder and the MCP server's template tools read, and a run reads a version from here only to check
a record against the exact version it was rendered for when no folder holds that version any more and someone saved it.
Not a ledger table: it has no `PartitionId`.

A version is deleted through `DELETE /api/v1/delivery/templates` (or `sqlflow template delete`); nothing a repository
holds refuses it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the kind and the version. |
| `Kind` | `nvarchar(200)` | no | The OSDU kind the schema describes (`osdu:wks:work-product-component--WellLog:1.4.0`). |
| `Version` | `nvarchar(64)` | no | The content version: the hash prefix of the canonical bundled schema. |
| `SchemaJson` | `nvarchar(max)` | no | The bundled JSON Schema, every reference resolved into its definitions. |
| `Origin` | `nvarchar(1000)` | no | Where the schema came from: the OSDU data definitions release and file, or the imported file. |
| `CapturedBy` | `nvarchar(200)` | no | Who captured or imported it. |
| `CapturedUtc` | `datetime2` | no | When it was saved. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Template` | `Id` | primary key | none | none | A template by its id. |
| `IX_Template_Kind_Version` | `Kind`, `Version` | unique | none | none | A kind's version, once. |

## Written and read by

- **Written** by `sqlflow template capture` and `import`, and the Templates page, through `POST /api/v1/delivery/templates`
  (`TemplateStore.cs`, `DeliveryTemplateEndpoints.cs`). Never by a repository sync.
- **Read** by the Templates page, the explorer, the mapping builder, the MCP server's template tools, and a run checking a
  record against the version it was rendered for when no template folder holds it any more (`FlowRuntime.cs`).
