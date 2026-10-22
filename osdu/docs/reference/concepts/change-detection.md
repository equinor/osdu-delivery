---
id: delivery-concept-change-detection
title: "Change detection: what makes an OSDU record deliver again, and what never does"
type: concept
summary: "Why a record is or is not sent again: the render context, the rendered and payload hashes, incremental windows, business versions, stale rows and cache changes."
keywords:
  - change detection
  - redeliver
  - render context
  - rendered hash
  - payload hash
  - incremental read
  - watermark
  - overlapseconds
  - lastmodified
  - stale row
  - tier 0
  - ingestion fingerprint
  - unchanged records
  - cache change
  - canonical json
  - key order
  - content hash
related:
  - delivery-flow-delivery
  - delivery-concept-partition-cache
  - delivery-concept-record-lifecycle
  - delivery-concept-submissions
  - delivery-cli-run
  - delivery-flow-mapping
  - delivery-flow-mapping-parent
  - concept-upsert-and-change-detection
  - concept-provenance-and-row-keys
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Planning/ChangeDetector.cs
  - osdu/src/SqlFlow.Delivery/Planning/SourceVersion.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Parents.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceSync.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionFingerprint.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingFingerprint.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Hashing/ContentHash.cs
  - osdu/src/SqlFlow.Delivery/Json/CanonicalJson.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
---

# Change detection: what makes an OSDU record deliver again, and what never does

A delivery flow sends a record only when what it renders to, or its payload, differs from what the ledger says OSDU
holds. Every run answers three questions in turn: which rows of the ingestion tables to read, whether a row read needs
rendering at all, and whether the rendered document or payload differs from what was delivered. Just before a worker
sends anything, it asks the last question once more. This page is what each question reads and what moves it, so an
operator can predict what a change will send and why a record was or was not delivered.

The ingestion side of the chain has its own change detection: an `ing` flow updates a row only when its row hash changed,
and stamps `UpdatedDate_DW` only then, or when it first inserts the row, so a row landed again unchanged keeps its stamp
([upsert and change detection](../../../../sqlflow/docs/reference/concepts/upsert-and-change-detection.md)).
A row landed again unchanged is therefore never read again by a delivery.

## Render-affecting versus operational

Only the inputs of the render enter the **render context** recorded against every record. Changing anything else
changes how a document gets to OSDU, never what it is, and redelivers nothing.

| Enters the render context | Operational (never redelivers by itself) |
| --- | --- |
| `render.mapping`, and the mapping file's content (a fingerprint of the document read as YAML: comments, layout and key order do not count, every other edit does, its `description` included) | `target.endpoint`, `target.auth`, `target.headers` |
| The template version the mapping pins (its content hash) | `target.protocol` and `target.protocolOptions` |
| The mapping's parameter values as they resolve (`render.parameters`, and the `${env:OSDU_LEGAL_TAG}`-style defaults the kind supplies) | `reliability`, `failWhen`, `verify` |
| The partition whose cache the render reads, and the cache version | `target.validation`, `target.verifyReferences` |
| For a mapping that searches the platform, the partition's system properties its searches rely on | `source.incremental`, `source.work`, `schedule` |

