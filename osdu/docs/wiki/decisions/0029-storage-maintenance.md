---
id: delivery-wiki-0029-storage-maintenance
title: "Decision 0029: one Maintenance page accounts for the whole database, each part kept, safe to remove or optional"
type: decision
summary: "Why the osdu schema is described on SQLFlow's Maintenance page, why each area has one of three verdicts, and why nothing is removed until an admin sets a limit."
keywords:
  - "maintenance"
  - "storage areas"
  - "retention"
  - "safe to remove"
  - "optional"
  - "keep"
  - "attempt prune"
  - "statistics"
sourceRefs:
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/StorageArea.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/MaintenanceRunner.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/StorageReport.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/MaintenanceEndpoints.cs
  - sqlflow/src/SqlFlow.Catalog/MaintenanceStore.cs
  - sqlflow/src/SqlFlow.Catalog/RunTraceStore.cs
  - sqlflow/gui/src/features/maintenance/MaintenancePage.tsx
  - sqlflow/gui/src/features/maintenance/StorageAreaSheet.tsx
  - sqlflow/gui/src/features/maintenance/CleanupSheet.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/Maintenance/OsduStorageContributor.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Assertions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Reversals.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/TestResult.cs
  - osdu/tests/SqlFlow.Delivery.ControlPlane.Tests/OsduStorageAreasTests.cs
referenceRefs:
  - delivery-concept-storage-and-retention
  - delivery-concept-availability-and-retention
  - concept-maintenance
related:
  - delivery-wiki-0005-ledger-retention
updated: 2026-10-25
---
# 0029: One Maintenance page accounts for the whole database, each part kept, safe to remove or optional

Status: accepted. Documented in [What the osdu schema stores](../../reference/concepts/storage-and-retention.md) and,
for the page and its contract, in SQLFlow's [Maintenance](../../../../sqlflow/docs/reference/concepts/maintenance.md).
It changes how [0005](0005-ledger-retention.md) is carried out, not what it keeps.

## Context

The Maintenance page showed SQLFlow's SQL statements and run events and nothing else. Measured on a development estate,
those two tables held about 18 MB of a database of about 6 GB; the dimensions held over 5 GB and the partition cache
almost 0.5 GB, and none of the `osdu` schema's 65 tables appeared at all. The ledger's own retention pass (0005) existed
only as an admin route that the estate's own scheduler had to call with a token; nothing in the product called it. Any
signed-in user could change the statements' retention and purge every statement and event. The page said the run events
fed a table's inflow history, which the run rows do, and the retention reference called the cache's tables never pruned
while the cache's own reference described their pruning.

Asked what is safe to remove, what has to stay and what may optionally go, an operator had no page that said it, and the
owner pointed out that statistics stay useful for longer than any 90-day window.

## Decision

- **One page for the whole database.** SQLFlow's Maintenance page describes every table of the catalog's database in
  storage areas through one contract (`IStorageContributor`): SQLFlow describes its catalog and the module describes the
  `osdu` schema (`OsduStorageContributor`), so one page, one report, one retention store, one cleanup runner and one
  automatic pass serve both. A table no area describes is shown as such, and both models are held by a test to having
  every table in exactly one area.
- **Three verdicts, in the words of the question.** *Keep* (needed: it explains a run, a record or an action, holds state,
  or holds statistics), *safe to remove* (nothing anyone needs is lost) and *optional* (removable, at a cost the area
  states). The verdict is about the data, not its size; the largest areas (dimensions, the cache) are kept content, and
  the page says how they do shrink.
- **Nothing is removed by default.** An area that may be cleaned up keeps everything until an admin sets a limit or
  cleans it up; nothing ever sets one for them.
- **The ledger's retention pass becomes three optional areas** with the same predicates 0005 set: superseded attempts
  (each record's latest kept), finished activities' logs (every row kept), and superseded assertion runs (each test's
  latest result kept). Each counts with the same query it deletes with, so a preview is exactly what a cleanup takes.
- **The run trace takes the same keep rule for both its tables**: a pipeline's latest run and every failed run keep
  their statements and events. Statements are safe to remove; events are optional, since Insights' step timings read them.
- **Writes are an admin's**, and every cleanup is logged with who asked, at which cut-off, and how many rows went.
  Anyone signed in sees what a cleanup would remove.

## Amended: the safe purge is a panel of its own

The first page put an area's cleanup at the foot of the area's own sheet, behind a click on its row. The owner, signed in
without the admin role, found no way to purge at all, and a Preview that answered "nothing from before that date is left
to remove" read as a button that did nothing. Cleaning up is now **Clean up**, in the page's header and on every row and
sheet of an area a cleanup can take: one panel listing every such area, what is safe ticked and what is optional not,
each optional one with its cost on its face, one age for all, and each area's count shown as soon as the age is chosen,
with nothing to press. It starts at 30 days because only what is safe starts ticked, and removing that loses nothing at
any age; an optional area is never ticked unless a person ticks it. The cleanup stays an admin's, as the owner decided:
without the role the panel shows the counts and says who can run it, rather than hiding the action. The same check found
that the log's actor was empty for a signed-in account, since the endpoints read the identity's name where the platform's
`RequestActor` reads the token's subject; it now records who asked.

## Rejected

- **A Maintenance page of the module's own.** Two pages would split one question in two and duplicate the runner, the
  store and the pass; the contract keeps OSDU code out of SQLFlow and gives any module the same page.
- **Turning the optional cleanups on at 90 days.** It would remove ledger history on upgrade without anyone choosing it,
  and statistics and trends read further back than 90 days; the default stays off and the choice is the admin's.
- **Keeping "Purge all".** Deleting every statement and event took the latest and failed runs' SQL too, leaving a failure
  unexplained; a cleanup at 0 days frees the same space and keeps what explains the runs that matter.
- **Clearing assertion results' detail instead of removing superseded runs.** A report rebuilds each test from that
  detail and leaves out a result it cannot read, so cleared detail would empty old reports without a new column and
  migration to tell them apart; removing a superseded run whole, as 0005 did, keeps every report that remains whole.
- **Adding a limit to every history that grows** (dimension builds, inventory runs, removal runs, retrievals, chat,
  compute tasks). Each is history someone reads, and a limit for each is its own decision; the page flags their growth so
  it is seen until one is made.
- **Each contributor storing its own limits.** One store keyed by area id, written by SQLFlow's endpoints, keeps the pass
  and the log in one place; a module's code never writes SQLFlow's tables.

## Consequences

- A table added to either model without an area fails a test naming it, so the page cannot quietly miss one.
- An area's id is stored with its limit and cleanups, so it never changes once shipped.
- The automatic pass runs on every replica; its deletes are bounded and idempotent, and a cleanup stopped with its host is
  closed as interrupted by that host or, after 12 hours, by any host.
- A cleanup frees space inside the database for new rows; it does not shrink the files, which stays a database
  administrator's decision.
