---
id: delivery-concept-renaming-a-pipeline
title: "Renaming, copying or deleting a flow: what happens to its ledger"
type: concept
summary: "A flow keeps its ledger under its name: a pure rename takes it along at the sync; any other change leaves it orphaned until a flow takes it over."
keywords:
  - rename a pipeline
  - renamed flow
  - rename a flow file
  - orphaned ledger
  - missing document
  - take over a ledger
  - ledger move
  - ledger-move
  - keep the ledger
  - records gone after a rename
  - already claimed by flow
  - which no repository declares any more
  - document hash
  - copied file
related:
  - delivery-flow-interfaces
  - delivery-concept-ledger
  - delivery-concept-submissions
  - delivery-guide-operations-runbook
  - delivery-concept-control-plane
  - delivery-concept-removal-and-reversal
  - delivery-concept-gui
  - delivery-table-ledger-flow
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerMoves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerBindings.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Moves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Bindings.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Moves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery/Catalog/LedgerOwnership.cs
  - osdu/src/SqlFlow.Delivery/Catalog/LedgerFlowCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/LedgerMoveService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryOrphanedLedgerEndpoints.cs
  - osdu/gui/src/features/delivery/OrphanedLedgersPage.tsx
  - osdu/gui/src/features/delivery/TakeoverDialog.tsx
  - osdu/gui/src/features/delivery/BulkTakeoverDialog.tsx
  - osdu/gui/src/features/delivery/OrphanedLedgersNotice.tsx
  - osdu/gui/src/features/delivery/RecordJourney.tsx
---

# Renaming, copying or deleting a flow: what happens to its ledger

Every flow that keeps a ledger keeps it under its name. Renaming the flow gives it a new ledger identity, while everything
the old name kept stays under the old one. This page says, for every kind of flow, what the old ledger holds, what the
repository sync does with it, and how a person hands it to the flow of their choice when the sync cannot.

| Kind | What its ledger keeps | The ledger's identity |
| --- | --- | --- |
| delivery | The records it delivered, every attempt, its submissions, watermarks, deletions, artifacts and reversals, and the OSDU id each record claimed | The flow's name (`name/interface` for an interface of a source), or the ledger it names with `ledger:` ([Ledger identity](../flow/interfaces.md#ledger-identity)); `name@partition` in each partition of a flow that works in partitions but the one marked `keepLedger` |
| retrieval | Its retrievals (what each run wrote out) | The flow's name, in the partition its `data-partition-id` header names |
| assertion | Its runs and every result | `name@partition` for a flow that names its partitions or follows the registry; the name in the header's partition otherwise |
| dimension | Its dimensions (keys, values, attributes, change log), its builds, and the views it wrote | As an assertion flow's |
| inventory | Its inventories and what each one found | As an assertion flow's |

Every kind's ledger also keeps the flow's audit trail. SQLFlow's run history of the old pipeline stays under the old name
whatever happens to the ledger.

## What happens, case by case

