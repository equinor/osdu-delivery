---
id: delivery-wiki-0040-dry-run-of-a-mapping
title: "Decision 0040: a dry run is a read of a mapping's staged rows, not a module, and the Schema service is read rather than asked"
type: decision
summary: "Why a mapping's dry run extends the value check rather than plan or a new flow kind, why documents are judged here, and how it stays flat at any table size."
keywords:
  - dry run
  - staged records
  - schema service validation
  - cross match
  - dedicated module
  - plan operation
  - value check
  - one render per row
  - bounded list
  - keyset paging
updated: 2026-11-01
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Checks/DryRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/SchemaCrossCheck.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueChecker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/StagedRowsOperation.cs
  - osdu/src/SqlFlow.Delivery/Validation/ReferenceResolver.cs
  - osdu/gui/src/features/delivery/StagedRecordsTab.tsx
  - osdu/gui/src/features/delivery/StagedRecordList.tsx
  - osdu/gui/src/features/delivery/DeliveryMappingPage.tsx
  - osdu/gui/src/features/delivery/scopeDefaults.ts
related:
  - delivery-wiki-0044-findings-say-why-and-prove-it
referenceRefs:
  - delivery-concept-dry-run
  - delivery-concept-gui
  - delivery-cli-values
---
# 0040: A dry run is a read of a mapping's staged rows, not a module, and the Schema service is read rather than asked

Status: accepted. Documented in [a dry run](../../reference/concepts/dry-run.md).

The human's words: "the staging area cant eaisly be picked up and render", "improve the development cycle where everything is
done but its not deliverd", "In the mapping document page we have some attribute testing that does this, but its verycryptic
and hard to understand. There shoudl be an easy way. here are your staged records attached to this mapping document. Dry run see
the document.", "any type of records can be based for the mapping document", "cross match the generated the generated osdu
document with osdu schema service which validates it", and "the base data for the template can be thousands of records. Can
also be multiple tabels. So it should be scaled for large dataset and performance."

## On the mapping, not in a module of its own

A delivery could already be rehearsed four ways (`sqlflow check`, Preview, the value check, the `plan` operation), and none was
what was asked for: Preview takes a key, the value check reports by attribute and lists only what fails, and `plan` keeps counts
and twenty rows. What was missing was a place that starts from the rows. A mapping knows its rows: it lists the flows that
render with it, and each names its record table. So the tab is on the mapping, the page a mapping's author already has open.

A new flow kind or module would be a second path beside the planner, and a dry run is worth trusting only because it is not one.

## On the value check, not on `plan`

The first answer here said to extend `plan`, and building the page changed it. `plan` asks what the next run would change, so it
passes over an unchanged row and needs a run to start; the value check asks what a mapping makes of rows, whatever the ledger
says, is queued from the mapping's page already, and names the mapping it is asked of. A dry run is the value check with a
witness on the same read, so one read feeds both views and one button runs both. **Check all attributes** became that
button, rather than leaving two.

The witness renders through the planner and not through the value check's `Inspect`: `Inspect` writes the document before it is
normalized and runs no enrichment, so validating its document would judge something a delivery never sends.

## The service is read, and the document judged here

The human asked for the Schema service to validate the document. It cannot: `SchemaController` at commit a857439 of
community.opengroup.org/osdu/platform/system/schema-service has four routes, which create, read, list and update schemas, and the
contract in `osdu/specs` agrees. So the schema is read and the document is judged here, by `RecordValidator`, the check the gate
before sending and the explorer share. That the check is local is said on the page, in plain words, so nobody takes a verdict for
the service's own.

Judging against the template alone would miss what this was asked for: the partition's schema can have moved past the file in
the repository, and the document is judged against both so a disagreement is said where it is found. The status comes from the
listing, since a schema's body carries none, and it is asked with an explicit status and scope, since a listing that names
neither answers only what the specification defaults them to.

A service that cannot answer leaves its check unjudged rather than failing the run. A dry run is how a person finds out what is
wrong, so it never stops at the first thing that is, and a document not judged is never counted as passed.

## Flat at any size

The list is a keyset page of the record table and a count, searched in the database, with the child tables counted for the page
alone in one statement: reading rows through the run's read would have read every child row of every row listed. The GUI draws
only the lines in view.

A dry run holds a batch at a time. Each row renders once in the planner, the ledger's pass rendering what it would send and a
second pass only what it skipped. The reference lookup was the surprise: asked as the gate asks under
`verifyReferences: storage`, storage is the authority for every id, so forty ids of reference data per record went to OSDU though
the cache held them. A dry run asks the cache first (`cacheFirst`), and the gate's meaning is unchanged.

Listing every row of a large run was rejected. A table to hold them would need a migration, a retention area and a cleanup for
something a person looks at once, when a document can be rendered again on a click, and a run's answer has to fit a node task's
result. So what is listed is capped with what stops a delivery first, every count stays exact, and any other row is a search
away.

## Rejected

- **A `--render` option on a run.** Forgetting it delivers to OSDU; a button of its own cannot.
- **Storing the rendered documents.** A second store of what the ledger's work batches already hold for real runs, for documents
  that go stale when the mapping moves.
- **A dry run CLI verb for now.** `sqlflow values` and `sqlflow preview` render the same rows, and the ask was the page.

## The page it came to live on (2026-11-01)

The first version sat in a sheet, with the render, the YAML and the record shape as tabs beside Properties, and it opened on a
refusal: the list asked for rows before the control plane had said the scope's values would do, so a required parameter left
empty came back as an error that stayed on the page. The human's words: "Mapping document selection should be a selection with a
search box, once selected the next page should not be a slide in but a full page", "We should default back to auto select first
option", "render and the source yaml should be sub tabpages of the same section", and "What we are missing is logical coherance
in the interface".

So a mapping is a page with an address, and the page is laid out in the order the work goes: which mapping, which rows, what
came of them. The toolbar is one, over both views, because the rows a dry run reads are the same whichever view reads them; before,
each view drew its own controls. The render, the YAML and the shape are pages of one section beside the records, because all
three answer "what does this mapping make?" and Properties, which needs the width of the window, answers another question.

A required parameter starts on its first option on this page and nowhere else. A value nobody picked is harmless where the page
only looks, and would be wrong where it decides what a run writes, so the trigger form stays as it was. A read of the rows waits
for the answer on the values, which is what removes the refusal; the first option is what makes the wait end in rows.
