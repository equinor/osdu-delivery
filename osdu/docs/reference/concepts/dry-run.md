---
id: delivery-concept-dry-run
title: "A dry run: a mapping's staged records taken through everything a delivery does, short of sending them"
type: concept
summary: "What a dry run does with a mapping's staged rows: what each comes to, how its document is held to the template and the Schema service, and what it costs."
keywords:
  - dry run
  - staged records
  - staged rows
  - see the document before delivery
  - schema service
  - validate a document
  - cross match
  - template and schema differ
  - unregistered kind
  - ready held worth a look
  - nothing sent
  - large tables
  - check-values dryRun
  - staged-rows
related:
  - delivery-concept-gui
  - delivery-concept-preflight
  - delivery-cli-values
  - delivery-cli-preview
  - delivery-concept-templates
  - delivery-guide-writing-a-mapping
  - delivery-concept-explorer
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Checks/DryRun.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/DryRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/SchemaCrossCheck.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueChecker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/StagedRowsOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/SchemaServiceReader.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Validation/ReferenceResolver.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryValueCheckEndpoints.cs
  - osdu/gui/src/features/delivery/StagedRecordsTab.tsx
  - osdu/gui/src/features/delivery/StagedRecordList.tsx
  - osdu/gui/src/features/delivery/SchemaCheckView.tsx
---

# A dry run: a mapping's staged records taken through everything a delivery does, short of sending them

A mapping's **Staged records** tab lists the rows staged for it, in the record table of a flow that renders with it, before
anything is delivered. Picking a row shows its document. **Dry run** takes the rows through everything a delivery does short
of sending them, and says what each comes to. Nothing is written: not OSDU, not the ledger, not the work location.

A record of any kind is taken the same way, whatever its route: the rows are a flow's, and a mapping used by several flows
picks which on the tab.

## The list of staged rows

The list reads the record table a page at a time, 200 rows to a page, in the order a run reads them, and renders nothing. A
row is named as the ledger would name its record, with the label the mapping gives it, the file and row it was landed from,
and how many rows each child table holds of it. The flow's scope parameters narrow it as they narrow a run.

The search asks the table: it finds a row by its key, its label or its file, ignoring case, wherever the row is in the table.
Only a page is read, and only the rows in view are drawn, so a table of millions lists as one of thousands does. A row marked
deleted is listed and says so; a row with an empty key part is listed, says so, and has no document to show.

The mapping need not pass its preflight for the list: it needs only the mapping's key and label. A mapping that cannot be read
at all leaves the rows named by their key, and says why.

## What a dry run does

It runs on a node, over the rows the line names (10,000 by default, or the whole scope). For each row, in the order a run
reads them:

1. **Plan it.** The planner a run plans with decides what the next run would do with the record, against the ledger as it
   stands, and renders what it would send, through the mapping and every enrichment. A row the plan has no need to render (one
   it would skip as unchanged, or one the ledger keeps blocked) is rendered as a first delivery would render it, so every row
   has its document, and no row renders twice.
