---
id: delivery-guide-mcp
title: "The OSDU Delivery MCP server: connecting an AI assistant to the ledger, and to the data an AI context opens"
type: guide
summary: "osdu-delivery-mcp: SQLFlow's MCP server with the delivery_* tools, metadata and a ! question's query, its modes, install and HTTP service."
keywords:
  - mcp
  - mcp server
  - osdu-delivery-mcp
  - ai assistant
  - claude
  - delivery_* tools
  - delivery_find_records
  - delivery_run
  - metadata only
  - data mode
  - "!exit"
  - withheld tools
  - search_docs
  - streamable http
  - chat assistant
  - sqlflow_control_plane_url
  - operator action
  - gui links
  - osdu id link
  - explorer link
related:
  - delivery-concept-explorer
  - guide-chat-assistant
  - guide-slack-assistant
  - concept-authentication-and-identity
  - delivery-concept-api
  - delivery-concept-authentication-and-identity
  - delivery-guide-deployment
  - delivery-cli-run
  - delivery-guide-vscode
sourceRefs:
  - osdu/hosts/osdu-delivery-mcp/Cargo.toml
  - osdu/hosts/osdu-delivery-mcp/build.rs
  - osdu/hosts/osdu-delivery-mcp/src/lib.rs
  - osdu/hosts/osdu-delivery-mcp/src/main.rs
  - osdu/hosts/osdu-delivery-mcp/src/docs.rs
  - osdu/hosts/osdu-delivery-mcp/src/instructions.rs
  - osdu/hosts/osdu-delivery-mcp/src/links.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/mod.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/records.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/flows.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/documents.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/cache.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/assertions.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/dimensions.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/inventories.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/operate.rs
  - osdu/hosts/osdu-delivery-mcp/src/support.rs
  - osdu/hosts/osdu-delivery-mcp/tests/server.rs
  - osdu/gui/src/features/delivery/useLinkedPartition.ts
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantAccess.cs
  - osdu/deploy/docker/mcp.Dockerfile
  - osdu/deploy/k8s/mcp.yaml
  - osdu/deploy/compose/docker-compose.yml
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/terraform/main.tf
  - sqlflow/tools/sqlflow-mcp/src/host.rs
  - sqlflow/tools/sqlflow-mcp/src/modes.rs
  - sqlflow/tools/sqlflow-mcp/src/links.rs
  - sqlflow/src/SqlFlow.Assistant/AssistantInstructions.cs
  - sqlflow/tools/sqlflow-mcp/src/control_plane.rs
  - sqlflow/tools/sqlflow-mcp/src/config.rs
  - sqlflow/tools/sqlflow-mcp/src/http_server.rs
---

# The OSDU Delivery MCP server

`osdu-delivery-mcp` is the Model Context Protocol server an AI assistant (Claude, Cursor, VS Code, Codex, or the GUI's own
chat assistant) uses to answer questions about an OSDU Delivery estate. It is SQLFlow's MCP server
(`sqlflow/tools/sqlflow-mcp`, a library) composed with the delivery module: everything SQLFlow's server answers about
the catalog, runs, schedules, lineage, search and its documentation, plus 30 `delivery_*` tools over the ledger and the
module's definitions, minus the SQLFlow tools that profile or sample data. Its instructions are the assistant's one set
of rules: every client reads them, the GUI's chat and the Slack bot included. It calls the same control plane the GUI and `sqlflow` call,
with the caller's own token (for the GUI's chat, a token delegated for one run), so it can do what that person can do
and no more.

## Metadata, and a `!` question's query

The server answers from metadata. The one read of data is a `!` question's query (below); nothing else reads it:

