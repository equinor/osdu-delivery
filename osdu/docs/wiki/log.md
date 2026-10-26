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

## [2026-10-23] ingest | Several values of a scope parameter, decision 0024

Source: the change that lets a run give a scope parameter several values: SQLFlow's run values
(`sqlflow/src/SqlFlow.Core/Runs/RunValueList.cs`, `RunValuesJsonConverter.cs`, `RunParameters.cs`, the schedule's
`values` in `sqlflow/src/SqlFlow.Yaml/YamlRunValues.cs`, the GUI's `RunValue`), and the module's use of them: which
parameters take several (`osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs`, `DeliveryRunPayload.cs`), the `IN` read
(`osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs`), the watermark guard (`Engine/Intake/SubmissionIntake.cs`), the
removal's comparison (`Ledger/SqlServerLedgerBulk.Removal.cs`), the scope field
(`osdu/gui/src/features/delivery/ScopeParameterFields.tsx`), and the reference pages it changed ([several values of a
scope parameter](../reference/flow/delivery.md#several-values-of-a-scope-parameter), [removal](../reference/flow/removal.md#what-a-run-finds)).
Discussed with the human: of keeping one value per run, dropping the scope, and taking several values, the last, made in
SQLFlow as a general improvement and extended by the module.

Added [0024](decisions/0024-scope-value-lists.md): why the list travels as JSON array text in the map every kind reads,
why only a scope parameter takes one, why the set is one scope with one watermark and per-value watermarks were rejected,
why the work root joins the values and a payload root refuses them, why a removal compares by subset rather than by
equality or membership, and why `--set` repeated gives a list. Indexed 0024.

## [2026-10-23] ingest | Parallelism and default fan-out, decision 0025

Source: a delivery of about 5,000 records the human watched run on one worker of an autoscaled pool, the scale rule
(`osdu/deploy/terraform/modules/worker/main.tf`) and the replica target it reads (`sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs`),
and the change that followed: the `parallelism` block (`osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs`,
`Documents/DeliveryDocumentLoader.cs`), the fan-out in `Engine/FlowRuntime.cs`, the rate share
(`Engine/DeliveryRunPayload.cs`, `Engine/TargetConnection.cs`, `Http/RateLimiter.cs`), the batch numbering of a share
planned again (`Engine/Intake/SubmissionIntake.cs`) and the check of a document read by its range
(`Engine/Worker/DeliveryWorker.cs`), with SQLFlow's fan-out cancel and rejoin (`sqlflow/src/SqlFlow.Catalog/RunQueueStore.cs`).
Discussed with the human: the block's name (fan-out is load balancing, not reliability), splitting it rather than
renaming it whole, and fan-out on by default.

Added [0025](decisions/0025-parallelism-and-default-fan-out.md): why parallelism is a block of its own and the old keys
are refused by name, why each kind takes only what it uses, why fan-out is on by default and bounded by the queued
batches, how a coordinating run stops members no node starts (at once for the drain, after a grace for the intake) and
why stopping single members was left to SQLFlow's group cancel, why the rate limit is shared, and the batch file rewrite
found on the way. Brought [0006](decisions/0006-work-batches.md) and the pattern catalog's spread-over-nodes row to the
new keys. Indexed 0025.

## [2026-10-23] ingest | A fan-out needs a work location every node reaches, decision 0025 amended

Source: the human's question whether the estate's flows would scale out unchanged, and the code it sent me to: how a
work location resolves (`osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs`, `ResolvePath`), where a node puts the
copy of the repository it runs a flow from (`sqlflow/src/SqlFlow.Node/RunWorker.cs`, the version cache under the node's
temp folder), and the estate's flows, which write their work to `../.work`. Each worker has a disk of its own, so a
member on another node would miss the batches the coordinating run wrote.

Amended [0025](decisions/0025-parallelism-and-default-fan-out.md): a delivery fans out only when its `source.work` is a
storage URI or an absolute path, a relative one keeps it on one run and its trace says why, and rebasing relative work
folders onto a deployment's storage root is named as the way to let unchanged flows fan out, not done here.

## [2026-10-23] ingest | The work location names the deployment's storage, decision 0025 amended

Source: the human's dev pool, where one delivery ran on one of three workers because its `source.work` was
`../.work`, and the deployment's storage reference every run is handed (`LANDING_URL`, `osdu/deploy/terraform/variables.tf`,
`osdu/deploy/bicep/main.bicep`) with SQLFlow's rule for a location that names one
(`sqlflow/src/SqlFlow.Core/Engine/SourceLocations.cs`). The human's aim, in their words: start several nodes within
one execution, not run many pipelines.

Amended [0025](decisions/0025-parallelism-and-default-fan-out.md): `source.work` may open with a reference the run
resolves, so a flow writes its work batches to `${env:LANDING_URL}/.work/...` and fans out on every deployment
unchanged, in place of the rebasing it had named as the way.

## [2026-10-23] ingest | One database connection, decision 0026

Source: a colleague's deployment of three database connections, two of them to the one metadata database, and the code
the human sent me to: the module's database declaration (`osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs`),
how each host resolved it (`DeliveryControlPlaneModule.cs`, `DeliveryCliModule.cs`, the local worker launcher), the
repository sync's two paths (`osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs`), SQLFlow's node, which has no
catalog connection (`sqlflow/src/SqlFlow.Catalog/Modules/ModuleDatabaseConnections.cs`), and the deployments' second
secret and variable (`osdu/deploy/terraform/main.tf`, `osdu/deploy/bicep/main.bicep`, the compose, Kubernetes and Radix
layouts). The human, in their words: there is just one catalog database, no need for duplicate connections, consolidate
every hard-coded lookup of it, and the control plane needs access to the osdu schema, which OSDU Delivery requires
though SQLFlow does not.

Added [0026](decisions/0026-one-database-connection.md): why the `osdu` schema lives in the catalog's database and every
host reaches it through the catalog's connection, a node through `SQLFLOW_CATALOG_DB`; why `SQLFLOW_OSDU_DB`,
`Osdu:Database:Connection` and `osdu_database_name` are refused rather than ignored; why the control plane owns the
schema; and why a database of the module's own was dropped. Brought the pattern catalog's nodes-in-a-customer-network
row to the variable. Indexed 0026.

## [2026-10-23] ingest | A fan-out is the flow queued once per share, decision 0025 amended

Source: the human's review of the two-stage fan-out and of the shared work folder built for it: one pipeline
execution is split into sub-executions of the same pipeline, each with its filter, and nothing passes between them.
Read with SQLFlow's run fan-out (`sqlflow/src/SqlFlow.Orchestration/IRunFanOut.cs`) and its export segments
(`sqlflow/src/SqlFlow.SqlServer/Export/ExportSegmentPlanner.cs`), which the human asked to reuse where they fit.

Amended [0025](decisions/0025-parallelism-and-default-fan-out.md): each run of a fan-out delivers its own share of the
key slices from its own node, on SQLFlow's run fan-out, in place of intake and drain members over a shared work folder;
the export's segment planner was looked at for the cut and not taken. [0006](decisions/0006-work-batches.md) says the
same.

## [2026-10-23] ingest | The local check names a retired module connection, decision 0026 amended

Source: the human's `dev.bat` run on their other computer after decision 0026 landed: the check said ready, the
migration failed with `SQLFLOW_OSDU_DB is no longer a setting`, and the control plane then stopped on the same refusal,
because their `.sqlflow/env` still named it. The check (`osdu/src/SqlFlow.Delivery.Setup/DevReadiness.cs`) had listed
the name among the file's extra settings and gone on.

Amended [0026](decisions/0026-one-database-connection.md): a retired setting in the file or the terminal is an error of
the check, so the launcher stops before it starts anything, with a fix that says whether the value names the catalog's
own database.

## [2026-10-23] ingest | A host ignores a retired module connection, decision 0026 amended

Source: the human, after the refusal stopped their `dev.bat`: an environment variable the application does not use
should do no harm, a deployed application's environment cannot always be changed, so it has to be handled
gracefully, and the old variable is to be ignored rather than used or expected to be gone. The code that read it:
the hosts' refusal (`osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs`, called from
`DeliveryControlPlaneModule.cs` and `DeliveryCliModule.cs`), the local check's error
(`osdu/src/SqlFlow.Delivery.Setup/DevReadiness.cs`), and the deployments' `osdu_database_name` and `osduDatabaseName`.

Amended [0026](decisions/0026-one-database-connection.md): the retired settings are ignored, by the hosts, the local
check and the deployments; refusing them, and reading the old variable as a node's fallback, are recorded as rejected,
with what the refusal had guarded and where that case now sits.

## [2026-10-24] ingest | A node reads the old variable when it lacks the new one, decision 0026 amended

Source: the dev workers deployed from the fork, which crash-looped on the latest code: their settings, deployed by an
administrator, give `SQLFLOW_OSDU_DB` and not `SQLFLOW_CATALOG_DB`, and the human cannot change them. The human asked
for the worker to support both names, in the C# code alone, as a temporary workaround. The code:
`osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs` (`CreateForNode`) and
`osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs`.

Amended [0026](decisions/0026-one-database-connection.md): the node's fallback moved from the rejected alternatives
into the decision, with why it was first rejected and what changed.

## [2026-10-24] ingest | A delivery is spread over nodes, a part per node, decision 0025 amended

Source: the human's dev pool after the slice runs landed, where one delivery's nine runs shared three workers four to
a worker, and their direction in the conversation that followed: name each setting for what it is, take the rows to
process and the nodes the user wants, and compute the rest, with a division that loses no record. Read with SQLFlow's
dispatcher and replica target (`sqlflow/src/SqlFlow.Dispatch/DispatchState.cs`, `sqlflow/src/SqlFlow.Catalog/ScaleTargetStore.cs`)
and the module's key cut (`osdu/src/SqlFlow.Delivery/Source/KeySlices.cs`).

Amended [0025](decisions/0025-parallelism-and-default-fan-out.md): `parallelism.nodes` and a part per
`minRowsPerNode` rows, each part on a node of its own through a new SQLFlow extension point, the first run only
coordinating, the keys renamed for what they are, and why the split cannot lose a row.

## [2026-10-24] ingest | Every flow kind writes recordsAtOnce, decision 0025 amended

Source: the human's estate after the delivery's keys were renamed. Its assertion and dimension flows were written with
`parallelism.recordsAtOnce`, following the delivery's table, and `sqlflow validate` refused all 23 of them while their
delivery flows loaded; the human directed that the code be fixed, not the documents. Read with the loader of every kind
(`osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs`, `osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs`).

Amended [0025](decisions/0025-parallelism-and-default-fan-out.md): every kind takes `parallelism.recordsAtOnce`, the
reasons the earlier choice of `concurrency` on the other kinds and a name per kind were rejected, and the consequences
restated in the delivery's keys. [0006](decisions/0006-work-batches.md) names the keys its decision rests on by their
names now.

## [2026-10-24] ingest | Duplicate records found on an inventory, decision 0027

Source: the human's request for a duplicate check (by any part of the document, attached to one type of record, ending in
a set of ids that can be deleted, the least complete copy deleted), their direction that it extend the inventory with an
interface as rich as the explorer's, and their point that the attribute count tells a copy with DDMS bulk data from one
without. Read with the code it became (`osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs`,
`osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicatePicker.cs`,
`osdu/src/SqlFlow.Delivery/Engine/Protocols/WellboreDdmsBulkLink.cs`).