| What you did | What the ledger does |
| --- | --- |
| Renamed the flow, its file or both, and changed nothing else | The next sync finds the same document under a new name and moves the old ledger to it, every kind ([The same document under a new name](#the-same-document-under-a-new-name)). |
| Renamed the flow and changed it in the same commit (a description, a parameter, a test, the mapping it pins, or that mapping's content) | Another document: the sync moves nothing, and the old ledger is **orphaned** until a flow takes it over ([Missing documents](#missing-documents-and-taking-them-over)). |
| Deleted a flow in one commit and added its replacement in a later one | The sync that saw it go found no replacement, so the old ledger is orphaned; the sync that finds the replacement moves it when the two are the same document. |
| Copied a flow's file under another name | One document under two names: the copy, found last, owns the ledger, and the sync says so. Deleting the copy gives the ledger back. |
| Moved a flow to another repository | Another document: its old ledger is orphaned until the flow takes it over. |
| Deleted a flow for good | Its ledger is orphaned and stays as it is, records and OSDU ids included, until a flow takes it over. |
| A flow file that does not read or parse | Not gone: its rows are kept as they were, and no move takes its ledger. |

A ledger kept before the sync recorded documents' hashes (a rename synced before this release) cannot be paired by its
hash: it is a missing document, taken over by hand.

## The same document under a new name

Every sync describes each delivery interface ([The interface read model](../flow/interfaces.md#the-interface-read-model))
and each retrieval, assertion, dimension, inventory and download flow ([`osdu.LedgerFlow`](../tables/ledger-flow.md)) with the hash
of its document, SHA-256 of what it is apart from its name and its file's path, so comments and layout play no part:

- a delivery flow as parsed, together with the content of the mapping document each interface pins, since what renders a
  delivery is part of what it is, and without what the machine reading it derives for itself (its render threads), so the
  hash is the same whichever control plane syncs it;
- a flow of another kind as its YAML reads, with its top-level `name` left out and the keys of every mapping taken in
  order, since such a flow derives values from its name as it is parsed (a dimension view's SQL names the flow).

The rows of the flows the repository no longer declares stay, inactive, with the hash they last had.

The flows of a repository with one kind and one hash (for a delivery flow, also one interface and partition) are one
document under several names. Of the names the repository declares, the one a sync found last owns its ledger (two found by
one sync: the file last by path); a flow that comes back after it left is found anew. The sync moves to the owner the
ledger of each other name of the document, declared or not, that holds anything, in each partition where it keeps one.

A copy is reported, once per document, since one of the two is most likely a mistake:

```text
<files>: '<name>', '<name>' are one document under 2 names, so '<copy>', the one found last, keeps its ledger, and the others work on a ledger of their own. Delete the copy that is a mistake; its ledger then goes back to the one that stays.
```

A ledger is not moved when a flow of another document keeps it (`ledger:`), when its own document did not read or parse
in this sync, or when the owner's ledger is kept in another partition, since a ledger keeps the rows of one partition:
`<file>: flow '<new>' is the same document as '<old>', and its ledger is kept in another partition than the ledger of '<old>', so the sync moves no ledger: ...`.
A move asked while another move of either ledger is under way is asked by the next sync, so one ledger's moves are made one
after another. A move the sync saw refused (below) it does not ask again.

The sync asks for the moves in its own transaction, as a `ledger-move` activity on the owner by `service:repository-sync`,
and makes them once that transaction has committed. Its result says what each did, one line per move:

```text
Partition 'dev': moved 5431 record(s) of '<old>' to '<new>', the same document under its newest name, with their history, their OSDU ids and the reading position.
```

A move that has to wait (below) stays running: the control plane's pass takes it up within 15 seconds of that work ending
([Background services](control-plane.md#background-services)), and a run of the delivery flow it goes to finishes it before
it reads its ledger, so no run reads half a ledger.

## What a move does

Each statement of a move reaches at most 1,000 rows and commits on its own, so a move of any size never holds a ledger
table for more than a slice, and a move that stops (a restart, a lost connection) is taken up where it stopped. One move
into a ledger is made at a time, under an application lock on it.

### A delivery ledger

1. Waits while a run works on either ledger, holding a lease on its records or a submission it plans or sends (a
   submission a stopped run left, which nobody holds, does not count). A run of the flow it goes to is refused meanwhile:
   `The ledger of '<old>' moves to '<new>', and a run is working on '<old>' or '<new>' (a lease on its records, or a submission it plans or sends), so the move waits for that work to end; nothing ran, since a run reads its ledger whole or not at all. Run again once that work has ended.`
2. Before it begins, refuses a ledger whose records claimed other OSDU ids under keys the old ledger holds records of (after
   a change of the mapping's id, say), since one key is one record of a ledger, closing the activity `failed`:
   `not moved: <n> record(s) of '<new>' claimed OSDU ids under keys '<old>' holds records of too, each with an id of its own, and one key is one record of a ledger, so the two ledgers stay apart and the old one stays orphaned.`
   Records the new flow delivered under keys of their own (rows the old name never read) stay beside the moved ones.
3. Lets the new flow's records that the old ledger also holds and that claimed no id give way. They wrote nothing to OSDU:
   the new flow held them because the old ledger claimed their ids. Their attempts stay, and read as part of the moved
   record's history. Where both ledgers hold a deletion a [removal flow](../flow/removal.md) found, or a watermark of one
   scope, the old ledger's is kept, so the next run reads every row changed since the old name last read.
4. Links every record of the old ledger to the move, so each record's timeline says it moved: **Moved with its ledger to
   the pipeline that replaced its own** for the sync's move, **Taken over with its ledger by another pipeline** for a
   takeover.
5. Moves every row of the old ledger under the new identity, table by table: records, their search entries, attempts,
   submissions, work batches, leases, lease events, watermarks, activities, deleted records' lines, artifacts, reversals
   and deletions. The removal runs and inventory rows that named the old ledger name the new one.
6. Drops the old ledger from the directory and closes the activity:
   `moved <n> record(s) of '<old>' to '<new>', the same document under its newest name, with their history, their OSDU ids and the reading position; <m> record(s) '<new>' had held, which claimed no OSDU id, gave way to them`
   (a takeover says `to which it was bound by hand`).

Afterwards the flow's records, submissions and activities are everything the old name had, a record's page shows its whole
history, and the next run sends only what changed since the old name last ran, as an update of the id each record claimed.

### A ledger of another kind

| Kind | What moves | Refused when |
| --- | --- | --- |
| retrieval | Its retrievals and audit trail | Never |
| assertion | Its runs, their results and its audit trail | Never |
| dimension | Its dimensions, which take the new flow's name, their builds, its audit trail, and the views it wrote, whose builds the new flow then owns | The new flow holds a dimension of a name the old one holds too |
| inventory | Its inventories, which take the new flow's name, and its audit trail | The new flow holds an inventory of a name the old one holds too |
| download | Its downloads, which take the new flow's name, with the record each lists and the file it is written to, and its audit trail | The new flow holds a download of a name the old one holds too |

A flow holds one dimension, inventory or download of each name, so a move into a flow that already built one of the same name is
refused before anything moves:
`not moved: '<new>' holds dimension(s) '<name>' of its own that '<old>' holds too, and a flow holds one of each name, so the two ledgers stay apart and the old one stays orphaned.`
A dimension, inventory or download flow is therefore taken over before the renamed flow's first build or run. These kinds have no work in
flight a move waits for, and the closing summary counts what moved: `moved <n> assertion run(s) of '<old>' to '<new>', ...`.

## Missing documents and taking them over

A ledger is **orphaned** when no flow a repository declares keeps it, it holds anything, and no move takes it away
already. For a delivery ledger, that is no declared interface keeps its identity; for another kind, no repository declares
a flow of its kind by the name it was kept under. The orphaned ledgers of one flow are one **missing document**.

The **Orphaned ledgers** page (the menu's Ledger group, `/delivery/ledgers/orphaned`; [The GUI](gui.md#orphaned-ledgers))
lists the missing documents in the workbench's partition, those that left the most behind first: the missing flow (its
kind, when it is no delivery flow) and the file it was last declared in, when a sync last found it, what it left behind,
and the flow suggested to take it over. The Delivery page says when any exist. **Take over** opens a dialog that lists its
ledgers, one per interface and partition, with links to their records and audit trail, and the flows a repository declares
that can take it over: those of its kind that keep, or would keep, a ledger for at least one of its ledgers in the same
partition. They are ranked by what they share with the missing document, each shown on a chip:

| Shares | When |
| --- | --- |
| same mapping | A delivery flow whose interfaces pin a mapping the missing one's pinned |
| same record table | A delivery flow that reads a record table the missing one read |
| same interface | A delivery flow with an interface of a name the missing one had |
| same folder | Any kind declared in the folder the missing one was last declared in, in the same repository |

The one flow that alone ranks first is **suggested** and picked to start with, when it shares a mapping (another kind: its
folder). A flow that would take only some of the ledgers says so (`takes 1 of 2 ledgers`), and **What goes where** shows,
for the flow picked, the ledger each one goes to or that it stays orphaned. Nothing moves until **Take over** is pressed.
The documents with a suggested flow can be selected and taken over together, each by its suggestion, one after another.

A takeover hands each ledger of the missing document to the ledger the chosen flow keeps, or would keep, in its partition,
as a move of its own, asked and made at once, and recorded as a `ledger-move` activity by the person who asked
(`user:<name>`):

- a delivery ledger goes to the interface of the same name, and the ledger of a flow in the single form to the chosen
  flow's one interface;
- a ledger of another kind goes to the chosen flow's ledger in the same partition (`name@partition` for a flow that works
  in partitions);
- a ledger the chosen flow keeps no counterpart of is **skipped** and stays orphaned, saying why:
  `'<flow>' declares no interface '<interface>' in partition '<partition>', so this ledger stays orphaned: give the flow that interface, or take this document over by a flow that has it.`

The dialog then says how each ledger ended: **Taken over**, **Waiting** (the move waits for work in flight and the control
plane finishes it), **Not taken over** (refused, as above) or **Stays orphaned**. A document no flow can take over says to
add the flow that replaces it, sync its repository, and take it over then.

The API is `GET /api/v1/delivery/ledgers/orphaned` and `POST /api/v1/delivery/ledgers/orphaned/takeover`
([Missing documents](api.md#missing-documents)).

## Until the ledger moves

A run of the delivery flow that replaced the old one holds every record whose id the old ledger claimed, and sends nothing
for it, so no OSDU record gets a second copy:

```text
OSDU id <id> is already claimed by flow '<old>' (<id>), which no repository declares any more, and one OSDU record belongs to one flow. When this flow replaced it, its ledger is orphaned: have this flow take it over on the Orphaned ledgers page, or sync the repository, which moves it here by itself when this flow is the same document under a new name. Then run this flow again, and these records deliver as updates of the ids they claimed. A flow can also keep that ledger by name, with ledger: <old ledger> in its document (interfaces.<name>.ledger for an interface of a source). Otherwise deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.
```

Records the old ledger never claimed are delivered as usual. Once the ledger has moved, the held records give way to the old
ones, and the next run sends nothing that has not changed. Deleting the new flow's ledger is never needed for this.

A run of a retrieval or assertion flow keeps its own history under its new identity meanwhile, and a takeover adds the old
runs beside it. A dimension, inventory or download flow is taken over before it builds or runs, as above.

## Keeping a ledger by name instead

A delivery flow can also keep an existing ledger where it is, under its old identity, by naming it: `ledger:` at the top of
a document in the single form, and `ledger: <old name>/<interface>` on each interface of a source.

```yaml
flowType: delivery
name: welldb-wellbore-03-v130-delivery
ledger: welldb-wellbore-03-delivery
```

In a flow that works in partitions, each partition keeps the old name's ledger there (`welldb-wellbore-03-delivery@test`),
and the partition marked `keepLedger` the old ledger itself. A ledger a flow keeps by name is never orphaned and never
moved. A flow that names the old ledger after it ran under its new name leaves the records it held in a ledger of its own,
which then is orphaned in turn; delete it with **Delete ledger** first
([Deleting the ledger](removal-and-reversal.md#deleting-the-ledger)), which removes nothing from OSDU, since a record that
never queued a document wrote nothing there.

## Related pages

- [A source with interfaces](../flow/interfaces.md): ledger identity and `ledger:`.
- [The delivery ledger](ledger.md): what a ledger holds.
- [`osdu.LedgerFlow`](../tables/ledger-flow.md): the read model of the other kinds' flows.
- [Operations runbook](../guides/operations-runbook.md#every-record-is-held-after-a-flow-was-renamed): records held after a rename.
- [The control plane](control-plane.md#background-services): the pass that finishes a move that waited.
- [The GUI](gui.md#orphaned-ledgers): the Orphaned ledgers page.
