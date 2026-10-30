---
id: delivery-concept-artifacts-and-undo
title: "Artifacts and the undo of every route: what a delivery mints, slots, artifact states, and what an unfinished delivery leaves behind"
type: concept
summary: "What each route and DDMS shape records as artifacts, how slots and artifact states move, and exactly what the undo of an unfinished delivery takes back."
keywords:
  - artifact
  - artifacts
  - artifact state
  - artifact role
  - slot
  - intent
  - due
  - undo
  - unfinished delivery
  - what the undo removes
  - left behind
  - kept artifact
  - workflow run undo
  - dataset registration undo
  - session abandoned
  - seismic store lock
  - dspdm row deleted
  - undo force
  - osdu.Artifact
related:
  - delivery-concept-removal-and-reversal
  - delivery-concept-ledger
  - delivery-concept-protocols
  - delivery-concept-record-lifecycle
  - delivery-flow-routes
  - delivery-flow-ddms
  - delivery-flow-ddms-services
  - delivery-cli-records
  - delivery-guide-finding-orphans
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Protocols/TargetArtifacts.cs
  - osdu/src/SqlFlow.Delivery/Protocols/IDeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/UndoRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/UndoSummary.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ArtifactUndo.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WorkflowRuns.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/WorkflowClient.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/RegistrationIntents.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/FileUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduFileProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDatasetProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduManifestProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduWorkflowProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduFileAndDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduManifestAndDdmsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduDspdmProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduEtpProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/IDdmsShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/WellboreDdmsV3Shape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/WellDeliveryShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/RafsShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/ProductionTimeSeriesShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/SeismicStoreShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/ReservoirManagementShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/ReversalRoute.cs
  - osdu/gui/src/features/delivery/ArtifactMarks.tsx
---

# Artifacts and the undo of every route: what a delivery mints, slots, artifact states, and what an unfinished delivery leaves behind

