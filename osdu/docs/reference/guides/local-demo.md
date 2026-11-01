---
id: delivery-guide-local-demo
title: "The local demo: OSDU Delivery on a laptop from one command, with no OSDU to connect to"
type: guide
summary: "One command builds and starts OSDU Delivery on a clean machine, delivers two sample estates to a local stand-in for OSDU, and proves them."
keywords:
  - demo
  - try it
  - bootstrap
  - one command
  - laptop
  - clean machine
  - no osdu
  - local osdu
  - stand-in
  - tour
  - sample estates
  - sqlflow estate
  - start over
  - reset
  - dev.bat demo
  - dev.sh demo
related:
  - delivery-cli-dev
  - delivery-guide-run-locally
  - delivery-guide-welldb-sample
  - delivery-guide-getting-started
sourceRefs:
  - bootstrap.ps1
  - bootstrap.sh
  - dev.bat
  - dev.sh
  - osdu/src/SqlFlow.Delivery.Setup/DevSetupWizard.Demo.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevTour.cs
  - osdu/gui/standin/osdu-standin.mjs
  - osdu/gui/standin/osdu-writes.mjs
---

# The local demo: OSDU Delivery on a laptop from one command

One command takes a clean machine to a running OSDU Delivery that has already delivered a small estate and proved it. You
need no OSDU platform, no account and no configuration: a stand-in for OSDU runs on the machine, and everything else is real.
The same engine, the same ledger, the same GUI, and the same flows you would point at a production partition.

## Run it

On Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/bootstrap.ps1 | iex
```

On macOS or Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/bootstrap.sh | bash
```

From a checkout, `dev.bat demo` or `./dev.sh demo` does the same.

It needs a SQL Server on the machine, or Docker Desktop running so it can start one. It offers to install what else is
missing: .NET, and on Windows Git and Node.js.

## What happens

1. **It fetches and builds** the code, into `osdu-delivery` beside where you ran it.
2. **It sets the machine up, asking nothing.** It finds a SQL Server or starts one in Docker, creates `OsduDelivery`, `OsduData`
   and `SqlFlowDemo`, generates the secrets, and registers the partition.
3. **It starts everything:** the control plane and its workers, the GUI on `http://localhost:5173`, and the stand-in for OSDU.
4. **The tour runs,** in a window of its own, and gives you the sign-in straight away so you can watch it in the GUI.

The tour fires each schedule of the two sample estates through the path **Run now** takes:

| Estate | What it shows |
| --- | --- |
| [sqlflow-estate](https://github.com/TahirRiaz/sqlflow-estate) | Files to an executive KPI, every file format and option, exports, documents, an ML health check, and 109 expected values |
| [osdu-delivery-estate](https://github.com/TahirRiaz/osdu-delivery-estate) | Reference data, wells, wellbores, surveys, formation tops and well logs delivered into an empty partition, with their curves as bulk data, then read back and held to the source files |

Before anything is asserted, the tour waits 310 seconds. A real OSDU's search index needs a moment after a write, and the
flows give it that time as they would in production, so the tour does too.

A green ending looks like this:

```text
Everything held: 164 runs in 8.7 minutes, and 21 of 21 assertions passed on what OSDU holds.

Open http://localhost:5173 and sign in as admin, password ...
  Schedules    the two estates, chained, and how long each link took
  Lineage      every table and flow, from source file to KPI: http://localhost:5173/lineage?repoId=...&project=sales
  Assertions   every check of what OSDU holds, each with its evidence
  Explorer     the records as OSDU holds them, every version of each
  Mappings     how a source row became a record, field by field
```

It says everything held only when every run succeeded and every assertion was **evaluated** and passed. A test skipped is
not a test passed. If something writes to the records while the assertions run, say you press **Redeliver** in the GUI, the tests it
touched are skipped, so the tour gives the index its window again and runs them once more; anything still not evaluated fails it.

## See that the proof is real

The assertions read what OSDU holds, not what the ledger says was sent. Change a value and they fail. Each log's `-samples`
test holds its curves to their source file, so a change of one sample in any curve fails it, and the Assertions page names the
record, the test and the value.

## Do it again

| You want | Run |
| --- | --- |
| To run the tour again, keeping everything | `dev.bat demo` or `./dev.sh demo` |
| To start over from nothing, for a new take | `dev.bat demo reset` or `./dev.sh demo reset`; with the bootstrap script, set `OSDU_DELIVERY_RESET=1` first |
| To start it again another day, with no tour | `dev.bat` or `./dev.sh` |

A reset removes the three databases, what the stand-in holds and the demo's checkouts. It runs only where this checkout's own demo
made them.

## When it does not go as planned

| What you see | What it means | What to do |
| --- | --- | --- |
| `already sets up an environment of another kind, so the demo leaves it alone` | This checkout's `.sqlflow/env` names something else | Clone into a new folder and run the demo there |
| `already holds a catalog, which this checkout did not make` | The SQL Server holds an OSDU Delivery catalog, which is someone's environment. The demo will not touch it, and the sample flows name their databases, so it cannot use others | Run the demo on a clean machine, or on a SQL Server of its own |
| `This machine has no SQL Server, and Docker's engine is not running` | There is nothing to hold the databases | Start Docker Desktop, or install SQL Server as the line under it says, and run it again |
| `holds something else` | The folder it would clone into is in use | Set `OSDU_DELIVERY_FOLDER` to a new folder |
| `The stand-in for OSDU does not answer` | Its window was closed | Run the demo again: it starts the stand-in, and what was delivered is kept |
| `Port 5301 is taken` | Something else holds the stand-in's port | Stop it, and run the demo again |
| `The sample estates did not sync` | GitHub could not be reached; the line gives git's error | Check the network or a proxy, and run the demo again |
| `The control plane refused admin` | The password was changed after the demo set it | Change it back on the Users page, or `demo reset` |
| `The tour lost the control plane` | It stopped while the tour ran; its window says why | Fix what it says, and run the demo again: what was delivered is kept |
| `of 21 assertions, ... were not run` | A test was still skipped after the second wait, because something kept writing to its records | Leave the delivery alone, and run the demo again |

## What the stand-in is

The stand-in is a small program, `osdu/gui/standin/osdu-standin.mjs`, not an OSDU. It answers the calls the sample estates
make, as the services' contracts do: Storage, Search, Legal, the Schema service and the Wellbore DDMS with its bulk data and
sessions. It keeps what is delivered in `.sqlflow/osdu-standin.json`, so a restart keeps it.

Its Schema service holds the published schemas the estates keep in their template folders, as a platform holds the standard
kinds. The estate's own `WellDbWellLog` kind is not among them, so it reads as unregistered, as it would until someone registered it.

It enforces no entitlements or legal tags, indexes at once where a real platform takes time, and answers only the Lucene the
flows write. A delivery that works against it has met these contracts, not a platform's operations, so it is the way to see
the product and to try a change, and no substitute for testing against your OSDU.

It listens on `127.0.0.1` alone. A demo's flows cannot reach a real OSDU, because its environment names none.

## Next

- [Run it locally](run-locally.md): the same system against your own OSDU
- [The sample estate](welldb-sample.md): what the OSDU estate's flows do, one by one
- [osdu-delivery-setup and sqlflow dev](../cli/dev.md): every verb of the setup
