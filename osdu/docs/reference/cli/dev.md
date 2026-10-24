---
id: delivery-cli-dev
title: "osdu-delivery-setup and sqlflow dev: set a checkout up for a local run, and check what it has and what it is missing"
type: cli-command
summary: "Set a checkout up for a local run (SQL Server, databases, admin, OSDU, flows), and check every tool, setting and database it needs, each with its fix."
keywords:
  - local development
  - run locally
  - dev.bat
  - dev.sh
  - first start
  - setup wizard
  - .sqlflow/env
  - dev.env.example
  - what is missing
  - readiness check
  - sql server not installed
  - sql server in docker
  - mac
  - macos
  - linux
  - catalog database missing
  - snapshot isolation
  - first admin password
  - osdu sign-in test
  - legal tag
  - env reference not set
  - windows authentication
  - sql login
  - osdu-delivery-setup
  - no .net installed
  - install .net
cliCommand: dev
related:
  - delivery-guide-getting-started
  - delivery-concept-environment-variables
  - delivery-concept-partitions
  - delivery-cli-partition
  - delivery-cli-db
  - concept-environment-variables
  - cli-control-plane
  - guide-github-app
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Setup/DevCommand.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevCommandLine.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevSetupWizard.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevReadiness.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevFacts.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevEnvFile.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevEnvTemplate.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevValues.cs
  - osdu/src/SqlFlow.Delivery.Setup/SqlTarget.cs
  - osdu/src/SqlFlow.Delivery.Setup/SqlServerProbe.cs
  - osdu/src/SqlFlow.Delivery.Setup/LocalSqlServers.cs
  - osdu/src/SqlFlow.Delivery.Setup/OsduSignIn.cs
  - osdu/hosts/SqlFlow.Delivery.Setup.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryDevVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - sqlflow/src/SqlFlow.Core/Secrets/LocalEnvFile.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerb.cs
  - dev.env.example
  - dev.bat
  - dev.sh
---

# osdu-delivery-setup and sqlflow dev

`osdu-delivery-setup` sets a checkout of OSDU Delivery up to run on this machine, and says what a local run has and
what it is missing. The launchers run it: `dev.bat` on Windows and `./dev.sh` on macOS and Linux run `setup` when
`.sqlflow/env` is missing, and `check` at every other start, and stop only when the check says a start would fail.
A start (not `dev.bat check`) also points git at `tools/git-hooks` when the clone has no hooks path set, so the unit
tests run before every push ([CONTRIBUTING.md, Tests](../../../../CONTRIBUTING.md#tests)).

It is a host of its own (`osdu/hosts/SqlFlow.Delivery.Setup.Host`) that carries only the setup, so the first question
comes without waiting for the whole product to build:

- **The launchers build it** from the checkout on every start, which takes seconds and always matches the checkout;
  nothing is downloaded. By hand: `dotnet build osdu/hosts/SqlFlow.Delivery.Setup.Host`.
- **Where .NET is not installed**, which every start needs, the launcher offers to install it before it builds the
  setup: `dev.bat` with winget, `./dev.sh` with Homebrew on a Mac and with Microsoft's `dotnet-install.sh` into
  `~/.dotnet` on Linux, which first needs ICU (`libicu`), as every .NET program on Linux does. Declined, or where the
  installer is missing, it names the install and stops.

The command line has the same code as a verb, `sqlflow dev`, for a machine that has built it. `dev` is about this
machine running OSDU Delivery itself; SQLFlow's `sqlflow doctor` is about a command line reaching a control plane
elsewhere ([the control plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md)).

## Synopsis

```bash
osdu-delivery-setup setup [--env-file <path>]
osdu-delivery-setup check [--env-file <path>] [--api] [--test-osdu] [--prepare] [--json]
osdu-delivery-setup env   [--env-file <path>]
osdu-delivery-setup --help
sqlflow dev setup | check | env ...     # the same, from the command line
```

```bash
dev.bat              # Windows: the setup on a first start, then the check, then the GUI, the control plane and its workers
dev.bat setup        # the setup questions again, offering the current values
dev.bat check        # the check alone, with a test sign-in to OSDU; nothing is stopped or started
dev.bat api          # the control plane and its workers without the GUI
dev.bat single       # one process: the control plane runs runs on its own node and starts no workers (as F5 does)
./dev.sh             # the same on macOS and Linux (./dev.sh setup, ./dev.sh check, ./dev.sh api, ./dev.sh single)
```

## Description

A local run reads its settings from `.sqlflow/env` at the checkout's root: `NAME=value` lines, git-ignored, holding this
machine's values and secrets ([SQLFlow's environment variables and .sqlflow/env](../../../../sqlflow/docs/reference/concepts/environment-variables.md)).
The file is written from the template `dev.env.example`, which sits beside the launchers and explains every setting.
The template's sections decide what a missing setting means:

