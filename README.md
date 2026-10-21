# OSDU Delivery, powered by SQLFlow

[![SCM Compliance](https://scm-compliance-api.radix.equinor.com/repos/equinor/c5db785e-d5ab-4ece-acfd-51394a8cd325/badge)](https://developer.equinor.com/governance/scm-policy/)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Get any data into OSDU by configuring it, not coding it.**

[OSDU](https://osduforum.org/) is the open data platform of the energy industry: one place for wells, logs, seismic,
production data and more, each stored as typed records behind a set of services. Getting data into it is hard. Every
type of data has its own schema, its own codes and its own services, so most teams end up writing and maintaining a
custom loader for each one.

OSDU Delivery replaces those loaders with configuration. It can, because everything a loader would otherwise read,
look up or hard-code lives in three places, each known and versioned, and a mapping connects them:

```text
     drop-off zone              template                 cache
    your source data         the OSDU schema       lookups from OSDU,
    files and tables         you deliver to       files and databases
            |                       |                       |
            +-----------------------+-----------------------+
                                    |
                                 mapping
                          (connects the three)
                                    |
                     OSDU records, files, bulk data
                                    |
                                    +--> ledger: every record, every attempt, every change
```

| | What it is | Why it makes configuration enough |
| --- | --- | --- |
| **Drop-off zone** | Where your source data arrives: files (CSV, JSON, Parquet) dropped in a folder or storage location, or the tables of a source database. Built-in flows load them into a table keyed by record, noting what changed and which file and row each row came from. | Whatever system the data came from, the mapping reads it in one shape: columns of a row. |
| **Template** | The OSDU schema of the type you deliver, picked from the published OSDU schemas and saved with a version. | The mapping fills a known shape, and is checked against it before anything runs. |
| **Cache** | What mappings look values up in: reference data captured from OSDU, lookup tables loaded from files or databases, and small dictionaries kept beside the mappings. One cache per OSDU data partition, refreshed by its own flows and kept as versions. | Codes and units are translated, and references checked against what OSDU holds, without a line of lookup code. |

With those three in place, a new type of data needs only a mapping: this column, translated through that lookup, fills
that property of the schema. OSDU Delivery does the rest:

- it **delivers** the records through the right OSDU services, with their files and bulk data (such as log curves);
- it **keeps OSDU in sync** with your source, sending only what changed, including when a mapping, template or cache
  changes;
- it **records** every record it sends, with the source row and the mapping, template and cache versions that produced
  it, so you can always say where a record came from and what happened to it.

It is built for data teams that have to load data into OSDU, keep it current, and answer for it.

## Why it matters

| Without OSDU Delivery | With OSDU Delivery |
| --- | --- |
| A custom loader for each type of data | A mapping for each type of data |
| Source-specific reading code in every loader | One drop-off zone, one shape: rows of a table |
| Lookup code against OSDU and other systems in every loader | One versioned cache that every mapping reads |
| Code for each OSDU service: storage, files, the domain data stores | Every OSDU write path built in |
| Retry and clean-up logic written by hand | Retries, resume and undo built in |
| Full reloads, or home-made change tracking | Only what changed is sent |
| "What did we send?" answered from logs, if at all | Every record traceable in a ledger |

## How it works

1. **Drop your data.** Put source files in the drop-off zone, or point a flow at a source database. Built-in flows load
   them into a table keyed by record.
2. **Save the template.** Pick the OSDU schema of the type you deliver, in the GUI. Nothing to write.
3. **Fill the cache.** A cache flow captures what your mappings look up, from OSDU, files or databases, and refreshes it
   on a schedule.
4. **Write the mapping.** A YAML file says how one row becomes one record. Write it by hand, build it in the GUI's
   mapping builder, or let an AI assistant draft it.
5. **Deliver.** A delivery flow names the table, the mapping and the OSDU target. `plan` shows what would be sent,
   `run` sends it, and a schedule keeps OSDU in sync from then on.

Here is the mapping from the included sample, which delivers well logs with their curve data, shortened. The comments
show where each part comes from:

```yaml
documentType: mapping
name: WellLog
version: 1.4.0
template:                                  # template: the OSDU schema this mapping fills
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
dataset:
  system: welldb
  key: [source_project, log_id]            # same source row, same OSDU record, every time
record:
  # ... ACLs and legal tags
  data:
    Name:
      $from: log_source                    # drop-off zone: a column of the source row
      $modifiers: [trim]
    Curves:
      $forEach: curves                     # drop-off zone: one entry per curve row
      $item:
        Mnemonic: { $from: curve_id }
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias    # cache: the source's unit, translated through a lookup table
            - ref                          # ...into a reference to OSDU's unit of measure
    # ... the rest of the log
```

This mapping is the only part that knows about well logs. Nothing in the engine does: given another template and
mapping, it delivers wellbores, seismic, samples, production volumes or any other OSDU type.

## What you get

- **Every OSDU write path.** The storage, file and dataset services; manifest and workflow ingestion; and the domain
  data stores (DDMSs) for wellbores, seismic, rock and fluid samples, well delivery, production and reservoir models.
  The [coverage plan](docs/osdu-coverage-plan.md) lists what each one covers.
- **Stable ids.** The same source row always becomes the same OSDU record, so reruns never create duplicates.
- **Only what changed.** Each run reads the rows that changed and sends only the records that come out different.
  Change a mapping or the reference data, and exactly the affected records go again.
- **Checked before sending.** Mappings are checked against their schema, legal tags are confirmed with OSDU, and each
  record can be validated before it goes out.
- **Safe when things fail.** Every step is recorded as it completes, so a retry picks up where it stopped. Problems
  that need a person are held for review, and a delivery that fails halfway is undone.
- **Scales out, runs near your data.** Work spreads over as many workers as you run. Workers fetch their work instead of
  accepting connections, so they can run inside the network that holds your data.
- **One configuration, every environment.** The same flows run in dev, test and prod. A new environment is a setting,
  not an edit to every flow.

## Every record accounted for

Traceability is the heart of the product. For every record OSDU Delivery sends, its ledger answers:

- **Where did it come from?** The source file and row.
- **What shaped it?** The versions of the mapping, schema and reference data it was built with.
- **What happened when it was sent?** Every attempt, with what OSDU answered.
- **What is it in OSDU?** The id and version it landed as.
- **Who touched it?** Every release, redelivery, removal and reversal, with who did it and when.

The dashboards count straight from the ledger, any record can be found by its key, OSDU id or source file, and nothing
is ever sent to OSDU without going through it.

## An AI assistant that builds it with you

Because a delivery is configuration, an AI assistant can build one. `osdu-delivery-mcp` connects Claude, Cursor,
VS Code, Codex, Slack or the GUI's own chat to OSDU Delivery:

- **Build.** Ask for a new data type and the assistant reads the schema, drafts the mapping, checks it until it is
  valid, and opens a pull request for a person to review.
- **Operate.** Ask whether a record was delivered, why it failed, which file it came from, or what a reference data
  change will resend.

The assistant works from definitions and delivery metadata, never from the records' content, and acts with the
signed-in person's own permissions. See the [MCP guide](osdu/docs/reference/guides/mcp.md).

## After delivery

- **Verify** reads delivered records back from OSDU and flags any that changed there.
- **Explore** what OSDU holds, live in the GUI, searching by property or by your own source column names.
- **Test** what landed with data quality checks, and publish the reports from CI.
- **Reconcile** OSDU against the ledger: find records no one accounts for and records OSDU lost, and fix them.
- **Retrieve** OSDU records back into your own storage as files.
- **Correct** with confidence: remove records, reverse a whole run, or send records again, with every action recorded.

## Try it

The sample in [osdu/samples/welldb](osdu/samples/welldb) is a complete package that fills an empty partition: the OSDU
reference values its records refer to (units, unit quantities, curve families, log and measurement types), the well
database's own curve types, three wells, five wellbores and five synthetic well logs with their curve data. Every
reference a record makes names a record the package delivers, and the suites check that it does. Lineage orders the
deliveries the way the records refer to each other: the reference data, the caches that capture it, the wells, the
wellbores that name them, and the logs, whose curves go to the Wellbore DDMS. It has all three parts: a drop-off zone
with sample files and the flows that load them, the templates its mappings pin (in
[osdu/samples/templates](osdu/samples/templates)), and cache flows for the lookup tables and the partition's reference
data. A data quality test checks the logs once they land. `sqlflow lineage welldb` prints the order.

You need a SQL Server and an OSDU instance you can write to. The
[getting started guide](osdu/docs/reference/guides/getting-started.md) sets up the databases and the OSDU connection,
saves the templates and fills the cache. The sample's schedules then deliver the reference data nightly, with the caches,
wells and wellbores chained after it. Once the wellbores are in OSDU, delivering the logs takes four commands:

```bash
cd osdu/samples
sqlflow check   welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
sqlflow preview welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
sqlflow run     welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE --operation plan
sqlflow run     welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
```

`check` validates the flow, its mapping and its schema; `preview` builds one record without sending it; `plan` shows
what a run would send; `run` delivers.

## Run it locally

The whole system (control plane, workers and GUI) runs on one machine, on Windows, macOS or Linux. You need:

| Component | What for | Windows | macOS | Linux |
| --- | --- | --- | --- | --- |
| .NET 9 SDK or later | builds and runs everything; the first start offers to install it | `winget install Microsoft.DotNet.SDK.9` | `brew install --cask dotnet-sdk` | [Install .NET](https://learn.microsoft.com/dotnet/core/install/linux) |
| SQL Server 2022 | the catalog and ledger (`OsduDelivery`), and the source data (`OsduData`) | `winget install Microsoft.SQLServer.2022.Developer`, or Docker | Docker Desktop: `brew install --cask docker`; the setup starts SQL Server in it | Docker Engine; the setup starts SQL Server in it |
| Node.js 22 or later | the GUI (not needed for the control plane alone) | `winget install OpenJS.NodeJS.LTS` | `brew install node` | [nodejs.org](https://nodejs.org/en/download) |
| An OSDU platform and a client | delivering: its URL, a data partition, a client id and secret, owners and viewers groups, a legal tag | | | |
| Azure CLI | only when flows read Azure Key Vault or Azure storage | `winget install Microsoft.AzureCLI` | `brew install azure-cli` | [Install the Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli-linux) |

Then start it from the checkout:

```bash
dev.bat          # Windows
./dev.sh         # macOS, Linux
```

On the first start the setup (`osdu-delivery-setup`, a small program of its own that builds in seconds) asks for what
it cannot know: .NET when it is missing (offering to install it), the SQL Server (and offers to start SQL Server 2022 in
Docker when the machine has none, which is how it runs on a Mac), the first admin's password, the OSDU platform and its client
(which it can test by signing in and reading the legal tag), and a folder or git repository of flows. It shows its plan,
and once you say yes it writes the git-ignored `.sqlflow/env`, creates the databases and registers the partition. Every
start after that checks the machine and the settings, prints what is set and what is missing with the fix for each, and
stops only when a start would fail. Then the GUI opens at `http://localhost:5173` (the API is `http://localhost:5000`);
sign in as `admin` with the password the setup put in `.sqlflow/env` as `SQLFLOW_ADMIN_PASSWORD`.

| Command | What it does |
| --- | --- |
| `dev.bat`, `./dev.sh` | The setup on a first start, then the check, then the control plane, its workers and the GUI. The control plane starts and stops worker processes of its own, as a deployment's workers run, so the Nodes page's scaling acts locally too. |
| `dev.bat check`, `./dev.sh check` | The check alone, with a test sign-in to OSDU; nothing is started or stopped. |
| `dev.bat setup`, `./dev.sh setup` | The setup questions again, offering the current values; the file it replaces is kept beside it. |
| `dev.bat api`, `./dev.sh api` | The control plane and its workers without the GUI. |
| `dev.bat single`, `./dev.sh single` | One process: the control plane runs runs on its own node and starts no workers, as F5 does. |

On a machine without .NET, the launcher offers to install it first (winget on Windows, Homebrew on a Mac, Microsoft's
`dotnet-install.sh` into `~/.dotnet` on Linux), then builds the setup from the checkout, as every start does.

Every setting, with what it is for and whether a start needs it, is in [dev.env.example](dev.env.example): copy it to
`.sqlflow/env` and fill it in to set up by hand. [osdu-delivery-setup](osdu/docs/reference/cli/dev.md) describes the
setup and the check in full.

On a Mac with Apple silicon, SQL Server's image runs under emulation: turn on Docker Desktop's "Use Rosetta for
x86_64/amd64 emulation on Apple Silicon" (Settings, General) before the first start.

## Under the hood

Each step of the journey is a flow with its own run and log: one flow lands the source files, one loads them into a
table keyed by record, and the delivery flow reads what changed in that table and sends it to OSDU. Flows are YAML
files in git, and the system runs them in the right order.

A deployment runs a control plane (the API, the scheduler and the sync from git), the workers, the web GUI and an
optional MCP server, over two SQL Server databases: one for the metadata and the ledger, one for the source data. It
ships with Docker Compose, Kubernetes, Azure Bicep, Terraform and Radix templates
([deployment guide](osdu/docs/reference/guides/deployment.md)).

OSDU Delivery is built on [SQLFlow](https://github.com/TahirRiaz/SQLFlow), a metadata-driven pipeline engine that
provides the platform underneath: flows in git, scheduling, lineage, workers, the GUI shell and the command line.
SQLFlow is vendored in [sqlflow/](sqlflow/) and extended only through generic extension points, each recorded in
[docs/sqlflow-changes.md](docs/sqlflow-changes.md).

## Status

Delivery has been verified against a live OSDU (Azure Data Manager for Energy 0.29, September 2026) through the
storage, file, manifest ingestion and Wellbore DDMS paths. Every write path is tested against OSDU's own service contracts,
pinned in [osdu/specs](osdu/specs). Recent changes are in the [changelog](CHANGELOG.md).

## Development

Requires the .NET 9 SDK, Node.js 22+, a SQL Server for the database tests, and Rust for the MCP server.

```bash
bash tools/test.sh --install-hook   # once, where dev.bat or ./dev.sh has not: the unit tier before every push
bash tools/test.sh                  # the unit tier: a few minutes, no database
bash tools/test.sh --full           # every test, against SQL Server: before a pull request
```

`dev.bat` (Windows) or `./dev.sh` (macOS, Linux) runs the control plane and GUI locally
([Run it locally](#run-it-locally)). See [CONTRIBUTING.md](CONTRIBUTING.md) for the full setup and [CLAUDE.md](CLAUDE.md)
for the project's working rules.

## Learn more

- [What OSDU Delivery is](osdu/docs/reference/concepts/overview.md): the product on one page
- [Pattern catalog](osdu/docs/reference/guides/pattern-catalog.md): which configuration solves which delivery problem
- [Reference documentation](osdu/docs/reference/README.md): every flow, mapping key, command and concept
- [Architecture](osdu/docs/reference/concepts/architecture.md): components, databases and how it sits on SQLFlow
- [Decision records](osdu/docs/decisions/README.md): the design choices and why they were made
- [osdu/README.md](osdu/README.md): a map of the module's code

## License and contact

Licensed under the GNU General Public License v3.0 ([LICENSE](LICENSE)).

Questions, bugs and feature requests: **[tahir@sqlflow.io](mailto:tahir@sqlflow.io)** or an issue on the repository.
Security reports go by email or a private security advisory, never a public issue ([SECURITY.md](SECURITY.md)).
