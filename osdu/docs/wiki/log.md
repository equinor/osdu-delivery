# Wiki log

Append-only, chronological. Newest entries go at the bottom. Every entry starts with the same
prefix so the log stays greppable:

```
grep "^## \[" osdu/docs/wiki/log.md | tail -5
```

Entry format, enforced by SQLFlow's [lint_wiki.py](../../../sqlflow/docs/wiki/lint_wiki.py):

```
## [YYYY-MM-DD] <ingest|query|lint> | <short title>
```

## [2026-10-21] ingest | Wiki instantiated from SQLFlow's internals wiki

Source: SQLFlow's wiki (`sqlflow/docs/wiki/`: its index, its log, `lint_wiki.py`, and the "Internals Wiki"
section of `sqlflow/CLAUDE.md`).

Established the same three layers for OSDU Delivery: raw sources are the code under `osdu/` with the vendored
`sqlflow/` it builds on, the git history, the sample estate under `osdu/samples/`, and the production estate's
metadata repository; the wiki is `osdu/docs/wiki/`; the schema is the "OSDU Wiki" section of `CLAUDE.md`. Two
conventions differ from SQLFlow's, both because one MCP index holds both wikis: an id is `delivery-wiki-` plus the
filename stem, so no page collides with SQLFlow's `wiki-` pages, and `referenceRefs` and `related` may name
SQLFlow's pages as well as this corpus's. SQLFlow's `lint_wiki.py` lints this wiki through the options a product's
wiki takes (`--wiki`, `--root`, `--manifest`, `--id-prefix`, `--known`), so there is one linter, as there is one
manifest builder.

Moved the seventeen decision records from `osdu/docs/decisions/` into [decisions/](decisions/0001-delivery-grain.md),
keeping their numbered filenames, and folded their README's framing and open questions into
[index.md](index.md). The reference page ids each record listed moved from `related` to `referenceRefs`, `related`
names the records each one links to, and `updated` is the date of each record's last change. Moved the pattern
catalog from the reference guides into [maps/pattern-catalog.md](maps/pattern-catalog.md), where SQLFlow keeps its
own; its rows still point at reference pages until the patterns are ingested.

## [2026-10-21] ingest | Template folders, decision 0018

Source: the change that moves templates into repository template folders: the code under
`osdu/src/SqlFlow.Delivery/Templates/` (`TemplateFolders.cs`, `TemplateChain.cs`, `TemplateSource.cs`), the catalog side
(`osdu/src/SqlFlow.Delivery/Catalog/SyncedTemplates.cs`, `DeliveryCatalogSync.cs`), and the reference pages it rewrote
([templates](../reference/concepts/templates.md), [sqlflow template](../reference/cli/template.md),
[osdu.RepositoryTemplate](../reference/tables/repository-template.md)).

Wrote [0018](decisions/0018-template-folders.md): why a repository's template folders settle the template each document
fills (a working copy can be read and checked in VS Code, a template of one's own is used as committed, and each project
of a repository keeps its own versions), why the saved templates stay a library of their own that no repository document
reads, with each file's copy kept on its row instead, and why a template path in each document, or mirroring the
repositories' templates into the library, was not taken. Amended [0003](decisions/0003-rendering-location.md),
[0009](decisions/0009-searched-references.md), [0010](decisions/0010-assertion-flows-read-only.md),
[0011](decisions/0011-dimension-flows.md) and [0016](decisions/0016-osdu-order.md) with a note each saying what 0018
changes in them, since each named the saved template as what a document is read against. In the
[pattern catalog](maps/pattern-catalog.md), the rows on starting a mapping, on `template.version` and on lineage's
missing cache reads now name template folders, and two rows were added: two projects of one repository filling different
versions of one kind, and a kind of one's own. Indexed 0018.

## [2026-10-22] ingest | Templates read from repository files, decision 0018 rewritten

Source: the change that makes templates purely repository based: the catalog side
(`osdu/src/SqlFlow.Delivery/Catalog/RepositoryTemplates.cs`, `RepositoryFolders.cs`, `SyncedTemplates.cs`), the
template routes (`osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs`), the Templates page
(`osdu/gui/src/features/delivery/TemplatesPage.tsx`, `TemplatesBrowseTab.tsx`), the migration that drops the library and
the copies, and the reference pages it rewrote ([templates](../reference/concepts/templates.md),
[sqlflow template](../reference/cli/template.md), [osdu.RepositoryTemplate](../reference/tables/repository-template.md)).

Rewrote [0018](decisions/0018-template-folders.md): the repository is the only place a template lives, the sync keeps an
index of the files and no copy, and every reader (the GUI and the MCP tools included) reads the file from the folder the
repository was synced into. Recorded the library of saved templates, and the copy on each file's row, as an alternative
built and taken back before release, with why: a second path to the same template that could disagree with the
repository. Its consequences now name the upgrade (module schema 1.54.0 drops both), the command line's `template` verb
without a database, the gate calling a record `unverified` once no folder holds its version, and the builder proposing
the mapping alone. Updated the notes in [0003](decisions/0003-rendering-location.md) and
[0016](decisions/0016-osdu-order.md) that named the saved templates and the copies, the
[pattern catalog](maps/pattern-catalog.md) row on starting a mapping (download on Browse OSDU, then sync), and the index
line of 0018.

## [2026-10-22] ingest | Linked templates and $parent, decision 0019

Source: the change that links a custom template to its canonical kind and lets its records read their parent: the link
(`osdu/src/SqlFlow.Delivery/Templates/TemplateParent.cs`), the `$parent` node and its render
(`osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs`, `osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs`), the
storage read of parents (`osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs`), the ledger's columns and the landing
hook (`osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs`), the end to end suite
(`osdu/tests/SqlFlow.Delivery.Tests/ParentDeliveryTests.cs`), and the reference pages it added and changed
([mapping $parent](../reference/flow/mapping-parent.md), [templates](../reference/concepts/templates.md#linked-templates)).

Added [0019](decisions/0019-linked-templates.md): why the link is the template's (OSDU's own key) while the mapping says
what to take, why the parent is the same minted id in the canonical namespace, why it is read from storage by id as the
record renders rather than searched for or copied from the canonical flow, why a missing parent holds the record, and
what a landing parent does to the records reading it. Added a [pattern catalog](maps/pattern-catalog.md) row for a custom
copy of a canonical kind that needs the canonical record's DDMS references. Indexed 0019.