Wrote [0027](decisions/0027-inventory-duplicate-checks.md): the key and the check as a run of the inventory flow, why the
fullest copy is kept and a ledger's copy never picked, the selection and the removal's check of the copy kept, and the
alternatives rejected (a live check in the explorer, the assertion's `unique`, reading the DDMS, keeping documents, fuzzy
matching, removing without an operator).

## [2026-10-24] lint | Inventory reference in pages of one topic

The inventory flow's reference page had grown past what the MCP index takes as one page, and the human asked for pages
small enough that a search lands on the part that answers it. The page now holds the document, its operations and their
payload, and a table of the pages of the flow: [inventory builds](../reference/flow/inventory-builds.md) (the findings,
what a build does, the tables), [selecting ids](../reference/flow/inventory-selections.md),
[inventory removals](../reference/flow/inventory-removals.md) (with repairing missing ids and removing gone ids) and
[finding duplicate records](../reference/flow/inventory-duplicates.md). Each run error moved to the page of its operation.
[0013](decisions/0013-inventory-flows.md), [0014](decisions/0014-inventory-removals.md),
[0015](decisions/0015-lost-records.md) and [0027](decisions/0027-inventory-duplicate-checks.md) name the page that holds
their topic in `referenceRefs`, and the [pattern catalog](maps/pattern-catalog.md) gained the duplicate check's row.

