# Contributing to OSDU Delivery

Thanks for your interest in contributing! This guide gets you productive quickly.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Node.js 22 or later, for the GUI under `osdu/gui`
- (For the DB-backed suites) a SQL Server instance you can create a throwaway database on: LocalDB, a
  container, or Azure SQL

## Getting started

```bash
git clone <this repository>
cd osdu-delivery
bash tools/test.sh --install-hook   # once, where dev.bat or ./dev.sh has not: the unit tier before every push
bash tools/test.sh                  # the unit tier: a few minutes, no database
bash tools/test.sh --full           # every test, against SQL Server: on main, once a pull request lands
```

`dev.bat` (Windows) and `./dev.sh` (macOS, Linux) run the whole thing locally: on the first start they ask for the
SQL Server, the OSDU platform and a flow repository and write the git-ignored `.sqlflow/env` from `dev.env.example`;
every start after that checks the machine and the settings and says what is missing
([Run it locally](osdu/docs/reference/guides/run-locally.md), [osdu-delivery-setup](osdu/docs/reference/cli/dev.md)). The setup is its
own host, `osdu/hosts/SqlFlow.Delivery.Setup.Host` over `osdu/src/SqlFlow.Delivery.Setup`, which depends on nothing
but `SqlFlow.Core` and the SQL Server client, so it builds in seconds; the command line carries the same code as
`sqlflow dev`. Every start builds it, so keep its dependencies that small.

## Tests

The tests run in two tiers, both through `tools/test.sh` (on Windows from Git Bash, or as `bash tools/test.sh` from
any terminal that has it):

- **The unit tier** (`bash tools/test.sh`) is the build with warnings as errors, every .NET test that needs no SQL
  Server, host or other process, and the GUI's build, lint and unit tests. It takes a few minutes and needs no
  database. It is what CI's **Build and test** runs on every pull request, and what the pre-push hook runs before every
  push that carries commits (`git push --no-verify` skips it once). `dev.bat` and `./dev.sh` install the hook on a
  clone's first start, unless a hooks path is set already.
- **The full tier** (`bash tools/test.sh --full`) is every test: the SQL Server suites, the control plane hosts, the
  MCP server's crates and the GUI end-to-end suite. It takes hours, so a pull request lands once the unit tier and the
  checks pass, the full tier runs on `main` straight after, and its failures are fixed in a follow-up pull request;
  the pull request says which suites have not run.
  CI runs it as **Full test suite** on a release tag, whose release waits for it, on a pull request labelled
  `full-tests`, and when the CI workflow is run by hand (Actions, CI, Run workflow).

- **An on-demand suite** (`bash tools/test.sh --suite <name>`) runs one suite alone, against SQL Server as the full tier
  runs the suites: the tests that carry `[Trait("Suite", "<name>")]`, which both tiers and CI leave out, for edge cases
  too many to run on every change. `DimensionEdgeCases` holds every edge of the dimension engine: its attributes,
  elements, lookups, incremental loads, grouped views and joins to views. Run it after a change to the engine.
  `RunParameters` holds every rule of a flow's `parameters` and run settings for each of the seven kinds: choices and
  lookups, a run's resolution checked against what a run form and a schedule's preview explain, the first records of a
  delivery's scope over every operation and payload, and a schedule's fire against its preview. Run it after a change to
  how a run takes its values. A new suite is named in `on_demand_suites` in `tools/test.sh` and left out in CI's two test
  steps.

A test that needs SQL Server, a host or another process carries `[Trait("Category", "Integration")]`, which puts it in
the full tier. The OSDU module's SQL Server tests are the classes in `[Collection(SqlServerSuite.Name)]`, and a guard
test fails one there without the trait. The unit tier points the suites at a server that does not exist, so any other
test that reaches for a database fails there, and the message names the trait.

The full tier's database is one database on a local SQL Server, `OsduDeliveryTests` under Windows authentication
(created when missing), or the one the `SQLFLOW_TEST_DB` environment variable names, from the environment or from
`.sqlflow/env`; on macOS and Linux it has to be named. A server that does not answer fails the suites. The tests that
use it run one at a time, so the suites never create a database per test or per run, and the end-to-end suite keeps
its estate in `OsduDeliveryE2E` (`SQLFLOW_E2E_CATALOG_DB` names another). Never point either at a database holding
real data: the suites migrate and seed the database they are given.