| Section | Settings | When one is missing or wrong |
| --- | --- | --- |
| Needed to start | the catalog database, the session signing key, the GUI's origin, the first admin | The check reports an error and the launchers stop. The first admin is needed only while the catalog has no user. |
| Needed to run flows | the data database; the OSDU platform, partition, client, groups and legal tag | The check warns; the launchers start, and a run that reads it fails. |
| Optional | the tenant, a flow repository, migrations, Azure sign-in, Microsoft sign-in, the assistant's ! questions, managed workers, the test database | Reported when set. |

The setup reads the env file itself, never applying it first (the command line applies it before every other verb), so
a file the hosts would refuse still reaches the check, which names each line it refuses.

### The setup

`setup` asks for what it cannot know, offering the current file's values as the defaults, and changes nothing until it
has shown its plan and you have said yes. Two steps need the command line (creating the catalog, registering the
partition), so the setup host starts building it in the background as the questions begin, with the .NET SDK on the
PATH or the one an installer just put in its usual place; by the time the plan is confirmed it is usually built.

0. **.NET**, when the command line cannot find it, which happens only to a setup started other than by a launcher
   (they install .NET first): it says how to install it, writes the file and makes the databases it can; its plan says
   the catalog and the partition wait for .NET, and the next `dev.bat setup` makes them.
1. **SQL Server.** It lists the SQL Servers on this machine: Windows services, LocalDB instances and Docker containers
   from a SQL Server image. When none runs and Docker's engine does, it offers to start SQL Server 2022 in a container
   (`osdu-delivery-sql`, port 1433, its databases kept in the volume of that name), with a generated `sa` password handed
   to Docker through its environment, never its command line, and waits up to three minutes for it to accept the
   login. That is how SQL Server runs on a Mac. It then asks for the server and the sign-in (Windows authentication on
   Windows, a SQL login otherwise), and connects. A server that does not answer is explained with its fix, and can be
   left for later.
2. **Databases.** The catalog (`OsduDelivery` by default) and the data database (`OsduData`); flows name them in
   three-part names, so the names are kept unless the flows say otherwise. A missing catalog is created with the
   catalog and the `osdu` schema after the file is written, through the command line's `db migrate --create`; a
   database the login cannot read is said so, never taken for an empty one. A missing data
   database is created with snapshot isolation allowed, and an existing one without it is offered it.
3. **Signing in.** The first admin's user name, and its password (12 characters at least; Enter generates one). A user
   that already exists keeps its password, so none is asked for. The session signing key is generated, or the current
   valid one kept.
4. **OSDU.** The platform's base URL, the data partition, the tenant (from which the token URL is built) or the token URL
   itself, the scope, the client id and secret, the owners and viewers groups, and the legal tag. On Azure Data Manager
   for Energy (`*.energy.azure.com`) the scope and the groups default to its conventions. On request it signs in with the
   client and reads the legal tag from the partition (`GET /api/legal/v1/legaltags/{name}`), writing nothing, and offers
   the questions again when that fails.
5. **Flows.** A flow repository to register: a folder on this machine, read live, or a git URL, cloned and pulled. A
   GitHub repository can be read with a GitHub App: its client ID goes in `SQLFLOW_GITHUB_APP_ID` and the path of its
   `.pem` file in `SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH`, and the repository names no credential, so it is read with a
   token the App mints for it alone ([creating a read-only App](../../../../sqlflow/docs/reference/guides/github-app.md)). Otherwise an access token is kept in
   `SQLFLOW_GIT_TOKEN` and named by reference. One the catalog registers already is left as it is.
6. **Optional.** Whether flows read Azure through `az login` (`SQLFLOW_AZURE_AUTH=cli`), and the assistant's ! questions.