## [2026-10-24] ingest | The duplicate key picked in a record, decision 0027 amended

Source: the human's review of the Duplicates tab: picking the key a property at a time from a list does not fit OSDU
records of hundreds of properties, and the key should be built on the JSON document itself, by clicking the properties
wanted, with the same operations as before. Read with the code it became
(`osdu/gui/src/features/delivery/inventories/duplicates/DuplicateKeyBuilder.tsx`,
`osdu/gui/src/features/delivery/inventories/duplicates/duplicateDocument.ts`).

Amended [0027](decisions/0027-inventory-duplicate-checks.md): the key is picked in a record of the inventory, its
properties shown with the values the record holds there, and the list of the kind's properties is rejected as the way to
pick, kept for a property the record shown holds no value at.

## [2026-10-24] ingest | Duplicates downloaded and grouped in SQL, decision 0027 amended

Source: the human's review of a check against a real partition, whose search per copy for the records naming it was
answered 429, and their direction: extend the inventory's ids with the key's properties, find the duplicates on the SQL
side with the schema evolution the dimension module uses, combine that analysis with live reads of OSDU, and compare equal
values alone. Read with the code it became (`osdu/src/SqlFlow.Delivery/Ledger/InventoryDuplicateTable.cs`,
`osdu/src/SqlFlow.Delivery/Ledger/RunTimeTables.cs`, `osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryDuplicateReferences.cs`,
`osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Duplicates.cs`).

