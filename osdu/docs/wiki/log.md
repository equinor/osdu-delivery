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

## [2026-10-22] ingest | The explorer's own partition connection, decision 0020

Source: the change that has the explorer read a partition through the partition's own connection rather than a delivery
flow's: the connection (`osdu/src/SqlFlow.Delivery/Engine/Operations/PartitionConnection.cs`), the operation reading
through a target made from a partition or a flow (`osdu/src/SqlFlow.Delivery/Engine/Operations/ExploreOperation.cs`,
`osdu/src/SqlFlow.Delivery/Engine/Operations/TargetClients.cs`), the control plane settling the partition and its values
(`osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.cs`), the five values the deployment templates and
the local setup already write, and the reference pages it changed ([the explorer](../reference/concepts/explorer.md#how-it-reaches-osdu),
[partitions](../reference/concepts/partitions.md#configured-partitions)).

Added [0020](decisions/0020-explorer-partition-connection.md): why borrowing a flow's connection made a view of OSDU
depend on the catalog (no search before a flow exists, credentials that moved with the flows), why the partition's
configured platform and client are the way in, why a record page's check stays on the record's own flow, and why the
explorer has no connection setting of its own. Indexed 0020.

## [2026-10-22] ingest | Adopting a record OSDU already holds, decision 0021

Source: the change that lets an operator adopt the record OSDU already holds at the id a held record's business key makes:
the hold keeping the id and version (`osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs`), the adoption
(`osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Adoptions.cs`, migration `RecordOccupiedTargetId`), the storage route's
content hash on every write, its reading of skipped versions and its restore of a skipped soft-deleted record
(`osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs`, `OwnedContent.cs`), and the reference pages it
changed ([record lifecycle](../reference/concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds),
[routes](../reference/flow/routes.md#storage), [sqlflow records adopt](../reference/cli/records.md#records-adopt)).

Added [0021](decisions/0021-adopting-occupied-records.md): why the id an ingestion row makes is its business key whoever
renders it, why a record OSDU holds there was held with no way forward when the delivery itself had written it, why an
operator adopts it and the adoption claims the id at once, and what two instances linked to one record need of the
storage route. Indexed 0021.

## [2026-10-22] ingest | Adoption in a batch, taken over by the next run, decision 0021 revised

Source: the change that makes adoption a batch operation asked of a ledger's held records and carried out by the flow's
next run: the request (`osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Adoptions.cs`,
`osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Adoptions.cs`, migration `RecordAdoptRequested`), the one test of what
can be adopted, old holds included (`osdu/src/SqlFlow.Delivery/Ledger/OccupiedHolds.cs`), the intake taking asked records
over (`osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs`), the whole-source route
(`osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs`), and the reference pages it changed
([record lifecycle](../reference/concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds),
[the GUI](../reference/concepts/gui.md), [the API](../reference/concepts/api.md),
[sqlflow records adopt](../reference/cli/records.md#records-adopt)). Discussed with the human: holds made before the hold
kept the id must be adoptable without rewriting stored records, and thousands of records cannot be adopted one by one.

Revised [0021](decisions/0021-adopting-occupied-records.md): adoption is asked of every record of a source at once and the
next run claims each id at the version OSDU holds then; claiming at the click moved to Rejected, with why (old holds carry
no id, the held version goes stale, the scale). Amended [0002](decisions/0002-client-supplied-ids.md) to match.
Re-indexed 0021.

## [2026-10-23] ingest | Removal flows, decision 0022

Source: the change that adds the removal flow kind: its document and loader (`osdu/src/SqlFlow.Delivery/Documents/RemovalFlowKind.cs`,
`RemovalDocumentMapper.cs`), the run (`osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs`), the key read through
SQLFlow's readers (`RemovalKeySources.cs`), the comparison and the deletions table (`osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs`,
migration `RemovalFlows`), the take-out through the delivery flow's removal path (`osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs`),
the plan's skip of removed keys (`osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs`), and the reference pages it adds
and changes ([removal flow](../reference/flow/removal.md), [sqlflow removal](../reference/cli/removal.md),
[removal and reversal](../reference/concepts/removal-and-reversal.md#records-the-source-no-longer-holds)). Discussed with the
human: a flow kind of its own on its own schedule, keys read from the source with a removed-keys list as a native part,
staging built in, a full key read with an incremental take-out, and the deletions a table of the delivery ledger written
whichever pipeline maintains the records.

Added [0022](decisions/0022-removal-flows.md): why a removal flow reads the source's keys whole, why `matchKeys`, an
operation of the delivery flow, the ingestion table, a per-run candidate table and a ledger of its own were rejected, and
what the guard and the lift are for. Indexed 0022.

## [2026-10-23] ingest | Child links and $child, decision 0023

Source: the change that lets a canonical record link the custom records of the templates linked to its kind: the
`$child` node and its render (`osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs`,
`osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs`), the storage look-up of children beside parents
(`osdu/src/SqlFlow.Delivery/Engine/OsduRecordSearch.cs`), the ledger's `ChildLinkKey` and the landing hook
(`osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Children.cs`, migration `RecordChildLinks`), the end to end suite
(`osdu/tests/SqlFlow.Delivery.Tests/ChildDeliveryTests.cs`), and the reference pages it added and changed
([mapping $child](../reference/flow/mapping-child.md), [templates](../reference/concepts/templates.md#linked-templates)).
Discussed with the human: optional, written only where the template has room and the mapping says; the canonical schema
never changed, so the link goes in `ExtensionProperties` or a tag; links made as the record renders, before its hash, so
the first delivery has none and the delivery after the child lands carries it.

Added [0023](decisions/0023-child-links.md): why the mapping says where and the custom template's link says which kinds,
why the child is looked up in storage rather than assumed from the key, why `LineageAssertions`, `Artefacts`, a property
in the canonical template, a hold and a lineage edge were rejected, and why the ledger finds the records to plan again by
the unique segment they share with their children rather than by the child's `ParentId`. Added a
[pattern catalog](maps/pattern-catalog.md) row for linking a canonical record to its custom record. Indexed 0023.

## [2026-10-23] ingest | Linked canonical and custom records, the first pattern page

Source: the production estate's well log folder after the canonical WellLog mapping linked its custom records
(`$child` in a property of `ExtensionProperties`), which the human ran and confirmed linking both ways, and the guide
added beside it ([linked records](../reference/guides/linked-records.md)). Discussed with the human: the pattern has to be
findable by the MCP server, with the code each piece needs.

Added the wiki's first pattern page, [a custom record beside its canonical record, linked both ways](patterns/linked-canonical-and-custom-records.md):
the problem, the shape, why it is this shape, what to watch for, and the estate folder that runs it, described by what it
delivers. The guide carries the code (the custom template, both mappings, both flows, the checks, what each run does).
Pointed the pattern catalog's two rows, 0019 and 0023 at it, and the `$parent`, `$child`, templates and writing-a-mapping
pages at the guide. The MCP server's instructions name the guide beside the mapping tools, and its templates tool says a
template's detail lists its children. Indexed the page under a new Patterns heading.
