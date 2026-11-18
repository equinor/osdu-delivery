---
id: delivery-wiki-0061-environments-are-deployments
title: "Decision 0061: dev, test and prod are deployments of their own, and the title bar links them"
type: decision
summary: "Why environments are separate deployments rather than partitions of one, and why the title bar's environment menu is a plain link to each."
keywords:
  - environments
  - dev test prod
  - partitions are not environments
  - separate deployments
  - instance switcher
  - environment menu
  - title bar
  - data isolation
  - ControlPlane:Environments
updated: 2026-11-18
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/WorkbenchPartition.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/hosts/osdu-delivery-mcp/src/tools/operate.rs
  - osdu/gui/src/features/delivery/useLinkedPartition.ts
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogEntities.cs
  - sqlflow/src/SqlFlow.ControlPlane/Configuration/EnvironmentsOptions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/EnvironmentEndpoints.cs
  - sqlflow/gui/src/layout/workbench/EnvironmentSwitcher.tsx
  - osdu/deploy/terraform/variables.tf
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/radix/radixconfig.yaml
referenceRefs:
  - delivery-concept-partitions
  - delivery-guide-deployment
  - delivery-concept-gui
  - concept-environments
  - concept-repository-auto-sync
related:
  - delivery-wiki-0020-explorer-partition-connection
  - delivery-wiki-0026-one-database-connection
---
# 0061: Dev, test and prod are deployments of their own, and the title bar links them

Status: accepted. Documented in [Environments](../../../../sqlflow/docs/reference/concepts/environments.md),
[partitions or environments](../../reference/concepts/partitions.md#partitions-or-environments) and
[the deployment guide](../../reference/guides/deployment.md#environments-builds-and-deploys).

The human's words: "The top line/menu in the GUI should have a posiblity to jump to diffrent instances of osdu delivery.
Think of it as an interface for deciding which eniviorment you want to work with. We have currently the posiblity to
setup multiple enviorments from the same instance. But i am not sure if thats 100% safe. THinking about agent when its
searching and other interfaces. Is partition implemented 100%? Can we safly introduce test to the same enviorment?"
Then, after the answer below: "i am thinking about having 3 instances for osdu delivery. dev test and prod. However from
the gui it should be easy to switch between these instances.", and of the switch: "a simple url pointing".

## What a partition keeps apart, and what it does not

[Partitions](../../reference/concepts/partitions.md) let one deployment deliver the same flows to several OSDU
partitions, and the reference called them `dev`, `test` and `prod`. The question was whether that makes them
environments. Read against the code, a partition keeps a run's data apart and nothing more:

- **Kept apart.** A run is bound to one partition: the `data-partition-id` header, the ids it mints, the cache it reads,
  its ledger (`<ledger>@<partition>`, every ledger table keyed by partition) and the configuration values it resolves.
- **Not a fence between people.** The `read`, `operate` and `author` policies are "signed in" for every partition
  (`ControlPlaneHost.cs`), and nothing in the module checks a partition against the caller. Whoever may release or
  redeliver in dev may do it in prod.
- **Not a fence between agents.** A read across flows that names no partition reads every partition
  (`WorkbenchPartition.cs`). The GUI sends the title bar's partition with each call; the MCP tools and the chat's
  model calls do not, so an assistant's search answers from all of them unless it names one. One cache decision may
  approve changes in several partitions at once (`operate.rs`).
- **Not a fence between versions.** A repository source has one branch (`CatalogEntities.cs`), so every partition of a
  deployment runs the commit it synced: a mapping changed for test reaches prod at its next run. The deployment's
  software version is one for all of them too.
- **Not a fence between sources and machines.** The source data and the flows that land it are the deployment's, every
  node runs every partition's work, and each run carries every partition's references (`ConfiguredRunDispatcher.cs`),
  so every node identity must be able to read prod's secrets.
- **Easy to cross by accident.** A run that names no partition runs in the default, and a link that names a partition
  makes it the title bar's (`useLinkedPartition.ts`), after which a run started from the GUI goes there.

So a partition is the right tool for one environment that delivers to more than one OSDU partition, and the wrong one
for keeping production apart from development and testing.

## The decision

Dev, test and prod are deployments of their own, each with its own sign-in, catalog, nodes, partitions and synced commit,
which [moves to a new commit only when that environment syncs it](../../../../sqlflow/docs/reference/concepts/repository-auto-sync.md).
Each deployment's configuration names the environment it is and lists every environment with the address of its GUI
(`ControlPlane:Environments`), and the title bar names the current one and opens another from a menu. The Terraform
estate and the Bicep template take the list as a variable, the Radix configuration carries its three environments as a
comment to uncomment, and the setting is SQLFlow's, since any SQLFlow estate runs environments the same way.

- **The menu is a link.** Opening another environment opens its GUI's start page, which signs the person in itself. It
  carries nothing over: no page, no partition, no token.
- **The list is the control plane's configuration**, as the rest of a deployment's description is, so the deployment
  templates set it like any other setting.
- **A mistake in it never stops a deployment.** The human, of deployed environments: "we have deployed and i dont want to
  break. so there should be a failsafe handling of this". The setting only labels and links, so the control plane reads
  it once at start, leaves out of the title bar what it cannot show, and logs why; when it cannot tell which environment
  it is, it shows none rather than a label that could be wrong. The GUI shows nothing for an answer it cannot read. The
  Terraform estate still refuses a mistake at plan, which stops the change before anything is deployed.
- **Only a signed-in person reads it.** The list names the estate's other addresses.

## Rejected

- **Environments as partitions of one deployment.** Every point above would need its own fence: a role per partition, a
  commit per partition, nodes and secrets per partition, a partition on every tool call. Each is a feature to build and
  keep, and together they would rebuild a second deployment inside the first.
- **Fencing partitions by role alone.** It would stop a person acting in prod by mistake, but not a mapping reaching prod
  untested, nor nodes holding prod's secrets.
- **Opening the same page in the other environment.** A page's address carries catalog ids, which differ between
  deployments, so the same address can name another flow there. The human asked for "a simple url pointing".
- **Keeping the list in the GUI's `config.json`.** That file is public and the GUI's alone, while the control plane's
  configuration is where a deployment is described and checked at start, and where another client can read it.
- **Showing the list on the sign-in page.** It would hand the estate's other addresses to anyone who reaches the page.
- **Refusing to start on a mistake in the list**, as the first version did and as the control plane does for settings it
  cannot run without. A typo in a label would have taken down a deployed environment, and a fork pulling the template
  could have deployed its placeholders.

## What stays open

Within one deployment that serves several partitions, the gaps above still hold: tools that read every partition when
none is named, a decision that spans partitions, and a link that moves the title bar's partition without saying so.
They are not fixed by this decision, which only takes environments out of their reach.
