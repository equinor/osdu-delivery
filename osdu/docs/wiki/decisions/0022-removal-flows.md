---
id: delivery-wiki-0022-removal-flows
title: "Decision 0022: a removal flow reads the source's keys whole and keeps its deletions in the delivery ledger"
type: decision
summary: "Why records a source dropped are found by a flow kind of its own, read from the source, and kept as the delivery ledger's deletions, taken out once."
keywords:
  - "removal flow"
  - "deletion detection"
  - "matchKeys"
  - "full key read"
  - "ledger deletions"
  - "guard"
  - "incremental removal"
referenceRefs:
  - delivery-flow-removal
  - delivery-concept-removal-and-reversal
  - delivery-table-removed-key
  - delivery-table-removal-run
  - flow-ing-load
related:
  - delivery-wiki-0013-inventory-flows
  - delivery-wiki-0014-inventory-removals
  - delivery-wiki-0002-client-supplied-ids
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/RemovalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Removals/RemovalKeySources.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.SourceRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
updated: 2026-10-23
---
# 0022: A removal flow reads the source's keys whole and keeps its deletions in the delivery ledger

Status: accepted. Documented in [removal flow](../../reference/flow/removal.md).

## Context

A record delivered to OSDU should leave it when its source drops it. Sources say so in one of two ways: some keep a list
of the keys they deleted (a table or a drop of files, each key with when), and the rest only stop holding the key, which a
consumer learns by comparing the keys the source holds now with the ones it delivered. Both must end the same way: the
record taken out of OSDU, recorded in the ledger of the delivery flow that delivered it, and never delivered again from
the row it had.

What existed did neither. A delivery flow holds a row the ingestion table marked deleted and never removes what OSDU holds
([record lifecycle](../../reference/concepts/record-lifecycle.md)); removing records is a deliberate operation an operator
asks for, a page or a selection at a time. SQLFlow's ingestion flow can mark rows deleted by comparing keys (`matchKeys`,
[SQLFlow's deleted-row detection](../../../../sqlflow/docs/reference/flow/ing-load.md)), but only against what one run
loaded. An ingestion flow that reads a data lake incrementally, the usual shape, loads only the new files, so a key absent
from a run's files is not a key the source dropped. A full key read and an incremental load do not mix in one flow.

Discussed with the human, who set the requirements: a flow kind of its own on a schedule of its own; keys read from the
source itself (its files or its table, not the ingestion table, since that is where the key lives and a lake is not a SQL
to SQL comparison); a source's list of removed keys a native part of it; dynamic schema and key formats as the rest of the
solution reads them; staging built in, with no pre and ingestion flows of its own; the key read a full load every run,
and taking out of OSDU incremental, never asking again what was taken out; the deletion a logical part of the delivery
ledger, in a table of its own, written by the removal flow although another pipeline maintains the records; and an
interface that scales to thousands of records.

## Decision

- **A flow kind of its own, `flowType: removal`.** It names the delivery flows whose records it takes out, and for each
  the sources of its keys: `keys`, the complete key set read whole every run, and `removed`, a list of removed keys with
  their moments, or both. It runs on its own schedule, after the deliveries it compares (lineage orders it so), and the
  pre, ingestion and delivery flows stay as they are.
- **Keys are read from the source, through SQLFlow's readers.** A reader source is read by the registered source reader
  of its type (any format SQLFlow reads, its schema found as it is read); a table source through SQLFlow's connection
  resolution and dialects. The key is the delivery flow's record key, read column by column in SQLFlow's transform
  notation; each value is converted where the ledger is kept, to the type the record table holds the column in, and its
  delivery key derived as a delivery derives it, so any source format lands on the key the ledger holds. A key that cannot
  be read stops the removal: an unreadable key would make its record look removed.
- **Staging is built in.** A run stages the keys of its three sides (the source's keys, its removed keys, and the delivery
  flow's record table in the removal's scope) in `osdu.RemovalKey`, compares them with the ledger in one transaction, and
  deletes them when it closes.
- **The deletions are the delivery ledger's.** A removal flow keeps no ledger of its own. What it finds is one row per key
  in `osdu.RemovedKey`, under the delivery flow's ledger identity, and its runs are kept there too (`osdu.RemovalRun`).
  The records are taken out through the delivery flow's own removal path, with its credentials and route, each record
  given an attempt and the removal an activity, as any removal is. The delivery flow's plan skips a row of a removed key
  that is not newer than the removal, so the record stays out.
- **Taking out is incremental.** A deletion is `pending` until a try finishes it (removed, already gone, settled in the
  ledger, or kept out), then `removed`, and only pending deletions are taken. A record left in OSDU (other records name it,
  another ledger holds the id, a worker holds it, OSDU refused) stays pending with why, for the next run.
- **A guard holds a suspicious run.** A run that finds more removed than a share of what it compares (20% by default,
  `matchKeys`'s own default), more than a count, or reads no key at all writes what it found `awaiting-approval`, keeps
  nothing out, and ends failed so it is noticed. An operator approves the count they were shown, with the partition typed
  back, and the run checks it again before it approves.
- **A deletion can end.** It is lifted when the source holds the key again, when a newer row of it is delivered, or when an
  operator delivers the record again; the record then follows its source as any other.

## Rejected

- **`matchKeys` in the ingestion flow.** It compares with what one run loaded; on an incremental lake load every key the
  run did not read would look deleted. Running the ingestion in full every time to make it work would give up the
  incremental load for every consumer of the table.
- **An operation of the delivery flow.** It would put the full key read on the delivery's schedule and in its run, and a
  delivery flow reads only its ingestion table, so it would need a source it does not declare. A flow of its own keeps the
  read where its schedule and its credentials are declared.
- **Comparing with the ingestion table.** The ingestion table holds what was loaded, not what the source holds now: an
  incremental table keeps every key it ever saw. The source is where a key stops existing.
- **A per-run candidate table, recomputed each run.** It would ask OSDU again, every run, about every record already taken
  out. A deletion kept once per key, worked through incrementally, asks each once and is what a record's history shows.
- **A ledger of the removal flow's own.** The records are the delivery flow's; a second ledger would split one record's
  history between two flows, and the delivery flow's plan could not see that its key was removed.

## Consequences

- The removal flow needs the repository tree: it opens the delivery flows it names and reads their mappings.
- A `keys` source has to be the complete key set: a filter, a date window or a folder holding part of the source makes
  every key outside it look removed. Date windows are refused on `keys` for that reason, and the guard catches the rest.
- A record whose source still lacks its key is found removed again after an operator delivers it again; keeping it means
  bringing the key back in the source.
- A removal of many records is one soft delete request per 500 records and one reference search per record when
  referenced records are kept; the ledger keeps one row per key, and the key staging one row per key and side for the run.
- Records delivered by the `dspdm` and `etp` routes cannot be taken out by a removal flow: those routes keep nothing they
  delete, and a removal flow removes reversibly only.
