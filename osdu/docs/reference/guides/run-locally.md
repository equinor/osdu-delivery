---
id: delivery-guide-run-locally
title: "Run it locally: what to install, the first start, and the dev.bat and ./dev.sh commands"
type: guide
summary: "Run the control plane, its workers and the GUI on one Windows, macOS or Linux machine: what to install, what the first start asks, and how to sign in."
keywords:
  - run locally
  - local machine
  - local development
  - dev.bat
  - dev.sh
  - prerequisites
  - what to install
  - first start
  - sign in as admin
  - sql server in docker
  - apple silicon
related:
  - delivery-cli-dev
  - delivery-guide-welldb-sample
  - delivery-guide-getting-started
  - delivery-guide-deployment
  - delivery-concept-environment-variables
  - concept-environment-variables
sourceRefs:
  - dev.bat
  - dev.sh
  - dev.env.example
  - osdu/hosts/SqlFlow.Delivery.Setup.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevSetupWizard.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevReadiness.cs
  - osdu/src/SqlFlow.Delivery.Setup/LocalSqlServers.cs
---

# Run it locally: what to install, the first start, and the dev.bat and ./dev.sh commands

The whole system (the control plane, its workers and the GUI) runs on one machine, on Windows, macOS or Linux, from a
checkout of this repository. One command sets the machine up on its first start and starts everything after that. The
setup it runs and the check it makes at every start are described in full on
[osdu-delivery-setup and sqlflow dev](../cli/dev.md); this page is the short way through.

## What you need

| Component | What for | Windows | macOS | Linux |
| --- | --- | --- | --- | --- |
| .NET 9 SDK or later | builds and runs everything; the first start offers to install it | `winget install Microsoft.DotNet.SDK.9` | `brew install --cask dotnet-sdk` | [Install .NET](https://learn.microsoft.com/dotnet/core/install/linux) |
| SQL Server 2022 | the catalog and ledger (`OsduDelivery`), and the source data (`OsduData`) | `winget install Microsoft.SQLServer.2022.Developer`, or Docker | Docker Desktop: `brew install --cask docker`; the setup starts SQL Server in it | Docker Engine; the setup starts SQL Server in it |
| Node.js 22 or later | the GUI (not needed for the control plane alone) | `winget install OpenJS.NodeJS.LTS` | `brew install node` | [nodejs.org](https://nodejs.org/en/download) |
| An OSDU platform and a client | delivering: its URL, a data partition, a client id and secret, owners and viewers groups, a legal tag | | | |
| Azure CLI | only when flows read Azure Key Vault or Azure storage | `winget install Microsoft.AzureCLI` | `brew install azure-cli` | [Install the Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli-linux) |

On a Mac with Apple silicon, SQL Server's image runs under emulation: turn on Docker Desktop's "Use Rosetta for
x86_64/amd64 emulation on Apple Silicon" (Settings, General) before the first start.

## Start it

From the checkout:

```bash
dev.bat          # Windows
./dev.sh         # macOS, Linux
```

On a machine without .NET, the launcher offers to install it first (winget on Windows, Homebrew on a Mac, Microsoft's
`dotnet-install.sh` into `~/.dotnet` on Linux), then builds the setup from the checkout, as every start does.

**The first start** runs the setup (`osdu-delivery-setup`, a small program of its own that builds in seconds). It asks
for what it cannot know:

- the SQL Server, offering to start SQL Server 2022 in Docker when the machine has none, which is how it runs on a Mac;
- the first admin's password;
- the OSDU platform and its client, which it can test by signing in and reading the legal tag;
- a folder or git repository of flows.

It shows its plan, and once you say yes it writes the git-ignored `.sqlflow/env`, creates the databases and registers
the partition.

**Every start after that** checks the machine and the settings, prints what is set and what is missing with the fix for
each, and stops only when a start would fail.

In a git checkout with no hooks path set, a start also points git at `tools/git-hooks` once, so the unit tier runs
before every push (`git push --no-verify` skips it once; [CONTRIBUTING.md](../../../../CONTRIBUTING.md#tests)).

**Then sign in.** The GUI opens at `http://localhost:5173` (the API is `http://localhost:5000`). Sign in as `admin` with
the password the setup put in `.sqlflow/env` as `SQLFLOW_ADMIN_PASSWORD`.

## The launcher's commands

| Command | What it does |
| --- | --- |
| `dev.bat`, `./dev.sh` | The setup on a first start, then the check, then the control plane, its workers and the GUI. The control plane starts and stops worker processes of its own, as a deployment's workers run, so the Nodes page's scaling acts locally too. |
| `dev.bat check`, `./dev.sh check` | The check alone, with a test sign-in to OSDU; nothing is started or stopped. |
| `dev.bat setup`, `./dev.sh setup` | The setup questions again, offering the current values; the file it replaces is kept beside it. |
| `dev.bat api`, `./dev.sh api` | The control plane and its workers without the GUI. |
| `dev.bat single`, `./dev.sh single` | One process: the control plane runs runs on its own node and starts no workers, as F5 does. Combine with `api` for neither workers nor the GUI. |
| `dev.bat managed`, `./dev.sh managed` | The default, named; refused together with `single`. |

## Setting it up by hand

Every setting, with what it is for and whether a start needs it, is in `dev.env.example` at the root of the checkout:
copy it to `.sqlflow/env` and fill it in. The file's sections, and what a missing setting means in each, are on
[osdu-delivery-setup and sqlflow dev](../cli/dev.md).

## Where to go next

- [The welldb sample](welldb-sample.md): a complete package to deliver into an empty partition.
- [Your first delivery](getting-started.md): one wellbore from a CSV file to OSDU, step by step on the command line.
- [Deployment](deployment.md): the same system in containers, on Kubernetes or on Azure.
- [CONTRIBUTING.md](../../../../CONTRIBUTING.md#tests): the tests, for working on OSDU Delivery itself.
