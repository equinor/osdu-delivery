---
name: sqlflow-local-dev
description: Run OSDU Delivery locally (dev.bat on Windows, ./dev.sh on macOS and Linux) for fast iteration and debugging, instead of building containers and deploying. Use when asked to run/debug/test locally, "spin it up", set up dev mode, reproduce a bug, or try a change without deploying. Covers the first-start setup and the readiness check (osdu-delivery-setup), the .sqlflow/env contract, VS Code F5, and the launcher traps.
---

# OSDU Delivery local dev

**Deploying to try a change is the slow path. Default to this.** A code change is `dotnet run` (seconds) and a GUI
change is a Vite hot-reload (instant).

## The shape

| | |
|---|---|
| GUI | http://localhost:5173 (Vite, hot-reloads on save) |
| Control plane | http://localhost:5000 (API + scheduler + dispatcher; its own node only with `single` or F5) |
| Workers | `SqlFlow.Delivery.Worker.Host` processes the control plane starts and stops (managed workers, the `process` launcher), named `worker-<pool>-<id>`, 1 to 4 (`ControlPlane__ManagedWorkers__MaxWorkers`), ended with it; their output is in the control plane's log under their names |
| Settings | `.sqlflow/env` (git-ignored), written from `dev.env.example` at the checkout's root |
| Local demo | `dev.bat demo` (`osdu/docs/reference/guides/local-demo.md`): stand-in OSDU on 127.0.0.1:5301, started when `.sqlflow/env` sets `OSDU_STANDIN_WRITES=true`. It refuses a catalog it did not make, so it does not run against this machine's dev estate |
| Setup and check | `osdu-delivery-setup` (`osdu/docs/reference/cli/dev.md`): host `osdu/hosts/SqlFlow.Delivery.Setup.Host`, code in `osdu/src/SqlFlow.Delivery.Setup/`; the command line's `sqlflow dev` runs the same code |

## Start it

```
dev.bat / ./dev.sh                 setup on a first start, then the check, then GUI + control plane + managed workers
dev.bat check / ./dev.sh check     the check alone, with an OSDU sign-in test; starts and stops nothing
dev.bat setup / ./dev.sh setup     the setup questions again (current values offered, old file kept as env.<time>.bak)
dev.bat demo / ./dev.sh demo       a clean machine, nothing asked: the sample estates against a stand-in for OSDU, then the tour
dev.bat demo reset                 the same from nothing; removes what an earlier demo made, and only that
dev.bat api                        control plane and workers only, no GUI
dev.bat single                     one process: the control plane runs runs on its own node, no workers (combine: single api)
```

Managed workers are the default because every deployment runs its work in worker processes, never in the control
plane: a run here crosses the same node protocol, credential and environment as it does deployed, and Spawn, Always on
and queued runs start and stop workers on the Nodes page. Each worker loads its own copy of the current cache version
when it starts. To step through a run's code, use `single` (or F5) so the run executes in the process you debug, or
attach to the `SqlFlow.Delivery.Worker.Host` process holding it.

The launchers find .NET (offering to install it when it is missing: winget, Homebrew, or `dotnet-install.sh` into
`~/.dotnet` on Linux), build the setup's host from the checkout (seconds), and run it: `setup` when `.sqlflow/env` is missing, otherwise
`check --prepare` (fills a copied template's `<generated>` values, runs `az login` when the file reads Azure through
it). The setup builds `osdu/hosts/SqlFlow.Delivery.Cli.Host` in the background for the catalog and partition steps.
The launchers stop a previous control plane only after the check passed, load the settings through the setup's `env`,
apply migrations with the command line's `db migrate`, and start. The check's exit code decides: 0 start,
2 stop (something a start needs is missing), 1 stopped.

Or **F5 in VS Code** for breakpoints (`.vscode/` is git-ignored, so `launch.json` is local). F5 does not migrate: run
the launcher once after pulling or authoring a migration. F5 starts the control plane as `.sqlflow/env` configures it,
which is the `single` shape: its own node runs the runs, and the Nodes page says that nothing there starts a worker.

## Before saying what a local run touches

Read the file's targets, never its values: `osdu-delivery-setup check` (or `sqlflow dev check`) prints each connection as server / database / sign-in
and each secret by its length only. Which catalog, which data database and which OSDU partition a run uses is whatever
`.sqlflow/env` names; it may be a local SQL Server or a shared one.

## Traps that will waste your time

**The control plane reads its `ControlPlane__*` settings only from the process environment.** It builds its
configuration before it applies `.sqlflow/env` itself, so the launchers set the file's values first (the setup's `env`). A
`dotnet run` by hand without them starts with an empty CORS list (every GUI call fails) and no signing key (refused).

**CORS defaults to EMPTY, and empty means no CORS at all.** `ControlPlane__Cors__AllowedOrigins__0` must be
`http://localhost:5173`; the check says so when it is not.

**Do not `source .sqlflow/env` in bash.** Values contain spaces and `;`. `./dev.sh` exports each `env` line as it
stands; by hand, read one value with `grep '^NAME=' .sqlflow/env | cut -d= -f2-`.

**A value cmd cannot set.** `dev.bat` sets each value with `set "NAME=value"`: a double quote next to `&`, `|`, `<`,
`>` or `^` breaks the line. The check reports such a value, and the setup quotes connection string values with single
quotes for that reason.

**SQL Server on a Mac runs in Docker** (`osdu-delivery-sql`, volume of the same name, port 1433); the setup starts it.
Apple silicon needs Docker Desktop's Rosetta emulation for the x86-64 image. A volume keeps the `sa` password it was
made with: a new container on an old volume refuses a new password.

## Windows batch traps (all three of these made dev.bat silently do NOTHING)

- **`cmd` needs CRLF.** `.gitattributes` checks `*.bat` out with CRLF; a file written with LF by a tool misparses labels
  and `for /f` blocks with no error. Re-apply CRLF after editing a `.bat` in a checkout that has not been refreshed.
- **`az`, `npm` are `.cmd` files.** Invoking one from a `.bat` WITHOUT `call` hands over control and never returns.
  Always `call az ...`, `call npm ...`. (`dotnet` is a real `.exe` and needs no `call`.)
- **A running process never sees a PATH change.** `dev.bat` finds Node under `%ProgramFiles%\nodejs` itself; a plain
  terminal still needs restarting to get a freshly installed tool.

## VS Code launch.json traps

- **`serverReadyAction.uriFormat` must contain exactly one `%s`.** A hardcoded URL makes VS Code reject the whole config.
  To open the GUI, drop `serverReadyAction` and let Vite's `--open` do it.
- Vite is pinned to port 5173 (`strictPort`), which is the origin the CORS line grants.
