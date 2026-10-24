---
id: delivery-concept-renaming-a-pipeline
title: "Renaming a pipeline: the repository sync moves its ledger to the new name"
type: concept
summary: "A renamed delivery flow keeps its records, history and OSDU ids: a ledger belongs to its document, and the sync moves it to the newest name."
keywords:
  - rename a pipeline
  - renamed flow
  - rename a flow file
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
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/LedgerMoves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Moves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Moves.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/InterfaceCatalogBackfillService.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/LedgerMoveService.cs
  - osdu/gui/src/features/delivery/RecordJourney.tsx
---

# Renaming a pipeline: the repository sync moves its ledger to the new name

A delivery flow keeps its ledger under its name ([Ledger identity](../flow/interfaces.md#ledger-identity)): the records it
delivered, every attempt, its submissions, its watermarks and the OSDU ids its records claimed. Renaming the flow gives it a
new ledger identity, while everything the old name delivered stays under the old one. The repository sync closes the gap.
A ledger belongs to its document, and the newest name of the document owns it: rename the flow (and its file, if you like),
commit, and sync. Nothing is written in the flow and nothing is deleted.

## How the sync finds the document's owner

Every sync describes each delivery interface of the repository ([The interface read model](../flow/interfaces.md#the-interface-read-model))
with the hash of its document: SHA-256 of the flow as parsed, with its name and its file's path left out, so comments and
layout play no part either, and of the mapping document each interface pins, so what renders a delivery is part of what it
is (`DocumentHash`). The rows of the pipelines the repository no longer declares stay, inactive, with the hash they had.
The pipelines of the repository with one hash, interface and partition are one document under
several names, and the one the repository declares that a sync found last owns its ledger (two found by one sync: the file
last by path). The sync moves to it the ledger of each other name of the document, declared or not, that claimed OSDU ids:

- a renamed pipeline, or one whose file was renamed, takes the old name's ledger;
- a file copied by mistake takes the original's ledger, and the sync says so, since one of the two is most likely a
  mistake:

  ```text
  <files>: '<name>', '<name>' are one document under 2 names, so '<copy>', the one found last, keeps its ledger, and a run of any other holds every record whose id that ledger claimed. Delete the copy that is a mistake; its ledger then goes back to the one that stays.
  ```

- deleting the copy gives the ledger back to the name that stays.

A ledger is not moved when a pipeline of another document keeps it (`ledger:`), when its own document did not read or parse
in this sync (that pipeline only looks gone), or when the two ledgers are kept in different partitions, since a ledger keeps
the records of one partition:
`<file>: flow '<new>' is the same document as '<old>', and its ledger is kept in another partition than the ledger of '<old>', so the sync moves no ledger: ...`.
A move asked while another move of either ledger is under way is asked by the next sync, so one ledger's moves are made one
after another.

A document that changed beyond its name (a description, a parameter, a mapping version, or the mapping it pins edited in
the same commit) is another document, and keeps its own ledger; see
[When the document also changed](#when-the-document-also-changed).

The control plane's backfill gives the rows written before the hash was recorded theirs, from the catalog's copies of the
pipelines, removed ones included, so a rename synced before then is moved by the next sync.

## What a move does

The sync asks for the move in its own transaction: it registers the owner's ledger in the old one's partition when no run
has, and records a `ledger-move` activity on it by `service:repository-sync`, `running`. The control plane makes the move
within 15 seconds ([Background services](control-plane.md#background-services)), and a run of the owner that comes first
makes it before it reads its ledger, so no run reads half a ledger. The move:

1. Waits while a run works on either ledger, holding a lease on its records or a submission it plans or sends (a
   submission a stopped run left, which nobody holds, does not count): the control plane takes the move up on a later pass,
   and a run of the renamed pipeline is refused:
   `The ledger of '<old>' moves to '<new>', which replaced it, and a run is working on '<old>' or '<new>' (a lease on its records, or a submission it plans or sends), so the move waits for that work to end; nothing ran, since a run reads its ledger whole or not at all. Run again once that work has ended.`
2. Before it begins, refuses a new ledger whose records claimed other OSDU ids under keys the old ledger holds records of
   (after a change of the mapping's id, say), since one key is one record of a ledger, closing the activity `failed`:
   `not moved: <n> record(s) of '<new>' claimed OSDU ids under keys '<old>' holds records of too, each with an id of its own, and one key is one record of a ledger. Keep the old ledger with ledger: <old ledger> in the flow's document, or leave the two apart.`
   Records the renamed pipeline delivered under keys of their own (rows the old name never read) stay beside the moved ones.
3. Lets the new pipeline's records that the old ledger also holds and that claimed no id give way. They wrote nothing to
   OSDU: a renamed pipeline that ran before the sync held them because the old ledger claimed their ids. Their attempts
   stay, and read as part of the moved record's history. Where both ledgers hold a deletion a [removal flow](../flow/removal.md) found,
   or a watermark of one scope, the old ledger's is kept, so the next run reads every row changed since the old name last
   read.
4. Links every record of the old ledger to the move, so each record's timeline says it moved: **Moved with its ledger to
   the pipeline that replaced its own**.
5. Moves every row of the old ledger under the new identity, table by table: records, their search entries, attempts,
   submissions, work batches, leases, lease events, watermarks, activities, deleted records' lines, artifacts, reversals
   and deletions. The removal runs and inventory rows that named the old ledger name the new one.
6. Drops the old ledger from the directory and closes the activity:
   `moved <n> record(s) of '<old>' to '<new>', which replaced it in its repository, with their history, their OSDU ids and the reading position; <m> record(s) '<new>' had held, which claimed no OSDU id, gave way to them`.

Each statement reaches at most 1,000 rows and commits on its own, so a move of any size never holds a ledger table for more
than a slice, and a move that stops (a restart, a lost connection) is taken up where it stopped. One move into a ledger is
made at a time, under an application lock on it. The sync reads which ledgers claimed ids without keeping locks, though
its own transaction is serializable, so a ledger of thousands of held records is read, not held.

After the move, the renamed pipeline's records, submissions and activities are everything the old name had, a record's page
shows its whole history, and the next run sends only what changed since the old name last ran. SQLFlow's run history of the
old pipeline stays under the old name. A document that names the delivery flow, such as a removal flow's `removals[].flow`
or an assertion's `delivered`, names it by its new name.

## When the document also changed

A pipeline renamed and changed in the same commit, or moved to another repository, is another document, and the sync moves
no ledger to it. Rename first, sync, and change it afterwards; or keep the old ledger by naming it: `ledger:` at the top of
a document in the single form, and `ledger: <old name>/<interface>` on each interface of a renamed source.

```yaml
flowType: delivery
name: welldb-wellbore-03-v130-delivery
ledger: welldb-wellbore-03-delivery
```

The flow then keeps the old ledger where it is, under the old identity. In a flow that works in partitions, each partition
keeps the old name's ledger there (`welldb-wellbore-03-delivery@test`), and the partition marked `keepLedger` the old ledger
itself.

Until one or the other happens, a run of the renamed pipeline holds every record whose id the old ledger claimed, and sends
nothing:

```text
OSDU id <id> is already claimed by flow '<old>' (<id>), which no repository declares any more, and one OSDU record belongs to one flow. When this flow replaced it, the repository sync moves that ledger here, with its records' history and OSDU ids, once it finds this flow is the same document under a new name: sync the repository and run this flow again. Where the document changed beyond its name, keep the ledger with ledger: <old ledger> in this flow's document (interfaces.<name>.ledger for an interface of a source). Otherwise deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.
```

A flow that keeps the old ledger with `ledger:` after it ran under its new name leaves the records it held in a ledger of
its own, which no pipeline then reads. Delete that ledger first, with **Delete ledger** on the flow's Delivery tab
([Deleting the ledger](removal-and-reversal.md#deleting-the-ledger)): it removes nothing from OSDU, since a record that
never queued a document wrote nothing there. Once `ledger:` is written, the Delivery tab shows the old ledger, and deleting
it would remove the old name's records from OSDU.

## Related pages

- [A source with interfaces](../flow/interfaces.md): ledger identity and `ledger:`.
- [The delivery ledger](ledger.md): what a ledger holds.
- [Operations runbook](../guides/operations-runbook.md#every-record-is-held-after-a-flow-was-renamed): records held after a rename.
- [The control plane](control-plane.md#background-services): the pass that makes the moves.
