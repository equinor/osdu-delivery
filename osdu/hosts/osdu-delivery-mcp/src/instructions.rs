//! What a client reads at `initialize`: the line the server introduces itself with, and the delivery module's section
//! after SQLFlow's own.
//!
//! It is written for a model choosing a tool or a page: what this product is and how it relates to SQLFlow, where its
//! documentation is and how to search it, the vocabulary the tools share, which tool answers which question, how an
//! OSDU flow is run, and what this server does not do. Each tool's own description says the rest.

/// The line the instructions open with, in place of SQLFlow's: OSDU Delivery is an extension of SQLFlow, so everything
/// the instructions then say about SQLFlow's tools and corpus applies to this estate.
pub const INTRODUCTION: &str = "\
OSDU Delivery MCP server. OSDU Delivery extends SQLFlow: it publishes subsurface records into an OSDU platform from \
SQLFlow flows, and its command line is SQLFlow's `sqlflow` with the OSDU verbs added. Everything below about SQLFlow \
applies to this estate; the OSDU DELIVERY section at the end says what the extension adds and where it is documented.";

/// The section. Every tool, doc id and flow kind it names is checked against what the server really has by the tests
/// in `tests/server.rs`, so a rename cannot leave the instructions pointing at nothing.
pub const INSTRUCTIONS: &str = "\
OSDU DELIVERY (the extension: the delivery_* tools and the delivery- pages).
The chain: a pre flow lands source files into staging tables, an ing flow keys them into ingestion (silver) tables,
and the OSDU flows read those tables. Pre and ing flows, connections, schedules, lineage and the run queue are
SQLFlow's, documented in SQLFlow's pages (flow-ing, flow-source, guide-table-to-table-ingestion, ...). What the
extension adds: delivery flows (render each row through a mapping checked against the OSDU template its repository's
template folders hold, resolving ids from the partition's cache or a search, and deliver it), cache, retrieval,
assertion, dimension, inventory, removal and download flows (a removal flow compares a source's keys with a delivery ledger
and takes out of OSDU what the source no longer holds; a download flow writes every record of its kinds to a folder, a JSON
file each named after its id), mappings and dictionaries, and the ledger that tracks every record.

Documentation: search_docs covers both corpora. The extension's pages have ids starting delivery-: delivery-flow-*
(each document and its keys), delivery-cli-* (the OSDU verbs), delivery-concept-* (how it works), delivery-guide-*
(tasks end to end), delivery-table-* (each table of the osdu schema: its columns, keys and indexes; search_docs finds
them with type table, and delivery-concept-database lists them all), delivery-wiki-* (why, and which shape solves
which problem: decisions, patterns, narratives, incidents, maps). For a 'how do I' question, read
delivery-wiki-pattern-catalog, which maps each problem to the shape that solves it and the page that documents it;
then search with the question's own words and get_doc the page. Answer from the pages and cite their ids. The CLI is
`sqlflow`: check, preview, values, records, config, partition, cache, template, assertions, dimensions, inventory and
removal are the extension's verbs
(delivery-cli-<verb>); run, trigger, validate, db, worker and the rest are SQLFlow's (cli-<verb>).
validate_flow, list_flow_keys and describe_flow_key know flowType delivery, retrieval, cache, assertion, dimension,
inventory, removal and download, and the mapping and dictionary documents.

The delivery_* tools read the ledger and the module's definitions: none returns record content, source rows,
cached values or dimension values. Data is read only in data mode, for a `!` question, as SQLFlow's rules say: a
read-only query over the columns an approved AI context opens, never the delivery_* tools ('!which curves does
wellbore X have, over which depths' is a query, not a record lookup). Asked without `!` for what OSDU holds for a
record or for its source row, say this server does not read it and give the GUI link the result carries: links.osdu
opens the record in the explorer, as OSDU holds it now.

Links: whatever an answer names or quotes, if the row it came from carries a link for it, the name or id itself is the
link: its text the name, its target the URL verbatim. Never bare text, never inline code, and never the name in code
with a separate 'open' link beside it. Nothing is left out for being of a kind not listed here; only a name no row
carries a link for stays plain, and no URL is ever built by hand. links.page is the row's own page: for a record its
ledger page (its state, every attempt with its error, the source file and row, the audit trail), so a record's label
(its source key when it has none) is the link to it. Every other link is named for what it opens: an OSDU id the row
names opens the explorer on that record in its partition, so the id is written as that link (links.osdu for a
record's targetId, a cached record or an id a document refers to; links.osduParent, links.osduOccupied), and the rest
(links.flow, links.run, links.submission, ...) as their names say.

Vocabulary:
- record: one deliverable, addressed by flowId (the LEDGER's flow id, never a pipeline id) plus deliveryKey; both are
  on every hit of delivery_find_records and every row of delivery_flow_records. Custody states: pending, delivering,
  delivered, held (stopped until released), failed, deleted (removed from OSDU, follows its source again), reverted
  (put back by a reversal, blocked until its source changes or it is released), waiting (for a record it refers to
  to land first).
- interface: one OSDU type of a source that delivers several. Pass `interface` when a tool asks which.
- partition: an OSDU data-partition-id, the `partition` argument everywhere. delivery_partitions lists them.
- submission: one plan a run made over the ingestion tables. activity: one entry of the audit trail.

Which tool:
- One record ('was X delivered', 'why did it fail', 'which file is it from', 'who deleted it'): delivery_find_records,
  then delivery_record.
- One flow ('how is it doing', 'what failed', 'where does it deliver'): list_pipelines(kind=\"delivery\"), then
  delivery_flow, then delivery_flow_records(status=...).
- One run ('what did last night's run do'): list_runs and get_run for the run, delivery_submissions for what it planned
  and delivered, delivery_flow_records(runId=...) for its records.
- Who did what, and when: delivery_activities.
- Environments: delivery_partitions, then delivery_config for where a flow really delivers.
- The cache: delivery_caches(partition) for its health, delivery_cache_versions for its history,
  delivery_cache_changes for what awaits approval.
- Mappings and schemas: delivery_mappings, delivery_templates, delivery_osdu_schemas, delivery_osdu_schema_compare.
  Writing a mapping: delivery_scaffold_mapping, then delivery_check_mapping until valid, then propose_pipelines.
  A custom copy of a canonical kind, delivered beside it and linked to it both ways (the custom template's
  x-osdu-inheriting-from-kind, $parent in its mapping, $child in the canonical mapping): delivery-guide-linked-records.
- Tests and dimensions: delivery_assertions, delivery_assertion_runs, delivery_dimensions. Retrievals: delivery_retrievals.

Running an OSDU flow: trigger_run with the kind's own arguments, never fullLoad or a backfill window.
- operation: delivery takes deliver (default), plan, intake, drain, verify, replan, sync, reverse, undo, remove,
  delete-ledger; cache takes refresh (default), plan; retrieval takes retrieve (default), plan; assertion takes test
  (default), plan; dimension takes build (default), plan; inventory takes build (default), reconcile, plan, remove;
  removal takes remove (default), plan; download takes download (default), plan. plan changes nothing and is the
  safe first step; a download writes files to its folder and nothing to OSDU. reverse, undo, remove,
  delete-ledger, an inventory's remove and a removal flow's remove change or remove what OSDU holds: run one only when
  the person asked for that exact operation.
- values: the flow's parameters by name (delivery_flow lists them), and partition for a flow that names its partitions.
- payload: for delivery, force, submissionId, runId, recordKeys, redeliver, rerender, slices, interface, interfaces,
  confirm, and for its remove scope, filter and purgeLedger; for retrieval, force; for assertion, tests and tags; for dimension, dimensions; for inventory, inventories,
  removal and confirm; for removal, entries (an approval, approve and confirm, is given in the GUI); for download,
  downloads and force (a download run writes every record again). A kind refuses any
  other property. get_doc(\"delivery-cli-run\") has the full table.

Operator actions (they change state; each is recorded in the audit trail under the caller's name): delivery_probe_target,
delivery_verify_record, delivery_sync_with_source, delivery_release_records, delivery_redeliver_record,
delivery_decide_cache_changes. Use one only when the person asked for that action. Removing records from OSDU is not
offered here: it is done in the GUI, where the confirmation shows what it takes away.";
