---
id: delivery-guide-deployment
title: "Deploying OSDU Delivery: the images, what each tier needs beyond SQLFlow, and a first start in order"
type: guide
summary: "Deploying OSDU Delivery: images, databases, partitions, what each tier needs, a first start, and an Azure estate its deployment sets up."
keywords:
  - deploy
  - deployment
  - docker compose
  - kubernetes
  - azure container apps
  - bicep
  - terraform
  - managed instance
  - images
  - osdu-delivery-worker
  - sqlflow_osdu_db
  - osdu_partitions
  - configure partitions
  - osdudelivery and osdudata databases
  - node token
  - first deployment
  - setup identity
  - sql_connections
  - landing storage
  - repositories
  - ready to use
  - external scheduler
  - azure data factory
related:
  - guide-deployment
  - cli-worker
  - delivery-concept-control-plane
  - delivery-concept-environment-variables
  - delivery-concept-architecture
  - delivery-guide-getting-started
  - delivery-cli-db
  - delivery-guide-mcp
  - delivery-guide-managed-workers
  - guide-github-app
sourceRefs:
  - osdu/deploy/README.md
  - osdu/deploy/docker/control-plane.Dockerfile
  - osdu/deploy/docker/worker.Dockerfile
  - osdu/deploy/docker/worker-entrypoint.sh
  - osdu/deploy/docker/gui.Dockerfile
  - osdu/deploy/docker/mcp.Dockerfile
  - osdu/deploy/compose/docker-compose.yml
  - osdu/deploy/compose/.env.example
  - osdu/deploy/k8s/controlplane.yaml
  - osdu/deploy/k8s/worker-pool.yaml
  - osdu/deploy/k8s/mcp.yaml
  - osdu/deploy/k8s/secrets.example.yaml
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/bicep/control-plane.bicep
  - osdu/deploy/bicep/worker.bicep
  - osdu/deploy/bicep/entra-app.bicep
  - osdu/deploy/bicep/mcp.bicep
  - osdu/deploy/bicep/ai-foundry.bicep
  - osdu/deploy/terraform/main.tf
  - osdu/deploy/terraform/variables.tf
  - osdu/deploy/terraform/outputs.tf
  - osdu/deploy/terraform/terraform.tfvars.example
  - osdu/deploy/terraform/environments/dev.tfvars
  - osdu/deploy/terraform/environments/dev.backend.hcl
  - osdu/deploy/setup/Setup-Estate.ps1
  - deploy.ps1
  - deploy-dev.ps1
  - osdu/deploy/OsduEnvironment.psm1
  - .github/workflows/deploy.yml
  - tools/github/connect-environment.ps1
  - osdu/deploy/terraform/modules/control-plane/variables.tf
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/PartitionReadiness.cs
  - osdu/hosts/SqlFlow.Delivery.ControlPlane.Host/Program.cs
  - osdu/hosts/SqlFlow.Delivery.Worker.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
---

# Deploying OSDU Delivery

