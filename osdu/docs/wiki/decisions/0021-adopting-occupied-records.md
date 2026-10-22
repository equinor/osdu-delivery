---
id: delivery-wiki-0021-adopting-occupied-records
title: "Decision 0021: A record whose business key OSDU already holds is adopted by an operator, and the ledger maintains it from then on"
type: decision
summary: "Why a row whose id OSDU already holds is held until an operator adopts that record, why the hold keeps the id, and how two linked instances share a record."
keywords:
  - adopt
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
  - delivery-table-record
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Adoptions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OwnedContent.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261022160710_RecordOccupiedTargetId.cs
related:
  - delivery-wiki-0002-client-supplied-ids
  - delivery-wiki-0015-lost-records
  - delivery-wiki-0013-inventory-flows
updated: 2026-10-22
---
# 0021: A record whose business key OSDU already holds is adopted by an operator, and the ledger maintains it from then on

Status: proposed. Documented in [record lifecycle](../../reference/concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds),
[routes, storage](../../reference/flow/routes.md#storage) and [sqlflow records adopt](../../reference/cli/records.md#records-adopt).
Amends [0002](0002-client-supplied-ids.md), whose ids made from a key are claimed only when they are known to be free.

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

## Decision

- **The hold keeps what it found.** A record held because OSDU holds a record at its id keeps that id and the version
  OSDU answered (`OccupiedTargetId`, `OccupiedTargetVersion`). A hold where OSDU could not say keeps neither, so nothing
  is adopted on a guess, and staging an id, or a hold for any other reason, clears them.
- **An operator adopts.** An adoption, of named records or of every record of an issue with the partition typed back,
  makes each such record claim the id at the version OSDU held, as if it had queued a document there, unblocked and asked
  to be planned again. Its history gets an `adopt` attempt naming the id and the version, and the activity names who
  adopted it. An id another record claimed since the hold stays that record's.
- **The claim is made by the adoption, not by the next run.** The hold already knows the id, so the record claims it at
  the operator's word, and every later step is the one a claimed record takes: the next run is not asked about the id
  again, stages the record under its claim and writes the row there; read back, verify, removal and inventory reach it
  through the claim. Nothing new runs at intake.
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

- **Adopting from an inventory's orphan and foreign ids.** An inventory lists what OSDU holds of a kind, and would need the
  id each staging row makes to match it with. The intake already makes that id from the business key and asks OSDU about
  exactly those ids, batch by batch, so a second path would repeat it.
- **Adopting at the next intake from a mark.** The record would carry a mark the next run reads to claim the id instead of
  holding it. The hold already holds the id and version, and claiming at the adoption keeps the run unchanged and the
  claim's uniqueness checked when the operator acts.
- **Recording an adopted record as delivered without a write.** The ledger cannot know the row renders what OSDU holds
  without writing it or comparing the whole record, and a record whose delivered version no row of this ledger produced
  would not be reconstructible from the ledger. The write is the comparison: with `skipDuplicates` OSDU writes no version
  when nothing changed.
- **Adopting automatically, for a flow that declares it.** A flow taking over every record OSDU holds at its ids would
  write over another system's records without anyone deciding it. An adoption of an issue reaches every such record in one
  act, which is the scale a migration needs.

## Consequences

- A new deployment over records an earlier one wrote takes two runs and one adoption: the first run holds every such
  record under one issue, the adoption claims them, and the next run writes the rows there.
- Two linked instances each see the other's writes. Where the content is the same, neither calls it drift; where it
  differs, each reports `drifted`, and a reconciling verify on both sides would write each one's content in turn.
- An adopted record removed by one instance is removed for both; the other's verify finds it `missing`.
- The write needs the flow's credentials to be an owner of the record OSDU holds; storage refuses it with 403 otherwise,
  which holds the record with that reason.
- Records a storage write delivered before module 1.56.0 without carrying keys have no content hash until their next
  write, and their verify compares versions alone.
