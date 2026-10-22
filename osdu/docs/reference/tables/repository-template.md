---
id: delivery-table-repository-template
title: "osdu.RepositoryTemplate: each file of a repository's template folders, as the last sync found it"
type: table
summary: "One row per file in a template folder of a synced repository: the kind and version it holds, or why none. The template is read from the file."
keywords:
  - osdu.RepositoryTemplate
  - template folder
  - templates folder
  - repository template
  - template file
  - template index
  - repository sync
related:
  - delivery-concept-database
  - delivery-concept-templates
  - delivery-table-mapping
  - delivery-cli-template
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Catalog/SyncedTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/RepositoryTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/RepositoryFolders.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Mappings.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
---

# osdu.RepositoryTemplate: each file of a repository's template folders, as the last sync found it

A file of a [template folder](../concepts/templates.md#template-folders) of a synced repository: a `.json` file under a
folder named `templates`, with the kind and version of the template it holds, or why it holds none. The row is an index
of the repository, never the template: whoever reads a template reads it from the file, in the folder the repository was
synced into, and these rows say which file to read. The control plane's pages settle a repository document's template
from them by the same rule a run settles it from the folders, and the Templates page lists them. Every sync writes the
rows of its repository again, adds the row of a file it finds for the first time, and deletes the row of a file the
repository no longer holds, so adding and removing a template is committing or deleting its file. Not a ledger table: it
has no `PartitionId`, and names its repository by id, with no foreign key into SQLFlow's catalog.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository id and the file's path. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository the sync found the file in, by id. |
| `Path` | `nvarchar(400)` | no | The file's path in the repository, with forward slashes (`welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json`). A file whose path is longer is not recorded, and the sync says so. |
| `Folder` | `nvarchar(400)` | no | The template folder the file is in, its path in the repository (`welldb/templates`): what a document's path is walked by. |
| `Kind` | `nvarchar(200)` | no | The kind the file's schema declares in `x-osdu-schema-source`; empty when it holds no template. |
| `Version` | `nvarchar(64)` | no | The template version the file holds, the hash prefix of its canonical schema; empty when it holds none. A reader checks the file still holds it, and one that changed since the sync is refused until the repository syncs again. |
| `Problem` | `nvarchar(2000)` | yes | Why the file holds no template (not JSON, no kind, a reference outside itself, no `data`, too large, changed while the sync read it); null when it holds one. |
| `Parent` | `nvarchar(200)` | yes | The kind the template is linked to as its parent, from its `x-osdu-inheriting-from-kind` ([Linked templates](../concepts/templates.md#linked-templates)); null for a template linked to none, and for a file that holds no template. Added by migration `LinkedTemplateParents` (module version 1.55.0). |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found the file. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found the file: the Templates page's Synced column, and among the files holding one kind, the one a reader naming the kind alone takes first. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RepositoryTemplate` | `Id` | primary key | none | none | A file by its id. |
| `IX_RepositoryTemplate_RepoId_Path` | `RepoId`, `Path` | unique | none | none | A repository's files, once each: what a sync reconciles, what a page settles a document's template from, and the one file a reader opens. |

## Written and read by

- **Written** by the repository sync (`DeliveryCatalogSync.cs`).
- **Read** by the sync's lineage check; the pages that settle a repository document's template (assertions, dimension
  blueprints and views, interfaces, search terms, the mapping views and builder, and
  `GET /api/v1/delivery/templates/resolve`); the Templates page, the explorer's checks and the MCP server's template tools,
  which read every repository's; and the OSDU order (`SyncedTemplates.cs`, `RepositoryTemplates.cs`,
  `OsduLedger.Mappings.cs`, `OrderCatalog.cs`, `DeliveryTemplateEndpoints.cs`). Each reads the template itself from the
  file the row names.
