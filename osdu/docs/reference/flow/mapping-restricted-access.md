---
id: delivery-flow-mapping-restricted-access
title: "Restricted until complete ($restrictWhenMissing, restrictedAccess): a record missing a value goes out under a registered restriction's ACL, and gets its own access back once the value arrives"
type: flow-reference
summary: "$restrictWhenMissing sends a record missing a value under the restriction restrictedAccess names, not held; it gets its own access back once the value arrives."
keywords:
  - restrictWhenMissing
  - $restrictWhenMissing
  - restrictedAccess
  - restricted until complete
  - restricted mode
  - restrict when missing
  - missing classification
  - security classification missing
  - deliver restricted
  - incomplete record
  - IncompleteRestrictionId
  - IncompleteMissing
  - missing values
  - unclassified records
yamlPath: "record.<property>.$restrictWhenMissing, restrictedAccess (documentType: mapping)"
related:
  - delivery-flow-mapping-values
  - delivery-flow-mapping
  - delivery-concept-access-restriction
  - delivery-concept-record-lifecycle
  - delivery-table-record
  - delivery-table-record-event
  - delivery-table-access-restriction
  - delivery-flow-enrich-records
  - delivery-cli-values
  - delivery-cli-records
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EnrichParts.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Reversals/VersionWriteBack.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DocumentAccess.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Catalog/AccessRestrictionRegistry.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingCoverage.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/gui/src/features/delivery/DeliveryFlowPanel.tsx
  - osdu/gui/src/features/delivery/RecordSituation.tsx
  - osdu/docs/census/keys.mapping.json
---

# Restricted until complete: a record missing a value goes out under a registered restriction's ACL