OSDU Delivery deploys exactly as SQLFlow does: an always-on control plane that owns the run queue and runs one replica,
a static GUI, and worker nodes that poll the control plane for work and scale on its replica target. The tiers, KEDA, the
ingress layouts, the drain contract, pools and triggering from Azure Data Factory are explained in
[SQLFlow's deployment guide](../../../../sqlflow/docs/reference/guides/deployment.md). OSDU Delivery ships its own
deployment assets under `osdu/deploy/` (docker, compose, Kubernetes, Bicep and its Terraform mirror, Radix, with a README of
their own), because its
images are different artifacts. This guide covers what an OSDU Delivery deployment needs that a plain SQLFlow one does not.

## The images

| Image | Dockerfile | Publishes | Scales on |
| --- | --- | --- | --- |
| `osdu-delivery-control-plane` | `osdu/deploy/docker/control-plane.Dockerfile` | `osdu/hosts/SqlFlow.Delivery.ControlPlane.Host` | one replica |
| `osdu-delivery-worker` | `osdu/deploy/docker/worker.Dockerfile` | `osdu/hosts/SqlFlow.Delivery.Worker.Host` | the control plane's replica target (KEDA) |
| `osdu-delivery-gui` | `osdu/deploy/docker/gui.Dockerfile` | `osdu/gui`, compiled with the vendored `sqlflow/gui` | trivially (static) |
| `osdu-delivery-mcp` (optional) | `osdu/deploy/docker/mcp.Dockerfile` | `osdu/hosts/osdu-delivery-mcp` | trivially (stateless) |

Every image builds from the **repository root**: the .NET hosts reference both `osdu/src` and `sqlflow/src`, the GUI
compiles both GUI trees, and the MCP server compiles the documentation of both into its binary.

```bash
docker build -f osdu/deploy/docker/control-plane.Dockerfile -t osdu-delivery-control-plane:latest .
docker build -f osdu/deploy/docker/worker.Dockerfile        -t osdu-delivery-worker:latest .
docker build -f osdu/deploy/docker/gui.Dockerfile           -t osdu-delivery-gui:latest .
docker build -f osdu/deploy/docker/mcp.Dockerfile           -t osdu-delivery-mcp:latest .
```

The worker host is the `sqlflow worker` verb with the OSDU module installed and nothing else: its entrypoint passes only
the node's options (`--pool` from `SQLFLOW_WORKER_POOL`, `--poll-seconds`, `--drain-seconds`), and it refuses to be told to
run another verb.

## The databases

| Database (shipped names) | Holds | Reached as |
| --- | --- | --- |
| `OsduDelivery` | The metadata: SQLFlow's catalog schema and the module's `osdu` schema beside it (the ledger, mappings, templates, caches, partitions, central configuration, and the dimension tables and views). | The control plane's catalog connection; on a node `SQLFLOW_OSDU_DB` (the `osdu` schema), and `SQLFLOW_CATALOG_DB` for the flows that read the dimension views, which the node itself never opens. |
| `OsduData` | The data the flows work in: what the pre flows land from the source files, the keyed tables the ingestion flows load, and what the delivery flows read, each in a schema of its own. | `${env:OSDU_DATA_DB}`, on the control plane and the nodes alike. |

The flows name both databases in three-part names (`OsduData.silver.Wellbore`, `OsduDelivery.osdu.v_dim_<view>`), so
keep the names: every template defaults to them. The control plane's bootstrap creates (with
`ControlPlane__Bootstrap__AllowCreate`) and migrates the metadata database: SQLFlow's catalog first, then the `osdu`
schema. SQLFlow never creates the data database: the compose stack's `dbinit` service does it once, the Bicep and
Terraform deployments create each database their SQL connections name on the server they create, and elsewhere it is
provisioned with the server.

**Decide where the `osdu` schema lives before the first migrate.** By default it sits in the catalog's database. An estate
that must keep it apart (Azure SQL lets no statement reach across two databases) names its database in
`Osdu:Database:Connection` (or `SQLFLOW_OSDU_DB`) on the control plane, and points every node at it; the choice cannot be
changed by editing the setting afterwards. In the Bicep templates this is `osduDatabaseName`, in the Terraform
`osdu_database_name`. Both can also point the whole estate at an existing server or Managed Instance whose databases
already exist (`existingSqlServer`, `existing_sql_server`), creating nothing SQL; the apps sign in as their own managed
identities by default, with no password in any connection.

The source database a delivery flow reads allows snapshot isolation once, so a record and its child rows are read as one
moment: `ALTER DATABASE [OsduData] SET ALLOW_SNAPSHOT_ISOLATION ON;` (Azure SQL Database allows it by default). A
flow that cannot have it declares `source.incremental.isolation: readCommitted`; without either, the run fails with
`the source database does not allow snapshot isolation, which is how a record and its child rows are read as one moment`.

## What each tier is given beyond SQLFlow's

