---
id: delivery-wiki-0010-assertion-flows-read-only
title: "Decision 0010: assertion flows read OSDU, check against templates, and keep whole reports"
type: decision
summary: "Why data quality tests read what OSDU serves, never write, and keep every report whole."
keywords:
  - "assertion"
  - "data quality"
  - "read only"
  - "test report"
  - "template check"
referenceRefs:
  - delivery-flow-assertion
  - delivery-guide-data-quality-tests
related:
  - delivery-wiki-0018-template-folders
updated: 2026-10-21
---
# 0010: Assertion flows read OSDU, check themselves against templates, and keep whole reports

Status: proposed. Documented in [assertion flow](../../reference/flow/assertion.md).

> **Amended by [0018](0018-template-folders.md).** A test is checked against the template the template folders on the
> flow's path hold, not the saved template of its kind.

## Context

Once data has landed, the question is what the partition holds: whether search finds the records, whether their values
are the ones meant, whether their references resolve, whether their bulk data is in its DDMS. A delivery run cannot
answer it, because it knows what it sent, not what a user sees. The answer has to be asked the same way every time,
kept, and read by people who did not write the mappings.

## Decision

- A test is of one type in one version, the one a mapping delivers (its `template.kind`), pinned to the schema that
  mapping pins; a kind with wildcards is refused. The tests of each mapping's type are a flow of their own, so each
  flow's lineage reads the type node its delivery writes.
- An assertion flow (`flowType: assertion`) is a flow kind of its own, holding tests of one kind each and assertions
  about what a test reads. It reads search, storage, legal and the Wellbore DDMS, and the delivery ledger; it never
  writes to OSDU, and it is not a delivery flow option, so a check cannot change what a delivery does.
- A test that reads fields is checked against the saved template of its kind before anything is read. A path that is
  not a variable of the schema, or an operator or operand that does not suit the variable, keeps the test from being
  evaluated and says why, rather than failing every record.
- A run tests one partition and keeps its report in the module's database, one row per run and one per test with the
  whole result, written as each test finishes. Boards and totals are read from those rows, never counted separately.
- The retention pass removes a run only whole, and only once a later result of each of its tests superseded it, so a
  report that is kept is the report as it ran, and every test's latest result stays.
- The report renders from one piece of code in JSON, Markdown, HTML and JUnit XML, for the GUI, the CLI and a CI job.

## Consequences

- A test pays for what it reads: counts and groups come from the index, and only assertions that need records page
  through them, bounded by `maxRecords`, with `sample` for a partition too large to read whole.
- Tests are versioned with the flows they check, in the same repository, and the board marks a result that answers an
  earlier definition of its test.
- A test in the same wave as a delivery would read an index that has not caught up; lineage orders the assertion flow
  after the flows writing the kinds it reads, which is how a schedule firing both avoids it.