A value its mapping requires holds a record while it is missing ([$required](mapping-values.md#required-what-an-empty-value-does)).
Some values decide who may read the record rather than what it says: a security classification, an export control, the
data owner's consent. A record without one cannot be shown to the groups its mapping gives, and holding it keeps the
data out of OSDU for everyone. `$restrictWhenMissing: true` on such a node sends the record anyway, **restricted until
complete**: without the property, readable only by the groups of a registered [access restriction](../concepts/access-restriction.md)
the mapping names under `restrictedAccess`. Once a delivery finds every such value, the record is written with the access
its mapping gives. Nothing needs releasing, and the ledger keeps, for every record, the access OSDU holds it under.

```yaml
# The access a record goes out under while a value marked $restrictWhenMissing is missing: an access restriction
# registered for the partition the flow delivers to (Admin, Access restrictions), here read from a parameter.
restrictedAccess: "{$param.restriction}"

parameters:
  restriction: { required: true, description: The access restriction an unclassified log goes out under. }

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  tags:
    dra.securityClassification:
      $from: Lra_securityClassification
      $restrictWhenMissing: true          # missing: the log goes out restricted, without the tag
    dra.exportControl:
      $from: Lra_exportControl
      $restrictWhenMissing: true
    dra.dataID: { $from: Lra_dataID }     # missing: the log is held, as before
```

## What a missing value does

`$restrictWhenMissing` changes what a missing value does and nothing else. Wherever `$required: false` would leave the
property out, the node leaves it out and restricts the record; wherever `$required: false` would still hold the record,
the node holds it too. A value that is there but wrong is never a reason to restrict.

| Situation | `$restrictWhenMissing: true` |
| --- | --- |
| The column, or what `$expr` gives, is empty after the modifiers | Property left out, record restricted |
| A lookup joined into the read (`source.*.lookups`) has no row for the record, so its columns are empty | Property left out, record restricted |
| The cache holds no record `$findBy` or `$lookup` finds, or no row `$findAll` finds | Property left out, record restricted |
| An `id` or `ref` names a record the cache does not hold, or a token of it has no value | Property left out, record restricted |
| No record on the platform matches a `$search` | Property left out, record restricted |
| The record's parent holds no section `$parent` reads | Property left out, record restricted |
| No alternative of a `$coalesce` gives a value | Property left out, record restricted, naming why each gave nothing |
| `$when` does not hold | Property left out; nothing is restricted |
| An assertion whose `onFail` is `omit` leaves the value out | Property left out; nothing is restricted |
| Several records answer to the value, the value cannot be read, an expression fails, the parent is not in OSDU | Record held |
| The platform was asked and has not answered yet | The render waits for the answer, as every render does |

Each missing value is said as a hold would say it, without its required clause:
`osdu.tags['dra.securityClassification']: dataset.Lra_securityClassification is empty`. A reason the items of a
`$forEach` share is said once.

## Where it can be written

| Node | Takes `$restrictWhenMissing` |
| --- | --- |
| A value node (`$from`, `$expr`, `$cache`, `$search`, `$parent`), a `$lookup` node, a `$coalesce` node | Yes, beside `$required` left true |
| An item of a list of values, a property of an item of a list of objects, a property of a `$forEach` item | Yes |
| A `$forEach` node itself, an alternative of a `$coalesce`, a literal `$value`, a `$child` or a `$link` node, the `lookups` block | No, refused naming what to write instead |
| Beside `$required: false` | No: `$restrictWhenMissing restricts the record when the value is missing, and $required: false leaves the property out with the record's own access; keep one of them.` |
| A property the template requires in `data` | No: the [preflight](../concepts/preflight.md) refuses it, since the record could not be sent without the value |
| An enrichment mapping (`enrichment: true`), a DSPDM business object row | No: neither writes an ACL of its own |

`restrictedAccess` is a header key of the mapping ([header keys](mapping.md#header-keys)): the name of the restriction as
the partition registers it, as text that may read parameters (`{$param.<name>}`), each a parameter the mapping declares.
A mapping names it exactly when a node is marked: a marked node without it, and `restrictedAccess` with no node marked,
are refused as the mapping is read.

## What a restricted record goes out with

The record's `acl` becomes the restriction's viewers and owners, whole: none of the groups its mapping gives (the flow's,
a field's, a country's) reads it. Its legal block, its other properties and its id are its mapping's. The ACL is part of
the document, so a record that moves between restricted and its own access is sent again, as any record whose document
changed is.

The records the mapping's enrichments add beside it ([records an enrichment adds](enrich-records.md)), a well log's
`dataset--File.Generic` among them, carry the record's ACL, so each goes with the record's access too. The access enters
each one's hash while the record is restricted, so restricting the record, or giving it its own access back, writes them
again with the record; a record with its own access hashes them as it always did, so nothing is sent again for a mapping
that names no restriction.

Storage reads every version of a record under the ACL the record holds now ([osdu/specs/core INTEGRATION.md](../../../specs/core/INTEGRATION.md),
2.3), so a restricted write hides the record's earlier versions with it, and the write that gives it its own access back
shows them again.

## Getting its own access back

A record restricted until complete is planned by every run as any other: it holds nothing off. The value arriving moves
it by the paths a changed value always takes:

| The value arrives through | What plans the record again |
| --- | --- |
| The record's row | The row's fingerprint moves ([change detection](../concepts/change-detection.md)) |
| A lookup table joined into the read | Each joined lookup row's update moment is in the record's fingerprint ([delivery lookups](delivery-lookups.md#when-a-lookup-row-changes)) |
| The partition's cache | The cache rollout plans every record built from the values that changed |
| The record's parent | The parent landing plans the records that read it |
| Anything else (a platform search) | The record's next plan: a change of its row, a redelivery, a replan |

The render that finds every marked value writes the record with the access its mapping gives, and the try that sends it
says so on the record's history.

## What the run checks

Before anything is read, the flow is resolved with its restriction, and refused when:

| Refusal | Why |
| --- | --- |
| `... under access restriction '<name>', and partition '<p>' registers none under that name; register it under Access restrictions ...` | A record is never sent with the access its mapping gives for want of the restriction. |
| `... and the flow cannot keep a record's access to its ACL: ...` | The route is one a restriction cannot rely on: the same routes an operator's restriction refuses ([Which routes](../concepts/access-restriction.md#which-routes)). The storage route and the Wellbore DDMS route under a platform endpoint take it. |
| `... its enrichment '<name>' adds records and is set aside on its holds (onHold: step) ...` | A record restricted while the enrichment is set aside would keep linking records written with the access they had. |

Before a deliver run plans anything, the flow's identity is checked, once, to be a member of one of the restriction's owner
groups or a data manager, as an operator's restriction checks it: storage lets only an owner write a record again, and a
record the flow restricted could otherwise never be given its own access back. Refused, nothing is planned or sent.

## What the ledger keeps

What OSDU holds changes only when a document is sent, so the ledger takes the access from the try that sent it: the work
batch line carries the access its document goes out under (`restricted`), and the completion of a try that wrote the
document hands it to the ledger (`osdu.RecordEvent.AccessSent`). A try that sent no document (a payload alone, nothing at
the final hash check, a failure) leaves the record's access as it was, since an identical document carries the same ACL.

| Column ([`osdu.Record`](../tables/record.md)) | Holds |
| --- | --- |
| `IncompleteRestrictionId` | The restriction OSDU holds the record under; null for its own access. |
| `IncompleteSinceUtc` | When OSDU first held it so, kept through the deliveries that keep it so. |
| `IncompleteMissing` | What the document OSDU holds goes without, a value to a line, at most 2,000 characters. |
| `IncompleteHash` | The group of missing values: records missing the same share it, as records held for the same reason share an issue. |

The delivered attempt says what the try did to the record's access, under `access` in its result
(`{"restricted": true, "restrictionId": 3, "restriction": "unclassified", "missing": [...], "was": null}`, or
`{"restricted": false, "was": 3}` for a record given its own access back), and in its detail
(`restricted until complete under access restriction 'unclassified': ...`, `given its own access back: ...`), which the
`record.delivered` event carries too. A record a run re-sends still restricted says `still restricted`.

## Finding what is missing

| Where | What |
| --- | --- |
| A flow's Records tab | The status filter's **restricted until complete** entry, with its count; the **Access** column next to Status, each badge listing what the record goes without on hover; a link's `missing=<group>` narrows to one group of missing values. |
| A record's page | A line naming the restriction, since when, and the first missing value, every value on hover, with **Every record missing the same**. |
| The Records page | The same status entry and Access column across every flow. |
| The Delivery page | A flow card's `N restricted until complete`, which opens the Records tab on them. |
| Export | The Records workbook's **Access**, **Missing values** and **Missing group** columns. |
| CLI | `sqlflow records list --incomplete`, `--missing <group>`; `records show` lists each missing value ([records](../cli/records.md)). |
| API | `incomplete=true`, `missing=<group>` on the records listing, lookup and export; `incompleteRestrictionId`, `incompleteSinceUtc`, `incompleteMissing`, `incompleteGroup` on a record, `restrictedUntilComplete` on its detail and a flow's counts ([API](../concepts/api.md)). |
| Before a run | `sqlflow values` and **Check values** report a row a marked node gives no value as `restricted`, counted apart, without failing the check ([values](../cli/values.md)). |

## With the other ways a record's access changes

| What | Does |
| --- | --- |
| An operator restricts the record ([Restricting access](../concepts/access-restriction.md)) | Holds it off as always; the ACL it gives back when lifted is the one OSDU held, restricted until complete or its own, and the record is planned again. |
| A reversal or a step back to an earlier version | Writes it back under the restriction's groups while the record is restricted until complete, so neither ever opens it; one whose restriction is no longer registered is not written back, saying why. |
| An undo of an aborted delivery | Writes back the version before the try as OSDU returns it; the retry that follows sends the record's document with the access it goes out under. |
| Removing the access restriction from the registry | Refused while OSDU holds a record restricted until complete under it, naming how many. |
| Another instance that delivers the same OSDU ids ([linked instances](../guides/linked-records.md)) | Writes the ACL its own mapping gives; name the restriction in every instance's mapping. |

## Changing it

`restrictedAccess`, the restriction's groups and every node's flag are part of the record's render context, so naming a
restriction, another one, or marking a node renders the flow's records again; a record whose document did not change is
not sent. Records held at their render for a value now marked are rendered again under the new rules by the next run that
reads their rows ([A record held at its render](../concepts/record-lifecycle.md#a-record-held-at-its-render)), and go out
restricted. Removing the mark holds a record still missing the value, which OSDU keeps restricted until it is delivered
again.

## Related

- [Mapping values](mapping-values.md): `$required` and the other settings of a node.
- [Restricting access](../concepts/access-restriction.md): the registry and an operator's restriction.
- [Record lifecycle](../concepts/record-lifecycle.md): states, holds and releases.
- [`osdu.Record`](../tables/record.md): the columns the ledger keeps.
