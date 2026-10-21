---
id: delivery-table-repository-template
title: "osdu.RepositoryTemplate: each file of a repository's template folders, as the last sync found it"
type: table
summary: "One row per file in a folder named templates of a synced repository: the template it holds with a copy of its schema, or why it holds none."
keywords:
  - osdu.RepositoryTemplate
  - template folder
  - templates folder
  - repository template
  - template file
  - template copy
  - repository sync
related:
  - delivery-concept-database
  - delivery-concept-templates
  - delivery-table-template
  - delivery-table-mapping
  - delivery-cli-template
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Catalog/SyncedTemplates.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Mappings.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
---

# osdu.RepositoryTemplate: each file of a repository's template folders, as the last sync found it

A file of a [template folder](../concepts/templates.md#template-folders) of a synced repository: a `.json` file under a
folder named `templates`, with the template it holds and a copy of its schema, or why it holds none. A repository's
documents read their templates from the template folders on their path, from the files themselves; these rows are what
the control plane's pages settle a document's template from without the files, by the same rule, and the copy is what
they read its schema from, so a page and a run agree on the version. Every sync writes the rows of its repository again
and deletes the row of a file the repository no longer holds. The copy is the repository's own: the library of saved
templates, [`osdu.Template`](template.md), never holds it and never reads it. Not a ledger table: it has no
`PartitionId`, and names its repository by id, with no foreign key into SQLFlow's catalog.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository id and the file's path. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository the sync found the file in, by id. |
| `Path` | `nvarchar(400)` | no | The file's path in the repository, with forward slashes (`welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json`). A file whose path is longer is not recorded, and the sync says so. |
| `Folder` | `nvarchar(400)` | no | The template folder the file is in, its path in the repository (`welldb/templates`): what a document's path is walked by. |
| `Kind` | `nvarchar(200)` | no | The kind the file's schema declares in `x-osdu-schema-source`; empty when it holds no template. |
| `Version` | `nvarchar(64)` | no | The template version the file holds, the hash prefix of its canonical schema; empty when it holds none. |
| `Problem` | `nvarchar(2000)` | yes | Why the file holds no template (not JSON, no kind, a reference outside itself, no `data`, too large, changed while the sync read it); null when it holds one. |
| `SchemaJson` | `nvarchar(max)` | yes | The schema the file holds as the sync read it, compact JSON whose canonical form hashes to `Version`; null when the file holds no template, and on a row recorded before copies were kept, until the next sync. |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found the file. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found the file. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RepositoryTemplate` | `Id` | primary key | none | none | A file by its id. |
| `IX_RepositoryTemplate_RepoId_Path` | `RepoId`, `Path` | unique | none | none | A repository's files, once each: what a sync reconciles, what a page settles a document's template from, and the copy of the one it settles on. |

## Written and read by

- **Written** by the repository sync (`DeliveryCatalogSync.cs`).
- **Read** by the sync's lineage check, the pages that settle a repository document's template and read its copy
  (assertions, dimension blueprints and views, interfaces, search terms, the mapping views and
  `GET /api/v1/delivery/templates/resolve`), and the OSDU order (`SyncedTemplates.cs`, `OsduLedger.Mappings.cs`,
  `OrderCatalog.cs`, `DeliveryTemplateEndpoints.cs`). Never by the Templates page or a template's delete.
