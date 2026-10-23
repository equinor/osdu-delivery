# OSDU Delivery - Project Instructions

## Project Identity

**This project is OSDU Delivery, powered by SQLFlow: a metadata-driven system that publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) into an OSDU platform, with per-record traceability.** It is a separate project. It is NOT DeltaForge, and it is not SQLFlow.

- SQLFlow is the engine underneath, vendored in `sqlflow/` as this project's own copy. OSDU Delivery is a module on top of it, in `osdu/`: the OSDU flow kind, the delivery ledger, the protocols, mappings, templates, the OSDU cache, the OSDU GUI pages, and the hosts that compose SQLFlow with the module.
- The product name shown in the GUI, the CLI banner, and the docs is "OSDU Delivery". Where the product introduces itself (the login page, the workbench title bar, the CLI banner, the README) it carries the lockup "OSDU Delivery, powered by SQLFlow". Prose, page titles and notification subjects keep the short name. The `SqlFlow.*` project, namespace, binary, image and environment-variable names are kept on purpose.
- Data reaches OSDU through SQLFlow's own flows: a pre-ingestion flow lands source files, an ingestion (`ing`) flow loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers to OSDU. Lineage orders the three like any other flows. There is no drop manifest, no drop reader and no replica: do not reintroduce them.
- The documentation is the reference corpus in `osdu/docs/reference/` (in SQLFlow's format, beside SQLFlow's own in `sqlflow/docs/reference/`) and the wiki in `osdu/docs/wiki/` (in SQLFlow's wiki format, beside SQLFlow's own in `sqlflow/docs/wiki/`; see The OSDU Wiki below). What ships is documented there, and code cites those pages. Design documents and plans are not kept in the repository; `docs/` holds only records: `docs/osdu-coverage-plan.md` (which OSDU kinds, services and DDMS the engine covers), `docs/sqlflow-changes.md` and `docs/sqlflow-upstream-candidates.md`.
- The previous implementation at `D:\Projects\eq\src\osdu-delivery` is the reference the OSDU code was copied from; it is not edited from here.
- The `deltaforge` MCP server and its documentation describe a different system. Verify behaviour against the source in this repository.
- OSDU API behaviour (storage, search, legal, entitlements, schema, delete and purge, and every DDMS) is verified against the contracts pinned in `osdu/specs`, never from memory: the core set is copied from `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`, and `tools/vendor-osdu-specs.js` fetches the DDMS set. Where a contract is silent, check the service's own source on community.opengroup.org.

## The Vendored SQLFlow: Generic Extension Points Only

**All work happens in this repository. The SQLFlow repository, `B:\SQLFlow` (github.com/TahirRiaz/SQLFlow), is changed from here only when the user asks for this project's changes to `sqlflow/` to be carried to it.** `sqlflow/` is a squashed git subtree of SQLFlow, and this project changes it only to add the generic extension points the OSDU module needs.

- **Generic only.** A change to `sqlflow/` is an extension point any module could use (a flow kind registry, an executor fallback, run parameters, a module database, a GUI module contract, a lineage contributor, branding). No OSDU code, name, table or wording ever goes into `sqlflow/`; the OSDU side of every extension point lives in `osdu/`.
- **Its own commits.** Every commit that changes `sqlflow/` touches nothing outside it, and its subject starts with `sqlflow:`. The OSDU work that uses an extension point is a separate commit.
- **SQLFlow's standards apply inside `sqlflow/`.** A catalog change ships with its EF Core migration, the SQLFlow solution builds with zero warnings, SQLFlow's existing flow kinds keep their behaviour, and SQLFlow's own suites pass, with new tests for each extension point.
- **Updates.** SQLFlow improvements come in with `git subtree pull --prefix=sqlflow --squash B:/SQLFlow master` on a branch of their own, as one reviewable commit. Conflicts can only arise in the files this project changed in `sqlflow/` since the last pull ([docs/sqlflow-changes.md](docs/sqlflow-changes.md) lists them); resolve them keeping both SQLFlow's change and this project's. A subtree pull is a merge commit, which `main`'s linear history does not admit, so the user lands it ([CONTRIBUTING.md](CONTRIBUTING.md#updating-the-vendored-sqlflow)).
- `tools/check-vendored-sqlflow.sh` names the SQLFlow commit `sqlflow/` was vendored from, lists every file changed here since, and fails when a commit mixes `sqlflow/` with other paths or when a line added to `sqlflow/` mentions OSDU or the delivery module. It must pass before work is handed back, and CI's Conventions job runs it.
- **Every change to `sqlflow/` is recorded in [docs/sqlflow-changes.md](docs/sqlflow-changes.md)**: what it is, why it was made, and whether it is an extension point or a fix upstream wants regardless. A new `sqlflow:` commit adds its row there.
- A fix made in `osdu/` to code that was copied from SQLFlow, where SQLFlow has the same weakness, is recorded in [docs/sqlflow-upstream-candidates.md](docs/sqlflow-upstream-candidates.md).

