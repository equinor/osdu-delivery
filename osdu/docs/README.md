# OSDU Delivery documentation

OSDU Delivery extends SQLFlow, and its documentation is the reference corpus in [reference/](reference/README.md), in
SQLFlow's format: [cli/](reference/cli/) (the `sqlflow` verbs the extension adds and what it adds to SQLFlow's),
[flow/](reference/flow/) (the flow kinds, routes, mappings and dictionaries, key by key),
[concepts/](reference/concepts/) (the ledger, record states, change detection, partitions, the cache, the GUI and the
API) and [guides/](reference/guides/) (tasks end to end).
SQLFlow's own corpus ([../../sqlflow/docs/reference/](../../sqlflow/docs/reference/README.md)) documents the platform
underneath. Beside the corpus are the wiki ([wiki/](wiki/index.md)), in SQLFlow's wiki format: why the extension is
the way it is and how its pieces fit (decisions, patterns, narratives, incidents and maps), and the key census of
every OSDU document ([census/](census/README.md)).

The design documents and plans the system was built from are not kept: what shipped is documented in the corpus, why
in the wiki, and the git history holds the rest.