| Returned | Never returned |
| --- | --- |
| The ledger's bookkeeping about a record: custody state, OSDU id and version, attempts with their outcome and error, the source file and row, who acted on it and when. | The record's content: what OSDU holds, what a mapping renders, the source row. |
| Definitions: flows, interfaces, mappings (their YAML), templates and OSDU schemas, cache flows and the types they declare, assertion tests, dimension definitions. | Cached values, and the values a cache change moved from and to. |
| Counts and health: records per state, submissions, cache versions and what each changed, test outcomes, dimension builds. | A dimension's values and keys, and the example records an assertion quotes (they are counted). |
| Configuration: partitions, and central configuration values, which are plain values or secret references. | Rows of any table, and the content of any file. |

A record is still identified: its OSDU id, source key, label and source file name are how it is found and traced.

A `!` question is answered from the data an approved AI context opens. SQLFlow's semantic layer tools
(`get_semantic_layer`, `search_semantic_layer`, `list_semantic_tables`, `describe_semantic_table`) answer from the AI
contexts an admin approved (`*.ai-context.yaml` files in a synced repository, see SQLFlow's
[ai-context.yaml](../../../../sqlflow/docs/reference/flow/ai-context.md)): table and column names, their business
descriptions, measures, joins and saved example SQL. Nothing is in it until a context is approved.
`find_similar_questions` returns the stored questions close to the question with the SQL that answers them, and when
none of them is trusted, the semantic layer the question is answered from (`grounding`), so the query is written
without searching and describing tables first; `prepare_query` and `run_query` run a query a person was shown,
`auto_run_trusted_match` runs a stored answer a person confirmed, and `confirm_question` records a person's verdict on
an answer. Every query is one read-only SELECT over the columns the approved contexts
open, which the control plane checks on prepare and on run; a column no context opens is refused.

