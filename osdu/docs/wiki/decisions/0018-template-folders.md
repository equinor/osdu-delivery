---
id: delivery-wiki-0018-template-folders
title: "Decision 0018: A repository's template folders hold the templates, and every reader reads the file"
type: decision
summary: "Why templates live only as files in template folders, how a document's template is settled, and why the database keeps an index and no copy."
keywords:
  - template folder
  - templates folder
  - template version
  - kind alone
  - nearest folder
  - custom template
  - template library
  - repository templates
  - download template
referenceRefs:
  - delivery-concept-templates
  - delivery-cli-template
  - delivery-table-repository-template
  - delivery-flow-mapping
  - delivery-concept-preflight
  - delivery-concept-gui
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
  - osdu/src/SqlFlow.Delivery/Catalog/SyncedTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/RepositoryTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/RepositoryFolders.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
updated: 2026-10-22
---
# 0018: A repository's template folders hold the templates, and every reader reads the file

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
- **The repository is the only place a template lives.** A repository adds a template by committing its file and removes
  one by deleting it. The sync records each file of each template folder in `[osdu].[RepositoryTemplate]`: its path,
  folder, kind and version, or why it holds no template. That row is an index, never a copy: nothing of a schema is kept
  in the database.
- **Every reader reads the file.** A run, `sqlflow check` and lineage read the checkout in front of them. The control
  plane's pages, the Templates page, the explorer, the mapping builder and the MCP tools find the file through the index
  and read it from the folder the repository was synced into, found as a node finds a run's checkout (the folder the
  sync read, a local source where it lives, or the synced commit checked out from the remote). A file that changed since
  the sync is refused until the repository syncs again, so a page never reads a version the index does not record.
- **Any kind is a template**, OSDU's or one's own: `sqlflow template capture` and `import` write a bundled file into a
  folder (`--out`, now required), declaring its kind when the schema did not, and the next sync records it with no GUI
  step. The Templates page lists the repositories' files and downloads a kind of the OSDU data definitions as the file a
  folder keeps.

## Alternatives considered

- **Keep templates in the database alone.** Rejected for the reasons in the context: the working copy could not be
  read or checked on its own, and a template of one's own needed a step outside the repository.
- **Keep a library of saved templates beside the folders.** Built first: `[osdu].[Template]` held what a person saved
  from the Templates page's import and browse tabs or the command line, for the explorer, the mapping builder and the
  assistant, and the sync kept a copy of each file's schema on its row for the control plane's pages. Taken back before
  it was released. It was a second path to the same thing: a version could be read from the library, from a row's copy
  or from the file, two of them could disagree with the repository, and the Templates page showed a library no document
  read. With the index pointing at the file and the file readable wherever the repository is synced, every reader has
  one source, and the GUI shows what the repositories hold.
- **Mirror the repositories' templates into the library.** Built before that and taken back too: every sync saved what
  the folders held, and the Templates page listed the files holding each version and refused to delete one a folder
  held. The library's list grew with every repository's files and a delete depended on files elsewhere.
- **A template path in each document.** Rejected in favour of folders: the path would be repeated in every mapping,
  search, test and dimension, would break when files move, and would leave a document that names a kind alone without
  a rule. A folder on the path gives each project of a repository its own versions with no path written anywhere.

## Consequences

- An estate whose templates were saved in the database must have every version its documents need as a file in a
  template folder before it upgrades: module schema 1.54.0 drops the library and the copies
  ([Upgrading an estate](../../reference/concepts/templates.md#upgrading-an-estate-that-saved-templates-in-the-database)).
  A version a release publishes is downloaded again from Browse OSDU or written with `sqlflow template capture --out`.
- `sqlflow check`, a run, lineage and the sync read the files, so a working copy is checked before it is committed;
  `sqlflow template folders --for <document>` shows what a document reads. The command line's `template` verb needs no
  database: `list`, `show` and `delete`, which worked on the library, are gone.
- The validation gate checks a record against the version it was rendered for while a folder on the mapping's path
  holds it, and calls the record `unverified` once none does, rather than reaching for a saved copy.
- A page that reads a repository's template needs the repository's files on its host, or the means to check out the
  synced commit; a repository whose folder cannot be had says why, rather than answering from a stale copy.
- The order the catalog keeps for a repository leaves out a delivery whose mapping's template no folder holds, since
  that mapping does not load; it reads a template from the file the sync recorded, and from a built release of OSDU's
  lineage when the file cannot be read ([0016](0016-osdu-order.md)).
- The mapping builder starts a mapping from a file the repository's folders hold and proposes the mapping alone, into
  the `mappings` folder beside that template folder.
