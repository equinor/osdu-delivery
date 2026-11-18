---
id: delivery-wiki-0062-restricted-until-complete-is-access-not-status
title: "Decision 0062: restricted until complete is the access a record is sent under, not a status of its own"
type: decision
summary: "Why a record missing a value that decides who reads it is delivered under a registered restriction's ACL, not held or given a status."
keywords:
  - restricted until complete
  - restrictWhenMissing
  - restrictedAccess
  - restricted status
  - record status
  - access axis
  - missing classification
  - incomplete record
updated: 2026-11-18
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DocumentAccess.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/VersionWriteBack.cs
  - osdu/src/SqlFlow.Delivery/Catalog/AccessRestrictionRegistry.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
referenceRefs:
  - delivery-flow-mapping-restricted-access
  - delivery-concept-access-restriction
  - delivery-concept-record-lifecycle
  - delivery-flow-mapping-values
  - delivery-table-record
---
# 0062: Restricted until complete is the access a record is sent under, not a status of its own

Status: accepted. Documented in [Restricted until complete](../../reference/flow/mapping-restricted-access.md),
[Restricting access](../../reference/concepts/access-restriction.md) and [`osdu.Record`](../../reference/tables/record.md).

The human's words: "one gap I have found is the restricted status. Records can be restricted but its missing as an
offical record status. See in some cases a required property missing should trigger a restricted mode. When the property
is there the record should be auto moved from restricted to regular mode. How can we implement a robust, production
grade solution to this and ensuring that the ledger doesnt break and does not lose its other triggers." Then: "the key
question is how do we figure whats missing on the restricted records?", and of the shape: "required attribute, but okay
for restricted. Thats the logic that would be nice that we can spesify on attribute left an addtional prop that puts the
record in a restricted mode. Secondly i like your idea for adding a restricted status column."

## What a new status would have broken

A record's status says where it is in custody: pending, delivering, delivered, held, failed, deleted
([record lifecycle](../../reference/concepts/record-lifecycle.md)). Everything that acts on a record reads it. The
planner sends a delivered record again only when its render changed, verify compares the delivered records with what
OSDU holds, a waiting record waits for its parent to be delivered, a parent landing plans the records that read it, and
the flow's figures count the delivered. A record OSDU holds under a restricted ACL is delivered in every one of those
senses: it has an OSDU id and version, verify can compare it, and its children can link it. A `Restricted` status would
have taken it out of each path, or every path would have had to learn the new value, which is where "does not lose its
other triggers" would have failed.

## The decision

- **An access axis beside the status.** `osdu.Record` keeps the access OSDU holds the record under in four columns of its
  own (`IncompleteRestrictionId`, `IncompleteSinceUtc`, `IncompleteMissing`, `IncompleteHash`), shown as an **Access**
  column next to the status. The status of a record restricted until complete stays delivered, so every path above
  treats it as it did.
- **The mark sits on the node, beside `$required`.** `$restrictWhenMissing: true` is the human's "required attribute, but
  okay for restricted". It restricts exactly where `$required: false` would leave the property out (an empty value, a
  cache or search that finds nothing, no `$coalesce` alternative), and everything that holds a record whatever
  `$required` says still holds it: a value that is wrong is never a reason to send the record.
- **The groups are a registered access restriction**, the same registry an operator's restriction uses, named in the
  mapping's header (`restrictedAccess`, which may read a parameter so a flow can choose it). Its groups never change once
  registered, a run checks before it plans anything that the flow's identity is in one of its owner groups or a data
  manager (storage lets only an owner write a record again, so a record the flow could not reopen is never sent), and the registry refuses to remove a
  restriction OSDU holds records under.
- **The ACL is part of the document.** The render replaces the record's `acl` whole, so the record is written once, under
  the restriction's groups. A render given no restriction holds the record rather than sending it with its mapping's
  ACL. The records an enrichment adds beside it hash the access while it is restricted, so they follow it.
- **The ledger takes the access from the try that sent the document.** The work batch line carries the access the
  document goes out under, and only a completion that wrote the document sets the columns; a try that sent nothing keeps
  them. The ledger says what OSDU holds, not what a render wanted, and the attempt's result says what the try did to the
  access.
- **What is missing is kept as a hold would say it**, a value to a line, and grouped by its issue signature as held
  records are grouped by their error: "every record missing the same" is one indexed read, on the record's page, the
  Records tab, the CLI and the API. That answers the human's key question.
- **Getting its own access back needs nothing of its own.** The paths a changed value already takes (the row's
  fingerprint, a joined lookup row, a cache rollout, the parent landing) plan the record again, and the render that finds
  every marked value writes it with its mapping's ACL.

## Rejected

- **A `Restricted` value of the record status.** See above: every reader of the status would have had to learn it, and
  each one that did not would have dropped the record from a path it belongs on.
- **Holding the record until the value arrives, with a release that sends it restricted.** A hold keeps the data out of
  OSDU for everyone, which is what the human wanted to avoid, and a release is a person's act where the human asked for
  the record to move back by itself.
- **Delivering it open and restricting it afterwards through the operator's restriction (an ACL patch).** Two writes per
  record, and a window in which the mapping's groups can read it; storage reads every version of a record under the ACL
  it holds now, so the window shows the record's whole history. An operator's restriction also holds the record off
  every run until it is lifted, so it could never open by itself.
- **An ACL written in the mapping instead of a registered restriction.** No owner check before a run, no refusal while
  records carry it, and the groups repeated in every mapping that restricts.
- **A restriction per missing value.** A record has one ACL, so several missing values would have had to pick one; one
  restriction per mapping says it once.

## What stays open

- An undo of an aborted delivery writes back the version before the try as OSDU returns it, so its ACL is the one OSDU
  held then; the retry that follows sends the record with the access it goes out under.
- Another instance that delivers the same OSDU ids writes the ACL its own mapping gives: each instance's mapping has to
  name the restriction.
- The editor's language server describes `$restrictWhenMissing` in the grammar of a record property, as it does every
  node setting; a misspelled setting is refused when the mapping is read, not as it is typed.