Amended [0027](decisions/0027-inventory-duplicate-checks.md): a check downloads the key's properties into a table of its
own and groups them in SQL, any key of what it downloaded groups again at once, a pick reads the copies of the groups an
operator settles on with the records naming them counted a batch to a search, and keys compare equal values only; reading
OSDU per copy and comparisons per property are recorded as rejected.

## [2026-10-24] ingest | A renamed pipeline's ledger moved by the repository sync, decision 0028

Source: the human's report that renaming delivery pipelines lost their delivery records and held every record, their
judgement that a `ledger:` key and a deleted ledger were needless steps for a routine rename, and their direction that the
repository sync check whether a removed pipeline's file was renamed and move its records automatically; then their rule
that a document's hash says whether it is the same document, that a ledger is owned by the hash and its last known file
name, and that a file duplicated by mistake takes the ledger. Read with the
code it became (`osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs`,
`osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Moves.cs`, `osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Moves.cs`)
and SQLFlow's sync (`sqlflow/src/SqlFlow.Catalog/CatalogSync.cs`), whose write phase is one serializable transaction.

Wrote [0028](decisions/0028-renamed-pipelines-move-their-ledger.md): a ledger owned by its document's hash and the name a
sync found last, moving the rows rather than aliasing the identity, the held records that give way with their history
kept, the sync asking and the move running in slices that the control plane or the owner's run makes, and the
alternatives rejected (`ledger:` alone, pairing by what two pipelines read, pairing by path, a claim button, a catalog
identity, moving in the sync's transaction).

## [2026-10-24] ingest | Orphaned ledgers taken over by hand, every kind follows its document, decision 0028 amended

Source: the human's live test of the rename (the sync moved nothing, since SQLFlow had deleted the old pipeline and no
hash of it existed), their direction that the re-shuffling belongs to the repository sync and not the pipeline, that every
kind keeping a ledger follows the same logic, that a file renamed and edited, or deleted and added back, cannot be linked
automatically, that automatic linking stays where it cannot be wrong with a fallback in the GUI, and that the GUI lists the
missing documents and lets a person pick the document that takes one over rather than matching records. Read with the
code it became (`osdu/src/SqlFlow.Delivery/Ledger/LedgerBindings.cs`, `osdu/src/SqlFlow.Delivery/Catalog/LedgerFlowCatalog.cs`,
`osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs`, `osdu/gui/src/features/delivery/OrphanedLedgersPage.tsx`) and
SQLFlow's sync (`sqlflow/src/SqlFlow.Catalog/CatalogSync.cs`), which deletes a pipeline that left and now calls its
extensions once it committed.

Amended [0028](decisions/0028-renamed-pipelines-move-their-ledger.md): the hash recorded for every kind, the sync making its
moves once it committed and reporting them, missing documents taken over by hand on the Orphaned ledgers page, and the
alternatives rejected (pairing a pre-hash rename by shared delivery keys, dropping the hash, matching records, runs that
move ledgers).

## [2026-10-25] ingest | Storage maintenance on one page, decision 0029, decision 0005 amended