| Tier | OSDU Delivery adds |
| --- | --- |
| Control plane | The partitions it delivers to, with their values (`Osdu__Partitions__<data-partition-id>__...`, below); the values every run of every kind resolves with, set for no partition (`Osdu__Properties__<NAME>`: `OSDU_DATA_DB`, `SQLFLOW_CATALOG_DB` for the flows that read the dimension views, a storage account's location), which also serve a person's reads run here (a flow's probe, read-back, preview and source read, and the explorer's reads of a partition); `SQLFLOW_DELIVERY_PRIVATE_NETWORKS`; the other `Osdu:*` settings it needs. It never delivers. |
| Node | `SQLFLOW_OSDU_DB`, the connection of the database holding the `osdu` schema with a login on that schema alone; `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` when a target resolves to a private address. Every run carries the values its flows resolve, so a node needs no flow's variable, though one set in its own environment still answers a reference no value of the run's covers. A node opens no catalog connection. |
| GUI | Nothing beyond SQLFlow's `SQLFLOW_API_BASE_URL`. |
| MCP server | Optional; holds no credential ([the MCP server](mcp.md#running-it-as-a-service)). |

Without `SQLFLOW_OSDU_DB` a node refuses to start (`ERROR  the worker refuses to start: ...`), and it also refuses a
module database that is missing, behind or ahead of its build. Every variable is listed in
[environment variables](../concepts/environment-variables.md).

The Bicep and Terraform deployments give the control plane the data databases (`sqlConnections`, `sql_connections`,
`OSDU_DATA_DB` by default), the metadata database as `SQLFLOW_CATALOG_DB`, the landing storage as `LANDING_URL` and any
further value (`properties`) as `Osdu__Properties__<NAME>`, each a setting or a `${keyvault:...}` reference, so a node
there holds its node credential and `SQLFLOW_OSDU_DB` alone. The compose and Kubernetes layouts still give a node
`OSDU_DATA_DB` and `SQLFLOW_CATALOG_DB` in its own environment. Values the central configuration holds, the
partitions' among them, need no variable on any tier: no template gives a node `OSDU_URL`, `OSDU_TOKEN_URL`,
`OSDU_SCOPE`, `OSDU_CLIENT_ID`, the ACLs or the legal tag any more, since every run carries its partition's values, and
`OSDU_DATA_PARTITION` is gone too (only a flow bound to no partition reads it). Those variables still work on a
workstation, where `sqlflow run` reads `.sqlflow/env`, and for a flow bound to no partition.

## The partitions

Every deployment names the OSDU partitions it delivers to in the control plane's configuration, and the control plane
registers them when it starts, with the values their runs resolve
([configured partitions](../concepts/partitions.md#configured-partitions)):

```text
Osdu__Partitions__dev__Default=true
Osdu__Partitions__dev__Description=Development platform
Osdu__Partitions__dev__Properties__OSDU_URL=https://osdu-dev.example.com
Osdu__Partitions__dev__Properties__OSDU_TOKEN_URL=https://login.example.com/oauth2/token
Osdu__Partitions__dev__Properties__OSDU_SCOPE=https://osdu-dev.example.com/.default
Osdu__Partitions__dev__Properties__OSDU_CLIENT_ID=welldb-delivery
Osdu__Partitions__dev__Properties__OSDU_CLIENT_SECRET=${keyvault:welldb-vault/osdu-dev-client-secret}
Osdu__Partitions__dev__Properties__OSDU_ACL_OWNER=data.default.owners@dev.example.com
Osdu__Partitions__dev__Properties__OSDU_ACL_VIEWER=data.default.viewers@dev.example.com
Osdu__Partitions__dev__Properties__OSDU_LEGAL_TAG=dev-welldb-public
```

- **At least one.** The control plane's start-up is not done (`/health/startup`, its `osdu-partitions` check) until a
  partition is registered, and it logs `OSDU Delivery has no partition to work against. ...` until then; it keeps
  serving the API meanwhile.
- **Secrets as references only.** A value whose name ends in `SECRET`, `PASSWORD` or `TOKEN`, or names an API key, is a
  `${keyvault:<vault>/<secret>}` or `${env:NAME}` reference, or the control plane refuses to start, naming the setting.
  The node running a run resolves the reference with its own identity (or from its own environment), and the control
  plane with its own for the reads a person waits on, so both need to reach that secret.
- **Changed only there.** The Partitions page, the API, `sqlflow partition` and `sqlflow config` refuse to change what the
  configuration sets; change the setting and restart the control plane. A partition taken out of the configuration stays
  registered, for an admin to remove.

The shipped templates carry these settings on the control plane alone:

| Layout | The partitions | The client secret |
| --- | --- | --- |
| docker compose | One partition, `dev`, its values from `.env` | `${env:OSDU_CLIENT_SECRET}`, which the control plane and the worker each resolve from `OSDU_CLIENT_SECRET` in their own environment |
| Kubernetes | One partition, `dev`, in `controlplane.yaml` (rename it to yours) | `${env:OSDU_CLIENT_SECRET}`, mounted from `osdu-delivery-secrets` on the control plane and every worker pool |
| Bicep, Terraform | `osduPartitions`, `osdu_partitions` (below) | Put in the estate's vault by hand as `osdu-<partition>-client-secret` (or the secret the partition names), and given as the `${keyvault:...}` reference to it; the start-up pass reports one it cannot read yet |
| Radix | One partition per environment in the `controlplane` component ([Deploying on Radix](radix.md)) | `${keyvault:<environment's vault>/osdu-client-secret}` |

## A first start, in order

1. **Deploy the control plane and the GUI**, with the partitions in the control plane's configuration. Bootstrap
   migrates the catalog and then the `osdu` schema, seeds the roles and creates the admin, and the control plane
   registers the partitions. Startup refuses pending module migrations, a database newer than the build and a catalog
   older than the module needs, naming the migration ([sqlflow db](../cli/db.md)).
2. **Mint a node token.** Sign in as the admin and mint a personal access token with the `node` scope
   (`POST /api/v1/me/tokens` with scopes `["node"]`, or the GUI's token page). It can only be minted once the control plane
   runs, which is why a first start has two steps.
3. **Deploy the workers** with that token as `SQLFLOW_TOKEN`, `SQLFLOW_OSDU_DB`, and the references their flows use.
4. **Register the flow repository** as a managed repository source, so the pre, ingestion and OSDU flows, the mappings and
   the cache flows are synced and their schedules fire.
5. **Fill each partition's cache** with a refresh run of its cache flows. The templates the mappings fill come with the
   repository, as files of its template folders, which its sync records ([templates](../concepts/templates.md)). The partitions are already registered from the configuration; `partition list`
   shows them marked `(configured)` ([sqlflow partition](../cli/partition.md)).
6. **Plan before anything touches OSDU**: run the pre and ingestion flows, then the delivery flow with operation `plan`,
   then `deliver` a small scope. [Getting started](getting-started.md) walks through it.

On Azure the deployment and the control plane's start-up pass do steps 1 to 4 themselves (below): the node credential
is generated into the vault before the workers start, and the configured repositories are registered and synced.

### docker compose

```bash
cd osdu/deploy/compose
cp .env.example .env                      # the secrets and the partition's values, never in the compose file
docker compose up -d --build mssql dbinit controlplane gui
# mint the node token, put it in .env as SQLFLOW_NODE_TOKEN, then:
docker compose up -d --build worker
docker compose --profile mcp up -d --build mcp   # optional: the MCP server on http://localhost:8787/mcp
```

The GUI is on `http://localhost:8081` and the API on `http://localhost:5000`.

### Kubernetes

`osdu/deploy/k8s` holds `namespace.yaml`, `controlplane.yaml`, `gui.yaml`, `ingress.yaml`, `worker-pool.yaml` (one pool;
copy it per pool), `mcp.yaml` (optional, adds `/mcp` to the same host) and `secrets.example.yaml`, which names every key of
the `osdu-delivery-secrets` secret, including `osdu-module-connection` (the node's `SQLFLOW_OSDU_DB`). The layout is
SQLFlow's: one host with `/api` to the control plane and `/` to the GUI, so no CORS.

### Azure Container Apps

`osdu/deploy/bicep/main.bicep` and its Terraform mirror `osdu/deploy/terraform` deploy the whole estate, ready to use
when the deployment ends: Log Analytics, the environment, a Key Vault holding every secret, a setup identity, the Azure
SQL databases, a landing storage account, and the control plane, a worker pool, the GUI and, given `mcpImage`, the MCP
server. Three layers make it so (`osdu/deploy/README.md` explains each):

1. **The configuration** describes the estate: its SQL connections, partitions, landing storage, repositories, further
   values and database owners. It names secrets and never holds one.
2. **The deployment** creates the resources and an identity per app, grants each what it needs, and gives the control
   plane its configuration. Inside it the setup identity runs `osdu/deploy/setup/Setup-Estate.ps1`: it generates the
   JWT signing key, the initial admin's password and the node credential into the vault when they are missing, and,
   as the SQL server's Entra administrator, creates the apps' database users and makes the configured people owners of
   every database. Both steps run again at every deployment and keep what they find.
3. **The control plane's start-up pass** applies the configuration at every start, step by step: the migrations, the
   initial admin, the node credential, the repositories, the partitions and the values every run is given
   (`GET /api/v1/bootstrap`, `/health/startup`). A step that fails is retried, so a secret put in the vault later is
   picked up without a redeploy.

A person puts in the vault only the secrets nobody else has: each partition's client secret, and for the private
flow repositories either the private key of the estate's GitHub App (`githubApp`, `github_app`) or each repository's
token, under the names the deployment's outputs give. A GitHub App is the better choice for a GitHub repository: it
belongs to the organization rather than a person, needs only read access to the repository's contents, and is asked for
a short-lived token for each sync and checkout, so no token is stored anywhere
([Reading a private GitHub repository with a GitHub App](../../../../sqlflow/docs/reference/guides/github-app.md) walks
through creating one).

OSDU Delivery's Bicep parameters beyond SQLFlow's:

| Parameter | Meaning |
| --- | --- |
| `osduPartitions` | The partitions, one object each: `id` (the data-partition-id), `default`, `description`, `url`, `tokenUrl`, `scope`, `clientId`, optionally `aclOwner`, `aclViewer`, `legalTag`, further `properties`, and `clientSecretName`. At least one, and with several exactly one default. Written as the control plane's `Osdu__Partitions__...` settings; `OSDU_CLIENT_SECRET` is the `${keyvault:...}` reference to `osdu-<id>-client-secret` (or the secret `clientSecretName` names), which a person puts in the vault. |
| `sqlConnections` | The SQL databases the flows read and write, by the name a flow spells (`{"OSDU_DATA_DB": {"database": "OsduData"}}`): a database on the estate's server (`create`, `access` write or read), or `secretName`, a vault secret holding the connection string of a database elsewhere. Every run is given each as a `${keyvault:...}` reference. |
| `sqlAdmins` | The people or groups (`name`, `objectId`, `type`) made owners of every database, inside every deployment. |
| `catalogDatabaseName` | The metadata database, `OsduDelivery` by default. |
| `osduDatabaseName` | The database holding the `osdu` schema; the catalog's database by default. |
| `landingStorage` | The storage account the flows read their files from, created or adopted, granted to the apps, and given to every run as `LANDING_URL` (`abfss://<container>@<account>.dfs.core.windows.net`). |
| `repositories` | The flow repositories the control plane syncs, registered at its start and kept as configured; a private one is read with the estate's GitHub App (`githubApp`, unless it sets `useGitHubApp` to false) or names the vault secret holding its own token (`credentialSecretName`, which wins). |
| `githubApp` | The GitHub App the estate's GitHub repositories are read with: `appId` (its client ID), `privateKeySecretName` (the vault secret a person puts its `.pem` in), and for GitHub Enterprise `apiUrl`. Each repository on the App's GitHub that names no token gets `${githubapp:app=<appId>;key=keyvault:<vault>/<privateKeySecretName>}` as its credential reference, which the control plane and each worker resolve with their own identity; the output `githubAppPrivateKeySecret` names the secret. |
| `properties` | Further values every run resolves with, as a setting or a `${keyvault:...}` reference. |
| `privateNetworks` | `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` for the apps. |
| `provisionEntraApp`, `azureAdAllowedGroupObjectId` | The Entra registration with the `SqlFlow.User` app role and assignment required ([authentication and identity](../concepts/authentication-and-identity.md)). |

The Terraform takes the same choices under snake-case names:

| Variable | Meaning |
| --- | --- |
| `osdu_partitions` | The partitions, a map by data-partition-id of `{ default, description, url, token_url, scope, client_id, client_secret_name, acl_owner, acl_viewer, legal_tag, properties }`. At least one; with several, exactly one `default = true`; property names as a flow spells them, and a property whose name says it is a secret only as a `${keyvault:...}` or `${env:...}` reference. `url`, `token_url`, `scope` and `client_id` become `OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE` and `OSDU_CLIENT_ID`, the three optional ones `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER` and `OSDU_LEGAL_TAG`. The client secret is never a variable: it is read from `osdu-<partition>-client-secret` (or `client_secret_name`) in the vault. |
| `sql_connections`, `sql_admins`, `landing_storage`, `repositories`, `github_app`, `properties` | As the Bicep's `sqlConnections`, `sqlAdmins`, `landingStorage`, `repositories` (with `use_github_app`), `githubApp` (`app_id`, `private_key_secret_name`, `api_url`) and `properties`. The output `github_app_private_key_secret` names the vault secret the App's key goes in, and `deploy.ps1 -Infrastructure` says when it is not there yet. |
| `catalog_database_name` | `OsduDelivery` by default. |
| `osdu_database_name` | The database holding the `osdu` schema; the catalog's by default. |
| `image_tag`, `mcp_enabled` | The images are the repository's, `<registry>/osdu-delivery-<app>:<image_tag>`, unless an app's image variable names one; `mcp_enabled` deploys the MCP server. An image is set when its app is created, and later applies leave it: the deploy scripts roll the apps forward. |

The `osdu_partitions` output lists each partition with whether it is the default and the vault secret its client secret is
read from; `sql_connections` lists each connection's vault secret, and `admin_password_secret` the one holding the initial
admin's password. On a server the deployment does not administer (`existing_sql_server`), an Entra administrator of it
runs the `sql_setup_script` output once. `terraform.tfvars.example` beside the templates shows an estate written out.

### Environments, builds and deploys

An environment (`dev`, `test`, `prod`) is two files, committed in `osdu/deploy/terraform/environments/` or kept outside
the repository:
`<env>.tfvars`, the estate (`subscription_id`, `resource_group_name`, `acr_name`) and its Terraform variables, with no
secret and no OSDU partition (those are `TF_VAR_osdu_partitions`, kept outside the repository); and `<env>.backend.hcl`,
where its Terraform state lives. The scripts at the repository root work on one:

| Command | What it does |
| --- | --- |
| `.\deploy-<env>.ps1` | Builds, in the environment's registry, the images whose sources changed since the commit each app serves (an image the registry already holds at the commit is reused), rolls the apps forward, checks each new revision serves, and rolls back one that does not. Naming apps deploys them anyway; `-WhatIf` shows the plan. |
| `.\deploy-<env>.ps1 -Infrastructure` | Plans the environment's Terraform with its files and the commit's `image_tag` (building the images of apps the plan creates), applies it once the environment's name is typed back, then waits for the control plane's start-up pass and shows each step, each repository's sync and whether the chat assistant is on. `-WhatIf` stops after the plan. |

No secret reaches Terraform. The partitions (`TF_VAR_osdu_partitions`, with no secret in them) come from the process
environment or the git-ignored `.sqlflow/deploy/<env>.env`. A placeholder left in an environment's files, or
uncommitted changes in what a deploy builds or applies, stops the script before anything changes. An environment kept
out of the repository leaves the committed files as their placeholders: on a workstation `OSDU_ENVIRONMENT_DIR` names
the directory holding its two files, and the Deploy workflow writes them there from its GitHub environment's
`OSDU_ENVIRONMENT_TFVARS` and `OSDU_ENVIRONMENT_BACKEND` variables.

**From GitHub.** The Deploy workflow (`.github/workflows/deploy.yml`) runs the same script. It runs only where the
repository variable `DEPLOY_METHOD` is `terraform`: a push to `main` deploys to `dev` once CI has passed, and `test`
and `prod` are promoted by running the workflow by hand, `prod` behind its GitHub environment's reviewers. Each GitHub
environment signs in as its own managed identity, trusted for that environment through a federated credential, which
`tools/github/connect-environment.ps1` creates with its roles. `osdu/deploy/README.md` lists the roles and the
environment's variables and secrets.

## Scale-in, placement and the module's own settings

- **Drain.** A node draining on SIGTERM finishes the deliveries it holds for up to `SQLFLOW_WORKER_DRAIN_SECONDS` (540 by
  default), so the termination grace period has to be longer: `worker-pool.yaml` and `worker.bicep` set 600. A severed
  delivery loses no record (the ledger leases each record to one attempt), but it repeats work and consumes one of the
  run's execution attempts ([SQLFlow's worker](../../../../sqlflow/docs/reference/cli/worker.md)).
- **Radix, or no autoscaler.** On a platform whose autoscaler cannot read the replica target, or that gives a pod no
  more than 30 seconds to stop (Radix gives every pod 30), let the control plane start its own workers:
  [managed workers](managed-workers.md) keep each pool at a minimum and let a worker leave only once it holds nothing,
  so no grace period matters. `osdu/deploy/radix/radixconfig.yaml` is the Radix application, and
  [Deploying on Radix](radix.md) says how to set it up and deploy it.
- **Placement.** A node runs where it reaches the data database and its pool's OSDU endpoints. The control plane needs
  the same reach for the reads it answers.
- **Probing and metrics.** The scheduled target probe (`Osdu:TargetProbe`) and the metrics export (`Osdu:Telemetry` on the
  control plane, `OSDU_TELEMETRY_*` on nodes) are off until a deployment turns them on
  ([run trace and metrics](../concepts/run-trace-and-metrics.md)).

## Triggering from an external scheduler

An OSDU flow is triggered as SQLFlow's ADF integration triggers any flow: authenticate with a service account's personal
access token, `POST /api/v1/runs`, poll `GET /api/v1/runs/{runId}` until it is terminal, and fail the pipeline when it did
not succeed. The trigger body adds the kind's own arguments:

```json
{
  "repoId": "0195c9a2-7f30-7c44-9c1e-0aa1b2c3d4e5",
  "flowName": "welldb-wellbore-03-delivery",
  "operation": "deliver",
  "values": { "partition": "dev" }
}
```

`operation` is the kind's (`deliver`, `plan`, `verify`, ... for a delivery flow), `values` are the flow's parameters and,
for a flow that names its partitions, `partition`; `payload` carries the kind's options. They are described in
[running an OSDU flow](../cli/run.md). A delivery run that held records still succeeds: held records are an outcome in the
ledger, not a failure of the run, so a pipeline that must stop on them reads the held and failed counts in the run
detail's `resultJson`.

## See also

- [SQLFlow: deploying](../../../../sqlflow/docs/reference/guides/deployment.md)
- [Environment variables](../concepts/environment-variables.md)
- [The control plane](../concepts/control-plane.md)
- [Managed workers](managed-workers.md): the control plane starts its own workers, on Radix or locally
- `osdu/deploy/README.md`, beside the assets