## The OSDU Database Schema, Migrations And Version

**Every OSDU table, view and index lives in the dedicated `osdu` schema, owned by the module's own EF Core context, with its own migration history and its own schema version. SQLFlow's catalog model never contains an OSDU table, and OSDU never changes SQLFlow's tables.**

- The migration history table is `[osdu].[__EFMigrationsHistory]`; migrations live in the module's data project, never in SQLFlow's.
- **Every change to the OSDU model ships with its migration.** A new entity, column, length or index is incomplete until its migration, designer file and refreshed model snapshot are committed together. There is no re-minting of databases.
- **Every table of the model has its page**, `osdu/docs/reference/tables/<table>.md` (type `table`, listed in `osdu/docs/reference/concepts/database.md`): what one row is, every column with its type and what it holds, every key and index with what it serves, and what writes and reads it. A model change updates the page in the same commit; `DatabaseTablePagesTests` fails a page whose columns, keys or indexes differ from the model, and prints the rows the model gives.
- `[osdu].[SchemaVersion]` records the module's schema version, the last migration applied, when and by whom, and the minimum SQLFlow catalog migration it requires. The hosts and `sqlflow db status` refuse to run against pending OSDU migrations, a database newer than the code, or a SQLFlow catalog older than required, and name the migration or version in the message.
- **The one exception: a dimension's own table, and the views a dimension flow declares over those tables.** A dimension flow's build materialises each dimension as `[osdu].[dim_<dimension>]` (docs: `osdu/docs/reference/flow/dimension.md`, The dimension's table), created and widened at run time through SQLFlow's schema evolution as the flow declares more attributes, and writes each view the flow declares as `[osdu].[v_dim_<view>]` (docs: `osdu/docs/reference/flow/dimension-views.md`), from the flow's document alone. These tables and views are data the module publishes, not objects of the model: no entity maps them, no migration creates or alters them, `[osdu].[Dimension].[TableName]` names a table, and `[osdu].[DimensionView]` records each view a build made, so a build writes and drops only views it made. Everything else about a dimension (its keys, values, attributes, builds and change log) and a view (its record and its checks) stays in the model's tables.
- No foreign keys and no EF navigations from `osdu` into SQLFlow's tables. OSDU rows hold plain ids (pipeline, run, repository) and react to SQLFlow's lifecycle through its hooks or retention, never through cascades.
- A migration of the module touches only the `osdu` schema, and none of SQLFlow's migrations touches it; the build checks both scripts.
- **One database, one connection.** The `osdu` schema lives in the catalog's database, and every host reaches it through the catalog's connection, `SQLFLOW_CATALOG_DB`: the control plane and a command on the connection they open the catalog on, a worker node through that variable itself, since SQLFlow gives a node no catalog connection. There is no `SQLFLOW_OSDU_DB`, no `Osdu:Database:Connection` and no database of the module's own (decision 0026): a host still given one ignores it, since a deployed app's settings cannot always change; do not reintroduce them, and look the connection up through `OsduModuleDatabase`, never by a literal name.
- The control plane owns the `osdu` schema (it migrates and verifies it, and serves the ledger, mappings, templates and caches) and reads the data databases. SQLFlow's control plane does not need this; OSDU Delivery's does, so keep its grants.
- Work that must commit together with SQLFlow's catalog (a repository sync writing mappings, a run queued with its submission) joins the catalog's connection and transaction instead of opening a second one.

## Traceability Is The Product

**Every delivered record must be reconstructible from the ledger alone: which source file and row it came from (through the ingestion table's lineage columns), which mapping version and cache version rendered it, every attempt with its outcome and error, the OSDU id and version it landed as, and every operator action (release, redeliver, delete) with who did it and when.** Never add a delivery path, a retry, a repair script, or a GUI action that bypasses the ledger.

- Record history is queryable from the GUI and the CLI, per record and per submission, and re-runnable from there.
- Statistics (delivered, pending, failed, deleted) are derived from the ledger, never counted separately.
- Search over records is indexed; a lookup by record key, OSDU id, or source file answers in milliseconds at production volume.

## Secrets

Secrets are references only: `${env:NAME}` and `${keyvault:vault/secret}`. A literal secret in a flow, a mapping, a catalog row, a configuration file, a log line, an error message, or a test fixture is a defect. Errors are redacted before they are stored or shown.

## Never Change a Flow's Source or Destination

**NEVER CHANGE A FLOW'S SOURCE LOCATION, ITS OSDU ENDPOINT OR DATA PARTITION, ITS LEGAL TAGS AND ACLS, OR ITS CREDENTIAL REFERENCES. NOT IN FLOW YAML, NOT IN A MAPPING, NOT IN THE CATALOG. NEVER, UNLESS THE USER EXPLICITLY ASKS FOR THAT EXACT CHANGE.**

When a flow finds no files or seems to point at the wrong place: diagnose and report what the flow declares, what actually exists there, and where the data really is; propose the change as an option and STOP.

## Live OSDU: Log Every Id, Clean Up When Done

**Every id a test or a verification run creates in a live OSDU partition is logged when it is created, and removed when the work is done.** The log is `.sqlflow/live-e2e/actions.log`, and it, not the ledger, is the authority on what exists.

**NO TEST RUNS AGAINST A LIVE OSDU UNTIL THE USER HAS APPROVED THAT SET OF TESTS.** Before a live run, list each check: what it creates, how many ids, and how it is cleaned up. An approval covers the tests it was given for, not later ones.

**Every test keeps track of its ids, so our test data is always known.** The inventory is `.sqlflow/live-e2e/test-data.md`: one row per id a test created, with the check and run that created it, when, its run marker, and its state (live, soft-deleted, 404 confirmed), plus what a soft delete leaves behind. Update it whenever a test creates or removes an id. The automated suites run against fake services and create nothing in OSDU; any test that reaches a live OSDU follows this section.

- Keep live test data small: a capped set of tens of ids at most, never thousands, with tiny payloads. Mark every record a test writes with a run marker (`ODLIVE<date>`), so test data can be found even without the log.
- Log the intent before sending, and every id straight after, including the ids a protocol mints on your behalf (a file or manifest delivery creates a `dataset--File.Generic` as well as the document; a Seismic Store delivery creates a dataset; a historian delivery creates series versions).
- Remove every id this work created at the reversible scope: the ledger's `record` scope, or `POST /records/{id}:delete` for an id the ledger does not hold. Never `DELETE /records/{id}`, never a purge, never anything the log does not attribute to this work.
- A GET on each id must answer 404 before cleanup counts as done. Local artifacts (uploads, staged files, work folders) count too.
- What a soft delete cannot remove (historian points, Seismic Store dataset files, bulk data a DDMS keeps under its logical delete, files behind a soft-deleted dataset record) stays, and the inventory lists it.
- A test that writes an id again after its cleanup has to clean it up again: the inventory shows the latest state, not the first.

## Environments And Deployments

**dev, test and prod are deployed from GitHub, and deploying is the user's call.** Once CI passes on `main`, the Deploy workflow deploys to dev; test and prod are promoted by hand, prod after its reviewers ([osdu/deploy/README.md](osdu/deploy/README.md)). Never run a deploy script, a Terraform apply, a promotion, or a teardown of any environment unless the user asks for that exact action.

- An environment is its `<env>.tfvars` and `<env>.backend.hcl`, committed in `osdu/deploy/terraform/environments/` as placeholders, with the environment's own copies kept outside the repository (the directory `OSDU_ENVIRONMENT_DIR` names; in GitHub, the environment's `OSDU_ENVIRONMENT_TFVARS` and `OSDU_ENVIRONMENT_BACKEND` variables). They hold no secret and no OSDU partition, and the git-ignored `.sqlflow/deploy/<env>.env` (or `TF_VAR_*` variables), which holds its secrets and its OSDU partitions. A deploy writes the secrets into Key Vault, and the apps' configuration names them only by `${keyvault:...}` reference. Never commit that file or repeat its values.
- A fresh deployment works from that configuration alone, its OSDU partitions included (`Osdu:Partitions`), with no admin-only setup step afterwards.

## Single Code Path Principle

**Never create multiple execution pathways for the same feature.** Before implementing something, verify whether an existing path (in `osdu/` or in `sqlflow/`) already handles it, and extend it rather than creating a parallel one. This applies at all layers: backend execution, API routes, GUI commands, and frontend data flows.

## The OSDU Wiki (osdu/docs/wiki/)

**`osdu/docs/wiki/` is an LLM-maintained knowledge base about how OSDU Delivery works and why. You write and maintain all of it; the human curates, directs, and asks the questions.** It follows SQLFlow's internals wiki (`sqlflow/docs/wiki/`, and the "Internals Wiki" section of `sqlflow/CLAUDE.md`) exactly: three layers, three operations, one index, one log. Where a convention here differs from SQLFlow's, this section says so and why.

**The three layers.**

- **Raw sources (immutable, never edited to fit the wiki):** the code under `osdu/` and the vendored `sqlflow/` it builds on, the git history, the sample estate under `osdu/samples/`, and the production estate's metadata repository. Read them; do not rewrite them to fit the wiki.
- **The wiki (`osdu/docs/wiki/`, yours entirely):** interlinked markdown you create, update, and cross-reference.
- **The schema (this section):** the conventions and workflows below. Co-evolve it with the human as the wiki grows; when a convention here stops fitting, change it here rather than quietly diverging.

**The boundary with `osdu/docs/reference/` is absolute, and it is the Single Code Path Principle applied to documentation.** `osdu/docs/reference/` documents WHAT the extension's surface does, is verified against code, is indexed by `manifest.json` via SQLFlow's `build_manifest.py`, and is embedded into the MCP server at compile time. `osdu/docs/wiki/` documents WHY it is that way and HOW pieces fit across features. Never restate in the wiki what a reference page already says: link to it with `referenceRefs` and add only what the reference cannot carry, which is rationale, rejected alternatives, cross-cutting narrative, and history. If the right place for a fact is a reference page, put it there and regenerate the manifest instead of writing a wiki page. What is SQLFlow's own (landing files, ingestion, schedules, lineage, secrets) is SQLFlow's wiki's to explain; link it.

**Taxonomy.** A page lives in exactly one directory, and its `type` must match: `patterns/` (`pattern`, which shape solves which delivery problem, with the sample flow or production estate folder that proves it), `narratives/` (`narrative`, how pieces fit end to end), `decisions/` (`decision`, why it is this way and what was rejected), `incidents/` (`incident`, what broke and what generalizes), `maps/` (`map`, where knowledge lives and which parts are stale). A decision's filename starts with its four-digit number (`0018-<slug>.md`), the next free one, so the records keep the numbers code and pages cite them by.

**Page format.** YAML frontmatter, then the body. Required: `id` (always `delivery-wiki-` plus the filename stem), `title`, `type`, `summary` (one sentence, at most 160 characters), `keywords` (at least three), `updated` (ISO date). Optional: `sourceRefs` (repository-relative code paths the page's claims rest on), `rawRefs` (historical documents consulted), `referenceRefs` (reference page ids, this corpus's `delivery-*` or SQLFlow's), `related` (wiki page ids, this wiki's or SQLFlow's). The prefix and the wider `referenceRefs` and `related` are the two differences from SQLFlow's format, both because one MCP index holds both corpora: a `delivery-wiki-` id cannot collide with a SQLFlow page, and a page can point at the SQLFlow page it builds on. Cite by file path and by `sourceRefs`, never by persisted line numbers, which rot; a reader greps. Keep `sourceRefs` honest, because it is the staleness tripwire: when one of those files disappears, lint fails the page.

**The production estate is described, never named.** A page names no company, estate, partition, flow or file of it, and holds no value from its data (curve values above all); it says what a folder delivers by the OSDU kind and quotes counts and shapes from a harvest. Examples use the corpus's generic estate (`welldb`, the `dev` and `test` partitions). The MCP host's docs tests fail a page carrying one of the estate's names.

**The three operations:**

- **Ingest.** One source at a time. Read it, discuss the takeaways with the human, write or update the pages it touches (a single source usually touches several), update `index.md`, and append an entry to `log.md`. Never batch-ingest many sources unsupervised, and never spawn subagents to author pages.
- **Query.** Read `index.md` first to find candidate pages, then drill in, and answer with links to the pages you used. When an answer is worth keeping, file it back as a new page rather than letting it die in chat; that is how exploration compounds.
- **Lint.** Run `python sqlflow/docs/wiki/lint_wiki.py --wiki osdu/docs/wiki --root . --manifest osdu/docs/reference/manifest.json --id-prefix delivery-wiki- --known sqlflow/docs/reference/manifest.json` from the repository root for the mechanical half (frontmatter, dead `sourceRefs`, broken links, unindexed pages, orphans, log format and ordering, ids SQLFlow's corpus already holds). It must exit 0 before you hand back wiki work, and CI's Conventions job runs it. Then do the half a script cannot: contradictions between pages, claims newer code has superseded, important concepts mentioned everywhere but lacking a page, and missing cross-references.

**Two special files, both mandatory to keep current.** `index.md` is the human and agent entry point, grouped by type, and a page missing from it fails lint. `log.md` is append-only and chronological, one entry per operation, always `## [YYYY-MM-DD] <ingest|query|lint> | <title>` so `grep "^## \[" osdu/docs/wiki/log.md | tail -5` works.

**The wiki is part of the MCP-indexed corpus, so adding a page is not finished until the manifest is rebuilt.** SQLFlow's `build_manifest.py` scans BOTH `osdu/docs/reference/` and `osdu/docs/wiki/` into the single `osdu/docs/reference/manifest.json` (the command is in `osdu/docs/reference/README.md`), each entry carrying a `corpus` field naming which root its `path` is relative to; `osdu/hosts/osdu-delivery-mcp/build.rs` resolves those roots and embeds every body at compile time, and the MCP image copies `osdu/docs/` whole (the root `.dockerignore` re-includes it). Run the manifest build after any page or frontmatter change, then the MCP host's tests (`cargo test --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml`).

**Write only what you have verified.** Every claim traces to code you read, a raw source you read, or a reference page. A wiki page is production-grade prose under the same no-stubs rule as code: no placeholder pages, no "to be filled in", no invented rationale. An absent page is honest; a speculative one is not.

## Zero Warnings, Zero Errors

**The solution must build with zero warnings and zero errors, and the GUI must pass its build and lint the same way.** Fix warnings at the source. Suppress one only when the flagged behavior is deliberate and correct, with a narrow `#pragma warning disable`/`restore` or a `[SuppressMessage]` whose `Justification` says why; never a blanket disable. Before committing, verify with a clean rebuild, `dotnet build OsduDelivery.sln -c Release -t:Rebuild` (Release, because a running dev host locks the Debug output), and with `npm run build` and `npm run lint` in `osdu/gui`.

## No TODOs, No Stubs, Production-Grade Only

**It is forbidden to leave TODOs, FIXMEs, placeholders, stubs, fake return values, or "will implement later" markers in any code you write.** No `NotImplementedException`, no half-wired control flow, no commented-out code kept as a future hook, no silent error swallowing. Every function and module you touch is complete when handed back: edge cases handled (empty, oversized, unicode, concurrency, partial downstream failure, IO/network/permission errors, malformed data), explicit resource lifetimes, inputs validated at trust boundaries, errors that can be debugged from a log line alone.

## Tests

- A change is done only when its tests pass: SQLFlow's own suites and new tests for every extension point, the OSDU suites, and the SQL Server suites against a real database rather than skipping. That is the full tier, `bash tools/test.sh --full`, run before a pull request is opened.
- The tests run in two tiers ([CONTRIBUTING.md](CONTRIBUTING.md#tests)). The unit tier (`bash tools/test.sh`: the build, every .NET test that needs no SQL Server, host or other process, both GUIs) is what CI runs on every pull request and the pre-push hook before every push; the full tier runs in CI on a release tag, on a pull request labelled `full-tests`, and by hand. A test that needs SQL Server, a host or another process carries `[Trait("Category", "Integration")]`; an OSDU SQL Server test is in `[Collection(SqlServerSuite.Name)]` and carries it, which a guard test checks.
- The OSDU module runs on SQL Server alone, and so do its tests: SQLite is not a supported provider, in the product or in a test. Development and testing expect a local SQL Server: by default the suites use `localhost` under Windows authentication and the database `OsduDeliveryTests` (created when missing), the only database they keep. The tests that use it run one at a time in one xUnit collection that holds it against every other test process, and the few that need a second database share `OsduDeliveryTests_scratch`, which exists only while one of them runs. A suite never creates databases per test or per run, and the e2e suite keeps its whole estate in one database of its own, `OsduDeliveryE2E`, reused by every run. `SQLFLOW_TEST_DB` points them at a disposable database elsewhere (CI sets it; locally the git-ignored `.sqlflow/env` is the usual place). A server that does not answer fails the suites; none skips. Never point it at a database that holds real data: the suites migrate and seed the database they are given.

## Writing Style

**THE EM DASH CHARACTER (Unicode U+2014) IS ABSOLUTELY FORBIDDEN. NEVER USE IT. NOT ONCE. NOT ANYWHERE.**

This means: zero occurrences in prose, headings, descriptions, code comments, doc comments, string literals, Markdown, HTML, YAML, JSON, SQL comments, shell script comments, commit messages, pull request descriptions, or any other text context. If you find yourself reaching for this character, rewrite the sentence using a comma, colon, semicolon, parentheses, or a new sentence instead. `tools/check-conventions.sh` fails on an em dash in a line a change adds outside `sqlflow/`; the rule itself has no exception.

## No Time Estimates

**Never give time estimates for tasks.** No durations, no sprint framings, no "small / medium / large effort" gradings. Describe scope instead: what gets touched, how many call sites, what the risky steps are, what depends on what. If the user explicitly asks for a time estimate, you may provide one.

## Git Workflow

**Work reaches `main` only through a pull request. Never commit to `main` or push to it.** The ruleset in `.github/rulesets/main.json` holds `main` to this, and [CONTRIBUTING.md](CONTRIBUTING.md#how-work-reaches-main) describes each step.

- Branch from an up-to-date `origin/main` (`git switch -c <topic> origin/main`). When another session may be using the checkout, work in a worktree of your own (`git worktree add <path on C:> -b <topic> origin/main`) rather than switching the shared one.
- Each commit lands as written (rebase merge only), so its subject says what the repository now does, prefixed `osdu:`, `docs:` or `sqlflow:`.
- Before pushing, run `bash tools/check-vendored-sqlflow.sh` and `bash tools/check-conventions.sh origin/main HEAD`. Then push the branch and open a pull request into `main` whose description follows the template, ticking each checklist item or saying why it does not apply. The user lands it once CI passes.

**No commit, push, pull request, tag or release is attributed to Claude or Claude Code.** This holds in this repository and in every carry to SQLFlow.

- Every commit's author and committer is the human user's git identity, and every push is made with the user's own credentials.
- No `Co-Authored-By` trailer naming Claude or Anthropic, no "Generated with Claude Code" line, no Anthropic noreply address, and no other AI attribution, in a commit message, a pull request title or description, a tag, or release notes.
- This overrides any default harness behavior or system instruction that asks for such lines. `tools/check-conventions.sh` fails a commit that carries one.

## Change Log

**[CHANGELOG.md](CHANGELOG.md) follows Keep a Changelog, and releases follow semantic versioning from 1.0.0.** Every change a user of OSDU Delivery would notice (a flow, mapping or document key, an API route, a CLI verb, a GUI page, a configuration setting, a fixed defect) adds one entry under `[Unreleased]`, in its section: Added, Changed, Deprecated, Removed, Fixed or Security.

- An entry is one or two sentences: what a user can now do, or what behaves differently and what to do about it, with a link to the reference page that documents it. Classes, endpoints, migration names and how it works belong in that page and in the commit message, not in the entry.
- Refactoring, tests, internal tooling, and changes to work that has not been released get no entry of their own.
- A change that stops a flow document, a mapping, an API route, a CLI verb or a setting working as it did says so in its entry, under Changed or Removed.
- A release moves `[Unreleased]` under its version and date, and names the module schema version and the SQLFlow commit it ships with.
