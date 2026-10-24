# OSDU Delivery wiki

The catalog of every page in this wiki. Read this first when answering a question against the
wiki, then drill into the pages it points at.

This wiki is written and maintained by an LLM agent, not by hand. Its conventions, and the
ingest / query / lint workflows that keep it current, live in the "OSDU Wiki" section of
[CLAUDE.md](../../../CLAUDE.md), which follows SQLFlow's internals wiki
([sqlflow/docs/wiki/](../../../sqlflow/docs/wiki/index.md)). See [log.md](log.md) for the chronological
record of what has been ingested and when.

**In a hurry?** [Pattern catalog](maps/pattern-catalog.md) maps a delivery problem onto the
OSDU Delivery shape that solves it and the page that documents it.

## What belongs here, and what does not

This wiki explains **why OSDU Delivery is the way it is, how its pieces fit together, and which shape
solves which delivery problem**. It is the synthesis layer over its raw sources: the code under `osdu/`
and the vendored SQLFlow under `sqlflow/` it builds on, the git history, the sample estate under
`osdu/samples/`, and the production estate's metadata repository, which the pages describe generically.

It is not the reference manual. [osdu/docs/reference/](../reference/README.md) documents **what** every
OSDU verb, document key, and concept does, derived from and verified against the code. A wiki page never
restates what a reference page already says. It links to it and adds what the reference cannot carry:
rationale, rejected alternatives, cross-cutting narrative, problem-to-shape mapping, and history. What is
SQLFlow's own (landing files, ingestion, schedules, lineage, secrets) is explained by SQLFlow's wiki, and a
page here links it rather than repeating it.

Both trees are indexed into one [manifest.json](../reference/manifest.json) by SQLFlow's
[build_manifest.py](../../../sqlflow/docs/reference/build_manifest.py) and embedded into the MCP server
at compile time by [osdu/hosts/osdu-delivery-mcp/build.rs](../../hosts/osdu-delivery-mcp/build.rs), so every
page here is reachable through `search_docs`, `get_doc`, `related_docs`, and `list_docs`, beside SQLFlow's
own. Filter by the `type` values below to narrow a search to one kind of page.

| Directory | `type` | Answers |
| --- | --- | --- |
| `narratives/` | `narrative` | How do I actually do X, end to end? (code-first recipes) |
| `patterns/` | `pattern` | Which shape solves this delivery problem? |
| `decisions/` | `decision` | Why is it this way, and what was rejected? |
| `incidents/` | `incident` | What broke, and what generalizes from it? |
| `maps/` | `map` | Where does knowledge live, and which parts are stale? |

## Pages

### Maps

- [Pattern catalog](maps/pattern-catalog.md) - problem-first index: where a lookup belongs, one kind or
  interfaces, which route, retrieval or explorer, assertions, orphans, and the page for each. **Start here.**

### Patterns

- [A custom record beside its canonical record, linked both ways](patterns/linked-canonical-and-custom-records.md) -
  when OSDU's schema lacks what a source knows: a custom copy of the kind linked to it, both records delivered from the
  same rows, the custom one reading its parent with `$parent` and the canonical one linking it back with `$child`, and
  the estate folder that runs it.

### Decisions

The design left these decisions open, and the implementation had to choose for each of them to ship working
code. Each record carries its status so the owning teams can confirm or overturn it, and the code isolates each
choice so overturning it is a contained change. Every record is proposed; 0002 also needs confirmation that the
data partition accepts client-supplied ids for the kinds in scope. Not decided here, and still open: storage
access grants, Change Data Feed, library ownership between osdu-client and osdu-csharp-client, and whether the
other packages adopt the same contract.

- [0001: the delivery grain, one record per source log](decisions/0001-delivery-grain.md) - why a well log
  record is keyed by its source's own identity (project and log id) and not collapsed per wellbore.
- [0002: deterministic, client-supplied OSDU ids](decisions/0002-client-supplied-ids.md) - why OSDU ids are
  computed from the data by the delivery instead of assigned by OSDU, and what that guarantees.
- [0003: rendering runs in the delivery service](decisions/0003-rendering-location.md) - why the mapping is
  interpreted by the delivery against a pinned template and cache version, not by a facade service.
- [0004: Datasets, DDMSDatasets and ExtensionProperties belong to OSDU](decisions/0004-preserved-keys.md) - why
  an update keeps the properties a DDMS or External Data Services writes into a record, and leaves them out of
  the hash.
- [0005: attempts are pruned by age, keeping the latest per record](decisions/0005-ledger-retention.md) - why
  the ledger's attempt history is pruned by age while every record keeps its latest attempt.
- [0006: rendered documents live in work batch files, not in the ledger](decisions/0006-work-batches.md) - why
  rendered documents are written to work batch files the ledger points at, and how large submissions fan out.
