---
id: delivery-concept-value-proof
title: "Why an attribute fails a value check: which records hold wrong values, what to do, and queries that prove it"
type: concept
summary: "What a check's finding says in plain words (the type, field and source column behind it), what to do, and read-only queries that prove it."
keywords:
  - why does an attribute fail
  - missing in the cache
  - id not held
  - what to do about a finding
  - proof query
  - prove a finding
  - business keys with wrong values
  - which records hold a wrong value
  - records to fix
  - run the query
  - copy into SSMS
related:
  - delivery-concept-dry-run
  - delivery-concept-gui-build
  - delivery-cli-values
  - delivery-concept-partition-cache
  - delivery-concept-api
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Rendering/RenderMiss.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueCheckExplainer.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueProof.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/ValueProofOperation.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheProofSql.cs
  - osdu/gui/src/features/delivery/FindingExplanation.tsx
---

# Why an attribute fails a value check: which records hold wrong values, what to do, and queries that prove it

A [value check](../cli/values.md) or a [dry run](dry-run.md) groups the rows an attribute fails in into findings. Each finding
says what went wrong as a person acts on it: what was looked for, where, with which value, read from which source column; what
that does to the records; and what to do. Its **proof** is a set of read-only queries that show the same thing from the data
itself: where the source holds the values, the records to fix by their keys, and what the cache holds of what was looked for.

## What a finding says

The render notes what each node looked for and did not find as it renders, so a finding is never read back from a sentence:

| Cause | Means | Headline, for example |
| --- | --- | --- |
| `empty` | The source column the attribute reads is empty in these rows. | `dataset.log_run` is empty |
| `idNotHeld` | The id the mapping wrote or built (a `ref` or `id` modifier, or a value that is already an id) names no record of its entity type in the cache version. | The cache holds no `reference-data--UnitOfMeasure` record `ft` |
| `noMatch` | A `$cache` lookup (or `$findAll`) found no record holding the value at the fields its `$findBy` compares. | No `UnitOfMeasure` in the cache has `Code` or `Name` equal to `FURLONG` |
| `ambiguous` | Several records answer to the value once case (or punctuation) is ignored, so none is taken. | `ft` matches 2 `UnitOfMeasure` records once case is ignored |
| `noType` | The cache version holds no type of the name the mapping reads. | The cache holds no type `UnitAlias` |
| `fieldEmpty` | The cached record found holds nothing at the field the mapping reads: the cache's data is what is incomplete. | The `UnitOfMeasure` record `...:ft` holds nothing at `Symbol` |
| `notFound` | A `$search` of the platform found no record holding the value. | No `master-data--Wellbore` on the platform has `data.FacilityName` equal to `NOWHERE 1` |
| `rule` | The value written breaks a rule of the template. | The value breaks the template's `pattern` rule |
| `assertion` | The value fails an assertion of the mapping. | The value fails the mapping's assertion `known-unit` |
| `other` | Anything else the render holds or leaves a value out for; the reason as rendered says what. | the reason itself |

Beside the headline a finding says:

- **What it does**: held (the records are not delivered), left out (delivered without the attribute), invalid (delivered with
  a value the template does not accept), or what a failed assertion's `onFail` does.
- **Where** it was looked for: the cache version and partition, or the platform's search; and, when they differ, what the
  source holds against what the mapping made of it (`FT` in `dataset.curves.curve_unit`, looked up as `ft`).
- **What to do**, the likeliest first. For a value the cache does not hold: correct the source column if the value is wrong;
  translate it in the mapping (a `replace` modifier, or a row of the lookup table it replaces through) if OSDU knows it under
  another code; load the record into the partition and refresh the cache flow that captures the type if OSDU lacks it.
- **The source values**: what the columns behind it hold in its rows, before the mapping's modifiers, with how many rows each.
- **The reason as rendered**: the render's own sentence, which the ledger and the run's log use.

A `$coalesce` none of whose alternatives gave a value has a cause for each alternative that looked for something; the
headline says none of them found a value and gives the first one's.