## Repository layout

| Path | What it is |
| --- | --- |
| `sqlflow/` | SQLFlow, vendored as a squashed git subtree. This project adds only generic extension points to it. |
| `osdu/` | Everything OSDU Delivery adds: the module, its data project, the control plane endpoints, the CLI verbs, the hosts, the GUI, the samples, the tests, the docs and the deployment assets. |
| `tools/check-vendored-sqlflow.sh` | Lists this project's changes to `sqlflow/` and fails when one mixes in other paths or OSDU code. |
| `tools/check-conventions.sh` | Fails when a change adds an em dash outside `sqlflow/` or a commit carries AI attribution. |
| `tools/github/apply-repo-settings.ps1` | Applies the ruleset, merge settings, environments and deploy method the repository runs under. |
| `.github/rulesets/main.json` | The rules `main` is held to, in the form GitHub's ruleset API takes. |
| `docs/sqlflow-changes.md` | Every change this project made to `sqlflow/`, why, and which of them upstream should take. A new `sqlflow:` commit adds its row. |
| `CLAUDE.md` | The project's working rules. Read it before your first change. |

## Development principles

1. **The vendored SQLFlow takes generic extension points only.** A change to `sqlflow/` must be something any
   module could use (a flow kind registry, an executor fallback, run parameters, a module database, a GUI module
   contract, a lineage contributor, branding). No OSDU code, name, table or wording goes into `sqlflow/`. Every
   commit that touches `sqlflow/` touches nothing else and starts its subject with `sqlflow:`. The OSDU work that
   uses an extension point is a separate commit. `bash tools/check-vendored-sqlflow.sh` enforces both and must
   pass before you hand work back.
2. **The OSDU schema owns its own lifecycle.** Every OSDU table, view and index lives in the `osdu` schema, owned
   by the module's EF Core context in `osdu/src/SqlFlow.Delivery.Data`, with its own migration history
   (`[osdu].[__EFMigrationsHistory]`) and its own schema version. **Every model change ships with its migration,
   designer file and refreshed model snapshot in the same commit.** No foreign keys or EF navigations from `osdu`
   into SQLFlow's tables.
3. **Traceability is the product.** Anything that happens to a record goes through the ledger. No delivery path,
   retry, repair script or GUI action bypasses it, and statistics are derived from the ledger rather than counted
   separately.
4. **Secrets are references.** `${env:NAME}` and `${keyvault:vault/secret}` only; a literal secret in a flow, a
   mapping, a catalog row, a log line or a test fixture is a defect.
5. **One code path per feature.** Before adding something, check whether a path in `osdu/` or `sqlflow/` already
   handles it and extend that instead. This applies at every layer: execution, API routes, GUI commands, data flow.

## Coding standards

- Modern C#: nullable reference types, file-scoped namespaces, `record` for data, primary constructors,
  `async`/`CancellationToken` for I/O.
- Style is enforced by `.editorconfig` and analyzers. `dotnet build OsduDelivery.sln -c Release` must be
  warning-clean, and in `osdu/gui` `npm run lint` (ESLint, no warnings allowed), `npm run build` (the type check and
  the bundle) and `npm run test:unit` (the unit tests under `osdu/gui/unit`, of the modules `tsconfig.unit.json` lists)
  must pass. Fix a lint finding at its source; a component file exports only components, so
  a hook, a context, a constant or a variants helper lives in a `.ts` module beside it.
- No TODOs, stubs or placeholders: every change you hand in is finished.
- Add or update tests for behavior changes. A change to an extension point in `sqlflow/` needs tests there too,
  written against a test-only module rather than the OSDU one.

## Dependencies

Package versions are managed centrally, in `osdu/Directory.Packages.props` for the OSDU projects and
`sqlflow/Directory.Packages.props` for the vendored ones; project files carry none of their own.

