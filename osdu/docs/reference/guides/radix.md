---
id: delivery-guide-radix
title: "Deploying OSDU Delivery on Radix: the control plane, the GUI, the MCP server and the workers"
type: guide
summary: "Run OSDU Delivery as one Radix application: what each component is, how to build and deploy, what the first start does, and why it might not start."
keywords:
  - radix
  - radixconfig
  - deploy
  - workload identity
  - key vault
  - azure sql
  - ai foundry
  - mcp server
  - job component
  - managed workers
  - github actions
  - rx
  - osdu partition per environment
related:
  - delivery-guide-radix-setup
  - delivery-guide-managed-workers
  - delivery-concept-partitions
  - delivery-guide-deployment
  - guide-deployment
  - guide-chat-assistant
  - cli-worker
sourceRefs:
  - osdu/deploy/radix/radixconfig.yaml
  - osdu/deploy/radix/radix-deploy.workflow.yml
  - osdu/deploy/docker/control-plane.Dockerfile
  - osdu/deploy/docker/gui.Dockerfile
  - osdu/deploy/docker/mcp.Dockerfile
  - osdu/deploy/docker/worker.Dockerfile
  - osdu/src/SqlFlow.Delivery.ControlPlane/ManagedWorkers/RadixWorkerLauncher.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
---

# Deploying OSDU Delivery on Radix

OSDU Delivery runs on Radix as one application, `osdu-delivery`, defined by
[osdu/deploy/radix/radixconfig.yaml](../../../deploy/radix/radixconfig.yaml), in the shape Radix applications commonly
take: images built by GitHub Actions and pushed to an Azure Container Registry, a deploy-only pipeline that rolls them
out, a workload identity per environment, and secrets from each environment's Key Vault.

| Component | Image | Public address (dev) | What it does |
| --- | --- | --- | --- |
| `controlplane` | `osdu-delivery-control-plane` | `https://controlplane-osdu-delivery-dev.<zone>` | The API, scheduling, dispatch and the chat assistant. One replica. It starts and stops the workers ([managed workers](managed-workers.md)) and calls the model in AI Foundry. |
| `gui` | `osdu-delivery-gui` | `https://gui-osdu-delivery-dev.<zone>` | The SPA. It reads the control plane's address from `SQLFLOW_API_BASE_URL`. |
| `mcp` | `osdu-delivery-mcp` | `https://mcp-osdu-delivery-dev.<zone>/mcp` | The MCP server. Public, because the model's MCP tool calls it from AI Foundry; every call carries a SQLFlow token, which it checks with the control plane and forwards to it. |
| `worker` (job) | `osdu-delivery-worker` | none | The workers. The control plane starts each as a job through the job component's scheduler at `http://worker:8080`. |

