---
id: delivery-wiki-0028-renamed-pipelines-move-their-ledger
title: "Decision 0028: a ledger belongs to its document, and the repository sync moves it to the document's newest name"
type: decision
summary: "Why a ledger follows its document's hash to the newest name, why its rows move rather than its identity being aliased, and why the sync only asks."
keywords:
  - "rename a pipeline"
  - "renamed flow"
  - "ledger move"
  - "document hash"
  - "copied file"
  - "repository sync"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerMoves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Moves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Moves.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/LedgerMoveService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogSync.cs
referenceRefs:
  - delivery-concept-renaming-a-pipeline
  - delivery-flow-interfaces
  - delivery-concept-ledger
  - delivery-concept-control-plane
  - delivery-table-interface
related:
  - delivery-wiki-0021-adopting-occupied-records
  - delivery-wiki-0005-ledger-retention
updated: 2026-10-24
---
# 0028: A ledger belongs to its document, and the repository sync moves it to the document's newest name

Status: proposed. Documented in [Renaming a pipeline](../../reference/concepts/renaming-a-pipeline.md). Leaves
[0021](0021-adopting-occupied-records.md) as it is: that adopts records another system wrote, this carries the delivery's
own ledger to its new name.

## Context

A delivery flow keeps its ledger under an identity derived from its name, so the ledger is found from the document alone,
in every environment, by every host. Renaming a pipeline is routine (a version in the name, a clearer one), and it gave the
flow a new, empty ledger: the records the old name delivered, their history, their watermarks and the OSDU ids they claimed
stayed under the old identity, the renamed pipeline's first run found every row new, and the claim check held each record
whose id the old ledger had claimed, sending nothing. To the operator the delivery records were gone and every record was
held, with no action in reach.

The first answer was a `ledger:` key on a flow in the single form, as an interface already had, and a hold that named it.
The human found that answer wrong in use: it made a routine rename into steps (write a key naming the old flow forever,
delete the ledger the renamed run had filled with held records, sync, run), repeated in every environment the rename
reached. They asked for the sync to find a renamed file and move the records itself. A second answer paired a pipeline that
left with one that arrived by the record table, mapping, interface and partition they read, refusing what it could not
tell apart; the human found it needlessly complicated and set the rule: a document's hash says whether it is the same
document, a ledger is owned by its hash and the last known file name for it, a file duplicated by mistake takes the ledger,
and the mapping document belongs in the hash.

## Decision

- **A ledger belongs to its document, and the document's newest name owns it.** Every sync records the hash of each
  delivery document: SHA-256 of the flow as parsed, with its name and file path left out, so comments and layout do not
  count, and of the mapping document each interface pins, by the content hash the sync recorded of it, since what renders a
  delivery is part of what it is and the human wanted the pipeline traced with its mapping (`DeliveryDocumentHash`,
  `osdu.Interface.DocumentHash`). The pipelines of a repository with one hash, interface and
  partition are one document under several names; the one the repository declares that a sync found last owns the ledger,
  and the ledger of every other name that claimed ids moves to it. A rename, a renamed file, and a copy made by mistake are
  then one rule: the new name takes the ledger, and deleting the copy gives it back. Two declared names of one document are
  reported, since one of them is most likely the mistake.
- **A document changed beyond its name is another document.** A rename and an edit in one commit keep their ledgers apart;
  the operator renames first, or names the old ledger with `ledger:`. A rule that guessed at an edited document's
  predecessor (by the table and mapping it reads) was set aside as the complication it was.
- **The hashes of what a repository no longer declares come from the catalog.** The read model keeps a removed pipeline's
  row with the hash it last had, and the control plane's backfill gives rows written before the hash was recorded theirs,
  from the catalog's copies of the pipelines, removed ones included, so a rename made before this existed is found too.
