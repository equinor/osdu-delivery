---
id: delivery-concept-authentication-and-identity
title: "Who may operate OSDU Delivery: scopes on the delivery routes, and how the ledger names people"
type: concept
summary: "Which delivery actions any signed-in user can take, which need admin, who can sign in at all, and how the ledger names who acted."
keywords:
  - authentication
  - authorization
  - permissions
  - roles
  - scopes
  - admin scope
  - who can deliver
  - who can delete records
  - actor
  - audit trail
  - entra app role
  - sqlflow.user
  - node token
  - requested by
related:
  - concept-authentication-and-identity
  - concept-control-plane
  - delivery-concept-api
  - delivery-concept-ledger
  - delivery-guide-deployment
  - delivery-guide-mcp
  - cli-auth
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliverySearchTermEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DirectOperationRunner.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ScheduledTargetProbeService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantAccess.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/deploy/bicep/entra-app.bicep
  - osdu/deploy/bicep/main.bicep
  - osdu/src/SqlFlow.Delivery.Setup/DevSetupWizard.cs
  - dev.env.example
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlanePolicies.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/RequestActor.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunActors.cs
  - sqlflow/src/SqlFlow.Node/RunWorker.cs
---

# Who may operate OSDU Delivery, and how the ledger names people

How the control plane signs people in and issues tokens is SQLFlow's, unchanged: local login, Microsoft Entra single
sign-on, the device grant the CLI and the MCP server use, personal access tokens, rolling sessions, roles and scopes. All
of it is documented in
[SQLFlow's authentication and identity page](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md).
This page says what that model means for the delivery surface, and how the ledger records who did what.

## Any signed-in user can deliver and remove

The delivery routes are mapped into SQLFlow's policy groups. `read`, `operate` and `author` all resolve to "a signed-in
user"; only `admin` checks the token's scope claim. So every signed-in user, a `viewer` included, can read the ledger and
also act on OSDU: trigger and cancel delivery runs, release and redeliver records, bring records up to date, reverse a run,
remove records from OSDU, delete a flow's ledger, approve cache changes, probe a target and use the explorer.

| Policy | Delivery routes behind it |
| --- | --- |
| `admin` (the `admin` scope) | Setting and removing central configuration properties (`PUT`, `DELETE /config/{name}`); registering, describing, defaulting and removing partitions; the ledger's retention pass (`POST /ledger/prune`); removing a dimension (`DELETE /dimensions/{id}`). |
| `author` (any signed-in user) | Refining, resetting, deleting and restoring search terms. |
| `operate` (any signed-in user) | Every intervention, every removal, every in-process read of OSDU or the source, the explorer, value checks, reversals, ledger deletion, the data definitions sync. |
| `read` (any signed-in user) | Every listing and detail of the ledger, mappings, templates, caches, partitions, configuration, assertions, dimensions and inventories. |

The full list is in [the delivery API](api.md). Because a signed-in user can send records to a live OSDU and take them
away, **the gate is who can sign in**. With Entra sign-on, the bundled Bicep (`entra-app.bicep`, on by default through
`provisionEntraApp`) and its Terraform mirror (`modules/entra-app`, through `provision_entra_app`) create the app registration with one app role, `SqlFlow.User`, and assignment required, so nobody
signs in until a group (`azureAdAllowedGroupObjectId`) or a user is assigned to the role. A first Entra sign-in is
provisioned with `ControlPlane:AzureAd:DefaultRole` (`azureAdDefaultRole` in the Bicep, `viewer` by default), which decides only whether the account reaches the `admin`
routes and user administration.

## Nodes, assistants and automation

- **Nodes** are not people. A worker authenticates with a personal access token minted with the `node` scope, as any
  SQLFlow node does ([sqlflow worker](../../../../sqlflow/docs/reference/cli/worker.md)); OSDU Delivery adds nothing to it.
  Mint it from an account whose own scopes cover it, and keep it in a secret store.
- **The MCP server** holds the session of the person who approved its sign-in and renews it as the GUI renews its own,
  or, run as a service, forwards each request's own bearer token after checking it with `GET /api/v1/me`. Its tools can
  do what that token can do ([the MCP server](../guides/mcp.md)).
- **The GUI chat assistant** runs each question under a token delegated from the signed-in user, issued for that one
  run, never renewable and without `admin` or `node` ([SQLFlow's assistant run tokens](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md#assistant-run-tokens)).
  The control plane refuses that token on every route not opened to it. Of the delivery routes, the module opens
  exactly the ones its assistant tools read, and the four mapping-builder previews they post a draft to
  (`DeliveryAssistantAccess`); an operator action, a record's content and a dimension's values stay closed. The six
  operator tools (probe, verify, sync, release, redeliver, decide cache changes) are listed to the chat as to any
  client of the MCP server, and the control plane refuses them to a chat run. A call the MCP server marks as the
  assistant's (the Slack bot's, made with a personal access token) is held to the same routes.
- **An external scheduler** triggers OSDU runs with a service account's personal access token
  ([deployment](../guides/deployment.md#triggering-from-an-external-scheduler)).

## How the ledger names who acted

Every activity in the ledger's audit trail carries an actor, written as text when the activity is recorded, so revoking
or renaming an account later does not rewrite what it did.

| Actor | Recorded for |
| --- | --- |
| `user:<subject>` | What the control plane does itself on a person's request: a release, the records a redelivery or a rerender names, a purge from the ledger, an issue released, a dimension removed, a probe of a flow's target; and the removals and value checks it queues for a node. `<subject>` is the token's `sub` claim. |
| `<subject>` | A run a person queued through the API (the GUI, `sqlflow trigger`, an MCP tool, an external scheduler): the run's requester, which the delivery run records its activities under. |
| `schedule:<name>` | A run a SQLFlow schedule fired. |
| `cli:<user>@<machine>` | Every change a command line verb records: a run executed directly with `sqlflow run` on a workstation, and a release, reversal, dimension removal, cache import, template, configuration property or partition set from the command line. |
| `service:schedule` | A probe made by the scheduled target probe. |
| `unknown` | A run with no requester recorded. |

The run row itself carries the requester and the trigger source (`schedule`, `manual` or `cli`) as SQLFlow records them,
so a delivery run's activities and its run agree on who asked. Outside the audit trail, a cache change records who
approved or rejected it, and a configuration property, a partition and a refined search term record
who last set them, each as `user:<subject>` (or `cli:<user>@<machine>` when a command line verb set it) except the cache
decision, which records the subject alone.

A read a person waits on (a record read back, a preview, a source read, an explorer search) changes nothing and writes no
activity. The control plane logs it as `<actor> ran <operation> on <flow> in process.` A probe of a flow's target is the
exception: asked for or scheduled, it is recorded as a `probe` activity under who asked
([watching the targets](run-trace-and-metrics.md#watching-the-targets)).

## Local development

A local run (`dev.bat`, `./dev.sh`) signs its own session tokens: the setup generates a signing key for this machine
alone into the git-ignored `.sqlflow/env`, so a token a local control plane issues is valid there and nowhere else, and
configures a first admin, created at the first start when the catalog has no user of that name and never reset after
([osdu-delivery-setup](../cli/dev.md)). A local control plane started against a catalog that already has users needs no first
admin, and signs in with those users' accounts and roles.

## See also

- [SQLFlow: authentication and identity](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md)
- [The delivery API](api.md)
- [The ledger](ledger.md)
- [sqlflow auth in OSDU Delivery](../cli/auth.md)