- [0007: manifest datasets take ids derived from their record's id](decisions/0007-manifest-dataset-ids.md) -
  why the datasets a manifest delivery registers get ids derived from the record, so a redelivery can replace
  them.
- [0008: retrieval lands records as OSDU holds them](decisions/0008-retrieval-lands-raw-records.md) - why a
  retrieval flow brings OSDU records back unchanged instead of reversing a mapping.
- [0009: references to business data are searched for, not cached](decisions/0009-searched-references.md) - why
  wellbores and other business data are found by a search per record while closed vocabularies stay in the cache.
- [0010: assertion flows read OSDU, check against templates, and keep whole reports](decisions/0010-assertion-flows-read-only.md) -
  why data quality tests read what OSDU serves, never write, and keep every report whole.
- [0011: dimension flows read every distinct value by ranges and keep each key](decisions/0011-dimension-flows.md) -
  why dimensions page through every distinct value of a field by ranges instead of the search aggregation.
- [0012: every delivery completes or is undone, and the ledger names every id it minted](decisions/0012-atomic-delivery.md) -
  why a delivery of many OSDU calls is a unit of work whose minted ids the ledger keeps, so an unfinished one can
  be undone.
- [0013: inventory flows compare what OSDU serves with every ledger of the partition](decisions/0013-inventory-flows.md) -
  why finding orphans needs a read of what OSDU serves set against every ledger, and what an inventory compares.
- [0014: an inventory flow may remove the orphan, stale and forgotten ids it found](decisions/0014-inventory-removals.md) -
  why an inventory flow may remove what it found when its document allows it, and how that stays traceable.
- [0015: a record OSDU lost is delivered again under its own id](decisions/0015-lost-records.md) - why it is
  delivered again under the id it claimed, restored first when soft-deleted, and given a new id only when an
  operator asks.
- [0016: OSDU's relationships order a repository's waves](decisions/0016-osdu-order.md) - why lineage orders
  delivery flows by the relationships OSDU's schemas declare, and why schedules that depend on each other are
  shown rather than chained.
- [0017: a pipeline names the cache it needs current before it runs and the cache it updates after](decisions/0017-pipelines-update-the-cache.md) -
  why the cache steps belong to the pipeline, why before is mandatory and after is not, why after reads storage,
  and how held records wait for what they miss.
- [0018: a repository's template folders hold the templates, and every reader reads the file](decisions/0018-template-folders.md) -
  why templates live only as files in a repository's template folders, how a document's template is settled, and why
  the database keeps an index of them and no copy.
- [0019: a custom template is linked to its canonical kind, and its records read their parent from OSDU](decisions/0019-linked-templates.md) -
  why the link is the template's, why a record's parent is the same minted id in the canonical namespace, why it is read
  from storage as the record renders, and what a missing or changed parent does.
- [0020: the explorer reads a partition through the partition's own connection, never a flow's](decisions/0020-explorer-partition-connection.md) -
  why the explorer signs in with the platform and client a partition's configuration names, what a record page's check
  still reads through, and why the explorer has no connection setting of its own.
- [0021: records whose business keys OSDU already holds are adopted in a batch, and the next run takes them over](decisions/0021-adopting-occupied-records.md) -
  why a row whose id OSDU already holds is held until adopted, why an adoption is asked of every such record at once and
  the next run claims each id at the version OSDU holds then, why claiming at the click was rejected, and how two
  instances linked to one record share it without calling each other's writes drift.
- [0022: a removal flow reads the source's keys whole and keeps its deletions in the delivery ledger](decisions/0022-removal-flows.md) -
  why records a source dropped are found by a flow kind of its own reading the source rather than by `matchKeys` or the
  ingestion table, why its deletions are the delivery ledger's, taken out once each, and why a guard holds a run that
  finds too many.
- [0023: a canonical record links its children as it renders, once OSDU holds them, and a child landing plans it again](decisions/0023-child-links.md) -
  why the link back to a custom record is a `$child` node the canonical mapping writes in an open property, why the child
  is looked up rather than assumed, why it never holds its parent, and how the ledger finds the records a landing child
  links.
- [0024: a scope parameter takes several values in one run, the set is its scope, and SQLFlow carries the list](decisions/0024-scope-value-lists.md) -
  why a run value may be a list SQLFlow carries as JSON array text, why only a scope parameter takes one, why the set is
  one scope with one watermark, why a removal compares a record whose values all lie in its own, and why `--set`
  repeated gives a list.
- [0025: what runs at once is a flow's parallelism, and a delivery fans out by default](decisions/0025-parallelism-and-default-fan-out.md) -
  why the settings that say how much runs at once left `reliability`, why a large delivery fans out by default and is
  bounded by its work batches, how a coordinating run stops members no node starts and does their part, why a rate limit
  holds for the fan-out as a whole, and why a share planned again writes its batches after the ones it wrote.
- [0026: the osdu schema lives in the catalog's database, reached through its one connection](decisions/0026-one-database-connection.md) -
  why the module has no database connection of its own, why a node reads the ledger through `SQLFLOW_CATALOG_DB`, why
  a host ignores the retired `SQLFLOW_OSDU_DB` rather than refusing it, and why the control plane owns the `osdu` schema.
- [0027: an inventory finds the records OSDU holds more than once](decisions/0027-inventory-duplicate-checks.md) -
  why duplicates are found on an inventory by an operator's key rather than in the explorer or by an assertion, why the
  copy holding the most values is kept and a ledger's copy is never picked to remove, why the DDMS is not read, and how a
  removal of the copies picked never takes the last copy of a record.

## Coverage

Instantiated 2026-10-21 with the structure, the lint, the seventeen decision records (moved here from
`osdu/docs/decisions/`) and the pattern catalog (moved here from the reference guides). Patterns, narratives
and incidents come from ingesting the sample estate, the production estate and the git history, one source at
a time. Absence of a page means the topic has not been ingested, never that it is out of scope.