- **The ledger's rows move; the identity stays the name's.** A move puts every row of the old ledger under the new identity
  and points what names the old ledger at the new one, then drops the old ledger from the directory. Afterwards the
  owner's identity is its name, as every other pipeline's, and nothing anywhere looks an alias up.
- **The owner's held records give way, and keep their history.** A renamed pipeline that ran before the sync held its
  records because the old ledger claimed their ids. Those records claimed nothing and wrote nothing to OSDU, so the old
  record of each key replaces them, while their attempts stay and read as part of the record's history. Records the owner
  delivered under keys of their own stay beside the moved ones; a ledger whose records claimed other ids under the old
  ledger's keys is never moved into, since one key is one record of a ledger.
- **The sync only asks; the move runs in committed slices.** The sync's write phase is one serializable transaction
  (`sqlflow/src/SqlFlow.Catalog/CatalogSync.cs`). Moving a ledger of millions of rows inside it would hold range locks on
  the ledger tables, and lock escalation would stop every other flow's writes until the sync committed, which is why the
  ledger's writes never touch more than a slice of rows. So the sync records the move as a `running` activity and registers
  the owner's ledger in its transaction, and the move runs a statement of at most 1,000 rows at a time, each committing on
  its own and reaching only rows the move has not reached, so it resumes where it stopped. The sync's read of which ledgers
  claimed ids keeps no locks (`READCOMMITTEDLOCK`), so its transaction never holds a ledger of held records.
- **Work in flight is told from the ledger, not from SQLFlow's runs.** The sync does not read the catalog's runs: those
  tables are not the module's to read, and a retrying execution strategy refuses a query of them inside a transaction it
  did not start. The move asks the ledger instead: a lease alive, or a submission a run holds (every run holds the
  submission it works on with a session lock, which `APPLOCK_TEST` sees and a stopped run's session no longer holds), keeps
  the move waiting. A move of a ledger another move is still moving is asked by a later sync, so a copy made and deleted
  between two syncs never sends rows back while they are on their way.
- **Whoever reaches the ledger first makes the move.** The control plane looks for asked moves every 15 seconds, and
  registering a delivery ledger (`LedgerRegistration`, which every run, removal and probe passes through) finishes any move
  into it first, under an application lock on the ledger moved into. A run never reads half a ledger.

## Rejected

- **`ledger:` as the only way** (the first answer). It made a routine rename a procedure, left a permanent alias naming a
  flow that no longer exists, and had to be repeated, with its deletion of the held ledger, in each environment. It stays
  for a document changed beyond its name.
- **Pairing by the table and mapping two pipelines read** (the second answer). A heuristic needing dates to tell a rename
  from a pipeline declared beside the old one, and refusals where it could not tell pipelines apart, where the document's
  hash answers the question exactly.
- **Pairing by file path.** A path finds a rename only when the file keeps its name, which a renamed flow rarely does.
- **A claim button that moves one record's claim to another ledger.** A claim moved without its record splits one OSDU
  record's history across two ledgers and counts it in both. Moving the ledger whole keeps one history.
- **An identity the catalog keeps across renames.** Every host that reads a ledger would have to ask the catalog for it, a
  run from a document outside a synced repository would find another ledger than one inside it, and the identity would no
  longer follow from the document.
- **Moving inside the sync's transaction.** Atomic with the read model, but it holds the ledger tables for as long as the
  sync runs and escalates to table locks on a large ledger, stopping every delivery of the estate.

## Consequences

- Renaming a pipeline, or its file, and syncing is the whole procedure, in every environment the rename reaches.
- A file copied by mistake takes the ledger, and a run of the original holds every record until the copy is deleted; the
  sync says which name keeps the ledger.
- Until the sync has run, the renamed pipeline holds the records the old ledger claimed, and the hold says to sync.
- The old pipeline's SQLFlow run history stays under its old name in the catalog; the ledger's history moves.
- A table that carries a ledger identity has to be one a move takes, points or leaves alone; a model test fails a table
  added without saying which.