A moved render context makes a record render again; whether it is sent is still decided by the hash of the rendered
document alone. A mapping edit that does not change what a record renders to sends nothing, and neither does a new cache
version for a record that renders the same document under it. The records a cache change does reach are redelivered by
its rollout ([below](#cache-changes-reaching-records)). Because parameter values are resolved before the context is built, a changed
value behind a reference (a new legal tag in the configuration) moves the context of every record that uses it.

A mapping that reads nothing from a cache renders against no cache, so refreshing a cache never moves its records'
context. A mapping that only searches is pinned to the partition's system properties instead of a cache version, so a
refresh that changes reference data never renders its records again.

## Which rows a run reads

An ordinary run does not read every row. Each scope (the flow, its interface, and one set of parameter values) keeps a
watermark: the upper bound of the last whole-scope read that completed.

| Read | When |
| --- | --- |
| The whole scope | The scope has no watermark yet (its first run), the rules moved since the watermark was written (the mapping, its content, the template or the parameter values: everything in the context but the cache version), a `replan` run, or a `plan` run with `rerender` |
| The window `(watermark - overlapSeconds, now]` | Every other run. `now` is the source database's clock when the read opens, fixed for the whole read |
| The records named | A run scoped to `recordKeys`, read by the key tuple the ledger stored |
| A submission's own window | A run that names a submission: a fan-out member, a re-run, a drain |

In a window the candidates are:

- record rows whose `systemColumns.updated` (`UpdatedDate_DW`) falls in it;
- record rows the ingestion flow marked deleted in it (`DeletedDate_DW`), which it does without touching the update
  column;
- records whose child rows were updated, or marked deleted, in it, since the document is built from them too.

`source.incremental.overlapSeconds` (900 by default, 0 to 86400) reads a little below the watermark again, so a row
whose transaction committed after the previous read fixed its upper bound is still picked up. A run under moved rules
reads the whole scope once; its watermark then carries the new rules, and later runs are incremental again. A moved cache
version does not trigger a whole read: the cache rollout asks for exactly the records the change reaches (below).

Before its own read, a deliver run plans the records the ledger was asked to plan again (a release, a redelivery, a
cache rollout), by key, in passes of up to 5000, each pass in a submission of its own. Their rows did not change, so a
window would never meet them.

**Tier 0.** With `change.useSourceVersions: true` (the default), an incremental run whose window holds no candidate and
for which no record waits to be planned again completes without reading a row. Its submission completes at once and the
watermark still advances. The run log says `Tier 0: no row of OsduData.silver.WellLog changed in the window for scope
..., and no record waits to be planned again; skipping the whole run.`

## What is decided for each record read

The plan takes each record read through these steps in order; the first that applies decides it.

| Step | Outcome |
| --- | --- |
| A key column is empty | Held: `dataset key incomplete: every key column must be non-empty` |
| A child dataset holds more rows than `maxRowsPerRecord` | Held, naming the dataset and the ceiling |
| The row is marked deleted | Held: a deleted row is never delivered. Removing the record from OSDU is a deliberate operation ([removal and reversal](removal-and-reversal.md)) |
| A cache change tagged against the record waits for approval | Skipped as awaiting approval; OSDU keeps the document it has |
| The record is held, failed, deleted or reverted, the source has not moved past the version it was left at, and the ledger did not ask for it to be planned again (a redelivery, a sync, a cache change) | Blocked until it is released or the source changes ([record lifecycle](record-lifecycle.md)) |
| `source.lastModified` is empty or unreadable | Held, naming the column and the value |
| `source.lastModified` is older than the version the ledger holds, delivered or queued | Skipped as **stale**, never sent; the skip is an attempt on the record's history |
| The payload's folder, files or hash cannot be resolved | Held with the reason |
| **Tier 1**: the source version, the payload hash and the render context all equal what the ledger holds for a delivered record the ledger has not asked to be planned again (a request, from a release, a redelivery or a cache change, is answered by a render) | Skipped without rendering |
| The render holds the record (a value its template refuses, a required property empty, a reference it cannot resolve) | Held with every reason |
| **Tier 2**: the rendered document's hash and the payload hash are compared with what was delivered | Create, update metadata, update payload, update both, or skipped as unchanged |

## The versions and hashes compared

| Value | What it is | Compared by |
| --- | --- | --- |
| Ingestion fingerprint | SHA-256 over the record row's `UpdatedDate_DW` and, for each child dataset in name order, its row count and newest `UpdatedDate_DW` | Tier 1, and whether a blocked record's source moved |
| Business version | The record row's `source.lastModified` moment, when the flow declares one | Tier 1, the stale gate, and whether a blocked record's source moved |
| Render context | The canonical JSON of the inputs in the table above | Tier 1 |
| Document hash | SHA-256 of the rendered document's canonical JSON alone | Tier 2 and the final check |
| Payload hash | The row's `hashColumn`; or, under `payloadDetect: lastModified` without a hash column, a signature over the files' names, sizes and modified times; for a route sending parts, a hash over each part's hash | Tier 2 and the final check |

A record whose source row changed but renders the document OSDU already has is not sent. The intake still records the
change on the record's history, with the row's new origin file and row. A held, failed or reverted record that renders it
is delivered again ([record lifecycle](record-lifecycle.md)).

### The canonical JSON a hash is computed over

A hash compares only when two renders of the same record write the same bytes, so the JSON that change detection and
versions rest on is written in one canonical form before it is hashed: the rendered document (its document hash, and the
text a work batch keeps and the record write sends), the render context, a template's bundled schema (its version,
[templates](templates.md#template-versions)) and a mapping's fingerprint. The hash is the SHA-256 of the canonical
text's UTF-8 bytes, written as 64 lower-case hexadecimal characters. The rules:

| Part | How it is written |
| --- | --- |
| Whitespace | None, between tokens or anywhere else outside a string. |
| Object keys | Sorted by ordinal comparison of their UTF-16 code units: case-sensitive, upper case before lower case (`Z` before `a`), with no regard to culture. |
| Null properties | Left out of their object. An object whose properties are all null stays, as `{}`, and an empty object or array stays as it is. |
| Arrays | Kept in their order, which carries meaning in OSDU documents (a log's curves). A null element stays, as `null`, so no element moves. |
| Integers | A whole number is written as an integer without a fraction or exponent, so `1`, `1.0` and `1e0` are all `1`. A whole number beyond 2^53 keeps all its digits when it arrives as a 64-bit integer (an integer in JSON text, an integer value); otherwise it is written as the other numbers are. |
| Other numbers | The shortest form that reads back as the same double, in the invariant culture: `0.1`, `0.30000000000000004`, `1E+20`, `1E-07`. A decimal value is converted to a double first. NaN and infinity cannot be written, and a render that produced one fails, naming the record. |
| Booleans | `true`, `false`. |
| Strings | In UTF-8, with `"` and `\` escaped by a backslash, and `\b`, `\f`, `\n`, `\r` and `\t` for those controls. Every other control character (U+0000 to U+001F, U+007F, U+0080 to U+009F), unassigned and private-use code points, the line and paragraph separators, the byte order mark and every character outside the Basic Multilingual Plane are written as `\u` escapes with upper-case hexadecimal (a character outside the plane as its surrogate pair, `😀`). Everything else, letters of every script and `<`, `>`, `&`, `'` and `/` included, is written as itself. |
| Dates and times | A date and time that reaches the document as a value rather than as text is written as text in RFC 3339 UTC, `yyyy-MM-ddTHH:mm:ssZ`, with a fraction of up to seven digits only when the value has one and without trailing zeros; a value with no time zone is read as UTC. A date that arrives as text stays the text it is. |
| GUIDs | Lower case, with hyphens: `0f8fad5b-d9cb-469f-a165-70867728950e`. |

Two documents that differ only in key order, whitespace, null properties or how a number was spelled therefore have the
same hash, and a document that differs in any value, any array order or any string's content does not.

### The business version: `source.lastModified`

A business version orders the source's rows, where the ingestion fingerprint only says they differ. A row whose moment is
later than the version the ledger holds is planned; the same moment can be skipped without rendering; an older one is
stale and never sent, so a late or re-landed row never takes OSDU back to an earlier version. The column is a `datetime`,
or RFC 3339 / ISO 8601 text (without an offset read as UTC). With it declared, a blocked record moves on when a row
arrives later than the moment it was left at, or at that same moment once the ingestion table rewrote it (a correction
need not touch the column); a row older than that moment never moves it.

### Payloads: `change.payloadDetect`

Under `contentHash` (the default) a payload is sent when its hash column moves. Under `lastModified` the payload's files
are its watermark: a payload is reconsidered when a file was modified after the ones already delivered or queued, or
when the set of files changed, at the cost of one storage listing per record per run; files older than what was
delivered are stale. A declared `hashColumn` stays the final check, so rewritten files with the same content are not
uploaded again. Under `always` the payload goes every time. A route that sends parts (files and bulk data) sends each
part only when its own hash moved, so new bulk data does not upload the record's files again
([routes](../flow/routes.md)).

### The change keys

| Key | Effect |
| --- | --- |
| `change.detect: always` | Every record read is rendered and its document sent. |
| `change.payloadDetect: always` | Every record read sends its payload. |
| `change.onUnchanged: deliver` | Every record read sends its document and payload, unchanged or not. |
| `change.useSourceVersions: false` | Turns tier 0 off: every run plans its window whatever it holds. |

None of them changes which rows a run reads. The keys and their refusals are on the [delivery flow](../flow/delivery.md#change).

## Work already queued, and the final check

A record whose newer version arrives while an earlier one is queued or being delivered does not lose it: the plan
compares the new render with what is queued as well as with what was delivered. A render identical to the queued work is
a skip; anything else replaces the queue, and the next pass of the same run sends it. Concurrent plans cannot take a
record backwards: the ledger refuses staged work older than what it holds and records it as stale.

Immediately before sending, the worker compares the claimed record's queued document and payload hashes with what was
delivered once more, since a delivery of the same content can have landed in between, and sends only the halves that
still differ. A run counts the outcomes: `skippedUnchanged`, `awaitingApproval`, `skippedStale`, `unchangedAtPush`
(found already delivered by that final check), `blocked`, `held`, `failed` and `waiting`, beside `planned` and
`delivered` ([running an OSDU flow](../cli/run.md)).

## Cache changes reaching records

A record that reads the partition cache records which cached values it was built from, kept as a shared set of values
rather than one row per record. When a cache flow's refresh finds that a value some records were built from changed,
was removed, or that a value records looked for and did not find now exists, it tags the change once:

- Under `onChange: auto` (the cache flow's default) the change is rolled out: every record built from the old value, of
  every flow, is marked in batches for a metadata redelivery (its document hash and fingerprint are forgotten), and the
  next run of each flow renders it under the new cache version and sends its document. Its payload stays as it is. The
  change is applied once no flow still builds a record from the old value.
- Under `onChange: approve` the records stay as they are, counted as awaiting approval, until someone approves or
  rejects the change on the cache page. An approval rolls it out; a rejection leaves the delivered documents alone.

A record read for another reason while its cache version moved renders against the current version anyway (its context
moved), and is sent only if it renders differently. The cache, its versions and approvals are on
[partition cache](partition-cache.md).

## A parent landing reaching records

A record of a custom template that reads its parent with `$parent` keeps the parent's id and the version it was rendered
from. When a record of any flow lands under that id, through the ledger, every record reading it is asked to be planned
again (one held because the parent was not in OSDU is released first), and its flow's next run renders it from what the
parent now holds and sends it only if it renders differently. The parent's version itself is not part of the render
context: a parent delivered again with the same sections sends nothing ([mapping $parent](../flow/mapping-parent.md#when-the-parent-changes)).

## A record keeps the OSDU id it claimed

A record's OSDU id is fixed when it first queues a document. An edit that would give it another id (a changed
`dataset.idFrom`, a kind of another entity type, or another `dataPartition`) holds the record, naming both ids, instead
of writing a second OSDU record the ledger does not name:

`the mapping now gives this record the OSDU id <new id>, and the record claimed <old id> when it first queued a document; a record keeps the OSDU id it claimed, so nothing is sent. To deliver the flow's records under the ids the mapping gives now, remove them from OSDU (the record scope) and either deliver them under a ledger of their own (the interface's ledger:), which keeps their history, or delete them from the ledger with the removal, after which the next run delivers the rows as new records; or put back what moved the id: the mapping's dataset.idFrom, the entity type of the kind it renders, or the partition the flow mints ids in (dataPartition)`

A changed `dataset.system` or key makes other delivery keys, so the rows become new records of the ledger, each with its
own id; under `idFrom: key` a new record whose key gives an id an old record already claimed is held instead, since one
OSDU id belongs to one record of the ledger ([mapping](../flow/mapping.md#the-osdu-id)).

## Sending records again on purpose

| Run | What it does |
| --- | --- |
| `force` in the payload | Lifts tier 0 and the completed-submission gate; each record's hashes still decide. |
| `--operation replan` | Reads the whole scope again; sends what renders differently now. |
| `rerender` with or without `recordKeys` | Asks the named records, or every delivered record, to be rendered again; sends only what renders differently. |
| `redeliver` with or without `recordKeys` | Sends the named records, or every delivered record, again whatever the hashes say: `all`, `record`, or a part (`files`, `bulk`, `workflow`). |
| `--operation sync` | Compares the ledger's records with their rows and asks the next run to plan those that changed or were marked deleted without a run seeing it; sends nothing. |

The payload keys are on [running an OSDU flow](../cli/run.md); verify, removal and reversal are on
[removal and reversal](removal-and-reversal.md).

## See also

- [Delivery flow](../flow/delivery.md): `source.lastModified`, `systemColumns`, `incremental` and `change`.
- [Partition cache](partition-cache.md): versions, change tags and approvals.
- [Record lifecycle](record-lifecycle.md): held, blocked and released records.
- [Submissions](submissions.md): what one run plans and drains.
