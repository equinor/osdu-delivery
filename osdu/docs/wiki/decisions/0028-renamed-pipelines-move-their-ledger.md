---
id: delivery-wiki-0028-renamed-pipelines-move-their-ledger
title: "Decision 0028: the sync moves a renamed flow's ledger when only the name changed, and a person takes over every other orphan"
type: decision
summary: "Why a pure rename's ledger follows its document's hash for every kind, why the sync makes the move once it committed, and why the rest is taken over by hand."
keywords:
  - "rename a pipeline"
  - "renamed flow"
  - "ledger move"
  - "orphaned ledger"
  - "missing document"
  - "takeover"
  - "document hash"
  - "copied file"
  - "repository sync"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/LedgerFlowCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/LedgerOwnership.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerMoves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerBindings.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Moves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Moves.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/LedgerMoveService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - osdu/gui/src/features/delivery/OrphanedLedgersPage.tsx
  - sqlflow/src/SqlFlow.Catalog/CatalogSync.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogSyncExtension.cs
referenceRefs:
  - delivery-concept-renaming-a-pipeline
  - delivery-flow-interfaces
  - delivery-concept-ledger
  - delivery-concept-control-plane
  - delivery-table-interface
  - delivery-table-ledger-flow
related:
  - delivery-wiki-0021-adopting-occupied-records
  - delivery-wiki-0005-ledger-retention
  - delivery-wiki-0038-download-flow
updated: 2026-10-30
---
# 0028: The sync moves a renamed flow's ledger when only the name changed, and a person takes over every other orphan

Status: proposed. Documented in [Renaming, copying or deleting a flow](../../reference/concepts/renaming-a-pipeline.md).
Leaves [0021](0021-adopting-occupied-records.md) as it is: that adopts records another system wrote, this carries a flow's
own ledger to its new name.

> **Amended by [0038](0038-download-flow.md).** A download flow keeps a ledger too. The sync records its hash in
> `osdu.LedgerFlow` beside the retrieval, assertion, dimension and inventory flows', and its ledger moves, or is taken
> over, by the rules below, as a dimension's or an inventory's is: a flow holds one download of each name.

## Context

Every flow that keeps a ledger (delivery, retrieval, assertion, dimension, inventory) keeps it under an identity derived
from its name, so the ledger is found from the document alone, in every environment, by every host. Renaming a flow is
routine (a version in the name, a clearer one), and it gave the flow a new, empty ledger: for a delivery flow, the records
the old name delivered, their history, their watermarks and the OSDU ids they claimed stayed under the old identity, the
renamed flow's first run found every row new, and the claim check held each record whose id the old ledger had claimed,
sending nothing. To the operator the delivery records were gone and every record was held, with no action in reach.

The answer grew in steps, each set by the human. A `ledger:` key naming the old flow made a routine rename into steps
repeated in every environment, so they asked the sync to find a renamed file itself. Pairing a pipeline that left with
one that arrived by what they read was needlessly complicated, so they set the rule: a document's hash says whether it is
the same document, the ledger is owned by the hash and the last known file name, a file duplicated by mistake takes the
ledger, and the mapping document belongs in the hash. Their first rename on a live estate then moved nothing: SQLFlow's
sync deletes a pipeline that left the repository, so the old name's document was nowhere to hash, and a rename synced
before hashes were recorded can never be paired by one. They pointed out what that means in general: a file renamed and
edited in one commit, or deleted in one sync and added back in another, can never be linked automatically either. They
asked for the other kinds to follow the same logic, for the re-shuffling to be the sync's work and not a run's, and for
automatic linking to stay where it cannot be wrong, with a fallback in the GUI that lists the old documents that went
missing and lets a person pick the new document that takes over each one's ledger.

## Decision

- **Automatic only when certain: one document under two names.** Every sync records the hash of each delivery interface
  (`osdu.Interface.DocumentHash`) and of each retrieval, assertion, dimension and inventory flow (`osdu.LedgerFlow`): SHA-256
  of a delivery flow as parsed, with its name and file path left out, and of the mapping each interface pins, by its
  content hash; of a flow of another kind as its YAML reads, without its top-level `name` and with every mapping's keys in
  order (`DeliveryDocumentHash`). Hashing those kinds as parsed was tried and set aside: a dimension view's SQL, written as
  the flow is parsed, names the flow, so a renamed dimension flow never matched its old name. The rows of a flow that left
  stay, inactive, with the hash they last had,
  which is what lets a later sync pair it. The flows of a repository with one kind and hash (and for a delivery flow one
  interface and partition) are one document; the one the repository declares that a sync found last owns the ledgers, and
  the ledger of every other name moves to it, in each partition where it keeps one (`LedgerOwnership`). A rename, a renamed
  file and a copy made by mistake are one rule, and deleting the copy gives the ledger back.