Dependabot (`.github/dependabot.yml`) opens one grouped pull request a week per ecosystem for what OSDU Delivery pins
itself: the workflows' actions, the OSDU GUI's npm packages, the packages `osdu/Directory.Packages.props` pins, and the
MCP host's Cargo lock file. It never moves anything in `sqlflow/`, whose pins come with a subtree pull, and its pull
requests go through CI and review like any other. Dependabot's security updates are a repository setting this
configuration does not cover; turned on, they would also propose fixes inside `sqlflow/`, and those are taken upstream
into SQLFlow and arrive with a subtree pull instead.

A move Dependabot does not make is a deliberate step someone takes and stands behind:

1. Change the single `PackageVersion` line for the package you mean to move, and nothing else. A bump inside
   `sqlflow/` follows the vendoring rule: its own commit, prefixed `sqlflow:`.
2. Run `dotnet restore OsduDelivery.sln`, then `bash tools/test.sh`, which builds warning-clean and runs the unit
   tier; the full tier runs on `main` once it lands ([Tests](#tests)). A bump across a major version usually asks for source changes; make them in the
   same change, so the history says what the new version needed.
3. Commit the bump on its own, naming what moved and what it cost, and open a pull request for it.

## Updating the vendored SQLFlow

SQLFlow improvements come in as one reviewable commit, on a branch from an up-to-date `main`. `<sqlflow-remote>` is
SQLFlow's repository (github.com/TahirRiaz/SQLFlow, or a clone of it), whose working branch is `master`:

```bash
git switch -c sqlflow-update origin/main
git subtree pull --prefix=sqlflow --squash <sqlflow-remote> master
bash tools/check-vendored-sqlflow.sh
bash tools/test.sh --full
```

Conflicts can only arise in the files this project changed in `sqlflow/` since the last pull
([docs/sqlflow-changes.md](docs/sqlflow-changes.md) lists them); resolve them keeping both SQLFlow's change and this
project's.

Push the branch and open a pull request, so CI runs on it and it is reviewed. A subtree pull is a merge commit, which
neither a rebase merge nor `main`'s linear history admits, so an administrator lands it once CI passes and the review
is in: with the branch still based on the current `main`, `git push origin HEAD:main` fast-forwards `main` to it, and
GitHub marks the pull request merged. If `main` moved in the meantime, start the branch again from the new `main`.

## How work reaches main

`main` takes changes only through pull requests; the ruleset in `.github/rulesets/main.json` holds it to that.

1. Branch from an up-to-date `main` (`git switch -c <topic> origin/main`) and commit as the principles above say: a
   `sqlflow:` commit touches only `sqlflow/`, and each subject says what the repository now does.
2. Before pushing, run the quick checks CI runs: `bash tools/check-vendored-sqlflow.sh` and
   `bash tools/check-conventions.sh origin/main HEAD`. The pre-push hook runs the unit tier on every push; the full
   tier (`bash tools/test.sh --full`) runs on `main` once the pull request lands ([Tests](#tests)).
3. Push the branch and open a pull request into `main`. The template's checklist is the repository's rules; say why
   an item does not apply rather than leaving it unticked without a word.
4. CI must pass on a branch up to date with `main`. The required checks are CI's jobs: **Conventions** (the
   vendored-SQLFlow guard, no em dash added outside `sqlflow/`, no AI attribution in the commits), **Power BI
   extractor**, **Build and test** (the unit tier: the warning-free build, the tests that need no SQL Server or host,
   both GUIs), **Container images** (built only when what the images are built from changes; skipped, it counts as
   passed) and **Terraform checks**. **Full test suite** is not required on a pull request: add the `full-tests` label
   to run it there. A job's name is its check's name, so renaming
   a job in `.github/workflows/ci.yml` changes `.github/rulesets/main.json` in the same pull request.
5. One approving review from someone other than the author, with every conversation resolved. A push after the
   approval dismisses it.
6. The pull request lands with **Rebase and merge**, the only merge method allowed: each commit reaches `main` as
   written, so a `sqlflow:` commit stays apart from the `osdu:` work that uses it. A squash would fold them into one
   commit, which `tools/check-vendored-sqlflow.sh` refuses, and a merge commit would break `main`'s linear history.
   Auto-merge can land it once the checks and the review are in, and the branch is deleted when it lands.

Administrators of the repository may bypass the ruleset, for the two changes that cannot land as above: a subtree pull
of SQLFlow, and an emergency fix that cannot wait for a review. CI runs on the push either way, and an emergency fix
is reviewed after it lands.

## Deployments

When the repository's deploy method is Terraform, merging to `main` deploys to dev once CI passes, and test and prod
are promoted with the Deploy workflow, prod after its environment's reviewers approve. The deploy method is chosen with
the repository variable `DEPLOY_METHOD` (`terraform` today), and no deploy workflow runs for a method that is not
chosen. The other methods (Bicep, Radix, Docker Compose, Kubernetes) run as their guides say; Radix's workflow is a
sample under `osdu/deploy/radix` that a deployer copies into `.github/workflows` to use.
[osdu/deploy/README.md](osdu/deploy/README.md) describes each method.

## Forks

A fork that builds, tests and deploys with workflows of its own leaves this repository's workflows as they are and
turns them off with repository variables: `CI_ENABLED` set to `false` skips every job of CI, and the Deploy workflow
runs only where `DEPLOY_METHOD` is `terraform`, so a fork that leaves it unset deploys nothing.

```bash
gh variable set CI_ENABLED --body false --repo <owner>/<fork>
```

Editing or commenting out `.github/workflows/ci.yml` or `deploy.yml` in the fork instead turns every later change to
them here into a merge conflict when the fork syncs. A fork's own files (its workflows, release configuration) never
conflict. What it changes in files this repository also keeps (a README badge, its `SECURITY.md`, `CODEOWNERS`,
`dependabot.yml`, the ruleset) conflicts only when a change here touches the same lines; keep the fork's side of those.

## Repository settings

The rules above are GitHub settings, applied from the repository by `tools/github/apply-repo-settings.ps1` (Windows
PowerShell 5.1 or PowerShell 7):

```powershell
pwsh -File tools/github/apply-repo-settings.ps1 -ProdReviewers <login or org/team> -DeployMethod terraform -WhatIf
```

It creates or updates the `main` ruleset from `.github/rulesets/main.json`; allows rebase merging only, deletes a
branch when it lands and allows auto-merge; creates the environments `dev`, `test` and `prod`, each deploying from
`main` only and prod after a review by `-ProdReviewers` (users, or `org/team` for a team); and with `-DeployMethod`
sets `DEPLOY_METHOD`. It reads each setting first and changes only what differs, so it can run again at any time, and
`-WhatIf` prints what it would change without changing anything. It works on the repository this clone belongs to,
or on the one `-Repository <owner/name>` names.

Prerequisites: the [gh CLI](https://cli.github.com), signed in as an administrator of the repository, and a plan that
offers what it sets. An organization on GitHub Team or Enterprise has rulesets; a private repository of a personal
account needs GitHub Pro, and on GitHub Free the script reports GitHub's refusal ("Upgrade to GitHub Pro or make this
repository public") and leaves `main` unprotected until the plan allows it. Required reviewers on a private
repository's environment need GitHub Enterprise; without them, prod is still deployed only from `main`, by someone
with write access running the Deploy workflow by hand.

## Releases

A release is a `v*` tag on `main` and its version's section in `CHANGELOG.md`:

1. A pull request moves `[Unreleased]` under `## [X.Y.Z] - <date>`, naming the module schema version and the SQLFlow
   commit (see the 1.0.0 entry), and lands.
2. Tag the landed commit and push the tag: `git tag -a vX.Y.Z -m "OSDU Delivery X.Y.Z" <commit>`, then
   `git push origin vX.Y.Z`. A tag with a suffix (`vX.Y.Z-rc.1`) makes a pre-release with `X.Y.Z`'s notes.
3. CI runs the unit tier and the full test suite on the tag, then creates the GitHub release with the version's notes;
   a version with no section in `CHANGELOG.md` gets no release. When the repository names an image registry
   (`IMAGE_REGISTRY`), the images are published under the version as well.

A release carries no binaries: `dev.bat` and `./dev.sh` build the setup from the checkout, and a deployment runs the
container images.

## Reporting bugs / requesting features

Open an issue. For security issues, see [SECURITY.md](SECURITY.md) and please do **not** open a public issue.
