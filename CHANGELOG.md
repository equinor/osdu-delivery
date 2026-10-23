# Changelog

All notable changes to OSDU Delivery are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Records a source no longer holds are taken out of OSDU by a removal flow.** A `flowType: removal` document reads the
  keys a source holds now, whole, from its files or a table, and optionally a list of the keys it removed, compares them
  with a delivery flow's ledger on a schedule of its own, and takes out of OSDU (reversibly) each record whose key is gone,
  once, recording it in that flow's ledger and keeping it out until a newer row arrives. A run that finds more gone than
  its guard allows takes nothing out and waits for an approval on the new **Removals** page, where every deletion is listed
  and can be delivered again; a delivery flow's **Deletions** tab, a record's page, `sqlflow removal` and
  `sqlflow records removed` and `lift-removal` show the same ([Removal flow](osdu/docs/reference/flow/removal.md)).
- **Records whose business keys OSDU already holds are adopted in one batch.** When the ids a flow's rows make from their
  keys are records OSDU already holds that no ledger claimed, written by an earlier deployment or another instance of the
  delivery, the records are held, and **Adopt (N)** on the flow's Delivery tab asks every one of them, records held so
  before this release included, once the partition is typed back; the next run takes each over at the version OSDU holds
  and writes the row there, so the ledger maintains it from then on, and the Records tab's selection, the Issues tab, a
  record's page and `sqlflow records adopt` ask fewer
  ([Adopting a record OSDU already holds](osdu/docs/reference/concepts/record-lifecycle.md#adopting-a-record-osdu-already-holds)).
- **A private GitHub repository can be read with a GitHub App instead of a personal access token.** Give the
  deployment a read-only App (`SQLFLOW_GITHUB_APP_ID` with its private key, or `github_app` in the Terraform and
  `githubApp` in the Bicep), or name one in a source's credential reference (`${githubapp:...}`), and every sync and
  checkout asks it for a short-lived token for that repository alone, so no token is stored and none belongs to a person
  ([Reading a private GitHub repository with a GitHub App](sqlflow/docs/reference/guides/github-app.md)).
- **The Repos page says which credential each repository is read with, and tests it.** A mark beside each remote, the
  repository page and the register dialog show whether a source reads with its own token, a GitHub App, the host's
  token or nothing; Git credentials shows the deployment's App and checks it with GitHub; Test access reads the branch
  tip with the credential a sync would use ([Git credentials](sqlflow/docs/reference/concepts/control-plane.md#git-credentials-a-reference-never-a-secret)).
- **A custom template can be linked to the canonical kind it stands beside.** A template file naming a record kind in
  `x-osdu-inheriting-from-kind` is linked to it: each of its records has a parent, the record of that kind with the same
  minted id, and the sync, the Templates page, the API (`parent`) and the MCP server's template tool say which
  ([Linked templates](osdu/docs/reference/concepts/templates.md#linked-templates)).
- **A mapping reads sections of its records' parent with `$parent`.** A record of a linked template takes any section of
  its canonical parent from OSDU as it renders, such as the wellbore DDMS's references to a well log's curve files
  (`$parent: data.DDMSDatasets`), so a custom record delivered through storage points at the same bulk data; a record
  whose parent is not in OSDU is held, and is released and sent once the parent lands. The ledger keeps each record's
  parent and the version of it every document was rendered from, the record page links a record and its parent both
  ways, and the mapping builder offers the parent as an input ([Mapping $parent](osdu/docs/reference/flow/mapping-parent.md)).
- **Every table of the osdu schema has a reference page.** Each says what one row is, what every column holds, what
  each key and index serves and what writes and reads the table, and
  [The osdu schema](osdu/docs/reference/concepts/database.md) lists them by what they serve; the MCP server's
  `search_docs` searches them when its `type` is `table`.
- **A kind of the OSDU data definitions downloads as a template file.** **Download** on the Templates page's Browse
  OSDU tab, on a kind's row or in its sheet (`GET /api/v1/delivery/templates/osdu/file`), gives the kind's bundled schema,
  naming its kind, as the file a template folder keeps, under the kind's file name; **Download** in a repository
  template's sheet copies one a repository holds. `sqlflow template capture --out` writes the same form, with the keys in
  the order the schema was written rather than sorted ([Templates](osdu/docs/reference/concepts/templates.md#a-template-as-a-file)).
- **A repository's template folders hold the templates its documents fill.** A folder named `templates`, anywhere in a
  repository and as many as it needs, holds the templates of the mappings, searches, assertion tests and dimensions
  beside and below it, read nearest first, so two projects of one repository can fill different versions of one kind.
  A repository adds and removes a template by committing and deleting its file; its sync records which file holds which
  template, and every reader reads the file ([Template folders](osdu/docs/reference/concepts/templates.md#template-folders)).
- **A mapping can name its template's kind alone.** Leave `template.version`, or a search's `schema.version`, out and the
  mapping fills the one version of the kind the nearest template folder holding it holds; a folder holding two versions of
  the kind asks for the version ([How documents name their template](osdu/docs/reference/concepts/templates.md#how-documents-name-their-template)).
- **A template of one's own is used as it is committed.** `sqlflow template import <file> --kind <kind> --out
  <folder>/<file>.json` writes the schema of any record kind into a template folder, naming its kind, and the next sync
  uses it with no step in the GUI; `capture` and `import` need no database
  ([Templates of one's own](osdu/docs/reference/concepts/templates.md#templates-of-ones-own)).
- **`sqlflow template folders`** lists a working copy's template folders, and with `--for <document>` the templates a
  document reads and what a kind named alone settles to, with no database
  ([sqlflow template](osdu/docs/reference/cli/template.md#folders)).
- **The template a repository's document reads, from the API.** `GET /api/v1/delivery/templates/resolve` answers the
  template a document of a synced repository reads, or why none
  ([API](osdu/docs/reference/concepts/api.md#mappings-templates-and-the-mapping-builder)).

- **The sample is a complete package.** Beside its well logs, the welldb sample delivers the wells and wellbores they
  are logged in, each wellbore's directional survey (its stations to the Wellbore DDMS) and formation tops, and every
  OSDU reference value its records refer to, OSDU's own published values and the well database's own curve types, so it
  fills an empty partition with records whose references all resolve. Lineage orders it: the reference data, which puts
  what it delivers into the partition's cache as it runs, then wells, wellbores, logs and surveys, which bring the cache
  types they read up to date before they render. The reference values and the
  order are explained on [The OSDU order](osdu/docs/reference/concepts/osdu-order.md) and in the
  [sample](osdu/samples/welldb). The sample also reads OSDU back with every other kind of flow: assertions of the wells
  (their mapping's own `$assert` rule and known values), dimensions and a view over the logs, a weekly inventory, and a
  retrieval of the wellbores.
- **Lineage orders delivery flows by what their records refer to.** A delivery flow runs after the flows of its
  repository delivering the entity types its records refer to, as OSDU's schemas declare them, however its mapping writes
  the reference (a column, a literal, the cache or a search), and each repository sync warns where its waves cannot keep
  that order ([The OSDU order](osdu/docs/reference/concepts/osdu-order.md)).
- **The Order page.** Under OSDU, Build: which schedules run best before which, with how their next fires fall and the
  schedules that refer to each other in a circle; a partition's order level by level; and OSDU's lineage of any release
  of the OSDU data definitions, built on demand. The same is served under `/api/v1/delivery/order`
  ([The OSDU order](osdu/docs/reference/concepts/osdu-order.md#in-the-gui-and-the-api)).
- **A pipeline updates the cache before and after it runs.** A delivery flow's `cache` block names, by cache type or by
  OSDU entity type, what must be current before it renders (`before`) and what it brings up to date as it ends (`after`),
  so the pipelines after it in a schedule render against what it delivered; the run page shows what each step did
  ([cache](osdu/docs/reference/flow/delivery.md#cache)).
- **A record held for a missing reference comes back by itself.** Once the cache holds the record its reference names, by
  a refresh or a pipeline's cache step, the held record is released and planned again, a record delivered before
  included, under an activity naming the cache version
  ([The partition cache](osdu/docs/reference/concepts/partition-cache.md#a-record-held-for-a-reference-waits-for-it)).
- **A cache run can refresh some of its types.** Pick them in the run dialog, or name them in the payload's `types`; the
  rest of the partition's cache stays as it is ([Cache flow](osdu/docs/reference/flow/cache.md#operations-and-the-run-payload)).
- **Select an inventory's ids by what OSDU holds.** On an inventory's report, Filter takes the explorer's conditions on
  the records (a property, or a column of a source system): a match run of the inventory flow finds the ids that hold
  them, each tab counts its matching ids, the grid lists them with their values, and every matching orphan, stale or
  forgotten id can be removed at once, however many
  ([Selecting ids by what OSDU holds](osdu/docs/reference/flow/inventory.md#selecting-ids-by-what-osdu-holds)).
- **The OSDU menu's sections fold, and the menu starts folded.** A click on Ledger, In OSDU, Build or Setup in the side
  bar folds its entries away or brings them back. A browser opening the workbench for the first time shows every group
  and section folded to its name; from then on each starts as it was left
  ([The OSDU menu](osdu/docs/reference/concepts/gui.md#the-osdu-menu)).

### Changed

- **A source's Interfaces table fits its panel instead of scrolling sideways.** Why an interface goes by its route is on
  the route's hover, with a copy beside it; the kind takes the width the other columns leave, clipped from the left with
  the whole kind on hover and a copy beside it; and a long mapping name or list of interfaces waited for clips with the
  whole on hover ([A delivery flow's page](osdu/docs/reference/concepts/gui.md#a-delivery-flows-page)).
- **A verify no longer calls a newer version drift when it holds the content the flow wrote.** Every write through the
  storage route records the hash of what it wrote, so a record another writer sent again unchanged, such as another
  instance of the delivery linked to it, is a match; records written before this change compare versions alone until
  their next write ([routes, storage](osdu/docs/reference/flow/routes.md#storage)).
- **The sign-in page says what OSDU Delivery is for, and is set on the brand's navy.** In place of four paragraphs of
  features, it shows the README's pitch in two lines and one sentence, a picture of the kinds of data it delivers
  (wells, logs, seismic, documents, production data and more) flowing into OSDU, the "powered by SQLFlow" lockup, and the
  sign-in form, in both themes ([The GUI](osdu/docs/reference/concepts/gui.md)).
- **The side bar's menu is a tree.** OSDU and the platform's groups are branches, each OSDU section a branch under its
  group and each entry a leaf under its section, every level indented under the one above with a guide line, and a
  group's and a section's name the same size as its entries. Tab enters the menu at the page shown, the arrow keys move
  through it and fold or unfold a branch, and Enter opens a page
  ([The OSDU menu](osdu/docs/reference/concepts/gui.md#the-osdu-menu)).
- **The explorer reads a partition through the partition's own connection, and needs no flow.** It signs in with the
  platform and client the deployment names for the partition (`OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`,
  `OSDU_CLIENT_ID` and `OSDU_CLIENT_SECRET`, from `Osdu:Partitions:<id>:Properties` or the control plane's environment,
  naming any that is missing), so a deployment can be searched before a flow is synced;
  `GET /api/v1/delivery/explorer/connection` answers the `endpoint` it reaches in place of the borrowed flow's `through`
  and `route` ([The explorer](osdu/docs/reference/concepts/explorer.md#how-it-reaches-osdu)).
- **`sqlflow dev setup` offers a GitHub App for a GitHub repository before a token.** It asks for the App's client ID
  and the path of its `.pem` file, and `sqlflow dev check` reports an App that is only half configured; Enter at the new
  question keeps the token as before ([dev](osdu/docs/reference/cli/dev.md)).
- **The compose stacks give the control plane the git credentials too.** A repository registered there with no
  credential reference now syncs with `SQLFLOW_GITHUB_APP_*` or `SQLFLOW_GIT_TOKEN` from `.env`, as the worker already
  checked out with them ([deployment](osdu/docs/reference/guides/deployment.md)).
- **Documents read their templates from the repository, not from the saved templates.** A mapping, its searches, an
  assertion test and a dimension read the template they fill from the template folders on their path, and a run,
  `sqlflow check`, lineage and the sync do too; a mapping whose template no folder on its path holds no longer loads,
  and the sync says where it looked, and a record rendered for a version no folder holds any longer is `unverified`.
  For an estate whose templates were saved in the module's
  database, make each version its documents pin a file in a template folder before upgrading: download a version a
  release publishes on Browse OSDU, or write it with `sqlflow template capture --release <tag> --out <folder>/<file>.json`;
  read one of your own from the running control plane with `GET /api/v1/delivery/templates/schema?kind=&version=` and
  write it with `sqlflow template import <file> --kind <kind> --out <folder>/<file>.json`; every pin keeps its version
  ([Upgrading an estate](osdu/docs/reference/concepts/templates.md#upgrading-an-estate-that-saved-templates-in-the-database)).
- **The Templates page lists the templates the synced repositories hold.** Its Repositories tab lists every file of
  every synced repository's template folders, read from the file, in place of the saved versions; a link naming a kind
  and version opens the file holding it. `GET /api/v1/delivery/templates` answers those files, and
  `GET /api/v1/delivery/templates/detail` and `/schema` name one by `repoId` and `path` instead of `kind` and `version`
  ([The GUI](osdu/docs/reference/concepts/gui.md#templates), [API](osdu/docs/reference/concepts/api.md#mappings-templates-and-the-mapping-builder)).
- **The mapping builder starts from a template a repository holds.** It offers the files of the picked repository's
  template folders and proposes the mapping alone, into the `mappings` folder beside the one holding its template;
  `POST /api/v1/delivery/mapping-builder/draft` takes `repoId` and `templatePath` instead of `kind` and `version`
  ([The GUI](osdu/docs/reference/concepts/gui.md#mapping-builder)).
- **The explorer checks a record against a repository's template.** Validation's **Saved template** is now
  **Repository template**: `schema` takes `template` instead of `saved` on the validate routes, and an answer lists the
  versions the repositories hold in `templateVersions` instead of `savedVersions`
  ([Explorer](osdu/docs/reference/concepts/explorer.md#validate)).
- **The MCP server's template tools read the repositories.** `delivery_templates` lists the template files and lays one
  out by `repoId` and `path` instead of `kind` and `version`, and `delivery_scaffold_mapping` takes `repoId` and
  `templatePath` ([The MCP server](osdu/docs/reference/guides/mcp.md)).
- **`sqlflow template capture` and `import` require `--out`.** A template is a file, so both write one and save nothing
  in a database ([sqlflow template](osdu/docs/reference/cli/template.md)).
- **The decision records and the pattern catalog are pages of the wiki.** They moved to
  [osdu/docs/wiki](osdu/docs/wiki/index.md), in SQLFlow's wiki format, and the MCP server knows them by new ids:
  `delivery-wiki-0014-inventory-removals` for what was `delivery-decision-0014`, and `delivery-wiki-pattern-catalog` for
  `delivery-guide-pattern-catalog`; an old id finds nothing, so search for the page instead.
- **A repository's waves can change at its next sync.** Flows that referred to each other only by ids written from
  columns or literals now run in the order of what they refer to, and a delivery checked against a cache whose flow also
  captures what it delivers now runs before that cache flow rather than unordered against it. Schedules keep their
  members; their fires run them in the new waves ([Lineage](osdu/docs/reference/concepts/lineage.md#how-the-nodes-order-flows)).

- **The sample estate's lookup tables and reference records cover the sample's own curves.** The welldb curve
  dictionary lists the mnemonics the five sample logs carry and one spelling filed under another, the unit table the
  spellings those curves use, and the sample reference records the units, curve types and families they name, so the
  example reads as one small, consistent set ([Sample cache records](osdu/samples/cache-records/README.md)).
- **A Radix environment is complete in its configuration.** `radixconfig.yaml` names the workers' credential (the
  vault's `node-token`, generated before the first deploy in SQLFlow's token form) and the flow repository, as the
  Terraform estate does, so the control plane adopts the one and registers the other when it starts, and nothing is
  minted or registered by hand after the first deploy; `git-token` is read as a reference by both tiers rather than
  mounted. An environment deployed before this mints nothing new: store its token as `node-token` and add the
  repository's settings ([Setting up a Radix environment](osdu/docs/reference/guides/radix-setup.md)).

- **The Tests page is the Assertions page.** The GUI calls each test of an assertion flow an assertion, as the flow
  type names them, and each of its assertions a check: the page, a flow's tab, the board, the sheet, the run dialog
  (whose operation reads **Check**) and the reports say so. Flow documents, the API and the CLI keep `tests:`,
  `assert:` and the `test` operation ([The GUI](osdu/docs/reference/concepts/gui.md#in-osdu)).
- **The board says what an assertion's document says now.** A disabled assertion shows its reason, one whose proof
  values are past their day shows that it needs new proof values, the Definition tab says where the schema comes from
  and how long the proof values hold, and a skipped result no longer reads as an error
  ([The GUI](osdu/docs/reference/concepts/gui.md#in-osdu)).
- **A local start runs its work in workers, as a deployment does.** `dev.bat` and `./dev.sh` start the control plane
  with managed workers: worker processes it starts for queued runs and lets leave when idle, so Spawn, Always on and the
  target act on the Nodes page here too. `dev.bat single` (`./dev.sh single`) is the one-process start that ran by
  default before, and `managed` still names the default
  ([Managed workers on a developer's machine](osdu/docs/reference/guides/managed-workers.md#on-a-developers-machine-devbat)).
- **The Nodes page answers one question per tab.** A line of four facts (workers online, runs running, runs queued and
  how many could start, how workers start) opens on Workers (the nodes), Scaling (the pools and their controls, and the
  managed workers), Queue (what runs, what is queued and the gate holding it) and, with managed workers on, Activity
  (what the control plane started and stopped, and why); explanations sit behind an info mark instead of paragraphs
  ([Managed workers](osdu/docs/reference/guides/managed-workers.md#watching-it)).
- **The Nodes page says when nothing starts workers.** With managed workers off and the control plane running runs on
  its own node (`dev.bat single`, F5), Worker pools names that node and shows a pool short of its target as below target
  rather than starting, and Spawn and Always on say they only record a target
  ([The control plane](sqlflow/docs/reference/concepts/control-plane.md)).

### Added

- **Proof values of records found by what they hold.** An assertion flow's `knownValues` finds each record a sheet
  describes by full paths in the OSDU record and values, including lists of paths up to the record a reference names
  (`'[data.WellboreID, data.FacilityName]'`, as a dimension's label reads) and down to the records naming one, then
  holds it to the sheet's proof values,
  saying row by row which hold, which differ, which are not found and which are found twice
  ([Known values](osdu/docs/reference/flow/known-values.md)).
- **Tests switched off, and proof values that hold for a while.** A test, an assertion, a row of known values or a proof
  value takes `disabled: <why>` and is reported as skipped with its reason. A test or an assertion says when its values
  were stated (`asOf`) and how long they hold (`validFor: 90d`, or `validUntil`); past that it is not judged and is
  flagged as needing new proof values, a warning on the board. `asOfField: createTime` compares only the records OSDU had
  created by that day
  ([Disabling a test](osdu/docs/reference/flow/assertion.md#disabling-a-test-and-the-day-its-values-hold-until)).
- **Numbers a little off warn rather than fail.** A `count`, an `aggregate` or a known value's proof value takes
  `warnWithin: 10%` (or a distance, `warnWithin: 5`): a number that misses by no more is a warning, so its test is warned
  rather than failed, and the result says by how much it missed
  ([A warning band](osdu/docs/reference/flow/assertion.md#a-warning-band)).
- **A test names its template by the mapping.** `template: WellLog@1.5.0` checks a test against the template that
  mapping fills, so a test reads as the delivery does and follows the mapping when it moves to another version; a
  template version (`template: 2f8a99cb38d32480`) is taken as before ([Tests](osdu/docs/reference/flow/assertion.md#tests)).
- **Aggregates that leave values out.** An aggregate's `except` lists values it does not count, such as a log's index
  curve `MD` among its curves ([Assertions](osdu/docs/reference/flow/assertion.md#assertions)).

### Removed

- **The library of saved templates.** The Templates page's Saved and Import file tabs, **Save template** and **Delete**,
  `POST` and `DELETE /api/v1/delivery/templates`, and `sqlflow template list`, `show` and `delete` are gone, and the
  upgrade's migration drops `[osdu].[Template]` with every version saved in it. Make each version an estate's documents
  pin a file in a template folder before upgrading, as **Documents read their templates from the repository** above
  says ([Upgrading an estate](osdu/docs/reference/concepts/templates.md#upgrading-an-estate-that-saved-templates-in-the-database)).

### Fixed

- **A write OSDU skips as unchanged lands at the version OSDU holds.** With `skipDuplicates`, the ledger recorded the
  version it last knew for a skipped record, which another writer may have moved, and recorded a skipped record OSDU held
  soft-deleted as delivered while OSDU served nothing; the route now reads the version, and brings such a record back
  under its id ([routes, storage](osdu/docs/reference/flow/routes.md#storage)).
- **The audit trail narrows to inventory repairs and new OSDU ids.** Its action filter now offers `inventory-repair` and
  `reassign-id`, which the trail records, beside the new `adopt` ([Audit trail](osdu/docs/reference/concepts/gui.md#audit-trail)).
- **Shift+Tab leaves a tree.** In the catalog, a repository's folders, the AI context tables and a template's
  variables, Shift+Tab from a row sent focus straight back into the tree, so the keyboard could not leave it backwards;
  it now steps out, and the schema changes tree can be reached with Tab at all
  ([The OSDU menu](osdu/docs/reference/concepts/gui.md#the-osdu-menu)).
- **Runs that finish together no longer deadlock in the catalog.** When a schedule's members ended at once, as a set
  of failing assertion flows does, each completion waited on the others and the control plane's log filled with failed
  commands and deadlock victims. Every read on a connection a catalog or ledger write had used also ran serializable,
  so the dispatcher's checks and the GUI's reads became victims too. Each connection now goes back to the pool at read
  committed, and a completion writes what follows from it once it has committed
  ([The control plane](sqlflow/docs/reference/concepts/control-plane.md#the-dispatcher)).
- **A page left while it loads is not logged as a failure.** A request the browser abandoned while its database read
  ran was logged as three errors, the first "A severe error occurred on the current command"; it now ends as the
  client's cancellation, logged at debug ([The control plane](sqlflow/docs/reference/concepts/control-plane.md)).
- **An inventory's grid keeps its footer in view when ids are picked.** The bar that says what is picked made the grid
  taller than the window, so its last rows and its footer went below the page's edge; the grid now shrinks to make room.
- **A cache flow's run dialog no longer offers Force,** which a cache run refuses: a refresh captures every type it is
  given in full ([Cache flow](osdu/docs/reference/flow/cache.md#operations-and-the-run-payload)).
- **A Radix environment delivers every flow.** The Radix application set only the LogType flow's access groups, so
  every other delivery flow, which reads `OSDU_ACL_OWNER` and `OSDU_ACL_VIEWER` unless it sets its own, could not
  resolve its groups; each partition now sets both, and the setup guide's SQL creates the `osdu` schema before it grants
  the worker's rights on it ([Setting up a Radix environment](osdu/docs/reference/guides/radix-setup.md)).

- **The Nodes page opens while managed workers run.** Each worker's state reached the page as a number rather than its
  name, so the page went blank as soon as a managed worker was listed, locally and on Radix; it now shows each worker
  as starting, ready, leaving and so on ([Managed workers](osdu/docs/reference/guides/managed-workers.md#watching-it)).
- **Local managed workers start when the osdu schema is in the catalog's database.** A setup with no `SQLFLOW_OSDU_DB`
  of its own, as `.sqlflow/env` usually is, started workers that exited at once ("exited with code 1 before its first
  poll"); each worker is now handed the module database the control plane uses, and no setting needs adding
  ([Managed workers on a developer's machine](osdu/docs/reference/guides/managed-workers.md#on-a-developers-machine-devbat)).
- **A tolerance is the number written.** A `tolerance` with few decimals, such as `0.0005`, was read as
  0.000500000024, which let a value a hair further off hold and showed that number in reports; it is now compared and
  shown as written. A test with such a tolerance is shown as changed once, until it runs again
  ([Conditions](osdu/docs/reference/flow/assertion.md#conditions)).
- **A worker pool asks only for workers that would get a run.** A schedule fire's members beyond its `maxConcurrency`,
  and a second queued run of a flow, counted as demand until the first started, so Azure and Kubernetes started workers
  that found nothing to run and were scaled away again. The target now counts what the pool's workers would start, and
  the Nodes page marks the members waiting their turn as at the group cap
  ([Deployment](sqlflow/docs/reference/guides/deployment.md)).

### Security

- **A GitHub token quoted in an error is redacted before it is logged or shown,** in every format GitHub issues, and a
  resolved git credential never prints its secret.
- **An unzip cannot write outside its target.** An archive names its entries itself, and on an Azure Blob or S3 target
  an entry such as `../x` landed beside the target folder. A copy flow now fails with `escapes the target root` on any
  target when an archive entry, or a source object's key, would leave it
  ([Copy flow](sqlflow/docs/reference/flow/cpy.md#operations)).
- **No dependency with a known advisory.** System.Security.Cryptography.Pkcs resolves at 10.0.13, past the .NET denial
  of service advisories CVE-2026-84008 and CVE-2026-70162, and the GUI's dompurify at 3.4.17, past its two DOM XSS
  advisories.

## [1.0.1] - 2026-10-19

Module schema 1.46.0 and SQLFlow `fe87877c`, as in 1.0.0; the one change to `sqlflow/` since is to its tests
([docs/sqlflow-changes.md](docs/sqlflow-changes.md)).

### Changed

- **A local start installs the pre-push tests.** In a git clone with no hooks path set, a start of `dev.bat` or
  `./dev.sh` points git at `tools/git-hooks`, whose pre-push runs the unit tests (`bash tools/test.sh`) before every
  push; `git push --no-verify` skips it once and `git config --unset core.hooksPath` removes it
  ([CONTRIBUTING.md](CONTRIBUTING.md#tests)).
- **A start without .NET offers to install it, on every platform.** `dev.bat` offers winget, and `./dev.sh`
  Homebrew on a Mac and Microsoft's `dotnet-install.sh` into `~/.dotnet` on Linux; then the setup is built from the
  checkout as on any start ([osdu-delivery-setup](osdu/docs/reference/cli/dev.md)).

### Removed

- **The setup's binaries.** A release no longer carries `osdu-delivery-setup` for each platform, and the launchers
  no longer download one: they build it from the checkout, in seconds, once .NET is there. A release is its tag and
  its notes ([CONTRIBUTING.md](CONTRIBUTING.md#releases)).

## [1.0.0] - 2026-10-18

The first release of OSDU Delivery, powered by SQLFlow: subsurface data delivered into an OSDU platform by
configuration rather than code, with every record traceable in a ledger. The [README](README.md) says what it is and the
[reference documentation](osdu/docs/reference/README.md) describes what it does. This log starts at this release.

### Added

- **Delivery as configuration.** A pre-ingestion flow lands source files, an ingestion flow loads them into a table
  keyed by record with the source file and row of each row, and an OSDU flow delivers from that table; lineage orders
  the three. A mapping turns one row into one record of a saved OSDU template, with lookups, expressions, conditions and
  business rules ([flow/mapping.md](osdu/docs/reference/flow/mapping.md)).
- **Every OSDU write path.** The Storage, File and Dataset services, manifest and workflow ingestion, and the Wellbore,
  Seismic, Rock and Fluid Samples, Well Delivery, Production and Reservoir domain data stores, with their files and bulk
  data ([docs/osdu-coverage-plan.md](docs/osdu-coverage-plan.md)).
- **A cache per data partition.** Reference data captured from OSDU, lookup tables from files, databases and ingestion
  tables, and dictionaries kept in the repository, held as versions that mappings read
  ([concepts/partition-cache.md](osdu/docs/reference/concepts/partition-cache.md)).
- **Only what changed is sent.** The same source row always becomes the same OSDU record, a run sends only the records
  that render differently, and a change of mapping, template or cache sends exactly the records it affects
  ([concepts/change-detection.md](osdu/docs/reference/concepts/change-detection.md)).
- **The delivery ledger.** Each record's source row, the mapping, template and cache versions that rendered it, every
  attempt and what OSDU answered, the OSDU id and version it landed as, and every operator action with who and when;
  statistics count from it, and a record is found by its key, OSDU id or source file
  ([concepts/ledger.md](osdu/docs/reference/concepts/ledger.md)).
- **Checked before sending.** `sqlflow check`, `preview` and `plan`; mappings are checked against their schema, legal
  tags are confirmed with OSDU, and each document is validated before it goes out, its verdict recorded.
- **Safe when things fail, and correctable.** A run resumes from its last recorded step, problems that need a person are
  held for release, and a delivery that fails halfway is undone. Records are redelivered, removed, restored to their
  previous version, or a whole run reversed, each through the ledger
  ([concepts/removal-and-reversal.md](osdu/docs/reference/concepts/removal-and-reversal.md)).
- **After delivery.** Verify and reconcile against OSDU, inventory flows that find orphans and records OSDU lost,
  retrieval of OSDU records into files, dimension flows, data quality tests, and an explorer of what OSDU holds.
- **The GUI and the command line.** The workbench pages for records, flows, the cache, mappings, tests and the audit
  trail, a mapping builder and a YAML editor that documents and checks every document, and `sqlflow` verbs for the same.
- **A local run set up by answering questions.** `dev.bat` (Windows) and `./dev.sh` (macOS, Linux) set a machine up on
  its first start: `osdu-delivery-setup` asks for the SQL Server (starting SQL Server 2022 in Docker where there is
  none), the OSDU platform and its client, and a flow repository, writes `.sqlflow/env` from `dev.env.example`, creates
  the databases and registers the partition. Every start then reports each tool, setting and database with its fix,
  and stops only when a start would fail. The release carries the setup for Windows, macOS and Linux, so a machine
  without .NET runs it too and is offered the install ([Run it locally](README.md#run-it-locally),
  [osdu-delivery-setup](osdu/docs/reference/cli/dev.md)).
- **An AI assistant.** `osdu-delivery-mcp` serves Claude, Cursor, VS Code, Codex, Slack and the GUI's chat from metadata
  and documentation, and answers questions over data only through AI contexts an admin approves
  ([guides/mcp.md](osdu/docs/reference/guides/mcp.md)).
- **Deployment.** A control plane, workers that fetch their work, the GUI and the MCP server, over two SQL Server
  databases, with Docker Compose, Kubernetes, Bicep, Terraform and Radix templates; dev, test and prod environments
  deployed from GitHub, each with its OSDU partitions in its configuration
  ([osdu/deploy/README.md](osdu/deploy/README.md)).
- **Ready to use when a deployment ends.** The Terraform (and its Bicep mirror) sets the whole Azure estate up from its
  configuration alone: the deployment generates the JWT signing key, the initial admin's password and the node
  credential into the vault, creates the apps' database users, and the control plane applies the partitions,
  databases, landing storage and flow repositories the configuration names at every start, in a start-up pass reported
  at `/health/startup`. A person puts in the vault only each partition's client secret and each private repository's
  token, and no secret reaches the Terraform. An environment's two files can be kept outside the repository, on a
  workstation (`OSDU_ENVIRONMENT_DIR`) or in its GitHub environment, so the committed ones stay placeholders.
- **Its own database schema.** Every OSDU table lives in the `osdu` schema under its own migrations: module schema
  1.46.0 (migration `20261014094057_PurgedRecordReadAgain`), requiring SQLFlow catalog migration
  `20261014215105_RunSuppliedReferences` or later. The hosts refuse to run against pending migrations.
- **Built on SQLFlow** `fe87877c` ([github.com/TahirRiaz/SQLFlow](https://github.com/TahirRiaz/SQLFlow)),
  vendored in `sqlflow/` with the extension points and fixes this project needed, each recorded with its reason
  ([docs/sqlflow-changes.md](docs/sqlflow-changes.md)). It runs on .NET 9, with every package on its newest release
  for it.

### Security

- Secrets are references only (`${env:NAME}`, `${keyvault:...}`): a document holding a literal secret is refused when it
  is read, and errors are redacted before they are stored or shown.