- **Everything else is a missing document, taken over by hand.** A ledger no declared flow keeps that holds anything is
  orphaned, and the orphaned ledgers of one flow are one missing document (`LedgerBindings`). The Orphaned ledgers page lists
  them by the flow's name, the file it was last declared in and what it left behind, and offers the declared flows of its
  kind that can take it over, those sharing a mapping document, record table, interface or folder first; the one that alone
  ranks first, sharing a mapping (another kind, its folder), is suggested, and nothing moves until a person confirms. A
  takeover moves each ledger as a move of its own, recorded as that person's: a delivery ledger to the interface of the same
  name, a single-form flow's to the one interface, another kind's to the flow's ledger in that partition; a ledger the flow
  has no counterpart of stays orphaned, saying why.
- **One move, two askers.** The sync and a takeover ask the same `ledger-move` activity (`service:repository-sync` or
  `user:<name>`), under an application lock every ask holds to the end of its transaction, so two asks never both find a
  ledger free. The move is the same: committed slices of at most 1,000 rows, waiting for work in flight on a delivery
  ledger, letting the new flow's held records that claimed nothing give way, refusing a ledger whose records claimed other
  ids under the old keys, and for a dimension or inventory refusing a flow that holds one of the same name, since a flow
  holds one of each.
- **The sync makes its moves once it committed.** The sync's write phase is one serializable transaction
  (`sqlflow/src/SqlFlow.Catalog/CatalogSync.cs`), and moving millions of rows inside it would hold range locks and escalate
  to table locks, stopping every other flow. So the sync asks in its transaction and makes the moves right after it
  commits, through a generic SQLFlow extension point for work after the commit (`ICatalogSyncExtension.CommittedAsync`),
  and says in its result what each move did. A move that waits for work in flight is finished by the control plane's
  pass, and a delivery run finishes a move into its ledger before it reads it, so no run reads half a ledger.
- **The ledger's rows move; the identity stays the name's.** Afterwards the owner's identity is its name, as every other
  flow's, and nothing anywhere looks an alias up. `ledger:` stays for a delivery flow that keeps another ledger by name.

## Rejected

- **`ledger:` as the only way.** It made a routine rename a procedure, left a permanent alias naming a flow that no longer
  exists, and had to be repeated in each environment. It stays as a choice.
- **Pairing by the table and mapping two pipelines read.** A heuristic, with refusals where it could not tell pipelines
  apart, where a document's hash answers the question exactly.
- **Pairing a pre-hash rename by the delivery keys both ledgers hold.** It would have fixed the live estate without a
  click, but it links two documents that differ, which is a guess; the human asked for automatic linking only where it
  cannot be wrong, and a takeover is one confirmation per missing document.
- **Dropping the hash and linking every rename by hand.** The human weighed it, since the hash cannot link an edited
  rename. A pure rename is the common case and the hash is certain about it, so it stays automatic, with the GUI as the
  backup for the rest.
- **Matching records in the GUI.** A person picks the document that takes over a missing one, not records: the unit that
  was renamed is the document, and its ledgers follow it whole.
- **Runs that pair flows and move ledgers, or moves in the sync's transaction.** Re-shuffling ledgers belongs to the sync,
  as the human placed it, not to the pipeline: a run only finishes a move the sync or a person already asked, so it never
  reads half a ledger. The sync's transaction would hold the ledger tables for as long as it runs.
- **A claim button that moves one record's claim to another ledger.** It splits one OSDU record's history across two
  ledgers and counts it in both.

## Consequences

- Renaming a flow, or its file, and syncing is the whole procedure for a pure rename, for every kind, in every environment.
- A flow renamed and changed at once, deleted and added back in separate syncs, moved to another repository, or renamed
  before hashes were recorded, waits on the Orphaned ledgers page; meanwhile a delivery flow holds the records the old
  ledger claimed, so nothing reaches OSDU twice, and the hold says how to take it over.
- A dimension or inventory flow is taken over before the renamed flow builds, or the takeover is refused.
- The old flow's SQLFlow run history stays under its old name in the catalog; the ledger's history moves.
- A table that carries a ledger identity has to be one a move takes, points or leaves alone; a model test fails a table
  added without saying which.
