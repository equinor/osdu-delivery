---
id: delivery-wiki-0018-template-folders
title: "Decision 0018: A repository's template folders settle the template each document fills, and the library stands apart"
type: decision
summary: "Why templates live as files in a repository's template folders, how a document's template is settled, and why the saved templates stay a separate library."
keywords:
  - template folder
  - templates folder
  - template version
  - kind alone
  - nearest folder
  - custom template
  - template library
  - repository templates
referenceRefs:
  - delivery-concept-templates
  - delivery-cli-template
  - delivery-table-repository-template
  - delivery-table-template
  - delivery-flow-mapping
  - delivery-concept-preflight
related:
  - delivery-wiki-0003-rendering-location
  - delivery-wiki-0009-searched-references
  - delivery-wiki-0010-assertion-flows-read-only
  - delivery-wiki-0011-dimension-flows
  - delivery-wiki-0016-osdu-order
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateChain.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSource.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/SyncedTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
updated: 2026-10-21
---
# 0018: A repository's template folders settle the template each document fills, and the library stands apart

Status: proposed. Documented in [templates](../../reference/concepts/templates.md#template-folders) and
[sqlflow template](../../reference/cli/template.md). Amends [0003](0003-rendering-location.md),
[0009](0009-searched-references.md), [0010](0010-assertion-flows-read-only.md), [0011](0011-dimension-flows.md) and
[0016](0016-osdu-order.md), each of which named the saved template as what a document is read against.

## Context

A mapping, a search, an assertion test and a dimension each read an OSDU schema, a template, by its kind and its
content version. Until this decision every template lived only in the module's database, saved from the Templates page
or `sqlflow template`. Three things followed from that:

- The repository a developer edits in VS Code did not hold the templates its documents filled. Writing or reviewing a
  mapping meant reading the schema in the GUI, and a working copy could not be checked without the database.
- A template of one's own (a custom kind, or a kind extended for one project) had to be saved through the GUI or the
  command line before any document could use it; committing it with the mapping did nothing.
- Several projects in one repository may fill different versions of one kind. A version is exact, so pinning by hash
  already told them apart, but nothing said which version a project holds as its own.

The database stays useful: the explorer, the mapping builder, the language assistance of the MCP tools and the runtime
suggestions read the library without a working copy.

## Decision

- **A template folder is a folder named exactly `templates`**, anywhere in a repository, holding template files: bundled
  OSDU record schemas that name their kind in `x-osdu-schema-source`. A repository may hold many. The version of a file
  is the hash of its canonical schema, as before, so a copy in any folder or repository is the same version.
- **A document reads the template folders on its path**, nearest first: the one beside it, then each folder above, up to
  the repository's top (the sync's root, or the folder holding `.git`), sixteen levels at most when no top is found.
- **A document that names a version** (`template.version`, a search's `schema.version`, a test's version) pins it
  exactly: the nearest folder holding that version answers. **A document that names the kind alone** fills the one
  version the nearest folder holding the kind holds; a folder holding two versions of that kind is an error naming both,
  never a guess.
- **The folders are the only thing that settles a document's template.** A version the library holds and no folder on
  the path holds is never a document's template. A mapping whose template no folder on its path holds does not load,
  and the sync says where it looked. The one exception is the validation gate: a record already rendered for an exact
  version is still checked against that version from the library once its file has changed, when someone saved it
  there, since every copy of a version is the same schema.
- **The library stands apart.** `[osdu].[Template]` holds only what a person saves, from the Templates page or the
  command line, for what reads templates where no repository is at hand: the explorer, the mapping builder and the
  assistant. A sync saves nothing in it. It records each file in `[osdu].[RepositoryTemplate]` with a copy of the
  file's schema, which the control plane's pages read to settle a synced repository's documents without its working
  copy. Nothing a repository holds keeps a saved version from being deleted, and the Templates page says nothing of
  repositories or of the mappings that fill a version.
- **Any kind is a template**, OSDU's or one's own: importing a schema writes it as a file of a folder (`--out`),
  declaring its kind when the schema did not, and the next sync uses it with no GUI step.

## Alternatives considered

- **Keep templates in the database alone.** Rejected for the reasons in the context: the working copy could not be
  read or checked on its own, and a template of one's own needed a step outside the repository.
- **Remove the library.** Rejected: the explorer, the builder and the assistant read templates where no repository is
  at hand, and the gate can check a record rendered before its file changed against a version someone saved.
- **Mirror the repositories' templates into the library.** Built first and taken back before it was released: every
  sync saved what the folders held, and the Templates page listed the files holding each version and refused to delete
  one a folder held. It tied the library to the repositories, so its list grew with every repository's files and a
  delete depended on files elsewhere, when the library only serves reading templates where no repository is at hand. A
  copy kept with each file's row serves the control plane's pages without it.
- **A template path in each document.** Rejected in favour of folders: the path would be repeated in every mapping,
  search, test and dimension, would break when files move, and would leave a document that names a kind alone without
  a rule. A folder on the path gives each project of a repository its own versions with no path written anywhere.

## Consequences

- An estate whose templates were saved only in the library must commit them as files before its next sync loads its
  mappings: `sqlflow template show --kind <kind> --version <version> --out <folder>/<file>` writes each one, and an
  exact pin keeps its version ([Upgrading an estate](../../reference/concepts/templates.md#upgrading-an-estate-whose-templates-were-saved-only-in-the-library)).
- `sqlflow check`, a run, lineage and the sync read the files, so a working copy is checked before it is committed;
  `sqlflow template folders --for <document>` shows what a document reads.
- The Templates page no longer counts the mappings that pin a version, and a saved version is deleted whatever a
  mapping names, since mappings read their folders. A row recorded before copies were kept reads as missing its copy
  until the next sync of its repository.
- The order the catalog keeps for a repository leaves out a delivery whose mapping's template no folder holds, since
  that mapping does not load; lineage still orders it by a built release of OSDU's lineage
  ([0016](0016-osdu-order.md)).
- The mapping builder proposes a mapping with its template's file when no folder on the mapping's path holds it yet.