A conversation is in one of two modes, as in SQLFlow (see SQLFlow's
[chat assistant guide](../../../../sqlflow/docs/reference/guides/chat-assistant.md#business-questions-and-modes--exit)):
only the person's command moves it. A message starting with `!data` or a bare `!` puts it in data mode, where
follow-ups need no `!`, and `!exit` (or `!estate`) returns it to estate mode; a question's wording, a request in words
or a follow-up that turns to the other kind of question never moves it, and every session refuses a tool of the other
mode without running it. "Which mode are you in?" is answered by `get_mode`, which names the mode, what set it and
whether the session lists every tool. Estate mode holds SQLFlow's metadata tools and every `delivery_*` tool together;
data mode holds only the semantic layer, the stored questions and the query tools. A client that opens a session per
message reads the command and names the mode (`?mode=data`), and the session lists only that mode's tools, offers no
switch and is given only that mode's instructions. An editor's assistant names none and sees every tool: it passes
the person's message to `start_data_mode` or `end_data_mode`, which switch only on the command they read there, and
the session answers only its mode's tools. Claude Code's terminal runs a prompt that starts with `!` as a shell
command, so there register the server once per mode (see [Installing it for a person](#installing-it-for-a-person)). So "!which curves
does wellbore X have, over which depths?" goes to the semantic layer and a query, never to the delivery ledger.

Seven of SQLFlow's tools are withheld, so they are neither listed nor callable: `detect_unique_key`,
`check_duplicate_keys`, `compare_baseline`, `dataops_capabilities`, `analyze_warehouse_health`, `discover_source` and
`scaffold_ingestion_flow`. Each profiles, samples or probes a datasource or a source's content. The list is `WITHHELD`
in `osdu/hosts/osdu-delivery-mcp/src/lib.rs`;
the server's tests fail when SQLFlow adds a tool this product has not decided to offer or withhold. No `delivery_*` tool
removes records from OSDU or reads what OSDU holds: removal is an operator's decision made in the GUI.

## The delivery tools

Every `delivery_*` tool calls a route under `/api/v1/delivery` and nothing else ([the delivery API](../concepts/api.md)).

| Question | Tool |
| --- | --- |
| Was this record delivered? Which records came from this file? | `delivery_find_records` (indexed prefix search; with no search, the newest records) |
| Why did it fail? Who deleted it? Which file and row is it from? | `delivery_record` (state, attempts, chain, activities) |
| How is this flow doing? Where does it deliver? What does a run need? | `delivery_flow` |
| What keeps this flow's records blocked, by cause? | `delivery_flow_issues` (each issue's pattern and counts, without the values an error names) |
| Which records of this flow failed, are held, drifted or came from that run? | `delivery_flow_records` |
| Which run is this id in the platform's logs? What did each part of a split delivery do? | `delivery_run` (a run id or a whole correlation-id) |
| What did the last run plan and deliver? Which batch is stuck? | `delivery_submissions` |
| What did the last retrieval bring back, and where is it? | `delivery_retrievals` |
| Who did what, and when? | `delivery_activities` |
| Which partitions are there, and what do we deliver to each? | `delivery_partitions` |
| Which OSDU does this flow really deliver to? | `delivery_config` |
| Is the cache fresh? Where does this type come from? What reads it? | `delivery_caches` |
| When did the cache last change? | `delivery_cache_versions` |
| What awaits approval, and what will it redeliver? | `delivery_cache_changes` |
| Which mapping does this flow use? Which flows does it affect? | `delivery_mappings` |
| Is this mapping valid? What does it leave required and empty? | `delivery_check_mapping` (with a synced repository's `repoId`, checked against the template the template folders on the mapping's path hold) |
| Start a mapping for a template a synced repository holds. | `delivery_scaffold_mapping` (the repository's id and the template file's path) |
| Which templates do the repositories hold, and which properties does one require? | `delivery_templates` (every template file, or one file laid out by its repository and path) |
| Which versions of a kind does a release publish? | `delivery_osdu_schemas` |
| What breaks if we move to the next version of a kind? | `delivery_osdu_schema_compare` |
| Are the data quality tests passing? What failed last night? | `delivery_assertions`, `delivery_assertion_runs` |
| Which dimensions exist, and are they up to date? What does this dimension's YAML make? | `delivery_dimensions` (with `blueprint`, how it is built) |
| Which inventories exist, and what did the last reconcile find? | `delivery_inventories` (counts by finding, never the ids) |
| What will this removal take out, and how did it last run? | `delivery_removals` (it reads; approving and removing are in the GUI) |

Six tools act. Each describes itself as an "(operator action)": the marker says the tool changes state, and the
assistant is told to use one only when the person asked for that action. The control plane admits any signed-in user to
these routes (only admin routes check a scope), so in practice a token that can sign in can use them
([authentication and identity](../concepts/authentication-and-identity.md)). Each is recorded in the audit trail under the
caller's name.

| Action | Tool |
| --- | --- |
| Ask a flow's OSDU whether it still answers (answers in the request, writes nothing to OSDU, and is recorded in the audit trail as a `probe` activity). | `delivery_probe_target` |
| Queue a verify run that compares one record with what OSDU holds. | `delivery_verify_record` |
| Queue a sync run that reconciles the ledger with the ingestion tables; sends nothing. | `delivery_sync_with_source` |
| Release held records so the next run tries them again. | `delivery_release_records` |
| Send one record to OSDU again, writing a new version. | `delivery_redeliver_record` |
| Approve or reject pending cache changes. | `delivery_decide_cache_changes` |

An OSDU flow is run with SQLFlow's `trigger_run`, which carries the kind's `operation`, `values` and `payload`
([running an OSDU flow](../cli/run.md)); `list_flow_kinds` names each kind's operations and which of them write to the
flow's target. `list_ai_contexts` says, to an admin, where each AI context stands. `run_trace` reads what a run did, `sync_repo_source` syncs a repository after a push and `run_schedule` fires a
schedule, the last two operator actions of SQLFlow's that a chat run is refused as `trigger_run` is. The server's instructions tell the assistant to run an operation that changes
or removes what OSDU holds (`reverse`, `undo`, `remove`, `delete-ledger`, an inventory's or a removal flow's `remove`)
only when the person asked for that exact operation; nothing in the server enforces it.

### Links into the GUI

Results carry links into the GUI, in a `links` object on each row and on an answer's envelope: `page` is the row's own
page, and every other link is named for what it opens. Set `SQLFLOW_GUI_URL` to make them absolute, which the Slack
assistant and a desktop client need; unset, they are root-relative, which is right for the GUI's own chat. The
instructions ask the assistant to make every name it gives the link its row carried for it, whatever it names (a
record by its label, an OSDU id, a flow, a run, a test), and to leave plain only a name no row carried a link for.

A link opens the page on the part of it the row is about. A flow opens on the interface and partition the row names,
as the GUI's own flow links do (a flow's page takes a partition the flow serves as the title bar's). The explorer, the
Records page and the audit trail make a partition a link names the title bar's, so a link to something in another
partition than the one last worked in still lands on it. Opening the explorer reads OSDU through the partition's
connection, as the explorer always does; the server itself still reads nothing from OSDU.

| Link | Opens | On |
| --- | --- | --- |
| `page` | A record's page (its state, attempts, source and audit trail); a submission's; an assertion run's report; a dimension (a build: its builds); a mapping in the builder; a template file; the partitions page; a cache on its records, a version on its history, a change on its deliveries, a cached type on its records; a cache flow's cache; a schedule's runs; the audit trail narrowed to an activity's ledger; a dimension or assertion flow's Dimensions or Tests tab; a test on its sheet, an assertion of it on its place there. | Every row of the module's that has a page. A dimension no build registered yet opens by its flow and name. |
| `osdu` | The explorer on one OSDU record, in its partition: the row's, else the one the id starts with (`{data-partition-id}:{object-type}:{id}`). | A record's `targetId` (hits, records, a flow's records, the records a record waits for, is waited for by, reads as its parent or is read by); the cached record a cache change is to (`itemId`, when it is an OSDU id of the cache's partition: a lookup table's key is not); an id a pending document refers to (`references`). `delivery_record` puts it on its answer's envelope too, once the record has landed. |
| `osduParent`, `osduOccupied` | The explorer on a record's parent (`parentId`), and on the record OSDU already holds at the id a held record's key makes, which no ledger claimed (`occupiedTargetId`). | Records. |
| `osduKind` | The explorer on the records of one OSDU kind, in the row's partition. | Mappings, templates, interfaces, dimensions, tests, schemas, a retrieval that reads one kind, the template a mapping check checked against. |
| `template` | The Templates page on the repository's template file: the file the row names (`repoId`, `templatePath`), else the file holding the template version it names. | Mappings, a schema comparison's sides, a bundled schema file, a mapping check's template. |
| `flow` | The flow's page on the interface and partition the row names. | Every row naming a pipeline with an interface or a partition; an action on a flow. |
| `records`, `delivered`, `submissions`, `issue` | The flow's Records tab (a submission's: the records it planned, and those it delivered), its Submissions tab, and its Issues tab on one issue. | A flow's figures, a flow rendering a mapping, a submission, an action on a flow; `issue` on a record's trace. |
| `activity` | The audit trail narrowed to one ledger, in its partition. | A flow's figures (one ledger's), an interface, a flow rendering a mapping. |
| `sourceFile` | The Records page on the records the row's ingestion file brought, in its partition. | Records and hits. |
| `record`, `lastSubmission`, `submission`, `changes` | A record's page, a submission's page, a cache's Deliveries tab. | Audit trail entries, actions on a record, records, attempts, a cache decision. |
| `cache`, `explorer` | A partition's cache, and the explorer on the partition. | Registry partitions. |
| `url`, `fromUrl`, `toUrl` | A schema's page where its release publishes it, outside the GUI. | Schemas and releases, a comparison's sides and files. |

SQLFlow's own links (`run`, `repo`, `lineage`, `schedule` and the rest) are added beside these; where a module's link and
a built-in one have the same name (`flow`), the module's wins, since it knows the part of the flow the row is about. The
envelope of a listing opens the GUI page that shows the same listing: `delivery_find_records` the Records page asked
the same, `delivery_flow_records` the flow's Records tab narrowed to the submission or run asked about, and the
assertion runs, matrix, board and dimension board their flow's Reports, History, Tests and Dimensions tabs.

## Documentation and flow language

SQLFlow's documentation tools (`search_docs`, `get_doc`, `list_docs`, `related_docs`, `get_doc_by_cli_command`,
`get_doc_by_yaml_path`) answer from this product's documentation beside SQLFlow's: the reference pages under
`osdu/docs/reference` (ids starting with `delivery-`) and the wiki under `osdu/docs/wiki` (decisions, patterns,
narratives, incidents and maps, ids starting with `delivery-wiki-`). A table of the `osdu` schema has a page of type
`table` (`delivery-table-<table>`, listed by `delivery-concept-database`), which `search_docs` searches when its `type`
is `table` and leaves out of a search with no type. Their frontmatter is compiled into
`osdu/docs/reference/manifest.json`, which the build embeds with every page it lists, so a page edit reaches the server
once the manifest is regenerated and the server rebuilt. The instructions a client reads at
`initialize` introduce the product as an extension of SQLFlow, send a "how do I" question to
`delivery-wiki-pattern-catalog` first, and say which tool answers which question, the vocabulary the tools share
(record, interface, partition, submission) and the operations `trigger_run` takes per kind. The flow-language tools
(`validate_flow`, `list_flow_kinds`, `list_flow_keys`, `describe_flow_key`) know the module's flow kinds and documents from the key census in
`osdu/docs/census`.

## Installing it for a person

```bash
cargo build --release --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
osdu/hosts/osdu-delivery-mcp/target/release/osdu-delivery-mcp install claude
# prints: claude mcp add osdu-delivery -- <path to osdu-delivery-mcp>
```

`install` also prints a registration for `cursor`, `vscode` and `codex`. [The VS Code extension](vscode.md) holds the
server already: it registers it with the editor's chat, and connects Claude Code, Cursor and Codex from a command.

An editor's session lists every tool, about 110 of them, since an editor keeps one session and does not refresh a
changed list in time; GitHub Copilot then holds most of them back, and the model spends steps finding the one a
question needs. Register the server once per mode instead, and turn on the registration a chat needs among the
editor's tools:

```bash
osdu-delivery-mcp install claude --mode data     # claude mcp add osdu-delivery-data -- <path> --mode data
osdu-delivery-mcp install vscode --mode estate   # "osdu-delivery-estate", with "args": ["--mode", "estate"]
```

A data-mode registration lists about a dozen tools (the semantic layer, the stored questions, the query tools, saving
an answer, `get_mode`) and the sign-in tools, and is given only the rules for a question answered from data; an
estate-mode registration lists SQLFlow's metadata tools and every `delivery_*` tool, and reads no rows. A registered
server stays in its mode: it lists no switch, and a call of the other mode's tool names the registration that answers
it. `SQLFLOW_MCP_MODE` sets the mode when `--mode` is not given; an unknown argument or mode stops the server with its
usage. Run by a client over stdio, the server reads
`SQLFLOW_CONTROL_PLANE_URL` (else the address it was last given with `set_control_plane_url`, else
`http://localhost:8080`). Sign in with the `login` tool: it returns an address and a code to approve in a browser, where
you sign in as you do in the GUI, Microsoft single sign-on included ([signing in](vscode.md#signing-in)), then
`check_auth_status` completes it. The server keeps the session token the approval grants, which carries no more than the
approving person's scopes, and renews it at `POST /api/v1/auth/renew` before it lapses, as the GUI renews its own,
until the session reaches its absolute cap; a refused renewal ends the session and the next tool call says so. `set_access_token` stores a token directly, and `SQLFLOW_CONTROL_PLANE_TOKEN`
supplies one from the environment. The address and token are kept in `~/.sqlflow/osdu-delivery-mcp-config.json` and
`~/.sqlflow/osdu-delivery-mcp-token.json`, apart from a `sqlflow-mcp` on the same machine.

## Running it as a service

`osdu-delivery-mcp http [--bind <addr>] [--allowed-hosts <h1,h2>]` serves streamable HTTP for remote clients and the
control plane's chat assistant. It holds no credential: every request to `/mcp` must carry `Authorization: Bearer <token>`
(otherwise 401 `missing or malformed Authorization header; expected: Bearer <SQLFlow access token>`). The server checks
each bearer with the control plane's `GET /api/v1/me` before any tool runs, and forwards it with every call, so the
control plane authorizes it as it does the GUI. A missing bearer never falls back to a stored one: over HTTP the server
holds none. `GET /healthz` answers without a token for the platform's probe.

| Variable | Meaning |
| --- | --- |
| `SQLFLOW_CONTROL_PLANE_URL` | The control plane the tools call. |
| `SQLFLOW_GUI_URL` | The GUI's public address, for absolute links; unset, links are root-relative. |
| `SQLFLOW_MCP_HTTP_BIND` | The listen address; `127.0.0.1:8787` unless set (the image sets `0.0.0.0:8080`). `--bind` wins. |
| `SQLFLOW_MCP_HTTP_ALLOWED_HOSTS` | A `Host` header allowlist, comma separated; `--allowed-hosts` wins. Unset, a loopback bind keeps a loopback-only check and any other bind disables it, since every request needs a bearer token. |
| `SQLFLOW_MCP_LOG` | The log filter (default `info`); logs go to stderr. |

```bash
docker build -f osdu/deploy/docker/mcp.Dockerfile -t osdu-delivery-mcp:latest .
docker run -p 8787:8080 -e SQLFLOW_CONTROL_PLANE_URL=http://controlplane:8080 osdu-delivery-mcp:latest
```

The compose stack has it behind the `mcp` profile, and `osdu/deploy/k8s/mcp.yaml` adds it as `/mcp` on the estate's host.
The Container Apps templates deploy it when asked (Bicep `mcpEnabled` or `mcpImage`, Terraform `mcp_enabled` or
`mcp_image`), and with an AI Foundry model the GUI chat assistant on it too ([deployment](deployment.md#azure-container-apps)).

## The GUI chat assistant

The control plane's chat assistant ([SQLFlow's chat assistant guide](../../../../sqlflow/docs/reference/guides/chat-assistant.md))
uses this server when `ControlPlane:Assistant:Mcp:ServerUrl` points at its `/mcp`. Each question runs under a token
delegated from the signed-in user for that run alone, and the control plane refuses it on every route not opened to it;
the delivery module opens exactly the routes its 25 `delivery_*` readers read (`DeliveryAssistantAccess.ReaderTools`,
[authentication and identity](../concepts/authentication-and-identity.md)). The chat reads this server's instructions as
its rules and opens each run's session in the conversation's mode, so it answers as any other client of the server
does: schema whole in estate mode, and a `!` question answered from the data in data mode, with its query run and the
answer saved on request. The six operator actions (`DeliveryAssistantAccess.ActionTools`), `trigger_run` and every
other tool that starts work are refused to a chat run by the control plane, and the assistant says where a person does
it. The same fence holds any call this server marks as the assistant's, such as the Slack bot's. A deployment that sets
`ControlPlane:Assistant:Mcp:AllowedTools` narrows the tools a run may call.

## Where it lives

| Path | What |
| --- | --- |
| `osdu/hosts/osdu-delivery-mcp` | The host crate: the tools, the instructions a client reads at `initialize`, the documentation index, the GUI link rules, `WITHHELD`, and the program. |
| `sqlflow/tools/sqlflow-mcp` | SQLFlow's server, as a library a host composes. |
| `osdu/deploy/docker/mcp.Dockerfile` | The image, built from the repository root. |

```bash
cargo test --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
```

The tests run against a control plane of their own, so they need no database, no OSDU and no network.

## See also

- [SQLFlow: the GUI chat assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md)
- [SQLFlow: the Slack assistant](../../../../sqlflow/docs/reference/guides/slack-assistant.md)
- [The delivery API](../concepts/api.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
