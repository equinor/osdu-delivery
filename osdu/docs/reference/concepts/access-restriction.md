---
id: delivery-concept-access-restriction
title: "Restricting access: replacing a delivered record's ACL in OSDU with a registered restriction's groups, and lifting it"
type: concept
summary: "Hiding a record instead of removing it: the registry of restricting ACLs, the call, what the ledger keeps and holds off, lifting, and which routes."
keywords:
  - restrict access
  - access restriction
  - restricted ACL
  - restricting ACL
  - lift restriction
  - unrestrict
  - quarantine a record
  - legal hold
  - hide a record in OSDU
  - change acl group
  - held records acl
  - metadata patch
  - PATCH /records
  - acl.viewers
  - acl.owners
  - RestrictionId
  - restrict a dataset
  - enrichment records acl
  - access check
related:
  - delivery-concept-removal-and-reversal
  - delivery-concept-record-lifecycle
  - delivery-concept-ledger
  - delivery-table-access-restriction
  - delivery-table-record
  - delivery-flow-routes
  - delivery-cli-run
  - delivery-concept-api
  - delivery-flow-enrich-records
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Restrictions/RecordRestrictions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Restrictions/RestrictionRoute.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/StorageRecordAccess.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/EnrichPartsProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/src/SqlFlow.Delivery/Protocols/RecordAccess.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/RecordRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Restrictions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/AccessRestrictions.cs
  - osdu/src/SqlFlow.Delivery/Catalog/AccessRestrictionRegistry.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAccessRestrictionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/gui/src/features/delivery/RemovalDialog.tsx
  - osdu/gui/src/features/delivery/AccessRestrictionsPage.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordPage.tsx
  - osdu/gui/src/features/delivery/accessRestrictionText.ts
---

# Restricting access: replacing a delivered record's ACL in OSDU with a registered restriction's groups, and lifting it

A record found not to belong in OSDU can be removed ([Removal and reversal](removal-and-reversal.md)), or it can be kept
and hidden: its ACL is replaced with the groups of a registered **access restriction**, so only the members of those groups
read it, and the ledger keeps it out of every run until someone lifts the restriction. Nothing is deleted and no version is
written: the record, every version and its history stay as they were, and lifting the restriction gives the record back the
ACL it had. It is the way to take a record out of reach when its requirements changed (a record now held because its
mapping's assertions changed, a record under legal review), whatever its custody state, as long as OSDU holds a version the
flow delivered.

| You want to | Use |
| --- | --- |
| Keep a record in OSDU, readable only by a registered group | **Remove**, **Restrict access**, picking the restriction (`scope: restrict`). |
| Give a restricted record its access back | **Remove**, **Lift the restriction** (`scope: unrestrict`). |
| Keep a restricted record out of OSDU as well | Remove it at the `record` scope: it stays restricted, so it is not delivered again. |
| Register, describe or remove a restricting ACL | The **Access restrictions** page (administrators), or `/api/v1/delivery/access-restrictions`. |

## The registry

The groups a restriction sets are registered once per partition by an administrator, in
[`osdu.AccessRestriction`](../tables/access-restriction.md), and picked by name: a typo cannot send a record to a group no
one can reach. Each restriction has a name (unique in its partition, whatever its case), what it is for, and 1 to 20
**viewers** and **owners**, each a data group email storage accepts in an ACL (`data.<name>@<domain>`). The partition has to
be registered. The groups never change once registered, since OSDU holds them on the records restricted under it; a
different set of groups is a restriction of its own. What it is for can be said again, and a restriction is removed only
while no record carries it (409 `Restriction in use` otherwise: lift its records first).

| Route | Who | Does |
| --- | --- | --- |
| `GET /api/v1/delivery/access-restrictions?partition=<p>` | anyone signed in | The partition's restrictions (every partition's without `partition`), by name, each with its groups and how many records carry it. |
| `POST /api/v1/delivery/access-restrictions` | administrators | Registers one: `{partition, name, description, viewers, owners}`; 400 naming what it refuses. |
| `PUT /api/v1/delivery/access-restrictions/{id}` | administrators | Says what it is for: `{description}`. |
| `DELETE /api/v1/delivery/access-restrictions/{id}` | administrators | Removes one no record carries. |