Read the maintenance code of both sides (SQLFlow's `StorageArea`, `CatalogStorageContributor`, `MaintenanceRunner`,
`StorageReport`, `MaintenanceEndpoints`, `MaintenanceStore`, `RunTraceStore`; the module's `OsduStorageContributor` and
the ledger's count and prune methods), the reversal SQL that reads an attempt's result, the assertion report's reading of
a result's detail, and a measure of a development estate's tables. Wrote [0029](decisions/0029-storage-maintenance.md):
one page for the whole database through one contract, three verdicts, nothing removed until an admin sets a limit, the
ledger's prune split into three optional areas, the run trace's keep rule for statements and events, admin-only writes
and a cleanup log, with the alternatives rejected (a page of the module's own, a 90-day default, purge-all, clearing
assertion detail, a limit for every growing history, contributors storing their own limits). Amended
[0005](decisions/0005-ledger-retention.md): the route it proposed is gone and its cadence with it. The reference holds the
areas themselves ([What the osdu schema stores](../reference/concepts/storage-and-retention.md)).

## [2026-10-25] ingest | The safe purge as a panel of its own, decision 0029 amended

Read the Maintenance page as the owner used it without the admin role: no way to purge in sight, and a Preview whose
empty answer read as no answer. Ran the landed page against a control plane on the test catalog as an operator and as an
admin, which reproduced both and showed the cleanup log recording no actor. Amended [0029](decisions/0029-storage-maintenance.md):
Clean up is a panel of its own in the page's header, on each row and in each area's sheet; safe areas start ticked and
optional ones never are; counts show as soon as an age is chosen; cleaning up stays an admin's, said rather than hidden;
the log records the requester through `RequestActor`.

## [2026-10-25] lint | The wiki's conventions move from CLAUDE.md into the index

The human found CLAUDE.md too large to help and asked that what the wiki covers live in the wiki. The "OSDU Wiki"
section of `CLAUDE.md`, which was this wiki's schema, is now [How this wiki is kept](index.md#how-this-wiki-is-kept):
the same layers, page format, estate rule, operations, lint command and upkeep of the index, the log and the manifest,
with no convention changed. `CLAUDE.md` keeps the working rules and links the index; the reference corpus's README
points here too. No decision record was written for the vendored SQLFlow or the module's own schema: their rules are
documented ([docs/sqlflow-changes.md](../../../docs/sqlflow-changes.md), [the osdu schema](../reference/concepts/architecture.md#the-osdu-schema)),
but no source records why they were chosen, and a record would have to invent it.

## [2026-10-25] ingest | Dimension tables keyed by dimension, decision 0030

Read SQL Server's deadlock reports and cached plans for a flow of sixteen dimensions building four at once on a database
whose dimension tables were still small, where three builds of four failed a dimension as a deadlock victim: two writes of
different dimensions waited on each other's key locks, since every statement read the partition's whole clustered index,
which interleaved every dimension's rows. Filed [0030](decisions/0030-dimension-rows-one-range.md): the dimension tables
are keyed on partition, dimension and row, and every statement of the store names its dimension and forces its seek, which
a test checks against the plans SQL Server made. The owner's direction: a deadlock that recurs is a missing index or faulty
logic, not something to retry.

## [2026-10-25] ingest | A dimension's own table kept by partition, decision 0030 amended

Read how SQLFlow's schema evolution makes a dimension's own table (clustered on its id, and never an index after) and the
statements a build writes a partition's rows of it with, which the plans of two partitions' builds at once showed reading
each other's rows. Amended [0030](decisions/0030-dimension-rows-one-range.md): the build keeps a partition's rows together
(`IX_key` clustered on partition, key and id), lays out once a table made before, under the table's lock, and every
statement writing a partition's rows seeks it; the test of plans covers two partitions at once.

## [2026-10-26] ingest | Run parameters from the document, decision 0031

Read the change that gives every flow kind's run form the parameters its document declares (the SQLFlow run inputs and
the OSDU kinds' descriptions of them) and the human's choices behind it: every declared parameter shown with the
document's value and changeable for one run, a schedule fixed but shown, choices and lookups to pick from, several values
where a parameter takes several, one rule in place of the parallel ones, and nothing that worked before changed. Filed
[0031](decisions/0031-run-parameters-from-the-document.md): a kind describes its run inputs and `RunValueResolution`
resolves them for the run, the form and a schedule's preview; a lookup reads a column of a delivery's record table,
never a query; only a delivery's reading operations resolve its parameters; and `maxRecords` is a default, which the
human chose over a ceiling. Amended [0024](decisions/0024-scope-value-lists.md): the several-values field is SQLFlow's run
value picker, which replaced the module's own.