A delivery that takes more than one call to OSDU is a unit of work, and every object the unit creates, or sets out to
create, is an **artifact** in `osdu.Artifact`. When the undo runs, how it is retried and how an operator forces it are on
[Unfinished deliveries and their undo](removal-and-reversal.md#unfinished-deliveries-and-their-undo), and the table's
columns are on [`osdu.Artifact`](ledger.md#osduartifact-every-id-a-delivery-minted). This page is the detail behind
both: what an artifact is, the rules its slot and state follow, and route by route what a delivery declares and what its
undo does with each artifact.

## What an artifact is

A route reports an artifact with the step that made it, and the ledger writes it in the transaction that writes the
step, so it is on the ledger before the delivery goes on. An artifact carries:

| Field | What it holds |
| --- | --- |
| `Slot` | The route's name for the object within its unit, unique there (`record`, `file:0:<hash>`, `content:Kr`, `run:<id>`). At most 200 characters. |
| `Role` | What the object is: one of the roles below. |
| `TargetId` | The OSDU id, or the key the target gave it (a row's primary key, a Seismic Store path, a session id); empty only for an intent whose call has not answered. |
| `Locator` | What finds the object when its id is not known, or what else names it: a landing-zone path, `workflow\|runId`, rows in runs, a row's business object and key. Cut to 1,000 characters, marked `...` when cut. |
| `Version`, `PriorVersion` | The version the unit wrote, and the version the write replaced. |
| `Status` | Where it stands: one of the states below. |
| `Note` | What the route or the undo had to say about it, redacted and cut to 1,000 characters. |

Every artifact belongs to a unit, whose id and start time it carries: the start time is what an undo compares OSDU's
creation time of a record with. A step that reports artifacts outside a unit is refused.

### Roles

| Role | What it is | Kept after its unit commits |
| --- | --- | --- |
| `record` | The record itself, written by a unit that created it, before a later call of the unit. | No |
| `version` | A version of a record that existed (the record, or a dataset beside it), written before a later call of the unit. | No |
| `dataset` | A dataset the unit registered for the record's files, its inputs or its manifest. | Yes |
| `content` | A dataset a DDMS registered for the record (a RAFS content table). | Yes |
| `output` | A record a workflow run wrote. | Yes |
| `dataspace` | A Reservoir DDMS dataspace and its OSDU record. | Yes |
| `session` | A Wellbore DDMS bulk session. | No |
| `lock` | A Seismic Store write lock, or a read-only flag a delivery lifted. | No |
| `rows` | Rows a DDMS keeps under the record (Reservoir Management, DSPDM). | No |
| `points` | Historian points accepted under series versions. | No |
| `objects` | An object store's objects, ETP objects, a Seismic Store dataset, a Well Delivery entity version. | No |
| `run` | A workflow run the unit triggered, which can write records until it ends. | No |

The roles kept after commit are OSDU ids that stay in OSDU, and the [inventory](../flow/inventory.md) joins them with
what OSDU serves. The others stand only for a unit's progress: when the unit commits they are deleted, since the record's
own state names what they stood for.

### States

| State | Meaning | Open (an undo may still take it) |
| --- | --- | --- |
| `intent` | The call that creates it is about to go; the ledger has not had its answer. | Yes |
| `pending` | Created by a unit that has not ended, or that newer work abandoned and the sweep has not reached. | Yes |
| `live` | Its unit committed; it stays in OSDU and the ledger names it. | No |
| `superseded` | A later delivery replaced it, or (as an undo's outcome) the undo left it to newer work that writes it again. It stays in OSDU. | No |
| `due` | Its unit aborted, or committed without the call's answer, and the undo has not run. | Yes |
| `removed` | The undo, or the route itself, removed it, reversibly where the route can. | No |
| `restored` | The undo wrote back the version it replaced. | No |
| `gone` | OSDU no longer held it when the undo came, or the call it stood for made nothing. | No |
| `kept` | No call removes it: it is left behind, and the note says why. | No |
| `failed` | The undo was refused or could not reach OSDU; it is tried again with backoff. | Yes |

`superseded` has two sources. A live dataset becomes `superseded` when a later delivery's payload registered new
datasets and the record no longer names it (the file, manifest, `fileAndDdms` and `manifestAndDdms` routes report these);
it stays live in OSDU, since earlier versions of the record name it. An undo answers `superseded` for the record itself
(and, on the Well Delivery and Seismic Store shapes, what stands for the record there) when newer work of the record
writes it again; the GUI shows that outcome as **left to newer work**.

### How the states move

| From | To | When |
| --- | --- | --- |
| (none) | `intent` | The route reports the call it is about to send. |
| (none), `intent` | `pending` | The call answered, with the id the target gave. |
| `intent`, `pending` | `removed`, `kept`, `gone` | The route settled the artifact itself before its unit ended: a lock its close released, a session it abandoned when the delivery failed, a manifest dataset it removed once the run settled, a duplicate registration it removed, a dataset it found already there and took over, a registration whose answer says it made nothing. Written as settled by `route`. |
| route-settled `removed`, `kept`, `gone` | `intent`, `pending` | A later try of the same unit reports the slot again (opens the lock again). |
| `intent` | `due` | The unit committed without the call's answer: the delivery went on without learning what the call made. |
| `pending` (`dataset`, `content`, `output`, `dataspace`) | `live` | The unit committed. |
| `pending`, route-`removed` (every other role) | row deleted | The unit committed. |
| `intent`, `pending` | `due` | A try of the unit ended held or failed. |
| `live` | `superseded` | A later unit of the record committed and named the id obsolete. |
| `intent`, `pending`, `due`, `failed` | `removed`, `restored`, `gone`, `kept`, `superseded` | An undo settled it, for good. |
| `intent`, `pending`, `due`, `failed` | `failed` | An undo could not take it yet; its tries are counted and its next try scheduled. |
| `live`, `superseded` (`dataset`, `content`, `output`) | an undo's outcome | [Deleting the ledger](removal-and-reversal.md#deleting-the-ledger) removes the ids committed deliveries minted. |

An `intent` or `pending` artifact of a unit the record no longer carries (its steps were dropped, or a newer unit began)
is taken as it stands, by the claim that next takes the record or by the sweep, without passing through `due`.

## Slots

A slot is the route's name for one object within one unit. The ledger keeps one row per record, unit and slot.

### Slot rules

- **A slot reported again updates its one row.** An intent completed by its id, or a step a resumed try reports again,
  updates the row rather than adding one; the id, locator, version and note a later report gives win, and an empty one
  keeps what the row holds.
- **The version replaced is the one first reported.** A resumed try that reads OSDU again sees the unit's own write, so
  `PriorVersion` stays what the first report gave.
- **A record slot never becomes a version.** A slot first reported as the record the unit created (`record`) stays so,
  with no prior version, when a later try reports it as a `version` of what it now finds.
- **A slot the route settled reopens; one an undo or a commit settled never does.** A later report updates a row only
  while it is `intent` or `pending`, or `removed`, `kept` or `gone` settled by the route. A row an undo settled, or that a
  commit made `live` or `superseded`, never changes again.
- **A route reports only what the ledger can keep.** Every artifact is checked before the call it describes goes: a slot
  of 1 to 200 characters, a known role, a state of `intent`, `pending`, `removed`, `kept` or `gone` (a route never reports
  an artifact `live`, `due`, `failed` or `restored`), an id unless it is an intent, and for an intent an id or a locator
  that finds the object if the answer is lost. A step named `$unit`, the name the worker keeps the unit under, is
  refused.
- **Only the worker holding a record's lease writes its artifacts**, so no two writers race for a slot.
- **An undo settles only open artifacts.** A sweep that meets an artifact the worker's undo already settled writes
  nothing to it, which is what makes undos idempotent.

## When the undo runs, and what it is given

The undo runs at the moments listed on [Unfinished deliveries and their
undo](removal-and-reversal.md#unfinished-deliveries-and-their-undo): the worker after a try ends held or failed, the claim
before newer work is sent, the sweep at the end of every deliver run and flow-wide drain (a drain of one submission does not sweep) and in the
`undo` operation, and a
removal or the deletion of the ledger. Each hands the route one record at a time with its open artifacts in the order
they were created, and why:

| Reason | When |
| --- | --- |
| `held` | A try of the unit ended held. |
| `failed` | The unit spent its retry budget, or the record is failed. |
| `abandoned` | The unit's steps were dropped while it was unfinished: newer work was staged, the planner held the record, or a newer completion superseded it. Any artifact still `intent` or `pending` makes the reason `abandoned`. |
| `removed` | The record is being removed from OSDU, or its ledger deleted. |

The undo also says whether newer work writes the record again (**keep record**): at a claim, when the work about to go
writes the record's metadata; in the sweep, when the record has pending work whose metadata differs at the final check.
A route then answers the record itself `superseded` and undoes only what the unit made beside it, except where noted
below. The worker's undo of its own aborted try and a removal never keep the record.

Only a route that can create something before its last call (every route but `storage`) reads or undoes artifacts. The
route must answer every artifact it was given exactly once, with an undo outcome (`removed`, `restored`, `gone`, `kept`,
`superseded` or `failed`); a failure is an answer, never an exception. A route that throws, answers an artifact it was not
given, answers one twice or answers with another state has every artifact of the undo answered `failed`, and one it gives
no answer for is `failed` with `the route gave no answer for it`.

### Retries, bounds and force

| Bound | Value |
| --- | --- |
| Tries the sweep gives a failed undo | 10; then the artifact is left for an operator, with no next try. |
| When a failed undo is tried again | After 1, 2, 4 ... minutes, doubling, at most six hours apart. |
| Records one page of the sweep takes and one write settles | 200, whole records only, so one record's undo is never split. |
| Records one sweep takes in a run | 10,000; the rest wait for the next run. |
| Clock skew allowed between OSDU and this host | 5 minutes: a record counts as created by the unit when OSDU created it no earlier than the unit's start less five minutes. |

The sweep skips a record a lease holds (a lease a stopped worker left is applied first, so the record says which unit it
carries). An undo run with `force` (`--payload '{"force":true}'`, or **Undo unfinished** with force) also takes the
failed artifacts that used their ten tries; a failed artifact still waiting out its backoff stays until its time.

## What every route's undo shares

### The record itself

The `record` and `version` artifacts are undone the same way on every route that has them, unless newer work keeps the
record:

| Artifact | What the undo does |
| --- | --- |
| `record` | Reads the record from storage. Not there: `gone`. Created (`createTime`) after the unit began, less the clock skew: removed through the route's reversible removal, `removed` (`gone` when it was already gone). Created before: the id was a record another system wrote before this flow claimed it, so it is given back the newest version older than the one the unit wrote, `restored`; `kept` when that version cannot be told. A route that cannot read storage removes the record only when the unit's step recorded the version it wrote, else `kept`. |
| `version` | Writes back `PriorVersion` as a new version, `restored`, and the ledger's version moves to that write when the version written back is the one the ledger holds. An intent whose write never answered and whose record's latest version is still the prior one: `gone`, nothing written. Prior version unknown, or purged from OSDU: `kept`. The route cannot write versions back: `kept`, naming why. |

A record that cannot be removed or written back yet keeps what it names with it: the datasets a record names, and the
record of a DDMS whose session or content could not be undone, are answered `failed` with the reason and go together on
the next undo, so OSDU never serves a record naming what the undo already removed.

### Datasets registered for files

A `dataset` artifact the file service minted carries the landing-zone path its file was uploaded to. The undo asks the
search index for every dataset registered for that path (each path is one upload's alone, so every one of them is the
unit's), adds the id the registration answered, and soft-deletes them all through storage (`POST /records/delete`, 500
ids a request): `removed`, `gone` when storage no longer holds them, or `gone` when the index lists none (the
registration did not land, or the index has not listed it yet; an inventory of the dataset kind finds it if it did). A
dataset artifact with an id and no path is soft-deleted by its id; one with neither is `kept`. The files a registration
copied stay in the landing zone, which no call removes.

### Registrations under ids the route chooses

The Dataset service writes a registration through storage under the id it is given, so its answer can be lost after it
landed. Before each such registration (a dataset record, the dataset a record of another kind keeps its files in, a
workflow's inputs and a storage anchor's files), the route asks storage in one batched read (`POST /query/records`)
whether it holds the id:

| Storage answers | Declared as |
| --- | --- |
| It holds the id, at a version | `version`, with that version as `PriorVersion`: the undo writes it back. |
| It does not hold the id | `record`: the undo removes it once storage confirms OSDU created it after the unit began. |
| An error, or no version | Nothing is declared and nothing is registered; the record fails with `storage could not say whether it holds <id> ... nothing was registered`. |

The intent is reported before the registration goes and completed with the version the Dataset service answered. These
registrations are taken back even when newer work follows: that work reads what OSDU holds of them (a record of another
kind names its dataset without a version, and a dataset record's own write carries the `DatasetProperties` storage holds),
so leaving them would hand the newer work the files of a delivery that did not complete.

### Workflow runs

A workflow run is declared before it is triggered, under the run id the route chose, in a slot of its own (`run:<runId>`,
role `run`, locator the workflow name), so a run whose trigger answer was lost and a run a later try triggered beside it
are both on the ledger. Before it takes anything back, the undo asks the Workflow service about every run the unit
triggered:

| The Workflow service says | What the undo does |
| --- | --- |
| A pending state (`SUBMITTED`, `INPROGRESS`, `IN_PROGRESS`, `RUNNING`, `QUEUED`) | Waits: every artifact of the record is answered `failed` with `workflow run <id> of <workflow> is still <state> ... nothing is taken back until it ends`, and the sweep tries again. |
| A state not known as ended (a retry Airflow scheduled) | Waits the same way. |
| The state cannot be asked | Waits the same way, naming the error. |
| An ended state (`SUCCESS`, `PARTIAL_SUCCESS`, `FINISHED`, `FAILED`) | Goes on; the run is `kept` with what it ended as (Airflow keeps its log). |
| No such run | Goes on; the run is `kept`: its trigger did not land. |

Records a run wrote that the delivery did not name stay, for an inventory of their kind to find. The manifest and
workflow routes, and `manifestAndDdms` for its manifest part, take this one path.

## storage

The `storage` route makes one atomic write per batch, so it declares no artifacts and has nothing to undo; the worker
reads no artifacts for it.

## file

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `file:<index>:<hash>` (the chunk's place and its landing-zone path, hashed) | `dataset` | An intent, locator the landing-zone path, before `POST files/metadata`; completed with the dataset id. A try that finds a registration whose answer was lost takes it over through the index. | [Datasets registered for files](#datasets-registered-for-files). |
| `file:<index>:<hash>:<id>` | `dataset` | A second registration a lost answer let a later try make for the same path: removed by the route at once (`removed`), or, when that fails, an intent naming it by id alone, which the unit's end makes `due` whatever the delivery comes to. | Soft-deleted by its id. |
| `record` | `record` or `version` | Only when the unit registered files: an intent before the storage write, then the written version once it answers. | [The record itself](#the-record-itself), first; its datasets wait for it. |

A payload change names the datasets the earlier delivery registered `superseded` when the unit commits. Each upload
lands at a new path, so a try that uploads a file again makes a slot of its own.

## dataset

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `record` (a record of a dataset kind) | `record` or `version` | [Registrations under ids the route chooses](#registrations-under-ids-the-route-chooses), before the Dataset service registration. | Removed through the Dataset service's `softDelete`, or given back the version before through storage, even when newer work follows. |
| `files-dataset` (the dataset a record of another kind keeps its files in, under an id derived from the record's) | `record` or `version` | The same. | The same. |

A record of another kind is written last, through storage, in one write, so no unit leaves it behind and it is not
declared. A change to a dataset record alone goes through storage with the `DatasetProperties` OSDU holds and declares
nothing. The files a registration staged stay in the platform's storage, which no public OSDU operation deletes.

## manifest

Declared in this order:

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `file:<index>:<hash>` | `dataset` | As the file route, before the manifest. | [Datasets registered for files](#datasets-registered-for-files), after the record. |
| `record` | `record` when storage held no version before the run, else `version` naming that version | An intent before anything of the run is sent, locator `workflow\|runId`; reported again with the version once the read-back finds it. One slot per unit: a retry's new run takes it over, and the prior version stays the one first reported. | [The record itself](#the-record-itself), through storage, once every run has ended. |
| `run:<runId>` | `run` | Before the trigger. | [Workflow runs](#workflow-runs): waited for, then `kept`. |
| `manifest:<runId>` | `dataset` | When the manifest goes by reference: an intent naming the id the route chose (`<partition>:<dataset entity type>:osdu-delivery-manifest-<runId>`), before it is stored. Reported `removed` by the route once its run settled; when that removal fails it stays an intent, which the unit's end makes `due`, and the sweep removes it. | Soft-deleted by its id. |

The undo also waits for any run a `record` artifact's locator names. A payload change names the datasets the earlier
delivery registered `superseded` when the unit commits.

## workflow

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `dataset:<name>` (an input's dataset, or a storage anchor's file) | `record` or `version` | [Registrations under ids the route chooses](#registrations-under-ids-the-route-chooses), under ids derived from the anchor's. | Removed through storage, or given back the version before, even when newer work follows. |
| `record` (the anchor) | A dataset anchor with new files: `record` or `version` from storage's answer. A storage anchor: `record` when the ledger held no version, else `version` | An intent before the write, then the written version. | A dataset anchor: removed through the Dataset service's `softDelete` or given back the version before, even when newer work follows. A storage anchor: [the record itself](#the-record-itself), left to newer work when it writes the anchor again. |
| `run:<runId>` | `run` | Before each stage's trigger, under the id the route chose. | [Workflow runs](#workflow-runs): nothing is taken back until every run has ended; then `kept`. |
| `output:<hash of id>` | `output` | The records the results found, other than the anchor, up to `results.keep`. | With `results.remove` (the default): read from storage; created after the unit began, soft-deleted; created before (a run updated it), `kept`; not there, `gone`. Without it: `kept`. |

## ddms

The `ddms` route undoes one record at a time on the record's DDMS: first what the record's shape made beside it, then
the record itself. When something beside the record could not be undone yet, and newer work does not keep the record, the
record is answered `failed` with it and goes on the next undo. A record the flow can no longer route has every artifact
answered `failed`, for the sweep to try again once it can.

How the record itself goes back depends on the shape:

| Shape | Removal of a record the unit created | Write-back of the version it replaced |
| --- | --- | --- |
| Wellbore DDMS | The DDMS's logical delete; its read says when OSDU created it. | Through storage with its own bulk link, which brings back the bulk data that version names, under a platform endpoint only. |
| Well Delivery | Not used: the entity version is undone as an `objects` artifact. | Not used. |
| RAFS | RAFS's logical delete; its read (past RAFS's cache) says when OSDU created it. | Through storage, under a platform endpoint. |
| Production historian, Seismic Store, Reservoir Management | Storage's soft delete, when the flow says where storage is (a platform endpoint with the DDMS's root under `target.ddms`), else `kept`, naming why. | Through storage, under a platform endpoint. |

With the flow's endpoint the DDMS itself, a version cannot be written back: `the flow's endpoint is the DDMS itself, so
the storage service that keeps the record's versions is not under it`.

### Wellbore DDMS

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `record` | `record` or `version` | Only when bulk data follows: an intent before the metadata write, then the written version. | [The record itself](#the-record-itself), after the session. |
| `session:<guid>` | `session` | An intent (locator the record id) before the create, which is never resent; then the session id. A session the route abandoned when its delivery failed is reported `removed`. | Abandoned (`PATCH {state: abandon}`), `removed`. Committed or abandoned already, or unknown: `gone`. Still `committing` or `abandoning`: `failed`, since a commit that lands writes the bulk data under the record; the undo waits. An intent with no id: the record's open sessions are listed and each abandoned. No session path for the collection: `kept`. |

What a session aggregated stays in the DDMS's store, which no call removes.

### Well Delivery

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `record` | `objects` (the version of the entity the unit wrote, with the version the ledger held as `PriorVersion` and, where the deployment copies entities into Storage, the copy's id as locator) | An intent before the write, then pending. Nothing for a version the ledger holds written again in place. | That version alone soft-deleted (`DELETE .../{id}/{version}`, which a write of the same version reverses), so the version the ledger holds is the latest again: `removed`; not held, `gone`. Newer work that writes the entity again: `superseded`. The Storage copy of an entity the ledger held no version of is soft-deleted too when Storage says the unit's write created it; any other copy keeps the version the write gave it. |

An entity another system wrote under the same id keeps its other versions. On Azure and IBM the DDMS's reads keep
returning a soft-deleted version.

### RAFS

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `record` | `record` or `version` | When content follows, once the metadata write answers. | [The record itself](#the-record-itself). |
| `content:<contentType>` | `content`, `version` or `objects` | An intent (`content`, locator `<record id>#<contentType>`) before each table's write; then `content` for a dataset RAFS minted, `version` for a new version of a dataset an earlier delivery made (naming the version the ledger held), or `objects` in blob mode, where RAFS keeps no dataset. | `content`: soft-deleted through storage (RAFS never removes one); `kept` when the flow does not say where storage is. An intent whose answer was lost: the record is read and the dataset RAFS named in `data.DDMSDatasets` for that type removed, or, when the write landed on the earlier delivery's dataset, that dataset given back the version the ledger holds; nothing found, `gone`. `version`: written back with the record. `objects`: `kept`, since blob-mode content stays in RAFS's own store. |

The files a content dataset points at stay.

### Production historian

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `record` | `record` or `version` | When points follow, once the metadata write answers. | [The record itself](#the-record-itself). |
| `points:<n>` and `points:<n>:partial:<guid>` | `points` (locator the accepted series versions, `series=version`) | Each request's accepted series versions, and those of a partly refused request before the try fails. | `kept`: the historian has no delete for points, so the series versions it accepted stay. The record given back its earlier version, or removed, no longer describes them. |

### Seismic Store

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `seismic-dataset` | `objects` (the dataset's path) | An intent before the registration; created once it answers. A dataset that already held this record before the unit is reported `gone` (taken over, not the unit's to delete); one an earlier try of the unit registered without hearing back is taken over as the unit's. A registration refused (locked by another writer, or held by another record) reports it `gone`. | Deleted with its files and its lock (Seismic Store has no reversible delete): `removed`, or `gone`. On gc: `kept`, since one dataset's delete takes the files of every dataset in its subproject. The provider cannot be told: `failed`, nothing deleted. Newer work that takes the dataset over: `superseded`. |
| `lock` | `lock` (locator the lock id) | An intent before the registration or the open; created once taken; reported `removed` when the close or a hold released it. | Released under this delivery's lock id, `removed`; no lock under that id (it lapsed, and any lock now is another writer's), `gone`; recorded without its id, `kept`. `gone` when the dataset was deleted. |
| `readonly` | `lock` | When the open lifts the dataset's read-only flag; reported `removed` when the close sets it again. | Set read-only again, after the lock is released: `removed`. |
| `record:register` | `record` or `version` | The record a registration carries, an intent before it goes; settled `gone` once the registration answers, the `record` slot naming what was written. | Taken back as [the record itself](#the-record-itself) while still open. |
| `record` | `record` or `version`, no version (Seismic Store does not say which it wrote) | An intent before a call that carries the record, then reported once written. | [The record itself](#the-record-itself), through storage. |

Objects an update overwrote cannot be put back: Seismic Store keeps no earlier objects, and the ledger no earlier bytes.

### Reservoir Management

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `record` | `record` or `version` | When rows follow, once the header record is written. | [The record itself](#the-record-itself), after the rows. |
| `rows:<block>` and `rows:<block>:<part>` | `rows` (locator the table and keys in runs) | Each block of up to 50 rows with the keys the service gave them, split where its runs would not fit one locator; the rows posted since the last block before a failed try ends. | Every row the unit recorded deleted, the rows below before the rows above (the service has no reversible delete): `removed`, or `gone` when none was held. |
| `rows-pending:<n>` | `rows` (locator `table\|parent`) | An intent for a row whose post was not answered. | The rows under its parent that no delivery names are read: exactly one, deleted (`removed`); none, `gone`; more than one, `kept`, none deleted. |
| `rows-replace` | `rows` | Once every row is posted and the earlier delivery's rows begin to be deleted. | `kept`: the rows already deleted cannot be put back, and the record's next delivery posts its rows again. |

## fileAndDdms and manifestAndDdms

`fileAndDdms` declares the file route's `dataset` artifacts and whatever the record's DDMS shape declares. Its undo
hands everything but the datasets to the `ddms` route (what the shape made beside the record, then the record), then
removes the datasets as the file route does, once the record has gone back.

`manifestAndDdms` declares the manifest route's artifacts (datasets, the record a run is to write, runs, a manifest by
reference) and what the DDMS shape makes for the bulk data. Its undo hands the bulk part (a session) to the `ddms`
route first, then the manifest's artifacts to the manifest route's undo, which waits for every run before it takes back
the record and the datasets.

Both name the datasets the earlier delivery registered `superseded` on a payload change.

## dspdm

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `row` | `rows` | An insert, or a row an earlier try inserted and this one takes over: an intent at `save-begin`, locator the business object's kind and the key the save sent (none when the key is too long to record), before the save; then the primary key DSPDM drew. An update declares nothing. | Deleted for good (DSPDM keeps no deleted rows) by its primary key, or, when the insert's answer was lost, by the one row its key finds: `removed`; no row, `gone`; more than one, `kept`, none deleted. A row recorded with neither key nor locator is `kept` for an inventory of the business object. |

The row is deleted even when newer work follows: that work finds the row by its key, does not know it as the record's,
and would be held. An update leaves nothing to undo, since DSPDM keeps no versions.

## etp

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `object` | `objects` (the object's URI, locator its dataspace) | Only for an object the ledger knows no object of: an intent before the commit, whose answer can be lost, then pending once it lands. An update declares nothing. | Read from the dataspace: created by the store after the unit began, deleted for good in one transaction per dataspace (`removed`); created before, `kept` with the content the delivery wrote, since the store keeps no earlier versions; not held, `gone`. A locked dataspace is unlocked for the delete and locked again. A deleted object's arrays stay in the dataspace. |
| `dataspace` | `dataspace` (its OSDU record id, locator the path) | On the first record that needed a dataspace the delivery created. Live once the unit commits. | `kept`: a dataspace is never deleted; the Reservoir DDMS purges its OSDU record when it is, and other records' objects may live in it. |

The undo deletes objects the unit created whether or not newer work follows.

## Records a mapping's enrichments add

A flow whose mapping's enrichments add records ([records an enrichment adds](../flow/enrich-records.md)) wraps its route,
whichever it is, `storage` included, and writes each added record before the record itself:

| Slot | Role | Declared | Undo |
| --- | --- | --- | --- |
| `enrich-dataset:<hash of id>` (a dataset an enrichment registers with its files) | `record` or `version` | [Registrations under ids the route chooses](#registrations-under-ids-the-route-chooses), before the Dataset service registration. | [The record itself](#the-record-itself), removed through the Dataset service's `softDelete`, even when newer work follows. |
| `enrich-record:<hash of id>` (any other record an enrichment adds) | `record` or `version` | The same, before its storage write. | [The record itself](#the-record-itself), removed through storage's soft delete, even when newer work follows. |
| `enrich.<name>.` followed by the workflow route's slot | as the [workflow](#workflow) route's | Before each trigger of an [enrichment's workflow](../flow/enrich-workflow.md). | The workflow route's undo. |

Each record an enrichment adds has a slot of its own, so an undo reaches every one. A ledger an earlier release wrote may
hold them under the bare `enrich-dataset` or `enrich-record`, one row for every record a delivery added, naming the last one
written; an undo still takes such a row, and a try resuming a unit an earlier release began declares a record that
release declared there in the same slot, so its intent is completed rather than undone. Everything else the unit
declared is handed to the flow's own route. The files a registration staged stay in the platform's storage.

## What no undo removes

| Left behind | Route or shape | Why |
| --- | --- | --- |
| Files a registration copied into a landing zone or staging area | file, dataset, manifest, workflow, composed | No call removes them. |
| Files behind a soft-deleted dataset | every route that registers datasets | A soft delete keeps them, so the dataset can be restored. |
| Workflow runs, and records a run wrote that the delivery did not name | manifest, workflow, manifestAndDdms | Airflow keeps the run; an inventory of the record's kind finds the records. |
| What a session aggregated | Wellbore DDMS | It stays in the DDMS's store. |
| Accepted series versions | Production historian | The historian has no delete for points. |
| A dataset registered on gc, and objects an update overwrote | Seismic Store | One delete takes a whole subproject's files; no earlier objects are kept. |
| Blob-mode content, and the files behind content datasets | RAFS | RAFS's own store keeps them. |
| Earlier rows a replacement deleted | Reservoir Management | They cannot be put back; the next delivery posts them again. |
| An object the store held before the unit, at the content the delivery wrote; a deleted object's arrays; dataspaces | etp | The store keeps no earlier versions and does not delete arrays with their object. |

Each of these is answered `kept` (or, for files, not declared at all), with the note on the artifact naming it.

## How the ledger and the GUI show it

Each undo of a record is one attempt, outcome `undone`, phase `undo`, written with what it settled in one transaction.
Its result carries the reason, whether the record was kept for newer work, the units, a summary line (`2 removed, 1
kept`) and every artifact with its slot, role, id, locator, versions, outcome and note; its error names up to three
artifacts still to undo. The record's own status is not changed: an undo takes back what a delivery left, it does not
decide what the record is. Settled artifacts record when, in which run and by whom (`route` for one the route settled
itself, else the worker or the operator). See [Record lifecycle](record-lifecycle.md#waiting-for-an-undo) for how newer
work waits on an undo.

`sqlflow records artifacts` lists a record's artifacts with their state and note, and `sqlflow records undos` the records
with open ones ([sqlflow records](../cli/records.md#records-artifacts)). In the GUI a record's **Artifacts** tab shows each
state as a neutral chip led by its glyph: due in the warning tone, failed in the destructive tone (with **failed, no
tries left** once the sweep has used its ten tries), live in the success tone, kept in the warning tone, and what an undo
settled quietly.

## Related pages

- [Removal and reversal](removal-and-reversal.md#unfinished-deliveries-and-their-undo): when the undo runs, its retries
  and the commands that list and force it.
- [The delivery ledger](ledger.md#osduartifact-every-id-a-delivery-minted): the `osdu.Artifact` table.
- [Protocols](protocols.md) and [Routes](../flow/routes.md): what each route sends and how a removal reaches it.
- [DDMSs](../flow/ddms.md) and [DDMS shapes and services](../flow/ddms-services.md): the shapes this page undoes.
- [Finding orphans](../guides/finding-orphans.md): ids OSDU serves that no ledger accounts for.