## The proof

Each step is one statement the control plane writes from the flow and the mapping; the request names the finding, never a
statement, and a step reads only the columns the attribute reads. **Run** lists a step's first rows on the page (200 by
default, at most 1,000), and the answer says when there are more. **Copy** gives the same statement with its values declared
at the top, for SSMS: the text copied is the text run, its `@rows` set to 100,000.

| Step | Runs on | Shows | How to read it |
| --- | --- | --- | --- |
| Where the source holds these values | The flow's source tables | Each value the check met in the columns behind the finding, with how many rows of the scope hold it. For an `empty` cause, how many rows leave the columns empty. | These are the rows to fix. A count above the finding's is rows the mapping took another way (another `$coalesce` alternative). |
| The records to fix | The flow's source tables | Each such row by its record key, with what it holds in those columns and the file and row it was landed from (and its child row's, for a repeated item). | The business keys to correct in the source. |
| What the cache holds of these ids, or values | The catalog database, in the cache version the check read | For each id or value looked for: how many records of the type hold it exactly at the fields compared, how many once case is ignored, and the first records holding it in any field or with a similar id. | 0 exactly proves the cache has none. A record ignoring case, or at another field, is what the mapping should look up instead. |
| The records the value matches | The catalog database | For `ambiguous` and `fieldEmpty`: the records named, with every field the cache holds of each. | Which one the source should name, or which field is missing. |
| The types the cache holds | The catalog database | For `noType`: every type of the version, with its records. | The type the mapping reads is not among them. |
| The search the render asked | The platform's search service | For `notFound`: the request the render sent for the first value, as the flow's identity sends it. Copy only. | POST it to the search API; an empty answer proves the platform holds no such record the flow may read. |

The source steps read the rows a delivery reads: the scope's, those marked deleted left out, under the flow's record and
dataset filters, a value compared as its column's own type. The cache step reads the version the check rendered with; a
version that is gone, or whose records the cache's [retention](partition-cache.md#retention) pruned, stops it with an error, so
an empty answer is never one about records that cannot be read.

Values a [line of several comparisons](../flow/mapping-lookups-composite.md) looked up together are named together
(`Z-9 / NPHI`), and the headline says no record holds them together. Such a finding has no cache step: a count of the
records holding each value would not show that none holds them all, and the proof says so in its notes.

A request carries as many of the finding's values as fit a task's argument, the most frequent first, and the page says when
it carries fewer than the check listed. A check lists up to 25 values a finding; a check of fewer rows lists the rest.

## Where it is shown

- **The Mappings page**: an attribute's **Data** tab leads each finding with its headline; opened (the first opens by itself)
  it shows what it does, where it was looked for, what to do, the records, and **Prove it from the data**, with Run, SQL and
  Copy on each step ([the Mappings page](gui-build.md#mappings)).
- **The issues workbook**: the Issues sheet's **What is wrong** column is each row's headline, and **Reason as rendered** the
  render's sentence; the Summary sheet adds **What to do**.
- **`sqlflow values`** prints each finding's headline, then `effect`, `where`, `reason`, `values`, `source` and the numbered
  `to fix` lines.

## Asking for one

| Request | What it does |
| --- | --- |
| `POST /api/v1/delivery/flows/{id}/value-proof` | Body `target`, `values`, `cause` (the finding's first cause, as the check answered it), `sourceValues`, `looked` (the finding's values), `cacheVersion`, `mapping`, `run` (`values`, `records` or `cache`; none writes the steps and runs nothing) and `limit`. Answers `steps`, each with `step`, `title`, `proves`, `runsOn`, `where`, `language`, `statement`, `runnable` and, for the step run, `result` (`columns`, `rows`, `more`), and `notes`. Read in the control plane with the flow's own connection reference; nothing is written. |

## See also

- [A dry run](dry-run.md): every attribute of a mapping's staged rows at once.
- [sqlflow values](../cli/values.md): the same check from the command line.
- [The partition cache](partition-cache.md): what a cache version holds and how it is refreshed.