Storage lets only an owner of a record change it again ([osdu/specs/core INTEGRATION.md](../../../specs/core/INTEGRATION.md),
2.8). A record restricted to groups the flow's identity is not a member of could never be given its access back by the flow,
so before a restriction changes anything, the run asks Entitlements for the flow's own groups (`GET /api/entitlements/v2/groups`
with the flow's credentials) and refuses unless the identity is a member of one of the restriction's owner groups (compared
by the name before `@`, as storage compares them) or a data manager (`users.data.root`):
`<identity> is a member of none of access restriction '<name>''s owner groups (...), nor a data manager, so a record restricted under it could never be given its access back by '<flow>' ... Nothing was restricted.`
The removal dialog asks the same check before anything is queued ([Checked before it is queued](#checked-before-it-is-queued)).

Who still reads a restricted record: the members of the restriction's groups (the flow's identity among them, so a verify and
an inventory still find it), and the partition's data managers, whom storage lets read and change every record.

## How a restriction runs

A restriction is a removal of scope `restrict` naming the restriction: the same `remove` run of the pipeline, named by keys
or by a listing filter (at most 25,000 records), resolved, held and worked 500 records at a time, under an activity of kind
`restrict` naming who asked ([Removing records from OSDU](removal-and-reversal.md#removing-records-from-osdu)). As every
removal does, it first undoes what unfinished deliveries of the records left. For each chunk:

1. The ledger alone passes over a record removed from OSDU, one OSDU holds no version of that the flow delivered, one
   restricted already (under this restriction, or another: lift that one first), and one its route cannot restrict
   ([Which routes](#which-routes)).
2. What OSDU holds of the others, and of the records beside each ([What a restriction reaches](#what-a-restriction-reaches)),
   is read with the batched read every route verifies with (`POST /api/storage/v2/query/records`), which answers each record
   with the ACL storage holds for it now. Storage's answer tells four records apart, and the result says which:

   | Storage | The record | Result |
   | --- | --- | --- |
   | Names it under `invalidRecords`: it holds no active version (removed outside this flow) | Passed over | `skipped` |
   | Names it under `retryRecords` each time it is asked (it could not read the record in time) | Nothing changed; ask again | `failed` |
   | Leaves it out of every list, as it does a record the caller may not read: the flow's identity is in none of the groups of its ACL and is not a data manager (storage's `BatchServiceImpl.getMultipleRecords`) | Nothing changed; add the identity to an owner group of the record's ACL | `failed` |
   | Answers it without an ACL of viewers and owners | Nothing changed; set its ACL in OSDU | `failed` |

   A record beside it that storage does not answer with its ACL fails the record the same way, since restricting the record
   alone would leave that one readable; one storage names under `invalidRecords` is left as it is.
3. Each is marked restricted in the ledger, keeping the ACL OSDU held for it and for each record beside it
   (`RestrictedFromAcl`), before OSDU is asked: the work queued for it is dropped (a pending or waiting record stands
   delivered at the version OSDU holds), and nothing is planned for it.
4. Its ACL, and that of each record beside it, is replaced through storage's metadata patch, `PATCH /api/storage/v2/records` with an `application/json` body
   of up to 500 ids, `{"query": {"ids": [...]}, "ops": [{"op": "replace", "path": "/acl/viewers", "value": [...]},
   {"op": "replace", "path": "/acl/owners", "value": [...]}]}`. Storage replaces the ACL in place, writes no version, governs
   every version of the record with it, and publishes the change, so the search index follows; it checks owner access per
   record and answers 206 naming the ids it did not change ([osdu/specs/core INTEGRATION.md](../../../specs/core/INTEGRATION.md),
   2.3).
5. What OSDU answered settles the ledger:

| OSDU answered | The record | Result |
| --- | --- | --- |
| Changed, for it and every record beside it | Restricted as of now (`RestrictedUtc`), with an attempt (outcome `restricted`, phase `restrict`) naming the restriction, the ACL set and the ACLs replaced. | `restricted` |
| Holds no record of the id, or refused it (the flow's identity is not an owner, an ACL refused), and changed none beside it | No longer restricted, and asked to be planned again, since step 3 dropped its queued work. | `skipped` or `failed`, with why |
| Changed some of the record and the records beside it, and refused the others | What it changed is given back at once and the restriction withdrawn, so a record is restricted whole or not at all. Should that give-back not go through, the record stays restricted, unconfirmed, as below. | `failed`, naming each id refused |
| Nothing that says (the answer was lost, the service failed) | Kept restricted, unconfirmed, with the ACL it had: OSDU may hold either ACL, and a restricted record is never shown by mistake. Asking again finishes it; lifting gives it the ACL it had. | `failed`, with why |

A restriction asked again of a record whose answer was lost uses the ACL the ledger kept, not the one OSDU holds now. A record
OSDU holds under the restriction's groups already, set outside it, is passed over, since the ACL it had cannot be told.
A run in which a record failed ends failed, as a removal does (`<n> of <m> record(s) of '<flow>' could not be restricted in OSDU: ...`).

The run's payload names the restriction:

```bash
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation remove --set partition=dev --payload '{"recordKeys":["<delivery key>"],"scope":"restrict","restriction":"legal-hold"}'
sqlflow run flows/welldb-wellbore-03-delivery.yaml --operation remove --set partition=dev --payload '{"recordKeys":["<delivery key>"],"scope":"unrestrict"}'
```

The API takes it as a removal does: `POST /api/v1/delivery/flows/{pipelineId}/records/remove` (and `/remove/preview`) and
`POST /api/v1/delivery/records/{flowId}/{key}/delete` with `{"scope": "restrict", "restriction": "<name>", ...}` or
`{"scope": "unrestrict", ...}`. A name the flow's partition has not registered is refused before a run is queued, and the
run checks it again. The preview counts the selection's restricted records (`restricted`), and the target names the call
a restriction makes for the flow (`restrictPath`) or why the route cannot (`restrictRefusal`). Each record restricted or
lifted emits `record.restricted` or `record.unrestricted`.

## What a restriction reaches

A restriction hides the record and every record OSDU holds beside it that the ledger names in the record's target state:
the records the mapping's enrichments add ([Records an enrichment adds](../flow/enrich-records.md)), such as the
`dataset--File.Generic` an enrichment registers for each well log's LAS file. Each is a storage record with an ACL of its own
(a copy of the record's when it was written, which a change of the record's ACL alone does not write again), so each is
patched with the record in one request, and the ledger keeps the ACL each had: `RestrictedFromAcl` holds the record's ACL,
and the records beside it in groups, each group with the ACL it had when that was not the record's. Lifting gives each the
ACL it had.

A dataset's files are reached through it: the Dataset service's retrieval instructions and the File service's download URL
read the dataset record through storage under the caller's token, and leave out a record the caller cannot read
([osdu/specs/core INTEGRATION.md](../../../specs/core/INTEGRATION.md), 2.4 and 2.5), so once its ACL is the restriction's no
one else is handed its files. A record beside it that OSDU no longer holds (storage names it under `invalidRecords`), or
that holds the restriction's groups already, set outside it, is left as it is and said in the record's result. One storage
withholds from the flow's identity, or does not answer for, keeps the record from being restricted, with why
([How a restriction runs](#how-a-restriction-runs), step 2). The ledger keeps the ACLs whatever their length: a record's
lists every group its mapping's lookups find, a well log's field and country groups among them.

## What a restriction holds off

While a record is restricted (`osdu.Record.RestrictionId` set, confirmed or not), nothing writes it in OSDU or forgets it:

| What | Does |
| --- | --- |
| A run's plan | Leaves the record as it is whatever its row says, saying `the record's access in OSDU is restricted since <time>; nothing is delivered to it until the restriction is lifted`. A write would carry the ACL its mapping gives and show it again. |
| A release (of the record, every blocked record, an issue) | Passes it over: a held record's kept document is never sent. |
| Send again, Bring up to date, a cache change, a parent or child that lands | Ask for it to be planned, and the plan leaves it as it is. |
| A step back to the previous version | Passes it over, saying so. |
| A reversal | Passes it over as `restricted`, and takes it again when the reversal is asked again, once the restriction is lifted. |
| A removal (`record`, `everything`) | Takes it out of OSDU and keeps the restriction, so it is not delivered again; `history` purges its earlier versions as usual. |
| Deleting from the ledger, alone or as a removal's extra step | Leaves it in the ledger (409 `Access restricted` for one record): forgetting it would deliver it again. |
| Deleting the ledger | Refused while any record of it is restricted, before anything is removed. |

A record held because its row now fails its mapping's assertions, and restricted, stays held and restricted: its old version
is hidden, and its new row waits until both are settled.

## Lifting a restriction

A removal of scope `unrestrict` (**Lift the restriction**) gives each restricted record of the selection back the ACL it had,
through the same metadata patch, one patch per distinct ACL, and each record beside it the ACL it had; a record not
restricted is passed over. A record removed from OSDU while restricted asks nothing of OSDU for itself, and the records beside
it, which removing the record alone leaves in OSDU, are given back their access. Each record lifted gets an attempt (outcome `unrestricted`, phase
`lift-restriction`), is no longer restricted, and is asked to be planned again, so it follows its source from here: the work
its restriction dropped, and any change of its row since, goes out with the next run, under the ACL its mapping gives. A
record OSDU no longer holds is lifted and marked missing, so that run sends it again under its id. One OSDU refuses, for
itself or a record beside it (the flow's identity is no longer an owner of it, for instance), stays restricted, with why.
Lifting it again once that is settled finishes it.

## Which routes

A record's access is restricted only where storage ACLs the ledger names are all of it:

| Route | Restricts | Why |
| --- | --- | --- |
| `storage` | Yes, through `PATCH` on its record path | The record is all the flow wrote. A flow that writes with another method than `PUT` (a facade) is refused, since the storage service is not known to be behind it. |
| `ddms`, a Wellbore DDMS record under a platform endpoint | Yes, through the storage service past the DDMS | The record is a storage record, and the DDMS reads it under the caller's token before it serves its bulk data, so its ACL governs both (Wellbore DDMS `app/routers/bulk/bulk_routes.py`). |
| `ddms`, another DDMS, or a DDMS endpoint | No | Data the DDMS keeps beside the record has access of its own; a DDMS endpoint has no storage service under it. |
| `file`, `dataset`, `manifest`, `workflow`, `fileAndDdms`, `manifestAndDdms` | No | Datasets or a workflow's records beside each record carry ACLs of their own. Remove the record instead. |
| `dspdm`, `etp` | No | Rows and Energistics objects carry no OSDU ACL. |
| A flow whose mapping's enrichments add records (on a route above that restricts) | Yes, with the records they add | Each record beside it is restricted with it ([What a restriction reaches](#what-a-restriction-reaches)). |
| A flow that runs a workflow over an enrichment whose created records the record keeps (`results.remove`, the default) | No | A search finds the records the runs created again; the ledger does not name each, so a restriction could not be sure to reach them all. Set `results.remove: false` where they are not the record's own ([Workflows over enrichments](../flow/enrich-workflow.md)). |

## Checked before it is queued

The removal dialog shows the call a restriction makes for the flow, or why its route cannot make it (`restrictRefusal`), before
anything is queued, and the control plane refuses a restriction its route cannot make with 400 `Cannot restrict`
(`'<flow>' cannot restrict its records' access: ... Nothing was queued.`). Once **Restrict access** is chosen, the dialog asks
whether the flow could lift each of the partition's restrictions again:

| Route | Who | Does |
| --- | --- | --- |
| `POST /api/v1/delivery/flows/{pipelineId}/access-check?interface=&partition=` | operate | Runs `delivery-access-check` in the control plane under the flow's own credentials, asking Entitlements for the flow's groups once, and answers each restriction registered for the flow's partition, by name. A partition that registers none is answered with none, and nothing is asked of OSDU. |

```json
{
  "flow": "welldb-welllog-03-delivery",
  "identity": "osdu-delivery@opendes.contoso.com",
  "restrictions": [
    { "restrictionId": 1, "name": "legal-hold", "usable": true },
    { "restrictionId": 2, "name": "quality-quarantine", "usable": false, "refusal": "The flow's identity (...) is a member of none of access restriction 'quality-quarantine''s owner groups (...), nor a data manager, ..." }
  ],
  "checkedUtc": "2026-11-06T12:00:00Z"
}
```

`refusal` beside `restrictions` says why the flow restricts nothing at all; `identity` is left out when Entitlements was not
asked. The dialog offers a restriction the flow could not lift disabled, says of the picked one whether the flow's identity
can lift it, and confirms **Restrict access** only for one it can. The run asks the same check again before it changes
anything, so a membership changed since is refused there in the same words.

## In the GUI

- **Remove**, on a record's page and on a flow's Records tab selection, offers **Restrict access** with the partition's
  registered restrictions to pick from (one alone is shown without a picker; with none, the choice says so and points to
  **Access restrictions**; one the flow could not lift again is offered disabled, and the picked one says whether the flow can),
  and **Lift the restriction** when the selection holds a restricted record. Neither offers **Delete from the ledger**, and
  both say why when the flow's route cannot restrict.
- A restricted record's page carries a **restricted** badge and a line naming the restriction, its groups and since when (or
  that it is asked and not confirmed) and the records beside it restricted with it, with **Lift the restriction**; the ACL
  it gives back, and the ids of the records beside it, are on hover. **Release** and
  **Redeliver** say why they do nothing for it. Its timeline shows the `restrict` and `lift-restriction` attempts with the
  ACL set and replaced.
- A flow's Records tab and the Records page narrow to **Restricted** records, the grid marks each one, and a flow's card
  counts them. A remove run's page says how many records it restricted or lifted, and under which restriction.
- **Access restrictions** (OSDU in the menu) keeps the registry ([The registry](#the-registry)).

## What it does not do

- It changes the ACL alone: the legal tags, the data and the version stay.
- Another instance whose ledger delivers the same OSDU ids ([linked instances](../guides/linked-records.md)) writes the ACL its
  mapping gives with its next write of the record. Restrict the record in every instance that delivers it, or remove it.
- It hides from search and storage reads; an export or a copy someone made before is theirs, and a download URL the File
  or Dataset service signed for a file before stays valid until it expires (an hour by default, at most seven days).
- Records a workflow over an enrichment created, where `results.remove` is false, are not the record's, and are left as
  they are.

## Related pages

- [Removal and reversal](removal-and-reversal.md): the removal a restriction runs as.
- [Record lifecycle](record-lifecycle.md): states, attempts and releases.
- [`osdu.AccessRestriction`](../tables/access-restriction.md) and [`osdu.Record`](../tables/record.md): what the ledger keeps.
- [Routes](../flow/routes.md): what each route calls.
