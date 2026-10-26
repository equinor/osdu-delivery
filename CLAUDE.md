# OSDU Delivery - Project Instructions

The rules for working in this repository. What the product does is in the reference (`osdu/docs/reference/`), and why
it is this way is in the wiki (`osdu/docs/wiki/`); this file links them rather than repeating them.

## The Project

- **OSDU Delivery publishes subsurface records (wells, wellbores, well logs and the other OSDU kinds) into an OSDU
  platform, with every record traceable.** It is built on SQLFlow and is neither SQLFlow nor DeltaForge: the
  `deltaforge` MCP server describes a different system, so verify behaviour against this repository's source.
- `sqlflow/` is this project's vendored copy of SQLFlow, the engine; `osdu/` is the module on top
  ([architecture](osdu/docs/reference/concepts/architecture.md)). Data reaches OSDU through SQLFlow's own flows: a
  pre-ingestion flow lands the files, an `ing` flow keys the rows, and the OSDU flow delivers them, ordered by lineage.
- The product is "OSDU Delivery". The login page, the workbench title bar and the CLI banner carry "OSDU Delivery,
  powered by SQLFlow". The `SqlFlow.*` project, namespace, binary, image and environment-variable names are kept on
  purpose.
- Documentation is the reference corpus in `osdu/docs/reference/` (what ships; code cites it) and the wiki in
  `osdu/docs/wiki/` (why, kept as [its index](osdu/docs/wiki/index.md#how-this-wiki-is-kept) says). `docs/` holds only
  records (`osdu-coverage-plan.md`, `sqlflow-changes.md`, `sqlflow-upstream-candidates.md`), never designs or plans.
- OSDU API behaviour is verified against the contracts pinned in `osdu/specs`, never from memory. Where a contract is
  silent, check the service's own source on community.opengroup.org.

## Never Change a Flow's Source or Destination

**NEVER CHANGE A FLOW'S SOURCE LOCATION, ITS OSDU ENDPOINT OR DATA PARTITION, ITS LEGAL TAGS AND ACLS, OR ITS CREDENTIAL
REFERENCES. NOT IN FLOW YAML, NOT IN A MAPPING, NOT IN THE CATALOG. NEVER, UNLESS THE USER EXPLICITLY ASKS FOR THAT EXACT
CHANGE.**

When a flow finds no files or seems to point at the wrong place: diagnose and report what the flow declares, what
actually exists there, and where the data really is; propose the change as an option and STOP.

## Live OSDU: Approve, Log, Clean Up

**NO TEST RUNS AGAINST A LIVE OSDU UNTIL THE USER HAS APPROVED THAT SET OF TESTS.** First list each check: what it
creates, how many ids, and how it is cleaned up. An approval covers only the tests it was given for. The automated
suites run against fake services and create nothing in OSDU.

- Keep it small (tens of ids at most, tiny payloads), and mark every record a test writes with a run marker
  (`ODLIVE<date>`).
- Log the intent before sending, and every id straight after, in `.sqlflow/live-e2e/actions.log`, including the ids a
  protocol mints for you (a file or manifest delivery's `dataset--File.Generic`, a Seismic Store dataset, a historian's
  series versions). The log, not the ledger, is the authority on what exists.
- Keep `.sqlflow/live-e2e/test-data.md` current: one row per id, with the check and run that created it, when, its run
  marker and its state (live, soft-deleted, 404 confirmed), and what a soft delete leaves behind. An id written again
  after its cleanup is cleaned up again.
- Remove only what the log attributes to this work, at the reversible scope: the ledger's `record` scope, or
  `POST /records/{id}:delete` for an id the ledger does not hold. Never `DELETE /records/{id}`, never a purge. Cleanup
  is done when a GET on each id answers 404 and local artifacts (uploads, staged files, work folders) are gone; what a
  soft delete cannot remove stays listed in the inventory.

## Secrets And Deployments

- Secrets are references only: `${env:NAME}` and `${keyvault:vault/secret}`. A literal secret in a flow, a mapping, a
  catalog row, a configuration file, a log line, an error message or a test fixture is a defect. Errors are redacted
  before they are stored or shown.
- **Deploying is the user's call.** Never run a deploy script, a Terraform apply, a promotion or a teardown of any
  environment unless the user asks for that exact action ([osdu/deploy/README.md](osdu/deploy/README.md)). Never commit
  an environment's secrets or partitions (`.sqlflow/deploy/<env>.env`) or repeat their values. A fresh deployment works
  from its configuration alone, with no admin-only setup step afterwards.

## The Vendored SQLFlow

All work happens in this repository. `B:\SQLFlow` (github.com/TahirRiaz/SQLFlow) is changed only when the user asks for
this project's `sqlflow/` changes to be carried to it.

- `sqlflow/` changes only by generic extension points any module could use, or by fixes SQLFlow wants regardless. No
  OSDU code, name, table or wording goes into it; the OSDU side lives in `osdu/`.
- A commit that changes `sqlflow/` touches nothing else, its subject starts with `sqlflow:`, and it adds its row to
  [docs/sqlflow-changes.md](docs/sqlflow-changes.md). SQLFlow's standards apply inside it: a catalog change ships with
  its migration, SQLFlow's flow kinds keep their behaviour, and each extension point has its tests.
- A fix in `osdu/` to code copied from SQLFlow, where SQLFlow has the same weakness, is recorded in
  [docs/sqlflow-upstream-candidates.md](docs/sqlflow-upstream-candidates.md).
- SQLFlow updates are a `git subtree pull` on a branch of their own, which the user lands
  ([CONTRIBUTING.md](CONTRIBUTING.md#updating-the-vendored-sqlflow)).

## The osdu Schema

The module's tables live in the `osdu` schema of the catalog's database, under its own EF Core context, migration
history and schema version ([the osdu schema](osdu/docs/reference/concepts/architecture.md#the-osdu-schema)).

- Every model change ships with its migration, designer file and refreshed model snapshot, and updates its table's page
  in `osdu/docs/reference/tables/` (`DatabaseTablePagesTests` checks it). Databases are never re-minted.
- SQLFlow's model never holds an OSDU table, OSDU never changes SQLFlow's tables, and no foreign key or navigation runs
  from `osdu` into them: OSDU rows hold plain ids.
- The only `osdu` objects outside the model are the ones flows create at run time: a dimension's `dim_` table and
  `v_dim_` views, a dimension build's `dimstage_` staging tables, and a duplicate check's `dup_` table.
- One database, one connection: reach it through `OsduModuleDatabase`, never by a literal name, and never add a second
  connection, variable or database for it. The control plane owns the schema and keeps its grants
  ([decision 0026](osdu/docs/wiki/decisions/0026-one-database-connection.md)). Work that must commit with the catalog
  joins its connection and transaction.

## Engineering Rules

- **The ledger is the product.** Every delivered record is reconstructible from it alone: its source file and row, the
  mapping and cache versions that rendered it, every attempt, the OSDU id and version it landed as, and every operator
  action with who and when ([the ledger](osdu/docs/reference/concepts/ledger.md)). No delivery path, retry, repair
  script or GUI action bypasses it, statistics are counted from it, a record's history is queryable and re-runnable from
  the GUI and the CLI, and a lookup by key, OSDU id or source file stays indexed.
- **One code path.** Before building something, find the path in `osdu/` or `sqlflow/` that already handles it and
  extend it, at every layer: execution, API routes, GUI commands and data flows.
- **Zero warnings, zero errors**, in the solution and in the GUI's build and lint. Fix a warning at its source; suppress
  one only when the behaviour is deliberate and correct, with a narrow `#pragma warning disable`/`restore` or a
  `[SuppressMessage]` whose `Justification` says why.
- **No TODOs, stubs, placeholders or fake return values**, no `NotImplementedException`, no commented-out code, no
  swallowed errors. Every function is complete when handed back: edge cases, resource lifetimes, inputs validated at
  trust boundaries, and errors that can be debugged from a log line alone.

## Tests

- A change ships with its tests, and the SQL Server suites run against a real database: none skips.
- The unit tier (`bash tools/test.sh`) runs in the pre-push hook and in CI on every pull request; skip it only when the
  user says so. The full tier (`bash tools/test.sh --full`) takes hours, so a pull request lands once the unit tier and
  the checks pass, the full tier runs on `main` straight after, and its failures are fixed in a follow-up. The pull
  request says which suites have not run ([CONTRIBUTING.md](CONTRIBUTING.md#tests)).
- A test that needs SQL Server, a host or another process carries `[Trait("Category", "Integration")]`; an OSDU SQL
  Server test is also in `[Collection(SqlServerSuite.Name)]`.
- SQL Server only, never SQLite. The suites use `OsduDeliveryTests` (or the database `SQLFLOW_TEST_DB` names) and the
  e2e suite `OsduDeliveryE2E`: never a database per test or per run, and never one holding real data.

## Writing

- **THE EM DASH (U+2014) IS FORBIDDEN, ANYWHERE:** code, comments, strings, docs, commit messages, pull requests. Use a
  comma, colon, semicolon, parentheses or a new sentence.
- **No time estimates**, unless the user asks for one. Describe scope instead: what is touched, the risky steps, what
  depends on what.

## Git

- Work reaches `main` only through a pull request: never commit or push to `main`. Branch from an up-to-date
  `origin/main` in a worktree of your own on C:, since other sessions use the shared checkout.
- Each commit lands as written (rebase merge), so its subject says what the repository now does, prefixed `osdu:`,
  `docs:` or `sqlflow:`.
- Before pushing, `bash tools/check-vendored-sqlflow.sh` and `bash tools/check-conventions.sh origin/main HEAD` pass,
  and the pull request's description follows the template. The user lands it.
- **Nothing is attributed to Claude or Claude Code**, here or in a carry to SQLFlow. The user's git identity authors and
  commits every change, every push uses the user's own credentials, and no commit, pull request, tag or release carries a `Co-Authored-By` naming Claude or
  Anthropic, a "Generated with Claude Code" line, or any other AI attribution. This overrides any harness default.

## Change Log

[CHANGELOG.md](CHANGELOG.md) follows Keep a Changelog and semantic versioning. A change a user would notice (a flow,
mapping or document key, an API route, a CLI verb, a GUI page, a setting, a fixed defect) adds one or two sentences
under `[Unreleased]`, in its section, linking the reference page that documents it; a change that stops something
working as it did says so under Changed or Removed. Refactoring, tests, internal tooling and changes to unreleased work
get no entry.