It then writes the file, keeping the one it replaces beside it as `env.<time>.bak`, carries over every name the old file
set that no question asked about and every setting it kept commented out, creates or adjusts the databases, and
registers the partition with the command line's `partition add` (as the default when the catalog has none), applying
the catalog's pending migrations first. On macOS and Linux the file is readable by its owner only. It waits for the
command line's build to finish, which the start needs next, and then runs the check.

### What the check reports

`check` prints a line per finding, with the fix under each that has one:

| Section | What it reads |
| --- | --- |
| Tools | the .NET SDK (as `global.json` takes it; one installed where this terminal's PATH does not reach yet is a warning to open a new terminal), Node.js 22 or later with npm (not with `--api`), the Azure CLI and its sign-in (when the file reads Azure through it), Docker (where SQL Server runs in it) |
| The file | lines the hosts refuse, names set twice (the first counts), values `dev.bat` cannot set (a double quote beside `&`, `\|`, `<`, `>` or `^`, or more than 8,000 characters), and names this terminal sets to another value |
| Settings | every setting of the template, by section: set, missing, still a placeholder, or wrong (a signing key under 32 bytes, an origin that is not the GUI's, a URL that is not https, a group that is not an e-mail, Windows authentication off Windows); misspelt names, with the one meant |
| SQL Server | the servers on this machine; the catalog's server, its release and edition; the catalog database (missing, empty, another application's, offline, closed to the login) and the data database (missing, without snapshot isolation) |
| Sign-in, partitions and flows | the users and the first admin; the partitions registered; each repository (its folder missing here, its last sync's error); the GitHub App the file names (all of it, or which part is missing; its client ID and the `.pem` path are checked as settings); every `${env:NAME}` the repositories' YAML names that nothing sets (the file, this terminal, or the central configuration) |
| OSDU | with `--test-osdu`: the client's sign-in and the legal tag |

A server that does not answer is explained from what this machine has: SQL Server not installed (with the installer to
run: `winget install Microsoft.SQLServer.2022.Developer` on Windows, Docker Desktop on a Mac), its service stopped or
disabled, its container stopped, a login the server refuses (with the `CREATE LOGIN` for your Windows account), or a
certificate it does not trust.

Nothing printed shows a secret: a secret's value is shown by its length, and a connection string by its server,
database and kind of sign-in.

### env

`env` prints the file's settings, one `NAME=value` line each, exactly as the hosts read them (surrounding quotes
taken off, the first of a name set twice). The launchers set them in their own process: the control plane builds its
configuration from the process environment before it reads `.sqlflow/env` itself, so its `ControlPlane__*` settings
reach it only that way.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--env-file <path>` | path | `<checkout>/.sqlflow/env` | The env file to set up, check or print. |
| `--api` | flag | off | `check`: the GUI does not start, so Node.js is not needed and the GUI's origin is not checked. |
| `--test-osdu` | flag | off | `check`: sign in with the OSDU client and read the legal tag. Writes nothing to OSDU. |
| `--prepare` | flag | off | `check`: first replace a copied template's `<generated>` values with generated ones, and run `az login` when the file reads Azure through the Azure CLI and nobody is signed in. The launchers pass it at every start. |
| `--json` | flag | off | `check` only: one JSON document (`ready`, `errors`, `warnings`, and each section's findings) instead of text. |

## Behavior and output

```text
OSDU Delivery: what this machine has for a local run, and what it is missing

Tools
  ok     .NET SDK                                      9.0.305
  ok     Node.js                                       v22.12.0

Settings needed to start (.sqlflow/env)
  ok     SQLFLOW_CATALOG_DB                            localhost / OsduDelivery, Windows authentication
  ok     ControlPlane__Jwt__SigningKey                 set, 64 characters
  ...
Settings needed to run flows (.sqlflow/env)
  WARN   OSDU platform                                 not set up: OSDU_URL, OSDU_DATA_PARTITION, OSDU_TOKEN_URL, ...
           -> Run dev.bat setup, which asks for them and can test them; dev.bat starts without them, and runs that reach OSDU fail.

SQL Server
         On this machine                               localhost (running)
  ok     localhost                                     SQL Server 2022 (16.0.4135.4), Developer Edition (64-bit), signed in as WELLDB\dev
  ERROR  Catalog OsduDelivery                          does not exist
           -> Create it: dev.bat setup offers to, or run: dotnet run --project osdu\hosts\SqlFlow.Delivery.Cli.Host -- db migrate --create

Not ready to start: 1 ERROR line(s) above. Fix them (dev.bat setup asks the setup questions again), then start dev.bat again.
```

### Errors

| Message | Cause |
| --- | --- |
| `No dev.env.example was found in <folder> or above it. Run the setup from an OSDU Delivery checkout ...` | It ran outside a checkout. |
| `The template <path> is missing; it ships with the repository. Restore it with: git checkout -- dev.env.example` | The checkout lost its template. |
| `<path>(<line>): the section '<title>' is none of 'Needed to start', 'Needed to run flows' and 'Optional'.` | An edited template renamed a section. |
| `name what to do: setup, check or env.` | No subcommand, or (`'<word>' is none of setup, check and env.`) an unknown one; the usage follows, as it does for each error of this kind. |
| `'<word>' follows the subcommand '<subcommand>', which takes no other word.` | A second word after the subcommand. |
| `'<option>' is not an option of this command; it takes --env-file <path>, --api, --test-osdu, --prepare and --json.` | An option it does not read. |
| `--env-file needs the path of the env file.` | `--env-file` with no path after it. |
| `--json applies to 'check' alone.` | `--json` with `setup` or `env`. |
| `The input ended before the setup was done; nothing was written.` | `setup` ran with no one to answer (its input ended); exit 1. |
| `<env file> is missing; dev.bat setup writes it.` | `env` with no file; exit 2. |

Each error is printed as `ERROR  <message>` on stderr.

## Examples

A first start on a Windows machine with SQL Server installed:

```bat
dev.bat
```

```text
Building the setup ...
.sqlflow\env is missing, so this is a first start: a few questions set this machine up.

1. SQL Server: where the catalog and the data database live
  On this machine: localhost (running)
  SQL Server name [localhost]:
  Sign in with your Windows account (WELLDB\dev)? n for a SQL login [Y/n]:
    Connected: SQL Server 2022 (16.0.4135.4), Developer Edition (64-bit), as WELLDB\dev.
  ...
```

A first start on a machine without .NET: the launcher offers the install before anything else (here dev.bat; on
Linux, `./dev.sh` offers `Microsoft's dotnet-install.sh, into ~/.dotnet`):

```text
The .NET SDK is not installed. The setup, the control plane and the workers all build and run on it.
Install the .NET 9 SDK now with winget (it may ask for an administrator's rights)? [Y/n]
```

A first start on a Mac with Docker Desktop running and no SQL Server:

```bash
./dev.sh
```

```text
  No SQL Server is installed on this machine.
  Start SQL Server 2022 in Docker now (container osdu-delivery-sql, port 1433, its databases kept in the volume osdu-delivery-sql)? [Y/n]:
    Starting SQL Server 2022 in Docker; the first start downloads its image (about 1.5 GB) ...
    SQL Server is up in osdu-delivery-sql.
```

The check from a script, as JSON:

```bash
osdu/hosts/SqlFlow.Delivery.Setup.Host/bin/Debug/net9.0/osdu-delivery-setup check --api --json
```

## Exit behavior

| Exit code | `setup` | `check` | `env` |
| --- | --- | --- | --- |
| 0 | written, and a start would succeed | a start would succeed (warnings may remain) | printed |
| 1 | stopped before writing, or a usage error | a usage error | a usage error |
| 130 | stopped with Ctrl+C | stopped with Ctrl+C | |
| 2 | written, and a start would fail | a start would fail | the file is missing or the hosts refuse a line of it; nothing printed |

## See also

- [Run it locally](../guides/run-locally.md): what to install, the first start and the launcher's commands, in short
- [Getting started](../guides/getting-started.md): the settings by hand, for a run from the command line alone
- [Environment variables OSDU Delivery adds](../concepts/environment-variables.md)
- [Partitions](../concepts/partitions.md)
- [sqlflow partition](partition.md)
- [sqlflow db](db.md)
- [SQLFlow: environment variables and .sqlflow/env](../../../../sqlflow/docs/reference/concepts/environment-variables.md)