Environments are `dev`, `test` and `prod`. Radix gives each public component the address
`<component>-<app>-<env>.<zone>`, where `<zone>` is the DNS zone of the cluster the application is registered on (the
Radix web console shows each component's address). The file is written for one cluster's zone; registered on another,
every address in it changes with the zone.

The file is the environment's whole configuration, as the Terraform estate's is ([Deployment](deployment.md)): the
partition its runs deliver to, the flow repository the control plane syncs, the workers' credential, the databases, the
sign-in registration and the assistant's model are all settings of it, and the control plane sets each of them up when it
starts. No secret is in the file: the JWT signing key is mounted from the environment's Key Vault, and every other
secret is a `${keyvault:...}` reference the component resolves with its workload identity when it needs it.

## Before the first deploy

Radix runs the application; the Azure resources and the secrets around it are set up once per environment by a person,
before the first deploy: the Radix application and the deploy identity, two workload identities, the two databases and
their users, the vault and its secrets (the workers' credential among them, generated in SQLFlow's token form), the OSDU
partition, the flow repository, the sign-in registration, the Foundry project and the network paths.
[Setting up a Radix environment](radix-setup.md) is that checklist, and ends with the file's placeholders filled in.

## Deploy

The **Deploy to Radix** workflow is a sample beside the application, `osdu/deploy/radix/radix-deploy.workflow.yml`:
a repository that deploys with Radix copies it to `.github/workflows/` and sets the repository variables
`DEPLOY_METHOD` to `radix` (which also keeps the Container Apps workflow from running) and `RADIX_REGISTRY_NAME` to
its registry. Run it with the environment. It builds the four images from `osdu/deploy/docker`, tags each with the
commit and with the environment (`dev`, `test`, or `latest` for `prod`), pushes them, and asks Radix for a deploy pinned
to the commit: `rx create pipeline-job deploy --commitID <sha> --image-tag-name <component>=<sha>,...`. Radix runs
exactly those images, and the commit reaches every component as `RADIX_GIT_COMMIT_HASH`, which managed workers use to
tell a rollout from a restart.

The control plane is ready once `/health/ready` answers: the catalog and the `osdu` schema are migrated and verified.
Its start-up is done once `/health/startup` answers too: the partition its variables name is registered, the flow
repository is registered and synced, the `node-token` secret is the admin's node credential, and every other step of
the start-up pass is done (`GET /api/v1/bootstrap` names one that is not, and why).

## The first start

Nothing is set up by hand after the first deploy. The control plane's start-up pass registers the partition and the flow
repository its variables name and makes the vault's `node-token` the admin's `node`-scope credential; the first worker,
started by the control plane as soon as it owns dispatch, presents that same secret and polls. Sign in to the GUI as
`admin` with the vault's `admin-password`, and the Nodes page shows the default pool keeping one worker running and
starting more for queued runs.

## Updating and promoting

Run the workflow again for the same environment, or for the next one: each environment gets the images of the commit
it was deployed from. A deploy rolls the control plane: for a few seconds a second replica runs beside the old one,
and a node call that reaches the one that does not own dispatch is refused and retried; the new control plane takes
over when the old one stops. Workers of the previous commit keep their runs; the new control plane starts their
replacements first and lets each old worker leave once it holds nothing ([managed workers](managed-workers.md)).

Rotating a secret is a vault write and a restart: a new `node-token` is adopted at the control plane's next start and
read by the next worker it starts (the old workers, drained and replaced on the rollout, never need it); a new
`jwt-signing-key` signs everyone out; a new `osdu-client-secret` or `git-token` is read the next time a run or a sync
needs it.

## When something does not start

| What you see | Why |
| --- | --- |
| The deploy is refused: a client id is not a GUID | A value in angle brackets was left in `radixconfig.yaml`. |
| A pod waits in `ContainerCreating` | `jwt-signing-key` is missing from the vault, or the identity cannot read it. |
| The control plane never becomes ready | It cannot reach or sign in to Azure SQL, the `OsduDelivery` database does not exist, or no partition is registered because the environment's variables name none; its log says which. |
| The control plane does not start, naming an `Osdu:Partitions` setting | A partition's setting is not one it takes: two defaults, a name that is no data-partition-id, or a secret's value where its `${keyvault:...}` reference belongs (the log never repeats the value). |
| `/health/startup` stays unhealthy; `GET /api/v1/bootstrap` names the node credential | `node-token` is missing from the vault, the control plane's identity cannot read it, or it is not in SQLFlow's token form (`sqlf_` and at least 32 random characters). Workers started meanwhile are refused and replaced until it is. |
| `/health/startup` stays unhealthy; `GET /api/v1/bootstrap` names the repository | Its `RemoteUrl` is not reachable, or `git-token` (or the GitHub App's key, `github-app-private-key`) is missing or cannot read it; a GitHub App that is not installed on the repository says so. The GUI's Repos page tests the access and names the credential it read with. |
| Workers start and end at once; the Nodes page shows the pool failing | The worker cannot read `node-token` from the vault, or cannot reach `SQLFLOW_CATALOG_DB`; the job's log says which. |
| The chat answers that the assistant is not configured, or fails on its first tool | A Foundry setting is missing, the control plane identity lacks `Cognitive Services OpenAI User`, or Foundry cannot reach the MCP server's public address. |
| Sign-in fails in the GUI | The app registration's redirect URI is not the GUI's address, or `ControlPlane__AzureAd__ClientId` names another registration. |
| A delivery fails resolving `OSDU_ACL_OWNER` or `OSDU_ACL_VIEWER` | The partition's properties do not set them (every delivery flow reads them unless it sets its own groups). |