2. **Hold the document to the template** the mapping pins, as the [check before sending](preflight.md#validation-before-a-record-is-sent)
   holds it, leaving out what the route fills when it sends the record.
3. **Hold it to the partition's Schema service** ([below](#the-schema-service)).
4. **Look up what it refers to**, once a batch: the ledger, then the cache the flow renders with, then OSDU's storage service for
   what neither holds. A reference to a record this run would deliver is no reference to nothing.
5. **Ask whether its id is taken**, when the id is made from the key and no ledger claims it: OSDU's answer is what an intake
   holds such a record for ([occupied ids](submissions.md)).

Once for the run, it also asks the legal service about the tags the mapping puts on every record.

The same read feeds the [value check](../cli/values.md) of the same rows, so **Properties** says attribute by attribute what
**Staged records** says record by record, and a dry run is one button on both.

## What each row comes to

| Outcome | Means |
| --- | --- |
| **Ready** | A delivery would send the record, and nothing was found wrong with it. |
| **Worth a look** | A delivery would send it, but it breaks a rule of the template or the Schema service's schema, fails an assertion of the mapping, or refers to a record nothing holds. |
| **Held** | A delivery would not send it: a part of its key is empty, the source holds it, the render holds it, the check before sending would hold it under the flow's `target.validation` or an assertion whose `onFail` is `hold`, a reference is missing under `target.verifyReferences: storage`, or OSDU already holds a record at its id. |
| **Passed over** | A delivery never renders the row: the ingestion table marks it deleted, or the source no longer holds its key. |

Whatever stops a delivery is said first, in the order a delivery would meet it. Each row also says what the next run would do
with it: create, update, leave unchanged or hold.

## The Schema service

The Schema service validates no record. It stores and serves schemas: its routes create, read, list and update them
(`osdu/specs/core/schema_service/openapi.yaml`). So the kind's schema is read from it, with every schema it refers to bundled in
([the explorer](explorer.md#validate) reads it the same way), and the document is held to it here, by the check a delivery
makes. A verdict is the one that check would reach against that schema, as the route would send the document.

What the service says of the kind is set against the template the mapping pins, property by property:

| State | Means | The documents |
| --- | --- | --- |
| Matches the template | The two say the same, titles and descriptions aside. | Held to the service's schema. |
| Differs from the template | A property is in one and not the other, or they say different things of it. Each is named, the ones that can make the service's schema refuse a document the template accepts first. | Held to the service's schema. |
| This kind is not registered | The service holds no schema of the kind. Storage takes the record all the same, but the indexer cannot index it, so search and the explorer will not find it by its data until the kind is registered. | Not judged. |
| Could not be read | The service refused or did not answer. | Not judged, and the rest of the run goes on. |
| Not an OSDU record | A DSPDM row or an Energistics object: no OSDU schema describes it, and the template the mapping pins is what it is held to. | Not judged. |

The status and scope the service lists the kind under (`PUBLISHED`, `DEVELOPMENT` or `OBSOLETE`; `INTERNAL` or `SHARED`) are shown
beside it, and an obsolete schema is called out. A record an enrichment adds is held to the schema of its own kind.

A document not judged is said to be, never counted as passed. A record held before it is whole is not judged either: what is
wrong with it is that it is not finished.

A record's **Schema service** tab shows the verdict with how to meet each rule, and says so where the template and the
service's schema disagree about that document. A flow's Preview tab shows the same.

Schemas read are kept for five minutes per flow and kind, so going through records reads a kind's schema once; that the service
holds none of a kind is kept for 30 seconds, so a kind registered just now is seen.

## Large tables

A dry run holds one batch of 200 documents at a time, and drops a document when its row has been judged, so memory does not
grow with the rows. What it asks of OSDU it asks once a batch, and what the cache holds is not asked at all, since thousands of
records name the same reference data.

Every count is exact. What is listed is bounded: 5,000 records, those a delivery would not send first, then those worth a look,
then clean rows, each in the order they were read. Narrowing to an outcome shows what was listed of it wherever those rows
are in the table; any other row is found by the search. The 50 rules broken most often are named with how many records
break each and an example. An answer past what a node task may carry lists fewer records, those with something to say kept
longest, and says so.

## What a dry run does not do

- It sends nothing, so **a record whose parent is not in OSDU yet is held**, where a run would deliver the parent first. A reference
  to a record another flow delivers first reads as not found until it does.
- It reads the mapping as the last sync found it. An edit is seen once the repository is synced.
- It asks nothing that only a send can answer: a service's own refusal of a document, a file's upload, a DDMS's bulk checks.

## Asking for one

| Request | What it does |
| --- | --- |
| `POST /api/v1/delivery/flows/{id}/staged-rows` | A page of staged rows, read in the control plane: `values`, `search`, `after` (the page's `next`) and `limit` (1 to 500, 200 by default). |
| `POST /api/v1/delivery/flows/{id}/check-values` with `dryRun: true` | A dry run queued on a node: `values`, `maxRows`, and the mapping. It names no `targets`, since it reads every attribute. The result's `dryRun` holds what is above. |
| `POST /api/v1/delivery/flows/{id}/preview` with `schemaCheck: true` | One record's document, with `schemaCheck` holding what the service says of it. |

## See also

- [The Mappings page](gui.md#mappings): where the tab is.
- [Preflight](preflight.md): every check before anything reaches OSDU.
- [sqlflow values](../cli/values.md) and [sqlflow preview](../cli/preview.md): the same render from the command line.
- [Writing a mapping](../guides/writing-a-mapping.md): a dry run as a step of getting a mapping right.
