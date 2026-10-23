---
id: delivery-guide-managed-workers
title: "Managed workers: the control plane starts its own workers, on Radix and on a developer's machine"
type: guide
summary: "Run OSDU Delivery's workers as Radix jobs or local processes the control plane starts, keeps at a minimum, and lets leave only when idle."
keywords:
  - managed workers
  - radix
  - radix job
  - job scheduler
  - launcher
  - process launcher
  - dev.bat managed
  - dev.bat single
  - worker pools
  - scale to demand
  - minimum workers
  - drain
  - node name
related:
  - guide-deployment
  - cli-worker
  - concept-control-plane
  - concept-authentication-and-identity
  - delivery-guide-deployment
  - delivery-guide-radix
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/ManagedWorkers/RadixWorkerLauncher.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/ManagedWorkers/RadixLauncherOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/ManagedWorkers/ProcessWorkerLauncher.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/deploy/radix/radixconfig.yaml
  - osdu/deploy/README.md
  - dev.bat
  - dev.sh
---

# Managed workers: the control plane starts its own workers

With managed workers on, OSDU Delivery's control plane starts and stops its workers itself instead of leaving that to
an autoscaler: every managed pool keeps at least its minimum running, more start for queued runs no free slot
covers, and an idle extra worker leaves only once it holds nothing. How the fleet decides, every setting under
`ControlPlane:ManagedWorkers`, and what it guarantees are SQLFlow's, in
[Managed workers](../../../../sqlflow/docs/reference/guides/deployment.md#managed-workers-the-control-plane-starts-its-own-workers).
This page covers the two launchers OSDU Delivery offers and how to run each.

| Launcher | A worker is | Use it for |
| --- | --- | --- |
| `radix` | a Radix job of the `worker` job component | an estate on Radix |
| `process` | a local `SqlFlow.Delivery.Worker.Host` process | a developer's machine: `dev.bat` and `./dev.sh` start it unless told `single` |

Both are registered while managed workers are on, and only the one `ControlPlane:ManagedWorkers:Launcher` names has
its settings checked. A host started with any other name refuses to start and names these two.

## On Radix

Radix runs KEDA, but builds its rules itself with no trigger that reads the control plane's replica target, and it
gives every pod 30 seconds to stop. Managed workers need neither: the workers are a Radix job component, and the
control plane starts one job per worker through the component's job scheduler.

`osdu/deploy/radix/radixconfig.yaml` is the whole application (the control plane, the GUI, the MCP server and the
worker job component), and [Deploying on Radix](radix.md) says how to deploy and
[Setting up a Radix environment](radix-setup.md) what a person sets up once per environment. The workers' credential
is the `node-token` secret of the environment's Key Vault, generated before the first deploy: the control plane makes it
the admin's node token when it starts (`ControlPlane__Bootstrap__NodeTokenReference`), and every worker presents it.

What each part does:

- **`controlplane`**: one replica, `ControlPlane__Worker__Enabled=false`, managed workers on with the `radix`
  launcher. `ControlPlane__ManagedWorkers__Radix__JobSchedulerUrl` is `http://worker:8080`: Radix serves the job
  scheduler of a job component at its name and `schedulerPort`, reachable only by the application's own components,
  so it needs no credential.
- **`worker`**: the job component. Nothing runs until the control plane starts a job. Its variables and Key Vault
  secrets are every worker's: the node token, `SQLFLOW_CATALOG_DB`, the flows' connections,
  `SQLFLOW_URL=http://controlplane:8080`.
- **Each job** the control plane starts carries only the worker's name (its `jobId` and `SQLFLOW_NODE_NAME`), its pool
  (`SQLFLOW_WORKER_POOL`), its drain window, its time limit, `backoffLimit: 0`, and the pool's CPU and memory when
  set. The job component's own `safeToRestart: false` keeps the cluster autoscaler from moving a worker, and its
  `timeLimitSeconds` is what a job falls back to. No credential is ever in a job request, which Radix stores in the
  job's specification.

`ControlPlane:ManagedWorkers:Radix`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `JobSchedulerUrl` | none | The job component's scheduler. Required. |
| `TimeLimitHours` | `24` | Each job's time limit. At least `ControlPlane:ManagedWorkers:MaxLifetimeHours` plus twice `DrainMinutes` (14 with the defaults): a worker that could not be replaced in time drains halfway between the two, and still has a whole drain window before Radix ends the job. |
| `DrainMinutes` | `60` | How long a worker stopped while busy (an operator restart) keeps finishing its runs. |
| `RequestTimeoutSeconds` | `30` | Timeout of each call to the job scheduler. |
| `Resources:Requests:Cpu`, `:Memory`, `Resources:Limits:Cpu`, `:Memory` | none | Every job's CPU and memory, as Kubernetes quantities (`500m`, `2Gi`); none leaves the job component's own. |
| `Pools:<name>:Resources:...` | none | The same for one managed pool, which must be listed under `ControlPlane:ManagedWorkers:Pools`. |

A redeploy leaves running jobs alone: they keep the image they started with. Every worker's name carries the build it
was started by: the commit Radix built and deployed the application from (`RADIX_GIT_COMMIT_HASH`, which Radix sets in
every component), or, when a deployment does not carry one, the control plane's own assemblies. So the new control plane
replaces the old commit's workers (`ReplaceWorkersOnStart`), even when only the worker image changed,
starting the new ones first (one for each busy old worker) and letting the old ones finish their runs before they
leave, so a rollout cuts nothing off; a control plane that merely restarts on the same image keeps every worker. A
worker nearing the job's time limit drains in time to finish its runs, even when no replacement could start, and the
control plane logs that as a warning. Radix keeps only the last ten finished jobs; the Nodes page's Activity tab
and the control plane's log are the record of what was started, drained and stopped, and why.

## On a developer's machine: `dev.bat`

```bat
dev.bat
```

Managed workers are how `dev.bat` and `./dev.sh` start, because every deployment runs its work in workers rather than
in the control plane, so a local run crosses the same node protocol and the Nodes page's scaling acts here too. The
launcher builds the worker host and starts the control plane API-only with the `process` launcher: each managed
worker is a `SqlFlow.Delivery.Worker.Host` process, named `worker-<pool>-<id>`, polling `http://localhost:5000`. It
inherits the control plane's environment (the same `.sqlflow\env`, so the same databases and Azure sign-in), and gets
a credential in its environment only, issued by the control plane for it: the `node` scope only, refused everywhere
but the node protocol. Its module database is the control plane's, the catalog's: `SQLFLOW_CATALOG_DB` is set in its
environment to the connection the control plane opens the catalog and the `osdu` schema on, whichever way the control
plane was given it (a worker reads that variable alone). Its output appears in the control plane's window
under its name, and it loads its own copy of the current cache version when it starts. `dev.bat api` does the same without the GUI. `dev.bat single` (and F5)
starts no workers: the control plane runs runs on its own node, and the Nodes page says that nothing there starts a
worker. `dev.bat managed` still names the default.

At most 4 workers start unless `.sqlflow\env` sets `ControlPlane__ManagedWorkers__MaxWorkers`; any other
`ControlPlane__ManagedWorkers__*` setting there applies as well (a lower `IdleMinutes` shows a worker leaving sooner).
The workers end with the control plane. One left behind by a control plane that was killed is ended by the next
`dev.bat`, and if it is still polling when a new control plane starts, it is asked to leave as a worker the launcher
does not have.

`ControlPlane:ManagedWorkers:Process`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Command` | none | The worker host to start. Required; `dev.bat` and `./dev.sh` set it. |
| `Arguments` | none | Arguments before the node's own options (`worker` when the command is the CLI host). |
| `WorkingDirectory` | the command's folder | Where the worker runs. |
| `ControlPlaneUrl` | this host's own address | The URL the workers poll. |
| `DrainMinutes` | `60` | As for Radix. |

## Watching it

The Nodes page opens on a line of four facts (workers online, runs running against the slots online, runs queued and
how many could start now, and how workers start, with a warning when starts keep failing), each leading to its tab. Its
Scaling tab shows, under Worker pools, each managed pool's minimum, its workers that take work against what it wants,
how many are starting, being replaced, leaving or silent (polled before, not in the last minute: it still counts for two
minutes after its last poll, then a replacement starts), and a warning when its starts keep failing. When no worker at
all has polled for longer than a long poll, the control plane takes the silence for its own or the network's: it
restores minimums but starts nothing for queued runs and stops no silent worker. Managed workers, below it, lists every
worker with its state; the Activity tab lists the recent actions with their reasons, and the Queue tab what is queued,
what holds it back and what runs where. `GET /api/v1/nodes/managed` returns the same.

When starts keep failing, the pool backs off (10 seconds, doubling to 5 minutes) and keeps trying; the warning's
tooltip says why:

- **"the Radix job scheduler at ... could not be reached"**: `JobSchedulerUrl` does not name the job component and its
  `schedulerPort`, or the component is not deployed in this environment.
- **"refused to start worker ...: 400"**: the scheduler rejected the job, usually a resource quantity the cluster
  does not accept; its answer follows.
- **"it ended ... before its first poll"**: the job ran and exited without ever polling. The worker's own log says
  why; the usual causes are a missing `SQLFLOW_TOKEN` secret ("no node credential"), a token without the `node` scope,
  or a missing `SQLFLOW_CATALOG_DB` ("the worker refuses to start").
- **"it ended ... after its first poll, unasked"**: the worker started, then died within ten minutes (out of memory
  while warming up, a module that failed after start). Its job's log says why.
- **"the platform never listed it"**: a start answered with an error that might have created the job did not (three lists
  and a minute missed it), and the control plane stopped waiting for it.
- **"the platform could not be asked about it"**: the job scheduler did not answer for the whole start timeout after a
  start whose answer was lost; the control plane stopped counting that start and starts another.
- **"it has not polled in the 10 min it was given to start"**: the job never ran (an image that cannot be pulled, a
  cluster with no room) or cannot reach `SQLFLOW_URL`.
