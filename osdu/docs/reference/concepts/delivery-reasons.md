---
id: delivery-concept-delivery-reasons
title: "Delivery reasons: why a record was delivered, and where an operator reads it"
type: concept
summary: "Why each delivered attempt was sent: the inputs that moved since the version the ledger holds, and the paths of its document that changed."
keywords:
  - why delivered
  - delivery reason
  - delivered again
  - trigger
  - mapping changed
  - cache changed
  - source changed
  - path diff
  - changed paths
  - reason not recorded
  - RenderPaths
  - RenderCommit
  - estate commit
related:
  - delivery-concept-change-detection
  - delivery-concept-record-lifecycle
  - delivery-concept-submissions
  - delivery-concept-partition-cache
  - delivery-table-attempt
  - delivery-table-record
  - delivery-cli-records
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Planning/DeliveryReason.cs
  - osdu/src/SqlFlow.Delivery/Planning/DeliveryReasons.cs
  - osdu/src/SqlFlow.Delivery/Planning/RenderPaths.cs
  - osdu/src/SqlFlow.Delivery/Planning/ChangeDetector.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Storage/WorkBatchFile.cs
  - osdu/src/SqlFlow.Delivery/Ledger/AttemptResult.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.Records.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.Submissions.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
  - osdu/gui/src/features/delivery/DeliveryReasonView.tsx
  - osdu/gui/src/features/delivery/deliveryReason.ts
---

# Delivery reasons: why a record was delivered, and where an operator reads it


Every attempt that delivers a record (a create, or an update of its document, its payload or both) keeps why the plan
sent it, under `reason` in its result ([`osdu.Attempt`](../tables/attempt.md)): what it sent (`action`), one line saying
it (`summary`), every input that moved since the version the ledger holds (`triggers`), and the paths of the document that
changed (`paths`). The plan builds it from what it already holds, the record's ledger state, the row it read and the run's
render context, and it travels with the document in the work batch, so a reason costs no OSDU call and no source read.

`mapping WellLog@1.4.0 changed (852db64); acl.viewers changed`

## What moved: the triggers

Each input is compared with what the ledger holds for the record, and every one that moved is listed, in this order, not
only the first. The comparison is with the version OSDU holds: work queued and replaced before it was sent is not a
version of the record.

| Trigger | When it is listed | What it names |
| --- | --- | --- |
| `first-delivery` | The ledger holds no record for the key, or holds one OSDU never held a version of (only ever held) | The status the ledger held it in |
| `requested` | The ledger asked for the record to be planned again: a redelivery, a release, a rerender, a removal that delivers again, a cache rollout, a parent or child landing | When it was asked; the intervention that asked, as the audit trail numbers it, with its kind and who made it, when one was under way at that moment; otherwise the note the request left on the record |
| `drift` | The last verify found OSDU holding another version or other content, or no record at the id | When it was verified |
| `forced` | The flow sends whatever the hashes say: `change.detect: always`, `change.payloadDetect: always` (for a payload sent), `change.onUnchanged: deliver` | The keys |
| `source-changed` | The ingestion table stamped the record row again (its `UpdatedDate_DW` moved), or its `source.lastModified` moved | The row's two update moments, and the two business versions. Which columns changed is not kept: the fingerprint covers the row's update moment, not its values |
| `child-or-lookup-changed` | The ingestion fingerprint moved and the record row's own update moment did not: a child dataset row, or a lookup row it joins, was written or marked deleted | |
| `mapping-changed` | The mapping reference or its document's fingerprint moved | Both references and fingerprints, and the estate commit of each: the commit the run that planned the version OSDU holds executed, and this run's, when the run is pinned to one |
| `template-changed` | The template version the mapping pins moved | Both versions |
| `parameters-changed` | A parameter's resolved value moved, or the restriction the mapping's `restrictedAccess` names did | The names of the parameters (`restrictedAccess` for the restriction), never their values |
| `enrichment-changed` | An enrichment was added, removed or reordered, or its mapping, template or parameters moved | The enrichments, and which of them moved how |
| `cache-changed` | The cache version moved, the render reads another partition's cache, or the system properties a searching mapping relies on moved | Both cache versions, and the cache changes that reached the record (their id, type, item, path and kind of change) when the rollout recorded them for the cache set the record was built from |
| `payload-changed` | The payload hash moved, for a delivery that sends the payload | Both hashes |
| `other-input-changed` | None of the above moved and the document still renders differently | That the render read something those inputs do not cover (a record it searches for in OSDU, its parent, a child it links), or that it replaces work already queued |

