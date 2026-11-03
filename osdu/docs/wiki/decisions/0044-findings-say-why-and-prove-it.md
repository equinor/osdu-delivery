---
id: delivery-wiki-0044-findings-say-why-and-prove-it
title: "Decision 0044: a finding says why in parts the render noted, and proves it with queries the control plane writes"
type: decision
summary: "Why a value check's finding is explained from what the render noted rather than its sentence, and why its proof is queries written server side, run as copied."
keywords:
  - value check explanation
  - proof query
  - cache miss
  - render miss
  - what to do
  - business keys
  - run the query
updated: 2026-11-03
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Rendering/RenderMiss.cs
  - osdu/src/SqlFlow.Delivery/Rendering/RenderTrail.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueTally.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueCheckExplainer.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueProof.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheProofSql.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/gui/src/features/delivery/FindingExplanation.tsx
related:
  - delivery-wiki-0040-dry-run-of-a-mapping
referenceRefs:
  - delivery-concept-value-proof
  - delivery-concept-dry-run
  - delivery-cli-values
---
# 0044: A finding says why in parts the render noted, and proves it with queries the control plane writes

Status: accepted. Documented in [why an attribute fails](../../reference/concepts/value-proof.md).

The human's words, of a mapping's dry run: "it was hard to understand what the data was showing", "This attribute failed
because X value is missing in this cache", "Not a general dataset where we have to analyse our selves. I want a list of
business keys that have wrong values, missing values", "The user should know what to do. Also add queries that can prove the
missing values", "it could be great to have builtin proof query generator, where I can run the proof query and identify the
records myself", and "the current solution is too vague and so is the message".

## The render notes what it missed; the sentence stays

A finding was a reason as the render states it, grouped by its shape. For the commonest case, a `ref` modifier's id the
cache does not hold, the sentence named the id template, the version label and the types in one breath, and nothing a page
could pick apart. Parsing the sentence back was rejected: its wording is the ledger's and the log's, and a page reading it
would break whenever it is improved.

So each place the render gives up on a value notes a `RenderMiss` on the render's trail: what kind of miss, the cached types
or the search, the fields compared, the value as compared, and the source columns it came from. The trail already carries
what a render met (searches asked, parents read, alternatives chosen), belongs to one render, and is sliced per entry the
way holds are, so an outcome carries the misses of its own entry. The hold sentences are unchanged, and nothing a delivery
writes depends on the misses.

The `CacheUsage` list was considered as the carrier, since it already notes ids looked for and not found. It was rejected:
it is the record's dependency set, hashed into the ledger, it does not note a lookup table's misses, and adding to it would
move every record's cache set.

## Said once, on the server

What a finding means and what to do is written by `ValueCheckExplainer`, beside the check, and carried in its answer. The page,
the workbook and `sqlflow values` say the same words. Writing them in the page was rejected for that reason alone: the
workbook is what people pass around.

## The proof is statements the control plane writes

A proof request carries the finding as the check answered it, never a statement: the control plane writes every statement
from the flow and the mapping, and refuses a column the attribute does not read, so a request cannot steer a proof to other
data. Run executes the same text the page offers to copy, its values bound where the copy declares them, so what a person
runs in SSMS is what the page ran.

It is several statements, not one: the source and the cache live behind different connections (the flow's own, and the
catalog's), and cross-database queries are not available everywhere the product runs. The source steps select by the values
the check met rather than recomputing the mapping in SQL: modifiers, `$coalesce` and `$when` have no faithful SQL
translation, and a person fixing the source wants every row holding a wrong value anyway. Their counts can exceed the
finding's when another alternative took a value, which the page says.

The cache step reads the version the check rendered with and stops with an error when it is gone or pruned: an empty answer
is the proof, so it may never be an answer about records nobody can read. It shows near misses (another case, another field)
because those are what a person acts on.

## Rejected

- **A list of every failing key built by the check.** A check's answer is bounded; the records step reads the source directly,
  and the workbook already lists every occurrence.
- **Running edited SQL.** The page runs only what the control plane wrote; an editable runner would be a query console over
  the estate's source data, which is not what was asked for. The route is also kept from the MCP server's tools, beside the
  other routes that return source rows.
- **Proofs inside the check's answer.** A dry run of every attribute would carry hundreds of statements nobody reads; a proof
  is written when a finding is opened.
