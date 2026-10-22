---
id: delivery-wiki-0021-adopting-occupied-records
title: "Decision 0021: Records whose business keys OSDU already holds are adopted in a batch, and the next run takes them over"
type: decision
summary: "Why records whose ids OSDU already holds are adopted in a batch the next run carries out, and how linked instances share a record."
keywords:
  - adopt
  - adopt in a batch
  - adoption asked
  - occupied id
  - business key already in osdu
  - ledger lost
  - another instance
  - linked instances
  - take over a record
  - skipdupes version
referenceRefs:
  - delivery-concept-record-lifecycle
  - delivery-flow-mapping
  - delivery-flow-routes
  - delivery-cli-records
  - delivery-concept-api
  - delivery-concept-gui
  - delivery-table-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OccupiedHolds.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OwnedContent.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022160710_RecordOccupiedTargetId.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022185830_RecordAdoptRequested.cs
related:
  - delivery-wiki-0002-client-supplied-ids
  - delivery-wiki-0015-lost-records
  - delivery-wiki-0013-inventory-flows
updated: 2026-10-22
---
# 0021: Records whose business keys OSDU already holds are adopted in a batch, and the next run takes them over

Status: proposed. Documented in [record lifecycle](../../reference/concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds),
[routes, storage](../../reference/flow/routes.md#storage), [the GUI](../../reference/concepts/gui.md) and
[sqlflow records adopt](../../reference/cli/records.md#records-adopt). Amends [0002](0002-client-supplied-ids.md), whose ids
made from a key are claimed only when they are known to be free. Revised the day it was proposed: the first version had the
operator's click claim each id, one record or one issue at a time, and is under Rejected.

## Context

An id is computed from the data ([0002](0002-client-supplied-ids.md)). The delivery key is derived from the mapping's
`dataset.system` and the business key's values alone (`DeliveryKey.Derive`), never from the flow, the ledger or the
deployment, and `dataset.idFrom: key` makes the id from the key's values themselves. So two deployments of the delivery
rendering the same ingestion row through the same mapping make the same OSDU id: the id is the business key.

Before a record first claims an id made from its key, the intake asks OSDU whether a record is there. One that no record of
the ledger claimed was taken for another system's, and the record was held with the advice to remove or rename it. That
is right for a record another system wrote, and wrong for one the delivery itself wrote from the same business key: a
deployment whose ledger is gone (a new database, a second environment), or another instance delivering to the same
partition. Every row of such a flow is held on its first run, its business key in the ingestion table and its record in
OSDU, and nothing the operator could do kept both: removing the OSDU record loses what was there, and changing the key
gives the rows other ids.

Such a flow holds its records by the hundred or the thousand, across every interface of a source, on one run. Records were
held so before the hold kept the id, and the only trace of what they were held for is their error's wording.

## Decision

- **Adoption is asked in a batch, and the next run does it.** An operator asks every record held for an id OSDU holds to
  adopt it: of the whole source from the Delivery tab, of an interface, of an issue, or of a selection, with the partition
  typed back. Asking marks the records (`AdoptRequestedUtc`), unblocks them and asks for them to be planned again; it claims
  nothing and asks nothing of OSDU. The flow's next run, which a batch can queue straight away, takes them over. The
  intended use is one sequence: a run holds the records, one adoption asks them all, and the next run puts the ledger in
  step with OSDU.
- **The run claims each id from what it finds then.** The intake makes the id from the record's key under the mapping it
  renders with, asks OSDU in the batched read it makes before any first claim, and for a record asked, where OSDU holds a
  record no ledger claimed, keeps the record pending at the version OSDU holds now instead of holding it. Staging claims the
  id, as for any first claim, so its uniqueness across every ledger of the database is checked by the statement that makes
  it; the record's history gets an `adopt` attempt (worker `intake`) naming the id and version, and the batch writes the
  row there as an update. An id free by then is delivered as new, an id OSDU cannot vouch for holds the record and keeps
  the request, and an id another record claimed since holds it, naming that record.
- **A record's hold says whether it can be adopted, whatever version wrote it.** The hold keeps the id and version it
  found (`OccupiedTargetId`, `OccupiedTargetVersion`), and a hold written before it kept them is recognised by its error's
  opening words, which every version of the hold has written the same way (`OccupiedHolds.Prefix`). One predicate
  (`OccupiedHolds.Adoptable`) and the batch statement apply the same test, and nothing stored is rewritten to make an old
  hold adoptable: the run makes the id again from the key, so the stored id is never what is claimed.
- **The adopted version is the ledger's.** The write after an adoption is an update of a record OSDU holds, so it carries
  forward the data keys OSDU keeps (`preserveDataKeys`), which the route reads only for a record whose version the ledger
  knows.
- **Two instances may be linked to one record.** Each keeps its own ledger, so each can adopt the same OSDU record, and
  both write it. The storage route therefore records the hash of the content it wrote on every write, not only on one that
  carried keys forward, so a verify that finds the other instance's newer version of the same content says `match`; a
  write OSDU skips as unchanged reads the version OSDU holds instead of keeping the one the ledger last knew; and a skipped
  record OSDU holds soft-deleted, which a skipped write leaves deleted, is brought back under its id rather than recorded as
  delivered while OSDU serves nothing.

## Rejected

- **Claiming at the operator's click** (the first version of this decision). The adoption made each record claim the id the
  hold kept, at the version OSDU held then, one record or one issue at a time. A hold written before it kept the id has no
  id to claim, and recovering it meant parsing the stored error into a column, a rewrite of stored records the delivery
  should not depend on. The version OSDU held at the hold can be stale by the run, when another instance has written the
  record since. And a source holds its records across interfaces and issues by the thousand: an operator cannot walk them
  record by record, nor issue by issue, so the act has to cover the source at once.
- **Adopting from an inventory's orphan and foreign ids.** An inventory lists what OSDU holds of a kind, and would need the
  id each staging row makes to match it with. The intake already makes that id from the business key and asks OSDU about
  exactly those ids, batch by batch, so a second path would repeat it. Adoption needs no inventory.
- **Recording an adopted record as delivered without a write.** The ledger cannot know the row renders what OSDU holds
  without writing it or comparing the whole record, and a record whose delivered version no row of this ledger produced
  would not be reconstructible from the ledger. The write is the comparison: with `skipDuplicates` OSDU writes no version
  when nothing changed.
- **Adopting automatically, for a flow that declares it.** A flow taking over every record OSDU holds at its ids would
  write over another system's records without anyone deciding it. An adoption of every record of a source, with the
  partition typed back, is one decision at the scale a migration needs.

## Consequences

- A new deployment over records an earlier one wrote takes two runs and one adoption: the first run holds every such
  record, the adoption asks them all, and the next run claims the ids and writes the rows there.
- The history of an adopted record reads in order: the hold, the operator's request under their name, the run's `adopt`
  attempt naming the id and version, then the write.
- A request outlasts a run that holds the record for another reason (OSDU not answering, a render that fails): released,
  the record is taken over by the run after. It is spent when a run writes the record.
- Two linked instances each see the other's writes. Where the content is the same, neither calls it drift; where it
  differs, each reports `drifted`, and a reconciling verify on both sides would write each one's content in turn.
- An adopted record removed by one instance is removed for both; the other's verify finds it `missing`.
- The write needs the flow's credentials to be an owner of the record OSDU holds; storage refuses it with 403 otherwise,
  which holds the record with that reason.
- Records a storage write delivered before module 1.56.0 without carrying keys have no content hash until their next
  write, and their verify compares versions alone.