A request forgets the record's fingerprint and its delivered hashes (a redelivery), so a fingerprint the ledger no longer
holds is never taken for a source change: the row's update moment alone says whether the row moved.

## What changed: the paths

The record keeps the paths of the document OSDU holds, each with a 64-bit hash of its value and never the value itself
([`osdu.Record.RenderPaths`](../tables/record.md)). A path descends through objects only: an array is one value, so
`acl.viewers` or `data.Curves` is one path however many items it has, and an item that moves changes it. Names are written
as every record path is: `tags.nativeUID`, `tags['dra.lraID']`. A delivery names the paths its document added, removed
and changed, the first 50 in path order with how many of each kind in all, and the record keeps the new paths once the
document lands.

A record's paths are kept from its first delivery made with this version of the ledger, and from the first plan that
finds a record delivered before it rendering exactly what OSDU holds. Until then a delivery says why its paths were not
compared: the record is new to the ledger, OSDU holds no version of it (a removal), or the ledger keeps no paths of the
version OSDU holds (delivered before the ledger kept them, or put back by a reversal or a step back). It never guesses.

A record keeps about 25 bytes a path, about 1 KB for a document of forty properties; hashing the document's values costs
one pass over the text the render has already written, a small part of what rendering it costs, and only a record that is
sent, or that has no paths yet, is hashed.

## When nothing is sent

A record rendered because something the ledger compares moved, and found rendering exactly what OSDU holds, is on its
history too: a **Nothing to send** attempt (phase `identical`) whose reason names what moved, with `action` `none`, no
paths, and a line ending `renders what OSDU holds` (`mapping WellLog@1.4.0 changed (852db64); renders what OSDU holds`).
So a mapping edit, a cache change or a rerender that changed nothing for a record still says it reached it. Such an
attempt is written once for each move: the next run finds the record as it left it and renders nothing. A render where
nothing compared moved (a replan of unchanged rules) writes nothing, as before. Work that OSDU already held when its turn
came to be sent keeps the reason it was planned for (phase `unchanged`). Records skipped without rendering, because
nothing moved at all, are counted on their submission only.

## Where to read it

| Where | What it shows |
| --- | --- |
| A record's **Timeline** | Each **Delivered** row titled with the reason's line; opened, every trigger with what it was and is now (its explanation behind a note mark) and the paths that changed. A **Nothing to send** row titled with what moved, opened to the same list under **What moved** (or **Why it was planned** for work OSDU already held) |
| A record's header | Under **Delivered**, the version OSDU holds and the line of the delivery that sent it, whole on hover |
| A submission's page | Its delivered records counted by what sent them, `11,768 delivered: mapping changed 11,768; cache changed 2,104` (a record under every kind its reason names), each cause opening the flow's Records tab narrowed to the records it delivered for it; each attempt's line in its **Attempts** tab |
| A run's page and a flow's **Runs** tab | On the run's page, **Delivered for**: the same breakdown for every record the run and the parts it split its delivery into delivered, each cause opening those records; on the Runs tab, the same on hover over a run's **Delivered** count, read when the hover opens |
| A flow's **Records** tab | `?trigger=<kind>` beside `?delivered=` or `?run=`, a chip **Delivered for** the cause, which goes with the submission or run it narrows |
| `sqlflow records show` | A `why` line under each delivered attempt ([records](../cli/records.md)) |
| The API | `reason` on each attempt that keeps one, of `GET /api/v1/delivery/records/{flowId}/{key}/attempts` and of a submission's attempts; `deliveredBy` on `GET /api/v1/delivery/submissions/{id}` and on `GET /api/v1/delivery/runs/lookup`; `trigger` on a flow's records listing and export ([the API](api.md)) |
| The MCP server | `delivery_record` (each attempt's `reason`), `delivery_submissions` with a submission id and `delivery_run` (`deliveredBy`), and `delivery_flow_records` (`trigger` with `deliveredBy` or `runId`) |

A delivery made before the ledger kept reasons says `reason not recorded`, and nothing else about why it was sent.

## See also

- [Change detection](change-detection.md): what decides whether a record is sent at all.
- [Record lifecycle](record-lifecycle.md): held, blocked, released and redelivered records.
- [`osdu.Attempt`](../tables/attempt.md) and [`osdu.Record`](../tables/record.md): where the reason and the paths are kept.
